using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Provider;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using heirowLLM.Mobile.Models;
using heirowLLM.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Microsoft.Maui;

namespace heirowLLM.Mobile.Platforms.Android;

public sealed class AndroidChickenChaserOverlayService : IChickenChaserOverlayService
{
    internal const string EnabledKey = "heirowllm.mobile.chicken.overlay.enabled";
    internal const string NotificationDotKey = "heirowllm.mobile.chicken.overlay.dot";

    public Task<ChickenChaserOverlayStatus> GetStatusAsync()
    {
        Context context = Platform.AppContext;
        bool permission = !OperatingSystem.IsAndroidVersionAtLeast(23) || Settings.CanDrawOverlays(context);
        return Task.FromResult(new ChickenChaserOverlayStatus(
            true,
            permission,
            permission && Preferences.Default.Get(EnabledKey, false)));
    }

    public Task<bool> EnableAsync()
    {
        Context context = Platform.AppContext;
        Preferences.Default.Set(EnabledKey, true);
        bool permission = !OperatingSystem.IsAndroidVersionAtLeast(23) || Settings.CanDrawOverlays(context);
        if (!permission) return Task.FromResult(false);

        var intent = new Intent(context, typeof(ChickenChaserBubbleService));
        intent.SetAction(ChickenChaserBubbleService.ActionShow);
        intent.PutExtra(ChickenChaserBubbleService.ExtraNotificationDot, Preferences.Default.Get(NotificationDotKey, false));
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) ContextCompat.StartForegroundService(context, intent);
            else context.StartService(intent);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public Task DisableAsync()
    {
        Context context = Platform.AppContext;
        Preferences.Default.Set(EnabledKey, false);
        var intent = new Intent(context, typeof(ChickenChaserBubbleService));
        intent.SetAction(ChickenChaserBubbleService.ActionHide);
        try { context.StartService(intent); } catch { }
        return Task.CompletedTask;
    }

    public Task<bool> OpenPermissionSettingsAsync()
    {
        Context context = Platform.AppContext;
        Preferences.Default.Set(EnabledKey, true);
        if (!OperatingSystem.IsAndroidVersionAtLeast(23))
        {
            return Task.FromResult(true);
        }

        var intent = new Intent(Settings.ActionManageOverlayPermission);
        try
        {
            intent.SetData(global::Android.Net.Uri.Parse("package:" + context.PackageName));
            intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
            context.StartActivity(intent);
            return Task.FromResult(true);
        }
        catch
        {
            try
            {
                intent.SetData(null);
                context.StartActivity(intent);
                return Task.FromResult(true);
            }
            catch { return Task.FromResult(false); }
        }
    }

    public void SetNotificationDot(bool visible)
    {
        Context context = Platform.AppContext;
        Preferences.Default.Set(NotificationDotKey, visible);
        if (!Preferences.Default.Get(EnabledKey, false)) return;
        var intent = new Intent(context, typeof(ChickenChaserBubbleService));
        intent.SetAction(ChickenChaserBubbleService.ActionUpdateDot);
        intent.PutExtra(ChickenChaserBubbleService.ExtraNotificationDot, visible);
        try { context.StartService(intent); } catch { }
    }
}

