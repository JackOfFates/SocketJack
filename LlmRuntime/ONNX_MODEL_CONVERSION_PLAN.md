# LlmRuntime ONNX Model Conversion Plan

## Why This Exists

The JackLLM model browser can currently land on Hugging Face repositories where the injected downloader panel says:

```text
No GGUF files found in this repository.
```

SocketJack now has ONNX support through JackONNX, and `ModelDownloadService` already accepts `.onnx` and `.safetensors` URLs. The missing piece is a conversion and registration path that lets the model browser handle repositories that publish source tensors or ONNX layouts instead of GGUF files.

This plan adds that path without pretending every GGUF can be converted losslessly. GGUF is an inference container with metadata and weights; it does not carry a full ONNX graph. Practical conversion should prefer original source tensors plus config. GGUF-to-ONNX should be an architecture-limited fallback where SocketJack can reconstruct the graph safely.

## Current State

- `LlmRuntime.ModelDownloadService` recognizes `.gguf`, `.safetensors`, and `.onnx` download URLs.
- `LlmRuntime.Wpf.HuggingFaceModelDownloaderControl` scans Hugging Face repos for `.gguf` files only, so ONNX-only and source-tensor-only repos appear empty.
- `LlmRuntime.LlmModelRegistry` enumerates and validates `.gguf` files only.
- `JackONNX` loads local ONNX model manifests and model files, but it does not export or convert models.
- The JackONNX CLI entry point in the single `JackONNX` project can list and validate ONNX manifests; it has no conversion command.

## Product Goal

When a Hugging Face repository does not contain downloadable GGUF files, the model browser should still show useful actions:

- Download ready-to-run ONNX files or ONNX model folders.
- Download source tensor bundles that can be converted to ONNX.
- Offer conversion jobs only when the repository has enough metadata and files.
- Register converted ONNX outputs so LlmRuntime, JackONNX, and the GUI can find them.
- Show a clear reason when a model is GGUF-only, source-incomplete, too large, unsupported, or unsafe to convert.

## Non-Goals

- Do not claim universal GGUF-to-ONNX conversion.
- Do not silently dequantize large quantized models into outputs that exceed disk, RAM, or shared video memory budgets.
- Do not replace the existing GGUF runtime path; GGUF remains the best path for many local LLMs.
- Do not require Python packages in the core LlmRuntime assembly. Keep conversion in a worker process or optional tool bundle.

## Architecture

Add a conversion lane beside the current download lane:

```text
Model browser
  -> repository file scan
  -> candidate classifier
  -> download job or conversion job
  -> conversion worker
  -> ONNX output layout
  -> JackONNX / LlmRuntime registration
```

New runtime pieces:

- `ModelRepositoryScanner`: reads Hugging Face tree metadata and classifies `.gguf`, `.onnx`, `.safetensors`, tokenizer/config files, and existing ONNX folders.
- `ModelConversionPlanner`: decides whether a repo/file set is ready, convertible, unsupported, or too large.
- `ModelConversionService`: owns queued conversion jobs, progress, cancellation, cleanup, and result manifests.
- `ModelConversionWorker`: isolated process for Python/Optimum/Transformers/ONNX tooling and any future native GGUF graph reconstruction.
- `OnnxModelRegistrar`: writes manifests and refreshes model registries after successful conversion.

Suggested API additions:

```text
GET  /api/v1/models/repository/scan?repo=owner/name
POST /api/v1/models/convert
GET  /api/v1/models/convert/status
POST /api/v1/models/convert/cancel
```

Suggested CLI additions:

```powershell
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- scan --repo=owner/name
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- convert --source=PATH --out=PATH --task=text-generation
dotnet run --project .\JackONNX\JackONNX.csproj -p:OutputType=Exe -p:StartupObject=JackONNX.Cli.JackOnnxCli -- validate --manifest=PATH
```

## Candidate Types

The browser should classify repository files into these actions:

