using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JackLLM.Security;
using System.Text.Json;
using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;

namespace JackLLM;

public partial class StartupLoadingWindow : Window {
    private readonly TaskCompletionSource<string?> _authenticationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
#if DEBUG
    private const bool DevelopmentSecurityMode = true;
#else
    private const bool DevelopmentSecurityMode = false;
#endif
    private readonly SecurityBrokerClient _securityBroker = new(DevelopmentSecurityMode);
    private AuthenticationMode _authenticationMode;
    private string? _pendingEnrollmentGrant;
    private string? _pendingRecoveryBackup;
    private string? _importedRecoveryBackup;
    private readonly bool _recoveryRequested;
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

    public StartupLoadingWindow(bool recoveryRequested = false) {
        _recoveryRequested = recoveryRequested;
        InitializeComponent();
    }

    public event EventHandler? CancelRequested;

    public async Task<string?> AuthenticateAsync(CancellationToken cancellationToken) {
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            _authenticationCompletion.TrySetCanceled(cancellationToken));
#if DEBUG
        await EnsureDevelopmentBrokerAsync(cancellationToken);
#endif
        await RefreshAuthenticationStatusAsync();
        string? grant = await _authenticationCompletion.Task;
        if (string.IsNullOrWhiteSpace(grant)) return null;
        SecurityResponse activation = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            UnlockGrant = grant
        }, cancellationToken);
        if (!activation.Success)
            throw new System.Security.SecurityException("The security broker rejected the unlock grant: " + activation.Message);
        return grant;
    }

