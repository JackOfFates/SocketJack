using System.Text.Json;
using JackONNX;
using JackONNX.Cuda;
using JackONNX.DirectML;
using JackONNX.LlmRuntime;
using JackONNX.Runtime;
using JackONNX.SocketJack;
using LlmRuntime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SocketJack.Net;

namespace JackONNX.Tests;

[TestClass]
public sealed class JackOnnxRuntimeTests
{
    [TestMethod]
    public async Task Catalog_ListsSampleManifest()
    {
        string manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "JackONNX", "Samples", "Manifests", "sd15-example.jackonnx.json"));
        var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
        {
            ModelManifestPaths = [manifest]
        });

        var models = await runtime.ListModelsAsync();

        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("sd15-example", models[0].Id);
        Assert.AreEqual("image.text-to-image", models[0].Type);
        Assert.IsTrue(models[0].RecommendedProviders.Contains(JackOnnxExecutionProvider.DirectML));
    }

    [TestMethod]
    public async Task LlmRuntimeTools_ListDevicesAndModelsThroughInvoker()
    {
        string toolRoot = CreateTempDirectory();
        try
        {
            string manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "JackONNX", "Samples", "Manifests", "sd15-example.jackonnx.json"));
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ModelManifestPaths = [manifest]
            });

            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = toolRoot });
            registry.RegisterJackOnnxTools();
            var invoker = new LlmToolInvoker(registry, builtInTools: JackOnnxLlmRuntimeToolRegistration.CreateJackOnnxBuiltInTools(runtime));

            var devices = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "jackonnx_devices_list",
                Input = JsonDocument.Parse("{}").RootElement.Clone()
            });

            Assert.IsTrue(devices.Success, devices.Error);
            Assert.IsNotNull(devices.OutputJson);
            Assert.IsTrue(devices.OutputJson.Value.GetProperty("devices").GetArrayLength() >= 1);

            var models = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "jackonnx_models_list",
                Input = JsonDocument.Parse("{}").RootElement.Clone()
            });

            Assert.IsTrue(models.Success, models.Error);
            Assert.AreEqual(1, models.OutputJson!.Value.GetProperty("model_count").GetInt32());
            Assert.AreEqual("sd15-example", models.OutputJson.Value.GetProperty("models")[0].GetProperty("id").GetString());
        }
        finally
        {
            TryDeleteDirectory(toolRoot);
        }
    }

    [TestMethod]
    public async Task LlmRuntimeTool_ImageGenerateCreatesTrackableFailedJobWhenNoManifestIsRegistered()
    {
        string toolRoot = CreateTempDirectory();
        try
        {
            var runtime = JackOnnxRuntimeEngine.Create();
            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = toolRoot });
            registry.RegisterJackOnnxTools(new JackOnnxLlmRuntimeToolOptions
            {
                GenerationApprovalMode = LlmToolApprovalMode.AlwaysAllow
            });
            var invoker = new LlmToolInvoker(registry, builtInTools: JackOnnxLlmRuntimeToolRegistration.CreateJackOnnxBuiltInTools(runtime));

            var result = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "jackonnx_image_generate",
                Approved = true,
                Input = JsonDocument.Parse("""{"prompt":"a tiny test image","width":256,"height":256,"steps":1}""").RootElement.Clone()
            });

            Assert.IsFalse(result.Success);
            StringAssert.Contains(result.Error, "No JackONNX image model manifest is registered");
            Assert.IsNotNull(result.OutputJson);
            string jobId = result.OutputJson.Value.GetProperty("jobId").GetString() ?? "";
            Assert.IsFalse(string.IsNullOrWhiteSpace(jobId));

            var status = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "jackonnx_jobs_status",
                Input = JsonSerializer.SerializeToElement(new { jobId })
            });

            Assert.IsTrue(status.Success, status.Error);
            Assert.AreEqual("Failed", status.OutputJson!.Value.GetProperty("job").GetProperty("state").GetString());
        }
        finally
        {
            TryDeleteDirectory(toolRoot);
        }
    }

    [TestMethod]
    public async Task LlmRuntimeTool_ImageGeneratePassesSourceMediaFieldsToRequest()
    {
        string toolRoot = CreateTempDirectory();
        try
        {
            var runtime = JackOnnxRuntimeEngine.Create();
            JackOnnxGenerationRequest? capturedRequest = null;
            runtime.GenerationRequested += (_, request) => capturedRequest = request;

            var registry = new LlmToolRegistry(new LlmRuntimeOptions { ToolRoot = toolRoot });
            registry.RegisterJackOnnxTools(new JackOnnxLlmRuntimeToolOptions
            {
                GenerationApprovalMode = LlmToolApprovalMode.AlwaysAllow
            });
            var invoker = new LlmToolInvoker(registry, builtInTools: JackOnnxLlmRuntimeToolRegistration.CreateJackOnnxBuiltInTools(runtime));

            await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = "jackonnx_image_generate",
                Approved = true,
                Input = JsonDocument.Parse("""{"prompt":"restore this","sourcePath":"C:\\temp\\source.png","sourceMediaType":"image/png","sourceKind":"image","generationMode":"image-from-image"}""").RootElement.Clone()
            });

            Assert.IsInstanceOfType<ImageGenerationRequest>(capturedRequest);
            var imageRequest = (ImageGenerationRequest)capturedRequest!;
            Assert.AreEqual(@"C:\temp\source.png", imageRequest.SourcePath);
            Assert.AreEqual("image/png", imageRequest.SourceMediaType);
            Assert.AreEqual("image", imageRequest.SourceKind);
            Assert.AreEqual("image-from-image", imageRequest.GenerationMode);
        }
        finally
        {
            TryDeleteDirectory(toolRoot);
        }
    }

    [TestMethod]
    public void LlmRuntimeTools_DefineSourceMediaAndGeneralAudioGenerationInputs()
    {
        var definitions = JackOnnxLlmRuntimeToolRegistration.CreateDefinitions(new JackOnnxLlmRuntimeToolOptions
        {
            GenerationApprovalMode = LlmToolApprovalMode.AlwaysAllow
        });

        var image = definitions.Single(definition => definition.Name == "jackonnx_image_generate");
        Assert.IsTrue(image.InputSchema.TryGetProperty("properties", out JsonElement imageProperties));
        Assert.IsTrue(imageProperties.TryGetProperty("sourcePath", out _));
        Assert.IsTrue(imageProperties.TryGetProperty("generationMode", out _));

        var audio = definitions.Single(definition => definition.Name == "jackonnx_audio_generate");
        Assert.IsTrue(audio.InputSchema.TryGetProperty("properties", out JsonElement audioProperties));
        Assert.IsTrue(audioProperties.TryGetProperty("sourcePath", out _));
        Assert.IsTrue(audioProperties.TryGetProperty("sourceKind", out _));
    }

    [TestMethod]
    public async Task ImagePipeline_SavesArtifactWhenRunnerReturnsImageBytes()
    {
        string artifactRoot = CreateTempDirectory();
        string modelRoot = CreateTempDirectory();
        try
        {
            string modelFile = Path.Combine(modelRoot, "model_index.json");
            File.WriteAllText(modelFile, "{}");
            string manifestPath = Path.Combine(modelRoot, "manifest.jackonnx.json");
            File.WriteAllText(manifestPath, """
{
  "id": "fake-diffusers-image",
  "name": "Fake Diffusers Image",
  "type": "image.text-to-image",
  "format": "pytorch",
  "components": {
    "modelIndex": "model_index.json"
  },
  "recommendedProviders": [
    "Cpu"
  ]
}
""");
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ArtifactRoot = artifactRoot,
                ModelManifestPaths = [manifestPath]
            });
            var pipeline = new JackONNX.Image.JackOnnxImagePipeline(runtime, new FakeImageRunner());

            var result = await pipeline.GenerateAsync(new ImageGenerationRequest
            {
                ModelId = "fake-diffusers-image",
                Prompt = "artifact please",
                Width = 65,
                Height = 65,
                Steps = 1,
                Seed = 123
            });

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(1, result.Artifacts.Count);
            Assert.AreEqual("image/png", result.Artifacts[0].MediaType);
            Assert.AreEqual("fake-diffusers-image", result.Artifacts[0].Metadata["modelId"]);
            Assert.AreEqual("64", result.Artifacts[0].Metadata["width"]);

            var content = await runtime.ReadArtifactAsync(result.Artifacts[0].Id);
            Assert.IsNotNull(content);
            CollectionAssert.AreEqual(FakePngBytes, content.Data);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
            TryDeleteDirectory(modelRoot);
        }
    }

    [TestMethod]
    public void PythonImageRunner_UsesBomSafeJsonHandoff()
    {
        var runnerType = typeof(JackONNX.Image.JackOnnxPythonDiffusersImageRunner);
        var scriptField = runnerType.GetField("PythonRunnerScript", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(scriptField);
        string script = (string)(scriptField.GetRawConstantValue() ?? "");
        StringAssert.Contains(script, "encoding=\"utf-8-sig\"");
        StringAssert.Contains(script, "LoRA adapter and requires the base model");
        StringAssert.Contains(script, "get_base_model_candidates");
        StringAssert.Contains(script, "manifest_metadata");
        StringAssert.Contains(script, "select_torch_device");
        StringAssert.Contains(script, "CUDA GPU is available, but this Python has a CPU-only PyTorch build");
        StringAssert.Contains(script, "preferred_provider");
        StringAssert.Contains(script, "supported_minor");
        StringAssert.Contains(script, "AutoPipelineForImage2Image");
        StringAssert.Contains(script, "load_source_image");
        StringAssert.Contains(script, "image-to-image");
        StringAssert.Contains(script, "patch_huggingface_hub_cached_download");
        StringAssert.Contains(script, "cached_download compatibility shim");
        StringAssert.Contains(script, "hf_hub_download");
        StringAssert.Contains(script, "def requested_image_device_map");
        StringAssert.Contains(script, "def diffusers_load_kwargs");
        StringAssert.Contains(script, "device_map");
        StringAssert.Contains(script, "max_memory");
        StringAssert.Contains(script, "maybe_move_pipeline_to_device");

        var qwenDiffusersVersionField = runnerType.GetField("MinimumQwenDiffusersVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(qwenDiffusersVersionField);
        Assert.AreEqual("0.35.0", qwenDiffusersVersionField.GetRawConstantValue());

        var peftVersionField = runnerType.GetField("MinimumPeftVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(peftVersionField);
        Assert.AreEqual("0.10.0", peftVersionField.GetRawConstantValue());

        var legacyHubVersionField = runnerType.GetField("LegacyMinimumHuggingFaceHubVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(legacyHubVersionField);
        Assert.AreEqual("0.34.0", legacyHubVersionField.GetRawConstantValue());

        var legacyHubMaximumVersionField = runnerType.GetField("LegacyMaximumHuggingFaceHubVersionExclusive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(legacyHubMaximumVersionField);
        Assert.AreEqual("1.0.0", legacyHubMaximumVersionField.GetRawConstantValue());

        var modernHubVersionField = runnerType.GetField("ModernMinimumHuggingFaceHubVersion", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(modernHubVersionField);
        Assert.AreEqual("1.5.0", modernHubVersionField.GetRawConstantValue());

        var modernHubMaximumVersionField = runnerType.GetField("ModernMaximumHuggingFaceHubVersionExclusive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(modernHubMaximumVersionField);
        Assert.AreEqual("2.0.0", modernHubMaximumVersionField.GetRawConstantValue());

        var encodingField = runnerType.GetField("Utf8NoBom", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(encodingField);
        var encoding = (System.Text.Encoding?)encodingField.GetValue(null);
        Assert.IsNotNull(encoding);
        Assert.AreEqual(0, encoding.GetPreamble().Length);
    }

    [TestMethod]
    public async Task VideoPipeline_LoadsSyntheticDiffusersGgufManifest()
    {
        string artifactRoot = CreateTempDirectory();
        string modelRoot = CreateTempDirectory();
        try
        {
            string ggufPath = Path.Combine(modelRoot, "ltx-video-2b-v0.9-Q4_K_M.gguf");
            File.WriteAllBytes(ggufPath, [0x47, 0x47, 0x55, 0x46, 1, 0, 0, 0]);
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ArtifactRoot = artifactRoot,
                ModelManifestPaths = [modelRoot]
            });

            var models = await runtime.ListModelsAsync();
            JackOnnxModelManifest manifest = models.Single(model => model.Format == "gguf");
            Assert.AreEqual("video.text-to-video", manifest.Type);
            Assert.AreEqual("LTXPipeline", manifest.Metadata["pipelineClass"]);
            Assert.AreEqual("Lightricks/LTX-Video", manifest.Metadata["baseModel"]);
            Assert.AreEqual("ltx-video-2b-v0.9-Q4_K_M.gguf", manifest.Components["transformer"]);

            var pipeline = new JackONNX.Video.JackOnnxVideoPipeline(runtime, new FakeVideoRunner());
            var result = await pipeline.GenerateAsync(new VideoGenerationRequest
            {
                ModelId = "ltx-video-2b-v0.9-Q4_K_M",
                Prompt = "tiny video",
                Width = 64,
                Height = 64,
                Seconds = 1,
                Fps = 1,
                Frames = 1,
                Steps = 12
            });

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual("video/mp4", result.Artifacts[0].MediaType);
            Assert.AreEqual(manifest.Id, result.Artifacts[0].Metadata["modelId"]);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
            TryDeleteDirectory(modelRoot);
        }
    }

    [TestMethod]
    public async Task VideoPipeline_CapsHighFpsDurationToFrameLimit()
    {
        string artifactRoot = CreateTempDirectory();
        string modelRoot = CreateTempDirectory();
        try
        {
            string ggufPath = Path.Combine(modelRoot, "ltx-video-2b-v0.9-Q4_K_M.gguf");
            File.WriteAllBytes(ggufPath, [0x47, 0x47, 0x55, 0x46, 1, 0, 0, 0]);
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ArtifactRoot = artifactRoot,
                ModelManifestPaths = [modelRoot]
            });

            var runner = new CapturingVideoRunner();
            var pipeline = new JackONNX.Video.JackOnnxVideoPipeline(runtime, runner);
            var result = await pipeline.GenerateAsync(new VideoGenerationRequest
            {
                ModelId = "ltx-video-2b-v0.9-Q4_K_M",
                Prompt = "high fps tiny video",
                Width = 64,
                Height = 64,
                Seconds = 30,
                Fps = 15,
                Steps = 12
            });

            Assert.IsTrue(result.Success, result.Message);
            Assert.AreEqual(96, runner.LastFrames);
            Assert.AreEqual(15, runner.LastFps);
            Assert.AreEqual(6.4, runner.LastSeconds, 0.001);
            Assert.AreEqual("96", result.Artifacts[0].Metadata["frames"]);
            Assert.AreEqual("6.4", result.Artifacts[0].Metadata["seconds"]);
            Assert.AreEqual("450", result.Artifacts[0].Metadata["requestedFrames"]);
            StringAssert.Contains(result.Message, "capped this run to 96 frames");
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
            TryDeleteDirectory(modelRoot);
        }
    }

    [TestMethod]
    public async Task VideoPipeline_RejectsTinySuccessfulMp4Artifact()
    {
        string artifactRoot = CreateTempDirectory();
        string modelRoot = CreateTempDirectory();
        try
        {
            string ggufPath = Path.Combine(modelRoot, "ltx-video-2b-v0.9-Q4_K_M.gguf");
            File.WriteAllBytes(ggufPath, [0x47, 0x47, 0x55, 0x46, 1, 0, 0, 0]);
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ArtifactRoot = artifactRoot,
                ModelManifestPaths = [modelRoot]
            });

            var pipeline = new JackONNX.Video.JackOnnxVideoPipeline(runtime, new TinyInvalidVideoRunner());
            var result = await pipeline.GenerateAsync(new VideoGenerationRequest
            {
                ModelId = "ltx-video-2b-v0.9-Q4_K_M",
                Prompt = "tiny invalid video",
                Width = 64,
                Height = 64,
                Seconds = 1,
                Fps = 1,
                Frames = 1,
                Steps = 12
            });

            Assert.IsFalse(result.Success, result.Message);
            Assert.IsTrue(result.Message.Contains("too small", StringComparison.OrdinalIgnoreCase), result.Message);
            Assert.AreEqual(0, result.Artifacts.Count);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
            TryDeleteDirectory(modelRoot);
        }
    }

    [TestMethod]
    public void PythonVideoRunner_IncludesDiffusersGgufLoader()
    {
        var runnerType = typeof(JackONNX.Video.JackOnnxPythonDiffusersVideoRunner);
        var scriptField = runnerType.GetField("PythonRunnerScript", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(scriptField);
        string script = (string)(scriptField.GetRawConstantValue() ?? "");

        StringAssert.Contains(script, "GGUFQuantizationConfig");
        StringAssert.Contains(script, "AutoModel.from_single_file");
        StringAssert.Contains(script, "load_diffusers_gguf_pipeline");
        StringAssert.Contains(script, "transformer_2");
        StringAssert.Contains(script, "WanPipeline");
        StringAssert.Contains(script, "LTXPipeline");
        StringAssert.Contains(script, "load_source_image");
        StringAssert.Contains(script, "source_video_reference");
        StringAssert.Contains(script, "does not accept source image/video input");
        StringAssert.Contains(script, "LoRA adapter and requires the base model");
        StringAssert.Contains(script, "get_base_model_candidates");
        StringAssert.Contains(script, "adapterType");
        StringAssert.Contains(script, "load_lora_weights");
        StringAssert.Contains(script, "patch_huggingface_hub_cached_download");
        StringAssert.Contains(script, "cached_download compatibility shim");
        StringAssert.Contains(script, "hf_hub_download");
        StringAssert.Contains(script, "huggingface-hub>=0.34.0,<1.0");
        StringAssert.Contains(script, "def normalize_video_device_map");
        StringAssert.Contains(script, "auto\", \"balanced_low_0");
        StringAssert.Contains(script, "return \"balanced\"");
    }

    [TestMethod]
    public void PythonImageRunner_ReportsCompatibilityRepairForGpuTorchFailure()
    {
        var runnerType = typeof(JackONNX.Image.JackOnnxPythonDiffusersImageRunner);
        var method = runnerType.GetMethod("BuildGpuGenerationDisabledDetail", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);

        var status = new global::LlmRuntime.LlmRuntimeCompatibilityStatus
        {
            Message = "Python has a CPU-only PyTorch build.",
            Diagnostics = new global::LlmRuntime.LlmRuntimeCompatibilityDiagnostics
            {
                RecommendedPytorch = new global::LlmRuntime.LlmPytorchPackageRecommendation
                {
                    PytorchVersion = "2.12",
                    CudaVersion = "12.6",
                    IndexUrl = "https://download.pytorch.org/whl/cu126"
                }
            }
        };

        string detail = (string)(method.Invoke(null, new object[] { status }) ?? "");
        StringAssert.Contains(detail, "GPU image generation is disabled");
        StringAssert.Contains(detail, "/api/v1/runtime/compatibility/repair-pytorch");
        StringAssert.Contains(detail, "CPU fallback is disabled");
        StringAssert.Contains(detail, "https://developer.nvidia.com/cuda-downloads");
    }

    [TestMethod]
    public void PythonImageRunner_FinalFailureDistinguishesPipelineFailureFromMissingPython()
    {
        var runnerType = typeof(JackONNX.Image.JackOnnxPythonDiffusersImageRunner);
        var method = runnerType.GetMethod("IsPeftDependencyFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);

        bool isPeftFailure = (bool)(method.Invoke(null, new object[] { "PEFT backend is required for this method." }) ?? false);
        Assert.IsTrue(isPeftFailure);

        var pipelineFailureMethod = runnerType.GetMethod("IsPythonPipelineDependencyFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(pipelineFailureMethod);
        bool isPipelineFailure = (bool)(pipelineFailureMethod.Invoke(null, new object[] { "ValueError: PEFT backend is required for this method." }) ?? false);
        Assert.IsTrue(isPipelineFailure);
    }

    [TestMethod]
    public void PythonImageRunner_DetectsDiffusersSchedulerCompatibilityFailure()
    {
        var runnerType = typeof(JackONNX.Image.JackOnnxPythonDiffusersImageRunner);
        var method = runnerType.GetMethod("IsDiffusersVersionCompatibilityFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(method);

        const string detail = "AttributeError: module diffusers has no attribute EDMDPMSolverMultistepScheduler";
        bool isDiffusersFailure = (bool)(method.Invoke(null, new object[] { detail }) ?? false);
        Assert.IsTrue(isDiffusersFailure);

        var pipelineFailureMethod = runnerType.GetMethod("IsPythonPipelineDependencyFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.IsNotNull(pipelineFailureMethod);
        bool isPipelineFailure = (bool)(pipelineFailureMethod.Invoke(null, new object[] { detail }) ?? false);
        Assert.IsTrue(isPipelineFailure);
    }

    [TestMethod]
    public async Task SocketJackHandlers_ReturnStatusAndModelsJson()
    {
        string manifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "JackONNX", "Samples", "Manifests", "sd15-example.jackonnx.json"));
        var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
        {
            ModelManifestPaths = [manifest]
        });
        var handlers = new JackOnnxSocketJackHandlers(runtime);

        using var statusDocument = JsonDocument.Parse(await handlers.StatusAsync());
        Assert.AreEqual("JackONNX", statusDocument.RootElement.GetProperty("service").GetString());
        Assert.AreEqual(1, statusDocument.RootElement.GetProperty("model_count").GetInt32());

        using var modelsDocument = JsonDocument.Parse(await handlers.ModelsAsync());
        Assert.AreEqual("sd15-example", modelsDocument.RootElement.GetProperty("models")[0].GetProperty("id").GetString());
    }

    [TestMethod]
    public void SocketJackRoutes_ExposeVideoPresentationEndpoint()
    {
        string routeList = string.Join("|", JackOnnxSocketJackRoutes.All);
        StringAssert.Contains(routeList, "/api/jackonnx/video/presentation");
    }

    [TestMethod]
    public async Task ArtifactStore_PersistsAndSocketJackServesContent()
    {
        string artifactRoot = CreateTempDirectory();
        try
        {
            var runtime = JackOnnxRuntimeEngine.Create(new JackOnnxOptions
            {
                ArtifactRoot = artifactRoot
            });
            var job = runtime.CreateJob(JackOnnxMediaKind.Log, new ImageGenerationRequest
            {
                Prompt = "artifact test"
            });

            var artifact = await runtime.SaveArtifactAsync(
                job.Id,
                JackOnnxMediaKind.Log,
                "result.json",
                "application/json",
                System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}"));

            var content = await runtime.ReadArtifactAsync(artifact.Id);
            Assert.IsNotNull(content);
            Assert.AreEqual("{\"ok\":true}", System.Text.Encoding.UTF8.GetString(content.Data));

            var handlers = new JackOnnxSocketJackHandlers(runtime);
            var response = await handlers.ArtifactResponseAsync(artifact.Id);
            Assert.IsInstanceOfType<FileResponse>(response);
            var file = (FileResponse)response;
            Assert.AreEqual("application/json", file.ContentType);
            Assert.AreEqual("result.json", file.FileName);
        }
        finally
        {
            TryDeleteDirectory(artifactRoot);
        }
    }

    [TestMethod]
    public void ProgressHistory_RecordsJobLifecycleEvents()
    {
        var runtime = JackOnnxRuntimeEngine.Create();
        var job = runtime.CreateJob(JackOnnxMediaKind.Image, new ImageGenerationRequest
        {
            Prompt = "progress test"
        });

        runtime.UpdateJobProgress(job.Id, new JackOnnxProgress
        {
            State = JackOnnxJobState.Running,
            Percent = 50,
            Message = "Halfway"
        });
        runtime.CompleteJob(job.Id, new JackOnnxGenerationResult
        {
            JobId = job.Id,
            Success = true,
            Message = "Done"
        });

        var history = runtime.GetProgressHistory(job.Id);
        Assert.IsTrue(history.Count >= 3);
        Assert.AreEqual(JackOnnxJobState.Queued, history[0].State);
        Assert.IsTrue(history.Any(item => item.State == JackOnnxJobState.Running && Math.Abs(item.Percent - 50) < 0.01));
        Assert.AreEqual(JackOnnxJobState.Completed, history[^1].State);
    }

    [TestMethod]
    public void CpuSessionFactory_RunsTinyIdentityModel()
    {
        string modelPath = Path.Combine(CreateTempDirectory(), "identity.onnx");
        try
        {
            File.WriteAllBytes(modelPath, TinyOnnxIdentityModel.CreateModelBytes());
            using var session = new OnnxRuntimeSessionFactory().CreateCpuSession(modelPath);
            var tensor = new DenseTensor<float>(new[] { 1 });
            tensor[0] = 42.5f;
            using var results = session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input", tensor)
            ]);

            float value = results.Single().AsTensor<float>().Single();
            Assert.AreEqual(42.5f, value);
        }
        finally
        {
            TryDeleteDirectory(Path.GetDirectoryName(modelPath) ?? "");
        }
    }

    [TestMethod]
    [TestCategory("HardwareIntegration")]
    public void DirectMlProvider_RunsTinyIdentityModelWhenNativeRuntimeAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            Assert.Inconclusive("DirectML requires Windows 10 version 1809 or newer.");

        var provider = new DirectMlExecutionProvider();
        JackOnnxProviderCompatibility compatibility = provider.CheckCompatibility();
        if (!compatibility.IsAvailable || !compatibility.CanCreateSessionOptions)
            Assert.Inconclusive("DirectML native runtime is unavailable: " + compatibility.Detail);

        RunTinyIdentityModelWithSessionOptions(() => provider.CreateSessionOptions());
    }

    [TestMethod]
    [TestCategory("HardwareIntegration")]
    public async Task CudaProvider_RunsTinyIdentityModelWhenNativeRuntimeAvailable()
    {
        var provider = new CudaExecutionProvider();
        IReadOnlyList<JackOnnxDeviceInfo> devices = await provider.ProbeDevicesAsync();
        if (!devices.Any(device => device.IsAvailable))
            Assert.Inconclusive("CUDA device probe found no available NVIDIA GPU: " + string.Join("; ", devices.Select(device => device.Detail)));

        JackOnnxProviderCompatibility compatibility = provider.CheckCompatibility();
        if (!compatibility.IsAvailable || !compatibility.CanCreateSessionOptions)
            Assert.Inconclusive("CUDA native runtime is unavailable: " + compatibility.Detail);

        RunTinyIdentityModelWithSessionOptions(() => provider.CreateSessionOptions());
    }

    private static void RunTinyIdentityModelWithSessionOptions(Func<SessionOptions> createOptions)
    {
        string modelPath = Path.Combine(CreateTempDirectory(), "identity.onnx");
        try
        {
            File.WriteAllBytes(modelPath, TinyOnnxIdentityModel.CreateModelBytes());
            using var options = createOptions();
            using var session = new InferenceSession(modelPath, options);
            var tensor = new DenseTensor<float>(new[] { 1 });
            tensor[0] = 42.5f;
            using var results = session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input", tensor)
            ]);

            float value = results.Single().AsTensor<float>().Single();
            Assert.AreEqual(42.5f, value);
        }
        finally
        {
            TryDeleteDirectory(Path.GetDirectoryName(modelPath) ?? "");
        }
    }

    private static string CreateTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "jackonnx-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
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

    private static readonly byte[] FakePngBytes =
    [
        0x89, 0x50, 0x4E, 0x47,
        0x0D, 0x0A, 0x1A, 0x0A
    ];

    private sealed class FakeImageRunner : JackONNX.Image.IJackOnnxImageModelRunner
    {
        public Task<JackONNX.Image.JackOnnxImageModelOutput> GenerateAsync(
            JackOnnxModelManifest manifest,
            ImageGenerationRequest request,
            string jobId,
            JackOnnxOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new JackONNX.Image.JackOnnxImageModelOutput
            {
                Data = FakePngBytes,
                FileName = "fake.png",
                MediaType = "image/png",
                Message = "Fake image generated.",
                Runner = "test.fake"
            });
        }
    }

    private sealed class FakeVideoRunner : JackONNX.Video.IJackOnnxVideoModelRunner
    {
        public Task<JackONNX.Video.JackOnnxVideoModelOutput> GenerateAsync(
            JackOnnxModelManifest manifest,
            VideoGenerationRequest request,
            string jobId,
            JackOnnxOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new JackONNX.Video.JackOnnxVideoModelOutput
            {
                Data = CreateFakeMp4Bytes(),
                FileName = "fake.mp4",
                MediaType = "video/mp4",
                Message = "Fake video generated.",
                Runner = "test.fake.gguf"
            });
        }
    }

    private sealed class CapturingVideoRunner : JackONNX.Video.IJackOnnxVideoModelRunner
    {
        public int LastFrames { get; private set; }

        public int LastFps { get; private set; }

        public double LastSeconds { get; private set; }

        public Task<JackONNX.Video.JackOnnxVideoModelOutput> GenerateAsync(
            JackOnnxModelManifest manifest,
            VideoGenerationRequest request,
            string jobId,
            JackOnnxOptions options,
            CancellationToken cancellationToken = default)
        {
            LastFrames = request.Frames.GetValueOrDefault();
            LastFps = request.Fps;
            LastSeconds = request.Seconds;
            return Task.FromResult(new JackONNX.Video.JackOnnxVideoModelOutput
            {
                Data = CreateFakeMp4Bytes(),
                FileName = "fake.mp4",
                MediaType = "video/mp4",
                Message = "Fake video generated.",
                Runner = "test.fake.capture"
            });
        }
    }

    private sealed class TinyInvalidVideoRunner : JackONNX.Video.IJackOnnxVideoModelRunner
    {
        public Task<JackONNX.Video.JackOnnxVideoModelOutput> GenerateAsync(
            JackOnnxModelManifest manifest,
            VideoGenerationRequest request,
            string jobId,
            JackOnnxOptions options,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new JackONNX.Video.JackOnnxVideoModelOutput
            {
                Data = [0, 0, 0, 24, 102, 116, 121, 112, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                FileName = "tiny.mp4",
                MediaType = "video/mp4",
                Message = "Fake tiny video generated.",
                Runner = "test.fake.tiny"
            });
        }
    }

    private static byte[] CreateFakeMp4Bytes()
    {
        byte[] data = new byte[2048];
        data[0] = 0;
        data[1] = 0;
        data[2] = 0;
        data[3] = 24;
        data[4] = (byte)'f';
        data[5] = (byte)'t';
        data[6] = (byte)'y';
        data[7] = (byte)'p';
        data[8] = (byte)'i';
        data[9] = (byte)'s';
        data[10] = (byte)'o';
        data[11] = (byte)'m';
        return data;
    }

    private static class TinyOnnxIdentityModel
    {
        public static byte[] CreateModelBytes()
        {
            byte[] input = CreateValueInfo("input");
            byte[] output = CreateValueInfo("output");
            byte[] node = CreateNode();
            byte[] opset = Concat(FieldString(1, ""), FieldVarint(2, 13));
            byte[] graph = Concat(
                FieldBytes(1, node),
                FieldString(2, "jackonnx_identity_graph"),
                FieldBytes(11, input),
                FieldBytes(12, output));

            return Concat(
                FieldVarint(1, 8),
                FieldString(2, "JackONNX.Tests"),
                FieldBytes(7, graph),
                FieldBytes(8, opset));
        }

        private static byte[] CreateNode()
        {
            return Concat(
                FieldString(1, "input"),
                FieldString(2, "output"),
                FieldString(3, "identity"),
                FieldString(4, "Identity"));
        }

        private static byte[] CreateValueInfo(string name)
        {
            byte[] dim = FieldVarint(1, 1);
            byte[] shape = FieldBytes(1, dim);
            byte[] tensorType = Concat(
                FieldVarint(1, 1),
                FieldBytes(2, shape));
            byte[] type = FieldBytes(1, tensorType);
            return Concat(
                FieldString(1, name),
                FieldBytes(2, type));
        }

        private static byte[] FieldString(int fieldNumber, string value)
        {
            return FieldBytes(fieldNumber, System.Text.Encoding.UTF8.GetBytes(value));
        }

        private static byte[] FieldBytes(int fieldNumber, byte[] value)
        {
            return Concat(Varint((uint)((fieldNumber << 3) | 2)), Varint((uint)value.Length), value);
        }

        private static byte[] FieldVarint(int fieldNumber, long value)
        {
            return Concat(Varint((uint)(fieldNumber << 3)), Varint((ulong)value));
        }

        private static byte[] Varint(ulong value)
        {
            var bytes = new List<byte>();
            do
            {
                byte current = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0)
                    current |= 0x80;
                bytes.Add(current);
            }
            while (value != 0);

            return bytes.ToArray();
        }

        private static byte[] Concat(params byte[][] parts)
        {
            byte[] bytes = new byte[parts.Sum(part => part.Length)];
            int offset = 0;
            foreach (byte[] part in parts)
            {
                Buffer.BlockCopy(part, 0, bytes, offset, part.Length);
                offset += part.Length;
            }

            return bytes;
        }
    }
}
