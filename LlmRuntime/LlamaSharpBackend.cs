using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    private static readonly string[] HiddenReasoningCloseTags = ["</think>", "</thinking>", "</thought>", "</analysis>", "</end_of_thought>", "<|end_of_thought|>", "<|end_of_analysis|>"];

    private LLamaWeights? _weights;
    private MtmdWeights? _mtmdWeights;
    private MtmdContextParams? _mtmdContextParams;
    private string? _multimodalProjectorPath;
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
            _multimodalProjectorPath = ResolveMultimodalProjectorPath(ModelPath);
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
        var mediaEmbeds = new List<SafeMtmdEmbed>();
        try
        {
            ThrowIfNotLoaded();
            int imageCount = CountStructuredImages(request.Messages);
            bool hasImageContent = imageCount > 0;
            MtmdWeights? mtmdWeights = hasImageContent
                ? await EnsureMultimodalWeightsLoadedAsync(imageCount, cancellationToken).ConfigureAwait(false)
                : null;
            IReadOnlyList<LlmChatMessage> inferenceMessages = BuildInferenceMessages(request.Messages);
            string mediaMarker = "";
            if (mtmdWeights != null)
            {
                if (!mtmdWeights.SupportsVision)
                    throw new InvalidOperationException($"The multimodal projector '{_multimodalProjectorPath}' does not support vision inputs.");

                mtmdWeights.ClearMedia();
                mediaMarker = _mtmdContextParams?.MediaMarker ?? NativeApi.MtmdDefaultMarker() ?? "<media>";
                inferenceMessages = BuildMultimodalInferenceMessages(inferenceMessages, mtmdWeights, mediaMarker, mediaEmbeds);
                if (mediaEmbeds.Count != imageCount)
                {
                    throw new InvalidOperationException(
                        $"Local vision inference expected {imageCount} image embedding(s), but loaded {mediaEmbeds.Count}. The request was not sent to the model.");
                }
            }

            using var context = _weights!.CreateContext(_parameters!);
            var executor = mtmdWeights == null
                ? new InteractiveExecutor(context)
                : new InteractiveExecutor(context, mtmdWeights);
            foreach (SafeMtmdEmbed embed in mediaEmbeds)
                executor.Embeds.Add(embed);
            var parameters = CreateInferenceParams(request);
            string prompt = BuildPrompt(_weights!, inferenceMessages);
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
            foreach (SafeMtmdEmbed embed in mediaEmbeds)
                embed.Dispose();
            _mtmdWeights?.ClearMedia();
            _inferenceLock.Release();
        }
    }

    private async Task<MtmdWeights> EnsureMultimodalWeightsLoadedAsync(int imageCount, CancellationToken cancellationToken)
    {
        int imageMaxTokens = ResolveMultimodalImageMaxTokens(LoadConfig.ContextLength, imageCount);
        if (_mtmdWeights != null && _mtmdContextParams?.ImageMaxTokens == imageMaxTokens)
            return _mtmdWeights;

        if (_mtmdWeights != null)
        {
            _mtmdWeights.Dispose();
            _mtmdWeights = null;
            _mtmdContextParams = null;
        }

        if (_weights == null)
            throw new InvalidOperationException("The text model must be loaded before its multimodal projector.");
        if (string.IsNullOrWhiteSpace(_multimodalProjectorPath) || !File.Exists(_multimodalProjectorPath))
            throw new InvalidOperationException($"Model '{InstanceId}' is marked as vision-capable, but no sibling mmproj/projector GGUF file was found.");

        _promptPipelineStatus = "loading_vision";
        _promptPipelineDetail = "Loading multimodal projector.";
        var mtmdParameters = MtmdContextParams.Default();
        mtmdParameters.UseGpu = ShouldUseGpuForMultimodal(LoadConfig);
        mtmdParameters.PrintTimings = false;
        mtmdParameters.ImageMaxTokens = imageMaxTokens;
        if (mtmdParameters.ImageMinTokens > mtmdParameters.ImageMaxTokens)
            mtmdParameters.ImageMinTokens = Math.Min(256, mtmdParameters.ImageMaxTokens);
        MtmdWeights loaded = await MtmdWeights.LoadFromFileAsync(
            _multimodalProjectorPath,
            _weights,
            mtmdParameters,
            cancellationToken).ConfigureAwait(false);
        if (!loaded.SupportsVision)
        {
            loaded.Dispose();
            throw new InvalidOperationException($"The multimodal projector '{_multimodalProjectorPath}' does not advertise vision support.");
        }

        _mtmdContextParams = mtmdParameters;
        _mtmdWeights = loaded;
        _promptPipelineStatus = "ready";
        _promptPipelineDetail = "Model weights and multimodal projector are loaded.";
        return loaded;
    }

    internal static string? ResolveMultimodalProjectorPath(string modelPath)
    {
        string? directory = Path.GetDirectoryName(modelPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string fullModelPath = Path.GetFullPath(modelPath);
        return Directory.EnumerateFiles(directory, "*.gguf", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                return !Path.GetFullPath(path).Equals(fullModelPath, StringComparison.OrdinalIgnoreCase) &&
                       (name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("projector", StringComparison.OrdinalIgnoreCase));
            })
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static IReadOnlyList<LlmChatMessage> BuildMultimodalInferenceMessages(
        IReadOnlyList<LlmChatMessage> messages,
        MtmdWeights mtmdWeights,
        string mediaMarker,
        List<SafeMtmdEmbed> mediaEmbeds)
    {
        var result = new List<LlmChatMessage>(messages.Count);
        foreach (LlmChatMessage message in messages)
        {
            if (!message.HasImageContent || !message.StructuredContent.HasValue)
            {
                result.Add(message);
                continue;
            }

            JsonElement content = message.StructuredContent.Value;
            if (content.ValueKind != JsonValueKind.Array)
            {
                result.Add(message);
                continue;
            }

            var text = new StringBuilder();
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (TryReadStructuredTextPart(part, out string partText))
                {
                    AppendMultimodalPromptPart(text, partText);
                    continue;
                }

                if (!TryReadStructuredImageUrl(part, out string imageUrl))
                    continue;
                if (!TryDecodeImageDataUrl(imageUrl, out byte[] imageBytes))
                    throw new InvalidOperationException("Local vision inference requires an image data URL with valid base64 bytes.");

                SafeMtmdEmbed embed = mtmdWeights.LoadMedia(imageBytes);
                mediaEmbeds.Add(embed);
                AppendMultimodalPromptPart(text, mediaMarker);
            }

            string contentText = text.Length > 0 ? text.ToString() : message.Content;
            result.Add(new LlmChatMessage(message.Role, contentText, message.StructuredContent, message.HasImageContent));
        }
        return result;
    }

    internal static int CountStructuredImages(IReadOnlyList<LlmChatMessage> messages)
    {
        int imageCount = 0;
        foreach (LlmChatMessage message in messages ?? Array.Empty<LlmChatMessage>())
        {
            if (!message.HasImageContent || !message.StructuredContent.HasValue)
                continue;

            JsonElement content = message.StructuredContent.Value;
            if (content.ValueKind == JsonValueKind.Array)
                imageCount += content.EnumerateArray().Count(part => TryReadStructuredImageUrl(part, out _));
            else if (TryReadStructuredImageUrl(content, out _))
                imageCount++;
        }
        return imageCount;
    }

    private static bool TryReadStructuredTextPart(JsonElement part, out string text)
    {
        text = "";
        if (part.ValueKind == JsonValueKind.String)
        {
            text = part.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(text);
        }
        if (part.ValueKind != JsonValueKind.Object)
            return false;

        string type = ReadJsonString(part, "type");
        if (!type.Equals("text", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("input_text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        text = FirstNonEmpty(ReadJsonString(part, "text"), ReadJsonString(part, "content"));
        return !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryReadStructuredImageUrl(JsonElement part, out string imageUrl)
    {
        imageUrl = "";
        if (part.ValueKind != JsonValueKind.Object)
            return false;

        string type = ReadJsonString(part, "type");
        bool imagePart = type.Equals("image_url", StringComparison.OrdinalIgnoreCase) ||
                         type.Equals("input_image", StringComparison.OrdinalIgnoreCase) ||
                         part.TryGetProperty("image_url", out _);
        if (!imagePart)
            return false;

        if (part.TryGetProperty("image_url", out JsonElement imageUrlElement))
        {
            imageUrl = imageUrlElement.ValueKind == JsonValueKind.String
                ? imageUrlElement.GetString() ?? ""
                : ReadJsonString(imageUrlElement, "url");
        }
        imageUrl = FirstNonEmpty(
            imageUrl,
            ReadJsonString(part, "url"),
            ReadJsonString(part, "data_url"),
            ReadJsonString(part, "image_url"));
        return !string.IsNullOrWhiteSpace(imageUrl);
    }

    internal static bool TryDecodeImageDataUrl(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int separator = value.IndexOf(',');
        if (separator <= 0 || !value[..separator].Contains(";base64", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            bytes = Convert.FromBase64String(value[(separator + 1)..]);
            return bytes.Length is > 0 and <= 32 * 1024 * 1024;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    private static void AppendMultimodalPromptPart(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.AppendLine();
        builder.Append(value.Trim());
    }

    private static string ReadJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return "";
        }
        return property.GetString() ?? "";
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

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
            SamplingPipeline = CreateSamplingPipeline(request)
        };
    }

    internal static ISamplingPipeline CreateSamplingPipeline(LlmChatRequest request)
    {
        if (request.Temperature <= 0)
            return new GreedySamplingPipeline();

        return new DefaultSamplingPipeline
        {
            Temperature = request.Temperature,
            TopP = request.TopP,
            RepeatPenalty = 1.12f,
            FrequencyPenalty = 0.08f,
            PresencePenalty = 0.02f,
            PenaltyCount = 256
        };
    }

    internal static string DetermineFinishReason(bool stoppedByGuard, int generatedTokens, int maxTokens, int completionTokenEstimate, bool stoppedInsideHiddenReasoning)
    {
        // A repetition guard can fire while a reasoning model is still inside a
        // hidden <think> block. That is still a clipped answer from the caller's
        // perspective and must remain eligible for Web Chat auto-continuation.
        if (stoppedInsideHiddenReasoning)
            return "length";

        if (stoppedByGuard)
            return "stop";

        return generatedTokens >= maxTokens ||
               completionTokenEstimate >= maxTokens
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

    private IReadOnlyList<LlmChatMessage> BuildInferenceMessages(IReadOnlyList<LlmChatMessage> messages)
    {
        messages ??= [];
        return messages;
    }

    internal bool ShouldSuppressReasoningByDefault()
    {
        // Reasoning visibility is controlled per request with /no_think. Model
        // naming must not silently disable the Workstation thinking stream.
        return false;
    }

    internal static bool ContainsNoThinkControl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Regex.IsMatch(text, @"(?:^|\s)/(?:no[_-]?think)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static int GetEffectiveGpuLayerCount(LlmLoadConfig loadConfig) =>
        LlmBackendAutoSelector.Resolve(loadConfig.Backend) == LlmBackendKind.Cpu ? 0 : loadConfig.GpuLayerCount;

    internal static bool ShouldUseGpuForMultimodal(LlmLoadConfig loadConfig) =>
        GetEffectiveGpuLayerCount(loadConfig) != 0;

    internal static int ResolveMultimodalImageMaxTokens(uint contextLength, int imageCount = 1)
    {
        int boundedContext = (int)Math.Min(int.MaxValue, Math.Max(512u, contextLength));
        // Keep the visual input to at most half of the context. A single image
        // gets enough visual tokens for UI text and layout; multiple images
        // divide the same budget instead of overflowing the prompt window.
        int safeImageCount = Math.Max(1, imageCount);
        return Math.Clamp(boundedContext / 2 / safeImageCount, 256, 2048);
    }

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
            _mtmdWeights?.Dispose();
            _mtmdWeights = null;
            _mtmdContextParams = null;
            _multimodalProjectorPath = null;
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
