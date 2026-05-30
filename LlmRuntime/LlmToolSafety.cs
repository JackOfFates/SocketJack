namespace LlmRuntime;

public sealed class LlmToolSafetyPolicy
{
    public LlmToolSafetyCheck Validate(LlmToolDefinition definition, LlmToolInvocationRequest request, IReadOnlyDictionary<string, string> resolvedSecrets)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        if (definition.ApprovalMode == LlmToolApprovalMode.Disabled)
            return LlmToolSafetyCheck.Denied("Tool is disabled.");

        if (RequiresApproval(definition) && !request.Approved)
            return LlmToolSafetyCheck.Denied("Tool invocation requires approval.");

        if (!IsProjectAllowed(definition, request.ProjectPath))
            return LlmToolSafetyCheck.Denied("Tool is not allowed for this project.");

        LlmToolPermissions required = RequiredPermissions(definition);
        if ((definition.Permissions & required) != required)
            return LlmToolSafetyCheck.Denied("Tool permissions do not allow " + required + ".");

        if (definition.RequiredSecrets.Count > 0 && !definition.Permissions.HasFlag(LlmToolPermissions.SecretsAccess))
            return LlmToolSafetyCheck.Denied("Tool requires secrets but does not have SecretsAccess permission.");

        if (definition.RequiredSecrets.Any(secret => !resolvedSecrets.ContainsKey(secret.SecretId) && !resolvedSecrets.ContainsKey(secret.Name)))
            return LlmToolSafetyCheck.Denied("One or more required tool secrets are missing.");

        return LlmToolSafetyCheck.CreateAllowed(required);
    }

    public static LlmToolPermissions RequiredPermissions(LlmToolDefinition definition)
    {
        LlmToolPermissions required = definition.SourceType switch
        {
            LlmToolSourceType.Http => LlmToolPermissions.NetworkAccess,
            LlmToolSourceType.Executable or LlmToolSourceType.PowerShell => LlmToolPermissions.ShellExecution,
            LlmToolSourceType.NamedPipe => LlmToolPermissions.NetworkAccess,
            LlmToolSourceType.DotNetAssembly => LlmToolPermissions.RepositoryAccess,
            LlmToolSourceType.McpServer => LlmToolPermissions.NetworkAccess,
            LlmToolSourceType.BuiltInSocketJack => definition.Permissions == LlmToolPermissions.None ? LlmToolPermissions.RepositoryAccess : definition.Permissions,
            _ => LlmToolPermissions.None
        };

        if (definition.RequiredSecrets.Count > 0)
            required |= LlmToolPermissions.SecretsAccess;
        return required;
    }

    private static bool RequiresApproval(LlmToolDefinition definition) =>
        definition.ApprovalMode == LlmToolApprovalMode.AskEveryTime
        || IsDestructive(definition)
        || (definition.ApprovalMode == LlmToolApprovalMode.AskOnDestructiveOperations && IsDestructive(definition));

    private static bool IsDestructive(LlmToolDefinition definition)
    {
        const LlmToolPermissions destructive =
            LlmToolPermissions.FileSystemWrite
            | LlmToolPermissions.ShellExecution
            | LlmToolPermissions.SecretsAccess;
        return (definition.Permissions & destructive) != 0;
    }

    private static bool IsProjectAllowed(LlmToolDefinition definition, string projectPath)
    {
        if (definition.AllowedProjects.Count == 0)
            return true;
        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        string fullProjectPath = Path.GetFullPath(projectPath);
        return definition.AllowedProjects.Any(path =>
        {
            try
            {
                string allowed = Path.GetFullPath(path);
                return fullProjectPath.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                    || fullProjectPath.StartsWith(allowed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        });
    }
}

public sealed class LlmToolSafetyCheck
{
    private LlmToolSafetyCheck(bool allowed, LlmToolPermissions requiredPermissions, string denialReason)
    {
        Allowed = allowed;
        RequiredPermissions = requiredPermissions;
        DenialReason = denialReason;
    }

    public bool Allowed { get; }

    public LlmToolPermissions RequiredPermissions { get; }

    public string DenialReason { get; }

    public static LlmToolSafetyCheck CreateAllowed(LlmToolPermissions requiredPermissions) => new(true, requiredPermissions, "");

    public static LlmToolSafetyCheck Denied(string reason) => new(false, LlmToolPermissions.None, reason);
}

public sealed class LlmToolRedactor
{
    private static readonly System.Text.RegularExpressions.Regex SecretPattern = new(
        "(password|passwd|token|secret|api[_-]?key|authorization)\\s*[:=]\\s*[^\\s;,&]+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private readonly IReadOnlyList<string> _sensitiveValues;

    public LlmToolRedactor(IEnumerable<string> sensitiveValues)
    {
        _sensitiveValues = (sensitiveValues ?? [])
            .Where(value => !string.IsNullOrEmpty(value) && value.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        string redacted = value.Replace("\r", " ").Replace("\n", " ");
        foreach (string secret in _sensitiveValues)
            redacted = redacted.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return SecretPattern.Replace(redacted, "$1=[REDACTED]");
    }
}
