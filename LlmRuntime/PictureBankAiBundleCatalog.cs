using System.Text.Json;

namespace LlmRuntime;

public static class PictureBankAiBundleCatalog
{
    public const string BundleId = "picturebank-ai-v1";
    public static IReadOnlyList<PictureBankRepositoryPin> Repositories { get; } = new[]
    {
        new PictureBankRepositoryPin("stable-diffusion-v1-5/stable-diffusion-v1-5", "451f4fe16113bff5a5d2269ed5ad43b0592e9a14", "CreativeML Open RAIL-M", new[] { "model_index.json", "scheduler/", "text_encoder/", "tokenizer/", "unet/", "vae/" }),
        new PictureBankRepositoryPin("stable-diffusion-v1-5/stable-diffusion-inpainting", "8a4288a76071f7280aedbdb3253bdb9e9d5d84bb", "CreativeML Open RAIL-M", new[] { "model_index.json", "scheduler/", "text_encoder/", "tokenizer/", "unet/", "vae/" }),
        new PictureBankRepositoryPin("facebook/sam-vit-base", "70c1a07f894ebb5b307fd9eaaee97b9dfc16068f", "Apache-2.0", new[] { "config.json", "model.safetensors" }),
        new PictureBankRepositoryPin("ZhengPeng7/BiRefNet_lite", "7838f1c3472f827cd8ce13ab5ccc2ce48077360f", "MIT", new[] { "config.json", "model.safetensors" })
    };

    public static async Task<IReadOnlyList<PictureBankResolvedRepository>> ResolveAsync(ModelRepositoryScanner scanner, string? cookies, string? bearerToken, CancellationToken cancellationToken = default)
    {
        var resolved = new List<PictureBankResolvedRepository>();
        foreach (PictureBankRepositoryPin pin in Repositories)
        {
            string[] parts = pin.Repository.Split('/', 2);
            ModelRepositoryScanResult scan = await scanner.ScanHuggingFaceAsync(parts[0], parts[1], pin.Revision, cookies, bearerToken, cancellationToken).ConfigureAwait(false);
            ModelRepositoryFile[] files = scan.Files.Where(file => Include(pin, file.Path)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (files.Length == 0) throw new InvalidOperationException("The pinned PictureBank repository returned no reviewed files: " + pin.Repository + ".");
            if (!files.Any(file => file.Path.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("The pinned PictureBank repository did not expose safetensors weights: " + pin.Repository + ". Refusing pickle or trust_remote_code content.");
            long bytes = files.Sum(file => Math.Max(0, file.SizeBytes));
            resolved.Add(new(pin, files.Select(file => file.Path).ToArray(), bytes));
        }
        return resolved;
    }

    public static string TargetDirectory(string repository) => "PictureBank/" + BundleId + "/" + repository.Replace('/', '_');

    public static string BundleDirectory(string completeModelsRoot) =>
        Path.Combine(completeModelsRoot, "PictureBank", BundleId);

    public static bool IsInstalled(string completeModelsRoot)
    {
        if (string.IsNullOrWhiteSpace(completeModelsRoot))
            return false;

        try
        {
            foreach (PictureBankRepositoryPin pin in Repositories)
            {
                string directory = Path.Combine(
                    completeModelsRoot,
                    TargetDirectory(pin.Repository).Replace('/', Path.DirectorySeparatorChar));
                string manifestPath = Path.Combine(directory, "manifest.json");
                if (!File.Exists(manifestPath))
                    return false;

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
                JsonElement root = document.RootElement;
                if (!ReadMetadata(root, "source").Equals("huggingface:" + pin.Repository, StringComparison.OrdinalIgnoreCase) ||
                    !ReadMetadata(root, "revision").Equals(pin.Revision, StringComparison.OrdinalIgnoreCase) ||
                    !ReadMetadata(root, "pictureBankBundleId").Equals(BundleId, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Read(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string ReadMetadata(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metadata", out JsonElement metadata)
            ? Read(metadata, name)
            : "";

    private static bool Include(PictureBankRepositoryPin pin, string path) { path = (path ?? "").Replace('\\', '/'); return pin.IncludePrefixes.Any(prefix => prefix.EndsWith("/", StringComparison.Ordinal) ? path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) : path.Equals(prefix, StringComparison.OrdinalIgnoreCase)); }
}

public sealed record PictureBankRepositoryPin(string Repository, string Revision, string License, IReadOnlyList<string> IncludePrefixes);
public sealed record PictureBankResolvedRepository(PictureBankRepositoryPin Pin, IReadOnlyList<string> SourcePaths, long ExpectedBytes);
