namespace heirowLLM.Mobile.Models;

public sealed class ChickenChaserMobileSettings
{
    public int SchemaVersion { get; set; } = 3;
    public string Mode { get; set; } = "chat";
    public string TaskMode { get; set; } = "agent";
    public string Model { get; set; } = "auto";
    public bool Retry { get; set; } = true;
    public bool HeirowForge { get; set; } = true;
    public bool IncludeContext { get; set; } = true;
    public double Temperature { get; set; } = .7;
    public double TopP { get; set; } = 1;
    public int MaxTokens { get; set; }
    public string Reasoning { get; set; } = "auto";
    public string WorkCap { get; set; } = "preset";
    public bool UseGlobalReasoning { get; set; } = true;
    public bool Hidden { get; set; }
    public bool Compact { get; set; }
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public List<string> IgnoredNotificationCategories { get; set; } = new();
}
