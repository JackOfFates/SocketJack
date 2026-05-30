using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace JackLLM;

public partial class StartupLoadingWindow : Window {
    private bool _allowClose;
    private bool _cancelRequested;
    private bool _isIndeterminate;
    private double _targetValue;
    private double _progressVelocity;
    private double _gradientAnglePhase;
    private double _gradientDepthPhase;
    private double _lavaSweepPhase;
    private DateTimeOffset _lastProgressUtc = DateTimeOffset.UtcNow;
    private TimeSpan _lastFrameTime;
    private static readonly double[] RgbBaseOffsets = { 0, 0.10, 0.22, 0.36, 0.52, 0.67, 0.82, 0.92, 1 };

    public StartupLoadingWindow() {
        InitializeComponent();
    }

    public event EventHandler? CancelRequested;

    private bool TryBeginOnUi(Action action) {
        if (action == null)
            return false;

        try {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return false;
            Dispatcher.BeginInvoke(new Action(() => {
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;
                try { action(); } catch { }
            }));
            return true;
        } catch {
            return false;
        }
    }

    public void UpdateProgress(StartupLoadingProgress progress) {
        if (!Dispatcher.CheckAccess()) {
            TryBeginOnUi(() => UpdateProgress(progress));
            return;
        }

        if (progress == null)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double elapsedSeconds = Math.Max(0.001, (now - _lastProgressUtc).TotalSeconds);
        double nextValue = Math.Clamp(progress.Value, 0, 100);
        double visibleValue = Math.Max(_targetValue, nextValue);
        double instantVelocity = Math.Max(0, visibleValue - _targetValue) / elapsedSeconds;
        _progressVelocity = (_progressVelocity * 0.72) + (instantVelocity * 0.28);
        _lastProgressUtc = now;
        _targetValue = visibleValue;
        _isIndeterminate = progress.IsIndeterminate;

        StatusText.Text = string.IsNullOrWhiteSpace(progress.Message)
            ? "Preparing JackLLM Workstation..."
            : progress.Message;
        DetailText.Text = string.IsNullOrWhiteSpace(progress.Detail)
            ? "Loading startup services."
            : progress.Detail;
        PercentText.Text = _targetValue.ToString("0") + "%";

        UpdateProgressWidth();
    }

    public void CompleteAndClose() {
        if (!Dispatcher.CheckAccess()) {
            TryBeginOnUi(CompleteAndClose);
            return;
        }

        _allowClose = true;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) {
        CompositionTarget.Rendering += OnRendering;
        UpdateProgressWidth();
        UpdateGradientBrush(0);
    }

    private void Window_Closed(object? sender, EventArgs e) {
        CompositionTarget.Rendering -= OnRendering;
    }

