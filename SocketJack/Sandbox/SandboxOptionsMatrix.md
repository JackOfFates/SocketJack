# SocketJack Sandbox Options Matrix

Last updated: 2026-05-11

This matrix is the starting option range for `SandboxOptions`. Defaults should be conservative: deny unknown host paths, keep writes virtual, keep registry writes virtual, and enforce memory limits.

## Top-Level Profiles

| Profile | Filesystem | Registry | Memory | Persistence | Intended Use |
|---|---|---|---|---|---|
| `Disabled` | Host passthrough | Host passthrough | None | None | Compatibility only. |
| `AuditOnly` | Host passthrough plus audit | Host passthrough plus audit | Metadata | Audit log | Inventory before migration. |
| `ReadOnlyHost` | Read-only bind mounts | Read-only allowlist | Lazy cache | None | Browsing and diagnostics. |
| `CopyOnWrite` | Host reads, virtual writes | Host reads, virtual writes | Write layer | Snapshot optional | Existing app migration. |
| `MemoryOverlay` | Virtual writes and hot cache | Virtual writes | Bounded memory | Snapshot optional | JackLLM sessions and uploads. |
| `FullMemory` | Preload declared root | Virtual hive preload | Bounded full preload | Snapshot or discard | Short-lived isolated sessions. |
| `LockedDown` | Deny except explicit mounts | Deny except explicit hives | Write layer only | Discard by default | Untrusted tools or public sessions. |

## Filesystem Options

| Option | Values |
|---|---|
| `Mode` | `Disabled`, `AuditOnly`, `ReadOnly`, `RedirectWrites`, `OverlayCopyOnWrite`, `MemoryOverlay`, `FullMemory`, `DenyAll` |
| `RootStrategy` | `EphemeralMemory`, `EphemeralDisk`, `PersistentDirectory`, `SnapshotImage`, `ExistingDirectoryOverlay` |
| `ReadPolicy` | `DenyByDefault`, `AllowDeclaredMounts`, `AllowSessionRoot`, `AllowSafeUserFolders`, `AllowAllReadOnly` |
| `WritePolicy` | `Deny`, `MemoryOnly`, `DiskSpillAllowed`, `PersistentSandboxRoot`, `HostWriteThroughAllowlist` |
| `Mounts` | Real file/directory binds with `ReadOnly`, `CopyOnWrite`, `WriteThrough`, `MemoryPreload`, or `DenyChildren` flags. |
| `PathComparison` | `WindowsCaseInsensitive`, `OrdinalCaseSensitive`, `AutoByPlatform` |
| `SymlinkPolicy` | `Deny`, `PreserveAsLink`, `FollowWithinSandbox`, `FollowDeclaredTargetsOnly` |
| `WatchPolicy` | `Disabled`, `SandboxEventsOnly`, `HostAndSandboxEvents` |
| `PublicPathMode` | `RelativeOnly`, `StableFileId`, `SignedDownloadToken`, `DebugRealPath` |

## Registry Options

| Option | Values |
|---|---|
| `Mode` | `Disabled`, `AuditOnly`, `ReadOnly`, `RedirectWrites`, `OverlayCopyOnWrite`, `MemoryOverlay`, `FullVirtualHive`, `DenyAll` |
| `HiveStrategy` | `EphemeralMemory`, `JsonSnapshot`, `RegFileSnapshot`, `PersistentDirectory`, `ExistingHiveOverlay` |
| `ReadPolicy` | `DenyByDefault`, `AllowDeclaredKeys`, `AllowSocketJackKeys`, `AllowCurrentUserReadOnly`, `AllowMachineDiagnosticsReadOnly` |
| `WritePolicy` | `Deny`, `VirtualOnly`, `CommitWithApproval`, `HostWriteThroughAllowlist` |
| `ValueTypes` | `String`, `ExpandString`, `MultiString`, `DWord`, `QWord`, `Binary`, `None` |
| `KeyComparison` | `WindowsCaseInsensitive`, `OrdinalCaseSensitive` |
| `DiffMode` | `Disabled`, `ValuesOnly`, `KeysAndValues`, `IncludeDeletedValues` |

## Memory Options

| Option | Values |
|---|---|
| `LoadMode` | `None`, `MetadataOnly`, `LazyBlocks`, `HotSet`, `WriteLayerOnly`, `FullAtStart`, `CompressedSnapshot` |
| `BlockSize` | Default 64 KB, configurable for large model/session artifacts. |
| `Eviction` | `FailWhenFull`, `LeastRecentlyUsed`, `PreferCleanBlocks`, `CompressColdBlocks`, `SpillColdBlocksToDisk` |
| `PreloadPaths` | Explicit files/directories to load early. |
| `PreloadBudgetBytes` | Separate cap for eager loading. |
| `DirtyPageBudgetBytes` | Cap for unsaved writes. |
| `Compression` | `None`, `Fast`, `Balanced`, `Maximum` |
| `Encryption` | `None`, `ProcessLocal`, `UserProfile`, `ExplicitKey` |

## Limits

| Limit | Purpose |
|---|---|
| `MaxMemoryBytes` | Hard memory cap for all memory layers in the session. |
| `MaxDiskSpillBytes` | Hard cap for spill files and persistent snapshots. |
| `MaxFileBytes` | Per-file cap for uploads, generated files, and downloads. |
| `MaxFileCount` | Prevents runaway extraction or generated file storms. |
| `MaxOpenHandles` | Protects long-running WPF/server processes. |
| `MaxRegistryKeys` | Prevents unbounded virtual hive growth. |
| `MaxRegistryValueBytes` | Prevents binary registry value abuse. |
| `SessionTtl` | Automatic cleanup window for idle sessions. |
| `CommitDeadline` | Maximum time allowed for snapshot/commit before rollback. |

## Persistence Options

| Option | Values |
|---|---|
| `Mode` | `Discard`, `SnapshotOnClose`, `Journal`, `ContinuousSnapshot`, `CommitWithApproval`, `MirrorToHostAllowlist` |
| `SnapshotFormat` | `Directory`, `Zip`, `Tar`, `SocketJackImage`, `EncryptedSocketJackImage` |
| `ConflictPolicy` | `Fail`, `KeepSandboxVersion`, `KeepHostVersion`, `MergeIfUnchanged`, `AskApproval` |
| `RetentionPolicy` | `DeleteOnClose`, `DeleteAfterTtl`, `KeepLastN`, `KeepUntilReviewed`, `Pinned` |

## Audit Options

| Option | Values |
|---|---|
| `DecisionLogging` | `Off`, `DeniedOnly`, `MutationsOnly`, `AllPolicyDecisions` |
| `ContentLogging` | `None`, `HashesOnly`, `SmallTextPreview`, `FullForTestOnly` |
| `Sink` | In-memory, SocketJack database table, JSONL file, WPF observable stream, web event stream. |
| `Redaction` | Credential paths, secrets, access tokens, private real paths, registry sensitive keys. |

## Recommended Initial Defaults

- Filesystem: `OverlayCopyOnWrite` with declared mounts only.
- Registry: `MemoryOverlay` with host registry writes denied.
- Memory: `WriteLayerOnly` with lazy reads and a small hot-set preload.
- Persistence: `SnapshotOnClose` for named sessions, `Discard` for anonymous sessions.
- Audit: log denied actions and mutations with hashes, not full contents.
