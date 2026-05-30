# LlmRuntime

Hostable local GGUF runtime for SocketJack. The first production path is replacing LM Studio inside JackLLM while preserving LM Studio/OpenAI-compatible endpoints for existing clients.

This README is the home for the LLM-specific SocketJack work: GGUF model loading, LM Studio/OpenAI-compatible APIs, DirectML runner integration, tool calling, local agent workflows, Visual Studio integration surfaces, and model-download UX. The root SocketJack README intentionally stays focused on the networking library.

## Quick Start

```csharp
using LlmRuntime;

var runtime = new LlmRuntimeHost(new LlmRuntimeOptions
{
    Port = 1234,
    ModelRoot = Path.Combine(Environment.CurrentDirectory, "Models")
});

runtime.Start();
runtime.Stop();
```

## Implemented Surface

- GGUF metadata scan and model descriptions.
- LLamaSharp model load/unload validation.
- CPU, CUDA12, and Vulkan backend selection.
- DirectML selection through a separate runner-backed GGUF backend using `DirectMlGgufRunnerPath`.
- Native SockJackDml Direct3D 12/DirectML probe and first real DirectML float32 tensor dispatch smoke path.
- OpenAI-compatible `GET /v1/models`.
- Native `GET /api/v1/models`.
- `POST /api/v1/models/load`.
- `POST /api/v1/models/unload`.
- `POST /api/v1/models/download`.
- `GET /api/v1/models/download/status`.
- `POST /v1/chat/completions` with non-streaming and SSE streaming.
- `POST /api/v1/chat` compatibility.
- OpenAI-style chat `tools`/`tool_choice` parsing plus native tool-call continuation for non-streaming chat.
- Stable scaffold errors for `/v1/responses`, `/v1/completions`, and `/v1/embeddings`.
- Proprietary tool registry endpoints under `/api/v1/tools`.
- OpenAI and MCP-shaped tool schema export.
- Standalone tool-call execution loop for approved tool calls, plus `/v1/chat/completions` integration that can parse model-emitted tool calls, invoke tools, and continue with tool results.
- Built-in proprietary `windows_desktop_automation` tool for approved local desktop automation on Windows.
- Durable agent sessions, file/terminal tools, sandbox profiles, plan/apply/review state, check loops, and local agent APIs under `/api/v1/agent`.
- Model-backed autonomous agent workflow under `/api/v1/agent/autonomous/run` where a loaded GGUF proposes approved file edits, commands, and plan steps.
- GitHub/team workflow APIs under `/api/v1/github` for repository inspection, issue/PR task starts, branch/commit/PR helpers, background draft PR jobs, summaries, diff review, Actions diagnostics, security review, dependency planning, policy, and audit logs.
- IDE/Copilot parity APIs under `/api/v1/ide` for inline completion, next-edit suggestions, Ask/Edit/Agent/Plan modes, checkpoints/rollback, code references, workspace indexing/search, custom instructions, prompt files, MCP-shaped context, BYOM/BYOK routing, vision attachments, context actions, and .NET upgrade plans.
- Code intelligence APIs under `/api/v1/code-intelligence` for symbol/call/dependency/ownership graphs, refactor and migration plans, missing-test exploration, profiling plans, architecture review, documentation sync, local privacy status, model evaluation harness planning, and context budget optimization.
- Production-readiness APIs under `/api/v1/production` for onboarding, diagnostics, telemetry-free local analytics, accessibility status, installer/update readiness, regression-suite metadata, and golden-path demo steps.

## SocketJack And JackLLM Integration

`LlmRuntime` is designed to be hosted beside SocketJack without adding a reverse reference from `SocketJack` back to `LlmRuntime`.

Integration points implemented so far:

- `LlmRuntimeHost` serves LM Studio/OpenAI-compatible endpoints over SocketJack `HttpServer`.
- `LlmRuntimeModelRuntimeAdapter` lets JackLLM route provider-neutral model calls to the embedded runtime.
- `JackLLM` can expose a provider dropdown for LM Studio fallback/debug mode versus embedded LlmRuntime mode.
- Model payloads default to `cwd\Models`.
- Tool definitions default to `cwd\Tools`.
- The WPF companion project provides model download and tool-definition UI without adding WPF dependencies to the core runtime.

## DirectML And SockJackDml

DirectML support is split into two layers:

- `LlmRuntime/DirectMlRunner` is the .NET process runner and `socketjack-jsonl` protocol host.
- `LlmRuntime/SockJackDml/Native` is the native C++ Direct3D 12/DirectML backend where true DirectML GGUF tensor execution is being built.

Current native status:

