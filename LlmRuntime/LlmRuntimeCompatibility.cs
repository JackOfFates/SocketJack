using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlmRuntime;

public sealed class LlmRuntimeCompatibilityService
{
    public const string CudaToolkitDownloadUrl = "https://developer.nvidia.com/cuda-downloads";
    public const string DefaultPytorchRepairMessage = "CUDA-capable GPU generation is disabled until Python has a compatible CUDA-enabled PyTorch build.";
    public const string DefaultLinuxPytorchCudaIndexUrl = "https://download.pytorch.org/whl/cu124";
    public const string DefaultWindowsLegacyCudaPytorchIndexUrl = "https://download.pytorch.org/whl/cu118";
    public const string LinuxCudaPytorchInstallEndpoint = "/api/v1/runtime/compatibility/install-linux-cuda-pytorch";
    public const string LinuxCudaPytorchInstallScriptRelativePath = "install/linux/install-jackllm-cuda-pytorch.sh";
    public const string WindowsLegacyCudaPythonVersion = "3.11.9";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private static readonly SemaphoreSlim WindowsLegacyCudaRepairGate = new(1, 1);

    private readonly LlmRuntimeOptions _options;
    private readonly ILlmRuntimeCompatibilityProbe _probe;
    private readonly object _statusCacheLock = new();
    private LlmRuntimeCompatibilityStatus? _cachedStatus;
    private DateTimeOffset _cachedStatusUtc = DateTimeOffset.MinValue;
    private string _cachedStatusPython = "";
    private static readonly TimeSpan StatusCacheTtl = TimeSpan.FromMinutes(10);

    public LlmRuntimeCompatibilityService(LlmRuntimeOptions? options = null, ILlmRuntimeCompatibilityProbe? probe = null)
    {
        _options = options ?? new LlmRuntimeOptions();
        _probe = probe ?? new DefaultLlmRuntimeCompatibilityProbe();
    }

    public string ConfigPath => ResolveCompatibilityConfigPath(_options);

    public LlmRuntimeCompatibilityStatus GetStatus(string? pythonExecutable = null, CancellationToken cancellationToken = default, bool forceRefresh = false)
    {
        string pythonPath = ResolvePythonExecutable(pythonExecutable);
        if (!forceRefresh)
        {
            lock (_statusCacheLock)
            {
                if (_cachedStatus != null &&
                    string.Equals(_cachedStatusPython, pythonPath, StringComparison.OrdinalIgnoreCase) &&
                    DateTimeOffset.UtcNow - _cachedStatusUtc < StatusCacheTtl)
                {
                    return _cachedStatus;
                }
            }
        }

        LlmRuntimeCompatibilityConfig config = LoadConfig();
        LlmRuntimeCompatibilityCatalog catalog = BuildCatalog(config);
        IReadOnlyList<LlmDetectedGpu> detectedGpus = _probe.DetectNvidiaGpus(cancellationToken);
        foreach (LlmDetectedGpu gpu in detectedGpus)
            PopulateGpuCatalogMatch(gpu, catalog);

        LlmPythonRuntimeStatus python = string.IsNullOrWhiteSpace(pythonPath)
            ? new LlmPythonRuntimeStatus { IsAvailable = false, Error = "No Python executable was configured or found." }
            : _probe.InspectPython(pythonPath, cancellationToken);

        bool hasCudaDriver = _probe.HasCudaDriver(cancellationToken);
        IReadOnlyList<string> missingCudaDependencies = _probe.FindMissingCudaDependencies();
        LlmDetectedGpu? bestGpu = detectedGpus
            .Where(gpu => gpu.IsNvidia)
            .OrderByDescending(gpu => ParseCapability(gpu.ComputeCapability))
            .FirstOrDefault();
        string computeCapability = FirstNonEmpty(bestGpu?.ComputeCapability, python.TorchCudaDeviceCapability);
        LlmPytorchPackageRecommendation? recommendation = python.IsAvailable && TryParsePythonVersion(python.Version, out Version? pythonVersion)
            ? catalog.RecommendPytorch(pythonVersion!, computeCapability, config)
            : null;

        bool gpuDetected = detectedGpus.Any(gpu => gpu.IsNvidia);
        bool torchCompatible = python.HasTorch &&
            !string.IsNullOrWhiteSpace(python.TorchCudaVersion) &&
            IsInstalledTorchCompatible(catalog, python, computeCapability, config);
        bool torchCudaAvailable = python.HasTorch && python.TorchCudaAvailable && torchCompatible;
        bool repairRequired = gpuDetected && (!python.HasTorch || !python.TorchCudaAvailable || !torchCompatible);
        bool generationDisabled = !torchCudaAvailable;
        bool legacyCudaRepairAvailable = repairRequired && CanRepairWithWindowsLegacyCudaPython(detectedGpus, hasCudaDriver, config);
        string status = torchCudaAvailable ? "ok" : repairRequired ? "repair_required" : "cpu_only";
        string message = BuildStatusMessage(gpuDetected, hasCudaDriver, python, torchCompatible, recommendation, missingCudaDependencies, legacyCudaRepairAvailable);
        string linuxInstallScriptPath = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ResolveLinuxCudaPytorchInstallScriptPath()
            : "";
        string linuxInstallCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? BuildLinuxCudaPytorchInstallCommand(pythonPath)
            : "";

        var actions = new List<LlmRuntimeCompatibilityAction>();
        if (missingCudaDependencies.Count > 0 || !hasCudaDriver)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                actions.Add(new LlmRuntimeCompatibilityAction
                {
                    Id = "install_cuda",
                    Label = "Install Linux CUDA",
                    Kind = "post",
                    Endpoint = LinuxCudaPytorchInstallEndpoint,
                    Command = linuxInstallCommand,
                    Enabled = true,
                    Detail = "Runs the bundled Linux installer. It installs available NVIDIA runtime packages through apt when root/passwordless sudo is available, then installs CUDA-enabled PyTorch into the JackLLM Python environment."
                });
            }
            else
            {
                actions.Add(new LlmRuntimeCompatibilityAction
                {
                    Id = "install_cuda",
                    Label = "Install CUDA",
                    Url = CudaToolkitDownloadUrl,
                    Kind = "open_url",
                    Enabled = true
                });
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            actions.Add(new LlmRuntimeCompatibilityAction
            {
                Id = "install_linux_cuda_pytorch",
                Label = "Install Linux CUDA + PyTorch",
                Kind = "post",
                Endpoint = LinuxCudaPytorchInstallEndpoint,
                Command = linuxInstallCommand,
                Enabled = true,
                Detail = "Creates or repairs the Linux Python venv, installs CUDA-enabled torch/torchvision/torchaudio, and verifies torch.cuda.is_available()."
            });
        }

        if (repairRequired)
        {
            actions.Add(new LlmRuntimeCompatibilityAction
            {
                Id = "repair_pytorch",
                Label = "Repair PyTorch",
                Kind = "post",
                Endpoint = "/api/v1/runtime/compatibility/repair-pytorch",
                Enabled = config.AllowPytorchRepair && (recommendation != null || legacyCudaRepairAvailable),
                Detail = recommendation == null
                    ? legacyCudaRepairAvailable
                        ? "Install JackLLM's Python " + WindowsLegacyCudaPythonVersion + " legacy CUDA runtime with torch 2.1/cu118 for this GPU."
                        : "No compatible PyTorch CUDA wheel could be selected for this Python/GPU combination."
                    : "Install torch " + recommendation.PytorchVersion + " from " + recommendation.IndexUrl + "."
            });
        }

        var statusPayload = new LlmRuntimeCompatibilityStatus
        {
            Status = status,
            Message = message,
            ConfigPath = ConfigPath,
            CudaToolkitDownloadUrl = CudaToolkitDownloadUrl,
            LinuxCudaPytorchInstallScriptPath = linuxInstallScriptPath,
            LinuxCudaPytorchInstallCommand = linuxInstallCommand,
            GenerationDisabled = generationDisabled,
            RequiresPytorchRepair = repairRequired,
            IsGpuGenerationEnabled = torchCudaAvailable,
            Config = config,
            Diagnostics = new LlmRuntimeCompatibilityDiagnostics
            {
                Gpus = detectedGpus,
                HasCudaDriver = hasCudaDriver,
                MissingCudaDependencies = missingCudaDependencies,
                Python = python,
                RecommendedPytorch = recommendation
            },
            Actions = actions
        };
        lock (_statusCacheLock)
        {
            _cachedStatus = statusPayload;
            _cachedStatusUtc = DateTimeOffset.UtcNow;
            _cachedStatusPython = pythonPath;
        }

