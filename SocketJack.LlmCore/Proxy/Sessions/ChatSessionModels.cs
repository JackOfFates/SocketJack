using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LmVs;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class DeveloperProjectWorkspaceRecord
        {
            public string Id { get; set; } = "";
            public string OwnerKey { get; set; } = "";
            public string Name { get; set; } = "Project Workspace";
            public string WorkspaceRoot { get; set; } = "";
            public string SessionId { get; set; } = "";
            public string RemoteServerId { get; set; } = "";
            public string RemoteServerName { get; set; } = "";
            public string RemoteOwnerUserName { get; set; } = "";
            public string RemoteEndpoint { get; set; } = "";
            public string RemoteOpenAiBaseUrl { get; set; } = "";
            public string LeaseId { get; set; } = "";
            public string Status { get; set; } = "active";
            public string CreatedUtc { get; set; } = "";
            public string UpdatedUtc { get; set; } = "";
            public string Notes { get; set; } = "";
            public string MetadataJson { get; set; } = "{}";
            public string FilesystemContextMode { get; set; } = "none";
            public string FilesystemContextRootsJson { get; set; } = "[]";
            public long FileQuotaBytes { get; set; } = 2147483648L;
            public string CommandPolicy { get; set; } = "restricted";
            public string NetworkPolicy { get; set; } = "deny-private";
            public bool SecretsVaultEnabled { get; set; } = true;
            public bool CleanupOnLeaseEnd { get; set; } = true;
        }

