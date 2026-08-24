using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using SocketJack.Net;

namespace heirowLLM;

internal sealed class ChickenChaserWindow : Window
{
    private readonly HeirowLlm _proxy;
    private readonly Action _openInHeirowLlm;
    private readonly Func<IReadOnlyList<string>> _installedChatModels;
    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(8) };
    private readonly ImageBrush _spriteBrush;
    private readonly BitmapSource _colorSpriteSheet;
    private readonly BitmapSource _graySpriteSheet;
    private readonly Rectangle _sprite;
    private readonly DispatcherTimer _animationTimer;
    private readonly List<ChickenChaserNotificationWindow> _notifications = new();
    private readonly Dictionary<string, ChickenChaserNotificationWindow> _activeNotifications = new(StringComparer.Ordinal);
    private ChickenChaserChatWindow? _chatWindow;
    private ChickenChaserLoginWindow? _loginWindow;
    private ChickenChaserSettings _settings = new();
    private string _accessToken = "";
    private string _signedInUserName = "";
    private string _authExpiresUtc = "";
    private bool _authRestoreAttempted;
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private int _animationFrame;
    private int _renderedFrame = -1;
    private long _lastSpriteFrameTick;
    private string _animationState = "idle";
    private bool _spriteHovered;
    private bool _offline;
    private DispatcherTimer? _singleClickTimer;
    private Point? _idleAvatarPosition;
    private bool _avatarDockedToChat;
    private bool _positioningChatLayout;
    private readonly long _animationEpoch = Stopwatch.GetTimestamp();
    private const double DefaultAvatarWidth = 122;
    private const double DefaultAvatarHeight = 146;
    private const double CompactAvatarWidth = 32;
    private const double CompactAvatarHeight = 39;
    private const double ScreenEdgeMargin = 8;
    private const double ChatPerchOverlap = 2;
    private const double ChatSideOverlap = 8;

    public event EventHandler? HiddenByUser;

    public ChickenChaserWindow(HeirowLlm proxy, Action openInHeirowLlm, Func<IReadOnlyList<string>> installedChatModels)
    {
        _proxy = proxy;
        _openInHeirowLlm = openInHeirowLlm;
        _installedChatModels = installedChatModels;
        Title = "Chicken Chaser";
        Width = DefaultAvatarWidth;
        Height = DefaultAvatarHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Point defaultPosition = GetDefaultAvatarPosition();
        Left = defaultPosition.X;
        Top = defaultPosition.Y;
        _idleAvatarPosition = defaultPosition;

        BitmapImage sheet = new(new Uri("pack://application:,,,/Assets/picturebank-chicken-chaser-sprites.png", UriKind.Absolute));
        _colorSpriteSheet = sheet;
        _graySpriteSheet = CreateGraySpriteSheet(sheet);
        _spriteBrush = new ImageBrush(_colorSpriteSheet)
        {
            Stretch = Stretch.Fill,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, Math.Max(1, sheet.PixelWidth / 4d), Math.Max(1, sheet.PixelHeight / 4d))
        };
        _sprite = new Rectangle
        {
            Fill = _spriteBrush,
            Opacity = .5,
            Cursor = Cursors.SizeAll,
            Margin = new Thickness(0),
            RenderTransformOrigin = new Point(.5, 1),
            RenderTransform = new TransformGroup { Children = new TransformCollection { new RotateTransform(), new TranslateTransform() } },
            ToolTip = "Chicken Chaser — drag to move"
        };
        _sprite.Clip = new RectangleGeometry(new Rect(1, 1, Width - 2, Height - 15));
        RenderOptions.SetBitmapScalingMode(_sprite, BitmapScalingMode.HighQuality);
        _sprite.SnapsToDevicePixels = true;
        _sprite.MouseEnter += (_, _) => { _spriteHovered = true; UpdateSpriteAppearance(); };
        _sprite.MouseLeave += (_, _) => { _spriteHovered = false; UpdateSpriteAppearance(); };
        _sprite.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2)
            {
                _singleClickTimer?.Stop();
                _singleClickTimer = null;
                ToggleCompactAvatar();
                e.Handled = true;
                return;
            }
            Point before = new(Left, Top);
            try { DragMove(); } catch { }
            Point after = new(Left, Top);
            if (_chatWindow?.IsVisible == true && Math.Abs(after.X - before.X) + Math.Abs(after.Y - before.Y) >= 4)
            {
                _positioningChatLayout = true;
                try
                {
                    _chatWindow.Left += after.X - before.X;
                    _chatWindow.Top += after.Y - before.Y;
                }
                finally { _positioningChatLayout = false; }
                PositionAvatarForChat(_chatWindow);
            }
            RepositionNotifications();
            if (Math.Abs(Left - before.X) + Math.Abs(Top - before.Y) < 4) ScheduleSingleClick();
            else if (_chatWindow?.IsVisible != true) _ = SavePositionAsync();
        };
        _sprite.ContextMenu = BuildContextMenu();
        Content = _sprite;

        _animationTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1000d / 60d) };
        _animationTimer.Tick += (_, _) => AnimateSprite(sheet);
        LocationChanged += (_, _) => RepositionNotifications();
        Loaded += async (_, _) => { _animationTimer.Start(); await RestoreRememberedLoginAsync(); if (IsAuthenticated) await LoadSettingsAsync(); };
        Closed += (_, _) => { _singleClickTimer?.Stop(); _animationTimer.Stop(); _authGate.Dispose(); _http.Dispose(); CloseAuxiliaryWindows(); };
    }

    public void ShowFromTray()
    {
        _settings.Hidden = false;
        if (!IsVisible) Show();
        Topmost = true;
        Activate();
        _ = SaveSettingsAsync(_settings);
    }

    public void ShowNotification(string source, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowNotification(source, message)); return; }
        string compact = Compact(message, 220);
        if (IsChatOfflineStatus(compact)) { SetConnectionOffline(true); return; }
        if (IsChatOnlineStatus(compact)) SetConnectionOffline(false);
        if (compact.Length < 3 || _settings.Hidden || compact.StartsWith("Updates: checking", StringComparison.OrdinalIgnoreCase)) return;
        string category = NotificationCategory(source, compact);
        if ((_settings.IgnoredNotificationCategories ?? new List<string>()).Contains(category, StringComparer.OrdinalIgnoreCase)) return;
        string signature = source + "|" + compact;
        if (_activeNotifications.TryGetValue(signature, out ChickenChaserNotificationWindow? active) && active.IsLoaded)
        {
            active.IncrementRepetitions();
            SetReaction("celebrate", 700);
            return;
        }
        _activeNotifications.Remove(signature);
        while (_notifications.Count >= 4) { _notifications[0].Close(); _notifications.RemoveAt(0); }
        var notification = new ChickenChaserNotificationWindow(source, compact, category, OpenChatWithMessage, IgnoreNotificationCategory);
        notification.Closed += (_, _) =>
        {
            _notifications.Remove(notification);
            if (_activeNotifications.TryGetValue(signature, out ChickenChaserNotificationWindow? current) && ReferenceEquals(current, notification))
                _activeNotifications.Remove(signature);
            RepositionNotifications();
        };
        _notifications.Add(notification);
        _activeNotifications[signature] = notification;
        RepositionNotifications();
        notification.Show();
        SetReaction("celebrate", 1400);
    }

    public void SetConnectionOffline(bool offline)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetConnectionOffline(offline)); return; }
        _offline = offline;
        _spriteBrush.ImageSource = offline ? _graySpriteSheet : _colorSpriteSheet;
        _sprite.ToolTip = offline
            ? (_settings.Compact ? "Chicken Chaser is offline and compact — double-click to restore" : "Chicken Chaser is offline — drag to move")
            : (_settings.Compact ? "Chicken Chaser compact — double-click to restore" : "Chicken Chaser — drag to move");
        UpdateSpriteAppearance();
    }

    private void UpdateSpriteAppearance() => _sprite.Opacity = _offline ? (_spriteHovered ? .46 : .28) : (_spriteHovered ? 1 : .5);

    private static bool IsChatOfflineStatus(string value) => value.Contains("chat", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("offline", StringComparison.OrdinalIgnoreCase) || value.Contains("disconnected", StringComparison.OrdinalIgnoreCase) || value.Contains("unavailable", StringComparison.OrdinalIgnoreCase));

    private static bool IsChatOnlineStatus(string value) => value.Contains("chat", StringComparison.OrdinalIgnoreCase) &&
        (value.Contains("heartbeat", StringComparison.OrdinalIgnoreCase) || value.Contains("online", StringComparison.OrdinalIgnoreCase) || value.Contains("connected", StringComparison.OrdinalIgnoreCase));

    private void IgnoreNotificationCategory(string category)
    {
        _settings.IgnoredNotificationCategories ??= new List<string>();
        if (!_settings.IgnoredNotificationCategories.Contains(category, StringComparer.OrdinalIgnoreCase))
            _settings.IgnoredNotificationCategories.Add(category);
        _ = SaveSettingsAsync(_settings);
    }

    private static string NotificationCategory(string source, string message)
    {
        string sourceKey = Compact(source, 60).ToLowerInvariant();
        if ((source + " " + message).Contains("update", StringComparison.OrdinalIgnoreCase)) return sourceKey + "|updates";
        if ((source + " " + message).Contains("download", StringComparison.OrdinalIgnoreCase)) return sourceKey + "|downloads";
        if ((source + " " + message).Contains("model", StringComparison.OrdinalIgnoreCase)) return sourceKey + "|models";
        int colon = message.IndexOf(':');
        string suffix = colon is > 1 and < 34 ? message[..colon] : message;
        return sourceKey + "|" + Compact(suffix, 96).ToLowerInvariant();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var modeHost = new MenuItem { StaysOpenOnClick = true };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var chatLabel = new TextBlock { Text = "Chat", VerticalAlignment = VerticalAlignment.Center };
        var helpLabel = new TextBlock { Text = "Agent", VerticalAlignment = VerticalAlignment.Center };
        var toggle = new ToggleButton
        {
            Width = 58,
            Height = 24,
            Margin = new Thickness(9, 0, 9, 0),
            Padding = new Thickness(3, 0, 3, 1),
            Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
            FontWeight = FontWeights.ExtraBold,
            Cursor = Cursors.Hand
        };
        void UpdateModeAppearance(bool helpSelected)
        {
            toggle.Content = helpSelected ? "··●" : "●··";
            toggle.Background = new SolidColorBrush(helpSelected ? Color.FromRgb(124, 58, 237) : Color.FromRgb(2, 132, 199));
            toggle.ToolTip = helpSelected ? "Agent mode selected" : "Chat mode selected";
            chatLabel.Foreground = new SolidColorBrush(helpSelected ? Color.FromRgb(148, 163, 184) : Color.FromRgb(125, 211, 252));
            chatLabel.FontWeight = helpSelected ? FontWeights.Normal : FontWeights.ExtraBold;
            helpLabel.Foreground = new SolidColorBrush(helpSelected ? Color.FromRgb(196, 181, 253) : Color.FromRgb(148, 163, 184));
            helpLabel.FontWeight = helpSelected ? FontWeights.ExtraBold : FontWeights.Normal;
        }
        toggle.Checked += (_, _) => { _settings.Mode = "help"; UpdateModeAppearance(true); _chatWindow?.RefreshCapabilities(); _ = SaveSettingsAsync(_settings); };
        toggle.Unchecked += (_, _) => { _settings.Mode = "chat"; UpdateModeAppearance(false); _chatWindow?.RefreshCapabilities(); _ = SaveSettingsAsync(_settings); };
        row.Children.Add(chatLabel);
        row.Children.Add(toggle);
        row.Children.Add(helpLabel);
        modeHost.Header = row;
        menu.Opened += (_, _) =>
        {
            bool helpSelected = _settings.Mode.Equals("help", StringComparison.OrdinalIgnoreCase);
            toggle.IsChecked = helpSelected;
            UpdateModeAppearance(helpSelected);
        };
        menu.Items.Add(modeHost);
        var settings = new MenuItem { Header = "Settings" };
        settings.Click += (_, _) => OpenSettings();
        menu.Items.Add(settings);
        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) =>
        {
            Point resetPosition = GetDefaultAvatarPosition();
            _idleAvatarPosition = resetPosition;
            _settings.Left = resetPosition.X;
            _settings.Top = resetPosition.Y;
            if (_chatWindow?.IsVisible == true) PositionChatNearIdleAvatar(_chatWindow, resetPosition);
            else SetAvatarPosition(resetPosition);
            _ = SavePositionAsync();
        };
        menu.Items.Add(reset);
        var hide = new MenuItem { Header = "Hide" };
        hide.Click += (_, _) => HideByUser();
        menu.Items.Add(hide);
        return menu;
    }

    private void AnimateSprite(BitmapImage sheet)
    {
        if (_spriteHovered) return;
        int[] frames = _animationState switch
        {
            "think" => new[] { 5, 7, 5, 6 },
            "working" => new[] { 8, 9, 10, 11 },
            "celebrate" => new[] { 12, 13, 14, 15 },
            _ => new[] { 0 }
        };
        long tick = Environment.TickCount64;
        if (_lastSpriteFrameTick == 0 || tick - _lastSpriteFrameTick >= 115)
        {
            _lastSpriteFrameTick = tick;
            int frame = frames[_animationFrame++ % frames.Length];
            if (frame != _renderedFrame)
            {
                _renderedFrame = frame;
                double cellWidth = Math.Max(1, sheet.PixelWidth / 4d), cellHeight = Math.Max(1, sheet.PixelHeight / 4d);
                _spriteBrush.Viewbox = new Rect((frame % 4) * cellWidth, (frame / 4) * cellHeight, cellWidth, cellHeight);
            }
        }
        if (_sprite.RenderTransform is TransformGroup group)
        {
            double elapsed = (Stopwatch.GetTimestamp() - _animationEpoch) / (double)Stopwatch.Frequency;
            double wave = Math.Sin(elapsed * Math.PI * 2d / 4.2d);
            ((RotateTransform)group.Children[0]).Angle = wave * .35;
            ((TranslateTransform)group.Children[1]).Y = -1d + wave;
        }
    }

    private void SetReaction(string state, int milliseconds)
    {
        _animationState = state;
        _animationFrame = 0;
        _lastSpriteFrameTick = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); _animationState = "idle"; _animationFrame = 0; _lastSpriteFrameTick = 0; };
        timer.Start();
    }

    private bool IsAuthenticated => !string.IsNullOrWhiteSpace(_accessToken) && !string.IsNullOrWhiteSpace(_signedInUserName);

    private string AuthFilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "heirowLLM",
        "ChickenChaser",
        "workstation-auth.json");

    private void OpenChat() => OpenChatWithMessage(null);

    private void ScheduleSingleClick()
    {
        _singleClickTimer?.Stop();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _singleClickTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (ReferenceEquals(_singleClickTimer, timer)) _singleClickTimer = null;
            ToggleChat();
        };
        timer.Start();
    }

    private void ToggleChat()
    {
        if (_chatWindow?.IsVisible == true) _chatWindow.Hide();
        else OpenChat();
    }

    private void ToggleCompactAvatar()
    {
        _settings.Compact = !_settings.Compact;
        ApplyCompactAvatar(_settings.Compact);
        if (_chatWindow?.IsVisible == true) PositionAvatarForChat(_chatWindow);
        else
        {
            _idleAvatarPosition = new Point(Left, Top);
            _settings.Left = Left;
            _settings.Top = Top;
        }
        _ = SaveSettingsAsync(_settings);
    }

    private void ApplyCompactAvatar(bool compact)
    {
        double right = Left + Width;
        double bottom = Top + Height;
        Width = compact ? CompactAvatarWidth : DefaultAvatarWidth;
        Height = compact ? CompactAvatarHeight : DefaultAvatarHeight;
        Left = Math.Clamp(right - Width, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = Math.Clamp(bottom - Height, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        _sprite.Clip = new RectangleGeometry(new Rect(compact ? 0 : 1, compact ? 0 : 1, Math.Max(1, Width - (compact ? 0 : 2)), Math.Max(1, Height - (compact ? 3 : 15))));
        _sprite.ToolTip = _offline
            ? (compact ? "Chicken Chaser is offline and compact — double-click to restore" : "Chicken Chaser is offline — drag to move")
            : (compact ? "Chicken Chaser compact — double-click to restore" : "Chicken Chaser — drag to move");
        RepositionNotifications();
    }

    private double AvatarFootInset => _settings.Compact ? 3 : 14;

    private Point GetDefaultAvatarPosition()
    {
        Rect workArea = SystemParameters.WorkArea;
        return new Point(
            workArea.Right - Width - ScreenEdgeMargin,
            workArea.Bottom - Height + AvatarFootInset);
    }

    private Point GetIdleAvatarPosition()
    {
        if (_idleAvatarPosition is Point idle) return idle;
        if (_settings.Left >= 0 && _settings.Top >= 0) return new Point(_settings.Left, _settings.Top);
        return new Point(Left, Top);
    }

    private void SetAvatarPosition(Point position)
    {
        Left = Math.Clamp(position.X, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = Math.Clamp(position.Y, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height);
        RepositionNotifications();
    }

    private void PositionChatNearIdleAvatar(ChickenChaserChatWindow chat, Point idlePosition)
    {
        if (_positioningChatLayout) return;
        Rect workArea = GetCurrentMonitorWorkArea();
        double chatWidth = chat.ActualWidth > 0 ? chat.ActualWidth : chat.Width;
        double chatHeight = chat.ActualHeight > 0 ? chat.ActualHeight : chat.Height;
        double left = Math.Clamp(
            idlePosition.X + Width / 2 - chatWidth / 2,
            workArea.Left + ScreenEdgeMargin,
            Math.Max(workArea.Left + ScreenEdgeMargin, workArea.Right - chatWidth - ScreenEdgeMargin));
        double top = Math.Max(workArea.Top + ScreenEdgeMargin, workArea.Bottom - chatHeight - ScreenEdgeMargin);
        _positioningChatLayout = true;
        try
        {
            chat.Left = left;
            chat.Top = top;
        }
        finally { _positioningChatLayout = false; }
        PositionAvatarForChat(chat);
    }

    private void PositionAvatarForChat(ChickenChaserChatWindow chat)
    {
        if (_positioningChatLayout || !ReferenceEquals(_chatWindow, chat)) return;
        if (!_avatarDockedToChat)
        {
            _idleAvatarPosition = new Point(Left, Top);
            _avatarDockedToChat = true;
        }

        Rect workArea = GetMonitorWorkArea(chat);
        double chatWidth = chat.ActualWidth > 0 ? chat.ActualWidth : chat.Width;
        double chatHeight = chat.ActualHeight > 0 ? chat.ActualHeight : chat.Height;
        double avatarHeightAboveFeet = Math.Max(1, Height - AvatarFootInset);
        double perchedTop = chat.Top - avatarHeightAboveFeet + ChatPerchOverlap;
        double combinedHeight = chatHeight + avatarHeightAboveFeet - ChatPerchOverlap;
        bool canPerchOnTop = combinedHeight <= workArea.Height - ScreenEdgeMargin * 2 && perchedTop >= workArea.Top + ScreenEdgeMargin;
        Point target;
        if (canPerchOnTop)
        {
            target = new Point(
                Math.Clamp(chat.Left + (chatWidth - Width) / 2, workArea.Left + ScreenEdgeMargin, Math.Max(workArea.Left + ScreenEdgeMargin, workArea.Right - Width - ScreenEdgeMargin)),
                perchedTop);
        }
        else
        {
            double freeLeft = chat.Left - workArea.Left;
            double freeRight = workArea.Right - (chat.Left + chatWidth);
            bool useRight = freeRight >= freeLeft;
            double sideLeft = useRight
                ? chat.Left + chatWidth - ChatSideOverlap
                : chat.Left - Width + ChatSideOverlap;
            target = new Point(
                Math.Clamp(sideLeft, workArea.Left + ScreenEdgeMargin, Math.Max(workArea.Left + ScreenEdgeMargin, workArea.Right - Width - ScreenEdgeMargin)),
                Math.Clamp(chat.Top + 18, workArea.Top + ScreenEdgeMargin, Math.Max(workArea.Top + ScreenEdgeMargin, workArea.Bottom - Height - ScreenEdgeMargin)));
        }

        _positioningChatLayout = true;
        try { SetAvatarPosition(target); }
        finally { _positioningChatLayout = false; }
    }

    private void RestoreIdleAvatarPosition()
    {
        if (!_avatarDockedToChat) return;
        Point idle = GetIdleAvatarPosition();
        _avatarDockedToChat = false;
        SetAvatarPosition(idle);
    }

    private void OpenChatWithMessage(string? applicationMessage) => _ = OpenChatWithMessageAsync(applicationMessage);

    private async Task OpenChatWithMessageAsync(string? applicationMessage)
    {
        await RestoreRememberedLoginAsync().ConfigureAwait(true);
        if (!IsAuthenticated)
        {
            ShowLogin(applicationMessage);
            return;
        }
        _loginWindow?.Close();
        _loginWindow = null;
        if (_chatWindow == null || !_chatWindow.IsLoaded)
        {
            ChickenChaserChatWindow chat = new(this, _settings, _signedInUserName, SendChatAsync, SaveSettingsAsync, LoadModelChoicesAsync, _openInHeirowLlm, LogoutAsync);
            _chatWindow = chat;
            Point idlePosition = GetIdleAvatarPosition();
            PositionChatNearIdleAvatar(chat, idlePosition);
            chat.LocationChanged += (_, _) => PositionAvatarForChat(chat);
            chat.SizeChanged += (_, _) => PositionAvatarForChat(chat);
            chat.IsVisibleChanged += (_, _) =>
            {
                if (chat.IsVisible) PositionAvatarForChat(chat);
                else RestoreIdleAvatarPosition();
            };
            chat.Closed += (_, _) =>
            {
                RestoreIdleAvatarPosition();
                if (ReferenceEquals(_chatWindow, chat)) _chatWindow = null;
            };
        }
        if (!string.IsNullOrWhiteSpace(applicationMessage)) _chatWindow.AddApplicationMessage(applicationMessage);
        _chatWindow.ShowChat();
        PositionAvatarForChat(_chatWindow);
        SetReaction("celebrate", 900);
    }

    private void OpenSettings()
    {
        _ = OpenSettingsAsync();
    }

    private async Task OpenSettingsAsync()
    {
        await OpenChatWithMessageAsync(null).ConfigureAwait(true);
        if (IsAuthenticated) _chatWindow?.ShowSettings();
    }

    private void ShowLogin(string? applicationMessage)
    {
        _chatWindow?.Close();
        _chatWindow = null;
        if (_loginWindow == null || !_loginWindow.IsLoaded)
        {
            _loginWindow = new ChickenChaserLoginWindow(this, LoginAsync);
            _loginWindow.Closed += (_, _) => _loginWindow = null;
        }
        if (!string.IsNullOrWhiteSpace(applicationMessage)) _loginWindow.PendingApplicationMessage = applicationMessage;
        _loginWindow.ShowLogin(_signedInUserName);
    }

    private async Task LoginAsync(string userName, string password, bool remember)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Enter your Web Chat user name and password.");
        using HttpRequestMessage request = CreateUnauthenticatedApiRequest(HttpMethod.Post, "api/web-auth/login", new { username = userName.Trim(), password, remember });
        using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true);
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ExtractApiError(json, "Chicken Chaser sign-in failed."));
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        JsonElement root = document.RootElement;
        string token = ReadJsonString(root, "accessToken", "token", "bearerToken");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("The Workstation login did not return an access token.");
        _accessToken = token;
        _signedInUserName = ReadJsonString(root, "username", "userName", "user");
        if (string.IsNullOrWhiteSpace(_signedInUserName)) _signedInUserName = userName.Trim();
        _authExpiresUtc = ReadJsonString(root, "expiresUtc", "expirationUtc");
        if (remember) SaveRememberedLogin(); else ClearRememberedLogin();
        await LoadSettingsAsync().ConfigureAwait(true);
        string? pending = _loginWindow?.PendingApplicationMessage;
        _loginWindow?.Close();
        _loginWindow = null;
        await OpenChatWithMessageAsync(pending).ConfigureAwait(true);
    }

    private async Task RestoreRememberedLoginAsync()
    {
        if (_authRestoreAttempted || IsAuthenticated) return;
        await _authGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_authRestoreAttempted || IsAuthenticated) return;
            _authRestoreAttempted = true;
            LoadRememberedLogin();
            if (!IsAuthenticated) return;
            using HttpRequestMessage request = CreateApiRequest(HttpMethod.Get, "api/web-auth/session");
            using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true);
            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                ResetAuthentication(clearSaved: true);
                return;
            }
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("authenticated", out JsonElement authenticated) || authenticated.ValueKind != JsonValueKind.True)
            {
                ResetAuthentication(clearSaved: true);
                return;
            }
            _signedInUserName = ReadJsonString(root, "username", "userName", "user");
            _authExpiresUtc = ReadJsonString(root, "expiresUtc", "expirationUtc");
            SaveRememberedLogin();
        }
        catch
        {
            ResetAuthentication(clearSaved: true);
        }
        finally { _authGate.Release(); }
    }

    private async Task LogoutAsync()
    {
        if (IsAuthenticated)
        {
            try
            {
                using HttpRequestMessage request = CreateApiRequest(HttpMethod.Post, "api/web-auth/logout", new { });
                using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true);
            }
            catch { }
        }
        ResetAuthentication(clearSaved: true);
        _chatWindow?.Close();
        _chatWindow = null;
        ShowLogin(null);
    }

    private void LoadRememberedLogin()
    {
        try
        {
            if (!System.IO.File.Exists(AuthFilePath)) return;
            using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllText(AuthFilePath));
            JsonElement root = document.RootElement;
            string protectedToken = ReadJsonString(root, "protectedToken");
            if (string.IsNullOrWhiteSpace(protectedToken)) return;
            byte[] encrypted = Convert.FromBase64String(protectedToken);
            byte[] tokenBytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try { _accessToken = Encoding.UTF8.GetString(tokenBytes); }
            finally { CryptographicOperations.ZeroMemory(tokenBytes); }
            _signedInUserName = ReadJsonString(root, "userName", "username");
            _authExpiresUtc = ReadJsonString(root, "expiresUtc");
        }
        catch { ResetAuthentication(clearSaved: true); }
    }

    private void SaveRememberedLogin()
    {
        if (!IsAuthenticated) return;
        string? directory = System.IO.Path.GetDirectoryName(AuthFilePath);
        if (!string.IsNullOrWhiteSpace(directory)) System.IO.Directory.CreateDirectory(directory);
        byte[] tokenBytes = Encoding.UTF8.GetBytes(_accessToken);
        try
        {
            byte[] encrypted = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
            string json = JsonSerializer.Serialize(new { userName = _signedInUserName, protectedToken = Convert.ToBase64String(encrypted), expiresUtc = _authExpiresUtc });
            System.IO.File.WriteAllText(AuthFilePath, json, new UTF8Encoding(false));
        }
        finally { CryptographicOperations.ZeroMemory(tokenBytes); }
    }

    private void ClearRememberedLogin()
    {
        try { if (System.IO.File.Exists(AuthFilePath)) System.IO.File.Delete(AuthFilePath); } catch { }
    }

    private void ResetAuthentication(bool clearSaved)
    {
        _accessToken = "";
        _signedInUserName = "";
        _authExpiresUtc = "";
        if (clearSaved) ClearRememberedLogin();
    }

    private async Task<ChickenChaserStreamResult> SendChatAsync(string prompt, string mode, IReadOnlyList<ChickenChaserChatAttachment> attachments, Action<string> onDelta, Action<string> onThought)
    {
        SetReaction("think", 5000);
        var payload = new
        {
            prompt,
            mode,
            context = new { activeView = "desktop", activeTitle = "heirowLLM Workstation", controls = Array.Empty<object>() },
            attachments = attachments.Select(item => new { name = item.Name, mediaType = item.MediaType, textPreview = item.TextPreview, dataUrl = item.DataUrl }).ToArray()
        };
        using HttpRequestMessage request = CreateApiRequest(HttpMethod.Post, "api/chickenchaser/chat/stream", payload);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(true);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
        var rendered = new StringBuilder();
        string finalReply = "";
        string thoughtSummary = "";
        await using System.IO.Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(true);
        using var reader = new System.IO.StreamReader(responseStream, Encoding.UTF8);
        while (await reader.ReadLineAsync().ConfigureAwait(true) is string line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;
            string type = root.TryGetProperty("type", out JsonElement typeNode) ? typeNode.GetString() ?? "" : "";
            if (type.Equals("delta", StringComparison.OrdinalIgnoreCase))
            {
                string delta = root.TryGetProperty("text", out JsonElement textNode) ? textNode.GetString() ?? "" : "";
                rendered.Append(delta);
                onDelta(delta);
            }
            else if (type.Equals("thought", StringComparison.OrdinalIgnoreCase))
            {
                thoughtSummary = root.TryGetProperty("text", out JsonElement thoughtNode) ? thoughtNode.GetString() ?? "" : "";
                onThought(thoughtSummary);
            }
            else if (type.Equals("done", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("result", out JsonElement result))
            {
                finalReply = ReadChickenChaserReply(result);
                if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("thoughtSummary", out JsonElement finalThought))
                    thoughtSummary = finalThought.GetString() ?? thoughtSummary;
            }
            else if (type.Equals("error", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(root.TryGetProperty("error", out JsonElement error) ? error.GetString() : "Chicken Chaser could not answer.");
        }
        if (string.IsNullOrWhiteSpace(finalReply)) finalReply = rendered.Length == 0 ? "How can I help?" : rendered.ToString();
        if (rendered.Length == 0) onDelta(finalReply);
        if (!string.IsNullOrWhiteSpace(thoughtSummary)) onThought(thoughtSummary);
        SetReaction("celebrate", 1600);
        return new ChickenChaserStreamResult(finalReply, thoughtSummary);
    }

    private static string ReadChickenChaserReply(JsonElement value)
    {
        JsonElement current = value;
        for (int depth = 0; depth < 4; depth++)
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("reply", out JsonElement reply)) current = reply;
            else if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty("response", out JsonElement response)) current = response;
            else break;
            if (current.ValueKind == JsonValueKind.String)
            {
                string text = current.GetString() ?? "";
                try
                {
                    using JsonDocument nested = JsonDocument.Parse(text);
                    if (nested.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        string resolved = ReadChickenChaserReply(nested.RootElement);
                        if (!string.IsNullOrWhiteSpace(resolved) && !resolved.Equals(text, StringComparison.Ordinal)) return resolved;
                    }
                }
                catch (JsonException) { }
                return text;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() ?? "" : current.ToString();
    }

    private async Task<IReadOnlyList<ChickenChaserModelChoice>> LoadModelChoicesAsync()
    {
        var choices = new List<ChickenChaserModelChoice> { new("auto", "Auto · Instant Router") };
        foreach (string modelId in _installedChatModels() ?? Array.Empty<string>())
        {
            string id = (modelId ?? "").Trim();
            if (id.Length == 0 || ChickenChaserModelLooksNonText(id) || choices.Any(choice => choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            choices.Add(new ChickenChaserModelChoice(id, id + " · installed"));
        }
        try
        {
            using HttpRequestMessage request = CreateApiRequest(HttpMethod.Get, "api/chickenchaser/models");
            using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            if (!response.IsSuccessStatusCode || !json.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array) return choices;
            foreach (JsonElement model in models.EnumerateArray())
            {
                string id = model.TryGetProperty("id", out JsonElement idNode) ? idNode.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                string label = model.TryGetProperty("displayName", out JsonElement labelNode) ? labelNode.GetString() ?? id : id;
                bool loaded = model.TryGetProperty("isLoaded", out JsonElement loadedNode) && loadedNode.ValueKind == JsonValueKind.True;
                bool compatible = model.TryGetProperty("chickenChaserCompatible", out JsonElement compatibleNode) && compatibleNode.ValueKind == JsonValueKind.True;
                bool disabled = model.TryGetProperty("disabled", out JsonElement disabledNode) && disabledNode.ValueKind == JsonValueKind.True;
                bool supportsVision = ReadChickenChaserModelFlag(model, "supportsImages", "supportsVision", "vision", "images");
                bool supportsTools = ReadChickenChaserModelFlag(model, "supportsTools", "tools", "toolUse", "trainedForToolUse", "trained_for_tool_use");
                supportsVision = supportsVision || ChickenChaserModelHasMarker(model, "vision", "image", "vlm", "multimodal");
                supportsTools = supportsTools || ChickenChaserModelHasMarker(model, "tool-use", "tool_use", "tools", "function-calling", "function_calling");
                string suffix = !compatible ? " · unavailable for chat" : loaded ? " · loaded" : disabled ? " · enable in Models" : "";
                var choice = new ChickenChaserModelChoice(id, label + suffix, compatible && !disabled, supportsVision, supportsTools);
                int existing = choices.FindIndex(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) choices[existing] = choice; else choices.Add(choice);
            }
        }
        catch { }
        bool autoSupportsAttachments = choices.Skip(1).Any(choice => choice.IsEnabled && choice.SupportsVision && choice.SupportsTools);
        choices[0] = new ChickenChaserModelChoice("auto", "Auto · Instant Router", true, autoSupportsAttachments, autoSupportsAttachments);
        return choices;
    }

    private static bool ReadChickenChaserModelFlag(JsonElement model, params string[] names)
    {
        foreach (string name in names)
            if (model.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return value.GetBoolean();
        if (model.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            foreach (string name in names)
                if (capabilities.TryGetProperty(name, out JsonElement value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return value.GetBoolean();
        return false;
    }

    private static bool ChickenChaserModelHasMarker(JsonElement model, params string[] markers)
    {
        foreach (string propertyName in new[] { "type", "architecture", "tags" })
        {
            if (!model.TryGetProperty(propertyName, out JsonElement value)) continue;
            string text = value.ValueKind == JsonValueKind.Array ? string.Join(' ', value.EnumerateArray().Select(item => item.ToString())) : value.ToString();
            if (markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;
        }
        if (model.TryGetProperty("capabilities", out JsonElement capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in capabilities.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.True && markers.Any(marker => property.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))) return true;
        return false;
    }

    private static bool ChickenChaserModelLooksNonText(string value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        string[] markers =
        {
            "ace-step", "acestep", "stable-diffusion", "stable diffusion", "diffusion-inpainting",
            "real-esrgan", "realesrgan", "birefnet", "sam-vit", "audio-generation", "audio_generation",
            "image-generation", "image_generation", "video-generation", "video_generation", "text-to-speech",
            "speech-to-text", "embedding", "reranker"
        };
        return markers.Any(text.Contains);
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            using HttpRequestMessage request = CreateApiRequest(HttpMethod.Get, "api/chickenchaser/settings");
            using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            if (response.IsSuccessStatusCode && json.RootElement.TryGetProperty("settings", out JsonElement node)) _settings = node.Deserialize<ChickenChaserSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? _settings;
            ApplyCompactAvatar(_settings.Compact);
            Point idlePosition = _settings.Left >= 0 && _settings.Top >= 0
                ? new Point(
                    Clamp(_settings.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width),
                    Clamp(_settings.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height))
                : GetDefaultAvatarPosition();
            _idleAvatarPosition = idlePosition;
            SetAvatarPosition(idlePosition);
            if (_settings.Hidden) Hide();
        }
        catch { }
    }

    private Task SavePositionAsync()
    {
        Point position = _avatarDockedToChat ? GetIdleAvatarPosition() : new Point(Left, Top);
        _idleAvatarPosition = position;
        _settings.Left = position.X;
        _settings.Top = position.Y;
        return SaveSettingsAsync(_settings);
    }

    private async Task SaveSettingsAsync(ChickenChaserSettings settings)
    {
        _settings = settings;
        using HttpRequestMessage request = CreateApiRequest(HttpMethod.Post, "api/chickenchaser/settings", settings);
        try { using HttpResponseMessage response = await _http.SendAsync(request).ConfigureAwait(true); } catch { }
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path, object? body = null)
    {
        if (!IsAuthenticated) throw new UnauthorizedAccessException("Sign in to Chicken Chaser with your Web Chat account first.");
        HttpRequestMessage request = CreateUnauthenticatedApiRequest(method, path, body);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken.Trim());
        request.Headers.TryAddWithoutValidation("X-SocketJack-Auth", _accessToken.Trim());
        request.Headers.TryAddWithoutValidation("X-SocketJack-User", _signedInUserName.Trim());
        request.Headers.TryAddWithoutValidation("X-SocketJack-Username", _signedInUserName.Trim());
        return request;
    }

    private HttpRequestMessage CreateUnauthenticatedApiRequest(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(_proxy.ChatServerUrl), path));
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
        return request;
    }

    private static string ReadJsonString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement node) && node.ValueKind == JsonValueKind.String)
                return node.GetString() ?? "";
        return "";
    }

    private static string ExtractApiError(string json, string fallback)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            string value = ReadJsonString(document.RootElement, "message", "error", "detail");
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
        catch { return fallback; }
    }

    private void HideByUser()
    {
        _settings.Hidden = true;
        _ = SaveSettingsAsync(_settings);
        CloseAuxiliaryWindows();
        Hide();
        HiddenByUser?.Invoke(this, EventArgs.Empty);
    }

    private void CloseAuxiliaryWindows()
    {
        foreach (ChickenChaserNotificationWindow item in _notifications.ToArray()) item.Close();
        _notifications.Clear();
        _loginWindow?.Close();
        _loginWindow = null;
        _chatWindow?.Close();
        _chatWindow = null;
    }

    private void RepositionNotifications()
    {
        ChickenChaserNotificationWindow[] visible = _notifications
            .Where(item => item.IsLoaded || !item.IsVisible)
            .Reverse()
            .ToArray();
        if (visible.Length == 0) return;

        Rect workArea = GetCurrentMonitorWorkArea();
        double totalHeight = visible.Sum(item => item.Height) + Math.Max(0, visible.Length - 1) * 8;
        double widest = visible.Max(item => item.Width);
        bool canUseLeft = Left - workArea.Left >= widest + 12;
        if (canUseLeft)
        {
            double top = Math.Min(workArea.Bottom - 8, Top + Height);
            if (top - totalHeight < workArea.Top + 8)
            {
                double down = Math.Min(
                    Math.Max(workArea.Top + 8, Top),
                    Math.Max(workArea.Top + 8, workArea.Bottom - totalHeight - 8));
                foreach (ChickenChaserNotificationWindow item in visible)
                {
                    item.Left = Math.Max(workArea.Left + 8, Left - item.Width - 12);
                    item.Top = down;
                    down += item.Height + 8;
                }
                return;
            }
            foreach (ChickenChaserNotificationWindow item in visible)
            {
                item.Left = Math.Max(workArea.Left + 8, Left - item.Width - 12);
                item.Top = Math.Max(workArea.Top + 8, top - item.Height);
                top = item.Top - 8;
            }
            return;
        }

        double above = Top - 12;
        if (above - totalHeight < workArea.Top + 8)
        {
            double down = Math.Min(
                Math.Max(workArea.Top + 8, Top + Height + 12),
                Math.Max(workArea.Top + 8, workArea.Bottom - totalHeight - 8));
            foreach (ChickenChaserNotificationWindow item in visible)
            {
                item.Left = Math.Clamp(Left + Width - item.Width, workArea.Left + 8, workArea.Right - item.Width - 8);
                item.Top = down;
                down += item.Height + 8;
            }
            return;
        }
        foreach (ChickenChaserNotificationWindow item in visible)
        {
            item.Left = Math.Clamp(Left + Width - item.Width, workArea.Left + 8, workArea.Right - item.Width - 8);
            item.Top = Math.Max(workArea.Top + 8, above - item.Height);
            above = item.Top - 8;
        }
    }

    private Rect GetCurrentMonitorWorkArea() => GetMonitorWorkArea(this);

    private static Rect GetMonitorWorkArea(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        System.Drawing.Rectangle pixels = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(window);
        return new Rect(
            pixels.Left / dpi.DpiScaleX,
            pixels.Top / dpi.DpiScaleY,
            pixels.Width / dpi.DpiScaleX,
            pixels.Height / dpi.DpiScaleY);
    }

    private static BitmapSource CreateGraySpriteSheet(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();
        int stride = converted.PixelWidth * 4;
        byte[] pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        for (int index = 0; index < pixels.Length; index += 4)
        {
            byte gray = (byte)Math.Clamp((int)Math.Round(pixels[index] * .0722 + pixels[index + 1] * .7152 + pixels[index + 2] * .2126), 0, 255);
            pixels[index] = gray;
            pixels[index + 1] = gray;
            pixels[index + 2] = gray;
        }
        BitmapSource result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));
    private static string Compact(string value, int max) { string text = string.Join(" ", (value ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0)); return text.Length <= max ? text : text[..Math.Max(1, max - 1)].TrimEnd() + "…"; }
}

