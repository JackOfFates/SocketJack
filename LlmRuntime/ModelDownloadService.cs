using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LlmRuntime;

public sealed class ModelDownloadService
{
    private readonly HttpClient _httpClient;
    private CancellationTokenSource? _cts;
    private bool _keepPartialOnCancel;

    public string ModelsDirectory { get; }

    public bool IsDownloading { get; private set; }

    public string? CurrentDownloadPath { get; private set; }

    public event Action<DownloadProgress>? ProgressChanged;

    public event Action<string>? DownloadCompleted;

    public event Action<ModelDownloadResult>? DownloadCompletedWithResult;

    public event Action<ModelBundleDownloadResult>? BundleDownloadCompletedWithResult;

    public event Action<string>? DownloadFailed;

    public event Action<string>? Log;

    public ModelDownloadService(string modelsDirectory, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        ModelsDirectory = modelsDirectory;
        Directory.CreateDirectory(modelsDirectory);
        _httpClient = handler == null ? new HttpClient() : new HttpClient(handler);
        _httpClient.Timeout = timeout ?? TimeSpan.FromHours(4);
    }

    public static bool IsGgufUrl(string url) => UrlContainsExtension(url, ".gguf");

    public static bool IsSafetensorUrl(string url) => UrlContainsExtension(url, ".safetensors");

    public static bool IsOnnxUrl(string url) => UrlContainsExtension(url, ".onnx");

    public static bool IsModelUrl(string url) => IsGgufUrl(url) || IsSafetensorUrl(url) || IsOnnxUrl(url);

    public static string NormalizeDownloadUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        if (!url.Contains("huggingface.co", StringComparison.OrdinalIgnoreCase))
            return url;

