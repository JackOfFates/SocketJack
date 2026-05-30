using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace JackLLMCompanion;

public partial class MainWindow : Window
{
    private readonly CompanionRepository _repository;
    private readonly CompanionHttpHost _host;
    private readonly DesktopAutomationService _desktop;
    private readonly CompanionProcessService _processes;
    private readonly CompanionLlmRunner _runner;
    private readonly CompanionTrainingService _training;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _remoteDesktopTimer;
    private readonly DispatcherTimer _processRefreshTimer;
    private readonly ObservableCollection<CompanionProcessInfo> _processItems = new();
    private readonly ObservableCollection<CompanionProcessBrowserEntry> _processBrowserItems = new();
    private readonly bool _startHidden;
    private readonly int? _ownerProcessId;
    private CancellationTokenSource? _ownerProcessMonitorCancellation;
    private HwndSource? _hotkeySource;
    private Forms.NotifyIcon? _trayIcon;
    private bool _allowExit;
    private bool _hotkeyRegistered;
    private string _hotkeyStatusText = "Ctrl+Esc emergency stop is not registered yet.";
    private string _lastForegroundSignature = "";
    private DateTimeOffset _lastFileScanUtc = DateTimeOffset.MinValue;
    private CompanionModelCatalogResult? _lastModelCatalog;
    private string _processBrowserPath = "";

    private const int EmergencyHotkeyId = 0x4A43;
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint VkEscape = 0x1B;

