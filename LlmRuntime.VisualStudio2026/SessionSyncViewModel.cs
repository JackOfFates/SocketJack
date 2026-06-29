namespace LlmRuntime.VisualStudio2026;

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;
using Microsoft.VisualStudio.ProjectSystem.Query;

[DataContract]
internal sealed class SessionSyncViewModel : SocketJackAuthenticatedViewModel
{
    private const string GlyphOk = "\u2713";
    private const string GlyphPending = "\u25CF";
    private const string GlyphDeleted = "X";
    private const string SessionFileName = "socketjack.session";
    private const int SessionFileVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly VisualStudioExtensibility extensibility;
    private readonly SessionSyncService service;
    private readonly Dictionary<string, SessionSyncTreeItem> currentItems = new(StringComparer.OrdinalIgnoreCase);
    private string solutionRoot = "";
    private string tempRoot = "";
    private string mirrorRoot = "";
    private string snapshotPath = "";
    private string sessionFilePath = "";
    private string ignoreManifestPath = "";
    private string endpointSummary = "Open a solution, then Refresh.";
    private string selectedDetails = "";
    private string ignorePattern = "";
    private string sessionId = "";
    private string status = "Ready.";
    private string createSessionButtonText = "Create Local Workstation Session";
    private bool isBusy;
    private bool canPull = true;
    private bool hasSessionFile;
    private bool sessionControlsEnabled;
    private bool isSessionBootstrapVisible = true;
    private bool isImportRepoVisible;
    private bool isImportingRepo;
    private string importRepository = "";
    private string importGitHubToken = "";
    private string importRepoStatus = "";
    private string importRepoError = "";
    private List<SessionSyncTreeItem> treeItems = new();
    private SessionSyncSnapshot snapshot = new();
    private SessionSyncIgnoreManifest ignoreManifest = new();
    private SessionSyncBridgeSelection bridgeSelection = new();

    public SessionSyncViewModel(VisualStudioExtensibility extensibility)
        : base(new SocketJackVisualStudioAuthService())
    {
        this.extensibility = extensibility;
        this.service = new SessionSyncService(new HttpClient());
        this.RefreshCommand = new AsyncCommand(this.RefreshAsync);
        this.PullCommand = new AsyncCommand(this.PullAsync);
        this.PushCommand = new AsyncCommand(this.PushAsync);
        this.ShowImportRepoCommand = new AsyncCommand(this.ShowImportRepoAsync);
        this.CancelImportRepoCommand = new AsyncCommand(this.CancelImportRepoAsync);
        this.ImportRepoCommand = new AsyncCommand(this.ImportRepoAsync);
        this.ResetSessionCommand = new AsyncCommand(this.ResetSessionAsync);
        this.CreateSessionCommand = new AsyncCommand(this.CreateSessionAsync);
        this.OpenFileLocationCommand = new AsyncCommand(this.OpenFileLocationAsync);
        this.IgnoreCommand = new AsyncCommand(this.IgnoreAsync);
        this.IgnoreRemoteCommand = new AsyncCommand(this.IgnoreRemoteAsync);
        this.PropertiesCommand = new AsyncCommand(this.PropertiesAsync);
        this.RemoveIgnoreCommand = new AsyncCommand(this.RemoveIgnoreAsync);
        this.IgnoreSelectedCommand = new AsyncCommand(this.IgnoreSelectedAsync);
        this.IgnoreRemoteSelectedCommand = new AsyncCommand(this.IgnoreRemoteSelectedAsync);
        this.RemoveSelectedIgnoreCommand = new AsyncCommand(this.RemoveSelectedIgnoreAsync);
        this.AddIgnoreGlobCommand = new AsyncCommand(this.AddIgnoreGlobAsync);
        this.AddIgnoreRegexCommand = new AsyncCommand(this.AddIgnoreRegexAsync);
        this.UseLocalWorkstationSessionMode();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.EnsureSolutionStateAsync(cancellationToken);
            if (this.HasSessionFile)
            {
                this.BuildTree();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Status = "Session Sync setup failed: " + ex.Message;
            this.RefreshSessionControlAvailability();
        }
    }

    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    [DataMember]
    public IAsyncCommand PullCommand { get; }

    [DataMember]
    public IAsyncCommand PushCommand { get; }

    [DataMember]
    public IAsyncCommand ShowImportRepoCommand { get; }

    [DataMember]
    public IAsyncCommand CancelImportRepoCommand { get; }

    [DataMember]
    public IAsyncCommand ImportRepoCommand { get; }

    [DataMember]
    public IAsyncCommand ResetSessionCommand { get; }

    [DataMember]
    public IAsyncCommand CreateSessionCommand { get; }

    [DataMember]
    public IAsyncCommand OpenFileLocationCommand { get; }

    [DataMember]
    public IAsyncCommand IgnoreCommand { get; }

    [DataMember]
    public IAsyncCommand IgnoreRemoteCommand { get; }

    [DataMember]
    public IAsyncCommand PropertiesCommand { get; }

    [DataMember]
    public IAsyncCommand RemoveIgnoreCommand { get; }

    [DataMember]
    public IAsyncCommand IgnoreSelectedCommand { get; }

    [DataMember]
    public IAsyncCommand IgnoreRemoteSelectedCommand { get; }

    [DataMember]
    public IAsyncCommand RemoveSelectedIgnoreCommand { get; }

    [DataMember]
    public IAsyncCommand AddIgnoreGlobCommand { get; }

    [DataMember]
    public IAsyncCommand AddIgnoreRegexCommand { get; }

    [DataMember]
    public List<SessionSyncTreeItem> TreeItems
    {
        get => this.treeItems;
        set => this.SetProperty(ref this.treeItems, value);
    }

    [DataMember]
    public string SessionId
    {
        get => this.sessionId;
        set
        {
            value = SanitizeAutoSessionId(value);
            if (string.Equals(this.sessionId, value, StringComparison.Ordinal))
            {
                return;
            }

            this.SetProperty(ref this.sessionId, value);
        }
    }

    [DataMember]
    public string EndpointSummary
    {
        get => this.endpointSummary;
        set => this.SetProperty(ref this.endpointSummary, value ?? "");
    }

    [DataMember]
    public string Status
    {
        get => this.status;
        set => this.SetProperty(ref this.status, value ?? "");
    }

    [DataMember]
    public string CreateSessionButtonText
    {
        get => this.createSessionButtonText;
        set => this.SetProperty(ref this.createSessionButtonText, value ?? "");
    }

    [DataMember]
    public string SelectedDetails
    {
        get => this.selectedDetails;
        set => this.SetProperty(ref this.selectedDetails, value ?? "");
    }

    [DataMember]
    public string IgnorePattern
    {
        get => this.ignorePattern;
        set => this.SetProperty(ref this.ignorePattern, value ?? "");
    }

    [DataMember]
    public bool IsBusy
    {
        get => this.isBusy;
        set
        {
            this.SetProperty(ref this.isBusy, value);
            this.RefreshSessionControlAvailability();
        }
    }

    [DataMember]
    public bool CanPull
    {
        get => this.canPull;
        set => this.SetProperty(ref this.canPull, value);
    }

    [DataMember]
    public bool HasSessionFile
    {
        get => this.hasSessionFile;
        set
        {
            this.SetProperty(ref this.hasSessionFile, value);
            this.RefreshSessionControlAvailability();
        }
    }

    [DataMember]
    public bool SessionControlsEnabled
    {
        get => this.sessionControlsEnabled;
        set => this.SetProperty(ref this.sessionControlsEnabled, value);
    }

    [DataMember]
    public bool IsSessionBootstrapVisible
    {
        get => this.isSessionBootstrapVisible;
        set => this.SetProperty(ref this.isSessionBootstrapVisible, value);
    }

    [DataMember]
    public bool IsImportRepoVisible
    {
        get => this.isImportRepoVisible;
        set => this.SetProperty(ref this.isImportRepoVisible, value);
    }

    [DataMember]
    public bool IsImportingRepo
    {
        get => this.isImportingRepo;
        set => this.SetProperty(ref this.isImportingRepo, value);
    }

    [DataMember]
    public string ImportRepository
    {
        get => this.importRepository;
        set => this.SetProperty(ref this.importRepository, value ?? "");
    }

    [DataMember]
    public string ImportGitHubToken
    {
        get => this.importGitHubToken;
        set => this.SetProperty(ref this.importGitHubToken, value ?? "");
    }

    [DataMember]
    public string ImportRepoStatus
    {
        get => this.importRepoStatus;
        set => this.SetProperty(ref this.importRepoStatus, value ?? "");
    }

    [DataMember]
    public string ImportRepoError
    {
        get => this.importRepoError;
        set => this.SetProperty(ref this.importRepoError, value ?? "");
    }

