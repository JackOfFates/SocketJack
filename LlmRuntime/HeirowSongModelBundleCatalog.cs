using System.Security.Cryptography;
using System.Text.Json;

namespace LlmRuntime;

public static class HeirowSongModelBundleCatalog
{
    public const string BundleId = "heirowsong-ace-step-1.5-v0.1.8";
    public const long PayloadBytes = 12_501_168_402L;
    public const string RuntimeVersion = "v0.1.8";
    public const string RuntimeCommit = "dce621408bee8c31b4fcf4811682eb9359e1bc94";

    public static IReadOnlyList<HeirowSongRepositoryPin> Repositories { get; } = new[]
    {
        new HeirowSongRepositoryPin("ACE-Step/Ace-Step1.5", "19671f406d603126926c1b7e2adc169acbcade22", 6_336_680_772L, new[] { "acestep-v15-turbo/", "vae/", "Qwen3-Embedding-0.6B/" }, Array.Empty<string>()),
        new HeirowSongRepositoryPin("ACE-Step/acestep-5Hz-lm-0.6B", "148d8ea0225bdab342ee1ae3a354275ccd60ca80", 1_372_702_240L, new[] { "*" }, Array.Empty<string>()),
        new HeirowSongRepositoryPin("ACE-Step/acestep-v15-base", "e432212fec32b8965a14ffa57ae653438d6abd14", 4_791_785_390L, new[] { "*" }, new[] { "README.md", ".gitattributes" })
    };

    public static async Task<IReadOnlyList<HeirowSongResolvedRepository>> ResolveAsync(ModelRepositoryScanner scanner, string? cookies, string? bearerToken, CancellationToken cancellationToken = default)
    {
        var resolved = new List<HeirowSongResolvedRepository>();
        foreach (HeirowSongRepositoryPin pin in Repositories)
        {
            string[] parts = pin.Repository.Split('/', 2);
            ModelRepositoryScanResult scan = await scanner.ScanHuggingFaceAsync(parts[0], parts[1], pin.Revision, cookies, bearerToken, cancellationToken).ConfigureAwait(false);
            ModelRepositoryFile[] files = scan.Files.Where(file => Include(pin, file.Path)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase).ToArray();
            long size = files.Sum(file => Math.Max(0, file.SizeBytes));
            if (files.Length == 0) throw new InvalidOperationException("The pinned heirowSong repository returned no selected files: " + pin.Repository + ".");
            if (size != pin.ExpectedBytes) throw new InvalidOperationException("The pinned heirowSong file set changed for " + pin.Repository + ". Expected " + pin.ExpectedBytes + " bytes but Hugging Face reported " + size + ". Refusing to install an unreviewed bundle.");
            resolved.Add(new HeirowSongResolvedRepository(pin, files.Select(file => file.Path).ToArray()));
        }
        if (resolved.Sum(value => value.Pin.ExpectedBytes) != PayloadBytes) throw new InvalidOperationException("heirowSong bundle size invariant failed.");
        return resolved;
    }

    public static string TargetDirectory(string repository) => "heirowSong/" + BundleId + "/" + repository.Replace('/', '_');

    public static string BundleDirectory(string completeModelsRoot) =>
        Path.Combine(completeModelsRoot, "heirowSong", BundleId);

    public static bool IsInstalled(string completeModelsRoot)
    {
        if (string.IsNullOrWhiteSpace(completeModelsRoot))
            return false;

        string manifestPath = Path.Combine(BundleDirectory(completeModelsRoot), "manifest.json");
        try
        {
            if (!File.Exists(manifestPath))
                return false;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement root = document.RootElement;
            if (!Read(root, "bundleId").Equals(BundleId, StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("verified", out JsonElement verified) ||
                verified.ValueKind != JsonValueKind.True)
            {
                return false;
            }

            foreach (HeirowSongRepositoryPin pin in Repositories)
            {
                string partManifest = Path.Combine(
                    completeModelsRoot,
                    TargetDirectory(pin.Repository).Replace('/', Path.DirectorySeparatorChar),
                    "manifest.json");
                if (!File.Exists(partManifest))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryFinalize(string completeModelsRoot, out string manifestPath, out string error)
    {
        manifestPath = Path.Combine(completeModelsRoot, "heirowSong", BundleId, "manifest.json");
        error = "";
        try
        {
            var files = new List<object>();
            long total = 0;
            foreach (HeirowSongRepositoryPin pin in Repositories)
            {
                string directory = Path.Combine(completeModelsRoot, TargetDirectory(pin.Repository).Replace('/', Path.DirectorySeparatorChar));
                string partManifest = Path.Combine(directory, "manifest.json");
                if (!File.Exists(partManifest)) { error = "Waiting for " + pin.Repository + "."; return false; }
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(partManifest));
                string source = ReadMetadata(document.RootElement, "source");
                if (!source.Equals("huggingface:" + pin.Repository, StringComparison.OrdinalIgnoreCase) || !string.Equals(ReadMetadata(document.RootElement, "revision"), pin.Revision, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Pinned repository identity mismatch for " + pin.Repository + ".");
                string[] sourcePaths = ReadMetadata(document.RootElement, "sourcePaths").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (string sourcePath in sourcePaths)
                {
                    string path = ResolveSafe(directory, sourcePath);
                    if (!File.Exists(path)) throw new InvalidOperationException("Downloaded heirowSong file is missing: " + sourcePath + ".");
                    var info = new FileInfo(path); if (info.Length <= 0) throw new InvalidOperationException("Downloaded heirowSong file is empty: " + sourcePath + ".");
                    string sha;
                    using (SHA256 hasher = SHA256.Create())
                    using (FileStream stream = File.OpenRead(path))
                        sha = Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
                    total += info.Length;
                    files.Add(new { repository = pin.Repository, revision = pin.Revision, path = sourcePath.Replace('\\', '/'), sizeBytes = info.Length, sha256 = sha });
                }
            }
            if (total != PayloadBytes) throw new InvalidOperationException("Downloaded heirowSong payload is " + total + " bytes; expected " + PayloadBytes + ". Registration was blocked.");
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            string temporary = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { schemaVersion = 1, bundleId = BundleId, displayName = "heirowSong — ACE-Step 1.5", task = "text-to-audio", license = "MIT", payloadBytes = total, payloadGiB = 11.64, verified = true, verifiedUtc = DateTimeOffset.UtcNow, runtime = new { version = RuntimeVersion, commit = RuntimeCommit, signedPackRequired = true }, files }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            File.Move(temporary, manifestPath, true);
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    private static bool Include(HeirowSongRepositoryPin pin, string path)
    {
        path = path.Replace('\\', '/');
        if (pin.Exclude.Any(value => path.Equals(value, StringComparison.OrdinalIgnoreCase))) return false;
        return pin.IncludePrefixes.Contains("*") || pin.IncludePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveSafe(string root, string relative)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Model file path escapes the heirowSong bundle.");
        return path;
    }

    private static string Read(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string ReadMetadata(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("metadata", out JsonElement metadata) ? Read(metadata, name) : "";
}

public sealed record HeirowSongRepositoryPin(string Repository, string Revision, long ExpectedBytes, IReadOnlyList<string> IncludePrefixes, IReadOnlyList<string> Exclude);
public sealed record HeirowSongResolvedRepository(HeirowSongRepositoryPin Pin, IReadOnlyList<string> SourcePaths);
