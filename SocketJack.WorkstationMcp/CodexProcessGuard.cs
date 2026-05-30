using Microsoft.AspNetCore.Builder;
using System.Diagnostics;

namespace SocketJack.WorkstationMcp;

internal static class CodexProcessGuard
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public static bool IsCodexExeRunning()
    {
        try
        {
            foreach (Process process in Process.GetProcessesByName("codex"))
            {
                using (process)
                {
                    if (IsCodexExecutable(process))
                        return true;
                }
            }

            foreach (Process process in Process.GetProcessesByName("Codex"))
            {
                using (process)
                {
                    if (IsCodexExecutable(process))
                        return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public static async Task StopWhenCodexExeExitsAsync(WebApplication app)
    {
        try
        {
            while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, app.Lifetime.ApplicationStopping);
                if (!IsCodexExeRunning())
                {
                    Console.Error.WriteLine("codex.exe is no longer running; stopping SocketJack.WorkstationMcp listener.");
                    await app.StopAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsCodexExecutable(Process process)
    {
        try
        {
            string? fileName = null;
            try
            {
                fileName = Path.GetFileName(process.MainModule?.FileName);
            }
            catch
            {
                fileName = null;
            }

            if (fileName is not null && fileName.Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
                return true;

            return process.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase) ||
                   process.ProcessName.Equals("Codex", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