[Service(
    Name = "com.socketjack.heirowllm.mobile.ChickenChaserBubbleService",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public sealed class ChickenChaserBubbleService : Service
{
    internal const string ActionShow = "com.socketjack.heirowllm.mobile.CHICKEN_OVERLAY_SHOW";
    internal const string ActionHide = "com.socketjack.heirowllm.mobile.CHICKEN_OVERLAY_HIDE";
    internal const string ActionUpdateDot = "com.socketjack.heirowllm.mobile.CHICKEN_OVERLAY_DOT";
    internal const string ExtraNotificationDot = "chicken_overlay_notification_dot";
    private const string ChannelId = "heirowllm_chicken_overlay";
    private const int NotificationId = 41003;
    private const string PositionXKey = "heirowllm.mobile.chicken.overlay.x";
    private const string PositionYKey = "heirowllm.mobile.chicken.overlay.y";

    private IWindowManager? _windowManager;
    private ChickenChaserBubbleView? _bubble;
    private WindowManagerLayoutParams? _layout;
    private LinearLayout? _chatWindow;
    private FrameLayout? _chatBody;
    private LinearLayout? _chatMessages;
    private global::Android.Widget.ScrollView? _chatScroll;
    private EditText? _chatPrompt;
    private global::Android.Widget.Button? _chatSend;
    private WindowManagerLayoutParams? _chatLayout;
    private int _chatRestingY;
    private int _chatRestingHeight;
    private bool _chatKeyboardAdjusted;
    private bool _chatPromptFocused;
    private HeirowLlmClient? _chatClient;
    private string _chatConnectedServerKey = "";
    private int _chatMessageCount;
    private ChickenChaserMobileSettings _chatSettings = new();
    private readonly List<(string Text, bool FromUser, bool Error)> _chatHistory =
    [
        ("How can I help?", false, false)
    ];

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionHide)
        {
            RemoveBubble();
            if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
            else StopForeground(true);
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        bool permission = !OperatingSystem.IsAndroidVersionAtLeast(23) || Settings.CanDrawOverlays(this);
        if (!permission || !Preferences.Default.Get(AndroidChickenChaserOverlayService.EnabledKey, false))
        {
            RemoveBubble();
            StopSelf();
            return StartCommandResult.NotSticky;
        }

        StartOverlayForeground();
        bool dot = intent?.GetBooleanExtra(ExtraNotificationDot,
            Preferences.Default.Get(AndroidChickenChaserOverlayService.NotificationDotKey, false)) ?? false;
        if (_bubble is null) AddBubble(dot);
        else _bubble.SetNotificationDot(dot);
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        RemoveBubble();
        base.OnDestroy();
    }

    private void StartOverlayForeground()
    {
        EnsureChannel();
        var open = new Intent(this, typeof(MainActivity));
        open.AddFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        PendingIntentFlags flags = PendingIntentFlags.UpdateCurrent;
        if (OperatingSystem.IsAndroidVersionAtLeast(23)) flags |= PendingIntentFlags.Immutable;
        PendingIntent pending = PendingIntent.GetActivity(this, NotificationId, open, flags)!;
        var builder = new NotificationCompat.Builder(this, ChannelId);
        builder.SetSmallIcon(Resource.Mipmap.appicon);
        builder.SetContentTitle("Chicken Chaser is floating");
        builder.SetContentText("Tap the avatar to return to Chicken Chaser. Turn it off in mobile Options.");
        builder.SetContentIntent(pending);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        Notification notification = builder.Build() ?? throw new InvalidOperationException("Unable to create the Chicken Chaser overlay notification.");
        if (OperatingSystem.IsAndroidVersionAtLeast(34))
            StartForeground(NotificationId, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(NotificationId, notification);
    }

    private void EnsureChannel()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;
        var manager = (NotificationManager?)GetSystemService(NotificationService);
        if (manager is null) return;
        var channel = new NotificationChannel(ChannelId, "Chicken Chaser overlay", NotificationImportance.Low)
        {
            Description = "Persistent status while the user-enabled Chicken Chaser avatar floats above other apps"
        };
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        manager.CreateNotificationChannel(channel);
    }

    private void AddBubble(bool dot)
    {
        _windowManager = GetSystemService(WindowService)?.JavaCast<IWindowManager>();
        if (_windowManager is null)
        {
            throw new InvalidOperationException("Android window manager is unavailable for the Chicken Chaser overlay.");
        }
        int size = Dp(76);
        WindowManagerTypes type = OperatingSystem.IsAndroidVersionAtLeast(26)
            ? WindowManagerTypes.ApplicationOverlay
            : WindowManagerTypes.Phone;
        int screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? size + Dp(12);
        int defaultX = Math.Max(0, screenWidth - size - Dp(12));
        _layout = new WindowManagerLayoutParams(
            size,
            size,
            type,
            WindowManagerFlags.NotFocusable | WindowManagerFlags.LayoutNoLimits,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Preferences.Default.Get(PositionXKey, defaultX),
            Y = Preferences.Default.Get(PositionYKey, Dp(180))
        };
        _bubble = new ChickenChaserBubbleView(this, dot, _layout, _windowManager, PersistPosition, ToggleFloatingChat);
        _windowManager.AddView(_bubble, _layout);
    }

    private void AddChatWindow()
    {
        if (_windowManager is null || _chatWindow is not null) return;
        int screenWidth = Resources?.DisplayMetrics?.WidthPixels ?? Dp(400);
        int screenHeight = Resources?.DisplayMetrics?.HeightPixels ?? Dp(760);
        int width = Math.Min(screenWidth - Dp(24), Dp(390));
        int height = Math.Min(screenHeight - Dp(120), Dp(520));

        _chatWindow = new LinearLayout(this)
        {
            Orientation = Orientation.Vertical,
            ContentDescription = "Chicken Chaser floating chat window",
            Background = RoundedBackground(global::Android.Graphics.Color.Rgb(9, 13, 22), Dp(17), global::Android.Graphics.Color.Rgb(168, 85, 247), Dp(1.5f))
        };
        _chatWindow.SetPadding(Dp(12), Dp(10), Dp(12), Dp(12));

        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        var title = new TextView(this)
        {
            Text = "Chicken Chaser",
            TextSize = 16,
            Gravity = GravityFlags.CenterVertical
        };
        title.SetTextColor(global::Android.Graphics.Color.White);
        title.SetTypeface(title.Typeface, global::Android.Graphics.TypefaceStyle.Bold);
        header.AddView(title, new LinearLayout.LayoutParams(0, Dp(46), 1));

        var settings = HeaderButton("⚙", "Chicken Chaser settings", global::Android.Graphics.Color.Rgb(29, 78, 216));
        settings.Click += async (_, _) => await ShowChatSettingsAsync();
        header.AddView(settings);
        var open = HeaderButton("↗", "Open in heirowLLM Mobile", global::Android.Graphics.Color.Rgb(91, 45, 160));
        open.Click += (_, _) => OpenChickenChaserInApp();
        header.AddView(open);
        var close = HeaderButton("×", "Close floating chat", global::Android.Graphics.Color.Rgb(127, 29, 29));
        close.Click += (_, _) => RemoveChatWindow();
        header.AddView(close);
        _chatWindow.AddView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(48)));

        _chatBody = new FrameLayout(this);
        _chatWindow.AddView(_chatBody, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        ShowFloatingChatView();

        _chatLayout = new WindowManagerLayoutParams(
            width,
            height,
            OperatingSystem.IsAndroidVersionAtLeast(26) ? WindowManagerTypes.ApplicationOverlay : WindowManagerTypes.Phone,
            WindowManagerFlags.LayoutInScreen | WindowManagerFlags.NotTouchModal,
            Format.Translucent)
        {
            Gravity = GravityFlags.Top | GravityFlags.Start,
            X = Math.Max(Dp(8), screenWidth - width - Dp(10)),
            Y = Math.Max(Dp(70), Math.Min(screenHeight - height - Dp(70), _layout?.Y ?? Dp(110))),
            SoftInputMode = SoftInput.AdjustResize
        };
        _chatRestingY = _chatLayout.Y;
        _chatRestingHeight = height;
        header.SetOnTouchListener(new FloatingWindowDragListener(_windowManager, _chatWindow, _chatLayout, Dp(5)));
        _windowManager.AddView(_chatWindow, _chatLayout);
        if (_chatWindow.ViewTreeObserver is { } observer)
            observer.GlobalLayout += OnFloatingChatGlobalLayout;
    }

    private void ShowFloatingChatView()
    {
        if (_chatBody is null) return;
        _chatBody.RemoveAllViews();
        var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _chatMessages = new LinearLayout(this) { Orientation = Orientation.Vertical };
        foreach ((string text, bool fromUser, bool error) in _chatHistory)
            AddChatMessageView(text, fromUser, error);
        _chatScroll = new global::Android.Widget.ScrollView(this) { FillViewport = true };
        _chatScroll.AddView(_chatMessages);
        content.AddView(_chatScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));

        var compose = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        compose.SetGravity(GravityFlags.Bottom);
        _chatPrompt = new EditText(this)
        {
            Hint = "Ask Chicken Chaser…",
            TextSize = 14,
            Background = RoundedBackground(global::Android.Graphics.Color.Rgb(12, 18, 30), Dp(10), global::Android.Graphics.Color.Rgb(51, 65, 85), Dp(1))
        };
        _chatPrompt.SetMaxLines(4);
        _chatPrompt.SetMinLines(2);
        _chatPrompt.SetHintTextColor(global::Android.Graphics.Color.Rgb(100, 116, 139));
        _chatPrompt.SetTextColor(global::Android.Graphics.Color.White);
        _chatPrompt.SetPadding(Dp(10), Dp(7), Dp(10), Dp(7));
        _chatPrompt.FocusChange += (_, args) =>
        {
            _chatPromptFocused = args.HasFocus;
            if (args.HasFocus) RaiseFloatingChatForKeyboard();
            else _chatWindow?.PostDelayed(KeepFloatingComposerVisible, 240);
        };
        _chatPrompt.Touch += (_, args) =>
        {
            if (args.Event?.ActionMasked != MotionEventActions.Down) return;
            _chatPromptFocused = true;
            RaiseFloatingChatForKeyboard();
        };
        compose.AddView(_chatPrompt, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        _chatSend = HeaderButton("↑", "Send to Chicken Chaser", global::Android.Graphics.Color.Rgb(109, 40, 217));
        _chatSend.SetMinimumWidth(Dp(54));
        _chatSend.SetMinimumHeight(Dp(58));
        _chatSend.Click += async (_, _) => await SendFloatingChatAsync();
        var sendLayout = new LinearLayout.LayoutParams(Dp(58), Dp(58)) { LeftMargin = Dp(7) };
        compose.AddView(_chatSend, sendLayout);
        content.AddView(compose, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        _chatBody.AddView(content, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        ScrollChatToBottom();
    }

    private async Task SendFloatingChatAsync()
    {
        string prompt = (_chatPrompt?.Text ?? "").Trim();
        if (prompt.Length == 0 || _chatSend is null) return;
        _chatPrompt!.Text = "";
        _chatHistory.Add((prompt, true, false));
        _chatMessageCount++;
        AddChatMessageView(prompt, true, false);
        _chatSend.Enabled = false;
        AddChatMessageView("Thinking…", false, false);
        TextView? thinking = _chatMessages?.GetChildAt((_chatMessages?.ChildCount ?? 1) - 1) as TextView;
        try
        {
            HeirowLlmClient client = await EnsureChatClientAsync();
            string reply = await client.AskChickenChaserAsync(prompt, "", "Floating Chicken Chaser", "Unsorted", _chatMessageCount);
            PostToUi(() =>
            {
                if (thinking is not null && thinking.Parent is ViewGroup parent) parent.RemoveView(thinking);
                _chatHistory.Add((reply, false, false));
                _chatMessageCount++;
                if (_chatWindow is not null) AddChatMessageView(reply, false, false);
                else ShowUnreadDot();
                if (_chatSend is not null) _chatSend.Enabled = true;
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                if (thinking is not null && thinking.Parent is ViewGroup parent) parent.RemoveView(thinking);
                string message = ex.GetBaseException().Message;
                _chatHistory.Add((message, false, true));
                if (_chatWindow is not null) AddChatMessageView(message, false, true);
                if (_chatSend is not null) _chatSend.Enabled = true;
            });
        }
    }

    private async Task<HeirowLlmClient> EnsureChatClientAsync()
    {
        IServiceProvider services = IPlatformApplication.Current?.Services
            ?? throw new InvalidOperationException("heirowLLM Mobile services are unavailable.");
        var store = services.GetService(typeof(ServerStore)) as ServerStore
            ?? throw new InvalidOperationException("Saved Workstations are unavailable.");
        ServerInfo server = store.Load().FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Endpoint))
            ?? throw new InvalidOperationException("Select and save a Workstation in heirowLLM Mobile first.");
        _chatClient ??= services.GetService(typeof(HeirowLlmClient)) as HeirowLlmClient
            ?? throw new InvalidOperationException("Chicken Chaser networking is unavailable.");
        if (!_chatConnectedServerKey.Equals(server.LaunchKey, StringComparison.OrdinalIgnoreCase))
        {
            await _chatClient.ConnectAsync(server);
            _chatConnectedServerKey = server.LaunchKey;
        }
        return _chatClient;
    }

    private void AddChatMessageView(string text, bool fromUser, bool error)
    {
        if (_chatMessages is null) return;
        var message = new TextView(this)
        {
            Text = text,
            TextSize = 13,
            Gravity = fromUser ? GravityFlags.End : GravityFlags.Start,
            Background = RoundedBackground(
                error ? global::Android.Graphics.Color.Rgb(69, 10, 10) : fromUser ? global::Android.Graphics.Color.Rgb(59, 32, 96) : global::Android.Graphics.Color.Rgb(30, 41, 59),
                Dp(11))
        };
        message.SetTextIsSelectable(true);
        message.SetTextColor(error ? global::Android.Graphics.Color.Rgb(254, 202, 202) : global::Android.Graphics.Color.White);
        message.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent)
        {
            Gravity = fromUser ? GravityFlags.End : GravityFlags.Start,
            TopMargin = Dp(5),
            BottomMargin = Dp(3)
        };
        _chatMessages.AddView(message, layout);
        ScrollChatToBottom();
    }

    private void ScrollChatToBottom()
    {
        global::Android.Widget.ScrollView? scroll = _chatScroll;
        if (scroll is not null) scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
    }

    internal void KeepFloatingComposerVisible()
    {
        if (_chatWindow is null || _chatLayout is null || _windowManager is null) return;

        var visibleFrame = new global::Android.Graphics.Rect();
        _chatWindow.GetWindowVisibleDisplayFrame(visibleFrame);
        int screenHeight = Resources?.DisplayMetrics?.HeightPixels ?? 0;
        if (screenHeight <= 0 || visibleFrame.Height() <= 0) return;

        int imeInsetBottom = 0;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && _chatWindow.RootWindowInsets is { } windowInsets)
            imeInsetBottom = windowInsets.GetInsets(WindowInsets.Type.Ime()).Bottom;
        if (OperatingSystem.IsAndroidVersionAtLeast(30) && imeInsetBottom == 0)
        {
            try
            {
                imeInsetBottom = _windowManager.CurrentWindowMetrics.WindowInsets
                    .GetInsets(WindowInsets.Type.Ime()).Bottom;
            }
            catch { }
        }
        int visibleBottom = imeInsetBottom > 0
            ? Math.Min(visibleFrame.Bottom, screenHeight - imeInsetBottom)
            : visibleFrame.Bottom;
        int hiddenBottom = Math.Max(imeInsetBottom, Math.Max(0, screenHeight - visibleFrame.Bottom));
        bool keyboardVisible = _chatPromptFocused || hiddenBottom >= Dp(100);
        int targetY = _chatRestingY;
        int targetHeight = _chatRestingHeight;

        if (keyboardVisible)
        {
            int top = Math.Max(Dp(8), visibleFrame.Top);
            if (_chatPromptFocused && imeInsetBottom == 0)
                visibleBottom = Math.Min(visibleBottom, (int)Math.Round(screenHeight * .54d));
            if (imeInsetBottom == 0)
            {
                int composerClearance = Math.Max(_chatPrompt?.Height ?? 0, Dp(56));
                visibleBottom = Math.Max(top + Dp(150), visibleBottom - composerClearance);
            }
            int availableHeight = Math.Max(Dp(150), visibleBottom - top - Dp(8));
            targetHeight = Math.Min(_chatRestingHeight, availableHeight);
            targetY = Math.Max(top, visibleBottom - targetHeight - Dp(8));
        }

        if (_chatLayout.Y == targetY && _chatLayout.Height == targetHeight) return;
        _chatLayout.Y = targetY;
        _chatLayout.Height = targetHeight;
        _chatKeyboardAdjusted = keyboardVisible;
        try { _windowManager.UpdateViewLayout(_chatWindow, _chatLayout); } catch { }
        if (keyboardVisible) ScrollChatToBottom();
    }

    private void RaiseFloatingChatForKeyboard()
    {
        if (_chatWindow is null || _chatLayout is null || _windowManager is null) return;
        int screenHeight = Resources?.DisplayMetrics?.HeightPixels ?? 0;
        if (screenHeight <= 0) return;

        int targetY = Dp(8);
        int keyboardSafeBottom = (int)Math.Round(screenHeight * .54d);
        int targetHeight = Math.Max(Dp(240), Math.Min(_chatRestingHeight, keyboardSafeBottom - targetY - Dp(8)));
        _chatLayout.Y = targetY;
        _chatLayout.Height = targetHeight;
        _chatKeyboardAdjusted = true;
        try { _windowManager.UpdateViewLayout(_chatWindow, _chatLayout); } catch { }
        ScrollChatToBottom();
        _chatWindow.PostDelayed(() =>
        {
            EditText? prompt = _chatPrompt;
            if (prompt is null || _chatWindow is null) return;
            prompt.RequestFocus();
            var keyboard = GetSystemService(InputMethodService)?.JavaCast<InputMethodManager>();
            keyboard?.ShowSoftInput(prompt, ShowFlags.Implicit);
        }, 120);
    }

    private void OnFloatingChatGlobalLayout(object? sender, EventArgs e) => KeepFloatingComposerVisible();

    private void ShowUnreadDot()
    {
        Preferences.Default.Set(AndroidChickenChaserOverlayService.NotificationDotKey, true);
        _bubble?.SetNotificationDot(true);
    }

    private static void PostToUi(Action action) => new Handler(Looper.MainLooper!).Post(action);

    private global::Android.Widget.Button HeaderButton(string text, string description, global::Android.Graphics.Color color)
    {
        var button = new global::Android.Widget.Button(this)
        {
            Text = text,
            TextSize = 17,
            ContentDescription = description,
            Background = RoundedBackground(color, Dp(9))
        };
        button.SetMinimumWidth(0);
        button.SetMinimumHeight(0);
        button.SetTextColor(global::Android.Graphics.Color.White);
        var layout = new LinearLayout.LayoutParams(Dp(42), Dp(40)) { LeftMargin = Dp(5) };
        button.LayoutParameters = layout;
        return button;
    }

    private static GradientDrawable RoundedBackground(global::Android.Graphics.Color color, float radius, global::Android.Graphics.Color? stroke = null, int strokeWidth = 0)
    {
        var background = new GradientDrawable();
        background.SetColor(color);
        background.SetCornerRadius(radius);
        if (stroke.HasValue && strokeWidth > 0) background.SetStroke(strokeWidth, stroke.Value);
        return background;
    }

    private async Task ShowChatSettingsAsync()
    {
        if (_chatBody is null) return;
        _chatBody.RemoveAllViews();
        var loading = new TextView(this) { Text = "Loading Workstation settings…", TextSize = 14, Gravity = GravityFlags.Center };
        loading.SetTextColor(global::Android.Graphics.Color.Rgb(203, 213, 225));
        _chatBody.AddView(loading, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        try
        {
            HeirowLlmClient client = await EnsureChatClientAsync();
            ChickenChaserMobileSettings settings = await client.GetChickenChaserSettingsAsync();
            IReadOnlyList<ModelInfo> models = await client.GetModelsAsync();
            PostToUi(() =>
            {
                _chatSettings = settings;
                BuildChatSettingsView(models);
            });
        }
        catch (Exception ex)
        {
            PostToUi(() =>
            {
                _chatBody?.RemoveAllViews();
                var error = new TextView(this) { Text = ex.GetBaseException().Message, TextSize = 14, Gravity = GravityFlags.Center };
                error.SetTextColor(global::Android.Graphics.Color.Rgb(254, 202, 202));
                error.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
                _chatBody?.AddView(error);
            });
        }
    }

    private void BuildChatSettingsView(IReadOnlyList<ModelInfo> models)
    {
        if (_chatBody is null) return;
        _chatBody.RemoveAllViews();
        var settings = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var scroll = new global::Android.Widget.ScrollView(this) { FillViewport = true };
        scroll.AddView(settings);

        string[] assistantValues = ["chat", "help"];
        Spinner assistant = AddSettingChoice(settings, "Assistant mode", ["Chat", "Agent"], assistantValues, _chatSettings.Mode);
        string[] taskValues = ["plan", "goal", "image", "video", "chat", "agent", "voice"];
        Spinner taskMode = AddSettingChoice(settings, "Task mode", ["Plan", "Goal", "Image", "Video", "Chat", "Agent", "Voice"], taskValues, _chatSettings.TaskMode);

        List<ModelInfo> availableModels = models.Where(model => model.SupportsChat && model.IsAvailable).ToList();
        if (!availableModels.Any(model => model.Id.Equals("auto", StringComparison.OrdinalIgnoreCase)))
            availableModels.Insert(0, new ModelInfo { Id = "auto", Name = "Auto · Instant Router", SupportsChat = true, IsAvailable = true });
        string[] modelValues = availableModels.Select(model => model.Id).ToArray();
        string[] modelLabels = availableModels.Select(model => string.IsNullOrWhiteSpace(model.Name) ? model.Id : model.Name).ToArray();
        Spinner model = AddSettingChoice(settings, "Model", modelLabels, modelValues, _chatSettings.Model);

        EditText temperature = AddSettingText(settings, "Temperature", _chatSettings.Temperature.ToString(System.Globalization.CultureInfo.InvariantCulture));
        EditText topP = AddSettingText(settings, "Top P", _chatSettings.TopP.ToString(System.Globalization.CultureInfo.InvariantCulture));
        EditText maxTokens = AddSettingText(settings, "Max tokens", _chatSettings.MaxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture));
        string[] reasoningValues = ["auto", "low", "medium", "high"];
        Spinner reasoning = AddSettingChoice(settings, "Reasoning", ["Auto", "Low", "Medium", "High"], reasoningValues, _chatSettings.Reasoning);
        string[] workValues = ["preset", "compact", "extended"];
        Spinner work = AddSettingChoice(settings, "Work cap", ["Preset", "Compact", "Extended"], workValues, _chatSettings.WorkCap);
        var retry = AddSettingToggle(settings, "Retry failed requests", _chatSettings.Retry);
        var forge = AddSettingToggle(settings, "heirowForge", _chatSettings.HeirowForge);
        var context = AddSettingToggle(settings, "Include current context", _chatSettings.IncludeContext);
        var globalReasoning = AddSettingToggle(settings, "Use global reasoning", _chatSettings.UseGlobalReasoning);

        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        actions.SetGravity(GravityFlags.End);
        var back = HeaderButton("Back", "Back to Chicken Chaser chat", global::Android.Graphics.Color.Rgb(51, 65, 85));
        back.TextSize = 12;
        back.LayoutParameters = new LinearLayout.LayoutParams(Dp(82), Dp(44)) { RightMargin = Dp(7) };
        back.Click += (_, _) => ShowFloatingChatView();
        actions.AddView(back);
        var save = HeaderButton("Save", "Save Chicken Chaser settings", global::Android.Graphics.Color.Rgb(91, 45, 160));
        save.TextSize = 12;
        save.LayoutParameters = new LinearLayout.LayoutParams(Dp(82), Dp(44));
        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            try
            {
                _chatSettings.Mode = SelectedValue(assistant, assistantValues, "chat");
                _chatSettings.TaskMode = SelectedValue(taskMode, taskValues, "agent");
                _chatSettings.Model = SelectedValue(model, modelValues, "auto");
                if (double.TryParse(temperature.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double temp))
                    _chatSettings.Temperature = Math.Clamp(temp, 0, 2);
                if (double.TryParse(topP.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double probability))
                    _chatSettings.TopP = Math.Clamp(probability, 0, 1);
                if (int.TryParse(maxTokens.Text, out int max)) _chatSettings.MaxTokens = Math.Clamp(max, 0, 32768);
                _chatSettings.Reasoning = SelectedValue(reasoning, reasoningValues, "auto");
                _chatSettings.WorkCap = SelectedValue(work, workValues, "preset");
                _chatSettings.Retry = retry.Checked;
                _chatSettings.HeirowForge = forge.Checked;
                _chatSettings.IncludeContext = context.Checked;
                _chatSettings.UseGlobalReasoning = globalReasoning.Checked;
                _chatSettings = await (await EnsureChatClientAsync()).SaveChickenChaserSettingsAsync(_chatSettings);
                PostToUi(ShowFloatingChatView);
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    save.Enabled = true;
                    Toast.MakeText(this, ex.GetBaseException().Message, ToastLength.Long)?.Show();
                });
            }
        };
        actions.AddView(save);
        var actionLayout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent)
        {
            TopMargin = Dp(10), BottomMargin = Dp(8)
        };
        settings.AddView(actions, actionLayout);
        _chatBody.AddView(scroll, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
    }

    private Spinner AddSettingChoice(LinearLayout parent, string label, string[] labels, string[] values, string selected)
    {
        AddSettingLabel(parent, label);
        var spinner = new Spinner(this) { Background = RoundedBackground(global::Android.Graphics.Color.Rgb(22, 30, 46), Dp(8)) };
        var adapter = new ArrayAdapter<string>(this, Resource.Layout.chicken_spinner_item, labels);
        adapter.SetDropDownViewResource(Resource.Layout.chicken_spinner_dropdown_item);
        spinner.Adapter = adapter;
        int index = Array.FindIndex(values, value => value.Equals(selected, StringComparison.OrdinalIgnoreCase));
        spinner.SetSelection(Math.Max(0, index));
        parent.AddView(spinner, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(48)));
        return spinner;
    }

    private EditText AddSettingText(LinearLayout parent, string label, string value)
    {
        AddSettingLabel(parent, label);
        var edit = new EditText(this)
        {
            Text = value,
            TextSize = 13,
            Background = RoundedBackground(global::Android.Graphics.Color.Rgb(22, 30, 46), Dp(8), global::Android.Graphics.Color.Rgb(51, 65, 85), Dp(1))
        };
        edit.SetSingleLine(true);
        edit.SetTextColor(global::Android.Graphics.Color.White);
        edit.SetPadding(Dp(9), Dp(5), Dp(9), Dp(5));
        parent.AddView(edit, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(46)));
        return edit;
    }

    private global::Android.Widget.Switch AddSettingToggle(LinearLayout parent, string label, bool value)
    {
        var toggle = new global::Android.Widget.Switch(this) { Text = label, Checked = value, TextSize = 13 };
        toggle.SetTextColor(global::Android.Graphics.Color.White);
        var layout = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(44)) { TopMargin = Dp(3) };
        parent.AddView(toggle, layout);
        return toggle;
    }

    private void AddSettingLabel(LinearLayout parent, string text)
    {
        var label = new TextView(this) { Text = text, TextSize = 11 };
        label.SetTextColor(global::Android.Graphics.Color.Rgb(148, 163, 184));
        label.SetPadding(0, Dp(8), 0, Dp(3));
        parent.AddView(label);
    }

    private static string SelectedValue(Spinner spinner, string[] values, string fallback)
    {
        int index = spinner.SelectedItemPosition;
        return index >= 0 && index < values.Length ? values[index] : fallback;
    }

    private void RemoveChatWindow()
    {
        if (_chatWindow?.ViewTreeObserver?.IsAlive == true)
        {
            _chatWindow.ViewTreeObserver.GlobalLayout -= OnFloatingChatGlobalLayout;
        }
        if (_chatWindow is not null && _windowManager is not null)
        {
            try { _windowManager.RemoveView(_chatWindow); } catch { }
        }
        _chatWindow?.Dispose();
        _chatWindow = null;
        _chatBody = null;
        _chatMessages = null;
        _chatScroll = null;
        _chatPrompt = null;
        _chatSend = null;
        _chatLayout = null;
        _chatKeyboardAdjusted = false;
        _chatPromptFocused = false;
    }

    private void RemoveBubble()
    {
        if (_bubble is not null && _windowManager is not null)
        {
            try { _windowManager.RemoveView(_bubble); } catch { }
        }
        _bubble?.Dispose();
        RemoveChatWindow();
        _bubble = null;
        _layout = null;
        _windowManager = null;
    }

    private void PersistPosition(int x, int y)
    {
        Preferences.Default.Set(PositionXKey, x);
        Preferences.Default.Set(PositionYKey, y);
    }

    private void ToggleFloatingChat()
    {
        Preferences.Default.Set(AndroidChickenChaserOverlayService.NotificationDotKey, false);
        _bubble?.SetNotificationDot(false);
        if (_chatWindow is null) AddChatWindow();
        else RemoveChatWindow();
    }

    private void OpenChickenChaserInApp()
    {
        Preferences.Default.Set(AndroidChickenChaserOverlayService.NotificationDotKey, false);
        _bubble?.SetNotificationDot(false);
        RemoveChatWindow();
        var open = new Intent(this, typeof(MainActivity));
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        StartActivity(open);
    }

    private int Dp(float value) => (int)Math.Round(value * (Resources?.DisplayMetrics?.Density ?? 1f));
}

