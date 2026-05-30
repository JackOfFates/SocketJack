# LlmRuntime DirectML Runner

`LlmRuntime.DirectMlRunner` is the external GGUF runner executable used by `LlmRuntime` when the `directml` backend is selected.

The runner speaks the `socketjack-jsonl` process protocol:

1. `LlmRuntime` starts the executable with `--model <path> --protocol socketjack-jsonl`.
2. `LlmRuntime` writes one JSON request to stdin.
3. The runner writes JSON lines to stdout:
   - `{ "content": "..." }` for non-streaming completion
   - `{ "token": "..." }` plus `[DONE]` for streaming completion

## Current Backend Behavior

The runner is protocol-complete and can load GGUF models through LLamaSharp using CPU, CUDA12, or Vulkan native backends.

Literal DirectML GGUF tensor dispatch is implemented incrementally under `LlmRuntime/SockJackDml/Native`. The current native increment can create a Direct3D 12 + DirectML device through `SockJackDml.dll`, uses native command queue/command list/fence dispatch helpers, passes a real `DML_OPERATOR_ELEMENT_WISE_IDENTITY` float32 tensor dispatch smoke test through `--native-identity-smoke`, loads GGUF metadata/tensor tables through `--native-load-smoke`, and dispatches a bounded F32 tensor payload slice from a mapped GGUF through `--native-tensor-smoke`.

When `--backend directml --allow-fallback false` is selected, the runner loads the model through SockJackDml and fails closed before token decode. It does not silently route that request through LLamaSharp, CPU, CUDA, or Vulkan. Full native token generation remains open until SockJackDml implements tensor payload upload, transformer operators, quantization kernels, KV cache, and decode.

## Useful Commands

Build and copy the runner to `Tools\DirectML`:

```powershell
dotnet build .\LlmRuntime\LlmRuntime.csproj -p:BuildDirectMlRunnerExe=true
```

Verify the protocol surface:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --self-test
```

Probe the native DirectML bridge:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-probe
```

Run the first native tensor dispatch smoke test:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke
```

Run the native GGUF load smoke test:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-load-smoke --model C:\Models\model.gguf
```

Run a native F32 GGUF tensor payload dispatch smoke test:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-tensor-smoke --model C:\Models\model.gguf --tensor token_embd.weight
```

Force CPU fallback:

```powershell
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --model C:\Models\model.gguf --protocol socketjack-jsonl --backend directml --fallback-backend cpu
```

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>1,210 lines / 4 files</code></summary>

<br>

<strong>Scope:</strong> <code>LlmRuntime/DirectMlRunner</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 2 | 1,081 |
| Markdown | 2 | 129 |
| **Total** | **4** | **1,210** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
