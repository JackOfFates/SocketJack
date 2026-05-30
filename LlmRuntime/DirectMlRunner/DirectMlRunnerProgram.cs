using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using LlmRuntime;

namespace LlmRuntime.DirectMlRunner;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        RunnerArguments options;
        try
        {
            options = RunnerArguments.Parse(args);
        }
        catch (Exception ex)
        {
            WriteError("argument_error", ex.Message);
            return 2;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine("LlmRuntime.DirectMlRunner 0.1.0");
            return 0;
        }

        if (options.SelfTest)
        {
            SockJackDmlProbe nativeProbe = SockJackDmlNative.Probe();
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                ok = true,
                protocol = "socketjack-jsonl",
                supports = new[] { "gguf", "cpu", "cuda12", "vulkan", "directml-fallback" },
                directml = nativeProbe.NativeDirectMlAvailable
                    ? "native-loader-ready; DirectML device creation and native GGUF metadata/tensor-table loading are available through SockJackDml"
                    : "protocol-ready; native DirectML tensor dispatch is not complete",
                sockjack_dml = nativeProbe
            }, JsonOptions));
            return 0;
        }

        if (options.NativeProbe)
        {
            Console.WriteLine(JsonSerializer.Serialize(SockJackDmlNative.Probe(), JsonOptions));
            return 0;
        }

        if (options.NativeIdentitySmoke)
        {
            Console.WriteLine(JsonSerializer.Serialize(SockJackDmlNative.RunIdentitySmoke(), JsonOptions));
            return 0;
        }

        if (options.NativeLoadSmoke)
        {
            if (string.IsNullOrWhiteSpace(options.ModelPath))
                throw new ArgumentException("--native-load-smoke requires --model.");

            SockJackDmlModelLoadResult result = SockJackDmlNative.RunModelLoadSmoke(options.ModelPath, options.ContextLength, options.GpuLayerCount);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (options.NativeTensorSmoke)
        {
            if (string.IsNullOrWhiteSpace(options.ModelPath))
                throw new ArgumentException("--native-tensor-smoke requires --model.");

            SockJackDmlTensorSmokeResult result = SockJackDmlNative.RunTensorIdentitySmoke(
                options.ModelPath,
                options.ContextLength,
                options.GpuLayerCount,
                options.TensorName,
                options.MaxTensorSmokeElements);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return result.Success ? 0 : 1;
        }

        if (!options.Protocol.Equals("socketjack-jsonl", StringComparison.OrdinalIgnoreCase))
        {
            WriteError("unsupported_protocol", "Only socketjack-jsonl is supported.");
            return 2;
        }

        try
        {
            string stdin = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            RunnerEnvelope envelope = ReadEnvelope(stdin, options);
            RunnerBackend backend = ResolveBackend(options);
            if (!File.Exists(envelope.ModelPath))
                throw new FileNotFoundException("Model file was not found.", envelope.ModelPath);

            if (options.RequestedBackend.Equals("directml", StringComparison.OrdinalIgnoreCase) && backend.Kind != RunnerBackendKind.DirectML)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new
                {
                    level = "warning",
                    code = "directml_native_kernel_unavailable",
                    message = "Native DirectML GGUF kernels are not implemented in this runner; using " + backend.Kind.ToString().ToLowerInvariant() + " fallback."
                }, JsonOptions));
            }

            if (backend.Kind == RunnerBackendKind.DirectML)
            {
                using var nativeEngine = new SockJackDmlRunnerEngine(envelope);
                await nativeEngine.LoadAsync(options.CancellationToken).ConfigureAwait(false);
                await nativeEngine.RunAsync(options.Stream || envelope.Request.Stream, options.CancellationToken).ConfigureAwait(false);
                return 0;
            }

            ConfigureLlamaSharp(backend);
            using var engine = new LlamaRunnerEngine(envelope, backend);
            await engine.LoadAsync(options.CancellationToken).ConfigureAwait(false);
            await engine.RunAsync(options.Stream || envelope.Request.Stream, options.CancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            WriteError("runner_failed", ex.Message, ex);
            return 1;
        }
    }

    private static RunnerEnvelope ReadEnvelope(string stdin, RunnerArguments options)
    {
        RunnerEnvelope? envelope = null;
        string trimmed = stdin.Trim().TrimStart('\uFEFF');
        if (!string.IsNullOrWhiteSpace(trimmed))
            envelope = JsonSerializer.Deserialize<RunnerEnvelope>(trimmed, JsonOptions);

        envelope ??= new RunnerEnvelope();
        if (string.IsNullOrWhiteSpace(envelope.ModelPath))
            envelope.ModelPath = options.ModelPath;
        if (string.IsNullOrWhiteSpace(envelope.InstanceId))
            envelope.InstanceId = Path.GetFileNameWithoutExtension(envelope.ModelPath);

        if (string.IsNullOrWhiteSpace(envelope.ModelPath))
            throw new ArgumentException("A model path is required through --model or stdin model_path.");

        envelope.ContextLength = envelope.ContextLength == 0 ? options.ContextLength : envelope.ContextLength;
        envelope.GpuLayerCount = envelope.GpuLayerCount == 0 ? options.GpuLayerCount : envelope.GpuLayerCount;
        envelope.Request ??= new RunnerRequest();
        return envelope;
    }

    private static RunnerBackend ResolveBackend(RunnerArguments options)
    {
        if (options.RequestedBackend.Equals("directml", StringComparison.OrdinalIgnoreCase))
        {
            if (!options.AllowFallback)
                return new RunnerBackend(RunnerBackendKind.DirectML, false);

            return RunnerBackend.Parse(options.FallbackBackend, allowFallback: true);
        }

        return RunnerBackend.Parse(options.RequestedBackend, options.AllowFallback);
    }

    private static void ConfigureLlamaSharp(RunnerBackend backend)
    {
        if (backend.Kind == RunnerBackendKind.DirectML)
        {
            throw new NotSupportedException(
                "Native DirectML GGUF kernels are not implemented in this runner yet. Re-run with --allow-fallback true and --fallback-backend vulkan, cuda12, auto, or cpu.");
        }

        LlmRuntime.LlmNativeRuntimePathConfigurator.ConfigureProcessPath(ToLlmBackendKind(backend.Kind));

        NativeLibraryConfig.All
            .WithAutoFallback(backend.AllowFallback)
            .WithCuda(backend.Kind is RunnerBackendKind.Cuda12)
            .WithVulkan(backend.Kind is RunnerBackendKind.Vulkan);
    }

    private static LlmRuntime.LlmBackendKind ToLlmBackendKind(RunnerBackendKind backend) =>
        backend switch
        {
            RunnerBackendKind.Cuda12 => LlmRuntime.LlmBackendKind.Cuda12,
            RunnerBackendKind.Vulkan => LlmRuntime.LlmBackendKind.Vulkan,
            RunnerBackendKind.Cpu => LlmRuntime.LlmBackendKind.Cpu,
            _ => LlmRuntime.LlmBackendKind.Auto
        };

    private static void WriteError(string code, string message, Exception? exception = null)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new
        {
            error = new
            {
                code,
                message,
                detail = exception?.GetType().FullName
            }
        }, JsonOptions));
    }
}

