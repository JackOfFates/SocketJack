using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Net.Database;

namespace JackLLM;

internal static class JackLlmUserData {
    public static string Root {
        get {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local))
                local = Path.GetTempPath();
            return Path.Combine(local, "SocketJack", "JackLLM");
        }
    }

    public static string ChatDataRoot => Path.Combine(Root, "Data", "Chat");
    public static string SessionFilesRoot => Path.Combine(Root, "SessionFiles");
    public static string RemoteSessionsRoot => Path.Combine(Root, "RemoteSessions");
    public static string ModelsRoot => Root;
    public static string LogsRoot => Path.Combine(Root, "Logs");
    public static string BackupsRoot => Path.Combine(Root, "Backups");
    public static string MigrationSourcesPath => Path.Combine(Root, "migration-sources.txt");

    public static void EnsureLayout() {
        foreach (string path in new[] {
            Root, ChatDataRoot, SessionFilesRoot, RemoteSessionsRoot,
            Path.Combine(Root, "Models"), Path.Combine(Root, "CompleteModels"),
            LogsRoot, BackupsRoot
        }) {
            Directory.CreateDirectory(path);
        }
    }

    public static void AddMigrationSource(string sourceRoot) {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return;
        EnsureLayout();
        string full = Path.GetFullPath(sourceRoot);
        var sources = File.Exists(MigrationSourcesPath)
            ? File.ReadAllLines(MigrationSourcesPath).Where(line => !string.IsNullOrWhiteSpace(line)).ToList()
            : new List<string>();
        if (!sources.Any(item => PathsEqual(item, full))) {
            sources.Add(full);
            File.WriteAllLines(MigrationSourcesPath, sources, new UTF8Encoding(false));
        }
    }

    public static async Task MigrateLegacyDataAsync(CancellationToken cancellationToken) {
        EnsureLayout();
        string destinationDatabase = Path.Combine(ChatDataRoot, "SocketJackDatabase.json");
        List<string> roots = DiscoverLegacyRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!File.Exists(destinationDatabase)) {
            string? bestDatabase = roots
                .SelectMany(GetDatabaseCandidates)
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.Length)
                .ThenByDescending(info => info.LastWriteTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(bestDatabase)) {
                BackupPersistenceSet(bestDatabase);
                var source = new DataServer(0, "JackLLM migration", loadFromDisk: false) {
                    DataPath = bestDatabase
                };
                await source.LoadAsync(cancellationToken).ConfigureAwait(false);
                int tableCount = source.Databases.Values.Sum(database => database?.Tables?.Count ?? 0);
                if (tableCount == 0)
                    throw new InvalidDataException("The legacy JackLLM database could not be decrypted and validated at " + bestDatabase + ". The original and backup were preserved.");
                await source.SaveReencryptedCopyAsync(destinationDatabase, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (string root in roots) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string sourceFilesRoot in GetSessionFileCandidates(root))
                MergeDirectory(sourceFilesRoot, SessionFilesRoot);
            foreach (string remoteRoot in GetRemoteSessionCandidates(root))
                MergeDirectory(remoteRoot, RemoteSessionsRoot);
        }
    }

    public static bool MigrateModelsFrom(string? configuredRoot) {
        if (string.IsNullOrWhiteSpace(configuredRoot))
            return false;
        string sourceRoot;
        try { sourceRoot = Path.GetFullPath(configuredRoot); } catch { return false; }
        if (PathsEqual(sourceRoot, ModelsRoot) || !ShouldMigrateModelRoot(sourceRoot))
            return false;

        bool moved = false;
        foreach (string name in new[] { "Models", "CompleteModels" }) {
            string source = Path.Combine(sourceRoot, name);
            string destination = Path.Combine(ModelsRoot, name);
            if (!Directory.Exists(source))
                continue;
            MergeDirectory(source, destination);
            moved = true;
        }
        return moved;
    }

    private static IEnumerable<string> DiscoverLegacyRoots() {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "JackLLM");
        if (File.Exists(MigrationSourcesPath)) {
            foreach (string line in File.ReadAllLines(MigrationSourcesPath))
                if (!string.IsNullOrWhiteSpace(line))
                    yield return line.Trim();
        }
    }

    private static IEnumerable<string> GetDatabaseCandidates(string root) {
        yield return Path.Combine(root, "SocketJack", "JackLLMChat", "SocketJackDatabase.json");
        yield return Path.Combine(root, "Data", "Chat", "SocketJackDatabase.json");
    }

    private static IEnumerable<string> GetSessionFileCandidates(string root) {
        yield return Path.Combine(root, "SocketJack", "JackLLMChat", "SessionFiles");
        yield return Path.Combine(root, "SessionFiles");
    }

    private static IEnumerable<string> GetRemoteSessionCandidates(string root) {
        yield return Path.Combine(root, "SocketJack", "RemoteSessions");
        yield return Path.Combine(root, "RemoteSessions");
    }

    private static bool ShouldMigrateModelRoot(string path) {
        string normalized = path.Replace('/', '\\');
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return normalized.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(programFiles) && normalized.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase));
    }

    private static void BackupPersistenceSet(string databasePath) {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        string folder = Path.Combine(BackupsRoot, "legacy-chat-" + stamp);
        Directory.CreateDirectory(folder);
        foreach (string path in new[] { databasePath, databasePath + ".bak", databasePath + ".sha256", databasePath + ".bak.sha256" }) {
            if (File.Exists(path))
                File.Copy(path, Path.Combine(folder, Path.GetFileName(path)), overwrite: true);
        }
        File.WriteAllText(Path.Combine(folder, "source-path.txt"), databasePath, new UTF8Encoding(false));
    }

    private static void MergeDirectory(string source, string destination) {
        if (!Directory.Exists(source) || PathsEqual(source, destination))
            return;
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) {
            string relative = Path.GetRelativePath(source, file);
            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!File.Exists(target)) {
                try { File.Move(file, target); }
                catch (IOException) { File.Copy(file, target, overwrite: false); }
            }
        }
    }

    private static bool PathsEqual(string left, string right) {
        try {
            return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }
}
