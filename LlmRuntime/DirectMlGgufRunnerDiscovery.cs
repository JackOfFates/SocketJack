namespace LlmRuntime;

public sealed class DirectMlGgufRunnerStatus
{
    public bool Configured { get; init; }

    public string RunnerPath { get; init; } = "";

    public IReadOnlyList<string> CandidatePaths { get; init; } = [];

    public string Message { get; init; } = "";
}

public static class DirectMlGgufRunnerDiscovery
{
    public const string EnvironmentVariable = "LLMRUNTIME_DIRECTML_GGUF_RUNNER";

    public static DirectMlGgufRunnerStatus GetStatus(LlmRuntimeOptions options)
    {
        string resolved = ResolveRunnerPath(options);
        IReadOnlyList<string> candidates = GetCandidatePaths(options).ToArray();
        return new DirectMlGgufRunnerStatus
        {
            Configured = !string.IsNullOrWhiteSpace(resolved),
            RunnerPath = resolved,
            CandidatePaths = candidates,
            Message = string.IsNullOrWhiteSpace(resolved)
                ? "No DirectML GGUF runner executable was found. Run tools\\Install-DirectMlGgufRunner.ps1 with a trusted runner path or download URL, or set " + EnvironmentVariable + "."
                : "DirectML GGUF runner found: " + resolved
        };
    }

    public static string ResolveRunnerPath(LlmRuntimeOptions options)
    {
        foreach (string candidate in GetCandidatePaths(options))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return "";
    }

    public static IEnumerable<string> GetCandidatePaths(LlmRuntimeOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.DirectMlGgufRunnerPath))
            yield return Path.GetFullPath(options.DirectMlGgufRunnerPath);

        string env = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "";
        if (!string.IsNullOrWhiteSpace(env))
            yield return Path.GetFullPath(env);

        string toolRoot = string.IsNullOrWhiteSpace(options.ToolRoot)
            ? Path.Combine(Environment.CurrentDirectory, "Tools")
            : options.ToolRoot;

        yield return Path.Combine(toolRoot, "DirectML", "llm-directml-gguf-runner.exe");
        yield return Path.Combine(toolRoot, "DirectML", "LlmRuntime.DirectMlRunner.exe");
        yield return Path.Combine(toolRoot, "DirectML", "llama-directml.exe");
        yield return Path.Combine(toolRoot, "DirectML", "llama-cli.exe");
        yield return Path.Combine(Environment.CurrentDirectory, "tools", "DirectML", "llm-directml-gguf-runner.exe");
        yield return Path.Combine(Environment.CurrentDirectory, "tools", "DirectML", "LlmRuntime.DirectMlRunner.exe");
        yield return Path.Combine(Environment.CurrentDirectory, "tools", "DirectML", "llama-directml.exe");
        yield return Path.Combine(Environment.CurrentDirectory, "tools", "DirectML", "llama-cli.exe");
    }
}
