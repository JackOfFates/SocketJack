using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class LlmAutonomousAgentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly LlmRuntimeOptions _options;
    private readonly LlmModelRegistry _models;
    private readonly LlmAgentRuntime _agents;

    public LlmAutonomousAgentService(LlmRuntimeOptions options, LlmModelRegistry models, LlmAgentRuntime agents)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _agents = agents ?? throw new ArgumentNullException(nameof(agents));
    }

    public async Task<LlmAutonomousAgentRunResult> RunAsync(LlmAutonomousAgentRunRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new LlmAutonomousAgentRunRequest();
        string workspace = ResolveWorkspace(request.WorkspaceRoot);
        var session = _agents.CreateSession(request.Goal, workspace, request.Sandbox, "Autonomous: " + FirstLine(request.Goal, "Agent task"));
        var modelRequest = new LlmChatRequest
        {
            Model = request.Model ?? "",
            MaxTokens = Math.Clamp(request.MaxTokens <= 0 ? 2048 : request.MaxTokens, 1, 131072),
            Temperature = request.Temperature,
            TopP = request.TopP,
            Messages =
            [
                new LlmChatMessage("system", BuildSystemPrompt()),
                new LlmChatMessage("user", BuildUserPrompt(request, workspace))
            ]
        };

        LlmChatResult modelResult = await _models.CompleteChatAsync(modelRequest, cancellationToken).ConfigureAwait(false);
        LlmAutonomousAgentPlan proposal = ParsePlan(modelResult.Content);

        if (proposal.Steps.Count > 0)
        {
            _agents.SavePlan(session.Id, proposal.Steps.Select(step => new LlmAgentPlanStep
            {
                Title = step.Title,
                Detail = step.Detail,
                Status = "pending"
            }));
        }

        var appliedFiles = new List<LlmAgentFileResult>();
        foreach (var file in proposal.Files.Take(Math.Clamp(request.MaxFiles <= 0 ? 20 : request.MaxFiles, 1, 100)))
        {
            if (string.IsNullOrWhiteSpace(file.Path))
                continue;

            var result = request.Approved
                ? _agents.WriteFile(session.Id, file.Path, file.Content, approved: true)
                : _agents.PreviewWriteFile(session.Id, file.Path, file.Content);
            appliedFiles.Add(result);
        }

        LlmAgentCheckRun? checks = null;
        var commands = proposal.Commands.Concat(request.CheckCommands ?? []).Where(command => !string.IsNullOrWhiteSpace(command)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (commands.Length > 0 && request.Approved)
            checks = await _agents.RunCheckLoopAsync(session.Id, commands, approved: true, request.CommandTimeoutSeconds <= 0 ? 300 : request.CommandTimeoutSeconds, cancellationToken).ConfigureAwait(false);

        session = _agents.UpdatePhase(session.Id, checks == null || checks.Success ? "complete" : "diagnose", checks == null || checks.Success ? LlmAgentSessionStatus.Completed : LlmAgentSessionStatus.Failed);
        return new LlmAutonomousAgentRunResult
        {
            Session = session,
            Model = modelResult.Model,
            RawModelOutput = modelResult.Content,
            Proposal = proposal,
            FileResults = appliedFiles,
            CheckRun = checks,
            Applied = request.Approved
        };
    }

    private static string BuildSystemPrompt() =>
        """
        You are LlmRuntime's local autonomous coding agent. Return only JSON.
        The JSON shape must be:
        {
          "summary": "short summary",
          "steps": [{ "title": "step", "detail": "why" }],
          "files": [{ "path": "relative/path", "content": "complete file content" }],
          "commands": ["optional verification command"]
        }
        Only propose relative file paths inside the workspace. Provide complete file contents for each edited file.
        """;

    private static string BuildUserPrompt(LlmAutonomousAgentRunRequest request, string workspace)
    {
        var context = new
        {
            goal = request.Goal,
            workspace,
            files = EnumerateContextFiles(workspace, request.ContextFileLimit <= 0 ? 12 : request.ContextFileLimit)
        };
        return JsonSerializer.Serialize(context, JsonOptions);
    }

    private static IReadOnlyList<object> EnumerateContextFiles(string workspace, int limit)
    {
        if (!Directory.Exists(workspace))
            return [];

        return Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(IsContextFile)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(path => new
            {
                path = Path.GetRelativePath(workspace, path),
                preview = ReadPreview(path, 4000)
            })
            .Cast<object>()
            .ToList();
    }

    private static bool IsContextFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase);
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

    private static LlmAutonomousAgentPlan ParsePlan(string content)
    {
        string json = ExtractJson(content);
        if (string.IsNullOrWhiteSpace(json))
            return new LlmAutonomousAgentPlan { Summary = "Model returned no JSON plan." };

        try
        {
            return JsonSerializer.Deserialize<LlmAutonomousAgentPlan>(json, JsonOptions) ?? new LlmAutonomousAgentPlan();
        }
        catch (JsonException)
        {
            return new LlmAutonomousAgentPlan { Summary = "Model JSON plan could not be parsed.", Steps = [new LlmAutonomousAgentStep { Title = "Inspect model output", Detail = content }] };
        }
    }

    private static string ExtractJson(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : "";
    }

    private string ResolveWorkspace(string? workspaceRoot)
    {
        string root = string.IsNullOrWhiteSpace(workspaceRoot) ? _options.DefaultWorkspaceRoot : workspaceRoot;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FirstLine(string? value, string fallback)
    {
        string first = (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(first) ? fallback : first;
    }
}

public sealed class LlmAutonomousAgentRunRequest
{
    public string Goal { get; set; } = "";
    public string? Model { get; set; }
    public string? WorkspaceRoot { get; set; }
    public bool Approved { get; set; }
    public LlmAgentSandboxProfile Sandbox { get; set; } = LlmAgentSandboxProfile.WorkspaceWrite;
    public int MaxTokens { get; set; } = 2048;
    public float Temperature { get; set; } = 0.2f;
    public float TopP { get; set; } = 0.95f;
    public int ContextFileLimit { get; set; } = 12;
    public int MaxFiles { get; set; } = 20;
    public int CommandTimeoutSeconds { get; set; } = 300;
    public IReadOnlyList<string> CheckCommands { get; set; } = [];
}

public sealed class LlmAutonomousAgentRunResult
{
    public LlmAgentSession Session { get; init; } = new();
    public string Model { get; init; } = "";
    public string RawModelOutput { get; init; } = "";
    public LlmAutonomousAgentPlan Proposal { get; init; } = new();
    public IReadOnlyList<LlmAgentFileResult> FileResults { get; init; } = [];
    public LlmAgentCheckRun? CheckRun { get; init; }
    public bool Applied { get; init; }
}

public sealed class LlmAutonomousAgentPlan
{
    public string Summary { get; set; } = "";
    public List<LlmAutonomousAgentStep> Steps { get; set; } = new();
    public List<LlmAutonomousAgentFileEdit> Files { get; set; } = new();
    public List<string> Commands { get; set; } = new();
}

public sealed class LlmAutonomousAgentStep
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

public sealed class LlmAutonomousAgentFileEdit
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}
