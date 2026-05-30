using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class ModelConversionService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly HashSet<string> ConversionAssetFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "added_tokens.json",
        "chat_template.jinja",
        "config.json",
        "generation_config.json",
        "merges.txt",
        "model.safetensors.index.json",
        "preprocessor_config.json",
        "processor_config.json",
        "special_tokens_map.json",
        "tokenizer.json",
        "tokenizer.model",
        "tokenizer_config.json",
        "vocab.json"
    };

    private readonly ConcurrentDictionary<string, ModelConversionJob> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HttpClient _httpClient;
    private readonly ModelRepositoryScanner _repositoryScanner;
    private bool _disposed;

    public ModelConversionService(string modelsDirectory, TimeSpan? timeout = null, HttpClient? httpClient = null)
    {
        ModelsDirectory = string.IsNullOrWhiteSpace(modelsDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "Models")
            : Path.GetFullPath(modelsDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = timeout ?? TimeSpan.FromHours(8);
        _repositoryScanner = new ModelRepositoryScanner(_httpClient);
    }

    public string ModelsDirectory { get; }

    public event Action<ModelConversionJob>? JobChanged;

    public ModelConversionJob StartConversion(ModelConversionRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.Repository))
            throw new ArgumentException("Repository is required.", nameof(request));

        string repository = NormalizeRepository(request.Repository);
        string revision = string.IsNullOrWhiteSpace(request.Revision) ? "main" : request.Revision.Trim();
        string jobId = "conv_" + Guid.NewGuid().ToString("N")[..10];
        var cancellation = new CancellationTokenSource();
        var job = new ModelConversionJob
        {
            JobId = jobId,
            Repository = repository,
            Revision = revision,
            Task = string.IsNullOrWhiteSpace(request.Task) ? "text-generation" : request.Task.Trim(),
            Precision = string.IsNullOrWhiteSpace(request.Precision) ? "fp32" : request.Precision.Trim().ToLowerInvariant(),
            Status = "queued",
            Message = "Queued for ONNX conversion.",
            SourcePaths = request.SourcePaths?.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? []
        };

        _jobs[jobId] = job;
        _cancellations[jobId] = cancellation;
        Notify(job);
        _ = Task.Run(() => RunConversionAsync(job, request, cancellation.Token));
        return job;
    }

    public IReadOnlyList<ModelConversionJob> ListJobs() =>
        _jobs.Values.OrderByDescending(job => job.StartedAtUtc).ToList();

    public ModelConversionJob? GetJob(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return null;
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public bool Cancel(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return false;
        if (!_cancellations.TryGetValue(jobId, out var cancellation))
            return false;

        cancellation.Cancel();
        if (_jobs.TryGetValue(jobId, out var job))
        {
            Update(job, "cancelling", job.Percent, "Cancellation requested.");
            job.AppendLog("Cancellation requested.");
        }

        return true;
    }

    private async Task RunConversionAsync(ModelConversionJob job, ModelConversionRequest request, CancellationToken cancellationToken)
    {
        job.StartedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            Update(job, "scanning", 3, "Scanning repository files.");
            string[] parts = job.Repository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
                throw new InvalidOperationException("Repository must be in owner/name form.");

            ModelRepositoryScanResult scan = await _repositoryScanner.ScanHuggingFaceAsync(parts[0], parts[1], job.Revision, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ModelRepositoryFile> bundleFiles = SelectConversionFiles(scan, job.SourcePaths);
            ValidateBundle(job, bundleFiles);

            string conversionRoot = Path.Combine(ModelsDirectory, ".conversion", job.JobId);
            string sourceDirectory = Path.Combine(conversionRoot, "source");
            string outputDirectory = BuildOutputDirectory(job);
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(outputDirectory);
            job.WorkingDirectory = conversionRoot;
            job.OutputDirectory = outputDirectory;

            await WriteSourceFileManifestAsync(job, bundleFiles, sourceDirectory, cancellationToken).ConfigureAwait(false);
            await DownloadBundleAsync(job, bundleFiles, sourceDirectory, cancellationToken).ConfigureAwait(false);

            Update(job, "converting", 60, "Running ONNX exporter.");
            string task = string.IsNullOrWhiteSpace(request.Task) ? InferTask(scan) : request.Task.Trim();
            string precision = string.IsNullOrWhiteSpace(request.Precision) ? "fp32" : request.Precision.Trim().ToLowerInvariant();
            await RunExporterAsync(job, sourceDirectory, outputDirectory, task, precision, request, cancellationToken).ConfigureAwait(false);

            Update(job, "validating", 90, "Validating exported ONNX files.");
            string[] onnxFiles = Directory.EnumerateFiles(outputDirectory, "*.onnx", SearchOption.AllDirectories).ToArray();
            if (onnxFiles.Length == 0)
                throw new InvalidOperationException("The exporter finished, but no .onnx files were created.");

            string manifestPath = OnnxModelManifestWriter.WriteFolderManifest(
                outputDirectory,
                job.Repository,
                bundleFiles.Select(file => file.Path).ToArray(),
                job.Revision,
                task,
                precision,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["jobId"] = job.JobId,
                    ["sourceFileCount"] = bundleFiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            job.ManifestPath = manifestPath;
            await WriteConversionReportAsync(job, bundleFiles, task, precision, "completed", null, cancellationToken).ConfigureAwait(false);

            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            Update(job, "completed", 100, "ONNX conversion completed and manifest registered.");
        }
        catch (OperationCanceledException)
        {
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await TryWriteConversionReportAsync(job, [], job.Task, job.Precision, "cancelled", "Cancelled by user").ConfigureAwait(false);
            Update(job, "cancelled", job.Percent, "ONNX conversion cancelled.");
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            await TryWriteConversionReportAsync(job, [], job.Task, job.Precision, "failed", ex.Message).ConfigureAwait(false);
            Update(job, "failed", job.Percent, "ONNX conversion failed: " + ex.Message);
        }
        finally
        {
            if (_cancellations.TryRemove(job.JobId, out var cancellation))
                cancellation.Dispose();
        }
    }

    private async Task DownloadBundleAsync(ModelConversionJob job, IReadOnlyList<ModelRepositoryFile> files, string sourceDirectory, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("No source files were selected for conversion.");

        for (int i = 0; i < files.Count; i++)
        {
            ModelRepositoryFile file = files[i];
            string targetPath = CombineUnderRoot(sourceDirectory, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? sourceDirectory);
            if (File.Exists(targetPath) && (file.SizeBytes <= 0 || new FileInfo(targetPath).Length == file.SizeBytes))
            {
                job.AppendLog("Using cached source file: " + file.Path);
                Update(job, "downloading", 10 + ((i + 1) / (double)files.Count * 45), "Using cached " + file.FileName);
                continue;
            }

            string url = BuildHuggingFaceResolveUrl(job.Repository, job.Revision, file.Path);
            job.AppendLog("Downloading " + file.Path);
            Update(job, "downloading", 10 + (i / (double)files.Count * 45), "Downloading " + file.FileName);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "SocketJack-LlmRuntime/1.0");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Hugging Face returned HTML for " + file.Path + ". The repository may require authentication.");

            await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            byte[] buffer = new byte[81920];
            long downloaded = 0;
            long total = response.Content.Headers.ContentLength ?? file.SizeBytes;
            DateTime lastProgress = DateTime.UtcNow;
            while (true)
            {
                int read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                downloaded += read;
                if ((DateTime.UtcNow - lastProgress).TotalMilliseconds < 350)
                    continue;

                lastProgress = DateTime.UtcNow;
                double fileProgress = total > 0 ? Math.Clamp(downloaded / (double)total, 0, 1) : 0;
                Update(job, "downloading", 10 + (((i + fileProgress) / files.Count) * 45), "Downloading " + file.FileName);
            }
        }
    }

    private async Task RunExporterAsync(
        ModelConversionJob job,
        string sourceDirectory,
        string outputDirectory,
        string task,
        string precision,
        ModelConversionRequest request,
        CancellationToken cancellationToken)
    {
        var attempts = new List<ProcessRunResult>();
        string configuredConverter = FirstNonEmpty(request.ConverterPath, Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER"));
        if (!string.IsNullOrWhiteSpace(configuredConverter))
        {
            var args = new List<string>
            {
                "--source", sourceDirectory,
                "--out", outputDirectory,
                "--task", task,
                "--precision", precision
            };
            attempts.Add(await RunProcessAsync(job, configuredConverter, args, outputDirectory, cancellationToken).ConfigureAwait(false));
            if (attempts[^1].Succeeded)
                return;
        }

        var optimumArgs = new List<string>
        {
            "export",
            "onnx",
            "--model",
            sourceDirectory,
            "--task",
            task,
            "--opset",
            request.Opset <= 0 ? "17" : request.Opset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.Equals(precision, "fp32", StringComparison.OrdinalIgnoreCase))
        {
            optimumArgs.Add("--dtype");
            optimumArgs.Add(precision);
        }
        optimumArgs.Add(outputDirectory);
        attempts.Add(await RunProcessAsync(job, "optimum-cli", optimumArgs, outputDirectory, cancellationToken).ConfigureAwait(false));
        if (attempts[^1].Succeeded)
            return;

        string python = FirstNonEmpty(request.PythonPath, Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_PYTHON"), "python");
        var pythonOptimumArgs = new List<string>
        {
            "-m",
            "optimum.exporters.onnx",
            "--model",
            sourceDirectory,
            "--task",
            task,
            "--opset",
            request.Opset <= 0 ? "17" : request.Opset.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (!string.Equals(precision, "fp32", StringComparison.OrdinalIgnoreCase))
        {
            pythonOptimumArgs.Add("--dtype");
            pythonOptimumArgs.Add(precision);
        }
        pythonOptimumArgs.Add(outputDirectory);
        attempts.Add(await RunProcessAsync(job, python, pythonOptimumArgs, outputDirectory, cancellationToken).ConfigureAwait(false));
        if (attempts[^1].Succeeded)
            return;

        var transformersArgs = new List<string>
        {
            "-m",
            "transformers.onnx",
            "--model",
            sourceDirectory,
            "--feature",
            MapTransformersFeature(task),
            outputDirectory
        };
        attempts.Add(await RunProcessAsync(job, python, transformersArgs, outputDirectory, cancellationToken).ConfigureAwait(false));
        if (attempts[^1].Succeeded)
            return;

        string detail = string.Join(Environment.NewLine, attempts.Select(attempt => attempt.Summary));
        throw new InvalidOperationException(
            "No ONNX exporter completed successfully. Install Optimum/Transformers in Python, install optimum-cli, or set LLMRUNTIME_ONNX_CONVERTER to a trusted converter executable." +
            Environment.NewLine + detail);
    }

    private static async Task<ProcessRunResult> RunProcessAsync(ModelConversionJob job, string fileName, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        string commandLine = fileName + " " + string.Join(" ", arguments.Select(QuoteForLog));
        job.AppendLog("Running: " + commandLine);
        job.Message = "Running " + Path.GetFileName(fileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var outputLines = new ConcurrentQueue<string>();
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            outputLines.Enqueue(e.Data);
            job.AppendLog(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
                return;
            outputLines.Enqueue(e.Data);
            job.AppendLog(e.Data);
        };
        process.Exited += (_, _) => tcs.TrySetResult(process.ExitCode);

        try
        {
            if (!process.Start())
                return ProcessRunResult.Failed(commandLine, "Process did not start.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
        {
            return ProcessRunResult.Failed(commandLine, "Executable was not found: " + fileName);
        }
        catch (Exception ex)
        {
            return ProcessRunResult.Failed(commandLine, ex.Message);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using (cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }
            tcs.TrySetCanceled(cancellationToken);
        }))
        {
            int exitCode = await tcs.Task.ConfigureAwait(false);
            return exitCode == 0
                ? ProcessRunResult.Success(commandLine)
                : ProcessRunResult.Failed(commandLine, "Exited with code " + exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + Environment.NewLine + Tail(outputLines));
        }
    }

    private static IReadOnlyList<ModelRepositoryFile> SelectConversionFiles(ModelRepositoryScanResult scan, IReadOnlyList<string> requestedSourcePaths)
    {
        var requested = new HashSet<string>(requestedSourcePaths ?? [], StringComparer.OrdinalIgnoreCase);
        bool explicitSources = requested.Count > 0;
        return scan.Files
            .Where(file =>
                file.Format == ModelFileFormat.Safetensors && (!explicitSources || requested.Contains(file.Path)) ||
                IncludeConversionAsset(file))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IncludeConversionAsset(ModelRepositoryFile file)
    {
        if (file.Format is ModelFileFormat.Config or ModelFileFormat.Tokenizer)
            return true;
        if (ConversionAssetFileNames.Contains(file.FileName))
            return true;
        if (file.FileName.EndsWith(".safetensors.index.json", StringComparison.OrdinalIgnoreCase))
            return true;
        return file.FileName.EndsWith(".model", StringComparison.OrdinalIgnoreCase) ||
               file.FileName.EndsWith(".tiktoken", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateBundle(ModelConversionJob job, IReadOnlyList<ModelRepositoryFile> files)
    {
        bool hasWeights = files.Any(file => file.Format == ModelFileFormat.Safetensors);
        bool hasConfig = files.Any(file => string.Equals(file.FileName, "config.json", StringComparison.OrdinalIgnoreCase));
        bool hasTokenizer = files.Any(file =>
            file.Format == ModelFileFormat.Tokenizer ||
            string.Equals(file.FileName, "tokenizer.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(file.FileName, "tokenizer.model", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(file.FileName, "vocab.json", StringComparison.OrdinalIgnoreCase));

        if (!hasWeights)
            throw new InvalidOperationException("No .safetensors files were selected for conversion.");
        if (!hasConfig)
            throw new InvalidOperationException("Source tensor conversion requires config.json.");
        if (!hasTokenizer)
            throw new InvalidOperationException("Source tensor conversion requires tokenizer assets.");

        long total = files.Sum(file => Math.Max(0, file.SizeBytes));
        job.SourceBytes = total;
        job.AppendLog("Selected " + files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " source files (" + total.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes).");
    }

    private async Task WriteSourceFileManifestAsync(ModelConversionJob job, IReadOnlyList<ModelRepositoryFile> files, string sourceDirectory, CancellationToken cancellationToken)
    {
        string path = Path.Combine(Path.GetDirectoryName(sourceDirectory) ?? sourceDirectory, "source-files.json");
        var manifest = new
        {
            job.JobId,
            job.Repository,
            job.Revision,
            sourceDirectory,
            files = files.Select(file => new
            {
                file.Path,
                file.FileName,
                file.SizeBytes,
                file.Format
            }).ToArray()
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteConversionReportAsync(ModelConversionJob job, IReadOnlyList<ModelRepositoryFile> files, string task, string precision, string status, string? error, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(job.OutputDirectory))
            return;

        Directory.CreateDirectory(job.OutputDirectory);
        string path = Path.Combine(job.OutputDirectory, "conversion.json");
        var report = new
        {
            job.JobId,
            job.Repository,
            job.Revision,
            status,
            error,
            task,
            precision,
            job.SourceBytes,
            job.OutputDirectory,
            job.ManifestPath,
            startedAtUtc = job.StartedAtUtc.ToString("O"),
            completedAtUtc = job.CompletedAtUtc?.ToString("O"),
            sourceFiles = files.Select(file => new
            {
                file.Path,
                file.FileName,
                file.SizeBytes,
                file.Format
            }).ToArray()
        };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private async Task TryWriteConversionReportAsync(ModelConversionJob job, IReadOnlyList<ModelRepositoryFile> files, string task, string precision, string status, string? error)
    {
        try
        {
            await WriteConversionReportAsync(job, files, task, precision, status, error, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private string BuildOutputDirectory(ModelConversionJob job)
    {
        string[] parts = job.Repository.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string owner = parts.Length > 0 ? SanitizeSegment(parts[0]) : "unknown";
        string repo = parts.Length > 1 ? SanitizeSegment(parts[1]) : "model";
        string variant = "onnx-" + SanitizeSegment(job.Revision) + "-" + job.JobId;
        return Path.Combine(ModelsDirectory, owner, repo, variant);
    }

    private static string InferTask(ModelRepositoryScanResult scan)
    {
        string combined = (scan.Repository + " " + string.Join(" ", scan.Files.Select(file => file.Path))).ToLowerInvariant();
        if (combined.Contains("embed", StringComparison.Ordinal) || combined.Contains("sentence-transformer", StringComparison.Ordinal))
            return "feature-extraction";
        if (combined.Contains("clip", StringComparison.Ordinal) || combined.Contains("vision", StringComparison.Ordinal))
            return "feature-extraction";
        return "text-generation";
    }

    private static string MapTransformersFeature(string task)
    {
        string lower = task.ToLowerInvariant();
        if (lower.Contains("embed", StringComparison.Ordinal) || lower.Contains("feature", StringComparison.Ordinal))
            return "default";
        if (lower.Contains("sequence", StringComparison.Ordinal) || lower.Contains("class", StringComparison.Ordinal))
            return "sequence-classification";
        return "causal-lm";
    }

    private static string BuildHuggingFaceResolveUrl(string repository, string revision, string path)
    {
        string escapedRevision = EscapePath(revision);
        string escapedPath = EscapePath(path);
        return "https://huggingface.co/" + repository.Trim('/') + "/resolve/" + escapedRevision + "/" + escapedPath + "?download=true";
    }

    private static string EscapePath(string path)
    {
        return string.Join("/", path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
    }

    private static string CombineUnderRoot(string root, string relativePath)
    {
        string cleaned = relativePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string rootFull = Path.GetFullPath(root);
        string full = Path.GetFullPath(Path.Combine(rootFull, cleaned));
        string rootWithSeparator = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unsafe source path outside conversion workspace: " + relativePath);
        return full;
    }

    private static string NormalizeRepository(string repository)
    {
        string normalized = repository.Trim().Replace('\\', '/').Trim('/');
        string[] parts = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            throw new ArgumentException("Repository must be in owner/name form.", nameof(repository));
        return parts[0] + "/" + parts[1];
    }

    private static string SanitizeSegment(string segment)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            segment = segment.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(segment) ? "model" : segment.Trim('.', ' ');
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

    private static string QuoteForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
    }

    private static string Tail(ConcurrentQueue<string> lines)
    {
        string[] values = lines.ToArray();
        if (values.Length == 0)
            return "";
        return string.Join(Environment.NewLine, values.TakeLast(12));
    }

    private void Update(ModelConversionJob job, string status, double percent, string message)
    {
        job.Status = status;
        job.Percent = Math.Clamp(percent, 0, 100);
        job.Message = message;
        job.AppendLog(message);
        Notify(job);
    }

    private void Notify(ModelConversionJob job) => JobChanged?.Invoke(job);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        foreach (var cancellation in _cancellations.Values)
        {
            try { cancellation.Cancel(); } catch { }
            cancellation.Dispose();
        }
        _cancellations.Clear();
    }

    private sealed record ProcessRunResult(bool Succeeded, string Command, string Detail)
    {
        public string Summary => Command + " => " + (Succeeded ? "ok" : Detail);

        public static ProcessRunResult Success(string command) => new(true, command, "");

        public static ProcessRunResult Failed(string command, string detail) => new(false, command, detail);
    }
}

public sealed class ModelConversionRequest
{
    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "main";

    [JsonPropertyName("sourcePaths")]
    public IReadOnlyList<string> SourcePaths { get; init; } = [];

    [JsonPropertyName("task")]
    public string Task { get; init; } = "text-generation";

    [JsonPropertyName("precision")]
    public string Precision { get; init; } = "fp32";

    [JsonPropertyName("opset")]
    public int Opset { get; init; } = 17;

    [JsonPropertyName("pythonPath")]
    public string PythonPath { get; init; } = "";

    [JsonPropertyName("converterPath")]
    public string ConverterPath { get; init; } = "";
}

public sealed class ModelConversionJob
{
    private readonly ConcurrentQueue<string> _logs = new();

    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("repository")]
    public string Repository { get; init; } = "";

    [JsonPropertyName("revision")]
    public string Revision { get; init; } = "main";

    [JsonPropertyName("task")]
    public string Task { get; set; } = "text-generation";

    [JsonPropertyName("precision")]
    public string Precision { get; set; } = "fp32";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "queued";

    [JsonPropertyName("percent")]
    public double Percent { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("error")]
    public string Error { get; set; } = "";

    [JsonPropertyName("sourcePaths")]
    public IReadOnlyList<string> SourcePaths { get; init; } = [];

    [JsonPropertyName("sourceBytes")]
    public long SourceBytes { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; set; } = "";

    [JsonPropertyName("outputDirectory")]
    public string OutputDirectory { get; set; } = "";

    [JsonPropertyName("manifestPath")]
    public string ManifestPath { get; set; } = "";

    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [JsonPropertyName("logs")]
    public IReadOnlyList<string> Logs => _logs.ToArray();

    public void AppendLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _logs.Enqueue(DateTimeOffset.UtcNow.ToString("HH:mm:ss") + " " + message.Trim());
        while (_logs.Count > 300 && _logs.TryDequeue(out _))
        {
        }
    }
}
