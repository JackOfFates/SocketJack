using System;

namespace LmVs
{
    public sealed class ChatSessionDiagnosticsSnapshot
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string Model { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public int MessageCount { get; set; }
        public int FileCount { get; set; }
    }

    public sealed class ActivePromptSessionDiagnosticsSnapshot
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public string Title { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Model { get; set; } = "";
        public string Status { get; set; } = "";
        public string Phase { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
        public int ElapsedSeconds { get; set; }
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
        public string MutedUntilUtc { get; set; } = "";
        public string BannedUntilUtc { get; set; } = "";
        public bool MuteUntilEnabled { get; set; }
        public bool BanUntilEnabled { get; set; }
        public bool IsMuted { get; set; }
        public bool IsBanned { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }
}
