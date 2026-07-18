using System;
using System.Collections.Generic;

namespace LmVs
{
    public sealed class ChatSessionDiagnosticsSnapshot
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string Model { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public int MessageCount { get; set; }
        public int FileCount { get; set; }
        public bool TitleLocked { get; set; }
        public bool Locked { get; set; }
        public string LockedUtc { get; set; } = "";
        public string SavedUtc { get; set; } = "";
        public string ClonedFromSessionId { get; set; } = "";
        public long PromptTokenCount { get; set; }
        public long PromptTokenBudget { get; set; }
    }

    public sealed class ActivePromptSessionDiagnosticsSnapshot
    {
        public string Id { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string StreamId { get; set; } = "";
        public string Model { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string Status { get; set; } = "";
        public string Phase { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public int ElapsedSeconds { get; set; }
        public long PromptTokenCount { get; set; }
        public long PromptTokenBudget { get; set; }
        public string PromptText { get; set; } = "";
        public string ReferencesText { get; set; } = "";
        public string AnswerContent { get; set; } = "";
        public string ReasoningContent { get; set; } = "";
    }

    public sealed class ChatPerformanceDiagnosticsSnapshot
    {
        public long TotalTokensUsed { get; set; }
        public int OwnerCount { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }

    public sealed class ObservabilityRouteSnapshot
    {
        public string Method { get; set; } = "";
        public string Route { get; set; } = "";
        public string Category { get; set; } = "";
        public long Count { get; set; }
        public long Failures { get; set; }
        public double FailureRate { get; set; }
        public double AverageLatencyMs { get; set; }
        public double MaxLatencyMs { get; set; }
        public long TotalTokens { get; set; }
        public string FirstSeenUtc { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
    }

    public sealed class ObservabilityRecentEventSnapshot
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string Detail { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Route { get; set; } = "";
        public long Tokens { get; set; }
        public double LatencyMs { get; set; }
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class ObservabilitySnapshot
    {
        public bool Ok { get; set; } = true;
        public string GeneratedUtc { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public double UptimeSeconds { get; set; }
        public double HealthScore { get; set; }
        public string HealthTier { get; set; } = "";
        public long TotalRequests { get; set; }
        public long FailedRequests { get; set; }
        public double FailureRate { get; set; }
        public double AverageLatencyMs { get; set; }
        public long TotalTokens { get; set; }
        public long TotalPromptSessions { get; set; }
        public long CompletedPromptSessions { get; set; }
        public long FailedPromptSessions { get; set; }
        public int ActivePromptSessions { get; set; }
        public long ActivePromptHighWatermark { get; set; }
        public int ChatSessions { get; set; }
        public int OwnerCount { get; set; }
        public int WebAuthUsers { get; set; }
        public int MasterServers { get; set; }
        public int TrustOpenCases { get; set; }
        public int PendingFilesystemRequests { get; set; }
        public int PendingTerminalRequests { get; set; }
        public int PendingTokenRateRequests { get; set; }
        public int PendingRegistrationRequests { get; set; }
        public int FinancePayments { get; set; }
        public object Hardware { get; set; }
        public object Services { get; set; }
        public object Security { get; set; }
        public object Marketplace { get; set; }
        public object DeveloperWorkflow { get; set; }
        public object Pending { get; set; }
        public object Database { get; set; }
        public List<ObservabilityRouteSnapshot> Routes { get; } = new List<ObservabilityRouteSnapshot>();
        public List<ObservabilityRecentEventSnapshot> RecentEvents { get; } = new List<ObservabilityRecentEventSnapshot>();
    }

    public sealed class ChatFilesystemAccessSnapshot
    {
        public string Path { get; set; } = "";
        public bool Exists { get; set; }
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class GpuTdpDetectionSnapshot
    {
        public string GpuName { get; set; } = "";
        public double TdpWatts { get; set; }
        public string Source { get; set; } = "";
        public string Detail { get; set; } = "";
        public bool IsNvidia { get; set; }
    }

    public sealed class FilesystemPermissionRequestSnapshot
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string ClientIp { get; set; } = "";
        public string RequestedPath { get; set; } = "";
        public string FullPath { get; set; } = "";
        public string DirectoryPath { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Reason { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class FilesystemPermissionRequestEventArgs : EventArgs
    {
        public FilesystemPermissionRequestEventArgs(FilesystemPermissionRequestSnapshot request)
        {
            Request = request;
        }

        public FilesystemPermissionRequestSnapshot Request { get; }
    }

    public sealed class TerminalPermissionRequestSnapshot
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string ClientIp { get; set; } = "";
        public string Command { get; set; } = "";
        public string Shell { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Summary { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class TerminalPermissionRequestEventArgs : EventArgs
    {
        public TerminalPermissionRequestEventArgs(TerminalPermissionRequestSnapshot request)
        {
            Request = request;
        }

        public TerminalPermissionRequestSnapshot Request { get; }
    }

    public sealed class WebAuthRegistrationRequestSnapshot
    {
        public string Id { get; set; } = "";
        public string UserName { get; set; } = "";
        public string ClientIp { get; set; } = "";
        public string Status { get; set; } = "";
        public string RequestedUtc { get; set; } = "";
        public string DecidedUtc { get; set; } = "";
        public string DecidedBy { get; set; } = "";
        public string Note { get; set; } = "";
    }

    public sealed class WebAuthRegistrationRequestEventArgs : EventArgs
    {
        public WebAuthRegistrationRequestEventArgs(WebAuthRegistrationRequestSnapshot request)
        {
            Request = request;
        }

        public WebAuthRegistrationRequestSnapshot Request { get; }
    }

    public sealed class TokenRateRequestSnapshot
    {
        public string Id { get; set; } = "";
        public string UserName { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string ClientIp { get; set; } = "";
        public long RequestedTokenRate { get; set; }
        public long AdvertisedTokenRate { get; set; }
        public long MinTokenRate { get; set; }
        public long MaxTokenRate { get; set; }
        public string Status { get; set; } = "";
        public string RequestedUtc { get; set; } = "";
        public string DecidedUtc { get; set; } = "";
        public string DecidedBy { get; set; } = "";
        public string Note { get; set; } = "";
    }

    public sealed class TokenRateRequestEventArgs : EventArgs
    {
        public TokenRateRequestEventArgs(TokenRateRequestSnapshot request)
        {
            Request = request;
        }

        public TokenRateRequestSnapshot Request { get; }
    }

    public sealed class TrustAbuseCaseSnapshot
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "";
        public string Severity { get; set; } = "";
        public string Status { get; set; } = "";
        public string SubjectType { get; set; } = "";
        public string SubjectId { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string UserName { get; set; } = "";
        public string ServerId { get; set; } = "";
        public string ServerName { get; set; } = "";
        public string ExternalIp { get; set; } = "";
        public string ReporterOwnerKey { get; set; } = "";
        public string ReporterUserName { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Detail { get; set; } = "";
        public string EvidenceJson { get; set; } = "";
        public string Command { get; set; } = "";
        public string Path { get; set; } = "";
        public int ReputationDelta { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string ClosedUtc { get; set; } = "";
        public string Admin { get; set; } = "";
        public string Intervention { get; set; } = "";
        public string AdminNote { get; set; } = "";
        public bool IsOpen { get; set; }
    }

    public sealed class TrustAbuseSummarySnapshot
    {
        public string OwnerKey { get; set; } = "";
        public string ServerId { get; set; } = "";
        public int TotalCases { get; set; }
        public int OpenCases { get; set; }
        public int DisputeCount { get; set; }
        public int HostVerificationCount { get; set; }
        public int RenterIdentityCount { get; set; }
        public int ContentAbuseCount { get; set; }
        public int CommandAbuseCount { get; set; }
        public int CriticalOpenCases { get; set; }
        public int HighOpenCases { get; set; }
        public int StaleOpenCases { get; set; }
        public int OpenInterventionCases { get; set; }
        public int ReputationScore { get; set; } = 100;
        public string ReputationTier { get; set; } = "Good";
        public int RiskScore { get; set; }
        public string RiskTier { get; set; } = "Low";
        public string HostVerificationStatus { get; set; } = "unverified";
        public string RenterIdentityStatus { get; set; } = "unknown";
        public bool HostVerified { get; set; }
        public bool RenterIdentityVerified { get; set; }
        public bool CommandsBlocked { get; set; }
        public bool HostSuspended { get; set; }
        public bool NeedsAdminIntervention { get; set; }
        public string LastCaseUtc { get; set; } = "";
        public string LastInterventionUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    public sealed class TrustAbusePolicySnapshot
    {
        public bool BlockHighRiskTerminalCommands { get; set; } = true;
        public bool RequireAuthenticatedRenters { get; set; } = true;
        public bool RequireVerifiedHostsForPreferredListing { get; set; } = true;
        public string Table { get; set; } = "";
    }

    public sealed class TokenRateRequestPolicySnapshot
    {
        public bool Enabled { get; set; }
        public long MinTokenRate { get; set; }
        public long MaxTokenRate { get; set; }
        public long AdvertisedTokenRate { get; set; }
        public string Table { get; set; } = "";
    }

    public sealed class WebAuthUserDiagnosticsSnapshot
    {
        public string UserName { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public bool Enabled { get; set; }
        public bool IsAdministrator { get; set; }
        public string LastClientIp { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string LastLoginUtc { get; set; } = "";
        public long TokenLimit { get; set; }
        public long TokensUsed { get; set; }
        public long TokensRemaining { get; set; }
        public bool Unlimited { get; set; }
        public string MutedUntilUtc { get; set; } = "";
        public string BannedUntilUtc { get; set; } = "";
        public bool MuteUntilEnabled { get; set; }
        public bool BanUntilEnabled { get; set; }
        public bool IsMuted { get; set; }
        public bool IsBanned { get; set; }
    }

    public sealed class ChatUsageDiagnosticsSnapshot
    {
        public string OwnerKey { get; set; } = "";
        public string UserName { get; set; } = "";
        public bool Authenticated { get; set; }
        public bool Unlimited { get; set; }
        public bool TokensRequired { get; set; } = true;
        public long TokensUsed { get; set; }
        public long TokenLimit { get; set; }
        public long TokensRemaining { get; set; }
        public double GpuSecondsUsed { get; set; }
        public double CpuComputeSecondsUsed { get; set; }
        public double RamGbSecondsUsed { get; set; }
        public double SystemSecondsUsed { get; set; }
        public long IoBytesProcessed { get; set; }
        public double GpuKw { get; set; }
        public double CpuKw { get; set; }
        public double GpuKwh { get; set; }
        public double CpuKwh { get; set; }
        public double RamKwh { get; set; }
        public double SystemKwh { get; set; }
        public double IoKwh { get; set; }
        public double TotalKwh { get; set; }
        public double TokenCostUsd { get; set; }
        public double ElectricityCostUsd { get; set; }
        public double GpuElectricityCostUsd { get; set; }
        public double CpuElectricityCostUsd { get; set; }
        public double RamElectricityCostUsd { get; set; }
        public double SystemElectricityCostUsd { get; set; }
        public double IoElectricityCostUsd { get; set; }
        public double ServiceTaxCostUsd { get; set; }
        public double StorageCostUsd { get; set; }
        public double TotalCostUsd { get; set; }
        public double TokenUsdPerToken { get; set; }
        public double ElectricityCentsPerKwh { get; set; }
        public double ElectricityMultiplier { get; set; }
        public double GpuTdpWatts { get; set; }
        public double CpuTdpWatts { get; set; }
        public double RamWattsPerGb { get; set; }
        public double SystemWatts { get; set; }
        public double IoWattsPerGbps { get; set; }
        public double RamGbEstimated { get; set; }
        public long StorageBytesUsed { get; set; }
        public long StorageLimitBytes { get; set; }
        public long StorageBytesRemaining { get; set; }
        public bool StorageUnlimited { get; set; }
        public long CurrentSessionStorageBytes { get; set; }
        public string CurrentSessionId { get; set; } = "";
        public string StorageType { get; set; } = "";
        public string CostSettingsTable { get; set; } = "";
    }

    public sealed class ServerUserUsageDiagnosticsSnapshot
    {
        public string UserName { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string UserType { get; set; } = "";
        public bool AccountManaged { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsAdministrator { get; set; }
        public string LastClientIp { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string LastLoginUtc { get; set; } = "";
        public long TokenLimit { get; set; }
        public long TokensUsed { get; set; }
        public long TokensRemaining { get; set; }
        public bool Unlimited { get; set; }
        public string MutedUntilUtc { get; set; } = "";
        public string BannedUntilUtc { get; set; } = "";
        public bool MuteUntilEnabled { get; set; }
        public bool BanUntilEnabled { get; set; }
        public bool IsMuted { get; set; }
        public bool IsBanned { get; set; }
        public int SessionCount { get; set; }
        public int ActiveSessionCount { get; set; }
        public string LastSessionUtc { get; set; } = "";
        public ChatUsageDiagnosticsSnapshot Usage { get; set; } = new ChatUsageDiagnosticsSnapshot();
    }

    public sealed class ChatSessionRenderDiagnosticsSnapshot
    {
        public bool Found { get; set; }
        public bool IsActive { get; set; }
        public string Id { get; set; } = "";
        public string ActivePromptId { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Title { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public string Model { get; set; } = "";
        public string Runtime { get; set; } = "";
        public string Status { get; set; } = "";
        public string Phase { get; set; } = "";
        public int ElapsedSeconds { get; set; }
        public long PromptTokenCount { get; set; }
        public long PromptTokenBudget { get; set; }
        public double TokensPerSecond { get; set; }
        public int MessageCount { get; set; }
        public int FileCount { get; set; }
        public string MessagesJson { get; set; } = "[]";
        public string FilesJson { get; set; } = "[]";
        public string DraftPrompt { get; set; } = "";
        public string PromptText { get; set; } = "";
        public string ReferencesText { get; set; } = "";
        public ChatUsageDiagnosticsSnapshot Usage { get; set; } = new ChatUsageDiagnosticsSnapshot();
    }

    public sealed class TerminalPermissionRuleSnapshot
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string CommandHash { get; set; } = "";
        public string Command { get; set; } = "";
        public string Decision { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }

    public sealed class ChatClientPermissionSnapshot
    {
        public string OwnerKey { get; set; } = "";
        public bool InternetSearch { get; set; }
        public bool VsCopilotTools { get; set; }
        public bool FileDownloads { get; set; }
        public bool FtpServer { get; set; }
        public bool SqlAdmin { get; set; }
        public bool TerminalCommands { get; set; } = true;
        public bool TerminalForeverApproved { get; set; }
        public bool AgentAccess { get; set; } = true;
        public bool FileUploads { get; set; } = true;
        public bool ImageUploads { get; set; } = true;
        public bool PcAccess { get; set; }
        public string MutedUntilUtc { get; set; } = "";
        public string BannedUntilUtc { get; set; } = "";
        public bool MuteUntilEnabled { get; set; }
        public bool BanUntilEnabled { get; set; }
        public bool IsMuted { get; set; }
        public bool IsBanned { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }
}
