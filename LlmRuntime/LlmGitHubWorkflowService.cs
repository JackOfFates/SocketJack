using System.Diagnostics;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class LlmGitHubWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly LlmRuntimeOptions _options;
    private readonly LlmAgentRuntime _agentRuntime;
    private readonly string _auditRoot;
    private readonly string _policyPath;
    private readonly ConcurrentDictionary<string, LlmGitHubWorkflowJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public LlmGitHubWorkflowService(LlmRuntimeOptions options, LlmAgentRuntime agentRuntime)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _agentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        _auditRoot = Path.Combine(_options.AgentRoot, "audit");
        _policyPath = Path.Combine(_options.AgentRoot, "policy.json");
        Directory.CreateDirectory(_auditRoot);
    }

    public LlmGitHubRepositoryInfo InspectRepository(string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        var info = new LlmGitHubRepositoryInfo
        {
            WorkspaceRoot = root,
            GitHubCliAvailable = IsCommandAvailable("gh")
        };

        var top = Run("git", "rev-parse --show-toplevel", root);
        info.IsGitRepository = top.ExitCode == 0;
        if (!info.IsGitRepository)
        {
            info.Message = "Workspace is not a git repository.";
            return info;
        }

        info.WorkspaceRoot = top.Output.Trim();
        info.RemoteUrl = Run("git", "remote get-url origin", info.WorkspaceRoot).Output.Trim();
        info.CurrentBranch = Run("git", "branch --show-current", info.WorkspaceRoot).Output.Trim();
        info.HeadSha = Run("git", "rev-parse HEAD", info.WorkspaceRoot).Output.Trim();
        info.IsDirty = !string.IsNullOrWhiteSpace(Run("git", "status --porcelain", info.WorkspaceRoot).Output);
        info.Message = string.IsNullOrWhiteSpace(info.RemoteUrl)
            ? "Git repository has no origin remote."
            : "Git repository connected.";
        return info;
    }

    public LlmGitHubItem FetchIssueOrPullRequest(string type, int number, string? workspaceRoot = null)
    {
        string normalizedType = string.Equals(type, "pr", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "pull_request", StringComparison.OrdinalIgnoreCase)
            ? "pr"
            : "issue";
        string root = ResolveWorkspace(workspaceRoot);

        if (!IsCommandAvailable("gh"))
        {
            return new LlmGitHubItem
            {
                Type = normalizedType,
                Number = number,
                Title = normalizedType.ToUpperInvariant() + " #" + number,
                UnavailableReason = "GitHub CLI is not installed."
            };
        }

        var result = Run("gh", normalizedType + " view " + number + " --json number,title,body,url,state", root);
        if (result.ExitCode != 0)
        {
            return new LlmGitHubItem
            {
                Type = normalizedType,
                Number = number,
                Title = normalizedType.ToUpperInvariant() + " #" + number,
                UnavailableReason = result.Error
            };
        }

        using var document = JsonDocument.Parse(result.Output);
        var rootElement = document.RootElement;
        return new LlmGitHubItem
        {
            Type = normalizedType,
            Number = rootElement.TryGetProperty("number", out var numberElement) && numberElement.TryGetInt32(out int parsedNumber) ? parsedNumber : number,
            Title = rootElement.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            Body = rootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            Url = rootElement.TryGetProperty("url", out var url) ? url.GetString() ?? "" : "",
            State = rootElement.TryGetProperty("state", out var state) ? state.GetString() ?? "" : "",
            RawJson = result.Output
        };
    }

    public LlmGitHubTaskResult StartAgentTaskFromItem(string type, int number, string? workspaceRoot = null)
    {
        var item = FetchIssueOrPullRequest(type, number, workspaceRoot);
        string goal = item.Type.ToUpperInvariant() + " #" + item.Number + ": " + item.Title;
        if (!string.IsNullOrWhiteSpace(item.Body))
            goal += Environment.NewLine + Environment.NewLine + item.Body;

        var session = _agentRuntime.CreateSession(goal, ResolveWorkspace(workspaceRoot), LlmAgentSandboxProfile.WorkspaceWrite, item.Title);
        Audit("github.task.start", "success", session.WorkspaceRoot, "Started agent task for " + item.Type + " #" + item.Number + ".");
        return new LlmGitHubTaskResult { Item = item, Session = session };
    }

    public LlmGitBranchResult CreateOrSwitchBranch(string branch, string? workspaceRoot = null, bool approved = false)
    {
        var policy = GetPolicy();
        string root = ResolveWorkspace(workspaceRoot);
        if (!policy.AllowBranchCreation)
            return new LlmGitBranchResult { Success = false, Branch = branch, Error = "Branch creation is disabled by policy." };
        if (policy.RequireApprovalForGitWrites && !approved)
            return new LlmGitBranchResult { Success = false, Branch = branch, Error = "Branch creation requires approval." };

        branch = NormalizeBranch(branch);
        var exists = Run("git", "rev-parse --verify " + Quote("refs/heads/" + branch), root);
        var result = exists.ExitCode == 0
            ? Run("git", "switch " + Quote(branch), root)
            : Run("git", "switch -c " + Quote(branch), root);
        Audit("git.branch", result.ExitCode == 0 ? "success" : "failed", root, result.Output + result.Error);
        return new LlmGitBranchResult { Success = result.ExitCode == 0, Branch = branch, Output = result.Output, Error = result.Error };
    }

    public LlmGitCommitResult Commit(string message, IEnumerable<string>? paths = null, string? workspaceRoot = null, bool approved = false)
    {
        var policy = GetPolicy();
        string root = ResolveWorkspace(workspaceRoot);
        if (!policy.AllowCommits)
            return new LlmGitCommitResult { Success = false, Error = "Commits are disabled by policy." };
        if (policy.RequireApprovalForGitWrites && !approved)
            return new LlmGitCommitResult { Success = false, Error = "Commit requires approval." };

        var pathList = (paths ?? []).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
        var add = pathList.Length == 0
            ? Run("git", "add -A", root)
            : Run("git", "add -- " + string.Join(' ', pathList.Select(Quote)), root);
        if (add.ExitCode != 0)
            return new LlmGitCommitResult { Success = false, Error = add.Error };

        var commit = Run("git", "commit -m " + Quote(message), root);
        string sha = commit.ExitCode == 0 ? Run("git", "rev-parse HEAD", root).Output.Trim() : "";
        string summary = string.IsNullOrWhiteSpace(sha) ? "" : SummarizeCommit(sha, root);
        Audit("git.commit", commit.ExitCode == 0 ? "success" : "failed", root, commit.Output + commit.Error);
        return new LlmGitCommitResult { Success = commit.ExitCode == 0, Sha = sha, Summary = summary, Output = commit.Output, Error = commit.Error };
    }

    public LlmGitHubPullRequestResult CreateDraftPullRequest(string title, string body, string? workspaceRoot = null, bool approved = false, string? baseBranch = null, bool pushBranch = true)
    {
        var policy = GetPolicy();
        string root = ResolveWorkspace(workspaceRoot);
        if (!policy.AllowPullRequests)
            return new LlmGitHubPullRequestResult { Success = false, Error = "Pull requests are disabled by policy." };
        if (policy.RequireApprovalForGitWrites && !approved)
            return new LlmGitHubPullRequestResult { Success = false, Error = "Pull request creation requires approval." };
        if (!IsCommandAvailable("gh"))
            return new LlmGitHubPullRequestResult { Unavailable = true, Error = "GitHub CLI is not installed." };
        if (!IsGitHubCliAuthenticated(root))
            return new LlmGitHubPullRequestResult { Unavailable = true, Error = "GitHub CLI is not authenticated. Run `gh auth login`." };

        string branch = Run("git", "branch --show-current", root).Output.Trim();
        if (string.IsNullOrWhiteSpace(branch))
            return new LlmGitHubPullRequestResult { Success = false, Error = "Current git branch could not be resolved." };

        if (pushBranch)
        {
            var upstream = Run("git", "rev-parse --abbrev-ref --symbolic-full-name @{u}", root);
            var push = upstream.ExitCode == 0
                ? Run("git", "push", root)
                : Run("git", "push -u origin " + Quote(branch), root);
            if (push.ExitCode != 0)
            {
                Audit("github.pr.push", "failed", root, push.Output + push.Error);
                return new LlmGitHubPullRequestResult { Success = false, Branch = branch, Error = push.Error, Output = push.Output };
            }
        }

        string args = "pr create --draft --title " + Quote(title) + " --body " + Quote(body) + " --head " + Quote(branch);
        if (!string.IsNullOrWhiteSpace(baseBranch))
            args += " --base " + Quote(baseBranch);

        var result = Run("gh", args, root);
        Audit("github.pr.create", result.ExitCode == 0 ? "success" : "failed", root, result.Output + result.Error);
        return new LlmGitHubPullRequestResult
        {
            Success = result.ExitCode == 0,
            Branch = branch,
            Url = result.Output.Trim(),
            Output = result.Output,
            Error = result.Error
        };
    }

    public LlmGitHubWorkflowJob StartDraftPullRequestJob(string title, string body, string? workspaceRoot = null, bool approved = false, string? baseBranch = null, bool pushBranch = true)
    {
        string root = ResolveWorkspace(workspaceRoot);
        var job = new LlmGitHubWorkflowJob
        {
            Kind = "draft_pull_request",
            WorkspaceRoot = root
        };
        _jobs[job.Id] = job;

        _ = Task.Run(() =>
        {
            try
            {
                job.Status = "running";
                job.UpdatedAtUtc = DateTimeOffset.UtcNow;
                job.PullRequestResult = CreateDraftPullRequest(title, body, root, approved, baseBranch, pushBranch);
                job.Status = job.PullRequestResult.Success ? "completed" : job.PullRequestResult.Unavailable ? "unavailable" : "failed";
                job.Error = job.PullRequestResult.Error;
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                job.Error = ex.Message;
            }
            finally
            {
                job.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        });

        Audit("github.pr.job", "queued", root, "Queued draft pull request job " + job.Id + ".");
        return job;
    }

    public IReadOnlyList<LlmGitHubWorkflowJob> ListJobs(string? kind = null) =>
        _jobs.Values
            .Where(job => string.IsNullOrWhiteSpace(kind) || string.Equals(job.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(job => job.UpdatedAtUtc)
            .ToList();

    public string SummarizeCommit(string? sha = null, string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        string target = string.IsNullOrWhiteSpace(sha) ? "HEAD" : sha;
        var show = Run("git", "show --stat --oneline --summary " + Quote(target), root);
        return show.ExitCode == 0 ? show.Output.Trim() : show.Error.Trim();
    }

    public string SummarizePullRequest(int number, string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        if (!IsCommandAvailable("gh"))
            return "GitHub CLI is not installed; PR summary is unavailable.";
        var result = Run("gh", "pr view " + number + " --json title,body,author,state,files,commits", root);
        return result.ExitCode == 0 ? result.Output.Trim() : result.Error.Trim();
    }

    public LlmGitHubReviewResult ReviewDiff(string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        var diff = Run("git", "diff --unified=0", root);
        string fullDiff = diff.Output + BuildUntrackedDiff(root);
        var result = new LlmGitHubReviewResult { Diff = fullDiff };
        foreach (var finding in AnalyzeDiff(fullDiff))
            result.Findings.Add(finding);
        result.Summary = result.Findings.Count == 0 ? "No obvious diff findings." : result.Findings.Count + " finding(s) found.";
        Audit("github.review", "success", root, result.Summary);
        return result;
    }

    public LlmGitHubTaskResult AddressReviewComments(IEnumerable<string> comments, string? workspaceRoot = null)
    {
        string body = string.Join(Environment.NewLine, comments ?? []);
        var session = _agentRuntime.CreateSession("Address review comments:" + Environment.NewLine + body, ResolveWorkspace(workspaceRoot), LlmAgentSandboxProfile.WorkspaceWrite, "Address review comments");
        Audit("github.review.address", "success", session.WorkspaceRoot, "Started review-addressing session.");
        return new LlmGitHubTaskResult { Session = session, Item = new LlmGitHubItem { Type = "review", Body = body, Title = "Review comments" } };
    }

    public LlmGitHubActionsDiagnosis DebugFailedActions(string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        if (!IsCommandAvailable("gh"))
            return new LlmGitHubActionsDiagnosis { Available = false, Error = "GitHub CLI is not installed.", Diagnosis = "Install and authenticate GitHub CLI to inspect Actions runs." };

        var runs = Run("gh", "run list --limit 5 --json databaseId,status,conclusion,name,headBranch,event", root);
        if (runs.ExitCode != 0)
            return new LlmGitHubActionsDiagnosis { Available = false, Error = runs.Error, Diagnosis = "GitHub Actions run list could not be read." };

        return new LlmGitHubActionsDiagnosis
        {
            Available = true,
            RunsJson = runs.Output,
            Diagnosis = runs.Output.Contains("\"failure\"", StringComparison.OrdinalIgnoreCase)
                ? "A recent failing Actions run was found. Use gh run view <id> --log to inspect the failing step."
                : "No failing recent Actions run was detected in the returned run list."
        };
    }

    public LlmSecurityReviewResult SecurityReview(string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        var result = new LlmSecurityReviewResult();
        foreach (string file in EnumerateReviewFiles(root))
        {
            int lineNumber = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNumber++;
                if (LooksLikeSecret(line))
                {
                    result.Findings.Add(new LlmCodeReviewFinding
                    {
                        Priority = 1,
                        File = file,
                        Line = lineNumber,
                        Title = "Potential secret in source",
                        Body = "A line appears to contain a hard-coded secret or credential. Move it to the tool secret store, environment, or user secrets."
                    });
                }
            }
        }
        result.Summary = result.Findings.Count == 0 ? "No obvious hard-coded secrets found." : result.Findings.Count + " potential secret finding(s).";
        Audit("security.review", "success", root, result.Summary);
        return result;
    }

    public LlmDependencyUpdatePlan DependencyUpdatePlan(string? workspaceRoot = null)
    {
        string root = ResolveWorkspace(workspaceRoot);
        var plan = new LlmDependencyUpdatePlan();
        foreach (string file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || path.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase)))
        {
            if (IsIgnoredPath(file))
                continue;
            plan.PackageFiles.Add(file);
            plan.Suggestions.Add("Review package versions in " + file + ". Run `dotnet list package --outdated` when network access is approved.");
        }

        if (plan.PackageFiles.Count == 0)
            plan.Suggestions.Add("No .NET package files were found.");
        Audit("dependency.plan", "success", root, plan.PackageFiles.Count + " package file(s) found.");
        return plan;
    }

    public LlmAgentPolicy GetPolicy()
    {
        if (!File.Exists(_policyPath))
            return new LlmAgentPolicy();
        try
        {
            return JsonSerializer.Deserialize<LlmAgentPolicy>(File.ReadAllText(_policyPath), JsonOptions) ?? new LlmAgentPolicy();
        }
        catch
        {
            return new LlmAgentPolicy();
        }
    }

    public LlmAgentPolicy SavePolicy(LlmAgentPolicy policy)
    {
        Directory.CreateDirectory(_options.AgentRoot);
        File.WriteAllText(_policyPath, JsonSerializer.Serialize(policy ?? new LlmAgentPolicy(), JsonOptions));
        Audit("policy.save", "success", _options.DefaultWorkspaceRoot, "Agent policy saved.");
        return policy ?? new LlmAgentPolicy();
    }

    public IReadOnlyList<LlmAgentAuditEntry> ListAudit(int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 5000);
        if (!Directory.Exists(_auditRoot))
            return [];
        return Directory.EnumerateFiles(_auditRoot, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(Path.GetFileName)
            .SelectMany(ReadAuditFile)
            .OrderByDescending(entry => entry.TimestampUtc)
            .Take(limit)
            .ToList();
    }

    private IEnumerable<LlmCodeReviewFinding> AnalyzeDiff(string diff)
    {
        string currentFile = "";
        int currentLine = 0;
        foreach (string line in (diff ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("+++ b/", StringComparison.Ordinal))
                currentFile = line[6..];
            else if (line.StartsWith("@@", StringComparison.Ordinal))
                currentLine = ParseAddedLineNumber(line);
            else if (line.StartsWith("+", StringComparison.Ordinal) && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (line.Contains("TODO", StringComparison.OrdinalIgnoreCase) || line.Contains("FIXME", StringComparison.OrdinalIgnoreCase))
                {
                    yield return new LlmCodeReviewFinding
                    {
                        Priority = 3,
                        File = currentFile,
                        Line = currentLine,
                        Title = "New TODO/FIXME in diff",
                        Body = "The change introduces a TODO/FIXME. Consider resolving it or linking it to a tracked task."
                    };
                }
                if (LooksLikeSecret(line))
                {
                    yield return new LlmCodeReviewFinding
                    {
                        Priority = 1,
                        File = currentFile,
                        Line = currentLine,
                        Title = "Potential secret added",
                        Body = "The diff appears to add a hard-coded secret."
                    };
                }
                currentLine++;
            }
        }
    }

    private static int ParseAddedLineNumber(string hunk)
    {
        int plus = hunk.IndexOf('+');
        if (plus < 0)
            return 0;
        int end = hunk.IndexOfAny([',', ' '], plus + 1);
        string number = end > plus ? hunk[(plus + 1)..end] : hunk[(plus + 1)..].Trim('@', ' ');
        return int.TryParse(number, out int parsed) ? parsed : 0;
    }

    private void Audit(string action, string outcome, string workspaceRoot, string message)
    {
        Directory.CreateDirectory(_auditRoot);
        var entry = new LlmAgentAuditEntry
        {
            Action = action,
            Outcome = outcome,
            WorkspaceRoot = workspaceRoot,
            Message = (message ?? "").Replace("\r", " ").Replace("\n", " ")
        };
        string path = Path.Combine(_auditRoot, entry.TimestampUtc.ToString("yyyyMMdd") + ".jsonl");
        File.AppendAllText(path, JsonSerializer.Serialize(entry, JsonLineOptions) + Environment.NewLine);
    }

    private static IEnumerable<LlmAgentAuditEntry> ReadAuditFile(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            LlmAgentAuditEntry? entry = null;
            try { entry = JsonSerializer.Deserialize<LlmAgentAuditEntry>(line, JsonOptions); } catch { }
            if (entry != null)
                yield return entry;
        }
    }

    private static string BuildUntrackedDiff(string root)
    {
        var untracked = Run("git", "ls-files --others --exclude-standard", root);
        if (untracked.ExitCode != 0 || string.IsNullOrWhiteSpace(untracked.Output))
            return "";

        var builder = new StringBuilder();
        foreach (string relativePath in untracked.Output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string fullPath = Path.Combine(root, relativePath);
            if (!File.Exists(fullPath) || IsIgnoredPath(fullPath))
                continue;
            builder.AppendLine("+++ b/" + relativePath.Replace('\\', '/'));
            builder.AppendLine("@@ -0,0 +1 @@");
            foreach (string line in File.ReadLines(fullPath).Take(200))
                builder.AppendLine("+" + line);
        }
        return builder.ToString();
    }

    private string ResolveWorkspace(string? workspaceRoot)
    {
        string root = string.IsNullOrWhiteSpace(workspaceRoot) ? _options.DefaultWorkspaceRoot : workspaceRoot;
        root = Path.GetFullPath(root);
        Directory.CreateDirectory(root);
        return root;
    }

    private static string NormalizeBranch(string branch)
    {
        branch = (branch ?? "").Trim();
        if (string.IsNullOrWhiteSpace(branch))
            branch = "codex/work";
        return branch.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ? branch : "codex/" + branch.TrimStart('/');
    }

    private static bool IsCommandAvailable(string command)
    {
        var result = Run(command, "--version", Environment.CurrentDirectory);
        return result.ExitCode == 0;
    }

    private static bool IsGitHubCliAuthenticated(string root)
    {
        var result = Run("gh", "auth status", root);
        return result.ExitCode == 0;
    }

    private static CommandResult Run(string fileName, string arguments, string workingDirectory)
    {
        try
        {
            string resolved = LlmCommandResolver.Resolve(fileName);
            var startInfo = new ProcessStartInfo(string.IsNullOrWhiteSpace(resolved) ? fileName : resolved, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process == null)
                return new CommandResult(-1, "", "Process could not be started.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new CommandResult(process.ExitCode, output, error);
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }

    private static IEnumerable<string> EnumerateReviewFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => !IsIgnoredPath(path))
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".env", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            .Take(1000);

    private static bool IsIgnoredPath(string path) =>
        path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSecret(string line)
    {
        string value = line ?? "";
        if (value.Contains("[REDACTED]", StringComparison.OrdinalIgnoreCase))
            return false;
        string lower = value.ToLowerInvariant();
        bool keyword = lower.Contains("api_key") || lower.Contains("apikey") || lower.Contains("secret") || lower.Contains("password") || lower.Contains("token");
        bool assignment = value.Contains('=') || value.Contains(':');
        return keyword && assignment && value.Length > 16;
    }

    private static string Quote(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    private readonly record struct CommandResult(int ExitCode, string Output, string Error);
}