    private Task ShowImportRepoAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        this.ImportRepoError = "";
        this.ImportRepoStatus = "";
        this.IsImportRepoVisible = true;
        return Task.CompletedTask;
    }

    private Task CancelImportRepoAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        if (this.IsImportingRepo)
        {
            return Task.CompletedTask;
        }

        this.IsImportRepoVisible = false;
        this.ImportRepoError = "";
        this.ImportRepoStatus = "";
        this.ImportGitHubToken = "";
        return Task.CompletedTask;
    }

    private async Task ImportRepoAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        if (this.IsImportingRepo)
        {
            return;
        }

        string repository = (this.ImportRepository ?? "").Trim();
        if (string.IsNullOrWhiteSpace(repository))
        {
            this.ImportRepoError = "Enter a GitHub repository such as owner/repo.";
            return;
        }

        this.ImportRepoError = "";
        this.ImportRepoStatus = "Checking GitHub repository...";
        this.IsImportingRepo = true;
        try
        {
            await this.RunBusyAsync("Importing GitHub repository...", async token =>
            {
                try
                {
                    this.UseLocalWorkstationSessionMode();
                    await this.EnsureSolutionStateAsync(token);
                    this.ThrowIfSessionFileMissing();
                    SessionSyncRepositoryImportResult result = await this.service.ImportGitHubRepositoryAsync(
                        this.bridgeSelection,
                        this.SessionId,
                        repository,
                        this.ImportGitHubToken,
                        token);
                    this.ImportGitHubToken = "";
                    foreach (SessionSyncRemoteFile remote in result.Files)
                    {
                        this.UpsertRemoteSnapshot(remote);
                    }

                    string remoteMessage = await this.LoadRemoteMetadataBestEffortAsync(token);
                    this.BuildTree();
                    this.SaveSnapshot();
                    string folder = string.IsNullOrWhiteSpace(result.TargetFolder) ? result.Repository : result.TargetFolder;
                    string message = "Imported " + result.FileCount.ToString(CultureInfo.InvariantCulture) +
                        " files into " + folder +
                        " (" + FormatBytes(result.ZipSizeBytes) + " zip, " +
                        FormatBytes(result.ExtractedSizeBytes) + " extracted).";
                    this.ImportRepoStatus = message;
                    this.Status = message + (string.IsNullOrWhiteSpace(remoteMessage) ? "" : Environment.NewLine + remoteMessage);
                    this.IsImportRepoVisible = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.ImportRepoError = ex.Message;
                    if (!this.HandleAuthException(ex))
                    {
                        this.Status = "GitHub import failed: " + ex.Message;
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            this.IsImportingRepo = false;
            this.ImportGitHubToken = "";
        }
    }

    private async Task RefreshAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Scanning solution files...", async token =>
        {
            this.UseLocalWorkstationSessionMode();
            await this.EnsureSolutionStateAsync(token);
            this.ThrowIfSessionFileMissing();
            string remoteMessage = await this.LoadRemoteMetadataBestEffortAsync(token);
            this.BuildTree();
            this.SaveSnapshot();
            this.Status = "Scanned " + this.currentItems.Count.ToString(CultureInfo.InvariantCulture) +
                " solution files. Temp mirror: " + this.mirrorRoot +
                (string.IsNullOrWhiteSpace(remoteMessage) ? "" : Environment.NewLine + remoteMessage);
        }, cancellationToken);
    }

    private async Task PullAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Pulling local Workstation session files...", async token =>
        {
            this.UseLocalWorkstationSessionMode();
            await this.EnsureSolutionStateAsync(token);
            this.ThrowIfSessionFileMissing();
            IReadOnlyList<SessionSyncRemoteFile> remoteFiles = await this.service.ListRemoteFilesAsync(this.bridgeSelection, this.SessionId, token);
            foreach (SessionSyncRemoteFile remote in remoteFiles)
            {
                this.UpsertRemoteSnapshot(remote);
            }

            this.BuildTree();
            if (this.HasUnpushedLocalChanges(out int pendingUploads, out int pendingDeletes))
            {
                this.SaveSnapshot();
                this.Status = BuildPullDisabledStatus(pendingUploads, pendingDeletes);
                return;
            }

            int downloaded = 0;
            int skipped = 0;

            foreach (SessionSyncRemoteFile remoteFile in remoteFiles)
            {
                if (this.ignoreManifest.IsRemoteIgnored(remoteFile.RelativePath) || this.ignoreManifest.IsIgnored(remoteFile.RelativePath))
                {
                    skipped++;
                    continue;
                }

                string localPath = this.ResolveSolutionPath(remoteFile.RelativePath);
                DateTime localUtc = File.Exists(localPath) ? File.GetLastWriteTimeUtc(localPath) : DateTime.MinValue;
                bool localMatchesRemote = false;
                if (File.Exists(localPath))
                {
                    FileInfo info = new(localPath);
                    string localHash = info.Length <= SessionSyncService.QuickHashBytes ? ComputeSha256(localPath) : "";
                    string remoteHash = FirstNonEmpty(remoteFile.Sha256, this.snapshot.Find(remoteFile.RelativePath)?.RemoteSha256);
                    localMatchesRemote = KnownHashesEqual(localHash, remoteHash);
                }

                if (File.Exists(localPath) && (localUtc >= remoteFile.LastWriteUtc.UtcDateTime || localMatchesRemote))
                {
                    skipped++;
                    continue;
                }

                SessionSyncTreeItem? item = this.TryFindItem(remoteFile.RelativePath);
                if (item != null)
                {
                    item.StatusText = "Downloading";
                    item.StatusGlyph = GlyphPending;
                    item.StatusColor = "#FFB347";
                    item.Progress = 15;
                }

                byte[] bytes = await this.service.DownloadRemoteFileAsync(this.bridgeSelection, this.SessionId, remoteFile, token);
                Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? this.solutionRoot);
                await File.WriteAllBytesAsync(localPath, bytes, token);
                if (remoteFile.LastWriteUtc > DateTimeOffset.MinValue)
                {
                    File.SetLastWriteTimeUtc(localPath, remoteFile.LastWriteUtc.UtcDateTime);
                }

                this.UpdateMirrorAndSnapshot(remoteFile.RelativePath, bytes, File.GetLastWriteTimeUtc(localPath), remoteFile);
                downloaded++;
                if (item != null)
                {
                    item.Progress = 100;
                    item.StatusText = "OK";
                    item.StatusGlyph = GlyphOk;
                    item.StatusColor = "#7DCEA0";
                }
            }

            this.SaveSnapshot();
            this.BuildTree();
            this.Status = "Pull complete. Downloaded " + downloaded.ToString(CultureInfo.InvariantCulture) +
                ", skipped " + skipped.ToString(CultureInfo.InvariantCulture) + ".";
        }, cancellationToken);
    }

    private async Task PushAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Pushing local changes to local Workstation session files...", async token =>
        {
            this.UseLocalWorkstationSessionMode();
            await this.EnsureSolutionStateAsync(token);
            this.ThrowIfSessionFileMissing();
            this.BuildTree();

            List<SessionSyncTreeItem> pending = this.currentItems.Values
                .Where(item => !item.IsFolder && !item.IsIgnored && item.SyncState == SessionSyncItemState.PendingUpload)
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            int uploaded = 0;
            int skipped = 0;
            foreach (SessionSyncTreeItem item in pending)
            {
                if (!File.Exists(item.FullPath))
                {
                    skipped++;
                    continue;
                }

                FileInfo info = new(item.FullPath);
                if (info.Length > SessionSyncService.MaxAutoUploadBytes)
                {
                    item.StatusText = "Too large";
                    item.MetricText = FormatBytes(info.Length);
                    skipped++;
                    continue;
                }

                SessionSyncSnapshotFile? previous = this.snapshot.Find(item.RelativePath);
                bool localChanged = previous == null ||
                    SessionSyncDiffState.HasUnpushedLocalChange(
                        localExists: true,
                        previous?.LocalLastWriteUtc ?? DateTimeOffset.MinValue,
                        previous?.LocalSizeBytes ?? 0,
                        previous?.LocalSha256 ?? "",
                        new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                        info.Length,
                        item.LocalSha256);
                if (previous != null && !localChanged && !item.WasChangedByBridge)
                {
                    item.StatusText = "No local change";
                    item.StatusGlyph = GlyphPending;
                    item.StatusColor = "#FFB347";
                    skipped++;
                    continue;
                }

                item.StatusText = "Uploading";
                item.StatusGlyph = GlyphPending;
                item.StatusColor = "#FFB347";
                item.Progress = 20;

                byte[] bytes = await File.ReadAllBytesAsync(item.FullPath, token);
                await this.service.DeleteRemoteFileAsync(this.bridgeSelection, this.SessionId, item.RelativePath, token);
                SessionSyncRemoteFile remote = await this.service.UploadFileAsync(this.bridgeSelection, this.SessionId, item.RelativePath, bytes, GuessMimeType(item.RelativePath), token);
                item.Progress = 80;
                this.UpdateMirrorAndSnapshot(item.RelativePath, bytes, info.LastWriteTimeUtc, remote);
                item.Progress = 100;
                item.StatusText = "OK";
                item.StatusGlyph = GlyphOk;
                item.StatusColor = "#7DCEA0";
                item.MetricText = FormatBytes(bytes.LongLength);
                uploaded++;
            }

            List<SessionSyncSnapshotFile> deleted = this.snapshot.Files
                .Where(file => !string.IsNullOrWhiteSpace(file.RelativePath) &&
                    !File.Exists(this.ResolveSolutionPath(file.RelativePath)) &&
                    HasLocalBaseline(file) &&
                    !this.ignoreManifest.IsIgnored(file.RelativePath) &&
                    !this.ignoreManifest.IsRemoteIgnored(file.RelativePath))
                .ToList();
            foreach (SessionSyncSnapshotFile file in deleted)
            {
                try
                {
                    await this.service.DeleteRemoteFileAsync(this.bridgeSelection, this.SessionId, file.RelativePath, token);
                    this.snapshot.Files.Remove(file);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    this.Status = "Delete failed for " + file.RelativePath + ": " + ex.Message;
                }
            }

            this.SaveSnapshot();
            this.BuildTree();
            this.Status = "Push complete. Uploaded " + uploaded.ToString(CultureInfo.InvariantCulture) +
                ", skipped " + skipped.ToString(CultureInfo.InvariantCulture) +
                (deleted.Count == 0 ? "." : ", remote deletes attempted " + deleted.Count.ToString(CultureInfo.InvariantCulture) + ".");
        }, cancellationToken);
    }

    private async Task CreateSessionAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        bool createdOrLoaded = false;
        await this.RunBusyAsync("Creating Session Sync configuration...", async token =>
        {
            this.UseLocalWorkstationSessionMode();
            await this.EnsureSolutionStateAsync(token);
            this.bridgeSelection.ThrowIfMissing();

            if (string.IsNullOrWhiteSpace(this.SessionId))
            {
                this.SessionId = this.bridgeSelection.GetDefaultSessionId(this.solutionRoot);
            }

            if (!File.Exists(this.sessionFilePath))
            {
                this.snapshot = new SessionSyncSnapshot
                {
                    Version = SessionFileVersion,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    Files = new List<SessionSyncSnapshotFile>()
                };
            }

            this.HasSessionFile = true;
            this.SaveSnapshot();
            this.BuildTree();
            this.Status = "Created .vs\\" + SessionFileName + " for " + this.bridgeSelection.DisplayServerName + " at " + this.bridgeSelection.AutoApiBase + ".";
            createdOrLoaded = true;
        }, cancellationToken);

        if (!createdOrLoaded || !this.HasSessionFile)
        {
            return;
        }

        bool pushInitialCommit = await this.PromptForInitialCommitAsync(cancellationToken);
        if (pushInitialCommit)
        {
            await this.PushAsync(null, cancellationToken);
        }
        else
        {
            this.Status = "Session file created. Initial push skipped.";
        }
    }

    private async Task ResetSessionAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.EnsureSolutionStateAsync(cancellationToken);
        string displayPath = string.IsNullOrWhiteSpace(this.sessionFilePath)
            ? ".vs\\" + SessionFileName
            : this.sessionFilePath;
        ChoiceResultCollection<bool> choices = new();
        choices.Add(new ChoiceDescription("Cancel"), false);
        choices.Add(new ChoiceDescription("Delete session file"), true);
        PromptOptions<bool> options = new(choices, defaultChoiceIndex: 0, dismissedReturns: false);
        bool confirmed = await this.extensibility.Shell().ShowPromptAsync(
            "Reset Session Sync for this solution?" + Environment.NewLine +
            "This will delete the local session file: " + displayPath + Environment.NewLine +
            "Synced files stored by JackLLM Workstation are not deleted.",
            options,
            cancellationToken);
        if (!confirmed)
        {
            this.Status = "Session reset canceled.";
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(this.sessionFilePath) && File.Exists(this.sessionFilePath))
            {
                File.Delete(this.sessionFilePath);
            }

            this.snapshot = new SessionSyncSnapshot();
            this.TreeItems = new List<SessionSyncTreeItem>();
            this.currentItems.Clear();
            this.HasSessionFile = false;
            this.SessionId = "";
            this.SelectedDetails = "";
            this.RefreshSessionControlAvailability();
            this.Status = "Deleted " + displayPath + ". Create a new local Workstation session to sync again.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.Status = "Session reset failed: " + ex.Message;
        }
    }

    private async Task OpenFileLocationAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        SessionSyncTreeItem? item = AsItem(commandParameter);
        if (item == null)
        {
            return;
        }

        string path = item.IsFolder ? item.FullPath : (File.Exists(item.FullPath) ? item.FullPath : Path.GetDirectoryName(item.FullPath) ?? this.solutionRoot);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ProcessStartInfo info = item.IsFolder || Directory.Exists(path)
            ? new ProcessStartInfo("explorer.exe", "\"" + path + "\"")
            : new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
        info.UseShellExecute = false;
        Process.Start(info);
        this.Status = "Opened " + path;
    }

    private async Task IgnoreAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        SessionSyncTreeItem? item = AsItem(commandParameter);
        if (item == null || string.IsNullOrWhiteSpace(item.RelativePath))
        {
            return;
        }

        await this.EnsureSolutionStateAsync(cancellationToken);
        this.ignoreManifest.AddIgnored(item.RelativePath, item.IsFolder);
        this.SaveIgnoreManifest();
        this.BuildTree();
        this.Status = "Ignored " + item.RelativePath + ".";
    }

    private async Task IgnoreRemoteAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        SessionSyncTreeItem? item = AsItem(commandParameter);
        if (item == null || string.IsNullOrWhiteSpace(item.RelativePath))
        {
            return;
        }

        await this.EnsureSolutionStateAsync(cancellationToken);
        this.ignoreManifest.AddRemoteIgnored(item.RelativePath, item.IsFolder);
        this.SaveIgnoreManifest();
        this.BuildTree();
        this.Status = "Remote changes ignored for " + item.RelativePath + ".";
    }

    private async Task RemoveIgnoreAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        SessionSyncTreeItem? item = AsItem(commandParameter);
        if (item == null || string.IsNullOrWhiteSpace(item.RelativePath))
        {
            return;
        }

        await this.EnsureSolutionStateAsync(cancellationToken);
        bool removed = this.ignoreManifest.Remove(item.RelativePath);
        this.SaveIgnoreManifest();
        this.BuildTree();
        this.Status = removed ? "Removed " + item.RelativePath + " from ignore lists." : item.RelativePath + " was not ignored.";
    }

    private async Task IgnoreSelectedAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.ApplyToSelectedAsync(false, false, cancellationToken);
    }

    private async Task IgnoreRemoteSelectedAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.ApplyToSelectedAsync(true, false, cancellationToken);
    }

    private async Task RemoveSelectedIgnoreAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.ApplyToSelectedAsync(false, true, cancellationToken);
    }

    private async Task ApplyToSelectedAsync(bool remoteOnly, bool remove, CancellationToken cancellationToken)
    {
        await this.EnsureSolutionStateAsync(cancellationToken);
        List<SessionSyncTreeItem> selected = FlattenItems(this.TreeItems)
            .Where(item => item.IsSelected && !item.IsIgnoreBranch && !string.IsNullOrWhiteSpace(item.RelativePath))
            .GroupBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (selected.Count == 0)
        {
            this.Status = "Select one or more files or folders first.";
            return;
        }

        foreach (SessionSyncTreeItem item in selected)
        {
            if (remove)
                this.ignoreManifest.Remove(item.RelativePath);
            else if (remoteOnly)
                this.ignoreManifest.AddRemoteIgnored(item.RelativePath, item.IsFolder);
            else
                this.ignoreManifest.AddIgnored(item.RelativePath, item.IsFolder);
        }

        this.SaveIgnoreManifest();
        this.BuildTree();
        this.Status = (remove ? "Removed ignore rules for " : remoteOnly ? "Ignored remote changes for " : "Ignored ") +
            selected.Count.ToString(CultureInfo.InvariantCulture) + " selected item(s).";
    }

    private async Task AddIgnoreGlobAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.AddIgnorePatternAsync(false, cancellationToken);
    }

    private async Task AddIgnoreRegexAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.AddIgnorePatternAsync(true, cancellationToken);
    }

    private async Task AddIgnorePatternAsync(bool regex, CancellationToken cancellationToken)
    {
        await this.EnsureSolutionStateAsync(cancellationToken);
        string value = (this.IgnorePattern ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            this.Status = regex ? "Enter a regex string first." : "Enter a file glob such as *.log first.";
            return;
        }

        if (regex)
        {
            value = value.StartsWith("regex:", StringComparison.OrdinalIgnoreCase) ? value : "regex:" + value;
            try
            {
                _ = new System.Text.RegularExpressions.Regex(value.Substring("regex:".Length), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch (ArgumentException ex)
            {
                this.Status = "Invalid ignore regex: " + ex.Message;
                return;
            }
        }
        else if (!value.Contains('*') && !value.Contains('?'))
        {
            value = "*." + value.TrimStart('.');
        }

        this.ignoreManifest.AddPattern(value);
        this.SaveIgnoreManifest();
        this.IgnorePattern = "";
        this.BuildTree();
        this.Status = "Added ignore rule " + value + ".";
    }

    private static IEnumerable<SessionSyncTreeItem> FlattenItems(IEnumerable<SessionSyncTreeItem> items)
    {
        foreach (SessionSyncTreeItem item in items)
        {
            yield return item;
            foreach (SessionSyncTreeItem child in FlattenItems(item.Children))
                yield return child;
        }
    }

    private async Task PropertiesAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await Task.Yield();
        SessionSyncTreeItem? item = AsItem(commandParameter);
        if (item == null)
        {
            return;
        }

        string localHash = item.LocalSha256;
        if (!item.IsFolder && File.Exists(item.FullPath) && string.IsNullOrWhiteSpace(localHash))
        {
            localHash = ComputeSha256(item.FullPath);
        }

        SessionSyncSnapshotFile? snapshotFile = this.snapshot.Find(item.RelativePath);
        string networkHash = FirstNonEmpty(item.RemoteSha256, snapshotFile?.RemoteSha256, "not reported");
        this.SelectedDetails =
            "Path: " + item.RelativePath + Environment.NewLine +
            "Local: " + item.FullPath + Environment.NewLine +
            "Local SHA256: " + FirstNonEmpty(localHash, "not available") + Environment.NewLine +
            "Network SHA256: " + networkHash + Environment.NewLine +
            "Status: " + item.StatusText + Environment.NewLine +
            "Local UTC: " + FormatUtc(item.LocalLastWriteUtc) + " | Network UTC: " + FormatUtc(item.RemoteLastWriteUtc);
    }

    private async Task EnsureSolutionStateAsync(CancellationToken cancellationToken)
    {
        string? directory = await this.GetCurrentSolutionDirectoryAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Open a solution before using Session Sync.");
        }

        bool solutionChanged = !string.Equals(this.solutionRoot, directory, StringComparison.OrdinalIgnoreCase);
        if (solutionChanged)
        {
            this.solutionRoot = directory;
            string hash = ShortHash(directory);
            this.tempRoot = Path.Combine(Path.GetTempPath(), "SocketJack", "SessionSync", hash);
            this.mirrorRoot = Path.Combine(this.tempRoot, "files");
            this.sessionFilePath = Path.Combine(this.solutionRoot, ".vs", SessionFileName);
            this.snapshotPath = this.sessionFilePath;
            this.ignoreManifestPath = Path.Combine(this.solutionRoot, "jackllm.ignore.manifest");
            Directory.CreateDirectory(this.mirrorRoot);
        }

        SessionSyncBridgeSelection configuredSelection = SessionSyncBridgeSelection.Load(this.solutionRoot).WithAuth(this.AuthToken, this.AuthUserName);
        bool sessionFileExists = !string.IsNullOrWhiteSpace(this.sessionFilePath) && File.Exists(this.sessionFilePath);
        this.snapshot = sessionFileExists ? this.LoadSnapshot() : new SessionSyncSnapshot();
        if (sessionFileExists)
        {
            this.SessionId = NormalizeStoredSessionId(this.snapshot.SessionId, configuredSelection.GetDefaultSessionId(this.solutionRoot));
            if (!string.Equals(this.snapshot.SessionId, this.SessionId, StringComparison.Ordinal))
            {
                this.snapshot.SessionId = this.SessionId;
                this.SaveSnapshot();
            }

            this.bridgeSelection = SessionSyncBridgeSelection.FromSnapshot(this.snapshot, configuredSelection).WithAuth(this.AuthToken, this.AuthUserName);
            this.HasSessionFile = true;
        }
        else
        {
            this.SessionId = "";
            this.bridgeSelection = configuredSelection;
            this.HasSessionFile = false;
        }

        this.ignoreManifest = SessionSyncIgnoreManifest.Load(this.ignoreManifestPath);
        this.EndpointSummary = this.BuildEndpointSummary();
        this.CreateSessionButtonText = "Create Local Workstation Session";
    }

    private void UseLocalWorkstationSessionMode()
    {
        this.IsLocalWorkstationMode = true;
        this.IsSignedIn = true;
        this.IsSignInOverlayVisible = false;
        this.IsInlineSignInVisible = false;
        this.SignInError = "";
        this.AuthStatus = "Session Sync uses the local JackLLM Workstation at " + SessionSyncBridgeSelection.LocalWorkstationEndpoint + ". No hosted account is required.";
    }

    private string BuildEndpointSummary()
    {
        if (!this.bridgeSelection.HasRemoteApi)
        {
            return "Local JackLLM Workstation session endpoint: " + SessionSyncBridgeSelection.LocalWorkstationEndpoint + ".";
        }

        string sessionPart = this.HasSessionFile ? ".vs\\" + SessionFileName : "create .vs\\" + SessionFileName + " first";
        string modelPart = string.IsNullOrWhiteSpace(this.bridgeSelection.ModelId) ? "local model" : this.bridgeSelection.ModelId;
        return this.bridgeSelection.DisplayServerName + " / " + modelPart + " / " + this.bridgeSelection.AutoApiBase + " / " + sessionPart;
    }

    private void ThrowIfSessionFileMissing()
    {
        if (!this.HasSessionFile || string.IsNullOrWhiteSpace(this.sessionFilePath) || !File.Exists(this.sessionFilePath))
        {
            throw new InvalidOperationException("Create .vs\\" + SessionFileName + " before using Session Sync controls.");
        }

        if (string.IsNullOrWhiteSpace(this.SessionId))
        {
            throw new InvalidOperationException(".vs\\" + SessionFileName + " does not contain a SessionId.");
        }
    }

    private async Task<bool> PromptForInitialCommitAsync(CancellationToken cancellationToken)
    {
        ChoiceResultCollection<bool> choices = new();
        choices.Add(new ChoiceDescription("Push initial commit"), true);
        choices.Add(new ChoiceDescription("Skip"), false);

        PromptOptions<bool> options = new(choices, defaultChoiceIndex: 0, dismissedReturns: false);
        return await this.extensibility.Shell().ShowPromptAsync(
            "Created .vs\\" + SessionFileName + " for " + this.bridgeSelection.DisplayServerName + " at " + this.bridgeSelection.AutoApiBase + "." + Environment.NewLine +
            "Push an initial commit of the current solution files now?",
            options,
            cancellationToken);
    }

    private async Task<string?> GetCurrentSolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        IQueryResults<ISolutionSnapshot> solutions = await this.extensibility.Workspaces().QuerySolutionAsync(solution => solution.With(solution => solution.Path), cancellationToken);
        foreach (ISolutionSnapshot solution in solutions)
        {
            if (string.IsNullOrWhiteSpace(solution.Path))
            {
                continue;
            }

            string? directory = Path.GetDirectoryName(solution.Path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private async Task<string> LoadRemoteMetadataBestEffortAsync(CancellationToken cancellationToken)
    {
        if (!this.bridgeSelection.HasRemoteApi || string.IsNullOrWhiteSpace(this.SessionId))
        {
            return "";
        }

        try
        {
            IReadOnlyList<SessionSyncRemoteFile> remoteFiles = await this.service.ListRemoteFilesAsync(this.bridgeSelection, this.SessionId, cancellationToken);
            foreach (SessionSyncRemoteFile remote in remoteFiles)
            {
                this.UpsertRemoteSnapshot(remote);
            }

            if (remoteFiles.Count > 0)
            {
                return "Loaded " + remoteFiles.Count.ToString(CultureInfo.InvariantCulture) + " local Workstation session file records.";
            }

            return "No local Workstation session files reported yet.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "Local Workstation session metadata unavailable: " + ex.Message;
        }
    }

    private void BuildTree()
    {
        this.currentItems.Clear();
        var roots = new List<SessionSyncTreeItem>();
        var folders = new Dictionary<string, SessionSyncTreeItem>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenFiles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string file in EnumerateSolutionFiles(this.solutionRoot))
        {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(this.solutionRoot, file));
            seenFiles.Add(relativePath);
            SessionSyncTreeItem item = this.CreateFileItem(relativePath, file);
            this.AddToTree(roots, folders, item);
            this.currentItems[relativePath] = item;
        }

        foreach (SessionSyncSnapshotFile file in this.snapshot.Files)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath) || seenFiles.Contains(file.RelativePath))
            {
                continue;
            }

            string relativePath = NormalizeRelativePath(file.RelativePath);
            SessionSyncTreeItem deleted = this.CreateDeletedItem(relativePath, this.ResolveSolutionPath(relativePath), file);
            this.AddToTree(roots, folders, deleted);
            this.currentItems[relativePath] = deleted;
        }

        foreach (SessionSyncTreeItem folder in folders.Values.OrderByDescending(item => item.RelativePath.Length))
        {
            AggregateFolder(folder);
        }

        SessionSyncTreeItem ignoreBranch = this.BuildIgnoreBranch();
        if (ignoreBranch.Children.Count > 0)
        {
            roots.Add(ignoreBranch);
        }

        this.TreeItems = roots
            .OrderBy(item => item.IsIgnoreBranch ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        this.RefreshPullAvailability();
    }

    private SessionSyncTreeItem CreateFileItem(string relativePath, string fullPath)
    {
        FileInfo info = new(fullPath);
        SessionSyncSnapshotFile? snapshotFile = this.snapshot.Find(relativePath);
        bool ignored = this.ignoreManifest.IsIgnored(relativePath);
        bool remoteIgnored = this.ignoreManifest.IsRemoteIgnored(relativePath);
        string localHash = info.Length <= SessionSyncService.QuickHashBytes ? ComputeSha256(fullPath) : "";
        DateTimeOffset localUtc = new(info.LastWriteTimeUtc, TimeSpan.Zero);
        DateTimeOffset remoteUtc = snapshotFile?.RemoteLastWriteUtc ?? DateTimeOffset.MinValue;
        string remoteHash = snapshotFile?.RemoteSha256 ?? "";

        SessionSyncItemState state;
        string statusText;
        string glyph;
        string color;
        double progress;

        bool localChanged = snapshotFile == null ||
            SessionSyncDiffState.HasUnpushedLocalChange(
                localExists: true,
                snapshotFile.LocalLastWriteUtc,
                snapshotFile.LocalSizeBytes,
                snapshotFile.LocalSha256,
                localUtc,
                info.Length,
                localHash);
        bool remoteDiffersFromLocal = snapshotFile != null &&
            HasRemoteBaseline(snapshotFile) &&
            !KnownHashesEqual(localHash, remoteHash) &&
            (remoteUtc > DateTimeOffset.MinValue ||
                snapshotFile.RemoteSizeBytes != info.Length ||
                !string.IsNullOrWhiteSpace(remoteHash));

        if (ignored || remoteIgnored)
        {
            state = SessionSyncItemState.Ignored;
            statusText = ignored ? "Ignored" : "Remote ignored";
            glyph = GlyphPending;
            color = "#9A9A9A";
            progress = 0;
        }
        else if (localChanged)
        {
            state = SessionSyncItemState.PendingUpload;
            statusText = remoteDiffersFromLocal ? "Push before pull" : "Pending upload";
            glyph = GlyphPending;
            color = "#FFB347";
            progress = 0;
        }
        else if (remoteDiffersFromLocal)
        {
            state = SessionSyncItemState.PendingDownload;
            statusText = "Pending pull";
            glyph = GlyphPending;
            color = "#FFB347";
            progress = 0;
        }
        else
        {
            state = SessionSyncItemState.Ok;
            statusText = "OK";
            glyph = GlyphOk;
            color = "#7DCEA0";
            progress = 100;
        }

        return new SessionSyncTreeItem
        {
            Name = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            FullPath = fullPath,
            IsFolder = false,
            IsIgnored = ignored || remoteIgnored,
            StatusText = statusText,
            StatusGlyph = glyph,
            StatusColor = color,
            Progress = progress,
            MetricText = FormatBytes(info.Length),
            LocalSha256 = localHash,
            RemoteSha256 = remoteHash,
            LocalLastWriteUtc = localUtc,
            RemoteLastWriteUtc = remoteUtc,
            SyncState = state
        };
    }

    private SessionSyncTreeItem CreateDeletedItem(string relativePath, string fullPath, SessionSyncSnapshotFile snapshotFile)
    {
        bool ignored = this.ignoreManifest.IsIgnored(relativePath);
        bool remoteIgnored = this.ignoreManifest.IsRemoteIgnored(relativePath);
        bool pendingDownload = HasRemoteBaseline(snapshotFile) && !HasLocalBaseline(snapshotFile);
        bool isIgnored = ignored || remoteIgnored;
        string statusText = isIgnored ? (ignored ? "Ignored" : "Remote ignored") : (pendingDownload ? "Pending pull" : "Deleted");
        string glyph = isIgnored ? GlyphPending : (pendingDownload ? GlyphPending : GlyphDeleted);
        string color = isIgnored ? "#9A9A9A" : (pendingDownload ? "#FFB347" : "#E57373");
        SessionSyncItemState state = isIgnored ? SessionSyncItemState.Ignored : (pendingDownload ? SessionSyncItemState.PendingDownload : SessionSyncItemState.Deleted);

        return new SessionSyncTreeItem
        {
            Name = Path.GetFileName(relativePath),
            RelativePath = relativePath,
            FullPath = fullPath,
            IsFolder = false,
            IsIgnored = isIgnored,
            StatusText = statusText,
            StatusGlyph = glyph,
            StatusColor = color,
            Progress = 0,
            MetricText = snapshotFile.RemoteSizeBytes > 0 ? FormatBytes(snapshotFile.RemoteSizeBytes) : "",
            LocalSha256 = snapshotFile.LocalSha256,
            RemoteSha256 = snapshotFile.RemoteSha256,
            LocalLastWriteUtc = snapshotFile.LocalLastWriteUtc,
            RemoteLastWriteUtc = snapshotFile.RemoteLastWriteUtc,
            SyncState = state
        };
    }

    private void AddToTree(List<SessionSyncTreeItem> roots, Dictionary<string, SessionSyncTreeItem> folders, SessionSyncTreeItem file)
    {
        string[] parts = file.RelativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 1)
        {
            roots.Add(file);
            return;
        }

        string currentPath = "";
        SessionSyncTreeItem? parent = null;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            currentPath = string.IsNullOrEmpty(currentPath) ? parts[i] : currentPath + "/" + parts[i];
            if (!folders.TryGetValue(currentPath, out SessionSyncTreeItem? folder))
            {
                folder = new SessionSyncTreeItem
                {
                    Name = parts[i],
                    RelativePath = currentPath,
                    FullPath = this.ResolveSolutionPath(currentPath),
                    IsFolder = true,
                    IsExpanded = false,
                    StatusText = "OK",
                    StatusGlyph = GlyphOk,
                    StatusColor = "#7DCEA0",
                    Progress = 100,
                    MetricText = "",
                    IsIgnored = this.ignoreManifest.IsIgnored(currentPath) || this.ignoreManifest.IsRemoteIgnored(currentPath),
                    SyncState = SessionSyncItemState.Ok
                };
                folders[currentPath] = folder;
                if (parent == null)
                {
                    roots.Add(folder);
                }
                else
                {
                    parent.Children.Add(folder);
                }
            }

            parent = folder;
        }

        parent?.Children.Add(file);
    }

    private SessionSyncTreeItem BuildIgnoreBranch()
    {
        var branch = new SessionSyncTreeItem
        {
            Name = "Ignore",
            RelativePath = "",
            FullPath = this.ignoreManifestPath,
            IsFolder = true,
            IsExpanded = true,
            IsIgnoreBranch = true,
            StatusText = "Ignored list",
            StatusGlyph = GlyphPending,
            StatusColor = "#9A9A9A",
            Progress = 0,
            MetricText = ""
        };

        foreach (string path in this.ignoreManifest.AllEntries().OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            bool remoteOnly = this.ignoreManifest.IsRemoteIgnored(path) && !this.ignoreManifest.IsIgnored(path);
            branch.Children.Add(new SessionSyncTreeItem
            {
                Name = path + (remoteOnly ? " (remote)" : ""),
                RelativePath = path.TrimEnd('/'),
                FullPath = this.ResolveSolutionPath(path.TrimEnd('/')),
                IsFolder = path.EndsWith("/", StringComparison.Ordinal),
                IsIgnored = true,
                StatusText = remoteOnly ? "Remote ignored" : "Ignored",
                StatusGlyph = GlyphPending,
                StatusColor = "#9A9A9A",
                Progress = 0,
                MetricText = ""
            });
        }

        return branch;
    }

    private SessionSyncSnapshot LoadSnapshot()
    {
        try
        {
            if (!File.Exists(this.snapshotPath))
            {
                return new SessionSyncSnapshot();
            }

            string json = File.ReadAllText(this.snapshotPath);
            SessionSyncSnapshot snapshot = JsonSerializer.Deserialize<SessionSyncSnapshot>(json, JsonOptions) ?? new SessionSyncSnapshot();
            snapshot.Files ??= new List<SessionSyncSnapshotFile>();
            return snapshot;
        }
        catch
        {
            return new SessionSyncSnapshot();
        }
    }

    private void SaveSnapshot()
    {
        this.snapshot.Version = SessionFileVersion;
        this.snapshot.SessionId = this.SessionId;
        this.snapshot.SolutionRoot = this.solutionRoot;
        this.snapshot.ServerEndpoint = this.bridgeSelection.ServerEndpoint;
        this.snapshot.AutoApiBase = this.bridgeSelection.AutoApiBase;
        this.snapshot.ServerId = this.bridgeSelection.ServerId;
        this.snapshot.ServerName = this.bridgeSelection.ServerName;
        this.snapshot.ModelId = this.bridgeSelection.ModelId;
        if (this.snapshot.CreatedUtc == DateTimeOffset.MinValue)
        {
            this.snapshot.CreatedUtc = DateTimeOffset.UtcNow;
        }

        this.snapshot.UpdatedUtc = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(this.snapshotPath) ?? this.tempRoot);
        File.WriteAllText(this.snapshotPath, JsonSerializer.Serialize(this.snapshot, JsonOptions), new UTF8Encoding(false));
        this.HasSessionFile = true;
    }

    private void SaveIgnoreManifest()
    {
        this.ignoreManifest.Save(this.ignoreManifestPath);
    }

    private void UpsertRemoteSnapshot(SessionSyncRemoteFile remoteFile)
    {
        SessionSyncSnapshotFile file = this.snapshot.GetOrAdd(remoteFile.RelativePath);
        DateTimeOffset previousRemoteLastWriteUtc = file.RemoteLastWriteUtc;
        long previousRemoteSizeBytes = file.RemoteSizeBytes;
        bool remoteChanged = RemoteMetadataChanged(previousRemoteLastWriteUtc, remoteFile.LastWriteUtc, previousRemoteSizeBytes, remoteFile.SizeBytes);
        file.RelativePath = remoteFile.RelativePath;
        file.RemoteLastWriteUtc = remoteFile.LastWriteUtc;
        file.RemoteSizeBytes = remoteFile.SizeBytes;
        if (!string.IsNullOrWhiteSpace(remoteFile.Sha256))
        {
            file.RemoteSha256 = remoteFile.Sha256.Trim();
        }
        else if (remoteChanged)
        {
            file.RemoteSha256 = "";
        }
    }

    private void UpdateMirrorAndSnapshot(string relativePath, byte[] bytes, DateTime localLastWriteUtc, SessionSyncRemoteFile? remoteFile)
    {
        relativePath = NormalizeRelativePath(relativePath);
        string mirrorPath = Path.Combine(this.mirrorRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(mirrorPath) ?? this.mirrorRoot);
        File.WriteAllBytes(mirrorPath, bytes);
        File.SetLastWriteTimeUtc(mirrorPath, localLastWriteUtc);

        string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        SessionSyncSnapshotFile snapshotFile = this.snapshot.GetOrAdd(relativePath);
        snapshotFile.RelativePath = relativePath;
        snapshotFile.LocalSha256 = sha;
        snapshotFile.LocalLastWriteUtc = new DateTimeOffset(localLastWriteUtc, TimeSpan.Zero);
        snapshotFile.LocalSizeBytes = bytes.LongLength;
        if (remoteFile != null)
        {
            snapshotFile.RemoteLastWriteUtc = remoteFile.LastWriteUtc > DateTimeOffset.MinValue ? remoteFile.LastWriteUtc : snapshotFile.LocalLastWriteUtc;
            snapshotFile.RemoteSizeBytes = remoteFile.SizeBytes > 0 ? remoteFile.SizeBytes : bytes.LongLength;
            snapshotFile.RemoteSha256 = FirstNonEmpty(remoteFile.Sha256, sha);
        }
        else
        {
            snapshotFile.RemoteLastWriteUtc = snapshotFile.LocalLastWriteUtc;
            snapshotFile.RemoteSizeBytes = bytes.LongLength;
            snapshotFile.RemoteSha256 = sha;
        }
    }

    private SessionSyncTreeItem? TryFindItem(string relativePath)
    {
        this.currentItems.TryGetValue(NormalizeRelativePath(relativePath), out SessionSyncTreeItem? item);
        return item;
    }

    private bool HasUnpushedLocalChanges(out int pendingUploads, out int pendingDeletes)
    {
        pendingUploads = 0;
        pendingDeletes = 0;
        foreach (SessionSyncTreeItem item in this.currentItems.Values)
        {
            if (item.IsFolder || item.IsIgnored)
            {
                continue;
            }

            if (item.SyncState == SessionSyncItemState.PendingUpload)
            {
                pendingUploads++;
            }
            else if (item.SyncState == SessionSyncItemState.Deleted)
            {
                pendingDeletes++;
            }
        }

        return pendingUploads > 0 || pendingDeletes > 0;
    }

    private void RefreshPullAvailability()
    {
        bool hasLocalChanges = this.HasSessionFile && this.HasUnpushedLocalChanges(out _, out _);
        this.CanPull = this.SessionControlsEnabled && !hasLocalChanges;
    }

    private void RefreshSessionControlAvailability()
    {
        this.SessionControlsEnabled = this.HasSessionFile && !this.IsBusy;
        this.IsSessionBootstrapVisible = !this.HasSessionFile;
        this.CreateSessionButtonText = "Create Local Workstation Session";
        this.RefreshPullAvailability();
    }

    private static string BuildPullDisabledStatus(int pendingUploads, int pendingDeletes)
    {
        List<string> parts = new();
        if (pendingUploads > 0)
        {
            parts.Add(pendingUploads.ToString(CultureInfo.InvariantCulture) + " upload" + (pendingUploads == 1 ? "" : "s"));
        }

        if (pendingDeletes > 0)
        {
            parts.Add(pendingDeletes.ToString(CultureInfo.InvariantCulture) + " delete" + (pendingDeletes == 1 ? "" : "s"));
        }

        string summary = parts.Count == 0 ? "local changes" : string.Join(", ", parts);
        return "Pull disabled because " + summary + " are waiting to push. Push first to update the local Workstation session files, then pull.";
    }

    private string ResolveSolutionPath(string relativePath)
    {
        string normalized = NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(this.solutionRoot, normalized));
        string root = Path.GetFullPath(this.solutionRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar), this.solutionRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Path is outside the solution root: " + relativePath);
        }

        return full;
    }

    private async Task RunBusyAsync(string busyStatus, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (this.IsBusy)
        {
            return;
        }

        this.IsBusy = true;
        this.Status = busyStatus;
        try
        {
            await action(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!this.HandleAuthException(ex))
            {
                this.Status = "Session Sync failed: " + ex.Message;
            }
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private static SessionSyncTreeItem? AsItem(object? commandParameter)
    {
        return commandParameter as SessionSyncTreeItem;
    }

    private static IEnumerable<string> EnumerateSolutionFiles(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        EnumerationOptions options = new()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };

        return Directory.EnumerateFiles(root, "*", options)
            .Where(file => !IsExcludedPath(root, file))
            .Take(5000)
            .ToArray();
    }

    private static bool IsExcludedPath(string root, string file)
    {
        string relative = NormalizeRelativePath(Path.GetRelativePath(root, file));
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts)
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("TestResults", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".vsextension", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string name = Path.GetFileName(file);
        return name.EndsWith(".user", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".suo", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("jackllm.ignore.manifest", StringComparison.OrdinalIgnoreCase);
    }

    private static void AggregateFolder(SessionSyncTreeItem folder)
    {
        foreach (SessionSyncTreeItem childFolder in folder.Children.Where(child => child.IsFolder))
        {
            AggregateFolder(childFolder);
        }

        if (folder.IsIgnored)
        {
            folder.StatusText = "Ignored";
            folder.StatusGlyph = GlyphPending;
            folder.StatusColor = "#9A9A9A";
            folder.Progress = 0;
            folder.SyncState = SessionSyncItemState.Ignored;
            return;
        }

        int count = folder.Children.Count;
        if (count == 0)
        {
            return;
        }

        if (folder.Children.Any(child => child.SyncState == SessionSyncItemState.Deleted))
        {
            folder.StatusText = "Deleted";
            folder.StatusGlyph = GlyphDeleted;
            folder.StatusColor = "#E57373";
            folder.Progress = 0;
            folder.SyncState = SessionSyncItemState.Deleted;
        }
        else if (folder.Children.Any(child => child.SyncState == SessionSyncItemState.PendingUpload || child.SyncState == SessionSyncItemState.PendingDownload))
        {
            folder.StatusText = "Pending";
            folder.StatusGlyph = GlyphPending;
            folder.StatusColor = "#FFB347";
            folder.Progress = folder.Children.Average(child => child.Progress);
            folder.SyncState = SessionSyncItemState.PendingUpload;
        }
        else
        {
            folder.StatusText = "OK";
            folder.StatusGlyph = GlyphOk;
            folder.StatusColor = "#7DCEA0";
            folder.Progress = 100;
            folder.SyncState = SessionSyncItemState.Ok;
        }

        folder.MetricText = count.ToString(CultureInfo.InvariantCulture) + " items";
    }

    private static bool KnownHashesEqual(string left, string right)
    {
        return SessionSyncDiffState.KnownHashesEqual(left, right);
    }

    private static bool HasLocalBaseline(SessionSyncSnapshotFile file)
    {
        return SessionSyncDiffState.HasLocalBaseline(file.LocalLastWriteUtc, file.LocalSizeBytes, file.LocalSha256);
    }

    private static bool HasRemoteBaseline(SessionSyncSnapshotFile file)
    {
        return SessionSyncDiffState.HasRemoteBaseline(file.RemoteLastWriteUtc, file.RemoteSizeBytes, file.RemoteSha256);
    }

    private static bool RemoteMetadataChanged(DateTimeOffset previousUtc, DateTimeOffset nextUtc, long previousSizeBytes, long nextSizeBytes)
    {
        return SessionSyncDiffState.RemoteMetadataChanged(previousUtc, nextUtc, previousSizeBytes, nextSizeBytes);
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeRelativePath(string value)
    {
        value = (value ?? "").Trim().Replace('\\', '/').Trim('/');
        List<string> parts = new();
        foreach (string part in value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "." || part == "..")
            {
                continue;
            }

            parts.Add(part);
        }

        return string.Join("/", parts);
    }

    private static string SanitizeAutoSessionId(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == ':' || ch == '-' ? ch : '_');
        }

        string result = builder.ToString();
        return result.Length <= 96 ? result : result.Substring(0, 96);
    }

    private static string NormalizeStoredSessionId(string storedSessionId, string defaultSessionId)
    {
        string stored = SanitizeAutoSessionId(storedSessionId);
        string fallback = SanitizeAutoSessionId(defaultSessionId);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return fallback;
        }

        if (stored.StartsWith("vsync_", StringComparison.OrdinalIgnoreCase))
        {
            int lastUnderscore = stored.LastIndexOf('_');
            if (lastUnderscore > "vsync_".Length &&
                stored.Length - lastUnderscore - 1 == 12 &&
                stored.Substring(lastUnderscore + 1).All(IsLowerHexDigit))
            {
                return fallback;
            }
        }

        return stored;
    }

    private static bool IsLowerHexDigit(char ch)
    {
        return (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
    }

    private static string ShortHash(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? ""))).Substring(0, 16).ToLowerInvariant();
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 10 || unit == 0
            ? Math.Round(value, 0).ToString(CultureInfo.InvariantCulture) + " " + units[unit]
            : value.ToString("0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static string FormatUtc(DateTimeOffset value)
    {
        return value <= DateTimeOffset.MinValue.AddDays(1) ? "not reported" : value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string GuessMimeType(string relativePath)
    {
        string extension = Path.GetExtension(relativePath).TrimStart('.').ToLowerInvariant();
        return extension switch
        {
            "cs" or "vb" or "js" or "ts" or "css" or "html" or "htm" or "xml" or "md" or "txt" or "json" or "xaml" or "csproj" or "sln" => "text/plain; charset=utf-8",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "webp" => "image/webp",
            "gif" => "image/gif",
            "pdf" => "application/pdf",
            "zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }
}

internal enum SessionSyncItemState
{
    Ok,
    PendingUpload,
    PendingDownload,
    Deleted,
    Ignored
}

[DataContract]
internal sealed class SessionSyncTreeItem : NotifyPropertyChangedObject
{
    private string name = "";
    private string relativePath = "";
    private string fullPath = "";
    private bool isFolder;
    private bool isExpanded;
    private bool isIgnored;
    private bool isIgnoreBranch;
    private bool isSelected;
    private string statusText = "";
    private string statusGlyph = "";
    private string statusColor = "#E8E8E8";
    private double progress;
    private string metricText = "";
    private string localSha256 = "";
    private string remoteSha256 = "";
    private DateTimeOffset localLastWriteUtc = DateTimeOffset.MinValue;
    private DateTimeOffset remoteLastWriteUtc = DateTimeOffset.MinValue;
    private SessionSyncItemState syncState = SessionSyncItemState.Ok;
    private bool wasChangedByBridge;

    [DataMember]
    public string Name { get => this.name; set => this.SetProperty(ref this.name, value ?? ""); }

    [DataMember]
    public string RelativePath { get => this.relativePath; set => this.SetProperty(ref this.relativePath, value ?? ""); }

    [DataMember]
    public string FullPath { get => this.fullPath; set => this.SetProperty(ref this.fullPath, value ?? ""); }

    [DataMember]
    public bool IsFolder { get => this.isFolder; set => this.SetProperty(ref this.isFolder, value); }

    [DataMember]
    public bool IsExpanded { get => this.isExpanded; set => this.SetProperty(ref this.isExpanded, value); }

    [DataMember]
    public bool IsIgnored { get => this.isIgnored; set => this.SetProperty(ref this.isIgnored, value); }

    [DataMember]
    public bool IsIgnoreBranch { get => this.isIgnoreBranch; set => this.SetProperty(ref this.isIgnoreBranch, value); }

    [DataMember]
    public bool IsSelected { get => this.isSelected; set => this.SetProperty(ref this.isSelected, value); }

    [DataMember]
    public string StatusText { get => this.statusText; set => this.SetProperty(ref this.statusText, value ?? ""); }

    [DataMember]
    public string StatusGlyph { get => this.statusGlyph; set => this.SetProperty(ref this.statusGlyph, value ?? ""); }

    [DataMember]
    public string StatusColor { get => this.statusColor; set => this.SetProperty(ref this.statusColor, value ?? "#E8E8E8"); }

    [DataMember]
    public double Progress { get => this.progress; set => this.SetProperty(ref this.progress, Math.Max(0, Math.Min(100, value))); }

    [DataMember]
    public string MetricText { get => this.metricText; set => this.SetProperty(ref this.metricText, value ?? ""); }

    [DataMember]
    public string LocalSha256 { get => this.localSha256; set => this.SetProperty(ref this.localSha256, value ?? ""); }

    [DataMember]
    public string RemoteSha256 { get => this.remoteSha256; set => this.SetProperty(ref this.remoteSha256, value ?? ""); }

    [DataMember]
    public DateTimeOffset LocalLastWriteUtc { get => this.localLastWriteUtc; set => this.SetProperty(ref this.localLastWriteUtc, value); }

    [DataMember]
    public DateTimeOffset RemoteLastWriteUtc { get => this.remoteLastWriteUtc; set => this.SetProperty(ref this.remoteLastWriteUtc, value); }

    [DataMember]
    public SessionSyncItemState SyncState { get => this.syncState; set => this.SetProperty(ref this.syncState, value); }

    [DataMember]
    public bool WasChangedByBridge { get => this.wasChangedByBridge; set => this.SetProperty(ref this.wasChangedByBridge, value); }

    [DataMember]
    public List<SessionSyncTreeItem> Children { get; set; } = new();
}

internal sealed class SessionSyncService
{
    public const long MaxAutoUploadBytes = 52_428_800;
    public const long QuickHashBytes = 10 * 1024 * 1024;
    private readonly HttpClient httpClient;

    public SessionSyncService(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SessionSyncRemoteFile>> ListRemoteFilesAsync(SessionSyncBridgeSelection selection, string sessionId, CancellationToken cancellationToken)
    {
        selection.ThrowIfMissing();
        Uri uri = selection.BuildAutoUri("/api/session-sync/files?sessionId=" + Uri.EscapeDataString(sessionId));
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        ApplyAuth(request, selection.AuthToken, selection.AuthUserName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Local Workstation list returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + ExtractError(json));
        }

        return ParseRemoteFiles(json);
    }

    public async Task<SessionSyncRemoteFile> UploadFileAsync(SessionSyncBridgeSelection selection, string sessionId, string relativePath, byte[] bytes, string mimeType, CancellationToken cancellationToken)
    {
        selection.ThrowIfMissing();
        if (bytes.LongLength > MaxAutoUploadBytes)
        {
            throw new InvalidOperationException("Auto session uploads are limited to 50 MB.");
        }

        string effectiveMimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType.Trim();
        JsonObject payload = new()
        {
            ["sessionId"] = sessionId,
            ["fileName"] = relativePath,
            ["mimeType"] = effectiveMimeType,
            ["sizeBytes"] = bytes.LongLength,
            ["dataUrl"] = "data:" + effectiveMimeType + ";base64," + Convert.ToBase64String(bytes)
        };

        using HttpRequestMessage request = new(HttpMethod.Post, selection.BuildAutoUri("/api/session-sync/files"));
        ApplyAuth(request, selection.AuthToken, selection.AuthUserName);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Local Workstation upload returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + ExtractError(json));
        }

        SessionSyncRemoteFile? file = ParseSingleRemoteFile(json);
        return file ?? new SessionSyncRemoteFile(relativePath, bytes.LongLength, DateTimeOffset.UtcNow, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public async Task<SessionSyncRepositoryImportResult> ImportGitHubRepositoryAsync(SessionSyncBridgeSelection selection, string sessionId, string repository, string githubToken, CancellationToken cancellationToken)
    {
        selection.ThrowIfMissing();
        JsonObject payload = new()
        {
            ["sessionId"] = sessionId,
            ["repository"] = (repository ?? "").Trim(),
            ["serverId"] = selection.ServerId,
            ["mode"] = "tools"
        };
        if (!string.IsNullOrWhiteSpace(githubToken))
        {
            payload["githubToken"] = githubToken.Trim();
        }

        using HttpRequestMessage request = new(HttpMethod.Post, selection.BuildAutoUri("/api/session-sync/github-import"));
        ApplyAuth(request, selection.AuthToken, selection.AuthUserName);
        request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(ExtractError(json));
        }

        return ParseRepositoryImportResult(json);
    }

    public Task<byte[]> DownloadRemoteFileAsync(SessionSyncBridgeSelection selection, string sessionId, string relativePath, CancellationToken cancellationToken)
    {
        return this.DownloadRemoteFileAsync(selection, sessionId, new SessionSyncRemoteFile(relativePath, 0, DateTimeOffset.MinValue, ""), cancellationToken);
    }

    public async Task<byte[]> DownloadRemoteFileAsync(SessionSyncBridgeSelection selection, string sessionId, SessionSyncRemoteFile remoteFile, CancellationToken cancellationToken)
    {
        selection.ThrowIfMissing();
        Uri uri;
        if (remoteFile.IsUpstream)
        {
            string path = FirstNonEmpty(remoteFile.DownloadPath, remoteFile.RelativePath);
            if (!path.StartsWith("\\", StringComparison.Ordinal) && !path.StartsWith("/", StringComparison.Ordinal))
            {
                path = "\\" + path;
            }

            List<string> query = new()
            {
                "kind=session",
                "sessionId=" + Uri.EscapeDataString(sessionId),
                "path=" + Uri.EscapeDataString(path)
            };

            uri = selection.BuildAutoUri("/api/chat-file-download?" + string.Join("&", query));
        }
        else
        {
            string relativePath = FirstNonEmpty(remoteFile.RelativePath, remoteFile.DownloadPath);
            uri = selection.BuildAutoUri("/api/session-sync/file?sessionId=" + Uri.EscapeDataString(sessionId) + "&name=" + Uri.EscapeDataString(relativePath));
        }

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        ApplyAuth(request, selection.AuthToken, selection.AuthUserName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException("Local Workstation download returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + ExtractError(error));
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeleteRemoteFileAsync(SessionSyncBridgeSelection selection, string sessionId, string relativePath, CancellationToken cancellationToken)
    {
        selection.ThrowIfMissing();
        Uri uri = selection.BuildAutoUri("/api/session-sync/file?sessionId=" + Uri.EscapeDataString(sessionId) + "&name=" + Uri.EscapeDataString(relativePath));
        using HttpRequestMessage request = new(HttpMethod.Delete, uri);
        ApplyAuth(request, selection.AuthToken, selection.AuthUserName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException("Local Workstation delete returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ": " + ExtractError(error));
        }
    }

    private static void ApplyAuth(HttpRequestMessage request, string authToken, string authUserName)
    {
        if (!string.IsNullOrWhiteSpace(authToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Headers.TryAddWithoutValidation("X-SocketJack-Auth", authToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(authUserName))
        {
            request.Headers.TryAddWithoutValidation("X-SocketJack-User", authUserName.Trim());
            request.Headers.TryAddWithoutValidation("X-SocketJack-Username", authUserName.Trim());
        }
    }

    private static IReadOnlyList<SessionSyncRemoteFile> ParseRemoteFiles(string json)
    {
        JsonNode? root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        JsonArray? files = null;
        if (root is JsonArray array)
        {
            files = array;
        }
        else if (root is JsonObject obj)
        {
            files = obj["files"] as JsonArray ?? obj["items"] as JsonArray;
        }

        if (files == null)
        {
            return Array.Empty<SessionSyncRemoteFile>();
        }

        List<SessionSyncRemoteFile> result = new();
        foreach (JsonNode? node in files)
        {
            if (node is JsonObject file && TryParseRemoteFile(file, out SessionSyncRemoteFile? parsed) && parsed != null)
            {
                result.Add(parsed);
            }
        }

        return result;
    }

    private static SessionSyncRemoteFile? ParseSingleRemoteFile(string json)
    {
        JsonNode? root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        if (root is JsonObject obj)
        {
            if (obj["file"] is JsonObject file && TryParseRemoteFile(file, out SessionSyncRemoteFile? parsed))
            {
                return parsed;
            }

            if (TryParseRemoteFile(obj, out parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static SessionSyncRepositoryImportResult ParseRepositoryImportResult(string json)
    {
        JsonObject? obj = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        if (obj == null)
        {
            throw new InvalidOperationException("GitHub import returned an invalid response.");
        }

        bool ok = obj["ok"] is JsonValue okValue && okValue.TryGetValue(out bool okResult) && okResult;
        if (!ok)
        {
            throw new InvalidOperationException(ExtractError(json));
        }

        return new SessionSyncRepositoryImportResult(
            FirstString(obj, "sessionId"),
            FirstString(obj, "repository"),
            FirstString(obj, "branch"),
            FirstString(obj, "targetFolder"),
            FirstLong(obj, "zipSizeBytes"),
            FirstLong(obj, "extractedSizeBytes"),
            (int)FirstLong(obj, "fileCount"),
            ParseRemoteFiles(json));
    }

    private static bool TryParseRemoteFile(JsonObject file, out SessionSyncRemoteFile? remoteFile)
    {
        remoteFile = null;
        string relativePath = FirstString(file, "relativePath", "path", "name", "fileName");
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        long size = FirstLong(file, "sizeBytes", "size", "bytes");
        DateTimeOffset lastWrite = FirstDate(file, "lastWriteUtc", "modifiedUtc", "uploadedUtc", "updatedUtc", "createdUtc");
        string sha = FirstString(file, "sha256", "sandboxSha256", "hash", "checksum");
        string source = FirstString(file, "source");
        string serverId = FirstString(file, "serverId", "server_id", "server");
        string routeMode = FirstString(file, "routeMode", "mode");
        string downloadPath = FirstString(file, "downloadPath", "sessionFilePath", "filePath", "path");
        remoteFile = new SessionSyncRemoteFile(relativePath.Replace('\\', '/').Trim('/'), size, lastWrite, sha, source, serverId, routeMode, downloadPath);
        return true;
    }

    private static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
            {
                continue;
            }

            string value = node.ToString();
            if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "null", StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static long FirstLong(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node is JsonValue value && value.TryGetValue(out long longValue))
            {
                return longValue;
            }

            if (node != null && long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            {
                return parsed;
            }
        }

        return 0;
    }

    private static DateTimeOffset FirstDate(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            string value = FirstString(obj, name);
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            {
                return parsed;
            }
        }

        return DateTimeOffset.MinValue;
    }

    private static string ExtractError(string json)
    {
        try
        {
            JsonObject? obj = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            string message = FirstString(obj ?? new JsonObject(), "error", "message", "detail", "code");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return string.IsNullOrWhiteSpace(json) ? "No response body." : json.Trim();
    }
}

internal sealed record SessionSyncRepositoryImportResult(
    string SessionId,
    string Repository,
    string Branch,
    string TargetFolder,
    long ZipSizeBytes,
    long ExtractedSizeBytes,
    int FileCount,
    IReadOnlyList<SessionSyncRemoteFile> Files);

internal sealed record SessionSyncRemoteFile(
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteUtc,
    string Sha256,
    string Source = "",
    string ServerId = "",
    string RouteMode = "",
    string DownloadPath = "")
{
    public bool IsUpstream =>
        this.Source.Equals("auto-upstream-session", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(this.ServerId);
}

internal sealed class SessionSyncBridgeSelection
{
    public const string LocalWorkstationEndpoint = "http://127.0.0.1:11436";
    private const string LocalWorkstationServerId = "local-workstation";
    private const string LocalWorkstationServerName = "Local JackLLM Workstation";

    public string ServerEndpoint { get; private init; } = "";
    public string AutoApiBase { get; private init; } = "";
    public string ServerId { get; private init; } = "";
    public string ServerName { get; private init; } = "";
    public string ModelId { get; private init; } = "";
    public string AuthToken { get; private init; } = "";
    public string AuthUserName { get; private init; } = "";

    public bool HasRemoteApi => !string.IsNullOrWhiteSpace(this.AutoApiBase);

    public string DisplayServerName => IsLocalWorkstationEndpoint(this.ServerEndpoint)
        ? LocalWorkstationServerName
        : FirstNonEmpty(this.ServerName, this.ServerId, GetHostName(this.ServerEndpoint), LocalWorkstationServerName);

    public SessionSyncBridgeSelection WithAuth(string authToken, string authUserName)
    {
        return new SessionSyncBridgeSelection
        {
            ServerEndpoint = this.ServerEndpoint,
            AutoApiBase = this.AutoApiBase,
            ServerId = this.ServerId,
            ServerName = this.ServerName,
            ModelId = this.ModelId,
            AuthToken = string.IsNullOrWhiteSpace(authToken) ? this.AuthToken : authToken.Trim(),
            AuthUserName = string.IsNullOrWhiteSpace(authUserName) ? this.AuthUserName : authUserName.Trim()
        };
    }

    public static SessionSyncBridgeSelection Load(string solutionRoot)
    {
        string mcpPath = Path.Combine(solutionRoot, ".vs", "mcp.json");
        if (!File.Exists(mcpPath))
        {
            return FromEnvironment();
        }

        try
        {
            JsonObject? root = JsonNode.Parse(File.ReadAllText(mcpPath)) as JsonObject;
            JsonObject? servers = root?["servers"] as JsonObject;
            if (servers == null)
            {
                return FromEnvironment();
            }

            foreach (KeyValuePair<string, JsonNode?> pair in servers)
            {
                if (!pair.Key.StartsWith("socketjack-", StringComparison.OrdinalIgnoreCase) || pair.Value is not JsonObject entry)
                {
                    continue;
                }

                List<string> args = ReadArgs(entry);
                string endpoint = ReadArg(args, "--server-endpoint");
                string localWebChatEndpoint = ReadArg(args, "--local-webchat-endpoint");
                string model = FirstNonEmpty(ReadArg(args, "--model"), ReadArg(args, "--model-id"));
                string serverId = ReadArg(args, "--server-id");
                string serverName = ReadArg(args, "--server-name");
                string token = FirstNonEmpty(ReadArg(args, "--auth-token"), Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_TOKEN"));
                string userName = FirstNonEmpty(ReadArg(args, "--auth-user"), ReadArg(args, "--auth-username"), Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_USER"));
                if (string.IsNullOrWhiteSpace(endpoint) && entry["url"] != null)
                {
                    endpoint = entry["url"]!.ToString();
                }

                return Create(SelectSessionEndpoint(endpoint, localWebChatEndpoint), serverId, serverName, model, token, userName);
            }
        }
        catch
        {
        }

        return FromEnvironment();
    }

    public static SessionSyncBridgeSelection FromSnapshot(SessionSyncSnapshot snapshot, SessionSyncBridgeSelection fallback)
    {
        string endpoint = FirstNonEmpty(snapshot.ServerEndpoint, snapshot.AutoApiBase, fallback.ServerEndpoint);
        if (IsHostedSocketJackEndpoint(endpoint) || IsLocalProxyEndpoint(endpoint))
        {
            endpoint = FirstNonEmpty(fallback.ServerEndpoint, LocalWorkstationEndpoint);
        }

        string serverId = FirstNonEmpty(snapshot.ServerId, fallback.ServerId);
        string serverName = FirstNonEmpty(snapshot.ServerName, fallback.ServerName, serverId);
        string modelId = FirstNonEmpty(snapshot.ModelId, fallback.ModelId);
        return Create(endpoint, serverId, serverName, modelId, fallback.AuthToken, fallback.AuthUserName);
    }

    public string GetDefaultSessionId(string solutionRoot)
    {
        string solutionName = Path.GetFileName(solutionRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return SanitizeAutoSessionId(FirstNonEmpty(solutionName, "VisualStudioSession"));
    }

    public Uri BuildAutoUri(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(this.AutoApiBase))
        {
            throw new InvalidOperationException("No local JackLLM Workstation session API base is configured.");
        }

        return new Uri(new Uri(this.AutoApiBase.TrimEnd('/') + "/"), pathAndQuery.TrimStart('/'));
    }

    public void ThrowIfMissing()
    {
        if (!this.HasRemoteApi)
        {
            throw new InvalidOperationException("Start JackLLM Workstation at " + LocalWorkstationEndpoint + " before using Session Sync.");
        }
    }

    private static SessionSyncBridgeSelection FromEnvironment()
    {
        return Create(
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ENDPOINT") ?? LocalWorkstationEndpoint,
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_ID") ?? LocalWorkstationServerId,
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_SERVER_NAME") ?? LocalWorkstationServerName,
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_MODEL_ID") ?? "",
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_TOKEN") ?? "",
            Environment.GetEnvironmentVariable("SOCKETJACK_COPILOT_AUTH_USER") ?? "");
    }

    private static SessionSyncBridgeSelection Create(string endpoint, string serverId, string serverName, string modelId, string authToken, string authUserName)
    {
        endpoint = NormalizeEndpoint(string.IsNullOrWhiteSpace(endpoint) ? LocalWorkstationEndpoint : endpoint);
        if (IsHostedSocketJackEndpoint(endpoint) || IsLocalProxyEndpoint(endpoint))
        {
            endpoint = LocalWorkstationEndpoint;
        }

        string autoBase = BuildAutoApiBase(endpoint);
        bool isLocalWorkstation = IsLocalWorkstationEndpoint(endpoint);
        return new SessionSyncBridgeSelection
        {
            ServerEndpoint = endpoint,
            AutoApiBase = autoBase,
            ServerId = isLocalWorkstation ? LocalWorkstationServerId : (string.IsNullOrWhiteSpace(serverId) ? InferServerId(endpoint) : serverId.Trim()),
            ServerName = isLocalWorkstation ? LocalWorkstationServerName : (string.IsNullOrWhiteSpace(serverName) ? serverId.Trim() : serverName.Trim()),
            ModelId = modelId.Trim(),
            AuthToken = authToken.Trim(),
            AuthUserName = authUserName.Trim()
        };
    }

    private static string SelectSessionEndpoint(string endpoint, string localWebChatEndpoint)
    {
        if (!string.IsNullOrWhiteSpace(localWebChatEndpoint))
        {
            return localWebChatEndpoint;
        }

        if (IsHostedSocketJackEndpoint(endpoint) || IsLocalProxyEndpoint(endpoint))
        {
            return LocalWorkstationEndpoint;
        }

        return FirstNonEmpty(endpoint, LocalWorkstationEndpoint);
    }

    private static List<string> ReadArgs(JsonObject entry)
    {
        var args = new List<string>();
        if (entry["args"] is JsonArray array)
        {
            foreach (JsonNode? node in array)
            {
                if (node != null)
                {
                    args.Add(node.ToString());
                }
            }
        }

        return args;
    }

    private static string ReadArg(IReadOnlyList<string> args, string name)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return "";
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        endpoint = (endpoint ?? "").Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return "";
        }

        if (!endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            endpoint = "https://" + endpoint;
        }

        return endpoint.TrimEnd('/');
    }

    private static string BuildAutoApiBase(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return "";
        }

        if (uri.Host.EndsWith("socketjack.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.GetLeftPart(UriPartial.Authority);
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static bool IsLocalWorkstationEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) &&
            (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) &&
            uri.Port == 11436;
    }

    private static bool IsLocalProxyEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) &&
            (uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) &&
            uri.Port != 11436;
    }

    private static bool IsHostedSocketJackEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) &&
            uri.Host.EndsWith("socketjack.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferServerId(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return "";
        }

        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (parts[i].Equals("proxy", StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1];
            }
        }

        return uri.Host;
    }

    private static string GetHostName(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ? uri.Host : "";
    }

    private static string SanitizeAutoSessionId(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return "vsync_" + Guid.NewGuid().ToString("N");
        }

        StringBuilder builder = new(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.' || ch == ':' || ch == '-' ? ch : '_');
        }

        string result = builder.ToString();
        return result.Length <= 96 ? result : result.Substring(0, 96);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }
}

internal sealed class SessionSyncIgnoreManifest
{
    public List<string> Ignored { get; set; } = new();
    public List<string> RemoteIgnored { get; set; } = new();

    public static SessionSyncIgnoreManifest Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new SessionSyncIgnoreManifest();
            }

            string text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return new SessionSyncIgnoreManifest();
            }

            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<SessionSyncIgnoreManifest>(text, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SessionSyncIgnoreManifest();
            }

            return new SessionSyncIgnoreManifest
            {
                Ignored = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            };
        }
        catch
        {
            return new SessionSyncIgnoreManifest();
        }
    }

    public void Save(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public bool IsIgnored(string relativePath)
    {
        return Matches(this.Ignored, relativePath);
    }

    public bool IsRemoteIgnored(string relativePath)
    {
        return Matches(this.RemoteIgnored, relativePath);
    }

    public void AddIgnored(string relativePath, bool isFolder)
    {
        Add(this.Ignored, relativePath, isFolder);
    }

    public void AddRemoteIgnored(string relativePath, bool isFolder)
    {
        Add(this.RemoteIgnored, relativePath, isFolder);
    }

    public void AddPattern(string pattern)
    {
        pattern = NormalizeEntry(pattern);
        if (!string.IsNullOrWhiteSpace(pattern) && !this.Ignored.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            this.Ignored.Add(pattern);
    }

    public bool Remove(string relativePath)
    {
        relativePath = NormalizeEntry(relativePath).TrimEnd('/');
        int removed = this.Ignored.RemoveAll(item => NormalizeEntry(item).TrimEnd('/').Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        removed += this.RemoteIgnored.RemoveAll(item => NormalizeEntry(item).TrimEnd('/').Equals(relativePath, StringComparison.OrdinalIgnoreCase));
        return removed > 0;
    }

    public IEnumerable<string> AllEntries()
    {
        return this.Ignored.Concat(this.RemoteIgnored).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void Add(List<string> entries, string relativePath, bool isFolder)
    {
        relativePath = Normalize(relativePath).TrimEnd('/');
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        if (isFolder)
        {
            relativePath += "/";
        }

        if (!entries.Any(item => string.Equals(item, relativePath, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(relativePath);
        }
    }

    private static bool Matches(IEnumerable<string> entries, string relativePath)
    {
        relativePath = Normalize(relativePath).TrimEnd('/');
        foreach (string rawEntry in entries)
        {
            string entry = NormalizeEntry(rawEntry);
            if (entry.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(relativePath, entry.Substring("regex:".Length), System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        return true;
                }
                catch (ArgumentException)
                {
                    // Keep malformed hand-edited rules visible in the manifest without breaking Session Sync.
                }
                continue;
            }

            if (entry.Contains('*') || entry.Contains('?'))
            {
                string wildcard = "^" + System.Text.RegularExpressions.Regex.Escape(entry)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(relativePath, wildcard, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
                continue;
            }

            string normalized = entry.TrimEnd('/');
            if (string.Equals(normalized, relativePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (entry.EndsWith("/", StringComparison.Ordinal) &&
                relativePath.StartsWith(normalized + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Normalize(string value)
    {
        return (value ?? "").Replace('\\', '/').Trim();
    }

    private static string NormalizeEntry(string value)
    {
        value = (value ?? "").Trim();
        return value.StartsWith("regex:", StringComparison.OrdinalIgnoreCase) ? value : Normalize(value);
    }
}

internal sealed class SessionSyncSnapshot
{
    public int Version { get; set; }
    public string SessionId { get; set; } = "";
    public string SolutionRoot { get; set; } = "";
    public string ServerEndpoint { get; set; } = "";
    public string AutoApiBase { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ServerName { get; set; } = "";
    public string ModelId { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.MinValue;
    public List<SessionSyncSnapshotFile> Files { get; set; } = new();

    public SessionSyncSnapshotFile? Find(string relativePath)
    {
        return this.Files.FirstOrDefault(file => string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
    }

    public SessionSyncSnapshotFile GetOrAdd(string relativePath)
    {
        SessionSyncSnapshotFile? file = this.Find(relativePath);
        if (file != null)
        {
            return file;
        }

        file = new SessionSyncSnapshotFile { RelativePath = relativePath };
        this.Files.Add(file);
        return file;
    }
}

internal sealed class SessionSyncSnapshotFile
{
    public string RelativePath { get; set; } = "";
    public string LocalSha256 { get; set; } = "";
    public string RemoteSha256 { get; set; } = "";
    public long LocalSizeBytes { get; set; }
    public long RemoteSizeBytes { get; set; }
    public DateTimeOffset LocalLastWriteUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset RemoteLastWriteUtc { get; set; } = DateTimeOffset.MinValue;
}