internal sealed class FloatingWindowDragListener : Java.Lang.Object, global::Android.Views.View.IOnTouchListener
{
    private readonly IWindowManager _windowManager;
    private readonly global::Android.Views.View _window;
    private readonly WindowManagerLayoutParams _layout;
    private readonly int _threshold;
    private float _downRawX;
    private float _downRawY;
    private int _downX;
    private int _downY;
    private bool _dragged;

    public FloatingWindowDragListener(IWindowManager windowManager, global::Android.Views.View window, WindowManagerLayoutParams layout, int threshold)
    {
        _windowManager = windowManager;
        _window = window;
        _layout = layout;
        _threshold = threshold;
    }

    public bool OnTouch(global::Android.Views.View? view, MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downRawX = e.RawX;
                _downRawY = e.RawY;
                _downX = _layout.X;
                _downY = _layout.Y;
                _dragged = false;
                return true;
            case MotionEventActions.Move:
                float dx = e.RawX - _downRawX;
                float dy = e.RawY - _downRawY;
                if (!_dragged && MathF.Sqrt(dx * dx + dy * dy) >= _threshold) _dragged = true;
                if (_dragged)
                {
                    _layout.X = _downX + (int)dx;
                    _layout.Y = _downY + (int)dy;
                    try { _windowManager.UpdateViewLayout(_window, _layout); } catch { }
                }
                return true;
            case MotionEventActions.Up:
            case MotionEventActions.Cancel:
                return _dragged;
            default:
                return false;
        }
    }
}

