using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.Services;

/// <summary>
/// Executes local terminal commands for LmVsProxy after the caller has passed
/// the GUI-owned permission gate.
/// </summary>
public sealed class TerminalService
{
    private const int DefaultTimeoutMs = 120000;
    private const int MaxTimeoutMs = 600000;
    private const int MaxCapturedChars = 24000;
    private const int StreamReadBufferBytes = 65536;
    private const int PostExitDrainMs = 750;

    public async Task<TerminalCommandResult> ExecuteAsync(TerminalCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string command = (request.Command ?? "").Trim();
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("Terminal command is required.");

        int timeoutMs = request.TimeoutMs <= 0 ? DefaultTimeoutMs : Math.Min(request.TimeoutMs, MaxTimeoutMs);
        string workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory, request.AllowedWorkingDirectories);
        ProcessStartInfo startInfo = BuildStartInfo(command, request.Shell, workingDirectory, request.AllowExecutionPolicyBypass);
        DateTimeOffset started = DateTimeOffset.UtcNow;

        using (var process = new Process())
        {
            process.StartInfo = startInfo;
            process.EnableRaisingEvents = true;

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Process failed to start.");
            }
            catch (Exception ex)
            {
                return new TerminalCommandResult
                {
                    Command = command,
                    Shell = NormalizeShell(request.Shell),
                    WorkingDirectory = workingDirectory,
                    ExitCode = -1,
                    Error = ex.Message,
                    DurationMs = 0,
                    TimedOut = false
                };
            }

            var stdoutBuffer = new CapturedTextBuffer(MaxCapturedChars);
            var stderrBuffer = new CapturedTextBuffer(MaxCapturedChars);
            Task stdoutTask = CaptureStreamAsync(process.StandardOutput.BaseStream, stdoutBuffer, cancellationToken);
            Task stderrTask = CaptureStreamAsync(process.StandardError.BaseStream, stderrBuffer, cancellationToken);
            ObserveTaskException(stdoutTask);
            ObserveTaskException(stderrTask);

            Task waitTask = Task.Run(() => process.WaitForExit());
            Task delayTask = Task.Delay(timeoutMs, cancellationToken);
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

            return new TerminalCommandResult
            {
                Command = command,
                Shell = NormalizeShell(request.Shell),
                WorkingDirectory = workingDirectory,
                ExitCode = exitCode,
                Output = stdoutBuffer.GetText(),
                Error = stderrBuffer.GetText(),
                DurationMs = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds)),
                TimedOut = timedOut,
                Canceled = canceled
            };
        }
    }

    private static ProcessStartInfo BuildStartInfo(string command, string shell, string workingDirectory, bool allowExecutionPolicyBypass)
    {
        string normalizedShell = NormalizeShell(shell);
        if (normalizedShell == "cmd")
        {
            return new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/S /C " + QuoteWindowsArgument(command),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
        }

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return new ProcessStartInfo
        {
            FileName = normalizedShell == "pwsh" ? "pwsh.exe" : "powershell.exe",
            Arguments = "-NoLogo -NoProfile " + (allowExecutionPolicyBypass ? "-ExecutionPolicy Bypass " : "") + "-EncodedCommand " + encoded,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
    }

    private static string NormalizeShell(string shell)
    {
        shell = (shell ?? "").Trim().ToLowerInvariant();
        if (shell == "cmd" || shell == "cmd.exe")
            return "cmd";
        if (shell == "pwsh" || shell == "pwsh.exe")
            return "pwsh";
        return "powershell";
    }

    private static string ResolveWorkingDirectory(string workingDirectory, string[] allowedWorkingDirectories)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)) {
                string resolved = Path.GetFullPath(workingDirectory);
                if (!IsAllowedWorkingDirectory(resolved, allowedWorkingDirectories))
                    throw new InvalidOperationException("Working directory is outside the allowed terminal roots.");
                return resolved;
            }
            string current = Directory.GetCurrentDirectory();
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                return current;
        }
        catch { }

        string fallback = AppContext.BaseDirectory;
        if (!IsAllowedWorkingDirectory(fallback, allowedWorkingDirectories))
            throw new InvalidOperationException("Default working directory is outside the allowed terminal roots.");
        return fallback;
    }

    private static bool IsAllowedWorkingDirectory(string workingDirectory, string[] allowedWorkingDirectories)
    {
        if (allowedWorkingDirectories == null || allowedWorkingDirectories.Length == 0)
            return true;

        string normalized = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return allowedWorkingDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Any(root => normalized.Equals(root, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task CaptureStreamAsync(Stream stream, CapturedTextBuffer output, CancellationToken cancellationToken)
    {
        if (stream == null || output == null)
            return;

        byte[] buffer = new byte[StreamReadBufferBytes];
        Decoder decoder = Encoding.Default.GetDecoder();
        char[] chars = new char[Encoding.Default.GetMaxCharCount(buffer.Length)];

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

    private static string QuoteWindowsArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
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
                       "\n...[terminal output truncated]...\n" +
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

public sealed class TerminalCommandRequest
{
    public string Command { get; set; } = "";
    public string Shell { get; set; } = "powershell";
    public string WorkingDirectory { get; set; } = "";
    public string Summary { get; set; } = "";
    public int TimeoutMs { get; set; }
    public bool AllowExecutionPolicyBypass { get; set; }
    public string[] AllowedWorkingDirectories { get; set; } = Array.Empty<string>();
}

public sealed class TerminalCommandResult
{
    public string Command { get; set; } = "";
    public string Shell { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int ExitCode { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int DurationMs { get; set; }
    public bool TimedOut { get; set; }
    public bool Canceled { get; set; }
}
