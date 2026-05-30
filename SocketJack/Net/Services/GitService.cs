using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Services;

/// <summary>
/// Executes a narrow, Codex-style Git tool surface for LmVsProxy agent mode.
/// This service builds known git commands itself instead of accepting raw shell text.
/// </summary>
public sealed class GitService
{
    private const int DefaultTimeoutMs = 120000;
    private const int MaxTimeoutMs = 600000;
    private const int MaxCapturedChars = 32000;
    private const int StreamReadBufferBytes = 65536;
    private const int PostExitDrainMs = 750;
    private static readonly Regex SafeRefRegex = new Regex("^[A-Za-z0-9._/\\-]+$", RegexOptions.Compiled);

    public bool IsMutating(GitCommandRequest request)
    {
        string operation = NormalizeOperation(request?.Operation);
        switch (operation)
        {
            case "stage":
            case "unstage":
            case "commit":
            case "create_branch":
            case "switch_branch":
            case "fetch":
            case "pull":
            case "push":
                return true;
            default:
                return false;
        }
    }

    public string BuildApprovalCommand(GitCommandRequest request)
    {
        string operation = NormalizeOperation(request?.Operation);
        string cwd = string.IsNullOrWhiteSpace(request?.WorkingDirectory) ? "" : request.WorkingDirectory.Trim();
        string target = request == null ? "" : FirstNonEmpty(request.Branch, request.Ref, request.Remote, request.Message);
        if (!string.IsNullOrWhiteSpace(target) && target.Length > 120)
            target = target.Substring(0, 120) + "...";
        return "git:" + operation + (string.IsNullOrWhiteSpace(target) ? "" : ":" + target) +
               (string.IsNullOrWhiteSpace(cwd) ? "" : " @ " + cwd);
    }

    public async Task<GitCommandResult> ExecuteAsync(GitCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string operation = NormalizeOperation(request.Operation);
        int timeoutMs = request.TimeoutMs <= 0 ? DefaultTimeoutMs : Math.Min(request.TimeoutMs, MaxTimeoutMs);
        string workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        if (operation == "dependency_check")
        {
            GitProcessResult version = await RunGitAsync("--version", workingDirectory, timeoutMs, cancellationToken).ConfigureAwait(false);
            return new GitCommandResult
            {
                Operation = operation,
                Command = "git --version",
                WorkingDirectory = workingDirectory,
                ExitCode = version.ExitCode,
                Output = version.Output,
                Error = version.Error,
                DurationMs = ElapsedMs(started),
                TimedOut = version.TimedOut,
                Canceled = version.Canceled,
                Ok = version.ExitCode == 0 && !version.TimedOut && !version.Canceled,
                GitAvailable = version.ExitCode == 0,
                DestructiveBlocked = false
            };
        }

        GitProcessResult rootResult = await RunGitAsync("rev-parse --show-toplevel", workingDirectory, 30000, cancellationToken).ConfigureAwait(false);
        if (rootResult.ExitCode != 0)
        {
            return new GitCommandResult
            {
                Operation = operation,
                Command = "git rev-parse --show-toplevel",
                WorkingDirectory = workingDirectory,
                ExitCode = rootResult.ExitCode,
                Output = rootResult.Output,
                Error = string.IsNullOrWhiteSpace(rootResult.Error) ? "Working directory is not inside a Git repository." : rootResult.Error,
                DurationMs = ElapsedMs(started),
                TimedOut = rootResult.TimedOut,
                Canceled = rootResult.Canceled,
                Ok = false,
                GitAvailable = rootResult.ExitCode != -1
            };
        }

        string repositoryRoot = NormalizeGitPath(rootResult.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Directory.Exists(repositoryRoot))
            repositoryRoot = workingDirectory;

        if (request.AllowedRoots != null && request.AllowedRoots.Count > 0 &&
            !request.AllowedRoots.Any(root => IsPathInsideRoot(repositoryRoot, root)))
        {
            return new GitCommandResult
            {
                Operation = operation,
                Command = "git rev-parse --show-toplevel",
                WorkingDirectory = workingDirectory,
                RepositoryRoot = repositoryRoot,
                ExitCode = -1,
                Error = "Repository root is outside the authorized filesystem roots: " + repositoryRoot,
                DurationMs = ElapsedMs(started),
                Ok = false,
                GitAvailable = true
            };
        }

        if (!TryBuildArguments(request, repositoryRoot, out string arguments, out string blockedReason, out bool destructiveBlocked))
        {
            return new GitCommandResult
            {
                Operation = operation,
                Command = "git " + (arguments ?? operation),
                WorkingDirectory = workingDirectory,
                RepositoryRoot = repositoryRoot,
                ExitCode = -1,
                Error = blockedReason,
                DurationMs = ElapsedMs(started),
                Ok = false,
                GitAvailable = true,
                DestructiveBlocked = destructiveBlocked
            };
        }

        GitProcessResult result = await RunGitAsync(arguments, repositoryRoot, timeoutMs, cancellationToken).ConfigureAwait(false);
        return new GitCommandResult
        {
            Operation = operation,
            Command = "git " + arguments,
            WorkingDirectory = workingDirectory,
            RepositoryRoot = repositoryRoot,
            ExitCode = result.ExitCode,
            Output = result.Output,
            Error = result.Error,
            DurationMs = ElapsedMs(started),
            TimedOut = result.TimedOut,
            Canceled = result.Canceled,
            Ok = result.ExitCode == 0 && !result.TimedOut && !result.Canceled,
            GitAvailable = true,
            DestructiveBlocked = false
        };
    }

