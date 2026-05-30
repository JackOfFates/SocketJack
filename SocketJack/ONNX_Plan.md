# JackONNX Project Plan

JackONNX should be a separate .NET library that gives SocketJack a local generative media engine: image, audio, and video generation powered by ONNX Runtime with selectable DirectML, CUDA, and CPU backends.

## Primary Goal

Create a production-ready inference library that can:

- Load ONNX models for generative AI.
- Run on NVIDIA CUDA when available.
- Run on DirectML for broad Windows GPU support.
- Fall back to CPU cleanly.
- Expose simple high-level APIs for image, audio, and video generation.
- Connect directly with `llmruntime` so LLM workflows can call local media generation as tools, return generated artifacts, and stream progress back into chat/agent sessions.
- Stream progress, previews, logs, and artifacts back through SocketJack/JackLLM.

## Recommended Project Shape

```text
JackONNX/
  JackONNX.sln
  src/
    JackONNX.Core/
    JackONNX/Runtime/
    JackONNX/Providers/DirectML/
    JackONNX/Providers/Cuda/
    JackONNX.Models/
    JackONNX/Media/Image/
    JackONNX/Media/Audio/
    JackONNX/Media/Video/
    JackONNX/Integrations/LlmRuntime/
    JackONNX/Integrations/SocketJack/
  samples/
    JackONNX/Samples/
  tests/
    JackONNX.Tests/
    JackONNX.IntegrationTests/
  docs/
    model-format.md
    providers.md
    pipelines.md
```

## Single Project Layout

Keep JackONNX as one source project with domain folders and namespaces. Native provider references stay optional at runtime so users do not accidentally activate every native runtime:

| Area | Purpose |
|---|---|
| `JackONNX/Abstractions` | Shared abstractions, tensors, model metadata, cancellation, progress, logging. |
| `JackONNX/Runtime` | ONNX Runtime session management, provider selection, memory pooling. |
| `JackONNX/Providers/DirectML` | DirectML execution provider integration for Windows GPUs. |
| `JackONNX/Providers/Cuda` | CUDA execution provider integration for NVIDIA GPUs. |
| `JackONNX/Media/Image` | Stable Diffusion-style image pipelines. |
| `JackONNX/Media/Audio` | TTS, audio generation, vocoder, transcription-ready utilities. |
| `JackONNX/Media/Video` | Frame generation, animation, interpolation, video assembly. |
| `JackONNX/Integrations/LlmRuntime` | Adapter that exposes JackONNX generation as `llmruntime` tools, capabilities, jobs, and artifact outputs. |
| `JackONNX/Integrations/SocketJack` | SocketJack/JackLLM integration layer. |

## Runtime Backend Plan

Execution providers should be selected through one clean API:

```csharp
var engine = JackOnnxEngine.Create(new JackOnnxOptions
{
    PreferredProvider = ExecutionProvider.Auto,
    DevicePolicy = DevicePolicy.PreferGpuThenCpu,
    ModelCachePath = "./models",
    EnableMemoryPattern = true,
    EnableIoBinding = true
});
```

Provider priority:

1. CUDA, if NVIDIA GPU and compatible ONNX Runtime GPU package are available.
2. DirectML, if running on Windows with DirectML-capable GPU.
3. CPU fallback.

Core provider features:

- Device discovery.
- VRAM estimation.
- Provider health check.
- Per-model provider compatibility.
- CPU fallback with warning.
- Session warmup.
- Session reuse.
- Tensor/memory pooling.
- ONNX Runtime I/O binding where supported.
- FP16 support where model/provider allows it.
- Optional INT8/quantized model support.

## Image Generation Features

Start with Stable Diffusion-style pipelines because ONNX support is mature and the user value is immediate.

Phase 1 image support:

- Text-to-image.
- Image-to-image.
- Inpainting.
- Negative prompts.
- Seeded deterministic generation.
- CFG scale.
- Scheduler selection.
- Width/height validation.
- Batch generation.
- Progress callbacks per diffusion step.
- Low-memory mode.
- VAE slicing/tiling.
- Safety checker as optional pluggable component.

Pipeline pieces:

```text
Tokenizer
Text Encoder
UNet / Transformer Denoiser
Scheduler
VAE Decoder
Postprocessor
```

Best-fit advanced features:

- LoRA merge/import workflow.
- ControlNet support.
- IP-Adapter style image conditioning.
- Upscaling pipeline.
- Face restore hook, optional external model.
- Prompt preset system.
- Model manifest files.
- Preview image streaming during generation.