internal sealed class ChickenChaserBubbleView : global::Android.Views.View
{
    private readonly WindowManagerLayoutParams _layout;
    private readonly IWindowManager _windowManager;
    private readonly Action<int, int> _persistPosition;
    private readonly Action _open;
    private readonly Bitmap? _sheet;
    private readonly global::Android.Graphics.Paint _paint = new(global::Android.Graphics.PaintFlags.AntiAlias | global::Android.Graphics.PaintFlags.FilterBitmap);
    private bool _showDot;
    private float _downRawX;
    private float _downRawY;
    private int _downX;
    private int _downY;
    private bool _dragged;

    public ChickenChaserBubbleView(
        Context context,
        bool showDot,
        WindowManagerLayoutParams layout,
        IWindowManager windowManager,
        Action<int, int> persistPosition,
        Action open) : base(context)
    {
        _showDot = showDot;
        _layout = layout;
        _windowManager = windowManager;
        _persistPosition = persistPosition;
        _open = open;
        ContentDescription = "Chicken Chaser floating assistant";
        Elevation = Dp(12);
        try
        {
            using Stream stream = context.Assets!.Open("chicken_chaser_sprites.png");
            _sheet = BitmapFactory.DecodeStream(stream);
        }
        catch { }
    }

    public void SetNotificationDot(bool visible)
    {
        _showDot = visible;
        Invalidate();
    }

