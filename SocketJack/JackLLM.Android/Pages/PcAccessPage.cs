using System.Net;
using System.Text.Json;
using JackLLM.Mobile.Controls;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using LibVLCSharp.MAUI;
using LibVLCSharp.Shared;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace JackLLM.Mobile.Pages;

public sealed class PcAccessPage : ContentPage
{
    private readonly ServerInfo _server;
    private readonly JackLlmClient _client;
    private readonly VideoView _video = new() { ZIndex = 0 };
    private readonly RemoteDesktopSurface _surface = new() { BackgroundColor = Colors.Transparent, IsClippedToBounds = true };
    private readonly GraphicsView _cursorView = new() { InputTransparent = true, BackgroundColor = Colors.Transparent, ZIndex = 10 };
    private readonly RemoteCursorDrawable _cursor = new();
    private readonly Label _status = new() { Text = "Preparing secure PC Access…", TextColor = Color.FromArgb("#CBD5E1"), LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _zoomLabel = new() { Text = "1.0×", TextColor = Color.FromArgb("#67E8F9"), VerticalTextAlignment = TextAlignment.Center };
    private readonly VerticalStackLayout _files = new() { Spacing = 6 };
    private readonly Border _filesPanel;
    private LibVLC? _libVlc;
    private VlcMediaPlayer? _player;
    private Media? _media;
    private CancellationTokenSource? _streamCancellation;
    private CancellationTokenSource? _backgroundGrace;
    private PcAccessStreamSession? _session;
    private string _path = "";
    private double _zoom = 1d;
    private double _panX;
    private double _panY;
    private bool _connected;
    private bool _disposed;
    private bool _backgroundStopped;
    private int _pointerFailures;

    public PcAccessPage(ServerInfo server, JackLlmClient client)
    {
        _server = server;
        _client = client;
        Title = "PC Access";
        BackgroundColor = Color.FromArgb("#0B1020");
        _cursorView.Drawable = _cursor;
        _surface.Children.Add(_video);
        _surface.Children.Add(_cursorView);
        _filesPanel = new Border
        {
            IsVisible = false,
            MaximumHeightRequest = 280,
            Padding = 8,
            BackgroundColor = Color.FromArgb("#111827"),
            Stroke = Color.FromArgb("#334155"),
            Content = new ScrollView { Content = _files }
        };

        var keyboardInput = new Entry { Opacity = 0.01, WidthRequest = 1, HeightRequest = 1, Keyboard = Keyboard.Default, ReturnType = ReturnType.Send };
        keyboardInput.Completed += async (_, _) =>
        {
            try
            {
                if (!string.IsNullOrEmpty(keyboardInput.Text) && _session is not null)
                    await _client.SendPcInputAsync(new { sessionId = _session.SessionId, type = "text", text = keyboardInput.Text });
                keyboardInput.Text = "";
                keyboardInput.Unfocus();
            }
            catch (Exception ex) { ShowError(ex); }
        };
        var keyboard = ActionButton("⌨", "Open keyboard");
        keyboard.Clicked += (_, _) => keyboardInput.Focus();
        var filesButton = ActionButton("📁", "Browse approved files");
        filesButton.Clicked += async (_, _) => await Navigation.PushAsync(new PcFileTransferPage(_client));
        var resetZoom = ActionButton("⊙", "Reset zoom and pan");
        resetZoom.Clicked += (_, _) => { _zoom = 1; _panX = _panY = 0; ApplyViewportTransform(); };
        var disconnect = ActionButton("✕", "Disconnect PC Access", "#991B1B");
        disconnect.Clicked += async (_, _) => { await StopAndDisconnectAsync(); await Navigation.PopAsync(); };

        var toolbar = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 6,
            Children = { _status, _zoomLabel.Column(1), keyboard.Column(2), filesButton.Column(3), resetZoom.Column(4), disconnect.Column(5), keyboardInput }
        };
        Grid.SetColumnSpan(keyboardInput, 1);

        Content = new Grid
        {
            Padding = new Thickness(8),
            RowSpacing = 7,
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { toolbar.Row(0), _surface.Row(1), _filesPanel.Row(2) }
        };

        _surface.RemoteTap += async (_, e) => await SendPointerAsync("click", e.X, e.Y, "left");
        _surface.RemoteRightTap += async (_, e) => await SendPointerAsync("click", e.X, e.Y, "right");
        _surface.RemoteDoubleTap += (_, e) => ToggleZoom(e.X, e.Y);
        _surface.RemoteTransform += (_, e) => TransformViewport(e);
        _surface.RemoteDrag += async (_, e) =>
        {
            string type = e.Phase switch { RemoteDragPhase.Started => "down", RemoteDragPhase.Completed => "up", _ => "move" };
            _cursor.Pressed = e.Phase != RemoteDragPhase.Completed;
            await SendPointerAsync(type, e.X, e.Y, "left");
        };

        Loaded += async (_, _) =>
        {
            AppVisibilityService.VisibilityChanged -= OnVisibilityChanged;
            AppVisibilityService.VisibilityChanged += OnVisibilityChanged;
            try
            {
                if (!_connected) { await _client.ConnectAsync(_server); _connected = true; }
                await StartStreamAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        };
        Unloaded += (_, _) => AppVisibilityService.VisibilityChanged -= OnVisibilityChanged;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (AppVisibilityService.IsActive) _ = StopAndDisconnectAsync();
        else BeginBackgroundGracePeriod();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_connected || _session is not null) return;
        _disposed = false;
        _ = MainThread.InvokeOnMainThreadAsync(async () =>
        {
            try { await StartStreamAsync(); }
            catch (Exception ex) { ShowError(ex); }
        });
    }

