using System.Collections.Generic;

namespace LlmRuntime.VisualStudio;

public sealed class LlmRuntimeVisualStudioFeature
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public string Surface { get; set; } = "";
}

public static class LlmRuntimeFeatureCatalog
{
    public static IReadOnlyList<LlmRuntimeVisualStudioFeature> Features { get; } =
    [
        new() { Id = "chat", Title = "Chat", Endpoint = "/api/v1/ide/ask", Surface = "tool-window" },
        new() { Id = "inline-completion", Title = "Inline Completion", Endpoint = "/api/v1/ide/completions/inline", Surface = "editor" },
        new() { Id = "next-edit", Title = "Next Edit", Endpoint = "/api/v1/ide/next-edit", Surface = "editor" },
        new() { Id = "agent", Title = "Agent Mode", Endpoint = "/api/v1/agent/autonomous/run", Surface = "solution-explorer" },
        new() { Id = "checkpoints", Title = "Checkpoints", Endpoint = "/api/v1/ide/checkpoints", Surface = "workspace" },
        new() { Id = "mcp", Title = "MCP Context", Endpoint = "/api/v1/ide/mcp/context", Surface = "tools" },
        new() { Id = "prompt-files", Title = "Prompt Files", Endpoint = "/api/v1/ide/prompts", Surface = "workspace" },
        new() { Id = "custom-instructions", Title = "Custom Instructions", Endpoint = "/api/v1/ide/instructions", Surface = "workspace" },
        new() { Id = "model-picker", Title = "Model Picker", Endpoint = "/api/v1/models", Surface = "status-bar" },
        new() { Id = "code-review", Title = "Code Review", Endpoint = "/api/v1/github/review", Surface = "git" },
        new() { Id = "workspace-index", Title = "Workspace Index", Endpoint = "/api/v1/ide/index", Surface = "solution" }
    ];
}
