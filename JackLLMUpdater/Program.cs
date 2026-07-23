using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace JackLLMUpdater;

internal static class Program {
    private const string DefaultManifestUrl = "https://socketjack.com/Update/meta";
    private const string DefaultUpdateFileBaseUrl = "https://socketjack.com/Update/";
    private const string DefaultCompanionChannelManifestUrl = "https://socketjack.com/Update/jackllm-companion/meta";
    private const string GuiRegistryKeyPath = @"Software\SocketJack\JackLLM";
    private const string GuiRegistryInstallLocationValueName = "InstallLocation";
    private const string LegacyLmVsProxyProcessPrefix = "LmVsProxy";
    private const string GuiProcessName = "JackLLM";
    private const string CompanionProcessName = "JackLLMCompanion";
    private const string CompanionDirectoryName = "Companion";
    private const string UpdaterProcessName = "JackLLMUpdater";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [STAThread]
    public static int Main(string[] args) {
        return MainAsync(args).GetAwaiter().GetResult();
    }

    private static async Task<int> MainAsync(string[] args) {
        UpdateOptions options = UpdateOptions.Parse(args);
        if (options.ShowHelp) {
            Console.WriteLine("JackLLM Updater");
            Console.WriteLine("--check | --force | --diff | --watch | --startup-check | --parent-pid <pid> | --target <folder> | --manifest <url> | --base-url <url> | --companion-manifest <url>");
            return 0;
        }

        if (options.DiffOnly) {
            UpdateDiff diff = await DownloadDiff(args);
            Console.WriteLine(JsonSerializer.Serialize(diff, JsonOptions));
            return diff.HasError ? 1 : 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(options.StatusPath) ?? AppContext.BaseDirectory);
        ApplyPendingReplacements(options.TargetDirectory, options.StatusPath);

        if (options.Watch) {
            using var mutex = new Mutex(false, "Local\\JackLLMUpdaterWatch");
            if (!mutex.WaitOne(0))
                return 0;

            using var watchCancellation = new CancellationTokenSource();
            using Process? parentProcess = TryAttachParentProcess(options.ParentProcessId, watchCancellation);
            CancellationToken watchToken = watchCancellation.Token;
            if (watchToken.IsCancellationRequested)
                return 0;

            if (options.StartupCheck)
                RunStartupCheckWithSplash(options);
            else
                await RunOnceAsync(options with { CheckOnly = true, AutoApplyMissingFiles = true }, TimeSpan.FromSeconds(15), watchToken);

            while (!watchToken.IsCancellationRequested) {
                try {
                    await Task.Delay(options.WatchInterval, watchToken);
                } catch (OperationCanceledException) when (watchToken.IsCancellationRequested) {
                    break;
                }
                UpdaterConfig config = UpdaterConfig.Load(options.ConfigPath);
                bool shouldApply = options.Force || config.AlwaysUpdate;
                UpdateStatus watchStatus = await RunOnceAsync(options with {
                    CheckOnly = !shouldApply,
                    Force = shouldApply,
                    AutoApplyMissingFiles = !shouldApply
                }, TimeSpan.FromSeconds(20), watchToken);
                if (!watchToken.IsCancellationRequested && !shouldApply && watchStatus.UpdateAvailable)
                    PromptForAvailableUpdate(options, watchStatus, config, allowSnooze: true);
            }

            return 0;
        }

        if (options.StartupCheck) {
            RunStartupCheckWithSplash(options);
            return 0;
        }

        UpdateStatus status = await RunOnceAsync(options, TimeSpan.FromSeconds(30));
        return status.HasError ? 1 : 0;
    }