## Audio Generation Features

Audio support should be modular because models differ a lot.

Phase 1 audio support:

- Text-to-speech pipeline.
- Voice model loading.
- Speaker embedding support.
- WAV/PCM output.
- Streaming chunk output.
- Sample-rate conversion.
- Loudness normalization.
- Silence trimming.
- Prompt-to-audio pipeline interface, even if first implementation starts with TTS.

Suggested model categories:

- Piper/VITS-style ONNX TTS.
- Vocoder-backed audio generation.
- Audio enhancement models.
- Future: music/SFX generation if suitable ONNX exports are available.

API shape:

```csharp
var wav = await audio.GenerateSpeechAsync(new SpeechRequest
{
    Text = "Hello from JackONNX.",
    Voice = "en-us-default",
    Speed = 1.0f,
    Seed = 42
});
```

## Video Generation Features

Video should begin as a composition system over image models, then grow into native video models.

Phase 1 video support:

- Text-to-video interface.
- Image-to-video interface.
- Frame sequence generation.
- Frame interpolation.
- Optical-flow/interpolation model support.
- MP4/WebM assembly through pluggable encoder.
- Progress callbacks per frame.
- Preview frame streaming.
- Resume/cancel long generations.
- Temporary artifact cleanup.

Good first implementation path:

1. Generate keyframes with image pipeline.
2. Interpolate between frames.
3. Optionally upscale/enhance frames.
4. Encode to video.

Later native model support:

- Stable Video Diffusion ONNX pipeline.
- AnimateDiff-style pipeline.
- Video inpainting.
- Camera motion presets.
- Motion strength controls.

## Model Management

JackONNX needs a strong model layer. This will save pain later.

Model manifest example:

```json
{
  "id": "sd15-directml-fp16",
  "name": "Stable Diffusion 1.5 FP16",
  "type": "image.text-to-image",
  "format": "onnx",
  "precision": "fp16",
  "components": {
    "tokenizer": "tokenizer/",
    "textEncoder": "text_encoder/model.onnx",
    "unet": "unet/model.onnx",
    "vaeDecoder": "vae_decoder/model.onnx"
  },
  "recommendedProviders": ["cuda", "directml", "cpu"],
  "defaultWidth": 512,
  "defaultHeight": 512
}
```

Model manager features:

- Local model registry.
- Local manifest registration for model files that already exist on disk.
- Manifest validation.
- File hashing.
- Versioning.
- Model compatibility check.
- Required RAM/VRAM metadata.
- Model conversion documentation from Hugging Face/Diffusers to ONNX.
- Optional encrypted/private model storage later.

## SocketJack Integration

This is where JackONNX becomes powerful inside the existing ecosystem.

Add the `JackONNX.SocketJack` integration namespace inside the single JackONNX project with:

- HTTP routes for generation.
- WebSocket streaming for progress/previews.
- Artifact serving.
- Job queue.
- Cancellation endpoints.
- Device/model listing endpoints.
- Health/status endpoint.
- Usage/cost accounting hooks.
- Remote generation worker support.

Suggested routes:

```text
GET  /api/jackonnx/status
GET  /api/jackonnx/devices
GET  /api/jackonnx/models
POST /api/jackonnx/image/generate
POST /api/jackonnx/audio/generate
POST /api/jackonnx/video/generate
GET  /api/jackonnx/jobs/{id}
POST /api/jackonnx/jobs/{id}/cancel
GET  /api/jackonnx/artifacts/{id}
WS   /api/jackonnx/jobs/{id}/stream
```

## llmruntime Integration

JackONNX and `llmruntime` should be connected as sibling runtime libraries. `llmruntime` should own LLM orchestration, tool calling, chat/session state, and agent workflows. JackONNX should own ONNX model loading, provider selection, GPU execution, media pipelines, and generated artifacts. The connection point should be an adapter namespace, `JackONNX.LlmRuntime`, inside the single JackONNX project, so either library can evolve without creating a hard circular dependency.

The integration should make JackONNX available to `llmruntime` as first-class multimodal tools:

```text
jackonnx.devices.list
jackonnx.models.list
jackonnx.image.generate
jackonnx.image.edit
jackonnx.audio.speech
jackonnx.audio.generate
jackonnx.video.generate
jackonnx.jobs.status
jackonnx.jobs.cancel
```

Connection features:

