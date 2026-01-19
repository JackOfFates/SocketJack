using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Collections.Generic;
using System.Linq;

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

    private string? _selfPlayerId;

    private bool _roundRunning;

    private readonly Dictionary<string, FrameworkElement> _remoteCursors = new();
    private readonly Dictionary<string, CursorSmoother> _cursorSmoothers = new();
    private readonly DispatcherTimer _cursorRenderTimer;

    private readonly object _pendingCursorLock = new();
    private readonly Dictionary<string, Point> _pendingCursorUpdates = new();

    private System.Windows.Shapes.Ellipse? _localCursor;
    private Point _lastLocalCursor;
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

    private sealed class CursorSmoother {
        public bool HasValue;
        public Point PrevPos;
        public DateTime PrevAt;
        public Point NextPos;
        public DateTime NextAt;
        public Point RenderPos;
    }

    public MainWindow() {
        InitializeComponent();

        _roundTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _roundTimer.Tick += (_, _) => { /* Timer no longer calls MoveTarget */ };

        _cursorRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _cursorRenderTimer.Tick += (_, _) => RenderCursors();

        UpdateScoreboardUi();
        UpdateStartButtonUi();

        GameCanvas.MouseMove += GameCanvas_MouseMove;
    }

    private void GameCanvas_MouseMove(object sender, MouseEventArgs e) {
        // Always use a visible cursor even when hovering the target button.
        if (Mouse.OverrideCursor != Cursors.Arrow)
            Mouse.OverrideCursor = Cursors.Arrow;

        var p = e.GetPosition(GameCanvas);
        _lastLocalCursor = p;

        if (_localCursor == null) {
            // Create a local visual cursor indicator.
            _localCursor = new System.Windows.Shapes.Ellipse {
                Width = 18,
                Height = 18,
                Fill = System.Windows.Media.Brushes.LimeGreen,
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 2,
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_localCursor, 1);
            GameCanvas.Children.Add(_localCursor);
        }

        Canvas.SetLeft(_localCursor, p.X - _localCursor.Width / 2.0);
        Canvas.SetTop(_localCursor, p.Y - _localCursor.Height / 2.0);

        if (_client == null || _client.IsConnected != true)
            return;

        // Rate-limit network cursors to reduce bandwidth; remote clients interpolate.
        var now = DateTime.UtcNow;
        if (now < _nextLocalCursorSendAt)
            return;

        _nextLocalCursorSendAt = now.AddMilliseconds(66);
        _client.SendCursor((int)p.X, (int)p.Y);
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
        const int botCount = 10;
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
    }

    private void StopAndDisposeBots() {
        foreach (var b in _bots) {
            try {
                b.Stop();
                b.Dispose();
            } catch {
            }
        }
        _bots.Clear();
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

    private void TargetButton_Click(object sender, RoutedEventArgs e) {
        if (_client?.IsConnected != true)
            return;

        // Send click location in canvas coordinates (relative to the target).
        // This avoids ambiguity when click event is raised from the button element.
        // Send click location in canvas coordinates (relative to the target).
        // This avoids ambiguity when click event is raised from the button element.
        var pInTarget = Mouse.GetPosition(TargetButton);
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
                continue;
            }

            // Shift the target window forward.
            s.PrevPos = s.NextPos;
            s.PrevAt = s.NextAt;
            s.NextPos = p;
            s.NextAt = now;

            // If updates are very sparse, snap to avoid long, incorrect glides.
            if ((s.NextAt - s.PrevAt).TotalMilliseconds > 250) {
                s.PrevPos = s.NextPos;
                s.PrevAt = s.NextAt;
                s.RenderPos = s.NextPos;
            }
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

            // Time-based interpolation between last two received samples.
            // This yields smooth cursor motion independent of network send rate.
            var denomMs = (s.NextAt - s.PrevAt).TotalMilliseconds;
            if (denomMs <= 0) {
                s.RenderPos = s.NextPos;
            } else {
                var t = (now - s.PrevAt).TotalMilliseconds / denomMs;
                if (t < 0)
                    t = 0;
                if (t > 1)
                    t = 1;

                var x = s.PrevPos.X + ((s.NextPos.X - s.PrevPos.X) * t);
                var y = s.PrevPos.Y + ((s.NextPos.Y - s.PrevPos.Y) * t);
                s.RenderPos = new Point(x, y);
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
        //Dispatcher.BeginInvoke(() => LogText.AppendText($"{DateTime.Now:HH:mm:ss} {text}{Environment.NewLine}"));
        //LogText.CaretIndex = LogText.Text.Length;
        // Dispatcher.BeginInvoke(() => LogText.ScrollToEnd(), DispatcherPriority.Background);
    }

    protected override void OnClosed(EventArgs e) {
        StopSession(resetUiOnly: false);
        base.OnClosed(e);
    }
}