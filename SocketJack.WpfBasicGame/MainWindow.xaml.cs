using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;
using SocketJack.Net.P2P;
using SocketJack.WPFController;

namespace SocketJack.WpfBasicGame;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window {
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

    private void UpdateShareButtonUi() {
        if (ShareToggleButton == null)
            return;
        ShareToggleButton.Content = _shareHandle == null ? "Start Sharing" : "Stop Sharing";
    }

    private int GetDesiredBotCount() {
        if (BotEnabledCheck.IsChecked != true)
            return 0;

        var item = BotCountCombo.SelectedItem as ComboBoxItem;
        var text = item == null ? null : item.Content?.ToString();
        if (int.TryParse(text, out var n) && n >= 0)
            return n;
        return 0;
    }

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

    public MainWindow() {
        InitializeComponent();
        _roundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _roundTimer.Tick += (_, _) => { /* Timer no longer calls MoveTarget */ };
        BotEnabledCheck.IsChecked = false;
        _cursorRenderTimer = new DispatcherTimer { Interval = CursorRenderInterval };
        _cursorRenderTimer.Tick += (_, _) => RenderCursors();
        init();
        PositionSideBySide(isHost: true);
        StartButton_Click(null, null);
    }

    public MainWindow(int aaa) {
        InitializeComponent();
        BotEnabledCheck.IsChecked = false;
        JoinRadio.IsChecked = true;
        _roundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _roundTimer.Tick += (_, _) => { /* Timer no longer calls MoveTarget */ };

        _cursorRenderTimer = new DispatcherTimer { Interval = CursorRenderInterval };
        _cursorRenderTimer.Tick += (_, _) => RenderCursors();
        init();
        PositionSideBySide(isHost: false);
        StartButton_Click(null, null);
    }

    private void PositionSideBySide(bool isHost) {
        try {
            WindowStartupLocation = WindowStartupLocation.Manual;

            var leftBound = (int)SystemParameters.VirtualScreenLeft;
            var topBound = (int)SystemParameters.VirtualScreenTop;
            var widthBound = (int)SystemParameters.VirtualScreenWidth;
            var heightBound = (int)SystemParameters.VirtualScreenHeight;

            var w = Width;
            var h = Height;
            if (double.IsNaN(w) || w <= 0)
                w = 900;
            if (double.IsNaN(h) || h <= 0)
                h = 520;

            var wInt = (int)Math.Round(w);
            var hInt = (int)Math.Round(h);
            var totalW = wInt * 2;
            var left0 = leftBound + ((widthBound - totalW) / 2);
            var top0 = topBound + ((heightBound - hInt) / 2);

            Left = isHost ? left0 : left0 + wInt;
            Top = top0;
        } catch {
        }
    }

    public void init() {
        UpdateScoreboardUi();
        UpdateStartButtonUi();
        UpdateShareButtonUi();

        BotEnabledCheck.Checked += async (_, _) => {
            try {
                await EnsureBotsAdjustedAsync().ConfigureAwait(false);
            } catch {
            }
        };
        BotEnabledCheck.Unchecked += async (_, _) => {
            try {
                await EnsureBotsAdjustedAsync().ConfigureAwait(false);
            } catch {
            }
        };

        GameCanvas.PreviewMouseMove += GameCanvas_MouseMove;

    }

    public void ShareToggleButton_Click(object sender, RoutedEventArgs e) {
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
            _shareHandle = TargetButton.Share(_client.RawClient, peer, fps: 10);
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

    private async void BotCountCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) {
        try {
            await EnsureBotsAdjustedAsync().ConfigureAwait(false);
        } catch {
        }
    }

