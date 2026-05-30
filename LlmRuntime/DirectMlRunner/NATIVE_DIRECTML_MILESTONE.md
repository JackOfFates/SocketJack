# Native DirectML GGUF Backend Milestone

## Decision

DirectML means a true DirectML execution backend for GGUF inference, not merely a DirectML-labeled compatibility path that falls back to Vulkan, CUDA, or CPU.

`LlmRuntime.DirectMlRunner` remains the process boundary and `socketjack-jsonl` protocol runner. When `--backend directml --allow-fallback false` is used, it now loads GGUF metadata and the tensor table through the native SockJackDml backend and fails closed before token decode. It can still use LLamaSharp-supported backends only when fallback is explicitly requested.

## Goal

Create a native DirectML GGUF inference backend that can execute supported transformer model layers on DirectML-capable hardware through Direct3D 12/DirectML.

## Acceptance Criteria

- Loads `.gguf` model metadata and tensor data without routing inference through LLamaSharp.
- Supports at least one Llama-family causal language model architecture end to end.
- Executes core inference operators through DirectML:
  - token embedding lookup
  - RMSNorm
  - quantized/dequantized matmul
  - RoPE
  - attention
  - feed-forward/SwiGLU
  - KV cache read/write
  - final logits projection
- Supports common GGUF quantization formats required by target models, starting with `Q4_0`, `Q4_K_M`, `Q5_K_M`, `Q8_0`, and `F16`.
- Streams tokens through the existing `socketjack-jsonl` protocol.
- Reports device name, adapter memory, selected precision, context length, and fallback/operator coverage.
- Fails closed when an operator or quantization format is unsupported; no silent CPU fallback unless explicitly requested.
- Includes a small deterministic smoke test with a tiny synthetic or test-safe model fixture.
- Keeps `LlmRuntime` API compatibility unchanged.

## Project Shape

- `LlmRuntime/SockJackDml/Native`: native C++ library for GGUF parsing, tensor loading, Direct3D 12 device setup, DirectML graph/operator execution, KV cache, and token sampling helpers.
- `LlmRuntime.DirectMlRunner`: existing .NET executable remains the process runner and protocol host. It P/Invokes or hosts the native library when `--backend directml --allow-fallback false` is requested.
- `LlmRuntime.Tests`: adds runner-level tests for protocol errors, unsupported quantization, and native backend availability probes.

## Implementation Phases

1. Native project scaffold
   - [x] Add native C++ build assets under `LlmRuntime/SockJackDml/Native`.
   - [x] Initialize Direct3D 12 adapter/device selection.
   - [x] Initialize DirectML device creation.
   - [x] Expose a minimal C ABI for version, device probe, and future tensor dispatch.
   - [x] Add `tools\Build-SockJackDml.ps1`.
   - [x] Wire `LlmRuntime.DirectMlRunner` to probe `SockJackDml.dll`.
   - [x] Add DirectML command queue/command list dispatch helpers.
   - [x] Implement the first real DirectML tensor operator dispatch.

2. GGUF loader
   - [x] Parse GGUF header, metadata, tensor table, tensor offsets, and alignment.
   - [x] Map model files read-only.
   - [x] Validate architecture and quantization support before allocation.
   - [x] Add a bounded F32 GGUF tensor payload DirectML dispatch smoke path.
   - [ ] Upload selected tensor payloads into Direct3D 12 resources.

3. Tensor and operator layer
   - Implement tensor descriptors, upload buffers, readback buffers, and resource lifetime.
   - Add DirectML operator wrappers for matmul/GEMM, elementwise ops, softmax, normalization support, and graph dispatch.

4. Llama inference path
   - Implement tokenizer handoff boundary or reuse managed tokenization initially.
   - Implement prompt evaluation.
   - Implement KV cache.
   - Implement decode loop.
   - Stream generated tokens through `socketjack-jsonl`.

5. Quantization support
   - Start with one practical quant format.
   - Add planned common GGUF quant formats.
   - Add validation and clear error messages for unsupported formats.

6. Production hardening
   - Device memory planning.
   - Context-length planning.
   - Cancellation.
   - Crash isolation.
   - Performance counters.
   - Compatibility tests across AMD, Intel, NVIDIA, and Microsoft Basic Render Driver rejection.

## Current Status

- `LlmRuntime.DirectMlRunner` exists and is protocol-complete.
- `LlmRuntime.DirectMlRunner` uses LLamaSharp-supported backends for fallback inference only.
- `LlmRuntime/SockJackDml/Native` exists and builds to `Tools\DirectML\SockJackDml.dll`.
- `LlmRuntime.DirectMlRunner.exe --native-probe` successfully loads `SockJackDml.dll` and creates a native Direct3D 12 + DirectML device.
- `LlmRuntime.DirectMlRunner.exe --native-identity-smoke` successfully dispatches `DML_OPERATOR_ELEMENT_WISE_IDENTITY` through SockJackDml and reads back the expected float32 tensor.
- `LlmRuntime.DirectMlRunner.exe --native-load-smoke --model <path.gguf>` maps a GGUF file read-only, parses metadata and tensor table natively, validates offsets and supported tensor types, initializes DirectML, and returns structured model info.
- `LlmRuntime.DirectMlRunner.exe --native-tensor-smoke --model <path.gguf>` selects an F32 tensor from the mapped GGUF, uploads a bounded slice through DirectML identity, reads it back, and reports input/output previews.
- `LlmRuntime.DirectMlRunner.exe --backend directml --allow-fallback false` uses the native SockJackDml load path and then fails closed with a clear unsupported-decode error instead of routing inference through LLamaSharp.
- The current verified adapter is `NVIDIA GeForce GTX TITAN X`.
- True native DirectML GGUF inference is not complete and should remain tracked as open implementation work until tensor payload upload, quantized tensor operators, KV cache, and token decode are native.

