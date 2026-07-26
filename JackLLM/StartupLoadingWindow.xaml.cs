using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Windows.Interop;
using JackLLM.Security;
using Microsoft.Win32;
using System.IO;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace JackLLM;

public partial class StartupLoadingWindow : Window {
    private readonly TaskCompletionSource<string?> _authenticationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SecurityBrokerClient _securityBroker = new(development: false);
    private readonly RememberedDeviceTokenStore _rememberedDeviceTokenStore = new();
    private AuthenticationMode _authenticationMode;
    private string? _pendingEnrollmentGrant;
    private DateTimeOffset? _pendingEnrollmentGrantExpiresUtc;
    private string? _pendingProtectedRecoveryFile;
    private string? _importedRecoveryBackup;
    private string? _importedRecoveryKey;
    private string? _rememberedUnlockFailure;
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
    private readonly DispatcherTimer _enrollmentGrantTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private static readonly double[] RgbBaseOffsets = { 0, 0.10, 0.22, 0.36, 0.52, 0.67, 0.82, 0.92, 1 };

    public StartupLoadingWindow(bool recoveryRequested = false) {
        _recoveryRequested = recoveryRequested;
        InitializeComponent();
        RecoverySavePathTextBox.Text = GetDefaultRecoveryFilePath();
        _enrollmentGrantTimer.Tick += EnrollmentGrantTimer_Tick;
    }

    public event EventHandler? CancelRequested;

