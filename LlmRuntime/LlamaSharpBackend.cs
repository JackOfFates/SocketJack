using System.Diagnostics;
using System.Text;
using System.Runtime.CompilerServices;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;

namespace LlmRuntime;

public sealed class LlamaSharpBackendFactory : ILlmBackendFactory
{
    public ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig) =>
        new LlamaSharpBackend(instanceId, modelPath, loadConfig);
}

public sealed class LlmBackendFactory : ILlmBackendFactory
{
    private readonly LlmRuntimeOptions _options;

    public LlmBackendFactory(LlmRuntimeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig) =>
        loadConfig.Backend == LlmBackendKind.DirectML
            ? new DirectMlGgufBackend(instanceId, modelPath, loadConfig, DirectMlGgufRunnerDiscovery.ResolveRunnerPath(_options), _options.DirectMlGgufRunnerArguments)
            : new LlamaSharpBackend(instanceId, modelPath, loadConfig);
}

public sealed class LlamaSharpBackend : ILlmBackend
{
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private readonly SemaphoreSlim _inferenceLock = new(1, 1);
    private readonly LlmBackendLifetimeGate _lifetime = new(nameof(LlamaSharpBackend));
    private bool _promptPipelineReady;
    private string _promptPipelineStatus = "cold";
    private string _promptPipelineDetail = "";
    private DateTimeOffset? _promptPipelineReadyAtUtc;
    private double _promptPipelineWarmupSeconds;