| Candidate | Example files | Action |
|---|---|---|
| Native GGUF | `*.gguf` | Existing download/load path. |
| Ready ONNX | `*.onnx`, `model.onnx`, `decoder_model.onnx`, `genai_config.json` | Download/register directly. |
| ONNX folder | `onnx/model.onnx`, `onnx/config.json` | Download required folder files and register as a manifest. |
| Source tensors | `*.safetensors`, `config.json`, tokenizer files | Offer convert-to-ONNX if architecture is supported. |
| GGUF fallback | `*.gguf` plus enough metadata | Experimental architecture-specific GGUF-to-ONNX conversion. |
| Unsupported | Missing config/tokenizer, unknown architecture, no weights | Show why no action is available. |

## Conversion Strategy

### 1. Prefer Existing ONNX

If the repo already contains ONNX files, skip conversion:

- Download all required files into `Models\<owner>\<repo>\<variant>\`.
- Preserve source repo, revision, file list, sizes, and hashes.
- Write a `.jackonnx.json` or LlmRuntime ONNX manifest.
- Validate the manifest and show the model as ready.

### 2. Convert Source Tensors

For `.safetensors` repositories with `config.json` and tokenizer assets:

- Create an isolated conversion workspace under `Models\.conversion\<jobId>\`.
- Download source files with resumable downloads and hash checks.
- Run an optional Python worker using Transformers/Optimum/ONNX tooling.
- Export the model as FP16 where the target provider supports it, otherwise FP32.
- Optionally run ONNX graph optimization and external data splitting for large weights.
- Write provider/memory metadata into the manifest.
- Run a smoke inference or shape validation before marking the model ready.

This path should be the default for Llama, Mistral, Qwen, Phi, Gemma, BERT-style embedding models, CLIP, and other architectures that mature exporters support.

### 3. GGUF-to-ONNX Fallback

Direct GGUF conversion should be explicit and limited:

- Read GGUF metadata and tensor table using the existing reader/native loader work.
- Support only known architectures with a tested ONNX graph template.
- Dequantize or map GGML quantized tensors into ONNX-compatible tensor formats.
- Estimate expanded output size before conversion.
- Reject conversion if dequantized output would exceed disk, RAM, or memory limits.
- Validate tensor names, dimensions, rope settings, tokenizer metadata, and special tokens.

Initial supported fallback target should be one small architecture family, such as Llama-compatible decoder-only text models. Expand only after golden tests pass.

## Output Layout

Use a stable folder shape so both the GUI and runtimes can reason about converted models:

```text
Models/
  owner/
    repo/
      variant/
        manifest.jackonnx.json
        conversion.json
        source-files.json
        model.onnx
        model.onnx.data
        tokenizer.json
        tokenizer_config.json
        special_tokens_map.json