- Register JackONNX capabilities in the `llmruntime` tool/capability registry.
- Let `llmruntime` route prompts, tool calls, and agent plans into JackONNX generation jobs.
- Return generated images, audio, and videos as `llmruntime` artifact/content parts instead of only file paths.
- Share cancellation tokens, progress events, logs, and job ids across both runtimes.
- Allow LLM agents to inspect available JackONNX devices, models, providers, estimated memory, and queue status before choosing a media action.
- Support streamed previews back into `llmruntime` sessions, including intermediate images, audio chunks, and video frames.
- Preserve stable request/response DTOs so SocketJack, `llmruntime`, and standalone JackONNX callers use the same generation contracts.
- Add policy hooks so `llmruntime` can enforce permissions, cost limits, safety settings, and filesystem/artifact access before a JackONNX job starts.

Suggested adapter API:

```csharp
var jack = await JackOnnx.CreateAsync();

llmRuntime.Tools.RegisterJackOnnx(jack, new JackOnnxToolOptions
{
    EnableImageGeneration = true,
    EnableAudioGeneration = true,
    EnableVideoGeneration = true,
    ReturnArtifacts = true,
    StreamPreviews = true
});
```

Suggested event bridge:

```text
llmruntime tool call -> JackONNX job queued
JackONNX progress -> llmruntime session event
JackONNX preview artifact -> llmruntime content part
JackONNX completed artifact -> llmruntime tool result
llmruntime cancellation -> JackONNX cancellation token
```

The first connected workflow should be: user asks an LLM to create an image, `llmruntime` plans or invokes `jackonnx.image.generate`, JackONNX runs the ONNX pipeline on CUDA/DirectML/CPU, previews stream into the session, and the final PNG is returned as a `llmruntime` artifact plus a SocketJack-served URL when hosted.

## JackLLM Changeover

The JackLLM should gradually change over from directly owning LLM/tool runtime behavior to presenting and controlling `llmruntime` state. JackONNX should plug into that same path, so image, audio, and video generation appear as `llmruntime` capabilities instead of a separate one-off media system.

Changeover goals:

- Show `llmruntime` as the active local runtime behind chat, tools, agent actions, and media generation.
- Surface JackONNX capabilities through `llmruntime`, including available providers, models, queue status, running jobs, previews, and final artifacts.
- Keep existing JackLLM affordances for approvals, permissions, terminal/tool safety, sessions, diagnostics, and remote admin, but route runtime decisions through `llmruntime`.
- Add GUI status for `llmruntime` connection health, tool schema refresh, runtime version, enabled providers, and JackONNX media availability.
- Update labels and diagnostics so users understand the stack as `JackLLM -> llmruntime -> JackONNX/LLM tools/providers`.
- Preserve backward-compatible routes while the GUI migrates, especially where existing clients still call JackLLM APIs directly.

Suggested GUI areas to update:

- Chat/model/tool status panels.
- Permission and approval dialogs for tool calls.
- Diagnostics/runtime health views.
- Artifact browser and generated-media previews.
- Job/progress surfaces for long-running image, audio, and video generation.
- Remote admin views that need to expose runtime/tool availability.

The PmVsProxy/SocketJack-MagicMasterListServer list may not need a direct schema or route change. Treat it as a compatibility review first: only extend the list entries if servers need to advertise `llmruntime` availability, JackONNX media capabilities, GPU/provider metadata, queue capacity, or pricing/cost hints for remote generation. If the list is only for server discovery and health, keep it stable and avoid adding migration churn.

## Job System

Generation tasks can be long-running, so treat everything as a job.

Job states:

```text
Queued
LoadingModel
Running
StreamingPreview
Encoding
Completed
Cancelled
Failed
```

Each job should track:

- Request parameters.
- Provider used.
- Model used.
- Seed.
- Start/end time.
- Progress percentage.
- Current step/frame/sample.
- Output artifacts.
- Logs/warnings.
- Error details.
- Resource usage estimate.
- Optional `llmruntime` session id, tool call id, parent run id, and artifact ids when the job was started from an LLM workflow.

## Public API Design

Keep the top-level API friendly:

```csharp
await using var jack = await JackOnnx.CreateAsync();

var image = await jack.Images.GenerateAsync(new ImageGenerationRequest
{
    Prompt = "a clean futuristic workstation",
    NegativePrompt = "blurry, distorted",
    Width = 1024,
    Height = 1024,
    Steps = 30,
    Seed = 1234
});

var speech = await jack.Audio.GenerateSpeechAsync(new SpeechRequest
{
    Text = "SocketJack now speaks.",
    Voice = "default"
});

var video = await jack.Video.GenerateAsync(new VideoGenerationRequest
{
    Prompt = "a rotating glass server tower",
    Seconds = 4,
    Fps = 24,
    Width = 768,
    Height = 432
});
```