    private static bool TryBuildArguments(GitCommandRequest request, string repositoryRoot, out string arguments, out string error, out bool destructiveBlocked)
    {
        arguments = "";
        error = "";
        destructiveBlocked = false;
        string operation = NormalizeOperation(request.Operation);
        int take = Math.Max(1, Math.Min(request.Take <= 0 ? 20 : request.Take, 200));

        switch (operation)
        {
            case "status":
                arguments = "status --short --branch";
                return true;

            case "diff":
                arguments = request.Staged ? "diff --cached --no-ext-diff" : "diff --no-ext-diff";
                if (request.Stat)
                    arguments += " --stat";
                return AppendPathSpecs(ref arguments, repositoryRoot, request.PathSpecs, out error);

            case "changed_files":
                arguments = request.Staged
                    ? "diff --cached --name-status"
                    : "status --porcelain=v1 -uall";
                return true;

            case "tracked_files":
                arguments = request.IncludeUntracked
                    ? "ls-files --cached --others --exclude-standard"
                    : "ls-files --cached";
                return AppendPathSpecs(ref arguments, repositoryRoot, request.PathSpecs, out error);

            case "file_diff":
                if (!TryNormalizeSinglePathSpec(FirstNonEmpty(request.PathSpecs?.FirstOrDefault(), request.Path), repositoryRoot, out string diffPath, out error))
                    return false;
                arguments = request.Staged ? "diff --cached --no-ext-diff" : "diff --no-ext-diff";
                if (request.Stat)
                    arguments += " --stat";
                arguments += " -- " + QuoteArgument(diffPath);
                return true;

            case "file_at_ref":
                if (!IsSafeRef(FirstNonEmpty(request.Ref, "HEAD"), out error))
                    return false;
                if (!TryNormalizeSinglePathSpec(FirstNonEmpty(request.PathSpecs?.FirstOrDefault(), request.Path), repositoryRoot, out string refPath, out error))
                    return false;
                arguments = "show " + QuoteArgument(FirstNonEmpty(request.Ref, "HEAD").Trim() + ":" + refPath);
                return true;

            case "file_history":
                if (!TryNormalizeSinglePathSpec(FirstNonEmpty(request.PathSpecs?.FirstOrDefault(), request.Path), repositoryRoot, out string historyPath, out error))
                    return false;
                arguments = "log --follow --decorate --oneline -n " + take.ToString() + " -- " + QuoteArgument(historyPath);
                return true;

            case "file_blame":
                if (!TryNormalizeSinglePathSpec(FirstNonEmpty(request.PathSpecs?.FirstOrDefault(), request.Path), repositoryRoot, out string blamePath, out error))
                    return false;
                arguments = "blame";
                if (request.StartLine > 0)
                {
                    int endLine = request.EndLine >= request.StartLine ? request.EndLine : request.StartLine;
                    arguments += " -L " + request.StartLine.ToString() + "," + endLine.ToString();
                }
                arguments += " -- " + QuoteArgument(blamePath);
                return true;

            case "grep":
                if (string.IsNullOrWhiteSpace(request.Query))
                {
                    error = "git_grep requires query.";
                    return false;
                }
                arguments = "grep -n -I --break --heading -e " + QuoteArgument(request.Query);
                return AppendPathSpecs(ref arguments, repositoryRoot, request.PathSpecs, out error);

            case "log":
                arguments = "log --decorate --oneline -n " + take.ToString();
                if (!string.IsNullOrWhiteSpace(request.Ref))
                {
                    if (!IsSafeRef(request.Ref, out error))
                        return false;
                    arguments += " " + QuoteArgument(request.Ref.Trim());
                }
                return true;

            case "show":
                if (!IsSafeRef(FirstNonEmpty(request.Ref, "HEAD"), out error))
                    return false;
                arguments = request.IncludePatch
                    ? "show --no-ext-diff --stat --patch " + QuoteArgument(FirstNonEmpty(request.Ref, "HEAD").Trim())
                    : "show --no-ext-diff --stat --summary " + QuoteArgument(FirstNonEmpty(request.Ref, "HEAD").Trim());
                return true;

            case "branch":
                arguments = "branch --all --verbose --no-abbrev";
                return true;

            case "remote":
                arguments = "remote -v";
                return true;

            case "stage":
                if (request.PathSpecs == null || request.PathSpecs.Count == 0)
                {
                    error = "git_stage requires at least one path. Use paths=[\".\"] only when staging every current repo change is intentional.";
                    return false;
                }
                arguments = "add";
                return AppendPathSpecs(ref arguments, repositoryRoot, request.PathSpecs, out error);

            case "unstage":
                if (request.PathSpecs == null || request.PathSpecs.Count == 0)
                {
                    error = "git_unstage requires at least one path.";
                    return false;
                }
                arguments = "restore --staged";
                return AppendPathSpecs(ref arguments, repositoryRoot, request.PathSpecs, out error);

            case "commit":
                if (string.IsNullOrWhiteSpace(request.Message))
                {
                    error = "git_commit requires a commit message.";
                    return false;
                }
                arguments = "commit -m " + QuoteArgument(SanitizeSingleLine(request.Message, 300));
                return true;

            case "create_branch":
                if (!IsSafeRef(request.Branch, out error))
                    return false;
                arguments = request.Checkout ? "switch -c " : "branch ";
                arguments += QuoteArgument(request.Branch.Trim());
                return true;

            case "switch_branch":
                if (!IsSafeRef(request.Branch, out error))
                    return false;
                arguments = "switch " + QuoteArgument(request.Branch.Trim());
                return true;

            case "fetch":
                arguments = "fetch --all --prune";
                return true;

            case "pull":
                arguments = "pull --ff-only";
                return true;

            case "push":
                arguments = "push";
                if (!string.IsNullOrWhiteSpace(request.Remote))
                {
                    if (!IsSafeRef(request.Remote, out error))
                        return false;
                    arguments += " " + QuoteArgument(request.Remote.Trim());
                }
                if (!string.IsNullOrWhiteSpace(request.Branch))
                {
                    if (!IsSafeRef(request.Branch, out error))
                        return false;
                    arguments += " " + QuoteArgument(request.Branch.Trim());
                }
                if (request.SetUpstream)
                    arguments += " --set-upstream";
                return true;

            case "reset":
            case "clean":
            case "checkout_path":
            case "restore_path":
                destructiveBlocked = true;
                error = "Destructive Git operation blocked. This tool intentionally does not expose reset/clean/checkout-file/restore-file operations.";
                return false;

            default:
                error = "Unknown Git operation: " + operation;
                return false;
        }
    }

