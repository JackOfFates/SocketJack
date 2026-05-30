using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Sandbox {

    public enum SandboxSessionState {
        Created,
        Active,
        Frozen,
        Committed,
        Discarded,
        Expired,
        Disposed
    }

    public interface ISandboxSession : IAsyncDisposable {
        string Id { get; }
        SandboxOptions Options { get; }
        SandboxSessionState State { get; }
        ISandboxFileSystem FileSystem { get; }
        ISandboxRegistry Registry { get; }
        ISandboxAuditLog Audit { get; }
        Task<SandboxSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
        void Freeze();
        void Commit();
        void Discard();
    }

    public interface ISandboxFileSystem {
        long MemoryBytes { get; }
        bool FileExists(string path);
        bool DirectoryExists(string path);
        SandboxFileEntry WriteAllBytes(string path, byte[] contents, SandboxFileWriteOptions options = null);
        Task<SandboxFileEntry> WriteAllBytesAsync(string path, byte[] contents, SandboxFileWriteOptions options = null, CancellationToken cancellationToken = default);
        SandboxFileEntry WriteAllText(string path, string contents, Encoding encoding = null, SandboxFileWriteOptions options = null);
        byte[] ReadAllBytes(string path);
        Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default);
        string ReadAllText(string path, Encoding encoding = null);
        Stream OpenRead(string path);
        bool DeleteFile(string path);
        SandboxFileEntry ImportFile(string hostPath, string sandboxPath = null, bool overwrite = false);
        IReadOnlyList<SandboxFileEntry> EnumerateFiles(string path = "/", string searchPattern = "*", bool recursive = true);
        SandboxFileEntry GetFile(string pathOrFileId);
        SandboxFileManifest GetManifest();
    }

    public interface ISandboxRegistry {
        long MemoryBytes { get; }
        SandboxRegistryValue SetValue(string key, string name, object value, SandboxRegistryValueKind kind = SandboxRegistryValueKind.String);
        bool TryGetValue(string key, string name, out SandboxRegistryValue value);
        bool DeleteValue(string key, string name);
        bool DeleteKey(string key, bool recursive = false);
        IReadOnlyList<string> EnumerateKeys(string key = "");
        SandboxRegistrySnapshot Snapshot();
    }

    public interface ISandboxAuditLog {
        event EventHandler<SandboxAuditEventArgs> EventRecorded;
        IReadOnlyList<SandboxAuditEvent> Events { get; }
        void Record(SandboxAuditEvent auditEvent);
    }

    public sealed class SandboxFileWriteOptions {
        public bool Overwrite { get; set; } = true;
        public string Kind { get; set; } = "file";
        public string SourcePath { get; set; } = "";
    }

    public sealed class SandboxFileEntry {
        public string Id { get; set; } = "";
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "file";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
        public string SourcePath { get; set; } = "";
        public bool IsMemoryBacked { get; set; } = true;
        public bool IsDeleted { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";

        public SandboxFileEntry Clone() {
            return (SandboxFileEntry)MemberwiseClone();
        }
    }

    public sealed class SandboxFileManifest {
        public string SessionId { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public int Count { get; set; }
        public long TotalBytes { get; set; }
        public long MemoryBytes { get; set; }
        public List<SandboxFileEntry> Files { get; set; } = new List<SandboxFileEntry>();
    }

    public sealed class SandboxRegistryValue {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public SandboxRegistryValueKind Kind { get; set; } = SandboxRegistryValueKind.String;
        public object Value { get; set; }
        public long SizeBytes { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";

        public SandboxRegistryValue Clone() {
            return new SandboxRegistryValue {
                Key = Key,
                Name = Name,
                Kind = Kind,
                Value = SandboxValueCloner.CloneValue(Value),
                SizeBytes = SizeBytes,
                CreatedUtc = CreatedUtc,
                UpdatedUtc = UpdatedUtc
            };
        }
    }

    public sealed class SandboxRegistrySnapshot {
        public string SessionId { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public int KeyCount { get; set; }
        public int ValueCount { get; set; }
        public long MemoryBytes { get; set; }
        public List<SandboxRegistryValue> Values { get; set; } = new List<SandboxRegistryValue>();
    }

    public sealed class SandboxSnapshot {
        public string SessionId { get; set; } = "";
        public string ProfileName { get; set; } = "";
        public string State { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string GeneratedUtc { get; set; } = "";
        public SandboxFileManifest Files { get; set; } = new SandboxFileManifest();
        public SandboxRegistrySnapshot Registry { get; set; } = new SandboxRegistrySnapshot();
        public List<SandboxAuditEvent> AuditEvents { get; set; } = new List<SandboxAuditEvent>();
    }

    public sealed class SandboxAuditEvent {
        public string Id { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Category { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Target { get; set; } = "";
        public bool Allowed { get; set; } = true;
        public bool Mutation { get; set; }
        public string Reason { get; set; } = "";
        public string Detail { get; set; } = "";
        public long Length { get; set; }
        public long MemoryBytes { get; set; }
        public string CreatedUtc { get; set; } = "";

        public SandboxAuditEvent Clone() {
            return (SandboxAuditEvent)MemberwiseClone();
        }
    }

    public sealed class SandboxAuditEventArgs : EventArgs {
        public SandboxAuditEventArgs(SandboxAuditEvent auditEvent) {
            Event = auditEvent;
        }

        public SandboxAuditEvent Event { get; }
    }

    public class SandboxPolicyException : InvalidOperationException {
        public SandboxPolicyException(string operation, string target, string message) : base(message) {
            Operation = operation ?? "";
            Target = target ?? "";
        }

        public string Operation { get; }
        public string Target { get; }
    }

    public class SandboxQuotaException : InvalidOperationException {
        public SandboxQuotaException(string quota, long limit, long requested, string message) : base(message) {
            Quota = quota ?? "";
            Limit = limit;
            Requested = requested;
        }

        public string Quota { get; }
        public long Limit { get; }
        public long Requested { get; }
    }

    internal static class SandboxValueCloner {
        public static object CloneValue(object value) {
            if (value is byte[] bytes)
                return (byte[])bytes.Clone();
            if (value is string[] strings)
                return (string[])strings.Clone();
            return value;
        }
    }
}
