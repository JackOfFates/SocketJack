using SocketJack.Net;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.WPFController;
using System.Linq;
using System.Threading;

namespace SocketJack.WpfBasicGame;

// Thin adapter over SocketJack's networking client.
// This keeps the WPF UI code focused on gameplay concerns (events + simple send methods)
// and centralizes all SocketJack options/whitelisting in one place.
internal sealed class SocketJackTcpGameClient : IDisposable {
    private readonly TcpClient _client;
    private readonly object _cursorSendGate = new();
    private readonly bool _lightweight;

    public TcpClient RawClient => _client;

    public event Action<StartRoundMessage>? StartRoundReceived;
    public event Action<TargetStateMessage>? TargetStateReceived;
    public event Action<PointsUpdateMessage>? PointsUpdateReceived;
    public event Action<CursorStateMessage>? CursorStateReceived;
    public event Action<string>? Log;

    public event Action? PeersChanged;

    public bool IsConnected => _client.Connected;

    public string? LocalPlayerId => _client.Connection == null ? null : _client.Connection.Identity == null ? null : _client.Connection.Identity.ID;

    public IReadOnlyCollection<Identifier> Peers {
        get {
            lock (_client.Peers)
                return _client.Peers.Values.ToList();
        }
    }

    public SocketJackTcpGameClient(string name = "ClickRaceClient", bool lightweight = false) {
        _lightweight = lightweight;

        // SocketJack runtime options.
        // Whitelisting is required so only expected message types are serialized.
        var opts = NetworkOptions.DefaultOptions.Clone<NetworkOptions>();
        opts.Logging = false;
        opts.LogReceiveEvents = false;
        opts.LogSendEvents = false;
        opts.UseCompression = false;
        opts.UsePeerToPeer = true;

        if (lightweight) {
            // Bot connections: low-frequency receive polling, batched sends.
            // Fps=0 causes a tight spin-loop in TcpConnection.StartReceiving;
            // a low positive value keeps the Task.Delay throttle while reducing
            // per-bot polling from 60fps down to 10fps (~100ms between reads).
            opts.Fps = 10;
            opts.Chunking = true;
            opts.ChunkingIntervalMs = 200;
        } else {
            opts.Fps = 60;
            opts.Chunking = false;
        }

        opts.Whitelist.Add(typeof(StartRoundMessage));
        opts.Whitelist.Add(typeof(TargetStateMessage));
        opts.Whitelist.Add(typeof(ClickMessage));
        opts.Whitelist.Add(typeof(PointsUpdateMessage));
        opts.Whitelist.Add(typeof(CursorStateMessage));

        _client = new TcpClient(opts, name);

        // Bots don't need WPF remote-control support.
        if (!lightweight)
            _client.EnableRemoteControl();

        // Surface connection lifecycle messages to the UI.
        _client.OnConnected += _ => Log?.Invoke("Connected");
        _client.OnDisconnected += _ => Log?.Invoke("Disconnected");

        // Bots only need the start-round event; skip peer/UI callbacks.
        if (!lightweight) {
            _client.PeerConnected += (_, _) => PeersChanged?.Invoke();
            _client.PeerDisconnected += (_, _) => PeersChanged?.Invoke();
        }

        // Map raw network messages into simple UI-friendly events.
        _client.RegisterCallback<StartRoundMessage>(e => {
            if (e.Object != null)
                StartRoundReceived?.Invoke(e.Object);
        });

        // Bots receive target/points/cursor from the host directly;
        // they don't need to process these network callbacks.
        if (!lightweight) {
            _client.RegisterCallback<TargetStateMessage>(e => {
                if (e.Object != null)
                    TargetStateReceived?.Invoke(e.Object);
            });

            _client.RegisterCallback<PointsUpdateMessage>(e => {
                if (e.Object != null)
                    PointsUpdateReceived?.Invoke(e.Object);
            });

            _client.RegisterCallback<CursorStateMessage>(e => {
                if (e.Object != null)
                    CursorStateReceived?.Invoke(e.Object);
            });
        }
    }

    public void SendToPeer(Identifier peer, object message) {
        if (!IsConnected)
            return;
        _client.Send(peer, message);
    }

    public void SendCursor(int x, int y) {
        if (!IsConnected)
            return;

        // Client -> server cursor updates; server rebroadcasts to all peers.
        var message = new CursorStateMessage {
            X = x,
            Y = y
        };

        if (_lightweight) {
            // Bot callers are already on a background thread; send directly.
            _client.Send(message);
        } else {
            ThreadPool.QueueUserWorkItem(_ => SendCursorInternal(message));
        }
    }

    private void SendCursorInternal(CursorStateMessage message) {
        lock (_cursorSendGate) {
            if (!IsConnected)
                return;
            _client.Send(message);
        }
    }

    public Task<bool> ConnectAsync(string host, int port) => _client.Connect(host, port);

    public void SetMetaData(string key, string value) {
        if (_client.Connection == null)
            return;

        // Metadata is used for lightweight peer attributes (e.g., "isnpc").
        _client.Connection.SetMetaData(key, value);
    }

    public void SendClick(int clickX, int clickY) {
        if (!IsConnected)
            return;

        // Click is sent as canvas coordinates to remove ambiguity from UI element hit tests.
        _client.Send(new ClickMessage {
            ClickX = clickX,
            ClickY = clickY
        });
    }

    public void Dispose() {
        _client.Dispose();
    }
}
