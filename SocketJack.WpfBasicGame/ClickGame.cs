using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;
using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.WPF.Controller;

namespace SocketJack.WpfBasicGame;

internal sealed class ClickGame : IDisposable {
    private readonly MainWindow _w;
    private readonly Dispatcher _dispatcher;

    // Optional host-side server (only created when hosting from this instance).
    private SocketJackTcpGameServer? _server;

    // Local client used for both host and join modes.
    private SocketJackTcpGameClient? _client;

    // Optional AI-controlled clients (created only when hosting and bots are enabled).
    private readonly List<NpcBotClient> _bots = new();

    private static bool PreferGpuBots() {
        var v = Environment.GetEnvironmentVariable("SOCKETJACK_BOT_GPU");
        if (string.IsNullOrWhiteSpace(v))
            return true;
        return v != "0";
    }
    private readonly BotUpdateLoop _botLoop = new(preferGpu: PreferGpuBots());

    private string? _selfPlayerId;
    private string? _sessionHost;
    private int _sessionPort;
    private bool _roundRunning;

    private readonly Dictionary<string, FrameworkElement> _remoteCursors = new();
    private readonly Dictionary<string, CursorSmoother> _cursorSmoothers = new();
    private readonly DispatcherTimer _cursorRenderTimer;

    private const double CursorUpdateHz = 60.0;
    private static readonly TimeSpan CursorRenderInterval = TimeSpan.FromMilliseconds(1000.0 / CursorUpdateHz);
    private static readonly TimeSpan CursorInterpolationDelay = TimeSpan.FromMilliseconds(50);
    private const double MaxExtrapolationMs = 60;
    private const double StaleSnapMs = 350;
    private const double VelocityEmaAlpha = 0.25;
    private const double StationaryEpsilonPx = 0.75;
    private const double MaxSpeedPxPerMs = 2.5;

    private readonly object _pendingCursorLock = new();
    private readonly Dictionary<string, Point> _pendingCursorUpdates = new();

    private System.Windows.Shapes.Ellipse? _localCursor;
    private Point _lastLocalCursor;
    private Point _pendingLocalCursor;
    private bool _hasPendingLocalCursor;
    private DateTime _nextLocalCursorSendAt = DateTime.MinValue;

    private readonly DispatcherTimer _roundTimer;
    private readonly DispatcherTimer _metricsTimer;
    private readonly Random _rng = new();

    private int _selfScore;
    private int _otherScore;

    private int _selfPoints;
    private int _otherPoints;

    private Point _lastTargetCenter;
    private Point _lastTargetTopLeft;

    private DateTime _nextScoreboardRefreshAt = DateTime.MinValue;

    private readonly Dictionary<string, bool> _isNpcByPlayerId = new();

    private IDisposable? _shareHandle;
    private ControlShareViewer? _shareViewer;

    private readonly object _remoteClickLock = new();
    private Point? _remoteClickNorm;

    private sealed class CursorSmoother {
        public bool HasValue;
        public Point PrevPos;
        public DateTime PrevAt;
        public Point NextPos;
        public DateTime NextAt;
        public Point RenderPos;

        public Vector Velocity;
        public DateTime VelocityAt;

        public DateTime RenderAt;
    }

    public ClickGame(MainWindow window) {
        _w = window;
        _dispatcher = window.Dispatcher;

        _roundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _roundTimer.Tick += (_, _) => { /* Timer no longer calls MoveTarget */ };
        _metricsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _metricsTimer.Tick += (_, _) => UpdateMetricsUi();
        _cursorRenderTimer = new DispatcherTimer { Interval = CursorRenderInterval };
        _cursorRenderTimer.Tick += (_, _) => RenderCursors();

        UpdateScoreboardUi();
        UpdateStartButtonUi();
        UpdateShareButtonUi();

        _w.BotEnabledCheck.Checked += async (_, _) => {
            try {
                await EnsureBotsAdjustedAsync().ConfigureAwait(false);
            } catch {
            }
        };
        _w.BotEnabledCheck.Unchecked += async (_, _) => {
            try {
                await EnsureBotsAdjustedAsync().ConfigureAwait(false);
            } catch {
            }
        };

        _w.GameCanvas.PreviewMouseMove += GameCanvas_MouseMove;
    }

