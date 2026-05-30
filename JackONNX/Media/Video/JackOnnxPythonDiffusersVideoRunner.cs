using JackONNX.Image;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace JackONNX.Video;

public sealed class JackOnnxPythonDiffusersVideoRunner : IJackOnnxVideoModelRunner
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<JackOnnxVideoModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        VideoGenerationRequest request,
        string jobId,
        JackOnnxOptions options,
        CancellationToken cancellationToken = default)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (request == null)
            throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("Job id is required.", nameof(jobId));

        options ??= new JackOnnxOptions();
        string tempRoot = Path.Combine(Path.GetTempPath(), "jackonnx-video-runner", jobId);
        Directory.CreateDirectory(tempRoot);
        string requestPath = Path.Combine(tempRoot, "request.json");
        string resultPath = Path.Combine(tempRoot, "result.json");
        string scriptPath = Path.Combine(tempRoot, "runner.py");
        string outputPath = Path.Combine(tempRoot, "video.mp4");
        string gifOutputPath = Path.Combine(tempRoot, "video.gif");

        try
        {
            await File.WriteAllTextAsync(scriptPath, PythonRunnerScript, Utf8NoBom, cancellationToken).ConfigureAwait(false);

            var run = await RunFirstAvailablePythonAsync(
                manifest,
                request,
                options,
                scriptPath,
                requestPath,
                resultPath,
                outputPath,
                gifOutputPath,
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException("Python video runner did not write a result file. " + BuildProcessDetail(run));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            bool success = ReadBool(root, "success");
            string message = ReadString(root, "message");
            string detail = ReadString(root, "detail");
            string backend = ReadString(root, "backend");
            string mediaType = ReadString(root, "media_type");
            string videoPath = ReadString(root, "output_path");
            if (string.IsNullOrWhiteSpace(videoPath))
                videoPath = outputPath;

            if (!success)
            {
                string failure = FirstNonEmpty(message, "Python video runner failed.");
                if (!string.IsNullOrWhiteSpace(detail))
                    failure += " " + detail.Trim();
                if (IsPythonPipelineDependencyFailure(failure))
                    failure += " Install local Diffusers/Torch packages for PyTorch text-to-video layouts.";
                throw new InvalidOperationException(failure);
            }

            if (run.ExitCode != 0)
                throw new InvalidOperationException("Python video runner exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + BuildProcessDetail(run));

            if (!File.Exists(videoPath))
                throw new InvalidOperationException("Python video runner reported success, but the media file was not found: " + videoPath);

            byte[] data = await File.ReadAllBytesAsync(videoPath, cancellationToken).ConfigureAwait(false);
            mediaType = FirstNonEmpty(mediaType, GuessMediaType(videoPath));
            string extension = MediaTypeToExtension(mediaType, videoPath);
            return new JackOnnxVideoModelOutput
            {
                Data = data,
                FileName = "video_" + jobId + extension,
                MediaType = mediaType,
                Message = FirstNonEmpty(message, "Video generated successfully."),
                Runner = FirstNonEmpty(backend, "python.diffusers"),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backend"] = FirstNonEmpty(backend, "python.diffusers"),
                    ["manifestPath"] = manifest.ManifestPath,
                    ["encodedPath"] = videoPath
                }
            };
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static Dictionary<string, object?> BuildPayload(
        JackOnnxModelManifest manifest,
        VideoGenerationRequest request,
        JackOnnxOptions options,
        string outputPath,
        string gifOutputPath)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? Environment.CurrentDirectory;
        return new Dictionary<string, object?>
        {
            ["manifest_id"] = manifest.Id,
            ["manifest_name"] = manifest.Name,
            ["manifest_path"] = manifest.ManifestPath,
            ["model_dir"] = manifestDirectory,
            ["source_model_dir"] = manifestDirectory,
            ["format"] = manifest.Format,
            ["components"] = manifest.Components,
            ["required_memory_bytes"] = ResolveRequiredMemoryBytes(manifest, manifestDirectory),
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = request.NegativePrompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
            ["frames"] = request.Frames.GetValueOrDefault(),
            ["seconds"] = request.Seconds,
            ["fps"] = request.Fps,
            ["steps"] = request.Steps,
            ["guidance_scale"] = request.GuidanceScale,
            ["seed"] = request.Seed,
            ["source_path"] = request.SourcePath,
            ["source_data_url"] = request.SourceDataUrl,
            ["source_media_type"] = request.SourceMediaType,
            ["source_name"] = request.SourceName,
            ["source_kind"] = request.SourceKind,
            ["generation_mode"] = request.GenerationMode,
            ["metadata"] = manifest.Metadata,
            ["preferred_provider"] = FirstNonEmpty(ReadContextMetadata(request.Context, "preferred_provider", "preferredProvider", "provider"), options.PreferredProvider.ToString()),
            ["device_policy"] = FirstNonEmpty(ReadContextMetadata(request.Context, "device_policy", "devicePolicy"), options.DevicePolicy.ToString()),
            ["device_id"] = ResolveRequestedDeviceId(request.Context),
            ["cuda_device"] = ResolveRequestedDeviceId(request.Context),
            ["device_map"] = ReadContextMetadata(request.Context, "device_map", "deviceMap", "video_device_map", "videoDeviceMap"),
            ["allow_cpu_offload"] = ReadContextMetadata(request.Context, "allow_cpu_offload", "allowCpuOffload", "video_allow_cpu_offload", "videoAllowCpuOffload"),
            ["offload_folder"] = ReadContextMetadata(request.Context, "offload_folder", "offloadFolder", "video_offload_folder", "videoOffloadFolder"),
            ["disable_cuda_memory_guard"] = ReadContextMetadata(request.Context, "disable_cuda_memory_guard", "disableCudaMemoryGuard", "video_disable_cuda_memory_guard", "videoDisableCudaMemoryGuard"),
            ["cuda_memory_reserve_mb"] = ReadContextMetadata(request.Context, "cuda_memory_reserve_mb", "cudaMemoryReserveMb", "video_cuda_memory_reserve_mb", "videoCudaMemoryReserveMb"),
            ["cpu_max_memory"] = ReadContextMetadata(request.Context, "cpu_max_memory", "cpuMaxMemory", "video_cpu_max_memory", "videoCpuMaxMemory"),
            ["video_memory_saving"] = ReadContextMetadata(request.Context, "video_memory_saving", "videoMemorySaving", "memory_saving", "memorySaving"),
            ["allow_package_install"] = options.AllowPythonPackageInstall,
            ["output_path"] = outputPath,
            ["gif_output_path"] = gifOutputPath
        };
    }

    private static long? ResolveRequiredMemoryBytes(JackOnnxModelManifest manifest, string modelDirectory)
    {
        if (manifest.RequiredMemoryBytes.HasValue && manifest.RequiredMemoryBytes.Value > 0)
            return manifest.RequiredMemoryBytes.Value;

        try
        {
            if (File.Exists(modelDirectory))
                return new FileInfo(modelDirectory).Length;
            if (!Directory.Exists(modelDirectory))
                return null;

            long total = 0;
            foreach (string file in Directory.EnumerateFiles(modelDirectory, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { }
            }

            return total > 0 ? total : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveRequestedDeviceId(JackOnnxGenerationContext? context)
    {
        if (!string.IsNullOrWhiteSpace(context?.DeviceId))
            return context.DeviceId.Trim();
        if (context?.Metadata != null)
        {
            foreach (string key in new[] { "device_id", "deviceId", "cuda_device", "cudaDevice" })
            {
                if (context.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        return "";
    }

    private static string ReadContextMetadata(JackOnnxGenerationContext? context, params string[] keys)
    {
        if (context?.Metadata == null)
            return "";

        foreach (string key in keys)
        {
            if (context.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static async Task<ProcessRunResult> RunFirstAvailablePythonAsync(
        JackOnnxModelManifest manifest,
        VideoGenerationRequest request,
        JackOnnxOptions options,
        string scriptPath,
        string requestPath,
        string resultPath,
        string outputPath,
        string gifOutputPath,
        CancellationToken cancellationToken)
    {
        var startErrors = new List<string>();
        bool foundPythonRuntime = false;

        foreach (var command in ResolvePythonCommands(options))
        {
            try
            {
                TryDeleteFile(resultPath);
                var payload = BuildPayload(manifest, request, options, outputPath, gifOutputPath);
                await File.WriteAllTextAsync(
                    requestPath,
                    JsonSerializer.Serialize(payload, JsonOptions),
                    Utf8NoBom,
                    cancellationToken).ConfigureAwait(false);

                var run = await RunPythonAsync(
                    command,
                    scriptPath,
                    requestPath,
                    resultPath,
                    options.ImageGenerationTimeout,
                    cancellationToken).ConfigureAwait(false);
                foundPythonRuntime = true;
                if (PythonRunnerSucceeded(resultPath, run))
                    return run;

                string detail = BuildPythonRunFailureDetail(run);
                if (IsTerminalCudaGenerationFailure(detail))
                    throw CreateTerminalCudaGenerationFailure(command, "video", detail);
                startErrors.Add(command.FileName + " exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + detail);
            }
            catch (Win32Exception ex)
            {
                startErrors.Add(command.FileName + ": " + ex.Message);
            }
        }

        string finalDetail = string.Join(" ", startErrors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (foundPythonRuntime)
        {
            throw new InvalidOperationException(
                "No compatible Python video generation runtime completed the request. Python was found, but the Diffusers/Torch video pipeline failed. " +
                "Set JACKONNX_VIDEO_PYTHON, JACKONNX_PYTHON, or JackOnnxOptions.ImageGenerationPythonExecutable to force a different runtime. " +
                finalDetail);
        }

        throw new InvalidOperationException(
            "No usable Python executable was found for local video generation. Set JACKONNX_VIDEO_PYTHON or JACKONNX_PYTHON to a Python environment with torch and diffusers. " +
            finalDetail);
    }

    private static IReadOnlyList<PythonCommand> ResolvePythonCommands(JackOnnxOptions options)
    {
        var commands = new List<PythonCommand>();
        string? explicitVideoPython = Environment.GetEnvironmentVariable("JACKONNX_VIDEO_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitVideoPython))
        {
            AddPythonCommand(commands, explicitVideoPython, requireExistingFile: true);
            return commands;
        }

        string? explicitPython = Environment.GetEnvironmentVariable("JACKONNX_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython))
        {
            AddPythonCommand(commands, explicitPython, requireExistingFile: true);
            return commands;
        }

        if (!string.IsNullOrWhiteSpace(options.ImageGenerationPythonExecutable))
        {
            AddPythonCommand(commands, options.ImageGenerationPythonExecutable.Trim(), requireExistingFile: !IsCommandName(options.ImageGenerationPythonExecutable));
            return commands;
        }

        AddPythonCommand(commands, JackOnnxPythonDiffusersImageRunner.DefaultCudaLegacyPythonExecutable, requireExistingFile: true);
        if (commands.Count > 0)
            return commands;

        AddPythonCommand(commands, JackOnnxPythonDiffusersImageRunner.DefaultBundledPythonExecutable, requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "PythonCudaLegacy", "Scripts", "python.exe"), requireExistingFile: true);
        if (commands.Count > 0)
            return commands;

        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "python.exe"), requireExistingFile: true);

        return commands;
    }

    private static void AddPythonCommand(
        List<PythonCommand> commands,
        string? fileName,
        IReadOnlyList<string>? arguments = null,
        bool requireExistingFile = false)
    {
        fileName = (fileName ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        if (requireExistingFile && !File.Exists(fileName))
            return;

        arguments ??= [];
        bool exists = commands.Any(command =>
            string.Equals(command.FileName, fileName, StringComparison.OrdinalIgnoreCase) &&
            command.Arguments.SequenceEqual(arguments, StringComparer.OrdinalIgnoreCase));
        if (!exists)
            commands.Add(new PythonCommand(fileName, arguments));
    }

    private static bool IsCommandName(string fileName)
    {
        fileName = (fileName ?? "").Trim();
        return !fileName.Contains(Path.DirectorySeparatorChar) &&
               !fileName.Contains(Path.AltDirectorySeparatorChar) &&
               !Path.IsPathRooted(fileName);
    }

    private static async Task<ProcessRunResult> RunPythonAsync(
        PythonCommand command,
        string scriptPath,
        string requestPath,
        string resultPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        startInfo.Environment["PYTHONNOUSERSITE"] = "1";
        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(resultPath);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout <= TimeSpan.Zero ? TimeSpan.FromHours(1) : timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Python video generation exceeded the configured timeout of " + timeout + ".");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        return new ProcessRunResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false),
            resultPath);
    }

    private static string BuildProcessDetail(ProcessRunResult run)
    {
        string detail = FirstNonEmpty(run.StandardError, run.StandardOutput);
        return string.IsNullOrWhiteSpace(detail) ? "" : detail.Trim();
    }

    private static bool PythonRunnerSucceeded(string resultPath, ProcessRunResult run)
    {
        if (TryReadPythonRunnerResult(resultPath, out bool success, out _))
            return success;

        return run.ExitCode == 0;
    }

    private static string BuildPythonRunFailureDetail(ProcessRunResult run)
    {
        if (TryReadPythonRunnerResult(run.ResultPath, out bool success, out string detail) && !success)
            return detail;

        return BuildProcessDetail(run);
    }

    private static bool IsTerminalCudaGenerationFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        return detail.Contains("JackONNX will not fall back to CPU/offload", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("JackONNX will not run this generator on CPU", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("pipeline is using device_map/offload", StringComparison.OrdinalIgnoreCase);
    }

    private static InvalidOperationException CreateTerminalCudaGenerationFailure(PythonCommand command, string mediaKind, string detail)
    {
        string prefix = string.IsNullOrWhiteSpace(command.FileName) ? "" : command.FileName + " rejected the request. ";
        return new InvalidOperationException(
            "CUDA " + mediaKind + " generation cannot run this model on the selected GPU. " +
            prefix +
            TrimPythonTraceback(detail));
    }

    private static string TrimPythonTraceback(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return "";

        int index = detail.IndexOf("Traceback (most recent call last):", StringComparison.OrdinalIgnoreCase);
        if (index > 0)
            detail = detail[..index];
        return detail.Trim();
    }

    private static bool TryReadPythonRunnerResult(string resultPath, out bool success, out string detail)
    {
        success = false;
        detail = "";

        if (string.IsNullOrWhiteSpace(resultPath) || !File.Exists(resultPath))
            return false;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultPath));
            JsonElement root = document.RootElement;
            success = ReadBool(root, "success");
            string message = ReadString(root, "message");
            string stack = ReadString(root, "detail");
            detail = string.Join(" ", new[] { message, stack }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPythonPipelineDependencyFailure(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("Diffusers/Torch video pipeline is unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No module named", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("diffusers", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("transformers", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("torch", StringComparison.OrdinalIgnoreCase));
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.True;
    }

    private static string ReadString(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object &&
               root.TryGetProperty(name, out JsonElement value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
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

    private static string GuessMediaType(string path)
    {
        string extension = Path.GetExtension(path ?? "");
        if (extension.Equals(".gif", StringComparison.OrdinalIgnoreCase))
            return "image/gif";
        if (extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";
        return "video/mp4";
    }

    private static string MediaTypeToExtension(string mediaType, string path)
    {
        string extension = Path.GetExtension(path ?? "");
        if (!string.IsNullOrWhiteSpace(extension))
            return extension;

        mediaType = (mediaType ?? "").Trim().ToLowerInvariant();
        return mediaType switch
        {
            "image/gif" => ".gif",
            "video/webm" => ".webm",
            _ => ".mp4"
        };
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed record PythonCommand(string FileName, IReadOnlyList<string> Arguments);

    private sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError, string ResultPath);

    private const string PythonRunnerScript = """
import json
import os
import sys
import traceback
import ctypes
import base64
import io
import tempfile


def configure_temp_directory():
    configured = str(os.environ.get("TMPDIR") or os.environ.get("TEMP") or os.environ.get("TMP") or "").strip()
    if not configured and os.path.isdir("/stor2"):
        configured = "/stor2/tmp"
    if not configured:
        return
    try:
        os.makedirs(configured, exist_ok=True)
        os.environ["TMPDIR"] = configured
        os.environ["TEMP"] = configured
        os.environ["TMP"] = configured
        tempfile.tempdir = configured
    except Exception:
        pass


configure_temp_directory()


def write_result(path, payload):
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(payload, handle, ensure_ascii=True, indent=2)


def normalize_device_policy(payload):
    return str(payload.get("device_policy") or "PreferGpuThenCpu").replace("_", "").replace("-", "").strip().lower()


def normalize_preferred_provider(payload):
    return str(payload.get("preferred_provider") or "auto").strip().lower()


def requires_cuda(payload):
    provider = normalize_preferred_provider(payload)
    policy = normalize_device_policy(payload)
    return provider == "cuda" or (policy == "requirepreferredprovider" and provider in ("", "auto", "cuda"))


def has_nvidia_cuda_driver():
    candidates = ["nvcuda.dll"] if os.name == "nt" else ["libcuda.so.1", "libcuda.so"]
    for candidate in candidates:
        try:
            ctypes.CDLL(candidate)
            return True
        except Exception:
            pass
    return False


def package_version(package):
    try:
        from importlib import metadata
    except Exception:
        import importlib_metadata as metadata
    try:
        return metadata.version(package)
    except Exception:
        return ""


def parse_major_version(version):
    digits = []
    for ch in str(version or ""):
        if ch.isdigit():
            digits.append(ch)
        elif digits:
            break
    if not digits:
        return None
    try:
        return int("".join(digits))
    except Exception:
        return None


def parse_version_parts(version):
    parts = []
    current = ""
    for ch in str(version or ""):
        if ch.isdigit():
            current += ch
            continue
        if current:
            parts.append(int(current))
            current = ""
        if ch not in ".-_+ ":
            break
    if current:
        parts.append(int(current))
    while len(parts) < 3:
        parts.append(0)
    return tuple(parts[:3])


def version_at_least(version, minimum):
    return parse_version_parts(version) >= parse_version_parts(minimum)


def version_less_than(version, maximum):
    return parse_version_parts(version) < parse_version_parts(maximum)


def huggingface_hub_requirement():
    transformers_version = package_version("transformers")
    diffusers_version = package_version("diffusers")
    if (transformers_version and version_at_least(transformers_version, "5.0.0")) or \
            (diffusers_version and version_at_least(diffusers_version, "0.38.0")):
        return ("1.5.0", "2.0.0", "huggingface-hub>=1.5.0,<2.0")
    return ("0.34.0", "1.0.0", "huggingface-hub>=0.34.0,<1.0")


def ensure_legacy_torch_numpy(payload):
    version = package_version("numpy")
    major = parse_major_version(version)
    if major is not None and major < 2:
        return
    if not payload.get("allow_package_install"):
        detail = "not installed" if not version else version
        raise RuntimeError(
            "The video runtime needs numpy<2 for this CUDA Torch build, but found " + detail + ". "
            "Enable Python package install or install numpy<2 in the selected Python runtime."
        )
    install_python_package("numpy<2")


def ensure_huggingface_hub_compatible(payload):
    minimum, maximum, specifier = huggingface_hub_requirement()
    version = package_version("huggingface-hub")
    if version and version_at_least(version, minimum) and version_less_than(version, maximum):
        patch_huggingface_hub_compatibility()
        return
    if not payload.get("allow_package_install"):
        detail = "not installed" if not version else version
        raise RuntimeError(
            "The video runtime needs " + specifier + " for this Diffusers/Transformers stack, but found "
            + detail + ". Enable Python package install or repair the selected Python runtime."
        )
    install_python_package(specifier)
    patch_huggingface_hub_compatibility()


def patch_huggingface_hub_compatibility():
    patch_huggingface_hub_cached_download()
    patch_huggingface_hub_offline_mode()


def patch_huggingface_hub_cached_download():
    try:
        import huggingface_hub
        if hasattr(huggingface_hub, "cached_download"):
            return

        from urllib.parse import unquote, urlparse

        def cached_download(url_or_filename, *args, **kwargs):
            text = str(url_or_filename or "").strip()
            if not text:
                raise RuntimeError("cached_download compatibility shim received an empty URL/path.")
            if os.path.exists(text):
                return text

            parsed = urlparse(text)
            host = (parsed.netloc or "").lower()
            path_parts = [unquote(part) for part in parsed.path.strip("/").split("/") if part]
            if host in ("huggingface.co", "www.huggingface.co") and len(path_parts) >= 5 and path_parts[2] == "resolve":
                token = kwargs.get("token", kwargs.get("use_auth_token", None))
                if token is False:
                    token = None
                return huggingface_hub.hf_hub_download(
                    repo_id=path_parts[0] + "/" + path_parts[1],
                    filename="/".join(path_parts[4:]),
                    revision=path_parts[3],
                    cache_dir=kwargs.get("cache_dir", None),
                    force_download=bool(kwargs.get("force_download", False)),
                    local_files_only=bool(kwargs.get("local_files_only", False)),
                    token=token)

            if parsed.scheme in ("http", "https"):
                if bool(kwargs.get("local_files_only", False)):
                    raise RuntimeError("cached_download compatibility shim cannot fetch URL in local_files_only mode: " + text)
                import hashlib
                import urllib.request
                cache_dir = kwargs.get("cache_dir", None) or os.path.join(os.path.expanduser("~"), ".cache", "huggingface", "hub")
                os.makedirs(cache_dir, exist_ok=True)
                basename = os.path.basename(parsed.path) or "cached_download"
                output_path = os.path.join(cache_dir, hashlib.sha256(text.encode("utf-8")).hexdigest() + "." + basename)
                if bool(kwargs.get("force_download", False)) or not os.path.exists(output_path):
                    with urllib.request.urlopen(text) as response, open(output_path, "wb") as handle:
                        handle.write(response.read())
                return output_path

            raise RuntimeError("cached_download compatibility shim only supports local paths, HTTP(S) URLs, and huggingface.co resolve URLs: " + text)

        huggingface_hub.cached_download = cached_download
        sys.modules["huggingface_hub"].cached_download = cached_download
    except Exception:
        return


def patch_huggingface_hub_offline_mode():
    try:
        import huggingface_hub
        if hasattr(huggingface_hub, "is_offline_mode"):
            return
        try:
            from huggingface_hub.utils import is_offline_mode
            huggingface_hub.is_offline_mode = is_offline_mode
            return
        except Exception:
            pass
        try:
            from huggingface_hub.constants import HF_HUB_OFFLINE
        except Exception:
            HF_HUB_OFFLINE = False
        huggingface_hub.is_offline_mode = lambda: bool(HF_HUB_OFFLINE)
    except Exception:
        return


def patch_diffusers_pipeline_aliases(model_dir, diffusers_module):
    model_index_path = os.path.join(model_dir, "model_index.json")
    if not os.path.exists(model_index_path):
        return
    try:
        with open(model_index_path, "r", encoding="utf-8-sig") as handle:
            class_name = str((json.load(handle) or {}).get("_class_name") or "").strip()
    except Exception:
        return
    if not class_name or hasattr(diffusers_module, class_name):
        return

    alias_name = ""
    if class_name in ("WanDMDPipeline", "Wan2Pipeline", "Wan2_2Pipeline"):
        alias_name = "WanPipeline"
    elif class_name in ("WanDMDImageToVideoPipeline", "WanImageToVideoDMDPipeline", "Wan2ImageToVideoPipeline", "Wan2_2ImageToVideoPipeline"):
        alias_name = "WanImageToVideoPipeline"
    elif class_name.startswith("Wan") and "ImageToVideo" in class_name:
        alias_name = "WanImageToVideoPipeline"
    elif class_name.startswith("Wan"):
        alias_name = "WanPipeline"

    if alias_name and hasattr(diffusers_module, alias_name):
        setattr(diffusers_module, class_name, getattr(diffusers_module, alias_name))


def first_non_empty(*values):
    for value in values:
        text = str(value or "").strip()
        if text:
            return text
    return ""


def metadata_value(payload, *keys):
    metadata = payload.get("metadata") or {}
    if not isinstance(metadata, dict):
        metadata = {}
    for key in keys:
        value = payload.get(key)
        if value:
            return str(value).strip()
        value = metadata.get(key)
        if value:
            return str(value).strip()
    return ""


def split_metadata_values(value):
    value = str(value or "").strip().strip("'\"")
    if not value:
        return []
    if value.startswith("[") and value.endswith("]"):
        value = value[1:-1]
        return [part.strip().strip("'\"") for part in value.split(",") if part.strip()]
    if "|" in value:
        return [part.strip().strip("'\"") for part in value.split("|") if part.strip()]
    if ";" in value:
        return [part.strip().strip("'\"") for part in value.split(";") if part.strip()]
    return [value]


def normalize_model_reference(value):
    value = str(value or "").strip().strip("'\"").replace("\\", "/")
    for prefix in ("https://huggingface.co/", "http://huggingface.co/"):
        if value.lower().startswith(prefix):
            value = value[len(prefix):]
            break
    value = value.split("?", 1)[0].split("#", 1)[0].strip("/")
    if "@" in value:
        value = value.split("@", 1)[0].strip("/")
    parts = [part for part in value.split("/") if part]
    if len(parts) >= 4 and parts[2].lower() == "tree":
        return "/".join(parts[:2])
    if len(parts) >= 2:
        return "/".join(parts[:2])
    return value


def resolve_local_base_model_path(model_dir, base_model):
    base_model = normalize_model_reference(base_model)
    if not base_model or "/" not in base_model:
        return ""

    parts = base_model.replace("\\", "/").strip("/").split("/")
    current = os.path.abspath(model_dir)
    while True:
        if os.path.basename(current).lower() == "completemodels":
            for candidate in (
                os.path.join(current, *parts, "main"),
                os.path.join(current, *parts),
            ):
                if os.path.exists(os.path.join(candidate, "model_index.json")):
                    return candidate
            return ""

        parent = os.path.dirname(current)
        if parent == current:
            return ""
        current = parent


def should_allow_hf_download_for_gguf(payload):
    value = str(metadata_value(payload, "allow_hf_download", "allowHfDownload") or "").strip().lower()
    if value in ("0", "false", "no", "off"):
        return False
    return True


def allow_hf_download_temporarily(payload):
    if not should_allow_hf_download_for_gguf(payload):
        return
    os.environ["HF_HUB_OFFLINE"] = "0"
    os.environ["TRANSFORMERS_OFFLINE"] = "0"


def resolve_component_path(model_dir, relative):
    relative = str(relative or "").strip()
    if not relative:
        return ""
    if os.path.isabs(relative):
        return os.path.abspath(relative)
    return os.path.abspath(os.path.join(model_dir, relative.replace("/", os.sep).replace("\\", os.sep)))


def is_gguf_video_payload(payload):
    if str(payload.get("format") or "").strip().lower() == "gguf":
        return True
    metadata = payload.get("metadata") or {}
    if isinstance(metadata, dict):
        if str(metadata.get("diffusersLayout") or metadata.get("diffusers_layout") or "").strip().lower() == "gguf":
            return True
    components = payload.get("components") or {}
    if isinstance(components, dict):
        for value in components.values():
            if str(value or "").lower().endswith(".gguf"):
                return True
    model_dir = payload.get("model_dir") or ""
    return os.path.isfile(payload.get("manifest_path") or "") and str(payload.get("manifest_path") or "").lower().endswith(".gguf") or (
        os.path.isdir(model_dir) and any(name.lower().endswith(".gguf") for name in os.listdir(model_dir))
    )


def infer_gguf_component_key(path, index):
    text = os.path.basename(path).lower()
    if any(token in text for token in ("text_encoder", "text-encoder", "umt5", "t5")):
        return "text_encoder"
    if any(token in text for token in ("vae", "autoencoder")):
        return "vae"
    if any(token in text for token in ("low-noise", "lownoise", "low_noise")):
        return "transformer_2"
    if any(token in text for token in ("high-noise", "highnoise", "high_noise")):
        return "transformer"
    if any(token in text for token in ("transformer2", "transformer_2", "transformer-2")):
        return "transformer_2"
    return "transformer" if index == 0 else "transformer_" + str(index + 1)


def find_gguf_components(payload):
    model_dir = payload["model_dir"]
    components = payload.get("components") or {}
    result = {}
    if isinstance(components, dict):
        for key, value in components.items():
            full_path = resolve_component_path(model_dir, value)
            if full_path.lower().endswith(".gguf") and os.path.exists(full_path):
                result[str(key or "").strip() or infer_gguf_component_key(full_path, len(result))] = full_path

    manifest_path = payload.get("manifest_path") or ""
    if not result and manifest_path.lower().endswith(".gguf") and os.path.exists(manifest_path):
        result[infer_gguf_component_key(manifest_path, 0)] = os.path.abspath(manifest_path)

    if not result and os.path.isdir(model_dir):
        candidates = []
        for root, _, names in os.walk(model_dir):
            for name in names:
                if name.lower().endswith(".gguf"):
                    candidates.append(os.path.join(root, name))
        for index, path in enumerate(sorted(candidates)):
            result[infer_gguf_component_key(path, index)] = path

    if "transformer_2" in result and "transformer" not in result:
        result["transformer"] = result.pop("transformer_2")
    return result


def infer_diffusers_video_base_model(payload, components):
    values = []
    for key in ("base_model", "baseModel", "baseModels", "pipeline_base_model", "diffusers_base_model", "model_id", "modelId"):
        values.extend(split_metadata_values(metadata_value(payload, key)))
    for value in values:
        normalized = normalize_model_reference(value)
        if normalized:
            local_path = resolve_local_base_model_path(payload["model_dir"], normalized)
            return local_path or normalized

    text = " ".join([payload.get("model_dir") or "", payload.get("manifest_name") or "", payload.get("manifest_id") or ""] + list(components.values())).lower()
    if any(token in text for token in ("ltx-video", "ltxv", "ltx_video")):
        return resolve_local_base_model_path(payload["model_dir"], "Lightricks/LTX-Video") or "Lightricks/LTX-Video"
    if any(token in text for token in ("wan2.2", "wan2_2", "wan-2.2", "wan_2.2")):
        return resolve_local_base_model_path(payload["model_dir"], "Wan-AI/Wan2.2-T2V-A14B-Diffusers") or "Wan-AI/Wan2.2-T2V-A14B-Diffusers"
    if any(token in text for token in ("wan2.1", "wan2_1", "wan-2.1", "wan_2.1")):
        base = "Wan-AI/Wan2.1-T2V-1.3B-Diffusers" if any(token in text for token in ("1.3b", "1_3b")) else "Wan-AI/Wan2.1-T2V-14B-Diffusers"
        return resolve_local_base_model_path(payload["model_dir"], base) or base
    return ""


def infer_diffusers_video_pipeline_class(payload, base_model, components):
    configured = metadata_value(payload, "pipelineClass", "pipeline_class", "pipeline")
    if configured:
        return configured
    text = " ".join([payload.get("model_dir") or "", payload.get("manifest_name") or "", payload.get("manifest_id") or "", base_model] + list(components.values())).lower()
    if any(token in text for token in ("ltx-video", "ltxv", "ltx_video")):
        return "LTXPipeline"
    if any(token in text for token in ("wan2", "wan-", "wan_")):
        return "WanPipeline"
    return "DiffusionPipeline"


def select_gguf_compute_dtype(torch, device):
    if str(device).startswith("cuda"):
        try:
            if torch.cuda.is_bf16_supported():
                return torch.bfloat16
        except Exception:
            pass
        return torch.float16
    return torch.float32


def load_gguf_component(path, key, base_model, dtype, torch, diffusers_module, gguf_config):
    try:
        AutoModel = getattr(diffusers_module, "AutoModel")
        try:
            return AutoModel.from_single_file(
                path,
                quantization_config=gguf_config,
                config=base_model,
                subfolder=key,
                torch_dtype=dtype,
            )
        except TypeError:
            return AutoModel.from_single_file(
                path,
                quantization_config=gguf_config,
                torch_dtype=dtype,
            )
        except Exception:
            return AutoModel.from_single_file(
                path,
                quantization_config=gguf_config,
                torch_dtype=dtype,
            )
    except Exception as auto_exc:
        class_candidates = []
        text = (path + " " + base_model + " " + key).lower()
        if "ltx" in text:
            class_candidates.append("LTXVideoTransformer3DModel")
        if "wan" in text:
            class_candidates.append("WanTransformer3DModel")
        class_candidates.extend(["LTXVideoTransformer3DModel", "WanTransformer3DModel"])
        errors = [str(auto_exc)]
        for class_name in class_candidates:
            model_cls = getattr(diffusers_module, class_name, None)
            if model_cls is None:
                continue
            try:
                return model_cls.from_single_file(
                    path,
                    quantization_config=gguf_config,
                    config=base_model,
                    subfolder=key,
                    torch_dtype=dtype,
                )
            except Exception as exc:
                errors.append(class_name + ": " + str(exc))
        raise RuntimeError("Could not load GGUF component " + key + " from " + path + ". " + " | ".join(errors))


def disabled_text(value):
    return str(value or "").strip().lower() in ("0", "false", "off", "none", "no", "disabled")


def payload_text(payload, *keys):
    for key in keys:
        try:
            value = payload.get(key)
        except Exception:
            value = None
        if value is None:
            continue
        text = str(value).strip()
        if text:
            return text
    return ""


def payload_bool(payload, *keys):
    for key in keys:
        try:
            value = payload.get(key)
        except Exception:
            value = None
        if value is None:
            continue
        if isinstance(value, bool):
            return value
        text = str(value).strip().lower()
        if not text:
            continue
        if text in ("1", "true", "yes", "on", "enabled"):
            return True
        if text in ("0", "false", "no", "off", "disabled"):
            return False
    return None


def requested_video_device_map(payload):
    requested = payload_text(payload, "device_map", "deviceMap", "video_device_map", "videoDeviceMap")
    if not requested:
        requested = str(os.environ.get("JACKONNX_VIDEO_DEVICE_MAP") or "").strip()
    if disabled_text(requested):
        return ""
    return normalize_video_device_map(requested)


def normalize_video_device_map(value):
    text = str(value or "").strip()
    if not text:
        return ""
    normalized = text.lower().replace("-", "_").replace(" ", "_")
    if normalized in ("auto", "balanced_low_0", "balancedlow0", "sequential"):
        return "balanced"
    if normalized in ("balanced", "cuda", "cpu"):
        return normalized
    if normalized in ("gpu", "gpus", "cuda_auto", "cuda_balanced", "multi_gpu", "multigpu"):
        return "balanced"
    if normalized.startswith("cuda:"):
        return "cuda"
    return text


def resolve_video_device_map(payload, torch, device):
    if not str(device).startswith("cuda"):
        return ""
    return requested_video_device_map(payload)


def cuda_max_memory_map(torch, payload=None):
    try:
        count = int(torch.cuda.device_count())
    except Exception:
        count = 0
    if count <= 0:
        return {}
    try:
        reserve_text = payload_text(payload or {}, "cuda_memory_reserve_mb", "cudaMemoryReserveMb", "video_cuda_memory_reserve_mb", "videoCudaMemoryReserveMb")
        reserve_mb = int(float(reserve_text or os.environ.get("JACKONNX_VIDEO_CUDA_MEMORY_RESERVE_MB") or "768"))
    except Exception:
        reserve_mb = 768
    reserve_mb = max(128, reserve_mb)
    free = query_cuda_free_memory_mb()
    result = {}
    for index in range(count):
        mb = 0
        if index < len(free):
            mb = int(free[index])
        if mb <= 0:
            try:
                mb = int(torch.cuda.get_device_properties(index).total_memory / (1024 * 1024))
            except Exception:
                mb = 0
        if mb > 0:
            result[index] = str(max(1024, mb - reserve_mb)) + "MiB"
    cpu_max = payload_text(payload or {}, "cpu_max_memory", "cpuMaxMemory", "video_cpu_max_memory", "videoCpuMaxMemory")
    if not cpu_max:
        cpu_max = str(os.environ.get("JACKONNX_VIDEO_CPU_MAX_MEMORY") or "").strip()
    if cpu_max and not disabled_text(cpu_max):
        result["cpu"] = cpu_max
    return result


def video_offload_folder(payload=None):
    configured = payload_text(payload or {}, "offload_folder", "offloadFolder", "video_offload_folder", "videoOffloadFolder")
    if not configured:
        configured = str(os.environ.get("JACKONNX_VIDEO_OFFLOAD_FOLDER") or "").strip()
    if configured and not disabled_text(configured):
        return configured
    if os.path.isdir("/stor2"):
        return "/stor2/JackONNXOffload"
    return os.path.join(tempfile.gettempdir(), "jackonnx-video-offload")


def diffusers_load_kwargs(payload, torch, device, dtype, local_files_only):
    kwargs = {
        "torch_dtype": dtype,
        "local_files_only": local_files_only,
    }
    variant = preferred_diffusers_variant(payload, device)
    if variant:
        kwargs["variant"] = variant
    device_map = resolve_video_device_map(payload, torch, device)
    if device_map:
        kwargs["device_map"] = device_map
        kwargs["low_cpu_mem_usage"] = True
        max_memory = cuda_max_memory_map(torch, payload)
        if max_memory:
            kwargs["max_memory"] = max_memory
        offload_folder = video_offload_folder(payload)
        try:
            os.makedirs(offload_folder, exist_ok=True)
            kwargs["offload_folder"] = offload_folder
            if str(os.environ.get("JACKONNX_VIDEO_OFFLOAD_STATE_DICT") or "").strip().lower() in ("1", "true", "yes", "on"):
                kwargs["offload_state_dict"] = True
        except Exception:
            pass
    return kwargs, device_map


def preferred_diffusers_variant(payload, device):
    configured = str(payload.get("variant") or payload.get("weight_variant") or os.environ.get("JACKONNX_VIDEO_VARIANT") or "").strip()
    if disabled_text(configured):
        return ""
    if configured:
        return configured
    if not str(device).startswith("cuda"):
        return ""
    model_dir = str(payload.get("model_dir") or "").strip()
    if not model_dir or not os.path.isdir(model_dir):
        return ""
    try:
        for root, _, files in os.walk(model_dir):
            for name in files:
                lower = name.lower()
                if ".fp16." in lower or lower.endswith(".fp16.safetensors") or lower.endswith(".fp16.bin"):
                    return "fp16"
    except Exception:
        pass
    return ""


def pipeline_uses_device_map(backend_detail):
    return ":device_map=" in str(backend_detail or "")


def maybe_move_pipeline_to_device(pipe, device, backend_detail):
    if pipeline_uses_device_map(backend_detail):
        return pipe
    return pipe.to(device)


def load_diffusers_gguf_pipeline(payload, torch, diffusers_module):
    try:
        GGUFQuantizationConfig = getattr(diffusers_module, "GGUFQuantizationConfig")
    except Exception as exc:
        raise RuntimeError("Diffusers GGUF support is unavailable. Install a recent diffusers build with GGUFQuantizationConfig and the gguf package. " + str(exc))

    components = find_gguf_components(payload)
    if not components:
        raise RuntimeError("No .gguf transformer checkpoint was found for this video model.")

    base_model = infer_diffusers_video_base_model(payload, components)
    if not base_model:
        raise RuntimeError("This GGUF video checkpoint needs a Diffusers base model. Add base_model/baseModel metadata to manifest.json or place a matching LTX/Wan CompleteModels base pipeline beside it.")

    allow_hf_download_temporarily(payload)
    device = select_torch_device(payload, torch)
    dtype = select_gguf_compute_dtype(torch, device)
    enforce_cuda_memory_guard(
        payload,
        device,
        sum(path_size_bytes(path) for path in components.values()) or payload.get("required_memory_bytes"),
        "video GGUF model '" + payload.get("manifest_id", "") + "'"
    )
    gguf_config = GGUFQuantizationConfig(compute_dtype=dtype)
    loaded_components = {}
    for key, path in components.items():
        loaded_components[key] = load_gguf_component(path, key, base_model, dtype, torch, diffusers_module, gguf_config)

    pipeline_class_name = infer_diffusers_video_pipeline_class(payload, base_model, components)
    pipeline_cls = getattr(diffusers_module, pipeline_class_name, None)
    if pipeline_cls is None:
        pipeline_cls = getattr(diffusers_module, "DiffusionPipeline")

    local_files_only = os.path.exists(str(base_model))
    kwargs, device_map = diffusers_load_kwargs(payload, torch, device, dtype, local_files_only)
    try:
        pipe = pipeline_cls.from_pretrained(
            base_model,
            **kwargs,
            **loaded_components,
        )
    except TypeError:
        pipe = getattr(diffusers_module, "DiffusionPipeline").from_pretrained(
            base_model,
            **kwargs,
            **loaded_components,
        )
    backend = "gguf:" + pipeline_class_name + ":" + ",".join(sorted(loaded_components.keys()))
    if device_map:
        backend += ":device_map=" + str(device_map)
    return pipe, device, backend


def read_model_card_metadata(model_dir):
    readme_path = os.path.join(model_dir, "README.md")
    metadata = {"tags": [], "base_models": []}
    if not os.path.exists(readme_path):
        return metadata

    try:
        with open(readme_path, "r", encoding="utf-8-sig") as handle:
            lines = handle.read().splitlines()
    except Exception:
        return metadata

    if not lines or lines[0].strip() != "---":
        return metadata

    current_key = ""
    for line in lines[1:]:
        stripped = line.strip()
        if stripped == "---":
            break
        if not stripped:
            continue
        if stripped.startswith("-") and current_key == "tags":
            value = stripped[1:].strip().strip("'\"")
            if value:
                metadata["tags"].append(value)
            continue
        if stripped.startswith("-") and current_key == "base_model":
            value = stripped[1:].strip().strip("'\"")
            if value:
                metadata["base_models"].append(value)
                if "base_model" not in metadata:
                    metadata["base_model"] = value
            continue
        if ":" in stripped:
            key, value = stripped.split(":", 1)
            key = key.strip()
            value = value.strip().strip("'\"")
            current_key = key
            if key == "tags":
                for item in split_metadata_values(value):
                    metadata["tags"].append(item)
            elif key == "base_model":
                for item in split_metadata_values(value):
                    metadata["base_models"].append(item)
                    if "base_model" not in metadata:
                        metadata["base_model"] = item
            else:
                metadata[key] = value
    return metadata


def find_safetensors_weight(model_dir):
    candidates = [
        name for name in os.listdir(model_dir)
        if name.lower().endswith(".safetensors") and os.path.isfile(os.path.join(model_dir, name))
    ]
    if not candidates:
        return ""
    candidates.sort(key=lambda name: (0 if "lora" in name.lower() else 1, name.lower()))
    return candidates[0]


def is_video_lora_adapter(model_dir, model_card, manifest_metadata=None, payload=None):
    manifest_metadata = manifest_metadata if isinstance(manifest_metadata, dict) else {}
    payload = payload if isinstance(payload, dict) else {}
    try:
        names = " ".join(os.listdir(model_dir))
    except Exception:
        names = ""
    text = " ".join(model_card.get("tags") or []) + " " + names
    text += " " + str(model_card.get("library_name") or model_card.get("template") or "")
    text += " " + str(manifest_metadata.get("adapterType") or manifest_metadata.get("adapter_type") or "")
    text += " " + str(manifest_metadata.get("adapterRequiresBaseModel") or manifest_metadata.get("adapter_requires_base_model") or "")
    text += " " + str(payload.get("manifest_id") or "") + " " + str(payload.get("manifest_name") or "") + " " + str(payload.get("format") or "")
    text = text.lower()
    if "lora" in text or "adapter" in text or "diffusion-lora" in text:
        return True
    if find_safetensors_weight(model_dir) and get_base_model_candidates(payload, model_card, manifest_metadata):
        return True
    return False


def infer_video_base_model_from_text(payload, extra_values):
    text = " ".join([
        str(payload.get("model_dir") or ""),
        str(payload.get("manifest_name") or ""),
        str(payload.get("manifest_id") or ""),
        " ".join(str(value or "") for value in extra_values),
    ]).lower()
    if any(token in text for token in ("ltx-video", "ltxv", "ltx_video")):
        return "Lightricks/LTX-Video"
    if any(token in text for token in ("wan2.2", "wan2_2", "wan-2.2", "wan_2.2")):
        return "Wan-AI/Wan2.2-T2V-A14B-Diffusers"
    if any(token in text for token in ("wan2.1", "wan2_1", "wan-2.1", "wan_2.1")):
        return "Wan-AI/Wan2.1-T2V-1.3B-Diffusers" if any(token in text for token in ("1.3b", "1_3b")) else "Wan-AI/Wan2.1-T2V-14B-Diffusers"
    return ""


def get_base_model_candidates(payload, model_card, manifest_metadata):
    values = []
    for key in ("base_model", "baseModel", "baseModels", "pipeline_base_model", "diffusers_base_model"):
        values.extend(split_metadata_values(model_card.get(key) if isinstance(model_card, dict) else ""))
        values.extend(split_metadata_values(manifest_metadata.get(key) if isinstance(manifest_metadata, dict) else ""))
        values.extend(split_metadata_values(payload.get(key) or ""))
    values.extend(model_card.get("base_models") or [])

    inferred = infer_video_base_model_from_text(payload, values + [find_safetensors_weight(payload["model_dir"])])
    if inferred:
        values.append(inferred)

    result = []
    seen = set()
    for value in values:
        normalized = normalize_model_reference(value)
        if not normalized or normalized.lower() in ("none", "null"):
            continue
        key = normalized.lower()
        if key not in seen:
            seen.add(key)
            result.append(normalized)
    return result


def load_diffusers_video_lora_pipeline(payload, torch, diffusers_module, model_card, manifest_metadata):
    model_dir = payload["model_dir"]
    base_models = get_base_model_candidates(payload, model_card, manifest_metadata)
    if not base_models:
        raise RuntimeError(
            "This video model is a LoRA adapter, not a standalone Diffusers pipeline. "
            "Its model card or manifest does not declare a base_model, and no Wan/LTX base model could be inferred."
        )

    base_model = base_models[0]
    base_path = ""
    source_model_dir = payload.get("source_model_dir") or model_dir
    for candidate_base_model in base_models:
        candidate_base_path = resolve_local_base_model_path(model_dir, candidate_base_model)
        if not candidate_base_path:
            candidate_base_path = resolve_local_base_model_path(source_model_dir, candidate_base_model)
        if candidate_base_path:
            base_model = candidate_base_model
            base_path = candidate_base_path
            break
    if not base_path:
        base_path = base_model

    weight_name = find_safetensors_weight(model_dir)
    if not weight_name:
        raise RuntimeError("This video model looks like a LoRA adapter, but no .safetensors weight file was found.")

    allow_hf_download_temporarily(payload)
    if os.path.exists(str(base_path)):
        patch_diffusers_pipeline_aliases(base_path, diffusers_module)
    device = select_torch_device(payload, torch)
    dtype = torch.float16 if str(device).startswith("cuda") else torch.float32
    enforce_cuda_memory_guard(
        payload,
        device,
        path_size_bytes(base_path) or payload.get("required_memory_bytes"),
        "video LoRA base model '" + base_model + "'"
    )
    pipeline_class_name = infer_diffusers_video_pipeline_class(payload, base_model, {"adapter": weight_name})
    pipeline_cls = getattr(diffusers_module, pipeline_class_name, None)
    if pipeline_cls is None:
        pipeline_cls = getattr(diffusers_module, "DiffusionPipeline")
    local_files_only = os.path.exists(str(base_path))
    kwargs, device_map = diffusers_load_kwargs(payload, torch, device, dtype, local_files_only)
    try:
        pipe = pipeline_cls.from_pretrained(
            base_path,
            **kwargs,
        )
    except TypeError:
        pipe = getattr(diffusers_module, "DiffusionPipeline").from_pretrained(
            base_path,
            **kwargs,
        )
    except Exception as exc:
        raise RuntimeError(
            "This video model is a LoRA adapter and requires the base model '" + base_model +
            "'. Download/register that base model locally before using this adapter. " + str(exc)
        )

    try:
        pipe.load_lora_weights(model_dir, weight_name=weight_name)
    except Exception as exc:
        raise RuntimeError("Could not load video LoRA weights '" + weight_name + "' from " + model_dir + ". " + str(exc))
    backend = "lora:" + pipeline_class_name + ":" + base_model + ":" + weight_name
    if device_map:
        backend += ":device_map=" + str(device_map)
    return pipe, device, backend


def ensure_torchvision_compatible(payload):
    try:
        import torch
    except Exception:
        return

    torch_version = str(getattr(torch, "__version__", ""))
    torch_cuda = str(getattr(torch.version, "cuda", "") or "")
    expected = ""
    index_url = ""
    if torch_version.startswith("2.1.") and torch_cuda.startswith("11.8"):
        expected = "0.16.2+cu118"
        index_url = "https://download.pytorch.org/whl/cu118"
    if not expected:
        return

    version = package_version("torchvision")
    if version == expected:
        return

    if not payload.get("allow_package_install"):
        detail = "not installed" if not version else version
        raise RuntimeError(
            "The video runtime needs torchvision==" + expected + " for torch " + torch_version +
            ", but found " + detail + ". Enable Python package install or repair the selected Python runtime."
        )
    install_python_package("torchvision==" + expected, no_deps=True, index_url=index_url)


def ensure_gguf_package_if_needed(payload):
    if not is_gguf_video_payload(payload):
        return
    try:
        import gguf  # noqa: F401
        return
    except Exception:
        pass
    if not payload.get("allow_package_install"):
        raise RuntimeError("The video runtime needs the gguf Python package to load Diffusers GGUF checkpoints. Enable Python package install or install gguf in the selected Python runtime.")
    install_python_package("gguf")


def ensure_runtime_dependencies(payload):
    # Torch 2.1 CUDA wheels used for legacy GPUs are compiled against NumPy 1.x.
    ensure_legacy_torch_numpy(payload)
    ensure_huggingface_hub_compatible(payload)
    ensure_torchvision_compatible(payload)
    ensure_gguf_package_if_needed(payload)


def should_prefer_cuda(payload):
    provider = normalize_preferred_provider(payload)
    policy = normalize_device_policy(payload)
    if provider == "cpu" or policy == "cpuonly":
        return False
    return provider in ("", "auto", "cuda")


def normalize_cuda_device_id(payload):
    raw = str(payload.get("device_id") or payload.get("cuda_device") or "").strip().lower()
    if not raw:
        return ""
    if raw == "cuda":
        return "cuda"
    if raw.startswith("cuda:"):
        raw = raw[5:].strip()
    elif raw.startswith("gpu:"):
        raw = raw[4:].strip()
    if raw.isdigit():
        return "cuda:" + raw
    return ""


def cuda_device_index(device):
    text = str(device or "").strip().lower()
    if text.startswith("cuda:"):
        suffix = text[5:].strip()
        if suffix.isdigit():
            return int(suffix)
    return 0


def query_cuda_free_memory_mb():
    try:
        import subprocess
        result = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.free", "--format=csv,noheader,nounits"],
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            timeout=2,
        )
        if result.returncode != 0:
            return []
        values = []
        for line in result.stdout.splitlines():
            text = line.strip().split(",", 1)[0].strip()
            if text:
                values.append(int(float(text)))
        return values
    except Exception:
        return []


def select_available_cuda_device(payload, torch, default_min_free_mb):
    requested_device = normalize_cuda_device_id(payload)
    if requested_device:
        return requested_device
    count = torch.cuda.device_count()
    free = query_cuda_free_memory_mb()
    if free:
        usable = [(index, mb) for index, mb in enumerate(free[:count])]
        if usable:
            index, mb = max(usable, key=lambda item: item[1])
            try:
                min_free_mb = int(float(payload.get("min_cuda_free_memory_mb") or os.environ.get("JACKONNX_MIN_VIDEO_CUDA_FREE_MEMORY_MB") or os.environ.get("JACKONNX_MIN_CUDA_FREE_MEMORY_MB") or default_min_free_mb))
            except Exception:
                min_free_mb = default_min_free_mb
            if mb < min_free_mb and not requires_cuda(payload):
                return "cpu"
            return "cuda:" + str(index) if count > 1 else "cuda"
    return "cuda"


def torch_cuda_supports_current_device(torch, device):
    try:
        major, minor = torch.cuda.get_device_capability(cuda_device_index(device))
        arch_list = [str(item).lower() for item in (torch.cuda.get_arch_list() or [])]
        for arch in arch_list:
            clean = arch.replace("sm_", "").replace("compute_", "").replace("_", ".").strip()
            if len(clean) == 2 and clean.isdigit():
                clean = clean[0] + "." + clean[1]
            parts = clean.split(".", 1)
            if len(parts) == 0 or not parts[0].isdigit():
                continue
            supported_major = int(parts[0])
            supported_minor = 0
            if len(parts) > 1:
                minor_digits = "".join(ch for ch in parts[1] if ch.isdigit())
                if minor_digits:
                    supported_minor = int(minor_digits)
            if int(major) == supported_major and int(minor) >= supported_minor:
                return True
        return False
    except Exception:
        return True


def select_torch_device(payload, torch):
    requested_device = normalize_cuda_device_id(payload)
    selected_device = select_available_cuda_device(payload, torch, 8192)
    if selected_device == "cpu":
        return "cpu"
    if torch.cuda.is_available():
        if requested_device:
            index = cuda_device_index(requested_device)
            if index >= torch.cuda.device_count():
                raise RuntimeError("CUDA device " + requested_device + " was requested, but only " + str(torch.cuda.device_count()) + " device(s) are visible.")
        if torch_cuda_supports_current_device(torch, selected_device):
            return selected_device
        if requires_cuda(payload):
            major, minor = torch.cuda.get_device_capability(cuda_device_index(selected_device))
            arch_list = ", ".join(torch.cuda.get_arch_list() or [])
            raise RuntimeError(
                "CUDA is available, but this PyTorch build does not include kernels for this GPU. "
                "Device capability: sm_" + str(int(major)) + str(int(minor)) + "; supported: " + arch_list + "."
            )
        return "cpu"
    if requires_cuda(payload):
        raise RuntimeError("CUDA was requested, but torch.cuda.is_available() returned false.")
    if not should_prefer_cuda(payload):
        return "cpu"
    if has_nvidia_cuda_driver():
        version = str(getattr(torch, "__version__", "unknown"))
        torch_cuda = str(getattr(torch.version, "cuda", "") or "none")
        raise RuntimeError(
            "CUDA GPU is available, but this Python has a CPU-only PyTorch build. "
            "Install CUDA-enabled PyTorch or point JACKONNX_VIDEO_PYTHON at the CUDA runtime. "
            "Torch version: " + version + "; torch CUDA: " + torch_cuda + "."
        )
    return "cpu"


def format_bytes(value):
    try:
        value = float(value)
    except Exception:
        return "unknown"
    units = ["bytes", "KiB", "MiB", "GiB", "TiB"]
    index = 0
    while value >= 1024.0 and index < len(units) - 1:
        value /= 1024.0
        index += 1
    if index == 0:
        return str(int(value)) + " " + units[index]
    return ("%.1f" % value) + " " + units[index]


def path_size_bytes(path):
    try:
        if not path:
            return 0
        if os.path.isfile(path):
            return os.path.getsize(path)
        if not os.path.isdir(path):
            return 0
        total = 0
        for root, _, files in os.walk(path):
            for name in files:
                try:
                    total += os.path.getsize(os.path.join(root, name))
                except Exception:
                    pass
        return total
    except Exception:
        return 0


def cuda_free_bytes_for_device(device):
    free = query_cuda_free_memory_mb()
    if not free:
        return 0
    index = cuda_device_index(device)
    if index < 0 or index >= len(free):
        return 0
    try:
        return int(free[index]) * 1024 * 1024
    except Exception:
        return 0


def cuda_memory_guard_disabled(payload=None):
    configured = payload_bool(payload or {}, "disable_cuda_memory_guard", "disableCudaMemoryGuard", "video_disable_cuda_memory_guard", "videoDisableCudaMemoryGuard")
    if configured is not None:
        return configured
    return str(os.environ.get("JACKONNX_DISABLE_CUDA_MEMORY_GUARD") or "").strip().lower() in ("1", "true", "yes", "on")


def enforce_cuda_memory_guard(payload, device, required_bytes, label):
    if cuda_memory_guard_disabled(payload) or not str(device).startswith("cuda"):
        return
    if not requires_cuda(payload):
        return
    if requested_video_device_map(payload) or cpu_offload_allowed(payload):
        return
    try:
        required = int(required_bytes or 0)
    except Exception:
        required = 0
    if required <= 0:
        return
    free_bytes = cuda_free_bytes_for_device(device)
    if free_bytes <= 0:
        return
    try:
        reserve_text = payload_text(payload or {}, "cuda_memory_reserve_mb", "cudaMemoryReserveMb", "video_cuda_memory_reserve_mb", "videoCudaMemoryReserveMb")
        reserve_mb = int(float(reserve_text or os.environ.get("JACKONNX_VIDEO_CUDA_MEMORY_RESERVE_MB") or os.environ.get("JACKONNX_CUDA_MEMORY_RESERVE_MB") or "1024"))
    except Exception:
        reserve_mb = 1024
    usable_bytes = max(0, free_bytes - max(128, reserve_mb) * 1024 * 1024)
    if required <= usable_bytes:
        return
    raise RuntimeError(
        "CUDA was requested for " + label + ", but the model needs about " + format_bytes(required) +
        " and " + str(device) + " only has " + format_bytes(free_bytes) +
        " free (" + format_bytes(usable_bytes) + " after reserve). JackONNX will not fall back to CPU/offload. "
        "Choose a smaller model, free GPU memory, or install a quantized model that fits this GPU."
    )


def cpu_offload_allowed(payload=None):
    configured = payload_bool(payload or {}, "allow_cpu_offload", "allowCpuOffload", "video_allow_cpu_offload", "videoAllowCpuOffload")
    if configured is not None:
        return configured
    return str(os.environ.get("JACKONNX_ALLOW_CPU_OFFLOAD") or "").strip().lower() in ("1", "true", "yes", "on")


def verify_cuda_pipeline(pipe, torch, device, payload, label, backend_detail=""):
    if cpu_offload_allowed(payload) or not str(device).startswith("cuda") or not requires_cuda(payload):
        return
    if pipeline_uses_device_map(backend_detail):
        if not requested_video_device_map(payload):
            raise RuntimeError(
                "CUDA was requested for " + label + ", but the pipeline is using device_map/offload. "
                "JackONNX will not run this generator through CPU/offload unless JACKONNX_ALLOW_CPU_OFFLOAD=1."
            )
    components = getattr(pipe, "components", None)
    if isinstance(components, dict):
        items = list(components.items())
    else:
        items = [(name, getattr(pipe, name, None)) for name in ("unet", "transformer", "vae", "text_encoder", "text_encoder_2")]
    cpu_components = []
    cuda_components = []
    for name, module in items:
        if module is None or isinstance(module, (str, int, float, bool)):
            continue
        if not hasattr(module, "parameters"):
            continue
        try:
            param = next(module.parameters(), None)
        except TypeError:
            try:
                param = next(iter(module.parameters()), None)
            except Exception:
                param = None
        except Exception:
            param = None
        if param is None:
            continue
        device_type = str(getattr(param, "device", "")).split(":", 1)[0].lower()
        if device_type == "cuda":
            cuda_components.append(str(name))
        elif device_type == "cpu":
            cpu_components.append(str(name))
    if cpu_components:
        raise RuntimeError(
            "CUDA was requested for " + label + ", but the Diffusers pipeline stayed on CPU components: " +
            ", ".join(cpu_components[:6]) + ". JackONNX will not run this generator on CPU."
        )


def should_enable_pipeline_memory_saving(payload, device):
    requested = payload_bool(payload or {}, "video_memory_saving", "videoMemorySaving", "memory_saving", "memorySaving")
    if requested is not None:
        return requested
    configured = str(os.environ.get("JACKONNX_VIDEO_MEMORY_SAVING") or "").strip().lower()
    if configured in ("1", "true", "yes", "on"):
        return True
    if configured in ("0", "false", "no", "off"):
        return False
    if not str(device).startswith("cuda"):
        return False
    try:
        required = int(payload.get("required_memory_bytes") or 0)
        if required >= 20 * 1024 * 1024 * 1024:
            return True
    except Exception:
        pass
    text = " ".join([
        str(payload.get("manifest_id") or ""),
        str(payload.get("manifest_name") or ""),
        str(payload.get("model_dir") or ""),
        json.dumps(payload.get("metadata") or {}),
    ]).lower()
    return any(token in text for token in ("wan2", "wan-", "wan_", "ltx-video", "ltxv", "gguf"))


def configure_pipeline_memory_options(pipe, payload, device):
    if not should_enable_pipeline_memory_saving(payload, device):
        return
    if hasattr(pipe, "enable_attention_slicing"):
        pipe.enable_attention_slicing()
    if hasattr(pipe, "enable_vae_slicing"):
        pipe.enable_vae_slicing()
    if hasattr(pipe, "enable_vae_tiling"):
        try:
            pipe.enable_vae_tiling()
        except Exception:
            pass


def load_pipeline(payload, torch):
    try:
        patch_huggingface_hub_compatibility()
        from diffusers import DiffusionPipeline
        import diffusers
    except Exception as exc:
        raise RuntimeError("Diffusers/Torch video pipeline is unavailable: " + str(exc))

    model_dir = payload["model_dir"]
    if is_gguf_video_payload(payload):
        pipe, device, backend_detail = load_diffusers_gguf_pipeline(payload, torch, diffusers)
        configure_pipeline_memory_options(pipe, payload, device)
        pipe = maybe_move_pipeline_to_device(pipe, device, backend_detail)
        verify_cuda_pipeline(pipe, torch, device, payload, "video GGUF model '" + payload.get("manifest_id", "") + "'", backend_detail)
        return pipe, device, backend_detail

    model_index = os.path.join(model_dir, "model_index.json")
    if not os.path.exists(model_index):
        model_card = read_model_card_metadata(model_dir)
        manifest_metadata = payload.get("metadata") or {}
        if not isinstance(manifest_metadata, dict):
            manifest_metadata = {}
        if is_video_lora_adapter(model_dir, model_card, manifest_metadata, payload):
            pipe, device, backend_detail = load_diffusers_video_lora_pipeline(payload, torch, diffusers, model_card, manifest_metadata)
            configure_pipeline_memory_options(pipe, payload, device)
            pipe = maybe_move_pipeline_to_device(pipe, device, backend_detail)
            verify_cuda_pipeline(pipe, torch, device, payload, "video LoRA model '" + payload.get("manifest_id", "") + "'", backend_detail)
            return pipe, device, backend_detail

        safetensors_weight = find_safetensors_weight(model_dir)
        if safetensors_weight:
            raise RuntimeError(
                "This video model folder contains a .safetensors file but no model_index.json. "
                "If it is a LoRA adapter, add adapterType=lora and base_model/baseModel metadata, or download/register the full Diffusers pipeline folder: "
                + model_dir
            )
        raise RuntimeError(
            "Video model folder is not a standalone Diffusers pipeline because model_index.json is missing: "
            + model_dir
        )

    patch_diffusers_pipeline_aliases(model_dir, diffusers)
    device = select_torch_device(payload, torch)
    dtype = torch.float16 if str(device).startswith("cuda") else torch.float32
    enforce_cuda_memory_guard(
        payload,
        device,
        payload.get("required_memory_bytes") or path_size_bytes(model_dir),
        "video model '" + payload.get("manifest_id", "") + "'"
    )
    kwargs, device_map = diffusers_load_kwargs(payload, torch, device, dtype, True)
    pipe = DiffusionPipeline.from_pretrained(model_dir, **kwargs)
    configure_pipeline_memory_options(pipe, payload, device)
    backend = "diffusers"
    if device_map:
        backend += ":device_map=" + str(device_map)
    pipe = maybe_move_pipeline_to_device(pipe, device, backend)
    verify_cuda_pipeline(pipe, torch, device, payload, "video model '" + payload.get("manifest_id", "") + "'", backend)
    return pipe, device, backend


def frame_to_pil(frame):
    import numpy as np
    from PIL import Image

    if isinstance(frame, Image.Image):
        return frame.convert("RGB")

    arr = np.asarray(frame)
    if arr.ndim == 3 and arr.shape[0] in (1, 3, 4) and arr.shape[-1] not in (1, 3, 4):
        arr = np.transpose(arr, (1, 2, 0))
    if arr.dtype != np.uint8:
        try:
            if float(arr.max()) <= 1.0:
                arr = arr * 255.0
        except Exception:
            pass
        arr = np.clip(arr, 0, 255).astype("uint8")
    if arr.ndim == 2:
        return Image.fromarray(arr).convert("RGB")
    return Image.fromarray(arr[..., :3]).convert("RGB")


def normalize_video_frames(raw_frames):
    import numpy as np

    if hasattr(raw_frames, "frames"):
        raw_frames = raw_frames.frames
    if isinstance(raw_frames, tuple) and len(raw_frames) > 0:
        raw_frames = raw_frames[0]
    if isinstance(raw_frames, list) and len(raw_frames) == 1:
        first = raw_frames[0]
        if isinstance(first, (list, tuple)):
            raw_frames = first
        else:
            first_arr = np.asarray(first)
            if first_arr.ndim == 4:
                raw_frames = first_arr

    if isinstance(raw_frames, np.ndarray):
        arr = raw_frames
        if arr.ndim == 5:
            arr = arr[0]
        if arr.ndim == 4:
            if arr.shape[1] in (1, 3, 4) and arr.shape[-1] not in (1, 3, 4):
                arr = np.transpose(arr, (0, 2, 3, 1))
            return [frame_to_pil(frame) for frame in arr]
        if arr.ndim == 3:
            return [frame_to_pil(arr)]

    frames = []
    for frame in raw_frames or []:
        frame_arr = np.asarray(frame)
        if frame_arr.ndim == 4:
            frames.extend(frame_to_pil(item) for item in frame_arr)
        else:
            frames.append(frame_to_pil(frame))
    return frames


def save_frames(frames, payload):
    fps = max(1, int(payload.get("fps") or 8))
    output_path = payload["output_path"]
    try:
        from diffusers.utils import export_to_video
        exported = export_to_video(frames, output_path, fps=fps)
        if exported:
            output_path = exported
        return output_path, "video/mp4", "diffusers.export_to_video"
    except Exception as mp4_exc:
        if should_try_opencv_repair(mp4_exc):
            try:
                if payload.get("allow_package_install"):
                    install_python_package("opencv-python", no_deps=True)
                output_path = write_mp4_with_cv2(frames, output_path, fps)
                return output_path, "video/mp4", "opencv.VideoWriter"
            except Exception as repaired_exc:
                mp4_exc = RuntimeError(str(mp4_exc) + "; opencv repair failed: " + str(repaired_exc))
        gif_path = payload.get("gif_output_path") or os.path.splitext(output_path)[0] + ".gif"
        duration = max(1, int(round(1000.0 / fps)))
        frames[0].save(gif_path, save_all=True, append_images=frames[1:], duration=duration, loop=0)
        return gif_path, "image/gif", "pil.gif fallback: " + str(mp4_exc)


def should_try_opencv_repair(exc):
    text = str(exc).lower()
    return "opencv" in text or "cv2" in text


def install_python_package(package, no_deps=False, index_url=""):
    import subprocess
    command = [sys.executable, "-m", "pip", "install", "--disable-pip-version-check", package]
    if no_deps:
        command.append("--no-deps")
    if index_url:
        command.extend(["--index-url", index_url])
    completed = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=300)
    if completed.returncode != 0:
        raise RuntimeError((completed.stderr or completed.stdout or "").strip())


def write_mp4_with_cv2(frames, output_path, fps):
    import cv2
    import numpy as np

    if not frames:
        raise RuntimeError("No frames were available for MP4 encoding.")
    first = frames[0].convert("RGB")
    width, height = first.size
    fourcc = cv2.VideoWriter_fourcc(*"mp4v")
    writer = cv2.VideoWriter(output_path, fourcc, float(max(1, fps)), (int(width), int(height)))
    if not writer.isOpened():
        raise RuntimeError("OpenCV VideoWriter could not open MP4 output: " + output_path)
    try:
        for frame in frames:
            rgb = np.asarray(frame.convert("RGB"))
            bgr = cv2.cvtColor(rgb, cv2.COLOR_RGB2BGR)
            writer.write(bgr)
    finally:
        writer.release()
    if not os.path.exists(output_path) or os.path.getsize(output_path) <= 0:
        raise RuntimeError("OpenCV VideoWriter did not produce a non-empty MP4 file.")
    return output_path


def video_generation_chunk_size(payload, total_frames):
    requested = int(payload.get("chunk_frames") or 0)
    if requested > 0:
        return max(1, min(int(total_frames), requested))

    width = int(payload.get("width") or 320)
    height = int(payload.get("height") or 192)
    pixels = max(1, width * height)
    if total_frames <= 12:
        return int(total_frames)
    if pixels >= 512 * 288:
        return 4
    if pixels >= 320 * 192:
        return 8
    return 12


def video_generation_keyframe_count(payload, total_frames):
    requested = int(payload.get("generate_frames") or payload.get("key_frames") or 0)
    if requested > 0:
        return max(1, min(int(total_frames), requested))

    width = int(payload.get("width") or 320)
    height = int(payload.get("height") or 192)
    pixels = max(1, width * height)
    if total_frames <= 16:
        return int(total_frames)
    if pixels >= 512 * 288:
        return min(int(total_frames), 6)
    if pixels >= 320 * 192:
        return min(int(total_frames), 10)
    return min(int(total_frames), 16)


def resample_video_frames(frames, total_frames):
    from PIL import Image

    total_frames = max(1, int(total_frames))
    if not frames:
        return []
    if len(frames) == total_frames:
        return frames
    if len(frames) == 1:
        return [frames[0].copy() for _ in range(total_frames)]

    output = []
    source_last = len(frames) - 1
    output_last = max(1, total_frames - 1)
    for index in range(total_frames):
        position = (float(index) * float(source_last)) / float(output_last)
        lower = int(position)
        upper = min(source_last, lower + 1)
        amount = position - float(lower)
        lower_frame = frames[lower].convert("RGB")
        if amount <= 0.0001 or upper == lower:
            output.append(lower_frame.copy())
        else:
            output.append(Image.blend(lower_frame, frames[upper].convert("RGB"), amount))
    return output


def decode_data_url(data_url):
    text = str(data_url or "").strip()
    if not text:
        return b""
    if "," in text and text.lower().startswith("data:"):
        text = text.split(",", 1)[1]
    return base64.b64decode(text)


def source_path_for_kind(payload, kind):
    kind = str(kind or "").strip().lower()
    source_kind = str(payload.get("source_kind") or "").strip().lower()
    source_path = str(payload.get("source_path") or "").strip()
    if kind and source_kind == kind and source_path:
        return source_path
    return str(payload.get("source_" + kind + "_path") or "").strip()


def load_source_image(payload):
    source_path = source_path_for_kind(payload, "image")
    data_url = str(payload.get("source_data_url") or "").strip()
    source_kind = str(payload.get("source_kind") or "").strip().lower()
    media_type = str(payload.get("source_media_type") or "").strip().lower()
    if source_path:
        if not os.path.exists(source_path):
            raise RuntimeError("Source image file was not found: " + source_path)
        from PIL import Image
        return Image.open(source_path).convert("RGB")
    if data_url and (source_kind == "image" or data_url.lower().startswith("data:image/") or media_type.startswith("image/")):
        from PIL import Image
        return Image.open(io.BytesIO(decode_data_url(data_url))).convert("RGB")
    return None


def source_video_reference(payload):
    source_path = source_path_for_kind(payload, "video")
    if source_path:
        if not os.path.exists(source_path):
            raise RuntimeError("Source video file was not found: " + source_path)
        return source_path
    data_url = str(payload.get("source_data_url") or "").strip()
    source_kind = str(payload.get("source_kind") or "").strip().lower()
    media_type = str(payload.get("source_media_type") or "").strip().lower()
    if data_url and (source_kind == "video" or data_url.lower().startswith("data:video/") or media_type.startswith("video/")):
        return data_url
    return ""


def filter_pipeline_kwargs(pipe, kwargs):
    try:
        import inspect
        signature = inspect.signature(pipe.__call__)
        for parameter in signature.parameters.values():
            if parameter.kind == parameter.VAR_KEYWORD:
                return kwargs
        return {key: value for key, value in kwargs.items() if key in signature.parameters}
    except Exception:
        return kwargs


def build_video_kwargs(payload, torch, device, frames, seed_offset, pipe=None):
    kwargs = {
        "prompt": payload.get("prompt") or "",
        "width": int(payload.get("width") or 320),
        "height": int(payload.get("height") or 192),
        "num_frames": int(frames),
        "num_inference_steps": int(payload.get("steps") or 8),
        "guidance_scale": float(payload.get("guidance_scale") or 7.5),
    }
    negative = payload.get("negative_prompt") or ""
    if negative.strip():
        kwargs["negative_prompt"] = negative
    source_image = load_source_image(payload)
    if source_image is not None:
        kwargs["image"] = source_image
    source_video = source_video_reference(payload)
    if source_video:
        kwargs["video"] = source_video
    seed = payload.get("seed")
    if seed is not None:
        kwargs["generator"] = torch.Generator(device=device).manual_seed(int(seed) + int(seed_offset))
    if pipe is not None:
        requested_source = source_image is not None or bool(source_video)
        kwargs = filter_pipeline_kwargs(pipe, kwargs)
        if requested_source and "image" not in kwargs and "video" not in kwargs:
            raise RuntimeError("The selected video pipeline does not accept source image/video input.")
    return kwargs


def run_diffusers_video(payload):
    try:
        import torch
    except Exception as exc:
        raise RuntimeError("Diffusers/Torch video pipeline is unavailable: " + str(exc))

    pipe, device, backend_detail = load_pipeline(payload, torch)
    total_frames = max(1, int(payload.get("frames") or 16))
    generation_frames = video_generation_keyframe_count(payload, total_frames)
    chunk_size = video_generation_chunk_size(payload, generation_frames)
    pil_frames = []
    for frame_offset in range(0, generation_frames, chunk_size):
        current_frames = min(chunk_size, generation_frames - frame_offset)
        kwargs = build_video_kwargs(payload, torch, device, current_frames, frame_offset, pipe)
        result = pipe(**kwargs)
        chunk_frames = normalize_video_frames(result)
        if not chunk_frames:
            raise RuntimeError("Video pipeline returned no frames for chunk starting at frame " + str(frame_offset) + ".")
        pil_frames.extend(chunk_frames[:current_frames])
        try:
            if str(device).startswith("cuda"):
                torch.cuda.empty_cache()
        except Exception:
            pass
    if not pil_frames:
        raise RuntimeError("Video pipeline returned no frames.")
    encoded_frames = resample_video_frames(pil_frames, total_frames)
    output_path, media_type, encoder = save_frames(encoded_frames, payload)
    try:
        if str(device).startswith("cuda"):
            torch.cuda.empty_cache()
    except Exception:
        pass
    return {
        "output_path": output_path,
        "media_type": media_type,
        "backend": "python.diffusers:" + device + ":" + backend_detail + ":" + encoder + ":chunks=" + str(chunk_size) + ":source_frames=" + str(len(pil_frames)) + ":out_frames=" + str(len(encoded_frames)),
        "frame_count": len(encoded_frames),
        "source_frame_count": len(pil_frames),
    }


def main():
    request_path = sys.argv[1]
    result_path = sys.argv[2]
    try:
        with open(request_path, "r", encoding="utf-8-sig") as handle:
            payload = json.load(handle)

        ensure_runtime_dependencies(payload)
        os.makedirs(os.path.dirname(payload["output_path"]), exist_ok=True)
        output = run_diffusers_video(payload)
        media_type = output.get("media_type") or "video/mp4"
        message = "Video generated successfully."
        if media_type == "image/gif":
            message = "Video frames generated successfully as an animated GIF because MP4 encoding is unavailable."

        write_result(result_path, {
            "success": True,
            "message": message,
            "backend": output.get("backend") or "python.diffusers",
            "media_type": media_type,
            "output_path": output["output_path"],
            "frame_count": output.get("frame_count") or 0,
            "source_frame_count": output.get("source_frame_count") or 0,
        })
    except Exception as exc:
        write_result(result_path, {
            "success": False,
            "message": str(exc),
            "detail": traceback.format_exc(limit=8),
        })
        sys.exit(2)


if __name__ == "__main__":
    main()
""";
}
