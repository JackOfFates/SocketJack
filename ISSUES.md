# LlmRuntime Roadmap Issues

This file reflects the current LlmRuntime implementation state as of May 10, 2026.

## Open Implementation Issues

- Native DirectML GGUF inference is started but not complete. Decision: true DirectML means a real DirectML GGUF inference backend, not a DirectML-labeled compatibility path with Vulkan/CUDA/CPU fallback. The native source under `LlmRuntime/SockJackDml/Native` now builds a DLL, proves Direct3D 12 + DirectML device creation, owns command queue/command list/fence/command-recorder dispatch helpers, passes a real DirectML float32 identity tensor dispatch smoke test, loads GGUF v2/v3 metadata plus tensor tables natively through a read-only mapped model handle, and can dispatch a bounded F32 GGUF tensor payload slice through DirectML. Persistent tensor resource loading, transformer operators, quantized kernels, KV cache, and decode are still open. The native backend milestone is documented in `LlmRuntime\DirectMlRunner\NATIVE_DIRECTML_MILESTONE.md`.

## External Setup Required

- GitHub CLI is installed locally at `C:\Program Files\GitHub CLI\gh.exe`, but it is not authenticated. Live issue, PR, and Actions workflows require running `tools\Initialize-GitHubCliAuth.ps1` and completing the GitHub browser login.
- Production signing is wired through `.github\workflows\jackllm-signing.yml`, but a real production PFX certificate is still required. Run `tools\Set-GitHubSigningSecrets.ps1 -CertificatePath <path-to-production.pfx> -Repository <owner/repo>` to set `CODE_SIGNING_PFX_BASE64` and `CODE_SIGNING_PFX_PASSWORD`.
- DirectML runner installation is source-backed. Run `tools\Install-DirectMlGgufRunner.ps1` to build `LlmRuntime.DirectMlRunner` and copy it to `Tools\DirectML`.
- Native DirectML bridge installation is source-backed. `dotnet build SocketJack.sln` now builds `Tools\DirectML\SockJackDml.dll` through the `LlmRuntime -p:BuildSockJackDmlNative=true` solution project; `tools\Build-SockJackDml.ps1` remains available for direct native-only builds, and `tools\Install-DirectMlGgufRunner.ps1` still builds SockJackDml unless `-SkipNativeSockJackDml` is passed.

## Current Implemented State