        url = url.Replace("/blob/", "/resolve/", StringComparison.OrdinalIgnoreCase);
        if (!url.Contains("download=true", StringComparison.OrdinalIgnoreCase))
            url += url.Contains('?') ? "&download=true" : "?download=true";
        return url;
    }

    public static string ExtractFileName(string url)
    {
        try
        {
            var uri = new Uri(url);
            string fileName = Path.GetFileName(uri.AbsolutePath);
            int qIdx = fileName.IndexOf('?');
            if (qIdx > 0)
                fileName = fileName[..qIdx];

            if (!string.IsNullOrWhiteSpace(fileName) &&
                (fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                 fileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase)))
                return fileName;
        }
        catch
        {
        }

        return $"model_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bin";
    }

    public async Task DownloadAsync(string url, string? customFileName = null, string? cookies = null, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            DownloadFailed?.Invoke("A download is already in progress. Cancel it first.");
            return;
        }

        if (!IsModelUrl(url))
        {
            DownloadFailed?.Invoke("URL does not point to a supported model file (.gguf, .safetensors, or .onnx).");
            return;
        }

        string fileName = customFileName ?? ExtractFileName(url);
        string finalPath = Path.Combine(ModelsDirectory, fileName);
        string partialPath = finalPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? ModelsDirectory);

        if (File.Exists(finalPath))
        {
            DownloadFailed?.Invoke($"File already exists: {fileName}");
            return;
        }

        IsDownloading = true;
        CurrentDownloadPath = finalPath;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        url = NormalizeDownloadUrl(url);

        Log?.Invoke($"Starting download: {fileName}");
        Log?.Invoke($"URL: {url}");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            long resumeFrom = GetPartialLength(partialPath);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "SocketJack-LlmRuntime/1.0");
            if (resumeFrom > 0)
                request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
            if (!string.IsNullOrWhiteSpace(cookies))
                request.Headers.Add("Cookie", cookies);
            if (!string.IsNullOrWhiteSpace(bearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, _cts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            bool isResume = resumeFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (resumeFrom > 0 && !isResume)
            {
                resumeFrom = 0;
                CleanupPartialFile(partialPath);
            }

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                DownloadFailed?.Invoke("Server returned HTML instead of the model file. The URL may require authentication.");
                return;
            }

            long responseBytes = response.Content.Headers.ContentLength ?? -1;
            long totalBytes = isResume && responseBytes > 0 ? resumeFrom + responseBytes : responseBytes;
            using var contentStream = await response.Content.ReadAsStreamAsync(_cts.Token).ConfigureAwait(false);
            using var fileStream = new FileStream(partialPath, isResume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920);

            byte[] buffer = new byte[81920];
            long totalRead = resumeFrom;
            DateTime lastProgressReport = DateTime.UtcNow;
            if (resumeFrom > 0)
                ProgressChanged?.Invoke(DownloadProgress.Create(fileName, totalRead, totalBytes, stopwatch.Elapsed, false));

            while (true)
            {
                int bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), _cts.Token).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), _cts.Token).ConfigureAwait(false);
                totalRead += bytesRead;

                if ((DateTime.UtcNow - lastProgressReport).TotalMilliseconds >= 250)
                {
                    lastProgressReport = DateTime.UtcNow;
                    ProgressChanged?.Invoke(DownloadProgress.Create(fileName, totalRead, totalBytes, stopwatch.Elapsed, false));
                }
            }

            await fileStream.FlushAsync(_cts.Token).ConfigureAwait(false);
            fileStream.Close();

            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(partialPath, finalPath);
            string sha256 = ComputeSha256(finalPath);

            ProgressChanged?.Invoke(DownloadProgress.Create(fileName, totalRead, totalRead, stopwatch.Elapsed, true));
            DownloadCompletedWithResult?.Invoke(new ModelDownloadResult
            {
                FilePath = finalPath,
                FileName = fileName,
                SizeBytes = totalRead,
                Sha256 = sha256
            });
            DownloadCompleted?.Invoke(finalPath);
        }
        catch (OperationCanceledException)
        {
            if (!_keepPartialOnCancel)
                CleanupPartialFile(partialPath);
            DownloadFailed?.Invoke(_keepPartialOnCancel ? "Download paused. Resume will continue from the partial file when the server supports range requests." : "Download cancelled by user.");
        }
        catch (Exception ex)
        {
            DownloadFailed?.Invoke($"Download failed: {ex.Message}");
        }
        finally
        {
            IsDownloading = false;
            CurrentDownloadPath = null;
            _keepPartialOnCancel = false;
            stopwatch.Stop();
        }
    }

    public async Task DownloadBundleAsync(ModelBundleDownloadRequest request, string? cookies = null, string? bearerToken = null, CancellationToken cancellationToken = default)
    {
        if (IsDownloading)
        {
            DownloadFailed?.Invoke("A download is already in progress. Cancel it first.");
            return;
        }

        if (request == null)
        {
            DownloadFailed?.Invoke("Bundle download request is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(request.Repository))
        {
            DownloadFailed?.Invoke("Bundle repository is required.");
            return;
        }

        string[] sourcePaths = request.SourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            DownloadFailed?.Invoke("Bundle download has no source files.");
            return;
        }

        string relativeDirectory = string.IsNullOrWhiteSpace(request.TargetRelativeDirectory)
            ? BuildRepositoryRelativeDirectory(request.Repository, request.Revision)
            : request.TargetRelativeDirectory;
        string targetDirectory;
        try
        {
            targetDirectory = ResolveSafeChildPath(ModelsDirectory, relativeDirectory);
        }
        catch (Exception ex)
        {
            DownloadFailed?.Invoke("Invalid bundle target path: " + ex.Message);
            return;
        }

        IsDownloading = true;
        CurrentDownloadPath = targetDirectory;
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _keepPartialOnCancel = false;
        Directory.CreateDirectory(targetDirectory);
        var stopwatch = Stopwatch.StartNew();
        long expectedTotalBytes = Math.Max(0, request.TotalSizeBytes);
        long completedBytes = 0;
        var downloadedFiles = new List<ModelBundleDownloadedFile>();

        Log?.Invoke("Starting complete model download: " + request.Repository);
        Log?.Invoke("Target: " + targetDirectory);

        try
        {
            foreach (string sourcePath in sourcePaths)
            {
                _cts.Token.ThrowIfCancellationRequested();
                string relativePath = NormalizeBundleSourcePath(sourcePath);
                string finalPath = ResolveSafeChildPath(targetDirectory, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(finalPath) ?? targetDirectory);

                if (File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                {
                    long existingLength = new FileInfo(finalPath).Length;
                    completedBytes += existingLength;
                    downloadedFiles.Add(new ModelBundleDownloadedFile(sourcePath, finalPath, existingLength, ""));
                    ProgressChanged?.Invoke(DownloadProgress.Create(sourcePath, completedBytes, expectedTotalBytes, stopwatch.Elapsed, false));
                    continue;
                }

                string url = BuildHuggingFaceResolveUrl(request.Repository, request.Revision, sourcePath);
                ModelDownloadResult result = await DownloadToPathAsync(
                    url,
                    finalPath,
                    sourcePath,
                    cookies,
                    bearerToken,
                    completedBytes,
                    expectedTotalBytes,
                    stopwatch,
                    _cts.Token).ConfigureAwait(false);
                completedBytes += result.SizeBytes;
                downloadedFiles.Add(new ModelBundleDownloadedFile(sourcePath, result.FilePath, result.SizeBytes, result.Sha256));
            }

            string manifestPath = CompleteModelManifestWriter.WriteManifest(
                targetDirectory,
                request.Repository,
                request.Revision,
                request.Task,
                "pytorch",
                sourcePaths,
                BuildManifestMetadata(request.Metadata));

            ProgressChanged?.Invoke(DownloadProgress.Create(Path.GetFileName(targetDirectory), completedBytes, completedBytes, stopwatch.Elapsed, true));
            var bundleResult = new ModelBundleDownloadResult
            {
                DirectoryPath = targetDirectory,
                ManifestPath = manifestPath,
                Repository = request.Repository,
                Revision = string.IsNullOrWhiteSpace(request.Revision) ? "main" : request.Revision,
                Task = request.Task,
                SizeBytes = completedBytes,
                Files = downloadedFiles
            };
            BundleDownloadCompletedWithResult?.Invoke(bundleResult);
            DownloadCompleted?.Invoke(manifestPath);
        }
        catch (OperationCanceledException)
        {
            DownloadFailed?.Invoke(_keepPartialOnCancel ? "Download paused. Resume will continue from partial files when the server supports range requests." : "Download cancelled by user.");
        }
        catch (Exception ex)
        {
            DownloadFailed?.Invoke("Bundle download failed: " + ex.Message);
        }
        finally
        {
            IsDownloading = false;
            CurrentDownloadPath = null;
            _keepPartialOnCancel = false;
            stopwatch.Stop();
        }
    }

    public void Cancel()
    {
        if (!IsDownloading)
            return;

        _cts?.Cancel();
        Log?.Invoke("Cancelling download...");
    }

    public void Pause()
    {
        if (!IsDownloading)
            return;

        _keepPartialOnCancel = true;
        _cts?.Cancel();
        Log?.Invoke("Pausing download...");
    }

    public int CleanupPartialFiles(TimeSpan? olderThan = null)
    {
        if (!Directory.Exists(ModelsDirectory))
            return 0;

        int deleted = 0;
        DateTime cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.Zero);
        foreach (string path in Directory.EnumerateFiles(ModelsDirectory, "*.partial", SearchOption.AllDirectories))
        {
            try
            {
                if (olderThan.HasValue && File.GetLastWriteTimeUtc(path) > cutoff)
                    continue;

                File.Delete(path);
                deleted++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to clean up partial file: {ex.Message}");
            }
        }

        return deleted;
    }

    public List<string> ListModels()
    {
        if (!Directory.Exists(ModelsDirectory))
            return [];

        return Directory.EnumerateFiles(ModelsDirectory, "*.gguf")
            .Where(file => !file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName)
            .ToList();
    }

    private static bool UrlContainsExtension(string url, string extension)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url);
            return uri.AbsolutePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
                   uri.Query.Contains(extension, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return url.Contains(extension, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task<ModelDownloadResult> DownloadToPathAsync(
        string url,
        string finalPath,
        string displayName,
        string? cookies,
        string? bearerToken,
        long completedBundleBytes,
        long expectedBundleBytes,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        string partialPath = finalPath + ".partial";
        long resumeFrom = GetPartialLength(partialPath);
        using var request = new HttpRequestMessage(HttpMethod.Get, NormalizeDownloadUrl(url));
        request.Headers.Add("User-Agent", "SocketJack-LlmRuntime/1.0");
        if (resumeFrom > 0)
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        if (!string.IsNullOrWhiteSpace(cookies))
            request.Headers.Add("Cookie", cookies);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        bool isResume = resumeFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (resumeFrom > 0 && !isResume)
        {
            resumeFrom = 0;
            CleanupPartialFile(partialPath);
        }

        string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Server returned HTML instead of " + displayName + ". The repository may require authentication.");

        long responseBytes = response.Content.Headers.ContentLength ?? -1;
        long fileTotalBytes = isResume && responseBytes > 0 ? resumeFrom + responseBytes : responseBytes;
        long totalBytes = expectedBundleBytes > 0
            ? Math.Max(expectedBundleBytes, completedBundleBytes + Math.Max(fileTotalBytes, 0))
            : fileTotalBytes > 0 ? completedBundleBytes + fileTotalBytes : -1;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(partialPath, isResume ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        byte[] buffer = new byte[81920];
        long fileRead = resumeFrom;
        DateTime lastProgressReport = DateTime.UtcNow;
        if (resumeFrom > 0)
            ProgressChanged?.Invoke(DownloadProgress.Create(displayName, completedBundleBytes + fileRead, totalBytes, stopwatch.Elapsed, false));

        while (true)
        {
            int bytesRead = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
                break;

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            fileRead += bytesRead;

            if ((DateTime.UtcNow - lastProgressReport).TotalMilliseconds >= 250)
            {
                lastProgressReport = DateTime.UtcNow;
                ProgressChanged?.Invoke(DownloadProgress.Create(displayName, completedBundleBytes + fileRead, totalBytes, stopwatch.Elapsed, false));
            }
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        fileStream.Close();

        if (File.Exists(finalPath))
            File.Delete(finalPath);
        File.Move(partialPath, finalPath);
        string sha256 = ComputeSha256(finalPath);
        return new ModelDownloadResult
        {
            FilePath = finalPath,
            FileName = Path.GetFileName(finalPath),
            SizeBytes = fileRead,
            Sha256 = sha256
        };
    }

    private static string BuildHuggingFaceResolveUrl(string repository, string revision, string sourcePath)
    {
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim('/');
        return "https://huggingface.co/" + repository.Trim('/') +
               "/resolve/" + Uri.EscapeDataString(rev) + "/" +
               EscapeHuggingFacePath(sourcePath) + "?download=true";
    }

    private static string EscapeHuggingFacePath(string sourcePath)
    {
        return string.Join("/", sourcePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.EscapeDataString));
    }

    private static string BuildRepositoryRelativeDirectory(string repository, string revision)
    {
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim('/');
        return string.Join("/", repository.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(rev)
            .Select(SanitizePathSegment));
    }

    private static string NormalizeBundleSourcePath(string sourcePath)
    {
        string[] parts = sourcePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new InvalidOperationException("Invalid source path.");

        return Path.Combine(parts.Select(SanitizePathSegment).ToArray());
    }

    private static Dictionary<string, string> BuildManifestMetadata(IReadOnlyDictionary<string, string>? requestMetadata)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["downloadedBy"] = "LlmRuntime.ModelDownloadService"
        };
        if (requestMetadata != null)
        {
            foreach (KeyValuePair<string, string> pair in requestMetadata)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                    metadata[pair.Key] = pair.Value;
            }
        }

        return metadata;
    }

    private static string SanitizePathSegment(string value)
    {
        string segment = value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        segment = segment.Replace(':', '_').Replace('/', '_').Replace('\\', '_');
        return string.IsNullOrWhiteSpace(segment) || segment == "." || segment == ".." ? "file" : segment;
    }

    private static string ResolveSafeChildPath(string root, string relativePath)
    {
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes the model directory.");
        return fullPath;
    }

    private static void CleanupPartialFile(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to clean up partial file: {ex.Message}");
        }
    }

    private static long GetPartialLength(string partialPath)
    {
        try
        {
            return File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public sealed class ModelDownloadResult
{
    public string FilePath { get; init; } = "";

    public string FileName { get; init; } = "";

    public long SizeBytes { get; init; }

    public string Sha256 { get; init; } = "";
}

public sealed class ModelBundleDownloadRequest
{
    public string Repository { get; init; } = "";

    public string Revision { get; init; } = "main";

    public string TargetRelativeDirectory { get; init; } = "";

    public string Task { get; init; } = "";

    public long TotalSizeBytes { get; init; }

    public IReadOnlyList<string> SourcePaths { get; init; } = [];

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelBundleDownloadResult
{
    public string DirectoryPath { get; init; } = "";

    public string ManifestPath { get; init; } = "";

    public string Repository { get; init; } = "";

    public string Revision { get; init; } = "main";

    public string Task { get; init; } = "";

    public long SizeBytes { get; init; }

    public IReadOnlyList<ModelBundleDownloadedFile> Files { get; init; } = [];
}

public sealed record ModelBundleDownloadedFile(string SourcePath, string FilePath, long SizeBytes, string Sha256);

public sealed class DownloadProgress
{
    public string FileName { get; init; } = "";
    public long DownloadedBytes { get; init; }
    public long TotalBytes { get; init; }
    public double Percent { get; init; }
    public double SpeedBytesPerSecond { get; init; }
    public TimeSpan Eta { get; init; }
    public bool IsComplete { get; init; }

    internal static DownloadProgress Create(string fileName, long downloadedBytes, long totalBytes, TimeSpan elapsed, bool complete)
    {
        double seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        double speed = downloadedBytes / seconds;
        double percent = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : -1;
        double etaSeconds = totalBytes > 0 && speed > 0 ? Math.Max(0, (totalBytes - downloadedBytes) / speed) : 0;
        return new DownloadProgress
        {
            FileName = fileName,
            DownloadedBytes = downloadedBytes,
            TotalBytes = totalBytes,
            Percent = complete ? 100 : percent,
            SpeedBytesPerSecond = speed,
            Eta = TimeSpan.FromSeconds(etaSeconds),
            IsComplete = complete
        };
    }
}