    private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e) {
        UpdateProgressWidth();
    }

    private void OnRendering(object? sender, EventArgs e) {
        if (e is not RenderingEventArgs renderingEventArgs)
            return;

        if (_lastFrameTime == TimeSpan.Zero) {
            _lastFrameTime = renderingEventArgs.RenderingTime;
            return;
        }

        double elapsedSeconds = Math.Max(0.001, (renderingEventArgs.RenderingTime - _lastFrameTime).TotalSeconds);
        _lastFrameTime = renderingEventArgs.RenderingTime;
        double velocityAmplifier = Math.Min(1.0, _progressVelocity * 0.05);
        double depthRate = 0.16 + (velocityAmplifier * 0.5);
        if (_isIndeterminate)
            depthRate = Math.Max(depthRate, 0.34);

        _gradientAnglePhase = (_gradientAnglePhase + (elapsedSeconds * 0.18)) % (Math.PI * 2);
        _gradientDepthPhase = (_gradientDepthPhase + (elapsedSeconds * depthRate)) % 2.0;
        _lavaSweepPhase = (_lavaSweepPhase + (elapsedSeconds * (0.24 + (velocityAmplifier * 0.72)))) % 1.0;
        UpdateGradientBrush(_gradientAnglePhase, _gradientDepthPhase, velocityAmplifier);
    }

    private void UpdateProgressWidth() {
        double trackWidth = ProgressTrack.ActualWidth;
        if (trackWidth <= 0)
            return;

        double targetWidth = trackWidth * (_targetValue / 100.0);
        targetWidth = Math.Max(0, Math.Min(trackWidth, targetWidth));
        ProgressFillHost.Width = targetWidth;
        ProgressTexture.Width = trackWidth;
        RgbStripe.Width = trackWidth;
        LavaBloom.Width = trackWidth;
        LavaSheen.Width = trackWidth;
    }

    private void UpdateGradientBrush(double anglePhase, double depthPhase = 0, double velocityAmplifier = 0) {
        double cycle = depthPhase <= 1 ? depthPhase : 2 - depthPhase;
        double easedDepth = EaseInOutCircle(Math.Clamp(cycle, 0, 1));
        double velocityEase = EaseInOutCircle(velocityAmplifier);
        double zoomAmplifier = velocityEase * 0.0625;
        double angle = anglePhase;
        double radius = 0.68 + (easedDepth * 0.12) + (zoomAmplifier * 0.16);
        double x = Math.Cos(angle) * radius;
        double y = Math.Sin(angle) * radius;
        RgbStripeBrush.StartPoint = new Point(0.5 - x, 0.5 - y);
        RgbStripeBrush.EndPoint = new Point(0.5 + x, 0.5 + y);
        RgbStripe.Opacity = 0.88 + (easedDepth * 0.12);
        LavaBloom.Opacity = 0.38 + (easedDepth * 0.22);
        LavaSheen.Opacity = 0.24 + (easedDepth * 0.24) + (velocityAmplifier * 0.16);
        double sweep = (_lavaSweepPhase * 1.85) - 0.42;
        LavaSheenBrush.StartPoint = new Point(sweep - 0.72, 0.5);
        LavaSheenBrush.EndPoint = new Point(sweep + 0.16, 0.5);

        GradientStop[] stops = { RgbStop0, RgbStop1, RgbStop2, RgbStop3, RgbStop4, RgbStop5, RgbStop6, RgbStop7, RgbStop8 };
        double previous = 0;
        for (int i = 0; i < stops.Length; i++) {
            if (i == 0) {
                stops[i].Offset = 0;
                continue;
            }

            if (i == stops.Length - 1) {
                stops[i].Offset = 1;
                continue;
            }

            double remainingSlots = stops.Length - i - 1;
            double lowerBound = previous + 0.025;
            double upperBound = 1 - (remainingSlots * 0.025);
            double waveAmplitude = 0.025 + (easedDepth * 0.035) + (zoomAmplifier * 0.02);
            double wave = Math.Sin((depthPhase * Math.PI * 2) + (i * 0.72)) * waveAmplitude;
            double offset = Math.Clamp(RgbBaseOffsets[i] + wave, lowerBound, upperBound);
            stops[i].Offset = offset;
            previous = offset;
        }
    }

    private static double EaseInOutCircle(double value) {
        value = Math.Clamp(value, 0, 1);
        if (value < 0.5)
            return (1 - Math.Sqrt(1 - Math.Pow(2 * value, 2))) / 2;
        return (Math.Sqrt(1 - Math.Pow(-2 * value + 2, 2)) + 1) / 2;
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        TryRequestCancelStartup();
    }

    private void Window_Closing(object? sender, CancelEventArgs e) {
        if (_allowClose)
            return;

        e.Cancel = true;
        TryRequestCancelStartup();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ButtonState != MouseButtonState.Pressed)
            return;

        try {
            DragMove();
        } catch {
        }
    }

    private bool TryRequestCancelStartup() {
        if (_cancelRequested)
            return true;

        MessageBoxResult result = MessageBox.Show(
            this,
            "Cancel opening JackLLM Workstation?",
            "Cancel startup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return false;

        _cancelRequested = true;
        CloseButton.IsEnabled = false;
        StatusText.Text = "Canceling startup...";
        DetailText.Text = "Waiting for startup services to stop.";
        PercentText.Text = "Canceling";
        CancelRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