- `SockJackDml.dll` exports version, native probe, identity tensor dispatch, and last-error APIs.
- `--native-probe` verifies Direct3D 12 + DirectML device creation.
- `--native-identity-smoke` uploads a float32 tensor, dispatches `DML_OPERATOR_ELEMENT_WISE_IDENTITY`, reads the tensor back, and verifies the output.
- GGUF loading, quantized matmul, transformer operators, KV cache, and native decode are still tracked in `..\LlmRuntime\DirectMlRunner\NATIVE_DIRECTML_MILESTONE.md`.

Useful commands:

```powershell
dotnet build .\SocketJack.sln
dotnet build .\LlmRuntime\LlmRuntime.csproj -p:BuildSockJackDmlNative=true
tools\Build-SockJackDml.ps1
dotnet build .\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal -p:BuildDirectMlRunnerExe=true
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-probe
.\Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke
```

`LlmRuntime -p:BuildSockJackDmlNative=true` invokes `tools\Build-SockJackDml.ps1` during solution builds so `dotnet build SocketJack.sln` produces `Tools\DirectML\SockJackDml.dll` without requiring the dotnet CLI to load the native `.vcxproj` directly.

## Tool Registry

Tool definitions are stored under `cwd\Tools` by default. The registry supports public, private, proprietary, and internal-only tools with JSON schemas, source metadata, approval modes, scoped permissions, audit history, and local encrypted secrets.

Invocation safety is enforced before a tool runs. HTTP tools require `NetworkAccess`, executable and PowerShell tools require `ShellExecution`, required secrets require `SecretsAccess`, allowed-project scopes are checked, and approval modes can block execution until the host marks the call approved. Required secrets can be expanded into approved source strings, HTTP headers, and process environment variables through `{{secret:name}}` placeholders. Tool outputs, errors, audit messages, and prompt/trace text passed through `RedactForTool` are scrubbed before they are persisted or returned.

Implemented invocation sources:

- HTTP endpoint
- Local executable
- PowerShell command
- Built-in SocketJack tool: `windows_desktop_automation`
- Named pipe JSON-line adapter
- .NET assembly static method adapter
- MCP-over-HTTP JSON-RPC adapter

Represented for later adapters:

- Named pipe
- .NET assembly/plugin
- MCP server

## Windows Desktop Automation Tool

`windows_desktop_automation` is a built-in proprietary tool registered under `cwd\Tools` by default. It requires explicit approval before every invocation and uses the new `DesktopAutomation` scoped permission.

Supported operations:

- Window discovery: `capabilities`, `get_foreground_window`, `list_windows`, `find_windows`.
- Window control: `focus_window`, `set_window_bounds`, `window_state`, `close_window`.
- Program launch: `open_program`.
- Foreground input: `send_keys`, `mouse_move`, `mouse_click`, `mouse_drag`.
- Non-cursor target-window mouse input: `window_mouse_move`, `window_mouse_click`, `window_mouse_drag`.
- Human cursor pathing: `preview_cursor_path` and optional `human` movement settings.
- Cursor inspection: `get_cursor_position`.

The tool can inspect foreground/background window titles and manage window bounds/state. Foreground keyboard and OS-cursor mouse input is visible on the desktop. The `window_mouse_*` operations post mouse messages directly to the selected window in screen or client coordinates and report whether the real cursor stayed unchanged. It does not install drivers, hook input, read password fields, bypass OS security prompts, or run hidden credential capture. The human cursor driver generates eased Bezier paths with bounded jitter for natural local UI control and deterministic replay in tests.

The implementation plan lives in `DESKTOP_AUTOMATION_TOOL_PLAN.md`.

Secrets are stored separately under `Tools\.secrets`. The local store prevents plaintext persistence in the tool definition JSON, but enterprise deployments should replace it with DPAPI, Windows Credential Manager, or a key vault.

## Verified Status

Verified on 2026-05-10:

- `dotnet test .\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal` passed 46/46.
- `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` passed with 0 warnings and 0 errors and invoked the SockJackDml native build through `LlmRuntime -p:BuildSockJackDmlNative=true`.
- `dotnet build .\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal -p:BuildSockJackDmlNative=true` passed and invoked the native SockJackDml build.
- `dotnet build .\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal` passed with 0 warnings and 0 errors.
- `tools\Build-SockJackDml.ps1` built `Tools\DirectML\SockJackDml.dll` with 0 warnings and 0 errors.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-probe` passed on `NVIDIA GeForce GTX TITAN X`.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke` passed with status `0` and matching input/output tensors.

Known gaps are tracked in `..\ISSUES.md`: native DirectML GGUF inference beyond the identity dispatch, DPAPI/key-vault secret storage, installing/authenticating GitHub CLI in live GitHub environments, and production signing certificate secrets.

