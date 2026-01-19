using SocketJack.Net;
using SocketJack.Extensions;
using System.Collections.Concurrent;

namespace SocketJack.WpfBasicGame;

// Authoritative game server.
// Clients send input events (cursor + click) and the server rebroadcasts state
// (target position + points + cursors) so every client sees a consistent game.
internal sealed class SocketJackTcpGameServer : IDisposable {
    private readonly TcpServer _server;
    private readonly ConcurrentDictionary<string, int> _points = new();
    private readonly ConcurrentDictionary<string, int> _playerIndex = new();
    private int _nextPlayerIndex;

    private readonly Random _rng = new();
    private int _targetX;
    private int _targetY;

    // Server-side playfield size (must match client GameCanvas size)
    private const double PlayfieldWidth = 560;
    private const double PlayfieldHeight = 360;
    private const double TargetSize = 70;
    private const double HitRadius = 45; // legacy (kept for fallback)

    public event Action<string>? Log;

    public SocketJackTcpGameServer(int port) {
        // SocketJack runtime options.
        // Use compression and whitelist message types to keep network traffic tight and safe.
        var opts = TcpOptions.DefaultOptions.Clone<TcpOptions>();
        opts.Fps = 60;
        opts.Logging = false;
        opts.LogReceiveEvents = false;
        opts.LogSendEvents = false;
        opts.UseCompression = true;
        opts.UsePeerToPeer = true;

        opts.Whitelist.Add(typeof(StartRoundMessage));
        opts.Whitelist.Add(typeof(TargetStateMessage));
        opts.Whitelist.Add(typeof(ClickMessage));
        opts.Whitelist.Add(typeof(PointsUpdateMessage));
        opts.Whitelist.Add(typeof(CursorStateMessage));

        _server = new TcpServer(opts, port, "ClickRaceServer");
        _server.ClientConnected += e => {
            Log?.Invoke($"Client connected: {e.Connection?.ID}");

            var conn = e.Connection;
            if (conn == null)
                return;

            var id = conn.ID;
            if (id == Guid.Empty)
                return;

            var playerId = id.ToString();

            // Stable player index (used by UI sorting/labels).
            var idx = _playerIndex.GetOrAdd(playerId, _ => Interlocked.Increment(ref _nextPlayerIndex));
            conn.SetMetaData("playerindex", idx.ToString(), Restricted: true);
            conn.SetMetaData("points", "0", Restricted: true);

            // Default metadata; bots override this after connecting.
            conn.SetMetaData("isnpc", "0", Restricted: true);
        };
        _server.ClientDisconnected += e => Log?.Invoke($"Client disconnected: {e.Connection?.ID}");

        // Register inbound message handlers.
        _server.RegisterCallback<ClickMessage>(OnClick);
        _server.RegisterCallback<CursorStateMessage>(OnCursorState);
    }

    public bool Start() => _server.Listen();

    public void StartRound(int roundLengthMs) {
        // Reset per-round scoreboard. Player identity stays stable across rounds.
        _points.Clear();
        MoveTarget();

        // Notify clients that the round is starting.
        _server.SendBroadcast(new StartRoundMessage {
            RoundLengthMs = roundLengthMs,
            ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        BroadcastTarget();
    }

    private void MoveTarget() {
        // Server chooses the target position so clients cannot cheat by moving it locally.
        var w = Math.Max(0, PlayfieldWidth - TargetSize);
        var h = Math.Max(0, PlayfieldHeight - TargetSize);
        _targetX = (int)(_rng.NextDouble() * w);
        _targetY = (int)(_rng.NextDouble() * h);
    }

    private void BroadcastTarget() {
        // Broadcast authoritative target to all clients.
        Log?.Invoke($"Broadcast TargetState {_targetX:0},{_targetY:0}");
        _server.SendBroadcast(new TargetStateMessage {
            TargetX = _targetX,
            TargetY = _targetY
        });
    }

    private void BroadcastPoints(string playerId, int points) {
        Log?.Invoke($"Broadcast PointsUpdate {playerId}={points}");
        _server.SendBroadcast(new PointsUpdateMessage {
            PlayerId = playerId,
            Points = points
        });
    }

    private void OnCursorState(ReceivedEventArgs<CursorStateMessage> e) {
        if (e.Object == null)
            return;

        var id = e.Connection == null ? Guid.Empty : e.Connection.ID;
        if (id == Guid.Empty)
            return;

        e.Object.PlayerId = id.ToString();

        // Server rebroadcasts raw cursor positions; clients handle interpolation/rendering.
        _server.SendBroadcast(e.Object);
    }

    private void OnClick(ReceivedEventArgs<ClickMessage> e) {
        if (e.Object == null)
            return;

        var id = e.Connection == null ? Guid.Empty : e.Connection.ID;
        if (id == Guid.Empty)
            return;

        var playerId = id.ToString();

        Log?.Invoke($"Click from {playerId} at {e.Object.ClickX:0},{e.Object.ClickY:0}");

        // Validate click against the current target location.
        // This keeps the server authoritative and mitigates spoofed clicks.
        var clickX = e.Object.ClickX;
        var clickY = e.Object.ClickY;

        // Prefer bounds check (matches WPF button hit area)
        var inBounds = clickX >= _targetX && clickX <= _targetX + TargetSize &&
                       clickY >= _targetY && clickY <= _targetY + TargetSize;

        if (!inBounds) {
            // fallback: allow a generous radius around center
            var targetCenterX = _targetX + TargetSize / 2.0;
            var targetCenterY = _targetY + TargetSize / 2.0;
            var dx = clickX - targetCenterX;
            var dy = clickY - targetCenterY;
            var dist = Math.Sqrt(dx * dx + dy * dy);
            if (dist > HitRadius) {
                Log?.Invoke($"Click rejected (too far), dist={dist:0.0}");
                return;
            }
        }

        Log?.Invoke("Click accepted");

        // Award point to the sender and broadcast updated score.
        var points = _points.AddOrUpdate(playerId, 1, (_, v) => v + 1);

        if (e.Connection != null)
            e.Connection.SetMetaData("points", points.ToString(), Restricted: true);

        BroadcastPoints(playerId, points);

        // Move target after a valid click (server authority).
        // Clients never move the target directly.
        MoveTarget();
        BroadcastTarget();
    }

    public void Dispose() {
        _server.Dispose();
    }
}
