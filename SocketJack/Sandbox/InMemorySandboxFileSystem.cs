using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Sandbox {

    public sealed class InMemorySandboxFileSystem : ISandboxFileSystem {
        private readonly object _sync = new object();
        private readonly SandboxOptions _options;
        private readonly ISandboxAuditLog _audit;
        private readonly Func<SandboxSessionState> _stateGetter;
        private readonly string _sessionId;
        private readonly Dictionary<string, SandboxFileRecord> _files;
        private readonly Dictionary<string, string> _idToPath;
        private long _memoryBytes;
        private long _lastLoggedMemoryBytes = long.MinValue;

        public InMemorySandboxFileSystem(SandboxOptions options, ISandboxAuditLog audit, Func<SandboxSessionState> stateGetter, string sessionId) {
            _options = options ?? new SandboxOptions();
            _audit = audit ?? new InMemorySandboxAuditLog(_options.Audit);
            _stateGetter = stateGetter ?? (() => SandboxSessionState.Active);
            _sessionId = sessionId ?? "";
            StringComparer comparer = _options.FileSystem.WindowsCaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _files = new Dictionary<string, SandboxFileRecord>(comparer);
            _idToPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public long MemoryBytes {
            get {
                lock (_sync) {
                    return _memoryBytes;
                }
            }
        }

        public bool FileExists(string path) {
            string normalized = NormalizeSandboxPath(path);
            bool exists;
            lock (_sync)
                exists = _files.ContainsKey(normalized);
            RecordFile("exists", normalized, true, false, exists ? "file exists" : "file missing", 0, 0);
            return exists;
        }

        public bool DirectoryExists(string path) {
            string normalized = NormalizeDirectoryPath(path);
            bool exists;
            lock (_sync) {
                exists = normalized == "/" || _files.Keys.Any(item => item.StartsWith(normalized + "/", PathComparison));
            }
            RecordFile("directory_exists", normalized, true, false, exists ? "directory exists" : "directory missing", 0, 0);
            return exists;
        }

        public SandboxFileEntry WriteAllBytes(string path, byte[] contents, SandboxFileWriteOptions options = null) {
            EnsureWritable("write", path);
            string normalized = NormalizeSandboxPath(path);
            options = options ?? new SandboxFileWriteOptions();
            byte[] data = contents == null ? Array.Empty<byte>() : (byte[])contents.Clone();
            EnforceFileQuota(normalized, data.Length, options.Overwrite);

            SandboxFileEntry entry;
            long previousLength = 0;
            long delta;
            lock (_sync) {
                if (_files.TryGetValue(normalized, out SandboxFileRecord existing)) {
                    if (!options.Overwrite)
                        throw new IOException("Sandbox file already exists: " + normalized);
                    previousLength = existing.Content.LongLength;
                    existing.Content = data;
                    existing.Entry.Length = data.LongLength;
                    existing.Entry.Sha256 = _options.Audit.IncludeContentHashes ? ComputeSha256(data) : "";
                    existing.Entry.Kind = string.IsNullOrWhiteSpace(options.Kind) ? existing.Entry.Kind : options.Kind.Trim();
                    existing.Entry.SourcePath = options.SourcePath ?? existing.Entry.SourcePath;
                    existing.Entry.UpdatedUtc = Now();
                    entry = existing.Entry.Clone();
                } else {
                    entry = new SandboxFileEntry {
                        Id = "file_" + Guid.NewGuid().ToString("N"),
                        Path = normalized,
                        Name = Path.GetFileName(normalized.Replace('/', Path.DirectorySeparatorChar)),
                        Kind = string.IsNullOrWhiteSpace(options.Kind) ? "file" : options.Kind.Trim(),
                        Length = data.LongLength,
                        Sha256 = _options.Audit.IncludeContentHashes ? ComputeSha256(data) : "",
                        SourcePath = options.SourcePath ?? "",
                        IsMemoryBacked = true,
                        CreatedUtc = Now(),
                        UpdatedUtc = Now()
                    };
                    _files[normalized] = new SandboxFileRecord { Entry = entry.Clone(), Content = data };
                    _idToPath[entry.Id] = normalized;
                }

                delta = data.LongLength - previousLength;
                _memoryBytes += delta;
            }

            RecordFile("write", normalized, true, true, "wrote sandbox file", data.LongLength, MemoryBytes);
            RecordMemoryIfNeeded("filesystem_write", normalized, delta);
            return entry;
        }

        public Task<SandboxFileEntry> WriteAllBytesAsync(string path, byte[] contents, SandboxFileWriteOptions options = null, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(WriteAllBytes(path, contents, options));
        }

        public SandboxFileEntry WriteAllText(string path, string contents, Encoding encoding = null, SandboxFileWriteOptions options = null) {
            encoding = encoding ?? new UTF8Encoding(false);
            return WriteAllBytes(path, encoding.GetBytes(contents ?? ""), options);
        }

        public byte[] ReadAllBytes(string path) {
            EnsureReadable("read", path);
            string normalized = ResolvePathOrFileId(path);
            lock (_sync) {
                if (!_files.TryGetValue(normalized, out SandboxFileRecord record)) {
                    RecordFile("read", normalized, false, false, "file not found", 0, _memoryBytes);
                    throw new FileNotFoundException("Sandbox file was not found.", normalized);
                }

                RecordFile("read", normalized, true, false, "read sandbox file", record.Content.LongLength, _memoryBytes);
                return (byte[])record.Content.Clone();
            }
        }

        public Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadAllBytes(path));
        }

        public string ReadAllText(string path, Encoding encoding = null) {
            encoding = encoding ?? Encoding.UTF8;
            return encoding.GetString(ReadAllBytes(path));
        }

        public Stream OpenRead(string path) {
            return new MemoryStream(ReadAllBytes(path), writable: false);
        }

        public bool DeleteFile(string path) {
            EnsureWritable("delete", path);
            string normalized = ResolvePathOrFileId(path);
            long delta = 0;
            bool deleted = false;

            lock (_sync) {
                if (_files.TryGetValue(normalized, out SandboxFileRecord record)) {
                    delta = -record.Content.LongLength;
                    _memoryBytes += delta;
                    _idToPath.Remove(record.Entry.Id);
                    _files.Remove(normalized);
                    deleted = true;
                }
            }

            RecordFile("delete", normalized, deleted, true, deleted ? "deleted sandbox file" : "file not found", 0, MemoryBytes);
            if (deleted)
                RecordMemoryIfNeeded("filesystem_delete", normalized, delta);
            return deleted;
        }

        public SandboxFileEntry ImportFile(string hostPath, string sandboxPath = null, bool overwrite = false) {
            if (string.IsNullOrWhiteSpace(hostPath))
                throw new ArgumentException("Host path is required.", nameof(hostPath));
            string fullHostPath = Path.GetFullPath(hostPath);
            if (!IsHostPathAllowed(fullHostPath)) {
                RecordFile("import", fullHostPath, false, false, "host path is not allowed by sandbox policy", 0, MemoryBytes);
                throw new SandboxPolicyException("import", fullHostPath, "Host file import is not allowed by this sandbox policy.");
            }
            if (!File.Exists(fullHostPath))
                throw new FileNotFoundException("Host file was not found.", fullHostPath);

            string target = string.IsNullOrWhiteSpace(sandboxPath) ? "/" + Path.GetFileName(fullHostPath) : sandboxPath;
            byte[] bytes = File.ReadAllBytes(fullHostPath);
            SandboxFileEntry entry = WriteAllBytes(target, bytes, new SandboxFileWriteOptions {
                Overwrite = overwrite,
                Kind = "imported-file",
                SourcePath = fullHostPath
            });
            RecordFile("import", entry.Path, true, true, "imported host file", entry.Length, MemoryBytes);
            return entry;
        }

        public IReadOnlyList<SandboxFileEntry> EnumerateFiles(string path = "/", string searchPattern = "*", bool recursive = true) {
            EnsureReadable("enumerate", path);
            string normalized = NormalizeDirectoryPath(path);
            Regex pattern = BuildWildcardRegex(searchPattern);
            List<SandboxFileEntry> entries;

            lock (_sync) {
                entries = _files.Values
                    .Where(record => IsInDirectory(record.Entry.Path, normalized, recursive))
                    .Where(record => pattern.IsMatch(record.Entry.Name ?? ""))
                    .Select(record => record.Entry.Clone())
                    .OrderBy(entry => entry.Path, _options.FileSystem.WindowsCaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToList();
            }

            RecordFile("enumerate", normalized, true, false, "enumerated " + entries.Count.ToString(CultureInfo.InvariantCulture) + " sandbox files", entries.Count, MemoryBytes);
            return entries;
        }

        public SandboxFileEntry GetFile(string pathOrFileId) {
            string normalized = ResolvePathOrFileId(pathOrFileId);
            lock (_sync) {
                if (!_files.TryGetValue(normalized, out SandboxFileRecord record))
                    throw new FileNotFoundException("Sandbox file was not found.", normalized);
                return record.Entry.Clone();
            }
        }

        public SandboxFileManifest GetManifest() {
            lock (_sync) {
                var files = _files.Values
                    .Select(record => record.Entry.Clone())
                    .OrderBy(entry => entry.Path, _options.FileSystem.WindowsCaseInsensitivePaths ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
                    .ToList();
                return new SandboxFileManifest {
                    SessionId = _sessionId,
                    GeneratedUtc = Now(),
                    Count = files.Count,
                    TotalBytes = files.Sum(file => file.Length),
                    MemoryBytes = _memoryBytes,
                    Files = files
                };
            }
        }

        private void EnsureReadable(string operation, string target) {
            if (_options.FileSystem.Mode == SandboxFileSystemMode.DenyAll) {
                RecordFile(operation, target, false, false, "filesystem sandbox is deny-all", 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Filesystem access is denied by this sandbox.");
            }
        }

        private void EnsureWritable(string operation, string target) {
            SandboxSessionState state = _stateGetter();
            if (state != SandboxSessionState.Active && state != SandboxSessionState.Created) {
                RecordFile(operation, target, false, true, "sandbox session is not writable while " + state, 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Sandbox session is not writable while " + state + ".");
            }
            if (_options.FileSystem.Mode == SandboxFileSystemMode.DenyAll ||
                _options.FileSystem.Mode == SandboxFileSystemMode.ReadOnly ||
                _options.FileSystem.WritePolicy == SandboxFileSystemWritePolicy.Deny) {
                RecordFile(operation, target, false, true, "filesystem writes are denied by sandbox policy", 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Filesystem writes are denied by this sandbox.");
            }
        }

        private void EnforceFileQuota(string path, long newLength, bool overwrite) {
            SandboxLimits limits = _options.Limits ?? new SandboxLimits();
            if (limits.MaxFileBytes > 0 && newLength > limits.MaxFileBytes) {
                RecordQuotaFailure("file_bytes", path, limits.MaxFileBytes, newLength);
                throw new SandboxQuotaException("MaxFileBytes", limits.MaxFileBytes, newLength, "Sandbox file exceeds the per-file limit.");
            }

            lock (_sync) {
                bool exists = _files.ContainsKey(path);
                if (!exists && limits.MaxFileCount > 0 && _files.Count + 1 > limits.MaxFileCount) {
                    RecordQuotaFailure("file_count", path, limits.MaxFileCount, _files.Count + 1);
                    throw new SandboxQuotaException("MaxFileCount", limits.MaxFileCount, _files.Count + 1, "Sandbox file count limit has been reached.");
                }

                long previousLength = exists && overwrite ? _files[path].Content.LongLength : 0;
                long requestedMemory = _memoryBytes - previousLength + newLength;
                if (limits.MaxMemoryBytes > 0 && requestedMemory > limits.MaxMemoryBytes) {
                    RecordQuotaFailure("memory_bytes", path, limits.MaxMemoryBytes, requestedMemory);
                    throw new SandboxQuotaException("MaxMemoryBytes", limits.MaxMemoryBytes, requestedMemory, "Sandbox memory limit has been reached.");
                }
            }
        }

        private void RecordQuotaFailure(string quota, string target, long limit, long requested) {
            RecordFile("quota", target, false, true, quota + " limit exceeded", requested, MemoryBytes);
            RecordMemory("quota_" + quota, target, requested, "limit=" + limit.ToString(CultureInfo.InvariantCulture));
        }

        private void RecordFile(string operation, string target, bool allowed, bool mutation, string reason, long length, long memoryBytes) {
            _audit.Record(new SandboxAuditEvent {
                SessionId = _sessionId,
                Category = "filesystem",
                Operation = operation ?? "",
                Target = target ?? "",
                Allowed = allowed,
                Mutation = mutation,
                Reason = reason ?? "",
                Length = length,
                MemoryBytes = memoryBytes
            });
        }

        private void RecordMemoryIfNeeded(string operation, string target, long deltaBytes) {
            if (!_options.Audit.LogMemory)
                return;

            long current = MemoryBytes;
            long minimumDelta = Math.Max(0, _options.Audit.MemoryLogMinimumDeltaBytes);
            bool shouldLog = _lastLoggedMemoryBytes == long.MinValue ||
                             minimumDelta == 0 ||
                             Math.Abs(current - _lastLoggedMemoryBytes) >= minimumDelta ||
                             Math.Abs(deltaBytes) >= minimumDelta;
            if (!shouldLog)
                return;

            _lastLoggedMemoryBytes = current;
            RecordMemory(operation, target, current, "delta=" + deltaBytes.ToString(CultureInfo.InvariantCulture));
        }

        private void RecordMemory(string operation, string target, long memoryBytes, string detail) {
            _audit.Record(new SandboxAuditEvent {
                SessionId = _sessionId,
                Category = "memory",
                Operation = operation ?? "",
                Target = target ?? "",
                Allowed = true,
                Mutation = false,
                Reason = "sandbox memory telemetry",
                Detail = detail ?? "",
                MemoryBytes = memoryBytes
            });
        }

        private bool IsHostPathAllowed(string fullHostPath) {
            SandboxFileSystemReadPolicy policy = _options.FileSystem.ReadPolicy;
            if (policy == SandboxFileSystemReadPolicy.AllowAllReadOnly)
                return true;
            if (policy == SandboxFileSystemReadPolicy.AllowSafeUserFolders && IsSafeUserFolder(fullHostPath))
                return true;

            foreach (SandboxFileMount mount in _options.FileSystem.Mounts ?? new List<SandboxFileMount>()) {
                if (mount == null || string.IsNullOrWhiteSpace(mount.HostPath) || !mount.AllowImports)
                    continue;
                string root;
                try {
                    root = Path.GetFullPath(mount.HostPath);
                } catch {
                    continue;
                }
                if (IsSameOrChildPath(fullHostPath, root))
                    return mount.Mode != SandboxMountMode.DenyChildren;
            }
            return false;
        }

        private static bool IsSafeUserFolder(string fullHostPath) {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return IsSameOrChildPath(fullHostPath, documents) ||
                   IsSameOrChildPath(fullHostPath, desktop) ||
                   (!string.IsNullOrWhiteSpace(profile) && IsSameOrChildPath(fullHostPath, Path.Combine(profile, "Downloads")));
        }

        private static bool IsSameOrChildPath(string candidate, string root) {
            if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
                return false;
            string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private string ResolvePathOrFileId(string pathOrFileId) {
            string value = pathOrFileId ?? "";
            lock (_sync) {
                if (_idToPath.TryGetValue(value, out string path))
                    return path;
            }
            return NormalizeSandboxPath(value);
        }

        private static string NormalizeSandboxPath(string path) {
            string value = (path ?? "").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Sandbox path is required.", nameof(path));
            if (value.Contains(":", StringComparison.Ordinal))
                throw new ArgumentException("Sandbox paths must be virtual paths, not drive-qualified host paths.", nameof(path));
            if (!value.StartsWith("/", StringComparison.Ordinal))
                value = "/" + value;

            var segments = new List<string>();
            foreach (string raw in value.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)) {
                string segment = raw.Trim();
                if (segment.Length == 0 || segment == ".")
                    continue;
                if (segment == "..")
                    throw new ArgumentException("Sandbox paths cannot contain parent traversal.", nameof(path));
                segments.Add(segment);
            }

            if (segments.Count == 0)
                throw new ArgumentException("Sandbox file path must include a file name.", nameof(path));
            return "/" + string.Join("/", segments);
        }

        private static string NormalizeDirectoryPath(string path) {
            string value = (path ?? "/").Trim().Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(value) || value == "/")
                return "/";
            if (!value.StartsWith("/", StringComparison.Ordinal))
                value = "/" + value;
            value = value.TrimEnd('/');
            if (value.Contains("/../", StringComparison.Ordinal) || value.EndsWith("/..", StringComparison.Ordinal) || value.Contains(":", StringComparison.Ordinal))
                throw new ArgumentException("Sandbox directory path is invalid.", nameof(path));
            return value;
        }

        private StringComparison PathComparison => _options.FileSystem.WindowsCaseInsensitivePaths ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private bool IsInDirectory(string filePath, string directoryPath, bool recursive) {
            if (directoryPath == "/")
                return true;
            if (!filePath.StartsWith(directoryPath + "/", PathComparison))
                return false;
            if (recursive)
                return true;
            string remainder = filePath.Substring(directoryPath.Length + 1);
            return !remainder.Contains("/", StringComparison.Ordinal);
        }

        private Regex BuildWildcardRegex(string searchPattern) {
            string pattern = string.IsNullOrWhiteSpace(searchPattern) ? "*" : searchPattern.Trim();
            string escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
            RegexOptions options = RegexOptions.CultureInvariant;
            if (_options.FileSystem.WindowsCaseInsensitivePaths)
                options |= RegexOptions.IgnoreCase;
            return new Regex("^" + escaped + "$", options);
        }

        private static string ComputeSha256(byte[] data) {
            using (SHA256 sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(data ?? Array.Empty<byte>());
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                    sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static string Now() {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }

        private sealed class SandboxFileRecord {
            public SandboxFileEntry Entry { get; set; } = new SandboxFileEntry();
            public byte[] Content { get; set; } = Array.Empty<byte>();
        }
    }
}