- `LlmRuntime` exists as a `net8.0` hostable library with model management, LM Studio/OpenAI-compatible API surfaces, tool invocation, autonomous agent workflows, production readiness endpoints, and JackLLM provider integration.
- `LlmRuntime.VisualStudio` now produces an installable VSIX at `LlmRuntime.VisualStudio\bin\Debug\net472\SocketJack.LlmRuntime.VisualStudio.vsix`.
- The Visual Studio VSIX includes `extension.vsixmanifest`, `LlmRuntime.VisualStudio.dll`, and generated `LlmRuntime.VisualStudio.pkgdef`.
- `LlmRuntime.VisualStudio` now declares the package as `Microsoft.VisualStudio.VsPackage`, not a MEF-only component, and targets Visual Studio 2022 17.x `amd64`.
- `LlmRuntime.DirectMlRunner` is built from `LlmRuntime\LlmRuntime.csproj` as an executable entry point. It implements the `socketjack-jsonl` process protocol expected by `DirectMlGgufBackend`, reads JSON requests from stdin, tolerates UTF-8 BOM-prefixed stdin payloads, loads fallback GGUF inference through LLamaSharp, and emits completion/token JSON lines to stdout.
- `LlmRuntime/SockJackDml/Native` contains the native C++ DirectML backend source. It exports a C ABI for version, native probe, DirectML tensor dispatch, native GGUF model loading/unloading, F32 GGUF tensor payload smoke dispatch, and last-error inspection.
- `LlmRuntime -p:BuildSockJackDmlNative=true` exists as an SDK-style solution bridge project. It lets `dotnet build SocketJack.sln` invoke `tools\Build-SockJackDml.ps1` without asking the dotnet CLI to evaluate the native `.vcxproj` directly.
- `Tools\DirectML\SockJackDml.dll` exists locally and is loaded by `LlmRuntime.DirectMlRunner.exe --native-probe`.
- `LlmRuntime.DirectMlRunner.exe --native-probe` reports native DirectML availability on `NVIDIA GeForce GTX TITAN X` with Direct3D 12 + DirectML device creation succeeding.
- `LlmRuntime.DirectMlRunner.exe --native-identity-smoke` now executes a real SockJackDml `DML_OPERATOR_ELEMENT_WISE_IDENTITY` dispatch and validates GPU readback against the input tensor.
- `LlmRuntime.DirectMlRunner.exe --native-load-smoke --model <path.gguf>` maps a GGUF file read-only, parses metadata and tensor table entries natively, validates offsets and supported GGML tensor types, initializes DirectML, and returns structured model info.
- `LlmRuntime.DirectMlRunner.exe --native-tensor-smoke --model <path.gguf>` dispatches a bounded F32 tensor payload slice from the mapped GGUF through DirectML identity and reports matching previews.
- `LlmRuntime.DirectMlRunner.exe --backend directml --allow-fallback false` now loads through SockJackDml and fails closed before token decode instead of silently routing inference through LLamaSharp.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe` exists locally after running the install script, and `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --self-test` returns a valid protocol status payload.
- `tools\Install-DirectMlGgufRunner.ps1` builds the in-repo runner by default when no external runner path or download URL is supplied, and also builds SockJackDml unless `-SkipNativeSockJackDml` is passed.
- DirectML runner discovery checks `LlmRuntimeOptions.DirectMlGgufRunnerPath`, `LLMRUNTIME_DIRECTML_GGUF_RUNNER`, `Tools\DirectML\LlmRuntime.DirectMlRunner.exe`, `Tools\DirectML\llm-directml-gguf-runner.exe`, `Tools\DirectML\llama-directml.exe`, and `Tools\DirectML\llama-cli.exe`.
- Tool secrets use Windows DPAPI `CurrentUser` protection on Windows. The local AES key-file path remains only as a non-Windows/legacy fallback.
- GitHub CLI installation, GitHub authentication, GitHub signing secret setup, Git for Windows installation, DirectML runner installation, installer signing, and update publishing scripts exist under `tools`.
- The signing workflow validates `CODE_SIGNING_PFX_BASE64` and `CODE_SIGNING_PFX_PASSWORD` before signing so unsigned artifacts are not silently treated as production signed.
- Tool invocation supports MCP-over-HTTP JSON-RPC, named-pipe JSON line calls, static .NET assembly method calls, HTTP, executable, PowerShell, and built-in tools.
- The proprietary desktop automation tool includes approved window inspection, foreground/background titles, keystrokes, process launching, target-window mouse operations, and human cursor pathing support.
- Local GGUF models can power approved file edits and autonomous agent workflows through `/api/v1/agent/autonomous/run`.
- Background GitHub workflows can queue draft PR creation through `/api/v1/github/pull-request/job`, but live PR creation still depends on authenticated `gh`.

## Verification

- `dotnet build .\LlmRuntime.VisualStudio\LlmRuntime.VisualStudio.csproj --nologo -v:minimal` passed with 0 warnings/errors and produced `LlmRuntime.VisualStudio\bin\Debug\net472\SocketJack.LlmRuntime.VisualStudio.vsix`.
- The generated VSIX archive contains `extension.vsixmanifest`, `LlmRuntime.VisualStudio.dll`, and `LlmRuntime.VisualStudio.pkgdef`.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --self-test` passed.
- `tools\Build-SockJackDml.ps1` passed and produced `Tools\DirectML\SockJackDml.dll`.
- `dotnet build .\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal -p:BuildSockJackDmlNative=true` passed and invoked the native SockJackDml build.
- `dotnet build .\SocketJack.Windows\SocketJack.WPF.csproj --no-restore --nologo -v:minimal /t:Rebuild` passed with 0 warnings and 0 errors after the SocketJack.Windows warning cleanup.
- `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` passed with 0 warnings and 0 errors and invoked the SockJackDml native build through `LlmRuntime -p:BuildSockJackDmlNative=true`.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-probe` passed and reported native DirectML availability.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke` passed with status `0` and matching input/output tensors.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-load-smoke --model $env:TEMP\sockjackdml-synthetic.gguf --context-length 8 --gpu-layers 1` passed with a synthetic GGUF fixture: 1 tensor, 5 metadata entries, native DirectML ready.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-tensor-smoke --model $env:TEMP\sockjackdml-synthetic.gguf --context-length 8 --gpu-layers 1 --tensor token_embd.weight --max-elements 16` passed and returned matching F32 input/output previews from the GGUF tensor payload.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --backend directml --allow-fallback false --protocol socketjack-jsonl` loaded the synthetic GGUF natively and then returned the expected fail-closed unsupported-decode error.
- `gh auth status` reports that no GitHub host is authenticated.
- `dotnet test .\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal` passed 46/46 tests after adding SockJackDml runner probing and hardening the named-pipe tool adapter against EOF-style pipe closes.

## Next Fix

- Continue the native DirectML GGUF backend milestone with persistent tensor resource loading, quantization support, transformer operator wrappers, KV cache, and decode.