    private void UpdateShareButtonUi() {
        if (_w.ShareToggleButton == null)
            return;
        _w.ShareToggleButton.Content = _shareHandle == null ? "Start Sharing" : "Stop Sharing";
    }

    private int GetDesiredBotCount() {
        if (_w.BotEnabledCheck.IsChecked != true)
            return 0;

        var item = _w.BotCountCombo.SelectedItem as ComboBoxItem;
        var text = item == null ? null : item.Content?.ToString();
        if (int.TryParse(text, out var n) && n >= 0)
            return n;
        return 0;
    }

    public void ToggleShare() {
        if (_shareHandle != null) {
            StopShareInternal();
            UpdateShareButtonUi();
            return;
        }

        try {
            if (_client == null || _client.IsConnected != true) {
                Log("Connect first.");
                return;
            }

            if (_client.IsUdp || _client.RawTcpClient == null) {
                Log("Control sharing is only available over TCP.");
                return;
            }

            var localId = _client.LocalPlayerId;
            if (string.IsNullOrWhiteSpace(localId)) {
                Log("Local identity not ready.");
                return;
            }

            // Only allow sharing when there's at least one remote peer.
            var peers = _client.Peers;
            var remotePeers = peers.Where(p => !string.IsNullOrWhiteSpace(p.ID) && p.ID != localId).ToList();
            if (remotePeers.Count < 1) {
                Log("Need at least 2 clients to share.");
                return;
            }

            var peer = remotePeers[0];
            if (peer == null || string.IsNullOrWhiteSpace(peer.ID)) {
                Log("No peers available to share with.");
                return;
            }

            if (peer.ID == localId) {
                Log("Refusing to share to self.");
                return;
            }

            StopShareInternal();
            _shareHandle = _w.Share(_client.RawTcpClient, peer, fps: 10);
            UpdateShareButtonUi();
        } catch (Exception ex) {
            Log(ex.Message);
        }
    }

    private void StopShareInternal() {
        if (_shareViewer != null) {
            try {
                _shareViewer.Dispose();
            } catch {
            }
            _shareViewer = null;
        }
        if (_shareHandle != null) {
            try {
                _shareHandle.Dispose();
            } catch {
            }
            _shareHandle = null;
        }

        UpdateShareButtonUi();
    }

    public async Task OnBotCountChangedAsync() {
        try {
            await EnsureBotsAdjustedAsync().ConfigureAwait(false);
        } catch {
        }
    }

    private async Task EnsureBotsAdjustedAsync() {
        // Only host mode uses bots.
        if (_w.HostRadio.IsChecked != true) {
            StopAndDisposeBots();
            _w.BotCursor.Visibility = Visibility.Collapsed;
            return;
        }
        if (_client == null || _server == null)
            return;
        if (_client.IsConnected != true)
            return;

        // Bots are local headless clients; only support when hosting locally.
        if (_sessionHost != "127.0.0.1" && _sessionHost != "localhost")
            return;

        var desired = GetDesiredBotCount();
        var current = _bots.Count;

        if (desired == current)
            return;

        if (desired < current) {
            for (var i = current - 1; i >= desired; i--) {
                var b = _bots[i];
                try {
                    _botLoop.Remove(b);
                    b.Stop();
                    b.Dispose();
                } catch {
                }
                _bots.RemoveAt(i);
            }
            return;
        }

        // Add bots.
        var host = _sessionHost;
        var port = _sessionPort;
        if (string.IsNullOrWhiteSpace(host) || port <= 0)
            return;

        var rng = new Random();
        var difficulty = GetSelectedBotDifficulty();
        var botUseUdp = _client?.IsUdp == true;
        for (var i = current + 1; i <= desired; i++) {
            var traits = new NpcBotClient.BotTraits {
                SpeedMultiplier = 0.7 + (rng.NextDouble() * 1.6),
                JitterMultiplier = 0.3 + (rng.NextDouble() * 2.0),
                Aggression = rng.NextDouble(),
                CursorSendIntervalMs = 120 + rng.Next(0, 180),
                EffectiveClickPaddingPx = rng.Next(3, 14)
            };

            var bot = new NpcBotClient(difficulty, traits, name: $"NpcBot-{i:000}", useUdp: botUseUdp);
            bot.Log += t => _dispatcher.BeginInvoke(() => Log($"[bot {i:000}] {t}"), DispatcherPriority.Background);
            _bots.Add(bot);
            _botLoop.Add(bot);

            var ok = await bot.ConnectAsync(host, port).ConfigureAwait(false);
            if (ok)
                bot.MarkAsNpc();

            bot.Start();

            if (i % 10 == 0)
                await Task.Delay(25).ConfigureAwait(false);
        }

        _botLoop.Start();
    }