    private async Task StartStreamAsync()
    {
        if (_disposed || _session is not null) return;
        _backgroundGrace?.Cancel();
        _backgroundStopped = false;
        _status.Text = "Starting private RTMP stream…";
        using JsonDocument status = await _client.GetPcAccessStatusAsync();
        JsonElement root = status.RootElement;
        if (!root.TryGetProperty("supported", out JsonElement supported) || !supported.GetBoolean()) throw new InvalidOperationException("Desktop capture is unsupported on this Workstation.");
        if (!root.TryGetProperty("tailscaleAvailable", out JsonElement tailscale) || !tailscale.GetBoolean()) throw new InvalidOperationException("Tailscale is not connected on the Workstation.");
        if (!root.TryGetProperty("ffmpegAvailable", out JsonElement ffmpeg) || !ffmpeg.GetBoolean()) throw new InvalidOperationException("FFmpeg is required for PC Access RTMP streaming.");

        _session = await _client.StartPcAccessStreamAsync(1280, 720, 20);
        if (string.IsNullOrWhiteSpace(_session.RtmpUrl) || string.IsNullOrWhiteSpace(_session.SessionId)) throw new InvalidOperationException("The Workstation did not return a valid RTMP session.");
        _cursor.X = _session.Cursor.X;
        _cursor.Y = _session.Cursor.Y;
        _cursor.Visible = _session.Cursor.Visible;
        EnsurePlayer();
        _media?.Dispose();
        _media = new Media(_libVlc!, _session.RtmpUrl, FromType.FromLocation);
        _media.AddOption(":network-caching=100");
        _media.AddOption(":live-caching=100");
        _media.AddOption(":file-caching=100");
        _media.AddOption(":clock-jitter=0");
        _media.AddOption(":clock-synchro=0");
        _media.AddOption(":drop-late-frames");
        _media.AddOption(":skip-frames");
        _media.AddOption(":avcodec-fast");
        _media.AddOption(":rtmp-live=1");
        _media.AddOption(":no-audio");
        if (DeviceInfo.Current.DeviceType == DeviceType.Virtual) _media.AddOption(":avcodec-hw=none");
        await WaitForVideoSurfaceAsync();
        _video.MediaPlayer = null;
        _video.MediaPlayer = _player;
        _player!.Media = _media;
        _status.Text = $"Connecting · 720p · {_session.BitrateKbps} kbps";
        if (!_player.Play()) throw new InvalidOperationException("LibVLC could not start the RTMP stream.");
        await Task.Delay(250);
        RefreshNativeVideoLayout();
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _streamCancellation = new CancellationTokenSource();
        _ = PollPointerAsync(_streamCancellation.Token);
    }