    public LlamaSharpBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig)
    {
        InstanceId = instanceId;
        ModelPath = modelPath;
        LoadConfig = loadConfig;
    }

    public string InstanceId { get; }

    public string ModelPath { get; }

    public LlmLoadConfig LoadConfig { get; }

    public bool IsPromptPipelineReady => _promptPipelineReady;

    public string PromptPipelineStatus => _promptPipelineStatus;

    public string PromptPipelineDetail => _promptPipelineDetail;

    public DateTimeOffset? PromptPipelineReadyAtUtc => _promptPipelineReadyAtUtc;

    public double PromptPipelineWarmupSeconds => _promptPipelineWarmupSeconds;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Model not found: {ModelPath}", ModelPath);

        using var operation = _lifetime.Enter();
        _promptPipelineReady = false;
        _promptPipelineStatus = "loading";
        _promptPipelineDetail = "Loading model weights.";
        _promptPipelineReadyAtUtc = null;
        _promptPipelineWarmupSeconds = 0;
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            LlamaSharpBackendSelector.Configure(LoadConfig);
            var parameters = new ModelParams(ModelPath)
            {
                ContextSize = LoadConfig.ContextLength,
                BatchSize = LoadConfig.EvalBatchSize > 0 ? (uint)LoadConfig.EvalBatchSize : 512,
                UBatchSize = LoadConfig.EvalBatchSize > 0 ? (uint)LoadConfig.EvalBatchSize : 512,
                FlashAttention = LoadConfig.FlashAttention,
                NoKqvOffload = !LoadConfig.OffloadKvCacheToGpu,
                GpuLayerCount = GetEffectiveGpuLayerCount(LoadConfig)
            };

            _weights = LLamaWeights.LoadFromFile(parameters);
            _parameters = parameters;
            LlamaSharpBackendSelector.ValidateLoadedBackend(LoadConfig.Backend);
            _promptPipelineStatus = "cold";
            _promptPipelineDetail = "Model weights are loaded; prompt pipeline is not warmed yet.";
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default)
    {
        if (_promptPipelineReady)
            return;

        var stopwatch = Stopwatch.StartNew();
        using var operation = _lifetime.Enter();
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_promptPipelineReady)
                return;

            ThrowIfNotLoaded();
            _promptPipelineStatus = "warming";
            _promptPipelineDetail = "Warming prompt context and executor.";

            using var context = _weights!.CreateContext(_parameters!);
            var executor = new InteractiveExecutor(context);
            var request = CreateWarmupRequest();
            var parameters = CreateInferenceParams(request);
            string prompt = BuildPrompt(_weights!, request.Messages);

            await foreach (string text in executor.InferAsync(prompt, parameters, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(text))
                    break;
            }

            stopwatch.Stop();
            _promptPipelineReady = true;
            _promptPipelineStatus = "ready";
            _promptPipelineDetail = "Prompt pipeline warmed and ready.";
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
        finally
        {
            _inferenceLock.Release();
        }
    }

    public async Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        await foreach (var token in StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
            output.Append(token.Text);

        stopwatch.Stop();
        string text = ApplyStopSequences(output.ToString(), request.Stop);
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
        await _inferenceLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfNotLoaded();
            using var context = _weights!.CreateContext(_parameters!);
            var executor = new InteractiveExecutor(context);
            var parameters = CreateInferenceParams(request);
            string prompt = BuildPrompt(_weights!, request.Messages);
            var repetitionGuard = new LlmRuntimeRepetitionGuard();

            await foreach (string text in executor.InferAsync(prompt, parameters, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(text))
                    continue;

                LlmRuntimeRepetitionGuardDecision decision = repetitionGuard.Accept(text);
                if (!string.IsNullOrEmpty(decision.Text))
                    yield return new LlmChatToken(decision.Text);

                if (decision.ShouldStop)
                    yield break;
            }
        }
        finally
        {
            _inferenceLock.Release();
        }
    }

    private void ThrowIfNotLoaded()
    {
        _lifetime.ThrowIfDisposed();
        if (_weights == null || _parameters == null)
            throw new InvalidOperationException("The model has not been loaded yet.");
    }

    private static InferenceParams CreateInferenceParams(LlmChatRequest request)
    {
        return new InferenceParams
        {
            MaxTokens = request.MaxTokens,
            AntiPrompts = request.Stop.ToList(),
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = request.Temperature,
                TopP = request.TopP,
                RepeatPenalty = 1.12f,
                FrequencyPenalty = 0.08f,
                PresencePenalty = 0.02f,
                PenaltyCount = 256
            }
        };
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

    private static string BuildPrompt(LLamaWeights weights, IReadOnlyList<LlmChatMessage> messages)
    {
        try
        {
            var template = new LLamaTemplate(weights, strict: false)
            {
                AddAssistant = true
            };

            foreach (var message in messages)
            {
                template.Add(ToTemplateRole(message.Role), message.Content.Trim());
            }

            return PromptTemplateTransformer.ToModelPrompt(template);
        }
        catch
        {
            return BuildPlainPrompt(messages);
        }
    }

    private static int GetEffectiveGpuLayerCount(LlmLoadConfig loadConfig) =>
        LlmBackendAutoSelector.Resolve(loadConfig.Backend) == LlmBackendKind.Cpu ? 0 : loadConfig.GpuLayerCount;

    private static string BuildPlainPrompt(IReadOnlyList<LlmChatMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (var message in messages)
        {
            string role = NormalizeRole(message.Role);
            builder.Append(role).Append(": ").AppendLine(message.Content.Trim());
        }

        builder.Append("Assistant: ");
        return builder.ToString();
    }

    private static string ToTemplateRole(string role)
    {
        if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            return "system";
        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return "assistant";
        if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            return "tool";
        return "user";
    }

    private static string NormalizeRole(string role)
    {
        if (role.Equals("system", StringComparison.OrdinalIgnoreCase))
            return "System";
        if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
            return "Assistant";
        if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
            return "Tool";
        return "User";
    }

    private static string ApplyStopSequences(string text, IReadOnlyList<string> stopSequences)
    {
        foreach (string stop in stopSequences.Where(stop => !string.IsNullOrEmpty(stop)))
        {
            int index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0)
                text = text[..index];
        }

        return text;
    }

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
            _weights?.Dispose();
            _weights = null;
            _parameters = null;
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