internal sealed record ChickenChaserModelChoice(string Id, string Label, bool IsEnabled = true, bool SupportsVision = false, bool SupportsTools = false)
{
    public override string ToString() => Label;
}

internal sealed record ChickenChaserChatAttachment(string Name, string MediaType, long Size, string TextPreview, string DataUrl)
{
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
}

internal sealed record ChickenChaserStreamResult(string Reply, string ThoughtSummary);

internal sealed record ChickenChaserPendingMessage(string Prompt, string Mode, IReadOnlyList<ChickenChaserChatAttachment> Attachments);

internal sealed record ChickenChaserThinkingPresentation(Border Host, TextBlock Label, TextBlock Summary, Ellipse Throbber, RotateTransform Rotation, TextBlock Checkmark);

internal sealed class ChickenChaserLoginWindow : Window
{
    private readonly Func<string, string, bool, Task> _login;
    private readonly TextBox _userName = new();
    private readonly PasswordBox _password = new();
    private readonly CheckBox _remember = new() { Content = "Remember me for 30 days", IsChecked = true };
    private readonly TextBlock _status = new();
    private readonly Button _signIn;
    private bool _signingIn;

    public string? PendingApplicationMessage { get; set; }

    public ChickenChaserLoginWindow(Window avatar, Func<string, string, bool, Task> login)
    {
        _login = login;
        Title = "Sign in to Chicken Chaser";
        Width = 430;
        Height = 455;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = false;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Left = Math.Max(SystemParameters.WorkArea.Left + 8, avatar.Left - Width + avatar.Width);
        Top = Math.Min(SystemParameters.WorkArea.Bottom - Height - 8, avatar.Top + avatar.Height + 8);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, CornerRadius = new CornerRadius(13), GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(7), UseAeroCaptionButtons = false });
        ChickenChaserWindowsBackdrop.EnableAcrylic(this);

        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox, Opacity = .8 };
        foreach ((Color color, double offset) in new[] { (Colors.DeepPink, 0d), (Colors.Orange, .18), (Colors.Yellow, .36), (Colors.SpringGreen, .54), (Colors.DeepSkyBlue, .72), (Colors.MediumPurple, .88), (Colors.DeepPink, 1d) }) rainbow.GradientStops.Add(new GradientStop(color, offset));
        var rotate = new RotateTransform(); rainbow.RelativeTransform = rotate; rotate.CenterX = rotate.CenterY = .5; rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
        var border = new Border { CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), BorderBrush = rainbow, Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)), Padding = new Thickness(18) };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Chicken Chaser", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Width = 34, Height = 34, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(128, 153, 27, 27)), BorderBrush = new SolidColorBrush(Color.FromRgb(220, 38, 38)), Foreground = Brushes.White, FontSize = 20, Cursor = Cursors.Hand, ToolTip = "Close Chicken Chaser sign-in" };
        close.Click += (_, _) => Hide();
        Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        root.Children.Add(header);

        var form = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 18, 16, 8) };
        form.Children.Add(new TextBlock { Text = "Sign in before chatting", Foreground = Brushes.White, FontSize = 24, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        form.Children.Add(new TextBlock { Text = "Use the same local account as the Web Chat UI. Your memories, projects, and sessions stay under that account.", Foreground = new SolidColorBrush(Color.FromRgb(170, 183, 202)), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 8, 0, 18) });
        form.Children.Add(new TextBlock { Text = "User name", Foreground = new SolidColorBrush(Color.FromRgb(170, 183, 202)), Margin = new Thickness(0, 0, 0, 4) });
        ApplyTextFieldStyle(_userName); form.Children.Add(_userName);
        form.Children.Add(new TextBlock { Text = "Password", Foreground = new SolidColorBrush(Color.FromRgb(170, 183, 202)), Margin = new Thickness(0, 12, 0, 4) });
        _password.Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)); _password.Foreground = Brushes.White; _password.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)); _password.Padding = new Thickness(9); form.Children.Add(_password);
        _remember.Foreground = Brushes.White; _remember.Margin = new Thickness(0, 12, 0, 8); _remember.Cursor = Cursors.Hand; form.Children.Add(_remember);
        _status.Foreground = new SolidColorBrush(Color.FromRgb(248, 113, 113)); _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 2, 0, 8); form.Children.Add(_status);
        _signIn = new Button { Content = "Sign in", Padding = new Thickness(18, 9, 18, 9), HorizontalAlignment = HorizontalAlignment.Right, Background = new SolidColorBrush(Color.FromRgb(91, 53, 160)), BorderBrush = new SolidColorBrush(Color.FromRgb(124, 88, 210)), Foreground = Brushes.White, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand };
        _signIn.Click += async (_, _) => await SubmitAsync();
        _password.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SubmitAsync(); } };
        form.Children.Add(_signIn);
        Grid.SetRow(form, 1); root.Children.Add(form);
        border.Child = root; Content = border;
    }

    public void ShowLogin(string userName)
    {
        if (string.IsNullOrWhiteSpace(_userName.Text) && !string.IsNullOrWhiteSpace(userName)) _userName.Text = userName;
        _status.Text = "";
        _password.Clear();
        if (!IsVisible) Show();
        Activate();
        if (string.IsNullOrWhiteSpace(_userName.Text)) _userName.Focus(); else _password.Focus();
    }

    private async Task SubmitAsync()
    {
        if (_signingIn) return;
        _signingIn = true; _signIn.IsEnabled = false; _signIn.Content = "Signing in…"; _status.Text = "";
        try { await _login(_userName.Text, _password.Password, _remember.IsChecked == true); }
        catch (Exception ex) { _status.Text = ex.Message; _password.SelectAll(); _password.Focus(); }
        finally { _signingIn = false; _signIn.IsEnabled = true; _signIn.Content = "Sign in"; }
    }

    private static void ApplyTextFieldStyle(TextBox box)
    {
        box.Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)); box.Foreground = Brushes.White; box.CaretBrush = Brushes.White; box.BorderBrush = new SolidColorBrush(Color.FromRgb(51, 65, 85)); box.Padding = new Thickness(9);
    }
}

