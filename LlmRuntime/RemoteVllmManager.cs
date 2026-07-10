using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmRuntime;

public enum VllmAccelerationMode
{
    Auto,
    DSpark,
    Standard
}

public sealed class RemoteVllmProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Remote vLLM";
    public string SshHost { get; set; } = "";
    public string PythonExecutable { get; set; } = "/opt/vllm/venv/bin/python";
    public string Model { get; set; } = "";
    public string ModelCachePath { get; set; } = "";
    public int ApiPort { get; set; } = 8000;
    public string OpenAiBaseUrl { get; set; } = "";
    public int TensorParallelSize { get; set; } = 1;
    public uint ContextLength { get; set; }
    public int MaxVramUsagePercent { get; set; } = 90;
    public string ExtraArguments { get; set; } = "--dtype auto";
    public VllmAccelerationMode Acceleration { get; set; } = VllmAccelerationMode.Auto;
    public int StartupTimeoutSeconds { get; set; } = 600;
}

public sealed class RemoteVllmStatus
{
    public string RemoteProfileId { get; init; } = "";
    public string Model { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string State { get; init; } = "stopped";
    public string Acceleration { get; init; } = "standard";
    public string AccelerationStatus { get; init; } = "unverified";
    public string FallbackReason { get; init; } = "";
    public string LatestError { get; init; } = "";
    public int? ProcessId { get; init; }
    public double? TokensPerSecond { get; init; }
}

public static class DSparkModelDetector
{
    private static readonly Regex OfficialName = new(
        @"(?:^|[\\/])(?:deepseek-ai[\\/])?DeepSeek-V4-(?:Pro|Flash)-DSpark(?:$|[\\/])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool IsOfficialDSparkModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && OfficialName.IsMatch(model.Trim());

    public static VllmAccelerationMode Resolve(string? model, VllmAccelerationMode configured) =>
        configured == VllmAccelerationMode.Auto
            ? (IsOfficialDSparkModel(model) ? VllmAccelerationMode.DSpark : VllmAccelerationMode.Standard)
            : configured;
}

internal interface IRemoteVllmCommandRunner
{
    Task<RemoteCommandResult> RunAsync(string host, string command, CancellationToken cancellationToken);
}

internal readonly record struct RemoteCommandResult(int ExitCode, string Output, string Error);

internal sealed class OpenSshRemoteVllmCommandRunner : IRemoteVllmCommandRunner
{
    public async Task<RemoteCommandResult> RunAsync(string host, string command, CancellationToken cancellationToken)
    {
        ValidateHost(host);
        var startInfo = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add(command);

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the system OpenSSH client.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new RemoteCommandResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    private static void ValidateHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || !Regex.IsMatch(host, @"^[A-Za-z0-9_.@:-]+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("SSH host must be a host name, alias, user@host, IPv4 address, or IPv6 address.", nameof(host));
    }
}

public sealed class RemoteVllmManager
{
    private const string DSparkConfig = "{\"method\":\"dspark\",\"num_speculative_tokens\":7,\"draft_sample_method\":\"greedy\"}";
    private readonly IRemoteVllmCommandRunner _runner;
    private readonly Dictionary<string, RemoteVllmStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);

    public RemoteVllmManager() : this(new OpenSshRemoteVllmCommandRunner()) { }

    internal RemoteVllmManager(IRemoteVllmCommandRunner runner) => _runner = runner;

    public IReadOnlyList<RemoteVllmStatus> ListStatuses() => _statuses.Values.ToArray();

    public RemoteVllmStatus GetStatus(RemoteVllmProfile profile) =>
        _statuses.TryGetValue(profile.Id, out RemoteVllmStatus? status) ? status : BuildStatus(profile, "stopped", "unverified");

    public void RecordThroughput(RemoteVllmProfile profile, double tokensPerSecond)
    {
        RemoteVllmStatus current = GetStatus(profile);
        _statuses[profile.Id] = new RemoteVllmStatus
        {
            RemoteProfileId = current.RemoteProfileId,
            Model = current.Model,
            Endpoint = current.Endpoint,
            State = current.State,
            Acceleration = current.Acceleration,
            AccelerationStatus = current.AccelerationStatus,
            FallbackReason = current.FallbackReason,
            LatestError = current.LatestError,
            ProcessId = current.ProcessId,
            TokensPerSecond = Math.Max(0, tokensPerSecond)
        };
    }

