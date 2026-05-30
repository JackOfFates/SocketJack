using System.Text.Json;
using JackONNX;
using JackONNX.Audio;
using JackONNX.Image;
using JackONNX.Runtime;
using JackONNX.Video;
using LlmRuntime;

namespace JackONNX.LlmRuntime;

public sealed class JackOnnxLlmRuntimeToolOptions
{
    public bool EnableImageGeneration { get; set; } = true;

    public bool EnableAudioGeneration { get; set; } = true;

    public bool EnableVideoGeneration { get; set; } = true;

    public bool ReturnArtifacts { get; set; } = true;

    public bool StreamPreviews { get; set; } = true;

    public LlmToolApprovalMode GenerationApprovalMode { get; set; } = LlmToolApprovalMode.AskEveryTime;
}

public static class JackOnnxLlmRuntimeToolRegistration
{
    private static readonly JsonSerializerOptions JsonOptions = JackOnnxModelManifest.CreateJsonOptions();

    public static IReadOnlyList<LlmToolDefinition> RegisterJackOnnxTools(this LlmToolRegistry registry, JackOnnxLlmRuntimeToolOptions? options = null)
    {
        if (registry == null)
            throw new ArgumentNullException(nameof(registry));

        options ??= new JackOnnxLlmRuntimeToolOptions();
        var definitions = CreateDefinitions(options);
        foreach (var definition in definitions)
            registry.UpsertDefinition(definition);

        return definitions;
    }

    public static IReadOnlyList<ILlmTool> CreateJackOnnxBuiltInTools(
        JackOnnxRuntimeEngine runtime,
        JackOnnxLlmRuntimeToolOptions? options = null)
    {
        if (runtime == null)
            throw new ArgumentNullException(nameof(runtime));

        return CreateDefinitions(options)
            .Select(definition => new JackOnnxLlmRuntimeTool(definition, runtime))
            .Cast<ILlmTool>()
            .ToList();
    }

