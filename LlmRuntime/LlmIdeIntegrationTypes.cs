namespace LlmRuntime;

public sealed class LlmIdeContextRequest
{
    public string WorkspaceRoot { get; set; } = "";
    public string ActiveFilePath { get; set; } = "";
    public string ActiveFileText { get; set; } = "";
    public string SelectionText { get; set; } = "";
    public int CursorLine { get; set; }
    public int CursorColumn { get; set; }
    public List<LlmIdeDiagnostic> Diagnostics { get; set; } = new();
    public string Prompt { get; set; } = "";
    public string Mode { get; set; } = "";
    public List<LlmIdeAttachment> Attachments { get; set; } = new();
}

public sealed class LlmIdeDiagnostic
{
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class LlmIdeCompletionResult
{
    public string Text { get; init; } = "";
    public string Detail { get; init; } = "";
    public double Confidence { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
}

public sealed class LlmIdeSuggestion
{
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
    public string ReplacementText { get; init; } = "";
}

public sealed class LlmIdeAnswer
{
    public string Mode { get; init; } = "";
    public string Answer { get; init; } = "";
    public IReadOnlyList<string> References { get; init; } = [];
}

public sealed class LlmIdeEditPreview
{
    public string FilePath { get; init; } = "";
    public string Before { get; init; } = "";
    public string After { get; init; } = "";
    public string Diff { get; init; } = "";
    public bool RequiresApproval { get; init; } = true;
}

public sealed class LlmIdePlan
{
    public string Goal { get; init; } = "";
    public IReadOnlyList<LlmAgentPlanStep> Steps { get; init; } = [];
    public bool ModifiesFiles { get; init; }
}

public sealed class LlmIdeCheckpoint
{
    public string Id { get; init; } = "";
    public string WorkspaceRoot { get; init; } = "";
    public string Path { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed class LlmIdeRollbackResult
{
    public string CheckpointId { get; init; } = "";
    public int RestoredFiles { get; init; }
    public IReadOnlyList<string> Files { get; init; } = [];
}

public sealed class LlmIdeCodeReferenceSet
{
    public string WorkspaceRoot { get; init; } = "";
    public string ActiveFilePath { get; init; } = "";
    public string SelectionText { get; init; } = "";
    public IReadOnlyList<string> SolutionFiles { get; init; } = [];
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];
    public IReadOnlyList<LlmIdeDiagnostic> Diagnostics { get; init; } = [];
    public string GitDiff { get; init; } = "";
    public IReadOnlyList<string> CustomInstructionFiles { get; init; } = [];
    public IReadOnlyList<string> PromptFiles { get; init; } = [];
}

public sealed class LlmIdeIndexDocument
{
    public string FilePath { get; init; } = "";
    public string Language { get; init; } = "";
    public IReadOnlyList<string> Symbols { get; init; } = [];
    public string Preview { get; init; } = "";
}

public sealed class LlmIdeWorkspaceIndex
{
    public string WorkspaceRoot { get; init; } = "";
    public int FileCount { get; init; }
    public IReadOnlyList<LlmIdeIndexDocument> Documents { get; init; } = [];
}

public sealed class LlmIdeSearchResult
{
    public string FilePath { get; init; } = "";
    public double Score { get; init; }
    public string Preview { get; init; } = "";
    public IReadOnlyList<string> Symbols { get; init; } = [];
}

public sealed class LlmIdeCapabilities
{
    public bool InlineCompletion { get; init; } = true;
    public bool NextEditSuggestions { get; init; } = true;
    public bool AskMode { get; init; } = true;
    public bool EditMode { get; init; } = true;
    public bool AgentMode { get; init; } = true;
    public bool PlanMode { get; init; } = true;
    public bool CheckpointsAndRollback { get; init; } = true;
    public bool CodeReferences { get; init; } = true;
    public bool WorkspaceIndexing { get; init; } = true;
    public bool CustomInstructions { get; init; } = true;
    public bool PromptFiles { get; init; } = true;
    public bool McpToolContext { get; init; } = true;
    public bool LocalPrivacyMode { get; init; } = true;
    public bool ByomByokRouting { get; init; } = true;
    public bool VisionInputs { get; init; } = true;
    public bool ContextMenuActions { get; init; } = true;
    public bool DotNetUpgradeAssistant { get; init; } = true;
    public IReadOnlyList<string> Endpoints { get; init; } = [];
}

public sealed class LlmIdeAttachment
{
    public string Name { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string Path { get; set; } = "";
    public string Base64Content { get; set; } = "";
}

public sealed class LlmIdeModelRoutingPlan
{
    public string PrivacyMode { get; init; } = "local-only";
    public string DefaultRoute { get; init; } = "local-gguf";
    public IReadOnlyList<string> Routes { get; init; } = [];
    public bool BringYourOwnModel { get; init; } = true;
    public bool BringYourOwnKey { get; init; } = true;
}

public sealed class LlmIdeVisionContext
{
    public bool Supported { get; init; }
    public IReadOnlyList<string> AcceptedContentTypes { get; init; } = [];
    public IReadOnlyList<string> AttachmentNames { get; init; } = [];
    public string Summary { get; init; } = "";
}

public sealed class LlmIdeContextAction
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Surface { get; init; } = "";
    public string Mode { get; init; } = "";
}

public sealed class LlmDotNetUpgradePlan
{
    public string WorkspaceRoot { get; init; } = "";
    public IReadOnlyList<string> ProjectFiles { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> Steps { get; init; } = [];
}
