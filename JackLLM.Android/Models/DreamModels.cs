namespace JackLLM.Mobile.Models;

public sealed class MobileDreamSettings
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = "conservative";
    public int PollSeconds { get; set; } = 5;
    public int StartGraceSeconds { get; set; } = 30;
    public int PauseGraceSeconds { get; set; } = 3;
    public int RecurrenceMinutes { get; set; } = 240;
    public int MaxRunMinutes { get; set; } = 10;
    public int TokenBudget { get; set; } = 2048;
    public int SourceTokenBudget { get; set; } = 12000;
    public int SessionsPerPass { get; set; } = 6;
    public int StartCpuPercent { get; set; } = 35;
    public int PauseCpuPercent { get; set; } = 65;
    public int StartRamPercent { get; set; } = 65;
    public int PauseRamPercent { get; set; } = 82;
    public int StartGpuPercent { get; set; } = 30;
    public int PauseGpuPercent { get; set; } = 70;
    public int StartVramPercent { get; set; } = 55;
    public int PauseVramPercent { get; set; } = 82;
    public int StartDiskPercent { get; set; } = 35;
    public int PauseDiskPercent { get; set; } = 75;
    public string Model { get; set; } = "auto";
    public string Service { get; set; } = "";
    public bool AutoSaveStrictFacts { get; set; } = true;
}

public sealed class MobileDreamSettingsEnvelope
{
    public string OwnerKey { get; set; } = "";
    public bool CanManageOwners { get; set; }
    public bool HasOverride { get; set; }
    public string SettingsSource { get; set; } = "global";
    public MobileDreamSettings Settings { get; set; } = new();
}

public sealed class MobileDreamOwner
{
    public string OwnerKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SessionCount { get; set; }
    public bool HasOverride { get; set; }
    public bool Enabled { get; set; }
    public string Status { get; set; } = "";
    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? OwnerKey : DisplayName;
}

public sealed class MobileDreamResources
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double GpuPercent { get; set; }
    public double VramPercent { get; set; }
    public double DiskPercent { get; set; }
    public bool ForegroundModelWork { get; set; }
}

public sealed class MobileDreamStatus
{
    public string Status { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Progress { get; set; }
    public string LimitingResource { get; set; } = "";
    public string NextRunUtc { get; set; } = "";
    public string LastError { get; set; } = "";
    public int ProcessedSessions { get; set; }
    public int ProcessedMessages { get; set; }
    public bool Enabled { get; set; }
    public bool UserPaused { get; set; }
    public MobileDreamResources Resources { get; set; } = new();
}

public sealed class MobileDreamCandidate
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Disposition { get; set; } = "review";
    public double Confidence { get; set; }
    public string CandidateType { get; set; } = "memory";
    public string StaleMemoryText { get; set; } = "";
}

public sealed class MobileDreamJournalEntry
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RawReflection { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public int ProcessedSessions { get; set; }
    public int ProcessedMessages { get; set; }
    public List<MobileDreamCandidate> Candidates { get; set; } = new();
    public List<string> ToolAudit { get; set; } = new();
}

public sealed class MobileDreamPermissionSnapshot
{
    public bool InternetSearch { get; set; }
    public bool VsCopilotTools { get; set; }
    public bool AgentAccess { get; set; }
    public bool TerminalCommands { get; set; }
    public bool TerminalForeverApproved { get; set; }
    public bool FileUploads { get; set; }
    public bool ImageUploads { get; set; }
    public bool FileDownloads { get; set; }
    public bool FtpServer { get; set; }
    public bool PcAccess { get; set; }
    public bool DreamInternetSearch { get; set; }
    public bool DreamVsCopilotTools { get; set; }
    public bool DreamAgentAccess { get; set; }
    public bool DreamTerminalCommands { get; set; }
    public bool DreamFileUploads { get; set; }
    public bool DreamImageUploads { get; set; }
    public bool DreamFileDownloads { get; set; }
    public bool DreamFtpServer { get; set; }
    public bool DreamPcAccess { get; set; }
}