    private static void RunStartupCheckWithSplash(UpdateOptions options) {
        if (!CanShowWpfUi()) {
            RunStartupCheckHeadless(options);
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) {
            try {
                RunOnStaThread(() => RunStartupCheckWithSplash(options), "Startup update check failed.");
            } catch (InvalidOperationException ex) when (IsWpfUiThreadException(ex)) {
                RunStartupCheckHeadless(options);
            }
            return;
        }

        try {
            var app = new Application {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var splash = new StartupSplashWindow();
            app.MainWindow = splash;
            splash.Loaded += async (_, _) => {
                try {
                    UpdateStatus status = await RunWithRetriesAsync(options with { CheckOnly = true }, attempts: 2, perAttemptTimeout: TimeSpan.FromSeconds(5));
                    splash.Hide();
                    HandleStartupAvailableUpdate(options, status);
                } finally {
                    splash.Dispatcher.Invoke(() => {
                        splash.Close();
                        app.Shutdown();
                    });
                }
            };
            app.Run(splash);
        } catch (InvalidOperationException ex) when (IsWpfUiThreadException(ex)) {
            RunStartupCheckHeadless(options);
        }
    }

    private static bool CanShowWpfUi() {
        return OperatingSystem.IsWindows()
            && Thread.CurrentThread.GetApartmentState() == ApartmentState.STA;
    }

    private static void RunStartupCheckHeadless(UpdateOptions options) {
        UpdateStatus status = RunWithRetriesAsync(options with { CheckOnly = true }, attempts: 2, perAttemptTimeout: TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        if (!status.UpdateAvailable)
            return;

        UpdaterConfig config = UpdaterConfig.Load(options.ConfigPath);
        if (config.AlwaysUpdate)
            _ = RunOnceAsync(options with { Force = true, CheckOnly = false }, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
    }

    private static bool IsWpfUiThreadException(Exception ex) {
        string message = ex.GetBaseException().Message;
        return message.IndexOf("STA", StringComparison.OrdinalIgnoreCase) >= 0
            || message.IndexOf("UI components", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void RunOnStaThread(Action action, string failureMessage) {
        Exception? failure = null;
        var thread = new Thread(() => {
            try {
                action();
            } catch (Exception ex) {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new InvalidOperationException(failureMessage, failure);
    }

    private static void HandleStartupAvailableUpdate(UpdateOptions options, UpdateStatus status) {
        if (status.UpdateAvailable) {
            UpdaterConfig config = UpdaterConfig.Load(options.ConfigPath);
            if (config.AlwaysUpdate) {
                _ = RunOnceAsync(options with { Force = true, CheckOnly = false }, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
                return;
            }

            PromptForAvailableUpdate(options, status, config, allowSnooze: false);
        }
    }

    private static void PromptForAvailableUpdate(UpdateOptions options, UpdateStatus status, UpdaterConfig config, bool allowSnooze) {
        if (allowSnooze && !config.ShouldPrompt(DateTimeOffset.UtcNow))
            return;

        UpdatePromptAction selectedAction = ShowJackUpdatePrompt(status);
        config.LastPromptUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        if (selectedAction == UpdatePromptAction.AlwaysUpdate) {
            config.AlwaysUpdate = true;
            config.Save(options.ConfigPath);
            _ = RunOnceAsync(options with { Force = true, CheckOnly = false }, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            return;
        }

        if (selectedAction == UpdatePromptAction.UpdateNow)
            _ = RunOnceAsync(options with { Force = true, CheckOnly = false }, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();

        config.Save(options.ConfigPath);
    }

    private static UpdatePromptAction ShowJackUpdatePrompt(UpdateStatus status) {
        if (Application.Current?.Dispatcher?.CheckAccess() == true) {
            var dialog = new UpdateAvailableDialog(status);
            _ = dialog.ShowDialog();
            return dialog.SelectedAction;
        }

        UpdatePromptAction selectedAction = UpdatePromptAction.Later;
        RunOnStaThread(() => {
            var dialog = new UpdateAvailableDialog(status);
            _ = dialog.ShowDialog();
            selectedAction = dialog.SelectedAction;
        }, "Update prompt failed.");
        return selectedAction;
    }

    private static async Task<UpdateStatus> RunWithRetriesAsync(UpdateOptions options, int attempts, TimeSpan perAttemptTimeout) {
        UpdateStatus last = UpdateStatus.Begin(options.TargetDirectory);
        for (int attempt = 1; attempt <= Math.Max(1, attempts); attempt++) {
            last.Message = "Checking for updates (" + attempt.ToString(CultureInfo.InvariantCulture) + "/" + attempts.ToString(CultureInfo.InvariantCulture) + ")";
            WriteStatus(options.StatusPath, last);
            try {
                last = await RunOnceAsync(options, perAttemptTimeout);
                if (!last.HasError)
                    return last;
            } catch (Exception ex) {
                last = UpdateStatus.Failed(options.TargetDirectory, "Update check failed: " + ex.Message);
                WriteStatus(options.StatusPath, last);
            }
        }

        return last;
    }

    public static async Task<UpdateDiff> DownloadDiff(string[] args) {
        UpdateOptions options = UpdateOptions.Parse(args);
        var diff = new UpdateDiff {
            TargetDirectory = options.TargetDirectory,
            ManifestUrl = options.ManifestUrl,
            CheckedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        try {
            using var http = new HttpClient {
                Timeout = TimeSpan.FromSeconds(30)
            };
            UpdateManifest? manifest = await LoadManifestAsync(http, options, CancellationToken.None);
            if (manifest == null || !manifest.Available) {
                diff.HasError = true;
                diff.Message = string.IsNullOrWhiteSpace(manifest?.Error) ? "Update metadata unavailable." : manifest.Error;
                return diff;
            }

            diff.ManifestGeneratedUtc = manifest.GeneratedUtc;
            diff.TotalFiles = manifest.Files.Count;
            List<UpdateFileChange> changed = FindChangedFiles(options.TargetDirectory, manifest, force: false);
            diff.Files = changed.Select(UpdateDiffFile.FromChange).ToList();
            diff.ChangedFiles = diff.Files.Count;
            diff.MissingFiles = diff.Files.Count(file => file.Missing);
            diff.RemoteNewerFiles = diff.Files.Count(file => file.RemoteIsNewer);
            diff.LocalNewerFiles = diff.Files.Count(file => file.LocalIsNewer);
            diff.Message = BuildChangeSummary(changed, changed.Where(change => change.ShouldApplyByDefault).ToList(), force: false);
            return diff;
        } catch (Exception ex) {
            diff.HasError = true;
            diff.Message = "Update diff failed: " + ex.Message;
            return diff;
        }
    }

    private static async Task<UpdateStatus> RunOnceAsync(UpdateOptions options, TimeSpan timeout, CancellationToken cancellationToken = default) {
        UpdateStatus status = UpdateStatus.Begin(options.TargetDirectory);
        WriteStatus(options.StatusPath, status);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try {
            using var http = new HttpClient {
                Timeout = timeout
            };
            UpdateManifest? manifest = await LoadManifestAsync(http, options, timeoutSource.Token);
            if (manifest == null || !manifest.Available) {
                status.HasError = true;
                status.Message = string.IsNullOrWhiteSpace(manifest?.Error) ? "Update manifest unavailable." : manifest.Error;
                WriteStatus(options.StatusPath, status);
                return status;
            }

            string companionTargetDirectory = GetCompanionTargetDirectory(options.TargetDirectory);
            ApplyPendingReplacements(companionTargetDirectory, options.StatusPath);

            UpdateManifest guiManifest = BuildGuiManifest(manifest);
            UpdateManifest companionManifest = BuildCompanionManifestFromGuiManifest(manifest, options);
            if (!options.SkipCompanionUpdate && companionManifest.Files.Count == 0)
                companionManifest = await TryLoadCompanionChannelManifestAsync(http, options, timeoutSource.Token, status);

            List<UpdateFileChange> changed = FindChangedFiles(options.TargetDirectory, guiManifest, options.Force);
            List<UpdateFileChange> companionChanged = options.SkipCompanionUpdate || companionManifest.Files.Count == 0
                ? new List<UpdateFileChange>()
                : FindChangedFiles(companionTargetDirectory, companionManifest, options.Force);
            List<UpdateFileChange> applicableChanges = changed
                .Where(change => options.Force || change.ShouldApplyByDefault)
                .ToList();
            List<UpdateFileChange> companionApplicableChanges = companionChanged
                .Where(change => options.Force || change.ShouldApplyByDefault)
                .ToList();
            List<UpdateFile> filesToApply = options.CheckOnly
                ? (options.AutoApplyMissingFiles ? applicableChanges.Where(change => change.Missing).Select(change => change.File).ToList() : new List<UpdateFile>())
                : applicableChanges.Select(change => change.File).ToList();
            List<UpdateFile> companionFilesToApply = options.CheckOnly
                ? (options.AutoApplyMissingFiles ? companionApplicableChanges.Where(change => change.Missing).Select(change => change.File).ToList() : new List<UpdateFile>())
                : companionApplicableChanges.Select(change => change.File).ToList();
            status.UpdateAvailable = applicableChanges.Count > 0 || companionApplicableChanges.Count > 0;
            status.ManifestGeneratedUtc = manifest.GeneratedUtc;
            status.TotalFiles = guiManifest.Files.Count + companionManifest.Files.Count;
            status.ChangedFiles = changed.Count + companionChanged.Count;
            status.MissingFiles = changed.Count(change => change.Missing) + companionChanged.Count(change => change.Missing);
            status.RemoteNewerFiles = changed.Count(change => change.RemoteIsNewer) + companionChanged.Count(change => change.RemoteIsNewer);
            status.LocalNewerFiles = changed.Count(change => change.LocalIsNewer) + companionChanged.Count(change => change.LocalIsNewer);
            status.SkippedLocalNewerFiles = changed.Count(change => change.LocalIsNewer && !options.Force) + companionChanged.Count(change => change.LocalIsNewer && !options.Force);
            status.CompanionTargetDirectory = companionTargetDirectory;
            status.CompanionTotalFiles = companionManifest.Files.Count;
            status.CompanionChangedFiles = companionChanged.Count;
            status.CompanionMissingFiles = companionChanged.Count(change => change.Missing);
            status.CompanionUpdateAvailable = companionApplicableChanges.Count > 0;
            status.Message = BuildCombinedChangeSummary(changed, applicableChanges, companionChanged, companionApplicableChanges, options.Force);
            WriteStatus(options.StatusPath, status);

            if (filesToApply.Count == 0 && companionFilesToApply.Count == 0)
                return status;

            int updated = 0;
            int pending = 0;
            int companionUpdated = 0;
            int companionPending = 0;
            var pendingReplacements = new List<PendingReplacement>();
            var companionPendingReplacements = new List<PendingReplacement>();
            RelatedProcessRestartScope restartScope = RelatedProcessRestartScope.Empty;
            if (!options.CheckOnly) {
                status.Message = options.NoCloseTarget
                    ? "Repairing install files without closing JackLLM..."
                    : "Closing JackLLM applications before applying update...";
                WriteStatus(options.StatusPath, status);
                if (!options.NoCloseTarget)
                    restartScope = CloseRelatedProcessesForUpdate(options.TargetDirectory);
            }

            try {
                foreach (UpdateFile file in filesToApply) {
                    status.Message = "Downloading " + file.Path;
                    WriteStatus(options.StatusPath, status);
                    UpdateApplyResult result = await DownloadAndApplyFileAsync(http, options, manifest, file, timeoutSource.Token);
                    if (result.Applied)
                        updated++;
                    if (!string.IsNullOrWhiteSpace(result.PendingSourcePath)) {
                        pending++;
                        pendingReplacements.Add(new PendingReplacement {
                            SourcePath = result.PendingSourcePath,
                            TargetPath = result.TargetPath,
                            LastWriteUtc = result.LastWriteUtc
                        });
                    }
                }
            } finally {
                if (restartScope.HasProcessesToRestart) {
                    status.Message = "Restarting JackLLM applications...";
                    WriteStatus(options.StatusPath, status);
                }
                restartScope.RestartClosedProcesses();
            }

            if (companionFilesToApply.Count > 0) {
                RelatedProcessRestartScope companionRestartScope = RelatedProcessRestartScope.Empty;
                status.CompanionIsDownloading = true;
                status.CompanionMessage = "Preparing Companion update...";
                status.CompanionCurrentFile = "";
                WriteStatus(options.StatusPath, status);

                try {
                    if (!options.NoCloseTarget)
                        companionRestartScope = CloseCompanionProcessForUpdate(companionTargetDirectory);
                    foreach (UpdateFile file in companionFilesToApply) {
                        status.CompanionIsDownloading = true;
                        status.CompanionCurrentFile = file.Path;
                        status.CompanionMessage = "Downloading Companion file: " + file.Path;
                        WriteStatus(options.StatusPath, status);

                        UpdateApplyResult result = await DownloadAndApplyFileAsync(http, options with { TargetDirectory = companionTargetDirectory }, companionManifest, file, timeoutSource.Token);
                        if (result.Applied)
                            companionUpdated++;
                        if (!string.IsNullOrWhiteSpace(result.PendingSourcePath)) {
                            companionPending++;
                            companionPendingReplacements.Add(new PendingReplacement {
                                SourcePath = result.PendingSourcePath,
                                TargetPath = result.TargetPath,
                                LastWriteUtc = result.LastWriteUtc
                            });
                        }
                    }
                } finally {
                    if (companionRestartScope.HasProcessesToRestart) {
                        status.CompanionMessage = "Restarting Companion...";
                        status.CompanionCurrentFile = "";
                        WriteStatus(options.StatusPath, status);
                    }
                    companionRestartScope.RestartClosedProcesses();
                    status.CompanionIsDownloading = false;
                    status.CompanionCurrentFile = "";
                }
            }

            if (pendingReplacements.Count > 0)
                SavePendingReplacements(options.TargetDirectory, pendingReplacements);
            if (companionPendingReplacements.Count > 0)
                SavePendingReplacements(companionTargetDirectory, companionPendingReplacements);

            status.UpdatedFiles = updated;
            status.PendingFiles = pending;
            status.CompanionUpdatedFiles = companionUpdated;
            status.CompanionPendingFiles = companionPending;
            List<UpdateFileChange> remainingChanged = FindChangedFiles(options.TargetDirectory, guiManifest, false);
            List<UpdateFileChange> remainingCompanionChanged = options.SkipCompanionUpdate || companionManifest.Files.Count == 0
                ? new List<UpdateFileChange>()
                : FindChangedFiles(companionTargetDirectory, companionManifest, false);
            status.ChangedFiles = remainingChanged.Count + remainingCompanionChanged.Count;
            status.MissingFiles = remainingChanged.Count(change => change.Missing) + remainingCompanionChanged.Count(change => change.Missing);
            status.RemoteNewerFiles = remainingChanged.Count(change => change.RemoteIsNewer) + remainingCompanionChanged.Count(change => change.RemoteIsNewer);
            status.LocalNewerFiles = remainingChanged.Count(change => change.LocalIsNewer) + remainingCompanionChanged.Count(change => change.LocalIsNewer);
            status.SkippedLocalNewerFiles = remainingChanged.Count(change => change.LocalIsNewer) + remainingCompanionChanged.Count(change => change.LocalIsNewer);
            status.CompanionChangedFiles = remainingCompanionChanged.Count;
            status.CompanionMissingFiles = remainingCompanionChanged.Count(change => change.Missing);
            status.CompanionUpdateAvailable = remainingCompanionChanged.Any(change => change.ShouldApplyByDefault) || companionPending > 0;
            status.UpdateAvailable = pending > 0 || companionPending > 0 ||
                                     remainingChanged.Any(change => change.ShouldApplyByDefault) ||
                                     remainingCompanionChanged.Any(change => change.ShouldApplyByDefault);
            status.Message = BuildCompletionSummary(pending, companionPending, remainingChanged, remainingCompanionChanged);
            status.CompanionMessage = companionPending > 0
                ? "Companion update staged. Restart Companion to finish " + companionPending.ToString(CultureInfo.InvariantCulture) + " locked file(s)."
                : remainingCompanionChanged.Count > 0
                    ? BuildChangeSummary(remainingCompanionChanged, remainingCompanionChanged.Where(change => change.ShouldApplyByDefault).ToList(), false)
                    : companionUpdated > 0
                        ? "Companion update complete."
                        : status.CompanionMessage;
            WriteStatus(options.StatusPath, status);
            return status;
        } catch (OperationCanceledException) {
            status.HasError = !cancellationToken.IsCancellationRequested;
            status.Message = cancellationToken.IsCancellationRequested ? "Updater stopped." : "Update check timed out.";
            WriteStatus(options.StatusPath, status);
            return status;
        } catch (Exception ex) {
            status.HasError = true;
            status.Message = "Update failed: " + ex.Message;
            WriteStatus(options.StatusPath, status);
            return status;
        }
    }

    private static async Task<UpdateManifest?> LoadManifestAsync(HttpClient http, UpdateOptions options, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, options.ManifestUrl);
        request.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
            return null;

        string trimmed = body.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
            throw new InvalidOperationException("The update URL returned a directory page instead of the JSON manifest.");

        UpdateManifest? manifest = JsonSerializer.Deserialize<UpdateManifest>(body, JsonOptions);
        if (manifest != null && string.IsNullOrWhiteSpace(manifest.BaseUrl))
            manifest.BaseUrl = BuildDefaultUpdateBaseUrl(options.ManifestUrl);
        return manifest;
    }

    private static UpdateManifest BuildGuiManifest(UpdateManifest manifest) {
        return new UpdateManifest {
            Available = manifest.Available,
            BaseUrl = manifest.BaseUrl,
            GeneratedUtc = manifest.GeneratedUtc,
            Error = manifest.Error,
            Files = manifest.Files
                .Where(file => !IsCompanionManifestPath(file.Path))
                .Where(file => IsInstallPayloadFile(file.Path))
                .Select(CloneUpdateFile)
                .ToList()
        };
    }

    private static UpdateManifest BuildCompanionManifestFromGuiManifest(UpdateManifest manifest, UpdateOptions options) {
        var files = manifest.Files
            .Where(file => IsCompanionManifestPath(file.Path))
            .Select(file => {
                UpdateFile clone = CloneUpdateFile(file);
                clone.Path = StripCompanionManifestPrefix(file.Path);
                if (string.IsNullOrWhiteSpace(clone.Url))
                    clone.Url = CombineUrl(CombineUrl(manifest.BaseUrl, CompanionDirectoryName + "/"), clone.Path);
                return clone;
            })
            .Where(file => IsInstallPayloadFile(file.Path))
            .ToList();

        return new UpdateManifest {
            Available = files.Count > 0,
            BaseUrl = CombineUrl(manifest.BaseUrl, CompanionDirectoryName + "/"),
            GeneratedUtc = manifest.GeneratedUtc,
            Error = files.Count > 0 ? "" : "Companion files were not listed in the GUI update manifest.",
            Files = files
        };
    }

    private static async Task<UpdateManifest> TryLoadCompanionChannelManifestAsync(HttpClient http, UpdateOptions options, CancellationToken cancellationToken, UpdateStatus status) {
        if (options.SkipCompanionUpdate || string.IsNullOrWhiteSpace(options.CompanionManifestUrl))
            return EmptyCompanionManifest("Companion update metadata disabled.");

        try {
            UpdateManifest? manifest = await LoadManifestAsync(http, options with { ManifestUrl = options.CompanionManifestUrl }, cancellationToken);
            if (manifest == null || !manifest.Available) {
                status.CompanionHasError = true;
                status.CompanionMessage = string.IsNullOrWhiteSpace(manifest?.Error)
                    ? "Companion update metadata unavailable."
                    : manifest.Error;
                return EmptyCompanionManifest(status.CompanionMessage);
            }

            return manifest;
        } catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidOperationException || ex is JsonException) {
            status.CompanionHasError = true;
            status.CompanionMessage = "Companion update metadata unavailable: " + ex.Message;
            return EmptyCompanionManifest(status.CompanionMessage);
        }
    }

    private static UpdateManifest EmptyCompanionManifest(string message) {
        return new UpdateManifest {
            Available = false,
            BaseUrl = "",
            GeneratedUtc = "",
            Error = message,
            Files = new List<UpdateFile>()
        };
    }

    private static UpdateFile CloneUpdateFile(UpdateFile file) {
        return new UpdateFile {
            Path = file.Path,
            Md5 = file.Md5,
            Sha256 = file.Sha256,
            Hash = file.Hash,
            HashAlgorithm = file.HashAlgorithm,
            Length = file.Length,
            LastWriteUtc = file.LastWriteUtc,
            Url = file.Url
        };
    }

    private static bool IsCompanionManifestPath(string path) {
        string normalized = NormalizeManifestPath(path);
        return normalized.StartsWith(CompanionDirectoryName + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string StripCompanionManifestPrefix(string path) {
        string normalized = NormalizeManifestPath(path);
        string prefix = CompanionDirectoryName + "/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized.Substring(prefix.Length)
            : normalized;
    }

    private static string NormalizeManifestPath(string path) {
        return (path ?? "").Replace('\\', '/').TrimStart('/');
    }

    private static string GetCompanionTargetDirectory(string targetDirectory) {
        return Path.Combine(targetDirectory, CompanionDirectoryName);
    }

    private static List<UpdateFileChange> FindChangedFiles(string targetDirectory, UpdateManifest manifest, bool force) {
        var changed = new List<UpdateFileChange>();
        foreach (UpdateFile file in manifest.Files) {
            if (!IsInstallPayloadFile(file.Path))
                continue;
            string targetPath = Path.Combine(targetDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(targetPath)) {
                changed.Add(UpdateFileChange.ForMissing(file, targetPath));
                continue;
            }

            string localHash = GetPreferredFileHash(targetPath, file);
            string remoteHash = GetPreferredManifestHash(file);
            bool hashMatches = !string.IsNullOrWhiteSpace(remoteHash) &&
                               string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
            if (hashMatches && !force)
                continue;

            changed.Add(UpdateFileChange.Changed(file, targetPath, force, localHash, remoteHash));
        }
        return changed;
    }

    private static string BuildCombinedChangeSummary(List<UpdateFileChange> guiChanged, List<UpdateFileChange> guiApplicableChanges, List<UpdateFileChange> companionChanged, List<UpdateFileChange> companionApplicableChanges, bool force) {
        if (guiChanged.Count == 0 && companionChanged.Count == 0)
            return "JackLLM and Companion are up to date.";

        var parts = new List<string>();
        if (guiChanged.Count > 0)
            parts.Add("JackLLM: " + BuildChangeSummary(guiChanged, guiApplicableChanges, force));
        if (companionChanged.Count > 0)
            parts.Add("Companion: " + BuildChangeSummary(companionChanged, companionApplicableChanges, force));
        return string.Join(" ", parts);
    }

    private static string BuildCompletionSummary(int pending, int companionPending, List<UpdateFileChange> remainingChanged, List<UpdateFileChange> remainingCompanionChanged) {
        if (pending > 0 || companionPending > 0) {
            var parts = new List<string>();
            if (pending > 0)
                parts.Add(pending.ToString(CultureInfo.InvariantCulture) + " JackLLM locked file(s)");
            if (companionPending > 0)
                parts.Add(companionPending.ToString(CultureInfo.InvariantCulture) + " Companion locked file(s)");
            return "Update staged. Restart affected JackLLM app(s) to finish " + string.Join(", ", parts) + ".";
        }

        if (remainingChanged.Count > 0 || remainingCompanionChanged.Count > 0) {
            return BuildCombinedChangeSummary(
                remainingChanged,
                remainingChanged.Where(change => change.ShouldApplyByDefault).ToList(),
                remainingCompanionChanged,
                remainingCompanionChanged.Where(change => change.ShouldApplyByDefault).ToList(),
                false);
        }

        return "Update complete.";
    }

    private static string BuildChangeSummary(List<UpdateFileChange> changed, List<UpdateFileChange> applicableChanges, bool force) {
        if (changed.Count == 0)
            return "JackLLM is up to date.";

        int localNewer = changed.Count(change => change.LocalIsNewer);
        int remoteNewer = changed.Count(change => change.RemoteIsNewer);
        int missing = changed.Count(change => change.Missing);
        int unknown = changed.Count - localNewer - remoteNewer - missing;
        if (!force && applicableChanges.Count == 0 && localNewer > 0)
            return localNewer.ToString(CultureInfo.InvariantCulture) + " local file(s) are newer than SocketJack update metadata. Keeping the newer local copy unless you force a revert.";

        var parts = new List<string>();
        if (missing > 0)
            parts.Add(missing.ToString(CultureInfo.InvariantCulture) + " missing");
        if (remoteNewer > 0)
            parts.Add(remoteNewer.ToString(CultureInfo.InvariantCulture) + " newer on SocketJack update");
        if (localNewer > 0 && !force)
            parts.Add(localNewer.ToString(CultureInfo.InvariantCulture) + " newer locally and kept");
        if (unknown > 0 || (localNewer > 0 && force))
            parts.Add((unknown + (force ? localNewer : 0)).ToString(CultureInfo.InvariantCulture) + " changed");

        string summary = string.Join(", ", parts);
        return applicableChanges.Count.ToString(CultureInfo.InvariantCulture) + " update file(s) ready" +
               (string.IsNullOrWhiteSpace(summary) ? "." : " (" + summary + ").");
    }

    private static bool IsMissingTargetFile(string targetDirectory, UpdateFile file) {
        if (!IsInstallPayloadFile(file.Path))
            return false;
        string targetPath = Path.Combine(targetDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
        return !File.Exists(targetPath);
    }

    private static async Task<UpdateApplyResult> DownloadAndApplyFileAsync(HttpClient http, UpdateOptions options, UpdateManifest manifest, UpdateFile file, CancellationToken cancellationToken) {
        string targetPath = Path.Combine(options.TargetDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
        string? directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string tempPath = targetPath + ".lmvsdownload";
        string fileUrl = string.IsNullOrWhiteSpace(file.Url) ? CombineUrl(manifest.BaseUrl, file.Path) : file.Url;
        await using (Stream source = await http.GetStreamAsync(fileUrl, cancellationToken))
        await using (FileStream destination = File.Create(tempPath)) {
            await source.CopyToAsync(destination, cancellationToken);
        }

        string downloadedHash = GetPreferredFileHash(tempPath, file);
        string manifestHash = GetPreferredManifestHash(file);
        if (string.IsNullOrWhiteSpace(manifestHash) || !string.Equals(downloadedHash, manifestHash, StringComparison.OrdinalIgnoreCase)) {
            TryDelete(tempPath);
            throw new InvalidOperationException("Downloaded hash did not match for " + file.Path + ".");
        }

        try {
            File.Copy(tempPath, targetPath, overwrite: true);
            ApplyRemoteLastWriteTime(targetPath, file);
            TryDelete(tempPath);
            return new UpdateApplyResult { Applied = true, TargetPath = targetPath };
        } catch (IOException) {
            string pendingPath = targetPath + ".lmvsupdate";
            File.Copy(tempPath, pendingPath, overwrite: true);
            ApplyRemoteLastWriteTime(pendingPath, file);
            TryDelete(tempPath);
            return new UpdateApplyResult {
                Applied = false,
                PendingSourcePath = pendingPath,
                TargetPath = targetPath,
                LastWriteUtc = file.LastWriteUtc
            };
        } catch (UnauthorizedAccessException) {
            string pendingPath = targetPath + ".lmvsupdate";
            File.Copy(tempPath, pendingPath, overwrite: true);
            ApplyRemoteLastWriteTime(pendingPath, file);
            TryDelete(tempPath);
            return new UpdateApplyResult {
                Applied = false,
                PendingSourcePath = pendingPath,
                TargetPath = targetPath,
                LastWriteUtc = file.LastWriteUtc
            };
        }
    }

    private static void ApplyPendingReplacements(string targetDirectory, string statusPath) {
        string path = PendingReplacementsPath(targetDirectory);
        if (!File.Exists(path))
            return;

        try {
            List<PendingReplacement>? replacements = JsonSerializer.Deserialize<List<PendingReplacement>>(File.ReadAllText(path), JsonOptions);
            if (replacements == null || replacements.Count == 0) {
                TryDelete(path);
                return;
            }

            var remaining = new List<PendingReplacement>();
            int applied = 0;
            foreach (PendingReplacement replacement in replacements) {
                try {
                    if (File.Exists(replacement.SourcePath)) {
                        Directory.CreateDirectory(Path.GetDirectoryName(replacement.TargetPath) ?? targetDirectory);
                        File.Copy(replacement.SourcePath, replacement.TargetPath, overwrite: true);
                        ApplyRemoteLastWriteTime(replacement.TargetPath, replacement.LastWriteUtc);
                        TryDelete(replacement.SourcePath);
                        applied++;
                    }
                } catch {
                    remaining.Add(replacement);
                }
            }

            if (remaining.Count == 0)
                TryDelete(path);
            else
                File.WriteAllText(path, JsonSerializer.Serialize(remaining, JsonOptions));

            if (applied > 0) {
                UpdateStatus status = UpdateStatus.Begin(targetDirectory);
                status.UpdatedFiles = applied;
                status.Message = "Finished applying " + applied.ToString(CultureInfo.InvariantCulture) + " staged update file(s).";
                WriteStatus(statusPath, status);
            }
        } catch {
            // Leave pending metadata in place for the next launch.
        }
    }

    private static void SavePendingReplacements(string targetDirectory, List<PendingReplacement> pendingReplacements) {
        string path = PendingReplacementsPath(targetDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(pendingReplacements, JsonOptions));
    }

    private static string PendingReplacementsPath(string targetDirectory) {
        return Path.Combine(targetDirectory, "jackllm.pending-updates.json");
    }

    internal static string ResolveUpdateTargetDirectory(string fallbackDirectory) {
        string registeredDirectory = ReadRegisteredInstallDirectory();
        if (!string.IsNullOrWhiteSpace(registeredDirectory))
            return registeredDirectory;

        try {
            return Path.GetFullPath(fallbackDirectory);
        } catch {
            return AppContext.BaseDirectory;
        }
    }

    private static string ReadRegisteredInstallDirectory() {
        if (!OperatingSystem.IsWindows())
            return "";

        string installLocation = ReadRegistryString(RegistryHive.LocalMachine, GuiRegistryKeyPath, GuiRegistryInstallLocationValueName);
        if (string.IsNullOrWhiteSpace(installLocation))
            installLocation = ReadRegistryString(RegistryHive.CurrentUser, GuiRegistryKeyPath, GuiRegistryInstallLocationValueName);
        if (string.IsNullOrWhiteSpace(installLocation))
            return "";

        try {
            installLocation = Environment.ExpandEnvironmentVariables(installLocation.Trim().Trim('"'));
            return Path.GetFullPath(installLocation);
        } catch {
            return "";
        }
    }

    private static string ReadRegistryString(RegistryHive hive, string keyPath, string valueName) {
        foreach (RegistryView view in GetRegistryViews()) {
            try {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? key = baseKey.OpenSubKey(keyPath);
                object? value = key?.GetValue(valueName);
                string? text = value as string;
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            } catch {
            }
        }

        return "";
    }

    private static IEnumerable<RegistryView> GetRegistryViews() {
        yield return RegistryView.Registry64;
        yield return RegistryView.Default;
        yield return RegistryView.Registry32;
    }

    private static RelatedProcessRestartScope CloseRelatedProcessesForUpdate(string targetDirectory) {
        List<RelatedProcessCandidate> candidates = FindRelatedProcessCandidates(targetDirectory);
        if (candidates.Count == 0)
            return RelatedProcessRestartScope.Empty;

        var restartByPath = new Dictionary<string, ProcessRestartInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (RelatedProcessCandidate candidate in candidates) {
            if (!restartByPath.TryGetValue(candidate.RestartInfo.ExecutablePath, out ProcessRestartInfo? existing) ||
                existing.StartHidden && !candidate.RestartInfo.StartHidden)
                restartByPath[candidate.RestartInfo.ExecutablePath] = candidate.RestartInfo;
        }

        foreach (RelatedProcessCandidate candidate in candidates) {
            try {
                using Process process = Process.GetProcessById(candidate.ProcessId);
                TryCloseProcessForUpdate(process);
            } catch {
            }
        }

        return new RelatedProcessRestartScope(restartByPath.Values.ToList());
    }

    private static RelatedProcessRestartScope CloseCompanionProcessForUpdate(string companionTargetDirectory) {
        List<RelatedProcessCandidate> candidates = FindCompanionProcessCandidates(companionTargetDirectory);
        if (candidates.Count == 0)
            return RelatedProcessRestartScope.Empty;

        var restartByPath = new Dictionary<string, ProcessRestartInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (RelatedProcessCandidate candidate in candidates) {
            if (!restartByPath.TryGetValue(candidate.RestartInfo.ExecutablePath, out ProcessRestartInfo? existing) ||
                existing.StartHidden && !candidate.RestartInfo.StartHidden)
                restartByPath[candidate.RestartInfo.ExecutablePath] = candidate.RestartInfo;
        }

        foreach (RelatedProcessCandidate candidate in candidates) {
            try {
                using Process process = Process.GetProcessById(candidate.ProcessId);
                TryCloseProcessForUpdate(process);
            } catch {
            }
        }

        return new RelatedProcessRestartScope(restartByPath.Values.ToList());
    }

    private static List<RelatedProcessCandidate> FindCompanionProcessCandidates(string companionTargetDirectory) {
        var candidates = new List<RelatedProcessCandidate>();
        string targetExecutable = Path.Combine(companionTargetDirectory, CompanionProcessName + ".exe");
        string targetDirectory = Path.GetFullPath(companionTargetDirectory);

        foreach (Process process in Process.GetProcessesByName(CompanionProcessName)) {
            try {
                if (process.Id == Environment.ProcessId || process.HasExited)
                    continue;

                string executablePath = TryGetProcessExecutablePath(process);
                bool pathKnown = !string.IsNullOrWhiteSpace(executablePath);
                if (!pathKnown)
                    executablePath = targetExecutable;

                if (pathKnown && !IsPathWithinDirectory(executablePath, targetDirectory))
                    continue;

                bool startHidden = ShouldRestartHidden(process, CompanionProcessName);
                candidates.Add(new RelatedProcessCandidate(
                    process.Id,
                    new ProcessRestartInfo(
                        executablePath,
                        BuildRestartArguments(CompanionProcessName, startHidden),
                        Path.GetDirectoryName(executablePath) ?? companionTargetDirectory,
                        startHidden)));
            } catch {
            } finally {
                process.Dispose();
            }
        }

        return candidates;
    }

    private static List<RelatedProcessCandidate> FindRelatedProcessCandidates(string targetDirectory) {
        var candidates = new List<RelatedProcessCandidate>();
        foreach (Process process in Process.GetProcesses()) {
            try {
                if (process.Id == Environment.ProcessId || process.HasExited)
                    continue;

                string processName = process.ProcessName;
                if (!IsRelatedProcessName(processName))
                    continue;

                string executablePath = TryGetProcessExecutablePath(process);
                if (string.IsNullOrWhiteSpace(executablePath))
                    executablePath = ResolveKnownRelatedExecutablePath(targetDirectory, processName);
                if (string.IsNullOrWhiteSpace(executablePath))
                    continue;

                bool startHidden = ShouldRestartHidden(process, processName);
                candidates.Add(new RelatedProcessCandidate(
                    process.Id,
                    new ProcessRestartInfo(
                        executablePath,
                        BuildRestartArguments(processName, startHidden),
                        Path.GetDirectoryName(executablePath) ?? targetDirectory,
                        startHidden)));
            } catch {
            } finally {
                process.Dispose();
            }
        }

        return candidates;
    }

    private static bool IsRelatedProcessName(string processName) {
        if (processName.Equals(UpdaterProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (processName.Equals(CompanionProcessName, StringComparison.OrdinalIgnoreCase))
            return false;
        return processName.Equals(GuiProcessName, StringComparison.OrdinalIgnoreCase) ||
               processName.StartsWith(LegacyLmVsProxyProcessPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveKnownRelatedExecutablePath(string targetDirectory, string processName) {
        string candidate = Path.Combine(targetDirectory, processName + ".exe");
        return File.Exists(candidate) ? candidate : "";
    }

    private static bool IsPathWithinDirectory(string path, string directory) {
        try {
            string fullPath = Path.GetFullPath(path);
            string fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    private static string TryGetProcessExecutablePath(Process process) {
        try {
            return process.MainModule?.FileName ?? "";
        } catch {
            return "";
        }
    }

    private static bool ShouldRestartHidden(Process process, string processName) {
        if (!processName.Equals(GuiProcessName, StringComparison.OrdinalIgnoreCase) &&
            !processName.Equals(CompanionProcessName, StringComparison.OrdinalIgnoreCase))
            return false;

        try {
            process.Refresh();
            return process.MainWindowHandle == IntPtr.Zero;
        } catch {
            return false;
        }
    }

    private static string BuildRestartArguments(string processName, bool startHidden) {
        if (!startHidden)
            return "";
        if (processName.Equals(GuiProcessName, StringComparison.OrdinalIgnoreCase) ||
            processName.Equals(CompanionProcessName, StringComparison.OrdinalIgnoreCase))
            return "--tray";
        return "";
    }

    private static void TryCloseProcessForUpdate(Process process) {
        try {
            if (process.HasExited)
                return;

            bool closeRequested = false;
            try {
                closeRequested = process.CloseMainWindow();
            } catch {
            }

            if (closeRequested && WaitForProcessExit(process, 4000))
                return;

            if (!process.HasExited) {
                try {
                    process.Kill(entireProcessTree: true);
                } catch {
                }
                WaitForProcessExit(process, 3000);
            }
        } catch {
        }
    }

    private static bool WaitForProcessExit(Process process, int milliseconds) {
        try {
            return process.WaitForExit(milliseconds) || process.HasExited;
        } catch {
            return false;
        }
    }

    internal static bool IsProcessRunningFromPath(string executablePath) {
        string processName = Path.GetFileNameWithoutExtension(executablePath);
        if (string.IsNullOrWhiteSpace(processName))
            return false;

        foreach (Process process in Process.GetProcessesByName(processName)) {
            try {
                if (process.Id == Environment.ProcessId || process.HasExited)
                    continue;

                string runningPath = TryGetProcessExecutablePath(process);
                if (string.IsNullOrWhiteSpace(runningPath) || PathsEqual(runningPath, executablePath))
                    return true;
            } catch {
            } finally {
                process.Dispose();
            }
        }

        return false;
    }

    private static bool PathsEqual(string left, string right) {
        try {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        } catch {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsSafeRelativePath(string path) {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        path = path.Replace('\\', '/');
        return !Path.IsPathRooted(path) &&
               !path.Contains(':', StringComparison.Ordinal) &&
               !path.StartsWith("/", StringComparison.Ordinal) &&
               !path.Contains("../", StringComparison.Ordinal) &&
               !path.Equals("..", StringComparison.Ordinal);
    }

    private static bool IsInstallPayloadFile(string relativePath) {
        return IsSafeRelativePath(relativePath) &&
               !IsUpdateMetadataOnlyFile(relativePath) &&
               !IsBlockedRuntimeDataPath(relativePath);
    }

    private static bool IsUpdateMetadataOnlyFile(string relativePath) {
        string fileName = Path.GetFileName(relativePath ?? "");
        return fileName.Equals("JackLLM-Setup.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM-Setup.msi", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM.zip", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("Update.zip", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedRuntimeDataPath(string relativePath) {
        relativePath = (relativePath ?? "").Replace('\\', '/').Trim('/');
        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsRuntimeDataSegment))
            return true;

        string fileName = Path.GetFileName(relativePath);
        if (IsRuntimeDataFile(fileName))
            return true;

        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) &&
               !fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeDataSegment(string segment) {
        return segment.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("caches", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("config", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("configs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("database", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("databases", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("jackllmchat", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("log", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("models", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("completemodels", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessionfiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sockjackdml", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("uploads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("userdata", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("user-data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspaces", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRuntimeDataFile(string fileName) {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        string extension = Path.GetExtension(fileName);
        return fileName.Equals(".socketjack-update.meta", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("auth.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dynamicUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM.settings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("lastUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-config.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-status.json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".iobj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ipdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".orig", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".suo", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".user", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wixpdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFileSha256(string path) {
        try {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        } catch {
            return "";
        }
    }

    private static string GetPreferredManifestHash(UpdateFile file) {
        if (file.HashAlgorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        if (!string.IsNullOrWhiteSpace(file.Sha256))
            return file.Sha256;
        if (!string.IsNullOrWhiteSpace(file.Hash) && file.Hash.Length == 64)
            return file.Hash;
        return "";
    }

    private static string GetPreferredFileHash(string path, UpdateFile file) {
        return GetFileSha256(path);
    }

    private static void ApplyRemoteLastWriteTime(string path, UpdateFile file) {
        ApplyRemoteLastWriteTime(path, file.LastWriteUtc);
    }

    private static void ApplyRemoteLastWriteTime(string path, string lastWriteUtc) {
        if (!DateTimeOffset.TryParse(lastWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
            return;
        try {
            File.SetLastWriteTimeUtc(path, timestamp.UtcDateTime);
        } catch {
        }
    }

    private static string CombineUrl(string baseUrl, string relativePath) {
        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultUpdateFileBaseUrl : baseUrl.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            baseUrl += "/";
        return baseUrl + (relativePath ?? "").TrimStart('/').Replace("\\", "/");
    }

    private static string BuildDefaultUpdateBaseUrl(string manifestUrl) {
        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri)) {
            string path = uri.AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/jackupdate", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/jackupdate/meta", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/jackupdate/manifest", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/jackupdate/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/update", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/update/meta", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/update/manifest", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/update/manifest.json", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/api/jackllm/update", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/lmvsproxy/update", StringComparison.OrdinalIgnoreCase))
                return DefaultUpdateFileBaseUrl;
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/";
        }

        return DefaultUpdateFileBaseUrl;
    }

    internal static string BuildDefaultCompanionManifestUrl(string manifestUrl) {
        if (Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri)) {
            string path = uri.AbsolutePath.Replace('\\', '/').TrimEnd('/');

            if (path.EndsWith("/update/meta", StringComparison.OrdinalIgnoreCase)) {
                if (uri.Host.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("www.socketjack.com", StringComparison.OrdinalIgnoreCase))
                    return new Uri(uri, "/Update/jackllm-companion/meta").ToString();

                string prefix = path.Substring(0, path.Length - "/update/meta".Length);
                return new Uri(uri, prefix + "/Update/jackllm-companion/meta").ToString();
            }
        }

        return DefaultCompanionChannelManifestUrl;
    }

    private static void WriteStatus(string statusPath, UpdateStatus status) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(statusPath) ?? AppContext.BaseDirectory);
            File.WriteAllText(statusPath, JsonSerializer.Serialize(status, JsonOptions));
        } catch {
            // Status is best effort.
        }
    }

    private static UpdateStatus ReadStatus(string statusPath) {
        try {
            if (File.Exists(statusPath))
                return JsonSerializer.Deserialize<UpdateStatus>(File.ReadAllText(statusPath), JsonOptions) ?? new UpdateStatus();
        } catch {
        }
        return new UpdateStatus();
    }

    private static void TryDelete(string path) {
        try {
            if (File.Exists(path))
                File.Delete(path);
        } catch {
        }
    }

    private static Process? TryAttachParentProcess(int parentProcessId, CancellationTokenSource cancellation) {
        if (parentProcessId <= 0)
            return null;

        try {
            Process parent = Process.GetProcessById(parentProcessId);
            if (parent.HasExited) {
                parent.Dispose();
                cancellation.Cancel();
                return null;
            }

            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) => {
                try { cancellation.Cancel(); } catch { }
            };
            return parent;
        } catch {
            cancellation.Cancel();
            return null;
        }
    }
}

internal sealed record UpdateOptions {
    public bool CheckOnly { get; init; } = true;
    public bool Force { get; init; }
    public bool Watch { get; init; }
    public bool StartupCheck { get; init; }
    public bool AutoApplyMissingFiles { get; init; }
    public bool DiffOnly { get; init; }
    public bool ShowHelp { get; init; }
    public bool NoCloseTarget { get; init; }
    public int ParentProcessId { get; init; }
    public TimeSpan WatchInterval { get; init; } = TimeSpan.FromMinutes(1);
    public string ManifestUrl { get; init; } = "https://socketjack.com/Update/meta";
    public string CompanionManifestUrl { get; init; } = "";
    public bool SkipCompanionUpdate { get; init; }
    public string TargetDirectory { get; init; } = AppContext.BaseDirectory;
    public string StatusPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JackLLM", "updater-status.json");
    public string ConfigPath { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JackLLM", "updater-config.json");

    public static UpdateOptions Parse(string[] args) {
        var options = new UpdateOptions();
        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (arg.ToLowerInvariant()) {
                case "-?":
                case "--help":
                case "/?":
                    options = options with { ShowHelp = true };
                    break;
                case "--watch":
                    options = options with { Watch = true };
                    break;
                case "--startup-check":
                    options = options with { StartupCheck = true };
                    break;
                case "--check":
                    options = options with { CheckOnly = true };
                    break;
                case "--diff":
                case "--download-diff":
                    options = options with { DiffOnly = true, CheckOnly = true };
                    break;
                case "--force":
                case "--apply":
                    options = options with { Force = true, CheckOnly = false };
                    break;
                case "--no-close-target":
                case "--repair-running-install":
                    options = options with { NoCloseTarget = true };
                    break;
                case "--target":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { TargetDirectory = Path.GetFullPath(next) };
                        i++;
                    }
                    break;
                case "--manifest":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { ManifestUrl = next };
                        i++;
                    }
                    break;
                case "--companion-manifest":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { CompanionManifestUrl = next };
                        i++;
                    }
                    break;
                case "--no-companion-update":
                    options = options with { SkipCompanionUpdate = true };
                    break;
                case "--base-url":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        string baseUrl = next.TrimEnd('/') + "/";
                        options = options with {
                            ManifestUrl = baseUrl.TrimEnd('/') + "/Update/meta",
                            CompanionManifestUrl = baseUrl.TrimEnd('/') + "/Update/jackllm-companion/meta"
                        };
                        i++;
                    }
                    break;
                case "--status":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { StatusPath = next };
                        i++;
                    }
                    break;
                case "--parent-pid":
                case "--parent-process-id":
                    if (int.TryParse(next, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parentProcessId) && parentProcessId > 0) {
                        options = options with { ParentProcessId = parentProcessId };
                        i++;
                    }
                    break;
                case "--interval-seconds":
                case "--watch-interval-seconds":
                    if (double.TryParse(next, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) {
                        options = options with { WatchInterval = TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600)) };
                        i++;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(options.CompanionManifestUrl))
            options = options with { CompanionManifestUrl = Program.BuildDefaultCompanionManifestUrl(options.ManifestUrl) };
        options = options with { TargetDirectory = Program.ResolveUpdateTargetDirectory(options.TargetDirectory) };
        return options;
    }
}

internal sealed class UpdaterConfig {
    public bool AlwaysUpdate { get; set; }
    public string LastPromptUtc { get; set; } = "";

    public bool ShouldPrompt(DateTimeOffset now) {
        if (!DateTimeOffset.TryParse(LastPromptUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset lastPrompt))
            return true;
        return now - lastPrompt.ToUniversalTime() >= TimeSpan.FromMinutes(15);
    }

    public static UpdaterConfig Load(string path) {
        try {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<UpdaterConfig>(File.ReadAllText(path), ProgramJson.Options) ?? new UpdaterConfig();
        } catch {
        }
        return new UpdaterConfig();
    }

    public void Save(string path) {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, ProgramJson.Options));
    }
}

internal static class ProgramJson {
    public static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

internal sealed class UpdateManifest {
    public bool Available { get; set; }
    public string BaseUrl { get; set; } = "";
    public string GeneratedUtc { get; set; } = "";
    public string Error { get; set; } = "";
    public List<UpdateFile> Files { get; set; } = new();
}

internal sealed class UpdateFile {
    public string Path { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string Hash { get; set; } = "";
    public string HashAlgorithm { get; set; } = "";
    public long Length { get; set; }
    public string LastWriteUtc { get; set; } = "";
    public string Url { get; set; } = "";
}

internal sealed class UpdateFileChange {
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public UpdateFile File { get; private set; } = new();
    public string TargetPath { get; private set; } = "";
    public bool Missing { get; private set; }
    public bool RemoteIsNewer { get; private set; }
    public bool LocalIsNewer { get; private set; }
    public string LocalHash { get; private set; } = "";
    public string RemoteHash { get; private set; } = "";
    public string LocalLastWriteUtc { get; private set; } = "";
    public string RemoteLastWriteUtc { get; private set; } = "";
    public bool ShouldApplyByDefault => Missing || RemoteIsNewer || !LocalIsNewer;

    public static UpdateFileChange ForMissing(UpdateFile file, string targetPath) {
        return new UpdateFileChange {
            File = file,
            TargetPath = targetPath,
            Missing = true,
            RemoteIsNewer = true,
            RemoteHash = ProgramHash.GetPreferredManifestHash(file),
            RemoteLastWriteUtc = file.LastWriteUtc
        };
    }

    public static UpdateFileChange Changed(UpdateFile file, string targetPath, bool force, string localHash, string remoteHash) {
        var change = new UpdateFileChange {
            File = file,
            TargetPath = targetPath,
            LocalHash = localHash,
            RemoteHash = remoteHash,
            LocalLastWriteUtc = System.IO.File.GetLastWriteTimeUtc(targetPath).ToString("O", CultureInfo.InvariantCulture),
            RemoteLastWriteUtc = file.LastWriteUtc
        };

        if (!force &&
            DateTimeOffset.TryParse(file.LastWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset remoteTimestamp)) {
            DateTimeOffset localTimestamp = new DateTimeOffset(System.IO.File.GetLastWriteTimeUtc(targetPath), TimeSpan.Zero);
            DateTimeOffset remoteUtc = remoteTimestamp.ToUniversalTime();
            if (remoteUtc - localTimestamp > TimestampTolerance)
                change.RemoteIsNewer = true;
            else if (localTimestamp - remoteUtc > TimestampTolerance)
                change.LocalIsNewer = true;
        }

        return change;
    }
}

internal static class ProgramHash {
    public static string GetPreferredManifestHash(UpdateFile file) {
        if (file.HashAlgorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        if (!string.IsNullOrWhiteSpace(file.Sha256))
            return file.Sha256;
        if (!string.IsNullOrWhiteSpace(file.Hash) && file.Hash.Length == 64)
            return file.Hash;
        return "";
    }
}

public sealed class UpdateDiff {
    public string CheckedUtc { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
    public string ManifestGeneratedUtc { get; set; } = "";
    public bool HasError { get; set; }
    public int TotalFiles { get; set; }
    public int ChangedFiles { get; set; }
    public int MissingFiles { get; set; }
    public int RemoteNewerFiles { get; set; }
    public int LocalNewerFiles { get; set; }
    public string Message { get; set; } = "";
    public List<UpdateDiffFile> Files { get; set; } = new();
}

public sealed class UpdateDiffFile {
    public string Path { get; set; } = "";
    public bool Missing { get; set; }
    public bool RemoteIsNewer { get; set; }
    public bool LocalIsNewer { get; set; }
    public string Recommendation { get; set; } = "";
    public string LocalHash { get; set; } = "";
    public string RemoteHash { get; set; } = "";
    public string LocalLastWriteUtc { get; set; } = "";
    public string RemoteLastWriteUtc { get; set; } = "";

    internal static UpdateDiffFile FromChange(UpdateFileChange change) {
        string recommendation = change.Missing || change.RemoteIsNewer
            ? "download"
            : change.LocalIsNewer
                ? "keep-local-newer"
                : "download-changed";
        return new UpdateDiffFile {
            Path = change.File.Path,
            Missing = change.Missing,
            RemoteIsNewer = change.RemoteIsNewer,
            LocalIsNewer = change.LocalIsNewer,
            Recommendation = recommendation,
            LocalHash = change.LocalHash,
            RemoteHash = change.RemoteHash,
            LocalLastWriteUtc = change.LocalLastWriteUtc,
            RemoteLastWriteUtc = change.RemoteLastWriteUtc
        };
    }
}

internal sealed class UpdateStatus {
    public string CheckedUtc { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string ManifestGeneratedUtc { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public bool HasError { get; set; }
    public int TotalFiles { get; set; }
    public int ChangedFiles { get; set; }
    public int MissingFiles { get; set; }
    public int RemoteNewerFiles { get; set; }
    public int LocalNewerFiles { get; set; }
    public int SkippedLocalNewerFiles { get; set; }
    public int UpdatedFiles { get; set; }
    public int PendingFiles { get; set; }
    public string Message { get; set; } = "";
    public bool CompanionUpdateAvailable { get; set; }
    public bool CompanionIsDownloading { get; set; }
    public bool CompanionHasError { get; set; }
    public int CompanionTotalFiles { get; set; }
    public int CompanionChangedFiles { get; set; }
    public int CompanionMissingFiles { get; set; }
    public int CompanionUpdatedFiles { get; set; }
    public int CompanionPendingFiles { get; set; }
    public string CompanionTargetDirectory { get; set; } = "";
    public string CompanionCurrentFile { get; set; } = "";
    public string CompanionMessage { get; set; } = "";

    public static UpdateStatus Begin(string targetDirectory) {
        return new UpdateStatus {
            CheckedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            TargetDirectory = targetDirectory,
            CompanionTargetDirectory = Path.Combine(targetDirectory, "Companion"),
            Message = "Checking for updates..."
        };
    }

    public static UpdateStatus Failed(string targetDirectory, string message) {
        UpdateStatus status = Begin(targetDirectory);
        status.HasError = true;
        status.Message = message;
        return status;
    }
}

internal sealed class PendingReplacement {
    public string SourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string LastWriteUtc { get; set; } = "";
}

internal sealed record RelatedProcessCandidate(int ProcessId, ProcessRestartInfo RestartInfo);

internal sealed record ProcessRestartInfo(string ExecutablePath, string Arguments, string WorkingDirectory, bool StartHidden);

internal sealed class RelatedProcessRestartScope {
    public static readonly RelatedProcessRestartScope Empty = new(new List<ProcessRestartInfo>());

    private readonly List<ProcessRestartInfo> _processesToRestart;

    public RelatedProcessRestartScope(List<ProcessRestartInfo> processesToRestart) {
        _processesToRestart = processesToRestart;
    }

    public bool HasProcessesToRestart => _processesToRestart.Count > 0;

    public void RestartClosedProcesses() {
        foreach (ProcessRestartInfo process in _processesToRestart) {
            try {
                if (!File.Exists(process.ExecutablePath) || Program.IsProcessRunningFromPath(process.ExecutablePath))
                    continue;

                var startInfo = new ProcessStartInfo(process.ExecutablePath, process.Arguments) {
                    UseShellExecute = true,
                    WorkingDirectory = string.IsNullOrWhiteSpace(process.WorkingDirectory)
                        ? Path.GetDirectoryName(process.ExecutablePath) ?? AppContext.BaseDirectory
                        : process.WorkingDirectory
                };
                if (process.StartHidden)
                    startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                Process.Start(startInfo);
            } catch {
            }
        }
    }
}

internal sealed class UpdateApplyResult {
    public bool Applied { get; set; }
    public string PendingSourcePath { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string LastWriteUtc { get; set; } = "";
}

internal sealed class StartupSplashWindow : Window {
    public StartupSplashWindow() {
        Width = 380;
        Height = 138;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Content = new System.Windows.Controls.Border {
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(115, 185, 220, 255)),
            Background = new SolidColorBrush(Color.FromArgb(185, 18, 28, 42)),
            Padding = new Thickness(22),
            Effect = new System.Windows.Media.Effects.DropShadowEffect {
                BlurRadius = 28,
                ShadowDepth = 0,
                Opacity = 0.36,
                Color = Color.FromRgb(0, 0, 0)
            },
            Child = new System.Windows.Controls.StackPanel {
                Children = {
                    new System.Windows.Controls.TextBlock {
                        Text = "Checking JackLLM",
                        Foreground = Brushes.White,
                        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                        FontSize = 21,
                        FontWeight = FontWeights.SemiBold
                    },
                    new System.Windows.Controls.TextBlock {
                        Text = "Looking for updates from SocketJack.com",
                        Foreground = new SolidColorBrush(Color.FromRgb(185, 207, 224)),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 13,
                        Margin = new Thickness(0, 8, 0, 14)
                    },
                    new System.Windows.Controls.ProgressBar {
                        IsIndeterminate = true,
                        Height = 4,
                        Foreground = new SolidColorBrush(Color.FromRgb(111, 242, 166)),
                        Background = new SolidColorBrush(Color.FromArgb(75, 255, 255, 255))
                    }
                }
            }
        };
        SourceInitialized += (_, _) => EnableBlur();
    }

    private void EnableBlur() {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        var accent = new AccentPolicy {
            AccentState = 3,
            GradientColor = unchecked((int)0x6618273A)
        };
        int size = Marshal.SizeOf(accent);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData {
                Attribute = 19,
                Data = ptr,
                SizeOfData = size
            };
            _ = SetWindowCompositionAttribute(handle, ref data);
        } finally {
            Marshal.FreeHGlobal(ptr);
        }
    }

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

internal enum UpdatePromptAction {
    Later,
    UpdateNow,
    AlwaysUpdate
}

internal sealed class UpdateAvailableDialog : Window {
    public UpdatePromptAction SelectedAction { get; private set; } = UpdatePromptAction.Later;

    public UpdateAvailableDialog(UpdateStatus status) {
        Title = "JackLLM Update";
        Width = 470;
        Height = 246;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        Background = new SolidColorBrush(Color.FromRgb(24, 32, 44));
        Topmost = true;
        Content = new System.Windows.Controls.Border {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(92, 122, 150)),
            Padding = new Thickness(18),
            Child = new System.Windows.Controls.Grid {
                RowDefinitions = {
                    new System.Windows.Controls.RowDefinition { Height = GridLength.Auto },
                    new System.Windows.Controls.RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new System.Windows.Controls.RowDefinition { Height = GridLength.Auto }
                },
                Children = {
                    new System.Windows.Controls.TextBlock {
                        Text = "Update available",
                        Foreground = Brushes.White,
                        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                        FontSize = 22,
                        FontWeight = FontWeights.SemiBold
                    },
                    CreateBodyText(status),
                    CreateButtons()
                }
            }
        };
    }

    private static System.Windows.Controls.TextBlock CreateBodyText(UpdateStatus status) {
        string detail = string.IsNullOrWhiteSpace(status.Message)
            ? "JackLLM has files that do not match SocketJack.com."
            : status.Message;
        var text = new System.Windows.Controls.TextBlock {
            Text = detail + " Update now to keep the local GUI file structure in sync with SocketJack /update/meta.",
            Foreground = new SolidColorBrush(Color.FromRgb(199, 215, 232)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 46, 0, 0),
            FontSize = 13
        };
        System.Windows.Controls.Grid.SetRow(text, 1);
        return text;
    }

    private System.Windows.Controls.StackPanel CreateButtons() {
        var panel = new System.Windows.Controls.StackPanel {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(panel, 2);

        var later = CreateButton("Later", new SolidColorBrush(Color.FromRgb(48, 60, 78)));
        later.Click += (_, _) => {
            SelectedAction = UpdatePromptAction.Later;
            DialogResult = false;
            Close();
        };
        var updateNow = CreateButton("Update now", new SolidColorBrush(Color.FromRgb(37, 99, 235)));
        updateNow.Margin = new Thickness(10, 0, 0, 0);
        updateNow.Click += (_, _) => {
            SelectedAction = UpdatePromptAction.UpdateNow;
            DialogResult = true;
            Close();
        };
        var always = CreateButton("Always update", new SolidColorBrush(Color.FromRgb(31, 138, 78)));
        always.Margin = new Thickness(10, 0, 0, 0);
        always.Click += (_, _) => {
            SelectedAction = UpdatePromptAction.AlwaysUpdate;
            DialogResult = true;
            Close();
        };
        panel.Children.Add(later);
        panel.Children.Add(updateNow);
        panel.Children.Add(always);
        return panel;
    }

    private static System.Windows.Controls.Button CreateButton(string text, Brush background) {
        return new System.Windows.Controls.Button {
            Content = text,
            MinWidth = 106,
            Padding = new Thickness(12, 7, 12, 7),
            Foreground = Brushes.White,
            Background = background,
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
    }
}
