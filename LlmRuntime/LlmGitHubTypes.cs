namespace LlmRuntime;

public sealed class LlmGitHubRepositoryInfo
{
    public string WorkspaceRoot { get; set; } = "";

    public bool IsGitRepository { get; set; }

    public bool GitHubCliAvailable { get; set; }

    public string RemoteUrl { get; set; } = "";

    public string CurrentBranch { get; set; } = "";

    public string HeadSha { get; set; } = "";

    public bool IsDirty { get; set; }

    public string Message { get; set; } = "";
}

public sealed class LlmGitHubItem
{
    public string Type { get; set; } = "issue";

    public int Number { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    public string Url { get; set; } = "";

    public string State { get; set; } = "";

    public string RawJson { get; set; } = "";

    public string UnavailableReason { get; set; } = "";
}

public sealed class LlmGitHubTaskResult
{
    public LlmAgentSession? Session { get; set; }

    public LlmGitHubItem Item { get; set; } = new();
}

public sealed class LlmGitBranchResult
{
    public bool Success { get; set; }

    public string Branch { get; set; } = "";

    public string Output { get; set; } = "";

    public string Error { get; set; } = "";
}

public sealed class LlmGitCommitResult
{
    public bool Success { get; set; }

    public string Sha { get; set; } = "";

    public string Summary { get; set; } = "";

    public string Output { get; set; } = "";

    public string Error { get; set; } = "";
}

public sealed class LlmGitHubPullRequestResult
{
    public bool Success { get; set; }

    public bool Unavailable { get; set; }

    public string Branch { get; set; } = "";

    public string Url { get; set; } = "";

    public string Output { get; set; } = "";

    public string Error { get; set; } = "";
}

public sealed class LlmGitHubWorkflowJob
{
    public string Id { get; set; } = "github_job_" + Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = "";

    public string Status { get; set; } = "queued";

    public string WorkspaceRoot { get; set; } = "";

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public LlmGitHubPullRequestResult? PullRequestResult { get; set; }

    public string Error { get; set; } = "";
}

public sealed class LlmCodeReviewFinding
{
    public int Priority { get; set; } = 2;

    public string File { get; set; } = "";

    public int Line { get; set; }

    public string Title { get; set; } = "";

    public string Body { get; set; } = "";
}

public sealed class LlmGitHubReviewResult
{
    public string Summary { get; set; } = "";

    public List<LlmCodeReviewFinding> Findings { get; set; } = new();

    public string Diff { get; set; } = "";
}

public sealed class LlmGitHubActionsDiagnosis
{
    public bool Available { get; set; }

    public string RunsJson { get; set; } = "";

    public string Diagnosis { get; set; } = "";

    public string Error { get; set; } = "";
}

public sealed class LlmSecurityReviewResult
{
    public List<LlmCodeReviewFinding> Findings { get; set; } = new();

    public string Summary { get; set; } = "";
}

public sealed class LlmDependencyUpdatePlan
{
    public List<string> PackageFiles { get; set; } = new();

    public List<string> Suggestions { get; set; } = new();
}

public sealed class LlmAgentPolicy
{
    public bool AllowInternetAccess { get; set; }

    public bool AllowGitHubWrites { get; set; }

    public bool AllowBranchCreation { get; set; } = true;

    public bool AllowCommits { get; set; } = true;

    public bool AllowPullRequests { get; set; }

    public bool RequireApprovalForGitWrites { get; set; } = true;

    public string ModelRoutingPolicy { get; set; } = "local-preferred";

    public string ToolApprovalPolicy { get; set; } = "ask";
}

public sealed class LlmAgentAuditEntry
{
    public string Id { get; set; } = "audit_" + Guid.NewGuid().ToString("N");

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Action { get; set; } = "";

    public string Outcome { get; set; } = "";

    public string WorkspaceRoot { get; set; } = "";

    public string Message { get; set; } = "";
}
