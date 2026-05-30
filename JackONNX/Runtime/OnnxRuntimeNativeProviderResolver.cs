using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace JackONNX.Runtime;

internal static class OnnxRuntimeNativeProviderResolver
{
    private const string CudaPackageName = "microsoft.ml.onnxruntime.gpu";
    private const string CudaWindowsPackageName = "microsoft.ml.onnxruntime.gpu.windows";
    private const string CudaLinuxPackageName = "microsoft.ml.onnxruntime.gpu.linux";
    private const string CudaPackageVersion = "1.24.4";
    private const string DirectMlPackageName = "microsoft.ml.onnxruntime.directml";
    private const string DirectMlPackageVersion = "1.24.4";

    private static readonly object SyncRoot = new();
    private static bool _resolverRegistered;
    private static IntPtr _onnxRuntimeHandle;
    private static string _selectedProvider = "";
    private static string _selectedOnnxRuntimePath = "";

    public static string SelectedProvider
    {
        get
        {
            lock (SyncRoot)
                return _selectedProvider;
        }
    }

    public static void TryPreselectGpuRuntime(JackOnnxOptions options, IEnumerable<IJackOnnxExecutionProvider> providers)
    {
        options ??= new JackOnnxOptions();
        if (options.DevicePolicy == JackOnnxDevicePolicy.CpuOnly ||
            options.PreferredProvider == JackOnnxExecutionProvider.Cpu)
            return;

        foreach (JackOnnxExecutionProvider provider in BuildProviderPreference(options, providers))
        {
            if (provider == JackOnnxExecutionProvider.Cuda)
            {
                ConfigureProviderDependencyPaths(provider);
                IReadOnlyList<string> missing = CudaNativeDependencyProbe.FindMissingDependencies();
                if (missing.Count > 0)
                    continue;
            }

            if (TryEnsureProvider(provider, out _))
                return;
        }
    }

    public static bool TryEnsureProvider(JackOnnxExecutionProvider provider, out string detail)
    {
        detail = "";
        if (provider == JackOnnxExecutionProvider.Auto || provider == JackOnnxExecutionProvider.Cpu)
            return true;

        lock (SyncRoot)
        {
            if (!string.IsNullOrWhiteSpace(_selectedProvider))
            {
                if (string.Equals(_selectedProvider, provider.ToString(), StringComparison.OrdinalIgnoreCase))
                    return true;

                detail = "ONNX Runtime native provider is already selected for " + _selectedProvider + ". Restart JackLLM to switch to " + provider + ".";
                return false;
            }

            string? providerDirectory = ResolveProviderDirectory(provider);
            if (string.IsNullOrWhiteSpace(providerDirectory))
            {
                detail = "Could not find the " + provider + " ONNX Runtime native package assets.";
                return false;
            }

            string? onnxRuntimePath = ResolveNativeLibraryPath(providerDirectory, GetOnnxRuntimeLibraryNames());
            if (string.IsNullOrWhiteSpace(onnxRuntimePath))
            {
                detail = "Missing ONNX Runtime native library under " + providerDirectory + ". Expected " + string.Join(" or ", GetOnnxRuntimeLibraryNames()) + ".";
                return false;
            }

            try
            {
                RegisterResolverLocked();
                _selectedProvider = provider.ToString();
                _selectedOnnxRuntimePath = onnxRuntimePath;
                AddDllDirectoryToPath(providerDirectory);
                ConfigureProviderDependencyPaths(provider);
                _onnxRuntimeHandle = NativeLibrary.Load(onnxRuntimePath);
                detail = provider + " ONNX Runtime native library loaded from " + providerDirectory + ".";
                return true;
            }
            catch (Exception ex)
            {
                _selectedProvider = "";
                _selectedOnnxRuntimePath = "";
                detail = ex.Message;
                return false;
            }
        }
    }

    internal static void TryEnsureCudaDependencyPaths()
    {
        ConfigureProviderDependencyPaths(JackOnnxExecutionProvider.Cuda);
    }

