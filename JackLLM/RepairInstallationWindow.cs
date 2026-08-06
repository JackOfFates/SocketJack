using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shell;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace JackLLM;

internal sealed class RepairInstallationWindow : Window {
    private readonly Exception _startupFailure;
    private readonly bool _startHiddenRequested;
    private readonly TextBlock _titleText;
    private readonly TextBlock _statusText;
    private readonly TextBlock _detailText;
    private readonly TextBlock _statsText;
    private readonly TextBox _logTextBox;
    private readonly ProgressBar _progressBar;
    private readonly Button _retryButton;
    private readonly Button _restartButton;
    private readonly Button _exitButton;
    private readonly DispatcherTimer _activityTimer;
    private int _activityIndex;

    public RepairInstallationWindow(Exception startupFailure, bool startHiddenRequested) {
        _startupFailure = startupFailure;
        _startHiddenRequested = startHiddenRequested;
        Title = "Startup Diagnosis";
        Width = 560;
        Height = 430;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = true;

        WindowChrome.SetWindowChrome(this, new WindowChrome {
            CaptionHeight = 42,
            CornerRadius = new CornerRadius(18),
            GlassFrameThickness = new Thickness(0),
            ResizeBorderThickness = new Thickness(7),
            UseAeroCaptionButtons = false
        });

        _titleText = new TextBlock {
            Text = "Startup Diagnosis",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center
        };
        _statusText = new TextBlock {
            Text = "Checking local installation files",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(228, 245, 255)),
            TextWrapping = TextWrapping.Wrap
        };
        _detailText = new TextBlock {
            Text = BuildFailureDetail(startupFailure),
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(169, 190, 211)),
            TextWrapping = TextWrapping.Wrap
        };
        _statsText = new TextBlock {
            Text = "No network repair or remote update will run",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 200))
        };
        _progressBar = new ProgressBar {
            Height = 5,
            IsIndeterminate = true,
            Minimum = 0,
            Maximum = 100,
            Foreground = new SolidColorBrush(Color.FromRgb(89, 214, 201)),
            Background = new SolidColorBrush(Color.FromArgb(75, 255, 255, 255))
        };
        _logTextBox = new TextBox {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromArgb(125, 7, 12, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 125, 211, 252)),
            Foreground = new SolidColorBrush(Color.FromRgb(215, 231, 246)),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Padding = new Thickness(6, 2, 6, 2),
            MinHeight = 88
        };
        _retryButton = CreateActionButton("Retry", false);
        _restartButton = CreateActionButton("Restart", false);
        _exitButton = CreateActionButton("Exit", true);

        Content = BuildContent();

        _retryButton.Click += (_, _) => StartRepair();
        _restartButton.Click += (_, _) => FinishAndRestart();
        _exitButton.Click += (_, _) => Close();
        SourceInitialized += (_, _) => EnableBlur();
        Loaded += (_, _) => StartRepair();

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _activityTimer.Tick += (_, _) => AdvanceActivityText();
        _activityTimer.Start();
    }

    private UIElement BuildContent() {
        var chrome = new Border {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 125, 211, 252)),
            Background = new SolidColorBrush(Color.FromArgb(184, 10, 15, 22)),
            Effect = new DropShadowEffect {
                BlurRadius = 36,
                ShadowDepth = 0,
                Opacity = 0.42,
                Color = Colors.Black
            }
        };

        var root = new Grid {
            RowDefinitions = {
                new RowDefinition { Height = new GridLength(44) },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }
            }
        };
        chrome.Child = root;

        var titleBar = new Grid {
            Margin = new Thickness(18, 6, 8, 0),
            ColumnDefinitions = {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        titleBar.MouseLeftButtonDown += (_, e) => {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };
        titleBar.Children.Add(_titleText);

        var minimizeButton = CreateChromeButton("_");
        minimizeButton.ToolTip = "Minimize";
        minimizeButton.Click += (_, _) => SystemCommands.MinimizeWindow(this);
        WindowChrome.SetIsHitTestVisibleInChrome(minimizeButton, true);
        Grid.SetColumn(minimizeButton, 1);
        titleBar.Children.Add(minimizeButton);
        root.Children.Add(titleBar);

        var body = new Grid {
            Margin = new Thickness(22, 12, 22, 22),
            RowDefinitions = {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        var orb = BuildAnimatedOrb();
        Grid.SetRow(orb, 0);
        body.Children.Add(orb);

        var statusPanel = new StackPanel { Margin = new Thickness(0, 88, 0, 0) };
        statusPanel.Children.Add(_statusText);
        statusPanel.Children.Add(new Border { Height = 10, Background = Brushes.Transparent });
        statusPanel.Children.Add(_detailText);
        Grid.SetRow(statusPanel, 0);
        body.Children.Add(statusPanel);

        _progressBar.Margin = new Thickness(0, 20, 0, 10);
        Grid.SetRow(_progressBar, 1);
        body.Children.Add(_progressBar);

        _statsText.Margin = new Thickness(0, 0, 0, 12);
        Grid.SetRow(_statsText, 2);
        body.Children.Add(_statsText);

        Grid.SetRow(_logTextBox, 3);
        body.Children.Add(_logTextBox);

        var buttons = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        _restartButton.Margin = new Thickness(10, 0, 0, 0);
        _exitButton.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(_retryButton);
        buttons.Children.Add(_restartButton);
        buttons.Children.Add(_exitButton);
        Grid.SetRow(buttons, 4);
        body.Children.Add(buttons);

        return chrome;
    }

    private UIElement BuildAnimatedOrb() {
        var canvas = new Canvas {
            Width = 148,
            Height = 74,
            HorizontalAlignment = HorizontalAlignment.Left,
            ClipToBounds = false
        };

        AddPulse(canvas, 8, 20, 58, Color.FromRgb(89, 214, 201), 0);
        AddPulse(canvas, 56, 10, 46, Color.FromRgb(125, 211, 252), 0.35);
        AddPulse(canvas, 98, 26, 34, Color.FromRgb(255, 179, 90), 0.7);
        return canvas;
    }

    private static void AddPulse(Canvas canvas, double left, double top, double size, Color color, double delaySeconds) {
        var ellipse = new Ellipse {
            Width = size,
            Height = size,
            Fill = new SolidColorBrush(Color.FromArgb(58, color.R, color.G, color.B)),
            Stroke = new SolidColorBrush(Color.FromArgb(190, color.R, color.G, color.B)),
            StrokeThickness = 1.2,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(0.82, 0.82)
        };
        Canvas.SetLeft(ellipse, left);
        Canvas.SetTop(ellipse, top);
        canvas.Children.Add(ellipse);

        var pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delaySeconds) };
        var scaleX = new DoubleAnimation(0.82, 1.18, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var scaleY = new DoubleAnimation(0.82, 1.18, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        var opacity = new DoubleAnimation(0.55, 1, TimeSpan.FromSeconds(1.8)) { AutoReverse = true, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(scaleX, ellipse);
        Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));
        Storyboard.SetTarget(scaleY, ellipse);
        Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));
        Storyboard.SetTarget(opacity, ellipse);
        Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
        pulse.Children.Add(scaleX);
        pulse.Children.Add(scaleY);
        pulse.Children.Add(opacity);
        pulse.Begin();
    }

    private static Button CreateChromeButton(string text) {
        return new Button {
            Content = text,
            Width = 42,
            Height = 32,
            Padding = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(215, 231, 246)),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold
        };
    }

    private static Button CreateActionButton(string text, bool secondary) {
        return new Button {
            Content = text,
            MinWidth = 96,
            Padding = new Thickness(14, 8, 14, 8),
            Foreground = Brushes.White,
            Background = secondary
                ? new SolidColorBrush(Color.FromArgb(170, 38, 52, 72))
                : new SolidColorBrush(Color.FromRgb(31, 122, 104)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold
        };
    }

    private void StartRepair() {
        _retryButton.IsEnabled = false;
        _restartButton.IsEnabled = false;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = "A required local installation file could not be loaded";
        _statsText.Text = "No network repair or remote update was attempted";
        _logTextBox.Clear();
        AppendLog(BuildFailureDetail(_startupFailure));
        AppendLog("Rebuild or restore the local Release deployment, then choose Restart.");
        _retryButton.IsEnabled = true;
        _restartButton.IsEnabled = true;
    }

    private void FinishAndRestart() {
        try {
            string exePath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "JackLLM.exe");
            string args = _startHiddenRequested ? "--tray" : "";
            Process.Start(new ProcessStartInfo(exePath, args) {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory
            });
        } catch (Exception ex) {
            SetRepairFailed("Restart failed: " + ex.Message);
            return;
        }
        Close();
    }

    private void SetRepairFailed(string message) {
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 0;
        _statusText.Text = message;
        _retryButton.IsEnabled = true;
        _restartButton.IsEnabled = false;
        AppendLog(message);
    }

    private void AdvanceActivityText() {
        if (!_progressBar.IsIndeterminate)
            return;
        string[] verbs = {
            "Checking local runtime files",
            "Reviewing the startup failure",
            "Waiting for a local rebuild",
            "Ready to restart locally"
        };
        _activityIndex = (_activityIndex + 1) % verbs.Length;
        if (_statusText.Text.StartsWith("Starting", StringComparison.OrdinalIgnoreCase) ||
            _statusText.Text.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase) ||
            _statusText.Text.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)) {
            _statusText.Text = verbs[_activityIndex];
        }
    }

    private void AppendLog(string message) {
        if (string.IsNullOrWhiteSpace(message))
            return;
        _logTextBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + message.Trim() + Environment.NewLine);
        _logTextBox.ScrollToEnd();
    }

    private static string BuildFailureDetail(Exception ex) {
        Exception baseException = ex.GetBaseException();
        return "JackLLM could not load a required install file: " + baseException.Message;
    }

    private void EnableBlur() {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        var accent = new AccentPolicy {
            AccentState = AccentEnableBlurBehind,
            GradientColor = unchecked((int)0x77101824)
        };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData {
                Attribute = WindowCompositionAttributeAccentPolicy,
                Data = ptr,
                SizeOfData = size
            };
            _ = SetWindowCompositionAttribute(handle, ref data);
        } finally {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private const int WindowCompositionAttributeAccentPolicy = 19;
    private const int AccentEnableBlurBehind = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
}
