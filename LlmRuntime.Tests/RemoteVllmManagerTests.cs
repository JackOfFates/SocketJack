using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class RemoteVllmManagerTests
{
    [DataTestMethod]
    [DataRow("deepseek-ai/DeepSeek-V4-Pro-DSpark")]
    [DataRow("DeepSeek-V4-Flash-DSpark")]
    public void Detector_RecognizesOfficialBundles(string model)
    {
        Assert.IsTrue(DSparkModelDetector.IsOfficialDSparkModel(model));
        Assert.AreEqual(VllmAccelerationMode.DSpark, DSparkModelDetector.Resolve(model, VllmAccelerationMode.Auto));
    }

    [DataTestMethod]
    [DataRow("deepseek-ai/DeepSeek-V4-Pro")]
    [DataRow("somebody/DeepSeek-V4-Pro-DSpark-remix")]
    [DataRow("Qwen3-235B")]
    public void Detector_DoesNotAutoEnableForOtherModels(string model)
    {
        Assert.IsFalse(DSparkModelDetector.IsOfficialDSparkModel(model));
        Assert.AreEqual(VllmAccelerationMode.Standard, DSparkModelDetector.Resolve(model, VllmAccelerationMode.Auto));
    }

    [TestMethod]
    public void BuildStartCommand_PreservesJsonAndRuntimeSettings()
    {
        RemoteVllmProfile profile = CreateProfile();
        profile.ContextLength = 131072;
        profile.TensorParallelSize = 4;
        profile.ModelCachePath = "/models/cache path";
        profile.ExtraArguments = "--dtype auto --kv-cache-dtype fp8";

        string command = RemoteVllmManager.BuildStartCommand(profile, VllmAccelerationMode.DSpark);

        StringAssert.Contains(command, "--speculative-config");
        StringAssert.Contains(command, "num_speculative_tokens");
        StringAssert.Contains(command, "--tensor-parallel-size 4");
        StringAssert.Contains(command, "--max-model-len 131072");
        StringAssert.Contains(command, "HF_HOME='/models/cache path'");
        StringAssert.Contains(command, "'--kv-cache-dtype' 'fp8'");
    }

    [TestMethod]
    public void Redact_RemovesCommonSecrets()
    {
        string redacted = RemoteVllmManager.Redact("--api-key secret123 token=abcdef Authorization: BearerValue");

        Assert.IsFalse(redacted.Contains("secret123", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("abcdef", StringComparison.Ordinal));
        Assert.IsFalse(redacted.Contains("BearerValue", StringComparison.Ordinal));
        StringAssert.Contains(redacted, "[redacted]");
    }

    [TestMethod]
    public void Registry_ExposesRemoteProfileAsLoadableDSparkModel()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackllm-remote-vllm-" + Guid.NewGuid().ToString("N"));
        string complete = Path.Combine(root, "complete");
        try
        {
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = complete,
                RemoteVllmProfiles = [CreateProfile()]
            });

            LlmModelInfo model = registry.ListModels().Single(candidate => candidate.Key == "remote-vllm:sable-primary");
            Assert.IsTrue(LlmModelRegistry.IsChatLoadableModel(model));
            Assert.AreEqual(VllmAccelerationMode.DSpark, model.Acceleration);
            Assert.AreEqual("remote-vllm://sable-primary", model.FilePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LocalVllmBackend_AutoAddsDSparkSpeculativeConfig()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeepSeek-V4-Flash-DSpark");
        Directory.CreateDirectory(root);
        try
        {
            using var backend = new VllmBackend("dspark", root, new LlmLoadConfig(), new LlmRuntimeOptions { VllmExtraArguments = "--dtype auto" });
            string[] arguments = backend.BuildVllmArguments().ToArray();
            CollectionAssert.Contains(arguments, "--speculative-config");
            StringAssert.Contains(arguments[Array.IndexOf(arguments, "--speculative-config") + 1], "dspark");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StartAsync_RetriesStandardAfterDSparkFailure()
    {
        var runner = new QueueRunner(
            new(1, "", "unsupported speculative method dspark"),
            new(0, "stopped", ""),
            new(0, "started:42", ""),
            new(0, "ready:42", ""));
        var manager = new RemoteVllmManager(runner);
        RemoteVllmProfile profile = CreateProfile();

        RemoteVllmStatus result = await manager.StartAsync(profile);

        Assert.AreEqual("running", result.State);
        Assert.AreEqual("standard", result.Acceleration);
        Assert.AreEqual("fallback", result.AccelerationStatus);
        StringAssert.Contains(result.FallbackReason, "unsupported speculative method");
        Assert.AreEqual(42, result.ProcessId);
        Assert.AreEqual(4, runner.Commands.Count);
        StringAssert.Contains(runner.Commands[0], "--speculative-config");
        Assert.IsFalse(runner.Commands[2].Contains("--speculative-config", StringComparison.Ordinal));
    }

    private static RemoteVllmProfile CreateProfile() => new()
    {
        Id = "sable-primary",
        SshHost = "user@example-host",
        Model = "deepseek-ai/DeepSeek-V4-Flash-DSpark",
        PythonExecutable = "/opt/vllm/venv/bin/python",
        ApiPort = 12435,
        StartupTimeoutSeconds = 1
    };

    private sealed class QueueRunner(params RemoteCommandResult[] results) : IRemoteVllmCommandRunner
    {
        private readonly Queue<RemoteCommandResult> _results = new(results);
        public List<string> Commands { get; } = [];

        public Task<RemoteCommandResult> RunAsync(string host, string command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
