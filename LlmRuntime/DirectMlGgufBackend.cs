using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace LlmRuntime;

public sealed class DirectMlGgufBackend : ILlmBackend
{
    private readonly string _runnerPath;
    private readonly string _runnerArguments;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly LlmBackendLifetimeGate _lifetime = new(nameof(DirectMlGgufBackend));
    private bool _loaded;
    private bool _promptPipelineReady;
    private string _promptPipelineStatus = "cold";
    private string _promptPipelineDetail = "";
    private DateTimeOffset? _promptPipelineReadyAtUtc;
    private double _promptPipelineWarmupSeconds;

    public DirectMlGgufBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig, string runnerPath, string runnerArguments)
    {
        InstanceId = instanceId;
        ModelPath = modelPath;
        LoadConfig = loadConfig;
        _runnerPath = runnerPath ?? "";
        _runnerArguments = runnerArguments ?? "";
    }

    public string InstanceId { get; }

    public string ModelPath { get; }

    public LlmLoadConfig LoadConfig { get; }

    public bool IsPromptPipelineReady => _promptPipelineReady;

    public string PromptPipelineStatus => _promptPipelineStatus;

    public string PromptPipelineDetail => _promptPipelineDetail;

    public DateTimeOffset? PromptPipelineReadyAtUtc => _promptPipelineReadyAtUtc;

    public double PromptPipelineWarmupSeconds => _promptPipelineWarmupSeconds;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter();

        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Model not found: {ModelPath}", ModelPath);

        if (string.IsNullOrWhiteSpace(_runnerPath))
        {
            throw new LlmRuntimeException(
                "DirectML GGUF backend is enabled, but no runner is configured. Run tools\\Install-DirectMlGgufRunner.ps1 with a trusted runner path or download URL, set LlmRuntimeOptions.DirectMlGgufRunnerPath, or set " + DirectMlGgufRunnerDiscovery.EnvironmentVariable + ".",
                "unsupported_backend",
                "directml_runner_not_configured");
        }

        if (!File.Exists(_runnerPath))
        {
            throw new LlmRuntimeException(
                "DirectML GGUF runner was not found: " + _runnerPath,
                "unsupported_backend",
                "directml_runner_not_found");
        }

        _loaded = true;
        _promptPipelineReady = false;
        _promptPipelineStatus = "cold";
        _promptPipelineDetail = "Runner is configured; prompt pipeline is not warmed yet.";
        _promptPipelineReadyAtUtc = null;
        _promptPipelineWarmupSeconds = 0;
        return Task.CompletedTask;
    }

    public async Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (_promptPipelineReady)
            return;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            _promptPipelineStatus = "warming";
            _promptPipelineDetail = "Warming DirectML runner prompt path.";
            await InvokeRunnerAsync(CreateWarmupRequest(), stream: false, cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            _promptPipelineReady = true;
            _promptPipelineStatus = "ready";
            _promptPipelineDetail = "DirectML runner prompt path warmed; runner process starts per prompt.";
            _promptPipelineReadyAtUtc = DateTimeOffset.UtcNow;
            _promptPipelineWarmupSeconds = stopwatch.Elapsed.TotalSeconds;
        }
        catch (OperationCanceledException)
        {
            _promptPipelineReady = false;
            _promptPipelineStatus = "cold";
            _promptPipelineDetail = "Prompt pipeline warmup was canceled.";
            _promptPipelineWarmupSeconds = stopwatch.Elapsed.TotalSeconds;
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _promptPipelineReady = false;
            _promptPipelineStatus = "failed";
            _promptPipelineDetail = TrimPromptPipelineDetail(ex.Message);
            _promptPipelineWarmupSeconds = stopwatch.Elapsed.TotalSeconds;
            throw;
        }
    }

    public async Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        string text = await InvokeRunnerAsync(request, stream: false, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return new LlmChatResult
        {
            Model = InstanceId,
            Content = text,
            FinishReason = "stop",
            Metrics = LlmInferenceMetrics.FromText(request, text, stopwatch.Elapsed)
        };
    }

    public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var operation = _lifetime.Enter();
        ThrowIfNotLoaded();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        Process? process = null;
        Task<string>? errorTask = null;
        try
        {
            process = StartRunnerProcess(stream: true);
            errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await WriteRunnerRequestAsync(process, request, stream: true, cancellationToken).ConfigureAwait(false);

            await foreach (LlmChatToken token in ReadRunnerTokenStreamAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false))
                yield return token;

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string error = await ReadRunnerErrorAsync(errorTask).ConfigureAwait(false);
            ThrowIfRunnerFailed(process, error);
        }
        finally
        {
            StopRunnerProcess(process);
            if (errorTask != null)
                _ = ObserveRunnerErrorTaskAsync(errorTask);
            process?.Dispose();
            _inferenceLock.Release();
        }
    }

    private async Task<string> InvokeRunnerAsync(LlmChatRequest request, bool stream, CancellationToken cancellationToken)
    {
        using var operation = _lifetime.Enter();
        ThrowIfNotLoaded();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        Process? process = null;
        Task<string>? errorTask = null;
        try
        {
            process = StartRunnerProcess(stream);
            errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await WriteRunnerRequestAsync(process, request, stream, cancellationToken).ConfigureAwait(false);

            string output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string error = await ReadRunnerErrorAsync(errorTask).ConfigureAwait(false);
            ThrowIfRunnerFailed(process, error);

            return ParseRunnerOutput(output);
        }
        finally
        {
            StopRunnerProcess(process);
            if (errorTask != null)
                _ = ObserveRunnerErrorTaskAsync(errorTask);
            process?.Dispose();
            _inferenceLock.Release();
        }
    }

    private Process StartRunnerProcess(bool stream)
    {
        string arguments = BuildArguments(stream);
        var startInfo = new ProcessStartInfo(_runnerPath, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        Process? process = Process.Start(startInfo);
        if (process == null)
            throw new LlmRuntimeException("DirectML GGUF runner could not be started.", "backend_error", "directml_runner_start_failed");

        return process;
    }

    private async Task WriteRunnerRequestAsync(Process process, LlmChatRequest request, bool stream, CancellationToken cancellationToken)
    {
        string payload = BuildRunnerPayload(request, stream);
        await process.StandardInput.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        process.StandardInput.Close();
    }

    private string BuildRunnerPayload(LlmChatRequest request, bool stream)
    {
        return JsonSerializer.Serialize(new
        {
            model_path = ModelPath,
            instance_id = InstanceId,
            backend = "directml",
            context_length = LoadConfig.ContextLength,
            gpu_layer_count = LoadConfig.GpuLayerCount,
            eval_batch_size = LoadConfig.EvalBatchSize,
            flash_attention = LoadConfig.FlashAttention,
            offload_kv_cache_to_gpu = LoadConfig.OffloadKvCacheToGpu,
            parallelism_mode = LoadConfig.ParallelismMode.ToString(),
            parallelism_placement = LoadConfig.ParallelismPlacement.ToString(),
            target_device_ids = LoadConfig.TargetDeviceIds,
            network_node_ids = LoadConfig.NetworkNodeIds,
            gpu_load_threshold_percent = LoadConfig.MaxGpuLoadPercent,
            max_vram_usage_percent = LoadConfig.MaxVramUsagePercent,
            pipeline_stage_count = LoadConfig.PipelineStageCount,
            data_parallel_replicas = LoadConfig.DataParallelReplicaCount,
            request = new
            {
                model = request.Model,
                messages = request.Messages.Select(message => new { role = message.Role, content = message.Content }).ToArray(),
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                top_p = request.TopP,
                stop = request.Stop,
                stream
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static async Task<string> ReadRunnerErrorAsync(Task<string> errorTask)
    {
        try
        {
            return await errorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return "";
        }
    }

    private static async Task ObserveRunnerErrorTaskAsync(Task<string> errorTask)
    {
        try
        {
            await errorTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static void ThrowIfRunnerFailed(Process process, string error)
    {
        if (process.ExitCode == 0)
            return;

        throw new LlmRuntimeException(
            string.IsNullOrWhiteSpace(error) ? "DirectML GGUF runner exited with code " + process.ExitCode + "." : error.Trim(),
            "backend_error",
            "directml_runner_failed");
    }

    private static void StopRunnerProcess(Process? process)
    {
        if (process == null)
            return;

        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private string BuildArguments(bool stream)
    {
        var arguments = new List<string>();
        if (!string.IsNullOrWhiteSpace(_runnerArguments))
            arguments.Add(_runnerArguments);
        arguments.Add("--backend");
        arguments.Add("directml");
        arguments.Add("--allow-fallback");
        arguments.Add(LoadConfig.AllowBackendFallback ? "true" : "false");
        arguments.Add("--model");
        arguments.Add(Quote(ModelPath));
        arguments.Add("--protocol");
        arguments.Add("socketjack-jsonl");
        if (stream)
            arguments.Add("--stream");
        return string.Join(" ", arguments);
    }

    private static string ParseRunnerOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return "";

        var builder = new StringBuilder();
        foreach (string line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseRunnerOutputLine(line, out string text, out _))
                builder.Append(text);
        }

        return LlmRuntimeRepetitionGuard.TrimRepeatingTail(builder.ToString()).TrimEnd();
    }

    internal static async IAsyncEnumerable<LlmChatToken> ReadRunnerTokenStreamAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                yield break;

            if (!TryParseRunnerOutputLine(line, out string text, out bool done))
                continue;

            if (done)
                yield break;

            if (!string.IsNullOrEmpty(text))
                yield return new LlmChatToken(text);
        }
    }

    internal static bool TryParseRunnerOutputLine(string line, out string text, out bool done)
    {
        text = "";
        done = false;

        string trimmed = (line ?? "").Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[5..].Trim();

        if (trimmed == "[DONE]")
        {
            done = true;
            return true;
        }

        if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.StartsWith("{", StringComparison.Ordinal))
            return false;

        try
        {
            string jsonCandidate = trimmed;
            if (trimmed.Length > 2 && !trimmed.EndsWith("}", StringComparison.Ordinal))
            {
                int firstBrace = trimmed.IndexOf('{');
                int lastBrace = trimmed.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                    jsonCandidate = trimmed[firstBrace..(lastBrace + 1)];
            }

            using var document = JsonDocument.Parse(jsonCandidate);
            var root = document.RootElement;
            var builder = new StringBuilder();

            if (root.TryGetProperty("text", out var rootText) && rootText.ValueKind == JsonValueKind.String)
                builder.Append(rootText.GetString());
            else if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                builder.Append(content.GetString());
            else if (root.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String)
                builder.Append(token.GetString());
            else if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("text", out var nestedText) && nestedText.ValueKind == JsonValueKind.String)
                        builder.Append(nestedText.GetString());
                    else if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object
                             && delta.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                        builder.Append(deltaContent.GetString());
                }
            }

            text = builder.ToString();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private LlmChatRequest CreateWarmupRequest() => new()
    {
        Model = InstanceId,
        Messages =
        [
            new LlmChatMessage("system", "Warm the prompt pipeline. Reply with one token.")
        ],
        MaxTokens = 1,
        MaxTokensSpecified = true,
        Temperature = 0,
        TopP = 1,
        Stop = []
    };

    private void ThrowIfNotLoaded()
    {
        _lifetime.ThrowIfDisposed();
        if (!_loaded)
            throw new InvalidOperationException("The DirectML GGUF model has not been loaded yet.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string TrimPromptPipelineDetail(string detail)
    {
        detail = (detail ?? "").Trim();
        if (detail.Length <= 240)
            return detail;
        return detail[..240] + "...";
    }

    public void Dispose()
    {
        if (!_lifetime.BeginDisposeAndWait())
            return;

        try
        {
            _loaded = false;
            _inferenceLock.Dispose();
        }
        finally
        {
            _lifetime.CompleteDispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
