using SocketJack.Net;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.WPF.Controller;
using System.Linq;
using System.Threading;

namespace SocketJack.WpfBasicGame;

// Thin adapter over SocketJack's networking client.
// This keeps the WPF UI code focused on gameplay concerns (events + simple send methods)
// and centralizes all SocketJack options/whitelisting in one place.
internal sealed class SocketJackTcpGameClient : IDisposable {
    private readonly TcpClient? _tcpClient;
    private readonly UdpClient? _udpClient;
    private readonly NetworkBase _base;
    private readonly object _cursorSendGate = new();
    private readonly bool _lightweight;
    private readonly bool _useUdp;

    public TcpClient RawTcpClient => _tcpClient!;
    public NetworkBase RawClient => _base;
    public bool IsUdp => _useUdp;

    public event Action<StartRoundMessage>? StartRoundReceived;
    public event Action<TargetStateMessage>? TargetStateReceived;
    public event Action<PointsUpdateMessage>? PointsUpdateReceived;
    public event Action<CursorStateMessage>? CursorStateReceived;
    public event Action<string>? Log;

    public event Action? PeersChanged;

    public bool IsConnected => _base.Connected;

    public long LatencyMs => _base.LatencyMs;

    private int _cachedUploadBps;
    private int _cachedDownloadBps;

    public int UploadBytesPerSecond => _cachedUploadBps;

    public int DownloadBytesPerSecond => _cachedDownloadBps;

    public string? LocalPlayerId {
        get {
            var conn = _base.Connection;
            if (conn == null) return null;
            if (conn.Identity == null) return null;
            return conn.Identity.ID;
        }
    }

    public IReadOnlyCollection<Identifier> Peers {
        get {
            lock (_base.Peers)
                return _base.Peers.Values.ToList();
        }
    }

    public SocketJackTcpGameClient(string name = "ClickRaceClient", bool lightweight = false, bool useUdp = false) {
        _lightweight = lightweight;
        _useUdp = useUdp;

        // SocketJack runtime options.
        // Whitelisting is required so only expected message types are serialized.
        var opts = NetworkOptions.NewDefault();
        opts.Logging = false;
        opts.LogReceiveEvents = false;
        opts.LogSendEvents = false;
        opts.UseCompression = false;
        opts.UsePeerToPeer = true;

        if (lightweight) {
            opts.Fps = 0;
            opts.Chunking = true;
            opts.ChunkingIntervalMs = 200;
        } else {
            opts.Fps = useUdp ? 0 : 60;
            opts.Chunking = false;
        }

        opts.Whitelist.Add(typeof(StartRoundMessage));
        opts.Whitelist.Add(typeof(TargetStateMessage));
        opts.Whitelist.Add(typeof(ClickMessage));
        opts.Whitelist.Add(typeof(PointsUpdateMessage));
        opts.Whitelist.Add(typeof(CursorStateMessage));

        if (useUdp) {
            _udpClient = new UdpClient(opts, name);
            _base = _udpClient;

            if (!lightweight)
                _udpClient.EnableRemoteControl();

            _udpClient.OnConnected += _ => {
                Log?.Invoke("Connected");
            };
            _udpClient.OnDisconnected += _ => {
                Log?.Invoke("Disconnected");
            };

            if (!lightweight) {
                _udpClient.PeerConnected += (_, _) => PeersChanged?.Invoke();
                _udpClient.PeerDisconnected += (_, _) => PeersChanged?.Invoke();
            }

            _udpClient.BytesPerSecondUpdate += OnBytesPerSecondUpdate;
            RegisterCallbacks(_udpClient, lightweight);
        } else {
            _tcpClient = new TcpClient(opts, name);
            _base = _tcpClient;

            if (!lightweight)
                _tcpClient.EnableRemoteControl();

            _tcpClient.OnConnected += _ => {
                Log?.Invoke("Connected");
            };
            _tcpClient.OnDisconnected += _ => {
                Log?.Invoke("Disconnected");
            };

            if (!lightweight) {
                _tcpClient.PeerConnected += (_, _) => PeersChanged?.Invoke();
                _tcpClient.PeerDisconnected += (_, _) => PeersChanged?.Invoke();
            }

            _tcpClient.BytesPerSecondUpdate += OnBytesPerSecondUpdate;
            RegisterCallbacks(_tcpClient, lightweight);
        }
    }

    private void OnBytesPerSecondUpdate(int receivedPerSecond, int sentPerSecond) {
        Interlocked.Exchange(ref _cachedUploadBps, sentPerSecond);
        Interlocked.Exchange(ref _cachedDownloadBps, receivedPerSecond);
    }

    private void RegisterCallbacks(NetworkBase client, bool lightweight) {
        client.RegisterCallback<StartRoundMessage>(e => {
            if (e.Object != null)
                StartRoundReceived?.Invoke(e.Object);
        });

        if (!lightweight) {
            client.RegisterCallback<TargetStateMessage>(e => {
                if (e.Object != null)
                    TargetStateReceived?.Invoke(e.Object);
            });

            client.RegisterCallback<PointsUpdateMessage>(e => {
                if (e.Object != null)
                    PointsUpdateReceived?.Invoke(e.Object);
            });

            client.RegisterCallback<CursorStateMessage>(e => {
                if (e.Object != null)
                    CursorStateReceived?.Invoke(e.Object);
            });
        }
    }

    private void SendRaw(object obj) {
        if (_useUdp)
            _udpClient!.Send(obj);
        else
            _tcpClient!.Send(obj);
    }

    public void SendToPeer(Identifier peer, object message) {
        if (!IsConnected)
            return;
        _base.Send(peer, message);
    }

    public void SendCursor(int x, int y) {
        if (!IsConnected)
            return;

        var message = new CursorStateMessage {
            X = x,
            Y = y
        };

        if (_lightweight) {
            SendRaw(message);
        } else {
            ThreadPool.QueueUserWorkItem(_ => SendCursorInternal(message));
        }
    }

    private void SendCursorInternal(CursorStateMessage message) {
        lock (_cursorSendGate) {
            if (!IsConnected)
                return;
            SendRaw(message);
        }
    }

    public Task<bool> ConnectAsync(string host, int port) {
        if (_useUdp)
            return _udpClient!.Connect(host, port);
        return _tcpClient!.Connect(host, port);
    }

    public void SetMetaData(string key, string value) {
        if (_base.Connection == null)
            return;
        _base.Connection.SetMetaData(key, value);
    }

    public void SendClick(int clickX, int clickY) {
        if (!IsConnected)
            return;

        SendRaw(new ClickMessage {
            ClickX = clickX,
            ClickY = clickY
        });
    }

    public void Dispose() {
        if (_useUdp)
            _udpClient!.Dispose();
        else
            _tcpClient!.Dispose();
    }
}
