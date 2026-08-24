namespace heirowLLM.Mobile.Models;

public sealed class ContextApprovalRequest
{
    public string Id { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Capability { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string QuerySummary { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public bool CanAlwaysAllow { get; set; }
}

public sealed class ContextApprovalEnvelope
{
    public bool Ok { get; set; }
    public List<ContextApprovalRequest> Approvals { get; set; } = new();
}
