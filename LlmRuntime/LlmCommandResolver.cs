using System.Runtime.InteropServices;

namespace LlmRuntime;

internal static class LlmCommandResolver
{
    public static bool Exists(string command) => !string.IsNullOrWhiteSpace(Resolve(command));

    public static string Resolve(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return "";

        if (Path.IsPathRooted(command) && File.Exists(command))
            return command;

        foreach (string candidate in EnumerateCandidates(command))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "";
    }

    private static IEnumerable<string> EnumerateCandidates(string command)
    {
        foreach (string path in EnumeratePathCandidates(command))
            yield return path;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            if (command.Equals("gh", StringComparison.OrdinalIgnoreCase) || command.Equals("gh.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(programFiles, "GitHub CLI", "gh.exe");
                yield return Path.Combine(localAppData, "Programs", "GitHub CLI", "gh.exe");
            }

            if (command.Equals("git", StringComparison.OrdinalIgnoreCase) || command.Equals("git.exe", StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.Combine(programFiles, "Git", "cmd", "git.exe");
                yield return Path.Combine(programFiles, "Git", "bin", "git.exe");
                yield return Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe");
            }
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates(string command)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        bool hasExtension = !string.IsNullOrWhiteSpace(Path.GetExtension(command));
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (hasExtension)
            {
                yield return Path.Combine(dir, command);
                continue;
            }

            foreach (string extension in extensions)
                yield return Path.Combine(dir, command + extension);
        }
    }
}