    private static bool AppendPathSpecs(ref string arguments, string repositoryRoot, List<string> pathSpecs, out string error)
    {
        error = "";
        if (pathSpecs == null || pathSpecs.Count == 0)
            return true;

        var normalized = new List<string>();
        foreach (string spec in pathSpecs)
        {
            if (!TryNormalizeSinglePathSpec(spec, repositoryRoot, out string relative, out error))
                return false;
            normalized.Add(relative);
        }

        if (normalized.Count > 0)
        {
            arguments += " --";
            foreach (string spec in normalized)
                arguments += " " + QuoteArgument(spec);
        }
        return true;
    }

    private static bool TryNormalizeSinglePathSpec(string spec, string repositoryRoot, out string relative, out string error)
    {
        relative = "";
        error = "";
        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "Git file path is required.";
            return false;
        }

        string cleaned = spec.Trim().Trim('"', '\'');
        if (cleaned.IndexOf('\0') >= 0 || cleaned.IndexOf('\r') >= 0 || cleaned.IndexOf('\n') >= 0)
        {
            error = "Pathspec contains invalid control characters.";
            return false;
        }
        if (cleaned.StartsWith("-", StringComparison.Ordinal))
        {
            error = "Pathspecs may not start with '-'.";
            return false;
        }

        try
        {
            if (Path.IsPathRooted(cleaned))
            {
                string full = Path.GetFullPath(cleaned);
                string root = Path.GetFullPath(repositoryRoot);
                if (!IsPathInsideRoot(full, root))
                {
                    error = "Path is outside the repository root: " + cleaned;
                    return false;
                }
                relative = Path.GetRelativePath(root, full);
            }
            else
            {
                string full = Path.GetFullPath(Path.Combine(repositoryRoot, cleaned));
                if (!IsPathInsideRoot(full, Path.GetFullPath(repositoryRoot)))
                {
                    error = "Path is outside the repository root: " + cleaned;
                    return false;
                }
                relative = cleaned;
            }
        }
        catch (Exception ex)
        {
            error = "Invalid pathspec '" + cleaned + "': " + ex.Message;
            return false;
        }

