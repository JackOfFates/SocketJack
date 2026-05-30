# SockJackDml

`LlmRuntime/SockJackDml/Native` is the native DirectML backend source for LlmRuntime GGUF inference.

SockJackDml is intentionally separate from `LlmRuntime.DirectMlRunner`:

- `LlmRuntime.DirectMlRunner` owns the process boundary and `socketjack-jsonl` protocol.
- `LlmRuntime/SockJackDml/Native` owns native Direct3D 12/DirectML device setup and, incrementally, DirectML tensor execution.

## Current Scope

This first native increment exports a small C ABI:

- `SockJackDmlGetVersion`
- `SockJackDmlProbe`
- `SockJackDmlRunIdentityFloat32`
- `SockJackDmlLoadModel`
- `SockJackDmlRunTensorIdentityFloat32`
- `SockJackDmlUnloadModel`
- `SockJackDmlGetLastError`

`SockJackDmlProbe` creates a DXGI adapter, Direct3D 12 device, and DirectML device. This proves the native DirectML runtime path is present without involving LLamaSharp, Vulkan, CUDA, or CPU fallback.

`SockJackDmlRunIdentityFloat32` performs the first real DirectML tensor dispatch. It creates native command queue, command list, fence, descriptor heap, binding table, and command-recorder state; uploads a float32 tensor; runs `DML_OPERATOR_ELEMENT_WISE_IDENTITY`; and reads the result back to the caller.

`SockJackDmlLoadModel` is the first native GGUF model-load boundary. It maps a `.gguf` file read-only, parses GGUF v2/v3 headers, metadata, tensor table entries, tensor offsets, and data alignment, records architecture/context/embedding metadata, validates known GGML tensor types, initializes the DirectML device, and returns an opaque native model handle. Token decode and transformer operator execution are intentionally still gated until the remaining native operators land.

`SockJackDmlRunTensorIdentityFloat32` proves native model-data dispatch. It selects an F32 tensor from the mapped GGUF handle, uploads a bounded payload slice, dispatches DirectML identity, reads the result back, and returns input/output previews for verification.

## Build

```powershell
dotnet build .\SocketJack.sln
```

The solution build uses the SDK-style `LlmRuntime -p:BuildSockJackDmlNative=true` bridge project to call the native MSBuild script. You can also build just the bridge:

```powershell
dotnet build .\LlmRuntime\LlmRuntime.csproj -p:BuildSockJackDmlNative=true
```

Or call the native script directly:

```powershell
tools\Build-SockJackDml.ps1
```

The DLL is copied to:

```text
Tools\DirectML\SockJackDml.dll
```

`LlmRuntime.DirectMlRunner.exe --self-test` probes this DLL when it is present.

Run the native tensor smoke test through the managed runner:

```powershell
Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke
```

Run the native GGUF load smoke test:

```powershell
Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-load-smoke --model C:\Models\model.gguf
```

Run a native F32 GGUF tensor payload dispatch smoke test:

```powershell
Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-tensor-smoke --model C:\Models\model.gguf --tensor token_embd.weight
```

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>1,543 lines / 5 files</code></summary>

<br>

<strong>Scope:</strong> <code>LlmRuntime/SockJackDml/Native</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C++ | 2 | 1,380 |
| MSBuild/XML | 2 | 102 |
| Markdown | 1 | 61 |
| **Total** | **5** | **1,543** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
