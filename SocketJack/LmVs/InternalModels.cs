using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LmVs
{
    internal enum ChatUiNativeStreamResult
    {
        Unavailable,
        Completed,
        Failed
    }

    internal enum ChatUiNativeFrameResult
    {
        Continue,
        Complete,
        Failed
    }

    /// <summary>
    /// Represents a request from Visual Studio.
    /// </summary>
    internal sealed class VsRequest
    {
        public string model { get; set; }
        public List<Dictionary<string, object>> messages { get; set; }
        public List<Dictionary<string, object>> tools { get; set; }
        public double? temperature { get; set; }
        public double? top_p { get; set; }
        public int? max_tokens { get; set; }
        public string rawJson { get; set; }
    }

    /// <summary>
    /// Represents a parsed Qwen raw tool call.
    /// </summary>
    internal sealed class ToolCallData
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
        public bool ArgumentsWereMalformed { get; set; }
    }

    internal sealed class ToolCallHistoryEntry
    {
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
    }

    internal sealed class ProxyToolParameter
    {
        public string Name { get; }
        public string Type { get; }
        public string Description { get; }

        public ProxyToolParameter(string name, string type, string description)
        {
            Name = name;
            Type = type;
            Description = description;
        }
    }

    internal sealed class ProxyToolExecutionResult
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string ArgumentsJson { get; set; }
        public string Result { get; set; }
    }

    /// <summary>
    /// Accumulates fragmented native OpenAI streaming tool-call deltas.
    /// </summary>
    internal sealed class ToolCallBuilder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public StringBuilder Arguments { get; } = new StringBuilder();
    }

    internal sealed class ChatUiCompletion
    {
        public string Content { get; set; } = "";
        public string Reasoning { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    internal sealed class ChatUiModelInfo
    {
        public string id { get; set; }
        public bool supportsImages { get; set; }
        public bool supportsTools { get; set; }
    }

    internal sealed class ChatUiServiceInfo
    {
        public string id { get; set; }
        public string name { get; set; }
        public string kind { get; set; }
        public string source { get; set; }
        public string permission { get; set; }
        public bool enabled { get; set; }
        public string description { get; set; }
    }

    internal sealed class ChatSessionSummary
    {
        public string id { get; set; }
        public string title { get; set; }
        public string createdUtc { get; set; }
        public string updatedUtc { get; set; }
        public string model { get; set; }
        public string ownerKey { get; set; }
    }

    internal sealed class ActivePromptSessionState
    {
        public string Id { get; set; }
        public string Source { get; set; }
        public string Title { get; set; }
        public string OwnerKey { get; set; }
        public string Model { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
    }

    internal sealed class ActiveChatStreamCancellation
    {
        public string OwnerKey { get; set; }
        public string StreamId { get; set; }
        public CancellationTokenSource Cancellation { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
    }

    internal sealed class ChatSessionFile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public int size { get; set; }
        public string path { get; set; }
        public string uploadedUtc { get; set; }
    }

    internal sealed class ChatFilesystemAccessEntry
    {
        public string path { get; set; }
        public bool exists { get; set; }
        public string createdUtc { get; set; }
    }

    internal sealed class DirectoryBrowserEntry
    {
        public string name { get; set; }
        public string path { get; set; }
        public bool exists { get; set; }
    }

    internal sealed class SolutionExplorerEntry
    {
        public string name { get; set; } = "";
        public string path { get; set; } = "";
        public string kind { get; set; } = "";
        public string sessionId { get; set; } = "";
        public string type { get; set; } = "";
        public bool exists { get; set; }
        public bool hasChildren { get; set; }
        public string extension { get; set; } = "";
        public long size { get; set; }
        public string modifiedUtc { get; set; } = "";
    }

    internal sealed class ChatFtpServerConfig
    {
        public bool enabled { get; set; } = false;
        public string rootPath { get; set; } = "";
        public int port { get; set; } = 2121;
        public string userName { get; set; } = "lmvsproxy";
        public string password { get; set; } = "";
        public bool allowWrite { get; set; } = true;
        public bool autoStart { get; set; } = false;
        public List<ChatFtpUserConfig> users { get; set; } = new List<ChatFtpUserConfig>();
        public string updatedUtc { get; set; }
    }

    internal sealed class ChatFtpUserConfig
    {
        public string userName { get; set; } = "";
        public string password { get; set; } = "";
        public string rootPath { get; set; } = "";
        public bool allowWrite { get; set; } = true;
    }

    internal sealed class ChatPermissionState
    {
        public bool internetSearch { get; set; } = false;
        public bool vsCopilotTools { get; set; } = true;
        public bool fileDownloads { get; set; } = false;
        public bool ftpServer { get; set; } = false;
        public bool sqlAdmin { get; set; } = false;
        public bool terminalCommands { get; set; } = true;
        public bool terminalForeverApproved { get; set; } = false;
        public bool agentAccess { get; set; } = true;
        public bool fileUploads { get; set; } = true;
        public bool imageUploads { get; set; } = true;
        public string mutedUntilUtc { get; set; } = "";
        public string bannedUntilUtc { get; set; } = "";
        public bool muteUntilEnabled { get; set; } = false;
        public bool banUntilEnabled { get; set; } = false;
        public string updatedUtc { get; set; } = "";
    }

    internal sealed class WebAuthRecord
    {
        public string userName { get; set; } = "";
        public string passwordSalt { get; set; } = "";
        public string passwordHash { get; set; } = "";
        public string createdUtc { get; set; } = "";
        public string updatedUtc { get; set; } = "";
        public string lastLoginUtc { get; set; } = "";
        public string tokenHash { get; set; } = "";
        public string tokenExpiresUtc { get; set; } = "";
        public bool enabled { get; set; } = true;
        public bool isAdministrator { get; set; } = false;
        public string lastClientIp { get; set; } = "";
        public long tokenLimit { get; set; } = 0;
        public long tokensUsed { get; set; } = 0;
    }

    internal sealed class WebAuthRegistrationRequestRecord
    {
        public string id { get; set; } = "";
        public string userName { get; set; } = "";
        public string passwordSalt { get; set; } = "";
        public string passwordHash { get; set; } = "";
        public string clientIp { get; set; } = "";
        public string status { get; set; } = "pending";
        public string requestedUtc { get; set; } = "";
        public string decidedUtc { get; set; } = "";
        public string decidedBy { get; set; } = "";
        public string note { get; set; } = "";
    }

    internal sealed class WebAuthPrincipal
    {
        public string UserName { get; set; } = "";
        public string AuthType { get; set; } = "";
        public string ExpiresUtc { get; set; } = "";
        public bool IsAdministrator { get; set; }
    }

    internal sealed class ChatUsageSnapshot
    {
        public string ownerKey { get; set; } = "";
        public string username { get; set; } = "";
        public bool authenticated { get; set; }
        public bool unlimited { get; set; }
        public long tokensUsed { get; set; }
        public long tokenLimit { get; set; }
        public long tokensRemaining { get; set; }
        public double gpuSecondsUsed { get; set; }
        public double cpuComputeSecondsUsed { get; set; }
        public double ramGbSecondsUsed { get; set; }
        public double systemSecondsUsed { get; set; }
        public long ioBytesProcessed { get; set; }
        public double tokenCostUsd { get; set; }
        public double electricityCostUsd { get; set; }
        public double gpuElectricityCostUsd { get; set; }
        public double cpuElectricityCostUsd { get; set; }
        public double ramElectricityCostUsd { get; set; }
        public double systemElectricityCostUsd { get; set; }
        public double ioElectricityCostUsd { get; set; }
        public double serviceTaxCostUsd { get; set; }
        public double storageCostUsd { get; set; }
        public double totalCostUsd { get; set; }
        public double tokenUsdPerToken { get; set; }
        public double electricityCentsPerKwh { get; set; }
        public double electricityMultiplier { get; set; }
        public double gpuTdpWatts { get; set; }
        public double cpuTdpWatts { get; set; }
        public double ramWattsPerGb { get; set; }
        public double systemWatts { get; set; }
        public double ioWattsPerGbps { get; set; }
        public double ramGbEstimated { get; set; }
        public double serviceTaxRate { get; set; }
        public string storageType { get; set; } = "";
        public double storageBaseUsdPerGb { get; set; }
        public double storageCostFactor { get; set; }
        public long storageBytesUsed { get; set; }
        public long storageLimitBytes { get; set; }
        public long storageBytesRemaining { get; set; }
        public bool storageUnlimited { get; set; }
        public string costSettingsTable { get; set; } = "";
    }

    internal sealed class ChatCostSettings
    {
        public double tokenUsdPerToken { get; set; }
        public double electricityCentsPerKwh { get; set; } = 18.74;
        public double electricityMultiplier { get; set; } = 1;
        public double gpuTdpWatts { get; set; }
        public double cpuTdpWatts { get; set; } = 65;
        public double ramWattsPerGb { get; set; } = 0.375;
        public double systemWatts { get; set; } = 20;
        public double ioWattsPerGbps { get; set; } = 4;
        public double serviceTaxRate { get; set; }
        public string storageType { get; set; } = "Nvme";
        public double storageCostFactor { get; set; }
        public long storageLimitBytes { get; set; } = 500L * 1024L * 1024L;
        public bool allowOpenRegistration { get; set; } = false;
        public string table { get; set; } = "";
    }

    internal sealed class ChatCostTotals
    {
        public string ownerKey { get; set; } = "";
        public long tokensUsed { get; set; }
        public double gpuSecondsUsed { get; set; }
        public double cpuComputeSecondsUsed { get; set; }
        public double ramGbSecondsUsed { get; set; }
        public double systemSecondsUsed { get; set; }
        public long ioBytesProcessed { get; set; }
        public double tokenCostUsd { get; set; }
        public double electricityCostUsd { get; set; }
        public double gpuElectricityCostUsd { get; set; }
        public double cpuElectricityCostUsd { get; set; }
        public double ramElectricityCostUsd { get; set; }
        public double systemElectricityCostUsd { get; set; }
        public double ioElectricityCostUsd { get; set; }
        public double serviceTaxCostUsd { get; set; }
        public double totalCostUsd { get; set; }
    }

    internal sealed class PendingTerminalPermissionRequest
    {
        public PendingTerminalPermissionRequest(TerminalPermissionRequestSnapshot request)
        {
            Request = request;
            Completion = new TaskCompletionSource<TerminalPermissionDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public TerminalPermissionRequestSnapshot Request { get; }
        public TaskCompletionSource<TerminalPermissionDecision> Completion { get; }
    }

    internal sealed class TerminalPermissionDecision
    {
        public TerminalPermissionDecision(bool approved, bool always, bool forever, string reason = "")
        {
            Approved = approved;
            Always = always;
            Forever = forever;
            Reason = reason ?? "";
        }

        public bool Approved { get; }
        public bool Always { get; }
        public bool Forever { get; }
        public string Reason { get; }
    }
}