private sealed class AgentFilesystemContextRootEntry
        {
            public string Id { get; set; } = "";
            public string Label { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public string Source { get; set; } = "";
            public string Path { get; set; } = "";
            public bool Exists { get; set; }
        }

private sealed class ChatSessionSummary
        {
            public string id { get; set; } = "";
            public string title { get; set; } = "";
            public string createdUtc { get; set; } = "";
            public string updatedUtc { get; set; } = "";
            public string model { get; set; } = "";
            public string ownerKey { get; set; } = "";
            public int messageCount { get; set; }
            public int fileCount { get; set; }
            public string shareKey { get; set; } = "";
            public bool shareEnabled { get; set; }
            public bool titleLocked { get; set; }
            public string savedUtc { get; set; } = "";
            public bool locked { get; set; }
            public string lockedUtc { get; set; } = "";
            public string clonedFromSessionId { get; set; } = "";
            public long promptTokenCount { get; set; }
            public long promptTokenBudget { get; set; }
            public string runtime { get; set; } = "";
            public string reasoningLevel { get; set; } = "inherit";
            public string projectId { get; set; } = "unsorted";
            public string projectName { get; set; } = "Unsorted";
            public bool pinned { get; set; }
            public string pinnedUtc { get; set; } = "";
            public int commentCount { get; set; }
            public long tokensUsed { get; set; }
            public double gpuSeconds { get; set; }
            public double cpuComputeSeconds { get; set; }
            public double ramGbSeconds { get; set; }
            public long ioBytes { get; set; }
            public ChatSessionCompatibilityPayload compatibility { get; set; }
        }

private sealed class ChatSessionComputeSummary
        {
            public long TokensUsed { get; set; }
            public double GpuSeconds { get; set; }
            public double CpuComputeSeconds { get; set; }
            public double RamGbSeconds { get; set; }
            public long IoBytes { get; set; }
        }

private sealed class ChatSessionListRowSnapshot
        {
            public object[] Row { get; set; }
            public string UpdatedUtc { get; set; } = "";
        }

private sealed class ChatSessionCompatibilityPayload
        {
            public string schema { get; set; } = "lmvsproxy.chat-session.compat.v1";
            public string source { get; set; } = "SocketJack-MagicMasterList";
            public string id { get; set; } = "";
            public string sessionId { get; set; } = "";
            public string ownerKey { get; set; } = "";
            public string title { get; set; } = "";
            public string createdUtc { get; set; } = "";
            public string updatedUtc { get; set; } = "";
            public string model { get; set; } = "";
            public string runtime { get; set; } = "";
            public string reasoningLevel { get; set; } = "inherit";
            public string projectId { get; set; } = "unsorted";
            public string projectName { get; set; } = "Unsorted";
            public bool pinned { get; set; }
            public string pinnedUtc { get; set; } = "";
            public string state { get; set; } = "";
            public bool titleLocked { get; set; }
            public bool locked { get; set; }
            public string lockedUtc { get; set; } = "";
            public string savedUtc { get; set; } = "";
            public string clonedFromSessionId { get; set; } = "";
            public bool shareEnabled { get; set; }
            public int messageCount { get; set; }
            public int fileCount { get; set; }
            public int commentCount { get; set; }
            public long promptTokenCount { get; set; }
            public long promptTokenBudget { get; set; }
            public int promptTokenPercent { get; set; }
            public ChatSessionCompatibilityCapabilities capabilities { get; set; } = new ChatSessionCompatibilityCapabilities();
            public string[] supportedActions { get; set; } = new[] { "save", "checkpoint", "rename", "lock", "unlock", "clone", "pin", "unpin", "assign-project", "delete" };
        }

private sealed class ChatPrivatePayloadCacheEntry
        {
            public string Text { get; set; } = "";
            public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        }

private sealed class SolutionExplorerStorageCacheEntry
        {
            public object Metrics { get; set; }
            public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        }

private sealed class ChatSessionListCacheEntry
        {
            public IReadOnlyList<ChatSessionSummary> Sessions { get; set; } = Array.Empty<ChatSessionSummary>();
            public int Total { get; set; }
            public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        }

private sealed class ChatUsageSnapshotCacheEntry
        {
            public ChatUsageSnapshot Snapshot { get; set; }
            public int Version { get; set; }
            public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        }

private sealed class ChatSessionCompatibilityCapabilities
        {
            public bool canSave { get; set; } = true;
            public bool canRename { get; set; } = true;
            public bool canClone { get; set; } = true;
            public bool canLock { get; set; }
            public bool canUnlock { get; set; }
            public bool canModify { get; set; }
            public bool canDelete { get; set; }
        }

private sealed class ChatSessionComment
        {
            public string id { get; set; } = "";
            public string sessionId { get; set; } = "";
            public string ownerKey { get; set; } = "";
            public string authorKey { get; set; } = "";
            public string authorName { get; set; } = "";
            public string body { get; set; } = "";
            public string createdUtc { get; set; } = "";
        }

private sealed class ChatUsageMeter
        {
            public DateTimeOffset LastGpuChargeUtc { get; set; } = DateTimeOffset.UtcNow;
            public DateTimeOffset LastUsageChargeUtc { get; set; } = DateTimeOffset.MinValue;
            public DateTimeOffset LastUsageEventWriteUtc { get; set; } = DateTimeOffset.MinValue;
            public TimeSpan? LastProcessCpuTime { get; set; }
            public ulong? LastIoBytes { get; set; }
            public double PendingGpuSeconds { get; set; }
            public double PendingCpuComputeSeconds { get; set; }
            public double PendingRamGbSeconds { get; set; }
            public double PendingSystemSeconds { get; set; }
            public long PendingIoBytes { get; set; }
            public double PendingRamGbEstimated { get; set; }
            public int PendingChargeTokenDelta { get; set; }
            public int PendingUsageTokenDelta { get; set; }
            public double PendingUsageGpuSecondsDelta { get; set; }
        }

private sealed class ChatResourceUsageDelta
        {
            public double gpuSeconds { get; set; }
            public double cpuComputeSeconds { get; set; }
            public double ramGbSeconds { get; set; }
            public double systemSeconds { get; set; }
            public long ioBytes { get; set; }
            public double ramGbEstimated { get; set; }
        }

private sealed class ChatUiNativeStreamState
        {
            public StringBuilder Content { get; } = new StringBuilder();
            public StringBuilder Reasoning { get; } = new StringBuilder();
            public string FinishReason { get; set; } = "";
            public DateTimeOffset LastProgressWriteUtc { get; set; } = DateTimeOffset.MinValue;
            public string LastProgressPhase { get; set; } = "";
            public string LastProgressStatus { get; set; } = "";
            public double? LastProgressValue { get; set; }
            public long LastPromptTokensLoaded { get; set; }
            public long LastPromptTokensTotal { get; set; }
        }
    }
}
