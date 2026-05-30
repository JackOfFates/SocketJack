using System.Globalization;
using System.Text.Json;

namespace LlmRuntime;

internal static class DiffusersModelConfigMetadata
{
    private const int MaxConfigFilesToInspect = 64;

    public static DiffusersModelConfigInfo? FindBest(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        try
        {
            return Directory.EnumerateFiles(directory, "config.json", SearchOption.AllDirectories)
                .Take(MaxConfigFilesToInspect)
                .Select(path => TryRead(path, directory))
                .Where(info => info != null && info.IsRecognizedGenerationConfig)
                .OrderByDescending(info => info!.IsWanConfig)
                .ThenByDescending(info => string.Equals(info!.ModelType, "t2v", StringComparison.OrdinalIgnoreCase))
                .ThenBy(info => info!.RelativePath, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    public static string RefineTask(string currentTask, DiffusersModelConfigInfo? config)
    {
        string inferred = config?.InferredTask ?? "";
        if (string.IsNullOrWhiteSpace(inferred))
            return currentTask;

        string normalized = (currentTask ?? "").Trim();
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.Equals("llm", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("llm.text-generation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("onnx", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("pytorch", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("torch", StringComparison.OrdinalIgnoreCase))
            return inferred;

        return normalized;
    }

    public static void ApplyToMetadata(Dictionary<string, string> metadata, DiffusersModelConfigInfo? config)
    {
        if (metadata == null || config == null)
            return;

        SetIfMissing(metadata, "architecture", config.ClassName);
        SetIfPresent(metadata, "diffusersConfigPath", config.RelativePath);
        SetIfPresent(metadata, "diffusersClassName", config.ClassName);
        SetIfPresent(metadata, "diffusersVersion", config.DiffusersVersion);
        SetIfPresent(metadata, "diffusersModelType", config.ModelType);
        SetIfPresent(metadata, "diffusersConfigSummary", config.ParameterSummary);

        SetIfPresent(metadata, "wan_dim", FormatNullable(config.Dim));
        SetIfPresent(metadata, "wan_ffn_dim", FormatNullable(config.FfnDim));
        SetIfPresent(metadata, "wan_freq_dim", FormatNullable(config.FreqDim));
        SetIfPresent(metadata, "wan_in_dim", FormatNullable(config.InDim));
        SetIfPresent(metadata, "wan_out_dim", FormatNullable(config.OutDim));
        SetIfPresent(metadata, "wan_num_heads", FormatNullable(config.NumHeads));
        SetIfPresent(metadata, "wan_num_layers", FormatNullable(config.NumLayers));
        SetIfPresent(metadata, "wan_text_len", FormatNullable(config.TextLen));
        SetIfPresent(metadata, "wan_eps", config.Eps);

        if (!string.IsNullOrWhiteSpace(config.PipelineClass))
        {
            SetIfMissing(metadata, "pipelineClass", config.PipelineClass);
            SetIfMissing(metadata, "pipeline_class", config.PipelineClass);
        }

        if (!string.IsNullOrWhiteSpace(config.InferredBaseModel))
        {
            SetIfMissing(metadata, "baseModel", config.InferredBaseModel);
            SetIfMissing(metadata, "base_model", config.InferredBaseModel);
            SetIfMissing(metadata, "baseModels", config.InferredBaseModel);
            SetIfMissing(metadata, "base_models", config.InferredBaseModel);
        }
    }

    private static DiffusersModelConfigInfo? TryRead(string path, string rootDirectory)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string className = ReadString(root, "_class_name") ?? "";
            string modelType = ReadString(root, "model_type") ?? "";
            string diffusersVersion = ReadString(root, "_diffusers_version") ?? "";
            if (string.IsNullOrWhiteSpace(className) && string.IsNullOrWhiteSpace(modelType))
                return null;

            return new DiffusersModelConfigInfo
            {
                Path = path,
                RelativePath = Path.GetRelativePath(rootDirectory, path).Replace('\\', '/'),
                ClassName = className,
                DiffusersVersion = diffusersVersion,
                ModelType = modelType,
                Dim = ReadLong(root, "dim"),
                FfnDim = ReadLong(root, "ffn_dim"),
                FreqDim = ReadLong(root, "freq_dim"),
                InDim = ReadLong(root, "in_dim"),
                OutDim = ReadLong(root, "out_dim"),
                NumHeads = ReadLong(root, "num_heads"),
                NumLayers = ReadLong(root, "num_layers"),
                TextLen = ReadLong(root, "text_len"),
                Eps = ReadScalarText(root, "eps")
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static long? ReadLong(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed))
            return parsed;
        return null;
    }

    private static string ReadScalarText(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return "";
        if (value.ValueKind == JsonValueKind.String)
            return value.GetString() ?? "";
        if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            return value.GetRawText();
        return "";
    }

    private static void SetIfPresent(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            metadata[key] = value!;
    }

    private static void SetIfMissing(Dictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !metadata.ContainsKey(key))
            metadata[key] = value!;
    }

    private static string FormatNullable(long? value) =>
        value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "";
}

internal sealed class DiffusersModelConfigInfo
{
    public string Path { get; init; } = "";

    public string RelativePath { get; init; } = "";

    public string ClassName { get; init; } = "";

    public string DiffusersVersion { get; init; } = "";

    public string ModelType { get; init; } = "";

    public long? Dim { get; init; }

    public long? FfnDim { get; init; }

    public long? FreqDim { get; init; }

    public long? InDim { get; init; }

    public long? OutDim { get; init; }

    public long? NumHeads { get; init; }

    public long? NumLayers { get; init; }

    public long? TextLen { get; init; }

    public string Eps { get; init; } = "";

    public bool IsWanConfig => ClassName.Contains("Wan", StringComparison.OrdinalIgnoreCase);

    public bool IsRecognizedGenerationConfig =>
        IsWanConfig &&
        (ModelType.Equals("t2v", StringComparison.OrdinalIgnoreCase) ||
         ModelType.Equals("i2v", StringComparison.OrdinalIgnoreCase) ||
         ModelType.Equals("v2v", StringComparison.OrdinalIgnoreCase) ||
         ModelType.Contains("video", StringComparison.OrdinalIgnoreCase));

    public string InferredTask
    {
        get
        {
            if (!IsWanConfig)
                return "";
            if (ModelType.Equals("i2v", StringComparison.OrdinalIgnoreCase))
                return "video.image-to-video";
            if (ModelType.Equals("v2v", StringComparison.OrdinalIgnoreCase))
                return "video.video-to-video";
            if (ModelType.Equals("t2v", StringComparison.OrdinalIgnoreCase) ||
                ModelType.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "video.text-to-video";
            return "";
        }
    }

    public string PipelineClass
    {
        get
        {
            if (!IsWanConfig)
                return "";
            return ModelType.Equals("i2v", StringComparison.OrdinalIgnoreCase)
                ? "WanImageToVideoPipeline"
                : "WanPipeline";
        }
    }

    public string InferredBaseModel
    {
        get
        {
            if (!IsWanConfig)
                return "";

            string sourceText = (Path + " " + RelativePath).ToLowerInvariant();
            bool isSmallWan = (Dim.HasValue && Dim.Value <= 2048) ||
                              (NumHeads.HasValue && NumHeads.Value <= 16) ||
                              (NumLayers.HasValue && NumLayers.Value <= 32);

            if (ModelType.Equals("i2v", StringComparison.OrdinalIgnoreCase))
                return sourceText.Contains("720", StringComparison.OrdinalIgnoreCase)
                    ? "Wan-AI/Wan2.1-I2V-14B-720P-Diffusers"
                    : "Wan-AI/Wan2.1-I2V-14B-480P-Diffusers";

            if (sourceText.Contains("wan2.2", StringComparison.OrdinalIgnoreCase) ||
                sourceText.Contains("wan2_2", StringComparison.OrdinalIgnoreCase) ||
                sourceText.Contains("wan-2.2", StringComparison.OrdinalIgnoreCase))
                return "Wan-AI/Wan2.2-T2V-A14B-Diffusers";

            return isSmallWan
                ? "Wan-AI/Wan2.1-T2V-1.3B-Diffusers"
                : "Wan-AI/Wan2.1-T2V-14B-Diffusers";
        }
    }

    public string ParameterSummary
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ClassName))
                parts.Add(ClassName);
            if (!string.IsNullOrWhiteSpace(ModelType))
                parts.Add(ModelType);
            Add(parts, "dim", Dim);
            Add(parts, "layers", NumLayers);
            Add(parts, "heads", NumHeads);
            Add(parts, "ffn", FfnDim);
            Add(parts, "text", TextLen);
            return string.Join(", ", parts);
        }
    }

    private static void Add(List<string> parts, string label, long? value)
    {
        if (value.HasValue)
            parts.Add(label + " " + value.Value.ToString(CultureInfo.InvariantCulture));
    }
}