    public MainWindow(bool startHidden = false, int? ownerProcessId = null)
    {
        _startHidden = startHidden;
        _ownerProcessId = ownerProcessId;
        InitializeComponent();

        _repository = new CompanionRepository();
        _desktop = new DesktopAutomationService();
        _processes = new CompanionProcessService();
        _runner = new CompanionLlmRunner(_repository, _desktop, _processes);
        _training = new CompanionTrainingService(_repository, _desktop);
        _host = new CompanionHttpHost(_repository, _desktop, _runner, _training, _processes);
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _telemetryTimer.Tick += TelemetryTimer_Tick;
        _remoteDesktopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _remoteDesktopTimer.Tick += (_, _) => CaptureRemoteDesktopFrame(audit: false);
        _processRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processRefreshTimer.Tick += (_, _) => RefreshProcessesView(userInitiated: false);
        ProcessDataGrid.ItemsSource = _processItems;
        ProcessBrowserListView.ItemsSource = _processBrowserItems;
        RegisterEmergencyHotkey();
        EnsureTrayIcon();
        StartHost();
        _runner.Start();
        StartOwnerProcessMonitor();
        _telemetryTimer.Start();
        RefreshView();
        RefreshProcessesView(userInitiated: false);
        RefreshProcessBrowser(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        _ = RefreshModelListAsync(userInitiated: false);

        if (_startHidden)
        {
            ShowInTaskbar = false;
            WindowState = WindowState.Minimized;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _ownerProcessMonitorCancellation?.Cancel();
        _ownerProcessMonitorCancellation?.Dispose();
        _ownerProcessMonitorCancellation = null;
        _telemetryTimer.Stop();
        _remoteDesktopTimer.Stop();
        _processRefreshTimer.Stop();
        _runner.Dispose();
        _training.Dispose();
        UnregisterEmergencyHotkey();
        _host.Dispose();
        DisposeTrayIcon();
        SocketJack.ThreadManager.Shutdown();
        base.OnClosed(e);
    }

    private void StartOwnerProcessMonitor()
    {
        if (!_ownerProcessId.HasValue || _ownerProcessId.Value <= 0 || _ownerProcessId.Value == Environment.ProcessId)
            return;

        _ownerProcessMonitorCancellation = new CancellationTokenSource();
        CancellationToken token = _ownerProcessMonitorCancellation.Token;
        int ownerProcessId = _ownerProcessId.Value;

        _ = Task.Run(async () =>
        {
            Process? ownerProcess = null;
            try
            {
                ownerProcess = Process.GetProcessById(ownerProcessId);
            }
            catch
            {
                RequestOwnerShutdown();
                return;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (ownerProcess.HasExited)
                        break;
                    await Task.Delay(1000, token);
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch
            {
            }
            finally
            {
                ownerProcess.Dispose();
            }

            if (!token.IsCancellationRequested)
                RequestOwnerShutdown();
        });
    }

    private void RequestOwnerShutdown()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _allowExit = true;
                Close();
            }), DispatcherPriority.Background);
        }
        catch
        {
        }
    }

    private void StartHost()
    {
        try
        {
            _host.Start();
            StatusText.Text = "Companion workspace running at " + _host.BaseUrl + "/Workspace";
            FooterText.Text = "Database: " + _repository.DataPath + " | " + _hotkeyStatusText;
            UpdateTrayText("SocketJack Companion: " + _host.BaseUrl);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Companion server failed: " + ex.Message;
            FooterText.Text = "Database: " + _repository.DataPath + " | " + _hotkeyStatusText;
            UpdateTrayText("SocketJack Companion failed to start");
        }
    }

    private void RefreshView()
    {
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        RecordingButton.Content = state.IsRecording ? "Stop Recording" : "Start Recording";

        var sb = new StringBuilder();
        sb.AppendLine("Workspace: " + (_host.BaseUrl.Length == 0 ? "not listening" : _host.BaseUrl + "/Workspace"));
        sb.AppendLine("Data: " + state.DataPath);
        sb.AppendLine("Recording: " + (state.IsRecording ? state.ActiveSessionId : "idle"));
        sb.AppendLine();
        sb.AppendLine("Projects");
        foreach (CompanionProject project in state.Projects)
            sb.AppendLine("- " + project.Name + " [" + project.Status + "] " + project.Summary);
        sb.AppendLine();
        sb.AppendLine("Recent sessions");
        foreach (CompanionSessionSummary session in state.Sessions.Take(8))
            sb.AppendLine("- " + session.Title + " | " + session.Status + " | " + session.EventCount + " events | " + session.Id);
        sb.AppendLine();
        sb.AppendLine("Recent events");
        foreach (CompanionEvent ev in state.RecentEvents.Take(12))
            sb.AppendLine("- " + ev.EventType + ": " + ev.Detail);
        OverviewTextBox.Text = sb.ToString();

        CompanionTemplate template = state.Templates.FirstOrDefault(item => string.Equals(item.Id, "JACK", StringComparison.OrdinalIgnoreCase))
            ?? state.Templates.FirstOrDefault()
            ?? new CompanionTemplate { CompanionName = "JACK" };
        CompanionNameTextBox.Text = template.CompanionName;
        InterestsTextBox.Text = template.Interests;
        TemplateTextBox.Text = template.TemplateText;

        CompanionPermissions p = state.Permissions;
        PermHumanInteraction.IsChecked = p.HumanInteraction;
        PermSpendMoney.IsChecked = p.SpendMoney;
        PermAccountLogin.IsChecked = p.AccountLogin;
        PermUseFiles.IsChecked = p.UseFiles;
        PermPcSettings.IsChecked = p.PcSettings;
        PermInternet.IsChecked = p.InternetAccess;
        PermLiveInput.IsChecked = p.LiveInput;

        RefreshRuntimePanels(state);
        CompanionLlmRunnerStatus runner = _runner.GetStatus();
        RunnerEndpointTextBox.Text = runner.ModelEndpoint;
        SetRunnerModelSelection(runner.Model);
        RunnerMaxStepsTextBox.Text = runner.MaxSteps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        RefreshSharesView();
        RefreshTrainingView(state);
    }

    private void TelemetryTimer_Tick(object? sender, EventArgs e)
    {
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        RefreshRuntimePanels(state);
        if (!state.IsRecording)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool includeFiles = state.Permissions.UseFiles && now - _lastFileScanUtc > TimeSpan.FromSeconds(30);
        CompanionEnvironmentSnapshot snapshot = _desktop.CaptureEnvironmentSnapshot(includeFiles);
        string signature = snapshot.Application + "\u001f" + snapshot.Window + "\u001f" + snapshot.Url + "\u001f" + snapshot.Person;

        if (includeFiles)
            _lastFileScanUtc = now;

        if (!includeFiles && string.Equals(signature, _lastForegroundSignature, StringComparison.Ordinal))
            return;

        _lastForegroundSignature = signature;
        _repository.RecordEnvironmentSnapshot(snapshot);
        _training.CaptureSessionKeyframe(state.ActiveSessionId, "environment_change", snapshot);
        StatusText.Text = "Recording Companion context: " + (string.IsNullOrWhiteSpace(snapshot.Application) ? "desktop" : snapshot.Application);
    }

    private void RefreshRuntimePanels(CompanionWorkspaceState state)
    {
        LlmTaskListTextBox.Text = BuildLlmTaskText(state);
        CompanionLlmRunnerStatus runner = _runner.GetStatus();
        LlmStatusText.Text = runner.Message;
        RefreshApprovalsView(state);
        if (state.PendingApprovals.Count > 0)
            StatusText.Text = state.PendingApprovals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Companion approval pending.";
    }

    private void RefreshApprovalsView(CompanionWorkspaceState state)
    {
        string selectedId = (ApprovalRequestsComboBox.SelectedItem as CompanionApprovalRequest)?.Id ?? "";
        ApprovalRequestsComboBox.ItemsSource = null;
        ApprovalRequestsComboBox.ItemsSource = state.PendingApprovals;
        if (state.PendingApprovals.Count > 0)
        {
            CompanionApprovalRequest selected = state.PendingApprovals.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                ?? state.PendingApprovals[0];
            ApprovalRequestsComboBox.SelectedItem = selected;
            ApprovalSummaryText.Text = state.PendingApprovals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " approval request(s) waiting.";
            ApproveSelectedButton.IsEnabled = true;
            DenySelectedButton.IsEnabled = true;
        }
        else
        {
            ApprovalRequestsComboBox.SelectedItem = null;
            ApprovalSummaryText.Text = "No pending approvals.";
            ApproveSelectedButton.IsEnabled = false;
            DenySelectedButton.IsEnabled = false;
        }
        UpdateApprovalDetailText();
    }

    private void UpdateApprovalDetailText()
    {
        if (ApprovalRequestsComboBox.SelectedItem is not CompanionApprovalRequest approval)
        {
            ApprovalDetailTextBox.Text = "No pending approvals.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(approval.Title);
        sb.AppendLine();
        sb.AppendLine("Source: " + approval.Source);
        sb.AppendLine("Capability: " + approval.Capability);
        if (!string.IsNullOrWhiteSpace(approval.RelatedTaskId))
            sb.AppendLine("Task: " + approval.RelatedTaskId);
        sb.AppendLine("Recommended action: " + approval.RecommendedAction);
        sb.AppendLine("Updated: " + approval.UpdatedUtc);
        sb.AppendLine();
        sb.AppendLine(approval.Detail);
        ApprovalDetailTextBox.Text = sb.ToString();
    }

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null)
            return;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Workspace", null, (_, _) => OpenUrl(_host.BaseUrl + "/Workspace"));
        menu.Items.Add("Open Files", null, (_, _) => OpenUrl(_host.BaseUrl + "/file"));
        menu.Items.Add("Show Companion", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Emergency Stop", null, (_, _) => Dispatcher.Invoke(() => EmergencyStop("tray menu")));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = "SocketJack Companion",
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void UpdateTrayText(string text)
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void DisposeTrayIcon()
    {
        if (_trayIcon == null)
            return;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayIcon = null;
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        RefreshView();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit)
            return;
        e.Cancel = true;
        HideToTray();
    }

    private void OpenWorkspace_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(_host.BaseUrl + "/Workspace");
    }

    private void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        OpenUrl(_host.BaseUrl + "/file");
    }

    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshView();
    }

    private void RecordingButton_Click(object sender, RoutedEventArgs e)
    {
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        if (state.IsRecording)
        {
            string sessionId = _repository.StopRecording("Stopped from WPF Companion.");
            _training.CaptureSessionKeyframe(sessionId, "recording_stopped");
            CompanionLlmRunnerStatus runner = _runner.GetStatus();
            _training.StartTraining(sessionId, runner.ModelEndpoint, runner.Model);
        }
        else
        {
            CompanionSessionSummary session = _repository.StartRecording("WPF Companion recording", "Started from the desktop Companion shell.");
            _training.CaptureSessionKeyframe(session.Id, "recording_started");
        }
        RefreshView();
    }

    private void AddManualEvent_Click(object sender, RoutedEventArgs e)
    {
        _repository.AddManualEvent("Manual checkpoint from the WPF Companion dashboard.");
        RefreshView();
    }

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        _repository.SaveTemplate(new CompanionTemplate
        {
            Id = "JACK",
            Name = "JACK",
            CompanionName = CompanionNameTextBox.Text,
            Interests = InterestsTextBox.Text,
            TemplateText = TemplateTextBox.Text
        });
        RefreshView();
    }

    private void GenerateName_Click(object sender, RoutedEventArgs e)
    {
        _repository.GenerateName();
        RefreshView();
    }

    private void InferInterests_Click(object sender, RoutedEventArgs e)
    {
        _repository.InferInterests();
        RefreshView();
    }

    private void SavePermissions_Click(object sender, RoutedEventArgs e)
    {
        _repository.SavePermissions(new CompanionPermissions
        {
            HumanInteraction = PermHumanInteraction.IsChecked == true,
            SpendMoney = PermSpendMoney.IsChecked == true,
            AccountLogin = PermAccountLogin.IsChecked == true,
            UseFiles = PermUseFiles.IsChecked == true,
            PcSettings = PermPcSettings.IsChecked == true,
            InternetAccess = PermInternet.IsChecked == true,
            LiveInput = PermLiveInput.IsChecked == true
        });
        RefreshView();
    }

    private void ApprovalRequestsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateApprovalDetailText();
    }

    private void ApproveSelectedApproval_Click(object sender, RoutedEventArgs e)
    {
        DecideSelectedApproval(approved: true);
    }

    private void DenySelectedApproval_Click(object sender, RoutedEventArgs e)
    {
        DecideSelectedApproval(approved: false);
    }

    private void DecideSelectedApproval(bool approved)
    {
        if (ApprovalRequestsComboBox.SelectedItem is not CompanionApprovalRequest approval)
        {
            ApprovalSummaryText.Text = "No pending approval is selected.";
            return;
        }

        CompanionApprovalDecision decision = _repository.DecideApproval(approval.Id, approved, "desktop companion");
        ApprovalSummaryText.Text = decision.Message;
        if (decision.Ok && approved)
            _runner.Start();
        RefreshView();
    }

    private void SubmitLlmTask_Click(object sender, RoutedEventArgs e)
    {
        SaveRunnerConfigFromUi();
        _runner.Start();
        CompanionLlmTask task = _repository.SubmitLlmTask(LlmGoalTextBox.Text, LlmModeTextBox.Text);
        LlmStatusText.Text = task.Plan;
        RefreshView();
    }

    private void StopLlmTask_Click(object sender, RoutedEventArgs e)
    {
        CompanionLlmTask task = _repository.StopLlmTask("Stopped from WPF Companion.");
        LlmStatusText.Text = string.IsNullOrWhiteSpace(task.Plan) ? "LLM task stopped." : task.Plan;
        RefreshView();
    }

    private void StartRunner_Click(object sender, RoutedEventArgs e)
    {
        SaveRunnerConfigFromUi();
        _runner.Start();
        RefreshView();
    }

    private void StopRunner_Click(object sender, RoutedEventArgs e)
    {
        _runner.Stop("Stopped from WPF Companion.");
        RefreshView();
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelListAsync(userInitiated: true);
    }

    private void SaveTrainingSettings_Click(object sender, RoutedEventArgs e)
    {
        string mode = SelectedComboTag(TrainingApprovalModeComboBox, "review_first");
        if (mode.Equals("enable_all", StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult result = System.Windows.MessageBox.Show(
                "Enable All learned skills is dangerous. JACK may reuse every inferred skill without review. Continue?",
                "Enable All Learned Skills",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return;
        }

        _repository.SaveTrainingSettings(new CompanionTrainingSettings
        {
            LearningEnabled = SelectedComboTag(TrainingLearningComboBox, "true").Equals("true", StringComparison.OrdinalIgnoreCase),
            ApprovalMode = mode,
            ReplayMaxFrames = ParseIntOrDefault(TrainingMaxFramesTextBox.Text, 300),
            ReplayMaxBytes = ParseIntOrDefault(TrainingMaxMbTextBox.Text, 250) * 1024L * 1024L,
            CaptureProfile = "HybridMinimizedReplay"
        });
        RefreshView();
    }

    private void StartTraining_Click(object sender, RoutedEventArgs e)
    {
        SaveRunnerConfigFromUi();
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        string sessionId = state.IsRecording ? state.ActiveSessionId : state.Sessions.FirstOrDefault()?.Id ?? "";
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            TrainingStatusText.Text = "No recording session is available for training.";
            return;
        }

        CompanionLlmRunnerStatus runner = _runner.GetStatus();
        CompanionTrainingRun run = _training.StartTraining(sessionId, runner.ModelEndpoint, runner.Model);
        TrainingStatusText.Text = run.Summary;
        RefreshView();
    }

    private void CancelTraining_Click(object sender, RoutedEventArgs e)
    {
        CompanionTrainingRun run = _training.CancelActiveTraining("Cancelled from WPF Companion.");
        TrainingStatusText.Text = run.Summary;
        RefreshView();
    }

    private void SkillDraftsComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateSkillDraftDetail();
    }

    private void ApproveSkillDraft_Click(object sender, RoutedEventArgs e)
    {
        ReviewSelectedSkillDraft("approve");
    }

    private void EnableSkillDraft_Click(object sender, RoutedEventArgs e)
    {
        ReviewSelectedSkillDraft("enable");
    }

    private void RejectSkillDraft_Click(object sender, RoutedEventArgs e)
    {
        ReviewSelectedSkillDraft("reject");
    }

    private void OpenReplayFolder_Click(object sender, RoutedEventArgs e)
    {
        string replayRoot = Path.Combine(_repository.TrainingRoot, "ReplayFrames");
        Directory.CreateDirectory(replayRoot);
        OpenUrl(replayRoot);
    }

    private void ReviewSelectedSkillDraft(string action)
    {
        if (SkillDraftsComboBox.SelectedItem is not CompanionSkillDraft draft)
        {
            TrainingStatusText.Text = "No skill draft is selected.";
            return;
        }

        bool warningAccepted = true;
        if (action.Equals("enable", StringComparison.OrdinalIgnoreCase) &&
            !draft.RiskLevel.Equals("low", StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult result = System.Windows.MessageBox.Show(
                "This learned skill is not low risk. Enable it anyway?",
                "Enable Learned Skill",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            warningAccepted = result == MessageBoxResult.Yes;
        }

        if (!warningAccepted)
            return;

        CompanionSkillReviewResult review = _repository.ReviewSkillDraft(draft.Id, action, warningAccepted);
        TrainingStatusText.Text = review.Message;
        RefreshView();
    }

    private void CaptureRemoteDesktop_Click(object sender, RoutedEventArgs e)
    {
        CaptureRemoteDesktopFrame(audit: true);
    }

    private void RemoteLiveCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        _remoteDesktopTimer.Start();
        CaptureRemoteDesktopFrame(audit: true);
    }

    private void RemoteLiveCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        _remoteDesktopTimer.Stop();
    }

    private void ClickCenter_Click(object sender, RoutedEventArgs e)
    {
        SendRemoteInput(new DesktopInputRequest
        {
            Action = "click",
            Button = "left",
            X = 0.5,
            Y = 0.5,
            Normalized = true,
            HasPoint = true
        });
    }

    private void SendEsc_Click(object sender, RoutedEventArgs e)
    {
        SendRemoteInput(new DesktopInputRequest { Action = "key", Key = "escape" });
    }

    private void SendRemoteText_Click(object sender, RoutedEventArgs e)
    {
        SendRemoteInput(new DesktopInputRequest { Action = "type", Text = RemoteTextTextBox.Text });
    }

    private void RemoteDesktopImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (RemoteDesktopImage.ActualWidth <= 0 || RemoteDesktopImage.ActualHeight <= 0)
            return;

        System.Windows.Point point = e.GetPosition(RemoteDesktopImage);
        SendRemoteInput(new DesktopInputRequest
        {
            Action = "click",
            Button = "left",
            X = Math.Clamp(point.X / RemoteDesktopImage.ActualWidth, 0, 1),
            Y = Math.Clamp(point.Y / RemoteDesktopImage.ActualHeight, 0, 1),
            Normalized = true,
            HasPoint = true
        });
    }

    private void ChooseShareFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a file to share with SocketJack Companion",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) == true)
            SharePathTextBox.Text = dialog.FileName;
    }

    private void ChooseShareFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder to share with SocketJack Companion",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            SharePathTextBox.Text = dialog.SelectedPath;
    }

    private void ShareSelectedFile_Click(object sender, RoutedEventArgs e)
    {
        SharePathWithApproval(SharePathTextBox.Text);
    }

    private void ShareDrop_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void ShareDrop_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            return;

        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        foreach (string path in paths)
            SharePathWithApproval(path);
    }

    private void SharePathWithApproval(string path)
    {
        try
        {
            List<CompanionSharedFile> shared = _repository.ShareExistingPath(path, ShareNoteTextBox.Text, approved: false);
            if (shared.Any(item => item.RequiresApproval))
            {
                string reason = shared.First(item => item.RequiresApproval).ApprovalReason;
                MessageBoxResult result = System.Windows.MessageBox.Show(
                    this,
                    "This file or folder needs explicit per-file approval before sharing.\n\n" + reason + "\n\nShare it anyway?",
                    "Approve Companion file share",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes)
                {
                    ShareStatusText.Text = "Share cancelled: " + reason;
                    RefreshView();
                    return;
                }

                shared = _repository.ShareExistingPath(path, ShareNoteTextBox.Text, approved: true);
            }

            int count = shared.Count(item => !string.IsNullOrWhiteSpace(item.Id));
            ShareStatusText.Text = count <= 0
                ? shared.FirstOrDefault()?.Note ?? "No files were shared."
                : "Shared " + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " item(s) to " + _repository.ShareRoot;
        }
        catch (Exception ex)
        {
            ShareStatusText.Text = "Share failed: " + ex.Message;
        }
        RefreshView();
    }

    private void RefreshShares_Click(object sender, RoutedEventArgs e)
    {
        RefreshSharesView();
    }

    private void RefreshProcesses_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcessesView(userInitiated: true);
    }

    private void ProcessFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        RefreshProcessesView(userInitiated: false);
    }

    private void ProcessAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        _processRefreshTimer.Start();
        RefreshProcessesView(userInitiated: false);
    }

    private void ProcessAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        _processRefreshTimer.Stop();
    }

    private void OpenProcessLocation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CompanionProcessInfo process)
            return;

        string path = process.ExecutablePath;
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                ProcessStatusText.Text = "Opened file location for " + process.Name + " (" + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").";
                return;
            }

            if (!string.IsNullOrWhiteSpace(process.DirectoryPath) && Directory.Exists(process.DirectoryPath))
            {
                Process.Start(new ProcessStartInfo(process.DirectoryPath) { UseShellExecute = true });
                ProcessStatusText.Text = "Opened folder for " + process.Name + ".";
                return;
            }

            ProcessStatusText.Text = "File location is unavailable for " + process.Name + ": " + process.UnavailableReason;
        }
        catch (Exception ex)
        {
            ProcessStatusText.Text = "Open file location failed: " + ex.Message;
        }
    }

    private void KillProcess_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not CompanionProcessInfo process)
            return;

        MessageBoxResult confirm = System.Windows.MessageBox.Show(
            this,
            "Kill " + process.Name + " (PID " + process.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")?\n\nUnsaved work in that app may be lost.",
            "Kill Process",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        CompanionProcessMutationResult result = _processes.KillProcess(process.Pid, entireTree: true);
        _repository.RecordControlResult("wpf_process_kill", result.Message, result.Ok);
        ProcessStatusText.Text = result.Message;
        RefreshProcessesView(userInitiated: false);
    }

    private void StartProcess_Click(object sender, RoutedEventArgs e)
    {
        var request = new CompanionProcessStartRequest
        {
            Path = ProcessStartPathTextBox.Text,
            Arguments = ProcessStartArgumentsTextBox.Text
        };
        CompanionProcessMutationResult result = _processes.StartProcess(request);
        _repository.RecordControlResult("wpf_process_start", result.Message, result.Ok);
        ProcessStatusText.Text = result.Message;
        RefreshProcessesView(userInitiated: false);
    }

    private void BrowseProcessPath_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcessBrowser(ProcessStartPathTextBox.Text);
    }

    private void BrowseProcessParent_Click(object sender, RoutedEventArgs e)
    {
        CompanionProcessBrowserSnapshot snapshot = _processes.BrowseFileSystem(_processBrowserPath, executableOnly: false);
        RefreshProcessBrowser(snapshot.ParentPath);
    }

    private void ProcessBrowserListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcessBrowserListView.SelectedItem is not CompanionProcessBrowserEntry entry)
            return;

        if (entry.IsDirectory)
        {
            RefreshProcessBrowser(entry.Path);
            return;
        }

        ProcessStartPathTextBox.Text = entry.Path;
        ProcessStatusText.Text = "Selected " + entry.Name + " for start.";
    }

    private void EmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        EmergencyStop("WPF button");
    }

    private void CaptureRemoteDesktopFrame(bool audit)
    {
        if (!_repository.CanUseLiveInput())
        {
            RemoteDesktopStatusText.Text = "Live Input approval is required before remote desktop capture/control.";
            RemoteLiveCheckBox.IsChecked = false;
            return;
        }

        try
        {
            DesktopScreenCapture capture = _desktop.CaptureScreen();
            RemoteDesktopImage.Source = LoadBitmap(capture.Bytes);
            RemoteDesktopStatusText.Text = "Remote desktop frame: " + capture.Width + "x" + capture.Height + " from virtual desktop " + capture.Left + "," + capture.Top + ".";
            if (audit)
                _repository.RecordControlResult("wpf_screen", "Screen captured from WPF remote desktop tab.", true);
        }
        catch (Exception ex)
        {
            RemoteDesktopStatusText.Text = "Capture failed: " + ex.Message;
            if (audit)
                _repository.RecordControlResult("wpf_screen", ex.Message, false);
        }
    }

    private void SendRemoteInput(DesktopInputRequest request)
    {
        if (!_repository.CanUseLiveInput())
        {
            RemoteDesktopStatusText.Text = "Live Input approval is required before sending desktop input.";
            _repository.RecordControlResult("wpf_input", "WPF input blocked because Live Input is disabled.", false);
            return;
        }

        DesktopInputResult result = _desktop.ExecuteInput(request);
        _repository.RecordControlResult(request.Action, result.Message, result.Ok);
        RemoteDesktopStatusText.Text = result.Message;
        if (result.Ok && RemoteLiveCheckBox.IsChecked == true)
            CaptureRemoteDesktopFrame(audit: false);
    }

    private void EmergencyStop(string source)
    {
        CompanionEmergencyStopResult result = _repository.EmergencyStop(source);
        _runner.Stop("Emergency stop from " + source + ".");
        _runner.Start();
        _remoteDesktopTimer.Stop();
        RemoteLiveCheckBox.IsChecked = false;
        RemoteDesktopStatusText.Text = result.Message;
        LlmStatusText.Text = result.Message;
        StatusText.Text = "Emergency stop complete. " + _hotkeyStatusText;
        RefreshView();
    }

    private void RefreshSharesView()
    {
        CompanionFileState files = _repository.GetFileState();
        var sb = new StringBuilder();
        sb.AppendLine("Share root: " + _repository.ShareRoot);
        sb.AppendLine();
        sb.AppendLine("Shared files");
        foreach (CompanionSharedFile shared in files.SharedFiles.Take(20))
            sb.AppendLine("- " + shared.Name + " | " + shared.SizeBytes + " bytes | " + shared.Id);
        if (files.SharedFiles.Count == 0)
            sb.AppendLine("- none");
        sb.AppendLine();
        sb.AppendLine("Session files");
        foreach (CompanionFileRecord file in files.Files.Take(20))
            sb.AppendLine("- " + file.Name + " | " + file.Kind + " | " + file.Path);
        if (files.Files.Count == 0)
            sb.AppendLine("- none");
        SharedFilesTextBox.Text = sb.ToString();
    }

    private void RefreshProcessesView(bool userInitiated)
    {
        try
        {
            var query = new CompanionProcessQuery
            {
                Query = ProcessFilterTextBox?.Text ?? "",
                WindowedOnly = ProcessWindowedOnlyCheckBox?.IsChecked == true,
                IncludeSystem = true,
                Take = 500,
                Sort = "cpu"
            };
            CompanionProcessSnapshot snapshot = _processes.GetProcessSnapshot(query);
            _processItems.Clear();
            foreach (CompanionProcessInfo process in snapshot.Processes)
                _processItems.Add(process);

            ProcessStatusText.Text =
                "Showing " + snapshot.Processes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " of " + snapshot.FilteredCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " filtered processes (" + snapshot.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " total). Total RAM: " + snapshot.TotalRamGb.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) +
                " GB. GPU metrics: " + (snapshot.GpuAvailable ? "available" : "unavailable - " + snapshot.GpuUnavailableReason);
            if (userInitiated)
                StatusText.Text = "Processes refreshed.";
        }
        catch (Exception ex)
        {
            ProcessStatusText.Text = "Process refresh failed: " + ex.Message;
            if (userInitiated)
                StatusText.Text = ProcessStatusText.Text;
        }
    }

    private void RefreshProcessBrowser(string path)
    {
        CompanionProcessBrowserSnapshot snapshot = _processes.BrowseFileSystem(path, executableOnly: false);
        _processBrowserPath = snapshot.Path;
        _processBrowserItems.Clear();
        foreach (CompanionProcessBrowserEntry entry in snapshot.Entries)
            _processBrowserItems.Add(entry);

        if (string.IsNullOrWhiteSpace(ProcessStartPathTextBox.Text) || Directory.Exists(ProcessStartPathTextBox.Text))
            ProcessStartPathTextBox.Text = snapshot.Path;
        if (!string.IsNullOrWhiteSpace(snapshot.Error))
            ProcessStatusText.Text = "Browse failed: " + snapshot.Error;
        else
            ProcessStatusText.Text = "Browsing " + (string.IsNullOrWhiteSpace(snapshot.Path) ? "drives" : snapshot.Path) + " (" + snapshot.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " entries).";
    }

    private void RefreshTrainingView(CompanionWorkspaceState state)
    {
        CompanionTrainingState training = state.Training ?? _repository.GetTrainingState();
        CompanionTrainingSettings settings = training.Settings ?? new CompanionTrainingSettings();
        SelectComboByTag(TrainingLearningComboBox, settings.LearningEnabled ? "true" : "false");
        SelectComboByTag(TrainingApprovalModeComboBox, settings.ApprovalMode);
        TrainingMaxFramesTextBox.Text = settings.ReplayMaxFrames.ToString(System.Globalization.CultureInfo.InvariantCulture);
        TrainingMaxMbTextBox.Text = Math.Max(1, settings.ReplayMaxBytes / 1024 / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture);

        CompanionTrainingRun? active = training.Runs.FirstOrDefault(run =>
            !new[] { "completed", "cancelled", "failed", "disabled", "needs_model" }.Contains(run.Status, StringComparer.OrdinalIgnoreCase));
        TrainingStatusText.Text = active == null
            ? "Training idle. Mode: " + settings.ApprovalMode + "."
            : "Training " + active.Status + " " + active.Progress.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%: " + active.Summary;

        var runs = new StringBuilder();
        foreach (CompanionTrainingRun run in training.Runs.Take(12))
        {
            runs.AppendLine(run.Status + " | " + run.Progress.ToString(System.Globalization.CultureInfo.InvariantCulture) + "% | " + run.RiskLevel);
            runs.AppendLine(run.SourceSessionId);
            runs.AppendLine(run.Summary);
            if (!string.IsNullOrWhiteSpace(run.Error))
                runs.AppendLine("Error: " + run.Error);
            runs.AppendLine();
        }
        if (runs.Length == 0)
            runs.AppendLine("No training runs yet.");
        TrainingRunsTextBox.Text = runs.ToString();

        string selectedId = (SkillDraftsComboBox.SelectedItem as CompanionSkillDraft)?.Id ?? "";
        SkillDraftsComboBox.ItemsSource = null;
        SkillDraftsComboBox.ItemsSource = training.SkillDrafts;
        if (training.SkillDrafts.Count > 0)
            SkillDraftsComboBox.SelectedItem = training.SkillDrafts.FirstOrDefault(item => string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase)) ?? training.SkillDrafts[0];
        UpdateSkillDraftDetail();

        var replay = new StringBuilder();
        foreach (CompanionTrainingEvidence evidence in training.Evidence.Where(item => !string.IsNullOrWhiteSpace(item.KeyframePath)).Take(20))
            replay.AppendLine(evidence.Id + " | " + evidence.SensitivityFlags + " | " + evidence.KeyframePath);
        if (replay.Length == 0)
            replay.AppendLine("No replay keyframes indexed yet.");
        ReplayEvidenceTextBox.Text = replay.ToString();
    }

    private void UpdateSkillDraftDetail()
    {
        if (SkillDraftsComboBox.SelectedItem is not CompanionSkillDraft draft)
        {
            SkillDraftDetailTextBox.Text = "No skill draft selected.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine(draft.Name);
        sb.AppendLine("Status: " + draft.Status + " | Risk: " + draft.RiskLevel + " | Confidence: " + draft.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%");
        sb.AppendLine("Trigger: " + draft.Trigger);
        sb.AppendLine("Prerequisites: " + draft.Prerequisites);
        sb.AppendLine("Safety gates: " + draft.SafetyGates);
        sb.AppendLine("Evidence: " + draft.EvidenceRefs);
        sb.AppendLine();
        sb.AppendLine(draft.Steps);
        SkillDraftDetailTextBox.Text = sb.ToString();
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        foreach (object item in comboBox.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem comboItem &&
                string.Equals(comboItem.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = comboItem;
                return;
            }
        }
        if (comboBox.Items.Count > 0)
            comboBox.SelectedIndex = 0;
    }

    private static string SelectedComboTag(System.Windows.Controls.ComboBox comboBox, string fallback)
    {
        return comboBox.SelectedItem is System.Windows.Controls.ComboBoxItem comboItem && comboItem.Tag != null
            ? comboItem.Tag.ToString() ?? fallback
            : fallback;
    }

    private static int ParseIntOrDefault(string value, int fallback)
    {
        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
    }

    private static string BuildLlmTaskText(CompanionWorkspaceState state)
    {
        var sb = new StringBuilder();
        foreach (CompanionLlmTask task in state.LlmTasks.Take(20))
        {
            sb.AppendLine(task.Status + " | " + task.Mode);
            sb.AppendLine(task.Goal);
            sb.AppendLine(task.Plan);
            sb.AppendLine(task.Id);
            sb.AppendLine();
        }
        if (sb.Length == 0)
            sb.AppendLine("No LLM desktop tasks queued.");
        return sb.ToString();
    }

    private void SaveRunnerConfigFromUi()
    {
        int.TryParse(RunnerMaxStepsTextBox.Text, out int maxSteps);
        _runner.Configure(RunnerEndpointTextBox.Text, GetSelectedRunnerModel(), maxSteps);
    }

    private async Task RefreshModelListAsync(bool userInitiated)
    {
        string selected = GetSelectedRunnerModel();
        try
        {
            CompanionModelCatalogResult catalog = await CompanionModelCatalog.DiscoverAsync(RunnerEndpointTextBox.Text, selected, CancellationToken.None).ConfigureAwait(true);
            _lastModelCatalog = catalog;
            SetRunnerModelOptions(catalog.Models.Select(model => model.Id).Where(id => !string.IsNullOrWhiteSpace(id)), catalog.Selected);
            LlmStatusText.Text = catalog.Ok
                ? "Loaded " + catalog.Models.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " model(s) from " + catalog.Source + "."
                : "Model list unavailable: " + catalog.Warning;
        }
        catch (Exception ex)
        {
            SetRunnerModelOptions(new[] { selected }, selected);
            if (userInitiated)
                LlmStatusText.Text = "Model refresh failed: " + ex.Message;
        }
    }

    private void SetRunnerModelSelection(string model)
    {
        model = string.IsNullOrWhiteSpace(model) ? "local-model" : model.Trim();
        IEnumerable<string> knownModels = _lastModelCatalog?.Models.Select(item => item.Id) ?? Array.Empty<string>();
        SetRunnerModelOptions(knownModels.Prepend(model), model);
    }

    private void SetRunnerModelOptions(IEnumerable<string> models, string selected)
    {
        selected = string.IsNullOrWhiteSpace(selected) ? "local-model" : selected.Trim();
        List<string> values = models
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!values.Any(model => string.Equals(model, selected, StringComparison.OrdinalIgnoreCase)))
            values.Insert(0, selected);
        if (values.Count == 0)
            values.Add("local-model");

        RunnerModelComboBox.ItemsSource = values;
        RunnerModelComboBox.Text = values.FirstOrDefault(model => string.Equals(model, selected, StringComparison.OrdinalIgnoreCase)) ?? values[0];
    }

    private string GetSelectedRunnerModel()
    {
        return string.IsNullOrWhiteSpace(RunnerModelComboBox.Text) ? "local-model" : RunnerModelComboBox.Text.Trim();
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void RegisterEmergencyHotkey()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            _hotkeySource = HwndSource.FromHwnd(handle);
            _hotkeySource?.AddHook(WndProc);
            _hotkeyRegistered = RegisterHotKey(handle, EmergencyHotkeyId, ModControl, VkEscape);
            _hotkeyStatusText = _hotkeyRegistered
                ? "Ctrl+Esc emergency stop is registered."
                : "Ctrl+Esc hotkey registration failed; use the Emergency Stop button.";
        }
        catch
        {
            _hotkeyRegistered = false;
            _hotkeyStatusText = "Ctrl+Esc hotkey registration failed; use the Emergency Stop button.";
        }
    }

    private void UnregisterEmergencyHotkey()
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            if (_hotkeyRegistered && handle != IntPtr.Zero)
                UnregisterHotKey(handle, EmergencyHotkeyId);
            _hotkeySource?.RemoveHook(WndProc);
        }
        catch
        {
        }
        finally
        {
            _hotkeyRegistered = false;
            _hotkeySource = null;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == EmergencyHotkeyId)
        {
            EmergencyStop("Ctrl+Esc hotkey");
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
