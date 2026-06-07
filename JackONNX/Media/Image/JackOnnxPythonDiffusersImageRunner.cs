using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using LlmRuntime;

namespace JackONNX.Image;

public sealed class JackOnnxPythonDiffusersImageRunner : IJackOnnxImageModelRunner
{
    private static readonly SemaphoreSlim PythonProvisioningGate = new(1, 1);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private const string BundledPythonBaseUrl = "https://www.python.org/ftp/python";
    private const string BundledPythonVersion = "3.12.8";
    private const string CudaLegacyPythonVersion = "3.11.9";
    private const string CudaLegacyTorchIndexUrl = "https://download.pytorch.org/whl/cu118";
    private const string MinimumSchedulerDiffusersVersion = "0.31.0";
    private const string MaximumSchedulerDiffusersVersionExclusive = "0.32.0";
    private const string MinimumQwenDiffusersVersion = "0.35.0";
    private const string MinimumTorchVersion = "2.1.0";
    private const string MinimumTransformersVersion = "4.30.0";
    private const string MinimumPeftVersion = "0.10.0";
    private const string LegacyMinimumHuggingFaceHubVersion = "0.34.0";
    private const string LegacyMaximumHuggingFaceHubVersionExclusive = "1.0.0";
    private const string ModernMinimumHuggingFaceHubVersion = "1.5.0";
    private const string ModernMaximumHuggingFaceHubVersionExclusive = "2.0.0";
    private const string GetPipUrl = "https://bootstrap.pypa.io/get-pip.py";
    private const string DefaultTorchCudaIndexUrl = "https://download.pytorch.org/whl/cu128";
    private const string TorchCudaIndexEnvironmentVariable = "JACKONNX_TORCH_CUDA_INDEX_URL";
    private const string AutoCudaTorchEnvironmentVariable = "JACKONNX_AUTO_CUDA_TORCH";
    private static string BundledPythonInstallerFile =>
        "python-" + BundledPythonVersion + (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "-amd64" : "") + ".exe";
    private static string BundledPythonEmbedFile =>
        "python-" + BundledPythonVersion + "-embed-" + GetPythonArchiveArchitecture() + ".zip";
    private static string CudaLegacyPythonInstallerFile =>
        "python-" + CudaLegacyPythonVersion + (RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "-amd64" : "") + ".exe";
    private static string ApplicationBaseDirectory => Path.GetFullPath(AppContext.BaseDirectory);
    private static string BundledPythonDirectory => Path.Combine(ApplicationBaseDirectory, "Python");
    private static string BundledPythonExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(BundledPythonDirectory, "python.exe")
        : Path.Combine(BundledPythonDirectory, "bin", "python3");
    private static string CudaLegacyPythonDirectory => Path.Combine(ApplicationBaseDirectory, "PythonCudaLegacy");
    private static string CudaLegacyPythonExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(CudaLegacyPythonDirectory, "python.exe")
        : Path.Combine(CudaLegacyPythonDirectory, "bin", "python3");
    private static string BundledPythonModelsDirectory => Path.Combine(BundledPythonDirectory, "ImageModels");