    public async Task<RemoteVllmStatus> StartAsync(RemoteVllmProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        VllmAccelerationMode requested = DSparkModelDetector.Resolve(profile.Model, profile.Acceleration);
        _statuses[profile.Id] = BuildStatus(profile, "starting", requested == VllmAccelerationMode.DSpark ? "starting" : "inactive", requested);

        RemoteCommandResult start = await _runner.RunAsync(profile.SshHost, BuildStartCommand(profile, requested), cancellationToken).ConfigureAwait(false);
        if (start.ExitCode == 0 && await WaitUntilReadyAsync(profile, cancellationToken).ConfigureAwait(false))
            return _statuses[profile.Id] = BuildStatus(profile, "running", requested == VllmAccelerationMode.DSpark ? "active" : "inactive", requested, processId: ParsePid(start.Output));

        string firstError = Redact(FirstNonEmpty(start.Error, start.Output, "Remote vLLM did not become ready."));
        if (requested != VllmAccelerationMode.DSpark)
            return _statuses[profile.Id] = BuildStatus(profile, "failed", "inactive", requested, latestError: firstError);

        await StopCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        RemoteCommandResult fallback = await _runner.RunAsync(profile.SshHost, BuildStartCommand(profile, VllmAccelerationMode.Standard), cancellationToken).ConfigureAwait(false);
        if (fallback.ExitCode == 0 && await WaitUntilReadyAsync(profile, cancellationToken).ConfigureAwait(false))
            return _statuses[profile.Id] = BuildStatus(profile, "running", "fallback", VllmAccelerationMode.Standard, firstError, processId: ParsePid(fallback.Output));

        string fallbackError = Redact(FirstNonEmpty(fallback.Error, fallback.Output, "Standard vLLM fallback did not become ready."));
        return _statuses[profile.Id] = BuildStatus(profile, "failed", "fallback_failed", VllmAccelerationMode.Standard, firstError, fallbackError);
    }

    public async Task<RemoteVllmStatus> StopAsync(RemoteVllmProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        RemoteCommandResult result = await StopCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        return _statuses[profile.Id] = BuildStatus(profile, result.ExitCode == 0 ? "stopped" : "failed", "inactive", latestError: Redact(result.Error));
    }

    public async Task<RemoteVllmStatus> RestartAsync(RemoteVllmProfile profile, CancellationToken cancellationToken = default)
    {
        await StopCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        return await StartAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RemoteVllmStatus> ProbeAsync(RemoteVllmProfile profile, CancellationToken cancellationToken = default)
    {
        Validate(profile);
        RemoteCommandResult result = await _runner.RunAsync(profile.SshHost, BuildProbeCommand(profile), cancellationToken).ConfigureAwait(false);
        RemoteVllmStatus previous = GetStatus(profile);
        string state = result.ExitCode == 0 && result.Output.Contains("ready", StringComparison.OrdinalIgnoreCase) ? "running" : "stopped";
        return _statuses[profile.Id] = BuildStatus(profile, state, state == "running" ? previous.AccelerationStatus : "unverified",
            previous.Acceleration.Equals("dspark", StringComparison.OrdinalIgnoreCase) ? VllmAccelerationMode.DSpark : VllmAccelerationMode.Standard,
            previous.FallbackReason, Redact(result.Error), ParsePid(result.Output), previous.TokensPerSecond);
    }

    internal static string BuildStartCommand(RemoteVllmProfile profile, VllmAccelerationMode acceleration)
    {
        string runtime = RuntimeDirectory(profile);
        var args = new List<string>
        {
            ShellQuote(profile.PythonExecutable), "-m", "vllm.entrypoints.openai.api_server",
            "--model", ShellQuote(profile.Model), "--port", profile.ApiPort.ToString(CultureInfo.InvariantCulture),
            "--tensor-parallel-size", Math.Max(1, profile.TensorParallelSize).ToString(CultureInfo.InvariantCulture),
            "--gpu-memory-utilization", (Math.Clamp(profile.MaxVramUsagePercent, 1, 100) / 100d).ToString("0.##", CultureInfo.InvariantCulture)
        };
        if (profile.ContextLength > 0)
        {
            args.Add("--max-model-len");
            args.Add(profile.ContextLength.ToString(CultureInfo.InvariantCulture));
        }
        if (acceleration == VllmAccelerationMode.DSpark)
        {
            args.Add("--speculative-config");
            args.Add(ShellQuote(DSparkConfig));
        }
        args.AddRange(SplitArguments(profile.ExtraArguments).Select(ShellQuote));

        string cache = string.IsNullOrWhiteSpace(profile.ModelCachePath) ? "" : "export HF_HOME=" + ShellQuote(profile.ModelCachePath) + "; ";
        string command = string.Join(" ", args);
        return "set -eu; mkdir -p " + ShellRuntimePath(runtime) + "; " + cache +
               "if [ -s " + ShellRuntimePath(runtime + "/vllm.pid") + "] && kill -0 $(cat " + ShellRuntimePath(runtime + "/vllm.pid") + ") 2>/dev/null; then echo already-running:$(cat " + ShellRuntimePath(runtime + "/vllm.pid") + "); exit 0; fi; " +
               "nohup " + command + " >" + ShellRuntimePath(runtime + "/vllm.log") + " 2>&1 </dev/null & pid=$!; echo $pid >" + ShellRuntimePath(runtime + "/vllm.pid") + "; echo started:$pid";
    }

    internal static string BuildStopCommand(RemoteVllmProfile profile)
    {
        string pid = RuntimeDirectory(profile) + "/vllm.pid";
        return "set -eu; if [ -s " + ShellRuntimePath(pid) + "]; then p=$(cat " + ShellRuntimePath(pid) + "); " +
               "if kill -0 $p 2>/dev/null; then kill $p; i=0; while kill -0 $p 2>/dev/null && [ $i -lt 20 ]; do sleep 0.25; i=$((i+1)); done; kill -0 $p 2>/dev/null && kill -9 $p || true; fi; rm -f " + ShellRuntimePath(pid) + "; fi; echo stopped";
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string value = Regex.Replace(text, @"(?i)(authorization|api[_-]?key|token|password|secret)\s*[:=]\s*[^\s;]+", "$1=[redacted]");
        value = Regex.Replace(value, """(?i)(--(?:api-key|token|password)\s+)(?:'[^']*'|"[^"]*"|\S+)""", "$1[redacted]");
        return value.Trim();
    }

    private async Task<bool> WaitUntilReadyAsync(RemoteVllmProfile profile, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(profile.StartupTimeoutSeconds, 1, 3600));
        do
        {
            RemoteCommandResult probe = await _runner.RunAsync(profile.SshHost, BuildProbeCommand(profile), cancellationToken).ConfigureAwait(false);
            if (probe.ExitCode == 0 && probe.Output.Contains("ready", StringComparison.OrdinalIgnoreCase)) return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        } while (true);
    }

