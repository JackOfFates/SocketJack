using System.Text.Json;

namespace LlmRuntime;

public static class CompleteModelManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string WriteManifest(
        string modelDirectory,
        string? sourceRepository = null,
        string? revision = null,
        string? task = null,
        string? format = null,
        IReadOnlyList<string>? sourcePaths = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
            throw new ArgumentException("Model directory is required.", nameof(modelDirectory));
        if (!Directory.Exists(modelDirectory))
            throw new DirectoryNotFoundException("Model directory does not exist: " + modelDirectory);

        string directory = Path.GetFullPath(modelDirectory);
        string manifestPath = Path.Combine(directory, "manifest.json");
        string displayName = BuildDisplayName(sourceRepository, directory);
        string normalizedFormat = string.IsNullOrWhiteSpace(format) ? "pytorch" : format!.Trim().ToLowerInvariant();
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(Path.GetFullPath(path), manifestPath, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            throw new FileNotFoundException("No model files were found in " + directory);

        DiffusersModelConfigInfo? diffusersConfig = DiffusersModelConfigMetadata.FindBest(directory);
        string normalizedTask = DiffusersModelConfigMetadata.RefineTask(
            NormalizeTask(task, sourceRepository, sourcePaths),
            diffusersConfig);
        var mergedMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = string.IsNullOrWhiteSpace(sourceRepository) ? "local" : "huggingface:" + sourceRepository!.Trim('/'),
            ["revision"] = string.IsNullOrWhiteSpace(revision) ? "main" : revision!,
            ["sourcePaths"] = sourcePaths == null || sourcePaths.Count == 0 ? "" : string.Join("|", sourcePaths),
            ["layout"] = "complete-model"
        };
        if (metadata != null)
        {
            foreach (KeyValuePair<string, string> pair in metadata)
                mergedMetadata[pair.Key] = pair.Value;
        }
        AddModelCardMetadata(directory, mergedMetadata);
        AddDiffusersGgufMetadata(directory, files, normalizedTask, normalizedFormat, mergedMetadata);
        DiffusersModelConfigMetadata.ApplyToMetadata(mergedMetadata, diffusersConfig);

        var manifest = new
        {
            id = SanitizeId((string.IsNullOrWhiteSpace(sourceRepository) ? displayName : sourceRepository!.Trim('/').Replace('/', '-')) + "-" + normalizedFormat),
            name = displayName,
            type = normalizedTask,
            format = normalizedFormat,
            precision = DetectPrecision(files),
            components = BuildComponentMap(directory, files, normalizedFormat),
            recommendedProviders = new[] { "Cuda", "DirectML", "Cpu" },
            requiredMemoryBytes = EstimateDirectoryBytes(directory),
            metadata = mergedMetadata
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return manifestPath;
    }

    private static Dictionary<string, string> BuildComponentMap(string directory, IReadOnlyList<string> files, string format)
    {
        if (string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase))
        {
            var ggufFiles = files
                .Where(file => file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ggufFiles.Length > 0)
                return BuildGgufComponentMap(directory, ggufFiles);
        }

        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string relativePath = Path.GetRelativePath(directory, files[i]).Replace('\\', '/');
            string key = SanitizeId(Path.GetFileNameWithoutExtension(relativePath));
            if (string.IsNullOrWhiteSpace(key))
                key = "file_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            while (components.ContainsKey(key))
                key += "_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            components[key] = relativePath;
        }

        return components;
    }

    private static Dictionary<string, string> BuildGgufComponentMap(string directory, IReadOnlyList<string> files)
    {
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Count; i++)
        {
            string relativePath = Path.GetRelativePath(directory, files[i]).Replace('\\', '/');
            string key = InferGgufComponentKey(files[i], i);
            while (components.ContainsKey(key))
                key += "_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            components[key] = relativePath;
        }

        return components;
    }

    private static string InferGgufComponentKey(string file, int index)
    {
        string text = Path.GetFileNameWithoutExtension(file).Replace('\\', '/').ToLowerInvariant();
        if (ContainsAny(text, "text_encoder", "text-encoder", "umt5", "t5"))
            return "text_encoder";
        if (ContainsAny(text, "vae", "autoencoder"))
            return "vae";
        if (ContainsAny(text, "low-noise", "lownoise", "low_noise"))
            return "transformer_2";
        if (ContainsAny(text, "high-noise", "highnoise", "high_noise"))
            return "transformer";
        if (ContainsAny(text, "transformer2", "transformer_2", "transformer-2"))
            return "transformer_2";
        return index == 0 ? "transformer" : "transformer_" + (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string NormalizeTask(string? task, string? sourceRepository, IReadOnlyList<string>? sourcePaths)
    {
        string text = string.Join(" ", new[]
        {
            task ?? "",
            sourceRepository ?? "",
            sourcePaths == null ? "" : string.Join(" ", sourcePaths)
        }).ToLowerInvariant();

        if (ContainsAny(text, "video", "text-to-video", "image-to-video", "video-to-video", "img2vid", "vid2vid", "t2v", "i2v", "v2v", "wan2", "hunyuanvideo", "ltxv", "mochi", "motifv"))
            return ContainsAny(text, "image-to-video", "img2vid", "i2v") ? "video.image-to-video" : "video.text-to-video";
        if (ContainsAny(text, "audio", "speech", "text-to-speech", "audio-to-audio", "voice-conversion", "voice-clone", "tts", "whisper", "wav2vec", "bark", "musicgen", "moshi", "moshiko"))
            return "audio.generation";
        if (ContainsAny(text, "image", "text-to-image", "image-to-image", "img2img", "controlnet", "inpaint", "stable-diffusion", "diffusers", "sdxl", "flux", "unet", "vae"))
            return ContainsAny(text, "image-to-image", "img2img", "controlnet", "inpaint") ? "image.image-to-image" : "image.text-to-image";
        if (ContainsAny(text, "embed", "embedding"))
            return "embedding";
        return "llm.text-generation";
    }

    private static string DetectPrecision(IReadOnlyList<string> files)
    {
        foreach (string file in files)
        {
            string quant = ModelHeuristics.DetectQuantType(file);
            if (!string.IsNullOrWhiteSpace(quant))
                return quant.ToLowerInvariant();
        }

        return "";
    }

    private static void AddDiffusersGgufMetadata(
        string directory,
        IReadOnlyList<string> files,
        string task,
        string format,
        Dictionary<string, string> metadata)
    {
        if (!string.Equals(format, "gguf", StringComparison.OrdinalIgnoreCase))
            return;

        var ggufFiles = files
            .Where(file => file.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (ggufFiles.Length == 0)
            return;

        metadata["layout"] = "complete-model";
        metadata["diffusersLayout"] = "gguf";
        metadata["ggufFiles"] = string.Join("|", ggufFiles.Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')));

        string text = string.Join(" ", new[]
        {
            directory,
            task,
            string.Join(" ", ggufFiles.Select(Path.GetFileName))
        });

        if (task.Contains("video", StringComparison.OrdinalIgnoreCase))
        {
            if (!metadata.ContainsKey("pipelineClass") && !metadata.ContainsKey("pipeline_class"))
            {
                string pipelineClass = InferDiffusersVideoPipelineClass(text);
                if (!string.IsNullOrWhiteSpace(pipelineClass))
                {
                    metadata["pipelineClass"] = pipelineClass;
                    metadata["pipeline_class"] = pipelineClass;
                }
            }

            if (!metadata.ContainsKey("baseModel") && !metadata.ContainsKey("base_model"))
            {
                string baseModel = InferDiffusersVideoBaseModel(text);
                if (!string.IsNullOrWhiteSpace(baseModel))
                {
                    metadata["baseModel"] = baseModel;
                    metadata["base_model"] = baseModel;
                    metadata["baseModels"] = baseModel;
                }
            }
        }
    }

    private static string InferDiffusersVideoPipelineClass(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        if (ContainsAny(text, "ltx-video", "ltxv", "ltx_video", "ltx-video"))
            return "LTXPipeline";
        if (ContainsAny(text, "wan2", "wan-", "wan_"))
            return "WanPipeline";
        return "";
    }

    private static string InferDiffusersVideoBaseModel(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        if (ContainsAny(text, "ltx-video", "ltxv", "ltx_video", "ltx-video"))
            return "Lightricks/LTX-Video";
        if (ContainsAny(text, "wan2.2", "wan2_2", "wan-2.2", "wan_2.2"))
            return "Wan-AI/Wan2.2-T2V-A14B-Diffusers";
        if (ContainsAny(text, "wan2.1", "wan2_1", "wan-2.1", "wan_2.1"))
            return ContainsAny(text, "1.3b", "1_3b") ? "Wan-AI/Wan2.1-T2V-1.3B-Diffusers" : "Wan-AI/Wan2.1-T2V-14B-Diffusers";
        return "";
    }

    private static long EstimateDirectoryBytes(string directory)
    {
        long total = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { }
        }

        return total;
    }

    private static string BuildDisplayName(string? sourceRepository, string directory)
    {
        if (!string.IsNullOrWhiteSpace(sourceRepository))
        {
            string repo = sourceRepository!.Trim().Replace('\\', '/').Trim('/');
            if (!string.IsNullOrWhiteSpace(repo))
                return repo.Replace('/', ' ');
        }

        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "Complete model" : name;
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

    private static void AddModelCardMetadata(string directory, Dictionary<string, string> metadata)
    {
        string readmePath = Path.Combine(directory, "README.md");
        if (!File.Exists(readmePath))
        {
            AddBaseModelMetadata(metadata, []);
            NormalizeAdapterMetadata(metadata, []);
            return;
        }

        var tags = new List<string>();
        var baseModels = new List<string>();
        try
        {
            string[] lines = File.ReadAllLines(readmePath);
            if (lines.Length == 0 || !string.Equals(lines[0].Trim(), "---", StringComparison.Ordinal))
            {
                AddBaseModelMetadata(metadata, []);
                NormalizeAdapterMetadata(metadata, tags);
                return;
            }

            string currentKey = "";
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (string.Equals(line, "---", StringComparison.Ordinal))
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("-", StringComparison.Ordinal) && string.Equals(currentKey, "tags", StringComparison.OrdinalIgnoreCase))
                {
                    string tag = Unquote(line.Substring(1).Trim());
                    if (!string.IsNullOrWhiteSpace(tag))
                        tags.Add(tag);
                    continue;
                }

                if (line.StartsWith("-", StringComparison.Ordinal) && string.Equals(currentKey, "base_model", StringComparison.OrdinalIgnoreCase))
                {
                    AddYamlScalarOrArrayValues(baseModels, line.Substring(1).Trim());
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                string key = line.Substring(0, colon).Trim();
                string value = Unquote(line.Substring(colon + 1).Trim());
                currentKey = key;
                if (string.Equals(key, "base_model", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    AddYamlScalarOrArrayValues(baseModels, value);
                else if (string.Equals(key, "tags", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                    AddYamlScalarOrArrayValues(tags, value);
            }
        }
        catch
        {
        }

        AddBaseModelMetadata(metadata, baseModels);
        NormalizeAdapterMetadata(metadata, tags);
    }

    private static void AddBaseModelMetadata(Dictionary<string, string> metadata, IEnumerable<string> values)
    {
        var baseModels = NormalizeMetadataValues(values)
            .Concat(NormalizeMetadataValues([
                metadata.TryGetValue("baseModel", out string? baseModel) ? baseModel : "",
                metadata.TryGetValue("base_model", out string? snakeBaseModel) ? snakeBaseModel : "",
                metadata.TryGetValue("baseModels", out string? baseModelList) ? baseModelList : "",
                metadata.TryGetValue("base_models", out string? snakeBaseModelList) ? snakeBaseModelList : ""
            ]))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (baseModels.Length == 0)
            return;

        metadata["baseModel"] = baseModels[0];
        metadata["base_model"] = baseModels[0];
        metadata["baseModels"] = string.Join("|", baseModels);
        metadata["base_models"] = string.Join("|", baseModels);
    }

    private static void NormalizeAdapterMetadata(Dictionary<string, string> metadata, IReadOnlyList<string> tags)
    {
        bool isLora = tags.Any(tag => tag.Contains("lora", StringComparison.OrdinalIgnoreCase)) ||
                      (metadata.TryGetValue("adapterType", out string? adapterType) && adapterType.Contains("lora", StringComparison.OrdinalIgnoreCase)) ||
                      (metadata.TryGetValue("adapter_type", out string? snakeAdapterType) && snakeAdapterType.Contains("lora", StringComparison.OrdinalIgnoreCase));
        if (!isLora)
            return;

        metadata["adapterType"] = "lora";
        metadata["adapter_type"] = "lora";
        metadata["adapterRequiresBaseModel"] = "true";
        metadata["adapter_requires_base_model"] = "true";
    }

    private static void AddYamlScalarOrArrayValues(List<string> values, string rawValue)
    {
        foreach (string value in NormalizeMetadataValues([rawValue]))
            values.Add(value);
    }

    private static IEnumerable<string> NormalizeMetadataValues(IEnumerable<string?> values)
    {
        foreach (string? raw in values)
        {
            string value = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string[] parts = value.Contains('|')
                ? value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : IsInlineArray(value)
                    ? value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [value];

            foreach (string part in parts)
            {
                string normalized = Unquote(part.Trim());
                if (!string.IsNullOrWhiteSpace(normalized))
                    yield return normalized;
            }
        }
    }

    private static bool IsInlineArray(string value) =>
        value.Length >= 2 && value[0] == '[' && value[^1] == ']';

    private static string Unquote(string value)
    {
        value = (value ?? "").Trim();
        return value.Length >= 2 &&
               ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
    }

    private static string SanitizeId(string value)
    {
        var chars = value.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray();
        string sanitized = new string(chars).Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "complete-model" : sanitized;
    }
}
