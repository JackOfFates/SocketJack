using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace LlmRuntime;

public sealed class LlmIdeIntegrationService
{
    private static readonly Regex SymbolRegex = new(@"\b(class|struct|interface|enum|record|void|async|public|private|protected|internal|static)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
    private static readonly HashSet<string> IndexedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".csproj", ".sln", ".json", ".md", ".xml", ".html", ".css", ".js", ".ts", ".ps1", ".props", ".targets"
    };

    private readonly LlmRuntimeOptions _options;
    private readonly LlmAgentRuntime _agents;
    private readonly LlmToolRegistry _tools;

    public LlmIdeIntegrationService(LlmRuntimeOptions options, LlmAgentRuntime agents, LlmToolRegistry tools)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
    }

    public LlmIdeCapabilities GetCapabilities() => new()
    {
        LocalPrivacyMode = _options.LocalPrivacyMode,
        Endpoints =
        [
            "/api/v1/ide/capabilities",
            "/api/v1/ide/completions/inline",
            "/api/v1/ide/next-edit",
            "/api/v1/ide/ask",
            "/api/v1/ide/edit/preview",
            "/api/v1/ide/agent/start",
            "/api/v1/ide/plan",
            "/api/v1/ide/checkpoints",
            "/api/v1/ide/rollback",
            "/api/v1/ide/references",
            "/api/v1/ide/index",
            "/api/v1/ide/search",
            "/api/v1/ide/instructions",
            "/api/v1/ide/prompts",
            "/api/v1/ide/mcp/context",
            "/api/v1/ide/model-routing",
            "/api/v1/ide/vision/context",
            "/api/v1/ide/actions",
            "/api/v1/ide/dotnet/upgrade-plan"
        ]
    };

    public LlmIdeCompletionResult CompleteInline(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string extension = Path.GetExtension(request.ActiveFilePath);
        string line = GetLine(request.ActiveFileText, request.CursorLine);
        string trimmed = line.TrimStart();
        string text;

        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (trimmed.StartsWith("if", StringComparison.OrdinalIgnoreCase) && !line.Contains('{'))
                text = " {\n    \n}";
            else if (trimmed.StartsWith("public", StringComparison.OrdinalIgnoreCase) && line.Contains('(') && !line.Contains('{'))
                text = " {\n    throw new NotImplementedException();\n}";
            else if (line.TrimEnd().EndsWith(".", StringComparison.Ordinal))
                text = "ConfigureAwait(false)";
            else
                text = " // TODO: continue implementation";
        }
        else if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
        {
            text = "\n\n## Next Steps\n\n- ";
        }
        else
        {
            text = "";
        }

        return new LlmIdeCompletionResult
        {
            Text = text,
            Detail = "Local deterministic completion scaffold; generation can be routed through loaded GGUF models later.",
            Confidence = string.IsNullOrWhiteSpace(text) ? 0.1 : 0.55,
            References = BuildReferenceLabels(request)
        };
    }

    public IReadOnlyList<LlmIdeSuggestion> SuggestNextEdits(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        var suggestions = new List<LlmIdeSuggestion>();
        string[] lines = SplitLines(request.ActiveFileText);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Contains("TODO", StringComparison.OrdinalIgnoreCase))
            {
                suggestions.Add(new LlmIdeSuggestion
                {
                    Title = "Resolve TODO",
                    Detail = line.Trim(),
                    FilePath = request.ActiveFilePath,
                    Line = i + 1,
                    ReplacementText = line.Replace("TODO", "Implemented", StringComparison.OrdinalIgnoreCase)
                });
            }
            else if (line.Contains("throw new NotImplementedException", StringComparison.Ordinal))
            {
                suggestions.Add(new LlmIdeSuggestion
                {
                    Title = "Implement placeholder",
                    Detail = "Replace NotImplementedException with a focused implementation.",
                    FilePath = request.ActiveFilePath,
                    Line = i + 1,
                    ReplacementText = line.Replace("throw new NotImplementedException();", "// implementation required")
                });
            }
        }

        foreach (var diagnostic in request.Diagnostics.Take(5))
        {
            suggestions.Add(new LlmIdeSuggestion
            {
                Title = "Address " + FirstNonEmpty(diagnostic.Severity, "diagnostic"),
                Detail = diagnostic.Message,
                FilePath = diagnostic.FilePath,
                Line = diagnostic.Line,
                ReplacementText = ""
            });
        }

        return suggestions.Take(12).ToList();
    }

    public LlmIdeAnswer Ask(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string prompt = FirstNonEmpty(request.Prompt, "Explain the current context.");
        var references = BuildReferenceLabels(request);
        string answer = "Ask mode context summary: " + prompt.Trim();
        if (!string.IsNullOrWhiteSpace(request.SelectionText))
            answer += " Selection length: " + request.SelectionText.Length + " characters.";
        if (request.Diagnostics.Count > 0)
            answer += " Diagnostics provided: " + request.Diagnostics.Count + ".";
        if (_options.LocalPrivacyMode)
            answer += " Local privacy mode is enabled; this response is assembled from local context only.";

        return new LlmIdeAnswer { Mode = "ask", Answer = answer, References = references };
    }

    public LlmIdeEditPreview PreviewEdit(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string before = request.ActiveFileText ?? "";
        string selection = request.SelectionText ?? "";
        string prompt = request.Prompt ?? "";
        string after = before;

        if (!string.IsNullOrWhiteSpace(selection) && before.Contains(selection, StringComparison.Ordinal))
        {
            string replacement = selection;
            if (prompt.Contains("null", StringComparison.OrdinalIgnoreCase))
                replacement += Environment.NewLine + "// Added null-safety review note.";
            else if (prompt.Contains("test", StringComparison.OrdinalIgnoreCase))
                replacement += Environment.NewLine + "// Added testability review note.";
            else
                replacement += Environment.NewLine + "// Edited by LlmRuntime preview.";
            after = before.Replace(selection, replacement, StringComparison.Ordinal);
        }

        return new LlmIdeEditPreview
        {
            FilePath = request.ActiveFilePath,
            Before = before,
            After = after,
            Diff = BuildUnifiedDiff(request.ActiveFilePath, before, after),
            RequiresApproval = true
        };
    }

    public LlmAgentSession StartAgent(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string goal = FirstNonEmpty(request.Prompt, "IDE agent task");
        var session = _agents.CreateSession(goal, ResolveWorkspaceRoot(request.WorkspaceRoot), LlmAgentSandboxProfile.WorkspaceWrite, "IDE Agent: " + FirstLine(goal));
        _agents.SavePlan(session.Id, CreatePlan(goal, modifiesFiles: true).Steps);
        return _agents.GetSession(session.Id);
    }

    public LlmIdePlan CreatePlan(string goal, bool modifiesFiles = false)
    {
        goal = FirstNonEmpty(goal, "Complete IDE task");
        return new LlmIdePlan
        {
            Goal = goal,
            ModifiesFiles = modifiesFiles,
            Steps =
            [
                new LlmAgentPlanStep { Title = "Gather context", Detail = "Read active file, selection, diagnostics, git diff, instructions, and prompt files.", Status = "pending" },
                new LlmAgentPlanStep { Title = "Choose minimal change", Detail = "Plan the smallest change that satisfies the IDE request.", Status = "pending" },
                new LlmAgentPlanStep { Title = modifiesFiles ? "Preview edits" : "Explain approach", Detail = modifiesFiles ? "Create a checkpoint and preview file diffs before writing." : "Return a plan without modifying files.", Status = "pending" },
                new LlmAgentPlanStep { Title = "Verify", Detail = "Run targeted tests/build checks or explain why verification is unavailable.", Status = "pending" }
            ]
        };
    }

    public LlmIdeCheckpoint CreateCheckpoint(string workspaceRoot, IEnumerable<string>? relativeFiles = null)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        string id = "checkpoint_" + Guid.NewGuid().ToString("N")[..12];
        string checkpointRoot = Path.Combine(_options.AgentRoot, "checkpoints", id);
        Directory.CreateDirectory(checkpointRoot);

        var copied = new List<string>();
        foreach (string relative in ResolveCheckpointFiles(root, relativeFiles))
        {
            string source = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsUnderRoot(root, source) || !File.Exists(source))
                continue;

            string target = Path.Combine(checkpointRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            copied.Add(relative);
        }

        return new LlmIdeCheckpoint { Id = id, WorkspaceRoot = root, Path = checkpointRoot, Files = copied };
    }

    public LlmIdeRollbackResult Rollback(string checkpointId, string workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        string checkpointRoot = Path.Combine(_options.AgentRoot, "checkpoints", checkpointId ?? "");
        if (!Directory.Exists(checkpointRoot))
            throw new DirectoryNotFoundException("Checkpoint not found: " + checkpointId);

        var restored = new List<string>();
        foreach (string source in SafeEnumerate(checkpointRoot, "*", SearchOption.AllDirectories).Take(500))
        {
            string relative = Path.GetRelativePath(checkpointRoot, source);
            string target = Path.GetFullPath(Path.Combine(root, relative));
            if (!IsUnderRoot(root, target))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            restored.Add(relative);
        }

        return new LlmIdeRollbackResult { CheckpointId = checkpointId ?? "", RestoredFiles = restored.Count, Files = restored };
    }

    public LlmIdeCodeReferenceSet BuildReferences(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string root = ResolveWorkspaceRoot(request.WorkspaceRoot);
        return new LlmIdeCodeReferenceSet
        {
            WorkspaceRoot = root,
            ActiveFilePath = request.ActiveFilePath ?? "",
            SelectionText = request.SelectionText ?? "",
            SolutionFiles = SafeEnumerate(root, "*.sln", SearchOption.TopDirectoryOnly),
            ProjectFiles = SafeEnumerate(root, "*.*proj", SearchOption.AllDirectories).Take(100).ToList(),
            Diagnostics = request.Diagnostics,
            GitDiff = ReadGitDiff(root),
            CustomInstructionFiles = FindExisting(root, ["AGENTS.md", ".github/copilot-instructions.md"]),
            PromptFiles = SafeEnumerate(Path.Combine(root, ".github", "prompts"), "*", SearchOption.AllDirectories).Take(100).ToList()
        };
    }

    public LlmIdeWorkspaceIndex BuildWorkspaceIndex(string workspaceRoot, int maxFiles = 300)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        var documents = SafeEnumerate(root, "*", SearchOption.AllDirectories)
            .Where(file => IndexedExtensions.Contains(Path.GetExtension(file)))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, maxFiles))
            .Select(CreateIndexDocument)
            .ToList();

        return new LlmIdeWorkspaceIndex { WorkspaceRoot = root, FileCount = documents.Count, Documents = documents };
    }

    public IReadOnlyList<LlmIdeSearchResult> Search(string workspaceRoot, string query, int maxFiles = 300)
    {
        query = query ?? "";
        string[] terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
            return [];

        return BuildWorkspaceIndex(workspaceRoot, maxFiles).Documents
            .Select(document => new LlmIdeSearchResult
            {
                FilePath = document.FilePath,
                Symbols = document.Symbols,
                Preview = document.Preview,
                Score = Score(document, terms)
            })
            .Where(result => result.Score > 0)
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.FilePath, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();
    }

    public IReadOnlyList<string> ReadCustomInstructions(string workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        return FindExisting(root, ["AGENTS.md", ".github/copilot-instructions.md"])
            .Select(path => File.ReadAllText(path))
            .ToList();
    }

    public IReadOnlyList<LlmPromptFile> ReadPromptFiles(string workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        string promptRoot = Path.Combine(root, ".github", "prompts");
        if (!Directory.Exists(promptRoot))
            return [];

        return SafeEnumerate(promptRoot, "*", SearchOption.AllDirectories)
            .Take(100)
            .Select(path => new LlmPromptFile { Path = path, Name = Path.GetFileNameWithoutExtension(path), Content = File.ReadAllText(path) })
            .ToList();
    }

    public object BuildMcpContext() => new
    {
        tools = LlmMcpToolAdapter.ExportMcpTools(_tools),
        privacy = _options.LocalPrivacyMode ? "local-only" : "host-configured",
        note = "MCP-shaped tool definitions are available for IDE clients; external MCP server transport adapters are represented by tool metadata."
    };

    public LlmIdeModelRoutingPlan BuildModelRoutingPlan() => new()
    {
        PrivacyMode = _options.LocalPrivacyMode ? "local-only" : "host-configured",
        DefaultRoute = "local-gguf",
        Routes =
        [
            "local-gguf:" + _options.ModelRoot,
            "openai-compatible:/v1/chat/completions",
            "tool-provided-cloud-route:requires BYOK secret and explicit policy"
        ],
        BringYourOwnModel = true,
        BringYourOwnKey = true
    };

    public LlmIdeVisionContext BuildVisionContext(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        string[] accepted = ["image/png", "image/jpeg", "image/webp", "image/gif", "application/pdf"];
        var attachments = request.Attachments
            .Where(attachment => accepted.Any(type => string.Equals(type, attachment.ContentType, StringComparison.OrdinalIgnoreCase)) ||
                                 IsImagePath(attachment.Path))
            .Select(attachment => string.IsNullOrWhiteSpace(attachment.Name) ? Path.GetFileName(attachment.Path) : attachment.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        return new LlmIdeVisionContext
        {
            Supported = true,
            AcceptedContentTypes = accepted,
            AttachmentNames = attachments,
            Summary = attachments.Count == 0
                ? "Vision/image context can be attached for screenshots, UI bugs, diagrams, and design references."
                : "Received " + attachments.Count + " vision attachment(s)."
        };
    }

    public IReadOnlyList<LlmIdeContextAction> BuildContextActions(LlmIdeContextRequest request)
    {
        request ??= new LlmIdeContextRequest();
        var actions = new List<LlmIdeContextAction>
        {
            new() { Id = "ask-selection", Title = "Ask LlmRuntime", Surface = "editor", Mode = "ask" },
            new() { Id = "edit-selection", Title = "Edit Selection", Surface = "editor", Mode = "edit" },
            new() { Id = "agent-task", Title = "Start Agent Task", Surface = "solution-explorer", Mode = "agent" },
            new() { Id = "explain-diagnostic", Title = "Explain Diagnostic", Surface = "error-list", Mode = "ask" },
            new() { Id = "fix-diagnostic", Title = "Fix Diagnostic", Surface = "error-list", Mode = "edit" },
            new() { Id = "plan-change", Title = "Plan Change", Surface = "editor", Mode = "plan" }
        };

        if (request.Diagnostics.Count == 0)
            actions.RemoveAll(action => action.Surface == "error-list");
        return actions;
    }

    public LlmDotNetUpgradePlan BuildDotNetUpgradePlan(string workspaceRoot)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        IReadOnlyList<string> projects = SafeEnumerate(root, "*.*proj", SearchOption.AllDirectories).Take(100).ToList();
        return new LlmDotNetUpgradePlan
        {
            WorkspaceRoot = root,
            ProjectFiles = projects,
            Steps =
            [
                new LlmAgentPlanStep { Title = "Inventory projects", Detail = "Read target frameworks, SDK style, package references, analyzers, and global.json.", Status = "pending" },
                new LlmAgentPlanStep { Title = "Choose target framework", Detail = "Select a compatible .NET target and identify Windows-specific projects.", Status = "pending" },
                new LlmAgentPlanStep { Title = "Plan package upgrades", Detail = "Update package references in small groups and keep rollback checkpoints.", Status = "pending" },
                new LlmAgentPlanStep { Title = "Run upgrade checks", Detail = "Build, test, inspect warnings, and create follow-up tasks for breaking APIs.", Status = "pending" }
            ]
        };
    }

    private string ResolveWorkspaceRoot(string? workspaceRoot)
    {
        string root = string.IsNullOrWhiteSpace(workspaceRoot) ? _options.DefaultWorkspaceRoot : workspaceRoot;
        return Path.GetFullPath(root);
    }

    private static LlmIdeIndexDocument CreateIndexDocument(string file)
    {
        string text = ReadPreview(file, 12000);
        return new LlmIdeIndexDocument
        {
            FilePath = file,
            Language = Path.GetExtension(file).TrimStart('.').ToLowerInvariant(),
            Preview = text.Length > 500 ? text[..500] : text,
            Symbols = SymbolRegex.Matches(text).Select(match => match.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList()
        };
    }

    private static double Score(LlmIdeIndexDocument document, string[] terms)
    {
        string haystack = (document.FilePath + "\n" + document.Preview + "\n" + string.Join("\n", document.Symbols)).ToLowerInvariant();
        double score = 0;
        foreach (string term in terms)
        {
            string needle = term.ToLowerInvariant();
            if (document.Symbols.Any(symbol => symbol.Contains(needle, StringComparison.OrdinalIgnoreCase)))
                score += 4;
            if (document.FilePath.Contains(needle, StringComparison.OrdinalIgnoreCase))
                score += 2;
            if (haystack.Contains(needle, StringComparison.Ordinal))
                score += 1;
        }
        return score;
    }

    private static bool IsImagePath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ResolveCheckpointFiles(string root, IEnumerable<string>? relativeFiles)
    {
        var files = (relativeFiles ?? []).Where(file => !string.IsNullOrWhiteSpace(file)).ToList();
        if (files.Count > 0)
            return files;

        return SafeEnumerate(root, "*", SearchOption.AllDirectories)
            .Where(file => IndexedExtensions.Contains(Path.GetExtension(file)))
            .Take(200)
            .Select(file => Path.GetRelativePath(root, file));
    }

    private static IReadOnlyList<string> SafeEnumerate(string root, string pattern, SearchOption option)
    {
        try
        {
            if (!Directory.Exists(root))
                return [];

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = option == SearchOption.AllDirectories,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            return Directory.EnumerateFiles(root, pattern, options).Take(5000).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<string> FindExisting(string root, IReadOnlyList<string> relativePaths) =>
        relativePaths.Select(path => Path.Combine(root, path)).Where(File.Exists).ToList();

    private static string ReadGitDiff(string root)
    {
        try
        {
            var start = new ProcessStartInfo("git", "diff -- .")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start);
            if (process == null)
                return "";

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cts.Token);
            if (!process.WaitForExit(3000))
            {
                TryKill(process);
                return "";
            }

            string output = outputTask.IsCompletedSuccessfully ? outputTask.Result : "";
            _ = errorTask.IsCompletedSuccessfully ? errorTask.Result : "";
            return output.Length > 12000 ? output[..12000] : output;
        }
        catch
        {
            return "";
        }
    }

    private static bool IsUnderRoot(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static string ReadPreview(string path, int maxChars)
    {
        try
        {
            using var reader = new StreamReader(path);
            char[] buffer = new char[maxChars];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch
        {
            return "";
        }
    }

    private static string BuildUnifiedDiff(string path, string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return "";

        var builder = new StringBuilder();
        builder.AppendLine("--- " + path);
        builder.AppendLine("+++ " + path);
        string[] beforeLines = SplitLines(before);
        string[] afterLines = SplitLines(after);
        int max = Math.Max(beforeLines.Length, afterLines.Length);
        for (int i = 0; i < max; i++)
        {
            string oldLine = i < beforeLines.Length ? beforeLines[i] : "";
            string newLine = i < afterLines.Length ? afterLines[i] : "";
            if (string.Equals(oldLine, newLine, StringComparison.Ordinal))
                continue;
            if (i < beforeLines.Length)
                builder.AppendLine("-" + oldLine);
            if (i < afterLines.Length)
                builder.AppendLine("+" + newLine);
        }
        return builder.ToString();
    }

    private static string[] SplitLines(string text) => (text ?? "").Replace("\r\n", "\n").Split('\n');

    private static string GetLine(string text, int line)
    {
        string[] lines = SplitLines(text);
        int index = Math.Max(0, Math.Min(lines.Length - 1, line <= 0 ? 0 : line - 1));
        return lines.Length == 0 ? "" : lines[index];
    }

    private static IReadOnlyList<string> BuildReferenceLabels(LlmIdeContextRequest request)
    {
        var labels = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.ActiveFilePath))
            labels.Add("active-file:" + request.ActiveFilePath);
        if (!string.IsNullOrWhiteSpace(request.SelectionText))
            labels.Add("selection");
        if (request.Diagnostics.Count > 0)
            labels.Add("diagnostics:" + request.Diagnostics.Count);
        return labels;
    }

    private static string FirstLine(string value)
    {
        value = FirstNonEmpty(value, "Task");
        int newline = value.IndexOfAny(['\r', '\n']);
        return newline >= 0 ? value[..newline] : value;
    }

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

public sealed class LlmPromptFile
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string Content { get; init; } = "";
}