    private async Task EnsureBotsAdjustedAsync() {
        // Only host mode uses bots.
        if (HostRadio.IsChecked != true) {
            StopAndDisposeBots();
            BotCursor.Visibility = Visibility.Collapsed;
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
        for (var i = current + 1; i <= desired; i++) {
            var traits = new NpcBotClient.BotTraits {
                SpeedMultiplier = 0.7 + (rng.NextDouble() * 1.6),
                JitterMultiplier = 0.3 + (rng.NextDouble() * 2.0),
                Aggression = rng.NextDouble(),
                CursorSendIntervalMs = 120 + rng.Next(0, 180),
                EffectiveClickPaddingPx = rng.Next(3, 14)
            };

            var bot = new NpcBotClient(difficulty, traits, name: $"NpcBot-{i:000}");
            bot.Log += t => Dispatcher.BeginInvoke(() => Log($"[bot {i:000}] {t}"), DispatcherPriority.Background);
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
        // Always use a visible cursor even when hovering the target button.
        if (Mouse.OverrideCursor != Cursors.Arrow)
            Mouse.OverrideCursor = Cursors.Arrow;

        var p = e.GetPosition(GameCanvas);
        _lastLocalCursor = p;
        _pendingLocalCursor = p;
        _hasPendingLocalCursor = true;
    }

    private async Task EnsureBotsConnectedAsync(string host, int port) {
        // Bots are only used when hosting to simulate a larger lobby.
        // Bots should auto-play when hosting. If checkbox is off, keep them disabled.
        if (BotEnabledCheck.IsChecked != true) {
            StopAndDisposeBots();
            BotCursor.Visibility = Visibility.Collapsed;
            return;
        }

        StopAndDisposeBots();
        BotCursor.Visibility = Visibility.Collapsed;

        var rng = new Random();
        var botCount = GetDesiredBotCount();
        if (botCount <= 0)
            return;
        var difficulty = GetSelectedBotDifficulty();

        for (var i = 1; i <= botCount; i++) {
            // Randomize traits so bots don't all behave identically.
            var traits = new NpcBotClient.BotTraits {
                SpeedMultiplier = 0.7 + (rng.NextDouble() * 1.6),
                JitterMultiplier = 0.3 + (rng.NextDouble() * 2.0),
                Aggression = rng.NextDouble(),
                CursorSendIntervalMs = 120 + rng.Next(0, 180),
                EffectiveClickPaddingPx = rng.Next(3, 14)
            };

            var bot = new NpcBotClient(difficulty, traits, name: $"NpcBot-{i:000}");
            bot.Log += t => Dispatcher.BeginInvoke(() => Log($"[bot {i:000}] {t}"), DispatcherPriority.Background);
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

    private async void StartButton_Click(object sender, RoutedEventArgs e) {
        // The button toggles between starting and stopping a session.
        if (_roundRunning) {
            StopSession(resetUiOnly: false);
            return;
        }

        StartButton.IsEnabled = false;
        try {
            RoundStatusText.Text = "Starting...";
            var host = ServerHostText.Text?.Trim();
            if (string.IsNullOrWhiteSpace(host)) {
                Log("Invalid host.");
                return;
            }
            if (!int.TryParse(ServerPortText.Text?.Trim(), out var port) || port <= 0 || port > 65535) {
                Log("Invalid port.");
                return;
            }

            // Always reset state (but keep UI visible)
            _roundTimer.Stop();
            _selfScore = 0;
            _otherScore = 0;
            _selfPoints = 0;
            _otherPoints = 0;
            UpdateScoreboardUi();
            BotCursor.Visibility = Visibility.Collapsed;

            // Show the target immediately; server will still drive its position via TargetState.
            TargetButton.Visibility = Visibility.Visible;
            Canvas.SetLeft(TargetButton, 10);
            Canvas.SetTop(TargetButton, 10);

            if (HostRadio.IsChecked == true) {
                // Host mode: start a local server and connect to it.
                // Host always runs a local server and connects locally so a single instance can play.
                _server = new SocketJackTcpGameServer(port);
                //_server.Log += t => Dispatcher.Invoke(() => Log($"[server] {t}"));
                if (!_server.Start()) {
                    Log($"Failed to listen on port {port}.");
                    return;
                }
                Log($"Hosting on 127.0.0.1:{port}");
                host = "127.0.0.1";
            }

            _client = new SocketJackTcpGameClient();

            // Wire network events onto the UI thread.
            _client.Log += t => Dispatcher.Invoke(() => Log($"[client] {t}"));
            _client.StartRoundReceived += msg => Dispatcher.Invoke(() => HandleStartRound(msg));
            _client.TargetStateReceived += msg => Dispatcher.Invoke(() => HandleTargetState(msg));
            _client.PointsUpdateReceived += msg => Dispatcher.Invoke(() => HandlePointsUpdate(msg));
            _client.CursorStateReceived += msg => Dispatcher.BeginInvoke(() => HandleCursorState(msg), DispatcherPriority.Background);
            _client.PeersChanged += () => Dispatcher.Invoke(UpdateScoreboardUi);

            RoundStatusText.Text = "Connecting...";
            var ok = await _client.ConnectAsync(host, port);
            if (!ok) {
                Log("Connect failed.");
                StopSession(resetUiOnly: false);
                return;
            }

            Log("Connected.");

            // Viewer: receive frames + forward mouse moves/clicks back to sharer.
            _shareViewer = new ControlShareViewer(_client.RawClient, SharedImage);

            _sessionHost = host;
            _sessionPort = port;

            _selfPlayerId = _client.LocalPlayerId;

            _cursorRenderTimer.Start();

            // In join mode, we just connect and wait for host to start the round.
            if (HostRadio.IsChecked == true) {
                // Host mode also starts bots (if enabled) and begins the round.
                RoundStatusText.Text = "Starting bot...";
                await EnsureBotsConnectedAsync(host, port);

                RoundStatusText.Text = "Starting round...";
                const int roundLengthMs = 15000;
                _server?.StartRound(roundLengthMs);
            } else {
                RoundStatusText.Text = "Connected (waiting for host)";
            }

            _roundRunning = true;
            UpdateStartButtonUi();
        } catch (Exception ex) {
            Log(ex.Message);
            StopSession(resetUiOnly: false);
        } finally {
            StartButton.IsEnabled = true;
        }
    }

    private void TargetButton_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (_client?.IsConnected != true)
            return;

        // Send click location in canvas coordinates (relative to the target).
        // This avoids ambiguity when click event is raised from the button element.
        // Send click location in canvas coordinates (relative to the target).
        // This avoids ambiguity when click event is raised from the button element.
        var pInTarget = e.GetPosition(TargetButton);
        var left = Canvas.GetLeft(TargetButton);
        var top = Canvas.GetTop(TargetButton);

        if (double.IsNaN(left) || double.IsNaN(top))
            return;

        int clickX = (int)(left + pInTarget.X);
        int clickY = (int)(top + pInTarget.Y);

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
        RoundStatusText.Text = "Round running";
        TargetButton.Visibility = Visibility.Visible;

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
            Dispatcher.Invoke(EndRound);
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
                var maxX = Math.Max(0.0, GameCanvas.ActualWidth);
                var maxY = Math.Max(0.0, GameCanvas.ActualHeight);
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
                GameCanvas.Children.Add(cursor);
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
            GameCanvas.Children.Add(_localCursor);
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
        if (!_roundTimer.IsEnabled)
            return;

        _roundTimer.Stop();
        TargetButton.Visibility = Visibility.Collapsed;
        foreach (var b in _bots)
            b.Stop();
        BotCursor.Visibility = Visibility.Collapsed;

        var result = _selfScore == _otherScore
            ? "Draw"
            : _selfScore > _otherScore ? "You win" : "You lose";

        RoundStatusText.Text = $"Round over: {result}";
        Log($"Round over. You={_selfScore}, Opponent={_otherScore}.");
    }

    private void StopSession(bool resetUiOnly = false) {
        // Centralized cleanup for both user-requested stop and error paths.
        _roundTimer.Stop();
        _cursorRenderTimer.Stop();
        TargetButton.Visibility = Visibility.Collapsed;
        StopShareInternal();
        StopAndDisposeBots();
        BotCursor.Visibility = Visibility.Collapsed;
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
            GameCanvas.Children.Remove(el);
        _remoteCursors.Clear();
        _cursorSmoothers.Clear();

        if (_localCursor != null) {
            GameCanvas.Children.Remove(_localCursor);
            _localCursor = null;
        }

        _roundRunning = false;
        if (!resetUiOnly)
            RoundStatusText.Text = "Not started";
        UpdateStartButtonUi();
    }

    private void UpdateStartButtonUi() {
        StartButton.Content = _roundRunning ? "Stop Round" : "Start Round";
    }

    private void MoveTarget() {
        if (TargetButton.Visibility != Visibility.Visible)
            return;

        var w = Math.Max(0, GameCanvas.ActualWidth - TargetButton.Width);
        var h = Math.Max(0, GameCanvas.ActualHeight - TargetButton.Height);

        var x = _rng.NextDouble() * w;
        var y = _rng.NextDouble() * h;

        Canvas.SetLeft(TargetButton, x);
        Canvas.SetTop(TargetButton, y);

        _lastTargetCenter = new Point(x + TargetButton.Width / 2.0, y + TargetButton.Height / 2.0);
        foreach (var b in _bots)
            b.SetTarget(_lastTargetCenter);
    }

    private BotDifficulty GetSelectedBotDifficulty() {
        var text = (BotDifficultyCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
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
            ScoreboardList.ItemsSource = new[] { $"Player #1: {_selfPoints}" };
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
            GameCanvas.Children.Remove(_remoteCursors[id]);
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
                int.TryParse(idxText, out idx);

            var pts = 0;
            if (p.Metadata != null && p.Metadata.TryGetValue("points", out var ptsText))
                int.TryParse(ptsText, out pts);

            if (idx <= 0)
                continue;

            rows.Add((idx, $"Player #{idx}: {pts}"));
        }

        rows.Sort((a, b) => a.idx.CompareTo(b.idx));
        ScoreboardList.ItemsSource = rows.Select(r => r.text).ToList();

        // Keep legacy fields updated for round-over text.
        var selfId = _selfPlayerId;
        if (string.IsNullOrWhiteSpace(selfId))
            selfId = _client.LocalPlayerId;

        if (!string.IsNullOrWhiteSpace(selfId)) {
            foreach (var p in peers) {
                if (p.ID != selfId)
                    continue;

                if (p.Metadata != null && p.Metadata.TryGetValue("points", out var ptsText))
                    int.TryParse(ptsText, out _selfPoints);

                break;
            }

            var firstOther = peers.FirstOrDefault(p => p.ID != selfId);
            if (firstOther != null && firstOther.Metadata != null && firstOther.Metadata.TryGetValue("points", out var otherPtsText))
                int.TryParse(otherPtsText, out _otherPoints);
        }
    }

    private void SetTargetPosition(int x, int y) {
        TargetButton.Visibility = Visibility.Visible;
        Canvas.SetLeft(TargetButton, x);
        Canvas.SetTop(TargetButton, y);

        _lastTargetTopLeft = new Point(x, y);
        _lastTargetCenter = new Point(x + TargetButton.Width / 2.0, y + TargetButton.Height / 2.0);
    }

    private void Log(string text) {
        Dispatcher.BeginInvoke(() => LogText.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"));
        LogText.CaretIndex = LogText.Text.Length;
        Dispatcher.BeginInvoke(() => LogText.ScrollToEnd(), DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e) {
        StopSession(resetUiOnly: false);
        _botLoop.Dispose();
        base.OnClosed(e);
    }
}