internal sealed class ChickenChaserNotificationWindow : Window
{
    private Storyboard? _lifetimeStoryboard;
    private readonly TextBlock _repetitionBadge;
    private readonly double _lifetimeMilliseconds;
    private int _repetitions = 1;
    private bool _hovered;

    public ChickenChaserNotificationWindow(string source, string message, string category, Action<string?> openConversation, Action<string> ignoreCategory)
    {
        Width = 354; Height = Math.Clamp(62 + message.Length / 5, 74, 138); WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false; Topmost = true; ShowActivated = false;
        var border = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(71, 85, 105)), Background = new SolidColorBrush(Color.FromArgb(232, 11, 16, 25)), Padding = new Thickness(11), Cursor = Cursors.Hand };
        _lifetimeMilliseconds = Math.Clamp(1800 + message.Length * 14, 2600, 6000);
        var grid = new Grid(); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        var text = new StackPanel(); text.Children.Add(new TextBlock { Text = source, Foreground = new SolidColorBrush(Color.FromRgb(196, 181, 253)), FontWeight = FontWeights.Bold, FontSize = 11 }); text.Children.Add(new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        _repetitionBadge = new TextBlock { Visibility = Visibility.Collapsed, Text = "x1", Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 11, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        var ignore = new Button { Content = "−", Width = 32, Height = 32, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromRgb(91, 33, 182)), Foreground = Brushes.White, BorderThickness = new Thickness(0), FontSize = 20, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand, ToolTip = "Ignore this notification type" };
        ignore.MouseEnter += (_, _) => ignore.Background = new SolidColorBrush(Color.FromRgb(124, 58, 237)); ignore.MouseLeave += (_, _) => ignore.Background = new SolidColorBrush(Color.FromRgb(91, 33, 182)); ignore.Click += (_, e) => { e.Handled = true; ignoreCategory(category); Close(); };
        var close = new Button { Content = "×", Width = 32, Height = 32, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(128, 220, 38, 38)), Foreground = Brushes.White, BorderThickness = new Thickness(0), Opacity = .5, FontSize = 18, FontWeight = FontWeights.Bold, Cursor = Cursors.Hand };
        close.MouseEnter += (_, _) => close.Opacity = 1; close.MouseLeave += (_, _) => close.Opacity = .5; close.Click += (_, e) => { e.Handled = true; Close(); };
        Grid.SetColumn(_repetitionBadge, 1); Grid.SetColumn(ignore, 2); Grid.SetColumn(close, 3); grid.Children.Add(text); grid.Children.Add(_repetitionBadge); grid.Children.Add(ignore); grid.Children.Add(close); border.Child = grid; Content = border;
        border.MouseLeftButtonUp += (_, _) => { openConversation(source + ": " + message); Close(); };
        border.MouseEnter += (_, _) => { _hovered = true; _lifetimeStoryboard?.Pause(this); };
        border.MouseLeave += (_, _) => { _hovered = false; _lifetimeStoryboard?.Resume(this); };
        Loaded += (_, _) => StartLifetime();
    }

    public void IncrementRepetitions()
    {
        _repetitions++;
        _repetitionBadge.Text = "x" + _repetitions.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _repetitionBadge.Visibility = Visibility.Visible;
        _repetitionBadge.Foreground = new SolidColorBrush(RepetitionColor(_repetitions));
        StartLifetime();
    }

    private void StartLifetime()
    {
        _lifetimeStoryboard?.Stop(this);
        Opacity = 1;
        double life = _lifetimeMilliseconds;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Math.Min(1800, life * .2))) { BeginTime = TimeSpan.FromMilliseconds(life - Math.Min(1800, life * .2)) };
        var movement = new DoubleAnimation(Top + 12, Top - 36, TimeSpan.FromMilliseconds(life)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, this); Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        Storyboard.SetTarget(movement, this); Storyboard.SetTargetProperty(movement, new PropertyPath(TopProperty));
        _lifetimeStoryboard = new Storyboard(); _lifetimeStoryboard.Children.Add(fade); _lifetimeStoryboard.Children.Add(movement); _lifetimeStoryboard.Completed += (_, _) => Close(); _lifetimeStoryboard.Begin(this, true);
        if (_hovered) _lifetimeStoryboard.Pause(this);
    }

    private static Color RepetitionColor(int count)
    {
        double progress = Math.Clamp((count - 2d) / 98d, 0d, 1d);
        double eased = (1d - Math.Cos(Math.PI * progress)) / 2d;
        return eased < .28d
            ? LerpColor(Color.FromRgb(148, 163, 184), Color.FromRgb(59, 130, 246), eased / .28d)
            : eased < .62d
                ? LerpColor(Color.FromRgb(59, 130, 246), Color.FromRgb(34, 197, 94), (eased - .28d) / .34d)
                : LerpColor(Color.FromRgb(34, 197, 94), Color.FromRgb(239, 68, 68), (eased - .62d) / .38d);
    }

    private static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * amount);
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }
}

