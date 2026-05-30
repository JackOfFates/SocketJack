# JackONNX Progress

This file tracks total JackONNX project creation progress. Update it whenever a project, feature, integration, or milestone materially changes.

## Overall

| Area | Progress | Bar | Notes |
|---|---:|---|---|
| Total project creation | 100% | `[####################]` | The JackONNX project foundation is created: projects, contracts, local manifest catalog, providers, jobs, artifacts, progress streaming, LlmRuntime bridge, SocketJack bridge, CLI, docs, samples, and tests are in place. |

## Milestone Progress

| Milestone | Progress | Bar | Current State |
|---|---:|---|---|
| Planning | 100% | `[####################]` | ONNX plan exists and includes DirectML/CUDA, media generation, LlmRuntime connection, GUI changeover, and MasterServer review. |
| Project scaffolding | 100% | `[####################]` | JackONNX project family created and added to the main solution. |
| Core abstractions | 100% | `[####################]` | Options, providers, manifests, artifacts, jobs, cancellation/status, requests, progress contracts, and provider compatibility contracts are in place. |
| ONNX Runtime layer | 100% | `[####################]` | Runtime package reference, CPU session factory, catalog shell, job lookup/cancel/update, artifact store, progress history, provider checks, clean build, and CPU ONNX smoke test are in place. |
| DirectML provider | 100% | `[####################]` | DirectML package reference, provider scaffold, session options factory, Windows baseline probe, fail-closed compatibility check, and clean build are in place. |
| CUDA provider | 100% | `[####################]` | CUDA package reference, provider scaffold, session options factory, `nvidia-smi` probe, fail-closed compatibility check, and clean build are in place. |
| Image generation foundation | 100% | `[####################]` | Text-to-image request/job scaffold, LlmRuntime/SocketJack invocation paths, manifest validation, artifact persistence, and optional Python Diffusers/Optimum execution are in place. |
| Audio generation foundation | 100% | `[####################]` | Speech request/job scaffold and LlmRuntime/SocketJack invocation paths are in place. Model-specific TTS execution is deferred until local model layouts are selected. |
| Video generation foundation | 100% | `[####################]` | Video request/job scaffold and LlmRuntime/SocketJack invocation paths are in place. Model-specific frame/video execution is deferred until local model layouts are selected. |
| LlmRuntime connection | 100% | `[####################]` | Tool definitions and built-in `ILlmTool` invocation bridge are in place for devices, models, jobs, cancellation, and scaffolded generation. SockJackDml remains the LlmRuntime DirectML LLM path. |
| SocketJack bridge | 100% | `[####################]` | Route constants, route mapper, status/devices/models/jobs/artifact handlers, scaffolded generation handlers, artifact serving, and SSE progress streaming are in place. |
| Tests and validation | 100% | `[####################]` | JackONNX MSTest project covers manifest catalog, LlmRuntime bridge, SocketJack handlers, artifacts, progress history, and CPU ONNX inference. |
| Documentation/package polish | 100% | `[####################]` | Progress tracking, README, sample manifest, and SockJackDml integration notes are in place. |

## Feature Checklist

| Feature | Status | Percent |
|---|---|---:|
| Create JackONNX project family | Done | 100% |
| Add ONNX Runtime package reference | Done | 100% |
| Add DirectML provider package reference | Done | 100% |
| Add CUDA provider package reference | Done | 100% |
| Add model manifest contract | Done | 100% |
| Add sample manifest | Done | 100% |
| Add device/provider discovery | Done | 100% |
| Add job tracking contracts | Done | 100% |
| Add artifact storage and serving | Done | 100% |
| Add progress history and SSE stream | Done | 100% |
| Add image pipeline foundation | Done | 100% |
| Add audio pipeline foundation | Done | 100% |
| Add video pipeline foundation | Done | 100% |
| Connect JackONNX to LlmRuntime tools | Done | 100% |
| Connect JackONNX to SocketJack routes | Done | 100% |
| Validate JackONNX baseline build | Done | 100% |
| Add CPU ONNX smoke test | Done | 100% |
| Add JackONNX test project | Done | 100% |
| Add CLI validation sample | Done | 100% |
| Add tests | Done | 100% |
| Add samples | Done | 100% |

## Next Steps

1. Select local model layouts for image/audio/video execution.
2. Implement model-specific ONNX graph orchestration against those local layouts.
3. Done: added CUDA and DirectML hardware/provider integration tests that execute tiny ONNX inference when the matching native runtime is available and self-skip otherwise.
4. Done: wired the JackLLM to start embedded LlmRuntime with JackONNX built-in tools registered and display LlmRuntime plus JackONNX capability status in Services, pipeline, and tray diagnostics.

## Deferred Local-Model Execution

The project creation work is complete. Image generation can now call a local Python Diffusers/Optimum runner for compatible local manifests and dependencies. Audio/video model execution and direct in-process diffusion orchestration remain intentionally deferred because model files and final target local model layouts are not part of this project-creation pass.

JackONNX consumes local model manifests and files only.

## Latest Validation

```text
dotnet test .\JackONNX.Tests\JackONNX.Tests.csproj --no-restore --nologo -v:minimal
Passed: 9, Failed: 0, Skipped: 2

dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- validate --manifest=.\JackONNX\Samples\Manifests\sd15-example.jackonnx.json
valid: true

dotnet build .\JackLLM\JackLLM.csproj
Build succeeded.
```

