# JackONNX

JackONNX is the local ONNX media runtime foundation for SocketJack and LlmRuntime.

It is intentionally model-local: JackONNX consumes model manifests and model files that already exist on disk.

## Current Scope

- Core media generation contracts for image, audio, and video requests.
- ONNX Runtime CPU session factory and smoke-tested inference path.
- Isolated DirectML and CUDA provider packages.
- Provider/device probing with fail-closed compatibility reporting.
- Local model manifest catalog.
- Job tracking, cancellation, progress history, and artifact storage.
- LlmRuntime built-in tool bridge.
- SocketJack HTTP route mapper, JSON handlers, artifact serving, and SSE progress stream.
- Image generation artifact handoff through a local Python Diffusers/Optimum runner when a compatible local image manifest and Python dependencies are present.
- CLI for local status, devices, providers, models, and manifest validation.

## CLI

```powershell
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- status
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- providers
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- models --manifest=.\JackONNX\Samples\Manifests\sd15-example.jackonnx.json
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- validate --manifest=.\JackONNX\Samples\Manifests\sd15-example.jackonnx.json
```

## LlmRuntime Connection

`JackONNX.LlmRuntime` registers JackONNX as LlmRuntime tools:

- `jackonnx_devices_list`
- `jackonnx_models_list`
- `jackonnx_jobs_status`
- `jackonnx_jobs_cancel`
- `jackonnx_image_generate`
- `jackonnx_audio_speech`
- `jackonnx_video_generate`

The bridge uses the normal `LlmToolRegistry` and `LlmToolInvoker` path.

## SocketJack Routes

`JackONNX.SocketJack` maps the local API surface:

- `GET /api/jackonnx/status`
- `GET /api/jackonnx/devices`
- `GET /api/jackonnx/models`
- `GET /api/jackonnx/jobs`
- `GET /api/jackonnx/jobs/*`
- `POST /api/jackonnx/jobs/cancel`
- `POST /api/jackonnx/jobs/cancel/*`
- `GET /api/jackonnx/artifacts/*`
- `GET /api/jackonnx/jobs/stream/*`
- `POST /api/jackonnx/image/generate`
- `POST /api/jackonnx/audio/generate`
- `POST /api/jackonnx/video/generate`
- `POST /api/jackonnx/video/presentation`

## Future Model-Specific Work

Audio and video model execution, plus direct in-process diffusion graph orchestration, will be implemented against local model manifests and files as target local model layouts are selected.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>8,886 lines / 19 files</code></summary>

<br>

<strong>Scope:</strong> <code>JackONNX</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 14 | 8,666 |
| Markdown | 3 | 142 |
| MSBuild/XML | 1 | 53 |
| JSON | 1 | 25 |
| **Total** | **19** | **8,886** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