Connected `llmruntime` usage should stay just as direct:

```csharp
await llmRuntime.Tools.InvokeAsync("jackonnx.image.generate", new
{
    prompt = "a clean futuristic workstation",
    width = 1024,
    height = 1024,
    steps = 30
});
```

## Performance Features

Important from the start:

- Session reuse.
- Lazy model loading.
- Model unload policy.
- Memory pressure detection.
- Tensor pooling.
- Streamed output.
- FP16-first GPU path.
- Optional CPU quantized models.
- Scheduler optimization.
- Batch control.
- Warmup runs.
- Provider benchmark command.
- Telemetry counters.

## Developer Tools

Include CLI or sample console commands:

```text
jackonnx devices
jackonnx models list
jackonnx models validate ./models/sd15/manifest.json
jackonnx benchmark --model sd15 --provider auto
jackonnx image --prompt "..." --out image.png
jackonnx speech --text "..." --out speech.wav
jackonnx video --prompt "..." --out clip.mp4
```

## Testing Plan

Core tests:

- Manifest parsing.
- Provider selection.
- CPU fallback.
- Tensor shape validation.
- Scheduler math.
- Cancellation.
- Job lifecycle.
- Artifact cleanup.
- Deterministic seed behavior where possible.

Integration tests:

- Tiny ONNX model inference.
- DirectML provider smoke test on Windows.
- CUDA provider smoke test when available.
- Image pipeline with miniature/dummy model.
- SocketJack route/job streaming test.

## Milestones

1. **Foundation**
   - Create solution and project structure.
   - Add core abstractions.
   - Add model manifest system.
   - Add provider selection.

2. **ONNX Runtime Layer**
   - Implement session factory.
   - Add DirectML provider package.
   - Add CUDA provider package.
   - Add CPU fallback.
   - Add diagnostics and benchmark utilities.

3. **Image MVP**
   - Implement text-to-image pipeline.
   - Add scheduler abstraction.
   - Add tokenizer/text encoder/UNet/VAE orchestration.
   - Save PNG outputs.
   - Stream progress.

4. **SocketJack Bridge**
   - Add generation job queue.
   - Add HTTP/WebSocket APIs.
   - Add artifact serving.
   - Add status/device/model routes.

5. **llmruntime Connection**
   - Add the `JackONNX.LlmRuntime` integration namespace inside the single JackONNX project.
   - Register JackONNX as `llmruntime` tools/capabilities.
   - Bridge job progress, preview artifacts, cancellation, and final results into `llmruntime` sessions.
   - Add policy hooks for permissions, cost limits, and artifact access.
   - Plan the JackLLM changeover so chat, tools, approvals, diagnostics, and media generation are presented as `llmruntime`-backed surfaces.
   - Review the JackLLM/SocketJack-MagicMasterList list for optional `llmruntime` and JackONNX capability metadata, but leave it unchanged if discovery already has enough information.

6. **Audio MVP**
   - Add TTS pipeline.
   - Add WAV output.
   - Add streaming audio chunks.

7. **Video MVP**
   - Add frame generation pipeline.
   - Add interpolation/enhancement hooks.
   - Add MP4/WebM encoding adapter.
   - Stream preview frames.

8. **Advanced Media Features**
   - Inpainting.
   - Image-to-image.
   - Upscaling.
   - LoRA merge support.
   - ControlNet support.
   - Video model support.

9. **Production Polish**
   - NuGet packaging.
   - Docs.
   - Samples.
   - Benchmarks.
   - Error messages.
   - Model compatibility matrix.

## Best First Version

For `v0.1`, keep the target tight:

- `JackONNX` single project
- `JackONNX.Runtime` namespace
- `JackONNX.DirectML` namespace
- `JackONNX.Cuda` namespace
- `JackONNX.Image` namespace
- `JackONNX.LlmRuntime` namespace
- `JackONNX.SocketJack` namespace
- Text-to-image generation
- `llmruntime` tool registration for image generation, model listing, job status, and cancellation
- JackLLM changeover notes for showing JackONNX through `llmruntime`
- Model manifests
- Device/provider detection
- Job queue
- WebSocket progress streaming
- PNG artifact output

That gives the system its spine. Audio and video can then plug into the same runtime, model registry, job system, progress stream, and artifact store without reinventing the engine.
