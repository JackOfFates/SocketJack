using JackONNX;
using JackONNX.Image;
using JackONNX.Runtime;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JackONNX.Audio;

public sealed class JackOnnxAudioPipeline
{
    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly IJackOnnxSpeechModelRunner _speechRunner;
    private readonly IJackOnnxAudioModelRunner _audioRunner;

    public JackOnnxAudioPipeline(
        JackOnnxRuntimeEngine runtime,
        IJackOnnxSpeechModelRunner? speechRunner = null,
        IJackOnnxAudioModelRunner? audioRunner = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        var pythonRunner = new JackOnnxPythonAudioRunner();
        _speechRunner = speechRunner ?? pythonRunner;
        _audioRunner = audioRunner ?? pythonRunner;
    }

    public async Task<JackOnnxGenerationResult> GenerateAsync(
        AudioGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt) && string.IsNullOrWhiteSpace(request.SourcePath) && string.IsNullOrWhiteSpace(request.SourceDataUrl))
            throw new ArgumentException("Audio prompt or source audio is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        var job = _runtime.CreateJob(JackOnnxMediaKind.Audio, request);
        using var jobCancellation = _runtime.CreateJobCancellationScope(job.Id, cancellationToken);
        cancellationToken = jobCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        request.Seconds = Math.Clamp(request.Seconds <= 0 ? 5 : request.Seconds, 0.1, 300);
        request.SampleRate = NormalizeSampleRate(request.SampleRate, 44100);
        Report(job.Id, JackOnnxJobState.LoadingModel, 5, "Resolving local audio generation model.", progress);

        var manifest = await ResolveAudioModelAsync(request, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CompleteFailure(
                job.Id,
                "No JackONNX audio model manifest is registered. Add a local audio generation or text-to-audio manifest under Models\\JackONNX or CompleteModels, then load that model in LlmRuntime.");
        }

        var missingComponents = GetMissingComponents(manifest);
        if (missingComponents.Count > 0)
        {
            return CompleteFailure(
                job.Id,
                "Audio model '" + manifest.Id + "' is registered, but model component files are missing: " + string.Join(", ", missingComponents.Take(5)) + ".");
        }

        if (!IsSupportedAudioManifest(manifest))
        {
            return CompleteFailure(
                job.Id,
                "Audio model '" + manifest.Id + "' uses format '" + manifest.Format + "', which is not executable by the current JackONNX audio runner. Supported local layouts are Transformers/PyTorch audio bundles, Diffusers AudioLDM-style bundles, and ONNX/Piper-style manifests.");
        }

        Report(job.Id, JackOnnxJobState.Running, 20, "Running local audio model '" + manifest.Id + "'.", progress);

        JackOnnxAudioModelOutput output;
        try
        {
            output = await _audioRunner.GenerateAsync(manifest, request, job.Id, _runtime.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CompleteFailure(job.Id, "Audio generation failed for model '" + manifest.Id + "': " + ex.Message);
        }

        return await SaveAudioArtifactAsync(job.Id, manifest, request, output, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JackOnnxGenerationResult> GenerateSpeechAsync(
        SpeechGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Speech text is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        var job = _runtime.CreateJob(JackOnnxMediaKind.Audio, request);
        using var jobCancellation = _runtime.CreateJobCancellationScope(job.Id, cancellationToken);
        cancellationToken = jobCancellation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        request.Speed = Math.Clamp(request.Speed <= 0 ? 1.0 : request.Speed, 0.25, 4.0);
        request.SampleRate = NormalizeSampleRate(request.SampleRate, 24000);
        Report(job.Id, JackOnnxJobState.LoadingModel, 5, "Resolving local speech generation model.", progress);

        var manifest = await ResolveSpeechModelAsync(request, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CompleteFailure(
                job.Id,
                "No JackONNX speech-capable audio model manifest is registered. Add a local text-to-speech, Piper, VITS, Bark, or Transformers TTS manifest under Models\\JackONNX or CompleteModels, then load that model in LlmRuntime.");
        }

        var missingComponents = GetMissingComponents(manifest);
        if (missingComponents.Count > 0)
        {
            return CompleteFailure(
                job.Id,
                "Speech model '" + manifest.Id + "' is registered, but model component files are missing: " + string.Join(", ", missingComponents.Take(5)) + ".");
        }

        if (!IsSupportedAudioManifest(manifest))
        {
            return CompleteFailure(
                job.Id,
                "Speech model '" + manifest.Id + "' uses format '" + manifest.Format + "', which is not executable by the current JackONNX speech runner. Supported local layouts are Transformers/PyTorch TTS bundles and ONNX/Piper-style manifests.");
        }

        Report(job.Id, JackOnnxJobState.Running, 20, "Running local speech model '" + manifest.Id + "'.", progress);

        JackOnnxAudioModelOutput output;
        try
        {
            output = await _speechRunner.GenerateSpeechAsync(manifest, request, job.Id, _runtime.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CompleteFailure(job.Id, "Speech generation failed for model '" + manifest.Id + "': " + ex.Message);
        }

        return await SaveAudioArtifactAsync(job.Id, manifest, request, output, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JackOnnxGenerationResult> SaveAudioArtifactAsync(
        string jobId,
        JackOnnxModelManifest manifest,
        JackOnnxGenerationRequest request,
        JackOnnxAudioModelOutput output,
        IProgress<JackOnnxProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (output.Data.Length == 0)
            return CompleteFailure(jobId, string.IsNullOrWhiteSpace(output.Message) ? "Audio generation returned no audio data." : output.Message);

        string fileName = string.IsNullOrWhiteSpace(output.FileName) ? "audio_" + jobId + ".wav" : output.FileName;
        string mediaType = string.IsNullOrWhiteSpace(output.MediaType) ? "audio/wav" : output.MediaType;
        if (!TryValidateAudioOutput(output.Data, fileName, mediaType, out string validationError))
            return CompleteFailure(jobId, validationError);

        Report(jobId, JackOnnxJobState.Encoding, 95, "Saving generated audio artifact.", progress);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["modelId"] = manifest.Id,
            ["runner"] = output.Runner,
            ["sampleRate"] = output.SampleRate.ToString(CultureInfo.InvariantCulture)
        };

        if (request is SpeechGenerationRequest speech)
        {
            metadata["text"] = speech.Text;
            metadata["voice"] = speech.Voice;
            metadata["speed"] = speech.Speed.ToString("0.###", CultureInfo.InvariantCulture);
        }
        else if (request is AudioGenerationRequest audio)
        {
            metadata["prompt"] = audio.Prompt;
            metadata["seconds"] = audio.Seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }

        if (request.Seed.HasValue)
            metadata["seed"] = request.Seed.Value.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(request.SourcePath))
            metadata["sourcePath"] = request.SourcePath;
        if (!string.IsNullOrWhiteSpace(request.SourceKind))
            metadata["sourceKind"] = request.SourceKind;
        if (!string.IsNullOrWhiteSpace(request.GenerationMode))
            metadata["generationMode"] = request.GenerationMode;
        foreach (var pair in output.Metadata)
            metadata[pair.Key] = pair.Value;

        var artifact = await _runtime.SaveArtifactAsync(jobId, JackOnnxMediaKind.Audio, fileName, mediaType, output.Data, metadata, cancellationToken).ConfigureAwait(false);
        var result = new JackOnnxGenerationResult
        {
            JobId = jobId,
            Success = true,
            Message = string.IsNullOrWhiteSpace(output.Message) ? "Audio generated successfully." : output.Message,
            Artifacts = [artifact]
        };
        _runtime.CompleteJob(jobId, result);
        return result;
    }

    private async Task<JackOnnxModelManifest?> ResolveSpeechModelAsync(SpeechGenerationRequest request, CancellationToken cancellationToken)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            string requested = request.ModelId.Trim();
            return models.FirstOrDefault(model =>
                IsAudioModel(model) &&
                ModelMatchesRequest(model, requested));
        }

        if (!string.IsNullOrWhiteSpace(request.Voice) && !request.Voice.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            string voice = request.Voice.Trim();
            var voiceModel = models.FirstOrDefault(model => IsAudioModel(model) && ModelMatchesRequest(model, voice));
            if (voiceModel != null)
                return voiceModel;
        }

        return models
            .Where(IsAudioModel)
            .Where(IsSpeechModel)
            .FirstOrDefault();
    }

    private async Task<JackOnnxModelManifest?> ResolveAudioModelAsync(AudioGenerationRequest request, CancellationToken cancellationToken)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            string requested = request.ModelId.Trim();
            return models.FirstOrDefault(model =>
                IsAudioModel(model) &&
                ModelMatchesRequest(model, requested));
        }

        return models.FirstOrDefault(IsAudioModel);
    }

    private JackOnnxGenerationResult CompleteFailure(string jobId, string message)
    {
        var result = new JackOnnxGenerationResult
        {
            JobId = jobId,
            Success = false,
            Message = message
        };
        _runtime.CompleteJob(jobId, result);
        return result;
    }

    private void Report(string jobId, JackOnnxJobState state, double percent, string message, IProgress<JackOnnxProgress>? progress)
    {
        var current = new JackOnnxProgress
        {
            JobId = jobId,
            State = state,
            Percent = percent,
            Message = message
        };
        progress?.Report(current);
        _runtime.UpdateJobProgress(jobId, current);
    }

    private static bool IsAudioModel(JackOnnxModelManifest model)
    {
        if (model == null)
            return false;

        string type = model.Type ?? "";
        if (type.StartsWith("audio", StringComparison.OrdinalIgnoreCase) ||
            type.StartsWith("speech", StringComparison.OrdinalIgnoreCase))
            return true;

        string text = BuildManifestSignal(model);
        return ContainsAny(text, "audio", "speech", "text-to-speech", "tts", "bark", "vits", "piper", "musicgen", "audioldm", "moshi", "moshiko");
    }

    private static bool IsSpeechModel(JackOnnxModelManifest model)
    {
        string text = BuildManifestSignal(model);
        return ContainsAny(text, "text-to-speech", "text_to_speech", "tts", "speech", "vits", "piper", "bark", "speecht5");
    }

    private static bool IsSupportedAudioManifest(JackOnnxModelManifest model)
    {
        string format = (model.Format ?? "").Trim();
        if (string.Equals(format, "onnx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "torch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "diffusers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "safetensors", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(ReadMetadata(model, "layout"), "complete-model", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModelMatchesRequest(JackOnnxModelManifest model, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return false;

        foreach (string candidate in BuildModelMatchCandidates(model))
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(candidate), Path.GetFileName(requested), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(candidate), Path.GetFileNameWithoutExtension(requested), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> BuildModelMatchCandidates(JackOnnxModelManifest model)
    {
        yield return model.Id;
        yield return model.Name;
        yield return model.Type;
        yield return Path.GetFileNameWithoutExtension(model.ManifestPath);
        yield return Path.GetFileName(model.ManifestPath);
        foreach (var pair in model.Components)
        {
            yield return pair.Key;
            yield return pair.Value;
        }
        foreach (var pair in model.Metadata)
        {
            yield return pair.Key;
            yield return pair.Value;
        }
    }

    private static IReadOnlyList<string> GetMissingComponents(JackOnnxModelManifest manifest)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? Environment.CurrentDirectory;
        var missing = new List<string>();
        foreach (var pair in manifest.Components)
        {
            string relative = (pair.Value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            string fullPath = Path.GetFullPath(Path.Combine(manifestDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
            bool exists = relative.EndsWith("/", StringComparison.Ordinal) || relative.EndsWith("\\", StringComparison.Ordinal)
                ? Directory.Exists(fullPath)
                : File.Exists(fullPath) || Directory.Exists(fullPath);
            if (!exists)
                missing.Add(pair.Key + "=" + relative);
        }

        return missing;
    }

    private static bool TryValidateAudioOutput(byte[] data, string fileName, string mediaType, out string error)
    {
        error = "";
        data ??= Array.Empty<byte>();
        long length = data.LongLength;
        string extension = Path.GetExtension(fileName ?? "");
        string normalizedMediaType = (mediaType ?? "").Trim().ToLowerInvariant();
        bool isWav = normalizedMediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) ||
                     normalizedMediaType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".wav", StringComparison.OrdinalIgnoreCase);

        if (isWav)
        {
            if (length < 44)
            {
                error = "Audio generation completed, but the generated WAV artifact is too small to be playable (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).";
                return false;
            }

            if (!ContainsAsciiMarker(data, "RIFF", 4) || !ContainsAsciiMarker(data.Skip(8).Take(4).ToArray(), "WAVE", 4))
            {
                error = "Audio generation completed, but the generated WAV artifact does not contain a WAV file signature.";
                return false;
            }
        }

        if (normalizedMediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) && length == 0)
        {
            error = "Audio generation completed, but the generated audio artifact is empty.";
            return false;
        }

        return true;
    }

    private static bool ContainsAsciiMarker(byte[] data, string marker, int searchLength)
    {
        if (data == null || string.IsNullOrEmpty(marker))
            return false;

        int limit = Math.Min(data.Length, Math.Max(marker.Length, searchLength));
        for (int i = 0; i <= limit - marker.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < marker.Length; j++)
            {
                if (data[i + j] != (byte)marker[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return true;
        }

        return false;
    }

    private static int NormalizeSampleRate(int value, int fallback)
    {
        return Math.Clamp(value <= 0 ? fallback : value, 8000, 192000);
    }

    private static string BuildManifestSignal(JackOnnxModelManifest model)
    {
        return string.Join(" ", new[]
        {
            model.Id,
            model.Name,
            model.Type,
            model.Format,
            model.ManifestPath,
            string.Join(" ", model.Components.Select(pair => pair.Key + " " + pair.Value)),
            string.Join(" ", model.Metadata.Select(pair => pair.Key + " " + pair.Value))
        });
    }

    private static string ReadMetadata(JackOnnxModelManifest model, string key)
    {
        return model.Metadata.TryGetValue(key, out string? value) ? value : "";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

public interface IJackOnnxSpeechModelRunner
{
    Task<JackOnnxAudioModelOutput> GenerateSpeechAsync(
        JackOnnxModelManifest manifest,
        SpeechGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default);
}

public interface IJackOnnxAudioModelRunner
{
    Task<JackOnnxAudioModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        AudioGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class JackOnnxAudioModelOutput
{
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public string FileName { get; init; } = "";

    public string MediaType { get; init; } = "audio/wav";

    public string Message { get; init; } = "";

    public string Runner { get; init; } = "";

    public int SampleRate { get; init; } = 24000;

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class JackOnnxPythonAudioRunner : IJackOnnxSpeechModelRunner, IJackOnnxAudioModelRunner
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public Task<JackOnnxAudioModelOutput> GenerateSpeechAsync(
        JackOnnxModelManifest manifest,
        SpeechGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default)
    {
        return GenerateAsync(manifest, request, jobId, options, "speech", cancellationToken);
    }

    public Task<JackOnnxAudioModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        AudioGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default)
    {
        return GenerateAsync(manifest, request, jobId, options, "audio", cancellationToken);
    }

    private static async Task<JackOnnxAudioModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        JackOnnxGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        string mode,
        CancellationToken cancellationToken)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));

        options ??= new JackOnnxOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "jackonnx-audio-runner", jobId);
        Directory.CreateDirectory(tempRoot);
        string requestPath = Path.Combine(tempRoot, "request.json");
        string resultPath = Path.Combine(tempRoot, "result.json");
        string scriptPath = Path.Combine(tempRoot, "runner.py");
        string outputPath = Path.Combine(tempRoot, mode.Equals("speech", StringComparison.OrdinalIgnoreCase) ? "speech.wav" : "audio.wav");

        try
        {
            await File.WriteAllTextAsync(scriptPath, PythonRunnerScript, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var run = await RunFirstAvailablePythonAsync(
                manifest,
                request,
                options,
                mode,
                scriptPath,
                requestPath,
                resultPath,
                outputPath,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException("Python audio runner did not write a result file. " + BuildProcessDetail(run));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            bool success = ReadBool(root, "success");
            string message = ReadString(root, "message");
            string detail = ReadString(root, "detail");
            string backend = ReadString(root, "backend");
            string mediaType = ReadString(root, "media_type");
            string audioPath = ReadString(root, "output_path");
            int sampleRate = ReadInt(root, "sample_rate", request is SpeechGenerationRequest speech ? speech.SampleRate : ((AudioGenerationRequest)request).SampleRate);
            if (string.IsNullOrWhiteSpace(audioPath))
                audioPath = outputPath;

            if (!success)
            {
                string failure = FirstNonEmpty(message, "Python audio runner failed.");
                if (!string.IsNullOrWhiteSpace(detail))
                    failure += " " + TrimPythonTraceback(detail);
                throw new InvalidOperationException(failure);
            }

            if (run.ExitCode != 0)
                throw new InvalidOperationException("Python audio runner exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + BuildProcessDetail(run));

            if (!File.Exists(audioPath))
                throw new InvalidOperationException("Python audio runner reported success, but the audio file was not found: " + audioPath);

            byte[] data = await File.ReadAllBytesAsync(audioPath, cancellationToken).ConfigureAwait(false);
            string suffix = mode.Equals("speech", StringComparison.OrdinalIgnoreCase) ? "speech_" : "audio_";
            return new JackOnnxAudioModelOutput
            {
                Data = data,
                FileName = suffix + jobId + ".wav",
                MediaType = FirstNonEmpty(mediaType, "audio/wav"),
                Message = FirstNonEmpty(message, mode.Equals("speech", StringComparison.OrdinalIgnoreCase) ? "Speech generated successfully." : "Audio generated successfully."),
                Runner = FirstNonEmpty(backend, "python.audio"),
                SampleRate = sampleRate,
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backend"] = FirstNonEmpty(backend, "python.audio"),
                    ["manifestPath"] = manifest.ManifestPath,
                    ["encodedPath"] = audioPath
                }
            };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static Dictionary<string, object?> BuildPayload(
        JackOnnxModelManifest manifest,
        JackOnnxGenerationRequest request,
        JackOnnxOptions options,
        string mode,
        string outputPath)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? Environment.CurrentDirectory;
        var payload = new Dictionary<string, object?>
        {
            ["manifest_id"] = manifest.Id,
            ["manifest_name"] = manifest.Name,
            ["manifest_path"] = manifest.ManifestPath,
            ["model_dir"] = manifestDirectory,
            ["format"] = manifest.Format,
            ["components"] = manifest.Components,
            ["metadata"] = manifest.Metadata,
            ["mode"] = mode,
            ["preferred_provider"] = FirstNonEmpty(ReadContextMetadata(request.Context, "preferred_provider", "preferredProvider", "provider"), options.PreferredProvider.ToString()),
            ["device_policy"] = FirstNonEmpty(ReadContextMetadata(request.Context, "device_policy", "devicePolicy"), options.DevicePolicy.ToString()),
            ["device_id"] = ResolveRequestedDeviceId(request.Context),
            ["cuda_device"] = ResolveRequestedDeviceId(request.Context),
            ["allow_package_install"] = options.AllowPythonPackageInstall,
            ["source_path"] = request.SourcePath,
            ["source_data_url"] = request.SourceDataUrl,
            ["source_media_type"] = request.SourceMediaType,
            ["source_name"] = request.SourceName,
            ["source_kind"] = request.SourceKind,
            ["generation_mode"] = request.GenerationMode,
            ["seed"] = request.Seed,
            ["output_path"] = outputPath
        };

        if (request is SpeechGenerationRequest speech)
        {
            payload["text"] = speech.Text;
            payload["voice"] = speech.Voice;
            payload["speed"] = speech.Speed;
            payload["sample_rate"] = speech.SampleRate;
        }
        else if (request is AudioGenerationRequest audio)
        {
            payload["prompt"] = audio.Prompt;
            payload["negative_prompt"] = audio.NegativePrompt;
            payload["seconds"] = audio.Seconds;
            payload["sample_rate"] = audio.SampleRate;
        }

        return payload;
    }

    private static string ResolveRequestedDeviceId(JackOnnxGenerationContext? context)
    {
        if (!string.IsNullOrWhiteSpace(context?.DeviceId))
            return context.DeviceId.Trim();
        if (context?.Metadata != null)
        {
            foreach (string key in new[] { "device_id", "deviceId", "cuda_device", "cudaDevice" })
            {
                if (context.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        return "";
    }

    private static string ReadContextMetadata(JackOnnxGenerationContext? context, params string[] keys)
    {
        if (context?.Metadata == null)
            return "";

        foreach (string key in keys)
        {
            if (context.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static async Task<ProcessRunResult> RunFirstAvailablePythonAsync(
        JackOnnxModelManifest manifest,
        JackOnnxGenerationRequest request,
        JackOnnxOptions options,
        string mode,
        string scriptPath,
        string requestPath,
        string resultPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startErrors = new List<string>();
        bool foundPythonRuntime = false;

        foreach (var command in ResolvePythonCommands(options))
        {
            try
            {
                TryDeleteFile(resultPath);
                var payload = BuildPayload(manifest, request, options, mode, outputPath);
                await File.WriteAllTextAsync(
                    requestPath,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Utf8NoBom,
                    cancellationToken).ConfigureAwait(false);

                var run = await RunPythonAsync(
                    command,
                    scriptPath,
                    requestPath,
                    resultPath,
                    options.ImageGenerationTimeout,
                    cancellationToken).ConfigureAwait(false);
                foundPythonRuntime = true;
                if (PythonRunnerSucceeded(resultPath, run))
                    return run;

                string detail = BuildPythonRunFailureDetail(run);
                startErrors.Add(command.FileName + " exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + detail);
            }
            catch (Win32Exception ex)
            {
                startErrors.Add(command.FileName + ": " + ex.Message);
            }
        }

        string finalDetail = string.Join(" ", startErrors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (foundPythonRuntime)
        {
            throw new InvalidOperationException(
                "No compatible Python audio generation runtime completed the request. Python was found, but the local audio pipeline failed. " +
                "Set JACKONNX_AUDIO_PYTHON, JACKONNX_PYTHON, or JackOnnxOptions.ImageGenerationPythonExecutable to force a different runtime. " +
                finalDetail);
        }

        throw new InvalidOperationException(
            "No usable Python executable was found for local audio generation. Set JACKONNX_AUDIO_PYTHON or JACKONNX_PYTHON to a Python environment with torch, transformers, and diffusers. " +
            finalDetail);
    }

    private static IReadOnlyList<PythonCommand> ResolvePythonCommands(JackOnnxOptions options)
    {
        var commands = new List<PythonCommand>();
        string? explicitAudioPython = Environment.GetEnvironmentVariable("JACKONNX_AUDIO_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitAudioPython))
        {
            AddPythonCommand(commands, explicitAudioPython, requireExistingFile: true);
            return commands;
        }

        string? explicitPython = Environment.GetEnvironmentVariable("JACKONNX_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython))
        {
            AddPythonCommand(commands, explicitPython, requireExistingFile: true);
            return commands;
        }

        if (!string.IsNullOrWhiteSpace(options.ImageGenerationPythonExecutable))
            AddPythonCommand(commands, options.ImageGenerationPythonExecutable.Trim(), requireExistingFile: !IsCommandName(options.ImageGenerationPythonExecutable));

        AddPythonCommand(commands, JackOnnxPythonDiffusersImageRunner.DefaultCudaLegacyPythonExecutable, requireExistingFile: true);
        AddPythonCommand(commands, JackOnnxPythonDiffusersImageRunner.DefaultBundledPythonExecutable, requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "python.exe"), requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "bin", "python3"), requireExistingFile: true);
        AddPythonCommand(commands, "python", requireExistingFile: false);
        AddPythonCommand(commands, "python3", requireExistingFile: false);
        if (OperatingSystem.IsWindows())
            AddPythonCommand(commands, "py", ["-3"], requireExistingFile: false);

        return commands;
    }

    private static void AddPythonCommand(
        List<PythonCommand> commands,
        string? fileName,
        IReadOnlyList<string>? arguments = null,
        bool requireExistingFile = false)
    {
        fileName = (fileName ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        if (requireExistingFile && !File.Exists(fileName))
            return;

        arguments ??= [];
        bool exists = commands.Any(command =>
            string.Equals(command.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
            command.Arguments.SequenceEqual(arguments, StringComparer.OrdinalIgnoreCase));
        if (!exists)
            commands.Add(new PythonCommand(fileName, arguments));
    }

    private static bool IsCommandName(string fileName)
    {
        fileName = (fileName ?? "").Trim();
        return !fileName.Contains(Path.DirectorySeparatorChar) &&
               !fileName.Contains(Path.AltDirectorySeparatorChar) &&
               !Path.IsPathRooted(fileName);
    }

    private static async Task<ProcessRunResult> RunPythonAsync(
        PythonCommand command,
        string scriptPath,
        string requestPath,
        string resultPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(resultPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromHours(1) : timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Python audio generation exceeded the configured timeout of " + timeout + ".");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            resultPath);
    }

    private static string BuildProcessDetail(ProcessRunResult run)
    {
        string detail = FirstNonEmpty(run.StandardError, run.StandardOutput);
        return string.IsNullOrWhiteSpace(detail) ? "" : detail.Trim();
    }

    private static bool PythonRunnerSucceeded(string resultPath, ProcessRunResult run)
    {
        if (TryReadPythonRunnerResult(resultPath, out bool success, out _))
            return success;

        return run.ExitCode == 0;
    }

    private static string BuildPythonRunFailureDetail(ProcessRunResult run)
    {
        if (TryReadPythonRunnerResult(run.ResultPath, out bool success, out string detail) && !success)
            return detail;

        return BuildProcessDetail(run);
    }

    private static bool TryReadPythonRunnerResult(string resultPath, out bool success, out string detail)
    {
        success = false;
        detail = "";

        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement root = document.RootElement;
            success = ReadBool(root, "success");
            string message = ReadString(root, "message");
            string stack = ReadString(root, "detail");
            detail = string.Join(" ", new[] { message, stack }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static int ReadInt(JsonElement root, string name, int defaultValue)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out JsonElement value) &&
               value.TryGetInt32(out int parsed)
            ? parsed
            : defaultValue;
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static string TrimPythonTraceback(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";

        int index = detail.IndexOf("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase);
        if (index > 0)
            detail = detail[..index];
        return detail.Trim();
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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private sealed record PythonCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, string ResultPath);

    private const string PythonRunnerScript = """
import json
import os
import subprocess
import sys
import traceback
import wave


def write_result(path, value):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(value, handle, ensure_ascii=False, indent=2)


def install_python_package(package):
    completed = subprocess.run(
        [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", package],
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        timeout=900,
    )
    if completed.returncode != 0:
        raise RuntimeError((completed.stderr or completed.stdout or "").strip())


def import_or_install(module_name, package_name, allow_install):
    try:
        return __import__(module_name)
    except Exception:
        if not allow_install:
            raise
        install_python_package(package_name)
        return __import__(module_name)


def select_torch_device(payload, torch):
    provider = str(payload.get("preferred_provider") or "").strip().lower()
    device_id = str(payload.get("cuda_device") or payload.get("device_id") or "").strip().lower()
    wants_cuda = provider in ("cuda", "jackonnxexecutionprovider.cuda") or device_id.startswith("cuda") or device_id.isdigit()
    if wants_cuda:
        if not torch.cuda.is_available():
            raise RuntimeError("CUDA audio generation was requested, but this Python runtime does not report a CUDA-capable torch build.")
        if device_id.startswith("cuda:"):
            return device_id
        if device_id.isdigit():
            return "cuda:" + device_id
        return "cuda"
    if torch.cuda.is_available() and provider not in ("cpu", "jackonnxexecutionprovider.cpu"):
        return "cuda"
    return "cpu"


def pipeline_device_id(device):
    device = str(device or "")
    if device.startswith("cuda"):
        if ":" in device:
            try:
                return int(device.split(":", 1)[1])
            except Exception:
                return 0
        return 0
    return -1


def to_mono_float_array(audio):
    import numpy as np

    if hasattr(audio, "detach"):
        audio = audio.detach().cpu().numpy()
    arr = np.asarray(audio, dtype="float32")
    if arr.ndim == 0:
        arr = arr.reshape(1)
    if arr.ndim > 1:
        if arr.shape[0] in (1, 2) and arr.shape[-1] not in (1, 2):
            arr = arr.T
        arr = arr.reshape(arr.shape[0], -1).mean(axis=1)
    if arr.size == 0:
        raise RuntimeError("The audio pipeline returned an empty waveform.")
    max_abs = float(np.max(np.abs(arr))) if arr.size else 0.0
    if max_abs > 1.0:
        arr = arr / max_abs
    return np.clip(arr, -1.0, 1.0)


def write_wav(path, audio, sample_rate):
    import numpy as np

    os.makedirs(os.path.dirname(path), exist_ok=True)
    arr = to_mono_float_array(audio)
    pcm = (arr * 32767.0).astype("<i2")
    with wave.open(path, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(int(sample_rate or 24000))
        handle.writeframes(pcm.tobytes())


def read_pipeline_audio(result):
    if isinstance(result, dict):
        audio = result.get("audio")
        sample_rate = result.get("sampling_rate") or result.get("sample_rate")
        if audio is not None:
            return audio, sample_rate
    if isinstance(result, (list, tuple)) and result:
        return read_pipeline_audio(result[0])
    if hasattr(result, "audios"):
        audios = getattr(result, "audios")
        if audios is not None and len(audios) > 0:
            return audios[0], None
    raise RuntimeError("The audio pipeline returned no recognizable waveform payload.")


def model_signal(payload):
    parts = [
        payload.get("manifest_id") or "",
        payload.get("manifest_name") or "",
        payload.get("model_dir") or "",
        payload.get("format") or "",
        json.dumps(payload.get("metadata") or {}),
        json.dumps(payload.get("components") or {}),
    ]
    return " ".join(str(part) for part in parts).lower()


def unsupported_model_note(payload):
    signal = model_signal(payload)
    if "moshi" in signal or "moshiko" in signal:
        return " This is a Moshi/Moshiko speech-dialogue bundle; it needs a dedicated streaming Moshi adapter rather than the standard Transformers text-to-speech or Diffusers audio pipelines."
    return ""


def resolve_component_paths(payload):
    model_dir = os.path.abspath(payload.get("model_dir") or ".")
    components = payload.get("components") or {}
    paths = []
    if isinstance(components, dict):
        for value in components.values():
            if not value:
                continue
            candidate = str(value)
            if not os.path.isabs(candidate):
                candidate = os.path.join(model_dir, candidate.replace("/", os.sep))
            paths.append(candidate)
    return paths


def find_first_component(payload, suffixes, tokens=()):
    suffixes = tuple(s.lower() for s in suffixes)
    tokens = tuple(t.lower() for t in tokens)
    candidates = resolve_component_paths(payload)
    model_dir = os.path.abspath(payload.get("model_dir") or ".")
    try:
        for root, _, files in os.walk(model_dir):
            for name in files:
                candidates.append(os.path.join(root, name))
    except Exception:
        pass
    for path in candidates:
        lower = path.lower().replace("\\", "/")
        if suffixes and not lower.endswith(suffixes):
            continue
        if tokens and not any(token in lower for token in tokens):
            continue
        if os.path.exists(path):
            return path
    return ""


def run_piper_speech(payload):
    signal = model_signal(payload)
    model_path = find_first_component(payload, (".onnx",), ("piper", "vits", ".onnx"))
    if not model_path:
        raise RuntimeError("No Piper/VITS ONNX model file was found in the manifest components.")
    if "piper" not in signal and "vits" not in signal and not model_path.lower().endswith(".onnx"):
        raise RuntimeError("This manifest does not look like a Piper/VITS ONNX speech model.")

    allow_install = bool(payload.get("allow_package_install"))
    import_or_install("piper", "piper-tts", allow_install)
    from piper.voice import PiperVoice

    config_path = find_first_component(payload, (".json",), ("config", "piper", os.path.basename(model_path).lower() + ".json"))
    text = str(payload.get("text") or "").strip()
    if not text:
        raise RuntimeError("Speech text is required.")
    kwargs = {}
    if config_path:
        kwargs["config_path"] = config_path
    try:
        voice = PiperVoice.load(model_path, **kwargs)
    except TypeError:
        voice = PiperVoice.load(model_path, config_path) if config_path else PiperVoice.load(model_path)
    os.makedirs(os.path.dirname(payload["output_path"]), exist_ok=True)
    with wave.open(payload["output_path"], "wb") as wav_file:
        voice.synthesize(text, wav_file)
    sample_rate = int(getattr(getattr(voice, "config", None), "sample_rate", None) or payload.get("sample_rate") or 22050)
    return {
        "backend": "python.piper.onnx",
        "sample_rate": sample_rate,
    }


def run_transformers_speech(payload):
    allow_install = bool(payload.get("allow_package_install"))
    import_or_install("numpy", "numpy", allow_install)
    import_or_install("transformers", "transformers", allow_install)
    torch = import_or_install("torch", "torch", allow_install)
    from transformers import pipeline

    device = select_torch_device(payload, torch)
    text = str(payload.get("text") or "").strip()
    if not text:
        raise RuntimeError("Speech text is required.")
    model_dir = payload["model_dir"]
    pipe = pipeline("text-to-speech", model=model_dir, local_files_only=True, device=pipeline_device_id(device))
    kwargs = {}
    voice = str(payload.get("voice") or "").strip()
    if voice and voice.lower() != "default":
        kwargs["speaker"] = voice
    result = pipe(text, **kwargs)
    audio, sample_rate = read_pipeline_audio(result)
    sample_rate = int(sample_rate or payload.get("sample_rate") or 24000)
    write_wav(payload["output_path"], audio, sample_rate)
    return {
        "backend": "python.transformers:text-to-speech:" + str(device),
        "sample_rate": sample_rate,
    }


def run_diffusers_audio(payload):
    allow_install = bool(payload.get("allow_package_install"))
    import_or_install("numpy", "numpy", allow_install)
    import_or_install("diffusers", "diffusers", allow_install)
    torch = import_or_install("torch", "torch", allow_install)
    import diffusers

    model_dir = payload["model_dir"]
    device = select_torch_device(payload, torch)
    pipeline_class = None
    for name in ("AudioLDM2Pipeline", "AudioLDMPipeline", "StableAudioPipeline"):
        pipeline_class = getattr(diffusers, name, None)
        if pipeline_class is not None:
            break
    if pipeline_class is None:
        raise RuntimeError("Diffusers audio generation is unavailable because no AudioLDM/StableAudio pipeline class is installed.")

    dtype = torch.float16 if str(device).startswith("cuda") else torch.float32
    try:
        pipe = pipeline_class.from_pretrained(model_dir, torch_dtype=dtype, local_files_only=True)
    except TypeError:
        pipe = pipeline_class.from_pretrained(model_dir, local_files_only=True)
    if hasattr(pipe, "to"):
        pipe = pipe.to(device)
    kwargs = {
        "prompt": str(payload.get("prompt") or payload.get("text") or ""),
        "num_inference_steps": int(payload.get("steps") or 25),
    }
    seconds = float(payload.get("seconds") or 5)
    kwargs["audio_length_in_s"] = seconds
    negative = str(payload.get("negative_prompt") or "").strip()
    if negative:
        kwargs["negative_prompt"] = negative
    result = pipe(**kwargs)
    audio, sample_rate = read_pipeline_audio(result)
    sample_rate = int(sample_rate or payload.get("sample_rate") or 44100)
    write_wav(payload["output_path"], audio, sample_rate)
    return {
        "backend": "python.diffusers:" + pipeline_class.__name__ + ":" + str(device),
        "sample_rate": sample_rate,
    }


def run_transformers_audio(payload):
    allow_install = bool(payload.get("allow_package_install"))
    import_or_install("numpy", "numpy", allow_install)
    import_or_install("transformers", "transformers", allow_install)
    torch = import_or_install("torch", "torch", allow_install)
    from transformers import pipeline

    device = select_torch_device(payload, torch)
    prompt = str(payload.get("prompt") or payload.get("text") or "").strip()
    if not prompt:
        raise RuntimeError("Audio prompt is required.")
    pipe = pipeline("text-to-audio", model=payload["model_dir"], local_files_only=True, device=pipeline_device_id(device))
    result = pipe(prompt)
    audio, sample_rate = read_pipeline_audio(result)
    sample_rate = int(sample_rate or payload.get("sample_rate") or 44100)
    write_wav(payload["output_path"], audio, sample_rate)
    return {
        "backend": "python.transformers:text-to-audio:" + str(device),
        "sample_rate": sample_rate,
    }


def run_audio(payload):
    failures = []
    for runner in (run_diffusers_audio, run_transformers_audio):
        try:
            return runner(payload)
        except Exception as exc:
            failures.append(str(exc))
    raise RuntimeError("No local audio runner could open this model. " + " | ".join(failures) + unsupported_model_note(payload))


def run_speech(payload):
    failures = []
    for runner in (run_piper_speech, run_transformers_speech):
        try:
            return runner(payload)
        except Exception as exc:
            failures.append(str(exc))
    raise RuntimeError("No local speech runner could open this model as text-to-speech. " + " | ".join(failures) + unsupported_model_note(payload))


def main():
    request_path = sys.argv[1]
    result_path = sys.argv[2]
    try:
        with open(request_path, "r", encoding="utf-8-sig") as handle:
            payload = json.load(handle)
        mode = str(payload.get("mode") or "speech").strip().lower()
        output = run_audio(payload) if mode == "audio" else run_speech(payload)
        write_result(result_path, {
            "success": True,
            "message": "Audio generated successfully." if mode == "audio" else "Speech generated successfully.",
            "backend": output.get("backend") or "python.audio",
            "media_type": "audio/wav",
            "sample_rate": int(output.get("sample_rate") or payload.get("sample_rate") or 24000),
            "output_path": payload["output_path"],
        })
    except Exception as exc:
        write_result(result_path, {
            "success": False,
            "message": str(exc),
            "detail": traceback.format_exc(limit=8),
        })
        sys.exit(2)


if __name__ == "__main__":
    main()
""";
}