internal sealed class LlamaRunnerEngine : IDisposable
{
    private readonly RunnerEnvelope _envelope;
    private readonly RunnerBackend _backend;
    private LLamaWeights? _weights;
    private ModelParams? _parameters;

    public LlamaRunnerEngine(RunnerEnvelope envelope, RunnerBackend backend)
    {
        _envelope = envelope;
        _backend = backend;
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _parameters = new ModelParams(_envelope.ModelPath)
            {
                ContextSize = _envelope.ContextLength == 0 ? 4096u : _envelope.ContextLength,
                GpuLayerCount = _backend.Kind is RunnerBackendKind.Auto or RunnerBackendKind.Cpu ? 0 : _envelope.GpuLayerCount
            };
            _weights = LLamaWeights.LoadFromFile(_parameters);
        }, cancellationToken);
    }

    public async Task RunAsync(bool stream, CancellationToken cancellationToken)
    {
        ThrowIfNotLoaded();
        var stopwatch = Stopwatch.StartNew();
        var builder = new StringBuilder();
        var repetitionGuard = new LlmRuntimeRepetitionGuard();

        await foreach (string token in InferAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(token))
                continue;

            LlmRuntimeRepetitionGuardDecision decision = repetitionGuard.Accept(token);
            if (!string.IsNullOrEmpty(decision.Text))
            {
                builder.Append(decision.Text);
                if (stream)
                    WriteJsonLine(new RunnerToken { Token = decision.Text });
            }

            if (decision.ShouldStop)
                break;
        }

        string output = ApplyStopSequences(builder.ToString(), _envelope.Request.Stop);
        if (!stream)
            WriteJsonLine(new RunnerToken { Content = output, FinishReason = "stop", ElapsedSeconds = stopwatch.Elapsed.TotalSeconds });
        else
        {
            Console.WriteLine("[DONE]");
            Console.Out.Flush();
        }
    }

    private async IAsyncEnumerable<string> InferAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var context = _weights!.CreateContext(_parameters!);
        var executor = new InteractiveExecutor(context);
        var parameters = new InferenceParams
        {
            MaxTokens = Math.Clamp(_envelope.Request.MaxTokens <= 0 ? LlmChatRequest.DefaultMaxCompletionTokens : _envelope.Request.MaxTokens, 1, 131072),
            AntiPrompts = _envelope.Request.Stop.Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
            OverflowStrategy = ContextOverflowStrategy.TruncateAndReprefill,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = Math.Clamp(_envelope.Request.Temperature <= 0 ? 0.7f : _envelope.Request.Temperature, 0f, 10f),
                TopP = Math.Clamp(_envelope.Request.TopP <= 0 ? 0.95f : _envelope.Request.TopP, 0f, 1f),
                RepeatPenalty = 1.12f,
                FrequencyPenalty = 0.08f,
                PresencePenalty = 0.02f,
                PenaltyCount = 256
            }
        };

        await foreach (string text in executor.InferAsync(BuildPrompt(_weights!, _envelope.Request.Messages), parameters, cancellationToken).ConfigureAwait(false))
            yield return text;
    }

    private static string BuildPrompt(LLamaWeights weights, IReadOnlyList<RunnerMessage> messages)
    {
        try
        {
            var template = new LLamaTemplate(weights, strict: false)
            {
                AddAssistant = true
            };

            foreach (RunnerMessage message in messages)
            {
                template.Add(ToTemplateRole(message.Role), (message.Content ?? "").Trim());
            }

            return PromptTemplateTransformer.ToModelPrompt(template);
        }
        catch
        {
            return BuildPlainPrompt(messages);
        }
    }

    private static string BuildPlainPrompt(IReadOnlyList<RunnerMessage> messages)
    {
        var builder = new StringBuilder();
        foreach (RunnerMessage message in messages)
        {
            string role = NormalizeRole(message.Role);
            builder.Append(role).Append(": ").AppendLine((message.Content ?? "").Trim());
        }

        builder.Append("Assistant: ");
        return builder.ToString();
    }

    private static string ToTemplateRole(string? role) =>
        (role ?? "").ToLowerInvariant() switch
        {
            "system" => "system",
            "assistant" => "assistant",
            "tool" => "tool",
            _ => "user"
        };

    private static string NormalizeRole(string? role) =>
        (role ?? "").ToLowerInvariant() switch
        {
            "system" => "System",
            "assistant" => "Assistant",
            "tool" => "Tool",
            _ => "User"
        };

    private static string ApplyStopSequences(string text, IReadOnlyList<string> stopSequences)
    {
        foreach (string stop in stopSequences.Where(value => !string.IsNullOrEmpty(value)))
        {
            int index = text.IndexOf(stop, StringComparison.Ordinal);
            if (index >= 0)
                text = text[..index];
        }

        return text;
    }

    private static void WriteJsonLine(RunnerToken token)
    {
        Console.WriteLine(JsonSerializer.Serialize(token, ProgramJson.Options));
        Console.Out.Flush();
    }

    private void ThrowIfNotLoaded()
    {
        if (_weights == null || _parameters == null)
            throw new InvalidOperationException("Model has not been loaded.");
    }

    public void Dispose()
    {
        _weights?.Dispose();
    }
}