    private void EnsurePlayer()
    {
        if (_player is not null) return;
        Core.Initialize();
        _libVlc = new LibVLC(false, "--no-audio", "--network-caching=100", "--live-caching=100", "--file-caching=100", "--clock-jitter=0", "--clock-synchro=0", "--drop-late-frames", "--skip-frames", "--avcodec-fast");
        _player = new VlcMediaPlayer(_libVlc)
        {
            // Android emulators frequently advertise a surface decoder that cannot render VLC output.
            // Physical phones keep hardware decoding enabled for power efficiency and low latency.
            EnableHardwareDecoding = DeviceInfo.Current.DeviceType != DeviceType.Virtual
        };
        _player.Playing += (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshNativeVideoLayout();
            _status.Text = $"Live · 720p · {_session?.BitrateKbps ?? 750} kbps";
        });
        _player.Buffering += (_, e) => MainThread.BeginInvokeOnMainThread(() =>
            _status.Text = e.Cache >= 99.5f
                ? $"Live · 720p · {_session?.BitrateKbps ?? 750} kbps"
                : $"Buffering {e.Cache:0}%…");
        _player.EncounteredError += (_, _) => MainThread.BeginInvokeOnMainThread(() => _status.Text = "RTMP interrupted — reconnecting…");
        _video.MediaPlayer = _player;
    }

    private async Task WaitForVideoSurfaceAsync()
    {
        for (int attempt = 0; attempt < 20 && (_video.Handler is null || _video.Width <= 0 || _video.Height <= 0); attempt++)
            await Task.Delay(50);
        RefreshNativeVideoLayout();
    }

    private void RefreshNativeVideoLayout()
    {
#if ANDROID
        if (_video.Handler?.PlatformView is JackLLM.Mobile.Platforms.Android.TextureVlcView nativeVideo)
        {
            nativeVideo.RequestLayout();
        }
#endif
    }

    private async Task PollPointerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _session is not null)
        {
            try
            {
                PcAccessPointerSnapshot snapshot = await _client.GetPcAccessPointerAsync(_session.SessionId, cancellationToken);
                _pointerFailures = 0;
                MainThread.BeginInvokeOnMainThread(() => UpdateCursor(snapshot.Cursor));
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                if (++_pointerFailures >= 3)
                {
                    MainThread.BeginInvokeOnMainThread(() => _status.Text = "Connection interrupted — reconnecting…");
                    break;
                }
            }
            try { await Task.Delay(100, cancellationToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendPointerAsync(string type, double viewportX, double viewportY, string button)
    {
        if (_session is null) return;
        Point normalized = ViewportToDesktop(viewportX, viewportY);
        UpdateCursor(new PcCursorState { X = normalized.X, Y = normalized.Y, Visible = true });
        try
        {
            await _client.SendPcInputAsync(new
            {
                sessionId = _session.SessionId,
                type,
                normalizedX = normalized.X,
                normalizedY = normalized.Y,
                button
            });
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Point ViewportToDesktop(double x, double y)
    {
        RemoteNormalizedPoint point = RemoteViewportMath.MapToDesktop(
            x, y, _surface.Width, _surface.Height,
            _session?.Desktop.Width ?? 16, _session?.Desktop.Height ?? 9,
            _zoom, _panX, _panY);
        return new Point(point.X, point.Y);
    }

    private void UpdateCursor(PcCursorState state)
    {
        if (_session is null) return;
        _session.Cursor = state;
        RemoteViewportPoint point = RemoteViewportMath.MapCursor(
            state.X, state.Y, _surface.Width, _surface.Height,
            _session.Desktop.Width, _session.Desktop.Height, _zoom, _panX, _panY);
        _cursor.X = point.X;
        _cursor.Y = point.Y;
        _cursor.Visible = state.Visible;
        _cursorView.Invalidate();
    }

    private void TransformViewport(RemoteTransformEventArgs e)
    {
        double oldZoom = _zoom;
        _zoom = Math.Clamp(_zoom * e.ScaleFactor, 1d, 4d);
        double ratio = _zoom / oldZoom;
        double width = Math.Max(1, _surface.Width), height = Math.Max(1, _surface.Height);
        _panX = e.CenterX - width / 2d - (e.CenterX - width / 2d - _panX) * ratio + e.DeltaX;
        _panY = e.CenterY - height / 2d - (e.CenterY - height / 2d - _panY) * ratio + e.DeltaY;
        ClampPan(width, height);
        ApplyViewportTransform();
    }

    private void ToggleZoom(double x, double y)
    {
        double target = _zoom > 1.05 ? 1d : 2d;
        double width = Math.Max(1, _surface.Width), height = Math.Max(1, _surface.Height);
        double ratio = target / _zoom;
        _panX = x - width / 2d - (x - width / 2d - _panX) * ratio;
        _panY = y - height / 2d - (y - height / 2d - _panY) * ratio;
        _zoom = target;
        ClampPan(width, height);
        ApplyViewportTransform();
    }

    private void ClampPan(double width, double height)
    {
        RemoteViewportPoint point = RemoteViewportMath.ClampPan(
            _panX, _panY, width, height,
            _session?.Desktop.Width ?? 16, _session?.Desktop.Height ?? 9, _zoom);
        _panX = point.X;
        _panY = point.Y;
    }

    private void ApplyViewportTransform()
    {
        if (_player is not null)
        {
            _player.CropGeometry = string.Empty;
            RefreshNativeVideoLayout();
        }
        _video.Scale = _zoom;
        _video.TranslationX = _panX;
        _video.TranslationY = _panY;
        _zoomLabel.Text = $"{_zoom:0.0}×";
        if (_session is not null) UpdateCursor(_session.Cursor);
    }

    private void OnVisibilityChanged(bool active)
    {
        if (active)
        {
            _backgroundGrace?.Cancel();
            if (_backgroundStopped && !_disposed) MainThread.BeginInvokeOnMainThread(async () => { try { await StartStreamAsync(); } catch (Exception ex) { ShowError(ex); } });
        }
        else BeginBackgroundGracePeriod();
    }

    private void BeginBackgroundGracePeriod()
    {
        _backgroundGrace?.Cancel();
        _backgroundGrace?.Dispose();
        _backgroundGrace = new CancellationTokenSource();
        CancellationToken token = _backgroundGrace.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                if (!token.IsCancellationRequested)
                {
                    _backgroundStopped = true;
                    await StopAndDisconnectAsync(disposePlayer: true, markDisposed: false);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task StopAndDisconnectAsync(bool disposePlayer = true, bool markDisposed = true)
    {
        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _streamCancellation = null;
        try { _player?.Stop(); } catch { }
        if (_session is not null)
        {
            try { await _client.DisconnectPcAccessAsync(); }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
            catch { }
        }
        _session = null;
        _media?.Dispose();
        _media = null;
        if (!disposePlayer) return;
        _disposed = markDisposed;
        _video.MediaPlayer = null;
        _player?.Dispose(); _player = null;
        _libVlc?.Dispose(); _libVlc = null;
    }

    private async Task LoadFilesAsync(string path)
    {
        try
        {
            using JsonDocument json = await _client.BrowsePcFilesAsync(path);
            _files.Children.Clear();
            _path = json.RootElement.TryGetProperty("path", out JsonElement current) ? current.GetString() ?? "" : path;
            _files.Children.Add(new Label { Text = _path, TextColor = Colors.White, FontAttributes = FontAttributes.Bold });
            if (!json.RootElement.TryGetProperty("entries", out JsonElement entries)) return;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                string name = entry.GetProperty("name").GetString() ?? "";
                string itemPath = entry.GetProperty("path").GetString() ?? "";
                bool directory = entry.GetProperty("directory").GetBoolean();
                var button = new Button { Text = (directory ? "📁 " : "📄 ") + name, HorizontalOptions = LayoutOptions.Fill, BackgroundColor = Color.FromArgb("#151C2F"), TextColor = Colors.White };
                if (directory) button.Clicked += async (_, _) => await LoadFilesAsync(itemPath);
                _files.Children.Add(button);
            }
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private static Button ActionButton(string text, string help, string background = "#1F2937")
    {
        var button = new Button { Text = text, FontSize = 18, WidthRequest = 46, HeightRequest = 42, Padding = 0, BackgroundColor = Color.FromArgb(background), TextColor = Colors.White };
        AutomationProperties.SetHelpText(button, help);
        return button;
    }

    private void ShowError(Exception exception)
    {
        MainThread.BeginInvokeOnMainThread(() => _status.Text = exception switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } => "Pair this device or sign in to use PC Access.",
            HttpRequestException { StatusCode: HttpStatusCode.Forbidden } => "PC Access permission is disabled for this device.",
            HttpRequestException { StatusCode: HttpStatusCode.Conflict } => "Another phone is controlling this Workstation.",
            HttpRequestException => "The Workstation connection was interrupted.",
            _ => string.IsNullOrWhiteSpace(exception.Message) ? "PC Access is unavailable." : exception.Message
        });
    }
}