    private Task<RemoteCommandResult> StopCoreAsync(RemoteVllmProfile profile, CancellationToken cancellationToken) =>
        _runner.RunAsync(profile.SshHost, BuildStopCommand(profile), cancellationToken);

    private static string BuildProbeCommand(RemoteVllmProfile profile)
    {
        string pid = RuntimeDirectory(profile) + "/vllm.pid";
        return "set -eu; test -s " + ShellRuntimePath(pid) + "; p=$(cat " + ShellRuntimePath(pid) + "); kill -0 $p; " +
               "curl -fsS --max-time 3 http://127.0.0.1:" + profile.ApiPort.ToString(CultureInfo.InvariantCulture) + "/v1/models >/dev/null; echo ready:$p";
    }

    private static RemoteVllmStatus BuildStatus(RemoteVllmProfile profile, string state, string accelerationStatus,
        VllmAccelerationMode? acceleration = null, string fallbackReason = "", string latestError = "", int? processId = null, double? tokensPerSecond = null) => new()
    {
        RemoteProfileId = profile.Id,
        Model = profile.Model,
        Endpoint = ResolveEndpoint(profile),
        State = state,
        Acceleration = (acceleration ?? DSparkModelDetector.Resolve(profile.Model, profile.Acceleration)) == VllmAccelerationMode.DSpark ? "dspark" : "standard",
        AccelerationStatus = accelerationStatus,
        FallbackReason = fallbackReason,
        LatestError = latestError,
        ProcessId = processId,
        TokensPerSecond = tokensPerSecond
    };

    private static string RuntimeDirectory(RemoteVllmProfile profile) => "$HOME/.local/state/jackllm/vllm-" + SafeId(profile.Id);
    internal static string ResolveEndpoint(RemoteVllmProfile profile) => !string.IsNullOrWhiteSpace(profile.OpenAiBaseUrl)
        ? profile.OpenAiBaseUrl.TrimEnd('/')
        : "http://" + profile.SshHost.Split('@').Last() + ":" + profile.ApiPort.ToString(CultureInfo.InvariantCulture);
    private static string SafeId(string value) => Regex.Replace(value ?? "", @"[^A-Za-z0-9_.-]", "-");
    private static string ShellQuote(string value) => "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
    private static string ShellRuntimePath(string value) => "\"$HOME/" + value[6..].Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static int? ParsePid(string text)
    {
        Match match = Regex.Match(text ?? "", @"(?:started|ready|already-running):(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int pid) ? pid : null;
    }
    private static IEnumerable<string> SplitArguments(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) yield break;
        foreach (Match match in Regex.Matches(value, """[^\s"']+|"([^"]*)"|'([^']*)'"""))
            yield return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Value;
    }
    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static void Validate(RemoteVllmProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Id)) throw new ArgumentException("Remote vLLM profile ID is required.");
        if (string.IsNullOrWhiteSpace(profile.SshHost)) throw new ArgumentException("Remote vLLM SSH host is required.");
        if (string.IsNullOrWhiteSpace(profile.PythonExecutable)) throw new ArgumentException("Remote vLLM Python executable is required.");
        if (string.IsNullOrWhiteSpace(profile.Model)) throw new ArgumentException("Remote vLLM model is required.");
        if (profile.ApiPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(profile.ApiPort));
    }
}
