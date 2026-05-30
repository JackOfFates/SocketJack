using System;
using System.Collections.Generic;

namespace SocketJack.Sandbox {

    public enum SandboxFileSystemMode {
        Disabled,
        AuditOnly,
        ReadOnly,
        RedirectWrites,
        OverlayCopyOnWrite,
        MemoryOverlay,
        FullMemory,
        DenyAll
    }

    public enum SandboxRegistryMode {
        Disabled,
        AuditOnly,
        ReadOnly,
        RedirectWrites,
        OverlayCopyOnWrite,
        MemoryOverlay,
        FullVirtualHive,
        DenyAll
    }

    public enum SandboxMemoryLoadMode {
        None,
        MetadataOnly,
        LazyBlocks,
        HotSet,
        WriteLayerOnly,
        FullAtStart,
        CompressedSnapshot
    }

    public enum SandboxMemoryEvictionMode {
        FailWhenFull,
        LeastRecentlyUsed,
        PreferCleanBlocks,
        CompressColdBlocks,
        SpillColdBlocksToDisk
    }

    public enum SandboxPersistenceMode {
        Discard,
        SnapshotOnClose,
        Journal,
        ContinuousSnapshot,
        CommitWithApproval,
        MirrorToHostAllowlist
    }

    public enum SandboxSnapshotFormat {
        Directory,
        Zip,
        Tar,
        SocketJackImage,
        EncryptedSocketJackImage
    }

    public enum SandboxDecisionLogging {
        Off,
        DeniedOnly,
        MutationsOnly,
        DeniedAndMutations,
        AllPolicyDecisions
    }

    public enum SandboxFileSystemReadPolicy {
        DenyByDefault,
        AllowDeclaredMounts,
        AllowSessionRoot,
        AllowSafeUserFolders,
        AllowAllReadOnly
    }

    public enum SandboxFileSystemWritePolicy {
        Deny,
        MemoryOnly,
        DiskSpillAllowed,
        PersistentSandboxRoot,
        HostWriteThroughAllowlist
    }

    public enum SandboxRegistryReadPolicy {
        DenyByDefault,
        AllowDeclaredKeys,
        AllowSocketJackKeys,
        AllowCurrentUserReadOnly,
        AllowMachineDiagnosticsReadOnly
    }

    public enum SandboxRegistryWritePolicy {
        Deny,
        VirtualOnly,
        CommitWithApproval,
        HostWriteThroughAllowlist
    }

    public enum SandboxMountMode {
        ReadOnly,
        CopyOnWrite,
        WriteThrough,
        MemoryPreload,
        DenyChildren
    }

    public enum SandboxRegistryValueKind {
        None,
        String,
        ExpandString,
        MultiString,
        DWord,
        QWord,
        Binary
    }

    public sealed class SandboxOptions {
        public string ProfileName { get; set; } = "default";
        public string TenantId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public SandboxFileSystemOptions FileSystem { get; set; } = new SandboxFileSystemOptions();
        public SandboxRegistryOptions Registry { get; set; } = new SandboxRegistryOptions();
        public SandboxMemoryOptions Memory { get; set; } = new SandboxMemoryOptions();
        public SandboxPersistenceOptions Persistence { get; set; } = new SandboxPersistenceOptions();
        public SandboxLimits Limits { get; set; } = new SandboxLimits();
        public SandboxAuditOptions Audit { get; set; } = new SandboxAuditOptions();

        public static SandboxOptions CreateMemoryOverlay(string profileName = "memory-overlay") {
            return new SandboxOptions {
                ProfileName = profileName,
                FileSystem = new SandboxFileSystemOptions {
                    Mode = SandboxFileSystemMode.MemoryOverlay,
                    ReadPolicy = SandboxFileSystemReadPolicy.AllowDeclaredMounts,
                    WritePolicy = SandboxFileSystemWritePolicy.MemoryOnly
                },
                Registry = new SandboxRegistryOptions {
                    Mode = SandboxRegistryMode.MemoryOverlay,
                    ReadPolicy = SandboxRegistryReadPolicy.AllowDeclaredKeys,
                    WritePolicy = SandboxRegistryWritePolicy.VirtualOnly
                },
                Memory = new SandboxMemoryOptions {
                    LoadMode = SandboxMemoryLoadMode.WriteLayerOnly,
                    Eviction = SandboxMemoryEvictionMode.FailWhenFull
                }
            };
        }