        relative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        return true;
    }

    private static async Task<GitProcessResult> RunGitAsync(string arguments, string workingDirectory, int timeoutMs, CancellationToken cancellationToken)
    {
        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            process.EnableRaisingEvents = true;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Git process failed to start.");
            }
            catch (Exception ex)
            {
                return new GitProcessResult
                {
                    ExitCode = -1,
                    Error = ex.Message
                };
            }

            var stdoutBuffer = new CapturedTextBuffer(MaxCapturedChars);
            var stderrBuffer = new CapturedTextBuffer(MaxCapturedChars);
            Task stdoutTask = CaptureStreamAsync(process.StandardOutput.BaseStream, stdoutBuffer, cancellationToken);
            Task stderrTask = CaptureStreamAsync(process.StandardError.BaseStream, stderrBuffer, cancellationToken);
            ObserveTaskException(stdoutTask);
            ObserveTaskException(stderrTask);

            Task waitTask = Task.Run(() => process.WaitForExit());
            Task delayTask = Task.Delay(Math.Max(1, timeoutMs), cancellationToken);
            Task completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);

            bool timedOut = false;
            bool canceled = false;
            if (completed != waitTask)
            {
                canceled = cancellationToken.IsCancellationRequested;
                timedOut = !canceled;
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }

                await WaitWithoutThrowAsync(waitTask, PostExitDrainMs).ConfigureAwait(false);
            }
            else
            {
                await WaitWithoutThrowAsync(Task.WhenAll(stdoutTask, stderrTask), PostExitDrainMs).ConfigureAwait(false);
            }

            int exitCode = -1;
            try
            {
                if (process.HasExited)
                    exitCode = process.ExitCode;
            }
            catch { }

            return new GitProcessResult
            {
                ExitCode = exitCode,
                Output = stdoutBuffer.GetText(),
                Error = stderrBuffer.GetText(),
                TimedOut = timedOut,
                Canceled = canceled
            };
        }
    }

    private static async Task CaptureStreamAsync(Stream stream, CapturedTextBuffer output, CancellationToken cancellationToken)
    {
        if (stream == null || output == null)
            return;

        byte[] buffer = new byte[StreamReadBufferBytes];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        char[] chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        while (true)
        {
            int read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
                break;

            int charCount = decoder.GetChars(buffer, 0, read, chars, 0, flush: false);
            if (charCount > 0)
                output.Append(chars, charCount);
        }

        int finalCharCount = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
        if (finalCharCount > 0)
            output.Append(chars, finalCharCount);
    }

    private static async Task WaitWithoutThrowAsync(Task task, int timeoutMs)
    {
        if (task == null)
            return;

        try
        {
            await Task.WhenAny(task, Task.Delay(Math.Max(1, timeoutMs))).ConfigureAwait(false);
        }
        catch { }
    }

    private static void ObserveTaskException(Task task)
    {
        if (task == null)
            return;

        task.ContinueWith(t =>
        {
            Exception ignored = t.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static string NormalizeOperation(string operation)
    {
        operation = (operation ?? "").Trim().ToLowerInvariant();
        if (operation.StartsWith("git_", StringComparison.Ordinal))
            operation = operation.Substring(4);
        if (string.IsNullOrWhiteSpace(operation))
            return "status";
        return operation.Replace("-", "_");
    }

    private static bool IsSafeRef(string value, out string error)
    {
        error = "";
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Git ref/branch is required.";
            return false;
        }
        if (value.StartsWith("-", StringComparison.Ordinal) ||
            value.Contains("..") ||
            value.Contains("@{") ||
            value.Contains("\\") ||
            value.Contains(":") ||
            !SafeRefRegex.IsMatch(value))
        {
            error = "Unsafe Git ref/branch rejected: " + value;
            return false;
        }
        return true;
    }

    private static string ResolveWorkingDirectory(string workingDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
                return Path.GetFullPath(workingDirectory);
            string current = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                return current;
        }
        catch { }

        return AppContext.BaseDirectory;
    }

    private static string QuoteArgument(string value)
    {
        value = value ?? "";
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            if (backslashes > 0)
            {
                sb.Append('\\', backslashes);
                backslashes = 0;
            }
            sb.Append(c);
        }
        if (backslashes > 0)
            sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    private static string FirstNonEmpty(params string[] values)
    {
        if (values == null)
            return "";
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }
        return "";
    }

    private static string SanitizeSingleLine(string value, int maxLength)
    {
        value = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (maxLength > 0 && value.Length > maxLength)
            value = value.Substring(0, maxLength);
        return value;
    }

    private static string NormalizeGitPath(string value)
    {
        return (value ?? "").Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
    }

    private static bool IsPathInsideRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static int ElapsedMs(DateTimeOffset started)
    {
        return Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds));
    }

    private sealed class GitProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public string Error { get; set; } = "";
        public bool TimedOut { get; set; }
        public bool Canceled { get; set; }
    }

    private sealed class CapturedTextBuffer
    {
        private readonly int _maxChars;
        private readonly StringBuilder _head = new StringBuilder();
        private readonly StringBuilder _tail = new StringBuilder();
        private readonly object _lock = new object();
        private bool _truncated;

        public CapturedTextBuffer(int maxChars)
        {
            _maxChars = Math.Max(1024, maxChars);
        }

        public void Append(char[] chars, int count)
        {
            if (chars == null || count <= 0)
                return;

            lock (_lock)
            {
                int headLimit = _maxChars / 2;
                int tailLimit = _maxChars - headLimit;

                if (_head.Length < headLimit)
                {
                    int take = Math.Min(count, headLimit - _head.Length);
                    _head.Append(chars, 0, take);
                    if (take == count)
                        return;

                    AppendTail(chars, take, count - take, tailLimit);
                    _truncated = true;
                    return;
                }

                AppendTail(chars, 0, count, tailLimit);
                _truncated = true;
            }
        }

        public string GetText()
        {
            lock (_lock)
            {
                if (!_truncated)
                    return _head.ToString();

                return _head.ToString() +
                       "\n...[git output truncated]...\n" +
                       _tail.ToString();
            }
        }

        private void AppendTail(char[] chars, int index, int count, int tailLimit)
        {
            _tail.Append(chars, index, count);
            if (_tail.Length <= tailLimit)
                return;

            _tail.Remove(0, _tail.Length - tailLimit);
        }
    }
}

public sealed class GitCommandRequest
{
    public string Operation { get; set; } = "status";
    public string WorkingDirectory { get; set; } = "";
    public List<string> AllowedRoots { get; set; } = new List<string>();
    public List<string> PathSpecs { get; set; } = new List<string>();
    public string Path { get; set; } = "";
    public string Query { get; set; } = "";
    public string Ref { get; set; } = "";
    public string Branch { get; set; } = "";
    public string Message { get; set; } = "";
    public string Remote { get; set; } = "";
    public bool Staged { get; set; }
    public bool Stat { get; set; }
    public bool IncludePatch { get; set; }
    public bool IncludeUntracked { get; set; } = true;
    public bool Checkout { get; set; } = true;
    public bool SetUpstream { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public int Take { get; set; }
    public int TimeoutMs { get; set; }
}

public sealed class GitCommandResult
{
    public bool Ok { get; set; }
    public bool GitAvailable { get; set; }
    public bool DestructiveBlocked { get; set; }
    public string Operation { get; set; } = "";
    public string Command { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string RepositoryRoot { get; set; } = "";
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public bool Canceled { get; set; }
}