    protected override void OnDraw(global::Android.Graphics.Canvas canvas)
    {
        base.OnDraw(canvas);
        float pad = Dp(5);
        float dotRoom = Dp(5);
        float diameter = Math.Min(Width, Height) - pad * 2 - dotRoom;
        float left = pad;
        float top = pad + dotRoom;
        float radius = diameter / 2f;
        float cx = left + radius;
        float cy = top + radius;

        _paint.Color = global::Android.Graphics.Color.Rgb(36, 19, 72);
        canvas.DrawCircle(cx, cy, radius, _paint);
        int save = canvas.Save();
        var clip = new global::Android.Graphics.Path();
        clip.AddCircle(cx, cy, radius - Dp(2), global::Android.Graphics.Path.Direction.Cw!);
        canvas.ClipPath(clip);
        if (_sheet is not null)
        {
            int cellWidth = Math.Max(1, _sheet.Width / 4);
            int cellHeight = Math.Max(1, _sheet.Height / 4);
            int cropWidth = Math.Max(1, (int)(cellWidth * .74f));
            int cropHeight = Math.Max(1, (int)(cellHeight * .74f));
            int cropLeft = Math.Max(0, (cellWidth - cropWidth) / 2);
            var source = new global::Android.Graphics.Rect(cropLeft, 0, cropLeft + cropWidth, cropHeight);
            var destination = new global::Android.Graphics.RectF(left, top, left + diameter, top + diameter);
            canvas.DrawBitmap(_sheet, source, destination, _paint);
        }
        canvas.RestoreToCount(save);

        _paint.SetStyle(global::Android.Graphics.Paint.Style.Stroke);
        _paint.StrokeWidth = Dp(2);
        _paint.Color = global::Android.Graphics.Color.Rgb(192, 132, 252);
        canvas.DrawCircle(cx, cy, radius - Dp(1), _paint);
        _paint.SetStyle(global::Android.Graphics.Paint.Style.Fill);

        if (_showDot)
        {
            float dotX = left + diameter - Dp(4);
            float dotY = top + Dp(5);
            _paint.Color = global::Android.Graphics.Color.White;
            canvas.DrawCircle(dotX, dotY, Dp(8), _paint);
            _paint.Color = global::Android.Graphics.Color.Rgb(239, 68, 68);
            canvas.DrawCircle(dotX, dotY, Dp(6), _paint);
        }
    }