        public static SandboxOptions CreateLockedDown(string profileName = "locked-down") {
            return new SandboxOptions {
                ProfileName = profileName,
                FileSystem = new SandboxFileSystemOptions {
                    Mode = SandboxFileSystemMode.DenyAll,
                    ReadPolicy = SandboxFileSystemReadPolicy.DenyByDefault,
                    WritePolicy = SandboxFileSystemWritePolicy.Deny
                },
                Registry = new SandboxRegistryOptions {
                    Mode = SandboxRegistryMode.DenyAll,
                    ReadPolicy = SandboxRegistryReadPolicy.DenyByDefault,
                    WritePolicy = SandboxRegistryWritePolicy.Deny
                },
                Persistence = new SandboxPersistenceOptions {
                    Mode = SandboxPersistenceMode.Discard
                }
            };
        }
    }

    public sealed class SandboxFileSystemOptions {
        public SandboxFileSystemMode Mode { get; set; } = SandboxFileSystemMode.MemoryOverlay;
        public SandboxFileSystemReadPolicy ReadPolicy { get; set; } = SandboxFileSystemReadPolicy.AllowDeclaredMounts;
        public SandboxFileSystemWritePolicy WritePolicy { get; set; } = SandboxFileSystemWritePolicy.MemoryOnly;
        public bool WindowsCaseInsensitivePaths { get; set; } = true;
        public bool AllowSymlinks { get; set; }
        public bool FollowSymlinksWithinSandbox { get; set; }
        public bool PublicPathsUseFileIds { get; set; } = true;
        public List<SandboxFileMount> Mounts { get; set; } = new List<SandboxFileMount>();
    }

    public sealed class SandboxRegistryOptions {
        public SandboxRegistryMode Mode { get; set; } = SandboxRegistryMode.MemoryOverlay;
        public SandboxRegistryReadPolicy ReadPolicy { get; set; } = SandboxRegistryReadPolicy.AllowDeclaredKeys;
        public SandboxRegistryWritePolicy WritePolicy { get; set; } = SandboxRegistryWritePolicy.VirtualOnly;
        public bool WindowsCaseInsensitiveKeys { get; set; } = true;
        public List<string> AllowedReadKeys { get; set; } = new List<string>();
        public List<string> AllowedWriteKeys { get; set; } = new List<string>();
    }

    public sealed class SandboxMemoryOptions {
        public SandboxMemoryLoadMode LoadMode { get; set; } = SandboxMemoryLoadMode.WriteLayerOnly;
        public SandboxMemoryEvictionMode Eviction { get; set; } = SandboxMemoryEvictionMode.FailWhenFull;
        public int BlockSize { get; set; } = 64 * 1024;
        public long PreloadBudgetBytes { get; set; } = 16L * 1024L * 1024L;
        public long DirtyPageBudgetBytes { get; set; } = 64L * 1024L * 1024L;
        public bool CompressColdBlocks { get; set; }
        public bool EncryptSnapshots { get; set; }
        public List<string> PreloadPaths { get; set; } = new List<string>();
    }

    public sealed class SandboxPersistenceOptions {
        public SandboxPersistenceMode Mode { get; set; } = SandboxPersistenceMode.SnapshotOnClose;
        public SandboxSnapshotFormat SnapshotFormat { get; set; } = SandboxSnapshotFormat.SocketJackImage;
        public string SnapshotDirectory { get; set; } = "";
        public int KeepLastSnapshots { get; set; } = 5;
        public bool RequireApprovalForCommit { get; set; } = true;
    }

    public sealed class SandboxLimits {
        public long MaxMemoryBytes { get; set; } = 64L * 1024L * 1024L;
        public long MaxDiskSpillBytes { get; set; }
        public long MaxFileBytes { get; set; } = 32L * 1024L * 1024L;
        public int MaxFileCount { get; set; } = 4096;
        public int MaxOpenHandles { get; set; } = 256;
        public int MaxRegistryKeys { get; set; } = 4096;
        public long MaxRegistryValueBytes { get; set; } = 1024L * 1024L;
        public TimeSpan SessionTtl { get; set; } = TimeSpan.FromHours(8);
        public TimeSpan CommitDeadline { get; set; } = TimeSpan.FromSeconds(30);
    }

    public sealed class SandboxAuditOptions {
        public SandboxDecisionLogging DecisionLogging { get; set; } = SandboxDecisionLogging.DeniedAndMutations;
        public bool LogFileSystem { get; set; } = true;
        public bool LogRegistry { get; set; } = true;
        public bool LogMemory { get; set; } = true;
        public bool IncludeContentHashes { get; set; } = true;
        public bool IncludeContentPreview { get; set; }
        public int MaxEvents { get; set; } = 2048;
        public int MaxDetailLength { get; set; } = 1024;
        public long MemoryLogMinimumDeltaBytes { get; set; } = 64L * 1024L;
    }

    public sealed class SandboxFileMount {
        public string VirtualPath { get; set; } = "/";
        public string HostPath { get; set; } = "";
        public SandboxMountMode Mode { get; set; } = SandboxMountMode.ReadOnly;
        public bool AllowImports { get; set; } = true;
        public bool Preload { get; set; }
    }
}