    public static IReadOnlyList<LlmToolDefinition> CreateDefinitions(JackOnnxLlmRuntimeToolOptions? options = null)
    {
        options ??= new JackOnnxLlmRuntimeToolOptions();
        var definitions = new List<LlmToolDefinition>
        {
            CreateDefinition(
                "jackonnx.devices.list",
                "jackonnx_devices_list",
                "List JackONNX execution providers and local devices.",
                "{}",
                LlmToolApprovalMode.AlwaysAllow,
                LlmToolPermissions.RepositoryAccess),
            CreateDefinition(
                "jackonnx.models.list",
                "jackonnx_models_list",
                "List locally registered JackONNX media models.",
                "{}",
                LlmToolApprovalMode.AlwaysAllow,
                LlmToolPermissions.FileSystemRead),
            CreateDefinition(
                "jackonnx.jobs.status",
                "jackonnx_jobs_status",
                "Inspect JackONNX generation job status.",
                """{"type":"object","properties":{"jobId":{"type":"string"}},"required":["jobId"]}""",
                LlmToolApprovalMode.AlwaysAllow,
                LlmToolPermissions.RepositoryAccess),
            CreateDefinition(
                "jackonnx.jobs.cancel",
                "jackonnx_jobs_cancel",
                "Cancel a running JackONNX generation job.",
                """{"type":"object","properties":{"jobId":{"type":"string"}},"required":["jobId"]}""",
                options.GenerationApprovalMode,
                LlmToolPermissions.RepositoryAccess)
        };

        if (options.EnableImageGeneration)
        {
            definitions.Add(CreateDefinition(
                "jackonnx.image.generate",
                "jackonnx_image_generate",
                "Generate an image with JackONNX and return the artifact to the LlmRuntime session. Optional source media enables image-from-image generation when the selected pipeline supports it.",
                """{"type":"object","properties":{"prompt":{"type":"string"},"negativePrompt":{"type":"string"},"modelId":{"type":"string"},"width":{"type":"integer","minimum":64},"height":{"type":"integer","minimum":64},"steps":{"type":"integer","minimum":1},"guidanceScale":{"type":"number","minimum":0.1},"seed":{"type":"integer"},"deviceId":{"type":"string"},"cudaDevice":{"type":"string"},"preferredProvider":{"type":"string","enum":["auto","cuda","cpu"]},"devicePolicy":{"type":"string","enum":["PreferGpuThenCpu","RequirePreferredProvider","CpuOnly"]},"deviceMap":{"type":"string"},"allowCpuOffload":{"type":"boolean"},"offloadFolder":{"type":"string"},"disableCudaMemoryGuard":{"type":"boolean"},"cudaMemoryReserveMb":{"type":"integer"},"cpuMaxMemory":{"type":"string"},"memorySaving":{"type":"boolean"},"sourcePath":{"type":"string"},"sourceDataUrl":{"type":"string"},"sourceMediaType":{"type":"string"},"sourceName":{"type":"string"},"sourceKind":{"type":"string","enum":["image","video","audio","media",""]},"generationMode":{"type":"string"}},"required":["prompt"]}""",
                options.GenerationApprovalMode,
                LlmToolPermissions.FileSystemWrite));
        }

        if (options.EnableAudioGeneration)
        {
            definitions.Add(CreateDefinition(
                "jackonnx.audio.generate",
                "jackonnx_audio_generate",
                "Generate audio with JackONNX and return the artifact to the LlmRuntime session. Optional source media enables audio-from-audio generation when implemented by the selected pipeline.",
                """{"type":"object","properties":{"prompt":{"type":"string"},"text":{"type":"string"},"negativePrompt":{"type":"string"},"modelId":{"type":"string"},"seconds":{"type":"number"},"sampleRate":{"type":"integer"},"seed":{"type":"integer"},"sourcePath":{"type":"string"},"sourceDataUrl":{"type":"string"},"sourceMediaType":{"type":"string"},"sourceName":{"type":"string"},"sourceKind":{"type":"string","enum":["image","video","audio","media",""]},"generationMode":{"type":"string"}}}""",
                options.GenerationApprovalMode,
                LlmToolPermissions.FileSystemWrite));
            definitions.Add(CreateDefinition(
                "jackonnx.audio.speech",
                "jackonnx_audio_speech",
                "Generate speech audio with JackONNX and return the artifact to the LlmRuntime session.",
                """{"type":"object","properties":{"text":{"type":"string"},"voice":{"type":"string"},"modelId":{"type":"string"},"speed":{"type":"number"},"sampleRate":{"type":"integer"},"seed":{"type":"integer"},"sourcePath":{"type":"string"},"sourceDataUrl":{"type":"string"},"sourceMediaType":{"type":"string"},"sourceName":{"type":"string"},"sourceKind":{"type":"string","enum":["image","video","audio","media",""]},"generationMode":{"type":"string"}},"required":["text"]}""",
                options.GenerationApprovalMode,
                LlmToolPermissions.FileSystemWrite));
        }

        if (options.EnableVideoGeneration)
        {
            definitions.Add(CreateDefinition(
                "jackonnx.video.generate",
                "jackonnx_video_generate",
                "Generate video frames or clips with JackONNX and return the artifact to the LlmRuntime session. Optional source media enables video-from-image or video-from-video generation when the selected pipeline supports it.",
                """{"type":"object","properties":{"prompt":{"type":"string"},"negativePrompt":{"type":"string"},"modelId":{"type":"string"},"width":{"type":"integer"},"height":{"type":"integer"},"seconds":{"type":"number","minimum":0.1,"maximum":30,"description":"Desired clip duration. If seconds multiplied by fps exceeds 96 output frames, JackONNX caps frames and shortens the effective duration."},"fps":{"type":"integer","minimum":1,"maximum":24,"description":"Playback frames per second. Higher FPS consumes the 96-frame output budget faster."},"frames":{"type":"integer","minimum":1,"maximum":96,"description":"Total output frame budget. Current local video generation is capped at 96 frames."},"steps":{"type":"integer"},"guidanceScale":{"type":"number"},"seed":{"type":"integer"},"deviceId":{"type":"string"},"cudaDevice":{"type":"string"},"preferredProvider":{"type":"string","enum":["auto","cuda","cpu"]},"devicePolicy":{"type":"string","enum":["PreferGpuThenCpu","RequirePreferredProvider","CpuOnly"]},"deviceMap":{"type":"string"},"videoDeviceMap":{"type":"string"},"allowCpuOffload":{"type":"boolean"},"videoAllowCpuOffload":{"type":"boolean"},"videoOffloadFolder":{"type":"string"},"disableCudaMemoryGuard":{"type":"boolean"},"videoDisableCudaMemoryGuard":{"type":"boolean"},"cudaMemoryReserveMb":{"type":"integer"},"videoCudaMemoryReserveMb":{"type":"integer"},"cpuMaxMemory":{"type":"string"},"videoCpuMaxMemory":{"type":"string"},"videoMemorySaving":{"type":"boolean"},"sourcePath":{"type":"string"},"sourceDataUrl":{"type":"string"},"sourceMediaType":{"type":"string"},"sourceName":{"type":"string"},"sourceKind":{"type":"string","enum":["image","video","audio","media",""]},"generationMode":{"type":"string"}},"required":["prompt"]}""",
                options.GenerationApprovalMode,
                LlmToolPermissions.FileSystemWrite));
        }

        return definitions;
    }

