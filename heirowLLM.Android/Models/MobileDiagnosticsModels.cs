namespace heirowLLM.Mobile.Models;

public sealed class MobileDiagnosticsSnapshot
{
    public bool Ok { get; set; }
    public string GeneratedUtc { get; set; } = "";
    public string Cursor { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public MobileDiagnosticsHealth Health { get; set; } = new();
    public List<MobileDiagnosticsEvent> Events { get; set; } = new();
    public List<MobileDiagnosticsUser> Users { get; set; } = new();
}

public sealed class MobileDiagnosticsHealth
{
    public double Score { get; set; }
    public string Tier { get; set; } = "";
    public double UptimeSeconds { get; set; }
    public long FailedRequests { get; set; }
    public int ActivePrompts { get; set; }
}

public sealed class MobileDiagnosticsEvent
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Route { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class MobileDiagnosticsUser
{
    public string UserName { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public string ConnectionState { get; set; } = "offline";
    public string LastSeenUtc { get; set; } = "";
    public int AuthenticatedSessionCount { get; set; }
    public long TokensUsed { get; set; }
    public long TokenLimit { get; set; }
    public bool Unlimited { get; set; }
    public double InboundBytesPerSecond { get; set; }
    public double OutboundBytesPerSecond { get; set; }
    public long InboundBytesTotal { get; set; }
    public long OutboundBytesTotal { get; set; }
}
