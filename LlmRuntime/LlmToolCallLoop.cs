using System.Text.Json;

namespace LlmRuntime;

public sealed class LlmToolCall
{
    public string Id { get; set; } = "call_" + Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public JsonElement Arguments { get; set; } = JsonDocument.Parse("{}").RootElement.Clone();
}

public sealed class LlmToolCallResult
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public bool Success { get; set; }

    public string Content { get; set; } = "";

    public string Error { get; set; } = "";

    public JsonElement? OutputJson { get; set; }

    public bool ApprovalRequired { get; set; }

    public LlmToolPermissions RequiredPermissions { get; set; }
}

public sealed class LlmToolCallLoop
{
    private readonly LlmToolRegistry _registry;
    private readonly ILlmToolInvoker _invoker;

    public LlmToolCallLoop(LlmToolRegistry registry, ILlmToolInvoker invoker)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _invoker = invoker ?? throw new ArgumentNullException(nameof(invoker));
    }

    public async Task<IReadOnlyList<LlmToolCallResult>> ExecuteAsync(IEnumerable<LlmToolCall> toolCalls, bool approved, string projectPath = "", CancellationToken cancellationToken = default)
    {
        if (toolCalls == null)
            throw new ArgumentNullException(nameof(toolCalls));

        var results = new List<LlmToolCallResult>();
        foreach (var call in toolCalls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string toolId = ResolveToolId(call.Name);
            if (string.IsNullOrWhiteSpace(toolId))
            {
                results.Add(new LlmToolCallResult
                {
                    Id = call.Id,
                    Name = call.Name,
                    Success = false,
                    Error = "Tool definition was not found."
                });
                continue;
            }

            var invocation = await _invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = toolId,
                ToolCallId = call.Id,
                Input = call.Arguments,
                Approved = approved,
                ProjectPath = projectPath
            }, cancellationToken).ConfigureAwait(false);

            results.Add(new LlmToolCallResult
            {
                Id = call.Id,
                Name = call.Name,
                Success = invocation.Success,
                Content = invocation.OutputText,
                Error = invocation.Error,
                OutputJson = invocation.OutputJson,
                ApprovalRequired = invocation.ApprovalRequired,
                RequiredPermissions = invocation.RequiredPermissions
            });
        }

        return results;
    }

    private string ResolveToolId(string name)
    {
        string normalized = LlmToolRegistry.NormalizeId(name);
        if (_registry.GetDefinition(normalized) != null)
            return normalized;

        return _registry.ListDefinitions()
            .FirstOrDefault(definition => string.Equals(LlmToolRegistry.NormalizeToolName(definition.Name), normalized, StringComparison.OrdinalIgnoreCase))
            ?.Id ?? "";
    }
}

public static class LlmMcpToolAdapter
{
    public static IReadOnlyList<object> ExportMcpTools(LlmToolRegistry registry, Func<LlmToolDefinition, bool>? predicate = null)
    {
        if (registry == null)
            throw new ArgumentNullException(nameof(registry));

        return registry.ListDefinitions()
            .Where(definition => definition.ApprovalMode != LlmToolApprovalMode.Disabled)
            .Where(definition => predicate == null || predicate(definition))
            .Select(definition => new
            {
                name = LlmToolRegistry.NormalizeToolName(definition.Name),
                description = definition.Description,
                inputSchema = definition.InputSchema,
                annotations = new
                {
                    visibility = definition.Visibility.ToString(),
                    sourceType = definition.SourceType.ToString(),
                    permissions = definition.Permissions.ToString(),
                    vendor = definition.Vendor,
                    license = definition.LicenseNotes
                }
            })
            .Cast<object>()
            .ToList();
    }
}
