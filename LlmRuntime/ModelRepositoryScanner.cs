using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class ModelRepositoryScanner
{
    private readonly HttpClient _httpClient;

    public ModelRepositoryScanner(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public Task<ModelRepositoryScanResult> ScanHuggingFaceAsync(
        string owner,
        string repo,
        string revision,
        CancellationToken cancellationToken) =>
        ScanHuggingFaceAsync(owner, repo, revision, null, null, cancellationToken);

    public async Task<ModelRepositoryScanResult> ScanHuggingFaceAsync(
        string owner,
        string repo,
        string revision = "main",
        string? cookies = null,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner))
            throw new ArgumentException("Repository owner is required.", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo))
            throw new ArgumentException("Repository name is required.", nameof(repo));

        string repoId = owner.Trim('/') + "/" + repo.Trim('/');
        string apiUrl = "https://huggingface.co/api/models/" +
                        Uri.EscapeDataString(owner.Trim('/')) + "/" +
                        Uri.EscapeDataString(repo.Trim('/')) +
                        "/tree/" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim('/')) +
                        "?recursive=true";

        using var request = CreateHuggingFaceRequest(apiUrl, cookies, bearerToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return new ModelRepositoryScanResult { Repository = repoId, Revision = revision, Reason = "Hugging Face did not return a file tree." };

        var files = new List<ModelRepositoryFile>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "file", StringComparison.OrdinalIgnoreCase))
                continue;

            string path = item.TryGetProperty("path", out JsonElement pathElement) ? pathElement.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(path))
                continue;

            long size = item.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value) ? value : 0;
            files.Add(new ModelRepositoryFile
            {
                Path = path,
                FileName = Path.GetFileName(path),
                SizeBytes = size,
                Format = DetectFormat(path)
            });
        }

        ModelRepositoryMetadata metadata = await TryReadHuggingFaceMetadataAsync(owner, repo, cookies, bearerToken, cancellationToken).ConfigureAwait(false);
        return BuildResult(repoId, revision, files, metadata);
    }

    private async Task<ModelRepositoryMetadata> TryReadHuggingFaceMetadataAsync(
        string owner,
        string repo,
        string? cookies,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        try
        {
            string apiUrl = "https://huggingface.co/api/models/" +
                            Uri.EscapeDataString(owner.Trim('/')) + "/" +
                            Uri.EscapeDataString(repo.Trim('/'));
            using var request = CreateHuggingFaceRequest(apiUrl, cookies, bearerToken);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new ModelRepositoryMetadata();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ModelRepositoryMetadata();

            var tags = new List<string>();
            AddStringArray(tags, root, "tags");
            string pipelineTag = TryReadString(root, "pipeline_tag") ?? "";
            string libraryName = TryReadString(root, "library_name") ?? "";
            var baseModels = new List<string>();
            AddBaseModelValues(baseModels, root, "base_model");
            AddBaseModelValues(baseModels, root, "baseModel");

            if (root.TryGetProperty("cardData", out JsonElement cardData) && cardData.ValueKind == JsonValueKind.Object)
            {
                pipelineTag = FirstNonEmpty(pipelineTag, TryReadString(cardData, "pipeline_tag"));
                libraryName = FirstNonEmpty(libraryName, TryReadString(cardData, "library_name"), TryReadString(cardData, "library"));
                AddStringArray(tags, cardData, "tags");
                AddBaseModelValues(baseModels, cardData, "base_model");
                AddBaseModelValues(baseModels, cardData, "baseModel");
            }

            string[] normalizedBaseModels = NormalizeBaseModels(baseModels).ToArray();
            return new ModelRepositoryMetadata
            {
                PipelineTag = pipelineTag,
                LibraryName = libraryName,
                Tags = tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                BaseModel = normalizedBaseModels.FirstOrDefault() ?? "",
                BaseModels = normalizedBaseModels
            };
        }
        catch
        {
            return new ModelRepositoryMetadata();
        }
    }

    private static HttpRequestMessage CreateHuggingFaceRequest(string url, string? cookies, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "SocketJack-LlmRuntime/1.0");
        if (!string.IsNullOrWhiteSpace(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        return request;
    }

    public static ModelRepositoryScanResult BuildResult(string repository, string revision, IReadOnlyList<ModelRepositoryFile> files) =>
        BuildResult(repository, revision, files, new ModelRepositoryMetadata());

    public static ModelRepositoryScanResult BuildResult(string repository, string revision, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata)
    {
        metadata ??= new ModelRepositoryMetadata();
        bool hasConfig = files.Any(file => string.Equals(file.FileName, "config.json", StringComparison.OrdinalIgnoreCase));
        bool hasTokenizer = files.Any(IsTokenizerFile);
        var candidates = new List<ModelFileCandidate>();

        foreach (ModelRepositoryFile file in files)
        {
            if (file.Format == ModelFileFormat.Gguf)
                candidates.Add(CreateDownloadCandidate(repository, revision, file, "download_gguf", "Download GGUF"));
            else if (file.Format == ModelFileFormat.Onnx)
                candidates.Add(CreateDownloadCandidate(repository, revision, file, "download_onnx", "Download ONNX"));
        }

        string task = InferTask(repository, files, metadata);
        bool hasPytorchBundle = ShouldOfferPytorchBundle(repository, files, metadata, task);
        if (hasPytorchBundle)
            candidates.Add(CreatePytorchBundleCandidate(repository, revision, files, metadata, task));

        var sourceTensorFiles = files.Where(file => file.Format == ModelFileFormat.Safetensors).ToList();
        if (sourceTensorFiles.Count > 0 && !hasPytorchBundle)
        {
            long totalSize = sourceTensorFiles.Sum(file => Math.Max(0, file.SizeBytes));
            bool convertible = hasConfig && hasTokenizer;
            candidates.Add(new ModelFileCandidate
            {
                Path = "source tensors",
                FileName = BuildSourceBundleFileName(repository),
                Repository = repository,
                Revision = revision,
                SizeBytes = totalSize,
                Format = ModelFileFormat.Safetensors,
                Quantization = "",
                Task = task,
                TargetDirectoryName = BuildRepositoryTargetDirectory(repository, revision),
                ModelKindLabel = BuildModelKindLabel(task),
                Tags = BuildTags(repository, task),
                SourcePaths = sourceTensorFiles.Select(file => file.Path).ToArray(),
                Action = convertible ? "convert_onnx" : "unsupported",
                ActionLabel = convertible ? "Convert to ONNX" : "Unsupported",
                CanConvert = convertible,
                Reason = convertible
                    ? "Source tensors include config and tokenizer files; convert with the ONNX worker."
                    : "Source tensors need config.json and tokenizer assets before they can be converted."
            });
        }

        if (candidates.Count == 0)
        {
            candidates.Add(new ModelFileCandidate
            {
                Path = repository,
                FileName = repository.Replace('/', '_'),
                Repository = repository,
                Revision = revision,
                Format = ModelFileFormat.Unknown,
                Action = "unsupported",
                ActionLabel = "Unsupported",
                Task = task,
                ModelKindLabel = BuildModelKindLabel(task),
                Reason = "No GGUF, ONNX, PyTorch, or source tensor files were found in this repository."
            });
        }

        return new ModelRepositoryScanResult
        {
            Repository = repository,
            Revision = revision,
            Files = files,
            Candidates = candidates
                .OrderByDescending(candidate => candidate.CanDownload)
                .ThenByDescending(candidate => candidate.CanConvert)
                .ThenBy(candidate => candidate.Action == "download_pytorch_bundle" ? 1 : 0)
                .ThenBy(candidate => candidate.SizeBytes)
                .ToArray()
        };
    }

    private static ModelFileCandidate CreateDownloadCandidate(string repository, string revision, ModelRepositoryFile file, string action, string actionLabel)
    {
        string quant = ModelHeuristics.DetectQuantType(file.Path);
        string task = InferTask(repository + "/" + file.Path, [file], new ModelRepositoryMetadata());
        string label = BuildModelKindLabel(task);
        if (file.Format != ModelFileFormat.Gguf || IsMediaTask(task))
            actionLabel = "Download " + label + " Model";
        return new ModelFileCandidate
        {
            Path = file.Path,
            FileName = file.FileName,
            Repository = repository,
            Revision = revision,
            SizeBytes = file.SizeBytes,
            Format = file.Format,
            Quantization = quant,
            Task = task,
            TargetDirectoryName = BuildRepositoryTargetDirectory(repository, revision),
            ModelKindLabel = BuildModelKindLabel(task),
            Tags = BuildTags(file.Path, task),
            Action = action,
            ActionLabel = actionLabel,
            CanDownload = true,
            Reason = file.Format == ModelFileFormat.Onnx ? "Ready ONNX file." : "Ready GGUF file."
        };
    }

    private static ModelFileCandidate CreatePytorchBundleCandidate(string repository, string revision, IReadOnlyList<ModelRepositoryFile> files, string task)
        => CreatePytorchBundleCandidate(repository, revision, files, new ModelRepositoryMetadata(), task);

    private static ModelFileCandidate CreatePytorchBundleCandidate(string repository, string revision, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata, string task)
    {
        ModelRepositoryFile[] bundleFiles = files
            .Where(ShouldIncludeInPytorchBundle)
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (bundleFiles.Length == 0)
            bundleFiles = files.OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();

        long totalSize = bundleFiles.Sum(file => Math.Max(0, file.SizeBytes));
        string label = BuildModelKindLabel(task);
        bool loraAdapter = IsLoraAdapter(repository, bundleFiles, metadata);
        IReadOnlyList<string> baseModels = GetBaseModels(metadata);
        string baseModel = baseModels.FirstOrDefault() ?? "";
        var tags = BuildTags(repository + "/" + string.Join("/", bundleFiles.Select(file => file.Path)), task).ToList();
        if (loraAdapter)
        {
            if (!tags.Contains("lora", StringComparer.OrdinalIgnoreCase))
                tags.Add("lora");
            if (!tags.Contains("adapter", StringComparer.OrdinalIgnoreCase))
                tags.Add("adapter");
        }
        return new ModelFileCandidate
        {
            Path = repository,
            FileName = BuildSourceBundleFileName(repository),
            Repository = repository,
            Revision = revision,
            SizeBytes = totalSize,
            Format = ModelFileFormat.Pytorch,
            Quantization = ModelHeuristics.DetectQuantType(repository),
            Task = task,
            TargetDirectoryName = BuildRepositoryTargetDirectory(repository, revision),
            ModelKindLabel = label,
            Tags = tags,
            SourcePaths = bundleFiles.Select(file => file.Path).ToArray(),
            AdapterType = loraAdapter ? "lora" : "",
            BaseModel = baseModel,
            BaseModels = baseModels,
            Action = "download_pytorch_bundle",
            ActionLabel = loraAdapter ? "Download " + label + " Adapter" : "Download " + label + " Model",
            CanDownload = true,
            Reason = loraAdapter
                ? "LoRA adapter bundle; downloads adapter files only and requires the base model" + (baseModels.Count == 0 ? "." : " " + FormatBaseModels(baseModels) + ".")
                : "PyTorch " + label.ToLowerInvariant() + " bundle; downloads the complete Hugging Face folder layout."
        };
    }

    private static ModelFileFormat DetectFormat(string path)
    {
        string fileName = Path.GetFileName(path);
        if (path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            return ModelFileFormat.Gguf;
        if (path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            return ModelFileFormat.Onnx;
        if (path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            return ModelFileFormat.Safetensors;
        if (IsPytorchFileName(fileName))
            return ModelFileFormat.Pytorch;
        if (string.Equals(fileName, "config.json", StringComparison.OrdinalIgnoreCase))
            return ModelFileFormat.Config;
        if (IsTokenizerFileName(fileName))
            return ModelFileFormat.Tokenizer;
        return ModelFileFormat.Other;
    }

    private static bool ShouldOfferPytorchBundle(string repository, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata, string task)
    {
        if (files.Count == 0)
            return false;

        bool hasWeights = files.Any(file => file.Format is ModelFileFormat.Pytorch or ModelFileFormat.Safetensors);
        if (!hasWeights)
            return false;

        string signalText = BuildDetectionText(repository, files, metadata);
        bool hasPytorchSignal = signalText.Contains("pytorch", StringComparison.OrdinalIgnoreCase) ||
                                signalText.Contains("torch", StringComparison.OrdinalIgnoreCase) ||
                                signalText.Contains("diffusers", StringComparison.OrdinalIgnoreCase) ||
                                files.Any(file => file.Format == ModelFileFormat.Pytorch);
        bool isMedia = IsMediaTask(task);
        return hasPytorchSignal || isMedia;
    }

    private static bool ShouldIncludeInPytorchBundle(ModelRepositoryFile file)
    {
        if (string.IsNullOrWhiteSpace(file.Path))
            return false;
        string fileName = file.FileName;
        if (string.Equals(fileName, ".gitattributes", StringComparison.OrdinalIgnoreCase))
            return false;
        if (file.Path.Contains("/.git/", StringComparison.OrdinalIgnoreCase) ||
            file.Path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool IsLoraAdapter(string repository, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata)
    {
        string text = string.Join(" ", new[]
        {
            repository,
            metadata.BaseModel,
            string.Join(" ", metadata.BaseModels),
            metadata.PipelineTag,
            metadata.LibraryName,
            string.Join(" ", metadata.Tags),
            string.Join(" ", files.Select(file => file.Path))
        });
        return text.Contains("lora", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("adapter", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("diffusion-lora", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPytorchFileName(string fileName)
    {
        return fileName.Equals("pytorch_model.bin", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".pt", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".pth", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".ckpt", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) && fileName.Contains("pytorch", StringComparison.OrdinalIgnoreCase);
    }

    private static string InferTask(string repository, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata)
    {
        string text = BuildDetectionText(repository, files, metadata);
        if (ContainsAny(text, "text-to-video", "image-to-video", "video-to-video", "video-generation", "video", "t2v", "i2v", "v2v", "wan2", "hunyuanvideo", "ltx-video", "ltxv", "mochi", "motifv"))
            return "video";
        if (ContainsAny(text, "text-to-audio", "audio-generation", "automatic-speech-recognition", "text-to-speech", "speech", "audio", "tts", "whisper", "wav2vec", "bark", "musicgen", "moshi", "moshiko"))
            return "audio";
        if (ContainsAny(text, "text-to-image", "image-to-image", "image-generation", "stable-diffusion", "diffusers", "sdxl", "flux", "unet", "vae", "controlnet"))
            return "image";
        if (ContainsAny(text, "feature-extraction", "sentence-similarity", "embedding", "embed"))
            return "embedding";
        return "llm";
    }

    private static bool IsMediaTask(string task) =>
        task.Equals("image", StringComparison.OrdinalIgnoreCase) ||
        task.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
        task.Equals("video", StringComparison.OrdinalIgnoreCase);

    private static string BuildModelKindLabel(string task)
    {
        if (task.Equals("video", StringComparison.OrdinalIgnoreCase))
            return "Video Generation";
        if (task.Equals("audio", StringComparison.OrdinalIgnoreCase))
            return "Audio Generation";
        if (task.Equals("image", StringComparison.OrdinalIgnoreCase))
            return "Image Generation";
        if (task.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            return "Embedding";
        return "Text Generation";
    }

    private static IReadOnlyList<string> BuildTags(string text, string task)
    {
        var tags = new HashSet<string>(ModelHeuristics.DetectModelTags(text), StringComparer.OrdinalIgnoreCase);
        if (task.Equals("image", StringComparison.OrdinalIgnoreCase))
            tags.Add("image");
        else if (task.Equals("audio", StringComparison.OrdinalIgnoreCase))
            tags.Add("audio");
        else if (task.Equals("video", StringComparison.OrdinalIgnoreCase))
            tags.Add("video");
        else if (task.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            tags.Add("embedding");
        else
            tags.Add("chat");
        return tags.ToArray();
    }

    private static string BuildRepositoryTargetDirectory(string repository, string revision)
    {
        string repo = repository.Replace('\\', '/').Trim('/');
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim().Trim('/');
        string[] parts = repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(rev)
            .Select(SanitizePathSegment)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToArray();
        return parts.Length == 0 ? "model/main" : string.Join("/", parts);
    }

    private static string BuildDetectionText(string repository, IReadOnlyList<ModelRepositoryFile> files, ModelRepositoryMetadata metadata)
    {
        return string.Join(" ", new[]
        {
            repository,
            metadata.PipelineTag,
            metadata.LibraryName,
            metadata.BaseModel,
            string.Join(" ", metadata.BaseModels),
            string.Join(" ", metadata.Tags),
            string.Join(" ", files.Select(file => file.Path))
        });
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? TryReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static void AddStringArray(List<string> values, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }
    }

    private static void AddBaseModelValues(List<string> values, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
            return;

        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
            return;

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? value = item.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
        }
    }

    private static IReadOnlyList<string> GetBaseModels(ModelRepositoryMetadata metadata) =>
        NormalizeBaseModels(metadata.BaseModels.Concat([metadata.BaseModel])).ToArray();

    private static IEnumerable<string> NormalizeBaseModels(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in values)
        {
            foreach (string candidate in SplitBaseModelValue(value))
            {
                string normalized = candidate.Trim();
                if (string.IsNullOrWhiteSpace(normalized) ||
                    normalized.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("null", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (seen.Add(normalized))
                    yield return normalized;
            }
        }
    }

    private static IEnumerable<string> SplitBaseModelValue(string? value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
        {
            string inner = value[1..^1];
            foreach (string part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate = part.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(candidate))
                    yield return candidate;
            }
            yield break;
        }

        yield return value.Trim('"', '\'');
    }

    private static string FormatBaseModels(IReadOnlyList<string> baseModels)
    {
        if (baseModels.Count == 0)
            return "";
        if (baseModels.Count == 1)
            return baseModels[0];
        return string.Join(", ", baseModels.Take(3)) + (baseModels.Count > 3 ? ", ..." : "");
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static string SanitizePathSegment(string value)
    {
        string segment = value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        segment = segment.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        return string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." ? "model" : segment;
    }

    private static bool IsTokenizerFile(ModelRepositoryFile file) => IsTokenizerFileName(file.FileName);

    private static bool IsTokenizerFileName(string fileName)
    {
        return fileName.Equals("tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("tokenizer.model", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("tokenizer_config.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("special_tokens_map.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("added_tokens.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("merges.txt", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("vocab.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".tiktoken", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSourceBundleFileName(string repository)
    {
        string value = repository.Replace('/', '_').Replace('\\', '_').Trim();
        return string.IsNullOrWhiteSpace(value) ? "source-tensors" : value + "-source-tensors";
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<ModelFileFormat>))]
public enum ModelFileFormat
{
    Unknown,
    Gguf,
    Onnx,
    Safetensors,
    Pytorch,
    Config,
    Tokenizer,
    Other
}

public sealed class ModelRepositoryMetadata
{
    public string PipelineTag { get; init; } = "";

    public string LibraryName { get; init; } = "";

    public string BaseModel { get; init; } = "";

    public IReadOnlyList<string> BaseModels { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];
}

public sealed class ModelRepositoryScanResult
{
    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "main";

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("files")]
    public IReadOnlyList<ModelRepositoryFile> Files { get; init; } = [];

    [JsonPropertyName("candidates")]
    public IReadOnlyList<ModelFileCandidate> Candidates { get; init; } = [];
}

public sealed class ModelRepositoryFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("format")]
    public ModelFileFormat Format { get; init; } = ModelFileFormat.Unknown;
}

public sealed class ModelFileCandidate
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; init; } = "";

    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "main";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; init; }

    [JsonPropertyName("estimatedRequiredDriveBytes")]
    public long EstimatedRequiredDriveBytes { get; set; }

    [JsonPropertyName("driveFreeBytes")]
    public long DriveFreeBytes { get; set; }

    [JsonPropertyName("targetRootDirectory")]
    public string TargetRootDirectory { get; set; } = "";

    [JsonPropertyName("format")]
    public ModelFileFormat Format { get; init; } = ModelFileFormat.Unknown;

    [JsonPropertyName("task")]
    public string Task { get; init; } = "";

    [JsonPropertyName("targetDirectoryName")]
    public string TargetDirectoryName { get; init; } = "";

    [JsonPropertyName("modelKindLabel")]
    public string ModelKindLabel { get; init; } = "";

    [JsonPropertyName("quantization")]
    public string Quantization { get; init; } = "";

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("action")]
    public string Action { get; set; } = "unsupported";

    [JsonPropertyName("actionLabel")]
    public string ActionLabel { get; set; } = "Unsupported";

    [JsonPropertyName("canDownload")]
    public bool CanDownload { get; set; }

    [JsonPropertyName("canConvert")]
    public bool CanConvert { get; set; }

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    [JsonPropertyName("isWarning")]
    public bool IsWarning { get; set; }

    [JsonPropertyName("sourcePaths")]
    public IReadOnlyList<string> SourcePaths { get; init; } = [];

    [JsonPropertyName("adapterType")]
    public string AdapterType { get; init; } = "";

    [JsonPropertyName("baseModel")]
    public string BaseModel { get; init; } = "";

    [JsonPropertyName("baseModels")]
    public IReadOnlyList<string> BaseModels { get; init; } = [];

    public void ApplyFit(ModelFitSnapshot fit, string modelsDirectory)
    {
        EstimatedRequiredDriveBytes = EstimateRequiredDriveBytes();
        DriveFreeBytes = Math.Max(0, fit.DriveFreeBytes);
        TargetRootDirectory = modelsDirectory;
        IsWarning = false;
        bool completeLayout = UsesCompleteModelLayout();
        string targetPath = Format == ModelFileFormat.Pytorch
            ? System.IO.Path.Combine(modelsDirectory, GetDefaultLocalDirectoryName(), "manifest.json")
            : completeLayout
                ? System.IO.Path.Combine(modelsDirectory, GetDefaultLocalDirectoryName(), SanitizeRelativePath(string.IsNullOrWhiteSpace(Path) ? FileName : Path))
            : System.IO.Path.Combine(modelsDirectory, GetDefaultLocalFileName());
        Exists = File.Exists(targetPath);
        if (Exists)
        {
            CanDownload = false;
            CanConvert = false;
            Reason = Format == ModelFileFormat.Pytorch
                ? "This complete model bundle already exists."
                : "This file already exists in the Models folder.";
            Action = "already_downloaded";
            ActionLabel = "Already downloaded";
            return;
        }

        if (SizeBytes <= 0 && (CanDownload || CanConvert))
        {
            CanDownload = false;
            CanConvert = false;
            Reason = "Hugging Face did not report a usable file size.";
            Action = "unsupported";
            ActionLabel = "Unsupported";
            return;
        }

        if (fit.DriveFreeBytes > 0 && EstimatedRequiredDriveBytes > fit.DriveFreeBytes)
        {
            CanDownload = false;
            CanConvert = false;
            Reason = "Not enough free drive space in " + modelsDirectory + ".";
            Action = "unsupported";
            ActionLabel = "Does not fit";
            return;
        }

        long runtimeMemoryEstimate = EstimateRuntimeMemoryBytes();
        if (fit.SharedVideoMemoryBytes > 0 && runtimeMemoryEstimate > fit.SharedVideoMemoryBytes)
        {
            IsWarning = true;
            Reason = fit.VideoMemoryIsDedicated
                ? "Larger than detected dedicated VRAM."
                : "Larger than estimated shared video memory.";
        }
    }

    public string GetDefaultLocalFileName()
    {
        string fileName = FileName;
        if (Format == ModelFileFormat.Onnx && !string.IsNullOrWhiteSpace(Repository))
        {
            string prefix = Repository.Replace('/', '-').Replace('\\', '-');
            string sourceName = string.IsNullOrWhiteSpace(Path) ? FileName : System.IO.Path.GetFileName(Path);
            fileName = prefix + "-" + sourceName;
        }

        return SanitizeFileName(fileName);
    }

    public string GetDefaultLocalDirectoryName()
    {
        if (!string.IsNullOrWhiteSpace(TargetDirectoryName))
            return TargetDirectoryName;
        if (!string.IsNullOrWhiteSpace(Repository))
            return BuildDirectoryName(Repository, Revision);
        return SanitizeFileName(System.IO.Path.GetFileNameWithoutExtension(FileName));
    }

    public bool UsesCompleteModelLayout()
    {
        if (Format != ModelFileFormat.Gguf)
            return true;
        return Task.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               Task.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
               Task.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    private long EstimateRequiredDriveBytes()
    {
        if (SizeBytes <= 0)
            return 0;
        if (CanConvert)
            return SaturatingMultiply(SizeBytes, 3);
        return SizeBytes;
    }

    private long EstimateRuntimeMemoryBytes()
    {
        if (SizeBytes <= 0)
            return 0;
        if (CanConvert)
            return SaturatingMultiply(SizeBytes, 2);
        return SizeBytes;
    }

    private static long SaturatingMultiply(long value, int multiplier)
    {
        if (value <= 0)
            return 0;
        return value > long.MaxValue / multiplier ? long.MaxValue : value * multiplier;
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "model.bin" : fileName;
    }

    private static string SanitizeRelativePath(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .ToArray();
        return parts.Length == 0 ? SanitizeFileName(path) : System.IO.Path.Combine(parts);
    }

    private static string BuildDirectoryName(string repository, string revision)
    {
        string repo = repository.Replace('\\', '/').Trim('/');
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim().Trim('/');
        return string.Join("/", repo.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(rev)
            .Select(SanitizeFileName));
    }
}

public sealed class ModelFitSnapshot
{
    public long SharedVideoMemoryBytes { get; init; }

    public bool VideoMemoryIsDedicated { get; init; }

    public long DriveFreeBytes { get; init; }
}
