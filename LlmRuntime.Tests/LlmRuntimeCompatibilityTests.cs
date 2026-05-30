using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LlmRuntime;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmRuntimeCompatibilityTests
{
    [TestMethod]
    public void DefaultCatalog_MatchesCurrentAndLegacyNvidiaGpus()
    {
        LlmRuntimeCompatibilityCatalog catalog = LlmRuntimeCompatibilityService.BuildCatalog();

        Assert.AreEqual("12.0", catalog.MatchGpu("NVIDIA GeForce RTX 5090")?.ComputeCapability);
        Assert.AreEqual("6.1", catalog.MatchGpu("GeForce GTX 1080 Ti")?.ComputeCapability);
        Assert.AreEqual("2.0", catalog.MatchGpu("GeForce GTX 580")?.ComputeCapability);
        Assert.AreEqual("1.0", catalog.MatchGpu("GeForce 8800 GTX")?.ComputeCapability);
        Assert.AreEqual("7.2", catalog.MatchGpu("Jetson AGX Xavier")?.ComputeCapability);
    }

    [TestMethod]
    public void RecommendPytorch_ChoosesCuda126ForAdaWithCurrentPython()
    {
        LlmRuntimeCompatibilityCatalog catalog = LlmRuntimeCompatibilityService.BuildCatalog();
        LlmPytorchPackageRecommendation? recommendation = catalog.RecommendPytorch(
            new Version(3, 12),
            "8.9",
            new LlmRuntimeCompatibilityConfig { PreferredPytorchVersion = "2.12", PreferredCudaVersion = "12.6" });

        Assert.IsNotNull(recommendation);
        Assert.AreEqual("2.12", recommendation.PytorchVersion);
        Assert.AreEqual("12.6", recommendation.CudaVersion);
        Assert.AreEqual("https://download.pytorch.org/whl/cu126", recommendation.IndexUrl);
    }

    [TestMethod]
    public void RecommendPytorch_ChoosesLegacyCuda118ForMaxwellWithPython311()
    {
        LlmRuntimeCompatibilityCatalog catalog = LlmRuntimeCompatibilityService.BuildCatalog();
        LlmPytorchPackageRecommendation? recommendation = catalog.RecommendPytorch(
            new Version(3, 11),
            "5.2",
            new LlmRuntimeCompatibilityConfig());

        Assert.IsNotNull(recommendation);
        Assert.AreEqual("2.1", recommendation.PytorchVersion);
        Assert.AreEqual("11.8", recommendation.CudaVersion);
        Assert.AreEqual("https://download.pytorch.org/whl/cu118", recommendation.IndexUrl);
    }

    [TestMethod]
    public void RecommendPytorch_RejectsCuda13ForPascalWhenForced()
    {
        LlmRuntimeCompatibilityCatalog catalog = LlmRuntimeCompatibilityService.BuildCatalog();
        LlmPytorchPackageRecommendation? recommendation = catalog.RecommendPytorch(
            new Version(3, 12),
            "6.1",
            new LlmRuntimeCompatibilityConfig
            {
                PreferredPytorchVersion = "2.12",
                PreferredCudaVersion = "13.0"
            });

        Assert.IsNull(recommendation);
    }

    [TestMethod]
    public void ConfigOverlay_AddsUserGpuAlias()
    {
        var config = new LlmRuntimeCompatibilityConfig
        {
            ExtraGpuAliases =
            [
                new LlmGpuAliasOverride
                {
                    Name = "Custom Lab GPU",
                    ComputeCapability = "8.6",
                    Aliases = ["Internal RTX Alias"]
                }
            ]
        };

        LlmRuntimeCompatibilityCatalog catalog = LlmRuntimeCompatibilityService.BuildCatalog(config);

        Assert.AreEqual("8.6", catalog.MatchGpu("Internal RTX Alias")?.ComputeCapability);
    }

    [TestMethod]
    public void Status_CpuOnlyTorchOnCudaGpuRequiresRepair()
    {
        var probe = new FakeCompatibilityProbe
        {
            Gpus = [new LlmDetectedGpu { Name = "GeForce RTX 4090", IsNvidia = true }],
            Python = new LlmPythonRuntimeStatus
            {
                ExecutablePath = "python.exe",
                IsAvailable = true,
                Version = "3.12.8",
                HasTorch = true,
                TorchVersion = "2.12.0",
                TorchCudaVersion = "",
                TorchCudaAvailable = false
            }
        };
        var service = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions(), probe);

        LlmRuntimeCompatibilityStatus status = service.GetStatus("python.exe");

        Assert.AreEqual("repair_required", status.Status);
        Assert.IsTrue(status.GenerationDisabled);
        Assert.IsTrue(status.RequiresPytorchRepair);
        Assert.IsNotNull(status.Diagnostics.RecommendedPytorch);
        StringAssert.Contains(status.Message, "CPU-only PyTorch");
    }

    [TestMethod]
    public void Status_TorchArchListRejectsUnsupportedGpu()
    {
        var probe = new FakeCompatibilityProbe
        {
            Gpus = [new LlmDetectedGpu { Name = "GeForce GTX TITAN X", IsNvidia = true }],
            Python = new LlmPythonRuntimeStatus
            {
                ExecutablePath = "python.exe",
                IsAvailable = true,
                Version = "3.12.8",
                HasTorch = true,
                TorchVersion = "2.11.0+cu128",
                TorchCudaVersion = "12.8",
                TorchCudaAvailable = true,
                TorchCudaDeviceName = "NVIDIA GeForce GTX TITAN X",
                TorchCudaDeviceCapability = "5.2",
                TorchCudaArchList = ["sm_75", "sm_80", "sm_86", "sm_90"]
            }
        };
        var service = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions(), probe);

        LlmRuntimeCompatibilityStatus status = service.GetStatus("python.exe");

        Assert.AreEqual("repair_required", status.Status);
        Assert.IsTrue(status.GenerationDisabled);
        StringAssert.Contains(status.Message, "does not include kernels");
        StringAssert.Contains(status.Message, "sm_52");
    }

    [TestMethod]
    public void Status_LiveTorchCudaProbeAllowsV100Sm70WhenCatalogLags()
    {
        var probe = new FakeCompatibilityProbe
        {
            Gpus = [new LlmDetectedGpu { Name = "Tesla V100-PCIE-16GB", IsNvidia = true, ComputeCapability = "7.0" }],
            Python = new LlmPythonRuntimeStatus
            {
                ExecutablePath = "python.exe",
                IsAvailable = true,
                Version = "3.13.5",
                HasTorch = true,
                TorchVersion = "2.6.0+cu124",
                TorchCudaVersion = "12.4",
                TorchCudaAvailable = true,
                TorchCudaDeviceName = "Tesla V100-PCIE-16GB",
                TorchCudaDeviceCapability = "7.0",
                TorchCudaArchList = ["sm_50", "sm_60", "sm_70", "sm_75", "sm_80", "sm_86", "sm_90"]
            }
        };
        var service = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions(), probe);

        LlmRuntimeCompatibilityStatus status = service.GetStatus("python.exe");

        Assert.AreEqual("ok", status.Status);
        Assert.IsFalse(status.GenerationDisabled);
        Assert.IsFalse(status.RequiresPytorchRepair);
        StringAssert.Contains(status.Message, "compatible");
    }

    [TestMethod]
    public void Status_CommandNamePythonIsProbedInsteadOfTreatedAsMissingFile()
    {
        var probe = new FakeCompatibilityProbe
        {
            Gpus = [new LlmDetectedGpu { Name = "GeForce RTX 4090", IsNvidia = true }],
            Python = new LlmPythonRuntimeStatus
            {
                IsAvailable = true,
                Version = "3.12.8",
                HasTorch = true,
                TorchVersion = "2.12.0",
                TorchCudaVersion = "12.6",
                TorchCudaAvailable = true
            }
        };
        var service = new LlmRuntimeCompatibilityService(new LlmRuntimeOptions(), probe);

        LlmRuntimeCompatibilityStatus status = service.GetStatus("python");

        Assert.AreEqual("ok", status.Status);
        Assert.IsFalse(status.GenerationDisabled);
        Assert.AreEqual("python", status.Diagnostics.Python.ExecutablePath);
    }

    [TestMethod]
    public async Task CompatibilityEndpoints_SaveResetAndRepair()
    {
        string root = LlmModelRegistryTests.CreateTempDirectory();
        int port = NextPort();
        try
        {
            var probe = new FakeCompatibilityProbe
            {
                Gpus = [new LlmDetectedGpu { Name = "GeForce RTX 4090", IsNvidia = true }],
                Python = new LlmPythonRuntimeStatus
                {
                    ExecutablePath = "python.exe",
                    IsAvailable = true,
                    Version = "3.12.8",
                    HasTorch = false
                }
            };
            var options = new LlmRuntimeOptions
            {
                ModelRoot = root,
                Port = port,
                CompatibilityConfigPath = Path.Combine(root, "compatibility.json")
            };
            using var host = new LlmRuntimeHost(options, compatibility: new LlmRuntimeCompatibilityService(options, probe));
            Assert.IsTrue(host.Start());

            using var client = new HttpClient();
            string body = await client.GetStringAsync($"http://127.0.0.1:{port}/api/v1/runtime/compatibility");
            using (var document = JsonDocument.Parse(body))
                Assert.AreEqual("repair_required", document.RootElement.GetProperty("status").GetString());

            var save = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/runtime/compatibility/config",
                new StringContent("{\"preferredCudaVersion\":\"12.6\",\"allowExperimentalCuda\":false}", Encoding.UTF8, "application/json"));
            save.EnsureSuccessStatusCode();
            Assert.IsTrue(File.Exists(options.CompatibilityConfigPath));

            var repair = await client.PostAsync(
                $"http://127.0.0.1:{port}/api/v1/runtime/compatibility/repair-pytorch",
                new StringContent("{\"python\":\"python.exe\"}", Encoding.UTF8, "application/json"));
            repair.EnsureSuccessStatusCode();
            using (var document = JsonDocument.Parse(await repair.Content.ReadAsStringAsync()))
                Assert.AreEqual("repaired", document.RootElement.GetProperty("status").GetString());
            CollectionAssert.Contains(probe.LastRunArguments.ToList(), "torch==2.12.*");
            CollectionAssert.Contains(probe.LastRunArguments.ToList(), "torchvision");
            CollectionAssert.Contains(probe.LastRunArguments.ToList(), "torchaudio");

            var reset = await client.PostAsync($"http://127.0.0.1:{port}/api/v1/runtime/compatibility/reset", new StringContent("{}", Encoding.UTF8, "application/json"));
            reset.EnsureSuccessStatusCode();
            Assert.IsFalse(File.Exists(options.CompatibilityConfigPath));
        }
        finally
        {
            LlmModelRegistryTests.TryDeleteDirectory(root);
        }
    }

    private static int NextPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeCompatibilityProbe : ILlmRuntimeCompatibilityProbe
    {
        public IReadOnlyList<LlmDetectedGpu> Gpus { get; init; } = [];

        public LlmPythonRuntimeStatus Python { get; set; } = new() { IsAvailable = true, Version = "3.12.8" };

        public IReadOnlyList<string> LastRunArguments { get; private set; } = [];

        public IReadOnlyList<LlmDetectedGpu> DetectNvidiaGpus(CancellationToken cancellationToken) => Gpus;

        public bool HasCudaDriver(CancellationToken cancellationToken) => true;

        public IReadOnlyList<string> FindMissingCudaDependencies() => [];

        public LlmPythonRuntimeStatus InspectPython(string pythonExecutable, CancellationToken cancellationToken)
        {
            Python.ExecutablePath = pythonExecutable;
            return Python;
        }

        public Task<LlmProcessRunResult> RunPythonAsync(string pythonExecutable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            LastRunArguments = arguments;
            Python = new LlmPythonRuntimeStatus
            {
                ExecutablePath = pythonExecutable,
                IsAvailable = true,
                Version = "3.12.8",
                HasTorch = true,
                TorchVersion = "2.12.0",
                TorchCudaVersion = "12.6",
                TorchCudaAvailable = true,
                TorchCudaDeviceCapability = "8.9",
                TorchCudaArchList = ["sm_86"]
            };

            return Task.FromResult(new LlmProcessRunResult { ExitCode = 0 });
        }
    }
}
