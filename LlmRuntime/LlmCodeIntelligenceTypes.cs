namespace LlmRuntime;

public sealed class LlmSymbolNode
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string FilePath { get; init; } = "";
    public int Line { get; init; }
}

public sealed class LlmSymbolGraph
{
    public string WorkspaceRoot { get; init; } = "";
    public IReadOnlyList<LlmSymbolNode> Symbols { get; init; } = [];
}

public sealed class LlmCodeGraph
{
    public string WorkspaceRoot { get; init; } = "";
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Calls { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    public IReadOnlyDictionary<string, string> Ownership { get; init; } = new Dictionary<string, string>();
}

public sealed class LlmPlannerResult
{
    public string Title { get; init; } = "";
    public IReadOnlyList<LlmAgentPlanStep> Steps { get; init; } = [];
}

public sealed class LlmTestCoverageExplorer
{
    public string WorkspaceRoot { get; init; } = "";
    public IReadOnlyList<string> TestProjects { get; init; } = [];
    public IReadOnlyList<string> SourceFilesWithoutObviousTests { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> MissingTestPlan { get; init; } = [];
}

public sealed class LlmProfilingAssistantPlan
{
    public IReadOnlyList<LlmAgentPlanStep> Cpu { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> Memory { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> AsyncDeadlocks { get; init; } = [];
}

public sealed class LlmArchitectureReview
{
    public string WorkspaceRoot { get; init; } = "";
    public IReadOnlyList<string> Observations { get; init; } = [];
    public IReadOnlyList<string> Risks { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> Recommendations { get; init; } = [];
}

public sealed class LlmDocumentationSyncPlan
{
    public IReadOnlyList<string> DocumentationFiles { get; init; } = [];
    public IReadOnlyList<string> SourceAreas { get; init; } = [];
    public IReadOnlyList<LlmAgentPlanStep> Steps { get; init; } = [];
}

public sealed class LlmModelEvaluationHarnessPlan
{
    public IReadOnlyList<string> CandidateModels { get; init; } = [];
    public IReadOnlyList<string> Tasks { get; init; } = [];
    public IReadOnlyList<string> Metrics { get; init; } = [];
}

public sealed class LlmContextBudgetPlan
{
    public int MaxTokens { get; init; }
    public IReadOnlyList<string> IncludeFirst { get; init; } = [];
    public IReadOnlyList<string> Summarize { get; init; } = [];
    public IReadOnlyList<string> Exclude { get; init; } = [];
}
