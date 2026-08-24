using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JackONNX.Runtime;
using SocketJack.Net;

namespace JackONNX.HeirowSong;

public sealed class JackOnnxHeirowSongMediaExecutor : IHeirowSongMediaExecutor, IDisposable
{
    private const string BundleId = "heirowsong-ace-step-1.5-v0.1.8";
    private const string RuntimeVersion = "v0.1.8";
    private const string RuntimeCommit = "dce621408bee8c31b4fcf4811682eb9359e1bc94";
    private const string TrustedWindowsRuntimeExecutableSha256 = "5f7b89a612c9b8af1d6456cdfcd1dbe5ca630849e79aebced9bee9a6694952ec";
    private readonly JackOnnxRuntimeEngine _runtime;
    private readonly System.Net.Http.HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private readonly List<AceWorker> _workers = new();
    private readonly ConcurrentDictionary<string, AceWorker> _assignments = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _completeModelsRoot;
    private bool _initialized;
    private bool _disposed;
    private string _runtimeDiagnostic = "The signed ACE-Step runtime manifest was not found.";

    public JackOnnxHeirowSongMediaExecutor(JackOnnxRuntimeEngine runtime, string? completeModelsRoot = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _completeModelsRoot = Path.GetFullPath(completeModelsRoot ?? Environment.GetEnvironmentVariable("HEIROWLLM_COMPLETE_MODELS") ?? Path.Combine(Environment.CurrentDirectory, "CompleteModels"));
    }