    public static string DefaultBundledPythonDirectory => BundledPythonDirectory;
    public static string DefaultBundledPythonExecutable => BundledPythonExecutable;
    public static string DefaultCudaLegacyPythonDirectory => CudaLegacyPythonDirectory;
    public static string DefaultCudaLegacyPythonExecutable => CudaLegacyPythonExecutable;
    public static string DefaultPreferredImageGenerationPythonExecutable =>
        File.Exists(CudaLegacyPythonExecutable) ? CudaLegacyPythonExecutable : BundledPythonExecutable;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<JackOnnxImageModelOutput> GenerateAsync(
        JackOnnxModelManifest manifest,
        ImageGenerationRequest request,
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
        string tempRoot = Path.Combine(Path.GetTempPath(), "jackonnx-image-runner", jobId);
        Directory.CreateDirectory(tempRoot);
        string requestPath = Path.Combine(tempRoot, "request.json");
        string resultPath = Path.Combine(tempRoot, "result.json");
        string scriptPath = Path.Combine(tempRoot, "runner.py");
        string outputPath = Path.Combine(tempRoot, "image.png");

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
                cancellationToken).ConfigureAwait(false);
            if (!File.Exists(resultPath))
                throw new InvalidOperationException("Python image runner did not write a result file. " + BuildProcessDetail(run));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath, cancellationToken).ConfigureAwait(false));
            JsonElement root = document.RootElement;
            bool success = ReadBool(root, "success");
            string message = ReadString(root, "message");
            string detail = ReadString(root, "detail");
            string backend = ReadString(root, "backend");
            string imagePath = ReadString(root, "output_path");
            if (string.IsNullOrWhiteSpace(imagePath))
                imagePath = outputPath;

            if (!success)
            {
                string failure = FirstNonEmpty(message, "Python image runner failed.");
                if (!string.IsNullOrWhiteSpace(detail))
                    failure += " " + detail.Trim();
                if (IsPythonPipelineDependencyFailure(failure))
                    failure += " Install local Diffusers/Torch packages for PyTorch layouts or Optimum ONNX Runtime packages for ONNX diffusion exports.";
                throw new InvalidOperationException(failure);
            }

            if (run.ExitCode != 0)
                throw new InvalidOperationException("Python image runner exited with code " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + BuildProcessDetail(run));

            if (!File.Exists(imagePath))
                throw new InvalidOperationException("Python image runner reported success, but the image file was not found: " + imagePath);

            byte[] data = await File.ReadAllBytesAsync(imagePath, cancellationToken).ConfigureAwait(false);
            return new JackOnnxImageModelOutput
            {
                Data = data,
                FileName = "image_" + jobId + ".png",
                MediaType = "image/png",
                Message = FirstNonEmpty(message, "Image generated successfully."),
                Runner = FirstNonEmpty(backend, "python.diffusers"),
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["backend"] = FirstNonEmpty(backend, "python.diffusers"),
                    ["manifestPath"] = manifest.ManifestPath
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
        ImageGenerationRequest request,
        JackOnnxOptions options,
        string modelDirectory,
        string outputPath)
    {
        string manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? Environment.CurrentDirectory;
        return new Dictionary<string, object?>
        {
            ["manifest_id"] = manifest.Id,
            ["manifest_name"] = manifest.Name,
            ["manifest_path"] = manifest.ManifestPath,
            ["model_dir"] = modelDirectory,
            ["source_model_dir"] = manifestDirectory,
            ["format"] = manifest.Format,
            ["required_memory_bytes"] = ResolveRequiredMemoryBytes(manifest, modelDirectory),
            ["prompt"] = request.Prompt,
            ["negative_prompt"] = request.NegativePrompt,
            ["width"] = request.Width,
            ["height"] = request.Height,
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
            ["base_model"] = ReadManifestMetadata(manifest, "base_model", "baseModel"),
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
            ["memory_saving"] = ReadContextMetadata(request.Context, "memory_saving", "memorySaving", "video_memory_saving", "videoMemorySaving"),
            ["output_path"] = outputPath
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

    private static string ReadManifestMetadata(JackOnnxModelManifest manifest, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (manifest.Metadata.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static async Task<ProcessRunResult> RunFirstAvailablePythonAsync(
        JackOnnxModelManifest manifest,
        ImageGenerationRequest request,
        JackOnnxOptions options,
        string scriptPath,
        string requestPath,
        string resultPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var startErrors = new List<string>();
        bool foundPythonRuntime = false;
        bool requiresBundledQwenFallback = false;

        foreach (var command in ResolvePythonCommands(options))
        {
            try
            {
                try
                {
                    if (options.AllowPythonPackageInstall)
                        await TryPreparePreferredImageRuntimeAsync(manifest, command, options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    startErrors.Add("Could not prepare image generation runtime in " + command.FileName + ": " + ex.Message);
                }

                var run = await RunPythonWithManifestPayloadAsync(
                    manifest,
                    request,
                    options,
                    command,
                    scriptPath,
                    requestPath,
                    resultPath,
                    outputPath,
                    options.ImageGenerationTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (PythonRunnerSucceeded(resultPath, run))
                    return run;

                foundPythonRuntime = foundPythonRuntime || ProvidesPythonInterpreter(command, run);
                string detail = BuildPythonRunFailureDetail(run);
                TryDeleteFile(resultPath);
                if (IsTerminalCudaGenerationFailure(detail))
                    throw CreateTerminalCudaGenerationFailure(command, "image", detail);

                bool shouldRetry = false;

                if (IsTorchDependencyFailure(detail))
                {
                    if (!options.AllowPythonPackageInstall)
                    {
                        startErrors.Add("Python package install is disabled; not repairing torch/transformers in " + command.FileName + ".");
                    }
                    else try
                    {
                        await EnsureTorchRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
                        shouldRetry = true;
                    }
                    catch (Exception ex)
                    {
                        startErrors.Add("Could not auto-install torch/transformers in " + command.FileName + ": " + ex.Message);
                    }
                }
                else if (IsDiffusersDependencyFailure(detail) || IsDiffusersVersionCompatibilityFailure(detail))
                {
                    if (!options.AllowPythonPackageInstall)
                    {
                        startErrors.Add("Python package install is disabled; not repairing diffusers in " + command.FileName + ".");
                    }
                    else try
                    {
                        await EnsureDiffusersRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
                        shouldRetry = true;
                    }
                    catch (Exception ex)
                    {
                        startErrors.Add("Could not auto-install/update diffusers in " + command.FileName + ": " + ex.Message);
                    }
                }
                else if (IsPeftDependencyFailure(detail))
                {
                    if (!options.AllowPythonPackageInstall)
                    {
                        startErrors.Add("Python package install is disabled; not repairing PEFT in " + command.FileName + ".");
                    }
                    else try
                    {
                        await EnsurePeftRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
                        shouldRetry = true;
                    }
                    catch (Exception ex)
                    {
                        startErrors.Add("Could not auto-install/update PEFT in " + command.FileName + ": " + ex.Message);
                    }
                }

                if (!shouldRetry && IsQwenPipelineCompatibilityFailure(detail))
                {
                    if (!options.AllowPythonPackageInstall)
                    {
                        startErrors.Add("Python package install is disabled; not upgrading Qwen diffusers support in " + command.FileName + ".");
                    }
                    else try
                    {
                        await EnsureQwenRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
                        shouldRetry = true;
                    }
                    catch (Exception ex)
                    {
                        startErrors.Add("Could not auto-upgrade Qwen diffusers support in " + command.FileName + ": " + ex.Message);
                    }
                }

                if (shouldRetry)
                {
                    var repairedRun = await RunPythonWithManifestPayloadAsync(
                        manifest,
                        request,
                        options,
                        command,
                        scriptPath,
                        requestPath,
                        resultPath,
                        outputPath,
                        options.ImageGenerationTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (PythonRunnerSucceeded(resultPath, repairedRun))
                        return repairedRun;

                    detail = BuildPythonRunFailureDetail(repairedRun);
                    TryDeleteFile(resultPath);
                    if (IsTerminalCudaGenerationFailure(detail))
                        throw CreateTerminalCudaGenerationFailure(command, "image", detail);

                    if (IsQwenPipelineCompatibilityFailure(detail))
                        requiresBundledQwenFallback = true;

                    if (requiresBundledQwenFallback)
                        startErrors.Add(command.FileName + " after dependency repair exited with code " +
                            repairedRun.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + detail);
                    else
                        startErrors.Add(command.FileName + " after dependency repair exited with code " +
                            repairedRun.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + detail);
                    continue;
                }

                requiresBundledQwenFallback = requiresBundledQwenFallback || IsQwenPipelineCompatibilityFailure(detail);
                startErrors.Add(command.FileName + " exited with code " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + detail);
            }
            catch (Win32Exception ex)
            {
                startErrors.Add(command.FileName + ": " + ex.Message);
            }
        }

        if ((!foundPythonRuntime || requiresBundledQwenFallback) && options.AllowPythonBootstrap)
        {
            try
            {
                PythonCommand? bundledPython = await ResolveBundledPythonAsyncInternal(cancellationToken).ConfigureAwait(false);
                if (bundledPython != null)
                {
                    if (File.Exists(resultPath))
                        File.Delete(resultPath);

                    if (requiresBundledQwenFallback)
                        await EnsureQwenRuntimeAsync(bundledPython, cancellationToken).ConfigureAwait(false);

                    var run = await RunPythonWithManifestPayloadAsync(
                        manifest,
                        request,
                        options,
                        bundledPython,
                        scriptPath,
                        requestPath,
                        resultPath,
                        outputPath,
                        options.ImageGenerationTimeout,
                        cancellationToken).ConfigureAwait(false);
                    if (PythonRunnerSucceeded(resultPath, run))
                        return run;

                    foundPythonRuntime = true;
                    string detail = BuildPythonRunFailureDetail(run);
                    TryDeleteFile(resultPath);
                    if (IsTerminalCudaGenerationFailure(detail))
                        throw CreateTerminalCudaGenerationFailure(bundledPython, "image", detail);

                    if (IsTorchDependencyFailure(detail))
                    {
                        await EnsureTorchRuntimeAsync(bundledPython, cancellationToken).ConfigureAwait(false);
                        if (File.Exists(resultPath))
                            File.Delete(resultPath);

                        var repairedRun = await RunPythonWithManifestPayloadAsync(
                            manifest,
                            request,
                            options,
                            bundledPython,
                            scriptPath,
                            requestPath,
                            resultPath,
                            outputPath,
                            options.ImageGenerationTimeout,
                            cancellationToken).ConfigureAwait(false);
                        if (PythonRunnerSucceeded(resultPath, repairedRun))
                            return repairedRun;

                        detail = BuildPythonRunFailureDetail(repairedRun);
                        TryDeleteFile(resultPath);
                        if (IsTerminalCudaGenerationFailure(detail))
                            throw CreateTerminalCudaGenerationFailure(bundledPython, "image", detail);
                    }
                    else if (IsDiffusersDependencyFailure(detail) || IsDiffusersVersionCompatibilityFailure(detail))
                    {
                        await EnsureDiffusersRuntimeAsync(bundledPython, cancellationToken).ConfigureAwait(false);
                        if (File.Exists(resultPath))
                            File.Delete(resultPath);

                        var repairedRun = await RunPythonWithManifestPayloadAsync(
                            manifest,
                            request,
                            options,
                            bundledPython,
                            scriptPath,
                            requestPath,
                            resultPath,
                            outputPath,
                            options.ImageGenerationTimeout,
                            cancellationToken).ConfigureAwait(false);
                        if (PythonRunnerSucceeded(resultPath, repairedRun))
                            return repairedRun;

                        detail = BuildPythonRunFailureDetail(repairedRun);
                        TryDeleteFile(resultPath);
                        if (IsTerminalCudaGenerationFailure(detail))
                            throw CreateTerminalCudaGenerationFailure(bundledPython, "image", detail);
                    }
                    else if (IsPeftDependencyFailure(detail))
                    {
                        await EnsurePeftRuntimeAsync(bundledPython, cancellationToken).ConfigureAwait(false);
                        if (File.Exists(resultPath))
                            File.Delete(resultPath);

                        var repairedRun = await RunPythonWithManifestPayloadAsync(
                            manifest,
                            request,
                            options,
                            bundledPython,
                            scriptPath,
                            requestPath,
                            resultPath,
                            outputPath,
                            options.ImageGenerationTimeout,
                            cancellationToken).ConfigureAwait(false);
                        if (PythonRunnerSucceeded(resultPath, repairedRun))
                            return repairedRun;

                        detail = BuildPythonRunFailureDetail(repairedRun);
                        TryDeleteFile(resultPath);
                        if (IsTerminalCudaGenerationFailure(detail))
                            throw CreateTerminalCudaGenerationFailure(bundledPython, "image", detail);
                    }

                    startErrors.Add("Bundled python at " + bundledPython.FileName + " exited with code " + run.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + detail);
                }
            }
            catch (Exception ex)
            {
                startErrors.Add("Bundled python bootstrap failed: " + ex.Message);
            }
        }

        string finalDetail = string.Join(" ", startErrors.Where(error => !string.IsNullOrWhiteSpace(error)));
        if (foundPythonRuntime)
        {
            throw new InvalidOperationException(
                "No compatible Python image generation runtime completed the request. Python was found, but the Diffusers/Torch pipeline failed. " +
                "Set JackOnnxOptions.ImageGenerationPythonExecutable or JACKONNX_PYTHON to force a different runtime, or repair the bundled Python runtime. " +
                (requiresBundledQwenFallback ? "This image request requires Qwen-Image diffusers support (diffusers 0.35.0+). " : "") +
                finalDetail);
        }

        throw new InvalidOperationException(
            "No usable Python executable was found for local image generation. On Windows this runner will auto-install a local Python to " +
            BundledPythonDirectory + "; on Linux it will use an existing python3 or create a local venv under that directory when python3-venv is available. " +
            "Set JackOnnxOptions.ImageGenerationPythonExecutable or JACKONNX_PYTHON to force your own path. " +
            (requiresBundledQwenFallback ? "This image request requires Qwen-Image diffusers support (diffusers 0.35.0+), so this runner also attempted to auto-upgrade diffusers before switching to bundled Python. " : "") +
            finalDetail);
    }

    private static IReadOnlyList<PythonCommand> ResolvePythonCommands(JackOnnxOptions options)
    {
        var commands = new List<PythonCommand>();
        string? explicitImagePython = Environment.GetEnvironmentVariable("JACKONNX_IMAGE_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitImagePython))
        {
            AddPythonCommand(commands, explicitImagePython, requireExistingFile: true);
            return commands;
        }

        string? explicitPython = Environment.GetEnvironmentVariable("JACKONNX_PYTHON");
        if (!string.IsNullOrWhiteSpace(explicitPython))
        {
            AddPythonCommand(commands, explicitPython, requireExistingFile: true);
            return commands;
        }

        if (!string.IsNullOrWhiteSpace(options.ImageGenerationPythonExecutable))
            AddPythonCommand(commands, options.ImageGenerationPythonExecutable.Trim(), requireExistingFile: !IsCommandName(options.ImageGenerationPythonExecutable));

        AddPythonCommand(commands, CudaLegacyPythonExecutable, requireExistingFile: true);
        AddPythonCommand(commands, BundledPythonExecutable, requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "python.exe"), requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "bin", "python3"), requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "Python", "bin", "python"), requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, ".venv", "bin", "python"), requireExistingFile: true);
        AddPythonCommand(commands, Path.Combine(Environment.CurrentDirectory, "venv", "bin", "python"), requireExistingFile: true);
        foreach (PythonCommand command in ResolveInstalledPythonCommands())
            AddPythonCommand(commands, command.FileName, command.Arguments, requireExistingFile: true);

        AddPythonCommand(commands, "python", requireExistingFile: false);
        AddPythonCommand(commands, "python3", requireExistingFile: false);
        if (OperatingSystem.IsWindows())
            AddPythonCommand(commands, "py", ["-3"], requireExistingFile: false);

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

    private static bool ProvidesPythonInterpreter(PythonCommand command, ProcessRunResult run)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        if (string.Equals(command.FileName, "py", StringComparison.OrdinalIgnoreCase))
            return !IsPyLauncherMissingInterpreter(run);

        return true;
    }

    private static bool IsPyLauncherMissingInterpreter(ProcessRunResult run)
    {
        string text = BuildProcessDetail(run);
        return run.ExitCode == 103 && text.Contains("No Installed Pythons Found", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessRunResult> RunPythonWithManifestPayloadAsync(
        JackOnnxModelManifest manifest,
        ImageGenerationRequest request,
        JackOnnxOptions options,
        PythonCommand command,
        string scriptPath,
        string requestPath,
        string resultPath,
        string outputPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (manifest == null)
            throw new ArgumentNullException(nameof(manifest));
        if (request == null)
            throw new ArgumentNullException(nameof(request));

        string modelDirectory = ResolveModelDirectory(manifest, command);
        var payload = BuildPayload(manifest, request, options, modelDirectory, outputPath);
        await File.WriteAllTextAsync(
            requestPath,
            JsonSerializer.Serialize(payload, JsonOptions),
            Utf8NoBom,
            cancellationToken).ConfigureAwait(false);

        return await RunPythonAsync(
            command,
            scriptPath,
            requestPath,
            resultPath,
            timeout,
            cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveModelDirectory(JackOnnxModelManifest manifest, PythonCommand command)
    {
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? Environment.CurrentDirectory;
        if (!IsBundledPythonCommand(command))
            return sourceDirectory;

        try
        {
            return StageModelDirectoryInBundledPython(sourceDirectory, manifest);
        }
        catch
        {
            return sourceDirectory;
        }
    }

    private static bool IsBundledPythonCommand(PythonCommand command)
    {
        string commandPath = command?.FileName ?? "";
        if (string.IsNullOrWhiteSpace(commandPath))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(commandPath),
                Path.GetFullPath(BundledPythonExecutable),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string StageModelDirectoryInBundledPython(string sourceDirectory, JackOnnxModelManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            return sourceDirectory;

        string destinationDirectory = Path.Combine(
            BundledPythonModelsDirectory,
            BuildBundledModelDirectoryName(manifest, sourceDirectory));
        if (Directory.Exists(destinationDirectory))
            return destinationDirectory;

        Directory.CreateDirectory(BundledPythonModelsDirectory);
        CopyDirectoryRecursively(sourceDirectory, destinationDirectory);
        TryWriteText(Path.Combine(destinationDirectory, "source_path.txt"), sourceDirectory);
        return destinationDirectory;
    }

    private static string BuildBundledModelDirectoryName(JackOnnxModelManifest manifest, string sourceDirectory)
    {
        string manifestId = string.IsNullOrWhiteSpace(manifest.Id) ? Path.GetFileName(sourceDirectory) : manifest.Id;
        string safeId = new string(manifestId
            .Select(character =>
                char.IsWhiteSpace(character) || Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray()).Trim('.');
        if (string.IsNullOrWhiteSpace(safeId))
            safeId = "image_model";

        return safeId + "-" + BuildShortPathHash(sourceDirectory);
    }

    private static string BuildShortPathHash(string value)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(value ?? "");
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private static void CopyDirectoryRecursively(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, file);
            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? destinationDirectory);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void TryWriteText(string path, string text)
    {
        try
        {
            File.WriteAllText(path, text, Utf8NoBom);
        }
        catch
        {
        }
    }

    public static async Task<string> EnsureBundledPythonAsync(
        CancellationToken cancellationToken = default,
        IProgress<string>? status = null)
    {
        if (!OperatingSystem.IsWindows())
            return "";

        PythonCommand? command = await ResolveBundledPythonAsyncInternal(cancellationToken, status).ConfigureAwait(false);
        if (command != null)
        {
            status?.Report("Bundled Python executable ready at " + command.FileName + ".");
            await EnsureDiffusersAtLeastAsync(command, cancellationToken, status).ConfigureAwait(false);
        }
        return command?.FileName ?? "";
    }

    public static async Task EnsureQwenDiffusersSupportAsync(
        string pythonPath,
        CancellationToken cancellationToken = default,
        IProgress<string>? status = null)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(pythonPath))
            return;

        status?.Report("Checking diffusers runtime for Qwen pipeline support...");
        await EnsureQwenRuntimeAsync(new PythonCommand(pythonPath, []), cancellationToken, status).ConfigureAwait(false);
        status?.Report("Qwen pipeline dependency check is complete.");
    }

    public static async Task<bool> PythonExecutableLooksValidAsync(string pythonPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
            return false;

        try
        {
            var run = await RunPythonArgumentsAsync(new PythonCommand(pythonPath, []), ["-V"], TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
            return run.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsQwenPipelineCompatibilityFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        return detail.Contains("QwenImagePipeline", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("0.35.0", StringComparison.OrdinalIgnoreCase) && detail.Contains("diffusers", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("requires diffusers", StringComparison.OrdinalIgnoreCase) && detail.Contains("qwen", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTorchDependencyFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        return IsMissingPythonModule(detail, "torch") ||
            IsMissingPythonModule(detail, "transformers") ||
            detail.Contains("CUDA GPU is available, but this Python has a CPU-only PyTorch build", StringComparison.OrdinalIgnoreCase) ||
            (detail.Contains("Diffusers/Torch pipeline is unavailable", StringComparison.OrdinalIgnoreCase) &&
             IsMissingPythonModule(detail, "torch"));
    }

    private static bool IsDiffusersDependencyFailure(string detail)
    {
        return IsMissingPythonModule(detail, "diffusers");
    }

    private static bool IsDiffusersVersionCompatibilityFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        return detail.Contains("EDMDPMSolverMultistepScheduler", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("module diffusers has no attribute", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("diffusers", StringComparison.OrdinalIgnoreCase) &&
               detail.Contains("has no attribute", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPeftDependencyFailure(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return false;

        return IsMissingPythonModule(detail, "peft") ||
               detail.Contains("PEFT backend is required", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("peft backend", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMissingPythonModule(string detail, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(detail) || string.IsNullOrWhiteSpace(moduleName))
            return false;

        string[] patterns = new[]
        {
            "No module named '" + moduleName + "'",
            "No module named \"" + moduleName + "\"",
            "No module named " + moduleName
        };
        return patterns.Any(pattern =>
            detail.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task EnsureTorchRuntimeAsync(PythonCommand command, CancellationToken cancellationToken, IProgress<string>? status = null)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        if (!await TryEnsureCudaTorchRuntimeAsync(command, cancellationToken, status).ConfigureAwait(false))
            await EnsurePythonPackageAtLeastAsync("torch", MinimumTorchVersion, command, cancellationToken, status).ConfigureAwait(false);

        await EnsurePythonPackageAtLeastAsync("transformers", MinimumTransformersVersion, command, cancellationToken, status).ConfigureAwait(false);
    }

    private static async Task TryPreparePreferredImageRuntimeAsync(
        JackOnnxModelManifest manifest,
        PythonCommand command,
        JackOnnxOptions options,
        CancellationToken cancellationToken)
    {
        if (ShouldPrepareQwenDiffusersRuntime(manifest))
        {
            await EnsureQwenRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (ShouldPrepareRecentDiffusersRuntime(manifest))
            await EnsureDiffusersRuntimeAsync(command, cancellationToken).ConfigureAwait(false);

        if (!ShouldAutoPrepareCudaTorch(manifest, command, options))
            return;

        await TryEnsureCudaTorchRuntimeAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static bool ShouldPrepareQwenDiffusersRuntime(JackOnnxModelManifest manifest)
    {
        if (manifest == null || !IsDiffusersFormat(manifest.Format))
            return false;

        string text = string.Join(" ",
            manifest.Id,
            manifest.Name,
            manifest.Type,
            manifest.Format,
            manifest.ManifestPath,
            string.Join(" ", manifest.Components.Values),
            string.Join(" ", manifest.Metadata.Select(pair => pair.Key + " " + pair.Value)));

        return text.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("QwenImagePipeline", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldPrepareRecentDiffusersRuntime(JackOnnxModelManifest manifest)
    {
        if (manifest == null || !IsDiffusersFormat(manifest.Format))
            return false;

        try
        {
            string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest.ManifestPath)) ?? "";
            if (string.IsNullOrWhiteSpace(sourceDirectory))
                return false;

            foreach (string path in new[]
            {
                Path.Combine(sourceDirectory, "model_index.json"),
                Path.Combine(sourceDirectory, "scheduler", "scheduler_config.json")
            })
            {
                if (!File.Exists(path))
                    continue;

                string text = File.ReadAllText(path);
                if (text.Contains("EDMDPMSolverMultistepScheduler", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool ShouldAutoPrepareCudaTorch(JackOnnxModelManifest manifest, PythonCommand command, JackOnnxOptions options)
    {
        string preference = Environment.GetEnvironmentVariable(AutoCudaTorchEnvironmentVariable) ?? "";
        if (preference.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            preference.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            preference.Equals("off", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!IsDiffusersFormat(manifest.Format))
            return false;

        bool forceAnyPython = preference.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            preference.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            preference.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            preference.Equals("force", StringComparison.OrdinalIgnoreCase);

        return (forceAnyPython || IsBundledPythonCommand(command) || ShouldPreferCudaTorch(options)) && HasNvidiaCudaDriver();
    }

    private static bool ShouldPreferCudaTorch(JackOnnxOptions options)
    {
        options ??= new JackOnnxOptions();
        return options.DevicePolicy != JackOnnxDevicePolicy.CpuOnly &&
               options.PreferredProvider is JackOnnxExecutionProvider.Auto or JackOnnxExecutionProvider.Cuda;
    }

    private static bool IsDiffusersFormat(string? format) =>
        string.Equals(format, "pytorch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "torch", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(format, "diffusers", StringComparison.OrdinalIgnoreCase);

    private static async Task<bool> TryEnsureCudaTorchRuntimeAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (!HasNvidiaCudaDriver())
            return false;

        status?.Report("Checking CUDA PyTorch compatibility in " + command.FileName + "...");
        var compatibility = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions()).GetStatus(command.FileName, cancellationToken);
        if (compatibility.IsGpuGenerationEnabled)
            return true;

        throw new InvalidOperationException(BuildGpuGenerationDisabledDetail(compatibility));
    }

    private static string BuildGpuGenerationDisabledDetail(LlmRuntimeCompatibilityStatus compatibility)
    {
        string message = compatibility?.Message ?? LlmRuntimeCompatibilityService.DefaultPytorchRepairMessage;
        string repair = "";
        LlmPytorchPackageRecommendation? recommendation = compatibility?.Diagnostics?.RecommendedPytorch;
        if (recommendation != null)
            repair = " Recommended repair: install torch " + recommendation.PytorchVersion + " from " + recommendation.IndexUrl + ".";

        return "GPU image generation is disabled because CUDA/PyTorch is not compatible. " +
               message + repair +
               " Open " + LlmRuntimeCompatibilityService.CudaToolkitDownloadUrl +
               " for CUDA setup, or use LlmRuntime POST /api/v1/runtime/compatibility/repair-pytorch to repair PyTorch. CPU fallback is disabled for auto GPU generation.";
    }

    private static async Task<TorchRuntimeStatus> ReadTorchRuntimeStatusAsync(PythonCommand command, CancellationToken cancellationToken)
    {
        const string code = """
import json
try:
    import torch
    print(json.dumps({
        "has_torch": True,
        "version": str(getattr(torch, "__version__", "")),
        "cuda_version": str(getattr(torch.version, "cuda", "") or ""),
        "cuda_available": bool(torch.cuda.is_available()),
    }))
except Exception as exc:
    print(json.dumps({"has_torch": False, "error": str(exc)}))
""";
        var run = await RunPythonArgumentsAsync(command, ["-c", code], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.StandardOutput))
            return new TorchRuntimeStatus(false, "", "", false);

        try
        {
            using var document = JsonDocument.Parse(run.StandardOutput.Trim());
            JsonElement root = document.RootElement;
            return new TorchRuntimeStatus(
                ReadBool(root, "has_torch"),
                ReadString(root, "version"),
                ReadString(root, "cuda_version"),
                ReadBool(root, "cuda_available"));
        }
        catch
        {
            return new TorchRuntimeStatus(false, "", "", false);
        }
    }

    private static async Task EnsurePythonPackageAtLeastAsync(
        string packageName,
        string minimumVersion,
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return;

        status?.Report("Checking existing package '" + packageName + "' in " + command.FileName + "...");
        string? installedVersion = await ReadPythonPackageVersionAsync(command, packageName, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(installedVersion) && HasAtLeastMinimumVersion(installedVersion, minimumVersion))
            return;

        string specifier = packageName;
        if (!string.IsNullOrWhiteSpace(minimumVersion))
            specifier += ">=" + minimumVersion;

        status?.Report("Installing/upgrading Python package '" + specifier + "' in " + command.FileName + "...");
        var pipArgs = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade", specifier };
        var install = await RunPythonArgumentsAsync(command, pipArgs, TimeSpan.FromMinutes(6), cancellationToken).ConfigureAwait(false);
        if (install.ExitCode != 0)
            throw new InvalidOperationException("Failed to install '" + specifier + "' in python at " + command.FileName + ". " + BuildProcessDetail(install));

        installedVersion = await ReadPythonPackageVersionAsync(command, packageName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installedVersion))
            throw new InvalidOperationException("Python at " + command.FileName + " has no readable version for package '" + packageName + "' after install.");

        if (!string.IsNullOrWhiteSpace(minimumVersion) && !HasAtLeastMinimumVersion(installedVersion, minimumVersion))
            throw new InvalidOperationException("Python at " + command.FileName + " still has package '" + packageName + "' at '" + installedVersion + "'. Required at least '" + minimumVersion + "'.");
    }

    private static async Task EnsurePythonPackageInRangeAsync(
        string packageName,
        string minimumVersion,
        string maximumExclusiveVersion,
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return;

        status?.Report("Checking existing package '" + packageName + "' in " + command.FileName + "...");
        string? installedVersion = await ReadPythonPackageVersionAsync(command, packageName, cancellationToken).ConfigureAwait(false);
        bool hasMinimum = string.IsNullOrWhiteSpace(minimumVersion) ||
            !string.IsNullOrWhiteSpace(installedVersion) &&
            HasAtLeastMinimumVersion(installedVersion, minimumVersion);
        bool belowMaximum = string.IsNullOrWhiteSpace(maximumExclusiveVersion) ||
            !string.IsNullOrWhiteSpace(installedVersion) &&
            IsVersionLessThan(installedVersion, maximumExclusiveVersion);
        if (!string.IsNullOrWhiteSpace(installedVersion) && hasMinimum && belowMaximum)
            return;

        string specifier = packageName;
        if (!string.IsNullOrWhiteSpace(minimumVersion))
            specifier += ">=" + minimumVersion;
        if (!string.IsNullOrWhiteSpace(maximumExclusiveVersion))
            specifier += ",<" + maximumExclusiveVersion;

        status?.Report("Installing/upgrading Python package '" + specifier + "' in " + command.FileName + "...");
        var pipArgs = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade", "--force-reinstall", specifier };
        var install = await RunPythonArgumentsAsync(command, pipArgs, TimeSpan.FromMinutes(6), cancellationToken).ConfigureAwait(false);
        if (install.ExitCode != 0)
            throw new InvalidOperationException("Failed to install '" + specifier + "' in python at " + command.FileName + ". " + BuildProcessDetail(install));

        installedVersion = await ReadPythonPackageVersionAsync(command, packageName, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(installedVersion))
            throw new InvalidOperationException("Python at " + command.FileName + " has no readable version for package '" + packageName + "' after install.");

        if (!string.IsNullOrWhiteSpace(minimumVersion) && !HasAtLeastMinimumVersion(installedVersion, minimumVersion))
            throw new InvalidOperationException("Python at " + command.FileName + " still has package '" + packageName + "' at '" + installedVersion + "'. Required at least '" + minimumVersion + "'.");
        if (!string.IsNullOrWhiteSpace(maximumExclusiveVersion) && !IsVersionLessThan(installedVersion, maximumExclusiveVersion))
            throw new InvalidOperationException("Python at " + command.FileName + " still has package '" + packageName + "' at '" + installedVersion + "'. Required below '" + maximumExclusiveVersion + "'.");
    }

    private static async Task EnsureQwenRuntimeAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        await EnsureDiffusersAtLeastAsync(command, cancellationToken, status).ConfigureAwait(false);
        await EnsureTorchRuntimeAsync(command, cancellationToken, status).ConfigureAwait(false);
        await EnsurePeftRuntimeAsync(command, cancellationToken, status).ConfigureAwait(false);
        await EnsureHuggingFaceHubCompatibleAsync(command, cancellationToken, status).ConfigureAwait(false);
    }

    private static async Task EnsureDiffusersRuntimeAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (command == null)
            throw new ArgumentNullException(nameof(command));

        await EnsureDiffusersAtLeastAsync(
            command,
            cancellationToken,
            status,
            MinimumSchedulerDiffusersVersion,
            MaximumSchedulerDiffusersVersionExclusive).ConfigureAwait(false);
    }

    private static async Task EnsurePeftRuntimeAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        await EnsurePythonPackageAtLeastAsync("peft", MinimumPeftVersion, command, cancellationToken, status).ConfigureAwait(false);
    }

    private static async Task EnsureHuggingFaceHubCompatibleAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        var range = await ResolveHuggingFaceHubRangeAsync(command, cancellationToken).ConfigureAwait(false);
        await EnsurePythonPackageInRangeAsync(
            "huggingface-hub",
            range.MinimumVersion,
            range.MaximumExclusiveVersion,
            command,
            cancellationToken,
            status).ConfigureAwait(false);
    }

    private static async Task<(string MinimumVersion, string MaximumExclusiveVersion)> ResolveHuggingFaceHubRangeAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        string plannedDiffusersMinimumVersion = "",
        string plannedDiffusersMaximumExclusiveVersion = "")
    {
        string? transformersVersion = await ReadPythonPackageVersionAsync(command, "transformers", cancellationToken).ConfigureAwait(false);
        string? diffusersVersion = await ReadPythonPackageVersionAsync(command, "diffusers", cancellationToken).ConfigureAwait(false);
        bool plannedUnboundedDiffusersUpgrade = !string.IsNullOrWhiteSpace(plannedDiffusersMinimumVersion) &&
            string.IsNullOrWhiteSpace(plannedDiffusersMaximumExclusiveVersion);
        bool needsModernHub =
            !string.IsNullOrWhiteSpace(transformersVersion) && HasAtLeastMinimumVersion(transformersVersion, "5.0.0") ||
            !string.IsNullOrWhiteSpace(diffusersVersion) && HasAtLeastMinimumVersion(diffusersVersion, "0.38.0") ||
            plannedUnboundedDiffusersUpgrade;

        return needsModernHub
            ? (ModernMinimumHuggingFaceHubVersion, ModernMaximumHuggingFaceHubVersionExclusive)
            : (LegacyMinimumHuggingFaceHubVersion, LegacyMaximumHuggingFaceHubVersionExclusive);
    }

    private static async Task EnsureDiffusersAtLeastAsync(
        PythonCommand command,
        CancellationToken cancellationToken,
        IProgress<string>? status = null,
        string minimumVersion = MinimumQwenDiffusersVersion,
        string maximumExclusiveVersion = "")
    {
        status?.Report("Checking existing diffusers version in " + command.FileName + "...");
        var hubRange = await ResolveHuggingFaceHubRangeAsync(
            command,
            cancellationToken,
            minimumVersion,
            maximumExclusiveVersion).ConfigureAwait(false);
        await EnsurePythonPackageInRangeAsync(
            "huggingface-hub",
            hubRange.MinimumVersion,
            hubRange.MaximumExclusiveVersion,
            command,
            cancellationToken,
            status).ConfigureAwait(false);
        string? installedVersion = await ReadPythonPackageVersionAsync(command, "diffusers", cancellationToken).ConfigureAwait(false);
        bool hasMinimum = !string.IsNullOrWhiteSpace(installedVersion) && HasAtLeastMinimumVersion(installedVersion, minimumVersion);
        bool belowMaximum = string.IsNullOrWhiteSpace(maximumExclusiveVersion) ||
            !string.IsNullOrWhiteSpace(installedVersion) &&
            IsVersionLessThan(installedVersion, maximumExclusiveVersion);
        if (hasMinimum && belowMaximum)
            return;

        string diffusersSpecifier = "diffusers>=" + minimumVersion;
        if (!string.IsNullOrWhiteSpace(maximumExclusiveVersion))
            diffusersSpecifier += ",<" + maximumExclusiveVersion;

        status?.Report(
            "Upgrading Python diffusers package in " + command.FileName + " to " + diffusersSpecifier + "...");
        var pipArgs = new List<string>
        {
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-input",
            "--upgrade",
            "--force-reinstall",
            "--no-cache-dir",
            diffusersSpecifier,
            "numpy<2",
            "huggingface-hub>=" + hubRange.MinimumVersion + ",<" + hubRange.MaximumExclusiveVersion
        };
        var install = await RunPythonArgumentsAsync(command, pipArgs, TimeSpan.FromMinutes(8), cancellationToken).ConfigureAwait(false);
        if (install.ExitCode != 0)
            throw new InvalidOperationException("Failed to update diffusers in python at " + command.FileName + ". " + BuildProcessDetail(install));

        status?.Report("Verified diffusers version in " + command.FileName + ".");
        installedVersion = await ReadPythonPackageVersionAsync(command, "diffusers", cancellationToken).ConfigureAwait(false);
        hasMinimum = !string.IsNullOrWhiteSpace(installedVersion) && HasAtLeastMinimumVersion(installedVersion, minimumVersion);
        belowMaximum = string.IsNullOrWhiteSpace(maximumExclusiveVersion) ||
            !string.IsNullOrWhiteSpace(installedVersion) &&
            IsVersionLessThan(installedVersion, maximumExclusiveVersion);
        if (hasMinimum && belowMaximum)
            return;

        string requiredRange = minimumVersion + " or later";
        if (!string.IsNullOrWhiteSpace(maximumExclusiveVersion))
            requiredRange += " and below " + maximumExclusiveVersion;

        throw new InvalidOperationException(
            "Python at " + command.FileName +
            " could not be updated to diffusers " + requiredRange + ".");
    }

    private static async Task<string?> ReadPythonPackageVersionAsync(PythonCommand command, string packageName, CancellationToken cancellationToken)
    {
        var code = "import importlib.metadata as m, sys; print(m.version('" + packageName.Replace("'", "\\'") + "'))";
        var run = await RunPythonArgumentsAsync(command, ["-c", code], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
            return null;

        string version = (run.StandardOutput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(version))
            return null;

        return version;
    }

    private static bool HasAtLeastMinimumVersion(string installedVersion, string minimumVersion)
    {
        int[] installed = ExtractNumericVersion(installedVersion);
        int[] minimum = ExtractNumericVersion(minimumVersion);
        int length = Math.Max(installed.Length, minimum.Length);
        for (int i = 0; i < length; i++)
        {
            int installedPart = i < installed.Length ? installed[i] : 0;
            int minimumPart = i < minimum.Length ? minimum[i] : 0;
            if (installedPart > minimumPart)
                return true;
            if (installedPart < minimumPart)
                return false;
        }

        return true;
    }

    private static bool IsVersionLessThan(string installedVersion, string maximumExclusiveVersion)
    {
        int[] installed = ExtractNumericVersion(installedVersion);
        int[] maximum = ExtractNumericVersion(maximumExclusiveVersion);
        int length = Math.Max(installed.Length, maximum.Length);
        for (int i = 0; i < length; i++)
        {
            int installedPart = i < installed.Length ? installed[i] : 0;
            int maximumPart = i < maximum.Length ? maximum[i] : 0;
            if (installedPart < maximumPart)
                return true;
            if (installedPart > maximumPart)
                return false;
        }

        return false;
    }

    private static int[] ExtractNumericVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return Array.Empty<int>();

        var matches = Regex.Matches(version, @"\d+");
        if (!matches.Any())
            return Array.Empty<int>();

        var values = new int[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            values[i] = int.Parse(matches[i].Value, System.Globalization.CultureInfo.InvariantCulture);

        return values;
    }

    private static async Task<PythonCommand?> ResolveBundledPythonAsyncInternal(
        CancellationToken cancellationToken,
        IProgress<string>? status = null)
    {
        if (OperatingSystem.IsLinux())
            return await ResolveLinuxBundledPythonAsyncInternal(cancellationToken, status).ConfigureAwait(false);

        if (!OperatingSystem.IsWindows())
            return null;

        if (File.Exists(BundledPythonExecutable))
        {
            status?.Report("Using existing bundled Python at " + BundledPythonExecutable + ".");
            return new PythonCommand(BundledPythonExecutable, []);
        }

        await PythonProvisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(BundledPythonExecutable))
            {
                status?.Report("Using existing bundled Python at " + BundledPythonExecutable + ".");
                return new PythonCommand(BundledPythonExecutable, []);
            }

            Directory.CreateDirectory(BundledPythonDirectory);
            string installerPath = Path.Combine(BundledPythonDirectory, BundledPythonInstallerFile);
            string installerUrl = string.Join("/", BundledPythonBaseUrl, BundledPythonVersion, BundledPythonInstallerFile);
            if (!File.Exists(installerPath))
            {
                status?.Report("Downloading Python " + BundledPythonVersion + " installer...");
                await DownloadFileAsync(installerUrl, installerPath, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                status?.Report("Installing Python from " + installerPath + "...");
                var install = await InstallPythonAsync(installerPath, BundledPythonDirectory, cancellationToken).ConfigureAwait(false);
                if (install.ExitCode != 0)
                    throw new InvalidOperationException("Python bootstrap installer returned exit code " + install.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + BuildProcessDetail(install));
            }
            catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                status?.Report("Python installer bootstrap failed (" + ex.Message + "); extracting embedded Python runtime...");
                return await EnsureEmbeddedPythonZipAsync(cancellationToken, status).ConfigureAwait(false);
            }

            if (File.Exists(BundledPythonExecutable))
            {
                status?.Report("Python bootstrap install completed at " + BundledPythonExecutable + ".");
                return new PythonCommand(BundledPythonExecutable, []);
            }

            status?.Report("Python installer completed but did not create python.exe; extracting embedded Python runtime...");
            return await EnsureEmbeddedPythonZipAsync(cancellationToken, status).ConfigureAwait(false);
        }
        finally
        {
            PythonProvisioningGate.Release();
        }
    }

    public static async Task<string> EnsureCudaLegacyPythonRuntimeAsync(
        CancellationToken cancellationToken = default,
        IProgress<string>? status = null)
    {
        if (!OperatingSystem.IsWindows())
            return "";

        PythonCommand command = await EnsureCudaLegacyPythonAsync(cancellationToken, status).ConfigureAwait(false);
        status?.Report("Installing legacy CUDA PyTorch for Maxwell/Pascal GPUs in " + command.FileName + "...");
        var torch = await RunPythonArgumentsAsync(
            command,
            [
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "--no-input",
                "--upgrade",
                "--force-reinstall",
                "torch==2.1.*",
                "torchvision==0.16.*",
                "torchaudio==2.1.*",
                "--index-url",
                CudaLegacyTorchIndexUrl
            ],
            TimeSpan.FromMinutes(30),
            cancellationToken).ConfigureAwait(false);
        if (torch.ExitCode != 0)
            throw new InvalidOperationException("Legacy CUDA PyTorch install failed. " + BuildProcessDetail(torch));

        status?.Report("Pinning NumPy for legacy CUDA PyTorch in " + command.FileName + "...");
        var numpy = await RunPythonArgumentsAsync(
            command,
            [
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "--no-input",
                "--upgrade",
                "--force-reinstall",
                "numpy<2"
            ],
            TimeSpan.FromMinutes(8),
            cancellationToken).ConfigureAwait(false);
        if (numpy.ExitCode != 0)
            throw new InvalidOperationException("Legacy CUDA NumPy pin failed. " + BuildProcessDetail(numpy));

        LlmRuntimeCompatibilityStatus compatibility = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions())
            .GetStatus(command.FileName, cancellationToken, forceRefresh: true);
        if (!compatibility.IsGpuGenerationEnabled)
            throw new InvalidOperationException("Legacy CUDA PyTorch installed, but GPU image generation is still disabled. " + BuildGpuGenerationDisabledDetail(compatibility));

        await EnsureTorchRuntimeAsync(command, cancellationToken, status).ConfigureAwait(false);
        await EnsureDiffusersAtLeastAsync(command, cancellationToken, status).ConfigureAwait(false);
        status?.Report("Legacy CUDA image-generation Python runtime is ready at " + command.FileName + ".");
        return command.FileName;
    }

    private static async Task<PythonCommand> EnsureCudaLegacyPythonAsync(
        CancellationToken cancellationToken,
        IProgress<string>? status)
    {
        if (File.Exists(CudaLegacyPythonExecutable))
        {
            status?.Report("Using existing legacy CUDA Python at " + CudaLegacyPythonExecutable + ".");
            return new PythonCommand(CudaLegacyPythonExecutable, []);
        }

        await PythonProvisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(CudaLegacyPythonExecutable))
            {
                status?.Report("Using existing legacy CUDA Python at " + CudaLegacyPythonExecutable + ".");
                return new PythonCommand(CudaLegacyPythonExecutable, []);
            }

            Directory.CreateDirectory(CudaLegacyPythonDirectory);
            string installerPath = Path.Combine(CudaLegacyPythonDirectory, CudaLegacyPythonInstallerFile);
            string installerUrl = string.Join("/", BundledPythonBaseUrl, CudaLegacyPythonVersion, CudaLegacyPythonInstallerFile);
            if (!File.Exists(installerPath))
            {
                status?.Report("Downloading Python " + CudaLegacyPythonVersion + " for legacy CUDA PyTorch...");
                await DownloadFileAsync(installerUrl, installerPath, cancellationToken).ConfigureAwait(false);
            }

            status?.Report("Installing legacy CUDA Python to " + CudaLegacyPythonDirectory + "...");
            var install = await InstallPythonAsync(installerPath, CudaLegacyPythonDirectory, cancellationToken).ConfigureAwait(false);
            if (install.ExitCode != 0)
                throw new InvalidOperationException("Legacy CUDA Python installer returned exit code " + install.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + BuildProcessDetail(install));

            if (!File.Exists(CudaLegacyPythonExecutable))
                throw new InvalidOperationException("Legacy CUDA Python install completed but did not create " + CudaLegacyPythonExecutable + ".");

            var pip = await RunPythonArgumentsAsync(
                new PythonCommand(CudaLegacyPythonExecutable, []),
                ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade", "pip", "setuptools", "wheel"],
                TimeSpan.FromMinutes(8),
                cancellationToken).ConfigureAwait(false);
            if (pip.ExitCode != 0)
                status?.Report("Legacy CUDA Python is usable, but pip upgrade failed: " + BuildProcessDetail(pip));

            return new PythonCommand(CudaLegacyPythonExecutable, []);
        }
        finally
        {
            PythonProvisioningGate.Release();
        }
    }

    private static IEnumerable<PythonCommand> ResolveInstalledPythonCommands()
    {
        if (OperatingSystem.IsLinux())
        {
            foreach (string pythonPath in EnumerateLinuxPythonExecutablePaths())
            {
                if (File.Exists(pythonPath))
                    yield return new PythonCommand(pythonPath, []);
            }

            yield break;
        }

        if (!OperatingSystem.IsWindows())
            yield break;

        foreach (string root in EnumeratePythonInstallRoots())
        {
            if (!Directory.Exists(root))
                continue;

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root, "Python3*");
            }
            catch
            {
                continue;
            }

            foreach (string directory in directories.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string pythonPath = Path.Combine(directory, "python.exe");
                if (File.Exists(pythonPath))
                    yield return new PythonCommand(pythonPath, []);
            }
        }
    }

    private static IEnumerable<string> EnumeratePythonInstallRoots()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "Programs", "Python");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return programFiles;
    }

    private static async Task<PythonCommand?> ResolveLinuxBundledPythonAsyncInternal(
        CancellationToken cancellationToken,
        IProgress<string>? status)
    {
        string? existing = ResolveExistingLinuxBundledPythonExecutable();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            status?.Report("Using existing Linux Python environment at " + existing + ".");
            return new PythonCommand(existing, []);
        }

        await PythonProvisioningGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            existing = ResolveExistingLinuxBundledPythonExecutable();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                status?.Report("Using existing Linux Python environment at " + existing + ".");
                return new PythonCommand(existing, []);
            }

            PythonCommand? systemPython = await ResolveLinuxSystemPythonAsync(cancellationToken).ConfigureAwait(false);
            if (systemPython == null)
            {
                status?.Report("Linux Python bootstrap skipped because python3 was not found. Install python3/python3-venv or set JACKONNX_PYTHON.");
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(BundledPythonDirectory) ?? ApplicationBaseDirectory);
            status?.Report("Creating Linux Python virtual environment at " + BundledPythonDirectory + "...");
            ProcessRunResult venv = await RunPythonArgumentsAsync(
                systemPython,
                ["-m", "venv", BundledPythonDirectory],
                TimeSpan.FromMinutes(6),
                cancellationToken).ConfigureAwait(false);
            if (venv.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    "Linux Python venv bootstrap failed. Install python3-venv or set JACKONNX_PYTHON to an existing environment. " +
                    BuildProcessDetail(venv));
            }

            existing = ResolveExistingLinuxBundledPythonExecutable();
            if (string.IsNullOrWhiteSpace(existing))
                throw new InvalidOperationException("Linux Python venv bootstrap completed but did not create " + BundledPythonExecutable + ".");

            status?.Report("Upgrading pip in Linux Python environment...");
            ProcessRunResult pip = await RunPythonArgumentsAsync(
                new PythonCommand(existing, []),
                ["-m", "pip", "install", "--disable-pip-version-check", "--upgrade", "pip", "setuptools", "wheel"],
                TimeSpan.FromMinutes(8),
                cancellationToken).ConfigureAwait(false);
            if (pip.ExitCode != 0)
                status?.Report("Linux Python environment is usable, but pip upgrade failed: " + BuildProcessDetail(pip));

            status?.Report("Linux Python runtime ready at " + existing + ".");
            return new PythonCommand(existing, []);
        }
        finally
        {
            PythonProvisioningGate.Release();
        }
    }

    private static string? ResolveExistingLinuxBundledPythonExecutable()
    {
        foreach (string candidate in new[]
                 {
                     BundledPythonExecutable,
                     Path.Combine(BundledPythonDirectory, "bin", "python"),
                     Path.Combine(BundledPythonDirectory, "bin", "python3")
                 })
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static async Task<PythonCommand?> ResolveLinuxSystemPythonAsync(CancellationToken cancellationToken)
    {
        foreach (PythonCommand command in new[]
                 {
                     new PythonCommand("/usr/bin/python3", []),
                     new PythonCommand("/usr/local/bin/python3", []),
                     new PythonCommand("/bin/python3", []),
                     new PythonCommand("python3", []),
                     new PythonCommand("python", [])
                 })
        {
            if (!IsCommandName(command.FileName) && !File.Exists(command.FileName))
                continue;

            try
            {
                ProcessRunResult run = await RunPythonArgumentsAsync(command, ["-V"], TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
                if (run.ExitCode == 0)
                    return command;
            }
            catch
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateLinuxPythonExecutablePaths()
    {
        foreach (string root in EnumerateLinuxPythonRoots())
        {
            yield return Path.Combine(root, "bin", "python3");
            yield return Path.Combine(root, "bin", "python");
        }

        foreach (string path in new[]
                 {
                     "/usr/local/bin/python3.14",
                     "/usr/local/bin/python3.13",
                     "/usr/local/bin/python3.12",
                     "/usr/local/bin/python3.11",
                     "/usr/local/bin/python3.10",
                     "/usr/local/bin/python3",
                     "/usr/bin/python3.14",
                     "/usr/bin/python3.13",
                     "/usr/bin/python3.12",
                     "/usr/bin/python3.11",
                     "/usr/bin/python3.10",
                     "/usr/bin/python3",
                     "/bin/python3"
                 })
        {
            yield return path;
        }
    }

    private static IEnumerable<string> EnumerateLinuxPythonRoots()
    {
        string configured = Environment.GetEnvironmentVariable("JACKONNX_PYTHON_HOME") ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return BundledPythonDirectory;
        yield return CudaLegacyPythonDirectory;
        yield return Path.Combine(ApplicationBaseDirectory, ".venv");
        yield return Path.Combine(ApplicationBaseDirectory, "venv");
        yield return Path.Combine(Environment.CurrentDirectory, "Python");
        yield return Path.Combine(Environment.CurrentDirectory, ".venv");
        yield return Path.Combine(Environment.CurrentDirectory, "venv");

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".jackllm", "python");
            yield return Path.Combine(home, ".jackllm", "venv");
            yield return Path.Combine(home, ".local", "share", "JackLLM", "Python");
            yield return Path.Combine(home, ".local", "share", "JackLLM", "venv");
            yield return Path.Combine(home, ".cache", "jackllm", "python");
            yield return Path.Combine(home, ".cache", "jackllm", "venv");
        }
    }

    private static async Task<PythonCommand> EnsureEmbeddedPythonZipAsync(
        CancellationToken cancellationToken,
        IProgress<string>? status)
    {
        Directory.CreateDirectory(BundledPythonDirectory);
        string archivePath = Path.Combine(BundledPythonDirectory, BundledPythonEmbedFile);
        string archiveUrl = string.Join("/", BundledPythonBaseUrl, BundledPythonVersion, BundledPythonEmbedFile);
        if (!File.Exists(archivePath))
        {
            status?.Report("Downloading embedded Python " + BundledPythonVersion + " runtime...");
            await DownloadFileAsync(archiveUrl, archivePath, cancellationToken).ConfigureAwait(false);
        }

        ZipFile.ExtractToDirectory(archivePath, BundledPythonDirectory, overwriteFiles: true);
        EnableSitePackagesForEmbeddedPython(BundledPythonDirectory);
        if (!File.Exists(BundledPythonExecutable))
            throw new InvalidOperationException("Embedded Python archive did not create " + BundledPythonExecutable + ".");

        status?.Report("Bootstrapping pip for embedded Python...");
        string getPipPath = Path.Combine(BundledPythonDirectory, "get-pip.py");
        await DownloadFileAsync(GetPipUrl, getPipPath, cancellationToken).ConfigureAwait(false);
        var pip = await RunPythonArgumentsAsync(
            new PythonCommand(BundledPythonExecutable, []),
            [getPipPath, "--disable-pip-version-check", "--no-warn-script-location"],
            TimeSpan.FromMinutes(6),
            cancellationToken).ConfigureAwait(false);
        if (pip.ExitCode != 0)
            throw new InvalidOperationException("Embedded Python pip bootstrap failed. " + BuildProcessDetail(pip));

        status?.Report("Embedded Python runtime ready at " + BundledPythonExecutable + ".");
        return new PythonCommand(BundledPythonExecutable, []);
    }

    private static void EnableSitePackagesForEmbeddedPython(string pythonDirectory)
    {
        string? pthPath = Directory.EnumerateFiles(pythonDirectory, "python*._pth").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(pthPath))
            return;

        string text = File.ReadAllText(pthPath, Utf8NoBom);
        if (text.Contains("import site", StringComparison.Ordinal))
        {
            text = Regex.Replace(text, @"(?m)^\s*#\s*import\s+site\s*$", "import site");
        }
        else
        {
            text = text.TrimEnd() + Environment.NewLine + "import site" + Environment.NewLine;
        }

        File.WriteAllText(pthPath, text, Utf8NoBom);
    }

    private static string GetPythonArchiveArchitecture()
    {
        return RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "win32",
            _ => "amd64"
        };
    }

    private static async Task<ProcessRunResult> InstallPythonAsync(string installerPath, string targetDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/quiet");
        startInfo.ArgumentList.Add("InstallAllUsers=0");
        startInfo.ArgumentList.Add("PrependPath=0");
        startInfo.ArgumentList.Add("Include_pip=1");
        startInfo.ArgumentList.Add("Include_test=0");
        startInfo.ArgumentList.Add($"TargetDir={targetDirectory}");

        using var process = new Process { StartInfo = startInfo };
        StartProcessOrThrow(process, "Python installer");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(10));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Python installer exceeded the installation timeout of 10 minutes.");
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
            "");
    }

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 16,
            useAsync: true);

        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProcessRunResult> RunPythonArgumentsAsync(
        PythonCommand command,
        IReadOnlyList<string> arguments,
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
        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        StartProcessOrThrow(process, "Python command");

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
            throw new TimeoutException("Python command exceeded the configured timeout of " + timeout + ".");
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
            "");
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
        foreach (string argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(requestPath);
        startInfo.ArgumentList.Add(resultPath);

        using var process = new Process { StartInfo = startInfo };
        StartProcessOrThrow(process, "Python image runner");

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
            throw new TimeoutException("Python image generation exceeded the configured timeout of " + timeout + ".");
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

    private static void StartProcessOrThrow(Process process, string purpose)
    {
        try
        {
            if (process.Start())
                return;

            throw new Win32Exception("Process.Start returned false.");
        }
        catch (Win32Exception ex)
        {
            string fileName = process?.StartInfo?.FileName ?? "";
            string arguments = process?.StartInfo == null
                ? ""
                : string.Join(" ", process.StartInfo.ArgumentList.Select(QuoteForProcessDetail));
            string detail = "Could not start " + purpose + " '" + fileName + "'";
            if (!string.IsNullOrWhiteSpace(arguments))
                detail += " " + arguments;
            detail += ": " + ex.Message;
            throw new Win32Exception(ex.NativeErrorCode, detail);
        }
    }

    private static string QuoteForProcessDetail(string value)
    {
        value ??= "";
        return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
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

        return message.Contains("Diffusers/Torch pipeline is unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("QwenImagePipeline", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("EDMDPMSolverMultistepScheduler", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("module diffusers has no attribute", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("PEFT backend is required", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Optimum ONNX Runtime pipeline is unavailable", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No module named", StringComparison.OrdinalIgnoreCase) &&
               (message.Contains("diffusers", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("peft", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("transformers", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("torch", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("optimum", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase));
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

    private static bool HasNvidiaCudaDriver() =>
        TryLoadNativeLibrary(GetCudaDriverLibraryNames()) || NvidiaSmiReportsGpu();

    private static bool TryLoadNativeLibrary(IEnumerable<string> libraryNames)
    {
        foreach (string name in libraryNames)
        {
            if (NativeLibrary.TryLoad(name, out nint handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    private static bool NvidiaSmiReportsGpu()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=name --format=csv,noheader",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
                return false;

            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            string output = process.StandardOutput.ReadToEnd();
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetCudaDriverLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["nvcuda.dll"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["libcuda.so.1", "libcuda.so"];
        return [];
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

    private sealed record TorchRuntimeStatus(bool HasTorch, string Version, string CudaVersion, bool CudaAvailable);

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


def select_onnx_provider(payload):
    try:
        import onnxruntime as ort
        providers = ort.get_available_providers()
        if "CUDAExecutionProvider" in providers:
            return "CUDAExecutionProvider"
        if requires_cuda(payload):
            raise RuntimeError(
                "CUDA was requested for ONNX image generation, but CUDAExecutionProvider is not available. "
                "Available providers: " + ", ".join(providers) + "."
            )
        for candidate in ("DmlExecutionProvider", "CPUExecutionProvider"):
            if candidate in providers:
                return candidate
    except Exception as exc:
        if requires_cuda(payload):
            raise RuntimeError("CUDA was requested for ONNX image generation, but ONNX Runtime provider selection failed: " + str(exc))
        pass
    return "CPUExecutionProvider"


def has_nvidia_cuda_driver():
    candidates = ["nvcuda.dll"] if os.name == "nt" else ["libcuda.so.1", "libcuda.so"]
    for candidate in candidates:
        try:
            ctypes.CDLL(candidate)
            return True
        except Exception:
            pass
    return False


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


def requested_image_device_map(payload):
    requested = payload_text(payload, "device_map", "deviceMap", "image_device_map", "imageDeviceMap", "video_device_map", "videoDeviceMap")
    if not requested:
        requested = str(os.environ.get("JACKONNX_IMAGE_DEVICE_MAP") or os.environ.get("JACKONNX_VIDEO_DEVICE_MAP") or "").strip()
    if disabled_text(requested):
        return ""
    return normalize_image_device_map(requested)


def normalize_image_device_map(value):
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


def resolve_image_device_map(payload, torch, device):
    if not str(device).startswith("cuda"):
        return ""
    return requested_image_device_map(payload)


def cuda_max_memory_map(torch, payload=None):
    try:
        count = int(torch.cuda.device_count())
    except Exception:
        count = 0
    if count <= 0:
        return {}
    try:
        reserve_text = payload_text(payload or {}, "cuda_memory_reserve_mb", "cudaMemoryReserveMb", "image_cuda_memory_reserve_mb", "imageCudaMemoryReserveMb", "video_cuda_memory_reserve_mb", "videoCudaMemoryReserveMb")
        reserve_mb = int(float(reserve_text or os.environ.get("JACKONNX_IMAGE_CUDA_MEMORY_RESERVE_MB") or os.environ.get("JACKONNX_VIDEO_CUDA_MEMORY_RESERVE_MB") or "768"))
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
    cpu_max = payload_text(payload or {}, "cpu_max_memory", "cpuMaxMemory", "image_cpu_max_memory", "imageCpuMaxMemory", "video_cpu_max_memory", "videoCpuMaxMemory")
    if not cpu_max:
        cpu_max = str(os.environ.get("JACKONNX_IMAGE_CPU_MAX_MEMORY") or os.environ.get("JACKONNX_VIDEO_CPU_MAX_MEMORY") or "").strip()
    if cpu_max and not disabled_text(cpu_max):
        result["cpu"] = cpu_max
    return result


def image_offload_folder(payload=None):
    configured = payload_text(payload or {}, "offload_folder", "offloadFolder", "image_offload_folder", "imageOffloadFolder", "video_offload_folder", "videoOffloadFolder")
    if not configured:
        configured = str(os.environ.get("JACKONNX_IMAGE_OFFLOAD_FOLDER") or os.environ.get("JACKONNX_VIDEO_OFFLOAD_FOLDER") or "").strip()
    if configured and not disabled_text(configured):
        return configured
    if os.path.isdir("/stor2"):
        return "/stor2/JackONNXOffload"
    return os.path.join(tempfile.gettempdir(), "jackonnx-image-offload")


def preferred_diffusers_variant(payload, device):
    configured = str(payload.get("variant") or payload.get("weight_variant") or os.environ.get("JACKONNX_IMAGE_VARIANT") or "").strip()
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


def diffusers_load_kwargs(payload, torch, device, dtype, local_files_only):
    kwargs = {
        "torch_dtype": dtype,
        "local_files_only": local_files_only,
        "safety_checker": None,
    }
    variant = preferred_diffusers_variant(payload, device)
    if variant:
        kwargs["variant"] = variant
    device_map = resolve_image_device_map(payload, torch, device)
    if device_map:
        kwargs["device_map"] = device_map
        kwargs["low_cpu_mem_usage"] = True
        max_memory = cuda_max_memory_map(torch, payload)
        if max_memory:
            kwargs["max_memory"] = max_memory
        offload_folder = image_offload_folder(payload)
        try:
            os.makedirs(offload_folder, exist_ok=True)
            kwargs["offload_folder"] = offload_folder
            if str(os.environ.get("JACKONNX_IMAGE_OFFLOAD_STATE_DICT") or "").strip().lower() in ("1", "true", "yes", "on"):
                kwargs["offload_state_dict"] = True
        except Exception:
            pass
    return kwargs, device_map


def strip_device_map_kwargs(kwargs):
    excluded = {"device_map", "low_cpu_mem_usage", "max_memory", "offload_folder", "offload_state_dict"}
    return {key: value for key, value in (kwargs or {}).items() if key not in excluded}


def pipeline_uses_device_map(backend_detail):
    return ":device_map=" in str(backend_detail or "")


def maybe_move_pipeline_to_device(pipe, device, backend_detail):
    if pipeline_uses_device_map(backend_detail):
        return pipe
    return pipe.to(device)


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
                min_free_mb = int(float(payload.get("min_cuda_free_memory_mb") or os.environ.get("JACKONNX_MIN_CUDA_FREE_MEMORY_MB") or default_min_free_mb))
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
    selected_device = select_available_cuda_device(payload, torch, 4096)
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
    if torch.cuda.is_available():
        return selected_device
    if should_prefer_cuda(payload) and has_nvidia_cuda_driver():
        version = str(getattr(torch, "__version__", "unknown"))
        torch_cuda = str(getattr(torch.version, "cuda", "") or "none")
        raise RuntimeError(
            "CUDA GPU is available, but this Python has a CPU-only PyTorch build. "
            "Install CUDA-enabled PyTorch or allow JackONNX to repair the image runtime. "
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
    configured = payload_bool(payload or {}, "disable_cuda_memory_guard", "disableCudaMemoryGuard", "image_disable_cuda_memory_guard", "imageDisableCudaMemoryGuard", "video_disable_cuda_memory_guard", "videoDisableCudaMemoryGuard")
    if configured is not None:
        return configured
    return str(os.environ.get("JACKONNX_DISABLE_CUDA_MEMORY_GUARD") or "").strip().lower() in ("1", "true", "yes", "on")


def enforce_cuda_memory_guard(payload, device, required_bytes, label):
    if cuda_memory_guard_disabled(payload) or not str(device).startswith("cuda"):
        return
    if not requires_cuda(payload):
        return
    if requested_image_device_map(payload) or cpu_offload_allowed(payload):
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
        reserve_text = payload_text(payload or {}, "cuda_memory_reserve_mb", "cudaMemoryReserveMb", "image_cuda_memory_reserve_mb", "imageCudaMemoryReserveMb", "video_cuda_memory_reserve_mb", "videoCudaMemoryReserveMb")
        reserve_mb = int(float(reserve_text or os.environ.get("JACKONNX_IMAGE_CUDA_MEMORY_RESERVE_MB") or os.environ.get("JACKONNX_CUDA_MEMORY_RESERVE_MB") or "1024"))
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
    configured = payload_bool(payload or {}, "allow_cpu_offload", "allowCpuOffload", "image_allow_cpu_offload", "imageAllowCpuOffload", "video_allow_cpu_offload", "videoAllowCpuOffload")
    if configured is not None:
        return configured
    return str(os.environ.get("JACKONNX_ALLOW_CPU_OFFLOAD") or "").strip().lower() in ("1", "true", "yes", "on")


def verify_cuda_pipeline(pipe, torch, device, payload, label, backend_detail=""):
    if cpu_offload_allowed(payload) or not str(device).startswith("cuda") or not requires_cuda(payload):
        return
    if pipeline_uses_device_map(backend_detail):
        if not requested_image_device_map(payload):
            raise RuntimeError(
                "CUDA was requested for " + label + ", but the pipeline is using device_map/offload. "
                "JackONNX will not run this generator through CPU/offload unless CPU/offload placement is enabled."
            )
        return
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
    requested = payload_bool(payload or {}, "memory_saving", "memorySaving", "image_memory_saving", "imageMemorySaving", "video_memory_saving", "videoMemorySaving")
    if requested is not None:
        return requested
    configured = str(os.environ.get("JACKONNX_IMAGE_MEMORY_SAVING") or os.environ.get("JACKONNX_VIDEO_MEMORY_SAVING") or "").strip().lower()
    if configured in ("1", "true", "yes", "on"):
        return True
    if configured in ("0", "false", "no", "off"):
        return False
    if not str(device).startswith("cuda"):
        return False
    try:
        required = int(payload.get("required_memory_bytes") or 0)
        if required >= 12 * 1024 * 1024 * 1024:
            return True
    except Exception:
        pass
    return False


def configure_pipeline_memory_options(pipe, payload, device):
    if hasattr(pipe, "enable_attention_slicing"):
        pipe.enable_attention_slicing()
    if not should_enable_pipeline_memory_saving(payload, device):
        return
    if hasattr(pipe, "enable_vae_slicing"):
        pipe.enable_vae_slicing()
    if hasattr(pipe, "enable_vae_tiling"):
        try:
            pipe.enable_vae_tiling()
        except Exception:
            pass


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
    key = "source_" + kind + "_path"
    return str(payload.get(key) or "").strip()


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


def adapt_image_pipeline_for_source(pipe, diffusers_module):
    auto_cls = getattr(diffusers_module, "AutoPipelineForImage2Image", None)
    if auto_cls is None:
        return pipe
    try:
        if hasattr(auto_cls, "from_pipe"):
            return auto_cls.from_pipe(pipe)
    except Exception:
        return pipe
    return pipe


def run_onnx(payload):
    try:
        from optimum.onnxruntime import ORTStableDiffusionPipeline
    except Exception as exc:
        raise RuntimeError("Optimum ONNX Runtime pipeline is unavailable: " + str(exc))

    provider = select_onnx_provider(payload)
    source_image = load_source_image(payload)
    pipeline_cls = ORTStableDiffusionPipeline
    if source_image is not None:
        try:
            from optimum.onnxruntime import ORTStableDiffusionImg2ImgPipeline
            pipeline_cls = ORTStableDiffusionImg2ImgPipeline
        except Exception as exc:
            raise RuntimeError("Optimum ONNX image-to-image pipeline is unavailable: " + str(exc))
    pipe = pipeline_cls.from_pretrained(
        payload["model_dir"],
        provider=provider,
        local_files_only=True,
    )
    kwargs = build_common_kwargs(payload)
    if source_image is not None:
        kwargs["image"] = source_image
    seed = payload.get("seed")
    if seed is not None:
        try:
            import numpy as np
            kwargs["generator"] = np.random.RandomState(int(seed))
        except Exception:
            pass
    kwargs = filter_pipeline_kwargs(pipe, kwargs)
    if source_image is not None and "image" not in kwargs:
        raise RuntimeError("The selected ONNX image pipeline does not accept source image input.")
    image = pipe(**kwargs).images[0]
    image.save(payload["output_path"])
    suffix = ":image-to-image" if source_image is not None else ""
    return "python.optimum.onnxruntime:" + provider + suffix


def run_diffusers(payload):
    try:
        patch_huggingface_hub_compatibility()
        import torch
        from diffusers import DiffusionPipeline
        import diffusers
    except Exception as exc:
        raise RuntimeError("Diffusers/Torch pipeline is unavailable: " + str(exc))

    model_dir = payload["model_dir"]
    patch_diffusers_pipeline_aliases(model_dir, diffusers)
    model_index = os.path.join(model_dir, "model_index.json")
    model_card = read_model_card_metadata(model_dir)
    manifest_metadata = payload.get("metadata") or {}
    source_model_dir = payload.get("source_model_dir") or model_dir
    device = select_torch_device(payload, torch)
    dtype = torch.float16 if str(device).startswith("cuda") else torch.float32
    load_kwargs, device_map = diffusers_load_kwargs(payload, torch, device, dtype, True)
    backend = "python.diffusers"
    if device_map:
        backend += ":device_map=" + str(device_map)
    if not os.path.exists(model_index) and is_lora_adapter(model_dir, model_card, manifest_metadata):
        base_models = get_base_model_candidates(payload, model_card, manifest_metadata)
        if not base_models:
            raise RuntimeError("This image model is a LoRA adapter, not a standalone Diffusers pipeline. Its model card or manifest does not declare a base_model.")

        base_model = base_models[0]
        base_path = ""
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
        enforce_cuda_memory_guard(
            payload,
            device,
            path_size_bytes(base_path) or payload.get("required_memory_bytes"),
            "image LoRA base model '" + base_model + "'"
        )
        try:
            pipe = DiffusionPipeline.from_pretrained(
                base_path,
                **load_kwargs,
            )
        except Exception as exc:
            if is_qwen_pipeline_dependency_error(exc):
                raise RuntimeError(format_qwen_pipeline_dependency_error(exc))
            raise RuntimeError(
                "This image model is a LoRA adapter and requires the base model '" + base_model +
                "'. Download/register that base model locally before using this adapter. " + str(exc)
            )

        weight_name = find_safetensors_weight(model_dir)
        if not weight_name:
            raise RuntimeError("This image model looks like a LoRA adapter, but no .safetensors weight file was found.")
        pipe.load_lora_weights(model_dir, weight_name=weight_name)
    elif not os.path.exists(model_index):
        single_file = find_safetensors_weight(model_dir)
        if not single_file:
            raise RuntimeError("No model_index.json or .safetensors model file was found in " + model_dir + ".")
        single_file_path = os.path.join(model_dir, single_file)
        enforce_cuda_memory_guard(payload, device, path_size_bytes(single_file_path), "image model '" + payload.get("manifest_id", "") + "'")
        try:
            pipe = DiffusionPipeline.from_single_file(
                single_file_path,
                **load_kwargs,
            )
        except TypeError:
            pipe = DiffusionPipeline.from_single_file(
                single_file_path,
                **strip_device_map_kwargs(load_kwargs),
            )
        except Exception as exc:
            if is_qwen_pipeline_dependency_error(exc):
                raise RuntimeError(format_qwen_pipeline_dependency_error(exc))
            raise RuntimeError(
                "This image model is not a Diffusers pipeline folder because model_index.json is missing, and it could not be loaded as a single-file checkpoint. " + str(exc)
            )
    else:
        enforce_cuda_memory_guard(
            payload,
            device,
            payload.get("required_memory_bytes") or path_size_bytes(model_dir),
            "image model '" + payload.get("manifest_id", "") + "'"
        )
        try:
            pipe = DiffusionPipeline.from_pretrained(
                model_dir,
                **load_kwargs,
            )
        except Exception as exc:
            if is_qwen_pipeline_dependency_error(exc):
                raise RuntimeError(format_qwen_pipeline_dependency_error(exc))
            raise

    source_image = load_source_image(payload)
    if source_image is not None:
        pipe = adapt_image_pipeline_for_source(pipe, diffusers)

    configure_pipeline_memory_options(pipe, payload, device)
    pipe = maybe_move_pipeline_to_device(pipe, device, backend)
    verify_cuda_pipeline(pipe, torch, device, payload, "image model '" + payload.get("manifest_id", "") + "'", backend)

    kwargs = build_common_kwargs(payload)
    if source_image is not None:
        kwargs["image"] = source_image
    seed = payload.get("seed")
    if seed is not None:
        kwargs["generator"] = torch.Generator(device=device).manual_seed(int(seed))
    kwargs = filter_pipeline_kwargs(pipe, kwargs)
    if source_image is not None and "image" not in kwargs:
        raise RuntimeError("The selected Diffusers image pipeline does not accept source image input.")
    image = pipe(**kwargs).images[0]
    image.save(payload["output_path"])
    suffix = ":image-to-image" if source_image is not None else ""
    return backend + ":" + device + suffix


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


def is_lora_adapter(model_dir, metadata, manifest_metadata=None):
    manifest_metadata = manifest_metadata or {}
    text = " ".join(metadata.get("tags") or []) + " " + " ".join(os.listdir(model_dir))
    text += " " + str(manifest_metadata.get("adapterType") or manifest_metadata.get("adapter_type") or "")
    return "lora" in text.lower() or "adapter" in text.lower()


def is_qwen_pipeline_dependency_error(exc):
    return "QwenImagePipeline" in str(exc)


def format_qwen_pipeline_dependency_error(exc):
    try:
        import diffusers
        version = str(getattr(diffusers, "__version__", "unknown"))
    except Exception:
        version = "unknown"
    return (
        "This image model requires Qwen-Image pipeline support in diffusers 0.35.0+, but your runtime has diffusers "
        + version +
        ". Update your Python runtime (set JACKONNX_PYTHON or ImageGenerationPythonExecutable) and re-run. Original error: "
        + str(exc)
    )


def split_metadata_values(value):
    value = (value or "").strip().strip("'\"")
    if not value:
        return []
    if value.startswith("[") and value.endswith("]"):
        value = value[1:-1]
        return [part.strip().strip("'\"") for part in value.split(",") if part.strip()]
    if "|" in value:
        return [part.strip().strip("'\"") for part in value.split("|") if part.strip()]
    return [value]


def get_base_model_candidates(payload, model_card, manifest_metadata):
    values = []
    for key in ("base_model", "baseModel", "baseModels"):
        values.extend(split_metadata_values(model_card.get(key) if isinstance(model_card, dict) else ""))
        values.extend(split_metadata_values(manifest_metadata.get(key) if isinstance(manifest_metadata, dict) else ""))
    values.extend(model_card.get("base_models") or [])
    values.extend(split_metadata_values(payload.get("base_model") or ""))

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


def find_safetensors_weight(model_dir):
    candidates = [
        name for name in os.listdir(model_dir)
        if name.lower().endswith(".safetensors") and os.path.isfile(os.path.join(model_dir, name))
    ]
    if not candidates:
        return ""
    candidates.sort(key=lambda name: (0 if "lora" in name.lower() else 1, name.lower()))
    return candidates[0]


def resolve_local_base_model_path(model_dir, base_model):
    base_model = normalize_model_reference(base_model)
    if not base_model or "/" not in base_model:
        return ""

    parts = base_model.replace("\\", "/").strip("/").split("/")
    current = os.path.abspath(model_dir)
    while True:
        if os.path.basename(current).lower() == "completemodels":
            candidate = os.path.join(current, *parts, "main")
            if os.path.exists(os.path.join(candidate, "model_index.json")):
                return candidate
            candidate = os.path.join(current, *parts)
            if os.path.exists(os.path.join(candidate, "model_index.json")):
                return candidate
            return ""

        parent = os.path.dirname(current)
        if parent == current:
            return ""
        current = parent


def normalize_model_reference(value):
    value = (value or "").strip().strip("'\"").replace("\\", "/")
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


def build_common_kwargs(payload):
    kwargs = {
        "prompt": payload.get("prompt") or "",
        "width": int(payload.get("width") or 512),
        "height": int(payload.get("height") or 512),
        "num_inference_steps": int(payload.get("steps") or 30),
        "guidance_scale": float(payload.get("guidance_scale") or 7.5),
    }
    negative = payload.get("negative_prompt") or ""
    if negative.strip():
        kwargs["negative_prompt"] = negative
    if (payload.get("source_path") or payload.get("source_data_url")) and "strength" not in kwargs:
        kwargs["strength"] = float(payload.get("strength") or 0.8)
    return kwargs


def main():
    request_path = sys.argv[1]
    result_path = sys.argv[2]
    try:
        with open(request_path, "r", encoding="utf-8-sig") as handle:
            payload = json.load(handle)

        os.makedirs(os.path.dirname(payload["output_path"]), exist_ok=True)
        model_format = (payload.get("format") or "").lower()
        if model_format == "onnx":
            backend = run_onnx(payload)
        else:
            backend = run_diffusers(payload)

        write_result(result_path, {
            "success": True,
            "message": "Image generated successfully.",
            "backend": backend,
            "output_path": payload["output_path"],
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