    private void GameCanvas_MouseMove(object sender, MouseEventArgs e) {
        var p = e.GetPosition(_w.GameCanvas);
        _lastLocalCursor = p;
        _pendingLocalCursor = p;
        _hasPendingLocalCursor = true;
    }

    private async Task EnsureBotsConnectedAsync(string host, int port) {
        // Bots are only used when hosting to simulate a larger lobby.
        // Bots should auto-play when hosting. If checkbox is off, keep them disabled.
        if (_w.BotEnabledCheck.IsChecked != true) {
            StopAndDisposeBots();
            _w.BotCursor.Visibility = Visibility.Collapsed;
            return;
        }

        StopAndDisposeBots();
        _w.BotCursor.Visibility = Visibility.Collapsed;

        var rng = new Random();
        var botCount = GetDesiredBotCount();
        if (botCount <= 0)
            return;
        var difficulty = GetSelectedBotDifficulty();
        var botUseUdp = _client?.IsUdp == true;

        for (var i = 1; i <= botCount; i++) {
            // Randomize traits so bots don't all behave identically.
            var traits = new NpcBotClient.BotTraits {
                SpeedMultiplier = 0.7 + (rng.NextDouble() * 1.6),
                JitterMultiplier = 0.3 + (rng.NextDouble() * 2.0),
                Aggression = rng.NextDouble(),
                CursorSendIntervalMs = 120 + rng.Next(0, 180),
                EffectiveClickPaddingPx = rng.Next(3, 14)
            };

            var bot = new NpcBotClient(difficulty, traits, name: $"NpcBot-{i:000}", useUdp: botUseUdp);
            bot.Log += t => _dispatcher.BeginInvoke(() => Log($"[bot {i:000}] {t}"), DispatcherPriority.Background);
            _bots.Add(bot);
            _botLoop.Add(bot);
        }

        for (var i = 0; i < _bots.Count; i++) {
            var bot = _bots[i];
            var ok = await bot.ConnectAsync(host, port);
            if (ok)
                bot.MarkAsNpc();

            if (i % 10 == 9)
                await Task.Delay(25);
        }

        foreach (var bot in _bots)
            bot.Start();

        _botLoop.Start();
    }

    private void StopAndDisposeBots() {
        foreach (var b in _bots) {
            try {
                _botLoop.Remove(b);
                b.Stop();
                b.Dispose();
            } catch {
            }
        }
        _bots.Clear();
        _botLoop.Stop();
    }

    public async Task ToggleSessionAsync() {
        // The button toggles between starting and stopping a session.
        if (_roundRunning) {
            StopSession(resetUiOnly: false);
            return;
        }

        // Always tear down any leftover resources from a previous session
        // (e.g. after EndRound which stops the round but keeps the
        // server/client alive). This releases the port for reuse.
        StopSession(resetUiOnly: false);

        _w.StartButton.IsEnabled = false;
        try {
            _w.RoundStatusText.Text = "Starting...";
            var host = _w.ServerHostText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host)) {
                Log("Invalid host.");
                return;
            }
            if (!int.TryParse(_w.ServerPortText.Text?.Trim(), out var port) || port <= 0 || port > 65535) {
                Log("Invalid port.");
                return;
            }

            var useUdp = _w.UdpCheckBox.IsChecked == true;

            // Always reset state (but keep UI visible)
            _roundTimer.Stop();
            _selfScore = 0;
            _otherScore = 0;
            _selfPoints = 0;
            _otherPoints = 0;
            UpdateScoreboardUi();
            _w.BotCursor.Visibility = Visibility.Collapsed;

            // Show the target immediately; server will still drive its position via TargetState.
            _w.TargetButton.Visibility = Visibility.Visible;
            Canvas.SetLeft(_w.TargetButton, 10);
            Canvas.SetTop(_w.TargetButton, 10);

