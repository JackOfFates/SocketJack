using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace JackLLMBridgeInstaller;

internal static class Program {
    private const string ProductName = "JackLLM";
    private const string Publisher = "SocketJack";
    private const string MainExecutableName = "JackLLM.exe";
    private const string StartupValueName = "JackLLM";
    private const string DefaultManifestUrl = "https://socketjack.com/update/meta";
    private const string DefaultFileBaseUrl = "https://socketjack.com/update/";
    private const string ProductRegistryPath = @"Software\SocketJack\JackLLM";
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppPathsRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths\JackLLM.exe";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\JackLLM";
    private const int MaxParallelDownloads = 6;
    private const int DownloadBufferSize = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [STAThread]
    public static async Task<int> Main(string[] args) {
        InstallerOptions options = InstallerOptions.Parse(args);
        if (options.ShowHelp) {
            PrintHelp();
            return 0;
        }

        if (options.DiffOnly) {
            DownloadDiffResult diff = await DownloadDiff(args);
            Console.WriteLine(JsonSerializer.Serialize(diff, JsonOptions));
            return diff.HasError ? 1 : 0;
        }

        if (!IsAdministrator())
            return RestartElevated(args);

        try {
            if (options.Uninstall) {
                Uninstall(options);
                Console.WriteLine(ProductName + " was removed.");
                return 0;
            }

            await InstallAsync(options);
            Console.WriteLine(ProductName + " installation complete.");
            if (!options.Quiet)
                ShowMessage(ProductName + " installation complete.", ProductName + " Installer", MessageBoxIcon.Information);
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("Install failed: " + ex.Message);
            if (!options.Quiet)
                ShowMessage("Install failed: " + ex.Message, ProductName + " Installer", MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void PrintHelp() {
        Console.WriteLine(ProductName + " Installer");
        Console.WriteLine("--manifest <url>     Update manifest URL. Default: " + DefaultManifestUrl);
        Console.WriteLine("--install-dir <path> Install directory. Default: %ProgramFiles%\\JackLLM");
        Console.WriteLine("--no-startup         Do not register Windows startup.");
        Console.WriteLine("--launch             Start the app hidden after install.");
        Console.WriteLine("--quiet              Do not show completion or failure dialogs.");
        Console.WriteLine("--diff               Print the files that would be downloaded.");
        Console.WriteLine("--force              Overwrite newer local files and allow reverting.");
        Console.WriteLine("--uninstall          Remove registry entries, shortcut, and install files.");
    }

    private static async Task InstallAsync(InstallerOptions options) {
        options = options with {
            ManifestUrl = RequireSecureSocketJackUrl(options.ManifestUrl, "manifest"),
            InstallDirectory = Path.GetFullPath(options.InstallDirectory)
        };

        Console.WriteLine("Downloading manifest from " + options.ManifestUrl);
        using var http = new HttpClient {
            Timeout = TimeSpan.FromMinutes(5)
        };
        UpdateManifest manifest = await DownloadManifestAsync(http, options.ManifestUrl);
        if (!manifest.Available)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(manifest.Error) ? "Update manifest is unavailable." : manifest.Error);
        if (manifest.Files.Count == 0)
            throw new InvalidOperationException("Update manifest did not contain any files.");
        if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
            manifest.BaseUrl = BuildDefaultUpdateBaseUrl(options.ManifestUrl);

        List<DownloadFilePlan> downloadPlan = BuildDownloadPlan(options.InstallDirectory, manifest, options.ForceOverwrite);
        List<DownloadFilePlan> filesToDownload = downloadPlan.Where(file => file.ShouldDownload).ToList();
        foreach (DownloadFilePlan plan in downloadPlan.Where(file => file.LocalIsNewer && !file.ShouldDownload))
            Console.WriteLine("Keeping newer local file " + plan.File.Path + ". Use --force to replace it.");

        string tempRoot = Path.Combine(Path.GetTempPath(), "JackLLMBridgeInstaller-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try {
            await DownloadFilesAsync(http, manifest, filesToDownload, tempRoot);

            Directory.CreateDirectory(options.InstallDirectory);
            foreach (DownloadFilePlan plan in filesToDownload) {
                UpdateFile file = plan.File;
                string sourcePath = Path.Combine(tempRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                string destinationPath = Path.Combine(options.InstallDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? options.InstallDirectory);
                File.Copy(sourcePath, destinationPath, overwrite: true);
                ApplyRemoteLastWriteTime(destinationPath, file);
            }

            string mainExe = Path.Combine(options.InstallDirectory, MainExecutableName);
            if (!File.Exists(mainExe))
                throw new InvalidOperationException(MainExecutableName + " was not installed. Verify the update folder contains the JackLLM executable.");

            WriteDefaultGuiSettings(options);
            WriteRegistry(options, mainExe);
            CreateStartMenuShortcut(mainExe, options.InstallDirectory);

            if (options.LaunchAfterInstall)
                Process.Start(new ProcessStartInfo(mainExe, "--tray") { UseShellExecute = true, WorkingDirectory = options.InstallDirectory });
        } finally {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static async Task<UpdateManifest> DownloadManifestAsync(HttpClient http, string manifestUrl) {
        using var request = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        request.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        if (body.TrimStart().StartsWith("<", StringComparison.Ordinal))
            throw new InvalidOperationException("The manifest URL returned HTML. The installer needs the JSON update manifest.");
        return JsonSerializer.Deserialize<UpdateManifest>(body, JsonOptions) ?? new UpdateManifest { Available = false, Error = "Manifest JSON was empty." };
    }

    public static async Task<DownloadDiffResult> DownloadDiff(string[] args) {
        InstallerOptions parsedOptions = InstallerOptions.Parse(args);
        InstallerOptions options = parsedOptions with {
            ManifestUrl = RequireSecureSocketJackUrl(parsedOptions.ManifestUrl, "manifest"),
            InstallDirectory = Path.GetFullPath(parsedOptions.InstallDirectory)
        };
        var diff = new DownloadDiffResult {
            TargetDirectory = options.InstallDirectory,
            ManifestUrl = options.ManifestUrl,
            CheckedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        try {
            using var http = new HttpClient {
                Timeout = TimeSpan.FromSeconds(30)
            };
            UpdateManifest manifest = await DownloadManifestAsync(http, options.ManifestUrl);
            if (!manifest.Available) {
                diff.HasError = true;
                diff.Message = string.IsNullOrWhiteSpace(manifest.Error) ? "Update metadata unavailable." : manifest.Error;
                return diff;
            }
            if (string.IsNullOrWhiteSpace(manifest.BaseUrl))
                manifest.BaseUrl = BuildDefaultUpdateBaseUrl(options.ManifestUrl);

            List<DownloadFilePlan> changed = BuildDownloadPlan(options.InstallDirectory, manifest, options.ForceOverwrite);
            diff.ManifestGeneratedUtc = manifest.GeneratedUtc;
            diff.TotalFiles = manifest.Files.Count;
            diff.Files = changed.Select(DownloadDiffFile.FromPlan).ToList();
            diff.ChangedFiles = diff.Files.Count;
            diff.FilesToDownload = diff.Files.Count(file => file.ShouldDownload);
            diff.MissingFiles = diff.Files.Count(file => file.Missing);
            diff.RemoteNewerFiles = diff.Files.Count(file => file.RemoteIsNewer);
            diff.LocalNewerFiles = diff.Files.Count(file => file.LocalIsNewer);
            diff.Message = BuildDownloadSummary(changed, options.ForceOverwrite);
            return diff;
        } catch (Exception ex) {
            diff.HasError = true;
            diff.Message = "Update diff failed: " + ex.Message;
            return diff;
        }
    }

    private static List<DownloadFilePlan> BuildDownloadPlan(string installDirectory, UpdateManifest manifest, bool forceOverwrite) {
        var changed = new List<DownloadFilePlan>();
        foreach (UpdateFile file in manifest.Files) {
            if (!IsSafeRelativePath(file.Path))
                throw new InvalidOperationException("Unsafe file path in manifest: " + file.Path);
            if (!IsInstallPayloadFile(file.Path))
                continue;

            string targetPath = Path.Combine(installDirectory, file.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(targetPath)) {
                changed.Add(DownloadFilePlan.ForMissing(file, targetPath));
                continue;
            }

            string remoteHash = GetPreferredManifestHash(file);
            string localHash = GetPreferredFileHash(targetPath, file);
            bool hashMatches = !string.IsNullOrWhiteSpace(remoteHash) &&
                               string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase);
            if (hashMatches && !forceOverwrite)
                continue;

            changed.Add(DownloadFilePlan.Changed(file, targetPath, forceOverwrite, localHash, remoteHash));
        }
        return changed;
    }

    private static string BuildDownloadSummary(List<DownloadFilePlan> changed, bool forceOverwrite) {
        if (changed.Count == 0)
            return ProductName + " files are up to date.";

        int localNewer = changed.Count(file => file.LocalIsNewer);
        int remoteNewer = changed.Count(file => file.RemoteIsNewer);
        int missing = changed.Count(file => file.Missing);
        int toDownload = changed.Count(file => file.ShouldDownload);
        if (!forceOverwrite && toDownload == 0 && localNewer > 0)
            return localNewer.ToString(CultureInfo.InvariantCulture) + " local file(s) are newer than SocketJack update metadata. Keeping the newer local copy unless you force a revert.";

        var parts = new List<string>();
        if (missing > 0)
            parts.Add(missing.ToString(CultureInfo.InvariantCulture) + " missing");
        if (remoteNewer > 0)
            parts.Add(remoteNewer.ToString(CultureInfo.InvariantCulture) + " newer on SocketJack update");
        if (localNewer > 0 && !forceOverwrite)
            parts.Add(localNewer.ToString(CultureInfo.InvariantCulture) + " newer locally and kept");

        return toDownload.ToString(CultureInfo.InvariantCulture) + " file(s) will be downloaded" +
               (parts.Count == 0 ? "." : " (" + string.Join(", ", parts) + ").");
    }

    private static async Task DownloadFilesAsync(HttpClient http, UpdateManifest manifest, IReadOnlyList<DownloadFilePlan> filesToDownload, string tempRoot) {
        if (filesToDownload.Count == 0)
            return;

        using var gate = new SemaphoreSlim(Math.Min(MaxParallelDownloads, filesToDownload.Count));
        object consoleLock = new();
        int completed = 0;
        List<Task> tasks = filesToDownload.Select(async plan => {
            await gate.WaitAsync().ConfigureAwait(false);
            try {
                UpdateFile file = plan.File;
                string fileUrl = string.IsNullOrWhiteSpace(file.Url)
                    ? CombineUrl(string.IsNullOrWhiteSpace(manifest.BaseUrl) ? DefaultFileBaseUrl : manifest.BaseUrl, file.Path)
                    : file.Url;
                fileUrl = RequireSecureSocketJackUrl(fileUrl, file.Path);
                string targetPath = Path.Combine(tempRoot, file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? tempRoot);

                lock (consoleLock)
                    Console.WriteLine("Downloading " + file.Path);
                await DownloadFileAsync(http, fileUrl, targetPath).ConfigureAwait(false);
                ValidateHash(targetPath, file);
                ApplyRemoteLastWriteTime(targetPath, file);

                int done = Interlocked.Increment(ref completed);
                lock (consoleLock)
                    Console.WriteLine("Downloaded " + file.Path + " (" + done.ToString(CultureInfo.InvariantCulture) + "/" + filesToDownload.Count.ToString(CultureInfo.InvariantCulture) + ")");
            } finally {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task DownloadFileAsync(HttpClient http, string fileUrl, string targetPath) {
        using HttpResponseMessage response = await http.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await using Stream source = await response.Content.ReadAsStreamAsync();
        await using FileStream destination = new(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, DownloadBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, DownloadBufferSize);
    }

    private static void ValidateHash(string path, UpdateFile file) {
        string manifestHash = GetPreferredManifestHash(file);
        if (string.IsNullOrWhiteSpace(manifestHash))
            throw new InvalidOperationException("Manifest is missing a hash for " + file.Path + ".");

        string fileHash = GetPreferredFileHash(path, file);
        if (!string.Equals(fileHash, manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hash mismatch for " + file.Path + ".");
    }

    private static void WriteDefaultGuiSettings(InstallerOptions options) {
        string settingsPath = Path.Combine(options.InstallDirectory, "JackLLM.settings.json");
        JsonObject settings = new();
        if (File.Exists(settingsPath)) {
            try {
                settings = JsonNode.Parse(File.ReadAllText(settingsPath)) as JsonObject ?? new JsonObject();
            } catch {
                settings = new JsonObject();
            }
        }

        settings["StartProxyOnOpen"] = true;
        settings["WindowsStartupEnabled"] = options.RegisterStartup;
        settings["HideToTrayOnStartup"] = true;
        settings["MasterServerUrl"] = "https://socketjack.com";
        File.WriteAllText(settingsPath, settings.ToJsonString(JsonOptions));
    }

    private static void WriteRegistry(InstallerOptions options, string mainExe) {
        using (RegistryKey productKey = Registry.LocalMachine.CreateSubKey(ProductRegistryPath)) {
            productKey.SetValue("DisplayName", ProductName, RegistryValueKind.String);
            productKey.SetValue("InstallLocation", options.InstallDirectory, RegistryValueKind.String);
            productKey.SetValue("ManifestUrl", options.ManifestUrl, RegistryValueKind.String);
            productKey.SetValue("Publisher", Publisher, RegistryValueKind.String);
            productKey.SetValue("InstallDate", DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
        }

        using (RegistryKey appPathKey = Registry.LocalMachine.CreateSubKey(AppPathsRegistryPath)) {
            appPathKey.SetValue("", mainExe, RegistryValueKind.String);
            appPathKey.SetValue("Path", options.InstallDirectory, RegistryValueKind.String);
        }

        using (RegistryKey uninstallKey = Registry.LocalMachine.CreateSubKey(UninstallRegistryPath)) {
            string installerPath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
            uninstallKey.SetValue("DisplayName", ProductName, RegistryValueKind.String);
            uninstallKey.SetValue("DisplayIcon", mainExe, RegistryValueKind.String);
            uninstallKey.SetValue("InstallLocation", options.InstallDirectory, RegistryValueKind.String);
            uninstallKey.SetValue("Publisher", Publisher, RegistryValueKind.String);
            uninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            if (!string.IsNullOrWhiteSpace(installerPath))
                uninstallKey.SetValue("UninstallString", Quote(installerPath) + " --uninstall --install-dir " + Quote(options.InstallDirectory), RegistryValueKind.String);
        }

        using RegistryKey runKey = Registry.LocalMachine.CreateSubKey(RunRegistryPath);
        if (options.RegisterStartup)
            runKey.SetValue(StartupValueName, Quote(mainExe) + " --tray", RegistryValueKind.String);
        else
            runKey.DeleteValue(StartupValueName, throwOnMissingValue: false);
    }

    private static void CreateStartMenuShortcut(string mainExe, string installDirectory) {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        if (string.IsNullOrWhiteSpace(programs))
            programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs");
        Directory.CreateDirectory(programs);
        string shortcutPath = Path.Combine(programs, ProductName + ".lnk");

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            throw new InvalidOperationException("Windows Script Host is unavailable; cannot create Start Menu shortcut.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic shortcut = shell.CreateShortcut(shortcutPath);
        shortcut.TargetPath = mainExe;
        shortcut.WorkingDirectory = installDirectory;
        shortcut.Description = ProductName;
        shortcut.IconLocation = mainExe + ",0";
        shortcut.Save();
    }

    private static void Uninstall(InstallerOptions options) {
        string installDirectory = Path.GetFullPath(options.InstallDirectory);
        using (RegistryKey? runKey = Registry.LocalMachine.OpenSubKey(RunRegistryPath, writable: true))
            runKey?.DeleteValue(StartupValueName, throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(ProductRegistryPath, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(AppPathsRegistryPath, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);

        string shortcutPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName + ".lnk");
        if (File.Exists(shortcutPath))
            File.Delete(shortcutPath);

        if (CanRemoveInstallDirectory(installDirectory) && Directory.Exists(installDirectory))
            Directory.Delete(installDirectory, recursive: true);
    }

    private static bool CanRemoveInstallDirectory(string path) {
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        return !string.IsNullOrWhiteSpace(path) &&
               path.StartsWith(programFiles.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
               (path.EndsWith("JackLLM", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("LMVS Bridge", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSafeRelativePath(string path) {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        path = path.Replace('\\', '/');
        return !path.StartsWith("/", StringComparison.Ordinal) &&
               !path.Contains("../", StringComparison.Ordinal) &&
               !path.Equals("..", StringComparison.Ordinal);
    }

    private static bool IsInstallPayloadFile(string relativePath) {
        return IsSafeRelativePath(relativePath) &&
               !IsInstallerMetadataOnlyFile(relativePath) &&
               !IsBlockedRuntimeDataPath(relativePath);
    }

    private static string RequireSecureSocketJackUrl(string url, string label) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
            throw new InvalidOperationException("Invalid URL for " + label + ": " + url);

        if (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            uri.Host.Equals("socketjack.com", StringComparison.OrdinalIgnoreCase)) {
            var builder = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 };
            return builder.Uri.ToString();
        }

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installer download URL for " + label + " must use HTTPS.");

        return uri.ToString();
    }

    private static string CombineUrl(string baseUrl, string relativePath) {
        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultFileBaseUrl : baseUrl.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            baseUrl += "/";
        return baseUrl + (relativePath ?? "").TrimStart('/').Replace("\\", "/");
    }

    private static string BuildDefaultUpdateBaseUrl(string manifestUrl) {
        if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? uri))
            return DefaultFileBaseUrl;

        var builder = new UriBuilder(uri) {
            Query = "",
            Fragment = ""
        };
        string path = builder.Path.TrimEnd('/');
        if (path.EndsWith("/meta", StringComparison.OrdinalIgnoreCase))
            path = path[..^5];
        else if (path.EndsWith("/manifest", StringComparison.OrdinalIgnoreCase))
            path = path[..^9];
        else if (path.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
            path = path[..^14];
        if (string.IsNullOrWhiteSpace(path) || path.Equals("/", StringComparison.Ordinal))
            path = "/update";
        builder.Path = path.TrimEnd('/') + "/";
        return builder.Uri.ToString();
    }

    private static bool IsInstallerMetadataOnlyFile(string relativePath) {
        string fileName = Path.GetFileName(relativePath ?? "");
        return fileName.Equals("JackLLM-Setup.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM-Setup.msi", StringComparison.OrdinalIgnoreCase) ||
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

    private static string GetPreferredManifestHash(UpdateFile file) {
        if (file.HashAlgorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        if (!string.IsNullOrWhiteSpace(file.Sha256))
            return file.Sha256;
        if (!string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        return file.Md5;
    }

    private static string GetPreferredFileHash(string path, UpdateFile file) {
        if (!string.IsNullOrWhiteSpace(file.Sha256) ||
            file.HashAlgorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(file.Hash) && file.Hash.Length == 64))
            return GetFileSha256(path);
        return GetFileMd5(path);
    }

    private static string GetFileSha256(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string GetFileMd5(string path) {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(MD5.HashData(stream)).ToLowerInvariant();
    }

    private static DateTimeOffset? TryParseRemoteLastWrite(UpdateFile file) {
        if (DateTimeOffset.TryParse(file.LastWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return parsed.ToUniversalTime();
        return null;
    }

    private static void ApplyRemoteLastWriteTime(string path, UpdateFile file) {
        DateTimeOffset? lastWriteUtc = TryParseRemoteLastWrite(file);
        if (!lastWriteUtc.HasValue)
            return;
        try {
            File.SetLastWriteTimeUtc(path, lastWriteUtc.Value.UtcDateTime);
        } catch {
        }
    }

    private static bool IsAdministrator() {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static int RestartElevated(string[] args) {
        string executable = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(executable)) {
            Console.Error.WriteLine("Administrator rights are required.");
            return 1;
        }

        try {
            Process.Start(new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote))) {
                UseShellExecute = true,
                Verb = "runas"
            });
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine("Administrator rights are required: " + ex.Message);
            return 1;
        }
    }

    private static string Quote(string value) {
        return "\"" + (value ?? "").Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void ShowMessage(string text, string caption, MessageBoxIcon icon) {
        _ = MessageBoxW(IntPtr.Zero, text, caption, (uint)MessageBoxType.Ok | (uint)icon);
    }

    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        } catch {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}

internal enum MessageBoxType : uint {
    Ok = 0x00000000
}

internal enum MessageBoxIcon : uint {
    Error = 0x00000010,
    Information = 0x00000040
}

internal sealed record InstallerOptions {
    public string ManifestUrl { get; init; } = "https://socketjack.com/update/meta";
    public string InstallDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JackLLM");
    public bool RegisterStartup { get; init; } = true;
    public bool LaunchAfterInstall { get; init; }
    public bool Uninstall { get; init; }
    public bool Quiet { get; init; }
    public bool DiffOnly { get; init; }
    public bool ForceOverwrite { get; init; }
    public bool ShowHelp { get; init; }

    public static InstallerOptions Parse(string[] args) {
        var options = new InstallerOptions();
        for (int i = 0; i < args.Length; i++) {
            string arg = args[i];
            string next = i + 1 < args.Length ? args[i + 1] : "";
            switch (arg.ToLowerInvariant()) {
                case "-?":
                case "/?":
                case "--help":
                    options = options with { ShowHelp = true };
                    break;
                case "--manifest":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { ManifestUrl = next };
                        i++;
                    }
                    break;
                case "--install-dir":
                    if (!string.IsNullOrWhiteSpace(next)) {
                        options = options with { InstallDirectory = next };
                        i++;
                    }
                    break;
                case "--no-startup":
                    options = options with { RegisterStartup = false };
                    break;
                case "--launch":
                    options = options with { LaunchAfterInstall = true };
                    break;
                case "--quiet":
                case "/quiet":
                    options = options with { Quiet = true };
                    break;
                case "--diff":
                case "--download-diff":
                    options = options with { DiffOnly = true };
                    break;
                case "--force":
                case "--revert":
                    options = options with { ForceOverwrite = true };
                    break;
                case "--uninstall":
                    options = options with { Uninstall = true };
                    break;
            }
        }
        return options;
    }
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

internal sealed class DownloadFilePlan {
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);

    public UpdateFile File { get; init; } = new();
    public string TargetPath { get; init; } = "";
    public string LocalHash { get; init; } = "";
    public string RemoteHash { get; init; } = "";
    public bool Missing { get; init; }
    public bool LocalIsNewer { get; init; }
    public bool RemoteIsNewer { get; init; }
    public bool ShouldDownload { get; init; }
    public string Recommendation { get; init; } = "";

    public static DownloadFilePlan ForMissing(UpdateFile file, string targetPath) {
        return new DownloadFilePlan {
            File = file,
            TargetPath = targetPath,
            Missing = true,
            RemoteIsNewer = true,
            ShouldDownload = true,
            RemoteHash = InstallerHash.GetPreferredManifestHash(file),
            Recommendation = "download"
        };
    }

    public static DownloadFilePlan Changed(UpdateFile file, string targetPath, bool forceOverwrite, string localHash, string remoteHash) {
        DateTimeOffset localLastWrite = System.IO.File.GetLastWriteTimeUtc(targetPath);
        DateTimeOffset? remoteLastWrite = InstallerHash.TryParseRemoteLastWrite(file);
        bool localIsNewer = remoteLastWrite.HasValue && localLastWrite > remoteLastWrite.Value.Add(TimestampTolerance);
        bool remoteIsNewer = !remoteLastWrite.HasValue || remoteLastWrite.Value > localLastWrite.Add(TimestampTolerance);
        bool shouldDownload = forceOverwrite || !localIsNewer;
        return new DownloadFilePlan {
            File = file,
            TargetPath = targetPath,
            LocalHash = localHash,
            RemoteHash = remoteHash,
            LocalIsNewer = localIsNewer,
            RemoteIsNewer = remoteIsNewer,
            ShouldDownload = shouldDownload,
            Recommendation = shouldDownload ? "download" : "keep-local"
        };
    }
}

public sealed class DownloadDiffResult {
    public bool HasError { get; set; }
    public string Message { get; set; } = "";
    public string TargetDirectory { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
    public string ManifestGeneratedUtc { get; set; } = "";
    public string CheckedUtc { get; set; } = "";
    public int TotalFiles { get; set; }
    public int ChangedFiles { get; set; }
    public int FilesToDownload { get; set; }
    public int MissingFiles { get; set; }
    public int RemoteNewerFiles { get; set; }
    public int LocalNewerFiles { get; set; }
    public List<DownloadDiffFile> Files { get; set; } = new();
}

public sealed class DownloadDiffFile {
    public string Path { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string LocalHash { get; set; } = "";
    public string RemoteHash { get; set; } = "";
    public string LastWriteUtc { get; set; } = "";
    public bool Missing { get; set; }
    public bool LocalIsNewer { get; set; }
    public bool RemoteIsNewer { get; set; }
    public bool ShouldDownload { get; set; }
    public string Recommendation { get; set; } = "";

    internal static DownloadDiffFile FromPlan(DownloadFilePlan plan) {
        return new DownloadDiffFile {
            Path = plan.File.Path,
            TargetPath = plan.TargetPath,
            LocalHash = plan.LocalHash,
            RemoteHash = plan.RemoteHash,
            LastWriteUtc = plan.File.LastWriteUtc,
            Missing = plan.Missing,
            LocalIsNewer = plan.LocalIsNewer,
            RemoteIsNewer = plan.RemoteIsNewer,
            ShouldDownload = plan.ShouldDownload,
            Recommendation = plan.Recommendation
        };
    }
}

internal static class InstallerHash {
    public static string GetPreferredManifestHash(UpdateFile file) {
        if (file.HashAlgorithm.Equals("sha256", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        if (!string.IsNullOrWhiteSpace(file.Sha256))
            return file.Sha256;
        if (!string.IsNullOrWhiteSpace(file.Hash))
            return file.Hash;
        return file.Md5;
    }

    public static DateTimeOffset? TryParseRemoteLastWrite(UpdateFile file) {
        if (DateTimeOffset.TryParse(file.LastWriteUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed))
            return parsed.ToUniversalTime();
        return null;
    }
}