```

`conversion.json` should include:

- source repo, revision, and source file hashes
- converter version
- architecture and task
- precision and quantization policy
- estimated memory and output size
- provider compatibility notes
- validation result
- original GGUF/source tensor path if applicable

## Fit And Safety Rules

Before showing a Convert button, estimate:

- download bytes
- temporary workspace bytes
- final ONNX bytes
- expected RAM during conversion
- expected shared video memory or provider memory for runtime

For the current GUI example with 7.84 GB shared video memory and 329.49 GB free drive space, the browser should avoid suggesting conversions whose final model or expected runtime memory is likely to exceed the memory limit, even when disk is fine.

Conversion jobs should:

- run outside the UI thread
- stream progress and logs
- support cancellation
- clean partial output safely
- never overwrite a registered model without user confirmation
- keep downloaded proprietary/private files inside the configured model root

## GUI Changes

Update `HuggingFaceModelDownloaderControl`:

- Scan for `.gguf`, `.onnx`, `.safetensors`, config, tokenizer, and ONNX folder files.
- Replace "No GGUF files found" with a format-aware empty state.
- Add action labels: `Download GGUF`, `Download ONNX`, `Convert to ONNX`, `Unsupported`.
- Show conversion fit estimates separately from raw file size.
- Add a conversion queue below the download queue or merge both into one job list.
- After conversion completes, refresh the local model list and show a health card for the ONNX manifest.

Suggested empty state:

```text
No GGUF files found. This repository has source tensors or ONNX assets; choose a convertible file set below.
```

## Runtime Changes

Update `LlmModelRegistry`:

- Enumerate registered ONNX manifests and complete ONNX layouts in addition to GGUF.
- Preserve `format = "onnx"` in `LlmModelInfo`.
- Report ONNX task/capability metadata from the manifest.
- Keep GGUF loading routed through existing backends.
- Route ONNX media models through JackONNX.
- Add a separate ONNX LLM backend only when text-generation ONNX execution is implemented.

Update `ModelDownloadService`:

- Support downloading multiple files as a repo bundle.
- Persist source hashes and file manifests.
- Expose total bundle progress.

Update JackONNX:

- Add manifest generation helpers.
- Add optional `convert` CLI command that delegates to the conversion worker.
- Validate generated manifests and referenced files.

## Milestones

### Milestone 1: Browser Recognizes ONNX Repos

- Extend Hugging Face scan to classify `.onnx` and `.safetensors`.
- Show "Download ONNX" for already exported ONNX assets.
- Download ONNX files and create a local manifest.
- Refresh model list after download.
- Tests: candidate classification, manifest writing, fit checks.

### Milestone 2: Source Tensor Conversion

- Add conversion job DTOs, status endpoints, and cancellation.
- Add optional conversion worker bootstrap.
- Convert one small source-tensor model to ONNX in a temp workspace.
- Validate output with ONNX Runtime CPU.
- Register the converted model.
- Tests: job lifecycle, cancellation, manifest validation, smoke inference.

### Milestone 3: GUI Conversion Queue

- Add Convert buttons for eligible source tensor repos.
- Stream conversion progress into the GUI.
- Show conversion logs and final validation result.
- Add retry and cleanup actions.
- Tests: WPF control classification and job status handling.

### Milestone 4: GGUF Fallback Prototype

- Pick one Llama-compatible synthetic GGUF fixture.
- Build an ONNX graph template for that architecture.
- Map/dequantize tensors into ONNX initializers.
- Validate names, shapes, tokenizer metadata, and logits shape.
- Mark the feature experimental until real model parity tests pass.

### Milestone 5: Production Hardening

- Add provider compatibility matrix.
- Add disk/RAM/shared-memory guardrails.
- Add model card/source license display before conversion.
- Add regression fixtures for ONNX-ready, source-tensor, GGUF-only, and unsupported repos.
- Add installer packaging for the optional conversion worker.

## Acceptance Criteria

- A repo with ONNX files no longer displays "No GGUF files found"; it offers direct ONNX download/registration.
- A repo with supported `.safetensors` plus config/tokenizer offers `Convert to ONNX`.
- Unsupported repos explain exactly what is missing.
- Conversion jobs survive GUI refreshes and expose status through API.
- Converted models produce a manifest that JackONNX can validate.
- The model registry distinguishes `format = "gguf"` from `format = "onnx"`.
- No conversion is offered when estimated output/runtime memory exceeds the configured fit policy.
- GGUF-to-ONNX is labeled experimental and limited to architectures with tested graph templates.

## First Implementation Slice

The smallest useful change is:

1. Extract Hugging Face file scanning from the WPF control into a reusable `ModelRepositoryScanner`.
2. Add a `ModelFileCandidate.Format` value for `gguf`, `onnx`, `safetensors`, `config`, and `tokenizer`.
3. Show ONNX files in the browser panel and allow direct download.
4. Write a minimal ONNX manifest after download.
5. Teach `LlmModelRegistry` to list that manifest as `format = "onnx"` without trying to load it through the GGUF backend.

After that, source tensor conversion can land as a separate worker-backed feature without blocking the immediate model browser fix.
