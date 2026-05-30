using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmNativeRuntimePathConfiguratorTests
{
    [TestMethod]
    public void EnumerateBackendSearchDirectories_IncludesBuildLocalRuntimesLayouts()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            string runtimeId = GetRuntimeId();
            string runtimesRoot = Path.Combine(root, "Runtimes");
            string backendRoot = Path.Combine(runtimesRoot, "cuda12");
            string backendRuntimeRoot = Path.Combine(backendRoot, runtimeId);
            string backendDependencyRoot = Path.Combine(backendRuntimeRoot, "dependencies");
            string runtimeNativeBackendRoot = Path.Combine(runtimesRoot, runtimeId, "native", "cuda12");

            Directory.CreateDirectory(backendRoot);
            Directory.CreateDirectory(backendDependencyRoot);
            Directory.CreateDirectory(runtimeNativeBackendRoot);

            Environment.CurrentDirectory = root;

            List<string> directories = LlmNativeRuntimePathConfigurator
                .EnumerateBackendSearchDirectories(LlmBackendKind.Cuda12)
                .ToList();

            AssertContainsDirectory(directories, runtimesRoot);
            AssertContainsDirectory(directories, backendRoot);
            AssertContainsDirectory(directories, backendRuntimeRoot);
            AssertContainsDirectory(directories, backendDependencyRoot);
            AssertContainsDirectory(directories, runtimeNativeBackendRoot);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private static void AssertContainsDirectory(IReadOnlyCollection<string> directories, string expected)
    {
        string normalized = NormalizeDirectory(expected);
        Assert.IsTrue(
            directories.Any(directory => string.Equals(directory, normalized, StringComparison.OrdinalIgnoreCase)),
            "Expected native runtime directory was not included: " + normalized);
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

    private static string NormalizeDirectory(string path) =>
        Path.GetFullPath(path.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
