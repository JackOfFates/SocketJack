using JackONNX;
using JackONNX.Runtime;
using System.Globalization;

namespace JackONNX.Image;

public sealed class JackOnnxImagePipeline
{
    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly IJackOnnxImageModelRunner _runner;

    public JackOnnxImagePipeline(JackOnnxRuntimeEngine runtime, IJackOnnxImageModelRunner? runner = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runner = runner ?? new JackOnnxPythonDiffusersImageRunner();
    }

    public async Task<JackOnnxGenerationResult> GenerateAsync(
        ImageGenerationRequest request,
        IProgress<JackOnnxProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Image prompt is required.", nameof(request));

        cancellationToken.ThrowIfCancellationRequested();
        request.Width = NormalizeDimension(request.Width);
        request.Height = NormalizeDimension(request.Height);
        request.Steps = Math.Clamp(request.Steps, 1, 150);
        request.GuidanceScale = Math.Clamp(request.GuidanceScale, 0.1, 30);

        var job = _runtime.CreateJob(JackOnnxMediaKind.Image, request);
        Report(job.Id, JackOnnxJobState.LoadingModel, 5, "Resolving local image generation model.", progress);

        var manifest = await ResolveImageModelAsync(request, cancellationToken).ConfigureAwait(false);
        if (manifest == null)
        {
            return CompleteFailure(
                job.Id,
                "No JackONNX image model manifest is registered. Add a local image generation manifest under Models\\JackONNX or CompleteModels, then load that model in LlmRuntime.");
        }

        var missingComponents = GetMissingComponents(manifest);
        if (missingComponents.Count > 0)
        {
            return CompleteFailure(
                job.Id,
                "Image model '" + manifest.Id + "' is registered, but model component files are missing: " + string.Join(", ", missingComponents.Take(5)) + ".");
        }

        if (!IsSupportedImageManifest(manifest))
        {
            return CompleteFailure(
                job.Id,
                "Image model '" + manifest.Id + "' uses format '" + manifest.Format + "', which is not executable by the current JackONNX image runner. Supported local layouts are Diffusers/PyTorch bundles and Optimum ONNX diffusion exports.");
        }

        Report(job.Id, JackOnnxJobState.Running, 20, "Running local image model '" + manifest.Id + "'.", progress);

        JackOnnxImageModelOutput output;
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
            string manifestPath = string.IsNullOrWhiteSpace(manifest.ManifestPath)
                ? "unknown manifest path"
                : manifest.ManifestPath;
            return CompleteFailure(job.Id, "Image generation failed for model '" + manifest.Id + "' at '" + manifestPath + "': " + ex.Message);
        }

        if (output.Data.Length == 0)
            return CompleteFailure(job.Id, string.IsNullOrWhiteSpace(output.Message) ? "Image generation returned no image data." : output.Message);

        Report(job.Id, JackOnnxJobState.Encoding, 95, "Saving generated image artifact.", progress);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["prompt"] = request.Prompt,
            ["modelId"] = manifest.Id,
            ["runner"] = output.Runner,
            ["width"] = request.Width.ToString(CultureInfo.InvariantCulture),
            ["height"] = request.Height.ToString(CultureInfo.InvariantCulture),
            ["steps"] = request.Steps.ToString(CultureInfo.InvariantCulture)
        };
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

        string fileName = string.IsNullOrWhiteSpace(output.FileName) ? job.Id + ".png" : output.FileName;
        string mediaType = string.IsNullOrWhiteSpace(output.MediaType) ? "image/png" : output.MediaType;
        var artifact = await _runtime.SaveArtifactAsync(job.Id, JackOnnxMediaKind.Image, fileName, mediaType, output.Data, metadata, cancellationToken).ConfigureAwait(false);

        var result = new JackOnnxGenerationResult
        {
            JobId = job.Id,
            Success = true,
            Message = string.IsNullOrWhiteSpace(output.Message)
                ? "Image generated successfully."
                : output.Message,
            Artifacts = [artifact]
        };

        _runtime.CompleteJob(job.Id, result);
        return result;
    }

    private async Task<JackOnnxModelManifest?> ResolveImageModelAsync(ImageGenerationRequest request, CancellationToken cancellationToken)
    {
        var models = await _runtime.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(request.ModelId))
        {
            string requested = request.ModelId.Trim();
            return models.FirstOrDefault(model =>
                IsImageModel(model) &&
                (string.Equals(model.Id, requested, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(model.Name, requested, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(Path.GetFileNameWithoutExtension(model.ManifestPath), requested, StringComparison.OrdinalIgnoreCase)));
        }

        return models.FirstOrDefault(IsImageModel);
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

    private static bool IsImageModel(JackOnnxModelManifest model)
    {
        return model != null &&
               (model.Type ?? "").StartsWith("image", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedImageManifest(JackOnnxModelManifest model)
    {
        string format = (model.Format ?? "").Trim();
        if (string.Equals(format, "onnx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "torch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(format, "diffusers", StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(ReadMetadata(model, "layout"), "complete-model", StringComparison.OrdinalIgnoreCase);
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

    private static int NormalizeDimension(int value)
    {
        value = Math.Clamp(value <= 0 ? 512 : value, 64, 2048);
        value -= value % 8;
        return Math.Max(64, value);
    }

    private static string ReadMetadata(JackOnnxModelManifest model, string key)
    {
        return model.Metadata.TryGetValue(key, out string? value) ? value : "";
    }

}

public interface IJackOnnxImageModelRunner
{
    Task<JackOnnxImageModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        ImageGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default);
}

public sealed class JackOnnxImageModelOutput
{
    public byte[] Data { get; init; } = Array.Empty<byte>();

    public string FileName { get; init; } = "";

    public string MediaType { get; init; } = "image/png";

    public string Message { get; init; } = "";

    public string Runner { get; init; } = "";

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
