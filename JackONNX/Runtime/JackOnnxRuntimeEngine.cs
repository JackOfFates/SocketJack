using Microsoft.ML.OnnxRuntime;
using JackONNX;
using System.Text;
using System.Text.Json;

namespace JackONNX.Runtime;

public sealed class JackOnnxRuntimeEngine : IJackOnnxRuntimeCatalog
{
    private readonly object _jobsLock = new();
    private readonly object _progressLock = new();
    private readonly List<IJackOnnxExecutionProvider> _providers;
    private readonly List<JackOnnxJobSnapshot> _jobs = new();
    private readonly Dictionary<string, List<JackOnnxProgress>> _progressHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<JackOnnxProgress>>> _progressSubscribers = new(StringComparer.OrdinalIgnoreCase);

    public JackOnnxRuntimeEngine(JackOnnxOptions? options = null, IEnumerable<IJackOnnxExecutionProvider>? providers = null)
    {
        Options = options ?? new JackOnnxOptions();
        _providers = (providers ?? [new OnnxRuntimeCpuExecutionProvider()])
            .OrderBy(provider => provider.Priority)
            .ToList();
        OnnxRuntimeNativeProviderResolver.TryPreselectGpuRuntime(Options, _providers);
    }

    public JackOnnxOptions Options { get; }

    public IReadOnlyList<IJackOnnxExecutionProvider> Providers => _providers;

    public event Action<JackOnnxJobSnapshot, JackOnnxGenerationRequest>? GenerationRequested;

    public event Action<JackOnnxProgress>? ProgressReported;

    public static JackOnnxRuntimeEngine Create(JackOnnxOptions? options = null, IEnumerable<IJackOnnxExecutionProvider>? providers = null)
    {
        return new JackOnnxRuntimeEngine(options, providers);
    }

