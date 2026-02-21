using System.Diagnostics;
using System.Windows;

namespace SocketJack.WpfBasicGame;

// Headless AI client used to populate the game with additional players.
// The bot connects over the same network protocol as a real client, moves a cursor,
// and sends click events when it reaches the target.
internal sealed class NpcBotClient : IDisposable, ITickableBot {
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

    private long _nextCursorEventAtTs;
    private int _lastSentX;
    private int _lastSentY;

    private Point _cursor;
    private Point _target;
    private Vector _velocity;
    private bool _roundActive;
    private BotDifficulty _difficulty;
    private long _lastTickAtTs;

    private bool _hasTarget;
    private bool _hasClicked;
    private long _nextWanderAtTs;
    private Point _wanderTarget;

    private float _gpuWanderX;
    private float _gpuWanderY;
    private float _gpuWanderTimer;

    public bool IsActive => _roundActive;

    public event Action<Point>? CursorMoved;
    public event Action<string>? Log;

    public string? LocalPlayerId => _client.LocalPlayerId;

    public NpcBotClient(BotDifficulty difficulty, BotTraits? traits = null, string name = "NpcBot", bool useUdp = false) {
        // Each bot is just a normal game client with automated input.
        _difficulty = difficulty;
        _traits = traits ?? new BotTraits();
        _client = new SocketJackTcpGameClient(name, lightweight: true, useUdp: useUdp);
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

    public void SetTarget(Point target) {
        _target = target;
        _hasClicked = false;
    }

    public void SetTarget(Point target, bool hasTarget) {
        _target = target;
        _hasTarget = hasTarget;
        _hasClicked = false;
    }

    private void OnStartRound(StartRoundMessage msg) {
        // Bots begin their simulation loop when a round starts.
        _roundActive = true;
        _hasClicked = false;
        if (_cursor == default)
            _cursor = new Point(_rng.Next(40, 250), _rng.Next(40, 250));
        _velocity = new Vector(0, 0);
        _lastTickAtTs = 0;
        _gpuWanderTimer = 0f;
    }

    public void Start() {
        _roundActive = true;
        _hasClicked = false;
        if (_cursor == default)
            _cursor = new Point(_rng.Next(40, 250), _rng.Next(40, 250));
        _velocity = new Vector(0, 0);
        _lastTickAtTs = 0;
        _gpuWanderX = (float)_rng.Next(30, 520);
        _gpuWanderY = (float)_rng.Next(30, 330);
        _gpuWanderTimer = 0f;
        Log?.Invoke("Bot started");
    }

    public void Tick(long nowTimestamp) {
        if (!_roundActive)
            return;

        var (maxSpeed, clickProbability) = GetTuning(_difficulty);
        maxSpeed *= _traits.SpeedMultiplier;
        clickProbability = Math.Min(1.0, clickProbability * (0.5 + _traits.Aggression));

        var dtMs = 0.0;
        if (_lastTickAtTs != 0) {
            dtMs = (nowTimestamp - _lastTickAtTs) * 1000.0 / Stopwatch.Frequency;
            if (dtMs < 0)
                dtMs = 0;
            if (dtMs > 50)
                dtMs = 50;
        }
        _lastTickAtTs = nowTimestamp;

        // If we don't have a target yet, wander so the cursor still moves.
        // When we do have a target, aim for the center of the button.
        const double targetSize = 70;
        var aim = _hasTarget
            ? new Point(_target.X + targetSize / 2.0, _target.Y + targetSize / 2.0)
            : GetWanderTarget(nowTimestamp);

        // Additive smoothing toward the aim point.
        var toTarget = new Vector(aim.X - _cursor.X, aim.Y - _cursor.Y);
        if (Math.Abs(toTarget.X) <= 0.75 && Math.Abs(toTarget.Y) <= 0.75)
            toTarget = new Vector(0, 0);

        // Add jitter so movement isn't perfectly straight.
        var jitter = new Vector((_rng.NextDouble() - 0.5) * 0.35 * _traits.JitterMultiplier,
                                (_rng.NextDouble() - 0.5) * 0.35 * _traits.JitterMultiplier);
        toTarget += jitter;

        const double accelPerMs = 0.02;
        const double dragPerMs = 0.12;
        var maxSpeedPerMs = maxSpeed / 16.0;

        if (dtMs > 0) {
            _velocity += toTarget * (accelPerMs * dtMs);
            var drag = Math.Exp(-dragPerMs * dtMs);
            _velocity *= drag;

            var speed = _velocity.Length;
            if (speed > maxSpeedPerMs && speed > 0.0001) {
                _velocity.Normalize();
                _velocity *= maxSpeedPerMs;
            }

            _cursor = new Point(_cursor.X + (_velocity.X * dtMs), _cursor.Y + (_velocity.Y * dtMs));
        }

        // UI can optionally visualize the bot locally.
        CursorMoved?.Invoke(_cursor);

        // Throttle outbound cursor updates.
        if (nowTimestamp >= _nextCursorEventAtTs) {
            _nextCursorEventAtTs = nowTimestamp + MsToTimestampDelta(_traits.CursorSendIntervalMs);
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
        if (isDeepOverTarget && !_hasClicked) {
            _hasClicked = true;
            _client.SendClick((int)_cursor.X, (int)_cursor.Y);
        }
    }

    private Point GetWanderTarget(long nowTimestamp) {
        // When the server hasn't sent a target yet, keep the cursor moving so the bot looks alive.
        if (nowTimestamp >= _nextWanderAtTs) {
            _nextWanderAtTs = nowTimestamp + MsToTimestampDelta(_rng.Next(350, 900));
            _wanderTarget = new Point(_rng.Next(30, 520), _rng.Next(30, 330));
        }
        return _wanderTarget;
    }

    private static long MsToTimestampDelta(int ms) {
        if (ms <= 0)
            return 0;
        return (long)(Stopwatch.Frequency * (ms / 1000.0));
    }

    public BotSimConfig GetSimConfig(int seed) {
        var (maxSpeed, _) = GetTuning(_difficulty);
        maxSpeed *= _traits.SpeedMultiplier;

        const float targetSize = 70f;
        var jitter = (float)(0.35 * _traits.JitterMultiplier);

        return new BotSimConfig(
            maxSpeed: (float)maxSpeed,
            jitter: jitter,
            seed: seed,
            targetCenterX: (float)(_target.X + targetSize / 2.0),
            targetCenterY: (float)(_target.Y + targetSize / 2.0),
            targetLeft: (float)_target.X,
            targetTop: (float)_target.Y,
            targetSize: targetSize,
            clickMargin: (float)_traits.EffectiveClickPaddingPx,
            hasTarget: _hasTarget ? 1 : 0);
    }

    public BotSimState ExportSimState() {
        return new BotSimState {
            X = (float)_cursor.X,
            Y = (float)_cursor.Y,
            Vx = (float)_velocity.X,
            Vy = (float)_velocity.Y,
            WanderX = _gpuWanderX,
            WanderY = _gpuWanderY,
            WanderTimer = _gpuWanderTimer,
            HitTarget = 0
        };
    }

    public void ImportSimState(BotSimState s, long nowTimestamp) {
        _cursor = new Point(s.X, s.Y);
        _velocity = new Vector(s.Vx, s.Vy);
        _gpuWanderX = s.WanderX;
        _gpuWanderY = s.WanderY;
        _gpuWanderTimer = s.WanderTimer;
        _lastTickAtTs = nowTimestamp;

        CursorMoved?.Invoke(_cursor);

        if (nowTimestamp >= _nextCursorEventAtTs) {
            _nextCursorEventAtTs = nowTimestamp + MsToTimestampDelta(_traits.CursorSendIntervalMs);

            var x = (int)_cursor.X;
            var y = (int)_cursor.Y;
            if (x != _lastSentX || y != _lastSentY) {
                _lastSentX = x;
                _lastSentY = y;
                _client.SendCursor(x, y);
            }
        }

        if (s.HitTarget == 1 && !_hasClicked) {
            _hasClicked = true;
            _client.SendClick((int)_cursor.X, (int)_cursor.Y);
        }
    }

    private static (double speed, double clickProbability) GetTuning(BotDifficulty d) => d switch {
        BotDifficulty.Easy => (speed: 2.2, clickProbability: 0.10),
        BotDifficulty.Normal => (speed: 3.8, clickProbability: 0.18),
        BotDifficulty.Hard => (speed: 5.5, clickProbability: 0.26),
        _ => (speed: 3.8, clickProbability: 0.18)
    };

    public void Stop() {
        _roundActive = false;
    }

    public void Dispose() {
        Stop();
        _client.Dispose();
    }
}