    public async Task<HeirowSongCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureWorkersAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<JackOnnxDeviceInfo> devices = await _runtime.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        bool bundleReady = IsBundleReady();
        bool healthy = false;
        foreach (AceWorker worker in _workers)
        {
            worker.Healthy = await ProbeAsync(worker, cancellationToken).ConfigureAwait(false);
            healthy |= worker.Healthy;
        }
        string backend = healthy ? string.Join(", ", _workers.Where(worker => worker.Healthy).Select(worker => worker.Backend).Distinct(StringComparer.OrdinalIgnoreCase)) : bundleReady ? "runtime-not-running" : "not-installed";
        string compatibility = healthy ? "Local ACE-Step worker is ready." : bundleReady ? _runtimeDiagnostic : "Install the 11.64 GiB heirowSong model bundle and signed ACE-Step v0.1.8 runtime pack in Models.";
        if (devices.Any(device => (device.Name ?? "").Contains("TITAN X", StringComparison.OrdinalIgnoreCase)) && !healthy)
            compatibility += " The Maxwell GTX Titan X is not marked GPU compatible until a real CUDA kernel and generation probe pass; CPU fallback remains available.";
        return new HeirowSongCapabilities
        {
            Installed = bundleReady,
            Ready = healthy,
            Backend = backend,
            CompatibilityMessage = compatibility,
            LocalWorkerCount = _workers.Count(worker => worker.Healthy),
            MaxBatchSize = _workers.Any(worker => worker.Healthy && worker.Backend.Contains("cuda", StringComparison.OrdinalIgnoreCase)) ? 2 : 1,
            Devices = devices.Where(device => device.IsAvailable).Select(device => device.Name + " (" + device.Provider + ")").ToList()
        };
    }

    public Task<HeirowSongMediaResult> GenerateAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => ExecuteAceAsync(request, "text2music", progress, cancellationToken);

    public Task<HeirowSongMediaResult> EditAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default)
    {
        if (request.Operation is "crop" or "fade" or "speed" or "export") return ExecuteFfmpegEditAsync(request, progress, cancellationToken);
        string task = request.Operation switch { "replace" => "repaint", "extend" => "repaint", _ => request.Operation };
        return ExecuteAceAsync(request, task, progress, cancellationToken);
    }

    public Task<HeirowSongMediaResult> ExtractStemAsync(HeirowSongMediaRequest request, string stem, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default)
    {
        request.Stem = stem;
        return ExecuteAceAsync(request, "extract", progress, cancellationToken);
    }

    public Task<HeirowSongMediaResult> CompleteTrackAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => ExecuteAceAsync(request, request.Operation is "lego" ? "lego" : "complete", progress, cancellationToken);

    public async Task<HeirowSongMediaResult> TranscribeMidiAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default)
    {
        string executable = ResolveBasicPitchExecutable();
        if (string.IsNullOrWhiteSpace(executable)) return Failure("Basic Pitch ONNX is not installed. Install the signed optional MIDI transcription pack; heirowSong does not label ACE-Step audio as native MIDI.", request.JobId);
        if (!File.Exists(request.SourcePath)) return Failure("MIDI source audio was not found.", request.JobId);
        string outputDirectory = EnsureOutputDirectory(request);
        progress?.Report(new HeirowSongProgress { Percent = 5, Message = "Transcribing isolated audio to MIDI with Basic Pitch." });
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add(outputDirectory); start.ArgumentList.Add(request.SourcePath); start.ArgumentList.Add("--save-midi"); start.ArgumentList.Add("--no-sonify-midi"); start.ArgumentList.Add("--no-save-model-outputs");
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return Failure("Basic Pitch process did not start.", request.JobId);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string detail = await error.ConfigureAwait(false);
            string midi = Directory.EnumerateFiles(outputDirectory, "*.mid", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() ?? "";
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(midi)) return Failure("Basic Pitch transcription failed: " + detail, request.JobId);
            progress?.Report(new HeirowSongProgress { Percent = 100, Message = "MIDI transcription is ready." });
            return Success(request.JobId, new HeirowSongMediaArtifact { FilePath = midi, MediaType = "audio/midi", Kind = "midi" });
        }
        catch (OperationCanceledException) { TryKill(process); throw; }
    }

    public Task<HeirowSongAdapterResult> TrainAdapterAsync(HeirowSongAdapterRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken = default) => Task.FromResult(new HeirowSongAdapterResult { Error = "ACE-Step LoRA training requires the signed training runtime pack. Quantized inference workers are intentionally not reused for training." });

    public Task<HeirowSongVoiceVerificationResult> VerifyVoiceAsync(HeirowSongVoiceVerificationRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new HeirowSongVoiceVerificationResult { Error = "The signed local ASR and ECAPA speaker-verification pack is not installed. A voice cannot be marked verified from a browser-supplied score." });

    private async Task<HeirowSongMediaResult> ExecuteAceAsync(HeirowSongMediaRequest request, string taskType, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken)
    {
        await EnsureWorkersAsync(cancellationToken).ConfigureAwait(false);
        AceWorker? worker = await AcquireWorkerAsync(cancellationToken).ConfigureAwait(false);
        if (worker == null) return Failure("No healthy local ACE-Step worker is available. Install the heirowSong model/runtime bundle or start the loopback worker configured by HEIROWSONG_ACE_API_URLS.", request.JobId);
        _assignments[request.JobId] = worker;
        try
        {
            progress?.Report(new HeirowSongProgress { Percent = 2, Message = "Assigned to " + worker.Name + "." });
            using var payload = new MultipartFormDataContent();
            Add(payload, "prompt", request.Prompt);
            Add(payload, "lyrics", request.Instrumental ? "[Instrumental]" : request.Lyrics);
            Add(payload, "task_type", taskType);
            Add(payload, "thinking", "true");
            Add(payload, "vocal_language", request.VocalLanguage);
            Add(payload, "audio_duration", request.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            Add(payload, "batch_size", request.BatchSize.ToString(CultureInfo.InvariantCulture));
            Add(payload, "audio_format", request.OutputFormat);
            Add(payload, "use_random_seed", (!request.Seed.HasValue).ToString().ToLowerInvariant());
            if (request.Seed.HasValue) Add(payload, "seed", request.Seed.Value.ToString(CultureInfo.InvariantCulture));
            if (request.Bpm.HasValue) Add(payload, "bpm", request.Bpm.Value.ToString(CultureInfo.InvariantCulture));
            Add(payload, "key_scale", request.KeyScale);
            Add(payload, "time_signature", request.TimeSignature);
            Add(payload, "instrumental", request.Instrumental.ToString().ToLowerInvariant());
            Add(payload, "track_name", request.Stem);
            if (File.Exists(request.SourcePath)) AddFile(payload, "src_audio", request.SourcePath);
            if (File.Exists(request.ReferencePath)) AddFile(payload, "reference_audio", request.ReferencePath);
            using HttpRequestMessage submit = worker.Request(HttpMethod.Post, "/release_task", payload);
            using HttpResponseMessage response = await _http.SendAsync(submit, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Failure("ACE-Step rejected the task: HTTP " + (int)response.StatusCode + ". " + Limit(body, 2000), request.JobId);
            string taskId = ReadTaskId(body);
            if (string.IsNullOrWhiteSpace(taskId)) return Failure("ACE-Step did not return a task identifier.", request.JobId);
            progress?.Report(new HeirowSongProgress { Percent = 5, Message = "ACE-Step accepted the local task." });
            DateTimeOffset started = DateTimeOffset.UtcNow;
            while (true)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                using var queryContent = new StringContent(JsonSerializer.Serialize(new { task_id_list = new[] { taskId } }), Encoding.UTF8, "application/json");
                using HttpRequestMessage query = worker.Request(HttpMethod.Post, "/query_result", queryContent);
                using HttpResponseMessage queryResponse = await _http.SendAsync(query, cancellationToken).ConfigureAwait(false);
                string queryBody = await queryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (!queryResponse.IsSuccessStatusCode) return Failure("ACE-Step task status failed: HTTP " + (int)queryResponse.StatusCode + ". " + Limit(queryBody, 2000), request.JobId);
                AceTaskSnapshot snapshot = ParseSnapshot(queryBody);
                double elapsedProgress = Math.Min(92, 8 + (DateTimeOffset.UtcNow - started).TotalSeconds / Math.Max(15, request.DurationSeconds * 1.5) * 80);
                progress?.Report(new HeirowSongProgress { Percent = snapshot.Progress > 0 ? Math.Min(95, snapshot.Progress) : elapsedProgress, Message = First(snapshot.Message, "Generating music locally with ACE-Step.") });
                if (snapshot.State is "failed" or "error") return Failure(First(snapshot.Error, snapshot.Message, "ACE-Step generation failed."), request.JobId);
                if (snapshot.State is "completed" or "success" or "succeeded" || snapshot.Paths.Count > 0)
                {
                    List<HeirowSongMediaArtifact> artifacts = await MaterializeOutputsAsync(worker, snapshot.Paths, request, taskType, cancellationToken).ConfigureAwait(false);
                    if (artifacts.Count == 0) return Failure("ACE-Step completed without a downloadable audio artifact.", request.JobId);
                    progress?.Report(new HeirowSongProgress { Percent = 100, Message = "Local music generation completed." });
                    return new HeirowSongMediaResult { Success = true, JobId = request.JobId, Artifacts = artifacts };
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (worker.OwnedProcess != null) await RestartWorkerAsync(worker).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) { return Failure(ex.Message, request.JobId); }
        finally { _assignments.TryRemove(request.JobId, out _); worker.Busy = false; worker.Lease.Release(); }
    }

    private async Task<List<HeirowSongMediaArtifact>> MaterializeOutputsAsync(AceWorker worker, IReadOnlyList<string> paths, HeirowSongMediaRequest request, string taskType, CancellationToken cancellationToken)
    {
        string outputDirectory = EnsureOutputDirectory(request);
        var artifacts = new List<HeirowSongMediaArtifact>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(path); if (string.IsNullOrWhiteSpace(extension)) extension = ".wav";
            string target = Path.Combine(outputDirectory, "ace-" + Guid.NewGuid().ToString("N")[..12] + extension);
            if (File.Exists(path)) File.Copy(path, target, true);
            else
            {
                string uri = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : path.StartsWith("/", StringComparison.Ordinal) ? worker.BaseUrl.TrimEnd('/') + path : worker.BaseUrl.TrimEnd('/') + "/v1/audio?path=" + Uri.EscapeDataString(path);
                using HttpRequestMessage download = worker.Request(HttpMethod.Get, uri, null, absolute: true);
                using HttpResponseMessage response = await _http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using FileStream destination = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            if (File.Exists(target) && new FileInfo(target).Length > 0) artifacts.Add(new HeirowSongMediaArtifact { FilePath = target, MediaType = Mime(target), Kind = taskType == "extract" ? "stem" : "song", Stem = taskType == "extract" ? request.Stem : "" });
        }
        return artifacts;
    }

    private async Task<HeirowSongMediaResult> ExecuteFfmpegEditAsync(HeirowSongMediaRequest request, IProgress<HeirowSongProgress> progress, CancellationToken cancellationToken)
    {
        string ffmpeg = ResolveFfmpeg();
        if (string.IsNullOrWhiteSpace(ffmpeg)) return Failure("FFmpeg is required for crop, fade, and speed edits.", request.JobId);
        if (!File.Exists(request.SourcePath)) return Failure("Edit source audio was not found.", request.JobId);
        string extension = request.Operation == "export" ? request.OutputFormat switch { "mp3" => ".mp3", "flac" => ".flac", _ => ".wav" } : ".wav";
        string target = Path.Combine(EnsureOutputDirectory(request), "edit-" + Guid.NewGuid().ToString("N")[..12] + extension);
        var start = new ProcessStartInfo { FileName = ffmpeg, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        AddArg(start, "-y", "-hide_banner", "-loglevel", "error");
        if (request.Operation == "crop") AddArg(start, "-ss", Math.Max(0, request.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture), "-to", Math.Max(request.StartSeconds + .1, request.EndSeconds).ToString("0.###", CultureInfo.InvariantCulture));
        AddArg(start, "-i", request.SourcePath);
        if (request.Operation == "fade")
        {
            double duration = Math.Max(.1, request.EndSeconds - request.StartSeconds);
            AddArg(start, "-af", "afade=t=in:st=" + Math.Max(0, request.StartSeconds).ToString("0.###", CultureInfo.InvariantCulture) + ":d=" + duration.ToString("0.###", CultureInfo.InvariantCulture));
        }
        else if (request.Operation == "speed") AddArg(start, "-af", BuildAtempo(Math.Clamp(request.Speed, .25, 4)));
        AddArg(start, "-ar", "48000", "-ac", "2", target);
        using var process = new Process { StartInfo = start };
        try
        {
            progress?.Report(new HeirowSongProgress { Percent = 10, Message = "Rendering non-destructive audio edit." });
            if (!process.Start()) return Failure("FFmpeg did not start.", request.JobId);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            string detail = await error.ConfigureAwait(false);
            if (process.ExitCode != 0 || !File.Exists(target)) return Failure("FFmpeg edit failed: " + detail, request.JobId);
            progress?.Report(new HeirowSongProgress { Percent = 100, Message = "Audio edit is ready." });
            return Success(request.JobId, new HeirowSongMediaArtifact { FilePath = target, MediaType = "audio/wav", Kind = "song" });
        }
        catch (OperationCanceledException) { TryKill(process); throw; }
    }

    private async Task EnsureWorkersAsync(CancellationToken cancellationToken)
    {
        if (_initialized && _workers.Count > 0) return;
        await _workerLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized && _workers.Count > 0) return;
            string configured = Environment.GetEnvironmentVariable("HEIROWSONG_ACE_API_URLS") ?? Environment.GetEnvironmentVariable("HEIROWSONG_ACE_API_URL") ?? "";
            foreach (string url in configured.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri != null && IPAddressIsLoopback(uri.Host)) _workers.Add(new AceWorker("ACE-Step " + (_workers.Count + 1), uri.GetLeftPart(UriPartial.Authority), Environment.GetEnvironmentVariable("HEIROWSONG_ACE_API_KEY") ?? "", "configured"));
            if (_workers.Count == 0) await StartManifestWorkersAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally { _workerLock.Release(); }
    }

    private async Task StartManifestWorkersAsync(CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(_completeModelsRoot, "heirowSong", "runtime", "runtime.json");
        if (!IsBundleReady()) { _runtimeDiagnostic = "The ACE-Step model bundle is incomplete or no longer verified."; return; }
        if (!File.Exists(manifestPath)) { _runtimeDiagnostic = "The signed ACE-Step runtime manifest was not found. Install or repair the heirowSong runtime in Models."; return; }
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        string runtimeRoot = Path.GetDirectoryName(manifestPath)!;
        if (!Fixed(Read(root, "runtimeVersion"), RuntimeVersion) || !Fixed(Read(root, "sourceCommit"), RuntimeCommit))
        {
            _runtimeDiagnostic = "The installed ACE-Step runtime is not the pinned v0.1.8 release.";
            return;
        }
        string executable = ResolveUnder(runtimeRoot, Read(root, "executable"));
        string workingDirectory = ResolveUnder(runtimeRoot, Read(root, "workingDirectory"));
        string checkpointsDirectory = ResolveUnder(runtimeRoot, Read(root, "checkpointsDirectory"));
        string expectedHash = Read(root, "sha256");
        string trustedHash = OperatingSystem.IsWindows() ? TrustedWindowsRuntimeExecutableSha256 : Environment.GetEnvironmentVariable("HEIROWSONG_RUNTIME_SHA256") ?? "";
        if (!File.Exists(executable) || !Directory.Exists(workingDirectory) || !Directory.Exists(checkpointsDirectory) || string.IsNullOrWhiteSpace(expectedHash) || string.IsNullOrWhiteSpace(trustedHash) || !Fixed(expectedHash, trustedHash) || !Fixed(expectedHash, Sha256(executable)))
        {
            _runtimeDiagnostic = "The ACE-Step runtime executable, working directory, or SHA-256 verification is invalid.";
            return;
        }
        string[] arguments = root.TryGetProperty("arguments", out JsonElement args) && args.ValueKind == JsonValueKind.Array ? args.EnumerateArray().Select(value => value.GetString() ?? "").ToArray() : Array.Empty<string>();
        int basePort = root.TryGetProperty("basePort", out JsonElement port) && port.TryGetInt32(out int parsedPort) ? parsedPort : 11520;
        string[] gpuIds = DetectNvidiaGpuIds();
        if (gpuIds.Length == 0) gpuIds = new[] { "cpu" };
        for (int index = 0; index < gpuIds.Length; index++)
        {
            string key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            int workerPort = basePort + index;
            var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument.Replace("{port}", workerPort.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{apiKey}", key, StringComparison.Ordinal));
            start.Environment["ACESTEP_API_KEY"] = key;
            start.Environment["ACESTEP_CHECKPOINTS_DIR"] = checkpointsDirectory;
            start.Environment["ACESTEP_CONFIG_PATH"] = "acestep-v15-turbo";
            start.Environment["ACESTEP_LM_MODEL_PATH"] = "acestep-5Hz-lm-0.6B";
            start.Environment["ACESTEP_LM_BACKEND"] = "pt";
            start.Environment["ACESTEP_USE_FLASH_ATTENTION"] = "false";
            start.Environment["ACESTEP_OFFLOAD_TO_CPU"] = "true";
            start.Environment["ACESTEP_OFFLOAD_DIT_TO_CPU"] = "true";
            start.Environment["ACESTEP_LM_OFFLOAD_TO_CPU"] = "true";
            start.Environment["ACESTEP_INIT_LLM"] = "true";
            start.Environment["ACESTEP_API_WORKERS"] = "1";
            start.Environment["HF_HUB_OFFLINE"] = "1";
            start.Environment["TRANSFORMERS_OFFLINE"] = "1";
            start.Environment["PYTHONUTF8"] = "1";
            start.Environment["PYTHONUNBUFFERED"] = "1";
            if (gpuIds[index] == "cpu") start.Environment["CUDA_VISIBLE_DEVICES"] = ""; else start.Environment["CUDA_VISIBLE_DEVICES"] = gpuIds[index];
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var worker = new AceWorker("ACE-Step local " + (index + 1), "http://127.0.0.1:" + workerPort.ToString(CultureInfo.InvariantCulture), key, gpuIds[index] == "cpu" ? "cpu-fallback" : IsGenerationProbeVerified(runtimeRoot, expectedHash) ? "cuda-legacy-verified" : "cuda-probe-required") { OwnedProcess = process, OwnedStartInfo = start };
            AttachProcessLogs(process, worker);
            if (!process.Start()) { process.Dispose(); continue; }
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            _workers.Add(worker);
        }
        foreach (AceWorker worker in _workers)
        {
            for (int attempt = 0; attempt < 360 && !await ProbeAsync(worker, cancellationToken).ConfigureAwait(false); attempt++)
            {
                if (worker.OwnedProcess?.HasExited == true) break;
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            if (!worker.Healthy)
                _runtimeDiagnostic = worker.OwnedProcess?.HasExited == true
                    ? "ACE-Step exited during startup (code " + worker.OwnedProcess.ExitCode.ToString(CultureInfo.InvariantCulture) + "). " + Limit(worker.LastLog, 1200)
                    : "ACE-Step did not become healthy within the 180-second startup window. " + Limit(worker.LastLog, 1200);
        }
    }

    private async Task<AceWorker?> AcquireWorkerAsync(CancellationToken cancellationToken)
    {
        AceWorker[] workers = _workers.Where(worker => worker.Healthy).OrderBy(worker => worker.Busy).ToArray();
        foreach (AceWorker worker in workers)
        {
            if (await worker.Lease.WaitAsync(0, cancellationToken).ConfigureAwait(false)) { worker.Busy = true; return worker; }
        }
        if (workers.Length == 0) return null;
        AceWorker first = workers[0]; await first.Lease.WaitAsync(cancellationToken).ConfigureAwait(false); first.Busy = true; return first;
    }

    private async Task<bool> ProbeAsync(AceWorker worker, CancellationToken cancellationToken)
    {
        try
        {
            if (worker.OwnedProcess?.HasExited == true) return worker.Healthy = false;
            using HttpRequestMessage request = worker.Request(HttpMethod.Get, "/health", null);
            using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return worker.Healthy = response.IsSuccessStatusCode;
        }
        catch { return worker.Healthy = false; }
    }

    private async Task RestartWorkerAsync(AceWorker worker)
    {
        Process? process = worker.OwnedProcess; ProcessStartInfo? start = worker.OwnedStartInfo; if (process == null || start == null) return;
        TryKill(process); process.Dispose(); await Task.Delay(200).ConfigureAwait(false); worker.Healthy = false; worker.OwnedProcess = null;
        try
        {
            var replacement = new Process { StartInfo = start, EnableRaisingEvents = true };
            AttachProcessLogs(replacement, worker);
            if (!replacement.Start()) { replacement.Dispose(); return; }
            replacement.BeginOutputReadLine(); replacement.BeginErrorReadLine();
            worker.OwnedProcess = replacement;
            for (int attempt = 0; attempt < 60; attempt++)
            {
                if (await ProbeAsync(worker, CancellationToken.None).ConfigureAwait(false)) { worker.Healthy = true; return; }
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
        catch { worker.Healthy = false; }
    }

    private bool IsBundleReady()
    {
        string manifest = Path.Combine(_completeModelsRoot, "heirowSong", BundleId, "manifest.json");
        if (!File.Exists(manifest)) return false;
        try { using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest)); JsonElement root = document.RootElement; return Read(root, "bundleId") == BundleId && root.TryGetProperty("payloadBytes", out JsonElement bytes) && bytes.TryGetInt64(out long value) && value == 12_501_168_402L && root.TryGetProperty("verified", out JsonElement verified) && verified.ValueKind == JsonValueKind.True; } catch { return false; }
    }

    private static bool IsGenerationProbeVerified(string runtimeRoot, string runtimeHash)
    {
        string path = Path.Combine(runtimeRoot, "acceptance.json");
        if (!File.Exists(path)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            return Read(root, "status").Equals("passed", StringComparison.OrdinalIgnoreCase)
                && Fixed(Read(root, "runtimeExecutableSha256"), runtimeHash)
                && root.TryGetProperty("sampleRate", out JsonElement sampleRate) && sampleRate.TryGetInt32(out int rate) && rate == 48_000
                && root.TryGetProperty("channels", out JsonElement channels) && channels.TryGetInt32(out int count) && count == 2
                && root.TryGetProperty("durationSeconds", out JsonElement duration) && duration.TryGetDouble(out double seconds) && seconds >= 9.5;
        }
        catch { return false; }
    }

    private static void AttachProcessLogs(Process process, AceWorker worker)
    {
        process.OutputDataReceived += (_, args) => worker.AppendLog(args.Data);
        process.ErrorDataReceived += (_, args) => worker.AppendLog(args.Data);
    }

    private string ResolveBasicPitchExecutable()
    {
        string configured = Environment.GetEnvironmentVariable("HEIROWSONG_BASIC_PITCH") ?? ""; if (File.Exists(configured)) return configured;
        string candidate = Path.Combine(_completeModelsRoot, "heirowSong", "basic-pitch", OperatingSystem.IsWindows() ? "basic-pitch.exe" : "basic-pitch"); return File.Exists(candidate) ? candidate : "";
    }

    private static string ResolveFfmpeg() { foreach (string? value in new string?[] { Environment.GetEnvironmentVariable("JACKONNX_FFMPEG"), Environment.GetEnvironmentVariable("SOCKETJACK_FFMPEG"), Environment.GetEnvironmentVariable("FFMPEG_PATH"), "ffmpeg" }) { if (string.IsNullOrWhiteSpace(value)) continue; if (value == "ffmpeg" || File.Exists(value)) return value; } return ""; }
    private static string EnsureOutputDirectory(HeirowSongMediaRequest request) { string output = string.IsNullOrWhiteSpace(request.OutputDirectory) ? Path.Combine(Path.GetTempPath(), "heirowSong", Safe(request.JobId)) : Path.GetFullPath(request.OutputDirectory); Directory.CreateDirectory(output); return output; }
    private static string ReadTaskId(string json) { try { using JsonDocument document = JsonDocument.Parse(json); JsonElement root = document.RootElement; foreach (string name in new[] { "task_id", "taskId", "id" }) if (root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; if (root.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object) foreach (string name in new[] { "task_id", "taskId", "id" }) if (data.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String) return value.GetString() ?? ""; } catch { } return ""; }

    private static AceTaskSnapshot ParseSnapshot(string json)
    {
        var snapshot = new AceTaskSnapshot();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json); JsonElement root = document.RootElement;
            if (root.TryGetProperty("data", out JsonElement data)) root = data;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];
            if (root.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int statusCode))
                snapshot.State = statusCode switch { 1 => "succeeded", 2 => "failed", _ => "running" };
            else snapshot.State = First(Read(root, "status"), Read(root, "state")).ToLowerInvariant();
            snapshot.Message = First(Read(root, "progress_text"), Read(root, "message"), snapshot.State); snapshot.Error = Read(root, "error");
            foreach (string property in new[] { "progress", "percent" }) if (root.TryGetProperty(property, out JsonElement value) && value.TryGetDouble(out double number)) snapshot.Progress = number <= 1 ? number * 100 : number;
            foreach (string property in new[] { "audio_path", "audioPath", "path", "file" }) { string value = Read(root, property); if (!string.IsNullOrWhiteSpace(value)) snapshot.Paths.Add(value); }
            foreach (string property in new[] { "audio_paths", "audioPaths", "files", "results" }) if (root.TryGetProperty(property, out JsonElement list) && list.ValueKind == JsonValueKind.Array) foreach (JsonElement item in list.EnumerateArray()) { if (item.ValueKind == JsonValueKind.String) snapshot.Paths.Add(item.GetString() ?? ""); else if (item.ValueKind == JsonValueKind.Object) { string value = First(Read(item, "audio_path"), Read(item, "path"), Read(item, "file"), Read(item, "url")); if (!string.IsNullOrWhiteSpace(value)) snapshot.Paths.Add(value); } }
            string resultJson = Read(root, "result");
            if (!string.IsNullOrWhiteSpace(resultJson)) ParseResultItems(resultJson, snapshot);
        }
        catch (Exception ex) { snapshot.State = "error"; snapshot.Error = "Invalid ACE-Step status response: " + ex.Message; }
        return snapshot;
    }

    private static void ParseResultItems(string json, AceTaskSnapshot snapshot)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement items = document.RootElement;
        if (items.ValueKind != JsonValueKind.Array) return;
        foreach (JsonElement item in items.EnumerateArray())
        {
            if (item.TryGetProperty("status", out JsonElement status) && status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out int code))
                snapshot.State = code switch { 1 => "succeeded", 2 => "failed", _ => snapshot.State };
            if (item.TryGetProperty("progress", out JsonElement progress) && progress.TryGetDouble(out double number)) snapshot.Progress = number <= 1 ? number * 100 : number;
            snapshot.Message = First(Read(item, "stage"), Read(item, "message"), snapshot.Message);
            snapshot.Error = First(Read(item, "error"), snapshot.Error);
            string path = First(Read(item, "audio_path"), Read(item, "path"), Read(item, "file"), Read(item, "url"));
            if (!string.IsNullOrWhiteSpace(path)) snapshot.Paths.Add(path);
        }
    }

    private static void Add(MultipartFormDataContent content, string name, string value) { if (!string.IsNullOrWhiteSpace(value)) content.Add(new StringContent(value), name); }
    private static void AddFile(MultipartFormDataContent content, string name, string path) { var stream = new StreamContent(File.OpenRead(path)); stream.Headers.ContentType = new MediaTypeHeaderValue(Mime(path)); content.Add(stream, name, Path.GetFileName(path)); }
    private static void AddArg(ProcessStartInfo start, params string[] arguments) { foreach (string argument in arguments) start.ArgumentList.Add(argument); }
    private static string BuildAtempo(double speed) { var factors = new List<double>(); while (speed > 2) { factors.Add(2); speed /= 2; } while (speed < .5) { factors.Add(.5); speed /= .5; } factors.Add(speed); return string.Join(',', factors.Select(value => "atempo=" + value.ToString("0.######", CultureInfo.InvariantCulture))); }
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch { ".wav" => "audio/wav", ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".ogg" => "audio/ogg", ".mid" or ".midi" => "audio/midi", _ => "application/octet-stream" };
    private static string[] DetectNvidiaGpuIds() { try { var start = new ProcessStartInfo { FileName = "nvidia-smi", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }; start.ArgumentList.Add("--query-gpu=index"); start.ArgumentList.Add("--format=csv,noheader,nounits"); using var process = Process.Start(start); if (process == null) return Array.Empty<string>(); string output = process.StandardOutput.ReadToEnd(); process.WaitForExit(5000); return process.ExitCode == 0 ? output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => int.TryParse(value, out _)).ToArray() : Array.Empty<string>(); } catch { return Array.Empty<string>(); } }
    private static bool IPAddressIsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(host, out System.Net.IPAddress? address) && System.Net.IPAddress.IsLoopback(address);
    private static string ResolveUnder(string root, string relative) { string result = Path.GetFullPath(Path.Combine(root, relative)); string basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar; if (!result.StartsWith(basePath, StringComparison.OrdinalIgnoreCase)) return ""; return result; }
    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static bool Fixed(string left, string right) { byte[] a = Encoding.UTF8.GetBytes((left ?? "").Trim().ToLowerInvariant()), b = Encoding.UTF8.GetBytes((right ?? "").Trim().ToLowerInvariant()); return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b); }
    private static string Read(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string First(params string[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    private static string Limit(string value, int max) => string.IsNullOrEmpty(value) || value.Length <= max ? value ?? "" : value[..max];
    private static string Safe(string value) { string result = new((value ?? "").Where(character => char.IsLetterOrDigit(character) || character is '_' or '-').Take(96).ToArray()); return string.IsNullOrWhiteSpace(result) ? Guid.NewGuid().ToString("N") : result; }
    private static HeirowSongMediaResult Failure(string error, string jobId) => new() { Error = error, JobId = jobId };
    private static HeirowSongMediaResult Success(string jobId, params HeirowSongMediaArtifact[] artifacts) => new() { Success = true, JobId = jobId, Artifacts = artifacts.ToList() };
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch { } }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        foreach (AceWorker worker in _workers) { TryKill(worker.OwnedProcess!); worker.Dispose(); }
        _workers.Clear(); _http.Dispose(); _workerLock.Dispose();
    }

    private sealed class AceWorker : IDisposable
    {
        public AceWorker(string name, string baseUrl, string apiKey, string backend) { Name = name; BaseUrl = baseUrl; ApiKey = apiKey; Backend = backend; }
        public string Name { get; }
        public string BaseUrl { get; }
        public string ApiKey { get; }
        public string Backend { get; }
        public bool Healthy { get; set; }
        public bool Busy { get; set; }
        public Process? OwnedProcess { get; set; }
        public ProcessStartInfo? OwnedStartInfo { get; set; }
        public string LastLog { get { lock (_logGate) return _lastLog; } }
        public SemaphoreSlim Lease { get; } = new(1, 1);
        private readonly object _logGate = new();
        private string _lastLog = "";
        public void AppendLog(string? line) { if (string.IsNullOrWhiteSpace(line)) return; lock (_logGate) _lastLog = Limit(_lastLog + Environment.NewLine + line, 16_000); }
        public HttpRequestMessage Request(HttpMethod method, string path, HttpContent? content, bool absolute = false) { var request = new HttpRequestMessage(method, absolute ? path : BaseUrl.TrimEnd('/') + path); if (!string.IsNullOrWhiteSpace(ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey); request.Content = content; return request; }
        public void Dispose() { OwnedProcess?.Dispose(); Lease.Dispose(); }
    }

    private sealed class AceTaskSnapshot
    {
        public string State { get; set; } = "";
        public string Message { get; set; } = "";
        public string Error { get; set; } = "";
        public double Progress { get; set; }
        public List<string> Paths { get; } = new();
    }
}
