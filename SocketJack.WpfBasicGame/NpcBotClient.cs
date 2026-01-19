using System.Windows;
using System.Windows.Threading;

namespace SocketJack.WpfBasicGame;

// Headless AI client used to populate the game with additional players.
// The bot connects over the same network protocol as a real client, moves a cursor,
// and sends click events when it reaches the target.
internal sealed class NpcBotClient : IDisposable {
    public sealed class BotTraits {
        // Trait multipliers allow a mix of movement styles and difficulty within the same round.
        public double SpeedMultiplier { get; init; } = 1.0;
        public double JitterMultiplier { get; init; } = 1.0;
        public double Aggression { get; init; } = 0.5;

        // Rate-limit network cursor updates so bots don't spam the server.
        public int CursorSendIntervalMs { get; init; } = 66;

        // Adds a "human" margin requirement before clicking (prevents perfect edge clicks).
        public int EffectiveClickPaddingPx { get; init; } = 5;
    }

    private readonly SocketJackTcpGameClient _client;
    private readonly Random _rng = new();

    private readonly BotTraits _traits;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;

    private DateTime _nextCursorEventAt = DateTime.MinValue;
    private int _lastSentX;
    private int _lastSentY;

    private Point _cursor;
    private Point _target;
    private Vector _velocity;
    private bool _roundActive;
    private BotDifficulty _difficulty;

    private bool _hasTarget;
    private DateTime _nextWanderAt = DateTime.MinValue;
    private Point _wanderTarget;

    public event Action<Point>? CursorMoved;
    public event Action<string>? Log;

    public string? LocalPlayerId => _client.LocalPlayerId;

    public NpcBotClient(BotDifficulty difficulty, BotTraits? traits = null, string name = "NpcBot") {
        // Each bot is just a normal game client with automated input.
        _difficulty = difficulty;
        _traits = traits ?? new BotTraits();
        _client = new SocketJackTcpGameClient(name);
        _client.Log += t => Log?.Invoke(t);
        _client.StartRoundReceived += OnStartRound;
    }

    public Task<bool> ConnectAsync(string host, int port) => _client.ConnectAsync(host, port);

    public void MarkAsNpc() {
        // Server will broadcast this metadata to all peers.
        // UI uses this flag to color cursors and bots can be filtered/sorted.
        _client.SetMetaData("isnpc", "1");
    }

    public void SetDifficulty(BotDifficulty difficulty) => _difficulty = difficulty;

    public void SetTarget(Point target) => _target = target;

    public void SetTarget(Point target, bool hasTarget) {
        _target = target;
        _hasTarget = hasTarget;
    }

    private void OnStartRound(StartRoundMessage msg) {
        // Bots begin their simulation loop when a round starts.
        _roundActive = true;
        if (_cursor == default)
            _cursor = new Point(_rng.Next(40, 250), _rng.Next(40, 250));
        _velocity = new Vector(0, 0);
        StartLoop();
    }

    public void Start() {
        _roundActive = true;
        if (_cursor == default)
            _cursor = new Point(_rng.Next(40, 250), _rng.Next(40, 250));
        _velocity = new Vector(0, 0);
        StartLoop();
        Log?.Invoke("Bot started");
    }

    private void StartLoop() {
        if (_loopTask is { IsCompleted: false })
            return;

        _loopCts?.Cancel();
        _loopCts?.Dispose();
        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;

        // Run a ~60 FPS update loop in the background to simulate movement.
        _loopTask = Task.Run(async () => {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
            while (_roundActive && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Tick();
        }, ct);
    }

    private void Tick() {
        if (!_roundActive)
            return;

        var (maxSpeed, clickProbability) = GetTuning(_difficulty);
        maxSpeed *= _traits.SpeedMultiplier;
        clickProbability = Math.Min(1.0, clickProbability * (0.5 + _traits.Aggression));

        // If we don't have a target yet, wander so the cursor still moves.
        // When we do have a target, aim for the center of the button.
        const double targetSize = 70;
        var aim = _hasTarget
            ? new Point(_target.X + targetSize / 2.0, _target.Y + targetSize / 2.0)
            : GetWanderTarget();

        // Steer toward the aim point using a simple velocity model.
        var to = new Vector(aim.X - _cursor.X, aim.Y - _cursor.Y);
        var dist = to.Length;
        if (dist > 0.01)
            to.Normalize();

        // Add jitter so movement isn't perfectly straight.
        var jitter = new Vector((_rng.NextDouble() - 0.5) * 0.35 * _traits.JitterMultiplier,
                                (_rng.NextDouble() - 0.5) * 0.35 * _traits.JitterMultiplier);

        var desiredVel = (to * maxSpeed) + jitter;

        // Loop tick is ~16ms; use a stable blend factor.
        //const double keep = 0.82;
        //const double take = 0.18;
        _velocity = desiredVel; //(_velocity * keep) + (desiredVel * take);

        // Clamp
        if (_velocity.Length > maxSpeed) {
            _velocity.Normalize();
            _velocity *= maxSpeed;
        }

        _cursor = new Point(_cursor.X + _velocity.X, _cursor.Y + _velocity.Y);

        // UI can optionally visualize the bot locally.
        CursorMoved?.Invoke(_cursor);

        // Throttle outbound cursor updates.
        var now = DateTime.UtcNow;
        if (now >= _nextCursorEventAt) {
            _nextCursorEventAt = now.AddMilliseconds(_traits.CursorSendIntervalMs);
            var x = (int)_cursor.X;
            var y = (int)_cursor.Y;
            if (x != _lastSentX || y != _lastSentY) {
                _lastSentX = x;
                _lastSentY = y;
                _client.SendCursor(x, y);
            }
        }

        if (!_hasTarget)
            return;

        // Click as soon as the cursor is at least 5px inside the target bounds.
        // In UI/server, TargetState uses top-left coordinates for a 70x70 target.
        var margin = _traits.EffectiveClickPaddingPx;
        var isDeepOverTarget = _cursor.X >= _target.X + margin && _cursor.X <= _target.X + targetSize - margin &&
                               _cursor.Y >= _target.Y + margin && _cursor.Y <= _target.Y + targetSize - margin;

        // Fire click as soon as the bot is clearly over the target.
        if (isDeepOverTarget)
            _client.SendClick((int)_cursor.X, (int)_cursor.Y);
    }

    private Point GetWanderTarget() {
        // When the server hasn't sent a target yet, keep the cursor moving so the bot looks alive.
        var now = DateTime.UtcNow;
        if (now >= _nextWanderAt) {
            _nextWanderAt = now.AddMilliseconds(_rng.Next(350, 900));
            _wanderTarget = new Point(_rng.Next(30, 520), _rng.Next(30, 330));
        }
        return _wanderTarget;
    }

    private static (double speed, double clickProbability) GetTuning(BotDifficulty d) => d switch {
        BotDifficulty.Easy => (speed: 2.2, clickProbability: 0.10),
        BotDifficulty.Normal => (speed: 3.8, clickProbability: 0.18),
        BotDifficulty.Hard => (speed: 5.5, clickProbability: 0.26),
        _ => (speed: 3.8, clickProbability: 0.18)
    };

    public void Stop() {
        _roundActive = false;
        _loopCts?.Cancel();
    }

    public void Dispose() {
        Stop();
        _client.Dispose();
    }
}
