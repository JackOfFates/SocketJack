using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class LlmToolRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static readonly JsonSerializerOptions JsonLineOptions = CreateJsonOptions(writeIndented: false);
    private readonly object _sync = new();
    private readonly string _toolRoot;
    private readonly string _definitionsRoot;
    private readonly string _auditRoot;
    private readonly ILlmToolSecretStore _secretStore;

    public LlmToolRegistry(LlmRuntimeOptions options, ILlmToolSecretStore? secretStore = null)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        _toolRoot = options.ToolRoot;
        _definitionsRoot = Path.Combine(_toolRoot, "definitions");
        _auditRoot = Path.Combine(_toolRoot, "audit");
        Directory.CreateDirectory(_definitionsRoot);
        Directory.CreateDirectory(_auditRoot);
        _secretStore = secretStore ?? new EncryptedLocalToolSecretStore(_toolRoot);
        EnsureBuiltInDefinition(WindowsDesktopAutomationTool.CreateDefinition());
    }

    public string ToolRoot => _toolRoot;

    public IReadOnlyList<LlmToolDefinition> ListDefinitions(LlmToolVisibility? visibility = null)
    {
        lock (_sync)
        {
            return Directory.EnumerateFiles(_definitionsRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Select(ReadDefinition)
                .Where(definition => definition != null)
                .Cast<LlmToolDefinition>()
                .Where(definition => !visibility.HasValue || definition.Visibility == visibility.Value)
                .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public LlmToolDefinition? GetDefinition(string id)
    {
        id = NormalizeId(id);
        lock (_sync)
        {
            string path = GetDefinitionPath(id);
            return File.Exists(path) ? ReadDefinition(path) : null;
        }
    }

    public LlmToolDefinition UpsertDefinition(LlmToolDefinition definition)
    {
        if (definition == null)
            throw new ArgumentNullException(nameof(definition));

        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            string id = string.IsNullOrWhiteSpace(definition.Id)
                ? NormalizeId(definition.Name)
                : NormalizeId(definition.Id);

            if (string.IsNullOrWhiteSpace(id))
                throw new ArgumentException("Tool id or name is required.", nameof(definition));
            if (string.IsNullOrWhiteSpace(definition.Name))
                throw new ArgumentException("Tool name is required.", nameof(definition));
            if (definition.InputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new ArgumentException("Tool input schema is required.", nameof(definition));

            var existing = GetDefinition(id);
            definition.Id = id;
            definition.Name = NormalizeToolName(definition.Name);
            definition.Description = definition.Description?.Trim() ?? "";
            definition.TimeoutSeconds = Math.Clamp(definition.TimeoutSeconds <= 0 ? 60 : definition.TimeoutSeconds, 1, 3600);
            definition.CreatedAtUtc = existing?.CreatedAtUtc ?? (definition.CreatedAtUtc == default ? now : definition.CreatedAtUtc);
            definition.UpdatedAtUtc = now;
            definition.Tags = definition.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
            definition.HttpHeaders = definition.HttpHeaders
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            definition.EnvironmentVariables = definition.EnvironmentVariables
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
            definition.RequiredSecrets = definition.RequiredSecrets
                .Where(secret => secret != null && !string.IsNullOrWhiteSpace(secret.Name))
                .Select(secret => new LlmToolSecretReference
                {
                    Name = secret.Name.Trim(),
                    SecretId = NormalizeId(string.IsNullOrWhiteSpace(secret.SecretId) ? id + "_" + secret.Name : secret.SecretId),
                    Description = secret.Description?.Trim() ?? ""
                })
                .ToList();

            File.WriteAllText(GetDefinitionPath(id), JsonSerializer.Serialize(definition, JsonOptions));
            AddAuditEntry(new LlmToolAuditEntry
            {
                ToolId = id,
                ToolName = definition.Name,
                Action = existing == null ? "create" : "update",
                Outcome = "success",
                Message = "Tool definition saved."
            });
            return definition;
        }
    }

    public bool DeleteDefinition(string id)
    {
        id = NormalizeId(id);
        lock (_sync)
        {
            string path = GetDefinitionPath(id);
            if (!File.Exists(path))
                return false;

            var definition = ReadDefinition(path);
            File.Delete(path);
            AddAuditEntry(new LlmToolAuditEntry
            {
                ToolId = id,
                ToolName = definition?.Name ?? id,
                Action = "delete",
                Outcome = "success",
                Message = "Tool definition deleted."
            });
            return true;
        }
    }

    public IReadOnlyList<object> ExportOpenAiTools(Func<LlmToolDefinition, bool>? predicate = null)
    {
        return ListDefinitions()
            .Where(definition => definition.ApprovalMode != LlmToolApprovalMode.Disabled)
            .Where(definition => predicate == null || predicate(definition))
            .Select(definition => new
            {
                type = "function",
                function = new
                {
                    name = NormalizeToolName(definition.Name),
                    description = definition.Description,
                    parameters = definition.InputSchema
                }
            })
            .Cast<object>()
            .ToList();
    }

    public void SetSecret(string secretId, string secretValue) => _secretStore.SetSecret(secretId, secretValue);

    public string? GetSecret(string secretId) => _secretStore.GetSecret(secretId);

    public bool DeleteSecret(string secretId) => _secretStore.DeleteSecret(secretId);

    public string RedactForTool(string toolId, string value)
    {
        var definition = GetDefinition(toolId);
        if (definition == null)
            return new LlmToolRedactor([]).Redact(value);

        var secrets = definition.RequiredSecrets
            .Select(secret => _secretStore.GetSecret(secret.SecretId))
            .Where(secret => !string.IsNullOrEmpty(secret))
            .Cast<string>()
            .ToArray();
        return new LlmToolRedactor(secrets).Redact(value);
    }

    public void AddAuditEntry(LlmToolAuditEntry entry)
    {
        if (entry == null)
            return;

        Directory.CreateDirectory(_auditRoot);
        if (string.IsNullOrWhiteSpace(entry.Id))
            entry.Id = "audit_" + Guid.NewGuid().ToString("N");
        if (entry.TimestampUtc == default)
            entry.TimestampUtc = DateTimeOffset.UtcNow;

        string path = Path.Combine(_auditRoot, entry.TimestampUtc.ToString("yyyyMMdd") + ".jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonLineOptions) + Environment.NewLine);
    }

    public IReadOnlyList<LlmToolAuditEntry> ListAuditEntries(string? toolId = null, int limit = 200)
    {
        toolId = string.IsNullOrWhiteSpace(toolId) ? null : NormalizeId(toolId);
        limit = Math.Clamp(limit, 1, 5000);
        lock (_sync)
        {
            if (!Directory.Exists(_auditRoot))
                return [];

            return Directory.EnumerateFiles(_auditRoot, "*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Path.GetFileName)
                .SelectMany(ReadAuditFile)
                .Where(entry => toolId == null || string.Equals(entry.ToolId, toolId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.TimestampUtc)
                .Take(limit)
                .ToList();
        }
    }

    public static string NormalizeId(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
                builder.Append(ch);
            else if (ch is '_' or '-' or '.' or ' ')
                builder.Append('_');
        }

        string id = builder.ToString().Trim('_');
        while (id.Contains("__", StringComparison.Ordinal))
            id = id.Replace("__", "_", StringComparison.Ordinal);
        return id;
    }

    public static string NormalizeToolName(string value)
    {
        string id = NormalizeId(value).Replace('.', '_');
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Tool name is required.", nameof(value));
        return id.Length <= 64 ? id : id[..64];
    }

    private string GetDefinitionPath(string id) => Path.Combine(_definitionsRoot, NormalizeId(id) + ".json");

    private void EnsureBuiltInDefinition(LlmToolDefinition definition)
    {
        string path = GetDefinitionPath(definition.Id);
        if (File.Exists(path))
            return;

        File.WriteAllText(path, JsonSerializer.Serialize(definition, JsonOptions));
        AddAuditEntry(new LlmToolAuditEntry
        {
            ToolId = definition.Id,
            ToolName = definition.Name,
            Action = "create",
            Outcome = "success",
            Message = "Built-in tool definition registered."
        });
    }

    private static LlmToolDefinition? ReadDefinition(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<LlmToolDefinition>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<LlmToolAuditEntry> ReadAuditFile(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            LlmToolAuditEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<LlmToolAuditEntry>(line, JsonOptions); } catch { }
            if (entry != null)
                yield return entry;
        }
    }

    private static JsonSerializerOptions CreateJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
