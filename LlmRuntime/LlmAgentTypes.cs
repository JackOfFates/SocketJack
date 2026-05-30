namespace LlmRuntime;

public enum LlmAgentSandboxProfile
{
    ReadOnly,
    WorkspaceWrite,
    FullAccess
}

public enum LlmAgentSessionStatus
{
    Created,
    Planning,
    Applying,
    Reviewing,
    Running,
    Completed,
    Failed,
    Canceled
}

public sealed class LlmAgentSession
{
    public string Id { get; set; } = "agent_" + Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Goal { get; set; } = "";

    public string WorkspaceRoot { get; set; } = "";

    public LlmAgentSandboxProfile Sandbox { get; set; } = LlmAgentSandboxProfile.WorkspaceWrite;

    public LlmAgentSessionStatus Status { get; set; } = LlmAgentSessionStatus.Created;

    public string Phase { get; set; } = "plan";

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<LlmAgentPlanStep> Plan { get; set; } = new();

    public List<LlmAgentEvent> Events { get; set; } = new();

    public List<string> ChildSessionIds { get; set; } = new();
}

public sealed class LlmAgentPlanStep
{
    public string Id { get; set; } = "step_" + Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "";

    public string Detail { get; set; } = "";

    public string Status { get; set; } = "pending";
}

public sealed class LlmAgentEvent
{
    public string Id { get; set; } = "event_" + Guid.NewGuid().ToString("N");

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Kind { get; set; } = "";

    public string Message { get; set; } = "";

    public string Data { get; set; } = "";
}

public sealed class LlmAgentFileResult
{
    public string Path { get; set; } = "";

    public string FullPath { get; set; } = "";

    public string Content { get; set; } = "";

    public string Before { get; set; } = "";

    public string After { get; set; } = "";

    public string Diff { get; set; } = "";

    public bool Applied { get; set; }
}

public sealed class LlmAgentCommandResult
{
    public string Command { get; set; } = "";

    public string WorkingDirectory { get; set; } = "";

    public int ExitCode { get; set; }

    public string Output { get; set; } = "";

    public string Error { get; set; } = "";

    public bool TimedOut { get; set; }

    public TimeSpan Elapsed { get; set; }

    public string Diagnosis { get; set; } = "";
}

public sealed class LlmAgentJob
{
    public string Id { get; set; } = "job_" + Guid.NewGuid().ToString("N");

    public string SessionId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Status { get; set; } = "queued";

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public LlmAgentCommandResult? CommandResult { get; set; }
}

public sealed class LlmAgentRepoContext
{
    public string WorkspaceRoot { get; set; } = "";

    public List<string> InstructionFiles { get; set; } = new();

    public List<string> SolutionFiles { get; set; } = new();

    public List<string> SkillFiles { get; set; } = new();

    public List<string> PluginFiles { get; set; } = new();
}

public sealed class LlmAgentAutomationHook
{
    public string Id { get; set; } = "hook_" + Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public string Prompt { get; set; } = "";

    public string Schedule { get; set; } = "";

    public bool Enabled { get; set; } = true;
}

public sealed class LlmAgentCheckRun
{
    public string SessionId { get; set; } = "";

    public List<string> Commands { get; set; } = new();

    public List<LlmAgentCommandResult> Results { get; set; } = new();

    public bool Success => Results.All(result => result.ExitCode == 0 && !result.TimedOut);

    public string Diagnosis { get; set; } = "";
}