internal sealed class ChickenChaserChatWindow : Window
{
    private readonly ChickenChaserSettings _settings;
    private readonly Func<string, string, IReadOnlyList<ChickenChaserChatAttachment>, Action<string>, Action<string>, Task<ChickenChaserStreamResult>> _send;
    private readonly Func<ChickenChaserSettings, Task> _save;
    private readonly Func<Task<IReadOnlyList<ChickenChaserModelChoice>>> _loadModels;
    private readonly Action _openInHeirowLlm;
    private readonly Func<Task> _logout;
    private readonly string _signedInUserName;
    private readonly StackPanel _messages = new();
    private readonly TextBox _prompt = new();
    private Button? _attachButton;
    private IReadOnlyList<ChickenChaserModelChoice> _modelChoices = Array.Empty<ChickenChaserModelChoice>();
    private bool _modelCapabilityRefreshInProgress;
    private readonly StackPanel _attachmentBar = new() { Margin = new Thickness(2, 0, 2, 7), Visibility = Visibility.Collapsed };
    private readonly List<ChickenChaserChatAttachment> _attachments = new();
    private readonly Queue<ChickenChaserPendingMessage> _pendingMessages = new();
    private readonly Grid _chatView = new();
    private readonly Grid _settingsView = new();
    private Button? _sendButton;
    private bool _sending;
    private bool _attachmentsSupported;
    private string _attachmentRequirement = "";

