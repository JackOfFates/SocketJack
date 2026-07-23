using System;
using System.Collections.Generic;

namespace LmVs;

public sealed class DreamSettingsSnapshot
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

public sealed class DreamResourceSnapshot
{
    public double CpuPercent { get; set; }
    public double RamPercent { get; set; }
    public double GpuPercent { get; set; }
    public double VramPercent { get; set; }
    public double DiskPercent { get; set; }
    public bool ForegroundModelWork { get; set; }
    public string SampledUtc { get; set; } = "";
}

public sealed class DreamHardwareRecommendationSnapshot
{
    public bool Pending { get; set; }
    public string Reason { get; set; } = "";
    public string PreviousHardware { get; set; } = "";
    public string CurrentHardware { get; set; } = "";
    public string DetectedUtc { get; set; } = "";
    public DreamSettingsSnapshot RecommendedSettings { get; set; } = new();
}

public sealed class DreamStatusSnapshot
{
    public string OwnerKey { get; set; } = "global";
    public string Status { get; set; } = "disabled";
    public string Phase { get; set; } = "waiting";
    public int Progress { get; set; }
    public string LimitingResource { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string CompletedUtc { get; set; } = "";
    public string LastRunUtc { get; set; } = "";
    public string NextRunUtc { get; set; } = "";
    public string CurrentJournalId { get; set; } = "";
    public string LastError { get; set; } = "";
    public int QueuePosition { get; set; }
    public int ProcessedSessions { get; set; }
    public int ProcessedMessages { get; set; }
    public bool Enabled { get; set; }
    public bool ManualRequested { get; set; }
    public bool UserPaused { get; set; }
    public bool HasOverride { get; set; }
    public string SettingsSource { get; set; } = "global";
    public DreamResourceSnapshot Resources { get; set; } = new();
}

public sealed class DreamCandidateSnapshot
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Topic { get; set; } = "General";
    public string Disposition { get; set; } = "review";
    public double Confidence { get; set; }
    public bool ExplicitFact { get; set; }
    public bool Sensitive { get; set; }
    public bool Conflicting { get; set; }
    public string SourceSessionId { get; set; } = "";
    public string StaleMemoryId { get; set; } = "";
    public string StaleMemoryText { get; set; } = "";
    public string CandidateType { get; set; } = "memory";
}

public sealed class DreamJournalSnapshot
{
    public string Id { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string RawReflection { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string CompletedUtc { get; set; } = "";
    public int ProcessedSessions { get; set; }
    public int ProcessedMessages { get; set; }
    public List<DreamCandidateSnapshot> Candidates { get; set; } = new();
    public List<string> ToolAudit { get; set; } = new();
}

public sealed class DreamOwnerSnapshot
{
    public string OwnerKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int SessionCount { get; set; }
    public bool HasOverride { get; set; }
    public bool Enabled { get; set; }
    public string Status { get; set; } = "";
}

public sealed class MobileDreamDeviceSnapshot
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Platform { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public bool DreamAdmin { get; set; }
    public string LastSeenUtc { get; set; } = "";
}
