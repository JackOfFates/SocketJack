using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

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
        int limit = Math.Clamp(modelsPerCategory, 1, 12);
        HashSet<string>? requested = categoryIds == null || categoryIds.Count == 0
            ? null
            : new HashSet<string>(categoryIds.Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
        Task<HuggingFaceIdealModelCategoryResult>[] scans = DefaultCategories
            .Where(category => requested == null || requested.Contains(category.Id))
            .Select(category => ScanCategoryAsync(category, limit, cookies, bearerToken, cancellationToken))
            .ToArray();

        return await Task.WhenAll(scans).ConfigureAwait(false);
    }

    private async Task<HuggingFaceIdealModelCategoryResult> ScanCategoryAsync(
        HuggingFaceIdealModelCategory category,
        int modelsPerCategory,
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
}