internal sealed class SockJackDmlRunnerEngine : IDisposable
{
    private readonly RunnerEnvelope _envelope;
    private SockJackDmlLoadedModel? _model;

    public SockJackDmlRunnerEngine(RunnerEnvelope envelope)
    {
        _envelope = envelope;
    }

    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _model = SockJackDmlNative.LoadModel(_envelope.ModelPath, _envelope.ContextLength, _envelope.GpuLayerCount);
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                level = "info",
                code = "sockjack_dml_model_loaded",
                message = _model.Info.Message,
                model = new
                {
                    _model.Info.ModelPath,
                    _model.Info.Architecture,
                    _model.Info.ModelName,
                    _model.Info.FileSize,
                    _model.Info.TensorCount,
                    _model.Info.MetadataCount,
                    _model.Info.DataOffset,
                    _model.Info.ContextLength,
                    _model.Info.EmbeddingLength,
                    _model.Info.SupportedTensorCount,
                    _model.Info.UnsupportedTensorCount,
                    _model.Info.DirectMlDeviceReady
                }
            }, ProgramJson.Options));
        }, cancellationToken);
    }

    public Task RunAsync(bool stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_model == null)
            throw new InvalidOperationException("Native DirectML model has not been loaded.");

        throw new NotSupportedException(
            "SockJackDml loaded the GGUF file natively and initialized DirectML, but native token decode is not implemented yet. This path fails closed and does not route inference through LLamaSharp or CPU fallback.");
    }

    public void Dispose()
    {
        _model?.Dispose();
    }
}

