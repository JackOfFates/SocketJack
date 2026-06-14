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
        _promptPipelineReady = true;
        _promptPipelineStatus = "ready";
        _promptPipelineDetail = "Runner is configured; the DirectML process starts per prompt.";
        _promptPipelineReadyAtUtc = DateTimeOffset.UtcNow;
        _promptPipelineWarmupSeconds = 0;
        return Task.CompletedTask;
    }

    public async Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (_promptPipelineReady)
            return;

        using var operation = _lifetime.Enter();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_promptPipelineReady)
                return;

            ThrowIfNotLoaded();
            _promptPipelineReady = true;
            _promptPipelineStatus = "ready";
            _promptPipelineDetail = "Runner is configured; the DirectML process starts per prompt.";
            _promptPipelineReadyAtUtc = DateTimeOffset.UtcNow;
            _promptPipelineWarmupSeconds = 0;
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        (string text, string finishReason, int rawCompletionTokens, bool stoppedInsideHiddenReasoning, bool hiddenReasoningOnly) = await InvokeRunnerAsync(request, stream: false, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        int completionTokens = Math.Max(rawCompletionTokens, LlmInferenceMetrics.EstimateTokens(text));
        int promptTokens = LlmInferenceMetrics.EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content)));
        double elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001d);
        return new LlmChatResult
        {
            Model = InstanceId,
            Content = text,
            FinishReason = NormalizeRunnerFinishReason(finishReason, request.MaxTokens, completionTokens, stoppedInsideHiddenReasoning || hiddenReasoningOnly),
            Metrics = new LlmInferenceMetrics
            {
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
                TokensPerSecond = completionTokens / elapsedSeconds,
                ManagedMemoryBytes = GC.GetTotalMemory(false)
            }
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

    private async Task<(string Text, string FinishReason, int RawCompletionTokens, bool StoppedInsideHiddenReasoning, bool HiddenReasoningOnly)> InvokeRunnerAsync(LlmChatRequest request, bool stream, CancellationToken cancellationToken)
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
            parallel_tensor = LoadConfig.ParallelTensor,
            tensor_parallel_size = LoadConfig.TensorParallelSize,
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

    private static (string Text, string FinishReason, int RawCompletionTokens, bool StoppedInsideHiddenReasoning, bool HiddenReasoningOnly) ParseRunnerOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return ("", "", 0, false, false);

        var builder = new StringBuilder();
        string finishReason = "";
        foreach (string line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryParseRunnerOutputLine(line, out string text, out _, out string lineFinishReason))
            {
                builder.Append(text);
                if (!string.IsNullOrWhiteSpace(lineFinishReason))
                    finishReason = lineFinishReason;
            }
        }

        string rawText = LlmRuntimeRepetitionGuard.TrimRepeatingTail(builder.ToString()).TrimEnd();
        string visibleText = StripHiddenReasoningTags(rawText, out bool stoppedInsideHiddenReasoning, out bool suppressedReasoning).TrimEnd();
        int rawCompletionTokens = LlmInferenceMetrics.EstimateTokens(rawText);
        bool hiddenReasoningOnly = rawCompletionTokens > 0 && suppressedReasoning && string.IsNullOrWhiteSpace(visibleText);
        if (string.IsNullOrWhiteSpace(visibleText))
            visibleText = "";
        return (visibleText, finishReason, rawCompletionTokens, stoppedInsideHiddenReasoning, hiddenReasoningOnly);
    }

    internal static async IAsyncEnumerable<LlmChatToken> ReadRunnerTokenStreamAsync(TextReader reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var reasoningFilter = new HiddenReasoningTagFilter();
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line == null)
                yield break;

            if (!TryParseRunnerOutputLine(line, out string text, out bool done, out string finishReason))
                continue;

            if (done)
                yield break;

            string visibleText = string.IsNullOrEmpty(text) ? "" : reasoningFilter.Accept(text);
            if (!string.IsNullOrEmpty(visibleText) || !string.IsNullOrWhiteSpace(finishReason))
                yield return new LlmChatToken(visibleText, finishReason);
        }
    }

    internal static bool TryParseRunnerOutputLine(string line, out string text, out bool done, out string finishReason)
    {
        text = "";
        done = false;
        finishReason = "";

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
            if (root.TryGetProperty("finish_reason", out var rootFinishReason) && rootFinishReason.ValueKind == JsonValueKind.String)
                finishReason = rootFinishReason.GetString() ?? "";

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
                    if (choice.TryGetProperty("finish_reason", out var choiceFinishReason) && choiceFinishReason.ValueKind == JsonValueKind.String)
                        finishReason = choiceFinishReason.GetString() ?? finishReason;

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

    private static string NormalizeRunnerFinishReason(string finishReason, int maxTokens, int completionTokens, bool stoppedInsideHiddenReasoning)
    {
        string normalized = string.IsNullOrWhiteSpace(finishReason) ? "stop" : finishReason;
        if (string.Equals(normalized, "stop", StringComparison.OrdinalIgnoreCase) &&
            maxTokens > 0 &&
            (completionTokens >= maxTokens || stoppedInsideHiddenReasoning))
        {
            return "length";
        }

        return normalized;
    }

    private static string StripHiddenReasoningTags(string text)
    {
        return StripHiddenReasoningTags(text, out _, out _);
    }

    private static string StripHiddenReasoningTags(string text, out bool stoppedInsideReasoning, out bool suppressedReasoning)
    {
        var filter = new HiddenReasoningTagFilter();
        string visibleText = filter.Accept(text) + filter.Complete();
        stoppedInsideReasoning = filter.StoppedInsideReasoning;
        suppressedReasoning = filter.SuppressedReasoning;
        return visibleText;
    }

    private sealed class HiddenReasoningTagFilter
    {
        private static readonly string[] OpenTags = ["<think>", "<thinking>", "<thought>", "<analysis>"];
        private static readonly string[] CloseTags = ["</think>", "</thinking>", "</thought>", "</analysis>"];
        private const int MaxTagLength = 12;
        private readonly StringBuilder _buffer = new();
        private bool _insideReasoning;

        public bool StoppedInsideReasoning { get; private set; }
        public bool SuppressedReasoning { get; private set; }

        public string Accept(string text)
        {
            if (!string.IsNullOrEmpty(text))
                _buffer.Append(text);

            var visible = new StringBuilder();
            while (_buffer.Length > 0)
            {
                if (_insideReasoning)
                {
                    int closeIndex = FindEarliestTag(_buffer, CloseTags, out int closeLength);
                    if (closeIndex < 0)
                    {
                        TrimBufferForPotentialTag();
                        break;
                    }

                    _buffer.Remove(0, closeIndex + closeLength);
                    _insideReasoning = false;
                    continue;
                }

                int openIndex = FindEarliestTag(_buffer, OpenTags, out int openLength);
                if (openIndex < 0)
                {
                    int flushLength = GetSafeFlushLength();
                    if (flushLength <= 0)
                        break;

                    visible.Append(_buffer.ToString(0, flushLength));
                    _buffer.Remove(0, flushLength);
                    break;
                }

                if (openIndex > 0)
                    visible.Append(_buffer.ToString(0, openIndex));

                _buffer.Remove(0, openIndex + openLength);
                SuppressedReasoning = true;
                _insideReasoning = true;
            }

            return visible.ToString();
        }

        public string Complete()
        {
            if (_insideReasoning)
            {
                StoppedInsideReasoning = true;
                _buffer.Clear();
                return "";
            }

            string remainder = _buffer.ToString();
            _buffer.Clear();
            return remainder;
        }

        private void TrimBufferForPotentialTag()
        {
            if (_buffer.Length <= MaxTagLength)
                return;

            _buffer.Remove(0, _buffer.Length - MaxTagLength);
        }

        private int GetSafeFlushLength()
        {
            int lastOpenBracket = _buffer.ToString().LastIndexOf('<');
            if (lastOpenBracket < 0)
                return _buffer.Length;

            return lastOpenBracket;
        }

        private static int FindEarliestTag(StringBuilder builder, string[] tags, out int tagLength)
        {
            string text = builder.ToString();
            int bestIndex = -1;
            tagLength = 0;
            foreach (string tag in tags)
            {
                int index = text.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (bestIndex < 0 || index < bestIndex))
                {
                    bestIndex = index;
                    tagLength = tag.Length;
                }
            }

            return bestIndex;
        }
    }

    private void ThrowIfNotLoaded()
    {
        _lifetime.ThrowIfDisposed();
        if (!_loaded)
            throw new InvalidOperationException("The DirectML GGUF model has not been loaded yet.");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

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