#if DEBUG
    private async Task EnsureDevelopmentBrokerAsync(CancellationToken cancellationToken) {
        SecurityResponse existing = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.Status }, cancellationToken, TimeSpan.FromMilliseconds(300));
        if (existing.State != SecurityStateKind.Error) return;
        string? brokerPath = FindDevelopmentBroker();
        if (brokerPath == null) {
            App.WriteCrashLog("Development security broker not found", detail: "BaseDirectory=" + AppContext.BaseDirectory);
            return;
        }
        try {
            Process.Start(new ProcessStartInfo(brokerPath, "--development") {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(brokerPath) ?? AppContext.BaseDirectory
            });
            for (int attempt = 0; attempt < 20; attempt++) {
                await Task.Delay(150, cancellationToken);
                SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.Status }, cancellationToken, TimeSpan.FromMilliseconds(300));
                if (response.State != SecurityStateKind.Error) return;
            }
        } catch (Exception ex) {
            App.WriteCrashLog("Development security broker failed to start", ex, brokerPath);
        }
    }

    private static string? FindDevelopmentBroker() {
        string besideApp = Path.Combine(AppContext.BaseDirectory, "JackLLM.SecurityBroker.exe");
        if (File.Exists(besideApp)) return besideApp;
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent) {
            string candidate = Path.Combine(directory.FullName, "JackLLM.SecurityBroker", "bin", "Debug",
                "net8.0-windows10.0.17763.0", "win-x64", "JackLLM.SecurityBroker.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
#endif

    public void ShowLoadingProgress() {
        AuthenticationPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        Height = 248;
        MinHeight = 230;
    }

    private async Task RefreshAuthenticationStatusAsync() {
        SetAuthenticationBusy(true);
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.Status });
        DevelopmentBadge.Visibility = response.DevelopmentMode || DevelopmentSecurityMode ? Visibility.Visible : Visibility.Collapsed;
        HardwareIdText.Text = FormatHardwareId(response.HardwareId);
        AuthenticationErrorText.Text = response.State == SecurityStateKind.Error ? response.Message : "";
        RecoveryButton.Visibility = response.State is SecurityStateKind.Unenrolled or SecurityStateKind.HardwareMismatch or SecurityStateKind.CredentialMissing or SecurityStateKind.CorruptEnrollment
            ? Visibility.Visible : Visibility.Collapsed;
        ChangePasswordButton.Visibility = response.State == SecurityStateKind.Locked ? Visibility.Visible : Visibility.Collapsed;
        switch (response.State) {
            case SecurityStateKind.Unenrolled:
                ConfigureAuthenticationMode(AuthenticationMode.Enroll, "Set workstation password", response.Message, "SET PASSWORD");
                break;
            case SecurityStateKind.Locked:
                ConfigureAuthenticationMode(AuthenticationMode.Unlock, "Unlock JackLLM Workstation", response.Message, "VERIFY & UNLOCK");
                break;
            case SecurityStateKind.Cooldown:
                ConfigureAuthenticationMode(AuthenticationMode.Cooldown, "Workstation temporarily locked", BuildCooldownMessage(response), "LOCKED");
                ScheduleCooldownRefresh(response.CooldownUntilUtc);
                break;
            case SecurityStateKind.UnsupportedHardware:
                ConfigureAuthenticationMode(AuthenticationMode.Blocked, "TPM-backed Windows Hello required", response.Message, "UNAVAILABLE");
                break;
            case SecurityStateKind.IntegrityFailure:
                ConfigureAuthenticationMode(AuthenticationMode.Blocked, "Build integrity verification failed", response.Message, "BLOCKED");
                break;
            case SecurityStateKind.HardwareMismatch:
            case SecurityStateKind.CredentialMissing:
            case SecurityStateKind.CorruptEnrollment:
                ConfigureAuthenticationMode(AuthenticationMode.Blocked, "Recovery required", response.Message, "BLOCKED");
                break;
            default:
                ConfigureAuthenticationMode(AuthenticationMode.Blocked, "Security broker unavailable", response.Message, "RETRY");
                break;
        }
        if (_recoveryRequested && response.State is SecurityStateKind.Unenrolled or SecurityStateKind.HardwareMismatch or SecurityStateKind.CredentialMissing or SecurityStateKind.CorruptEnrollment)
            ConfigureAuthenticationMode(AuthenticationMode.Recover, "Recover or rebind workstation",
                "Import the recovery-key file and choose a new password. Windows Hello will create a new TPM-bound key.", "RECOVER & REBIND");
        SetAuthenticationBusy(false);
    }

    private void ConfigureAuthenticationMode(AuthenticationMode mode, string title, string detail, string action) {
        _authenticationMode = mode;
        AuthenticationTitleText.Text = title;
        AuthenticationDetailText.Text = detail;
        AuthenticationActionButton.Content = action;
        ConfirmPasswordPanel.Visibility = mode is AuthenticationMode.Enroll or AuthenticationMode.Recover or AuthenticationMode.ChangePassword ? Visibility.Visible : Visibility.Collapsed;
        CurrentPasswordPanel.Visibility = mode == AuthenticationMode.ChangePassword ? Visibility.Visible : Visibility.Collapsed;
        PasswordLabelText.Text = mode == AuthenticationMode.ChangePassword ? "New workstation password" : "Workstation password";
        RecoveryInputPanel.Visibility = mode == AuthenticationMode.Recover ? Visibility.Visible : Visibility.Collapsed;
        AuthenticationPasswordBox.IsEnabled = mode is AuthenticationMode.Enroll or AuthenticationMode.Unlock or AuthenticationMode.Recover or AuthenticationMode.ChangePassword;
        AuthenticationActionButton.IsEnabled = mode is AuthenticationMode.Enroll or AuthenticationMode.Unlock or AuthenticationMode.Recover or AuthenticationMode.ChangePassword;
        RecoveryKeyPanel.Visibility = Visibility.Collapsed;
        if (mode is AuthenticationMode.Enroll or AuthenticationMode.Unlock)
            AuthenticationPasswordBox.Focus();
    }

    private async void AuthenticationActionButton_Click(object sender, RoutedEventArgs e) {
        if (_pendingEnrollmentGrant != null) {
            if (RecoveryKeySavedCheckBox.IsChecked != true) {
                AuthenticationErrorText.Text = "Confirm that you saved the recovery key before continuing.";
                return;
            }
            _authenticationCompletion.TrySetResult(_pendingEnrollmentGrant);
            return;
        }
        if (_authenticationMode == AuthenticationMode.Blocked) {
            await RefreshAuthenticationStatusAsync();
            return;
        }
        if (_authenticationMode is not (AuthenticationMode.Enroll or AuthenticationMode.Unlock or AuthenticationMode.Recover or AuthenticationMode.ChangePassword)) return;
        string password = AuthenticationPasswordBox.Password;
        if (_authenticationMode is AuthenticationMode.Enroll or AuthenticationMode.Recover or AuthenticationMode.ChangePassword) {
            string? validation = PasswordSecurity.Validate(password);
            if (validation != null) { AuthenticationErrorText.Text = validation; return; }
            if (!string.Equals(password, ConfirmPasswordBox.Password, StringComparison.Ordinal)) {
                AuthenticationErrorText.Text = "The password confirmation does not match.";
                return;
            }
        }
        SetAuthenticationBusy(true);
        AuthenticationErrorText.Text = "";
        AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 165));
        try {
            if (_authenticationMode == AuthenticationMode.Enroll)
                await EnrollAsync(password);
            else if (_authenticationMode == AuthenticationMode.Recover)
                await RecoverAsync(password);
            else if (_authenticationMode == AuthenticationMode.ChangePassword)
                await ChangePasswordAsync(password);
            else
                await UnlockAsync(password);
        } catch (Exception ex) {
            AuthenticationErrorText.Text = ex.Message;
        } finally {
            AuthenticationPasswordBox.Clear();
            ConfirmPasswordBox.Clear();
            CurrentPasswordBox.Clear();
            SetAuthenticationBusy(false);
        }
    }

    private async Task EnrollAsync(string password) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginEnroll });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.CreateAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.Enroll, ChallengeId = challenge.ChallengeId, Password = password,
            PublicKey = proof.PublicKey, Signature = proof.Signature, Attestation = proof.Attestation
        });
        if (!response.Success || string.IsNullOrWhiteSpace(response.RecoveryKey)) { ApplyFailure(response); return; }
        _pendingEnrollmentGrant = response.UnlockGrant;
        _pendingRecoveryBackup = response.RecoveryBackup;
        HardwareIdText.Text = FormatHardwareId(response.HardwareId);
        GeneratedRecoveryKeyText.Text = response.RecoveryKey;
        RecoveryKeyPanel.Visibility = Visibility.Visible;
        AuthenticationActionButton.Content = "I SAVED IT — OPEN JACKLLM";
        AuthenticationDetailText.Text = "Enrollment succeeded. Save the recovery key now; it will not be shown again.";
        AuthenticationPasswordBox.IsEnabled = false;
        ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
    }

    private async Task UnlockAsync(string password) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginUnlock });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.OpenAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock, ChallengeId = challenge.ChallengeId,
            Password = password, Signature = proof.Signature
        });
        if (!response.Success || string.IsNullOrWhiteSpace(response.UnlockGrant)) { ApplyFailure(response); return; }
        _authenticationCompletion.TrySetResult(response.UnlockGrant);
    }

    private async Task RecoverAsync(string newPassword) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginUnlock });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.CreateAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.Recover, ChallengeId = challenge.ChallengeId, NewPassword = newPassword,
            RecoveryKey = RecoveryKeyInput.Text, RecoveryBackup = _importedRecoveryBackup,
            PublicKey = proof.PublicKey, Signature = proof.Signature, Attestation = proof.Attestation
        });
        if (!response.Success) { ApplyFailure(response); return; }
        RecoveryKeyInput.Clear();
        await RefreshAuthenticationStatusAsync();
    }

    private async Task ChangePasswordAsync(string newPassword) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginUnlock });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.OpenAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.ChangePassword, ChallengeId = challenge.ChallengeId,
            Password = CurrentPasswordBox.Password, NewPassword = newPassword, Signature = proof.Signature
        });
        if (!response.Success) { ApplyFailure(response); return; }
        await RefreshAuthenticationStatusAsync();
        AuthenticationDetailText.Text = "Password changed. Unlock with the new password.";
    }

    private void ChangePasswordButton_Click(object sender, RoutedEventArgs e) {
        ConfigureAuthenticationMode(AuthenticationMode.ChangePassword, "Change workstation password",
            "Windows Hello and the current password are required before a new password can be saved.", "CHANGE PASSWORD");
        ChangePasswordButton.Visibility = Visibility.Collapsed;
        RecoveryButton.Visibility = Visibility.Collapsed;
        CurrentPasswordBox.Focus();
    }

    private void SaveRecoveryFileButton_Click(object sender, RoutedEventArgs e) {
        if (string.IsNullOrWhiteSpace(GeneratedRecoveryKeyText.Text) || string.IsNullOrWhiteSpace(_pendingRecoveryBackup)) {
            AuthenticationErrorText.Text = "The broker did not provide a portable recovery package.";
            return;
        }
        var dialog = new SaveFileDialog {
            Title = "Save JackLLM recovery key",
            FileName = "JackLLM-Workstation-Recovery-Key.jackllm-recovery",
            DefaultExt = ".jackllm-recovery",
            Filter = "JackLLM recovery key (*.jackllm-recovery)|*.jackllm-recovery|JSON files (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var recoveryFile = new PortableRecoveryFile {
            RecoveryKey = GeneratedRecoveryKeyText.Text,
            RecoveryBackup = _pendingRecoveryBackup
        };
        File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(recoveryFile, new JsonSerializerOptions { WriteIndented = true }));
        RecoveryKeySavedCheckBox.IsChecked = true;
        AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(126, 233, 255));
        AuthenticationErrorText.Text = "Recovery key saved. Keep that file offline or in a protected vault.";
    }

    private void ImportRecoveryFileButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFileDialog {
            Title = "Import JackLLM recovery key",
            Filter = "JackLLM recovery key (*.jackllm-recovery)|*.jackllm-recovery|JSON files (*.json)|*.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        try {
            PortableRecoveryFile file = JsonSerializer.Deserialize<PortableRecoveryFile>(File.ReadAllText(dialog.FileName), SecurityProtocol.Json)
                ?? throw new InvalidDataException("The recovery file is empty.");
            if (file.Version != 1 || !string.Equals(file.Format, "JackLLM Workstation Recovery Key", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(file.RecoveryKey) || string.IsNullOrWhiteSpace(file.RecoveryBackup))
                throw new InvalidDataException("This is not a supported JackLLM recovery-key file.");
            RecoveryKeyInput.Text = file.RecoveryKey;
            _importedRecoveryBackup = file.RecoveryBackup;
            AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(126, 233, 255));
            AuthenticationErrorText.Text = "Recovery backup loaded. Choose and confirm a new workstation password.";
        } catch (Exception ex) {
            _importedRecoveryBackup = null;
            AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 165));
            AuthenticationErrorText.Text = "Could not import the recovery file: " + ex.Message;
        }
    }

    private void RecoveryButton_Click(object sender, RoutedEventArgs e) {
        if (!IsRunningAsAdministrator()) {
            try {
                Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Executable path is unavailable."), "--security-recovery") {
                    UseShellExecute = true,
                    Verb = "runas"
                });
                _cancelRequested = true;
                _authenticationCompletion.TrySetResult(null);
                return;
            } catch (Exception ex) {
                AuthenticationErrorText.Text = "Administrator approval is required for recovery: " + ex.Message;
                return;
            }
        }
        ConfigureAuthenticationMode(AuthenticationMode.Recover, "Recover or rebind workstation",
            "Enter the saved recovery key and choose a new password. Windows Hello will create a new TPM-bound key.", "RECOVER & REBIND");
        RecoveryButton.Visibility = Visibility.Collapsed;
    }

    private static bool IsRunningAsAdministrator() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private void ApplyFailure(SecurityResponse response) {
        AuthenticationErrorText.Text = response.Message;
        HardwareIdText.Text = FormatHardwareId(response.HardwareId);
        if (response.State == SecurityStateKind.Cooldown) {
            ConfigureAuthenticationMode(AuthenticationMode.Cooldown, "Workstation temporarily locked", BuildCooldownMessage(response), "LOCKED");
            ScheduleCooldownRefresh(response.CooldownUntilUtc);
        }
    }

    private void ScheduleCooldownRefresh(DateTimeOffset? untilUtc) {
        if (untilUtc == null) return;
        TimeSpan delay = untilUtc.Value - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(250);
        _ = Task.Delay(delay).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(async () => await RefreshAuthenticationStatusAsync())), TaskScheduler.Default);
    }

    private static string BuildCooldownMessage(SecurityResponse response) {
        if (response.CooldownUntilUtc == null) return response.Message;
        TimeSpan remaining = response.CooldownUntilUtc.Value - DateTimeOffset.UtcNow;
        return $"Too many failed attempts. Try again in {Math.Max(1, Math.Ceiling(remaining.TotalSeconds)):0} seconds.";
    }

    private static string FormatHardwareId(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return "Not enrolled";
        string compact = new(value.Where(Uri.IsHexDigit).ToArray());
        return string.Join('-', Enumerable.Range(0, Math.Min(8, compact.Length / 4)).Select(i => compact.Substring(i * 4, 4))).ToUpperInvariant();
    }

    private void SetAuthenticationBusy(bool busy) {
        AuthenticationActionButton.IsEnabled = !busy && _authenticationMode is AuthenticationMode.Enroll or AuthenticationMode.Unlock or AuthenticationMode.Recover or AuthenticationMode.ChangePassword or AuthenticationMode.Blocked;
        AuthenticationPasswordBox.IsEnabled = !busy && _authenticationMode is AuthenticationMode.Enroll or AuthenticationMode.Unlock or AuthenticationMode.Recover or AuthenticationMode.ChangePassword;
        CurrentPasswordBox.IsEnabled = !busy;
        ConfirmPasswordBox.IsEnabled = !busy;
        RecoveryKeyInput.IsEnabled = !busy;
        if (busy) AuthenticationActionButton.Content = "WORKING...";
        else if (_pendingEnrollmentGrant != null) AuthenticationActionButton.Content = "I SAVED IT — OPEN JACKLLM";
        else AuthenticationActionButton.Content = _authenticationMode switch {
            AuthenticationMode.Enroll => "SET PASSWORD", AuthenticationMode.Unlock => "VERIFY & UNLOCK",
            AuthenticationMode.Recover => "RECOVER & REBIND", AuthenticationMode.ChangePassword => "CHANGE PASSWORD",
            AuthenticationMode.Blocked => "RETRY", _ => "LOCKED"
        };
    }

    private void AuthenticationPasswordBox_KeyDown(object sender, KeyEventArgs e) {
        if (e.Key == Key.Enter) AuthenticationActionButton_Click(AuthenticationActionButton, new RoutedEventArgs());
    }

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
        _authenticationCompletion.TrySetResult(null);
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
        _authenticationCompletion.TrySetResult(null);
        CloseButton.IsEnabled = false;
        StatusText.Text = "Canceling startup...";
        DetailText.Text = "Waiting for startup services to stop.";
        PercentText.Text = "Canceling";
        CancelRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private enum AuthenticationMode { Blocked, Enroll, Unlock, Recover, ChangePassword, Cooldown }
}