    public override bool OnTouchEvent(MotionEvent? e)
    {
        if (e is null) return false;
        switch (e.ActionMasked)
        {
            case MotionEventActions.Down:
                _downRawX = e.RawX;
                _downRawY = e.RawY;
                _downX = _layout.X;
                _downY = _layout.Y;
                _dragged = false;
                return true;
            case MotionEventActions.Move:
                float dx = e.RawX - _downRawX;
                float dy = e.RawY - _downRawY;
                if (!_dragged && MathF.Sqrt(dx * dx + dy * dy) >= Dp(5)) _dragged = true;
                if (_dragged)
                {
                    _layout.X = _downX + (int)dx;
                    _layout.Y = _downY + (int)dy;
                    try { _windowManager.UpdateViewLayout(this, _layout); } catch { }
                }
                return true;
            case MotionEventActions.Up:
                if (_dragged) _persistPosition(_layout.X, _layout.Y);
                else _open();
                PerformClick();
                return true;
            case MotionEventActions.Cancel:
                return true;
            default:
                return base.OnTouchEvent(e);
        }
    }

    public override bool PerformClick()
    {
        base.PerformClick();
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _sheet?.Dispose();
            _paint.Dispose();
        }
        base.Dispose(disposing);
    }

    private float Dp(float value) => value * (Resources?.DisplayMetrics?.Density ?? 1f);
}
