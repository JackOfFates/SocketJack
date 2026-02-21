using SocketJack.Net;
using SocketJack.Extensions;
using System.Collections.Concurrent;
using Mono.Nat;
using SocketJack.WPFController;

namespace SocketJack.WpfBasicGame;

// Authoritative game server.
// Clients send input events (cursor + click) and the server rebroadcasts state
// (target position + points + cursors) so every client sees a consistent game.
internal sealed class SocketJackTcpGameServer : IDisposable {
    private readonly TcpServer? _tcpServer;
    private readonly UdpServer? _udpServer;
    private readonly NetworkBase _base;
    private readonly bool _useUdp;
    private readonly ConcurrentDictionary<string, int> _points = new();
    private readonly ConcurrentDictionary<string, int> _playerIndex = new();
    private int _nextPlayerIndex;

    private readonly object _clickLock = new();
    private readonly Random _rng = new();
    private int _targetX;
    private int _targetY;

    // Server-side playfield size (must match client GameCanvas size)
    private const double PlayfieldWidth = 560;
    private const double PlayfieldHeight = 360;
    private const double TargetSize = 70;
    private const double HitRadius = 45; // legacy (kept for fallback)

    public event Action<string>? Log;

    public SocketJackTcpGameServer(int port, bool useUdp = false) {
        _useUdp = useUdp;

        // SocketJack runtime options.
        // Use compression and whitelist message types to keep network traffic tight and safe.
        var opts = NetworkOptions.NewDefault();
        opts.Fps = 0;
        opts.Chunking = false;
        opts.Logging = false;
        opts.LogReceiveEvents = false;
        opts.LogSendEvents = false;
        opts.UseCompression = false;
        opts.UsePeerToPeer = true;

        opts.Whitelist.Add(typeof(StartRoundMessage));
        opts.Whitelist.Add(typeof(TargetStateMessage));
        opts.Whitelist.Add(typeof(ClickMessage));
        opts.Whitelist.Add(typeof(PointsUpdateMessage));
        opts.Whitelist.Add(typeof(CursorStateMessage));

        NIC.NatDiscovered += ()=> {
            var ip = NIC.NAT.GetExternalIP();
            var portforwarded = NIC.ForwardPort(port);
            Log?.Invoke($"NAT detected. External IP: {ip}, Port forwarded: {portforwarded}");
        };

        if (useUdp) {
            _udpServer = new UdpServer(opts, port, "ClickRaceServer");
            _base = _udpServer;
            _udpServer.EnableRemoteControl();
            _udpServer.ClientConnected += OnClientConnected;
            _udpServer.ClientDisconnected += e => Log?.Invoke($"Client disconnected: {e.Connection?.ID}");

            _udpServer.RegisterCallback<ClickMessage>(OnClick);
            _udpServer.RegisterCallback<CursorStateMessage>(OnCursorState);
        } else {
            _tcpServer = new TcpServer(opts, port, "ClickRaceServer");
            _base = _tcpServer;
            _tcpServer.EnableRemoteControl();
            _tcpServer.ClientConnected += OnClientConnected;
            _tcpServer.ClientDisconnected += e => Log?.Invoke($"Client disconnected: {e.Connection?.ID}");

            _tcpServer.RegisterCallback<ClickMessage>(OnClick);
            _tcpServer.RegisterCallback<CursorStateMessage>(OnCursorState);
        }
    }

    private void OnClientConnected(ConnectedEventArgs e) {
        Log?.Invoke($"Client connected: {e.Connection?.ID}");

        var conn = e.Connection;
        if (conn == null)
            return;

        var id = conn.ID;
        if (id == Guid.Empty)
            return;

        var playerId = id.ToString();

        var idx = _playerIndex.GetOrAdd(playerId, _ => Interlocked.Increment(ref _nextPlayerIndex));
        conn.SetMetaData("playerindex", idx.ToString(), Restricted: true);
        conn.SetMetaData("points", "0", Restricted: true);
        conn.SetMetaData("isnpc", "0", Restricted: true);

        try {
            conn.Send(new TargetStateMessage {
                TargetX = _targetX,
                TargetY = _targetY
            });
        } catch {
        }
    }

    public bool Start() {
        // Set an initial target position so clients connecting before
        // StartRound receive a valid TargetState instead of (0,0).
        MoveTarget();

        if (_useUdp)
            return _udpServer!.Listen();
        return _tcpServer!.Listen();
    }

    public void StartRound(int roundLengthMs) {
        _points.Clear();

        lock (_clickLock) {
            MoveTarget();
        }

        _base.SendBroadcast(new StartRoundMessage {
            RoundLengthMs = roundLengthMs,
            ServerUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });

        BroadcastTarget();
    }

    private void MoveTarget() {
        var w = Math.Max(0, PlayfieldWidth - TargetSize);
        var h = Math.Max(0, PlayfieldHeight - TargetSize);
        _targetX = (int)(_rng.NextDouble() * w);
        _targetY = (int)(_rng.NextDouble() * h);
    }

    private void BroadcastTarget() {
        _base.SendBroadcast(new TargetStateMessage {
            TargetX = _targetX,
            TargetY = _targetY
        });
    }

    private void BroadcastPoints(string playerId, int points) {
        _base.SendBroadcast(new PointsUpdateMessage {
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

        _base.SendBroadcast(e.Object);
    }

    private void OnClick(ReceivedEventArgs<ClickMessage> e) {
        if (e.Object == null)
            return;

        var id = e.Connection == null ? Guid.Empty : e.Connection.ID;
        if (id == Guid.Empty)
            return;

        var playerId = id.ToString();

        Log?.Invoke($"Click from {playerId} at {e.Object.ClickX:0},{e.Object.ClickY:0}");

        var clickX = e.Object.ClickX;
        var clickY = e.Object.ClickY;

        // Lock the hit-test + target-move sequence so concurrent bot clicks
        // cannot corrupt Random or double-score on the same target position.
        lock (_clickLock) {
            var inBounds = clickX >= _targetX && clickX <= _targetX + TargetSize &&
                           clickY >= _targetY && clickY <= _targetY + TargetSize;

            if (!inBounds) {
                var targetCenterX = _targetX + TargetSize / 2.0;
                var targetCenterY = _targetY + TargetSize / 2.0;
                var dx = clickX - targetCenterX;
                var dy = clickY - targetCenterY;
                var dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > HitRadius) {
                    return;
                }
            }

            var points = _points.AddOrUpdate(playerId, 1, (_, v) => v + 1);

            if (e.Connection != null)
                e.Connection.SetMetaData("points", points.ToString(), Restricted: true);

            BroadcastPoints(playerId, points);

            MoveTarget();
            BroadcastTarget();
        }
    }

    public void Dispose() {
        if (_useUdp)
            _udpServer!.Dispose();
        else
            _tcpServer!.Dispose();
    }
}