    public async Task<IReadOnlyList<JackOnnxDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<JackOnnxDeviceInfo>();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                devices.AddRange(await provider.ProbeDevicesAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                devices.Add(new JackOnnxDeviceInfo
                {
                    Id = provider.Id,
                    Name = provider.Id,
                    Provider = provider.Kind,
                    IsAvailable = false,
                    Detail = ex.Message
                });
            }
        }

        return devices;
    }

    public async Task<IReadOnlyList<JackOnnxProviderCompatibility>> CheckProviderCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<JackOnnxProviderCompatibility>();
        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var devices = await SafeProbeProviderAsync(provider, cancellationToken).ConfigureAwait(false);
            bool available = devices.Any(device => device.IsAvailable);
            var compatibility = new JackOnnxProviderCompatibility
            {
                ProviderId = provider.Id,
                Provider = provider.Kind,
                IsAvailable = available,
                CanCreateSessionOptions = provider is not IJackOnnxSessionOptionsFactory,
                Detail = available ? "Provider reported at least one available device." : string.Join("; ", devices.Select(device => device.Detail).Where(detail => !string.IsNullOrWhiteSpace(detail)))
            };

            if (provider is IJackOnnxSessionOptionsFactory factory)
            {
                try
                {
                    compatibility = factory.CheckCompatibility(Options);
                    compatibility.IsAvailable = available && compatibility.IsAvailable;
                }
                catch (Exception ex)
                {
                    compatibility.CanCreateSessionOptions = false;
                    compatibility.Detail = ex.Message;
                }
            }

            results.Add(compatibility);
        }

        return results;
    }

    public async Task<IReadOnlyList<JackOnnxModelManifest>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var manifests = new List<JackOnnxModelManifest>();
        var manifestDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateManifestPaths().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await JackOnnxModelManifest.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                manifests.Add(manifest);
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrWhiteSpace(directory))
                    manifestDirectories.Add(directory);
            }
            catch
            {
                // Invalid manifests are ignored by catalog listing; validation tooling will report details.
            }
        }

        foreach (string path in EnumerateSyntheticDiffusersGgufPaths(manifestDirectories).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                manifests.Add(CreateSyntheticDiffusersGgufManifest(path));
            }
            catch
            {
                // Synthetic GGUF entries are best-effort; the Python runner reports execution details.
            }
        }

        return manifests
            .OrderBy(manifest => manifest.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<IReadOnlyList<JackOnnxJobSnapshot>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_jobsLock)
            return Task.FromResult<IReadOnlyList<JackOnnxJobSnapshot>>(_jobs.Select(CloneJob).ToList());
    }

    public Task<JackOnnxJobSnapshot?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult<JackOnnxJobSnapshot?>(null);

        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(item => string.Equals(item.Id, jobId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(job == null ? null : CloneJob(job));
        }
    }

    public Task<JackOnnxJobSnapshot?> CancelJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(jobId))
            return Task.FromResult<JackOnnxJobSnapshot?>(null);

        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(item => string.Equals(item.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job == null)
                return Task.FromResult<JackOnnxJobSnapshot?>(null);

            if (job.State is not JackOnnxJobState.Completed and not JackOnnxJobState.Failed and not JackOnnxJobState.Cancelled)
            {
                job.State = JackOnnxJobState.Cancelled;
                job.Percent = Math.Max(job.Percent, 0);
                job.Message = "Job was cancelled.";
                job.CompletedAtUtc = DateTimeOffset.UtcNow;
                ReportProgressLocked(CloneProgress(job));
            }

            return Task.FromResult<JackOnnxJobSnapshot?>(CloneJob(job));
        }
    }

    public JackOnnxJobSnapshot CreateJob(JackOnnxMediaKind kind, JackOnnxGenerationRequest request)
    {
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        var job = new JackOnnxJobSnapshot
        {
            Kind = kind,
            ModelId = request.ModelId,
            LlmRuntimeSessionId = request.Context.LlmRuntimeSessionId,
            LlmRuntimeToolCallId = request.Context.LlmRuntimeToolCallId,
            LlmRuntimeParentRunId = request.Context.LlmRuntimeParentRunId
        };

        lock (_jobsLock)
            _jobs.Insert(0, job);

        ReportProgress(new JackOnnxProgress
        {
            JobId = job.Id,
            State = JackOnnxJobState.Queued,
            Percent = 0,
            Message = "Job queued."
        });

        var snapshot = CloneJob(job);
        try
        {
            GenerationRequested?.Invoke(snapshot, request);
        }
        catch
        {
        }

        return snapshot;
    }

    public void UpdateJobProgress(string jobId, JackOnnxProgress progress)
    {
        if (string.IsNullOrWhiteSpace(jobId) || progress == null)
            return;

        progress.JobId = jobId;
        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(item => string.Equals(item.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job != null)
            {
                job.State = progress.State;
                job.Percent = Math.Clamp(progress.Percent, 0, 100);
                job.Message = progress.Message;
                if (job.StartedAtUtc == null && progress.State is JackOnnxJobState.LoadingModel or JackOnnxJobState.Running or JackOnnxJobState.StreamingPreview or JackOnnxJobState.Encoding)
                    job.StartedAtUtc = DateTimeOffset.UtcNow;
                if (progress.PreviewArtifact != null)
                    job.Artifacts.Add(progress.PreviewArtifact);
            }
        }

        ReportProgress(progress);
    }

    public void CompleteJob(string jobId, JackOnnxGenerationResult result)
    {
        if (string.IsNullOrWhiteSpace(jobId) || result == null)
            return;

        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(item => string.Equals(item.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job == null)
                return;

            job.State = result.Success ? JackOnnxJobState.Completed : JackOnnxJobState.Failed;
            job.Percent = result.Success ? 100 : job.Percent;
            job.Message = result.Message;
            job.CompletedAtUtc = DateTimeOffset.UtcNow;
            job.Artifacts = result.Artifacts.ToList();
            ReportProgressLocked(CloneProgress(job));
        }
    }

    public async Task<JackOnnxArtifact> SaveArtifactAsync(
        string jobId,
        JackOnnxMediaKind kind,
        string fileName,
        string mediaType,
        byte[] data,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));
        if (string.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("File name is required.", nameof(fileName));
        data ??= Array.Empty<byte>();

        string jobRoot = Path.Combine(Options.ArtifactRoot, SanitizePathSegment(jobId));
        Directory.CreateDirectory(jobRoot);
        string fullPath = Path.Combine(jobRoot, SanitizeFileName(fileName));
        await File.WriteAllBytesAsync(fullPath, data, cancellationToken).ConfigureAwait(false);

        var artifact = new JackOnnxArtifact
        {
            Kind = kind,
            MediaType = mediaType,
            FilePath = fullPath,
            LengthBytes = data.LongLength,
            Metadata = metadata == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase)
        };

        lock (_jobsLock)
        {
            var job = _jobs.FirstOrDefault(item => string.Equals(item.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (job != null)
                job.Artifacts.Add(artifact);
        }

        return artifact;
    }

    public async Task<JackOnnxArtifactContent?> ReadArtifactAsync(string artifactId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            return null;

        JackOnnxArtifact? artifact;
        lock (_jobsLock)
        {
            artifact = _jobs.SelectMany(job => job.Artifacts)
                .FirstOrDefault(item => string.Equals(item.Id, artifactId, StringComparison.OrdinalIgnoreCase));
        }

        if (artifact == null || string.IsNullOrWhiteSpace(artifact.FilePath) || !File.Exists(artifact.FilePath))
            return null;

        return new JackOnnxArtifactContent
        {
            Artifact = artifact,
            Data = await File.ReadAllBytesAsync(artifact.FilePath, cancellationToken).ConfigureAwait(false)
        };
    }

    public IReadOnlyList<JackOnnxProgress> GetProgressHistory(string jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            return [];

        lock (_progressLock)
            return _progressHistory.TryGetValue(jobId, out var events) ? events.ToList() : [];
    }

    public IDisposable SubscribeProgress(string jobId, Action<JackOnnxProgress> handler)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));
        if (handler == null)
            throw new ArgumentNullException(nameof(handler));

        lock (_progressLock)
        {
            if (!_progressSubscribers.TryGetValue(jobId, out var subscribers))
            {
                subscribers = new List<Action<JackOnnxProgress>>();
                _progressSubscribers[jobId] = subscribers;
            }

            subscribers.Add(handler);
        }

        return new ProgressSubscription(this, jobId, handler);
    }

    private IEnumerable<string> EnumerateManifestPaths()
    {
        foreach (string path in Options.ModelManifestPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (File.Exists(path))
                yield return path;
            else if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*.jackonnx.json", SearchOption.AllDirectories))
                    yield return file;
                foreach (string file in Directory.EnumerateFiles(path, "manifest.json", SearchOption.AllDirectories))
                    yield return file;
            }
        }

        if (Directory.Exists(Options.ModelCachePath))
        {
            foreach (string file in Directory.EnumerateFiles(Options.ModelCachePath, "*.jackonnx.json", SearchOption.AllDirectories))
                yield return file;
            foreach (string file in Directory.EnumerateFiles(Options.ModelCachePath, "manifest.json", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private IEnumerable<string> EnumerateSyntheticDiffusersGgufPaths(IReadOnlySet<string> manifestDirectories)
    {
        foreach (string root in EnumerateModelRoots())
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories).ToArray();
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                if (!LooksLikeVideoGguf(file) || IsCoveredByManifest(file, manifestDirectories))
                    continue;
                yield return file;
            }
        }
    }

    private IEnumerable<string> EnumerateModelRoots()
    {
        foreach (string path in Options.ModelManifestPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (Directory.Exists(path))
                yield return Path.GetFullPath(path);
        }

        if (Directory.Exists(Options.ModelCachePath))
            yield return Path.GetFullPath(Options.ModelCachePath);
    }

    private static bool IsCoveredByManifest(string file, IReadOnlySet<string> manifestDirectories)
    {
        string directory = Path.GetFullPath(Path.GetDirectoryName(file) ?? "");
        foreach (string manifestDirectory in manifestDirectories)
        {
            string root = Path.GetFullPath(manifestDirectory);
            if (string.Equals(directory, root, StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static JackOnnxModelManifest CreateSyntheticDiffusersGgufManifest(string path)
    {
        path = Path.GetFullPath(path);
        string id = SanitizePathSegment(Path.GetFileNameWithoutExtension(path));
        string name = Path.GetFileNameWithoutExtension(path);
        var components = BuildSyntheticGgufComponents(path);
        string signal = string.Join(" ", components.Values.Append(path));
        string baseModel = InferDiffusersVideoBaseModel(signal);
        string pipelineClass = InferDiffusersVideoPipelineClass(signal);
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["layout"] = "complete-model",
            ["diffusersLayout"] = "gguf",
            ["source"] = "local",
            ["ggufFile"] = path,
            ["ggufFiles"] = string.Join("|", components.Values)
        };
        if (!string.IsNullOrWhiteSpace(baseModel))
        {
            metadata["baseModel"] = baseModel;
            metadata["base_model"] = baseModel;
            metadata["baseModels"] = baseModel;
        }
        if (!string.IsNullOrWhiteSpace(pipelineClass))
        {
            metadata["pipelineClass"] = pipelineClass;
            metadata["pipeline_class"] = pipelineClass;
        }

        long size = 0;
        foreach (string component in components.Values)
        {
            try
            {
                string componentPath = Path.IsPathRooted(component)
                    ? component
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory, component.Replace('/', Path.DirectorySeparatorChar)));
                size += new FileInfo(componentPath).Length;
            }
            catch
            {
            }
        }

        return new JackOnnxModelManifest
        {
            Id = id,
            Name = name,
            Type = "video.text-to-video",
            Format = "gguf",
            Precision = DetectGgufPrecision(path),
            Components = components,
            RecommendedProviders = [JackOnnxExecutionProvider.Cuda, JackOnnxExecutionProvider.DirectML, JackOnnxExecutionProvider.Cpu],
            RequiredMemoryBytes = size <= 0 ? null : size,
            ManifestPath = path,
            Metadata = metadata
        };
    }

    private static Dictionary<string, string> BuildSyntheticGgufComponents(string path)
    {
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        var files = FindRelatedVideoGgufFiles(path)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            files = [path];

        var components = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++)
        {
            string key = InferGgufComponentKey(files[i], i);
            while (components.ContainsKey(key))
                key += "_" + (i + 1).ToString();
            components[key] = Path.GetRelativePath(directory, files[i]).Replace('\\', '/');
        }

        return components;
    }

    private static IEnumerable<string> FindRelatedVideoGgufFiles(string path)
    {
        string fileName = Path.GetFileName(path).ToLowerInvariant();
        string directory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
        if (!ContainsAny(fileName, "highnoise", "high-noise", "high_noise", "lownoise", "low-noise", "low_noise"))
            return [path];

        foreach (string root in EnumerateCandidateBundleRoots(directory))
        {
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories)
                    .Where(LooksLikeVideoGguf)
                    .Take(8)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            bool hasHighNoise = files.Any(file => ContainsAny(Path.GetFileName(file).ToLowerInvariant(), "highnoise", "high-noise", "high_noise"));
            bool hasLowNoise = files.Any(file => ContainsAny(Path.GetFileName(file).ToLowerInvariant(), "lownoise", "low-noise", "low_noise"));
            if (hasHighNoise && hasLowNoise)
                return files;
        }

        return [path];
    }

    private static IEnumerable<string> EnumerateCandidateBundleRoots(string directory)
    {
        string current = Path.GetFullPath(directory);
        for (int i = 0; i < 3 && !string.IsNullOrWhiteSpace(current); i++)
        {
            yield return current;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                yield break;
            current = parent;
        }
    }

    private static string InferGgufComponentKey(string file, int index)
    {
        string text = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (ContainsAny(text, "text_encoder", "text-encoder", "umt5", "t5"))
            return "text_encoder";
        if (ContainsAny(text, "vae", "autoencoder"))
            return "vae";
        if (ContainsAny(text, "low-noise", "lownoise", "low_noise"))
            return "transformer_2";
        if (ContainsAny(text, "high-noise", "highnoise", "high_noise"))
            return "transformer";
        return index == 0 ? "transformer" : "transformer_" + (index + 1).ToString();
    }

    private static bool LooksLikeVideoGguf(string path)
    {
        string text = (path ?? "").Replace('\\', '/').ToLowerInvariant();
        return text.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) &&
               ContainsAny(text, "video", "text-to-video", "image-to-video", "t2v", "i2v", "v2v", "wan2", "wan-", "hunyuanvideo", "ltx-video", "ltxv", "mochi", "motifv", "highnoise", "lownoise");
    }

    private static string InferDiffusersVideoPipelineClass(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        if (ContainsAny(text, "ltx-video", "ltxv", "ltx_video"))
            return "LTXPipeline";
        if (ContainsAny(text, "wan2", "wan-", "wan_"))
            return "WanPipeline";
        return "";
    }

    private static string InferDiffusersVideoBaseModel(string text)
    {
        text = (text ?? "").ToLowerInvariant();
        if (ContainsAny(text, "ltx-video", "ltxv", "ltx_video"))
            return "Lightricks/LTX-Video";
        if (ContainsAny(text, "wan2.2", "wan2_2", "wan-2.2", "wan_2.2"))
            return "Wan-AI/Wan2.2-T2V-A14B-Diffusers";
        if (ContainsAny(text, "wan2.1", "wan2_1", "wan-2.1", "wan_2.1"))
            return ContainsAny(text, "1.3b", "1_3b") ? "Wan-AI/Wan2.1-T2V-1.3B-Diffusers" : "Wan-AI/Wan2.1-T2V-14B-Diffusers";
        return "";
    }

    private static string DetectGgufPrecision(string path)
    {
        string text = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        foreach (string token in new[] { "BF16", "Q2_K", "Q3_K", "Q4_K", "Q5_K", "Q6_K", "Q8_0", "Q5_1", "Q5_0", "Q4_1", "Q4_0" })
        {
            if (text.Contains(token, StringComparison.Ordinal))
                return token.ToLowerInvariant();
        }

        return "";
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (string value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<IReadOnlyList<JackOnnxDeviceInfo>> SafeProbeProviderAsync(IJackOnnxExecutionProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ProbeDevicesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return
            [
                new JackOnnxDeviceInfo
                {
                    Id = provider.Id,
                    Name = provider.Id,
                    Provider = provider.Kind,
                    IsAvailable = false,
                    Detail = ex.Message
                }
            ];
        }
    }

    private void ReportProgress(JackOnnxProgress progress)
    {
        lock (_progressLock)
            ReportProgressLocked(progress);
    }

    private void ReportProgressLocked(JackOnnxProgress progress)
    {
        progress.Percent = Math.Clamp(progress.Percent, 0, 100);
        if (!_progressHistory.TryGetValue(progress.JobId, out var history))
        {
            history = new List<JackOnnxProgress>();
            _progressHistory[progress.JobId] = history;
        }

        history.Add(CloneProgress(progress));
        if (history.Count > 500)
            history.RemoveRange(0, history.Count - 500);

        try
        {
            ProgressReported?.Invoke(CloneProgress(progress));
        }
        catch
        {
        }

        if (!_progressSubscribers.TryGetValue(progress.JobId, out var subscribers))
            return;

        foreach (var subscriber in subscribers.ToList())
        {
            try
            {
                subscriber(CloneProgress(progress));
            }
            catch
            {
            }
        }
    }

    private void UnsubscribeProgress(string jobId, Action<JackOnnxProgress> handler)
    {
        lock (_progressLock)
        {
            if (!_progressSubscribers.TryGetValue(jobId, out var subscribers))
                return;

            subscribers.Remove(handler);
            if (subscribers.Count == 0)
                _progressSubscribers.Remove(jobId);
        }
    }

    private static JackOnnxJobSnapshot CloneJob(JackOnnxJobSnapshot job)
    {
        return new JackOnnxJobSnapshot
        {
            Id = job.Id,
            State = job.State,
            Kind = job.Kind,
            ModelId = job.ModelId,
            Provider = job.Provider,
            CreatedAtUtc = job.CreatedAtUtc,
            StartedAtUtc = job.StartedAtUtc,
            CompletedAtUtc = job.CompletedAtUtc,
            Percent = job.Percent,
            Message = job.Message,
            Artifacts = job.Artifacts.ToList(),
            LlmRuntimeSessionId = job.LlmRuntimeSessionId,
            LlmRuntimeToolCallId = job.LlmRuntimeToolCallId,
            LlmRuntimeParentRunId = job.LlmRuntimeParentRunId
        };
    }

    private static JackOnnxProgress CloneProgress(JackOnnxJobSnapshot job)
    {
        return new JackOnnxProgress
        {
            JobId = job.Id,
            State = job.State,
            Percent = job.Percent,
            Message = job.Message
        };
    }

    private static JackOnnxProgress CloneProgress(JackOnnxProgress progress)
    {
        return new JackOnnxProgress
        {
            JobId = progress.JobId,
            State = progress.State,
            Percent = progress.Percent,
            Step = progress.Step,
            TotalSteps = progress.TotalSteps,
            Message = progress.Message,
            PreviewArtifact = progress.PreviewArtifact
        };
    }

    private static string SanitizePathSegment(string value)
    {
        return string.Concat((value ?? "").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')).Trim('_');
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char ch in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(ch, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "artifact.bin" : fileName;
    }

    private sealed class ProgressSubscription : IDisposable
    {
        private readonly JackOnnxRuntimeEngine _runtime;
        private readonly string _jobId;
        private readonly Action<JackOnnxProgress> _handler;
        private bool _disposed;

        public ProgressSubscription(JackOnnxRuntimeEngine runtime, string jobId, Action<JackOnnxProgress> handler)
        {
            _runtime = runtime;
            _jobId = jobId;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _runtime.UnsubscribeProgress(_jobId, _handler);
        }
    }
}

public sealed class OnnxRuntimeCpuExecutionProvider : IJackOnnxExecutionProvider, IJackOnnxSessionOptionsFactory
{
    public string Id => "onnxruntime.cpu";

    public JackOnnxExecutionProvider Kind => JackOnnxExecutionProvider.Cpu;

    public int Priority => 300;

    public Task<IReadOnlyList<JackOnnxDeviceInfo>> ProbeDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<JackOnnxDeviceInfo> devices =
        [
            new JackOnnxDeviceInfo
            {
                Id = "cpu:0",
                Name = Environment.ProcessorCount + "-thread CPU",
                Provider = JackOnnxExecutionProvider.Cpu,
                IsAvailable = true,
                Detail = "ONNX Runtime CPU provider"
            }
        ];
        return Task.FromResult(devices);
    }

    public JackOnnxProviderCompatibility CheckCompatibility(JackOnnxOptions? options = null)
    {
        try
        {
            using var sessionOptions = new OnnxRuntimeSessionFactory().CreateCpuSessionOptions(options ?? new JackOnnxOptions());
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = true,
                CanCreateSessionOptions = true,
                Detail = "CPU SessionOptions created successfully."
            };
        }
        catch (Exception ex)
        {
            return new JackOnnxProviderCompatibility
            {
                ProviderId = Id,
                Provider = Kind,
                IsAvailable = false,
                CanCreateSessionOptions = false,
                Detail = ex.Message
            };
        }
    }
}

public sealed class OnnxRuntimeSessionFactory
{
    public SessionOptions CreateCpuSessionOptions(JackOnnxOptions options)
    {
        options ??= new JackOnnxOptions();
        return new SessionOptions
        {
            EnableMemoryPattern = options.EnableMemoryPattern
        };
    }

    public InferenceSession CreateCpuSession(string modelPath, JackOnnxOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
            throw new ArgumentException("Model path is required.", nameof(modelPath));

        return new InferenceSession(modelPath, CreateCpuSessionOptions(options ?? new JackOnnxOptions()));
    }
}