## Agent And GitHub Workflows

Agent sessions are stored under `cwd\Agents` by default. The agent runtime supports resumable session history, approved file writes with diff previews, approved terminal commands with captured logs, read-only/workspace-write/full-access sandbox profiles, background jobs, self-correction plans, repo instruction discovery, skills/plugins discovery, and simple automation-hook persistence.

The GitHub workflow layer requires local `git` for repository, branch, commit, review, security, dependency, policy, and audit work. `tools\Install-GitForWindows.ps1` installs/checks Git for Windows on new machines. Live GitHub issue, pull request, draft PR, and Actions operations use GitHub CLI when it is installed and authenticated; otherwise the APIs return stable unavailable results so local workflows can keep running. Draft PRs can run synchronously through `/api/v1/github/pull-request` or as background jobs through `/api/v1/github/pull-request/job`.

`/api/v1/agent/autonomous/run` is the model-backed end-to-end agent path: it sends workspace context to the loaded GGUF model, expects a JSON plan with file edits and verification commands, applies approved edits through the sandboxed agent runtime, and records the resulting session/check state.

## IDE And Copilot Parity Surface

`LlmRuntimeHost` exposes a local IDE integration API that a Visual Studio extension, WPF GUI, or editor adapter can call:

- `GET /api/v1/ide/capabilities`
- `POST /api/v1/ide/completions/inline`
- `POST /api/v1/ide/next-edit`
- `POST /api/v1/ide/ask`
- `POST /api/v1/ide/edit/preview`
- `POST /api/v1/ide/agent/start`
- `POST /api/v1/ide/plan`
- `POST /api/v1/ide/checkpoints`
- `POST /api/v1/ide/rollback`
- `POST /api/v1/ide/references`
- `POST /api/v1/ide/index`
- `POST /api/v1/ide/search`
- `POST /api/v1/ide/instructions`
- `POST /api/v1/ide/prompts`
- `GET /api/v1/ide/mcp/context`
- `GET /api/v1/ide/model-routing`
- `POST /api/v1/ide/vision/context`
- `POST /api/v1/ide/actions`
- `POST /api/v1/ide/dotnet/upgrade-plan`

The first implementation is local and deterministic so the API remains useful before a model is loaded. Model-backed ranking and generation can be layered behind the same shapes.

## Code Intelligence

Phase 8 is exposed as hostable local APIs:

- `POST /api/v1/code-intelligence/symbol-graph`
- `POST /api/v1/code-intelligence/graph`
- `POST /api/v1/code-intelligence/refactor-plan`
- `POST /api/v1/code-intelligence/migration-plan`
- `POST /api/v1/code-intelligence/tests/coverage`
- `GET /api/v1/code-intelligence/profiling`
- `POST /api/v1/code-intelligence/architecture-review`
- `POST /api/v1/code-intelligence/documentation-sync`
- `POST /api/v1/code-intelligence/model-evaluation`
- `POST /api/v1/code-intelligence/context-budget`
- `GET /api/v1/code-intelligence/privacy`

The first pass uses deterministic local parsing and heuristics so code stays on the machine. A future language-server adapter can replace the parser internals without changing these route shapes.

## WPF Companion

`LlmRuntime.Wpf` provides:

- `HuggingFaceModelDownloaderControl`
- `ToolDefinitionsControl`
- `ToolDefinitionsWindow`

The tool editor supports create/edit/delete, source selection, JSON input/result schemas, HTTP headers, process environment variables, allowed-project scopes, approval and timeout settings, hidden secret-value entry, import/export, generated schemas from sample input, a test console, and badges for visibility/license and OpenAI/MCP/SocketJack/local compatibility.

These controls are kept outside the core runtime so services and command-line hosts can use `LlmRuntime` without WPF dependencies.

## Production Polish

Phase 9 readiness is exposed through local-only JSON surfaces:

- `GET /api/v1/production/onboarding`
- `GET /api/v1/production/diagnostics`
- `GET /api/v1/production/analytics/local`
- `GET /api/v1/production/accessibility`
- `GET /api/v1/production/installer`
- `GET /api/v1/production/regression-suite`
- `GET /api/v1/production/golden-path`

Additional guides:

- `QUICKSTART.md`
- `REGRESSION_SUITE.md`
- `GOLDEN_PATH_DEMO.md`

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>21,536 lines / 61 files</code></summary>

<br>

<strong>Scope:</strong> <code>LlmRuntime</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 47 | 19,258 |
| C++ | 2 | 1,380 |
| Markdown | 9 | 754 |
| MSBuild/XML | 3 | 144 |
| **Total** | **61** | **21,536** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
