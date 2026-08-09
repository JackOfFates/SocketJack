using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LlmRuntime;

public sealed class HuggingFaceIdealModelScanner
{
    private readonly HttpClient _httpClient;

    public HuggingFaceIdealModelScanner(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public static IReadOnlyList<HuggingFaceIdealModelCategory> DefaultCategories { get; } =
    [
        new HuggingFaceIdealModelCategory
        {
            Id = "text",
            Label = "Text",
            Description = "GGUF chat and code models that are easiest to run locally.",
            Queries =
            [
                new HuggingFaceIdealModelQuery { PipelineTag = "text-generation", Filter = "gguf", Limit = 14 },
                new HuggingFaceIdealModelQuery { PipelineTag = "text-generation", Filter = "gguf", Search = "instruct", Limit = 8 }
            ]
        },
        new HuggingFaceIdealModelCategory
        {
            Id = "vision",
            Label = "Vision",
            Description = "Image understanding models for multimodal chat.",
            Queries =
            [
                new HuggingFaceIdealModelQuery { PipelineTag = "image-text-to-text", Limit = 12 },
                new HuggingFaceIdealModelQuery { PipelineTag = "visual-question-answering", Limit = 8 }
            ]
        },
        new HuggingFaceIdealModelCategory
        {
            Id = "image",
            Label = "Image",
            Description = "Diffusers image generation models.",
            Queries =
            [
                new HuggingFaceIdealModelQuery { PipelineTag = "text-to-image", Library = "diffusers", Limit = 12 },
                new HuggingFaceIdealModelQuery { PipelineTag = "image-to-image", Library = "diffusers", Limit = 8 }
            ]
        },
        new HuggingFaceIdealModelCategory
        {
            Id = "video",
            Label = "Video",
            Description = "Text/image to video generation models.",
            Queries =
            [
                new HuggingFaceIdealModelQuery
                {
                    ModelId = "cerspense/zeroscope_v2_576w",
                    PipelineTag = "text-to-video",
                    Library = "diffusers",
                    Limit = 1,
                    Reason = "Small Diffusers text-to-video model; its model card reports 7.9 GB VRAM for 30 frames at 576x320, making it a safer 12 GB GPU smoke-test target."
                },
                new HuggingFaceIdealModelQuery { PipelineTag = "text-to-video", Limit = 12 },
                new HuggingFaceIdealModelQuery { PipelineTag = "image-to-video", Limit = 8 }
            ]
        },
        new HuggingFaceIdealModelCategory
        {
            Id = "audio",
            Label = "Audio",
            Description = "Speech, voice, and audio generation models.",
            Queries =
            [
                new HuggingFaceIdealModelQuery { PipelineTag = "text-to-audio", Limit = 8 },
                new HuggingFaceIdealModelQuery { PipelineTag = "text-to-speech", Limit = 8 },
                new HuggingFaceIdealModelQuery { PipelineTag = "automatic-speech-recognition", Limit = 8 }
            ]
        },
        new HuggingFaceIdealModelCategory
        {
            Id = "embedding",
            Label = "Embeddings",
            Description = "Feature extraction and sentence embedding models.",
            Queries =
            [
                new HuggingFaceIdealModelQuery { PipelineTag = "feature-extraction", Limit = 12 },
                new HuggingFaceIdealModelQuery { PipelineTag = "sentence-similarity", Limit = 8 }
            ]
        }
    ];

    public async Task<IReadOnlyList<HuggingFaceIdealModelCategoryResult>> ScanAsync(
        int modelsPerCategory = 5,
        string? cookies = null,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        return await ScanAsync(null, modelsPerCategory, cookies, bearerToken, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HuggingFaceIdealModelCategoryResult>> ScanAsync(
        IReadOnlyCollection<string>? categoryIds,
        int modelsPerCategory = 5,
        string? cookies = null,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        return await ScanAsync(
            categoryIds,
            new HuggingFaceIdealModelScanOptions { ModelsPerCategory = modelsPerCategory },
            cookies,
            bearerToken,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<HuggingFaceIdealModelCategoryResult>> ScanAsync(
        IReadOnlyCollection<string>? categoryIds,
        HuggingFaceIdealModelScanOptions options,
        string? cookies = null,
        string? bearerToken = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new HuggingFaceIdealModelScanOptions();
        int limit = Math.Clamp(options.ModelsPerCategory, 1, 20);
        HashSet<string>? requested = categoryIds == null || categoryIds.Count == 0
            ? null
            : new HashSet<string>(categoryIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        Task<HuggingFaceIdealModelCategoryResult>[] scans = DefaultCategories
            .Where(category => requested == null || requested.Contains(category.Id))
            .Select(category => ScanCategoryAsync(category, limit, options, cookies, bearerToken, cancellationToken))
            .ToArray();

        return await Task.WhenAll(scans).ConfigureAwait(false);
    }

    private async Task<HuggingFaceIdealModelCategoryResult> ScanCategoryAsync(
        HuggingFaceIdealModelCategory category,
        int modelsPerCategory,
        HuggingFaceIdealModelScanOptions options,
        string? cookies,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        var models = new Dictionary<string, HuggingFaceIdealModel>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();

        foreach (HuggingFaceIdealModelQuery query in category.Queries)
        {
            try
            {
                foreach (HuggingFaceIdealModel model in await FetchModelsAsync(category, query, modelsPerCategory, cookies, bearerToken, cancellationToken).ConfigureAwait(false))
                {
                    if (!models.TryGetValue(model.ModelId, out HuggingFaceIdealModel? existing) || model.Score > existing.Score)
                        models[model.ModelId] = model;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                errors.Add(ex.Message);
            }
        }

        return new HuggingFaceIdealModelCategoryResult
        {
            Id = category.Id,
            Label = category.Label,
            Description = category.Description,
            Models = models.Values
                .Select(model => ApplyHardwareEstimate(model, options.Hardware, options.Thresholds))
                .Where(model => MeetsThresholds(model, options.Hardware, options.Thresholds))
                .OrderByDescending(model => model.Score)
                .ThenByDescending(model => model.Downloads)
                .ThenByDescending(model => model.Likes)
                .Take(modelsPerCategory)
                .ToArray(),
            Error = models.Count == 0 ? string.Join(" ", errors.Take(2)) : ""
        };
    }

    private async Task<IReadOnlyList<HuggingFaceIdealModel>> FetchModelsAsync(
        HuggingFaceIdealModelCategory category,
        HuggingFaceIdealModelQuery query,
        int modelsPerCategory,
        string? cookies,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(query.ModelId))
            return await FetchModelByIdAsync(category, query, cookies, bearerToken, cancellationToken).ConfigureAwait(false);

        string url = BuildModelSearchUrl(query, Math.Max(query.Limit, modelsPerCategory * 3));
        using var request = CreateHuggingFaceRequest(url, cookies, bearerToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<HuggingFaceIdealModel>();
        int rank = 0;
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            rank++;
            if (item.TryGetProperty("private", out JsonElement privateElement) && privateElement.ValueKind == JsonValueKind.True)
                continue;

            string id = FirstNonEmpty(ReadString(item, "modelId"), ReadString(item, "id"));
            if (string.IsNullOrWhiteSpace(id) || id.Contains(' '))
                continue;

            IReadOnlyList<string> tags = ReadStringArray(item, "tags");
            string pipelineTag = FirstNonEmpty(ReadString(item, "pipeline_tag"), query.PipelineTag);
            string libraryName = ReadString(item, "library_name");
            long downloads = ReadInt64(item, "downloads");
            int likes = (int)Math.Min(int.MaxValue, ReadInt64(item, "likes"));
            long score = CalculateScore(category, query, tags, libraryName, downloads, likes, rank);
            double parameterCountBillion = ReadParameterCountBillion(item, id, tags);

            models.Add(new HuggingFaceIdealModel
            {
                CategoryId = category.Id,
                CategoryLabel = category.Label,
                ModelId = id,
                Url = "https://huggingface.co/" + id.Trim('/'),
                PipelineTag = pipelineTag,
                LibraryName = libraryName,
                Downloads = downloads,
                Likes = likes,
                Tags = tags,
                Score = score,
                ParameterCountBillion = parameterCountBillion,
                ActiveParameterCountBillion = ReadActiveParameterCountBillion(id, tags),
                QuantizationBits = InferQuantizationBits(id, tags, libraryName),
                Reason = BuildReason(category, query, tags, libraryName)
            });
        }

        return models;
    }

    private async Task<IReadOnlyList<HuggingFaceIdealModel>> FetchModelByIdAsync(
        HuggingFaceIdealModelCategory category,
        HuggingFaceIdealModelQuery query,
        string? cookies,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        string requestedId = query.ModelId.Trim().Trim('/');
        string[] parts = requestedId.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return [];

        string url = "https://huggingface.co/api/models/" +
                     Uri.EscapeDataString(parts[0]) + "/" +
                     Uri.EscapeDataString(parts[1]);
        using var request = CreateHuggingFaceRequest(url, cookies, bearerToken);
        using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return [];

        JsonElement item = document.RootElement;
        string id = FirstNonEmpty(ReadString(item, "modelId"), ReadString(item, "id"), requestedId);
        if (string.IsNullOrWhiteSpace(id) || id.Contains(' '))
            return [];

        IReadOnlyList<string> tags = ReadStringArray(item, "tags");
        string pipelineTag = FirstNonEmpty(ReadString(item, "pipeline_tag"), query.PipelineTag);
        string libraryName = FirstNonEmpty(ReadString(item, "library_name"), query.Library);
        long downloads = ReadInt64(item, "downloads");
        int likes = (int)Math.Min(int.MaxValue, ReadInt64(item, "likes"));
        long score = CalculateScore(category, query, tags, libraryName, downloads, likes, 1);
        double parameterCountBillion = ReadParameterCountBillion(item, id, tags);

        return
        [
            new HuggingFaceIdealModel
            {
                CategoryId = category.Id,
                CategoryLabel = category.Label,
                ModelId = id,
                Url = "https://huggingface.co/" + id.Trim('/'),
                PipelineTag = pipelineTag,
                LibraryName = libraryName,
                Downloads = downloads,
                Likes = likes,
                Tags = tags,
                Score = score,
                ParameterCountBillion = parameterCountBillion,
                ActiveParameterCountBillion = ReadActiveParameterCountBillion(id, tags),
                QuantizationBits = InferQuantizationBits(id, tags, libraryName),
                Reason = BuildReason(category, query, tags, libraryName)
            }
        ];
    }

    private static string BuildModelSearchUrl(HuggingFaceIdealModelQuery query, int limit)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("sort", "downloads"),
            new("direction", "-1"),
            new("limit", Math.Clamp(limit, 1, 50).ToString(CultureInfo.InvariantCulture))
        };

        AddParameter(parameters, "pipeline_tag", query.PipelineTag);
        AddParameter(parameters, "filter", query.Filter);
        AddParameter(parameters, "library", query.Library);
        AddParameter(parameters, "search", query.Search);

        return "https://huggingface.co/api/models?" + string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
    }

    private static HttpRequestMessage CreateHuggingFaceRequest(string url, string? cookies, string? bearerToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SocketJack-LlmRuntime", "1.0"));
        if (!string.IsNullOrWhiteSpace(cookies))
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        return request;
    }

    private static long CalculateScore(
        HuggingFaceIdealModelCategory category,
        HuggingFaceIdealModelQuery query,
        IReadOnlyList<string> tags,
        string libraryName,
        long downloads,
        int likes,
        int rank)
    {
        long score = Math.Max(0, downloads) + Math.Max(0, likes) * 2_000L + Math.Max(0, 75 - rank) * 20_000L;
        if (HasTag(tags, "gguf"))
            score += category.Id.Equals("text", StringComparison.OrdinalIgnoreCase) ? 8_000_000 : 1_000_000;
        if (HasTag(tags, "safetensors"))
            score += 1_000_000;
        if (HasTag(tags, "onnx"))
            score += 1_250_000;
        if (libraryName.Equals("diffusers", StringComparison.OrdinalIgnoreCase) || HasTag(tags, "diffusers"))
            score += category.Id.Equals("image", StringComparison.OrdinalIgnoreCase) ? 4_000_000 : 1_500_000;
        if (!string.IsNullOrWhiteSpace(query.Filter) && HasTag(tags, query.Filter))
            score += 2_000_000;
        if (!string.IsNullOrWhiteSpace(query.Library) && libraryName.Equals(query.Library, StringComparison.OrdinalIgnoreCase))
            score += 2_000_000;
        if (!string.IsNullOrWhiteSpace(query.ModelId))
            score += 50_000_000_000L;
        if (HasTag(tags, "endpoints_compatible"))
            score += 250_000;
        return score;
    }

    private static string BuildReason(
        HuggingFaceIdealModelCategory category,
        HuggingFaceIdealModelQuery query,
        IReadOnlyList<string> tags,
        string libraryName)
    {
        if (!string.IsNullOrWhiteSpace(query.Reason))
            return query.Reason.Trim();
        if (category.Id.Equals("text", StringComparison.OrdinalIgnoreCase) && HasTag(tags, "gguf"))
            return "GGUF text-generation model; good fit for local download and runtime loading.";
        if (category.Id.Equals("image", StringComparison.OrdinalIgnoreCase) && (libraryName.Equals("diffusers", StringComparison.OrdinalIgnoreCase) || HasTag(tags, "diffusers")))
            return "Diffusers image model; opens into the existing complete-model downloader.";
        if (category.Id.Equals("video", StringComparison.OrdinalIgnoreCase))
            return "Video generation model; opens the repo so downloadable bundles can be scanned.";
        if (category.Id.Equals("audio", StringComparison.OrdinalIgnoreCase))
            return "Audio model; opens the repo so speech/audio files can be scanned.";
        if (category.Id.Equals("embedding", StringComparison.OrdinalIgnoreCase))
            return "Feature extraction model for embeddings and retrieval workflows.";
        if (!string.IsNullOrWhiteSpace(query.PipelineTag))
            return "Popular " + query.PipelineTag + " model from Hugging Face.";
        return "Popular Hugging Face model.";
    }

    internal static HuggingFaceIdealModel ApplyHardwareEstimate(
        HuggingFaceIdealModel model,
        HuggingFaceIdealModelHardwareProfile? hardware,
        HuggingFaceIdealModelThresholds? thresholds = null)
    {
        hardware ??= new HuggingFaceIdealModelHardwareProfile();
        thresholds ??= new HuggingFaceIdealModelThresholds();
        double parameters = model.ParameterCountBillion;
        double bits = model.QuantizationBits;
        if (parameters <= 0 || bits <= 0)
            return model;

        double activeParameters = model.ActiveParameterCountBillion > 0
            ? Math.Min(model.ActiveParameterCountBillion, parameters)
            : parameters;
        double weightBytes = parameters * 1_000_000_000d * bits / 8d;
        double overheadBytes = Math.Max(768d * 1024d * 1024d, weightBytes * (bits <= 8 ? 0.10d : 0.16d));
        long estimatedVramBytes = (long)Math.Min(long.MaxValue, weightBytes + overheadBytes);
        double tokensPerSecond = 0;
        if (model.CategoryId.Equals("text", StringComparison.OrdinalIgnoreCase) && hardware.VideoMemoryBytes > 0)
        {
            double bandwidthGbps = hardware.EstimatedMemoryBandwidthGbps > 0
                ? hardware.EstimatedMemoryBandwidthGbps
                : hardware.VideoMemoryIsDedicated
                    ? Math.Max(140d, hardware.VideoMemoryBytes / (1024d * 1024d * 1024d) * 28d)
                    : 55d;
            double activeWeightBytes = Math.Max(1d, activeParameters * 1_000_000_000d * bits / 8d);
            double efficiency = bits <= 6 ? 0.55d : bits <= 8 ? 0.50d : 0.42d;
            tokensPerSecond = bandwidthGbps * 1_000_000_000d * efficiency / activeWeightBytes;
            if (estimatedVramBytes > hardware.VideoMemoryBytes)
            {
                double residentRatio = Math.Clamp(hardware.VideoMemoryBytes / (double)estimatedVramBytes, 0.08d, 1d);
                tokensPerSecond *= Math.Clamp(residentRatio * 0.38d, 0.08d, 0.38d);
            }
            tokensPerSecond = Math.Clamp(tokensPerSecond, 0.1d, 999d);
        }

        string fitLabel = "Size estimated";
        double vramPercent = 0;
        if (hardware.VideoMemoryBytes > 0)
        {
            vramPercent = estimatedVramBytes * 100d / hardware.VideoMemoryBytes;
            fitLabel = vramPercent <= 72d ? "Comfortable fit" :
                vramPercent <= thresholds.MaxVramUsagePercent ? "Fits threshold" :
                vramPercent <= 100d ? "Tight fit" : "CPU offload likely";
        }

        long hardwareScore = 0;
        if (hardware.VideoMemoryBytes > 0)
        {
            hardwareScore += vramPercent <= thresholds.MaxVramUsagePercent ? 30_000_000_000L : -30_000_000_000L;
            hardwareScore += (long)Math.Min(10_000_000_000d, tokensPerSecond * 100_000_000d);
        }

        string estimateReason = hardware.VideoMemoryBytes > 0
            ? $"Estimated {FormatParameters(parameters)} parameters at {bits:0.#}-bit: {FormatBytes(estimatedVramBytes)} VRAM, {fitLabel.ToLowerInvariant()} on {hardware.DisplayName}." +
              (tokensPerSecond > 0 ? $" Rough generation estimate: {tokensPerSecond:0.#} tok/s." : "")
            : $"Estimated {FormatParameters(parameters)} parameters at {bits:0.#}-bit; GPU capacity was not detected.";

        return new HuggingFaceIdealModel
        {
            CategoryId = model.CategoryId,
            CategoryLabel = model.CategoryLabel,
            ModelId = model.ModelId,
            Url = model.Url,
            PipelineTag = model.PipelineTag,
            LibraryName = model.LibraryName,
            Downloads = model.Downloads,
            Likes = model.Likes,
            Tags = model.Tags,
            Score = model.Score + hardwareScore,
            Reason = model.Reason,
            ParameterCountBillion = parameters,
            ActiveParameterCountBillion = model.ActiveParameterCountBillion,
            QuantizationBits = bits,
            EstimatedVramBytes = estimatedVramBytes,
            EstimatedTokensPerSecond = tokensPerSecond,
            EstimatedVramPercent = vramPercent,
            FitLabel = fitLabel,
            HardwareEstimate = estimateReason
        };
    }

    internal static bool MeetsThresholds(
        HuggingFaceIdealModel model,
        HuggingFaceIdealModelHardwareProfile? hardware,
        HuggingFaceIdealModelThresholds? thresholds)
    {
        thresholds ??= new HuggingFaceIdealModelThresholds();
        hardware ??= new HuggingFaceIdealModelHardwareProfile();
        if (model.ParameterCountBillion > 0)
        {
            if (thresholds.MinimumParametersBillion > 0 && model.ParameterCountBillion < thresholds.MinimumParametersBillion)
                return false;
            if (thresholds.MaximumParametersBillion > 0 && model.ParameterCountBillion > thresholds.MaximumParametersBillion)
                return false;
        }
        if (model.EstimatedTokensPerSecond > 0 &&
            thresholds.MinimumTokensPerSecond > 0 &&
            model.EstimatedTokensPerSecond < thresholds.MinimumTokensPerSecond)
            return false;
        if (model.EstimatedVramBytes > 0 && hardware.VideoMemoryBytes > 0 && thresholds.MaxVramUsagePercent > 0 &&
            model.EstimatedVramBytes > hardware.VideoMemoryBytes * thresholds.MaxVramUsagePercent / 100d)
            return false;
        return true;
    }

    private static double ReadParameterCountBillion(JsonElement item, string modelId, IReadOnlyList<string> tags)
    {
        if (item.TryGetProperty("safetensors", out JsonElement safetensors) && safetensors.ValueKind == JsonValueKind.Object)
        {
            long total = ReadInt64(safetensors, "total");
            if (total <= 0 && safetensors.TryGetProperty("parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in parameters.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out long count) && count > 0)
                        total += count;
            }
            if (total > 0)
                return total / 1_000_000_000d;
        }

        return ReadLargestParameterCountBillion(string.Join(" ", tags.Prepend(modelId)));
    }

    internal static double ReadLargestParameterCountBillion(string text)
    {
        double largest = 0;
        foreach (Match match in Regex.Matches(text ?? "", @"(?<![A-Za-z0-9])(\d+(?:\.\d+)?)\s*[Bb](?![A-Za-z])", RegexOptions.CultureInvariant))
        {
            if (double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                largest = Math.Max(largest, value);
        }
        return largest;
    }

    private static double ReadActiveParameterCountBillion(string modelId, IReadOnlyList<string> tags)
    {
        string text = string.Join(" ", tags.Prepend(modelId));
        Match match = Regex.Match(text, @"(?:^|[-_/\s])A(\d+(?:\.\d+)?)B(?:$|[-_/\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }

    internal static double InferQuantizationBits(string modelId, IReadOnlyList<string> tags, string libraryName = "")
    {
        string text = string.Join(" ", tags.Prepend(modelId).Append(libraryName));
        Match gguf = Regex.Match(text, @"(?:^|[-_/\s])Q([2-8])(?:_|[-/\s]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (gguf.Success && double.TryParse(gguf.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out double bits))
            return bits;
        if (ContainsAnyText(text, "int4", "4bit", "4-bit", "gptq", "awq")) return 4;
        if (ContainsAnyText(text, "int8", "8bit", "8-bit", "fp8")) return 8;
        if (ContainsAnyText(text, "bf16", "fp16", "float16")) return 16;
        if (ContainsAnyText(text, "fp32", "float32")) return 32;
        if (ContainsAnyText(text, "gguf")) return 4.5d;
        return 16d;
    }

    private static bool ContainsAnyText(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static string FormatParameters(double billions) =>
        billions >= 1d ? billions.ToString("0.#", CultureInfo.InvariantCulture) + "B" :
        (billions * 1000d).ToString("0", CultureInfo.InvariantCulture) + "M";

    private static string FormatBytes(long bytes)
    {
        double gib = bytes / (1024d * 1024d * 1024d);
        return gib >= 10d ? gib.ToString("0", CultureInfo.InvariantCulture) + " GB" : gib.ToString("0.0", CultureInfo.InvariantCulture) + " GB";
    }

    private static bool HasTag(IReadOnlyList<string> tags, string tag) =>
        tags.Any(candidate => candidate.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                              candidate.Contains(tag, StringComparison.OrdinalIgnoreCase));

    private static void AddParameter(List<KeyValuePair<string, string>> parameters, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            parameters.Add(new KeyValuePair<string, string>(key, value.Trim()));
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

    private static string ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static long ReadInt64(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed))
            return parsed;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
            return parsed;
        return 0;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed class HuggingFaceIdealModelCategory
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Description { get; init; } = "";

    public IReadOnlyList<HuggingFaceIdealModelQuery> Queries { get; init; } = [];
}

public sealed class HuggingFaceIdealModelScanOptions
{
    public int ModelsPerCategory { get; init; } = 6;

    public HuggingFaceIdealModelThresholds Thresholds { get; init; } = new();

    public HuggingFaceIdealModelHardwareProfile Hardware { get; init; } = new();
}

public sealed class HuggingFaceIdealModelThresholds
{
    [JsonPropertyName("modelLimit")]
    public int ModelLimit { get; init; } = 6;

    [JsonPropertyName("minimumTokensPerSecond")]
    public double MinimumTokensPerSecond { get; init; }

    [JsonPropertyName("maxVramUsagePercent")]
    public double MaxVramUsagePercent { get; init; } = 90d;

    [JsonPropertyName("minimumParametersBillion")]
    public double MinimumParametersBillion { get; init; }

    [JsonPropertyName("maximumParametersBillion")]
    public double MaximumParametersBillion { get; init; }
}

public sealed class HuggingFaceIdealModelHardwareProfile
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = "this PC";

    [JsonPropertyName("videoMemoryBytes")]
    public long VideoMemoryBytes { get; init; }

    [JsonPropertyName("videoMemoryIsDedicated")]
    public bool VideoMemoryIsDedicated { get; init; }

    [JsonPropertyName("estimatedMemoryBandwidthGbps")]
    public double EstimatedMemoryBandwidthGbps { get; init; }
}

public sealed class HuggingFaceIdealModelQuery
{
    public string ModelId { get; init; } = "";

    public string PipelineTag { get; init; } = "";

    public string Filter { get; init; } = "";

    public string Library { get; init; } = "";

    public string Search { get; init; } = "";

    public string Reason { get; init; } = "";

    public int Limit { get; init; } = 10;
}

public sealed class HuggingFaceIdealModelCategoryResult
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("models")]
    public IReadOnlyList<HuggingFaceIdealModel> Models { get; init; } = [];

    [JsonPropertyName("error")]
    public string Error { get; init; } = "";
}

public sealed class HuggingFaceIdealModel
{
    [JsonPropertyName("categoryId")]
    public string CategoryId { get; init; } = "";

    [JsonPropertyName("categoryLabel")]
    public string CategoryLabel { get; init; } = "";

    [JsonPropertyName("modelId")]
    public string ModelId { get; init; } = "";

    [JsonPropertyName("url")]
    public string Url { get; init; } = "";

    [JsonPropertyName("pipelineTag")]
    public string PipelineTag { get; init; } = "";

    [JsonPropertyName("libraryName")]
    public string LibraryName { get; init; } = "";

    [JsonPropertyName("downloads")]
    public long Downloads { get; init; }

    [JsonPropertyName("likes")]
    public int Likes { get; init; }

    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    [JsonPropertyName("score")]
    public long Score { get; init; }

    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";

    [JsonPropertyName("parameterCountBillion")]
    public double ParameterCountBillion { get; init; }

    [JsonPropertyName("activeParameterCountBillion")]
    public double ActiveParameterCountBillion { get; init; }

    [JsonPropertyName("quantizationBits")]
    public double QuantizationBits { get; init; }

    [JsonPropertyName("estimatedVramBytes")]
    public long EstimatedVramBytes { get; init; }

    [JsonPropertyName("estimatedTokensPerSecond")]
    public double EstimatedTokensPerSecond { get; init; }

    [JsonPropertyName("estimatedVramPercent")]
    public double EstimatedVramPercent { get; init; }

    [JsonPropertyName("fitLabel")]
    public string FitLabel { get; init; } = "";

    [JsonPropertyName("hardwareEstimate")]
    public string HardwareEstimate { get; init; } = "";
}
