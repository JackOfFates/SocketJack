using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace LlmRuntime;

internal static class LlmNativeRuntimePathConfigurator
{
    public static void ConfigureProcessPath(LlmBackendKind backend)
    {
        foreach (string directory in EnumerateBackendSearchDirectories(backend))
            AddDllDirectoryToPath(directory);
    }

    public static IEnumerable<string> EnumerateBackendSearchDirectories(LlmBackendKind backend)
    {
        var directories = new List<string>();
        foreach (string root in EnumerateSearchRoots())
            directories.AddRange(EnumerateBackendAssetDirectories(root, backend));

        return directories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectory)
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        string assemblyDirectory = Path.GetDirectoryName(typeof(LlmNativeRuntimePathConfigurator).Assembly.Location) ?? "";

        return new[] { AppContext.BaseDirectory, Environment.CurrentDirectory, assemblyDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectory)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateBackendAssetDirectories(string root, LlmBackendKind backend)
    {
        string runtimeId = GetRuntimeId();
        string backendDirectory = backend switch
        {
            LlmBackendKind.Cuda or LlmBackendKind.Cuda12 => "cuda12",
            LlmBackendKind.Vulkan => "vulkan",
            _ => ""
        };

        foreach (string runtimesDirectoryName in new[] { "Runtimes", "runtimes" })
        {
            string runtimesRoot = Path.Combine(root, runtimesDirectoryName);
            string runtimeRoot = Path.Combine(runtimesRoot, runtimeId);
            string runtimeNativeRoot = Path.Combine(runtimeRoot, "native");

            yield return runtimesRoot;
            yield return Path.Combine(runtimesRoot, "bin");
            yield return Path.Combine(runtimesRoot, "dependencies");
            yield return runtimeRoot;
            yield return Path.Combine(runtimeRoot, "bin");
            yield return Path.Combine(runtimeRoot, "dependencies");
            yield return runtimeNativeRoot;
            yield return Path.Combine(runtimeNativeRoot, "bin");
            yield return Path.Combine(runtimeNativeRoot, "dependencies");

            if (!string.IsNullOrWhiteSpace(backendDirectory))
            {
                string backendRoot = Path.Combine(runtimesRoot, backendDirectory);
                string backendRuntimeRoot = Path.Combine(backendRoot, runtimeId);
                string backendNativeRoot = Path.Combine(backendRuntimeRoot, "native");
                string runtimeBackendRoot = Path.Combine(runtimeRoot, backendDirectory);
                string runtimeNativeBackendRoot = Path.Combine(runtimeNativeRoot, backendDirectory);

                yield return backendRoot;
                yield return Path.Combine(backendRoot, "bin");
                yield return Path.Combine(backendRoot, "dependencies");
                yield return backendRuntimeRoot;
                yield return Path.Combine(backendRuntimeRoot, "bin");
                yield return Path.Combine(backendRuntimeRoot, "dependencies");
                yield return backendNativeRoot;
                yield return Path.Combine(backendNativeRoot, "bin");
                yield return Path.Combine(backendNativeRoot, "dependencies");
                yield return runtimeBackendRoot;
                yield return Path.Combine(runtimeBackendRoot, "bin");
                yield return Path.Combine(runtimeBackendRoot, "dependencies");
                yield return runtimeNativeBackendRoot;
                yield return Path.Combine(runtimeNativeBackendRoot, "bin");
                yield return Path.Combine(runtimeNativeBackendRoot, "dependencies");

                foreach (string cpuDependencyDirectory in EnumerateCpuBackendDependencyDirectories(runtimeNativeRoot))
                    yield return cpuDependencyDirectory;

                if (backend is LlmBackendKind.Cuda or LlmBackendKind.Cuda12)
                {
                    foreach (string cudaDependencyDirectory in EnumerateCudaDependencyDirectories(root))
                        yield return cudaDependencyDirectory;
                }
            }
        }

        yield return root;
        yield return Path.Combine(root, runtimeId);

        if (!string.IsNullOrWhiteSpace(backendDirectory))
            yield return Path.Combine(root, backendDirectory);
    }

    private static IEnumerable<string> EnumerateCpuBackendDependencyDirectories(string runtimeNativeRoot)
    {
        foreach (string name in EnumeratePreferredCpuBackendDirectoryNames().Reverse())
            yield return Path.Combine(runtimeNativeRoot, name);
    }

    private static IEnumerable<string> EnumeratePreferredCpuBackendDirectoryNames()
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in EnumerateDetectedCpuBackendDirectoryNames())
        {
            if (yielded.Add(name))
                yield return name;
        }

        foreach (string name in new[] { "avx2", "avx", "noavx", "avx512" })
        {
            if (yielded.Add(name))
                yield return name;
        }
    }

    private static IEnumerable<string> EnumerateDetectedCpuBackendDirectoryNames()
    {
        if (Avx512F.IsSupported)
            yield return "avx512";
        if (Avx2.IsSupported)
            yield return "avx2";
        if (Avx.IsSupported)
            yield return "avx";
        yield return "noavx";
    }

    private static IEnumerable<string> EnumerateCudaDependencyDirectories(string root)
    {
        yield return Path.Combine(root, "Python", "Lib", "site-packages", "torch", "lib");

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            yield break;

        string packageRoot = Path.Combine(userProfile, ".nuget", "packages");
        foreach (string packageName in new[]
        {
            "ntvlibs.cuda12.cudart64_12.runtime.win-x64",
            "ntvlibs.cuda12.cublas64_12.runtime.win-x64",
            "ntvlibs.cuda12.cublaslt64_12.runtime.win-x64"
        })
        {
            string packageDirectory = Path.Combine(packageRoot, packageName);
            if (!Directory.Exists(packageDirectory))
                continue;

            foreach (string versionDirectory in Directory.EnumerateDirectories(packageDirectory).OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                yield return Path.Combine(versionDirectory, "runtimes", "win-x64", "native");
        }
    }

    private static void AddDllDirectoryToPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        string normalized = NormalizeDirectory(directory);
        string current = Environment.GetEnvironmentVariable("PATH") ?? "";
        bool alreadyPresent = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(path => string.Equals(NormalizeDirectory(path), normalized, StringComparison.OrdinalIgnoreCase));
        if (!alreadyPresent)
            Environment.SetEnvironmentVariable("PATH", normalized + Path.PathSeparator + current);
    }

    private static string GetRuntimeId()
    {
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64"
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "win-" + architecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return "linux-" + architecture;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return "osx-" + architecture;

        return architecture;
    }

    private static string NormalizeDirectory(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
