using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class LlmAgentRuntime
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly object _sync = new();
    private readonly string _agentRoot;
    private readonly string _sessionRoot;
    private readonly string _automationPath;
    private readonly ConcurrentDictionary<string, LlmAgentJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public LlmAgentRuntime(LlmRuntimeOptions options)
    {
        if (options == null)
            throw new ArgumentNullException(nameof(options));

        Options = options;
        _agentRoot = options.AgentRoot;
        _sessionRoot = Path.Combine(_agentRoot, "sessions");
        _automationPath = Path.Combine(_agentRoot, "automation-hooks.json");
        Directory.CreateDirectory(_sessionRoot);
    }

    public LlmRuntimeOptions Options { get; }

    public IReadOnlyList<LlmAgentSession> ListSessions()
    {
        lock (_sync)
        {
            return Directory.EnumerateFiles(_sessionRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Select(ReadSession)
                .Where(session => session != null)
                .Cast<LlmAgentSession>()
                .OrderByDescending(session => session.UpdatedAtUtc)
                .ToList();
        }
    }

    public LlmAgentSession CreateSession(string goal, string? workspaceRoot = null, LlmAgentSandboxProfile sandbox = LlmAgentSandboxProfile.WorkspaceWrite, string? title = null)
    {
        string workspace = ResolveWorkspaceRoot(workspaceRoot);
        var session = new LlmAgentSession
        {
            Title = string.IsNullOrWhiteSpace(title) ? FirstLine(goal, "Agent Session") : title.Trim(),
            Goal = goal?.Trim() ?? "",
            WorkspaceRoot = workspace,
            Sandbox = sandbox,
            Status = LlmAgentSessionStatus.Created,
            Phase = "plan"
        };

        AddEvent(session, "session.created", "Agent session created.");
        SaveSession(session);
        return session;
    }

    public LlmAgentSession GetSession(string sessionId)
    {
        var session = TryGetSession(sessionId);
        return session ?? throw new InvalidOperationException("Agent session was not found: " + sessionId);
    }

    public LlmAgentSession? TryGetSession(string sessionId)
    {
        string path = GetSessionPath(sessionId);
        lock (_sync)
        {
            return File.Exists(path) ? ReadSession(path) : null;
        }
    }

    public LlmAgentSession SavePlan(string sessionId, IEnumerable<LlmAgentPlanStep> steps)
    {
        var session = GetSession(sessionId);
        session.Plan = (steps ?? []).Select(step => new LlmAgentPlanStep
        {
            Id = string.IsNullOrWhiteSpace(step.Id) ? "step_" + Guid.NewGuid().ToString("N") : step.Id,
            Title = step.Title?.Trim() ?? "",
            Detail = step.Detail?.Trim() ?? "",
            Status = string.IsNullOrWhiteSpace(step.Status) ? "pending" : step.Status.Trim()
        }).ToList();
        session.Status = LlmAgentSessionStatus.Planning;
        session.Phase = "plan";
        AddEvent(session, "plan.saved", "Plan saved with " + session.Plan.Count + " step(s).");
        SaveSession(session);
        return session;
    }

    public LlmAgentSession UpdatePhase(string sessionId, string phase, LlmAgentSessionStatus status)
    {
        var session = GetSession(sessionId);
        session.Phase = string.IsNullOrWhiteSpace(phase) ? session.Phase : phase.Trim();
        session.Status = status;
        AddEvent(session, "phase.updated", "Phase changed to " + session.Phase + ".");
        SaveSession(session);
        return session;
    }

    public LlmAgentFileResult ReadFile(string sessionId, string relativePath)
    {
        var session = GetSession(sessionId);
        string fullPath = ResolvePath(session, relativePath, requireWorkspace: session.Sandbox != LlmAgentSandboxProfile.FullAccess);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("File was not found.", fullPath);

        string content = File.ReadAllText(fullPath);
        AddEvent(session, "file.read", "Read " + relativePath);
        SaveSession(session);
        return new LlmAgentFileResult
        {
            Path = relativePath,
            FullPath = fullPath,
            Content = content
        };
    }

    public LlmAgentFileResult PreviewWriteFile(string sessionId, string relativePath, string content)
    {
        var session = GetSession(sessionId);
        string fullPath = ResolvePath(session, relativePath, requireWorkspace: session.Sandbox != LlmAgentSandboxProfile.FullAccess);
        string before = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
        string after = content ?? "";
        return new LlmAgentFileResult
        {
            Path = relativePath,
            FullPath = fullPath,
            Before = before,
            After = after,
            Diff = BuildUnifiedDiff(relativePath, before, after),
            Applied = false
        };
    }

    public LlmAgentFileResult WriteFile(string sessionId, string relativePath, string content, bool approved)
    {
        var session = GetSession(sessionId);
        if (session.Sandbox == LlmAgentSandboxProfile.ReadOnly)
            throw new InvalidOperationException("Sandbox is read-only.");
        if (!approved)
            throw new InvalidOperationException("File write requires approval.");

        var preview = PreviewWriteFile(sessionId, relativePath, content);
        Directory.CreateDirectory(Path.GetDirectoryName(preview.FullPath)!);
        File.WriteAllText(preview.FullPath, content ?? "");
        preview.Applied = true;
        session.Status = LlmAgentSessionStatus.Applying;
        session.Phase = "apply";
        AddEvent(session, "file.write", "Wrote " + relativePath, preview.Diff);
        SaveSession(session);
        return preview;
    }

    public async Task<LlmAgentCommandResult> RunCommandAsync(string sessionId, string command, bool approved, int timeoutSeconds = 120, CancellationToken cancellationToken = default)
    {
        var session = GetSession(sessionId);
        if (session.Sandbox == LlmAgentSandboxProfile.ReadOnly)
            throw new InvalidOperationException("Sandbox is read-only.");
        if (!approved)
            throw new InvalidOperationException("Terminal command requires approval.");

        timeoutSeconds = Math.Clamp(timeoutSeconds <= 0 ? 120 : timeoutSeconds, 1, 3600);
        var stopwatch = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var startInfo = CreateShellStartInfo(command ?? "");
        startInfo.WorkingDirectory = session.WorkspaceRoot;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Process could not be started.");
            string output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();
            var result = new LlmAgentCommandResult
            {
                Command = command ?? "",
                WorkingDirectory = session.WorkspaceRoot,
                ExitCode = process.ExitCode,
                Output = output,
                Error = error,
                Elapsed = stopwatch.Elapsed,
                Diagnosis = DiagnoseCommand(process.ExitCode, output, error)
            };
            session.Status = process.ExitCode == 0 ? LlmAgentSessionStatus.Reviewing : LlmAgentSessionStatus.Failed;
            session.Phase = process.ExitCode == 0 ? "review" : "diagnose";
            AddEvent(session, "terminal.run", "Command exited with code " + process.ExitCode + ": " + command, output + Environment.NewLine + error);
            SaveSession(session);
            return result;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var result = new LlmAgentCommandResult
            {
                Command = command ?? "",
                WorkingDirectory = session.WorkspaceRoot,
                ExitCode = -1,
                TimedOut = true,
                Elapsed = stopwatch.Elapsed,
                Diagnosis = "Command timed out or was canceled."
            };
            session.Status = LlmAgentSessionStatus.Failed;
            session.Phase = "diagnose";
            AddEvent(session, "terminal.timeout", result.Diagnosis);
            SaveSession(session);
            return result;
        }
    }

    public LlmAgentJob StartCommandJob(string sessionId, string name, string command, bool approved, int timeoutSeconds = 120)
    {
        var session = GetSession(sessionId);
        var job = new LlmAgentJob
        {
            SessionId = session.Id,
            Name = string.IsNullOrWhiteSpace(name) ? command : name
        };
        _jobs[job.Id] = job;
        AddEvent(session, "job.started", "Started background job " + job.Id + ".");
        SaveSession(session);

        _ = Task.Run(async () =>
        {
            try
            {
                job.Status = "running";
                job.UpdatedAtUtc = DateTimeOffset.UtcNow;
                job.CommandResult = await RunCommandAsync(sessionId, command, approved, timeoutSeconds).ConfigureAwait(false);
                job.Status = job.CommandResult.ExitCode == 0 ? "completed" : "failed";
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.CommandResult = new LlmAgentCommandResult { Command = command, WorkingDirectory = session.WorkspaceRoot, ExitCode = -1, Error = ex.Message, Diagnosis = ex.Message };
            }
            finally
            {
                job.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        });

        return job;
    }

    public async Task<LlmAgentCheckRun> RunCheckLoopAsync(string sessionId, IEnumerable<string> commands, bool approved, int timeoutSeconds = 300, CancellationToken cancellationToken = default)
    {
        var commandList = (commands ?? []).Where(command => !string.IsNullOrWhiteSpace(command)).ToList();
        if (commandList.Count == 0)
            commandList = ["dotnet build"];

        var run = new LlmAgentCheckRun
        {
            SessionId = sessionId,
            Commands = commandList
        };

        foreach (string command in commandList)
        {
            var result = await RunCommandAsync(sessionId, command, approved, timeoutSeconds, cancellationToken).ConfigureAwait(false);
            run.Results.Add(result);
            if (result.ExitCode != 0 || result.TimedOut)
                break;
        }

        run.Diagnosis = run.Success
            ? "All checks passed."
            : CreateSelfCorrectionPlan(sessionId, run.Results.LastOrDefault()?.Diagnosis ?? "Check failed.").FirstOrDefault()?.Detail ?? "Check failed.";

        var session = GetSession(sessionId);
        AddEvent(session, "checks.completed", run.Diagnosis, JsonSerializer.Serialize(run, JsonOptions));
        SaveSession(session);
        return run;
    }

    public IReadOnlyList<LlmAgentJob> ListJobs(string? sessionId = null) =>
        _jobs.Values
            .Where(job => string.IsNullOrWhiteSpace(sessionId) || string.Equals(job.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(job => job.UpdatedAtUtc)
            .ToList();

    public IReadOnlyList<LlmAgentPlanStep> CreateSelfCorrectionPlan(string sessionId, string failureSummary)
    {
        var session = GetSession(sessionId);
        var steps = new[]
        {
            new LlmAgentPlanStep { Title = "Inspect failure", Detail = failureSummary, Status = "pending" },
            new LlmAgentPlanStep { Title = "Patch focused cause", Detail = "Apply the smallest targeted code or configuration change that addresses the reported failure.", Status = "pending" },
            new LlmAgentPlanStep { Title = "Rerun checks", Detail = "Run the same failing command or test target again and capture the result.", Status = "pending" },
            new LlmAgentPlanStep { Title = "Summarize outcome", Detail = "Record the fix, remaining risk, and any follow-up work.", Status = "pending" }
        };

        AddEvent(session, "self_correction.planned", "Created self-correction plan.", failureSummary);
        SaveSession(session);
        return steps;
    }

    public IReadOnlyList<LlmAgentSession> CreateSubAgents(string parentSessionId, IEnumerable<string> goals)
    {
        var parent = GetSession(parentSessionId);
        var children = new List<LlmAgentSession>();
        foreach (string goal in goals ?? [])
        {
            var child = CreateSession(goal, parent.WorkspaceRoot, parent.Sandbox, "Sub-agent: " + FirstLine(goal, "Task"));
            parent.ChildSessionIds.Add(child.Id);
            children.Add(child);
        }

        AddEvent(parent, "subagents.created", "Created " + children.Count + " sub-agent session(s).");
        SaveSession(parent);
        return children;
    }

    public LlmAgentRepoContext DiscoverRepoContext(string? workspaceRoot = null)
    {
        string root = ResolveWorkspaceRoot(workspaceRoot);
        return new LlmAgentRepoContext
        {
            WorkspaceRoot = root,
            InstructionFiles = FindExisting(root, ["AGENTS.md", ".editorconfig", ".github/copilot-instructions.md"]),
            SolutionFiles = Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly).ToList(),
            SkillFiles = Directory.Exists(Path.Combine(root, ".agents", "skills"))
                ? Directory.EnumerateFiles(Path.Combine(root, ".agents", "skills"), "SKILL.md", SearchOption.AllDirectories).ToList()
                : new List<string>(),
            PluginFiles = Directory.Exists(Path.Combine(root, ".agents", "plugins"))
                ? Directory.EnumerateFiles(Path.Combine(root, ".agents", "plugins"), "*.json", SearchOption.AllDirectories).ToList()
                : new List<string>()
        };
    }

    public IReadOnlyList<LlmAgentAutomationHook> ListAutomationHooks()
    {
        if (!File.Exists(_automationPath))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<LlmAgentAutomationHook>>(File.ReadAllText(_automationPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public LlmAgentAutomationHook UpsertAutomationHook(LlmAgentAutomationHook hook)
    {
        if (hook == null)
            throw new ArgumentNullException(nameof(hook));
        if (string.IsNullOrWhiteSpace(hook.Id))
            hook.Id = "hook_" + Guid.NewGuid().ToString("N");

        var hooks = ListAutomationHooks().ToList();
        int index = hooks.FindIndex(existing => string.Equals(existing.Id, hook.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            hooks[index] = hook;
        else
            hooks.Add(hook);

        Directory.CreateDirectory(_agentRoot);
        File.WriteAllText(_automationPath, JsonSerializer.Serialize(hooks, JsonOptions));
        return hook;
    }

    public static async Task<int> RunCliAsync(string[] args, LlmRuntimeOptions options, TextWriter output, CancellationToken cancellationToken = default)
    {
        var runtime = new LlmAgentRuntime(options);
        if (args.Length == 0 || args[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("agent new <goal> | agent list | agent run <sessionId> <command>").ConfigureAwait(false);
            return 0;
        }

        if (args[0].Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            var session = runtime.CreateSession(string.Join(' ', args.Skip(1)));
            await output.WriteLineAsync(session.Id).ConfigureAwait(false);
            return 0;
        }

        if (args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var session in runtime.ListSessions())
                await output.WriteLineAsync(session.Id + " " + session.Title).ConfigureAwait(false);
            return 0;
        }

        if (args[0].Equals("run", StringComparison.OrdinalIgnoreCase) && args.Length >= 3)
        {
            var result = await runtime.RunCommandAsync(args[1], string.Join(' ', args.Skip(2)), approved: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(result.Output + result.Error).ConfigureAwait(false);
            return result.ExitCode;
        }

        await output.WriteLineAsync("Unknown agent command.").ConfigureAwait(false);
        return 2;
    }

    private void SaveSession(LlmAgentSession session)
    {
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        lock (_sync)
        {
            Directory.CreateDirectory(_sessionRoot);
            File.WriteAllText(GetSessionPath(session.Id), JsonSerializer.Serialize(session, JsonOptions));
        }
    }

    private LlmAgentSession? ReadSession(string path)
    {
        try { return JsonSerializer.Deserialize<LlmAgentSession>(File.ReadAllText(path), JsonOptions); } catch { return null; }
    }

    private string GetSessionPath(string sessionId) => Path.Combine(_sessionRoot, NormalizeId(sessionId) + ".json");

    private string ResolveWorkspaceRoot(string? workspaceRoot)
    {
        string root = string.IsNullOrWhiteSpace(workspaceRoot) ? Options.DefaultWorkspaceRoot : workspaceRoot;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolvePath(LlmAgentSession session, string path, bool requireWorkspace)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        string fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(session.WorkspaceRoot, path));

        if (requireWorkspace && !IsUnderRoot(fullPath, session.WorkspaceRoot))
            throw new InvalidOperationException("Path is outside the session workspace.");
        return fullPath;
    }

    private static bool IsUnderRoot(string fullPath, string root)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = Path.GetFullPath(fullPath);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Equals(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static void AddEvent(LlmAgentSession session, string kind, string message, string data = "")
    {
        session.Events.Add(new LlmAgentEvent { Kind = kind, Message = message, Data = data ?? "" });
        if (session.Events.Count > 500)
            session.Events.RemoveRange(0, session.Events.Count - 500);
    }

    private static string BuildUnifiedDiff(string path, string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return "";

        var builder = new StringBuilder();
        builder.AppendLine("--- " + path);
        builder.AppendLine("+++ " + path);
        foreach (string line in SplitLines(before))
            builder.AppendLine("-" + line);
        foreach (string line in SplitLines(after))
            builder.AppendLine("+" + line);
        return builder.ToString();
    }

    private static string[] SplitLines(string text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string DiagnoseCommand(int exitCode, string output, string error)
    {
        if (exitCode == 0)
            return "Command succeeded.";
        string combined = (output + Environment.NewLine + error).Trim();
        if (combined.Contains("CS", StringComparison.OrdinalIgnoreCase))
            return "Build or compiler errors were detected. Inspect the reported file and line diagnostics, patch, then rerun the command.";
        if (combined.Contains("test", StringComparison.OrdinalIgnoreCase) || combined.Contains("failed", StringComparison.OrdinalIgnoreCase))
            return "Test or command failure detected. Inspect the failing case, patch the implementation or test expectation, then rerun.";
        return "Command failed with exit code " + exitCode + ". Inspect stderr/stdout and retry after applying a focused fix.";
    }

    private static List<string> FindExisting(string root, IEnumerable<string> relativePaths) =>
        relativePaths.Select(path => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();

    private static string FirstLine(string? value, string fallback)
    {
        string first = (value ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        return string.IsNullOrWhiteSpace(first) ? fallback : (first.Length <= 80 ? first : first[..80]);
    }

    private static string NormalizeId(string value) => LlmToolRegistry.NormalizeId(value);

    private static string QuoteArgument(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static string QuotePosixArgument(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    private static ProcessStartInfo CreateShellStartInfo(string command)
    {
        if (OperatingSystem.IsWindows())
            return new ProcessStartInfo("powershell", "-NoProfile -ExecutionPolicy Bypass -Command " + QuoteArgument(command));

        string shell = Environment.GetEnvironmentVariable("SHELL") ?? "";
        if (string.IsNullOrWhiteSpace(shell))
            shell = "/bin/sh";
        return new ProcessStartInfo(shell, "-lc " + QuotePosixArgument(command));
    }
}