    public ChickenChaserChatWindow(Window avatar, ChickenChaserSettings settings, string signedInUserName, Func<string, string, IReadOnlyList<ChickenChaserChatAttachment>, Action<string>, Action<string>, Task<ChickenChaserStreamResult>> send, Func<ChickenChaserSettings, Task> save, Func<Task<IReadOnlyList<ChickenChaserModelChoice>>> loadModels, Action openCurrentSession, Func<Task> logout)
    {
        _settings = settings; _signedInUserName = signedInUserName; _send = send; _save = save; _loadModels = loadModels; _openInHeirowLlm = openCurrentSession; _logout = logout; Title = "Chicken Chaser"; Width = 430; Height = 540; WindowStyle = WindowStyle.None; AllowsTransparency = false; Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.CanResizeWithGrip;
        Left = Math.Max(SystemParameters.WorkArea.Left + 8, avatar.Left - Width + avatar.Width); Top = Math.Min(SystemParameters.WorkArea.Bottom - Height - 8, avatar.Top + avatar.Height + 8);
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, CornerRadius = new CornerRadius(13), GlassFrameThickness = new Thickness(-1), ResizeBorderThickness = new Thickness(7), UseAeroCaptionButtons = false });
        ChickenChaserWindowsBackdrop.EnableAcrylic(this);
        var border = new Border { CornerRadius = new CornerRadius(13), BorderThickness = new Thickness(2), Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)), Padding = new Thickness(12) };
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), MappingMode = BrushMappingMode.RelativeToBoundingBox, Opacity = .8 };
        foreach ((Color color, double offset) in new[] { (Colors.DeepPink, 0d), (Colors.Orange, .18), (Colors.Yellow, .36), (Colors.SpringGreen, .54), (Colors.DeepSkyBlue, .72), (Colors.MediumPurple, .88), (Colors.DeepPink, 1d) }) rainbow.GradientStops.Add(new GradientStop(color, offset));
        border.BorderBrush = rainbow; var rotate = new RotateTransform(); rainbow.RelativeTransform = rotate; rotate.CenterX = rotate.CenterY = .5; rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(7)) { RepeatBehavior = RepeatBehavior.Forever });
        var root = new Grid(); root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); root.RowDefinitions.Add(new RowDefinition());
        var head = new Grid { Cursor = Cursors.SizeAll }; head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var title = new TextBlock { Text = "Chicken Chaser", Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), FontWeight = FontWeights.Bold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center }; var settingsButton = CreateHeaderIconButton("settings", "Chicken Chaser settings", Color.FromRgb(29, 78, 216), Color.FromRgb(37, 99, 235)); settingsButton.Click += (_, e) => { e.Handled = true; ShowSettings(); }; var openInHeirowLlm = CreateHeaderIconButton("open", "Open in heirowLLM", Color.FromRgb(109, 40, 217), Color.FromRgb(124, 58, 237)); openInHeirowLlm.Click += (_, e) => { e.Handled = true; _openInHeirowLlm(); }; var close = CreateHeaderIconButton("close", "Close Chicken Chaser chat", Color.FromRgb(153, 27, 27), Color.FromRgb(220, 38, 38), false); close.Click += (_, _) => Hide(); head.Children.Add(title); Grid.SetColumn(settingsButton, 1); head.Children.Add(settingsButton); Grid.SetColumn(openInHeirowLlm, 2); head.Children.Add(openInHeirowLlm); Grid.SetColumn(close, 3); head.Children.Add(close); head.MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } }; root.Children.Add(head);
        BuildChatView(); BuildSettingsView(); Grid.SetRow(_chatView, 1); Grid.SetRow(_settingsView, 1); root.Children.Add(_chatView); root.Children.Add(_settingsView); border.Child = root; Content = border; AddMessage("How can I help?", false);
    }

    public void ShowChat() { _settingsView.Visibility = Visibility.Collapsed; _chatView.Visibility = Visibility.Visible; if (!IsVisible) Show(); Activate(); _prompt.Focus(); _ = RefreshModelCapabilitiesAsync(); }
    public void ShowSettings() { _chatView.Visibility = Visibility.Collapsed; _settingsView.Visibility = Visibility.Visible; if (!IsVisible) Show(); Activate(); }
    public void AddApplicationMessage(string? value) { if (!string.IsNullOrWhiteSpace(value)) AddMessage(value, false, true); }
    public void RefreshCapabilities() => UpdateAttachmentCapability();

    private async Task RefreshModelCapabilitiesAsync()
    {
        if (_modelCapabilityRefreshInProgress) return;
        _modelCapabilityRefreshInProgress = true;
        try { _modelChoices = await _loadModels(); UpdateAttachmentCapability(); }
        catch { UpdateAttachmentCapability(); }
        finally { _modelCapabilityRefreshInProgress = false; }
    }

    private void BuildChatView()
    {
        _chatView.RowDefinitions.Add(new RowDefinition()); _chatView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); _chatView.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var scroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 8) }; _messages.Margin = new Thickness(3); _chatView.Children.Add(scroll);
        Grid.SetRow(_attachmentBar, 1); _chatView.Children.Add(_attachmentBar);
        var compose = new Grid(); compose.ColumnDefinitions.Add(new ColumnDefinition()); compose.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); _prompt.MinHeight = 86; _prompt.AcceptsReturn = true; _prompt.TextWrapping = TextWrapping.Wrap; _prompt.Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)); _prompt.Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)); _prompt.BorderBrush = new SolidColorBrush(Color.FromArgb(204, 51, 65, 85)); _prompt.CaretBrush = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)); _prompt.Padding = new Thickness(8, 8, 8, 38); var send = new Button { Content = "Send", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(13, 7, 13, 7), Background = new SolidColorBrush(Color.FromArgb(84, 91, 53, 160)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 124, 88, 210)), BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), Cursor = Cursors.Hand }; send.MouseEnter += (_, _) => send.Background = new SolidColorBrush(Color.FromArgb(174, 91, 53, 160)); send.MouseLeave += (_, _) => send.Background = new SolidColorBrush(Color.FromArgb(84, 91, 53, 160)); _sendButton = send;
        var promptHost = new Grid(); promptHost.Children.Add(_prompt); var attach = new Button { Content = CreateVectorIcon("add"), Width = 30, Height = 30, Margin = new Thickness(6), Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(84, 14, 116, 144)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 34, 211, 238)), BorderThickness = new Thickness(1), Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), FontSize = 14, Cursor = Cursors.Hand, ToolTip = "Attach or import a file or image", Visibility = Visibility.Visible }; _attachButton = attach; System.Windows.Automation.AutomationProperties.SetName(attach, "Attach or import a file or image"); attach.MouseEnter += (_, _) => attach.Background = new SolidColorBrush(Color.FromArgb(174, 8, 145, 178)); attach.MouseLeave += (_, _) => attach.Background = new SolidColorBrush(Color.FromArgb(84, 14, 116, 144)); attach.Click += (_, e) => { e.Handled = true; AddAttachments(); }; promptHost.Children.Add(attach);
        send.Click += (_, _) => QueueCurrentMessage(); _prompt.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; QueueCurrentMessage(); } else if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryPasteClipboardAttachments()) e.Handled = true; }; DataObject.AddPastingHandler(_prompt, HandlePromptPaste); compose.Children.Add(promptHost); Grid.SetColumn(send, 1); compose.Children.Add(send); Grid.SetRow(compose, 2); _chatView.Children.Add(compose);
    }

    private void QueueCurrentMessage()
    {
        string value = _prompt.Text.Trim();
        if (value.Length == 0 && _attachments.Count == 0) return;
        IReadOnlyList<ChickenChaserChatAttachment> attachments = _attachments.ToArray();
        string display = value + (attachments.Count == 0 ? "" : (value.Length == 0 ? "" : "\n") + "📎 " + string.Join(", ", attachments.Select(item => item.Name)));
        _prompt.Clear();
        _attachments.Clear();
        RefreshAttachmentBar();
        AddMessage(display, true);
        _pendingMessages.Enqueue(new ChickenChaserPendingMessage(value.Length == 0 ? "Please inspect the attached material." : value, _settings.Mode, attachments));
        if (_sending) AddMessage("Queued follow-up · " + _pendingMessages.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), false, true);
        _ = ProcessMessageQueueAsync();
        _prompt.Focus();
    }

    private async Task ProcessMessageQueueAsync()
    {
        if (_sending) return;
        _sending = true;
        if (_sendButton is not null) _sendButton.Content = "Add";
        try
        {
            while (_pendingMessages.Count > 0)
            {
                ChickenChaserPendingMessage pending = _pendingMessages.Dequeue();
                ChickenChaserThinkingPresentation thinking = AddThinkingIndicator();
                ChickenChaserMarkdownMessage response = AddMessage("", false);
                response.Visibility = Visibility.Collapsed;
                void AppendDelta(string delta)
                {
                    if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppendDelta(delta)); return; }
                    if (delta.Length == 0) return;
                    response.Visibility = Visibility.Visible;
                    response.AppendMarkdown(delta);
                }
                void UpdateThought(string summary)
                {
                    if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => UpdateThought(summary)); return; }
                    thinking.Summary.Text = string.IsNullOrWhiteSpace(summary) ? "Chicken Chaser is still thinking..." : summary;
                }
                try
                {
                    ChickenChaserStreamResult result = await _send(pending.Prompt, pending.Mode, pending.Attachments, AppendDelta, UpdateThought);
                    if (response.Markdown.Length == 0) AppendDelta(result.Reply);
                    UpdateThought(result.ThoughtSummary);
                    CompleteThinkingIndicator(thinking);
                }
                catch (Exception ex)
                {
                    response.Visibility = Visibility.Collapsed;
                    AddMessage(ex.Message, false, true);
                    UpdateThought("The request stopped before a response was completed.");
                    StopThinkingIndicator(thinking);
                }
            }
        }
        finally
        {
            _sending = false;
            if (_sendButton is not null) _sendButton.Content = "Send";
        }
    }

    private ChickenChaserThinkingPresentation AddThinkingIndicator()
    {
        var rgb = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach ((Color color, double offset) in new[] { (Colors.DeepPink, 0d), (Colors.Gold, .25), (Colors.SpringGreen, .5), (Colors.DeepSkyBlue, .75), (Colors.MediumPurple, 1d) })
            rgb.GradientStops.Add(new GradientStop(color, offset));
        var rotate = new RotateTransform();
        var throbber = new Ellipse { Width = 18, Height = 18, Stroke = rgb, StrokeThickness = 3, RenderTransform = rotate, RenderTransformOrigin = new Point(.5, .5) };
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        throbber.BeginAnimation(OpacityProperty, new DoubleAnimation(.45, 1, TimeSpan.FromMilliseconds(560)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        var checkmark = new TextBlock { Text = "✓", Width = 18, Height = 18, FontSize = 17, FontWeight = FontWeights.Bold, Foreground = ColorBrush(Color.FromRgb(34, 197, 94)), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        var statusIcon = new Grid { Width = 18, Height = 18 };
        statusIcon.Children.Add(throbber);
        statusIcon.Children.Add(checkmark);
        var label = new TextBlock { Text = "Thinking...", Foreground = ColorBrush(Color.FromArgb(204, 224, 231, 255)), FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var heading = new StackPanel { Orientation = Orientation.Horizontal };
        heading.Children.Add(statusIcon);
        label.Margin = new Thickness(8, 0, 0, 0);
        heading.Children.Add(label);
        var summary = new TextBlock { Text = "Chicken Chaser is still thinking...", Margin = new Thickness(0, 8, 0, 0), Foreground = ColorBrush(Color.FromArgb(204, 203, 213, 225)), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var stack = new StackPanel(); stack.Children.Add(heading); stack.Children.Add(summary);
        rgb.Opacity = .8;
        var host = new Border { Child = stack, Margin = new Thickness(0, 4, 42, 4), Padding = new Thickness(9), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = rgb, Background = ColorBrush(Color.FromArgb(84, 15, 23, 42)), Cursor = Cursors.Hand, ToolTip = "Show thought process" };
        host.MouseLeftButtonUp += (_, _) => summary.Visibility = summary.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        _messages.Children.Add(host);
        return new ChickenChaserThinkingPresentation(host, label, summary, throbber, rotate, checkmark);
    }

    private static void CompleteThinkingIndicator(ChickenChaserThinkingPresentation thinking)
    {
        thinking.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        thinking.Throbber.BeginAnimation(OpacityProperty, null);
        thinking.Throbber.Visibility = Visibility.Collapsed;
        thinking.Checkmark.Text = "✓";
        thinking.Checkmark.Foreground = ColorBrush(Color.FromRgb(34, 197, 94));
        thinking.Checkmark.Visibility = Visibility.Visible;
        thinking.Label.Text = "Conclusion";
        thinking.Host.ToolTip = "Show conclusion";
        System.Windows.Automation.AutomationProperties.SetName(thinking.Host, "Conclusion");
    }

    private static void StopThinkingIndicator(ChickenChaserThinkingPresentation thinking)
    {
        thinking.Rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        thinking.Throbber.BeginAnimation(OpacityProperty, null);
        thinking.Throbber.Visibility = Visibility.Collapsed;
        thinking.Checkmark.Text = "×";
        thinking.Checkmark.Foreground = ColorBrush(Color.FromRgb(248, 113, 113));
        thinking.Checkmark.Visibility = Visibility.Visible;
        thinking.Label.Text = "Stopped";
        thinking.Host.ToolTip = "Show failure details";
        System.Windows.Automation.AutomationProperties.SetName(thinking.Host, "Stopped");
    }

    private static SolidColorBrush ColorBrush(Color color) => new(color);

    private void AddAttachments()
    {
        if (!_attachmentsSupported)
        {
            AddMessage(_attachmentRequirement, false, true);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Attach files or images to Chicken Chaser", Filter = "Supported files|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.txt;*.md;*.json;*.csv;*.xml;*.html;*.css;*.js;*.ts;*.cs;*.py;*.ps1;*.log;*.pdf|Images|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        AddAttachmentPaths(dialog.FileNames);
        RefreshAttachmentBar();
    }

    private void HandlePromptPaste(object sender, DataObjectPastingEventArgs e)
    {
        IDataObject data = e.SourceDataObject;
        bool hasFiles = data.GetDataPresent(DataFormats.FileDrop, true);
        bool hasImage = data.GetDataPresent(DataFormats.Bitmap, true);
        if (!hasFiles && !hasImage) return; // Preserve ordinary text/HTML paste behavior.

        e.CancelCommand();
        PasteClipboardAttachments(data, hasFiles, hasImage);
    }

    private bool TryPasteClipboardAttachments()
    {
        try
        {
            IDataObject? data = Clipboard.GetDataObject();
            if (data is null) return false;
            bool hasFiles = data.GetDataPresent(DataFormats.FileDrop, true);
            bool hasImage = data.GetDataPresent(DataFormats.Bitmap, true);
            if (!hasFiles && !hasImage) return false;
            PasteClipboardAttachments(data, hasFiles, hasImage);
            return true;
        }
        catch (Exception ex)
        {
            AddMessage("Could not read the clipboard: " + ex.Message, false, true);
            return true;
        }
    }

    private void PasteClipboardAttachments(IDataObject data, bool hasFiles, bool hasImage)
    {
        if (!_attachmentsSupported)
        {
            AddMessage(_attachmentRequirement, false, true);
            return;
        }

        int before = _attachments.Count;
        if (hasFiles && data.GetData(DataFormats.FileDrop, true) is string[] paths)
            AddAttachmentPaths(paths);
        else if (hasImage && data.GetData(DataFormats.Bitmap, true) is BitmapSource image)
            AddClipboardImage(image);

        if (_attachments.Count == before)
            AddMessage("The clipboard did not contain an image or file Chicken Chaser could attach.", false, true);
        RefreshAttachmentBar();
    }

    private void AddAttachmentPaths(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (_attachments.Count >= 4)
            {
                AddMessage("Chicken Chaser accepts up to four attachments per message.", false, true);
                break;
            }
            try
            {
                if (!System.IO.File.Exists(path)) throw new InvalidOperationException("Only files can be attached.");
                var info = new System.IO.FileInfo(path);
                if (info.Length > 8 * 1024 * 1024) throw new InvalidOperationException("Attachments are limited to 8 MiB each.");
                byte[] bytes = System.IO.File.ReadAllBytes(path);
                string name = System.IO.Path.GetFileName(path);
                string mediaType = GuessAttachmentMediaType(name);
                bool image = mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
                string dataUrl = image ? "data:" + mediaType + ";base64," + Convert.ToBase64String(bytes) : "";
                string preview = image ? "" : ReadAttachmentTextPreview(path, mediaType, info.Length);
                _attachments.Add(new ChickenChaserChatAttachment(name, mediaType, info.Length, preview, dataUrl));
            }
            catch (Exception ex) { AddMessage("Could not attach " + System.IO.Path.GetFileName(path) + ": " + ex.Message, false, true); }
        }
    }

    private void AddClipboardImage(BitmapSource source)
    {
        if (_attachments.Count >= 4)
        {
            AddMessage("Chicken Chaser accepts up to four attachments per message.", false, true);
            return;
        }
        try
        {
            BitmapSource image = source;
            if (image.Format == PixelFormats.Default)
                image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = new System.IO.MemoryStream();
            encoder.Save(stream);
            byte[] bytes = stream.ToArray();
            if (bytes.LongLength > 8 * 1024 * 1024) throw new InvalidOperationException("Attachments are limited to 8 MiB each.");
            string name = "clipboard-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png";
            _attachments.Add(new ChickenChaserChatAttachment(name, "image/png", bytes.LongLength, "", "data:image/png;base64," + Convert.ToBase64String(bytes)));
        }
        catch (Exception ex) { AddMessage("Could not attach the clipboard image: " + ex.Message, false, true); }
    }

    private void UpdateAttachmentCapability()
    {
        if (_attachButton is null) return;
        ChickenChaserModelChoice? selected = _modelChoices.FirstOrDefault(choice => choice.Id.Equals(_settings.Model, StringComparison.OrdinalIgnoreCase));
        bool agentMode = _settings.Mode.Equals("help", StringComparison.OrdinalIgnoreCase) || _settings.TaskMode.Equals("agent", StringComparison.OrdinalIgnoreCase);
        bool autoRouting = _settings.Model.Equals("auto", StringComparison.OrdinalIgnoreCase);
        bool supported = autoRouting || selected is not null && selected.IsEnabled && selected.SupportsVision;
        _attachmentsSupported = agentMode && supported;
        _attachmentRequirement = !agentMode
            ? "Switch Chicken Chaser to Agent mode to attach files or images."
            : "Select Auto or a vision-capable model to attach files or images.";
        _attachButton.Visibility = Visibility.Visible;
        _attachButton.ToolTip = _attachmentsSupported
            ? "Attach or import a file or image"
            : _attachmentRequirement;
        _prompt.Padding = new Thickness(8, 8, 8, 38);
        _prompt.MinHeight = 86;
    }

    private static Button CreateHeaderIconButton(string icon, string tooltip, Color baseColor, Color hoverColor, bool addRightMargin = true)
    {
        var button = new Button
        {
            Content = CreateVectorIcon(icon),
            Width = 32,
            Height = 28,
            Margin = addRightMargin ? new Thickness(0, 0, 5, 0) : new Thickness(0),
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Color.FromArgb(84, baseColor.R, baseColor.G, baseColor.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(204, hoverColor.R, hoverColor.G, hoverColor.B)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(174, hoverColor.R, hoverColor.G, hoverColor.B));
        button.MouseLeave += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(84, baseColor.R, baseColor.G, baseColor.B));
        return button;
    }

    private static Button CreateTranslucentTextButton(
        string content,
        Thickness margin,
        Thickness padding,
        Color? backgroundColor = null,
        Color? borderColor = null)
    {
        Color background = backgroundColor ?? Color.FromRgb(15, 23, 42);
        Color border = borderColor ?? Color.FromRgb(71, 85, 105);
        var button = new Button
        {
            Content = content,
            Margin = margin,
            Padding = padding,
            Background = new SolidColorBrush(Color.FromArgb(84, background.R, background.G, background.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(204, border.R, border.G, border.B)),
            BorderThickness = new Thickness(1),
            Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)),
            Cursor = Cursors.Hand
        };
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(174, background.R, background.G, background.B));
        button.MouseLeave += (_, _) => button.Background = new SolidColorBrush(Color.FromArgb(84, background.R, background.G, background.B));
        return button;
    }

    private static FrameworkElement CreateVectorIcon(string icon)
    {
        string data = icon switch
        {
            "settings" => "M19.43,12.98 C19.47,12.66 19.5,12.34 19.5,12 C19.5,11.66 19.47,11.34 19.43,11.02 L21.54,9.37 C21.73,9.22 21.78,8.95 21.66,8.73 L19.66,5.27 C19.54,5.05 19.27,4.96 19.05,5.05 L16.56,6.05 C16.04,5.66 15.48,5.32 14.87,5.07 L14.5,2.42 C14.47,2.18 14.25,2 14,2 L10,2 C9.75,2 9.54,2.18 9.5,2.42 L9.13,5.07 C8.52,5.32 7.96,5.66 7.44,6.05 L4.95,5.05 C4.72,4.96 4.46,5.05 4.34,5.27 L2.34,8.73 C2.21,8.95 2.27,9.22 2.46,9.37 L4.57,11.02 C4.53,11.34 4.5,11.67 4.5,12 C4.5,12.33 4.53,12.66 4.57,12.98 L2.46,14.63 C2.27,14.78 2.21,15.05 2.34,15.27 L4.34,18.73 C4.46,18.95 4.73,19.04 4.95,18.95 L7.44,17.95 C7.96,18.34 8.52,18.68 9.13,18.93 L9.5,21.58 C9.54,21.82 9.75,22 10,22 L14,22 C14.25,22 14.47,21.82 14.5,21.58 L14.87,18.93 C15.48,18.68 16.04,18.34 16.56,17.95 L19.05,18.95 C19.28,19.04 19.54,18.95 19.66,18.73 L21.66,15.27 C21.78,15.05 21.73,14.78 21.54,14.63 Z M12,15.5 A3.5,3.5 0 1 1 12,8.5 A3.5,3.5 0 0 1 12,15.5 Z",
            "open" => "M2,8 L2,14 L8,14 M6,2 L14,2 L14,10 M7,9 L14,2",
            "close" => "M3,3 L13,13 M13,3 L3,13",
            _ => "M8,2 L8,14 M2,8 L14,8"
        };
        var path = new Path { Data = Geometry.Parse(data), Stretch = Stretch.Uniform, Width = 15, Height = 15, SnapsToDevicePixels = true };
        var iconBrush = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255));
        iconBrush.Freeze();
        if (icon == "settings") path.Fill = iconBrush;
        else { path.Stroke = iconBrush; path.StrokeThickness = 1.8; path.StrokeStartLineCap = PenLineCap.Round; path.StrokeEndLineCap = PenLineCap.Round; path.StrokeLineJoin = PenLineJoin.Round; }
        return path;
    }

    private void RefreshAttachmentBar()
    {
        _attachmentBar.Children.Clear();
        foreach (ChickenChaserChatAttachment attachment in _attachments.ToArray())
        {
            if (attachment.IsImage && TryCreateAttachmentPreview(attachment.DataUrl, out BitmapSource? preview))
            {
                var previewCard = new Border { CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Color.FromArgb(84, 15, 23, 42)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 51, 65, 85)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(6) };
                var previewGrid = new Grid(); previewGrid.RowDefinitions.Add(new RowDefinition()); previewGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var image = new Image { Source = preview, MaxHeight = 180, MaxWidth = 370, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, SnapsToDevicePixels = true, ToolTip = attachment.Name };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                var footer = new Grid { Margin = new Thickness(2, 5, 2, 0) }; footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                footer.Children.Add(new TextBlock { Text = attachment.Name, Foreground = new SolidColorBrush(Color.FromArgb(204, 203, 213, 225)), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var removeImage = new Button { Content = CreateVectorIcon("close"), Width = 26, Height = 24, Padding = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(84, 127, 29, 29)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 248, 113, 113)), Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), Cursor = Cursors.Hand, ToolTip = "Remove image" };
                removeImage.Click += (_, e) => { e.Handled = true; _attachments.Remove(attachment); RefreshAttachmentBar(); };
                Grid.SetColumn(removeImage, 1); footer.Children.Add(removeImage); Grid.SetRow(footer, 1); previewGrid.Children.Add(image); previewGrid.Children.Add(footer); previewCard.Child = previewGrid; _attachmentBar.Children.Add(previewCard);
                continue;
            }
            var chip = new Border { CornerRadius = new CornerRadius(9), Background = new SolidColorBrush(Color.FromArgb(84, 30, 41, 59)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 71, 85, 105)), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 4, 5, 4) };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = (attachment.IsImage ? "▧ " : "▤ ") + attachment.Name, Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), MaxWidth = 230, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            var remove = new Button { Content = "×", Width = 22, Height = 22, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = new SolidColorBrush(Color.FromArgb(204, 248, 113, 113)), Cursor = Cursors.Hand, ToolTip = "Remove attachment" };
            remove.Click += (_, e) => { e.Handled = true; _attachments.Remove(attachment); RefreshAttachmentBar(); };
            row.Children.Add(remove); chip.Child = row; _attachmentBar.Children.Add(chip);
        }
        _attachmentBar.Visibility = _attachments.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static bool TryCreateAttachmentPreview(string dataUrl, out BitmapSource? preview)
    {
        preview = null;
        try
        {
            string value = dataUrl ?? "";
            int comma = value.IndexOf(',');
            if (comma <= 0 || !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return false;
            byte[] bytes = Convert.FromBase64String(value[(comma + 1)..]);
            using var stream = new System.IO.MemoryStream(bytes, writable: false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); preview = bitmap; return true;
        }
        catch { return false; }
    }

    private static string ReadAttachmentTextPreview(string path, string mediaType, long size)
    {
        if (size > 512 * 1024 || !(mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType is "application/json" or "application/xml")) return "";
        string text = System.IO.File.ReadAllText(path);
        return text.Length <= 12000 ? text : text[..12000] + "\n[preview truncated]";
    }

    private static string GuessAttachmentMediaType(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", ".bmp" => "image/bmp",
        ".txt" or ".log" or ".md" => "text/plain", ".json" => "application/json", ".csv" => "text/csv", ".xml" => "application/xml", ".html" => "text/html", ".css" => "text/css", ".js" => "text/javascript", ".ts" => "text/typescript", ".cs" => "text/x-csharp", ".py" => "text/x-python", ".ps1" => "text/x-powershell", ".pdf" => "application/pdf", _ => "application/octet-stream"
    };

    private void BuildSettingsView()
    {
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(2, 12, 8, 2) };
        var account = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 51, 65, 85)), Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)), Padding = new Thickness(10), Margin = new Thickness(0, 0, 0, 10) };
        var accountGrid = new Grid(); accountGrid.ColumnDefinitions.Add(new ColumnDefinition()); accountGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var identity = new StackPanel(); identity.Children.Add(new TextBlock { Text = "Signed in", Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)), FontSize = 11 }); identity.Children.Add(new TextBlock { Text = _signedInUserName, Foreground = Brushes.White, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 2, 0, 0) }); accountGrid.Children.Add(identity);
        var logout = CreateTranslucentTextButton("Log out", new Thickness(9, 0, 0, 0), new Thickness(10, 6, 10, 6), Color.FromRgb(127, 29, 29), Color.FromRgb(185, 28, 28));
        logout.Click += async (_, _) => { logout.IsEnabled = false; await _logout(); };
        Grid.SetColumn(logout, 1); accountGrid.Children.Add(logout); account.Child = accountGrid; stack.Children.Add(account);
        ComboBox taskMode = AddChoice(stack, "Task mode", new[] { new ChickenChaserModelChoice("plan", "Plan"), new ChickenChaserModelChoice("goal", "Goal"), new ChickenChaserModelChoice("image", "Image"), new ChickenChaserModelChoice("video", "Video"), new ChickenChaserModelChoice("chat", "Chat"), new ChickenChaserModelChoice("agent", "Agent"), new ChickenChaserModelChoice("voice", "Voice") }, _settings.TaskMode);
        ComboBox model = AddChoice(stack, "Model", new[] { new ChickenChaserModelChoice("auto", "Auto · Instant Router") }, _settings.Model);
        TextBox temp = AddSetting(stack, "Temperature", _settings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture)); TextBox topP = AddSetting(stack, "Top P", _settings.TopP.ToString(System.Globalization.CultureInfo.InvariantCulture)); TextBox max = AddSetting(stack, "Max tokens", _settings.MaxTokens.ToString());
        ComboBox reasoning = AddChoice(stack, "Reasoning", new[] { new ChickenChaserModelChoice("auto", "Auto"), new ChickenChaserModelChoice("low", "Low"), new ChickenChaserModelChoice("medium", "Medium"), new ChickenChaserModelChoice("high", "High") }, _settings.Reasoning);
        ComboBox work = AddChoice(stack, "Work cap", new[] { new ChickenChaserModelChoice("preset", "Preset"), new ChickenChaserModelChoice("compact", "Compact"), new ChickenChaserModelChoice("extended", "Extended") }, _settings.WorkCap);
        var retry = AddToggle(stack, "Retry failed requests", _settings.Retry); var forge = AddToggle(stack, "heirowForge", _settings.HeirowForge); var context = AddToggle(stack, "Include current context", _settings.IncludeContext); var global = AddToggle(stack, "Use global reasoning", _settings.UseGlobalReasoning);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) }; var clear = CreateTranslucentTextButton("Clear ignored", new Thickness(0, 0, 7, 0), new Thickness(10, 6, 10, 6)); clear.Click += async (_, _) => { _settings.IgnoredNotificationCategories.Clear(); await _save(_settings); }; var back = CreateTranslucentTextButton("Back", new Thickness(0, 0, 7, 0), new Thickness(12, 6, 12, 6)); back.Click += (_, _) => ShowChat(); var save = CreateTranslucentTextButton("Save", new Thickness(0), new Thickness(12, 6, 12, 6), Color.FromRgb(91, 53, 160), Color.FromRgb(124, 88, 210));
        save.Click += async (_, _) => { _settings.TaskMode = SelectedChoice(taskMode, "agent"); _settings.Model = SelectedChoice(model, "auto"); if (double.TryParse(temp.Text, out double t)) _settings.Temperature = Math.Clamp(t, 0, 2); if (double.TryParse(topP.Text, out double p)) _settings.TopP = Math.Clamp(p, 0, 1); if (int.TryParse(max.Text, out int m)) _settings.MaxTokens = Math.Clamp(m, 0, 32768); _settings.Reasoning = SelectedChoice(reasoning, "auto"); _settings.WorkCap = SelectedChoice(work, "preset"); _settings.Retry = retry.IsChecked == true; _settings.HeirowForge = forge.IsChecked == true; _settings.IncludeContext = context.IsChecked == true; _settings.UseGlobalReasoning = global.IsChecked == true; UpdateAttachmentCapability(); await _save(_settings); ShowChat(); };
        actions.Children.Add(clear); actions.Children.Add(back); actions.Children.Add(save); stack.Children.Add(actions); scroll.Content = stack; _settingsView.Children.Add(scroll);
        Loaded += async (_, _) =>
        {
            string selected = _settings.Model;
            IReadOnlyList<ChickenChaserModelChoice> choices = await _loadModels();
            _modelChoices = choices;
            model.ItemsSource = choices;
            model.SelectedValue = choices.Any(choice => choice.Id.Equals(selected, StringComparison.OrdinalIgnoreCase)) ? selected : "auto";
            UpdateAttachmentCapability();
        };
    }

    private static TextBox AddSetting(Panel panel, string label, string value) { panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromArgb(204, 170, 183, 202)), Margin = new Thickness(0, 5, 0, 3) }); var box = new TextBox { Text = value, Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)), Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), CaretBrush = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 51, 65, 85)), Padding = new Thickness(7) }; panel.Children.Add(box); return box; }
    private static ComboBox AddChoice(Panel panel, string label, IEnumerable<ChickenChaserModelChoice> choices, string selected) { panel.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromArgb(204, 170, 183, 202)), Margin = new Thickness(0, 5, 0, 3) }); var itemStyle = new Style(typeof(ComboBoxItem)); itemStyle.Setters.Add(new Setter(UIElement.IsEnabledProperty, new Binding(nameof(ChickenChaserModelChoice.IsEnabled)))); itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)))); itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)))); var box = new ComboBox { ItemsSource = choices.ToList(), DisplayMemberPath = nameof(ChickenChaserModelChoice.Label), SelectedValuePath = nameof(ChickenChaserModelChoice.Id), SelectedValue = selected, ItemContainerStyle = itemStyle, Background = new SolidColorBrush(Color.FromArgb(84, 7, 11, 18)), Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), BorderBrush = new SolidColorBrush(Color.FromArgb(204, 51, 65, 85)), Padding = new Thickness(7), Cursor = Cursors.Hand }; panel.Children.Add(box); return box; }
    private static CheckBox AddToggle(Panel panel, string label, bool value) { var box = new CheckBox { Content = label, IsChecked = value, Foreground = new SolidColorBrush(Color.FromArgb(204, 255, 255, 255)), Margin = new Thickness(0, 7, 0, 2), Cursor = Cursors.Hand }; panel.Children.Add(box); return box; }
    private static string SelectedChoice(Selector selector, string fallback) => selector.SelectedValue?.ToString() is string value && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    private ChickenChaserMarkdownMessage AddMessage(string text, bool user, bool application = false) { var message = new ChickenChaserMarkdownMessage(user, application); message.SetMarkdown(text); _messages.Children.Add(message); return message; }
}

