using System.Text.Json;

namespace LlmRuntime;

public static class OnnxModelManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string WriteSingleFileManifest(string onnxFilePath, string? sourceRepository = null, string? sourcePath = null, string? revision = null)
    {
        if (string.IsNullOrWhiteSpace(onnxFilePath))
            throw new ArgumentException("ONNX file path is required.", nameof(onnxFilePath));
        if (!File.Exists(onnxFilePath))
            throw new FileNotFoundException("ONNX file does not exist.", onnxFilePath);
        if (!onnxFilePath.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The manifest writer only accepts .onnx files.", nameof(onnxFilePath));

        string directory = Path.GetDirectoryName(Path.GetFullPath(onnxFilePath)) ?? Environment.CurrentDirectory;
        string fileName = Path.GetFileName(onnxFilePath);
        string displayName = Path.GetFileNameWithoutExtension(fileName);
        string idBase = string.IsNullOrWhiteSpace(sourceRepository)
            ? displayName
            : sourceRepository.Trim().Replace('\\', '/').Trim('/').Replace('/', '-') + "-" + displayName;
        string manifestPath = Path.Combine(directory, displayName + ".jackonnx.json");
        string id = SanitizeId(idBase);

        var manifest = new
        {
            id,
            name = displayName,
            type = InferTaskType(fileName, sourcePath),
            format = "onnx",
            precision = "",
            components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["model"] = fileName
            },
            recommendedProviders = new[] { "Cuda", "DirectML", "Cpu" },
            requiredMemoryBytes = new FileInfo(onnxFilePath).Length,
            metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source"] = string.IsNullOrWhiteSpace(sourceRepository) ? "local" : "huggingface:" + sourceRepository.Trim('/'),
                ["sourcePath"] = sourcePath ?? fileName,
                ["revision"] = string.IsNullOrWhiteSpace(revision) ? "main" : revision!
            }
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return manifestPath;
    }

    public static string WriteFolderManifest(
        string outputDirectory,
        string? sourceRepository = null,
        IReadOnlyList<string>? sourcePaths = null,
        string? revision = null,
        string? task = null,
        string? precision = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
            throw new ArgumentException("ONNX output directory is required.", nameof(outputDirectory));
        if (!Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException("ONNX output directory does not exist: " + outputDirectory);

        string directory = Path.GetFullPath(outputDirectory);
        var onnxFiles = Directory.EnumerateFiles(directory, "*.onnx", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (onnxFiles.Length == 0)
            throw new FileNotFoundException("No .onnx files were found in " + directory);

        string displayName = BuildDisplayName(sourceRepository, directory);
        string idBase = string.IsNullOrWhiteSpace(sourceRepository)
            ? displayName
            : sourceRepository.Trim().Replace('\\', '/').Trim('/').Replace('/', '-') + "-" + displayName;
        string manifestPath = Path.Combine(directory, "manifest.jackonnx.json");
        string id = SanitizeId(idBase);
        var components = BuildComponentMap(directory, onnxFiles);
        var mergedMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source"] = string.IsNullOrWhiteSpace(sourceRepository) ? "local" : "huggingface:" + sourceRepository.Trim('/'),
            ["sourcePaths"] = sourcePaths == null || sourcePaths.Count == 0 ? "" : string.Join("|", sourcePaths),
            ["revision"] = string.IsNullOrWhiteSpace(revision) ? "main" : revision!,
            ["task"] = string.IsNullOrWhiteSpace(task) ? InferTaskType(onnxFiles[0], onnxFiles[0]) : task!,
            ["converter"] = "LlmRuntime.ModelConversionService"
        };
        if (metadata != null)
        {
            foreach (var pair in metadata)
                mergedMetadata[pair.Key] = pair.Value;
        }

        var manifest = new
        {
            id,
            name = displayName,
            type = string.IsNullOrWhiteSpace(task) ? InferTaskType(onnxFiles[0], onnxFiles[0]) : NormalizeTask(task!),
            format = "onnx",
            precision = precision ?? "",
            components,
            recommendedProviders = new[] { "Cuda", "DirectML", "Cpu" },
            requiredMemoryBytes = EstimateDirectoryBytes(directory),
            metadata = mergedMetadata
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return manifestPath;
    }

    private static string InferTaskType(string fileName, string? sourcePath)
    {
        string value = ((sourcePath ?? "") + "/" + fileName).ToLowerInvariant();
        if (value.Contains("embed", StringComparison.Ordinal) || value.Contains("embedding", StringComparison.Ordinal))
            return "embedding";
        if (value.Contains("clip", StringComparison.Ordinal) || value.Contains("vision", StringComparison.Ordinal))
            return "vlm";
        if (value.Contains("tts", StringComparison.Ordinal) || value.Contains("speech", StringComparison.Ordinal))
            return "audio.speech";
        if (value.Contains("unet", StringComparison.Ordinal) ||
            value.Contains("vae", StringComparison.Ordinal) ||
            value.Contains("diffusion", StringComparison.Ordinal) ||
            value.Contains("stable-diffusion", StringComparison.Ordinal))
            return "image.text-to-image";
        return "llm.text-generation";
    }

    private static string NormalizeTask(string task)
    {
        string lower = task.Trim().ToLowerInvariant();
        if (lower.Contains("embed", StringComparison.Ordinal))
            return "embedding";
        if (lower.Contains("image", StringComparison.Ordinal) || lower.Contains("diffusion", StringComparison.Ordinal))
            return "image.text-to-image";
        if (lower.Contains("audio", StringComparison.Ordinal) || lower.Contains("speech", StringComparison.Ordinal))
            return "audio.speech";
        if (lower.Contains("vision", StringComparison.Ordinal) || lower.Contains("clip", StringComparison.Ordinal))
            return "vlm";
        return "llm.text-generation";
    }

    private static Dictionary<string, string> BuildComponentMap(string directory, IReadOnlyList<string> onnxFiles)
    {
        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < onnxFiles.Count; i++)
        {
            string file = onnxFiles[i];
            string relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            string componentName = onnxFiles.Count == 1 ? "model" : SanitizeId(Path.GetFileNameWithoutExtension(file));
            if (string.IsNullOrWhiteSpace(componentName))
                componentName = "model_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            while (components.ContainsKey(componentName))
                componentName += "_" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            components[componentName] = relativePath;
        }

        return components;
    }

    private static string BuildDisplayName(string? sourceRepository, string directory)
    {
        if (!string.IsNullOrWhiteSpace(sourceRepository))
        {
            string repo = sourceRepository.Trim().Replace('\\', '/').Trim('/');
            if (!string.IsNullOrWhiteSpace(repo))
                return repo.Replace('/', ' ');
        }

        string name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(name) ? "ONNX model" : name;
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

    private static string SanitizeId(string value)
    {
        var chars = value.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '-').ToArray();
        string sanitized = new string(chars).Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(sanitized) ? "onnx-model" : sanitized;
    }
}
