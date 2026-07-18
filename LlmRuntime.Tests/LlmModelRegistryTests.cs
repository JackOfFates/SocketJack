using LlmRuntime;
using LLama.Native;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmModelRegistryTests
{
    [TestMethod]
    public void ListModels_IgnoresPartialFilesAndReportsMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            string modelPath = Path.Combine(root, "Tiny-Llama-Q4_0.gguf");
            GgufMetadataReaderTests.WriteSyntheticGguf(modelPath);
            File.WriteAllText(Path.Combine(root, "Broken.gguf.partial"), "partial");

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root });

            var models = registry.ListModels();

            Assert.AreEqual(1, models.Count);
            Assert.AreEqual("Tiny-Llama-Q4_0", models[0].Key);
            Assert.AreEqual("llama", models[0].Architecture);
            Assert.AreEqual("Q4_0", models[0].QuantizationName);
            Assert.AreEqual((uint)4096, models[0].MaxContextLength);
            Assert.AreEqual("7.0B", models[0].ParamsString);
            Assert.IsNotNull(models[0].Tags);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void DownloadHelpers_NormalizeHuggingFaceAndExtractFileName()
    {
        string url = "https://huggingface.co/user/repo/blob/main/model.Q4_0.gguf";

        string normalized = ModelDownloadService.NormalizeDownloadUrl(url);

        Assert.AreEqual("model.Q4_0.gguf", ModelDownloadService.ExtractFileName(normalized));
        StringAssert.Contains(normalized, "/resolve/");
        StringAssert.Contains(normalized, "download=true");
    }

    [TestMethod]
    public void ListModels_IncludesLmStudioModelDirectoryAliasesWhenEnabled()
    {
        string root = CreateTempDirectory();
        string lmStudioRoot = CreateTempDirectory();
        string previousLmStudioModelsDir = Environment.GetEnvironmentVariable("LMSTUDIO_MODELS_DIR") ?? "";
        try
        {
            string alias = "socketjack-test-model-" + Guid.NewGuid().ToString("N")[..8];
            string modelPath = Path.Combine(lmStudioRoot, "socketjack", alias, "weights-Q4_0.gguf");
            string auxiliaryProjectionPath = Path.Combine(lmStudioRoot, "socketjack", alias, "mmproj-BF16.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            GgufMetadataReaderTests.WriteSyntheticGguf(modelPath);
            GgufMetadataReaderTests.WriteSyntheticGguf(auxiliaryProjectionPath);
            Environment.SetEnvironmentVariable("LMSTUDIO_MODELS_DIR", lmStudioRoot);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                IncludeLmStudioModels = true
            });

            var model = registry.FindModel(alias);

            Assert.IsNotNull(model);
            Assert.AreEqual(alias, model.Key);
            Assert.AreEqual("weights-Q4_0.gguf", model.FileName);
            Assert.AreEqual("lmstudio", model.Publisher);
            CollectionAssert.Contains(model.Aliases.ToList(), alias);
            Assert.IsFalse(registry.ListModels().Any(model => model.FileName.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("LMSTUDIO_MODELS_DIR", string.IsNullOrWhiteSpace(previousLmStudioModelsDir) ? null : previousLmStudioModelsDir);
            TryDeleteDirectory(root);
            TryDeleteDirectory(lmStudioRoot);
        }
    }

    [TestMethod]
    public void ListModels_IncludesLmStudioSettingsAndBundledDirectoriesWhenEnabled()
    {
        string root = CreateTempDirectory();
        string lmStudioHome = Path.Combine(root, ".lmstudio");
        string configuredModelsRoot = Path.Combine(root, "configured-models");
        string previousLmStudioHome = Environment.GetEnvironmentVariable("LMSTUDIO_HOME") ?? "";
        string previousLmStudioModelsDir = Environment.GetEnvironmentVariable("LMSTUDIO_MODELS_DIR") ?? "";
        try
        {
            string configuredAlias = "socketjack-configured-" + Guid.NewGuid().ToString("N")[..8];
            string bundledAlias = "socketjack-bundled-" + Guid.NewGuid().ToString("N")[..8];
            string configuredModelPath = Path.Combine(configuredModelsRoot, "publisher", configuredAlias, "configured-Q4_0.gguf");
            string bundledModelPath = Path.Combine(lmStudioHome, ".internal", "bundled-models", "publisher", bundledAlias, "bundled-Q4_0.gguf");
            Directory.CreateDirectory(Path.GetDirectoryName(configuredModelPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(bundledModelPath)!);
            GgufMetadataReaderTests.WriteSyntheticGguf(configuredModelPath);
            GgufMetadataReaderTests.WriteSyntheticGguf(bundledModelPath);
            Directory.CreateDirectory(lmStudioHome);
            File.WriteAllText(Path.Combine(lmStudioHome, "settings.json"), "{\"downloadsFolder\":\"" + configuredModelsRoot.Replace("\\", "\\\\") + "\"}");
            Environment.SetEnvironmentVariable("LMSTUDIO_HOME", lmStudioHome);
            Environment.SetEnvironmentVariable("LMSTUDIO_MODELS_DIR", null);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = Path.Combine(root, "local-models"),
                IncludeLmStudioModels = true
            });

            Assert.IsNotNull(registry.FindModel(configuredAlias));
            Assert.IsNotNull(registry.FindModel(bundledAlias));
            Assert.AreEqual("lmstudio", registry.FindModel(configuredAlias)!.Publisher);
            Assert.AreEqual("lmstudio", registry.FindModel(bundledAlias)!.Publisher);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LMSTUDIO_HOME", string.IsNullOrWhiteSpace(previousLmStudioHome) ? null : previousLmStudioHome);
            Environment.SetEnvironmentVariable("LMSTUDIO_MODELS_DIR", string.IsNullOrWhiteSpace(previousLmStudioModelsDir) ? null : previousLmStudioModelsDir);
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ModelHeuristics_DetectsCapabilityTagsFromNames()
    {
        CollectionAssert.AreEquivalent(
            new[] { "chat", "instruct", "code", "tool-use" },
            ModelHeuristics.DetectModelTags("Qwen3-Coder-Instruct-Tool-Use-Q4_K_M.gguf").ToArray());

        CollectionAssert.Contains(ModelHeuristics.DetectModelTags("nomic-embed-text-Q8_0.gguf").ToList(), "embedding");
        CollectionAssert.Contains(ModelHeuristics.DetectModelTags("llava-vision-Q4_0.gguf").ToList(), "vision");
    }

    [TestMethod]
    public void ModelHeuristics_DetectsMediaGgufsAsNonChat()
    {
        Assert.AreEqual("video", ModelHeuristics.DetectModelType(null, "Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf"));
        Assert.AreEqual("video", ModelHeuristics.DetectModelType(null, "motifv-2b-dev-BF16.gguf"));
        Assert.AreEqual("image", ModelHeuristics.DetectModelType(null, "flux-dev-Q4_K_M.gguf"));

        CollectionAssert.Contains(ModelHeuristics.DetectModelTags("Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf").ToList(), "video");
    }

    [TestMethod]
    public void Load_RejectsMediaGgufBeforeChatBackend()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf"), "wan2");
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root });

            var model = registry.ListModels().Single();
            Assert.AreEqual("video", model.Type);
            Assert.IsFalse(LlmModelRegistry.IsChatLoadableModel(model));
            Assert.IsFalse(LlmModelRegistry.IsRuntimeLoadableModel(model));

            var ex = Assert.ThrowsException<LlmRuntimeException>(() => registry.Load(model.Key));
            Assert.AreEqual("unsupported_model_type", ex.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_AllowsCompleteModelsMediaGgufAsRuntimeModel()
    {
        string root = CreateTempDirectory();
        string completeRoot = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(completeRoot, "owner", "wan-gguf", "main");
            Directory.CreateDirectory(modelDirectory);
            string ggufPath = Path.Combine(modelDirectory, "Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf");
            GgufMetadataReaderTests.WriteSyntheticGgufWithoutMetadata(ggufPath);
            string manifestPath = CompleteModelManifestWriter.WriteManifest(
                modelDirectory,
                "owner/wan-gguf",
                "main",
                "video",
                "gguf",
                ["Wan2.2-T2V-A14B-HighNoise-Q2_K.gguf"]);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = completeRoot
            });

            var ggufModel = registry.ListModels().Single(model => model.FilePath == ggufPath);
            Assert.AreEqual("video", ggufModel.Type);
            CollectionAssert.Contains(ggufModel.Tags.ToList(), "complete-model");
            Assert.IsFalse(LlmModelRegistry.IsChatLoadableModel(ggufModel));
            Assert.IsTrue(LlmModelRegistry.IsRuntimeLoadableModel(ggufModel));
            Assert.IsNull(LlmModelRegistry.GetRuntimeLoadDisabledReason(ggufModel));

            var manifestModel = registry.ListModels().Single(model => model.FilePath == manifestPath);
            Assert.AreEqual("gguf", manifestModel.Format);
            Assert.AreEqual("video", manifestModel.Type);
            Assert.IsTrue(LlmModelRegistry.IsRuntimeLoadableModel(manifestModel));

            LlmLoadResult result = registry.Load(ggufModel.Key, echoLoadConfig: true);
            Assert.AreEqual("video", result.Type);
            Assert.AreEqual("video_generation", result.ChatService);
            Assert.IsTrue(registry.Unload(ggufModel.Key));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(completeRoot);
        }
    }

    [TestMethod]
    public void ListModels_DetectsCompleteModelsGptqSafetensorsBundleAsVllmChat()
    {
        string root = CreateTempDirectory();
        string completeRoot = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(completeRoot, "Qwen2.5-72B-Instruct-GPTQ-Int4");
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "config.json"), """
                {
                  "architectures": [ "Qwen2ForCausalLM" ],
                  "model_type": "qwen2",
                  "max_position_embeddings": 32768,
                  "sliding_window": 131072,
                  "quantization_config": {
                    "bits": 4,
                    "quant_method": "gptq"
                  }
                }
                """);
            File.WriteAllText(Path.Combine(modelDirectory, "tokenizer_config.json"), """
                {
                  "model_max_length": 131072
                }
                """);
            File.WriteAllText(Path.Combine(modelDirectory, "model.safetensors.index.json"), "{\"metadata\":{},\"weight_map\":{}}");
            string shard1 = Path.Combine(modelDirectory, "model-00001-of-00002.safetensors");
            string shard2 = Path.Combine(modelDirectory, "model-00002-of-00002.safetensors");
            File.WriteAllBytes(shard1, [1, 2, 3, 4]);
            File.WriteAllBytes(shard2, [5, 6, 7, 8]);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = completeRoot
            }, new CapturingBackendFactory());

            var model = registry.ListModels().Single(candidate => candidate.FilePath == modelDirectory);
            Assert.AreEqual("Qwen2.5-72B-Instruct-GPTQ-Int4", model.DisplayName);
            Assert.AreEqual("safetensors", model.Format);
            Assert.AreEqual("Qwen2ForCausalLM", model.Architecture);
            Assert.AreEqual("gptq", model.QuantizationName);
            Assert.AreEqual((uint)131072, model.MaxContextLength);
            Assert.IsTrue(
                model.SizeBytes >= new FileInfo(shard1).Length + new FileInfo(shard2).Length,
                "Safetensors bundle size should include weight shards so large-model placement defaults can trigger.");
            CollectionAssert.Contains(model.Tags.ToList(), "complete-model");
            CollectionAssert.Contains(model.Tags.ToList(), "vllm");
            Assert.IsTrue(LlmModelRegistry.IsChatLoadableModel(model));
            Assert.IsTrue(LlmModelRegistry.IsRuntimeLoadableModel(model));
            Assert.AreEqual("chat_completion", LlmModelRegistry.GetRuntimeServiceForModel(model));

            LlmLoadResult result = registry.Load(model.Key, echoLoadConfig: true);
            Assert.AreEqual("chat_completion", result.ChatService);
            Assert.AreEqual(LlmBackendKind.Vllm, result.LoadConfig?.Backend);
            Assert.AreEqual((uint)131072, result.LoadConfig?.ContextLength);

            var loaded = registry.ListModels().Single(candidate => candidate.FilePath == modelDirectory);
            Assert.AreEqual(1, loaded.LoadedInstances.Count);
            Assert.AreEqual("vllm", loaded.LoadedInstances[0].Backend);
            Assert.AreEqual((uint)131072, loaded.LoadedInstances[0].Config.ContextLength);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(completeRoot);
        }
    }

    [TestMethod]
    public void VllmBackend_BuildArguments_UsesContextTensorParallelAndMemoryCap()
    {
        string root = CreateTempDirectory();
        try
        {
            using var backend = new VllmBackend(
                "Qwen2.5-72B-Instruct-GPTQ-Int4",
                root,
                new LlmLoadConfig
                {
                    ContextLength = 32768,
                    MaxVramUsagePercent = 100,
                    TensorParallelSize = 4,
                    TargetDeviceIds = ["cuda:0", "cuda:1", "cuda:2", "cuda:3"]
                },
                new LlmRuntimeOptions
                {
                    VllmBaseUrl = "http://127.0.0.1:8000",
                    VllmExtraArguments = "--dtype auto --enforce-eager"
                });

            string[] args = backend.BuildVllmArguments().ToArray();

            Assert.AreEqual("32768", ValueAfter(args, "--max-model-len"));
            Assert.AreEqual("4", ValueAfter(args, "--tensor-parallel-size"));
            Assert.AreEqual("32768", ValueAfter(args, "--max-num-batched-tokens"));
            Assert.AreEqual("0.9", ValueAfter(args, "--gpu-memory-utilization"));
            CollectionAssert.Contains(args.ToList(), "--enforce-eager");
            Assert.AreEqual(0.80d, VllmBackend.ResolveGpuMemoryUtilization(80), 0.001d);
        }
        finally
        {
            TryDeleteDirectory(root);
        }

        static string ValueAfter(IReadOnlyList<string> args, string option)
        {
            int index = args.ToList().FindIndex(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(index >= 0, "Missing option " + option + ".");
            Assert.IsTrue(index + 1 < args.Count, "Missing value for " + option + ".");
            return args[index + 1];
        }
    }

    [TestMethod]
    public void VllmBackend_LongContextOverrideRequiresTokenizerLimit()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "config.json"), """
                {
                  "max_position_embeddings": 32768
                }
                """);
            File.WriteAllText(Path.Combine(root, "tokenizer_config.json"), """
                {
                  "model_max_length": 131072
                }
                """);

            using var backend = new VllmBackend(
                "Qwen2.5-72B-Instruct-GPTQ-Int4",
                root,
                new LlmLoadConfig
                {
                    ContextLength = 131072
                },
                new LlmRuntimeOptions());

            Assert.IsTrue(backend.ShouldAllowLongMaxModelLenOverride());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void VllmBackend_LongContextCompatibilityProfileCapsPrefillAndAddsRuntimeFlags()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "config.json"), """
                {
                  "max_position_embeddings": 32768
                }
                """);
            File.WriteAllText(Path.Combine(root, "tokenizer_config.json"), """
                {
                  "model_max_length": 131072
                }
                """);

            using var backend = new VllmBackend(
                "Qwen2.5-72B-Instruct-GPTQ-Int4",
                root,
                new LlmLoadConfig
                {
                    ContextLength = 131072,
                    MaxVramUsagePercent = 100,
                    TensorParallelSize = 4
                },
                new LlmRuntimeOptions
                {
                    VllmExtraArguments = "--dtype auto --enforce-eager"
                });

            string[] args = backend.BuildVllmArguments().ToArray();

            Assert.IsTrue(backend.ShouldUseLongContextCompatibilityProfile());
            Assert.AreEqual("131072", ValueAfter(args, "--max-model-len"));
            Assert.AreEqual("32768", ValueAfter(args, "--max-num-batched-tokens"));
            Assert.AreEqual("0.66", ValueAfter(args, "--gpu-memory-utilization"));
            Assert.AreEqual("1", ValueAfter(args, "--max-num-seqs"));
            Assert.AreEqual("20", ValueAfter(args, "--cpu-offload-gb"));
            Assert.AreEqual("fp8_e5m2", ValueAfter(args, "--kv-cache-dtype"));
            CollectionAssert.Contains(args.ToList(), "--enable-chunked-prefill");
            CollectionAssert.Contains(args.ToList(), "--disable-custom-all-reduce");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_RejectsMetadataLessGgufBeforeChatBackend()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGgufWithoutMetadata(Path.Combine(root, "metadata-less-Q4_K_M.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root });

            var model = registry.ListModels().Single();
            Assert.IsNull(model.Architecture);
            Assert.IsFalse(LlmModelRegistry.IsChatLoadableModel(model));
            StringAssert.Contains(model.LoadDisabledReason ?? "", "general.architecture");

            var ex = Assert.ThrowsException<LlmRuntimeException>(() => registry.Load(model.Key));
            Assert.AreEqual("unsupported_model_metadata", ex.Code);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_DefaultAutoBackendResolvesForcedGpuBackend()
    {
        string root = CreateTempDirectory();
        string previousAutoBackend = Environment.GetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable) ?? "";
        try
        {
            Environment.SetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable, "vulkan");
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "SafeAuto-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());

            registry.Load("SafeAuto-Q4_0");

            var model = registry.ListModels().Single();
            Assert.AreEqual(LlmBackendKind.Vulkan, model.LoadedInstances[0].Config.Backend);
            Assert.AreEqual(-1, model.LoadedInstances[0].Config.GpuLayerCount);
            Assert.IsTrue(model.LoadedInstances[0].Config.OffloadKvCacheToGpu);
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable, string.IsNullOrWhiteSpace(previousAutoBackend) ? null : previousAutoBackend);
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_DefaultAutoBackendRejectsForcedCpuBackend()
    {
        string root = CreateTempDirectory();
        string previousAutoBackend = Environment.GetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable) ?? "";
        try
        {
            Environment.SetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable, "cpu");
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "UnsafeAuto-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());

            var ex = Assert.ThrowsException<LlmRuntimeException>(() => registry.Load("UnsafeAuto-Q4_0"));

            Assert.AreEqual("gpu_backend_required", ex.Code);
            StringAssert.Contains(ex.Message, "CPU fallback is disabled");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlmBackendAutoSelector.EnvironmentVariable, string.IsNullOrWhiteSpace(previousAutoBackend) ? null : previousAutoBackend);
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_ExplicitCpuBackendUsesZeroGpuLayerCount()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "CpuExplicit-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());

            registry.Load("CpuExplicit-Q4_0", backend: LlmBackendKind.Cpu);

            var model = registry.ListModels().Single();
            Assert.AreEqual(LlmBackendKind.Cpu, model.LoadedInstances[0].Config.Backend);
            Assert.AreEqual(0, model.LoadedInstances[0].Config.GpuLayerCount);
            Assert.IsFalse(model.LoadedInstances[0].Config.OffloadKvCacheToGpu);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_ExplicitGpuBackendKeepsDefaultFullOffload()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "GpuExplicit-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());

            registry.Load("GpuExplicit-Q4_0", backend: LlmBackendKind.Vulkan);

            var model = registry.ListModels().Single();
            Assert.AreEqual(LlmBackendKind.Vulkan, model.LoadedInstances[0].Config.Backend);
            Assert.AreEqual(-1, model.LoadedInstances[0].Config.GpuLayerCount);
            Assert.IsTrue(model.LoadedInstances[0].Config.OffloadKvCacheToGpu);
            Assert.IsFalse(model.LoadedInstances[0].Config.AllowBackendFallback);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_ReportsReadyProgressWhenBackendIsAlreadyReady()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "ReadyProgress-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());
            var progress = new List<LlmModelLoadProgress>();
            registry.ModelLoadProgressChanged += progress.Add;

            registry.Load("ReadyProgress-Q4_0");

            Assert.IsTrue(progress.Any(item => item.Percent == 100 && item.Status == "ready"));
            Assert.IsFalse(progress.Any(item => item.Status == "warming_prompt_pipeline"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RestorePersistedLoadedModelsAsync_LoadsSavedModelsFromRuntimeConfig()
    {
        string root = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(root, "LlmRuntime.config.json");
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Persisted-Q4_0.gguf"));
            var options = new LlmRuntimeOptions
            {
                ModelRoot = root,
                RuntimeConfigPath = configPath,
                RestoreLoadedModelsOnStartup = true
            };

            using (var registry = new LlmModelRegistry(options, new CapturingBackendFactory()))
            {
                registry.Load(
                    "Persisted-Q4_0",
                    contextLength: 2048,
                    gpuLayerCount: 24,
                    evalBatchSize: 256,
                    flashAttention: true,
                    offloadKvCacheToGpu: true,
                    backend: LlmBackendKind.Vulkan);
            }

            Assert.IsTrue(File.Exists(configPath));
            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                JsonElement saved = document.RootElement.GetProperty("loadedModels")[0];
                Assert.AreEqual("Persisted-Q4_0", saved.GetProperty("model").GetString());
                Assert.AreEqual("vulkan", saved.GetProperty("config").GetProperty("backend").GetString());
            }

            var progress = new List<LlmModelLoadProgress>();
            using var restored = new LlmModelRegistry(options, new CapturingBackendFactory());
            restored.ModelLoadProgressChanged += progress.Add;

            await restored.RestorePersistedLoadedModelsAsync();

            LlmModelInfo model = restored.ListModels().Single(candidate => candidate.Key == "Persisted-Q4_0");
            Assert.AreEqual(1, model.LoadedInstances.Count);
            Assert.AreEqual(LlmBackendKind.Vulkan, model.LoadedInstances[0].Config.Backend);
            Assert.AreEqual((uint)2048, model.LoadedInstances[0].Config.ContextLength);
            Assert.AreEqual(24, model.LoadedInstances[0].Config.GpuLayerCount);
            Assert.IsTrue(progress.Any(item => item.Percent == 0 && item.IsStartupRestore));
            Assert.IsTrue(progress.Any(item => item.Percent == 100 && item.IsStartupRestore));

            Assert.IsTrue(restored.Unload(model.Key));
            using JsonDocument unloadedDocument = JsonDocument.Parse(File.ReadAllText(configPath));
            Assert.AreEqual(0, unloadedDocument.RootElement.GetProperty("loadedModels").GetArrayLength());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RestorePersistedLoadedModelsAsync_DefaultDoesNotLoadSavedModels()
    {
        string root = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(root, "LlmRuntime.config.json");
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "PersistedOff-Q4_0.gguf"));
            var saveOptions = new LlmRuntimeOptions
            {
                ModelRoot = root,
                RuntimeConfigPath = configPath,
                RestoreLoadedModelsOnStartup = true
            };

            using (var registry = new LlmModelRegistry(saveOptions, new CapturingBackendFactory()))
            {
                registry.Load("PersistedOff-Q4_0", backend: LlmBackendKind.Vulkan);
            }

            using var restored = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                RuntimeConfigPath = configPath
            }, new CapturingBackendFactory());

            await restored.RestorePersistedLoadedModelsAsync();

            LlmModelInfo model = restored.ListModels().Single(candidate => candidate.Key == "PersistedOff-Q4_0");
            Assert.AreEqual(0, model.LoadedInstances.Count);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void RepositoryScanner_ClassifiesOnnxAndSourceTensorCandidates()
    {
        var scan = ModelRepositoryScanner.BuildResult("owner/repo", "main",
        [
            new ModelRepositoryFile { Path = "model-Q4_0.gguf", FileName = "model-Q4_0.gguf", SizeBytes = 1024, Format = ModelFileFormat.Gguf },
            new ModelRepositoryFile { Path = "onnx/model.onnx", FileName = "model.onnx", SizeBytes = 2048, Format = ModelFileFormat.Onnx },
            new ModelRepositoryFile { Path = "model.safetensors", FileName = "model.safetensors", SizeBytes = 4096, Format = ModelFileFormat.Safetensors },
            new ModelRepositoryFile { Path = "config.json", FileName = "config.json", SizeBytes = 256, Format = ModelFileFormat.Config },
            new ModelRepositoryFile { Path = "tokenizer.json", FileName = "tokenizer.json", SizeBytes = 512, Format = ModelFileFormat.Tokenizer }
        ]);

        Assert.IsTrue(scan.Candidates.Any(candidate => candidate.Action == "download_gguf" && candidate.CanDownload));
        Assert.IsTrue(scan.Candidates.Any(candidate => candidate.Action == "download_onnx" && candidate.CanDownload));
        Assert.IsTrue(scan.Candidates.Any(candidate => candidate.Action == "convert_onnx" && candidate.CanConvert));
    }

    [TestMethod]
    public async Task IdealModelScanner_GroupsHubSuggestionsByCategory()
    {
        using var httpClient = new HttpClient(new FakeHuggingFaceHandler());
        var scanner = new HuggingFaceIdealModelScanner(httpClient);

        IReadOnlyList<HuggingFaceIdealModelCategoryResult> categories = await scanner.ScanAsync(2);

        HuggingFaceIdealModelCategoryResult text = categories.Single(category => category.Id == "text");
        HuggingFaceIdealModelCategoryResult image = categories.Single(category => category.Id == "image");
        HuggingFaceIdealModelCategoryResult video = categories.Single(category => category.Id == "video");

        Assert.AreEqual("owner/runtime-gguf", text.Models[0].ModelId);
        Assert.AreEqual("Text", text.Models[0].CategoryLabel);
        Assert.AreEqual("https://huggingface.co/owner/runtime-gguf", text.Models[0].Url);
        StringAssert.Contains(text.Models[0].Reason, "GGUF");
        CollectionAssert.Contains(text.Models[0].Tags.ToList(), "gguf");

        Assert.AreEqual("owner/diffusers-image", image.Models[0].ModelId);
        Assert.AreEqual("diffusers", image.Models[0].LibraryName);
        StringAssert.Contains(image.Models[0].Reason, "Diffusers");

        Assert.AreEqual("cerspense/zeroscope_v2_576w", video.Models[0].ModelId);
        Assert.AreEqual("diffusers", video.Models[0].LibraryName);
        StringAssert.Contains(video.Models[0].Reason, "12 GB GPU");

        IReadOnlyList<HuggingFaceIdealModelCategoryResult> filtered = await scanner.ScanAsync(["image"], 2);

        Assert.AreEqual(1, filtered.Count);
        Assert.AreEqual("image", filtered[0].Id);
    }

    [TestMethod]
    public void RepositoryScanner_KeepsStandardGgufChatInModelsLayout()
    {
        var scan = ModelRepositoryScanner.BuildResult("owner/chat-repo", "main",
        [
            new ModelRepositoryFile { Path = "model-Q4_0.gguf", FileName = "model-Q4_0.gguf", SizeBytes = 1024, Format = ModelFileFormat.Gguf }
        ]);

        ModelFileCandidate candidate = scan.Candidates.Single(item => item.Action == "download_gguf");

        Assert.AreEqual("llm", candidate.Task);
        Assert.IsFalse(candidate.UsesCompleteModelLayout());
        Assert.AreEqual("Download GGUF", candidate.ActionLabel);
    }

    [TestMethod]
    public void RepositoryScanner_ClassifiesPytorchVideoBundleCandidate()
    {
        var scan = ModelRepositoryScanner.BuildResult("nvidia/video-pytorch", "main",
        [
            new ModelRepositoryFile { Path = "model_index.json", FileName = "model_index.json", SizeBytes = 128, Format = ModelFileFormat.Other },
            new ModelRepositoryFile { Path = "transformer/pytorch_model.bin", FileName = "pytorch_model.bin", SizeBytes = 4096, Format = ModelFileFormat.Pytorch },
            new ModelRepositoryFile { Path = "scheduler/scheduler_config.json", FileName = "scheduler_config.json", SizeBytes = 128, Format = ModelFileFormat.Other }
        ],
        new ModelRepositoryMetadata
        {
            PipelineTag = "text-to-video",
            LibraryName = "pytorch",
            Tags = ["video-generation"]
        });

        ModelFileCandidate candidate = scan.Candidates.Single(item => item.Action == "download_pytorch_bundle");

        Assert.AreEqual(ModelFileFormat.Pytorch, candidate.Format);
        Assert.AreEqual("video", candidate.Task);
        Assert.AreEqual("Download Video Generation Model", candidate.ActionLabel);
        Assert.AreEqual("nvidia/video-pytorch/main", candidate.TargetDirectoryName);
        CollectionAssert.Contains(candidate.Tags.ToList(), "video");
        CollectionAssert.Contains(candidate.SourcePaths.ToList(), "transformer/pytorch_model.bin");
    }

    [TestMethod]
    public void RepositoryScanner_ClassifiesPytorchAudioBundleCandidate()
    {
        var scan = ModelRepositoryScanner.BuildResult("kyutai/moshiko-pytorch-bf16", "main",
        [
            new ModelRepositoryFile { Path = "config.json", FileName = "config.json", SizeBytes = 128, Format = ModelFileFormat.Config },
            new ModelRepositoryFile { Path = "model.safetensors", FileName = "model.safetensors", SizeBytes = 4096, Format = ModelFileFormat.Safetensors },
            new ModelRepositoryFile { Path = "tokenizer.json", FileName = "tokenizer.json", SizeBytes = 128, Format = ModelFileFormat.Tokenizer }
        ]);

        ModelFileCandidate candidate = scan.Candidates.Single(item => item.Action == "download_pytorch_bundle");

        Assert.AreEqual("audio", candidate.Task);
        Assert.AreEqual("Download Audio Generation Model", candidate.ActionLabel);
        CollectionAssert.Contains(candidate.Tags.ToList(), "audio");
    }

    [TestMethod]
    public void RepositoryScanner_SharedVideoMemoryOversizeWarnsButAllowsDownload()
    {
        string root = CreateTempDirectory();
        try
        {
            var candidate = new ModelFileCandidate
            {
                Path = "owner/large-image",
                FileName = "owner_large-image",
                Repository = "owner/large-image",
                Revision = "main",
                SizeBytes = 64L * 1024 * 1024 * 1024,
                Format = ModelFileFormat.Pytorch,
                Task = "image",
                TargetDirectoryName = "owner/large-image/main",
                Action = "download_pytorch_bundle",
                ActionLabel = "Download Image Generation Model",
                CanDownload = true,
                Reason = "Ready."
            };

            candidate.ApplyFit(new ModelFitSnapshot
            {
                SharedVideoMemoryBytes = 8L * 1024 * 1024 * 1024,
                DriveFreeBytes = 256L * 1024 * 1024 * 1024
            }, root);

            Assert.IsTrue(candidate.CanDownload);
            Assert.IsFalse(candidate.CanConvert);
            Assert.IsTrue(candidate.IsWarning);
            Assert.AreEqual("download_pytorch_bundle", candidate.Action);
            Assert.AreEqual("Download Image Generation Model", candidate.ActionLabel);
            Assert.AreEqual("Larger than estimated shared video memory.", candidate.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void RepositoryScanner_DedicatedVideoMemoryOversizeUsesVramWarning()
    {
        string root = CreateTempDirectory();
        try
        {
            var candidate = new ModelFileCandidate
            {
                Path = "large-model.gguf",
                FileName = "large-model.gguf",
                Repository = "owner/large-model",
                Revision = "main",
                SizeBytes = 16L * 1024 * 1024 * 1024,
                Format = ModelFileFormat.Gguf,
                Action = "download_gguf",
                ActionLabel = "Download GGUF",
                CanDownload = true,
                Reason = "Ready GGUF file."
            };

            candidate.ApplyFit(new ModelFitSnapshot
            {
                SharedVideoMemoryBytes = 12L * 1024 * 1024 * 1024,
                VideoMemoryIsDedicated = true,
                DriveFreeBytes = 256L * 1024 * 1024 * 1024
            }, root);

            Assert.IsTrue(candidate.CanDownload);
            Assert.IsTrue(candidate.IsWarning);
            Assert.AreEqual("Larger than detected dedicated VRAM.", candidate.Reason);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void RepositoryScanner_LabelsLoraBundlesAsAdapters()
    {
        var scan = ModelRepositoryScanner.BuildResult("owner/image-lora", "main",
        [
            new ModelRepositoryFile { Path = "README.md", FileName = "README.md", SizeBytes = 128, Format = ModelFileFormat.Other },
            new ModelRepositoryFile { Path = "adapter.safetensors", FileName = "adapter.safetensors", SizeBytes = 4096, Format = ModelFileFormat.Safetensors }
        ],
        new ModelRepositoryMetadata
        {
            PipelineTag = "text-to-image",
            BaseModel = "owner/base-image",
            Tags = ["lora", "diffusers"]
        });

        ModelFileCandidate candidate = scan.Candidates.Single(item => item.Action == "download_pytorch_bundle");

        Assert.AreEqual("Download Image Generation Adapter", candidate.ActionLabel);
        StringAssert.Contains(candidate.Reason, "requires the base model owner/base-image");
        Assert.AreEqual("lora", candidate.AdapterType);
        Assert.AreEqual("owner/base-image", candidate.BaseModel);
        CollectionAssert.Contains(candidate.BaseModels.ToList(), "owner/base-image");
        CollectionAssert.Contains(candidate.Tags.ToList(), "lora");
        CollectionAssert.Contains(candidate.Tags.ToList(), "adapter");
    }

    [TestMethod]
    public void CompleteModelManifestWriter_CapturesLoraBaseModelMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), """
---
tags:
- text-to-image
- lora
base_model: owner/base-image
---
""");
            File.WriteAllBytes(Path.Combine(root, "adapter.safetensors"), [1, 2, 3, 4]);

            string manifestPath = CompleteModelManifestWriter.WriteManifest(root, "owner/adapter", "main", "image", "pytorch", ["README.md", "adapter.safetensors"]);
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement metadata = document.RootElement.GetProperty("metadata");

            Assert.AreEqual("lora", metadata.GetProperty("adapterType").GetString());
            Assert.AreEqual("true", metadata.GetProperty("adapterRequiresBaseModel").GetString());
            Assert.AreEqual("owner/base-image", metadata.GetProperty("baseModel").GetString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void CompleteModelManifestWriter_CapturesLoraBaseModelListMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), """
---
tags: [text-to-image, lora]
base_model:
- owner/base-image
- owner/alternate-base
---
""");
            File.WriteAllBytes(Path.Combine(root, "adapter.safetensors"), [1, 2, 3, 4]);

            string manifestPath = CompleteModelManifestWriter.WriteManifest(root, "owner/adapter", "main", "image", "pytorch", ["README.md", "adapter.safetensors"]);
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement metadata = document.RootElement.GetProperty("metadata");

            Assert.AreEqual("lora", metadata.GetProperty("adapterType").GetString());
            Assert.AreEqual("owner/base-image", metadata.GetProperty("baseModel").GetString());
            Assert.AreEqual("owner/base-image|owner/alternate-base", metadata.GetProperty("baseModels").GetString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void CompleteModelManifestWriter_CapturesNestedWanConfigMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(root, "owner", "wan-component", "main");
            string transformerDirectory = Path.Combine(modelDirectory, "transformer");
            Directory.CreateDirectory(transformerDirectory);
            WriteWanConfig(Path.Combine(transformerDirectory, "config.json"));
            File.WriteAllBytes(Path.Combine(transformerDirectory, "diffusion_pytorch_model.safetensors"), [1, 2, 3, 4]);

            string manifestPath = CompleteModelManifestWriter.WriteManifest(
                modelDirectory,
                "owner/wan-component",
                "main",
                null,
                "pytorch",
                ["transformer/config.json", "transformer/diffusion_pytorch_model.safetensors"]);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement metadata = document.RootElement.GetProperty("metadata");

            Assert.AreEqual("video.text-to-video", document.RootElement.GetProperty("type").GetString());
            Assert.AreEqual("WanModel", metadata.GetProperty("architecture").GetString());
            Assert.AreEqual("0.33.1", metadata.GetProperty("diffusersVersion").GetString());
            Assert.AreEqual("t2v", metadata.GetProperty("diffusersModelType").GetString());
            Assert.AreEqual("5120", metadata.GetProperty("wan_dim").GetString());
            Assert.AreEqual("40", metadata.GetProperty("wan_num_layers").GetString());
            Assert.AreEqual("WanPipeline", metadata.GetProperty("pipelineClass").GetString());
            Assert.AreEqual("Wan-AI/Wan2.1-T2V-14B-Diffusers", metadata.GetProperty("baseModel").GetString());
            Assert.AreEqual("Wan-AI/Wan2.1-T2V-14B-Diffusers", metadata.GetProperty("base_model").GetString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ListModels_InfersMissingWanBaseModelFromNestedConfig()
    {
        string root = CreateTempDirectory();
        string completeRoot = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(completeRoot, "owner", "wan-component", "main");
            string transformerDirectory = Path.Combine(modelDirectory, "transformer");
            Directory.CreateDirectory(transformerDirectory);
            WriteWanConfig(Path.Combine(transformerDirectory, "config.json"));
            File.WriteAllBytes(Path.Combine(transformerDirectory, "diffusion_pytorch_model.safetensors"), [1, 2, 3, 4]);
            string manifestPath = Path.Combine(modelDirectory, "manifest.json");
            File.WriteAllText(manifestPath, """
                {
                  "id": "owner-wan-component-pytorch",
                  "name": "owner wan-component",
                  "type": "llm.text-generation",
                  "format": "pytorch",
                  "components": {
                    "transformer_config": "transformer/config.json",
                    "transformer": "transformer/diffusion_pytorch_model.safetensors"
                  },
                  "metadata": {
                    "layout": "complete-model",
                    "source": "huggingface:owner/wan-component"
                  }
                }
                """);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = completeRoot
            });

            var model = registry.ListModels().Single(candidate => candidate.FilePath == manifestPath);

            Assert.AreEqual("video", model.Type);
            Assert.AreEqual("WanModel", model.Architecture);
            StringAssert.Contains(model.ParamsString, "dim 5120");
            Assert.AreEqual("Wan-AI/Wan2.1-T2V-14B-Diffusers", model.BaseModel);
            CollectionAssert.Contains(model.BaseModels.ToList(), "Wan-AI/Wan2.1-T2V-14B-Diffusers");
            Assert.IsFalse(registry.IsBaseModelAvailable(model));
            CollectionAssert.Contains(model.Tags.ToList(), "video");
            Assert.AreEqual("video_generation", LlmModelRegistry.GetRuntimeServiceForModel(model));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(completeRoot);
        }
    }

    [TestMethod]
    public void RepositoryScanner_LabelsOnnxMediaDownloadAsGenerationModel()
    {
        var scan = ModelRepositoryScanner.BuildResult("owner/video-onnx", "main",
        [
            new ModelRepositoryFile { Path = "onnx/text-to-video/model.onnx", FileName = "model.onnx", SizeBytes = 4096, Format = ModelFileFormat.Onnx }
        ],
        new ModelRepositoryMetadata
        {
            PipelineTag = "text-to-video",
            Tags = ["video-generation"]
        });

        ModelFileCandidate candidate = scan.Candidates.Single(item => item.Action == "download_onnx");

        Assert.AreEqual("video", candidate.Task);
        Assert.IsTrue(candidate.UsesCompleteModelLayout());
        Assert.AreEqual("Download Video Generation Model", candidate.ActionLabel);
        Assert.AreEqual("owner/video-onnx/main", candidate.TargetDirectoryName);
    }

    [TestMethod]
    public async Task DownloadService_DownloadsPytorchBundleToCompleteModelsAndWritesManifest()
    {
        string root = CreateTempDirectory();
        try
        {
            var service = new ModelDownloadService(root, handler: new FakeHuggingFaceHandler());

            await service.DownloadBundleAsync(new ModelBundleDownloadRequest
            {
                Repository = "owner/repo",
                Revision = "main",
                TargetRelativeDirectory = "owner/repo/main",
                Task = "video",
                TotalSizeBytes = 48,
                SourcePaths = ["config.json", "tokenizer.json", "model.safetensors"]
            });

            string manifestPath = Path.Combine(root, "owner", "repo", "main", "manifest.json");
            Assert.IsTrue(File.Exists(manifestPath));
            Assert.IsTrue(File.Exists(Path.Combine(root, "owner", "repo", "main", "model.safetensors")));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert.AreEqual("pytorch", document.RootElement.GetProperty("format").GetString());
            Assert.AreEqual("video.text-to-video", document.RootElement.GetProperty("type").GetString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task DownloadService_PersistsBundleBaseModelMetadata()
    {
        string root = CreateTempDirectory();
        try
        {
            var service = new ModelDownloadService(root, handler: new FakeHuggingFaceHandler());

            await service.DownloadBundleAsync(new ModelBundleDownloadRequest
            {
                Repository = "owner/repo",
                Revision = "main",
                TargetRelativeDirectory = "owner/repo/main",
                Task = "image",
                TotalSizeBytes = 48,
                SourcePaths = ["config.json", "model.safetensors"],
                Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["adapterType"] = "lora",
                    ["baseModel"] = "owner/base-image"
                }
            });

            string manifestPath = Path.Combine(root, "owner", "repo", "main", "manifest.json");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            JsonElement metadata = document.RootElement.GetProperty("metadata");

            Assert.AreEqual("lora", metadata.GetProperty("adapterType").GetString());
            Assert.AreEqual("true", metadata.GetProperty("adapterRequiresBaseModel").GetString());
            Assert.AreEqual("owner/base-image", metadata.GetProperty("baseModel").GetString());
            Assert.AreEqual("owner/base-image", metadata.GetProperty("base_model").GetString());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void Load_AllowsCompletePytorchGenerationManifestAsRuntimeModel()
    {
        string root = CreateTempDirectory();
        string completeRoot = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(completeRoot, "owner", "repo", "main");
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllText(Path.Combine(modelDirectory, "config.json"), "{}");
            File.WriteAllBytes(Path.Combine(modelDirectory, "pytorch_model.bin"), [1, 2, 3, 4]);
            string manifestPath = CompleteModelManifestWriter.WriteManifest(modelDirectory, "owner/repo", "main", "audio", "pytorch", ["config.json", "pytorch_model.bin"]);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = completeRoot
            });

            var model = registry.ListModels().Single(candidate => candidate.FilePath == manifestPath);
            Assert.AreEqual("pytorch", model.Format);
            Assert.AreEqual("audio", model.Type);
            CollectionAssert.Contains(model.Tags.ToList(), "pytorch");
            CollectionAssert.Contains(model.Tags.ToList(), "audio");
            CollectionAssert.Contains(model.Tags.ToList(), "complete-model");
            Assert.IsFalse(LlmModelRegistry.IsChatLoadableModel(model));
            Assert.IsTrue(LlmModelRegistry.IsRuntimeLoadableModel(model));

            LlmLoadResult result = registry.Load(model.Key, echoLoadConfig: true);

            Assert.AreEqual(model.Key, result.InstanceId);
            Assert.AreEqual("audio", result.Type);
            Assert.AreEqual("audio_generation", result.ChatService);
            Assert.IsNotNull(result.LoadConfig);

            var loaded = registry.ListModels().Single(candidate => candidate.FilePath == manifestPath);
            Assert.AreEqual(1, loaded.LoadedInstances.Count);
            Assert.IsTrue(registry.Unload(model.Key));
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(completeRoot);
        }
    }

    [TestMethod]
    public void ListModels_ClassifiesImageToVideoManifestAsVideoGeneration()
    {
        string root = CreateTempDirectory();
        string completeRoot = CreateTempDirectory();
        try
        {
            string modelDirectory = Path.Combine(completeRoot, "SulphurAI", "Sulphur-2-base", "main");
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllBytes(Path.Combine(modelDirectory, "sulphur_dev_fp8mixed.safetensors"), [1, 2, 3, 4]);
            string manifestPath = Path.Combine(modelDirectory, "manifest.json");
            File.WriteAllText(manifestPath, """
                {
                  "id": "SulphurAI-Sulphur-2-base-pytorch",
                  "name": "SulphurAI Sulphur-2-base",
                  "type": "video.image-to-video",
                  "format": "pytorch",
                  "precision": "f16",
                  "components": {
                    "sulphur_dev_fp8mixed": "sulphur_dev_fp8mixed.safetensors"
                  },
                  "recommendedProviders": [ "Cuda", "DirectML", "Cpu" ],
                  "metadata": {
                    "layout": "complete-model",
                    "source": "huggingface:SulphurAI/Sulphur-2-base"
                  }
                }
                """);

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions
            {
                ModelRoot = root,
                CompleteModelRoot = completeRoot
            });

            var model = registry.ListModels().Single(candidate => candidate.FilePath == manifestPath);
            Assert.AreEqual("video", model.Type);
            CollectionAssert.Contains(model.Tags.ToList(), "video");
            CollectionAssert.Contains(model.Tags.ToList(), "image");
            Assert.AreEqual("video_generation", LlmModelRegistry.GetRuntimeServiceForModel(model));
            Assert.IsTrue(LlmModelRegistry.IsRuntimeLoadableModel(model));

            LlmLoadResult result = registry.Load(model.Key, echoLoadConfig: true);
            Assert.AreEqual("video", result.Type);
            Assert.AreEqual("video_generation", result.ChatService);
        }
        finally
        {
            TryDeleteDirectory(root);
            TryDeleteDirectory(completeRoot);
        }
    }

    [TestMethod]
    public void Load_AllowsMultipleDeviceInstancesForSameModel()
    {
        string root = CreateTempDirectory();
        try
        {
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "Parallel-Q4_0.gguf"));
            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root }, new CapturingBackendFactory());

            LlmLoadResult first = registry.Load("Parallel-Q4_0", deviceId: "cuda:0", concurrencyLimit: 2, echoLoadConfig: true);
            LlmLoadResult second = registry.Load(
                "Parallel-Q4_0",
                deviceId: "cuda:1",
                concurrencyLimit: 3,
                parallelismMode: LlmParallelismMode.DataParallel,
                parallelismPlacement: LlmParallelismPlacement.Local,
                targetDeviceIds: ["cuda:0", "cuda:1"],
                maxGpuLoadPercent: 75,
                maxVramUsagePercent: 80,
                dataParallelReplicaCount: 2,
                echoLoadConfig: true);

            Assert.AreNotEqual(first.InstanceId, second.InstanceId);
            Assert.AreEqual("cuda:0", first.LoadConfig?.DeviceId);
            Assert.AreEqual("cuda:1", second.LoadConfig?.DeviceId);
            Assert.AreEqual(2, first.LoadConfig?.ConcurrencyLimit);
            Assert.AreEqual(3, second.LoadConfig?.ConcurrencyLimit);
            Assert.AreEqual(LlmParallelismMode.DataParallel, second.LoadConfig?.ParallelismMode);
            Assert.AreEqual(LlmParallelismPlacement.Local, second.LoadConfig?.ParallelismPlacement);
            Assert.AreEqual(75, second.LoadConfig?.MaxGpuLoadPercent);
            Assert.AreEqual(80, second.LoadConfig?.MaxVramUsagePercent);
            Assert.AreEqual(2, second.LoadConfig?.DataParallelReplicaCount);
            CollectionAssert.AreEquivalent(new[] { "cuda:0", "cuda:1" }, second.LoadConfig?.TargetDeviceIds.ToArray());

            LlmModelInfo loaded = registry.ListModels().Single(model => model.DisplayName.Contains("Parallel", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(2, loaded.LoadedInstances.Count);
            CollectionAssert.Contains(loaded.LoadedInstances.Select(instance => instance.DeviceId).ToList(), "cuda:0");
            CollectionAssert.Contains(loaded.LoadedInstances.Select(instance => instance.DeviceId).ToList(), "cuda:1");
            CollectionAssert.Contains(loaded.LoadedInstances.Select(instance => instance.ConcurrencyLimit).ToList(), 2);
            CollectionAssert.Contains(loaded.LoadedInstances.Select(instance => instance.ConcurrencyLimit).ToList(), 3);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RestorePersistedLoadedModelsAsync_RoundTripsTensorParallelConfig()
    {
        string root = CreateTempDirectory();
        try
        {
            string configPath = Path.Combine(root, "LlmRuntime.config.json");
            GgufMetadataReaderTests.WriteSyntheticGguf(Path.Combine(root, "TensorPersisted-Q4_0.gguf"));
            var options = new LlmRuntimeOptions
            {
                ModelRoot = root,
                RuntimeConfigPath = configPath,
                RestoreLoadedModelsOnStartup = true
            };

            using (var registry = new LlmModelRegistry(options, new CapturingBackendFactory()))
            {
                LlmLoadResult result = registry.Load(
                    "TensorPersisted-Q4_0",
                    parallelismMode: LlmParallelismMode.TensorParallel,
                    targetDeviceIds: ["cuda:0", "cuda:1"],
                    echoLoadConfig: true);

                Assert.AreEqual(LlmParallelismMode.TensorParallel, result.LoadConfig?.ParallelismMode);
                Assert.IsTrue(result.LoadConfig?.ParallelTensor);
                Assert.AreEqual(2, result.LoadConfig?.TensorParallelSize);
                CollectionAssert.AreEquivalent(new[] { "cuda:0", "cuda:1" }, result.LoadConfig?.TargetDeviceIds.ToArray());

                LlmModelInfo loaded = registry.ListModels().Single(candidate => candidate.Key == "TensorPersisted-Q4_0");
                Assert.AreEqual(1, loaded.LoadedInstances.Count);
                Assert.AreEqual(LlmParallelismMode.TensorParallel, loaded.LoadedInstances[0].Config.ParallelismMode);
                Assert.IsTrue(loaded.LoadedInstances[0].Config.ParallelTensor);
                Assert.AreEqual(2, loaded.LoadedInstances[0].Config.TensorParallelSize);
            }

            using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(configPath)))
            {
                JsonElement config = document.RootElement.GetProperty("loadedModels")[0].GetProperty("config");
                Assert.AreEqual("tensorParallel", config.GetProperty("parallelismMode").GetString());
                Assert.IsTrue(config.GetProperty("parallelTensor").GetBoolean());
                Assert.AreEqual(2, config.GetProperty("tensorParallelSize").GetInt32());
            }

            using var restored = new LlmModelRegistry(options, new CapturingBackendFactory());
            await restored.RestorePersistedLoadedModelsAsync();

            LlmModelInfo restoredModel = restored.ListModels().Single(candidate => candidate.Key == "TensorPersisted-Q4_0");
            Assert.AreEqual(1, restoredModel.LoadedInstances.Count);
            Assert.AreEqual(LlmParallelismMode.TensorParallel, restoredModel.LoadedInstances[0].Config.ParallelismMode);
            Assert.IsTrue(restoredModel.LoadedInstances[0].Config.ParallelTensor);
            Assert.AreEqual(2, restoredModel.LoadedInstances[0].Config.TensorParallelSize);
            CollectionAssert.AreEquivalent(new[] { "cuda:0", "cuda:1" }, restoredModel.LoadedInstances[0].Config.TargetDeviceIds.ToArray());
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void LlamaSharpBackend_ResolveTensorParallelSettings_UsesTensorSplitMode()
    {
        var settings = LlamaSharpBackend.ResolveTensorParallelSettings(new LlmLoadConfig
        {
            ParallelismMode = LlmParallelismMode.TensorParallel,
            ParallelTensor = true,
            TensorParallelSize = 2,
            TargetDeviceIds = ["cuda:0", "cuda:1"]
        });

        Assert.IsTrue(settings.Enabled);
        Assert.AreEqual(GPUSplitMode.Tensor, settings.SplitMode);
        Assert.AreEqual(0, settings.MainGpu);
        CollectionAssert.AreEqual(new[] { 1f, 1f }, settings.TensorSplits);
    }

    [TestMethod]
    public void ListModels_IncludesOnnxManifestButDoesNotLoadItAsGguf()
    {
        string root = CreateTempDirectory();
        try
        {
            string onnxPath = Path.Combine(root, "model.onnx");
            File.WriteAllBytes(onnxPath, [1, 2, 3, 4]);
            string manifestPath = OnnxModelManifestWriter.WriteSingleFileManifest(onnxPath, "owner/repo", "model.onnx", "main");

            using var registry = new LlmModelRegistry(new LlmRuntimeOptions { ModelRoot = root });

            var model = registry.ListModels().Single(candidate => candidate.FilePath == manifestPath);
            Assert.AreEqual("onnx", model.Format);
            Assert.AreEqual("llm", model.Type);
            Assert.AreEqual("model", model.DisplayName);
            CollectionAssert.Contains(model.Tags.ToList(), "onnx");
            Assert.ThrowsException<LlmRuntimeException>(() => registry.Load(model.Key));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ConversionService_CreatesTrackedFailClosedJobWhenWorkerMissing()
    {
        string root = CreateTempDirectory();
        string previousWorker = Environment.GetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER") ?? "";
        try
        {
            Environment.SetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER", null);
            using var httpClient = new HttpClient(new FakeHuggingFaceHandler());
            using var service = new ModelConversionService(root, httpClient: httpClient);

            var job = service.StartConversion(new ModelConversionRequest
            {
                Repository = "owner/repo",
                SourcePaths = ["model.safetensors"],
                Task = "text-generation",
                ConverterPath = "definitely-missing-socketjack-converter.exe",
                PythonPath = "definitely-missing-socketjack-python.exe"
            });

            SpinWait.SpinUntil(() => service.GetJob(job.JobId)?.Status == "failed", TimeSpan.FromSeconds(20));
            var completed = service.GetJob(job.JobId);

            Assert.IsNotNull(completed);
            Assert.AreEqual("failed", completed.Status);
            StringAssert.Contains(completed.Message, "ONNX conversion failed");
            Assert.IsTrue(File.Exists(Path.Combine(completed.OutputDirectory, "conversion.json")));
            Assert.AreEqual(1, service.ListJobs().Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LLMRUNTIME_ONNX_CONVERTER", string.IsNullOrWhiteSpace(previousWorker) ? null : previousWorker);
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void DownloadService_CleanupPartialFilesDeletesPartials()
    {
        string root = CreateTempDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "model.gguf.partial"), "partial");
            File.WriteAllText(Path.Combine(root, "model.gguf"), "complete");

            var service = new ModelDownloadService(root);

            Assert.AreEqual(1, service.CleanupPartialFiles());
            Assert.IsFalse(File.Exists(Path.Combine(root, "model.gguf.partial")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "model.gguf")));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void DirectMlRunnerDiscovery_FindsBundledRunnerName()
    {
        string root = CreateTempDirectory();
        try
        {
            string runnerPath = Path.Combine(root, "DirectML", "LlmRuntime.DirectMlRunner.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(runnerPath)!);
            File.WriteAllText(runnerPath, "stub");

            var status = DirectMlGgufRunnerDiscovery.GetStatus(new LlmRuntimeOptions { ToolRoot = root });

            Assert.IsTrue(status.Configured);
            Assert.AreEqual(runnerPath, status.RunnerPath);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [TestMethod]
    public void DirectMlBackend_PassesFallbackPolicyToRunner()
    {
        using var backend = new DirectMlGgufBackend(
            "model",
            @"C:\Models\model.gguf",
            new LlmLoadConfig { Backend = LlmBackendKind.DirectML, AllowBackendFallback = false },
            @"C:\Tools\DirectML\LlmRuntime.DirectMlRunner.exe",
            "");

        var method = typeof(DirectMlGgufBackend).GetMethod("BuildArguments", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.IsNotNull(method);
        string arguments = (string)method.Invoke(backend, [false])!;
        StringAssert.Contains(arguments, "--backend directml");
        StringAssert.Contains(arguments, "--allow-fallback false");
    }

    internal static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "llmruntime_models_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); } catch { }
    }

    private static string ValueAfter(IReadOnlyList<string> args, string option)
    {
        int index = args.ToList().FindIndex(value => string.Equals(value, option, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(index >= 0, "Missing option " + option + ".");
        Assert.IsTrue(index + 1 < args.Count, "Missing value for " + option + ".");
        return args[index + 1];
    }

    private static void WriteWanConfig(string path)
    {
        File.WriteAllText(path, """
            {
              "_class_name": "WanModel",
              "_diffusers_version": "0.33.1",
              "dim": 5120,
              "eps": 1e-06,
              "ffn_dim": 13824,
              "freq_dim": 256,
              "in_dim": 16,
              "model_type": "t2v",
              "num_heads": 40,
              "num_layers": 40,
              "out_dim": 16,
              "text_len": 512
            }
            """);
    }

    private sealed class FakeHuggingFaceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? "";
            if (url.Contains("/api/models?", StringComparison.OrdinalIgnoreCase))
            {
                string query = request.RequestUri?.Query ?? "";
                string json = "[]";
                if (query.Contains("pipeline_tag=text-generation", StringComparison.OrdinalIgnoreCase))
                {
                    json = """
                    [
                      {
                        "id": "owner/runtime-gguf",
                        "modelId": "owner/runtime-gguf",
                        "downloads": 1200,
                        "likes": 14,
                        "private": false,
                        "tags": [ "gguf", "text-generation", "endpoints_compatible" ],
                        "pipeline_tag": "text-generation"
                      },
                      {
                        "id": "owner/plain-transformers",
                        "downloads": 50,
                        "likes": 2,
                        "private": false,
                        "tags": [ "transformers", "text-generation" ],
                        "pipeline_tag": "text-generation",
                        "library_name": "transformers"
                      }
                    ]
                    """;
                }
                else if (query.Contains("pipeline_tag=text-to-image", StringComparison.OrdinalIgnoreCase))
                {
                    json = """
                    [
                      {
                        "id": "owner/diffusers-image",
                        "modelId": "owner/diffusers-image",
                        "downloads": 3000,
                        "likes": 30,
                        "private": false,
                        "tags": [ "diffusers", "safetensors", "text-to-image" ],
                        "pipeline_tag": "text-to-image",
                        "library_name": "diffusers"
                      }
                    ]
                    """;
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/api/models/cerspense/zeroscope_v2_576w", StringComparison.OrdinalIgnoreCase))
            {
                string json = """
                {
                  "id": "cerspense/zeroscope_v2_576w",
                  "modelId": "cerspense/zeroscope_v2_576w",
                  "downloads": 100,
                  "likes": 40,
                  "private": false,
                  "tags": [ "diffusers", "text-to-video", "safetensors" ],
                  "pipeline_tag": "text-to-video",
                  "library_name": "diffusers"
                }
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/api/models/owner/repo/tree/", StringComparison.OrdinalIgnoreCase))
            {
                string json = """
                [
                  { "type": "file", "path": "model.safetensors", "size": 16 },
                  { "type": "file", "path": "config.json", "size": 16 },
                  { "type": "file", "path": "tokenizer.json", "size": 16 }
                ]
                """;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (url.Contains("/resolve/", StringComparison.OrdinalIgnoreCase))
            {
                string content = url.Contains("config.json", StringComparison.OrdinalIgnoreCase)
                    ? "{\"model_type\":\"llama\"}"
                    : url.Contains("tokenizer.json", StringComparison.OrdinalIgnoreCase)
                        ? "{\"version\":\"1.0\"}"
                        : "fake tensor bytes";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content))
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class CapturingBackendFactory : ILlmBackendFactory
    {
        public ILlmBackend Create(string instanceId, string modelPath, LlmLoadConfig loadConfig) =>
            new CapturingBackend(instanceId, modelPath, loadConfig);
    }

    private sealed class CapturingBackend : ILlmBackend
    {
        public CapturingBackend(string instanceId, string modelPath, LlmLoadConfig loadConfig)
        {
            InstanceId = instanceId;
            ModelPath = modelPath;
            LoadConfig = loadConfig;
        }

        public string InstanceId { get; }

        public string ModelPath { get; }

        public LlmLoadConfig LoadConfig { get; }

        public bool IsPromptPipelineReady => true;

        public string PromptPipelineStatus => "ready";

        public string PromptPipelineDetail => "";

        public DateTimeOffset? PromptPipelineReadyAtUtc => DateTimeOffset.UtcNow;

        public double PromptPipelineWarmupSeconds => 0;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task WarmPromptPipelineAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<LlmChatResult> CompleteChatAsync(LlmChatRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmChatResult { Model = InstanceId, Content = "" });

        public async IAsyncEnumerable<LlmChatToken> StreamChatAsync(LlmChatRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
