using System;
using System.Text.Json.Serialization;

namespace JackLLM.Mobile.Models;

public sealed class ServerInfo
{
    public string ServerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string OwnerUserName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string OpenAiBaseUrl { get; set; } = "";
    public string SelectedModel { get; set; } = "";
    public string AvailableModels { get; set; } = "";
    public string HardwareSummary { get; set; } = "";
    public string AvailableRam { get; set; } = "";
    public string AvailableVram { get; set; } = "";
    public string GpuModel { get; set; } = "";
    public string CpuModel { get; set; } = "";
    public bool Online { get; set; } = true;
    public int HealthScore { get; set; }
    public string CertificateFingerprint { get; set; } = "";
    public bool IsSaved { get; set; }
    public string CredentialKey { get; set; } = "";
    [JsonIgnore] public string ConnectionLine { get; set; } = "Checking Workstation status...";
    [JsonIgnore] public string TailscaleLine { get; set; } = "Checking Tailscale...";
    [JsonIgnore] public Color ConnectionColor { get; set; } = Color.FromArgb("#94A3B8");

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name
        : !string.IsNullOrWhiteSpace(Endpoint)
            ? Endpoint
            : "SocketJack AI server";

    public string Host => !string.IsNullOrWhiteSpace(OwnerUserName)
        ? OwnerUserName
        : string.Empty;

    public string LaunchKey => !string.IsNullOrWhiteSpace(ServerId)
        ? ServerId.Trim()
        : FallbackLaunchKey();

    public string HardwareLine =>
        string.Join(" | ", new[] { GpuModel, AvailableVram, CpuModel, AvailableRam }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    public string ModelLine => string.IsNullOrWhiteSpace(SelectedModel)
        ? AvailableModels
        : SelectedModel;

    public override string ToString() => DisplayName;

    private string FallbackLaunchKey()
    {
        string key = Endpoint;
        if (string.IsNullOrWhiteSpace(key))
            key = OpenAiBaseUrl;
        if (string.IsNullOrWhiteSpace(key))
            return "manual";
        try
        {
            return new Uri(key).Host;
        }
        catch
        {
            return key;
        }
    }
}
