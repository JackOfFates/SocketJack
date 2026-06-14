using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using LLama;
using LLama.Abstractions;
using LLama.Common;
using LLama.Exceptions;
using LLama.Native;
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
        loadConfig.Backend switch
        {
            LlmBackendKind.DirectML => new DirectMlGgufBackend(instanceId, modelPath, loadConfig, DirectMlGgufRunnerDiscovery.ResolveRunnerPath(_options), _options.DirectMlGgufRunnerArguments),
            LlmBackendKind.Vllm => new VllmBackend(instanceId, modelPath, loadConfig, _options),
            _ => new LlamaSharpBackend(instanceId, modelPath, loadConfig)
        };
}

public sealed class LlamaSharpBackend : ILlmBackend
{
    private static readonly string[] HiddenReasoningOpenTags = ["<think>", "<thinking>", "<thought>", "<analysis>"];
    private static readonly string[] HiddenReasoningCloseTags = ["</think>", "</thinking>", "</thought>", "</analysis>"];

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
            ApplyTensorParallelSettings(parameters, LoadConfig);

            _weights = LLamaWeights.LoadFromFile(parameters);
            _parameters = parameters;
            LlamaSharpBackendSelector.ValidateLoadedBackend(LoadConfig.Backend);
            _promptPipelineReady = true;
            _promptPipelineStatus = "ready";
            _promptPipelineDetail = "Model weights are loaded; prompt contexts are created per request.";
            _promptPipelineReadyAtUtc = DateTimeOffset.UtcNow;
            _promptPipelineWarmupSeconds = 0;
        }, cancellationToken).ConfigureAwait(false);
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
            _promptPipelineDetail = "Model weights are loaded; prompt contexts are created per request.";
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
        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        string finishReason = "";
        await foreach (var token in StreamChatAsync(request, cancellationToken).ConfigureAwait(false))
        {
            output.Append(token.Text);
            if (!string.IsNullOrWhiteSpace(token.FinishReason))
                finishReason = token.FinishReason;
        }

        stopwatch.Stop();
        string text = ApplyStopSequences(output.ToString(), request.Stop);
        return new LlmChatResult
        {
            Model = InstanceId,
            Content = text,
            FinishReason = string.IsNullOrWhiteSpace(finishReason) ? "stop" : finishReason,
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
            var output = new StringBuilder();
            int generatedTokens = 0;
            bool stoppedByGuard = false;
            bool contextOverflowed = false;

            await using var enumerator = executor.InferAsync(prompt, parameters, cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (!contextOverflowed)
            {
                string text;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                        break;
                    text = enumerator.Current;
                }
                catch (ContextOverflowException)
                {
                    contextOverflowed = true;
                    break;
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(text))
                    continue;

                generatedTokens++;
                LlmRuntimeRepetitionGuardDecision decision = repetitionGuard.Accept(text);
                if (!string.IsNullOrEmpty(decision.Text))
                {
                    output.Append(decision.Text);
                    yield return new LlmChatToken(decision.Text);
                }

                if (decision.ShouldStop)
                {
                    stoppedByGuard = true;
                    break;
                }
            }

            string rawText = ApplyStopSequences(output.ToString(), request.Stop);
            int completionTokenEstimate = LlmInferenceMetrics.EstimateTokens(rawText);
            string finishReason = contextOverflowed
                ? "length"
                : DetermineFinishReason(
                stoppedByGuard,
                generatedTokens,
                request.MaxTokens,
                completionTokenEstimate,
                EndsInsideHiddenReasoning(rawText) || IsHiddenReasoningOnly(rawText));
            yield return new LlmChatToken("", finishReason);
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

    internal static InferenceParams CreateInferenceParams(LlmChatRequest request)
    {
        return new InferenceParams
        {
            MaxTokens = request.MaxTokens,
            AntiPrompts = request.Stop.ToList(),
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

    internal static string DetermineFinishReason(bool stoppedByGuard, int generatedTokens, int maxTokens, int completionTokenEstimate, bool stoppedInsideHiddenReasoning)
    {
        if (stoppedByGuard)
            return "stop";

        return generatedTokens >= maxTokens ||
               completionTokenEstimate >= maxTokens ||
               stoppedInsideHiddenReasoning
            ? "length"
            : "stop";
    }

    internal static bool EndsInsideHiddenReasoning(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        bool insideReasoning = false;
        int offset = 0;
        while (offset < text.Length)
        {
            string[] searchTags = insideReasoning ? HiddenReasoningCloseTags : HiddenReasoningOpenTags;
            int index = FindEarliestTag(text, offset, searchTags, out int tagLength);
            if (index < 0)
                return insideReasoning;

            insideReasoning = !insideReasoning;
            offset = index + tagLength;
        }

        return insideReasoning;
    }

    internal static bool IsHiddenReasoningOnly(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string visibleText = StripHiddenReasoningTags(text, out bool sawHiddenReasoning);
        return sawHiddenReasoning && string.IsNullOrWhiteSpace(visibleText);
    }

    private static string StripHiddenReasoningTags(string text, out bool sawHiddenReasoning)
    {
        sawHiddenReasoning = false;
        if (string.IsNullOrEmpty(text))
            return "";

        var visible = new StringBuilder();
        bool insideReasoning = false;
        int offset = 0;
        while (offset < text.Length)
        {
            if (insideReasoning)
            {
                int closeIndex = FindEarliestTag(text, offset, HiddenReasoningCloseTags, out int closeLength);
                if (closeIndex < 0)
                    break;

                offset = closeIndex + closeLength;
                insideReasoning = false;
                continue;
            }

            int openIndex = FindEarliestTag(text, offset, HiddenReasoningOpenTags, out int openLength);
            if (openIndex < 0)
            {
                visible.Append(text, offset, text.Length - offset);
                break;
            }

            if (openIndex > offset)
                visible.Append(text, offset, openIndex - offset);

            sawHiddenReasoning = true;
            offset = openIndex + openLength;
            insideReasoning = true;
        }

        return visible.ToString();
    }

    private static int FindEarliestTag(string text, int startIndex, string[] tags, out int tagLength)
    {
        int bestIndex = -1;
        tagLength = 0;
        foreach (string tag in tags)
        {
            int index = text.IndexOf(tag, startIndex, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                tagLength = tag.Length;
            }
        }

        return bestIndex;
    }

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

    internal static LlamaSharpTensorParallelSettings ResolveTensorParallelSettings(LlmLoadConfig loadConfig)
    {
        if (loadConfig == null ||
            loadConfig.ParallelismMode != LlmParallelismMode.TensorParallel ||
            loadConfig.TargetDeviceIds.Count < 2)
        {
            return LlamaSharpTensorParallelSettings.Disabled;
        }

        int[] gpuIndices = loadConfig.TargetDeviceIds
            .Select(ParseGpuDeviceIndex)
            .Where(index => index >= 0)
            .Distinct()
            .OrderBy(index => index)
            .ToArray();
        if (gpuIndices.Length < 2)
            gpuIndices = Enumerable.Range(0, Math.Max(2, loadConfig.TensorParallelSize)).ToArray();

        float[] splits = new float[gpuIndices.Max() + 1];
        foreach (int index in gpuIndices)
            splits[index] = 1f;

        return new LlamaSharpTensorParallelSettings(true, GPUSplitMode.Tensor, gpuIndices[0], splits);
    }

    internal static void ApplyTensorParallelSettings(ModelParams parameters, LlmLoadConfig loadConfig)
    {
        LlamaSharpTensorParallelSettings settings = ResolveTensorParallelSettings(loadConfig);
        if (!settings.Enabled)
            return;

        parameters.SplitMode = settings.SplitMode;
        parameters.MainGpu = settings.MainGpu;
        parameters.TensorSplits = new TensorSplitsCollection(settings.TensorSplits);
    }

    private static int ParseGpuDeviceIndex(string value)
    {
        string text = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return -1;

        Match match = Regex.Match(text, @"(?:cuda|gpu)?\s*:?\s*(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, out int index) ? index : -1;
    }

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

    internal sealed record LlamaSharpTensorParallelSettings(bool Enabled, GPUSplitMode SplitMode, int MainGpu, float[] TensorSplits)
    {
        public static LlamaSharpTensorParallelSettings Disabled { get; } = new(false, GPUSplitMode.None, 0, []);
    }
}