    private static IReadOnlyList<JackOnnxExecutionProvider> BuildProviderPreference(
        JackOnnxOptions options,
        IEnumerable<IJackOnnxExecutionProvider> providers)
    {
        var knownProviders = providers
            .Select(provider => provider.Kind)
            .Where(provider => provider is JackOnnxExecutionProvider.Cuda or JackOnnxExecutionProvider.DirectML)
            .Distinct()
            .ToList();

        if (options.PreferredProvider is JackOnnxExecutionProvider.Cuda or JackOnnxExecutionProvider.DirectML)
        {
            var ordered = new List<JackOnnxExecutionProvider> { options.PreferredProvider };
            if (options.DevicePolicy != JackOnnxDevicePolicy.RequirePreferredProvider)
            {
                foreach (JackOnnxExecutionProvider provider in knownProviders)
                {
                    if (!ordered.Contains(provider))
                        ordered.Add(provider);
                }
            }

            return ordered;
        }

        return providers
            .OrderBy(provider => provider.Priority)
            .Select(provider => provider.Kind)
            .Where(provider => provider is JackOnnxExecutionProvider.Cuda or JackOnnxExecutionProvider.DirectML)
            .Distinct()
            .ToList();
    }

    private static void RegisterResolverLocked()
    {
        if (_resolverRegistered)
            return;

        NativeLibrary.SetDllImportResolver(typeof(SessionOptions).Assembly, ResolveOnnxRuntimeImport);
        _resolverRegistered = true;
    }

    private static IntPtr ResolveOnnxRuntimeImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "onnxruntime", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(_selectedOnnxRuntimePath))
            return IntPtr.Zero;

        lock (SyncRoot)
        {
            if (_onnxRuntimeHandle != IntPtr.Zero)
                return _onnxRuntimeHandle;

            _onnxRuntimeHandle = NativeLibrary.Load(_selectedOnnxRuntimePath);
            return _onnxRuntimeHandle;
        }
    }

    private static string? ResolveProviderDirectory(JackOnnxExecutionProvider provider)
    {
        string providerName = provider == JackOnnxExecutionProvider.Cuda ? "cuda" : "directml";
        string runtimeId = GetCurrentRuntimeId();

        var candidates = new List<string>();
        foreach (string baseDirectory in ResolveApplicationBaseDirectories())
        {
            candidates.AddRange(EnumerateBuildRuntimeDirectories(baseDirectory, providerName, runtimeId));
            candidates.Add(Path.Combine(baseDirectory, "JackONNX", "providers", providerName, runtimeId));
            candidates.Add(Path.Combine(baseDirectory, "providers", providerName, runtimeId));
            candidates.Add(Path.Combine(baseDirectory, "runtimes", runtimeId, "native"));
            candidates.Add(Path.Combine(baseDirectory, runtimeId, "JackONNX", "providers", providerName, runtimeId));
            candidates.Add(Path.Combine(baseDirectory, runtimeId, "providers", providerName, runtimeId));

            if (provider == JackOnnxExecutionProvider.Cuda)
                candidates.Add(Path.Combine(baseDirectory, runtimeId));
        }

        candidates.AddRange(ResolveNuGetProviderDirectories(provider, runtimeId));

        foreach (string directory in candidates)
        {
            if (ProviderDirectoryLooksUsable(provider, directory))
                return directory;
        }

        return null;
    }

    internal static string GetCurrentRuntimeId()
    {
        string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-" + architecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "osx-" + architecture;
        return "win-" + architecture;
    }

    internal static IEnumerable<string> EnumerateBuildRuntimeDirectories(string baseDirectory, string providerName, string runtimeId)
    {
        foreach (string runtimesDirectoryName in new[] { "Runtimes", "runtimes" })
        {
            string runtimesRoot = Path.Combine(baseDirectory, runtimesDirectoryName);
            string providerRoot = Path.Combine(runtimesRoot, providerName);
            string providerRuntimeRoot = Path.Combine(providerRoot, runtimeId);
            string providerNativeRoot = Path.Combine(providerRuntimeRoot, "native");
            string runtimeRoot = Path.Combine(runtimesRoot, runtimeId);
            string runtimeNativeRoot = Path.Combine(runtimeRoot, "native");

            yield return runtimesRoot;
            yield return providerRoot;
            yield return Path.Combine(providerRoot, "bin");
            yield return Path.Combine(providerRoot, "dependencies");
            yield return providerRuntimeRoot;
            yield return Path.Combine(providerRuntimeRoot, "bin");
            yield return Path.Combine(providerRuntimeRoot, "dependencies");
            yield return providerNativeRoot;
            yield return Path.Combine(providerNativeRoot, "bin");
            yield return Path.Combine(providerNativeRoot, "dependencies");
            yield return runtimeRoot;
            yield return runtimeNativeRoot;
            yield return Path.Combine(runtimeRoot, providerName);
            yield return Path.Combine(runtimeRoot, providerName, "bin");
            yield return Path.Combine(runtimeRoot, providerName, "dependencies");
            yield return Path.Combine(runtimeNativeRoot, providerName);
            yield return Path.Combine(runtimeNativeRoot, providerName, "bin");
            yield return Path.Combine(runtimeNativeRoot, providerName, "dependencies");
        }
    }

    private static IEnumerable<string> ResolveApplicationBaseDirectories()
    {
        yield return AppContext.BaseDirectory;

        string currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory) &&
            !string.Equals(Path.GetFullPath(currentDirectory), Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            yield return currentDirectory;
        }
    }

    private static IEnumerable<string> ResolveNuGetProviderDirectories(JackOnnxExecutionProvider provider, string runtimeId)
    {
        string packageVersion = provider == JackOnnxExecutionProvider.Cuda ? CudaPackageVersion : DirectMlPackageVersion;

        foreach (string root in ResolveNuGetRoots())
        {
            foreach (string packageName in ResolveNuGetProviderPackageNames(provider))
                yield return Path.Combine(root, packageName, packageVersion, "runtimes", runtimeId, "native");
        }
    }

    private static IEnumerable<string> ResolveNuGetProviderPackageNames(JackOnnxExecutionProvider provider)
    {
        if (provider != JackOnnxExecutionProvider.Cuda)
        {
            yield return DirectMlPackageName;
            yield break;
        }

        yield return CudaPackageName;
        yield return RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? CudaLinuxPackageName
            : CudaWindowsPackageName;
    }

    private static IEnumerable<string> ResolveNuGetRoots()
    {
        string fromEnvironment = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? "";
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            yield return fromEnvironment;

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
            yield return Path.Combine(userProfile, ".nuget", "packages");
    }

    private static bool ProviderDirectoryLooksUsable(JackOnnxExecutionProvider provider, string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        if (!ContainsNativeLibrary(directory, GetOnnxRuntimeLibraryNames()))
            return false;

        return provider switch
        {
            JackOnnxExecutionProvider.Cuda => ContainsNativeLibrary(directory, GetCudaProviderLibraryNames()),
            JackOnnxExecutionProvider.DirectML => ContainsNativeLibrary(directory, ["DirectML.dll"]) ||
                                                  CanFindDirectMlDependency(),
            _ => true
        };
    }

    private static IReadOnlyList<string> GetOnnxRuntimeLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["libonnxruntime.so"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["libonnxruntime.dylib"];
        return ["onnxruntime.dll"];
    }

    private static IReadOnlyList<string> GetCudaProviderLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ["libonnxruntime_providers_cuda.so"];
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["libonnxruntime_providers_cuda.dylib"];
        return ["onnxruntime_providers_cuda.dll"];
    }

    private static bool ContainsNativeLibrary(string directory, IEnumerable<string> fileNames) =>
        !string.IsNullOrWhiteSpace(ResolveNativeLibraryPath(directory, fileNames));

    private static string? ResolveNativeLibraryPath(string directory, IEnumerable<string> fileNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        foreach (string fileName in fileNames)
        {
            string exact = Path.Combine(directory, fileName);
            if (File.Exists(exact))
                return exact;

            if (fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string? versioned = Directory.EnumerateFiles(directory, fileName + "*").FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(versioned))
                        return versioned;
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static bool CanFindDirectMlDependency()
    {
        if (CanFindNativeLibrary("DirectML.dll", EnumerateDirectMlSearchDirectories()))
            return true;

        foreach (string directory in ResolveNuGetRoots())
        {
            string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-win" : "x64-win";
            string path = Path.Combine(directory, "microsoft.ai.directml", "1.15.4", "bin", architecture, "DirectML.dll");
            if (File.Exists(path))
                return true;
        }

        return false;
    }

    private static void AddDllDirectoryToPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return;

        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        bool alreadyPresent = current.Split(Path.PathSeparator)
            .Any(path => string.Equals(path.Trim(), directory, StringComparison.OrdinalIgnoreCase));
        if (!alreadyPresent)
            Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + current);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string libraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
            bool libraryAlreadyPresent = libraryPath.Split(Path.PathSeparator)
                .Any(path => string.Equals(path.Trim(), directory, StringComparison.OrdinalIgnoreCase));
            if (!libraryAlreadyPresent)
                Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", directory + Path.PathSeparator + libraryPath);
        }
    }

    private static void ConfigureProviderDependencyPaths(JackOnnxExecutionProvider provider)
    {
        if (provider == JackOnnxExecutionProvider.Cuda)
        {
            foreach (string directory in CudaNativeDependencyProbe.EnumerateNativeSearchDirectories())
                AddDllDirectoryToPath(directory);
            return;
        }

        if (provider != JackOnnxExecutionProvider.DirectML)
            return;

        foreach (string baseDirectory in ResolveApplicationBaseDirectories())
        {
            foreach (string directory in EnumerateBuildRuntimeDirectories(baseDirectory, "directml", "win-x64"))
                AddDllDirectoryToPath(directory);
        }

        foreach (string root in ResolveNuGetRoots())
        {
            string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64-win" : "x64-win";
            AddDllDirectoryToPath(Path.Combine(root, "microsoft.ai.directml", "1.15.4", "bin", architecture));
        }
    }

    internal static IEnumerable<string> GetNuGetRoots() => ResolveNuGetRoots();

    private static bool CanFindNativeLibrary(string fileName, IEnumerable<string> directories)
    {
        foreach (string directory in directories)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, fileName)))
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateDirectMlSearchDirectories()
    {
        foreach (string baseDirectory in ResolveApplicationBaseDirectories())
        {
            foreach (string directory in EnumerateBuildRuntimeDirectories(baseDirectory, "directml", "win-x64"))
                yield return directory;
            yield return baseDirectory;
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "directml", "win-x64");
            yield return Path.Combine(baseDirectory, "providers", "directml", "win-x64");
            yield return Path.Combine(baseDirectory, "runtimes", "win-x64", "native");
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory.Trim();
    }
}