            if (_w.HostRadio.IsChecked == true) {
                // Host mode: start a local server and connect to it.
                // Host always runs a local server and connects locally so a single instance can play.
                _server = new SocketJackTcpGameServer(port, useUdp: useUdp);
                //_server.Log += t => _dispatcher.Invoke(() => Log($"[server] {t}"));
                if (!_server.Start()) {
                    Log($"Failed to listen on port {port}.");
                    return;
                }
                Log($"Hosting on 127.0.0.1:{port} ({(useUdp ? "UDP" : "TCP")})");
                host = "127.0.0.1";
            }

            _client = new SocketJackTcpGameClient(useUdp: useUdp);

            // Wire network events onto the UI thread.
            _client.Log += t => _dispatcher.BeginInvoke(() => Log($"[client] {t}"));
            _client.StartRoundReceived += msg => _dispatcher.BeginInvoke(() => HandleStartRound(msg));
            _client.TargetStateReceived += msg => _dispatcher.BeginInvoke(() => HandleTargetState(msg));
            _client.PointsUpdateReceived += msg => _dispatcher.BeginInvoke(() => HandlePointsUpdate(msg));
            // HandleCursorState is thread-safe (lock-protected dictionary write only) —
            // run it directly on the network thread to avoid flooding the dispatcher.
            _client.CursorStateReceived += HandleCursorState;
            _client.PeersChanged += () => _dispatcher.BeginInvoke(UpdateScoreboardUi);

            // Capture remote click coordinates before the library dispatches the
            // Click event. The normalized position is stored so OnTargetClicked
            // can translate it to canvas coordinates instead of using Mouse.GetPosition.
            _client.RawClient.RegisterCallback<RemoteAction>(e => {
                if (e?.Object == null || e.Object.Action != RemoteAction.ActionType.MouseDown)
                    return;
                var parts = e.Object.Arguments?.Split(',');
                if (parts == null || parts.Length < 3)
                    return;
                if (!double.TryParse(parts[1].Trim(), out var nx) ||
                    !double.TryParse(parts[2].Trim(), out var ny))
                    return;
                lock (_remoteClickLock)
                    _remoteClickNorm = new Point(nx, ny);
            });

            _w.RoundStatusText.Text = "Connecting...";
            var ok = await _client.ConnectAsync(host, port);
            if (!ok) {
                Log("Connect failed.");
                StopSession(resetUiOnly: false);
                return;
            }

            Log("Connected.");

            // Viewer: receive frames + forward mouse moves/clicks back to sharer.
            // Control sharing only works over TCP.
            if (!useUdp && _client.RawTcpClient != null)
                _shareViewer = new ControlShareViewer(_client.RawTcpClient, _w.SharedImage);

            _sessionHost = host;
            _sessionPort = port;

            _selfPlayerId = _client.LocalPlayerId;

            _cursorRenderTimer.Start();
            _metricsTimer.Start();

            // In join mode, we just connect and wait for host to start the round.
            if (_w.HostRadio.IsChecked == true) {
                // Host mode also starts bots (if enabled) and begins the round.
                _w.RoundStatusText.Text = "Starting bot...";
                await EnsureBotsConnectedAsync(host, port);

                // UDP is connectionless; yield so the server can process the
                // initial ping datagrams and register all clients before the
                // round broadcast is sent.
                if (useUdp)
                    await Task.Delay(100);

                _w.RoundStatusText.Text = "Starting round...";
                const int roundLengthMs = 150000;
                _server?.StartRound(roundLengthMs);
            } else {
                _w.RoundStatusText.Text = "Connected (waiting for host)";
            }