    public async Task<string?> AuthenticateAsync(CancellationToken cancellationToken) {
        using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            _authenticationCompletion.TrySetCanceled(cancellationToken));
        await RefreshAuthenticationStatusAsync();
        return await _authenticationCompletion.Task;
    }

    public void ShowLoadingProgress() {
        AuthenticationPanel.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        MinHeight = 248;
        MaxHeight = 280;
        Height = 260;
        Width = 560;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            UpdateLayout();
            RecenterOnCurrentMonitor();
        }));
    }

    private void RecenterOnCurrentMonitor() {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        System.Drawing.Rectangle workingArea =
            System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        double workingLeft = workingArea.Left / dpi.DpiScaleX;
        double workingTop = workingArea.Top / dpi.DpiScaleY;
        double workingWidth = workingArea.Width / dpi.DpiScaleX;
        double workingHeight = workingArea.Height / dpi.DpiScaleY;
        double windowWidth = ActualWidth > 0 ? ActualWidth : Width;
        double windowHeight = ActualHeight > 0 ? ActualHeight : Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = workingLeft + Math.Max(0, (workingWidth - windowWidth) / 2);
        Top = workingTop + Math.Max(0, (workingHeight - windowHeight) / 2);
    }

    private async Task RefreshAuthenticationStatusAsync() {
        SetAuthenticationBusy(true);
        SecurityResponse response = await GetSecurityStatusWithStartupAsync();
        if (response.State == SecurityStateKind.Locked && await TryRememberedUnlockAsync())
            return;
        DevelopmentBadge.Visibility = Visibility.Collapsed;
        HardwareIdText.Text = FormatHardwareId(response.HardwareId);
        AuthenticationErrorText.Text = !string.IsNullOrWhiteSpace(_rememberedUnlockFailure)
            ? _rememberedUnlockFailure
            : response.State == SecurityStateKind.Error ? response.Message : "";
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

    private async Task<bool> TryRememberedUnlockAsync() {
        RememberedDeviceToken? remembered = _rememberedDeviceTokenStore.Load();
        if (remembered == null) return false;
        _rememberedUnlockFailure = null;
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.RememberedUnlock,
            RememberedDeviceToken = remembered.Token
        });
        if (response.Success && !string.IsNullOrWhiteSpace(response.UnlockGrant)) {
            SecurityResponse activation = await ActivateUnlockGrantAsync(response.UnlockGrant);
            if (activation.Success) {
                RememberDeviceStatusText.Text =
                    $"Remembered device verified through {(response.RememberedDeviceExpiresUtc ?? remembered.ExpiresUtc).LocalDateTime:g}.";
                _authenticationCompletion.TrySetResult(response.UnlockGrant);
                return true;
            }
        }
        if (response.State == SecurityStateKind.Locked)
            _rememberedDeviceTokenStore.Delete();
        if (!string.IsNullOrWhiteSpace(response.Message)) {
            _rememberedUnlockFailure = response.Message;
            AuthenticationErrorText.Text = response.Message;
            RememberDeviceStatusText.Text = response.Message;
            RememberDeviceStatusText.Visibility = Visibility.Visible;
        }
        return false;
    }

    private async Task<SecurityResponse> GetSecurityStatusWithStartupAsync() {
        SecurityResponse response = await _securityBroker.SendAsync(
            new SecurityRequest { Operation = SecurityOperation.Status },
            timeoutValue: TimeSpan.FromMilliseconds(500));
        if (response.State != SecurityStateKind.Error)
            return response;
        try {
            await EnsureOfficialBrokerRunningAsync(CancellationToken.None);
            return await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.Status });
        } catch (Exception ex) {
            App.WriteCrashLog("Security broker automatic startup failed", ex);
            return new SecurityResponse {
                State = SecurityStateKind.Error,
                Message = "JackLLM could not start the Security Broker: " + ex.GetBaseException().Message,
                DevelopmentMode = false
            };
        }
    }

    private async Task EnsureOfficialBrokerRunningAsync(CancellationToken cancellationToken) {
        try {
            using var service = new ServiceController("JackLLMSecurityBroker");
            try {
                service.Refresh();
                if (service.Status == ServiceControllerStatus.Running)
                    return;
                if (service.Status == ServiceControllerStatus.StartPending) {
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)), cancellationToken);
                    return;
                }
                if (service.Status == ServiceControllerStatus.StopPending) {
                    await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10)), cancellationToken);
                    service.Refresh();
                }
                service.Start();
                await Task.Run(() => service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10)), cancellationToken);
                return;
            } catch (InvalidOperationException) {
                // A source-tree Release build may not have gone through the MSI yet.
                // Start the bundled official broker elevated; it still uses the official
                // pipe and machine-protected credential store.
            }

            string brokerPath = Path.Combine(AppContext.BaseDirectory, "SecurityBroker", "JackLLM.SecurityBroker.exe");
            if (!File.Exists(brokerPath)) {
                throw new FileNotFoundException(
                    "The official Release Security Broker is not installed or bundled. Rebuild JackLLM Workstation in Release mode.",
                    brokerPath);
            }

            using Process brokerProcess = Process.Start(new ProcessStartInfo(brokerPath, "--local-release") {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(brokerPath) ?? AppContext.BaseDirectory
            }) ?? throw new InvalidOperationException("Windows did not start the official Release Security Broker.");

            for (int attempt = 0; attempt < 40; attempt++) {
                await Task.Delay(250, cancellationToken);
                if (brokerProcess.HasExited)
                    throw new InvalidOperationException($"The official Release Security Broker exited with code {brokerProcess.ExitCode}.");
                SecurityResponse response = await _securityBroker.SendAsync(
                    new SecurityRequest { Operation = SecurityOperation.Status },
                    cancellationToken,
                    TimeSpan.FromMilliseconds(300));
                if (response.State != SecurityStateKind.Error &&
                    response.BrokerCompatibility == SecurityProtocol.BrokerCompatibility &&
                    !response.DevelopmentMode)
                    return;
            }
            throw new System.TimeoutException("The official Release Security Broker started but did not become ready.");
        } catch (Exception ex) {
            App.WriteCrashLog("Official Release security broker failed to start", ex);
            throw;
        }
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
        RememberDeviceCheckBox.Visibility = mode is AuthenticationMode.Enroll or AuthenticationMode.Unlock ? Visibility.Visible : Visibility.Collapsed;
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
            SetAuthenticationBusy(true);
            try {
                string grant = _pendingEnrollmentGrant;
                SecurityResponse activation = await ActivateUnlockGrantAsync(grant);
                if (activation.Success) {
                    StopEnrollmentGrantTimer();
                    _authenticationCompletion.TrySetResult(grant);
                    return;
                }
                _pendingEnrollmentGrant = null;
                StopEnrollmentGrantTimer();
                await RefreshAuthenticationStatusAsync();
                AuthenticationErrorText.Text =
                    "Enrollment and recovery-file saving succeeded. The temporary opening grant expired, so unlock once with your new password.";
            } finally {
                SetAuthenticationBusy(false);
            }
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
            SecurityResponse brokerStatus = await GetSecurityStatusWithStartupAsync();
            if (brokerStatus.State == SecurityStateKind.Error) {
                ApplyFailure(brokerStatus);
                return;
            }
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
            RecoveryFilePasswordBox.Clear();
            SetAuthenticationBusy(false);
        }
    }

    private async Task EnrollAsync(string password) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginEnroll });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.CreateAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.Enroll, ChallengeId = challenge.ChallengeId, Password = password,
            PublicKey = proof.PublicKey, Signature = proof.Signature, Attestation = proof.Attestation,
            RememberDevice = RememberDeviceCheckBox.IsChecked == true
        });
        if (!response.Success || string.IsNullOrWhiteSpace(response.RecoveryKey)) { ApplyFailure(response); return; }
        if (string.IsNullOrWhiteSpace(response.RecoveryBackup)) {
            AuthenticationErrorText.Text = "The broker did not generate the portable recovery package.";
            return;
        }
        _pendingEnrollmentGrant = response.UnlockGrant;
        _pendingEnrollmentGrantExpiresUtc = response.UnlockGrantExpiresUtc;
        SaveRememberedToken(response);
        _pendingProtectedRecoveryFile = RecoveryFileProtection.Protect(new PortableRecoveryFile {
            RecoveryKey = response.RecoveryKey,
            RecoveryBackup = response.RecoveryBackup
        }, password);
        HardwareIdText.Text = FormatHardwareId(response.HardwareId);
        GeneratedRecoveryKeyText.Text =
            "Your recovery file has been generated and encrypted. Save it before opening JackLLM; it will not be generated again.";
        RecoveryKeyPanel.Visibility = Visibility.Visible;
        StartEnrollmentGrantTimer();
        AuthenticationActionButton.Content = "I SAVED IT — OPEN JACKLLM";
        AuthenticationDetailText.Text =
            "Enrollment succeeded. Save the generated password-protected recovery file now. It uses your workstation password.";
        AuthenticationPasswordBox.IsEnabled = false;
        ConfirmPasswordPanel.Visibility = Visibility.Collapsed;
    }

    private async Task UnlockAsync(string password) {
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginUnlock });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.OpenAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock, ChallengeId = challenge.ChallengeId,
            Password = password, Signature = proof.Signature,
            RememberDevice = RememberDeviceCheckBox.IsChecked == true
        });
        if (!response.Success || string.IsNullOrWhiteSpace(response.UnlockGrant)) { ApplyFailure(response); return; }
        SaveRememberedToken(response);
        SecurityResponse activation = await ActivateUnlockGrantAsync(response.UnlockGrant);
        if (!activation.Success) { ApplyFailure(activation); return; }
        _authenticationCompletion.TrySetResult(response.UnlockGrant);
    }

    private Task<SecurityResponse> ActivateUnlockGrantAsync(string grant) =>
        _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.CompleteUnlock,
            UnlockGrant = grant
        });

    private void SaveRememberedToken(SecurityResponse response) {
        if (!string.IsNullOrWhiteSpace(response.RememberedDeviceToken) &&
            response.RememberedDeviceExpiresUtc is DateTimeOffset expires) {
            _rememberedDeviceTokenStore.Save(response.RememberedDeviceToken, expires);
            _rememberedUnlockFailure = null;
            RememberDeviceStatusText.Text = $"This device is remembered through {expires.LocalDateTime:g}.";
            RememberDeviceStatusText.Visibility = Visibility.Visible;
        } else if (RememberDeviceCheckBox.IsChecked == true) {
            _rememberedDeviceTokenStore.Delete();
            RememberDeviceStatusText.Text = response.Message;
            RememberDeviceStatusText.Visibility = Visibility.Visible;
        } else {
            _rememberedDeviceTokenStore.Delete();
        }
    }

    private void StartEnrollmentGrantTimer() {
        UpdateEnrollmentGrantTimer();
        _enrollmentGrantTimer.Start();
    }

    private void StopEnrollmentGrantTimer() {
        _enrollmentGrantTimer.Stop();
        _pendingEnrollmentGrantExpiresUtc = null;
    }

    private void EnrollmentGrantTimer_Tick(object? sender, EventArgs e) =>
        UpdateEnrollmentGrantTimer();

    private void UpdateEnrollmentGrantTimer() {
        if (_pendingEnrollmentGrantExpiresUtc == null) {
            RecoveryGrantTimerText.Text = "Opening time remaining: unavailable";
            return;
        }
        TimeSpan remaining = _pendingEnrollmentGrantExpiresUtc.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) {
            _enrollmentGrantTimer.Stop();
            RecoveryGrantTimerText.Text = "Opening grant expired — your enrollment and recovery file are still saved.";
            RecoveryGrantTimerText.Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 165));
            return;
        }
        int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        RecoveryGrantTimerText.Text = $"Opening time remaining: {totalSeconds / 60:00}:{totalSeconds % 60:00}";
        RecoveryGrantTimerText.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 122));
    }

    private async Task RecoverAsync(string newPassword) {
        LoadRecoveryFile();
        SecurityResponse challenge = await _securityBroker.SendAsync(new SecurityRequest { Operation = SecurityOperation.BeginUnlock });
        if (!challenge.Success || string.IsNullOrWhiteSpace(challenge.Challenge)) { ApplyFailure(challenge); return; }
        WindowsHelloProof proof = await WindowsHelloAuthenticator.CreateAndSignAsync(Convert.FromBase64String(challenge.Challenge));
        SecurityResponse response = await _securityBroker.SendAsync(new SecurityRequest {
            Operation = SecurityOperation.Recover, ChallengeId = challenge.ChallengeId, NewPassword = newPassword,
            RecoveryKey = _importedRecoveryKey, RecoveryBackup = _importedRecoveryBackup,
            PublicKey = proof.PublicKey, Signature = proof.Signature, Attestation = proof.Attestation
        });
        if (!response.Success) { ApplyFailure(response); return; }
        _rememberedDeviceTokenStore.Delete();
        _importedRecoveryKey = null;
        _importedRecoveryBackup = null;
        RecoveryFilePasswordBox.Clear();
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
        _rememberedDeviceTokenStore.Delete();
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
        if (string.IsNullOrWhiteSpace(_pendingProtectedRecoveryFile)) {
            AuthenticationErrorText.Text = "The broker did not provide a portable recovery package.";
            return;
        }
        try {
            string path = RecoverySavePathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Choose where to save the recovery file.");
            if (!string.Equals(Path.GetExtension(path), ".jackllm-recovery", StringComparison.OrdinalIgnoreCase))
                path += ".jackllm-recovery";
            string fullPath = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("The selected recovery-file folder does not exist.");
            File.WriteAllText(fullPath, _pendingProtectedRecoveryFile);
            RecoverySavePathTextBox.Text = fullPath;
            RecoveryKeySavedCheckBox.IsChecked = true;
            AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(126, 233, 255));
            AuthenticationErrorText.Text = "Password-protected recovery file saved. Keep it offline or in a protected vault.";
        } catch (Exception ex) {
            AuthenticationErrorText.Foreground = new SolidColorBrush(Color.FromRgb(255, 143, 165));
            AuthenticationErrorText.Text = "Could not save the recovery file: " + ex.Message;
        }
    }

    private void BrowseRecoverySavePathButton_Click(object sender, RoutedEventArgs e) {
        string current = RecoverySavePathTextBox.Text.Trim();
        var dialog = CreateRecoverySaveDialog(current);
        if (dialog.ShowDialog(this) == true)
            RecoverySavePathTextBox.Text = dialog.FileName;
    }

    private void BrowseRecoveryOpenPathButton_Click(object sender, RoutedEventArgs e) {
        var dialog = new OpenFileDialog {
            Title = "Open password-protected JackLLM recovery file",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Filter = "JackLLM recovery file (*.jackllm-recovery)|*.jackllm-recovery",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) {
            RecoveryFilePathInput.Text = dialog.FileName;
            RecoveryFilePasswordBox.Focus();
        }
    }

    private void LoadRecoveryFile() {
        string path = RecoveryFilePathInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Choose an existing password-protected recovery file.", path);
        PortableRecoveryFile file = RecoveryFileProtection.Unprotect(
            File.ReadAllText(path), RecoveryFilePasswordBox.Password);
        _importedRecoveryKey = file.RecoveryKey;
        _importedRecoveryBackup = file.RecoveryBackup;
    }

    private static SaveFileDialog CreateRecoverySaveDialog(string currentPath) {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string fileName = "JackLLM-Workstation-Recovery.jackllm-recovery";
        string initialDirectory = documents;
        if (!string.IsNullOrWhiteSpace(currentPath)) {
            try {
                string fullPath = Path.GetFullPath(currentPath);
                fileName = Path.GetFileName(fullPath);
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                    initialDirectory = directory;
            } catch {
            }
        }
        return new SaveFileDialog {
            Title = "Save password-protected JackLLM recovery file",
            InitialDirectory = initialDirectory,
            FileName = fileName,
            DefaultExt = ".jackllm-recovery",
            Filter = "JackLLM recovery file (*.jackllm-recovery)|*.jackllm-recovery",
            AddExtension = true,
            OverwritePrompt = true
        };
    }

    private static string GetDefaultRecoveryFilePath() {
        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents))
            documents = AppContext.BaseDirectory;
        return Path.Combine(documents, "JackLLM-Workstation-Recovery.jackllm-recovery");
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
        RecoveryFilePathInput.IsEnabled = !busy;
        RecoveryFilePasswordBox.IsEnabled = !busy;
        RecoverySavePathTextBox.IsEnabled = !busy;
        SaveRecoveryFileButton.IsEnabled = !busy;
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
        _enrollmentGrantTimer.Stop();
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