        return statusPayload;
    }

    public LlmRuntimeCompatibilityConfig LoadConfig()
    {
        string path = ConfigPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new LlmRuntimeCompatibilityConfig();

        try
        {
            return JsonSerializer.Deserialize<LlmRuntimeCompatibilityConfig>(File.ReadAllText(path), JsonOptions) ??
                   new LlmRuntimeCompatibilityConfig();
        }
        catch
        {
            return new LlmRuntimeCompatibilityConfig();
        }
    }

    public LlmRuntimeCompatibilityConfig SaveConfig(LlmRuntimeCompatibilityConfig config)
    {
        config ??= new LlmRuntimeCompatibilityConfig();
        config.Version = Math.Max(1, config.Version);
        string path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(config, JsonOptions));
        InvalidateStatusCache();
        return config;
    }

    public void ResetConfig()
    {
        string path = ConfigPath;
        if (File.Exists(path))
            File.Delete(path);
        InvalidateStatusCache();
    }

    private void InvalidateStatusCache()
    {
        lock (_statusCacheLock)
        {
            _cachedStatus = null;
            _cachedStatusUtc = DateTimeOffset.MinValue;
            _cachedStatusPython = "";
        }
    }

    public async Task<LlmPytorchRepairResult> RepairPytorchAsync(string? pythonExecutable = null, CancellationToken cancellationToken = default)
    {
        LlmRuntimeCompatibilityStatus status = GetStatus(pythonExecutable, cancellationToken, forceRefresh: true);
        if (!status.Config.AllowPytorchRepair)
            throw new InvalidOperationException("PyTorch repair is disabled in LlmRuntime compatibility configuration.");
        if (status.Diagnostics.RecommendedPytorch == null && CanRepairWithWindowsLegacyCudaPython(status))
            return await RepairWindowsLegacyCudaPytorchAsync(cancellationToken).ConfigureAwait(false);

        string pythonPath = status.Diagnostics.Python.ExecutablePath;
        if (string.IsNullOrWhiteSpace(pythonPath) || !status.Diagnostics.Python.IsAvailable)
            throw new InvalidOperationException("No usable Python executable was found for PyTorch repair.");
        if (status.Diagnostics.RecommendedPytorch == null)
            throw new InvalidOperationException("No compatible CUDA PyTorch wheel could be selected for this Python/GPU combination.");

        LlmPytorchPackageRecommendation recommendation = status.Diagnostics.RecommendedPytorch;
        var args = new List<string>
        {
            "-m",
            "pip",
            "install",
            "--disable-pip-version-check",
            "--no-input",
            "--upgrade",
            "--force-reinstall",
            "torch==" + recommendation.PytorchVersion + ".*",
            "torchvision",
            "torchaudio",
            "--index-url",
            recommendation.IndexUrl
        };

        LlmProcessRunResult run = await _probe.RunPythonAsync(pythonPath, args, TimeSpan.FromMinutes(20), cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException("Failed to repair PyTorch. " + BuildProcessDetail(run));

        LlmRuntimeCompatibilityStatus repaired = GetStatus(pythonPath, cancellationToken, forceRefresh: true);
        return new LlmPytorchRepairResult
        {
            Status = repaired.IsGpuGenerationEnabled ? "repaired" : "failed",
            PythonExecutable = pythonPath,
            PytorchVersion = repaired.Diagnostics.Python.TorchVersion,
            TorchCudaVersion = repaired.Diagnostics.Python.TorchCudaVersion,
            IndexUrl = recommendation.IndexUrl,
            Message = repaired.IsGpuGenerationEnabled
                ? "CUDA-enabled PyTorch is ready for GPU generation."
                : repaired.Message,
            Compatibility = repaired
        };
    }

    private async Task<LlmPytorchRepairResult> RepairWindowsLegacyCudaPytorchAsync(CancellationToken cancellationToken)
    {
        string pythonPath = await EnsureWindowsLegacyCudaPythonAsync(cancellationToken).ConfigureAwait(false);
        var args = new List<string>
        {
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
            DefaultWindowsLegacyCudaPytorchIndexUrl
        };

        LlmProcessRunResult run = await _probe.RunPythonAsync(pythonPath, args, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException("Failed to install legacy CUDA PyTorch. " + BuildProcessDetail(run));

        LlmProcessRunResult numpy = await _probe.RunPythonAsync(
            pythonPath,
            ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade", "--force-reinstall", "numpy<2"],
            TimeSpan.FromMinutes(8),
            cancellationToken).ConfigureAwait(false);
        if (numpy.ExitCode != 0)
            throw new InvalidOperationException("Failed to pin NumPy for legacy CUDA PyTorch. " + BuildProcessDetail(numpy));

        LlmRuntimeCompatibilityStatus repaired = GetStatus(pythonPath, cancellationToken, forceRefresh: true);
        return new LlmPytorchRepairResult
        {
            Status = repaired.IsGpuGenerationEnabled ? "repaired" : "failed",
            PythonExecutable = pythonPath,
            PytorchVersion = repaired.Diagnostics.Python.TorchVersion,
            TorchCudaVersion = repaired.Diagnostics.Python.TorchCudaVersion,
            IndexUrl = DefaultWindowsLegacyCudaPytorchIndexUrl,
            Message = repaired.IsGpuGenerationEnabled
                ? "Legacy CUDA-enabled PyTorch is ready for GPU generation."
                : repaired.Message,
            Compatibility = repaired
        };
    }

    public async Task<LlmLinuxCudaPytorchInstallResult> InstallLinuxCudaPytorchAsync(
        string? pythonExecutable = null,
        string? torchIndexUrl = null,
        string? torchVersion = null,
        CancellationToken cancellationToken = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("The Linux CUDA/PyTorch installer can only run on Linux.");

        string scriptPath = ResolveLinuxCudaPytorchInstallScriptPath();
        if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            throw new FileNotFoundException("The bundled Linux CUDA/PyTorch installer script was not found.", scriptPath);

        LlmRuntimeCompatibilityStatus before = GetStatus(pythonExecutable, cancellationToken, forceRefresh: true);
        string pythonPath = ResolvePythonExecutable(pythonExecutable);
        string indexUrl = FirstNonEmpty(
            torchIndexUrl,
            Environment.GetEnvironmentVariable("JACKONNX_TORCH_CUDA_INDEX_URL"),
            DefaultLinuxPytorchCudaIndexUrl,
            before.Diagnostics.RecommendedPytorch?.IndexUrl);
        string version = FirstNonEmpty(torchVersion, before.Config.PreferredPytorchVersion);
        string installRoot = ResolveLinuxCudaPytorchInstallRoot();

        var args = new List<string>
        {
            scriptPath,
            "--app-root",
            installRoot,
            "--torch-index-url",
            indexUrl,
            "--noninteractive"
        };

        if (!string.IsNullOrWhiteSpace(pythonPath))
        {
            args.Add("--python");
            args.Add(pythonPath);
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            args.Add("--torch-version");
            args.Add(version);
        }

        LlmProcessRunResult run = await RunProcessAsync("bash", args, TimeSpan.FromMinutes(45), cancellationToken).ConfigureAwait(false);
        LlmRuntimeCompatibilityStatus after = GetStatus(pythonExecutable, cancellationToken, forceRefresh: true);
        bool installed = run.ExitCode == 0 && after.IsGpuGenerationEnabled;
        return new LlmLinuxCudaPytorchInstallResult
        {
            Status = installed ? "installed" : "failed",
            ScriptPath = scriptPath,
            Command = BuildLinuxCudaPytorchInstallCommand(pythonPath, indexUrl, version, installRoot),
            ExitCode = run.ExitCode,
            StandardOutput = run.StandardOutput,
            StandardError = run.StandardError,
            Message = installed
                ? "Linux CUDA-enabled PyTorch is ready."
                : "Linux CUDA/PyTorch install finished with exit code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + after.Message,
            Compatibility = after
        };
    }

    private static async Task<string> EnsureWindowsLegacyCudaPythonAsync(CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException("The legacy CUDA Python repair is only available on Windows.");

        string pythonPath = ResolveWindowsLegacyCudaPythonExecutable();
        if (File.Exists(pythonPath))
            return pythonPath;

        await WindowsLegacyCudaRepairGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(pythonPath))
                return pythonPath;

            string directory = ResolveWindowsLegacyCudaPythonDirectory();
            Directory.CreateDirectory(directory);
            string installerFile = "python-" + WindowsLegacyCudaPythonVersion + "-" + GetWindowsPythonInstallerArchitecture() + ".exe";
            string installerPath = Path.Combine(directory, installerFile);
            if (!File.Exists(installerPath))
            {
                string installerUrl = "https://www.python.org/ftp/python/" + WindowsLegacyCudaPythonVersion + "/" + installerFile;
                await DownloadFileAsync(installerUrl, installerPath, cancellationToken).ConfigureAwait(false);
            }

            LlmProcessRunResult install = await RunProcessAsync(
                installerPath,
                [
                    "/quiet",
                    "InstallAllUsers=0",
                    "PrependPath=0",
                    "Include_pip=1",
                    "Include_test=0",
                    "TargetDir=" + directory
                ],
                TimeSpan.FromMinutes(10),
                cancellationToken).ConfigureAwait(false);
            if (install.ExitCode != 0)
                throw new InvalidOperationException("Legacy CUDA Python installer failed. " + BuildProcessDetail(install));
            if (!File.Exists(pythonPath))
                throw new InvalidOperationException("Legacy CUDA Python install completed but did not create " + pythonPath + ".");

            var probe = new DefaultLlmRuntimeCompatibilityProbe();
            LlmProcessRunResult pip = await probe.RunPythonAsync(
                pythonPath,
                ["-m", "pip", "install", "--disable-pip-version-check", "--no-input", "--upgrade", "pip", "setuptools", "wheel"],
                TimeSpan.FromMinutes(8),
                cancellationToken).ConfigureAwait(false);
            if (pip.ExitCode != 0)
                throw new InvalidOperationException("Legacy CUDA Python pip upgrade failed. " + BuildProcessDetail(pip));

            return pythonPath;
        }
        finally
        {
            WindowsLegacyCudaRepairGate.Release();
        }
    }

    private static string ResolveWindowsLegacyCudaPythonDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "PythonCudaLegacy");

    private static string ResolveWindowsLegacyCudaPythonExecutable() =>
        Path.Combine(ResolveWindowsLegacyCudaPythonDirectory(), "python.exe");

    private static string GetWindowsPythonInstallerArchitecture() =>
        RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "win32",
            _ => "amd64"
        };

    private static async Task DownloadFileAsync(string url, string destinationPath, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1 << 16,
            useAsync: true);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public static string ResolveCompatibilityConfigPath(LlmRuntimeOptions options)
    {
        options ??= new LlmRuntimeOptions();
        if (!string.IsNullOrWhiteSpace(options.CompatibilityConfigPath))
            return Path.GetFullPath(options.CompatibilityConfigPath);

        if (!string.IsNullOrWhiteSpace(options.RuntimeConfigPath))
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(options.RuntimeConfigPath)) ?? Environment.CurrentDirectory;
            return Path.Combine(directory, "LlmRuntime.compatibility.json");
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.CurrentDirectory;
        return Path.Combine(localAppData, "SocketJack", "LlmRuntime.compatibility.json");
    }

    public static string ResolveLinuxCudaPytorchInstallScriptPath()
    {
        string primary = Path.Combine(AppContext.BaseDirectory, "install", "linux", "install-jackllm-cuda-pytorch.sh");
        if (File.Exists(primary))
            return primary;

        string tools = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tools", "linux", "install-jackllm-cuda-pytorch.sh"));
        if (File.Exists(tools))
            return tools;

        return primary;
    }

    public static string BuildLinuxCudaPytorchInstallCommand(string? pythonExecutable = null, string? torchIndexUrl = null, string? torchVersion = null)
        => BuildLinuxCudaPytorchInstallCommand(pythonExecutable, torchIndexUrl, torchVersion, ResolveLinuxCudaPytorchInstallRoot());

    private static string BuildLinuxCudaPytorchInstallCommand(string? pythonExecutable, string? torchIndexUrl, string? torchVersion, string appRoot)
    {
        var builder = new StringBuilder("bash ");
        builder.Append(ShellQuote(ResolveLinuxCudaPytorchInstallScriptPath()));
        if (!string.IsNullOrWhiteSpace(appRoot))
        {
            builder.Append(" --app-root ");
            builder.Append(ShellQuote(appRoot.Trim()));
        }

        if (!string.IsNullOrWhiteSpace(pythonExecutable))
        {
            builder.Append(" --python ");
            builder.Append(ShellQuote(pythonExecutable.Trim()));
        }

        string indexUrl = FirstNonEmpty(torchIndexUrl, Environment.GetEnvironmentVariable("JACKONNX_TORCH_CUDA_INDEX_URL"), DefaultLinuxPytorchCudaIndexUrl);
        builder.Append(" --torch-index-url ");
        builder.Append(ShellQuote(indexUrl));

        if (!string.IsNullOrWhiteSpace(torchVersion))
        {
            builder.Append(" --torch-version ");
            builder.Append(ShellQuote(torchVersion.Trim()));
        }

        return builder.ToString();
    }

    private static string ResolveLinuxCudaPytorchInstallRoot()
    {
        string pythonHome = Environment.GetEnvironmentVariable("JACKONNX_PYTHON_HOME") ?? "";
        if (!string.IsNullOrWhiteSpace(pythonHome))
        {
            try
            {
                string fullPythonHome = Path.GetFullPath(Environment.ExpandEnvironmentVariables(pythonHome.Trim()));
                string? parent = Path.GetDirectoryName(fullPythonHome);
                if (!string.IsNullOrWhiteSpace(parent))
                    return parent;
            }
            catch
            {
            }
        }

        string dataRoot = Environment.GetEnvironmentVariable("JACKLLM_DATA_ROOT") ?? "";
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            try
            {
                return Path.GetFullPath(Environment.ExpandEnvironmentVariables(dataRoot.Trim()));
            }
            catch
            {
            }
        }

        return AppContext.BaseDirectory;
    }

    public static LlmRuntimeCompatibilityCatalog BuildCatalog(LlmRuntimeCompatibilityConfig? config = null)
    {
        config ??= new LlmRuntimeCompatibilityConfig();
        var gpus = LlmRuntimeCompatibilityDefaults.Gpus()
            .Concat(config.AdvancedGpuCatalog ?? [])
            .ToList();

        foreach (LlmGpuAliasOverride alias in config.ExtraGpuAliases ?? [])
        {
            if (string.IsNullOrWhiteSpace(alias.Name) || string.IsNullOrWhiteSpace(alias.ComputeCapability))
                continue;
            gpus.Add(new LlmCudaGpuCatalogEntry
            {
                Name = alias.Name,
                ComputeCapability = alias.ComputeCapability,
                Aliases = alias.Aliases ?? [],
                Source = "user"
            });
        }

        var releases = LlmRuntimeCompatibilityDefaults.PytorchReleases()
            .Concat(config.AdvancedPytorchReleases ?? [])
            .GroupBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderByDescending(release => ParseVersionForOrdering(release.Version))
            .ToList();

        return new LlmRuntimeCompatibilityCatalog
        {
            Gpus = gpus,
            PytorchReleases = releases
        };
    }

    public static bool IsInstalledTorchCompatible(
        LlmRuntimeCompatibilityCatalog catalog,
        LlmPythonRuntimeStatus python,
        string computeCapability,
        LlmRuntimeCompatibilityConfig? config = null)
    {
        if (catalog == null || python == null || string.IsNullOrWhiteSpace(python.TorchVersion) ||
            string.IsNullOrWhiteSpace(python.TorchCudaVersion) ||
            !TryParsePythonVersion(python.Version, out Version? pyVersion))
        {
            return false;
        }

        config ??= new LlmRuntimeCompatibilityConfig();
        string effectiveComputeCapability = FirstNonEmpty(computeCapability, python.TorchCudaDeviceCapability);
        if ((python.TorchCudaArchList?.Count ?? 0) > 0 &&
            !TorchArchListSupportsComputeCapability(python.TorchCudaArchList, effectiveComputeCapability))
            return false;
        if (python.TorchCudaAvailable)
            return true;

        string torchMajorMinor = MajorMinor(python.TorchVersion);
        string cudaMajorMinor = MajorMinor(python.TorchCudaVersion);
        foreach (LlmPytorchReleaseCatalogEntry release in catalog.PytorchReleases)
        {
            if (!string.Equals(MajorMinor(release.Version), torchMajorMinor, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!release.SupportsPython(pyVersion!))
                continue;

            IEnumerable<LlmPytorchCudaWheel> wheels = release.StableCudaWheels;
            if (config.AllowExperimentalCuda)
                wheels = wheels.Concat(release.ExperimentalCudaWheels);
            foreach (LlmPytorchCudaWheel wheel in wheels)
            {
                if (string.Equals(MajorMinor(wheel.CudaVersion), cudaMajorMinor, StringComparison.OrdinalIgnoreCase) &&
                    wheel.SupportsComputeCapability(effectiveComputeCapability))
                    return true;
            }
        }

        return false;
    }

    private static bool TorchArchListSupportsComputeCapability(IReadOnlyList<string>? archList, string computeCapability)
    {
        if ((archList?.Count ?? 0) == 0 || string.IsNullOrWhiteSpace(computeCapability))
            return true;
        if (!TryParseCapability(computeCapability, out int gpuMajor, out int gpuMinor))
            return false;

        foreach (string arch in archList ?? [])
        {
            string clean = (arch ?? "")
                .Replace("sm_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("compute_", "", StringComparison.OrdinalIgnoreCase)
                .Replace("_", ".", StringComparison.OrdinalIgnoreCase)
                .Trim();
            if (clean.Length == 2 && clean.All(char.IsDigit))
                clean = clean[0] + "." + clean[1];
            if (!TryParseCapability(clean, out int supportedMajor, out int supportedMinor))
                continue;
            if (gpuMajor == supportedMajor && gpuMinor >= supportedMinor)
                return true;
        }

        return false;
    }

    private static void PopulateGpuCatalogMatch(LlmDetectedGpu gpu, LlmRuntimeCompatibilityCatalog catalog)
    {
        if (gpu == null || catalog == null)
            return;
        if (!string.IsNullOrWhiteSpace(gpu.ComputeCapability))
        {
            gpu.IsCudaSupported = true;
            return;
        }

        LlmCudaGpuCatalogEntry? match = catalog.MatchGpu(gpu.Name);
        if (match == null)
            return;

        gpu.CatalogName = match.Name;
        gpu.ComputeCapability = match.ComputeCapability;
        gpu.IsCudaSupported = true;
    }

    private string ResolvePythonExecutable(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
            return requested.Trim();
        string fromEnvironment = Environment.GetEnvironmentVariable("JACKONNX_PYTHON") ?? "";
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();
        string pythonHome = Environment.GetEnvironmentVariable("JACKONNX_PYTHON_HOME") ?? "";
        if (!string.IsNullOrWhiteSpace(pythonHome))
        {
            foreach (string candidate in EnumeratePythonExecutablesUnderRoot(pythonHome.Trim()))
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        string dataRoot = Environment.GetEnvironmentVariable("JACKLLM_DATA_ROOT") ?? "";
        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            foreach (string candidate in EnumeratePythonExecutablesUnderRoot(Path.Combine(dataRoot.Trim(), "Python")))
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        string cudaLegacyRoot = Path.Combine(AppContext.BaseDirectory, "PythonCudaLegacy");
        foreach (string candidate in EnumeratePythonExecutablesUnderRoot(cudaLegacyRoot))
        {
            if (File.Exists(candidate))
                return candidate;
        }
        string bundled = Path.Combine(AppContext.BaseDirectory, "Python", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python.exe" : "bin/python3");
        if (File.Exists(bundled))
            return bundled;
        bundled = Path.Combine(AppContext.BaseDirectory, "Python", "bin", "python");
        if (File.Exists(bundled))
            return bundled;
        return "";
    }

    private static IEnumerable<string> EnumeratePythonExecutablesUnderRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            yield break;

        string expanded;
        try
        {
            expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
        }
        catch
        {
            expanded = root;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return Path.Combine(expanded, "python.exe");
            yield return Path.Combine(expanded, "Scripts", "python.exe");
        }
        else
        {
            yield return Path.Combine(expanded, "bin", "python3");
            yield return Path.Combine(expanded, "bin", "python");
        }
    }

    private static string BuildStatusMessage(
        bool gpuDetected,
        bool hasCudaDriver,
        LlmPythonRuntimeStatus python,
        bool torchCompatible,
        LlmPytorchPackageRecommendation? recommendation,
        IReadOnlyList<string> missingCudaDependencies,
        bool legacyCudaRepairAvailable)
    {
        if (!gpuDetected)
            return "No NVIDIA CUDA GPU was detected. Local image generation should remain disabled instead of falling back silently to CPU.";
        if (!hasCudaDriver)
            return "NVIDIA GPU was detected, but the CUDA driver runtime is unavailable. Install the NVIDIA driver/CUDA toolkit.";
        if (missingCudaDependencies.Count > 0)
            return "CUDA GPU detected, but CUDA/cuDNN native dependencies are missing: " + string.Join(", ", missingCudaDependencies) + ".";
        if (!python.IsAvailable)
            return "Python runtime is unavailable: " + python.Error;
        if (!python.HasTorch)
            return recommendation == null
                ? legacyCudaRepairAvailable
                    ? "Python is available, but this GPU/Python pair needs JackLLM's legacy CUDA Python runtime. Use Repair PyTorch to install Python " + WindowsLegacyCudaPythonVersion + " with CUDA-enabled torch 2.1/cu118."
                    : "Python is available, but PyTorch is not installed in JackLLM's image-generation Python environment. CUDA was detected, but JackLLM could not automatically select a matching PyTorch CUDA wheel for this Python/GPU pair."
                : "Python is available, but PyTorch is not installed in JackLLM's image-generation Python environment. Use Repair PyTorch to install CUDA-enabled PyTorch.";
        if (string.IsNullOrWhiteSpace(python.TorchCudaVersion))
            return "Python has a CPU-only PyTorch build. Use Repair PyTorch to install a compatible CUDA wheel.";
        if (!python.TorchCudaAvailable)
            return "Python has PyTorch CUDA " + python.TorchCudaVersion + ", but torch.cuda.is_available() is false.";
        if (!torchCompatible)
        {
            if (!string.IsNullOrWhiteSpace(python.TorchCudaDeviceCapability) &&
                (python.TorchCudaArchList?.Count ?? 0) > 0 &&
                !TorchArchListSupportsComputeCapability(python.TorchCudaArchList, python.TorchCudaDeviceCapability))
            {
                return "Python has PyTorch CUDA " + python.TorchCudaVersion +
                       ", but this build does not include kernels for GPU capability sm_" +
                       python.TorchCudaDeviceCapability.Replace(".", "", StringComparison.OrdinalIgnoreCase) +
                        ". Supported by this torch install: " + string.Join(", ", python.TorchCudaArchList ?? []) + ".";
            }
            return "Installed PyTorch/CUDA does not match this Python/GPU compatibility catalog. Use Repair PyTorch.";
        }
        return "CUDA-enabled PyTorch is compatible with this GPU.";
    }

    private static bool CanRepairWithWindowsLegacyCudaPython(LlmRuntimeCompatibilityStatus status)
    {
        if (status == null)
            return false;
        return CanRepairWithWindowsLegacyCudaPython(
            status.Diagnostics.Gpus,
            status.Diagnostics.HasCudaDriver,
            status.Config);
    }

    private static bool CanRepairWithWindowsLegacyCudaPython(
        IReadOnlyList<LlmDetectedGpu> gpus,
        bool hasCudaDriver,
        LlmRuntimeCompatibilityConfig config)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || !hasCudaDriver || config.AllowPytorchRepair == false)
            return false;

        LlmDetectedGpu? bestGpu = (gpus ?? [])
            .Where(gpu => gpu.IsNvidia)
            .OrderByDescending(gpu => ParseCapability(gpu.ComputeCapability))
            .FirstOrDefault();
        if (bestGpu == null || !TryParseCapability(bestGpu.ComputeCapability, out int major, out int minor))
            return false;

        if (major < 5)
            return false;
        return major < 7 || (major == 7 && minor < 5);
    }

    private static string BuildProcessDetail(LlmProcessRunResult run)
    {
        string text = ((run.StandardError ?? "") + " " + (run.StandardOutput ?? "")).Trim();
        return string.IsNullOrWhiteSpace(text)
            ? "python exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + "."
            : "python exited with code " + run.ExitCode.ToString(CultureInfo.InvariantCulture) + ". " + text;
    }

    private static async Task<LlmProcessRunResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
                output.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                error.AppendLine(args.Data);
        };

        if (!process.Start())
            throw new InvalidOperationException("Unable to start process: " + fileName);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new TimeoutException(fileName + " did not finish within " + timeout.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture) + " minutes.");
        }

        return new LlmProcessRunResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = output.ToString(),
            StandardError = error.ToString()
        };
    }

    private static void TryKillProcess(Process process)
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

    private static string ShellQuote(string value) =>
        "'" + (value ?? "").Replace("'", "'\"'\"'", StringComparison.OrdinalIgnoreCase) + "'";

    internal static bool TryParsePythonVersion(string value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var match = System.Text.RegularExpressions.Regex.Match(value, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
            return false;
        int major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        int minor = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    private static Version ParseVersionForOrdering(string value)
    {
        TryParsePythonVersion(value, out Version? version);
        return version ?? new Version(0, 0);
    }

    private static string MajorMinor(string value)
    {
        if (!TryParsePythonVersion(value, out Version? version) || version == null)
            return value.Trim();
        return version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture);
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private static bool TryParseCapability(string value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        string[] parts = (value ?? "").Split('.', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            return false;
        if (parts.Length > 1)
            int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
        return true;
    }

    private static double ParseCapability(string value)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
            return result;
        return 0;
    }
}

public sealed class LlmRuntimeCompatibilityConfig
{
    public int Version { get; set; } = 1;

    public string Mode { get; set; } = "simple";

    public string PreferredPytorchVersion { get; set; } = "";

    public string PreferredCudaVersion { get; set; } = "";

    public bool AllowExperimentalCuda { get; set; }

    public bool AllowPytorchRepair { get; set; } = true;

    public List<LlmGpuAliasOverride> ExtraGpuAliases { get; set; } = [];

    public List<LlmCudaGpuCatalogEntry> AdvancedGpuCatalog { get; set; } = [];

    public List<LlmPytorchReleaseCatalogEntry> AdvancedPytorchReleases { get; set; } = [];
}

public sealed class LlmRuntimeCompatibilityStatus
{
    public string Status { get; init; } = "";

    public string Message { get; init; } = "";

    public bool GenerationDisabled { get; init; }

    public bool RequiresPytorchRepair { get; init; }

    public bool IsGpuGenerationEnabled { get; init; }

    public string ConfigPath { get; init; } = "";

    public string CudaToolkitDownloadUrl { get; init; } = LlmRuntimeCompatibilityService.CudaToolkitDownloadUrl;

    public string LinuxCudaPytorchInstallScriptPath { get; init; } = "";

    public string LinuxCudaPytorchInstallCommand { get; init; } = "";

    public LlmRuntimeCompatibilityConfig Config { get; init; } = new();

    public LlmRuntimeCompatibilityDiagnostics Diagnostics { get; init; } = new();

    public IReadOnlyList<LlmRuntimeCompatibilityAction> Actions { get; init; } = [];
}

public sealed class LlmRuntimeCompatibilityDiagnostics
{
    public IReadOnlyList<LlmDetectedGpu> Gpus { get; init; } = [];

    public bool HasCudaDriver { get; init; }

    public IReadOnlyList<string> MissingCudaDependencies { get; init; } = [];

    public LlmPythonRuntimeStatus Python { get; init; } = new();

    public LlmPytorchPackageRecommendation? RecommendedPytorch { get; init; }
}

public sealed class LlmRuntimeCompatibilityAction
{
    public string Id { get; init; } = "";

    public string Label { get; init; } = "";

    public string Kind { get; init; } = "";

    public string Url { get; init; } = "";

    public string Endpoint { get; init; } = "";

    public string Command { get; init; } = "";

    public bool Enabled { get; init; }

    public string Detail { get; init; } = "";
}

public sealed class LlmPytorchRepairResult
{
    public string Status { get; init; } = "";

    public string PythonExecutable { get; init; } = "";

    public string PytorchVersion { get; init; } = "";

    public string TorchCudaVersion { get; init; } = "";

    public string IndexUrl { get; init; } = "";

    public string Message { get; init; } = "";

    public LlmRuntimeCompatibilityStatus? Compatibility { get; init; }
}

public sealed class LlmLinuxCudaPytorchInstallResult
{
    public string Status { get; init; } = "";

    public string ScriptPath { get; init; } = "";

    public string Command { get; init; } = "";

    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = "";

    public string StandardError { get; init; } = "";

    public string Message { get; init; } = "";

    public LlmRuntimeCompatibilityStatus? Compatibility { get; init; }
}

public sealed class LlmRuntimeCompatibilityCatalog
{
    public IReadOnlyList<LlmCudaGpuCatalogEntry> Gpus { get; init; } = [];

    public IReadOnlyList<LlmPytorchReleaseCatalogEntry> PytorchReleases { get; init; } = [];

    public LlmCudaGpuCatalogEntry? MatchGpu(string name)
    {
        string normalized = LlmCompatibilityText.NormalizeGpuName(name);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        return Gpus.FirstOrDefault(entry =>
            entry.AllNames().Any(candidate =>
                string.Equals(normalized, LlmCompatibilityText.NormalizeGpuName(candidate), StringComparison.OrdinalIgnoreCase)));
    }

    public LlmPytorchPackageRecommendation? RecommendPytorch(Version pythonVersion, string computeCapability, LlmRuntimeCompatibilityConfig config)
    {
        config ??= new LlmRuntimeCompatibilityConfig();
        foreach (LlmPytorchReleaseCatalogEntry release in PytorchReleases.OrderByDescending(release => LlmRuntimeCompatibilityService.TryParsePythonVersion(release.Version, out Version? v) ? v : new Version(0, 0)))
        {
            if (!string.IsNullOrWhiteSpace(config.PreferredPytorchVersion) &&
                !string.Equals(release.Version, config.PreferredPytorchVersion, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!release.SupportsPython(pythonVersion))
                continue;

            IEnumerable<LlmPytorchCudaWheel> wheels = release.StableCudaWheels;
            if (config.AllowExperimentalCuda)
                wheels = wheels.Concat(release.ExperimentalCudaWheels);

            foreach (LlmPytorchCudaWheel wheel in wheels.OrderByDescending(wheel => wheel.CudaVersion, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(config.PreferredCudaVersion) &&
                    !string.Equals(LlmCompatibilityText.NormalizeCudaVersion(wheel.CudaVersion), LlmCompatibilityText.NormalizeCudaVersion(config.PreferredCudaVersion), StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!wheel.SupportsComputeCapability(computeCapability))
                    continue;

                return new LlmPytorchPackageRecommendation
                {
                    PytorchVersion = release.Version,
                    CudaVersion = wheel.CudaVersion,
                    PythonMinimum = release.PythonMinimum,
                    PythonMaximum = release.PythonMaximum,
                    IndexUrl = wheel.IndexUrl,
                    IsExperimental = release.ExperimentalCudaWheels.Contains(wheel)
                };
            }
        }

        return null;
    }
}

public sealed class LlmCudaGpuCatalogEntry
{
    public string Name { get; set; } = "";

    public string ComputeCapability { get; set; } = "";

    public List<string> Aliases { get; set; } = [];

    public bool Legacy { get; set; }

    public string Source { get; set; } = "bundled";

    public IEnumerable<string> AllNames()
    {
        if (!string.IsNullOrWhiteSpace(Name))
            yield return Name;
        foreach (string alias in Aliases ?? [])
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias;
    }
}

public sealed class LlmGpuAliasOverride
{
    public string Name { get; set; } = "";

    public string ComputeCapability { get; set; } = "";

    public List<string> Aliases { get; set; } = [];
}

public sealed class LlmPytorchReleaseCatalogEntry
{
    public string Version { get; set; } = "";

    public string PythonMinimum { get; set; } = "";

    public string PythonMaximum { get; set; } = "";

    public List<LlmPytorchCudaWheel> StableCudaWheels { get; set; } = [];

    public List<LlmPytorchCudaWheel> ExperimentalCudaWheels { get; set; } = [];

    public bool SupportsPython(Version version)
    {
        if (version == null)
            return false;
        if (LlmRuntimeCompatibilityService.TryParsePythonVersion(PythonMinimum, out Version? min) && min != null &&
            CompareMajorMinor(version, min) < 0)
            return false;
        if (LlmRuntimeCompatibilityService.TryParsePythonVersion(PythonMaximum, out Version? max) && max != null &&
            CompareMajorMinor(version, max) > 0)
            return false;
        return true;
    }

    private static int CompareMajorMinor(Version left, Version right)
    {
        int major = left.Major.CompareTo(right.Major);
        return major != 0 ? major : left.Minor.CompareTo(right.Minor);
    }
}

public sealed class LlmPytorchCudaWheel
{
    public string CudaVersion { get; set; } = "";

    public string IndexUrl { get; set; } = "";

    public List<string> SupportedComputeCapabilities { get; set; } = [];

    public bool SupportsComputeCapability(string computeCapability)
    {
        if (string.IsNullOrWhiteSpace(computeCapability))
            return false;
        if (!TryParseCapability(computeCapability, out int gpuMajor, out int gpuMinor))
            return false;

        foreach (string supported in SupportedComputeCapabilities ?? [])
        {
            string clean = supported.Replace("+PTX", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (!TryParseCapability(clean, out int major, out int minor))
                continue;
            if (gpuMajor == major && gpuMinor >= minor)
                return true;
        }

        return false;
    }

    private static bool TryParseCapability(string value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        string[] parts = (value ?? "").Split('.', 2, StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out major))
            return false;
        if (parts.Length > 1)
            int.TryParse(new string(parts[1].TakeWhile(char.IsDigit).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out minor);
        return true;
    }
}

public sealed class LlmPytorchPackageRecommendation
{
    public string PytorchVersion { get; init; } = "";

    public string CudaVersion { get; init; } = "";

    public string PythonMinimum { get; init; } = "";

    public string PythonMaximum { get; init; } = "";

    public string IndexUrl { get; init; } = "";

    public bool IsExperimental { get; init; }
}

public sealed class LlmDetectedGpu
{
    public string DeviceId { get; set; } = "";

    public string Name { get; set; } = "";

    public bool IsNvidia { get; set; } = true;

    public string ComputeCapability { get; set; } = "";

    public string CatalogName { get; set; } = "";

    public bool IsCudaSupported { get; set; }

    public long? DedicatedMemoryBytes { get; set; }

    public long? MemoryUsedBytes { get; set; }

    public long? MemoryTotalBytes { get; set; }

    public double? GpuUsagePercent { get; set; }

    public double? VramUsagePercent =>
        MemoryTotalBytes.GetValueOrDefault() > 0
            ? Math.Clamp(MemoryUsedBytes.GetValueOrDefault() * 100.0 / MemoryTotalBytes.GetValueOrDefault(), 0, 100)
            : null;

    public string Detail { get; set; } = "";
}

public sealed class LlmPythonRuntimeStatus
{
    public string ExecutablePath { get; set; } = "";

    public bool IsAvailable { get; set; }

    public string Version { get; set; } = "";

    public bool HasTorch { get; set; }

    public string TorchVersion { get; set; } = "";

    public string TorchCudaVersion { get; set; } = "";

    public bool TorchCudaAvailable { get; set; }

    public string TorchCudaDeviceName { get; set; } = "";

    public string TorchCudaDeviceCapability { get; set; } = "";

    public List<string> TorchCudaArchList { get; set; } = [];

    public string Error { get; set; } = "";
}

public sealed class LlmProcessRunResult
{
    public int ExitCode { get; init; }

    public string StandardOutput { get; init; } = "";

    public string StandardError { get; init; } = "";
}

public interface ILlmRuntimeCompatibilityProbe
{
    IReadOnlyList<LlmDetectedGpu> DetectNvidiaGpus(CancellationToken cancellationToken);

    bool HasCudaDriver(CancellationToken cancellationToken);

    IReadOnlyList<string> FindMissingCudaDependencies();

    LlmPythonRuntimeStatus InspectPython(string pythonExecutable, CancellationToken cancellationToken);

    Task<LlmProcessRunResult> RunPythonAsync(string pythonExecutable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

internal sealed class DefaultLlmRuntimeCompatibilityProbe : ILlmRuntimeCompatibilityProbe
{
    private static IReadOnlyList<string> RequiredCudaNativeLibraries =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ["libcudart.so", "libcublas.so", "libcublasLt.so", "libcufft.so", "libcudnn.so"]
            : ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "cudnn64_9.dll"];

    public IReadOnlyList<LlmDetectedGpu> DetectNvidiaGpus(CancellationToken cancellationToken)
    {
        string output = RunProcess("nvidia-smi", "--query-gpu=name,compute_cap,memory.total,memory.used,utilization.gpu --format=csv,noheader,nounits", TimeSpan.FromSeconds(2), cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            output = RunProcess("nvidia-smi", "--query-gpu=name,memory.total --format=csv,noheader,nounits", TimeSpan.FromSeconds(2), cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
            return [];

        var gpus = new List<LlmDetectedGpu>();
        foreach (string line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                continue;
            string computeCapability = parts.Length > 2 ? parts[1] : "";
            string memoryText = parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : "";
            long? memory = ParseMibToBytes(memoryText);
            long? memoryUsed = parts.Length > 3 ? ParseMibToBytes(parts[3]) : null;
            double? gpuUsagePercent = parts.Length > 4 ? ParsePercent(parts[4]) : null;
            gpus.Add(new LlmDetectedGpu
            {
                DeviceId = "cuda:" + gpus.Count.ToString(CultureInfo.InvariantCulture),
                Name = parts[0],
                ComputeCapability = NormalizeComputeCapability(computeCapability),
                IsNvidia = true,
                IsCudaSupported = true,
                DedicatedMemoryBytes = memory,
                MemoryTotalBytes = memory,
                MemoryUsedBytes = memoryUsed,
                GpuUsagePercent = gpuUsagePercent,
                Detail = "Reported by nvidia-smi."
            });
        }

        return gpus;
    }

    public bool HasCudaDriver(CancellationToken cancellationToken)
    {
        if (TryLoadNativeLibrary(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["nvcuda.dll"] : ["libcuda.so.1", "libcuda.so"]))
            return true;
        return DetectNvidiaGpus(cancellationToken).Count > 0;
    }

    public IReadOnlyList<string> FindMissingCudaDependencies()
    {
        var missing = new List<string>();
        foreach (string library in RequiredCudaNativeLibraries)
            if (!CanFindNativeLibrary(library, EnumerateCudaSearchDirectories()))
                missing.Add(library);
        return missing;
    }

    public LlmPythonRuntimeStatus InspectPython(string pythonExecutable, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pythonExecutable))
            return new LlmPythonRuntimeStatus { ExecutablePath = pythonExecutable ?? "", IsAvailable = false, Error = "Python executable was not found." };
        if (!File.Exists(pythonExecutable) && LooksLikeFilePath(pythonExecutable))
            return new LlmPythonRuntimeStatus { ExecutablePath = pythonExecutable, IsAvailable = false, Error = "Python executable was not found." };

        const string code = """
import json
import sys
payload = {
    "available": True,
    "version": ".".join(str(x) for x in sys.version_info[:3]),
    "has_torch": False,
    "torch_version": "",
    "torch_cuda": "",
    "torch_cuda_available": False,
    "torch_cuda_device_name": "",
    "torch_cuda_device_capability": "",
    "torch_cuda_arch_list": [],
}
try:
    import torch
    payload["has_torch"] = True
    payload["torch_version"] = str(getattr(torch, "__version__", "") or "")
    payload["torch_cuda"] = str(getattr(torch.version, "cuda", "") or "")
    payload["torch_cuda_available"] = bool(torch.cuda.is_available())
    try:
        payload["torch_cuda_arch_list"] = [str(item) for item in (torch.cuda.get_arch_list() or [])]
    except Exception:
        pass
    if payload["torch_cuda_available"]:
        try:
            payload["torch_cuda_device_name"] = str(torch.cuda.get_device_name(0) or "")
        except Exception:
            pass
        try:
            major, minor = torch.cuda.get_device_capability(0)
            payload["torch_cuda_device_capability"] = str(int(major)) + "." + str(int(minor))
        except Exception:
            pass
except Exception as exc:
    payload["torch_error"] = str(exc)
print(json.dumps(payload))
""";
        try
        {
            LlmProcessRunResult run = RunPythonAsync(pythonExecutable, ["-c", code], TimeSpan.FromMinutes(2), cancellationToken).GetAwaiter().GetResult();
            if (run.ExitCode != 0)
                return new LlmPythonRuntimeStatus { ExecutablePath = pythonExecutable, IsAvailable = false, Error = (run.StandardError + " " + run.StandardOutput).Trim() };
            using var document = JsonDocument.Parse(run.StandardOutput.Trim());
            JsonElement root = document.RootElement;
            return new LlmPythonRuntimeStatus
            {
                ExecutablePath = pythonExecutable,
                IsAvailable = ReadBool(root, "available"),
                Version = ReadString(root, "version"),
                HasTorch = ReadBool(root, "has_torch"),
                TorchVersion = ReadString(root, "torch_version"),
                TorchCudaVersion = ReadString(root, "torch_cuda"),
                TorchCudaAvailable = ReadBool(root, "torch_cuda_available"),
                TorchCudaDeviceName = ReadString(root, "torch_cuda_device_name"),
                TorchCudaDeviceCapability = ReadString(root, "torch_cuda_device_capability"),
                TorchCudaArchList = ReadStringArray(root, "torch_cuda_arch_list"),
                Error = ReadString(root, "torch_error")
            };
        }
        catch (Exception ex)
        {
            return new LlmPythonRuntimeStatus { ExecutablePath = pythonExecutable, IsAvailable = false, Error = ex.Message };
        }
    }

    public async Task<LlmProcessRunResult> RunPythonAsync(string pythonExecutable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        foreach (string argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };
        if (!process.Start())
            throw new InvalidOperationException("Could not start Python executable: " + pythonExecutable);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Python command exceeded " + timeout + ".");
        }

        return new LlmProcessRunResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = output.ToString(),
            StandardError = error.ToString()
        };
    }

    private static string RunProcess(string fileName, string arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            if (!process.Start())
                return "";
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                TryKill(process);
                return "";
            }
            cancellationToken.ThrowIfCancellationRequested();
            return process.ExitCode == 0 ? process.StandardOutput.ReadToEnd() : "";
        }
        catch
        {
            return "";
        }
    }

    private static bool LooksLikeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        return value.Contains(Path.DirectorySeparatorChar) ||
               value.Contains(Path.AltDirectorySeparatorChar) ||
               value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
               Path.IsPathRooted(value);
    }

    private static string NormalizeComputeCapability(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase))
            return "";
        return value;
    }

    private static long? ParseMibToBytes(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase))
            return null;
        string numeric = new(value.TakeWhile(character => char.IsDigit(character) || character == '.' || character == '-').ToArray());
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double mib) && mib >= 0
            ? (long)Math.Round(mib * 1024d * 1024d)
            : null;
    }

    private static double? ParsePercent(string value)
    {
        value = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Equals("[Not Supported]", StringComparison.OrdinalIgnoreCase))
            return null;
        string numeric = new(value.TakeWhile(character => char.IsDigit(character) || character == '.' || character == '-').ToArray());
        return double.TryParse(numeric, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
            ? Math.Clamp(percent, 0, 100)
            : null;
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True ||
        root.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed;

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<string>();
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string? text = item.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }
        }

        return result;
    }

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

    private static bool CanFindNativeLibrary(string fileName, IEnumerable<string> directories)
    {
        foreach (string directory in directories)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directory))
                    continue;

                string exact = Path.Combine(directory, fileName);
                if (File.Exists(exact))
                    return true;

                if (fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(directory) &&
                    Directory.EnumerateFiles(directory, fileName + "*").Any())
                    return true;
            }
            catch
            {
            }
        }
        return false;
    }

    private static IEnumerable<string> EnumerateCudaSearchDirectories()
    {
        string runtimeId = RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64")
            : (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64");

        foreach (string root in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory }.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string runtimesName in new[] { "Runtimes", "runtimes" })
            {
                yield return Path.Combine(root, runtimesName);
                yield return Path.Combine(root, runtimesName, "cuda");
                yield return Path.Combine(root, runtimesName, "cuda", runtimeId);
                yield return Path.Combine(root, runtimesName, "cuda", runtimeId, "dependencies");
                yield return Path.Combine(root, runtimesName, "cuda", runtimeId, "lib");
                yield return Path.Combine(root, runtimesName, "cuda", runtimeId, "lib64");
                yield return Path.Combine(root, runtimesName, runtimeId, "native");
            }

            yield return Path.Combine(root, "JackONNX", "providers", "cuda", runtimeId);
            yield return Path.Combine(root, "JackONNX", "providers", "cuda", runtimeId, "dependencies");
            yield return Path.Combine(root, "providers", "cuda", runtimeId);
            yield return Path.Combine(root, "providers", "cuda", runtimeId, "dependencies");
        }

        foreach (string variable in new[] { "JACKONNX_CUDA_DEPENDENCY_PATHS", "JACKONNX_CUDA_PATHS" })
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            foreach (string directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                yield return directory.Trim();
        }

        foreach (string variable in new[] { "LD_LIBRARY_PATH", "CUDA_PATH", "CUDA_HOME", "CUDA_ROOT", "CUDNN_PATH", "JACKONNX_CUDA_BIN", "JACKONNX_CUDA_PATH" })
        {
            string value = Environment.GetEnvironmentVariable(variable) ?? "";
            if (string.IsNullOrWhiteSpace(value))
                continue;

            foreach (string directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return directory;
                yield return Path.Combine(directory, "bin");
                yield return Path.Combine(directory, "lib", "x64");
                yield return Path.Combine(directory, "lib");
                yield return Path.Combine(directory, "lib64");
                yield return Path.Combine(directory, "targets", "x86_64-linux", "lib");
                yield return Path.Combine(directory, "targets", "aarch64-linux", "lib");
            }
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory.Trim();

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            if (Directory.Exists(cudaRoot))
            {
                foreach (string directory in Directory.EnumerateDirectories(cudaRoot).Select(path => Path.Combine(path, "bin")))
                    yield return directory;
            }
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            foreach (string directory in new[]
                     {
                         "/usr/local/cuda/lib64",
                         "/usr/local/cuda/lib",
                         "/usr/local/cuda/targets/x86_64-linux/lib",
                         "/usr/local/cuda/targets/aarch64-linux/lib",
                         "/usr/lib/x86_64-linux-gnu",
                         "/usr/lib/aarch64-linux-gnu",
                         "/usr/lib64",
                         "/usr/lib",
                         "/opt/cuda/lib64",
                         "/opt/cuda/lib",
                         "/usr/lib/wsl/lib"
                     })
            {
                yield return directory;
            }

            foreach (string directory in EnumerateLinuxPythonCudaPackageDirectories())
                yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateLinuxPythonCudaPackageDirectories()
    {
        foreach (string root in EnumerateLinuxPythonEnvironmentRoots())
        {
            foreach (string sitePackages in EnumerateSitePackageDirectories(root))
            {
                yield return Path.Combine(sitePackages, "torch", "lib");

                string nvidiaRoot = Path.Combine(sitePackages, "nvidia");
                if (!Directory.Exists(nvidiaRoot))
                    continue;

                IEnumerable<string> libraryDirectories;
                try
                {
                    libraryDirectories = Directory.EnumerateDirectories(nvidiaRoot, "lib", SearchOption.AllDirectories);
                }
                catch
                {
                    continue;
                }

                foreach (string directory in libraryDirectories)
                    yield return directory;
            }
        }
    }

    private static IEnumerable<string> EnumerateLinuxPythonEnvironmentRoots()
    {
        foreach (string root in new[]
                 {
                     Environment.GetEnvironmentVariable("JACKONNX_PYTHON_HOME") ?? "",
                     Path.Combine(Environment.GetEnvironmentVariable("JACKLLM_DATA_ROOT") ?? "", "Python"),
                     Path.Combine(AppContext.BaseDirectory, "Python"),
                     Path.Combine(AppContext.BaseDirectory, ".venv"),
                     Path.Combine(AppContext.BaseDirectory, "venv"),
                     Path.Combine(Environment.CurrentDirectory, "Python"),
                     Path.Combine(Environment.CurrentDirectory, ".venv"),
                     Path.Combine(Environment.CurrentDirectory, "venv")
                 })
        {
            if (!string.IsNullOrWhiteSpace(root))
                yield return root;
        }

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

    private static IEnumerable<string> EnumerateSitePackageDirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            yield break;

        foreach (string candidate in new[]
                 {
                     root,
                     Path.Combine(root, "site-packages"),
                     Path.Combine(root, "Lib", "site-packages")
                 })
        {
            if (Directory.Exists(candidate) && string.Equals(Path.GetFileName(candidate), "site-packages", StringComparison.OrdinalIgnoreCase))
                yield return candidate;
        }

        foreach (string libDirectory in new[] { Path.Combine(root, "lib"), Path.Combine(root, "lib64") })
        {
            if (!Directory.Exists(libDirectory))
                continue;

            IEnumerable<string> pythonDirectories;
            try
            {
                pythonDirectories = Directory.EnumerateDirectories(libDirectory, "python*");
            }
            catch
            {
                continue;
            }

            foreach (string pythonDirectory in pythonDirectories)
            {
                string sitePackages = Path.Combine(pythonDirectory, "site-packages");
                if (Directory.Exists(sitePackages))
                    yield return sitePackages;
            }
        }
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
}

internal static class LlmRuntimeCompatibilityDefaults
{
    public static IReadOnlyList<LlmCudaGpuCatalogEntry> Gpus() =>
    [
        Gpu("NVIDIA RTX PRO 6000 Blackwell", "12.0", "GeForce RTX 5090", "GeForce RTX 5080", "GeForce RTX 5070 Ti", "GeForce RTX 5070", "GeForce RTX 5060 Ti", "GeForce RTX 5060", "GeForce RTX 5050"),
        Gpu("NVIDIA GB200", "10.0", "NVIDIA B200", "NVIDIA GB300", "NVIDIA B300"),
        Gpu("NVIDIA GH200", "9.0", "NVIDIA H200", "NVIDIA H100"),
        Gpu("NVIDIA RTX 6000 Ada", "8.9", "NVIDIA RTX 5000 Ada", "NVIDIA RTX 4500 Ada", "NVIDIA RTX 4000 Ada", "NVIDIA RTX 2000 Ada", "GeForce RTX 4090", "GeForce RTX 4080", "GeForce RTX 4070 Ti", "GeForce RTX 4070", "GeForce RTX 4060 Ti", "GeForce RTX 4060", "GeForce RTX 4050"),
        Gpu("NVIDIA RTX A6000", "8.6", "NVIDIA RTX A5000", "NVIDIA RTX A4000", "NVIDIA RTX A3000", "NVIDIA RTX A2000", "NVIDIA A40", "NVIDIA A10", "NVIDIA A16", "NVIDIA A2", "GeForce RTX 3090 Ti", "GeForce RTX 3090", "GeForce RTX 3080 Ti", "GeForce RTX 3080", "GeForce RTX 3070 Ti", "GeForce RTX 3070", "GeForce RTX 3060 Ti", "GeForce RTX 3060", "GeForce RTX 3050 Ti", "GeForce RTX 3050"),
        Gpu("NVIDIA A100", "8.0", "NVIDIA A30"),
        Gpu("NVIDIA T4", "7.5", "NVIDIA TITAN RTX", "GeForce RTX 2080 Ti", "GeForce RTX 2080", "GeForce RTX 2070", "GeForce RTX 2060", "QUADRO RTX 8000", "QUADRO RTX 6000", "QUADRO RTX 5000", "QUADRO RTX 4000", "GeForce GTX 1650 Ti"),
        Gpu("Jetson AGX Xavier", "7.2", true, "Jetson Xavier NX"),
        Gpu("NVIDIA V100", "7.0", true, "Quadro GV100", "NVIDIA TITAN V"),
        Gpu("Jetson TX2", "6.2", true),
        Gpu("NVIDIA Tesla P40", "6.1", true, "Tesla P4", "Quadro P6000", "Quadro P5200", "Quadro P5000", "Quadro P4200", "Quadro P4000", "Quadro P3200", "Quadro P3000", "Quadro P2200", "Quadro P2000", "Quadro P1000", "Quadro P620", "Quadro P600", "Quadro P500", "Quadro P400", "P620", "P520", "NVIDIA TITAN Xp", "NVIDIA TITAN X", "GeForce GTX 1080 Ti", "GeForce GTX 1080", "GeForce GTX 1070 Ti", "GeForce GTX 1070", "GeForce GTX 1060", "GeForce GTX 1050"),
        Gpu("NVIDIA Tesla P100", "6.0", true, "Quadro GP100"),
        Gpu("Jetson Nano", "5.3", true),
        Gpu("NVIDIA Tesla M60", "5.2", true, "Tesla M40", "Quadro M6000 24GB", "Quadro M6000", "Quadro M5000", "Quadro M4000", "Quadro M2000", "Quadro M5500M", "Quadro M2200", "Quadro M620", "GeForce GTX TITAN X", "GeForce GTX 980 Ti", "GeForce GTX 980", "GeForce GTX 970", "GeForce GTX 960", "GeForce GTX 950", "GeForce GTX 980M", "GeForce GTX 970M", "GeForce GTX 965M", "GeForce 910M"),
        Gpu("NVIDIA Quadro K2200", "5.0", true, "Quadro K1200", "Quadro K620", "Quadro M1200", "Quadro M520", "Quadro M5000M", "Quadro M4000M", "Quadro M3000M", "Quadro M2000M", "Quadro M1000M", "Quadro K620M", "Quadro M600M", "Quadro M500M", "NVIDIA NVS 810", "GeForce GTX 750 Ti", "GeForce GTX 750", "GeForce GTX 960M", "GeForce GTX 950M", "GeForce 940M", "GeForce 930M", "GeForce GTX 850M", "GeForce 840M", "GeForce 830M"),
        Gpu("NVIDIA Tesla K80", "3.7", true),
        Gpu("NVIDIA Tesla K40", "3.5", true, "Tesla K20", "Quadro K6000", "Quadro K5200", "Quadro K610M", "Quadro K510M", "GeForce GTX TITAN Z", "GeForce GTX TITAN Black", "GeForce GTX TITAN", "GeForce GTX 780 Ti", "GeForce GTX 780", "GeForce GT 730", "GeForce GT 720", "GeForce GT 705", "GeForce GT 640 GDDR5", "GeForce 920M"),
        Gpu("Tegra K1", "3.2", true, "Jetson TK1"),
        Gpu("NVIDIA Tesla K10", "3.0", true, "Quadro K5000", "Quadro K4200", "Quadro K4000", "Quadro K2000", "Quadro K2000D", "Quadro K600", "Quadro K420", "Quadro 410", "Quadro K6000M", "Quadro K5200M", "Quadro K5100M", "Quadro K500M", "Quadro K4200M", "Quadro K4100M", "Quadro K3100M", "Quadro K2200M", "Quadro K2100M", "Quadro K1100M", "NVIDIA NVS 510", "GeForce GTX 770", "GeForce GTX 760", "GeForce GTX 690", "GeForce GTX 680", "GeForce GTX 670", "GeForce GTX 660 Ti", "GeForce GTX 660", "GeForce GTX 650 Ti BOOST", "GeForce GTX 650 Ti", "GeForce GTX 650", "GeForce GT 740", "GeForce GTX 880M", "GeForce GTX 870M", "GeForce GTX 780M", "GeForce GTX 770M", "GeForce GTX 765M", "GeForce GTX 760M", "GeForce GTX 680MX", "GeForce GTX 680M", "GeForce GTX 675MX", "GeForce GTX 670MX", "GeForce GTX 660M", "GeForce GT 755M", "GeForce GT 750M", "GeForce GT 650M", "GeForce GT 745M", "GeForce GT 645M", "GeForce GT 740M", "GeForce GT 730M", "GeForce GT 640M", "GeForce GT 640M LE", "GeForce GT 735M"),
        Gpu("NVIDIA NVS 315", "2.1", true, "NVIDIA NVS 310", "NVS 5400M", "NVS 5200M", "NVS 4200M", "Quadro 2000", "Quadro 2000D", "Quadro 600", "Quadro 4000M", "Quadro 3000M", "Quadro 2000M", "Quadro 1000M", "GeForce GTX 560 Ti", "GeForce GTX 550 Ti", "GeForce GTX 460", "GeForce GTS 450", "GeForce GT 730 DDR3", "GeForce GT 640 GDDR3", "GeForce GT 630", "GeForce GT 620", "GeForce GT 610", "GeForce GT 520", "GeForce GT 440", "GeForce GT 430", "GeForce 820M", "GeForce 800M", "GeForce GTX 675M", "GeForce GTX 670M", "GeForce GT 635M", "GeForce GT 630M", "GeForce GT 625M", "GeForce GT 720M", "GeForce GT 620M", "GeForce 710M", "GeForce 705M", "GeForce 610M", "GeForce GTX 580M", "GeForce GTX 570M", "GeForce GTX 560M", "GeForce GT 555M", "GeForce GT 550M", "GeForce GT 540M", "GeForce GT 525M", "GeForce GT 520MX", "GeForce GT 520M", "GeForce GTX 485M", "GeForce GTX 470M", "GeForce GTX 460M", "GeForce GT 445M", "GeForce GT 435M", "GeForce GT 420M", "GeForce GT 415M", "GeForce 410M"),
        Gpu("NVIDIA Tesla C2075", "2.0", true, "Tesla C2050", "Tesla C2070", "Tesla M2050", "Tesla M2070", "Tesla M2075", "Tesla M2090", "Quadro Plex 7000", "Quadro 6000", "Quadro 5000", "Quadro 4000", "Quadro 4000 for Mac", "Quadro 5010M", "Quadro 5000M", "GeForce GTX 590", "GeForce GTX 580", "GeForce GTX 570", "GeForce GTX 480", "GeForce GTX 470", "GeForce GTX 465", "GeForce GTX 480M"),
        Gpu("NVIDIA Tesla C1060", "1.3", true, "Tesla S1070", "Tesla M1060", "Quadro FX 5800", "Quadro FX 4800", "Quadro FX 4800 for Mac", "Quadro FX 3800", "Quadro CX", "Quadro Plex 2200 D2", "GeForce GTX 295", "GeForce GTX 285", "GeForce GTX 285 for Mac", "GeForce GTX 280", "GeForce GTX 275", "GeForce GTX 260"),
        Gpu("NVIDIA Quadro 400", "1.2", true, "Quadro FX 380 Low Profile", "NVIDIA NVS 300", "Quadro FX 1800M", "Quadro FX 880M", "Quadro FX 380M", "NVS 5100M", "NVS 3100M", "NVS 2100M", "GeForce GT 240", "GeForce GT 220", "GeForce 210", "GeForce GTS 360M", "GeForce GTS 350M", "GeForce GT 335M", "GeForce GT 330M", "GeForce GT 325M", "GeForce GT 240M", "GeForce G210M", "GeForce 310M", "GeForce 305M"),
        Gpu("NVIDIA Quadro FX 4700 X2", "1.1", true, "Quadro FX 3700", "Quadro FX 1800", "Quadro FX 580", "Quadro FX 570", "Quadro FX 470", "Quadro FX 380", "Quadro FX 370", "Quadro FX 370 Low Profile", "Quadro NVS 450", "Quadro NVS 420", "Quadro NVS 295", "Quadro Plex 2100 D4", "Quadro FX 3800M", "Quadro FX 3700M", "Quadro FX 3600M", "Quadro FX 2800M", "Quadro FX 2700M", "Quadro FX 1700M", "Quadro FX 1600M", "Quadro FX 770M", "Quadro FX 570M", "Quadro FX 370M", "Quadro FX 360M", "Quadro NVS 320M", "Quadro NVS 160M", "Quadro NVS 150M", "Quadro NVS 140M", "Quadro NVS 135M", "Quadro NVS 130M", "GeForce GTS 250", "GeForce GTS 150", "GeForce GT 130", "GeForce GT 120", "GeForce G100", "GeForce 9800 GX2", "GeForce 9800 GTX+", "GeForce 9800 GTX", "GeForce 9600 GSO", "GeForce 9500 GT", "GeForce 8800 GTS", "GeForce 8800 GT", "GeForce 8800 GS", "GeForce 8600 GTS", "GeForce 8600 GT", "GeForce 8500 GT", "GeForce 8400 GS", "GeForce 9400 mGPU", "GeForce 9300 mGPU", "GeForce 8300 mGPU", "GeForce 8200 mGPU", "GeForce 8100 mGPU", "GeForce GTX 285M", "GeForce GTX 280M", "GeForce GTX 260M", "GeForce 9800M GTX", "GeForce 8800M GTX", "GeForce GTS 260M", "GeForce GTS 250M", "GeForce 9800M GT", "GeForce 9600M GT", "GeForce 8800M GTS", "GeForce 9800M GTS", "GeForce GT 230M", "GeForce 9700M GT", "GeForce 9650M GS", "GeForce 9600M GS", "GeForce 9500M GS", "GeForce 8700M GT", "GeForce 8600M GT", "GeForce 8600M GS", "GeForce 9500M G", "GeForce 9300M G", "GeForce 8400M GS", "GeForce G210M", "GeForce G110M", "GeForce 9300M GS", "GeForce 9200M GS", "GeForce 9100M G", "GeForce 8400M GT", "GeForce G105M"),
        Gpu("NVIDIA Tesla C870", "1.0", true, "Tesla D870", "Tesla S870", "Quadro FX 5600", "Quadro FX 4600", "Quadro Plex 2100 S4", "GeForce GT 420", "GeForce 8800 Ultra", "GeForce 8800 GTX", "GeForce GT 340", "GeForce GT 330", "GeForce GT 320", "GeForce 315", "GeForce 310", "GeForce 9800 GT", "GeForce 9600 GT", "GeForce 9400GT")
    ];

    public static IReadOnlyList<LlmPytorchReleaseCatalogEntry> PytorchReleases() =>
    [
        Release("2.12", "3.10", "3.14", ModernStable("12.6", "13.0"), ModernExperimental("13.2")),
        Release("2.11", "3.10", "3.14", ModernStable("12.6", "12.8", "13.0")),
        Release("2.10", "3.10", "3.14", ModernStable("12.6", "12.8"), ModernExperimental("13.0")),
        Release("2.9", "3.10", "3.14", ModernStable("12.6", "12.8"), ModernExperimental("13.0")),
        Release("2.8", "3.9", "3.13", ModernStable("12.6", "12.8"), ModernExperimental("12.9")),
        Release("2.7", "3.9", "3.13", ModernStable("11.8", "12.6"), ModernExperimental("12.8")),
        Release("2.6", "3.9", "3.13", ModernStable("11.8", "12.4"), ModernExperimental("12.6")),
        Release("2.5", "3.9", "3.12", ModernStable("11.8", "12.1", "12.4")),
        Release("2.4", "3.8", "3.12", ModernStable("11.8", "12.1"), ModernExperimental("12.4")),
        Release("2.3", "3.8", "3.11", ModernStable("11.8"), ModernExperimental("12.1")),
        Release("2.2", "3.8", "3.11", ModernStable("11.8"), ModernExperimental("12.1")),
        Release("2.1", "3.8", "3.11", LegacyStable("11.8"), LegacyExperimental("12.1"))
    ];

    private static LlmCudaGpuCatalogEntry Gpu(string name, string computeCapability, params string[] aliases) =>
        Gpu(name, computeCapability, false, aliases);

    private static LlmCudaGpuCatalogEntry Gpu(string name, string computeCapability, bool legacy, params string[] aliases) =>
        new()
        {
            Name = name,
            ComputeCapability = computeCapability,
            Legacy = legacy,
            Aliases = aliases.ToList()
        };

    private static LlmPytorchReleaseCatalogEntry Release(string version, string pyMin, string pyMax, IEnumerable<LlmPytorchCudaWheel> stable, IEnumerable<LlmPytorchCudaWheel>? experimental = null) =>
        new()
        {
            Version = version,
            PythonMinimum = pyMin,
            PythonMaximum = pyMax,
            StableCudaWheels = stable.ToList(),
            ExperimentalCudaWheels = (experimental ?? []).ToList()
        };

    private static IEnumerable<LlmPytorchCudaWheel> ModernStable(params string[] cudaVersions) => cudaVersions.Select(ModernWheel);

    private static IEnumerable<LlmPytorchCudaWheel> ModernExperimental(params string[] cudaVersions) => cudaVersions.Select(ModernWheel);

    private static IEnumerable<LlmPytorchCudaWheel> LegacyStable(params string[] cudaVersions) => cudaVersions.Select(LegacyWheel);

    private static IEnumerable<LlmPytorchCudaWheel> LegacyExperimental(params string[] cudaVersions) => cudaVersions.Select(LegacyWheel);

    private static LlmPytorchCudaWheel ModernWheel(string cudaVersion) =>
        Wheel(cudaVersion, ["7.5", "8.0", "8.6", "8.9", "9.0", "10.0", "12.0"]);

    private static LlmPytorchCudaWheel LegacyWheel(string cudaVersion) =>
        Wheel(cudaVersion, ["3.7", "5.0", "6.0", "6.1", "7.0", "7.5", "8.0", "8.6", "9.0"]);

    private static LlmPytorchCudaWheel Wheel(string cudaVersion, string[] architectures)
    {
        string normalized = LlmCompatibilityText.NormalizeCudaVersion(cudaVersion);
        return new LlmPytorchCudaWheel
        {
            CudaVersion = cudaVersion,
            IndexUrl = "https://download.pytorch.org/whl/" + normalized,
            SupportedComputeCapabilities = architectures.ToList()
        };
    }
}

internal static class LlmCompatibilityText
{
    public static string NormalizeGpuName(string value)
    {
        value = (value ?? "").ToLowerInvariant();
        foreach (char ch in new[] { '-', '_', ',', '.', '(', ')', '[', ']' })
            value = value.Replace(ch, ' ');
        value = value.Replace("nvidia", "", StringComparison.OrdinalIgnoreCase)
            .Replace("geforce", "", StringComparison.OrdinalIgnoreCase)
            .Replace("graphics device", "", StringComparison.OrdinalIgnoreCase);
        return string.Join(" ", value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string NormalizeCudaVersion(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant().Replace("cuda", "");
        if (value.StartsWith("cu", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 2)
            return "cu" + parts[0] + parts[1];
        if (value.Length == 3)
            return "cu" + value;
        return "cu" + value.Replace(".", "");
    }
}
