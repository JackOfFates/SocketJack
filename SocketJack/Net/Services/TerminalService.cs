using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EasyYoloOcr.Example.Wpf.Services;

/// <summary>
/// Executes local terminal commands for LmVsProxy after the caller has passed
/// the GUI-owned permission gate.
/// </summary>
public sealed class TerminalService
{
    private const int DefaultTimeoutMs = 120000;
    private const int MaxTimeoutMs = 600000;
    private const int MaxCapturedChars = 24000;

    public async Task<TerminalCommandResult> ExecuteAsync(TerminalCommandRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string command = (request.Command ?? "").Trim();
        if (string.IsNullOrWhiteSpace(command))
            throw new InvalidOperationException("Terminal command is required.");

        int timeoutMs = request.TimeoutMs <= 0 ? DefaultTimeoutMs : Math.Min(request.TimeoutMs, MaxTimeoutMs);
        string workingDirectory = ResolveWorkingDirectory(request.WorkingDirectory);
        ProcessStartInfo startInfo = BuildStartInfo(command, request.Shell, workingDirectory);
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

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
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

                try
                {
                    await waitTask.ConfigureAwait(false);
                }
                catch { }
            }

            string stdout = await ReadCompletedTextAsync(stdoutTask).ConfigureAwait(false);
            string stderr = await ReadCompletedTextAsync(stderrTask).ConfigureAwait(false);
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
                Output = TrimCapturedText(stdout),
                Error = TrimCapturedText(stderr),
                DurationMs = Math.Max(0, (int)Math.Round((DateTimeOffset.UtcNow - started).TotalMilliseconds)),
                TimedOut = timedOut,
                Canceled = canceled
            };
        }
    }

    private static ProcessStartInfo BuildStartInfo(string command, string shell, string workingDirectory)
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
            Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encoded,
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

    private static async Task<string> ReadCompletedTextAsync(Task<string> task)
    {
        if (task == null)
            return "";

        Task completed = await Task.WhenAny(task, Task.Delay(1000)).ConfigureAwait(false);
        if (completed != task)
            return "";

        try
        {
            return await task.ConfigureAwait(false) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string TrimCapturedText(string text)
    {
        text = text ?? "";
        if (text.Length <= MaxCapturedChars)
            return text;

        int keep = MaxCapturedChars / 2;
        return text.Substring(0, keep) +
               "\n...[terminal output truncated]...\n" +
               text.Substring(text.Length - keep);
    }

    private static string QuoteWindowsArgument(string value)
    {
        return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
    }
}

public sealed class TerminalCommandRequest
{
    public string Command { get; set; } = "";
    public string Shell { get; set; } = "powershell";
    public string WorkingDirectory { get; set; } = "";
    public string Summary { get; set; } = "";
    public int TimeoutMs { get; set; }
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
