using JackONNX;
using JackONNX.Runtime;
using System.Globalization;

namespace JackONNX.Video;

public sealed class JackOnnxVideoPipeline
{
    private const int MaxVideoFrameCount = 96;

    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly IJackOnnxVideoModelRunner _runner;

    public JackOnnxVideoPipeline(JackOnnxRuntimeEngine runtime, IJackOnnxVideoModelRunner? runner = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runner = runner ?? new JackOnnxPythonDiffusersVideoRunner();
    }

    public async Task<JackOnnxGenerationResult> GenerateAsync(
        VideoGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Video prompt is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        request.Width = NormalizeDimension(request.Width, 320, 1024);
        request.Height = NormalizeDimension(request.Height, 192, 576);
        request.Fps = Math.Clamp(request.Fps <= 0 ? 4 : request.Fps, 1, 24);
        request.Seconds = Math.Clamp(request.Seconds <= 0 ? 7 : request.Seconds, 1, 30);
        request.Steps = Math.Clamp(request.Steps <= 0 ? 16 : request.Steps, 12, 80);
        request.GuidanceScale = Math.Clamp(request.GuidanceScale <= 0 ? 7.5 : request.GuidanceScale, 0.1, 30);
        double requestedSeconds = request.Seconds;
        int requestedFps = request.Fps;
        int requestedFrameCount = ResolveRequestedFrameCount(request);
        request.Frames = NormalizeFrameCount(requestedFrameCount);
        request.Seconds = NormalizeEffectiveSeconds(request.Frames.Value, request.Fps);
        string timingAdjustment = BuildTimingAdjustmentMessage(requestedFrameCount, request.Frames.Value, request.Fps);

        var job = _runtime.CreateJob(JackOnnxMediaKind.Video, request);
        using var jobCancellation = _runtime.CreateJobCancellationScope(job.Id, cancellationToken);
        cancellationToken = jobCancellation.Token;
        Report(job.Id, JackOnnxJobState.LoadingModel, 5, "Resolving local video generation model.", progress);

        var manifest = await ResolveVideoModelAsync(request, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CompleteFailure(
                job.Id,
                "No JackONNX video model manifest is registered. Add a local video generation manifest under Models\\JackONNX or CompleteModels, then load that model in LlmRuntime.");
        }

        var missingComponents = GetMissingComponents(manifest);
        if (missingComponents.Count > 0)
        {
            return CompleteFailure(
                job.Id,
                "Video model '" + manifest.Id + "' is registered, but model component files are missing: " + string.Join(", ", missingComponents.Take(5)) + ".");
        }

        if (!IsSupportedVideoManifest(manifest))
        {
            return CompleteFailure(
                job.Id,
                "Video model '" + manifest.Id + "' uses format '" + manifest.Format + "', which is not executable by the current JackONNX video runner. Supported local layouts are Diffusers/PyTorch text-to-video bundles and Diffusers GGUF transformer checkpoints.");
        }

        Report(job.Id, JackOnnxJobState.Running, 20, "Running local video model '" + manifest.Id + "'.", progress);

        JackOnnxVideoModelOutput output;
        try
        {
            output = await _runner.GenerateAsync(manifest, request, job.Id, _runtime.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CompleteFailure(job.Id, "Video generation failed: " + ex.Message);
        }

        if (output.Data.Length == 0)
            return CompleteFailure(job.Id, string.IsNullOrWhiteSpace(output.Message) ? "Video generation returned no media data." : output.Message);

        string fileName = string.IsNullOrWhiteSpace(output.FileName) ? job.Id + ".mp4" : output.FileName;
        string mediaType = string.IsNullOrWhiteSpace(output.MediaType) ? "video/mp4" : output.MediaType;
        if (!TryValidateVideoOutput(output.Data, fileName, mediaType, out string validationError))
            return CompleteFailure(job.Id, validationError);

        Report(job.Id, JackOnnxJobState.Encoding, 95, "Saving generated video artifact.", progress);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = request.Prompt,
            ["modelId"] = manifest.Id,
            ["runner"] = output.Runner,
            ["width"] = request.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = request.Height.ToString(CultureInfo.InvariantCulture),
            ["frames"] = (request.Frames ?? 0).ToString(CultureInfo.InvariantCulture),
            ["fps"] = request.Fps.ToString(CultureInfo.InvariantCulture),
            ["seconds"] = request.Seconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["steps"] = request.Steps.ToString(CultureInfo.InvariantCulture)
        };
        if (requestedFrameCount != request.Frames.GetValueOrDefault() || Math.Abs(requestedSeconds - request.Seconds) > 0.001)
        {
            metadata["requestedFrames"] = requestedFrameCount.ToString(CultureInfo.InvariantCulture);
            metadata["requestedSeconds"] = requestedSeconds.ToString("0.###", CultureInfo.InvariantCulture);
            metadata["requestedFps"] = requestedFps.ToString(CultureInfo.InvariantCulture);
            metadata["frameLimit"] = MaxVideoFrameCount.ToString(CultureInfo.InvariantCulture);
            metadata["frameLimitApplied"] = (requestedFrameCount > MaxVideoFrameCount).ToString(CultureInfo.InvariantCulture);
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

        var artifact = await _runtime.SaveArtifactAsync(job.Id, JackOnnxMediaKind.Video, fileName, mediaType, output.Data, metadata, cancellationToken).ConfigureAwait(false);
        var artifacts = new List<JackOnnxArtifact> { artifact };
        JackOnnxArtifact? presentationArtifact = await TryCreatePresentationArtifactAsync(job.Id, artifact, metadata, cancellationToken).ConfigureAwait(false);
        if (presentationArtifact != null)
        {
            artifact.Metadata["presentationArtifactId"] = presentationArtifact.Id;
            artifact.Metadata["presentationFilePath"] = presentationArtifact.FilePath;
            artifacts.Insert(0, presentationArtifact);
        }

        var result = new JackOnnxGenerationResult
        {
            JobId = job.Id,
            Success = true,
            Message = AppendTimingAdjustment(string.IsNullOrWhiteSpace(output.Message)
                ? "Video generated successfully."
                : output.Message, timingAdjustment) + (presentationArtifact == null ? "" : " Browser-playable MP4 prepared."),
            Artifacts = artifacts
        };

        _runtime.CompleteJob(job.Id, result);
        return result;
    }

    public async Task<JackOnnxArtifact?> CreatePresentationArtifactAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            return null;

        var jobs = await _runtime.ListJobsAsync(cancellationToken).ConfigureAwait(false);
        var job = jobs.FirstOrDefault(item => item.Artifacts.Any(artifact => string.Equals(artifact.Id, artifactId, StringComparison.OrdinalIgnoreCase)));
        var artifact = job?.Artifacts.FirstOrDefault(item => string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        if (job == null || artifact == null)
            return null;

        if (artifact.Metadata.TryGetValue("presentationArtifactId", out string? existingPresentationId) &&
            !string.IsNullOrWhiteSpace(existingPresentationId))
        {
            var existing = job.Artifacts.FirstOrDefault(item => string.Equals(item.Id, existingPresentationId, StringComparison.OrdinalIgnoreCase));
            if (existing != null && File.Exists(existing.FilePath))
                return existing;
        }

        return await TryCreatePresentationArtifactAsync(job.Id, artifact, artifact.Metadata, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JackOnnxArtifact?> TryCreatePresentationArtifactAsync(
        string jobId,
        JackOnnxArtifact artifact,
        IReadOnlyDictionary<string, string> sourceMetadata,
        CancellationToken cancellationToken)
    {
        if (artifact == null || string.IsNullOrWhiteSpace(artifact.FilePath) || !File.Exists(artifact.FilePath))
            return null;
        if (!IsVideoPresentationCandidate(artifact))
            return null;
        if (artifact.Metadata.TryGetValue("presentationKind", out string? presentationKind) &&
            presentationKind.Equals("browser-playable", StringComparison.OrdinalIgnoreCase))
            return null;

        var converter = new JackOnnxVideoPresentationConverter();
        string outputDirectory = Path.GetDirectoryName(artifact.FilePath) ?? _runtime.Options.ArtifactRoot;
        JackOnnxVideoPresentationConversionResult converted = await converter.TryConvertAsync(artifact.FilePath, outputDirectory, cancellationToken).ConfigureAwait(false);
        if (!converted.Success || string.IsNullOrWhiteSpace(converted.OutputPath) || !File.Exists(converted.OutputPath))
        {
            artifact.Metadata["presentationConversion"] = converted.Message;
            return null;
        }

        var metadata = new Dictionary<string, string>(sourceMetadata, StringComparer.OrdinalIgnoreCase)
        {
            ["presentationKind"] = "browser-playable",
            ["presentationOfArtifactId"] = artifact.Id,
            ["presentationOfFilePath"] = artifact.FilePath,
            ["encoding"] = converted.Encoding,
            ["encoder"] = "ffmpeg"
        };

        byte[] data = await File.ReadAllBytesAsync(converted.OutputPath, cancellationToken).ConfigureAwait(false);
        if (!TryValidateVideoOutput(data, Path.GetFileName(converted.OutputPath), converted.MediaType, out string validationError))
        {
            artifact.Metadata["presentationConversion"] = validationError;
            return null;
        }

        JackOnnxArtifact presentation = await _runtime.SaveArtifactAsync(
            jobId,
            JackOnnxMediaKind.Video,
            Path.GetFileName(converted.OutputPath),
            converted.MediaType,
            data,
            metadata,
            cancellationToken).ConfigureAwait(false);
        artifact.Metadata["presentationArtifactId"] = presentation.Id;
        artifact.Metadata["presentationFilePath"] = presentation.FilePath;
        return presentation;
    }

    private static bool IsVideoPresentationCandidate(JackOnnxArtifact artifact)
    {
        string mediaType = (artifact.MediaType ?? "").Trim();
        string path = artifact.FilePath ?? "";
        if (mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase))
            return true;
        if (mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            return true;
        return HasAnyExtension(path, ".mp4", ".webm", ".mov", ".m4v", ".ogv", ".gif", ".avi", ".mkv");
    }

    private static bool HasAnyExtension(string path, params string[] extensions)
    {
        string extension = Path.GetExtension(path ?? "");
        if (string.IsNullOrWhiteSpace(extension))
            return false;

        return extensions.Any(candidate => extension.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryValidateVideoOutput(byte[] data, string fileName, string mediaType, out string error)
    {
        error = "";
        data ??= Array.Empty<byte>();
        long length = data.LongLength;
        string extension = Path.GetExtension(fileName ?? "");
        string normalizedMediaType = (mediaType ?? "").Trim().ToLowerInvariant();
        bool isMp4 = normalizedMediaType.Equals("video/mp4", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".m4v", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".mov", StringComparison.OrdinalIgnoreCase);
        bool isWebm = normalizedMediaType.Equals("video/webm", StringComparison.OrdinalIgnoreCase) ||
                      extension.Equals(".webm", StringComparison.OrdinalIgnoreCase);
        bool isGif = normalizedMediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
                     extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);

        if (isMp4)
        {
            if (length < 1024)
            {
                error = "Video generation completed, but the generated MP4 artifact is too small to be playable (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).";
                return false;
            }

            if (!ContainsAsciiMarker(data, "ftyp", 64))
            {
                error = "Video generation completed, but the generated MP4 artifact does not contain an MP4 file signature.";
                return false;
            }

            return true;
        }

        if (isWebm)
        {
            if (length < 256)
            {
                error = "Video generation completed, but the generated WebM artifact is too small to be playable (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).";
                return false;
            }

            if (!StartsWith(data, 0x1A, 0x45, 0xDF, 0xA3))
            {
                error = "Video generation completed, but the generated WebM artifact does not contain a WebM file signature.";
                return false;
            }

            return true;
        }

        if (isGif)
        {
            if (length < 32)
            {
                error = "Video generation completed, but the generated GIF artifact is too small to be playable (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).";
                return false;
            }

            if (!ContainsAsciiMarker(data, "GIF87a", 8) && !ContainsAsciiMarker(data, "GIF89a", 8))
            {
                error = "Video generation completed, but the generated GIF artifact does not contain a GIF file signature.";
                return false;
            }

            return true;
        }

        if (normalizedMediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) && length < 1024)
        {
            error = "Video generation completed, but the generated video artifact is too small to be playable (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).";
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

    private static bool StartsWith(byte[] data, params byte[] prefix)
    {
        if (data == null || prefix == null || data.Length < prefix.Length)
            return false;

        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
                return false;
        }

        return true;
    }

    private async Task<JackOnnxModelManifest?> ResolveVideoModelAsync(VideoGenerationRequest request, CancellationToken cancellationToken)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            string requested = request.ModelId.Trim();
            return models.FirstOrDefault(model =>
                IsVideoModel(model) &&
                ModelMatchesRequest(model, requested));
        }

        return models.FirstOrDefault(IsVideoModel);
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

    private static bool IsVideoModel(JackOnnxModelManifest model)
    {
        return model != null &&
               (model.Type ?? "").StartsWith("video", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedVideoManifest(JackOnnxModelManifest model)
    {
        string format = (model.Format ?? "").Trim();
        if (string.Equals(format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "torch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "diffusers", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase))
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
        yield return Path.GetFileNameWithoutExtension(model.ManifestPath);
        yield return Path.GetFileName(model.ManifestPath);
        foreach (var pair in model.Components)
        {
            yield return pair.Key;
            yield return pair.Value;
        }
        foreach (string key in new[] { "ggufFile", "ggufFiles", "source", "baseModel", "base_model" })
        {
            if (!model.Metadata.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
                continue;
            foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return part;
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

    private static int NormalizeDimension(int value, int fallback, int max)
    {
        value = Math.Clamp(value <= 0 ? fallback : value, 64, max);
        value -= value % 8;
        return Math.Max(64, value);
    }

    private static int ResolveRequestedFrameCount(VideoGenerationRequest request)
    {
        int minimumFramesForDuration = Math.Max(1, (int)Math.Ceiling(request.Seconds * request.Fps));
        int requested = request.Frames.GetValueOrDefault();
        if (requested <= 0)
            requested = minimumFramesForDuration;
        return Math.Max(requested, minimumFramesForDuration);
    }

    private static int NormalizeFrameCount(int requestedFrameCount)
    {
        return Math.Clamp(requestedFrameCount, 1, MaxVideoFrameCount);
    }

    private static double NormalizeEffectiveSeconds(int frames, int fps)
    {
        return Math.Max(1d / Math.Max(1, fps), frames / (double)Math.Max(1, fps));
    }

    private static string BuildTimingAdjustmentMessage(int requestedFrameCount, int normalizedFrameCount, int fps)
    {
        if (requestedFrameCount <= normalizedFrameCount)
            return "";

        return "Requested " + requestedFrameCount.ToString(CultureInfo.InvariantCulture) +
               " video frames; JackONNX capped this run to " + normalizedFrameCount.ToString(CultureInfo.InvariantCulture) +
               " frames (" + NormalizeEffectiveSeconds(normalizedFrameCount, fps).ToString("0.###", CultureInfo.InvariantCulture) +
               " seconds at " + fps.ToString(CultureInfo.InvariantCulture) +
               " FPS) to stay within the current local video frame limit.";
    }

    private static string AppendTimingAdjustment(string message, string timingAdjustment)
    {
        if (string.IsNullOrWhiteSpace(timingAdjustment))
            return message;
        if (string.IsNullOrWhiteSpace(message))
            return timingAdjustment;
        return message.TrimEnd() + " " + timingAdjustment;
    }

    private static string ReadMetadata(JackOnnxModelManifest model, string key)
    {
        return model.Metadata.TryGetValue(key, out string? value) ? value : "";
    }

}

public interface IJackOnnxVideoModelRunner
{
    Task<JackOnnxVideoModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        VideoGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class JackOnnxVideoModelOutput
{
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public string FileName { get; init; } = "";

    public string MediaType { get; init; } = "video/mp4";

    public string Message { get; init; } = "";

    public string Runner { get; init; } = "";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