internal static class ProgramJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class RunnerArguments
{
    public string ModelPath { get; private set; } = "";
    public string Protocol { get; private set; } = "socketjack-jsonl";
    public string RequestedBackend { get; private set; } = "directml";
    public string FallbackBackend { get; private set; } = "vulkan";
    public uint ContextLength { get; private set; } = 4096;
    public int GpuLayerCount { get; private set; } = -1;
    public bool AllowFallback { get; private set; } = true;
    public bool Stream { get; private set; }
    public bool SelfTest { get; private set; }
    public bool NativeProbe { get; private set; }
    public bool NativeIdentitySmoke { get; private set; }
    public bool NativeLoadSmoke { get; private set; }
    public bool NativeTensorSmoke { get; private set; }
    public string TensorName { get; private set; } = "";
    public uint MaxTensorSmokeElements { get; private set; } = 1024;
    public bool ShowVersion { get; private set; }
    public CancellationToken CancellationToken => CancellationToken.None;

    public static RunnerArguments Parse(string[] args)
    {
        var result = new RunnerArguments();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--model":
                    result.ModelPath = RequireValue(args, ref i, arg);
                    break;
                case "--protocol":
                    result.Protocol = RequireValue(args, ref i, arg);
                    break;
                case "--backend":
                    result.RequestedBackend = RequireValue(args, ref i, arg);
                    break;
                case "--fallback-backend":
                    result.FallbackBackend = RequireValue(args, ref i, arg);
                    break;
                case "--context-length":
                    result.ContextLength = uint.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--gpu-layers":
                case "--gpu-layer-count":
                    result.GpuLayerCount = int.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--allow-fallback":
                    result.AllowFallback = bool.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--stream":
                    result.Stream = true;
                    break;
                case "--self-test":
                    result.SelfTest = true;
                    break;
                case "--native-probe":
                    result.NativeProbe = true;
                    break;
                case "--native-identity-smoke":
                    result.NativeIdentitySmoke = true;
                    break;
                case "--native-load-smoke":
                    result.NativeLoadSmoke = true;
                    break;
                case "--native-tensor-smoke":
                    result.NativeTensorSmoke = true;
                    break;
                case "--tensor":
                case "--tensor-name":
                    result.TensorName = RequireValue(args, ref i, arg);
                    break;
                case "--max-elements":
                case "--max-tensor-elements":
                    result.MaxTensorSmokeElements = uint.Parse(RequireValue(args, ref i, arg));
                    break;
                case "--version":
                    result.ShowVersion = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                        throw new ArgumentException("Unknown argument: " + arg);
                    break;
            }
        }

        return result;
    }

    private static string RequireValue(string[] args, ref int index, string name)
    {
        if (index + 1 >= args.Length)
            throw new ArgumentException(name + " requires a value.");
        index++;
        return args[index];
    }
}

internal sealed record RunnerBackend(RunnerBackendKind Kind, bool AllowFallback)
{
    public static RunnerBackend Parse(string value, bool allowFallback)
    {
        RunnerBackendKind kind = (value ?? "").ToLowerInvariant() switch
        {
            "auto" => RunnerBackendKind.Auto,
            "cpu" => RunnerBackendKind.Cpu,
            "cuda" or "cuda12" => RunnerBackendKind.Cuda12,
            "vulkan" => RunnerBackendKind.Vulkan,
            "directml" or "direct_ml" or "direct-ml" => RunnerBackendKind.DirectML,
            _ => RunnerBackendKind.Vulkan
        };

        return new RunnerBackend(kind, allowFallback);
    }
}

internal enum RunnerBackendKind
{
    Auto,
    Cpu,
    Cuda12,
    Vulkan,
    DirectML
}

internal sealed class RunnerEnvelope
{
    [JsonPropertyName("model_path")]
    public string ModelPath { get; set; } = "";

    [JsonPropertyName("instance_id")]
    public string InstanceId { get; set; } = "";

    [JsonPropertyName("context_length")]
    public uint ContextLength { get; set; }

    [JsonPropertyName("gpu_layer_count")]
    public int GpuLayerCount { get; set; } = -1;

    public RunnerRequest Request { get; set; } = new();
}

internal sealed class RunnerRequest
{
    public string Model { get; set; } = "";
    public IReadOnlyList<RunnerMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 512;

    public float Temperature { get; set; } = 0.7f;

    [JsonPropertyName("top_p")]
    public float TopP { get; set; } = 0.95f;

    public bool Stream { get; set; }
    public IReadOnlyList<string> Stop { get; set; } = [];
}

internal sealed class RunnerMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

internal sealed class RunnerToken
{
    public string? Token { get; set; }
    public string? Content { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    [JsonPropertyName("elapsed_seconds")]
    public double? ElapsedSeconds { get; set; }
}

