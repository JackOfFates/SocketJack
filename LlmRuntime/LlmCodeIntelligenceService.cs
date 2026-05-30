using System.Text.RegularExpressions;

namespace LlmRuntime;

public sealed class LlmCodeIntelligenceService
{
    private static readonly Regex TypeRegex = new(@"\b(class|struct|interface|enum|record)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly Regex MethodRegex = new(@"\b(?:public|private|protected|internal|static|async|virtual|override|sealed|\s)+\s*[\w<>\[\]\?]+\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly Regex CallRegex = new(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.Compiled);
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".xaml", ".csproj", ".sln", ".json", ".md", ".xml", ".html", ".css", ".js", ".ts", ".ps1" };

    private readonly LlmRuntimeOptions _options;
    private readonly LlmModelRegistry _models;

    public LlmCodeIntelligenceService(LlmRuntimeOptions options, LlmModelRegistry models)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _models = models ?? throw new ArgumentNullException(nameof(models));
    }

    public LlmSymbolGraph BuildSymbolGraph(string? workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        var symbols = new List<LlmSymbolNode>();
        foreach (string file in SourceFiles(root).Where(file => Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)).Take(1000))
        {
            string[] lines = SafeReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (Match match in TypeRegex.Matches(lines[i]))
                    symbols.Add(new LlmSymbolNode { Name = match.Groups[2].Value, Kind = match.Groups[1].Value, FilePath = file, Line = i + 1 });
                foreach (Match match in MethodRegex.Matches(lines[i]))
                    symbols.Add(new LlmSymbolNode { Name = match.Groups[1].Value, Kind = "method", FilePath = file, Line = i + 1 });
            }
        }
        return new LlmSymbolGraph { WorkspaceRoot = root, Symbols = symbols };
    }

    public LlmCodeGraph BuildCodeGraph(string? workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        var calls = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var dependencies = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var ownership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string file in SourceFiles(root).Take(1000))
        {
            string relative = Path.GetRelativePath(root, file);
            ownership[relative] = InferOwner(relative);
            string text = SafeRead(file);
            calls[relative] = CallRegex.Matches(text).Select(match => match.Groups[1].Value)
                .Where(name => !IsLanguageKeyword(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();
            dependencies[relative] = ExtractDependencies(file, text);
        }

        return new LlmCodeGraph { WorkspaceRoot = root, Calls = calls, Dependencies = dependencies, Ownership = ownership };
    }

    public LlmPlannerResult CreateRefactorPlan(string? workspaceRoot, string goal)
    {
        var graph = BuildCodeGraph(workspaceRoot);
        return new LlmPlannerResult
        {
            Title = "Solution-wide refactor plan",
            Steps =
            [
                Step("Inventory affected symbols", "Use the symbol graph to locate files related to: " + FirstNonEmpty(goal, "requested refactor")),
                Step("Create checkpoint", "Snapshot affected files before edits."),
                Step("Apply narrow edits", "Change public contracts first, then update callers from the call graph."),
                Step("Run targeted checks", "Build affected projects and run nearby tests."),
                Step("Review architecture impact", "Check ownership boundaries across " + graph.Ownership.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() + " owner area(s).")
            ]
        };
    }

    public LlmPlannerResult CreateMigrationPlan(string? workspaceRoot, string target)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        IReadOnlyList<string> projects = SafeEnumerate(root, "*.*proj", SearchOption.AllDirectories).ToList();
        return new LlmPlannerResult
        {
            Title = "Migration plan to " + FirstNonEmpty(target, "new target"),
            Steps =
            [
                Step("Read project graph", "Inspect " + projects.Count + " project file(s), central package files, and global.json."),
                Step("Update target frameworks", "Move leaf libraries first, then application projects."),
                Step("Upgrade packages", "Batch package updates by subsystem and keep rollback checkpoints."),
                Step("Fix compiler/API breaks", "Use diagnostics and code search to repair breaking changes."),
                Step("Run regression suite", "Run builds and tests listed by the production regression suite.")
            ]
        };
    }

    public LlmTestCoverageExplorer ExploreTests(string? workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        var projects = SafeEnumerate(root, "*.*proj", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase) || SafeRead(path).Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var testNames = SourceFiles(root).Where(path => Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase)).Select(path => Path.GetFileNameWithoutExtension(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = SourceFiles(root)
            .Where(path => Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !Path.GetFileName(path).Contains("test", StringComparison.OrdinalIgnoreCase))
            .Where(path => !testNames.Contains(Path.GetFileNameWithoutExtension(path) + "Tests"))
            .Take(100)
            .ToList();

        return new LlmTestCoverageExplorer
        {
            WorkspaceRoot = root,
            TestProjects = projects,
            SourceFilesWithoutObviousTests = missing,
            MissingTestPlan =
            [
                Step("Prioritize risky files", "Start with changed files, public APIs, and files with many call graph edges."),
                Step("Generate focused tests", "Add tests around observable behavior, errors, and edge cases."),
                Step("Run tests", "Execute the nearest test project before broader regression.")
            ]
        };
    }

    public LlmProfilingAssistantPlan BuildProfilingPlan() => new()
    {
        Cpu = [Step("Capture CPU trace", "Run dotnet-trace or Visual Studio Profiler around the slow workflow."), Step("Rank hot paths", "Group samples by method and async continuation.")],
        Memory = [Step("Capture allocation trace", "Use dotnet-counters/dotnet-gcdump for managed allocation and GC pressure."), Step("Review retention", "Inspect long-lived collections, event handlers, and caches.")],
        AsyncDeadlocks = [Step("Inspect awaits", "Search for .Result, .Wait(), and sync-over-async."), Step("Add timeouts", "Prefer cancellation-aware async flows and ConfigureAwait where appropriate.")]
    };

    public LlmArchitectureReview ReviewArchitecture(string? workspaceRoot)
    {
        var graph = BuildCodeGraph(workspaceRoot);
        return new LlmArchitectureReview
        {
            WorkspaceRoot = graph.WorkspaceRoot,
            Observations =
            [
                "Indexed " + graph.Dependencies.Count + " source/config file(s).",
                "Detected " + graph.Ownership.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() + " ownership area(s)."
            ],
            Risks = graph.Dependencies.Count > 500 ? ["Large solution context requires budget optimization."] : [],
            Recommendations =
            [
                Step("Keep ownership explicit", "Group changes by project or feature area."),
                Step("Protect host/runtime boundary", "Avoid reverse references from SocketJack core to LlmRuntime-specific WPF surfaces."),
                Step("Add regression gates", "Keep runtime, proxy, downloader, and agent tests separate but runnable together.")
            ]
        };
    }

    public LlmDocumentationSyncPlan CreateDocumentationSyncPlan(string? workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        return new LlmDocumentationSyncPlan
        {
            DocumentationFiles = SafeEnumerate(root, "*.md", SearchOption.AllDirectories).Take(100).ToList(),
            SourceAreas = SafeEnumerate(root, "*.*proj", SearchOption.AllDirectories).Select(Path.GetDirectoryName).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList()!,
            Steps =
            [
                Step("Map docs to source", "Pair README/roadmap files with project folders and public endpoints."),
                Step("Detect stale claims", "Compare implemented endpoints, commands, and project names with docs."),
                Step("Patch docs", "Update quickstart, troubleshooting, and golden-path sections."),
                Step("Verify examples", "Run commands or mark known blockers in ISSUES.md.")
            ]
        };
    }

    public LlmModelEvaluationHarnessPlan CreateModelEvaluationHarness(string? workspaceRoot)
    {
        return new LlmModelEvaluationHarnessPlan
        {
            CandidateModels = _models.ListModels().Select(model => model.Key).ToList(),
            Tasks =
            [
                "Explain changed file",
                "Suggest next edit",
                "Plan multi-file refactor",
                "Generate focused unit test",
                "Diagnose build failure"
            ],
            Metrics = ["latency_ms", "tokens_per_second", "compile_success", "test_success", "edit_distance", "human_rating"]
        };
    }

    public LlmContextBudgetPlan OptimizeContext(string? workspaceRoot, int maxTokens)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        var files = SourceFiles(root).Take(1000).ToList();
        return new LlmContextBudgetPlan
        {
            MaxTokens = maxTokens <= 0 ? 32000 : maxTokens,
            IncludeFirst = files.Where(file => Path.GetFileName(file).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase) ||
                                               file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                                               file.Contains(".github", StringComparison.OrdinalIgnoreCase)).Take(50).ToList(),
            Summarize = files.Where(file => Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)).Take(200).ToList(),
            Exclude = files.Where(file => file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                                          file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)).Take(200).ToList()
        };
    }

    private string ResolveWorkspaceRoot(string? workspaceRoot) => Path.GetFullPath(string.IsNullOrWhiteSpace(workspaceRoot) ? _options.DefaultWorkspaceRoot : workspaceRoot);

    private static IEnumerable<string> SourceFiles(string root) => SafeEnumerate(root, "*", SearchOption.AllDirectories)
        .Where(path => SourceExtensions.Contains(Path.GetExtension(path)))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SafeEnumerate(string root, string pattern, SearchOption option)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, pattern, option).ToList() : []; }
        catch { return []; }
    }

    private static string[] SafeReadAllLines(string path)
    {
        try { return File.ReadAllLines(path); } catch { return []; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private static IReadOnlyList<string> ExtractDependencies(string file, string text)
    {
        if (Path.GetExtension(file).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            return Regex.Matches(text, "<PackageReference\\s+Include=\"([^\"]+)\"", RegexOptions.IgnoreCase)
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return Regex.Matches(text, "^\\s*using\\s+([^;]+);", RegexOptions.Multiline)
                .Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return [];
    }

    private static string InferOwner(string relativePath)
    {
        string[] parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length > 1 ? parts[0] : "root";
    }

    private static bool IsLanguageKeyword(string name) =>
        name is "if" or "for" or "foreach" or "while" or "switch" or "catch" or "using" or "return" or "new";

    private static LlmAgentPlanStep Step(string title, string detail) => new() { Title = title, Detail = detail, Status = "pending" };

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }
}
