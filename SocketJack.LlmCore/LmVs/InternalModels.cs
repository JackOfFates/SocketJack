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
        public string FinishReason { get; set; } = "";
    }

    internal sealed class ChatUiModelInfo
    {
        public string id { get; set; }
        public bool supportsImages { get; set; }
        public bool supportsVision
        {
            get { return supportsImages; }
            set { supportsImages = value; }
        }
        public bool supportsTools { get; set; }
        public bool isLoaded { get; set; }
        public bool isAvailable { get; set; }
        public bool enabled { get; set; }
        public bool isEnabled
        {
            get { return enabled; }
            set { enabled = value; }
        }
        public bool disabled { get; set; }
        public string status { get; set; } = "";
        public bool dynamicLoadEnabled { get; set; }
        public bool serverDynamicLoadingEnabled { get; set; }
        public bool web_chat_dynamic_load_enabled { get; set; }
        public bool web_chat_model_load_api_enabled { get; set; }
        public bool web_chat_model_enabled { get; set; }
        public int web_chat_idle_unload_minutes { get; set; }
        public string web_chat_load_disabled_reason { get; set; } = "";
        public string loadDisabledReason { get; set; } = "";
        public bool benchmarked { get; set; }
        public double tokensPerSecond { get; set; }
        public double tokensPerHour { get; set; }
        public int outputTokens { get; set; }
        public double benchmarkDurationMs { get; set; }
        public int benchmarkRating { get; set; }
        public int rating { get; set; }
        public int score { get; set; }
        public string benchmarkedUtc { get; set; } = "";
        public string benchmarkStatus { get; set; } = "";
        public bool requiresPayment { get; set; }
        public string stripePriceId { get; set; } = "";
        public string stripeAccount { get; set; } = "";
        public long stripeUnitAmountCents { get; set; }
        public string stripeCurrency { get; set; } = "usd";
        public string paymentStatus { get; set; } = "";
        public bool checkoutConfigured { get; set; }
        public bool supportsImageGeneration { get; set; }
        public bool imageGeneration
        {
            get { return supportsImageGeneration; }
            set { supportsImageGeneration = value; }
        }
        public bool supportsAudioGeneration { get; set; }
        public bool audioGeneration
        {
            get { return supportsAudioGeneration; }
            set { supportsAudioGeneration = value; }
        }
        public bool supportsVideoGeneration { get; set; }
        public bool videoGeneration
        {
            get { return supportsVideoGeneration; }
            set { supportsVideoGeneration = value; }
        }
    }

    internal sealed class ChatUiModelInventorySnapshot
    {
        public bool ok { get; set; } = true;
        public string selected { get; set; } = "";
        public bool modelListAvailable { get; set; }
        public string warning { get; set; } = "";
        public string error { get; set; } = "";
        public bool dynamicLoadingEnabled { get; set; }
        public bool serverDynamicLoadingEnabled { get; set; }
        public bool web_chat_model_load_api_enabled { get; set; }
        public int modelCount { get; set; }
        public int availableModelCount { get; set; }
        public int loadedModelCount { get; set; }
        public int enabledModelCount { get; set; }
        public int disabledModelCount { get; set; }
        public string availableModels { get; set; } = "";
        public string loadedModels { get; set; } = "";
        public string enabledModels { get; set; } = "";
        public string disabledModels { get; set; } = "";
        public string modelBenchmarksJson { get; set; } = "";
        public int benchmarkedModelCount { get; set; }
        public List<ChatUiModelInfo> models { get; set; } = new List<ChatUiModelInfo>();
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
        public int messageCount { get; set; }
        public int fileCount { get; set; }
        public bool shareEnabled { get; set; }
        public string shareKey { get; set; }
        public int commentCount { get; set; }
        public bool titleLocked { get; set; }
        public bool locked { get; set; }
        public string savedUtc { get; set; }
        public string lockedUtc { get; set; }
        public string clonedFromSessionId { get; set; }
        public long promptTokenCount { get; set; }
        public long promptTokenBudget { get; set; }
    }

    internal sealed class ChatSessionComment
    {
        public string id { get; set; }
        public string sessionId { get; set; }
        public string ownerKey { get; set; }
        public string authorKey { get; set; }
        public string authorName { get; set; }
        public string body { get; set; }
        public string createdUtc { get; set; }
    }

    internal sealed class ChatMemoryRecord
    {
        public string id { get; set; } = "";
        public string ownerKey { get; set; } = "";
        public string text { get; set; } = "";
        public string sourceSessionId { get; set; } = "";
        public string createdUtc { get; set; } = "";
        public string updatedUtc { get; set; } = "";
        public string deletedUtc { get; set; } = "";
        public string category { get; set; } = "";
    }

    internal sealed class ActivePromptSessionState
    {
        public string Id { get; set; }
        public string SessionId { get; set; }
        public string Source { get; set; }
        public string Title { get; set; }
        public string OwnerKey { get; set; }
        public string ParticipantKey { get; set; }
        public string StreamId { get; set; }
        public string Model { get; set; }
        public string Runtime { get; set; }
        public string Status { get; set; }
        public string Phase { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public DateTimeOffset? CompletedUtc { get; set; }
        public long PromptTokenCount { get; set; }
        public long PromptTokenBudget { get; set; }
        public string PromptText { get; set; }
        public string ReferencesText { get; set; }
        public string AnswerContent { get; set; }
        public string ReasoningContent { get; set; }
    }

    internal sealed class ActiveChatStreamCancellation
    {
        public string OwnerKey { get; set; }
        public string StreamId { get; set; }
        public string SessionId { get; set; }
        public CancellationTokenSource Cancellation { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public string MediaToolCallId { get; set; }
        public string MediaJobId { get; set; }
        public List<string> SteeringMessages { get; } = new List<string>();
    }

    internal sealed class PendingChatStreamSteering
    {
        public string OwnerKey { get; set; }
        public string StreamId { get; set; }
        public string SessionId { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public List<string> Messages { get; } = new List<string>();
    }

    internal sealed class ChatSessionFile
    {
        public string id { get; set; }
        public string name { get; set; }
        public string type { get; set; }
        public int size { get; set; }
        public string path { get; set; }
        public string uploadedUtc { get; set; }
        public bool sandboxed { get; set; }
        public string sandboxSessionId { get; set; } = "";
        public string sandboxFileId { get; set; } = "";
        public string sandboxPath { get; set; } = "";
        public string sandboxSha256 { get; set; } = "";
        public string contentBase64 { get; set; } = "";
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
        public bool sandboxed { get; set; }
        public string sandboxSessionId { get; set; } = "";
        public string sandboxFileId { get; set; } = "";
        public string sandboxPath { get; set; } = "";
        public string sandboxSha256 { get; set; } = "";
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

    internal sealed class PersonaPlexSettings
    {
        public bool enabled { get; set; } = true;
        public string serverUrl { get; set; } = "ws://localhost:8998";
        public string voicePrompt { get; set; } = "NATF0.pt";
        public string personaPrompt { get; set; } = "You are a wise and friendly teacher. Answer questions or provide advice in a clear and engaging way.";
        public string updatedUtc { get; set; } = "";
    }

    internal sealed class PersonaPlexInstallState
    {
        public string id { get; set; } = "";
        public string status { get; set; } = "idle";
        public string message { get; set; } = "";
        public int progress { get; set; }
        public bool localInstalled { get; set; }
        public bool serverRunning { get; set; }
        public bool hfTokenAvailable { get; set; }
        public bool needsHuggingFaceToken { get; set; }
        public bool hfTokenValidated { get; set; }
        public bool hfTokenRejected { get; set; }
        public string hfTokenError { get; set; } = "";
        public bool installFootprintFound { get; set; }
        public bool installIncomplete { get; set; }
        public bool runtimeSupported { get; set; } = true;
        public string runtimeCompatibilityError { get; set; } = "";
        public List<string> missingComponents { get; set; } = new List<string>();
        public string installRoot { get; set; } = "";
        public string repositoryPath { get; set; } = "";
        public string pythonExecutable { get; set; } = "";
        public string logPath { get; set; } = "";
        public string startedUtc { get; set; } = "";
        public string updatedUtc { get; set; } = "";
        public string completedUtc { get; set; } = "";
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
        public bool pcAccess { get; set; } = false;
        public bool dreamInternetSearch { get; set; }
        public bool dreamVsCopilotTools { get; set; }
        public bool dreamFileDownloads { get; set; }
        public bool dreamFtpServer { get; set; }
        public bool dreamSqlAdmin { get; set; }
        public bool dreamTerminalCommands { get; set; }
        public bool dreamAgentAccess { get; set; }
        public bool dreamFileUploads { get; set; }
        public bool dreamImageUploads { get; set; }
        public bool dreamPcAccess { get; set; }
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

    internal sealed class TokenRateRequestRecord
    {
        public string id { get; set; } = "";
        public string userName { get; set; } = "";
        public string ownerKey { get; set; } = "";
        public string clientIp { get; set; } = "";
        public long requestedTokenRate { get; set; }
        public long advertisedTokenRate { get; set; }
        public long minTokenRate { get; set; }
        public long maxTokenRate { get; set; }
        public string status { get; set; } = "pending";
        public string requestedUtc { get; set; } = "";
        public string decidedUtc { get; set; } = "";
        public string decidedBy { get; set; } = "";
        public string note { get; set; } = "";
    }

    internal sealed class TrustAbuseCaseRecord
    {
        public string id { get; set; } = "";
        public string category { get; set; } = "dispute";
        public string severity { get; set; } = "medium";
        public string status { get; set; } = "open";
        public string subjectType { get; set; } = "";
        public string subjectId { get; set; } = "";
        public string ownerKey { get; set; } = "";
        public string userName { get; set; } = "";
        public string serverId { get; set; } = "";
        public string serverName { get; set; } = "";
        public string externalIp { get; set; } = "";
        public string reporterOwnerKey { get; set; } = "";
        public string reporterUserName { get; set; } = "";
        public string summary { get; set; } = "";
        public string detail { get; set; } = "";
        public string evidenceJson { get; set; } = "";
        public string command { get; set; } = "";
        public string path { get; set; } = "";
        public int reputationDelta { get; set; }
        public string createdUtc { get; set; } = "";
        public string updatedUtc { get; set; } = "";
        public string closedUtc { get; set; } = "";
        public string admin { get; set; } = "";
        public string intervention { get; set; } = "";
        public string adminNote { get; set; } = "";
    }

    internal sealed class WebAuthPrincipal
    {
        public string UserName { get; set; } = "";
        public string AuthType { get; set; } = "";
        public string AccountType { get; set; } = "";
        public string AccessToken { get; set; } = "";
        public string EncryptionSecret { get; set; } = "";
        public string ExpiresUtc { get; set; } = "";
        public bool IsAdministrator { get; set; }
        public bool IsServerOwner { get; set; }
        public bool IsPublicAccount { get; set; }
        public string ServerOwnerUserName { get; set; } = "";
    }

    internal sealed class ChatUsageSnapshot
    {
        public string ownerKey { get; set; } = "";
        public string username { get; set; } = "";
        public bool authenticated { get; set; }
        public bool unlimited { get; set; }
        public bool tokensRequired { get; set; } = true;
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
        public double storageCostFactor { get; set; } = 1;
        public long storageBytesUsed { get; set; }
        public long storageLimitBytes { get; set; }
        public long storageBytesRemaining { get; set; }
        public bool storageUnlimited { get; set; }
        public long currentSessionStorageBytes { get; set; }
        public string currentSessionId { get; set; } = "";
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
        public double storageCostFactor { get; set; } = 1;
        public long storageLimitBytes { get; set; } = 500L * 1024L * 1024L;
        public bool allowOpenRegistration { get; set; } = false;
        public bool tokenRateRequestsEnabled { get; set; } = false;
        public long tokenRateRequestMin { get; set; } = 1000;
        public long tokenRateRequestMax { get; set; } = 100000;
        public long advertisedTokenRate { get; set; } = 0;
        public int throughputTokensPerSecond { get; set; } = 0;
        public string table { get; set; } = "";
    }

    internal sealed class ChatThroughputThrottleState
    {
        public DateTimeOffset NextWriteUtc { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.MinValue;
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