internal static class CudaNativeDependencyProbe
{
    private static IReadOnlyList<string> RequiredNativeLibraries =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
            ? ["libcudart.so", "libcublas.so", "libcublasLt.so", "libcufft.so", "libcudnn.so"]
            : ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll", "cufft64_11.dll", "cudnn64_9.dll"];

    public static IReadOnlyList<string> FindMissingDependencies()
    {
        var missing = new List<string>();
        foreach (string library in RequiredNativeLibraries)
            if (!CanFindNativeLibrary(library))
                missing.Add(library);

        return missing;
    }

    public static string BuildMissingDependencyDetail(IReadOnlyList<string> missing)
    {
        if (missing.Count == 0)
            return "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "CUDA GPU was detected, but ONNX Runtime CUDA needs missing CUDA/cuDNN native libraries: " +
                   string.Join(", ", missing) +
                   ". Put the libraries under the build-local Runtimes/cuda/linux-x64/dependencies folder, set JACKONNX_CUDA_DEPENDENCY_PATHS or LD_LIBRARY_PATH, or install CUDA and cuDNN under /usr/local/cuda, /usr/lib/x86_64-linux-gnu, or /opt/cuda.";
        }

        return "CUDA GPU was detected, but ONNX Runtime CUDA needs missing CUDA 12/cuDNN native DLLs: " +
               string.Join(", ", missing) +
               ". Put the DLLs under the build-local Runtimes folder, under JackONNX\\providers\\cuda\\win-x64\\dependencies, set JACKONNX_CUDA_DEPENDENCY_PATHS, or install/configure CUDA 12 and cuDNN 9. DirectML can still use the GPU on Windows without these CUDA toolkit DLLs.";
    }

    private static bool CanFindNativeLibrary(string fileName)
    {
        return OnnxRuntimeNativeProviderResolverCanFindNativeLibrary(fileName, EnumerateNativeSearchDirectories());
    }

    private static bool OnnxRuntimeNativeProviderResolverCanFindNativeLibrary(string fileName, IEnumerable<string> directories)
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

    internal static IEnumerable<string> EnumerateNativeSearchDirectories()
    {
        string runtimeId = OnnxRuntimeNativeProviderResolver.GetCurrentRuntimeId();
        foreach (string baseDirectory in EnumerateApplicationBaseDirectories())
        {
            foreach (string directory in OnnxRuntimeNativeProviderResolver.EnumerateBuildRuntimeDirectories(baseDirectory, "cuda", runtimeId))
                yield return directory;
            yield return baseDirectory;
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "cuda", runtimeId);
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "cuda", runtimeId, "dependencies");
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "cuda", runtimeId, "bin");
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "cuda", runtimeId, "lib");
            yield return Path.Combine(baseDirectory, "JackONNX", "providers", "cuda", runtimeId, "lib64");
            yield return Path.Combine(baseDirectory, "providers", "cuda", runtimeId);
            yield return Path.Combine(baseDirectory, "providers", "cuda", runtimeId, "dependencies");
            yield return Path.Combine(baseDirectory, "providers", "cuda", runtimeId, "bin");
            yield return Path.Combine(baseDirectory, "providers", "cuda", runtimeId, "lib");
            yield return Path.Combine(baseDirectory, "providers", "cuda", runtimeId, "lib64");
            yield return Path.Combine(baseDirectory, "runtimes", runtimeId, "native");
            yield return Path.Combine(baseDirectory, runtimeId);
            yield return Path.Combine(baseDirectory, runtimeId, "JackONNX", "providers", "cuda", runtimeId);
            yield return Path.Combine(baseDirectory, runtimeId, "JackONNX", "providers", "cuda", runtimeId, "dependencies");
        }

        foreach (string directory in EnumerateEnvironmentPathList("JACKONNX_CUDA_DEPENDENCY_PATHS"))
            yield return directory;

        foreach (string directory in EnumerateEnvironmentPathList("JACKONNX_CUDA_PATHS"))
            yield return directory;

        foreach (string directory in EnumerateEnvironmentPathList("LD_LIBRARY_PATH"))
            yield return directory;

        foreach (string variable in new[] { "JACKONNX_CUDA_BIN", "JACKONNX_CUDA_PATH", "CUDA_PATH", "CUDA_HOME", "CUDA_ROOT", "CUDNN_PATH" })
        {
            string configured = Environment.GetEnvironmentVariable(variable) ?? "";
            if (string.IsNullOrWhiteSpace(configured))
                continue;

            yield return configured;
            yield return Path.Combine(configured, "bin");
            yield return Path.Combine(configured, "lib", "x64");
            yield return Path.Combine(configured, "lib");
            yield return Path.Combine(configured, "lib64");
            yield return Path.Combine(configured, "targets", "x86_64-linux", "lib");
            yield return Path.Combine(configured, "targets", "aarch64-linux", "lib");
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory.Trim();

        foreach (string directory in EnumerateLikelyCudaDirectories())
            yield return directory;

        foreach (string directory in EnumerateNuGetCudaRuntimeDirectories())
            yield return directory;
    }

    private static IEnumerable<string> EnumerateApplicationBaseDirectories()
    {
        yield return AppContext.BaseDirectory;

        string currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory) &&
            !string.Equals(Path.GetFullPath(currentDirectory), Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        {
            yield return currentDirectory;
        }
    }

    private static IEnumerable<string> EnumerateEnvironmentPathList(string variable)
    {
        string value = Environment.GetEnvironmentVariable(variable) ?? "";
        foreach (string directory in value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return directory.Trim();
    }

    private static IEnumerable<string> EnumerateLikelyCudaDirectories()
    {
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

            yield break;
        }

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            string cudaRoot = Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "CUDA");
            foreach (string directory in EnumerateBinChildren(cudaRoot))
                yield return directory;

            string cudnnRoot = Path.Combine(programFiles, "NVIDIA", "CUDNN");
            foreach (string directory in EnumerateBinChildren(cudnnRoot))
                yield return directory;

            string nvidiaRoot = Path.Combine(programFiles, "NVIDIA");
            foreach (string directory in EnumerateBinChildren(nvidiaRoot))
                yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateNuGetCudaRuntimeDirectories()
    {
        foreach (string root in OnnxRuntimeNativeProviderResolver.GetNuGetRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            IEnumerable<string> packages;
            try
            {
                packages = Directory.EnumerateDirectories(root, "ntvlibs.cuda12.*")
                    .Concat(Directory.EnumerateDirectories(root, "ntvlibs.cudnn.cuda12.*"))
                    .Concat(Directory.EnumerateDirectories(root, "microsoft.ml.onnxruntime.gpu*"));
            }
            catch
            {
                continue;
            }

            foreach (string package in packages)
            {
                foreach (string directory in EnumerateBinChildren(package))
                    yield return directory;

                foreach (string directory in EnumerateNativeAssetDirectories(package))
                    yield return directory;
            }
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

    private static IEnumerable<string> EnumerateNativeAssetDirectories(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        foreach (string pattern in new[] { "runtimes", "native", "bin" })
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(root, pattern, SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string directory in directories)
                yield return directory;
        }
    }

    private static IEnumerable<string> EnumerateBinChildren(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string directory in children)
            {
                pending.Push(directory);
                if (string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase) ||
                    directory.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    yield return directory;
            }
        }
    }
}