    private static LlmToolDefinition CreateDefinition(
        string id,
        string name,
        string description,
        string inputSchema,
        LlmToolApprovalMode approvalMode,
        LlmToolPermissions permissions)
    {
        return new LlmToolDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            Visibility = LlmToolVisibility.Proprietary,
            SourceType = LlmToolSourceType.BuiltInSocketJack,
            Source = id,
            Version = "0.1.0",
            Vendor = "JackOfFates",
            Tags = ["jackonnx", "onnx", "media", "generation"],
            InputSchema = ParseSchema(inputSchema),
            ApprovalMode = approvalMode,
            Permissions = permissions,
            TimeoutSeconds = 3600
        };
    }

    private static JsonElement ParseSchema(string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return document.RootElement.Clone();
    }

    internal static JsonElement ToJsonElement<T>(T value)
    {
        return JsonSerializer.SerializeToElement(value, JsonOptions);
    }
}

public sealed class JackOnnxLlmRuntimeTool : ILlmTool
{
    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly JackOnnxImagePipeline _images;
    private readonly JackOnnxAudioPipeline _audio;
    private readonly JackOnnxVideoPipeline _video;

    public JackOnnxLlmRuntimeTool(LlmToolDefinition definition, JackOnnxRuntimeEngine runtime)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _images = new JackOnnxImagePipeline(_runtime);
        _audio = new JackOnnxAudioPipeline(_runtime);
        _video = new JackOnnxVideoPipeline(_runtime);
    }

    public string Id => Definition.Id;

    public LlmToolDefinition Definition { get; }

    public async Task<LlmToolInvocationResult> InvokeAsync(LlmToolInvocationRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string toolId = LlmToolRegistry.NormalizeId(string.IsNullOrWhiteSpace(Definition.Source) ? Definition.Id : Definition.Source);
        try
        {
            return toolId switch
            {
                "jackonnx_devices_list" => await InvokeDevicesAsync(cancellationToken).ConfigureAwait(false),
                "jackonnx_models_list" => await InvokeModelsAsync(cancellationToken).ConfigureAwait(false),
                "jackonnx_jobs_status" => await InvokeJobStatusAsync(request.Input, cancellationToken).ConfigureAwait(false),
                "jackonnx_jobs_cancel" => await InvokeJobCancelAsync(request.Input, cancellationToken).ConfigureAwait(false),
                "jackonnx_image_generate" => await InvokeImageAsync(request, cancellationToken).ConfigureAwait(false),
                "jackonnx_audio_generate" => await InvokeAudioAsync(request, cancellationToken).ConfigureAwait(false),
                "jackonnx_audio_speech" => await InvokeSpeechAsync(request, cancellationToken).ConfigureAwait(false),
                "jackonnx_video_generate" => await InvokeVideoAsync(request, cancellationToken).ConfigureAwait(false),
                _ => Failure("Unknown JackONNX tool: " + Definition.Id)
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failure("JackONNX tool failed: " + ex.Message);
        }
    }

    private async Task<LlmToolInvocationResult> InvokeDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await _runtime.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        return Success(new
        {
            provider_count = _runtime.Providers.Count,
            devices
        }, "JackONNX devices listed.");
    }

    private async Task<LlmToolInvocationResult> InvokeModelsAsync(CancellationToken cancellationToken)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return Success(new
        {
            model_count = models.Count,
            models
        }, "JackONNX models listed.");
    }

    private async Task<LlmToolInvocationResult> InvokeJobStatusAsync(JsonElement input, CancellationToken cancellationToken)
    {
        string jobId = ReadString(input, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
        {
            var jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);
            return Success(new
            {
                job_count = jobs.Count,
                jobs
            }, "JackONNX jobs listed.");
        }

        var job = await _runtime.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return Failure("JackONNX job was not found: " + jobId);

        return Success(new { job }, "JackONNX job status returned.");
    }

    private async Task<LlmToolInvocationResult> InvokeJobCancelAsync(JsonElement input, CancellationToken cancellationToken)
    {
        string jobId = ReadString(input, "jobId");
        if (string.IsNullOrWhiteSpace(jobId))
            return Failure("jobId is required.");

        var job = await _runtime.CancelJobAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job == null)
            return Failure("JackONNX job was not found: " + jobId);

        return Success(new { job }, "JackONNX job cancellation requested.");
    }

    private async Task<LlmToolInvocationResult> InvokeImageAsync(LlmToolInvocationRequest invocation, CancellationToken cancellationToken)
    {
        JsonElement input = invocation.Input;
        var request = new ImageGenerationRequest
        {
            Prompt = ReadString(input, "prompt"),
            NegativePrompt = ReadString(input, "negativePrompt"),
            ModelId = FirstNonEmpty(ReadString(input, "modelId"), ReadString(input, "model")),
            Width = ReadInt(input, "width", 512),
            Height = ReadInt(input, "height", 512),
            Steps = ReadInt(input, "steps", 30),
            GuidanceScale = ReadDouble(input, "guidanceScale", ReadDouble(input, "guidance_scale", 7.5)),
            Seed = ReadNullableInt(input, "seed")
        };
        ApplySourceFields(request, input);
        ApplyInvocationContext(request, invocation);

        var result = await _images.GenerateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FromGenerationResult(result);
    }

    private async Task<LlmToolInvocationResult> InvokeAudioAsync(LlmToolInvocationRequest invocation, CancellationToken cancellationToken)
    {
        JsonElement input = invocation.Input;
        var request = new AudioGenerationRequest
        {
            Prompt = FirstNonEmpty(ReadString(input, "prompt"), ReadString(input, "text")),
            NegativePrompt = ReadString(input, "negativePrompt"),
            ModelId = FirstNonEmpty(ReadString(input, "modelId"), ReadString(input, "model")),
            Seconds = ReadDouble(input, "seconds", ReadDouble(input, "duration", 5)),
            SampleRate = ReadInt(input, "sampleRate", ReadInt(input, "sample_rate", 44100)),
            Seed = ReadNullableInt(input, "seed")
        };
        ApplySourceFields(request, input);
        ApplyInvocationContext(request, invocation);

        var result = await _audio.GenerateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FromGenerationResult(result);
    }

    private async Task<LlmToolInvocationResult> InvokeSpeechAsync(LlmToolInvocationRequest invocation, CancellationToken cancellationToken)
    {
        JsonElement input = invocation.Input;
        var request = new SpeechGenerationRequest
        {
            Text = ReadString(input, "text"),
            Voice = ReadString(input, "voice", "default"),
            ModelId = FirstNonEmpty(ReadString(input, "modelId"), ReadString(input, "model")),
            Speed = ReadDouble(input, "speed", 1.0),
            SampleRate = ReadInt(input, "sampleRate", 24000),
            Seed = ReadNullableInt(input, "seed")
        };
        ApplySourceFields(request, input);
        ApplyInvocationContext(request, invocation);

        var result = await _audio.GenerateSpeechAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FromGenerationResult(result);
    }

    private async Task<LlmToolInvocationResult> InvokeVideoAsync(LlmToolInvocationRequest invocation, CancellationToken cancellationToken)
    {
        JsonElement input = invocation.Input;
        var request = new VideoGenerationRequest
        {
            Prompt = ReadString(input, "prompt"),
            NegativePrompt = ReadString(input, "negativePrompt"),
            ModelId = FirstNonEmpty(ReadString(input, "modelId"), ReadString(input, "model")),
            Width = ReadInt(input, "width", 320),
            Height = ReadInt(input, "height", 192),
            Seconds = ReadDouble(input, "seconds", ReadDouble(input, "duration", 7)),
            Fps = ReadInt(input, "fps", 4),
            Frames = ReadNullableInt(input, "frames") ?? ReadNullableInt(input, "numFrames"),
            Steps = ReadInt(input, "steps", 16),
            GuidanceScale = ReadDouble(input, "guidanceScale", ReadDouble(input, "guidance_scale", 7.5)),
            Seed = ReadNullableInt(input, "seed")
        };
        ApplySourceFields(request, input);
        ApplyInvocationContext(request, invocation);

        var result = await _video.GenerateAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return FromGenerationResult(result);
    }

    private static void ApplyInvocationContext(JackOnnxGenerationRequest request, LlmToolInvocationRequest invocation)
    {
        if (request == null || invocation == null)
            return;

        if (!string.IsNullOrWhiteSpace(invocation.ToolCallId))
        {
            request.Context.LlmRuntimeToolCallId = invocation.ToolCallId.Trim();
            request.Context.Metadata["llm_runtime_tool_call_id"] = request.Context.LlmRuntimeToolCallId;
        }
    }

    private static void ApplySourceFields(JackOnnxGenerationRequest request, JsonElement input)
    {
        string deviceId = FirstNonEmpty(
            ReadString(input, "deviceId"),
            ReadString(input, "device_id"),
            ReadString(input, "cudaDevice"),
            ReadString(input, "cuda_device"));
        if (!string.IsNullOrWhiteSpace(deviceId))
        {
            request.Context.DeviceId = deviceId.Trim();
            request.Context.Metadata["device_id"] = request.Context.DeviceId;
        }

        AddContextMetadata(
            request,
            "preferred_provider",
            FirstNonEmpty(
                ReadString(input, "preferredProvider"),
                ReadString(input, "preferred_provider"),
                ReadString(input, "provider")));
        AddContextMetadata(
            request,
            "device_policy",
            FirstNonEmpty(
                ReadString(input, "devicePolicy"),
                ReadString(input, "device_policy")));
        AddContextMetadata(
            request,
            "device_map",
            ReadScalarString(input, "deviceMap", "device_map", "videoDeviceMap", "video_device_map"));
        AddContextMetadata(
            request,
            "allow_cpu_offload",
            ReadScalarString(input, "allowCpuOffload", "allow_cpu_offload", "videoAllowCpuOffload", "video_allow_cpu_offload"));
        AddContextMetadata(
            request,
            "offload_folder",
            ReadScalarString(input, "offloadFolder", "offload_folder", "videoOffloadFolder", "video_offload_folder"));
        AddContextMetadata(
            request,
            "disable_cuda_memory_guard",
            ReadScalarString(input, "disableCudaMemoryGuard", "disable_cuda_memory_guard", "videoDisableCudaMemoryGuard", "video_disable_cuda_memory_guard"));
        AddContextMetadata(
            request,
            "cuda_memory_reserve_mb",
            ReadScalarString(input, "cudaMemoryReserveMb", "cuda_memory_reserve_mb", "videoCudaMemoryReserveMb", "video_cuda_memory_reserve_mb"));
        AddContextMetadata(
            request,
            "cpu_max_memory",
            ReadScalarString(input, "cpuMaxMemory", "cpu_max_memory", "videoCpuMaxMemory", "video_cpu_max_memory"));
        AddContextMetadata(
            request,
            "video_memory_saving",
            ReadScalarString(input, "videoMemorySaving", "video_memory_saving", "memorySaving", "memory_saving"));
        AddContextMetadata(
            request,
            "memory_saving",
            ReadScalarString(input, "memorySaving", "memory_saving", "videoMemorySaving", "video_memory_saving"));
        request.SourcePath = FirstNonEmpty(
            ReadString(input, "sourcePath"),
            ReadString(input, "source_path"),
            ReadString(input, "sourceImagePath"),
            ReadString(input, "sourceVideoPath"),
            ReadString(input, "sourceAudioPath"),
            ReadString(input, "image"),
            ReadString(input, "video"),
            ReadString(input, "audio"),
            ReadString(input, "inputPath"),
            ReadString(input, "input_path"));
        request.SourceDataUrl = FirstNonEmpty(ReadString(input, "sourceDataUrl"), ReadString(input, "source_data_url"));
        request.SourceMediaType = FirstNonEmpty(ReadString(input, "sourceMediaType"), ReadString(input, "source_media_type"), ReadString(input, "mediaType"), ReadString(input, "media_type"));
        request.SourceName = FirstNonEmpty(ReadString(input, "sourceName"), ReadString(input, "source_name"), ReadString(input, "name"));
        request.SourceKind = FirstNonEmpty(ReadString(input, "sourceKind"), ReadString(input, "source_kind"), ReadString(input, "kind"));
        request.GenerationMode = FirstNonEmpty(ReadString(input, "generationMode"), ReadString(input, "generation_mode"), ReadString(input, "mode"));
    }

    private static void AddContextMetadata(JackOnnxGenerationRequest request, string key, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        request.Context.Metadata[key] = value.Trim();
    }

    private LlmToolInvocationResult FromGenerationResult(JackOnnxGenerationResult result)
    {
        return new LlmToolInvocationResult
        {
            Success = result.Success,
            ToolId = Definition.Id,
            OutputText = result.Message,
            OutputJson = JackOnnxLlmRuntimeToolRegistration.ToJsonElement(result),
            Error = result.Success ? "" : result.Message
        };
    }

    private LlmToolInvocationResult Success<T>(T value, string outputText)
    {
        return new LlmToolInvocationResult
        {
            Success = true,
            ToolId = Definition.Id,
            OutputText = outputText,
            OutputJson = JackOnnxLlmRuntimeToolRegistration.ToJsonElement(value)
        };
    }

    private LlmToolInvocationResult Failure(string error)
    {
        return new LlmToolInvocationResult
        {
            Success = false,
            ToolId = Definition.Id,
            Error = error,
            OutputText = error
        };
    }

    private static string ReadString(JsonElement input, string name, string defaultValue = "")
    {
        return input.ValueKind == JsonValueKind.Object &&
               input.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? defaultValue
            : defaultValue;
    }

    private static int ReadInt(JsonElement input, string name, int defaultValue)
    {
        return input.ValueKind == JsonValueKind.Object &&
               input.TryGetProperty(name, out var value) &&
               value.TryGetInt32(out int parsed)
            ? parsed
            : defaultValue;
    }

    private static int? ReadNullableInt(JsonElement input, string name)
    {
        return input.ValueKind == JsonValueKind.Object &&
               input.TryGetProperty(name, out var value) &&
               value.TryGetInt32(out int parsed)
            ? parsed
            : null;
    }

    private static double ReadDouble(JsonElement input, string name, double defaultValue)
    {
        return input.ValueKind == JsonValueKind.Object &&
               input.TryGetProperty(name, out var value) &&
               value.TryGetDouble(out double parsed)
            ? parsed
            : defaultValue;
    }

    private static string ReadScalarString(JsonElement input, params string[] names)
    {
        if (input.ValueKind != JsonValueKind.Object)
            return "";

        foreach (string name in names)
        {
            if (!input.TryGetProperty(name, out var value))
                continue;

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    string? text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text.Trim();
                    break;
                case JsonValueKind.True:
                    return "true";
                case JsonValueKind.False:
                    return "false";
                case JsonValueKind.Number:
                    return value.GetRawText();
            }
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }
}
