using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LmVs;
using SocketJack.Net.Database;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    private const string ProjectVersionControlSettingsTableName = "JackLLMProjectVersionControlSettings";
    private const string SessionVersionControlSettingsTableName = "JackLLMSessionVersionControlSettings";
    private readonly object _versionControlLock = new object();
    private readonly Dictionary<string, VersionControlRunState> _versionControlRuns = new Dictionary<string, VersionControlRunState>(StringComparer.Ordinal);

    private sealed class VersionControlRunState
    {
        public string PromptId { get; set; } = "";
        public string VersionId { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Error { get; set; } = "";
        public VersionControlManifest Baseline { get; set; }
        public bool Completed { get; set; }
    }

    private sealed class VersionControlManifest
    {
        public int Schema { get; set; } = 1;
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string ProjectId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PromptId { get; set; } = "";
        public string Type { get; set; } = "automatic";
        public bool ProjectScope { get; set; }
        public string Name { get; set; } = "";
        public string Status { get; set; } = "active";
        public string CreatedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public string SemanticVersion { get; set; } = "";
        public string BranchName { get; set; } = "";
        public Dictionary<string, VersionControlFileState> Before { get; set; } = new Dictionary<string, VersionControlFileState>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VersionControlFileState> After { get; set; } = new Dictionary<string, VersionControlFileState>(StringComparer.OrdinalIgnoreCase);
        public List<VersionControlChange> Changes { get; set; } = new List<VersionControlChange>();
    }

    private sealed class VersionControlFileState
    {
        public string Key { get; set; } = "";
        public string RootPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Hash { get; set; } = "";
        public long Length { get; set; }
        public string BlobHash { get; set; } = "";
    }

    private sealed class VersionControlChange
    {
        public string Key { get; set; } = "";
        public string Path { get; set; } = "";
        public string Kind { get; set; } = "modified";
        public VersionControlFileState Before { get; set; }
        public VersionControlFileState After { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
    }

    private sealed class ProjectVersionControlState
    {
        public bool AutomaticEnabled { get; set; } = true;
        public string CurrentVersion { get; set; } = "1.0.0";
        public string ActiveBranch { get; set; } = "";
        public string BranchBaseVersion { get; set; } = "";
        public string BranchVersion { get; set; } = "";
        public string EffectiveVersion => string.IsNullOrWhiteSpace(ActiveBranch) ? CurrentVersion : BranchVersion;
    }

    private sealed class VersionControlRestorePreview
    {
        public VersionControlManifest Manifest { get; set; }
        public List<VersionControlChange> Safe { get; } = new List<VersionControlChange>();
        public List<VersionControlChange> Conflicts { get; } = new List<VersionControlChange>();
        public List<VersionControlChange> AlreadyRestored { get; } = new List<VersionControlChange>();
    }

    private string VersionControlRoot => Path.Combine(_chatSessionFilesRoot, ".version-control");

    private Table GetProjectVersionControlSettingsTable()
    {
        SocketJack.Net.Database.Database db = _chatSessionData.Databases.GetOrAdd("SocketJack", _ => new SocketJack.Net.Database.Database("SocketJack"));
        Table table = db.Tables.GetOrAdd(ProjectVersionControlSettingsTableName, _ => new Table(ProjectVersionControlSettingsTableName));
        if (table.Columns == null) table.Columns = new List<Column>();
        EnsureColumn(table, 0, "OwnerKey", 180);
        EnsureColumn(table, 1, "ProjectId", 128);
        EnsureColumn(table, 2, "AutomaticEnabled", 16);
        EnsureColumn(table, 3, "UpdatedUtc", 80);
        EnsureColumn(table, 4, "CurrentVersion", 32);
        EnsureColumn(table, 5, "ActiveBranch", 160);
        EnsureColumn(table, 6, "BranchBaseVersion", 32);
        EnsureColumn(table, 7, "BranchVersion", 32);
        if (table.Rows == null) table.Rows = new List<object[]>();
        return table;
    }

    private Table GetSessionVersionControlSettingsTable()
    {
        SocketJack.Net.Database.Database db = _chatSessionData.Databases.GetOrAdd("SocketJack", _ => new SocketJack.Net.Database.Database("SocketJack"));
        Table table = db.Tables.GetOrAdd(SessionVersionControlSettingsTableName, _ => new Table(SessionVersionControlSettingsTableName));
        if (table.Columns == null) table.Columns = new List<Column>();
        EnsureColumn(table, 0, "OwnerKey", 180);
        EnsureColumn(table, 1, "SessionId", 128);
        EnsureColumn(table, 2, "AutomaticEnabled", 16);
        EnsureColumn(table, 3, "UpdatedUtc", 80);
        if (table.Rows == null) table.Rows = new List<object[]>();
        return table;
    }

    private bool GetVersionControlSetting(Table table, string ownerKey, string key)
    {
        foreach (object[] source in table.Rows)
        {
            if (source != null && source.Length >= 3 && ChatOwnerKeysMatch(ownerKey, source[0]?.ToString()) &&
                string.Equals(source[1]?.ToString(), key, StringComparison.OrdinalIgnoreCase))
                return ParseStoredBool(source[2]?.ToString(), true);
        }
        return true;
    }

    private void SaveVersionControlSetting(Table table, string ownerKey, string key, bool enabled)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        for (int i = 0; i < table.Rows.Count; i++)
        {
            object[] row = table.Rows[i];
            if (row != null && row.Length >= 2 && ChatOwnerKeysMatch(ownerKey, row[0]?.ToString()) &&
                string.Equals(row[1]?.ToString(), key, StringComparison.OrdinalIgnoreCase))
            {
                int length = Math.Max(4, row.Length);
                object[] next = new object[length];
                Array.Copy(row, next, row.Length);
                next[0] = ownerKey;
                next[1] = key;
                next[2] = enabled ? "true" : "false";
                next[3] = now;
                table.Rows[i] = next;
                return;
            }
        }
        table.Rows.Add(new object[] { ownerKey, key, enabled ? "true" : "false", now });
    }

    private ProjectVersionControlState GetProjectVersionControlState(string ownerKey, string projectId)
    {
        Table table = GetProjectVersionControlSettingsTable();
        foreach (object[] row in table.Rows)
        {
            if (row == null || row.Length < 2 || !ChatOwnerKeysMatch(ownerKey, row[0]?.ToString()) ||
                !string.Equals(row[1]?.ToString(), projectId, StringComparison.OrdinalIgnoreCase)) continue;
            return new ProjectVersionControlState
            {
                AutomaticEnabled = row.Length < 3 || ParseStoredBool(row[2]?.ToString(), true),
                CurrentVersion = NormalizeSemanticVersion(row.Length > 4 ? row[4]?.ToString() : "1.0.0"),
                ActiveBranch = row.Length > 5 ? (row[5]?.ToString() ?? "").Trim() : "",
                BranchBaseVersion = NormalizeSemanticVersion(row.Length > 6 ? row[6]?.ToString() : "1.0.0"),
                BranchVersion = NormalizeSemanticVersion(row.Length > 7 ? row[7]?.ToString() : (row.Length > 4 ? row[4]?.ToString() : "1.0.0"))
            };
        }
        return new ProjectVersionControlState();
    }

    private void SaveProjectVersionControlState(string ownerKey, string projectId, ProjectVersionControlState state)
    {
        Table table = GetProjectVersionControlSettingsTable();
        string now = DateTimeOffset.UtcNow.ToString("O");
        object[] next = new object[] { ownerKey, projectId, state.AutomaticEnabled ? "true" : "false", now,
            NormalizeSemanticVersion(state.CurrentVersion), (state.ActiveBranch ?? "").Trim(),
            NormalizeSemanticVersion(state.BranchBaseVersion), NormalizeSemanticVersion(state.BranchVersion) };
        for (int index = 0; index < table.Rows.Count; index++)
        {
            object[] row = table.Rows[index];
            if (row != null && row.Length >= 2 && ChatOwnerKeysMatch(ownerKey, row[0]?.ToString()) &&
                string.Equals(row[1]?.ToString(), projectId, StringComparison.OrdinalIgnoreCase))
            {
                table.Rows[index] = next;
                return;
            }
        }
        table.Rows.Add(next);
    }

    private static string NormalizeSemanticVersion(string value)
    {
        int[] parts = (value ?? "").Split('.').Take(3).Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ? Math.Max(0, parsed) : 0).ToArray();
        return parts.Length == 3 ? string.Join(".", parts) : "1.0.0";
    }

    private static string AdvanceSemanticVersion(string value, int component)
    {
        int[] parts = NormalizeSemanticVersion(value).Split('.').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        component = Math.Max(0, Math.Min(2, component));
        parts[component]++;
        for (int index = component + 1; index < parts.Length; index++) parts[index] = 0;
        return string.Join(".", parts);
    }

    private string AdvanceProjectRestorePointVersion(string ownerKey, string projectId)
    {
        lock (_chatSessionLock)
        {
            ProjectVersionControlState state = GetProjectVersionControlState(ownerKey, projectId);
            if (string.IsNullOrWhiteSpace(state.ActiveBranch)) state.CurrentVersion = AdvanceSemanticVersion(state.CurrentVersion, 2);
            else state.BranchVersion = AdvanceSemanticVersion(state.BranchVersion, 2);
            SaveProjectVersionControlState(ownerKey, projectId, state);
            SaveChatSessionDataAndInvalidateCaches();
            return state.EffectiveVersion;
        }
    }

    private bool IsAutomaticVersionControlEnabled(string ownerKey, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !ChatSessionBelongsToOwner(sessionId, ownerKey)) return false;
        string projectId = GetChatSessionProjectId(sessionId);
        lock (_chatSessionLock)
        {
            return GetVersionControlSetting(GetProjectVersionControlSettingsTable(), ownerKey, projectId) &&
                GetVersionControlSetting(GetSessionVersionControlSettingsTable(), ownerKey, sessionId);
        }
    }

    private string GetVersionControlOwnerDirectory(string ownerKey)
    {
        return Path.Combine(VersionControlRoot, HashText(NormalizeChatFilesystemOwnerKey(ownerKey)).Substring(0, 24));
    }

    private string GetVersionControlBlobPath(string ownerKey, string hash)
    {
	    if (string.IsNullOrWhiteSpace(hash) || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
	        throw new InvalidDataException("Version content hash is invalid.");
        return Path.Combine(GetVersionControlOwnerDirectory(ownerKey), "blobs", hash.Substring(0, 2), hash + ".blob");
    }

    private string GetVersionControlManifestDirectory(string ownerKey, string projectId)
    {
        return Path.Combine(GetVersionControlOwnerDirectory(ownerKey), "versions", SanitizeFileName(NormalizeChatProjectId(projectId)));
    }

    private string GetVersionControlManifestPath(string ownerKey, string projectId, string versionId)
    {
        return Path.Combine(GetVersionControlManifestDirectory(ownerKey, projectId), SanitizeFileName(versionId) + ".json");
    }

    private static string HashText(string text)
    {
        using SHA256 sha = SHA256.Create();
        return ToLowerHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? "")));
    }

    private static string HashFile(string path)
    {
        using FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using SHA256 sha = SHA256.Create();
        return ToLowerHex(sha.ComputeHash(input));
    }

    private static string ToLowerHex(byte[] bytes)
    {
        var builder = new StringBuilder((bytes?.Length ?? 0) * 2);
        foreach (byte value in bytes ?? Array.Empty<byte>()) builder.Append(value.ToString("x2", CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(value), new UTF8Encoding(false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private List<string> GetVersionControlRoots(string ownerKey, string sessionId, bool projectScope)
    {
        IEnumerable<string> sessionIds = projectScope ? GetChatProjectSessionIds(sessionId, ownerKey) : new[] { sessionId };
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relatedSessionId in sessionIds)
        {
            string filesRoot = GetChatSessionFilesDirectory(relatedSessionId);
            if (!string.IsNullOrWhiteSpace(filesRoot) && Directory.Exists(filesRoot)) roots.Add(Path.GetFullPath(filesRoot));
            foreach (var workspace in GetChatWorkspaceRootsDiagnostics(ownerKey, relatedSessionId))
            {
                if (!string.Equals(workspace.AccessMode, "read-write", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(workspace.Path) || !Directory.Exists(workspace.Path)) continue;
                roots.Add(Path.GetFullPath(workspace.Path));
            }
        }
        return roots.OrderBy(path => path.Length).Where(path => !roots.Any(other => !PathsEqual(path, other) && IsPathInsideRoot(path, other))).ToList();
    }

    private bool IsVersionControlExcludedDirectory(string path)
    {
        string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) || name.Equals(".hg", StringComparison.OrdinalIgnoreCase) ||
	        name.Equals(".svn", StringComparison.OrdinalIgnoreCase) || name.Equals(".bzr", StringComparison.OrdinalIgnoreCase) ||
	        name.Equals("_darcs", StringComparison.OrdinalIgnoreCase) || name.Equals(".pijul", StringComparison.OrdinalIgnoreCase) ||
	        name.Equals(".fossil-settings", StringComparison.OrdinalIgnoreCase) || name.Equals(".versions", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(".version-control", StringComparison.OrdinalIgnoreCase) || IsPathInsideRoot(path, VersionControlRoot);
    }

    private IEnumerable<string> EnumerateVersionControlFiles(string ownerKey, string sessionId, string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (IsVersionControlExcludedDirectory(directory)) continue;
            DirectoryInfo info;
            try { info = new DirectoryInfo(directory); }
            catch { continue; }
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            string[] files;
            try { files = Directory.GetFiles(directory); }
            catch { continue; }
            foreach (string file in files)
            {
	            bool include = false;
	            try { include = (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0 && !IsChatWorkspacePathIgnored(ownerKey, sessionId, file, out _); }
	            catch { }
	            if (include) yield return file;
            }
            string[] directories;
            try { directories = Directory.GetDirectories(directory); }
            catch { continue; }
            foreach (string child in directories) pending.Push(child);
        }
    }

    private Dictionary<string, VersionControlFileState> CaptureVersionControlSnapshot(string ownerKey, string sessionId, bool projectScope)
    {
        var snapshot = new Dictionary<string, VersionControlFileState>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in GetVersionControlRoots(ownerKey, sessionId, projectScope))
        {
            string rootId = HashText(root).Substring(0, 16);
            foreach (string file in EnumerateVersionControlFiles(ownerKey, sessionId, root))
            {
                string fullPath = Path.GetFullPath(file);
                string relative = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("../", StringComparison.Ordinal) || relative == "..") continue;
                string hash = HashFile(fullPath);
                string blobPath = GetVersionControlBlobPath(ownerKey, hash);
                if (!File.Exists(blobPath))
                {
                    long length = new FileInfo(fullPath).Length;
                    if (!EnsureVersionControlStorageCanAdd(ownerKey, sessionId, length, out string storageError))
                        throw new InvalidOperationException(storageError);
                    Directory.CreateDirectory(Path.GetDirectoryName(blobPath) ?? GetVersionControlOwnerDirectory(ownerKey));
                    string temp = blobPath + ".tmp-" + Guid.NewGuid().ToString("N");
                    File.Copy(fullPath, temp, false);
                    if (File.Exists(blobPath)) File.Delete(temp); else File.Move(temp, blobPath);
                    lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
                }
                string key = rootId + ":" + relative;
                snapshot[key] = new VersionControlFileState
                {
                    Key = key,
                    RootPath = root,
                    RelativePath = relative,
                    Hash = hash,
                    BlobHash = hash,
                    Length = new FileInfo(fullPath).Length
                };
            }
        }
        return snapshot;
    }

    private static List<VersionControlChange> CompareVersionControlSnapshots(Dictionary<string, VersionControlFileState> before, Dictionary<string, VersionControlFileState> after)
    {
        before ??= new Dictionary<string, VersionControlFileState>(StringComparer.OrdinalIgnoreCase);
        after ??= new Dictionary<string, VersionControlFileState>(StringComparer.OrdinalIgnoreCase);
        var changes = new List<VersionControlChange>();
        foreach (string key in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(key, out VersionControlFileState oldState);
            after.TryGetValue(key, out VersionControlFileState newState);
            if (oldState != null && newState != null && string.Equals(oldState.Hash, newState.Hash, StringComparison.OrdinalIgnoreCase)) continue;
            changes.Add(new VersionControlChange
            {
                Key = key,
                Path = (newState ?? oldState)?.RootPath + Path.DirectorySeparatorChar + ((newState ?? oldState)?.RelativePath ?? ""),
                Kind = oldState == null ? "created" : (newState == null ? "deleted" : "modified"),
                Before = oldState,
                After = newState
            });
        }
        return changes;
    }

    private void PopulateVersionControlLineStats(string ownerKey, IEnumerable<VersionControlChange> changes)
    {
        foreach (VersionControlChange change in changes ?? Enumerable.Empty<VersionControlChange>())
        {
            string[] before = ReadVersionControlTextLines(ownerKey, change.Before);
            string[] after = ReadVersionControlTextLines(ownerKey, change.After);
            if (before == null || after == null) continue;
            int prefix = 0;
            while (prefix < before.Length && prefix < after.Length && string.Equals(before[prefix], after[prefix], StringComparison.Ordinal)) prefix++;
            int suffix = 0;
            while (suffix < before.Length - prefix && suffix < after.Length - prefix &&
                string.Equals(before[before.Length - 1 - suffix], after[after.Length - 1 - suffix], StringComparison.Ordinal)) suffix++;
            change.Deletions = Math.Max(0, before.Length - prefix - suffix);
            change.Additions = Math.Max(0, after.Length - prefix - suffix);
        }
    }

    private string[] ReadVersionControlTextLines(string ownerKey, VersionControlFileState state)
    {
        if (state == null) return Array.Empty<string>();
        if (state.Length > 4L * 1024L * 1024L || string.IsNullOrWhiteSpace(state.BlobHash)) return null;
        string path = GetVersionControlBlobPath(ownerKey, state.BlobHash);
        if (!File.Exists(path)) return null;
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Any(value => value == 0)) return null;
        try
        {
            string text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length == 0) return Array.Empty<string>();
            string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0) Array.Resize(ref lines, lines.Length - 1);
            return lines;
        }
        catch (DecoderFallbackException) { return null; }
    }

    private void BeginAutomaticVersionControlRun(string promptId, string ownerKey, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(promptId) || !IsAutomaticVersionControlEnabled(ownerKey, sessionId)) return;
        RecoverInterruptedVersionControlCheckpoints(ownerKey);
        string versionId = "version_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var run = new VersionControlRunState { PromptId = promptId, VersionId = versionId, OwnerKey = ownerKey, SessionId = sessionId };
        try
        {
            run.Baseline = new VersionControlManifest
            {
                Id = versionId,
                OwnerKey = ownerKey,
                ProjectId = GetChatSessionProjectId(sessionId),
                SessionId = sessionId,
                PromptId = promptId,
                Type = "automatic",
                ProjectScope = false,
                Name = "Before LLM response",
                Status = "active",
                CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Before = CaptureVersionControlSnapshot(ownerKey, sessionId, false)
            };
            WriteJsonAtomic(GetVersionControlManifestPath(ownerKey, run.Baseline.ProjectId, versionId) + ".active", run.Baseline);
        }
        catch (Exception ex)
        {
            run.Error = "Automatic restore point failed: " + ex.Message;
            LogMessage("[Version Control] " + run.Error);
        }
        lock (_versionControlLock) _versionControlRuns[promptId] = run;
    }

    private string CompleteAutomaticVersionControlRun(string promptId, string status)
    {
        VersionControlRunState run;
        lock (_versionControlLock)
        {
            if (!_versionControlRuns.TryGetValue(promptId ?? "", out run) || run.Completed) return run?.VersionId ?? "";
            run.Completed = true;
        }
        if (run.Baseline == null || !string.IsNullOrWhiteSpace(run.Error)) return "";
        try
        {
            run.Baseline.After = CaptureVersionControlSnapshot(run.OwnerKey, run.SessionId, false);
            run.Baseline.Changes = CompareVersionControlSnapshots(run.Baseline.Before, run.Baseline.After);
            PopulateVersionControlLineStats(run.OwnerKey, run.Baseline.Changes);
            string activePath = GetVersionControlManifestPath(run.OwnerKey, run.Baseline.ProjectId, run.VersionId) + ".active";
            if (run.Baseline.Changes.Count == 0)
            {
                if (File.Exists(activePath)) File.Delete(activePath);
	            GarbageCollectVersionControlBlobs(run.OwnerKey, includeRecent: true);
	            lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
                return "";
            }
            run.Baseline.Status = string.IsNullOrWhiteSpace(status) ? "completed" : status.ToLowerInvariant();
            run.Baseline.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
            ProjectVersionControlState state;
            lock (_chatSessionLock) state = GetProjectVersionControlState(run.OwnerKey, run.Baseline.ProjectId);
            run.Baseline.BranchName = state.ActiveBranch;
            run.Baseline.SemanticVersion = AdvanceProjectRestorePointVersion(run.OwnerKey, run.Baseline.ProjectId);
            WriteJsonAtomic(GetVersionControlManifestPath(run.OwnerKey, run.Baseline.ProjectId, run.VersionId), run.Baseline);
            if (File.Exists(activePath)) File.Delete(activePath);
            lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
            return run.VersionId;
        }
        catch (Exception ex)
        {
            run.Error = "Automatic version finalization failed: " + ex.Message;
            LogMessage("[Version Control] " + run.Error);
            return "";
        }
    }

    private string GetActiveAutomaticVersionId(string ownerKey, string sessionId)
    {
        lock (_versionControlLock)
        {
            return _versionControlRuns.Values.Where(run => !run.Completed && string.IsNullOrWhiteSpace(run.Error) &&
                ChatOwnerKeysMatch(ownerKey, run.OwnerKey) && string.Equals(sessionId, run.SessionId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(run => run.Baseline?.CreatedUtc).Select(run => run.VersionId).FirstOrDefault() ?? "";
        }
    }

    private bool CanExecuteVersionControlledMutation(string ownerKey, string sessionId, out string error)
    {
        error = "";
        if (!IsAutomaticVersionControlEnabled(ownerKey, sessionId)) return true;
        lock (_versionControlLock)
        {
            VersionControlRunState run = _versionControlRuns.Values.Where(item => !item.Completed && ChatOwnerKeysMatch(ownerKey, item.OwnerKey) &&
                string.Equals(sessionId, item.SessionId, StringComparison.OrdinalIgnoreCase)).OrderByDescending(item => item.Baseline?.CreatedUtc).FirstOrDefault();
            if (run == null || string.IsNullOrWhiteSpace(run.Error)) return true;
            error = run.Error + " Mutating tools are disabled for this response.";
            return false;
        }
    }

    private static bool IsVersionControlSensitiveTool(string toolName)
    {
        return IsProxyOwnedMutatingVsTool(toolName) || string.Equals(toolName, "run_command_in_terminal", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "download_file", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "git_stage", StringComparison.OrdinalIgnoreCase) || string.Equals(toolName, "git_unstage", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "git_commit", StringComparison.OrdinalIgnoreCase) || string.Equals(toolName, "git_switch_branch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(toolName, "git_pull", StringComparison.OrdinalIgnoreCase);
    }

    private string GetCompletedVersionControlVersionId(string promptId)
    {
        lock (_versionControlLock)
        {
            if (!_versionControlRuns.TryGetValue(promptId ?? "", out VersionControlRunState run) || !run.Completed || run.Baseline == null || run.Baseline.Changes == null || run.Baseline.Changes.Count == 0)
                return "";
            return run.VersionId;
        }
    }

    private VersionControlManifest LoadVersionControlManifest(string ownerKey, string versionId)
    {
        string ownerDirectory = GetVersionControlOwnerDirectory(ownerKey);
        string versionsDirectory = Path.Combine(ownerDirectory, "versions");
        if (!Directory.Exists(versionsDirectory)) return null;
        foreach (string file in Directory.EnumerateFiles(versionsDirectory, SanitizeFileName(versionId) + ".json", SearchOption.AllDirectories))
        {
            VersionControlManifest manifest = JsonSerializer.Deserialize<VersionControlManifest>(File.ReadAllText(file));
            if (manifest != null && ChatOwnerKeysMatch(ownerKey, manifest.OwnerKey) && string.Equals(versionId, manifest.Id, StringComparison.OrdinalIgnoreCase)) return manifest;
        }
        return null;
    }

    private VersionControlFileState ReadCurrentVersionControlFileState(VersionControlFileState template)
    {
        if (template == null) return null;
        string path = Path.GetFullPath(Path.Combine(template.RootPath, template.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInsideRoot(path, template.RootPath) || !File.Exists(path)) return null;
        return new VersionControlFileState { Key = template.Key, RootPath = template.RootPath, RelativePath = template.RelativePath, Hash = HashFile(path), Length = new FileInfo(path).Length };
    }

    private VersionControlRestorePreview PreviewVersionControlRestore(string ownerKey, VersionControlManifest manifest)
    {
        var preview = new VersionControlRestorePreview { Manifest = manifest };
        List<VersionControlChange> changes = manifest.Changes ?? new List<VersionControlChange>();
        if (changes.Count == 0 && manifest.Before != null && manifest.Before.Count > 0)
        {
            Dictionary<string, VersionControlFileState> current = CaptureVersionControlSnapshot(ownerKey, manifest.SessionId, manifest.ProjectScope);
            changes = CompareVersionControlSnapshots(manifest.Before, current);
        }
        foreach (VersionControlChange change in changes)
        {
            VersionControlFileState current = ReadCurrentVersionControlFileState(change.After ?? change.Before);
            bool currentExists = current != null;
            bool afterExists = change.After != null;
            bool beforeExists = change.Before != null;
            if (currentExists == beforeExists && (!currentExists || string.Equals(current.Hash, change.Before.Hash, StringComparison.OrdinalIgnoreCase)))
                preview.AlreadyRestored.Add(change);
            else if (currentExists == afterExists && (!currentExists || string.Equals(current.Hash, change.After.Hash, StringComparison.OrdinalIgnoreCase)))
                preview.Safe.Add(change);
            else
                preview.Conflicts.Add(change);
        }
        return preview;
    }

    private void ApplyVersionControlChange(string ownerKey, string sessionId, VersionControlChange change)
    {
        VersionControlFileState target = change.Before;
        VersionControlFileState location = change.After ?? change.Before;
        string path = Path.GetFullPath(Path.Combine(location.RootPath, location.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        string authorizedSessionId = GetChatProjectSessionIds(sessionId, ownerKey)
            .FirstOrDefault(candidate => EnsureChatWorkspacePathAccess(ownerKey, candidate, path, requireWrite: true, out _));
        if (string.IsNullOrWhiteSpace(authorizedSessionId))
        {
            EnsureChatWorkspacePathAccess(ownerKey, sessionId, path, requireWrite: true, out string error);
            throw new InvalidOperationException("Restore path is no longer authorized: " + error);
        }
        if (target == null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        string blobPath = GetVersionControlBlobPath(ownerKey, target.BlobHash);
        if (!File.Exists(blobPath)) throw new FileNotFoundException("Version content is missing.", target.RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? target.RootPath);
        File.Copy(blobPath, path, true);
        if (IsJackLlmSessionManagedPath(path)) ImportChatSessionFileToSandbox(sessionId, ownerKey, path, "version-control-restore", out _);
        RegisterChatSessionFileIfTracked(sessionId, ownerKey, path, "version-control-restore");
    }

    private VersionControlManifest CreateManualVersionControlCheckpoint(string ownerKey, string sessionId, string name, bool projectScope, string type = "manual", bool advanceVersion = true, string branchNameOverride = null)
    {
        string versionId = "version_" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        Dictionary<string, VersionControlFileState> snapshot = CaptureVersionControlSnapshot(ownerKey, sessionId, projectScope);
        string projectId = GetChatSessionProjectId(sessionId);
        ProjectVersionControlState state;
        lock (_chatSessionLock) state = GetProjectVersionControlState(ownerKey, projectId);
        string semanticVersion = advanceVersion ? AdvanceProjectRestorePointVersion(ownerKey, projectId) : state.EffectiveVersion;
        var manifest = new VersionControlManifest
        {
            Id = versionId,
            OwnerKey = ownerKey,
            ProjectId = projectId,
            SessionId = sessionId,
            Type = type,
            ProjectScope = projectScope,
            Name = string.IsNullOrWhiteSpace(name) ? "Manual restore point" : name.Trim(),
            Status = "completed",
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O"),
            CompletedUtc = DateTimeOffset.UtcNow.ToString("O"),
            SemanticVersion = semanticVersion,
            BranchName = branchNameOverride ?? state.ActiveBranch,
            Before = snapshot,
            After = snapshot,
            Changes = new List<VersionControlChange>()
        };
        WriteJsonAtomic(GetVersionControlManifestPath(ownerKey, manifest.ProjectId, versionId), manifest);
        lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
        return manifest;
    }

    private ProjectVersionControlState CreateProjectVersionControlBranch(string ownerKey, string projectId, string name)
    {
        string branch = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(branch)) throw new InvalidOperationException("Enter a branch name.");
        if (branch.Length > 80) branch = branch.Substring(0, 80).Trim();
        lock (_chatSessionLock)
        {
            ProjectVersionControlState state = GetProjectVersionControlState(ownerKey, projectId);
            if (!string.IsNullOrWhiteSpace(state.ActiveBranch)) throw new InvalidOperationException("Merge the active branch before creating another branch.");
            state.ActiveBranch = branch;
            state.BranchBaseVersion = state.CurrentVersion;
            state.BranchVersion = state.CurrentVersion;
            SaveProjectVersionControlState(ownerKey, projectId, state);
            SaveChatSessionDataAndInvalidateCaches();
            return state;
        }
    }

    private VersionControlManifest PromoteProjectVersionControl(string ownerKey, string sessionId, bool major)
    {
        string projectId = GetChatSessionProjectId(sessionId);
        string branchName;
        ProjectVersionControlState state;
        lock (_chatSessionLock)
        {
            state = GetProjectVersionControlState(ownerKey, projectId);
            branchName = state.ActiveBranch;
            if (!major && string.IsNullOrWhiteSpace(branchName)) throw new InvalidOperationException("Create a branch before merging a feature.");
            state.CurrentVersion = AdvanceSemanticVersion(state.CurrentVersion, major ? 0 : 1);
            state.ActiveBranch = "";
            state.BranchBaseVersion = "";
            state.BranchVersion = state.CurrentVersion;
            SaveProjectVersionControlState(ownerKey, projectId, state);
            SaveChatSessionDataAndInvalidateCaches();
        }
        string name = major ? "Major redesign " + state.CurrentVersion : "Merged branch " + branchName;
        return CreateManualVersionControlCheckpoint(ownerKey, sessionId, name, true, major ? "major" : "branch-merge", advanceVersion: false, branchNameOverride: branchName);
    }

    private IEnumerable<VersionControlManifest> EnumerateVersionControlManifests(string ownerKey, string projectId)
    {
        string directory = GetVersionControlManifestDirectory(ownerKey, projectId);
        if (!Directory.Exists(directory)) yield break;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            VersionControlManifest manifest = null;
            try { manifest = JsonSerializer.Deserialize<VersionControlManifest>(File.ReadAllText(file)); } catch { }
            if (manifest != null && ChatOwnerKeysMatch(ownerKey, manifest.OwnerKey)) yield return manifest;
        }
    }

    private object BuildVersionControlPayload(string ownerKey, string sessionId, string scope, int take, int skip)
    {
        string projectId = GetChatSessionProjectId(sessionId);
        bool projectEnabled;
        bool sessionEnabled;
        ProjectVersionControlState projectState;
        string projectName;
        lock (_chatSessionLock)
        {
            projectEnabled = GetVersionControlSetting(GetProjectVersionControlSettingsTable(), ownerKey, projectId);
            sessionEnabled = GetVersionControlSetting(GetSessionVersionControlSettingsTable(), ownerKey, sessionId);
            projectState = GetProjectVersionControlState(ownerKey, projectId);
            projectName = FindChatProject(ownerKey, projectId, includeArchived: true)?.Name ?? "Unsorted";
        }
        IEnumerable<VersionControlManifest> versions = EnumerateVersionControlManifests(ownerKey, projectId);
        if (string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase)) versions = versions.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        var allVersions = versions.Select(item => (object)new
        {
            id = item.Id,
            name = item.Name,
            type = item.Type,
            status = item.Status,
            sessionId = item.SessionId,
            promptId = item.PromptId,
            createdUtc = item.CreatedUtc,
            completedUtc = item.CompletedUtc,
            semanticVersion = item.SemanticVersion,
            branchName = item.BranchName,
            fileCount = item.Changes?.Count ?? item.Before?.Count ?? 0,
            additions = item.Changes?.Sum(change => Math.Max(0, change.Additions)) ?? 0,
            deletions = item.Changes?.Sum(change => Math.Max(0, change.Deletions)) ?? 0,
            storageBytes = (item.Before?.Values.Sum(file => Math.Max(0L, file.Length)) ?? 0L)
        }).ToList();
        if (!string.Equals(scope, "session", StringComparison.OrdinalIgnoreCase))
        {
            string legacyRoot = GetProjectFileVersionsDirectory(sessionId);
            if (Directory.Exists(legacyRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(legacyRoot))
                {
                    string legacyId = Path.GetFileName(directory);
                    string labelPath = Path.Combine(directory, "label.txt");
                    string filesPath = Path.Combine(directory, "files");
                    allVersions.Add(new
                    {
                        id = "legacy_" + legacyId,
                        name = File.Exists(labelPath) ? File.ReadAllText(labelPath).Trim() : legacyId,
                        type = "legacy",
                        status = "completed",
                        sessionId,
                        promptId = "",
                        createdUtc = Directory.GetCreationTimeUtc(directory).ToString("O"),
                        completedUtc = Directory.GetLastWriteTimeUtc(directory).ToString("O"),
                        semanticVersion = "",
                        branchName = "",
                        fileCount = Directory.Exists(filesPath) ? Directory.EnumerateFiles(filesPath, "*", SearchOption.AllDirectories).Count() : 0,
                        additions = 0,
                        deletions = 0,
                        storageBytes = Directory.Exists(filesPath) ? Directory.EnumerateFiles(filesPath, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0L
                    });
                }
            }
        }
        List<object> list = allVersions.OrderByDescending(item => JsonSerializer.Serialize(item).Contains("\"createdUtc\"") ? ExtractVersionCreatedUtc(item) : "", StringComparer.Ordinal)
            .Skip(Math.Max(0, skip)).Take(Math.Max(1, Math.Min(250, take))).ToList();
        return new
        {
            ok = true,
            projectId,
            projectName,
            sessionId,
            currentVersion = projectState.EffectiveVersion,
            mainVersion = projectState.CurrentVersion,
            activeBranch = projectState.ActiveBranch,
            branchBaseVersion = projectState.BranchBaseVersion,
            settings = new { projectAutomaticEnabled = projectEnabled, sessionAutomaticEnabled = sessionEnabled, effectiveAutomaticEnabled = projectEnabled && sessionEnabled, retention = "storage-quota" },
            protectedRoots = GetVersionControlRoots(ownerKey, sessionId, string.Equals(scope, "project", StringComparison.OrdinalIgnoreCase)),
            storageBytes = GetVersionControlStorageUsageBytes(ownerKey),
            versions = list
        };
    }

    private static string ExtractVersionCreatedUtc(object item)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(item));
            return document.RootElement.TryGetProperty("createdUtc", out JsonElement value) ? value.GetString() ?? "" : "";
        }
        catch { return ""; }
    }

    private string HandleProjectVersionControlGetRequest(NetworkConnection connection, HttpRequest request)
    {
        try
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            string sessionId = EnsureChatUiSessionId(GetQueryParameter(request, "sessionId"));
            if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) return BuildJsonError(request, 403, "Forbidden", "The session does not belong to this user.");
            int take = int.TryParse(GetQueryParameter(request, "take"), out int parsedTake) ? parsedTake : 50;
            int skip = int.TryParse(GetQueryParameter(request, "skip"), out int parsedSkip) ? parsedSkip : 0;
            return JsonSerializer.Serialize(BuildVersionControlPayload(ownerKey, sessionId, GetQueryParameter(request, "scope") ?? "session", take, skip));
        }
        catch (Exception ex) { return BuildJsonError(request, 500, "Internal Server Error", ex.Message); }
    }

    private string HandleProjectVersionControlPostRequest(NetworkConnection connection, HttpRequest request)
    {
        try
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            JsonElement root = document.RootElement;
            string sessionId = EnsureChatUiSessionId(ExtractStringProperty(root, "sessionId"));
            if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) return BuildJsonError(request, 403, "Forbidden", "The session does not belong to this user.");
            string action = (ExtractStringProperty(root, "action") ?? "").Trim().ToLowerInvariant();
            string projectId = GetChatSessionProjectId(sessionId);
            if (action == "save-settings")
            {
                lock (_chatSessionLock)
                {
                    if (root.TryGetProperty("projectAutomaticEnabled", out JsonElement projectEnabled))
                        SaveVersionControlSetting(GetProjectVersionControlSettingsTable(), ownerKey, projectId, ReadJsonBool(projectEnabled, true));
                    if (root.TryGetProperty("sessionAutomaticEnabled", out JsonElement sessionEnabled))
                        SaveVersionControlSetting(GetSessionVersionControlSettingsTable(), ownerKey, sessionId, ReadJsonBool(sessionEnabled, true));
                    SaveChatSessionDataAndInvalidateCaches();
                }
            }
            else if (action == "create-checkpoint" || action == "create-restore-point")
            {
                CreateManualVersionControlCheckpoint(ownerKey, sessionId, ExtractStringProperty(root, "name"), !string.Equals(ExtractStringProperty(root, "scope"), "session", StringComparison.OrdinalIgnoreCase));
            }
            else if (action == "create-branch")
            {
                lock (_chatSessionLock)
                {
                    CreateProjectVersionControlBranch(ownerKey, projectId, ExtractStringProperty(root, "name"));
                }
            }
            else if (action == "merge-branch")
            {
                PromoteProjectVersionControl(ownerKey, sessionId, major: false);
            }
            else if (action == "increment-major")
            {
                PromoteProjectVersionControl(ownerKey, sessionId, major: true);
            }
            else if (action == "preview-restore" || action == "restore")
            {
                string versionId = ExtractStringProperty(root, "versionId") ?? "";
                VersionControlManifest manifest = LoadVersionControlManifest(ownerKey, versionId);
                if (manifest == null && versionId.StartsWith("legacy_", StringComparison.OrdinalIgnoreCase))
                {
                    string legacyId = versionId.Substring("legacy_".Length);
                    if (action == "preview-restore") return JsonSerializer.Serialize(new { ok = true, versionId, safe = Array.Empty<string>(), conflicts = Array.Empty<string>(), alreadyRestored = Array.Empty<string>(), legacy = true });
                    RestoreProjectFileVersion(sessionId, ownerKey, legacyId);
                    return JsonSerializer.Serialize(new { ok = true, versionId, restored = 1, conflicts = Array.Empty<string>(), legacy = true });
                }
                if (manifest == null) return BuildJsonError(request, 404, "Not Found", "Version was not found.");
                if (!string.Equals(manifest.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) return BuildJsonError(request, 403, "Forbidden", "Version belongs to another project.");
                VersionControlRestorePreview preview = PreviewVersionControlRestore(ownerKey, manifest);
                if (action == "preview-restore") return JsonSerializer.Serialize(new { ok = true, versionId, safe = preview.Safe.Select(item => item.Path), conflicts = preview.Conflicts.Select(item => item.Path), alreadyRestored = preview.AlreadyRestored.Select(item => item.Path) });
                CreateManualVersionControlCheckpoint(ownerKey, sessionId, "Before restoring " + (manifest.Name ?? manifest.Id), true, "safety");
                bool force = root.TryGetProperty("forceConflicts", out JsonElement forceElement) && ReadJsonBool(forceElement, false);
                HashSet<string> selected = root.TryGetProperty("selectedPaths", out JsonElement selectedElement) && selectedElement.ValueKind == JsonValueKind.Array
                    ? selectedElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : null;
                List<VersionControlChange> apply = preview.Safe.Concat(force ? preview.Conflicts : Enumerable.Empty<VersionControlChange>()).Where(item => selected == null || selected.Contains(item.Path) || selected.Contains(item.Key)).ToList();
                foreach (VersionControlChange change in apply) ApplyVersionControlChange(ownerKey, sessionId, change);
                return JsonSerializer.Serialize(new { ok = true, versionId, restored = apply.Count, conflicts = preview.Conflicts.Where(item => !force || (selected != null && !selected.Contains(item.Path) && !selected.Contains(item.Key))).Select(item => item.Path).ToArray() });
            }
            else if (action == "delete-checkpoint" || action == "delete-restore-point")
            {
                string versionId = ExtractStringProperty(root, "versionId") ?? "";
                VersionControlManifest manifest = LoadVersionControlManifest(ownerKey, versionId);
                if (manifest == null && versionId.StartsWith("legacy_", StringComparison.OrdinalIgnoreCase))
                {
                    DeleteProjectFileVersion(sessionId, versionId.Substring("legacy_".Length));
                    return JsonSerializer.Serialize(BuildVersionControlPayload(ownerKey, sessionId, ExtractStringProperty(root, "scope") ?? "project", 50, 0));
                }
                if (manifest == null) return BuildJsonError(request, 404, "Not Found", "Version was not found.");
                if (!string.Equals(manifest.ProjectId, projectId, StringComparison.OrdinalIgnoreCase)) return BuildJsonError(request, 403, "Forbidden", "Version belongs to another project.");
                string path = GetVersionControlManifestPath(ownerKey, manifest.ProjectId, manifest.Id);
                if (File.Exists(path)) File.Delete(path);
	            GarbageCollectVersionControlBlobs(ownerKey, includeRecent: true);
                lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
            }
            else return BuildJsonError(request, 400, "Bad Request", "Use save-settings, create-restore-point, create-branch, merge-branch, increment-major, preview-restore, restore, or delete-restore-point.");
            return JsonSerializer.Serialize(BuildVersionControlPayload(ownerKey, sessionId, ExtractStringProperty(root, "scope") ?? "session", 50, 0));
        }
        catch (JsonException ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
        catch (Exception ex) { LogMessage("[Version Control] Request failed: " + ex); return BuildJsonError(request, 500, "Internal Server Error", ex.Message); }
    }

    private long GetVersionControlStorageUsageBytes(string ownerKey)
    {
        string directory = GetVersionControlOwnerDirectory(ownerKey);
        if (!Directory.Exists(directory)) return 0L;
        long total = 0L;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                AddStorageUsageBytes(ref total, new FileInfo(file).Length);
        }
        catch { }
        return total;
    }

    public ProjectVersionControlDiagnosticsSnapshot GetProjectVersionControlDiagnostics(string ownerKey, string sessionId, bool projectScope = false)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        RecoverInterruptedVersionControlCheckpoints(ownerKey);
        string projectId = GetChatSessionProjectId(sessionId);
        bool projectEnabled;
        bool sessionEnabled;
        ProjectVersionControlState projectState;
        string projectName;
        lock (_chatSessionLock)
        {
            projectEnabled = GetVersionControlSetting(GetProjectVersionControlSettingsTable(), ownerKey, projectId);
            sessionEnabled = GetVersionControlSetting(GetSessionVersionControlSettingsTable(), ownerKey, sessionId);
            projectState = GetProjectVersionControlState(ownerKey, projectId);
            projectName = FindChatProject(ownerKey, projectId, includeArchived: true)?.Name ?? "Unsorted";
        }
        IEnumerable<VersionControlManifest> manifests = EnumerateVersionControlManifests(ownerKey, projectId);
        if (!projectScope) manifests = manifests.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        return new ProjectVersionControlDiagnosticsSnapshot
        {
            OwnerKey = ownerKey,
            ProjectId = projectId,
            ProjectName = projectName,
            SessionId = sessionId,
            CurrentVersion = projectState.EffectiveVersion,
            MainVersion = projectState.CurrentVersion,
            ActiveBranch = projectState.ActiveBranch,
            ProjectAutomaticEnabled = projectEnabled,
            SessionAutomaticEnabled = sessionEnabled,
            EffectiveAutomaticEnabled = projectEnabled && sessionEnabled,
            StorageBytes = GetVersionControlStorageUsageBytes(ownerKey),
            ProtectedRoots = GetVersionControlRoots(ownerKey, sessionId, projectScope),
            Versions = manifests.OrderByDescending(item => item.CreatedUtc, StringComparer.Ordinal).Select(item => new ProjectVersionControlVersionSnapshot
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.Type,
                Status = item.Status,
                SessionId = item.SessionId,
                PromptId = item.PromptId,
                CreatedUtc = item.CreatedUtc,
                SemanticVersion = item.SemanticVersion,
                BranchName = item.BranchName,
                AffectedFileCount = item.Changes?.Count ?? item.Before?.Count ?? 0,
                Additions = item.Changes?.Sum(change => Math.Max(0, change.Additions)) ?? 0,
                Deletions = item.Changes?.Sum(change => Math.Max(0, change.Deletions)) ?? 0,
                StorageBytes = item.Before?.Values.Sum(file => Math.Max(0L, file.Length)) ?? 0L
            }).ToList()
        };
    }

    public ProjectVersionControlDiagnosticsSnapshot SaveProjectVersionControlDiagnosticsSettings(string ownerKey, string sessionId, bool? projectAutomaticEnabled, bool? sessionAutomaticEnabled)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        lock (_chatSessionLock)
        {
            if (projectAutomaticEnabled.HasValue) SaveVersionControlSetting(GetProjectVersionControlSettingsTable(), ownerKey, GetChatSessionProjectId(sessionId), projectAutomaticEnabled.Value);
            if (sessionAutomaticEnabled.HasValue) SaveVersionControlSetting(GetSessionVersionControlSettingsTable(), ownerKey, sessionId, sessionAutomaticEnabled.Value);
            SaveChatSessionDataAndInvalidateCaches();
        }
        return GetProjectVersionControlDiagnostics(ownerKey, sessionId);
    }

    public string CreateProjectVersionControlCheckpointDiagnostics(string ownerKey, string sessionId, string name, bool projectScope)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        return CreateManualVersionControlCheckpoint(ownerKey, sessionId, name, projectScope).Id;
    }

    public ProjectVersionControlDiagnosticsSnapshot CreateProjectVersionControlBranchDiagnostics(string ownerKey, string sessionId, string name)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        CreateProjectVersionControlBranch(ownerKey, GetChatSessionProjectId(sessionId), name);
        return GetProjectVersionControlDiagnostics(ownerKey, sessionId, true);
    }

    public ProjectVersionControlDiagnosticsSnapshot PromoteProjectVersionControlDiagnostics(string ownerKey, string sessionId, bool major)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        PromoteProjectVersionControl(ownerKey, sessionId, major);
        return GetProjectVersionControlDiagnostics(ownerKey, sessionId, true);
    }

    public ProjectVersionControlRestoreSnapshot RestoreProjectVersionControlDiagnostics(string ownerKey, string sessionId, string versionId, bool forceConflicts = false)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        sessionId = EnsureChatUiSessionId(sessionId);
        if (!ChatSessionBelongsToOwner(sessionId, ownerKey)) throw new UnauthorizedAccessException("The session does not belong to this user.");
        VersionControlManifest manifest = LoadVersionControlManifest(ownerKey, versionId) ?? throw new FileNotFoundException("Version was not found.");
        if (!string.Equals(manifest.ProjectId, GetChatSessionProjectId(sessionId), StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("Version belongs to another project.");
        VersionControlRestorePreview preview = PreviewVersionControlRestore(ownerKey, manifest);
        CreateManualVersionControlCheckpoint(ownerKey, sessionId, "Before restoring " + (manifest.Name ?? manifest.Id), manifest.ProjectScope, "safety");
        List<VersionControlChange> applied = preview.Safe.Concat(forceConflicts ? preview.Conflicts : Enumerable.Empty<VersionControlChange>()).ToList();
        foreach (VersionControlChange change in applied) ApplyVersionControlChange(ownerKey, sessionId, change);
        return new ProjectVersionControlRestoreSnapshot
        {
            VersionId = versionId,
            RestoredFileCount = applied.Count,
            Conflicts = (forceConflicts ? new List<VersionControlChange>() : preview.Conflicts).Select(item => item.Path).ToList(),
            AlreadyRestored = preview.AlreadyRestored.Select(item => item.Path).ToList()
        };
    }

    private IEnumerable<string> EnumerateOwnerVersionControlManifestFiles(string ownerKey, string searchPattern = "*.json")
    {
        string directory = Path.Combine(GetVersionControlOwnerDirectory(ownerKey), "versions");
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory, searchPattern, SearchOption.AllDirectories) : Enumerable.Empty<string>();
    }

    private bool EnsureVersionControlStorageCanAdd(string ownerKey, string sessionId, long bytesToAdd, out string error)
    {
        if (EnsureChatStorageCanAdd(ownerKey, sessionId, bytesToAdd, out _, out error)) return true;
        foreach (var candidate in EnumerateOwnerVersionControlManifestFiles(ownerKey)
            .Select(path => new { Path = path, Manifest = TryReadVersionControlManifest(path) })
	        .Where(item => item.Manifest != null && !string.Equals(item.Manifest.Type, "manual", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Manifest.CreatedUtc, StringComparer.Ordinal))
        {
            try { File.Delete(candidate.Path); } catch { continue; }
	        GarbageCollectVersionControlBlobs(ownerKey);
            lock (_chatUsageSnapshotCacheLock) _chatUsageSnapshotCache.Clear();
            if (EnsureChatStorageCanAdd(ownerKey, sessionId, bytesToAdd, out _, out error)) return true;
        }
        error = "Protected storage quota is full and only named/manual versions remain. Delete a manual version or increase the account storage quota before allowing file changes.";
        return false;
    }

    private static VersionControlManifest TryReadVersionControlManifest(string path)
    {
        try { return JsonSerializer.Deserialize<VersionControlManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }

	private void GarbageCollectVersionControlBlobs(string ownerKey, bool includeRecent = false)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string manifestPath in EnumerateOwnerVersionControlManifestFiles(ownerKey, "*.json*"))
        {
            VersionControlManifest manifest = TryReadVersionControlManifest(manifestPath);
            if (manifest == null) continue;
            foreach (VersionControlFileState file in (manifest.Before?.Values ?? Enumerable.Empty<VersionControlFileState>()).Concat(manifest.After?.Values ?? Enumerable.Empty<VersionControlFileState>()))
                if (!string.IsNullOrWhiteSpace(file.BlobHash)) referenced.Add(file.BlobHash);
        }
        string blobDirectory = Path.Combine(GetVersionControlOwnerDirectory(ownerKey), "blobs");
        if (!Directory.Exists(blobDirectory)) return;
        foreach (string blob in Directory.EnumerateFiles(blobDirectory, "*.blob", SearchOption.AllDirectories))
        {
            string hash = Path.GetFileNameWithoutExtension(blob);
	        if (!referenced.Contains(hash) && (includeRecent || File.GetLastWriteTimeUtc(blob) < DateTime.UtcNow.AddMinutes(-10))) try { File.Delete(blob); } catch { }
        }
    }

    private void RecoverInterruptedVersionControlCheckpoints(string ownerKey)
    {
        foreach (string activePath in EnumerateOwnerVersionControlManifestFiles(ownerKey, "*.json.active").ToList())
        {
            VersionControlManifest manifest = TryReadVersionControlManifest(activePath);
            if (manifest == null || !ChatOwnerKeysMatch(ownerKey, manifest.OwnerKey) || !ChatSessionBelongsToOwner(manifest.SessionId, ownerKey)) continue;
            try
            {
                manifest.After = CaptureVersionControlSnapshot(ownerKey, manifest.SessionId, false);
                manifest.Changes = CompareVersionControlSnapshots(manifest.Before, manifest.After);
                PopulateVersionControlLineStats(ownerKey, manifest.Changes);
                if (manifest.Changes.Count > 0)
                {
                    manifest.Status = "interrupted";
                    manifest.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                    ProjectVersionControlState state;
                    lock (_chatSessionLock) state = GetProjectVersionControlState(ownerKey, manifest.ProjectId);
                    manifest.BranchName = state.ActiveBranch;
                    manifest.SemanticVersion = AdvanceProjectRestorePointVersion(ownerKey, manifest.ProjectId);
                    WriteJsonAtomic(GetVersionControlManifestPath(ownerKey, manifest.ProjectId, manifest.Id), manifest);
                }
                File.Delete(activePath);
            }
            catch (Exception ex) { LogMessage("[Version Control] Interrupted restore point recovery failed: " + ex.Message); }
        }
    }

    private string RestoreVersionControlFromUndo(string ownerKey, string sessionId, string versionId, HttpRequest request)
    {
        VersionControlManifest manifest = LoadVersionControlManifest(ownerKey, versionId);
        if (manifest == null) return BuildJsonError(request, 404, "Not Found", "Version was not found or is still being finalized.");
        if (!string.Equals(manifest.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return BuildJsonError(request, 403, "Forbidden", "Version belongs to another session.");
        VersionControlRestorePreview preview = PreviewVersionControlRestore(ownerKey, manifest);
        CreateManualVersionControlCheckpoint(ownerKey, sessionId, "Before undoing " + (manifest.Name ?? manifest.Id), false, "safety");
        foreach (VersionControlChange change in preview.Safe) ApplyVersionControlChange(ownerKey, sessionId, change);
        return JsonSerializer.Serialize(new { ok = true, undoToken = versionId, versionId, sessionId, restored = preview.Safe.Count, conflicts = preview.Conflicts.Select(item => item.Path).ToArray(), alreadyRestored = preview.AlreadyRestored.Count });
    }
}
