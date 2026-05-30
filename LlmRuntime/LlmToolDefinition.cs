using System.Text.Json;

namespace LlmRuntime;

public enum LlmToolVisibility
{
    Public,
    Private,
    Proprietary,
    InternalOnly
}

public enum LlmToolSourceType
{
    Http,
    Executable,
    PowerShell,
    NamedPipe,
    DotNetAssembly,
    McpServer,
    BuiltInSocketJack
}

public enum LlmToolApprovalMode
{
    AlwaysAllow,
    AskEveryTime,
    AskOnDestructiveOperations,
    Disabled
}

[Flags]
public enum LlmToolPermissions
{
    None = 0,
    FileSystemRead = 1,
    FileSystemWrite = 2,
    ShellExecution = 4,
    NetworkAccess = 8,
    BrowserAccess = 16,
    RepositoryAccess = 32,
    SecretsAccess = 64,
    DesktopAutomation = 128
}

public sealed class LlmToolDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public LlmToolVisibility Visibility { get; set; } = LlmToolVisibility.Private;

    public LlmToolSourceType SourceType { get; set; } = LlmToolSourceType.Http;

    public string Source { get; set; } = "";

    public Dictionary<string, string> HttpHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Version { get; set; } = "1.0.0";

    public string Vendor { get; set; } = "";

    public string LicenseNotes { get; set; } = "";

    public List<string> Tags { get; set; } = new();

    public List<LlmToolSecretReference> RequiredSecrets { get; set; } = new();

    public JsonElement InputSchema { get; set; } = JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    public JsonElement? ResultSchema { get; set; }

    public LlmToolApprovalMode ApprovalMode { get; set; } = LlmToolApprovalMode.AskEveryTime;

    public LlmToolPermissions Permissions { get; set; } = LlmToolPermissions.None;

    public int TimeoutSeconds { get; set; } = 60;

    public List<string> AllowedProjects { get; set; } = new();

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LlmToolSecretReference
{
    public string Name { get; set; } = "";

    public string SecretId { get; set; } = "";

    public string Description { get; set; } = "";
}

public sealed class LlmToolAuditEntry
{
    public string Id { get; set; } = "audit_" + Guid.NewGuid().ToString("N");

    public string ToolId { get; set; } = "";

    public string ToolName { get; set; } = "";

    public string Action { get; set; } = "";

    public string Outcome { get; set; } = "";

    public string Message { get; set; } = "";

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
}

public interface ILlmTool
{
    string Id { get; }

    LlmToolDefinition Definition { get; }

    Task<LlmToolInvocationResult> InvokeAsync(LlmToolInvocationRequest request, CancellationToken cancellationToken = default);
}