            _roundRunning = true;
            UpdateStartButtonUi();
        } catch (Exception ex) {
            Log(ex.Message);
            StopSession(resetUiOnly: false);
        } finally {
            _w.StartButton.IsEnabled = true;
        }
    }

    public void OnTargetClicked(MouseButtonEventArgs e) {
        if (_client?.IsConnected != true)
            return;

        var left = Canvas.GetLeft(_w.TargetButton);
        var top = Canvas.GetTop(_w.TargetButton);

        if (double.IsNaN(left) || double.IsNaN(top))
            return;

        int clickX;
        int clickY;

        var pInTarget = e.GetPosition(_w.TargetButton);
        var isLocal = pInTarget.X >= 0 && pInTarget.X <= _w.TargetButton.ActualWidth &&
                      pInTarget.Y >= 0 && pInTarget.Y <= _w.TargetButton.ActualHeight;

        if (isLocal) {
            // Local click: mouse is physically over the button.
            clickX = (int)(left + pInTarget.X);
            clickY = (int)(top + pInTarget.Y);
        } else {
            // Remote click via control sharing. Translate the stored normalized
            // coordinates (percentage of the shared element) to canvas space.
            Point? norm;
            lock (_remoteClickLock) {
                norm = _remoteClickNorm;
                _remoteClickNorm = null;
            }

            if (norm.HasValue) {
                var windowPoint = new Point(norm.Value.X * _w.ActualWidth, norm.Value.Y * _w.ActualHeight);
                var canvasPoint = _w.TranslatePoint(windowPoint, _w.GameCanvas);
                clickX = (int)canvasPoint.X;
                clickY = (int)canvasPoint.Y;
            } else {
                // No stored remote position; fall back to button center.
                clickX = (int)(left + _w.TargetButton.ActualWidth / 2.0);
                clickY = (int)(top + _w.TargetButton.ActualHeight / 2.0);
            }
        }

        Log($"Click at {clickX:0},{clickY:0}");
        _client.SendClick(clickX, clickY);
    }

    private void HandleStartRound(StartRoundMessage msg) {
        // Reset local round UI state and kick off a delayed end-of-round.
        _selfScore = 0;
        _otherScore = 0;
        _selfPoints = 0;
        _otherPoints = 0;
        UpdateScoreboardUi();
        _w.RoundStatusText.Text = "Round running";
        _w.TargetButton.Visibility = Visibility.Visible;

        foreach (var b in _bots) {
            b.SetDifficulty(GetSelectedBotDifficulty());
            b.SetTarget(_lastTargetTopLeft, hasTarget: false);
        }

        _roundRunning = true;
        UpdateStartButtonUi();

        var len = msg.RoundLengthMs;
        if (len == 0)
            len = 15000;

        _ = Task.Run(async () => {
            // Host sends the authoritative duration; clients simply stop rendering at the end.
            await Task.Delay(len);
            _dispatcher.Invoke(EndRound);
        });
    }

    private void HandleTargetState(TargetStateMessage msg) {
        // Update the target button location and inform bots of the new aim point.
        Log($"TargetState {msg.TargetX:0},{msg.TargetY:0}");
        SetTargetPosition(msg.TargetX, msg.TargetY);
        foreach (var b in _bots)
            b.SetTarget(_lastTargetTopLeft, hasTarget: true);
    }

    private void HandlePointsUpdate(PointsUpdateMessage msg) {
        // Track points per-player and refresh the scoreboard.
        if (_client == null)
            return;

        var selfId = _selfPlayerId;
        if (string.IsNullOrWhiteSpace(selfId))
            selfId = _client.LocalPlayerId;

        // Refresh NPC flags from latest peer metadata so cursor colors stay correct.
        try {
            foreach (var p in _client.Peers) {
                var isNpc = false;
                if (p.Metadata != null && p.Metadata.TryGetValue("isnpc", out var isnpcText))
                    isNpc = isnpcText == "1";
                _isNpcByPlayerId[p.ID] = isNpc;
            }
        } catch {
        }

        if (string.IsNullOrWhiteSpace(selfId))
            return;

        if (msg.PlayerId == selfId)
            _selfPoints = msg.Points;
        else
            _otherPoints = msg.Points;

        UpdateScoreboardUi();
    }

    private void HandleCursorState(CursorStateMessage msg) {
        // Buffer cursor updates; the renderer consumes these at ~60 FPS.
        var playerId = string.IsNullOrWhiteSpace(msg.PlayerId) ? null : msg.PlayerId;
        if (playerId == null)
            return;

        lock (_pendingCursorLock)
            _pendingCursorUpdates[playerId] = new Point(msg.X, msg.Y);
    }

    private void RenderCursors() {
        // Render remote cursors at a fixed cadence.
        // Network updates arrive at a lower rate; interpolation fills in between.
        if (_client == null)
            return;

        KeyValuePair<string, Point>[] updates;
        lock (_pendingCursorLock) {
            // Drain pending network updates so render code stays deterministic.
            if (_pendingCursorUpdates.Count == 0)
                updates = Array.Empty<KeyValuePair<string, Point>>();
            else {
                updates = _pendingCursorUpdates.ToArray();
                _pendingCursorUpdates.Clear();
            }
        }

        var now = DateTime.UtcNow;
        for (var i = 0; i < updates.Length; i++) {
            var playerId = updates[i].Key;
            var p = updates[i].Value;

            if (!_cursorSmoothers.TryGetValue(playerId, out var s)) {
                s = new CursorSmoother();
                _cursorSmoothers[playerId] = s;
            }

            if (!s.HasValue) {
                s.HasValue = true;
                s.PrevPos = p;
                s.PrevAt = now;
                s.NextPos = p;
                s.NextAt = now;
                s.RenderPos = p;
                s.RenderAt = now;
                s.Velocity = new Vector(0, 0);
                s.VelocityAt = now;
                continue;
            }

            // Shift the target window forward.
            s.PrevPos = s.NextPos;
            s.PrevAt = s.NextAt;
            s.NextPos = p;
            s.NextAt = now;
        }

        var selfId = _selfPlayerId;
        if (string.IsNullOrWhiteSpace(selfId))
            selfId = _client.LocalPlayerId;

        foreach (var kvp in _cursorSmoothers) {
            // Skip our own cursor to avoid duplicating the local indicator.
            var playerId = kvp.Key;
            if (!string.IsNullOrWhiteSpace(selfId) && playerId == selfId)
                continue;

            var s = kvp.Value;
            if (!s.HasValue)
                continue;

            // Additive, high-frequency smoothing (no interpolation/extrapolation).
            var ageMs = (now - s.NextAt).TotalMilliseconds;
            if (ageMs > StaleSnapMs) {
                s.RenderPos = s.NextPos;
                s.Velocity = new Vector(0, 0);
                s.RenderAt = now;
            } else {
                var dtMs = (now - s.RenderAt).TotalMilliseconds;
                if (dtMs < 0)
                    dtMs = 0;
                if (dtMs > 50)
                    dtMs = 50;

                var toTarget = new Vector(s.NextPos.X - s.RenderPos.X, s.NextPos.Y - s.RenderPos.Y);
                if (Math.Abs(toTarget.X) <= StationaryEpsilonPx && Math.Abs(toTarget.Y) <= StationaryEpsilonPx)
                    toTarget = new Vector(0, 0);

                const double accelPerMs = 0.02;
                const double dragPerMs = 0.12;

                s.Velocity += toTarget * (accelPerMs * dtMs);
                var drag = Math.Exp(-dragPerMs * dtMs);
                s.Velocity *= drag;

                var speed = s.Velocity.Length;
                if (speed > MaxSpeedPxPerMs && speed > 0.0001) {
                    s.Velocity.Normalize();
                    s.Velocity *= MaxSpeedPxPerMs;
                }

                var newPos = new Point(
                    s.RenderPos.X + (s.Velocity.X * dtMs),
                    s.RenderPos.Y + (s.Velocity.Y * dtMs));

                // Clamp to canvas bounds.
                var maxX = Math.Max(0.0, _w.GameCanvas.ActualWidth);
                var maxY = Math.Max(0.0, _w.GameCanvas.ActualHeight);
                if (newPos.X < 0)
                    newPos.X = 0;
                if (newPos.Y < 0)
                    newPos.Y = 0;
                if (newPos.X > maxX)
                    newPos.X = maxX;
                if (newPos.Y > maxY)
                    newPos.Y = maxY;

                s.RenderPos = newPos;
                s.RenderAt = now;
            }

            var isNpc = _isNpcByPlayerId.TryGetValue(playerId, out var npcFlag) && npcFlag;

            if (!_remoteCursors.TryGetValue(playerId, out var el)) {
                // Lazily materialize a visual element per peer.
                var cursor = new System.Windows.Shapes.Ellipse {
                    Width = 22,
                    Height = 22,
                    Fill = isNpc ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Red,
                    Stroke = System.Windows.Media.Brushes.Black,
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };

                Canvas.SetZIndex(cursor, 1);
                _w.GameCanvas.Children.Add(cursor);
                _remoteCursors[playerId] = cursor;
                el = cursor;
            } else {
                if (el is System.Windows.Shapes.Ellipse ellipse)
                    ellipse.Fill = isNpc ? System.Windows.Media.Brushes.Orange : System.Windows.Media.Brushes.Red;
            }

            el.Visibility = Visibility.Visible;
            var p = s.RenderPos;
            Canvas.SetLeft(el, p.X - el.Width / 2.0);
            Canvas.SetTop(el, p.Y - el.Height / 2.0);
        }

        UpdateLocalCursor(now);
    }

    private void UpdateLocalCursor(DateTime now) {
        if (!_hasPendingLocalCursor)
            return;

        if (_localCursor == null) {
            _localCursor = new System.Windows.Shapes.Ellipse {
                Width = 8,
                Height = 8,
                Fill = System.Windows.Media.Brushes.LimeGreen,
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_localCursor, 10);
            _w.GameCanvas.Children.Add(_localCursor);
        }

        var pending = _pendingLocalCursor;
        Canvas.SetLeft(_localCursor, pending.X - _localCursor.Width / 2.0);
        Canvas.SetTop(_localCursor, pending.Y - _localCursor.Height / 2.0);
        _hasPendingLocalCursor = false;

        if (_client == null || _client.IsConnected != true)
            return;

        var sendAt = now - CursorInterpolationDelay;
        if (sendAt < _nextLocalCursorSendAt)
            return;

        _nextLocalCursorSendAt = sendAt.AddMilliseconds(33);
        _client.SendCursor((int)pending.X, (int)pending.Y);
    }

    private void EndRound() {
        if (!_roundRunning)
            return;

        _roundRunning = false;
        _roundTimer.Stop();
        _w.TargetButton.Visibility = Visibility.Collapsed;
        foreach (var b in _bots)
            b.Stop();
        _w.BotCursor.Visibility = Visibility.Collapsed;

        var result = _selfScore == _otherScore
            ? "Draw"
            : _selfScore > _otherScore ? "You win" : "You lose";

        _w.RoundStatusText.Text = $"Round over: {result}";
        Log($"Round over. You={_selfScore}, Opponent={_otherScore}.");
        UpdateStartButtonUi();
    }

    private void StopSession(bool resetUiOnly = false) {
        // Centralized cleanup for both user-requested stop and error paths.
        _roundTimer.Stop();
        _cursorRenderTimer.Stop();
        _metricsTimer.Stop();
        _w.TargetButton.Visibility = Visibility.Collapsed;
        StopShareInternal();
        StopAndDisposeBots();
        _w.BotCursor.Visibility = Visibility.Collapsed;
        _selfScore = 0;
        _otherScore = 0;
        _selfPoints = 0;
        _otherPoints = 0;
        UpdateScoreboardUi();

        if (!resetUiOnly) {
            // Only dispose network/server resources when fully stopping.
            _client?.Dispose();
            _client = null;
            _selfPlayerId = null;
            _server?.Dispose();
            _server = null;
        }

        foreach (var el in _remoteCursors.Values)
            _w.GameCanvas.Children.Remove(el);
        _remoteCursors.Clear();
        _cursorSmoothers.Clear();

        if (_localCursor != null) {
            _w.GameCanvas.Children.Remove(_localCursor);
            _localCursor = null;
        }

        _roundRunning = false;
        if (!resetUiOnly)
            _w.RoundStatusText.Text = "Not started";
        UpdateStartButtonUi();
    }

    private void UpdateStartButtonUi() {
        _w.StartButton.Content = _roundRunning ? "Stop Round" : "Start Round";
    }

    private void MoveTarget() {
        if (_w.TargetButton.Visibility != Visibility.Visible)
            return;

        var w = Math.Max(0, _w.GameCanvas.ActualWidth - _w.TargetButton.Width);
        var h = Math.Max(0, _w.GameCanvas.ActualHeight - _w.TargetButton.Height);

        var x = _rng.NextDouble() * w;
        var y = _rng.NextDouble() * h;

        Canvas.SetLeft(_w.TargetButton, x);
        Canvas.SetTop(_w.TargetButton, y);

        _lastTargetCenter = new Point(x + _w.TargetButton.Width / 2.0, y + _w.TargetButton.Height / 2.0);
        foreach (var b in _bots)
            b.SetTarget(_lastTargetCenter);
    }

    private BotDifficulty GetSelectedBotDifficulty() {
        var text = (_w.BotDifficultyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        return text switch {
            "Easy" => BotDifficulty.Easy,
            "Hard" => BotDifficulty.Hard,
            _ => BotDifficulty.Normal
        };
    }

    private void UpdateScoreboardUi() {
        var now = DateTime.UtcNow;
        if (now < _nextScoreboardRefreshAt)
            return;

        _nextScoreboardRefreshAt = now.AddMilliseconds(150);

        if (_client == null) {
            _w.ScoreboardList.ItemsSource = new[] { $"Player #1: {_selfPoints}" };
            return;
        }

        var peers = _client.Peers;

        var activeIds = new HashSet<string>(peers.Select(p => p.ID));

        // Remove cursors for peers that are no longer connected.
        foreach (var id in _cursorSmoothers.Keys.ToList()) {
            if (activeIds.Contains(id))
                continue;
            _cursorSmoothers.Remove(id);
        }
        foreach (var id in _remoteCursors.Keys.ToList()) {
            if (activeIds.Contains(id))
                continue;
            _w.GameCanvas.Children.Remove(_remoteCursors[id]);
            _remoteCursors.Remove(id);
        }

        _isNpcByPlayerId.Clear();
        foreach (var p in peers) {
            var isNpc = false;
            if (p.Metadata != null && p.Metadata.TryGetValue("isnpc", out var isnpcText))
                isNpc = isnpcText == "1";
            _isNpcByPlayerId[p.ID] = isNpc;
        }

        var rows = new List<(int idx, string text)>();

        foreach (var p in peers) {
            var idx = 0;
            if (p.Metadata != null && p.Metadata.TryGetValue("playerindex", out var idxText))
                _ = int.TryParse(idxText, out idx);

            var pts = 0;
            if (p.Metadata != null && p.Metadata.TryGetValue("points", out var ptsText))
                _ = int.TryParse(ptsText, out pts);

            if (idx <= 0)
                continue;

            rows.Add((idx, $"Player #{idx}: {pts}"));
        }

        rows.Sort((a, b) => a.idx.CompareTo(b.idx));
        _w.ScoreboardList.ItemsSource = rows.Select(r => r.text).ToList();

        // Keep legacy fields updated for round-over text.
        var selfId = _selfPlayerId;
        if (string.IsNullOrWhiteSpace(selfId))
            selfId = _client.LocalPlayerId;

        if (!string.IsNullOrWhiteSpace(selfId)) {
            foreach (var p in peers) {
                if (p.ID != selfId)
                    continue;

                if (p.Metadata != null && p.Metadata.TryGetValue("points", out var ptsText))
                    _ = int.TryParse(ptsText, out _selfPoints);

                break;
            }

            var firstOther = peers.FirstOrDefault(p => p.ID != selfId);
            if (firstOther != null && firstOther.Metadata != null && firstOther.Metadata.TryGetValue("points", out var otherPtsText))
                _ = int.TryParse(otherPtsText, out _otherPoints);
        }
    }

    private void SetTargetPosition(int x, int y) {
        _w.TargetButton.Visibility = Visibility.Visible;
        Canvas.SetLeft(_w.TargetButton, x);
        Canvas.SetTop(_w.TargetButton, y);

        _lastTargetTopLeft = new Point(x, y);
        _lastTargetCenter = new Point(x + _w.TargetButton.Width / 2.0, y + _w.TargetButton.Height / 2.0);
    }

    private void UpdateMetricsUi() {
        if (_client == null || _client.IsConnected != true) {
            _w.UploadSpeedText.Text = "--";
            _w.DownloadSpeedText.Text = "--";
            _w.LatencyText.Text = "--";
            _w.PingText.Text = "--";
            return;
        }

        var up = _client.UploadBytesPerSecond;
        var down = _client.DownloadBytesPerSecond;
        var latency = _client.LatencyMs;
        var ping = latency * 2;

        _w.UploadSpeedText.Text = ((long)up).ByteToString(1) + "/s";
        _w.DownloadSpeedText.Text = ((long)down).ByteToString(1) + "/s";
        _w.LatencyText.Text = latency > 0 ? $"{latency} ms" : "--";
        _w.PingText.Text = ping > 0 ? $"{ping} ms" : "--";
    }

    private void Log(string text) {
        _dispatcher.BeginInvoke(() => _w.LogText.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"));
        _dispatcher.BeginInvoke(() => {
            _w.LogText.CaretIndex = _w.LogText.Text.Length;
            _w.LogText.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    public void Dispose() {
        StopSession(resetUiOnly: false);
        _botLoop.Dispose();
    }
}