internal static class ChickenChaserWindowsBackdrop
{
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowAttributeSystemBackdropType = 38;
    private const int DwmWindowCornerPreferenceRound = 2;
    private const int DwmSystemBackdropNone = 1;
    private const int WindowCompositionAttributeAccentPolicy = 19;

    public static void EnableAcrylic(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
        window.Activated += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var margins = new DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(handle, ref margins);
            int rounded = DwmWindowCornerPreferenceRound;
            _ = DwmSetWindowAttribute(handle, DwmWindowAttributeCornerPreference, ref rounded, sizeof(int));
            // The Windows 11 transient system material applies its own gray tint. Disable it
            // before installing our explicit 33% dark-navy acrylic policy below.
            int backdrop = DwmSystemBackdropNone;
            if (Environment.OSVersion.Version.Build >= 22000)
                _ = DwmSetWindowAttribute(handle, DwmWindowAttributeSystemBackdropType, ref backdrop, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }

        var accent = new AccentPolicy
        {
            State = AccentState.EnableAcrylicBlurBehind,
            Flags = 2,
            GradientColor = unchecked((int)0x54120B07)
        };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPointer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = accentPointer,
                SizeOfData = size
            };
            _ = SetWindowCompositionAttribute(handle, ref data);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        finally { Marshal.FreeHGlobal(accentPointer); }
    }

    private enum AccentState
    {
        EnableAcrylicBlurBehind = 4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState State;
        public int Flags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr windowHandle, ref DwmMargins margins);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr windowHandle, ref WindowCompositionAttributeData data);
}
