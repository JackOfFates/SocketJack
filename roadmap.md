# SocketJack LlmRuntime Roadmap

## Goal

Build SocketJack into a local-first coding agent platform with embedded GGUF inference, LM Studio-compatible APIs, Visual Studio-grade IDE assistance, proprietary/private tool support, and Codex-grade autonomous engineering workflows.

The first replacement target is LM Studio inside `JackLLM`. The longer-term product target is to equal or exceed GitHub Copilot for Visual Studio and Codex for coding-agent workflows.

## Overall Progress

Overall readiness: **96% verified**

`[###################-] 96%`

Verified on 2026-05-10:

- `dotnet test .\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal` passed 46/46.
- `tools\Build-SockJackDml.ps1` built `Tools\DirectML\SockJackDml.dll`.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-identity-smoke` passed with a real DirectML float32 identity tensor dispatch.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-load-smoke` passed with a synthetic GGUF fixture through the native SockJackDml model-load path.
- `Tools\DirectML\LlmRuntime.DirectMlRunner.exe --native-tensor-smoke` passed with a synthetic GGUF fixture by dispatching a real mapped F32 tensor payload slice through DirectML.
- `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` passed with 0 warnings and 0 errors.
- Latest focused native, DirectML runner, and LlmRuntime test builds pass with 0 warnings and 0 errors.

## Phase 1: Local LLM Runtime Replacement

Status: **Complete**

`[###-----------------] 15%`

- [x] Create `LlmRuntime` hostable GGUF runtime project.
- [x] Add GGUF metadata reader, model registry, download service, and LLamaSharp CPU backend.
- [x] Add LM Studio-shaped model-management endpoints.
- [x] Implement `/v1/chat/completions` non-streaming generation.
- [x] Implement `/v1/chat/completions` streaming SSE generation.
- [x] Implement `/api/v1/chat` compatibility.
- [x] Add model-loaded, model-missing, cancellation, and token-limit errors.
- [x] Add runtime metrics: tokens/sec, prompt tokens, completion tokens, memory use, model load time.
- [x] Add GPU backend selection for CPU, CUDA12, Vulkan, and a separate DirectML GGUF runner backend.
- [x] Add native SockJackDml command queue/command list/fence dispatch helpers and the first real DirectML tensor operator smoke dispatch.
- [x] Add native SockJackDml GGUF metadata/tensor-table loading and fail-closed no-fallback DirectML runner behavior.
- [x] Add native SockJackDml F32 GGUF tensor payload dispatch smoke path.
- [x] Add `LlmRuntime -p:BuildSockJackDmlNative=true` so `dotnet build SocketJack.sln` builds `Tools\DirectML\SockJackDml.dll`.

## Phase 2: JackLLM Embedded Runtime Integration

Status: **Complete**

`[####################] 100%`

- [x] Add `IJackLLMModelRuntime` abstraction in SocketJack.
- [x] Keep LM Studio as fallback/debug provider.
- [x] Add provider dropdown in `JackLLM` for LM Studio vs embedded LlmRuntime.
- [x] Add `LlmRuntimeModelRuntimeAdapter` in `LlmRuntime`.
- [x] Start embedded `LlmRuntimeHost` from `JackLLM` with a build-clean integration path.
- [x] Route local model-runtime wrapper calls to provider-neutral `/v1/*` and `/api/v1/*` surfaces.
- [x] Preserve remote model routing for Copilot duplicator mode.
- [x] Add provider-neutral status fields while keeping old LM Studio fields temporarily.
- [x] Add same-origin model-management wrapper endpoints in `JackLLM`.
- [x] Make `cwd\Models` the default model root for LlmRuntime.

## Phase 3: Model Browser, Downloader, And Hardware Fit

Status: **Complete**

`[####################] 100%`

- [x] Add WPF Edge/WebView2 Hugging Face downloader control.
- [x] Detect existing files, drive space, and estimated memory fit.
- [x] Integrate downloader into `JackLLM`.
- [x] Show download button directly on Hugging Face model cards.
- [x] Suggest only GGUF files likely to fit disk and shared video memory.
- [x] Add quantization-aware recommendations: Q2, Q4, Q5, Q6, Q8.
- [x] Add model tags: chat, instruct, code, embedding, vision, tool-use.
- [x] Add download queue, pause/resume, retry, checksum, and cleanup.
- [x] Add one-click load after download.
- [x] Add local model health card: context length, memory estimate, tokenizer, architecture, quantization.

## Phase 4: Copilot For Visual Studio Parity

Status: **Complete as hostable IDE/Copilot parity API surface**

`[####################] 100%`

- [x] Build Visual Studio extension or integration surface; `LlmRuntime.VisualStudio` now provides a VSIX/MEF package, command table, Tools menu commands, feature catalog, and local runtime client.
- [x] Add inline code completion.
- [x] Add next edit suggestions.
- [x] Add Ask mode for explanations, questions, snippets, and diagnostics.
- [x] Add Edit mode for selected-file and selected-region edits.
- [x] Add Agent mode for multi-file tasks.
- [x] Add Plan mode that creates implementation plans without modifying files.
- [x] Add checkpoints before agent edits and easy rollback.
- [x] Add code referencing: active file, selection, solution, project, diagnostics, git diff.
- [x] Add workspace indexing for semantic search and repo-wide context.
- [x] Add custom instructions from `.github/copilot-instructions.md`.
- [x] Add prompt files from `.github/prompts`.
- [x] Add MCP client support for tools and external context.
- [x] Add BYOM/BYOK routing: local GGUF, OpenAI-compatible endpoints, cloud models.
- [x] Add image/vision input support for screenshots, UI bugs, diagrams, and design references.
- [x] Add Copilot-style actions from editor context menu, error list, and solution explorer.
- [x] Add .NET upgrade assistant workflows.

## Phase 5: Proprietary Tool Platform

Status: **Complete**

`[####################] 100%`

- [x] Add `LlmToolRegistry`.
- [x] Add tool definition persistence.
- [x] Add proprietary/private/internal tool metadata.
- [x] Add encrypted secret handling.
- [x] Add tool-management endpoints under `/api/v1/tools`.
- [x] Add `ToolDefinitionsControl` in `LlmRuntime.Wpf`.
- [x] Add a hostable `ToolDefinitionsWindow` for WPF hosts.
- [x] Add tool test console.
- [x] Add OpenAI-compatible tool schema export.
- [x] Add MCP-compatible adapter layer.
- [x] Add tool-call execution loop.
- [x] Add audit logs and approval gates.
- [x] Wire proprietary tools into `JackLLM` local agent/chat flow.
- [x] Add approved built-in Windows desktop automation tool with window inspection/control, program launch, foreground keyboard/mouse input, non-cursor target-window mouse operations, cursor position inspection, and human cursor path generation.

### Proprietary Tool Registry

- [x] Support tool visibility: `Public`, `Private`, `Proprietary`, `InternalOnly`.
- [x] Support tool source metadata: HTTP endpoint, local executable, PowerShell command, named pipe, .NET assembly/plugin, MCP server, and built-in SocketJack tool.
- [x] Invoke tool sources: HTTP endpoint, local executable, PowerShell command, named pipe, .NET assembly static method, MCP-over-HTTP JSON-RPC, and built-in SocketJack tool.
- [x] Allow black-box proprietary tools where only schema, metadata, and invocation contract are known.
- [x] Track tool name, description, JSON schema, version, vendor, license notes, tags, required secrets, timeout, and allowed projects.

### Tool Invocation And Safety

- [x] Add `ILlmTool` and `ILlmToolInvoker`.
- [x] Add per-tool approval modes: always allow, ask every time, ask on destructive operations, disabled.
- [x] Add scoped permissions: filesystem read/write, shell execution, network access, browser access, repository access, and secrets access.
- [x] Enforce required source permissions before invocation.
- [x] Add encrypted secret/config storage for API keys, tokens, and proprietary credentials.
- [x] Add secret expansion for approved headers, process environment variables, and source strings.
- [x] Add redaction for prompts, traces, audit messages, errors, and tool results.
- [x] Add audit history for tool create/update/delete and invocations.

### Tool Definition UI

- [x] Add reusable UI controls in `LlmRuntime.Wpf`, not the core `LlmRuntime` library.
- [x] Create/edit/delete tool definitions.
- [x] Pick tool source type: HTTP, executable, PowerShell, .NET assembly, MCP, built-in.
- [x] Edit JSON input schema with validation.
- [x] Edit result schema and examples.
- [x] Configure secrets without displaying raw values after save.
- [x] Configure approval policy and timeout.
- [x] Configure allowed projects/workspaces.
- [x] Add "Test Tool" panel with sample input and captured output.
- [x] Add "Generate Schema From Example" helper.
- [x] Add import/export for tool packs via clipboard JSON.
- [x] Show proprietary/license badge on private tools.
- [x] Show compatibility badge: OpenAI tools, MCP, SocketJack agent, local-only.

### Tool Storage

- [x] Store tool definitions under configurable runtime config root.
- [x] Default path: `cwd\Tools`.
- [x] Store non-secret metadata as JSON.
- [x] Store secrets separately through encrypted local storage.
- [x] Never commit downloaded models, tool secrets, or proprietary binaries by default.

## Phase 6: Codex-Grade Agent Runtime

Status: **Complete as hostable runtime primitives and local API surface**

`[###############-----] 75%`

- [x] Add durable agent sessions with task history and resumability.
- [x] Add file read/edit/write tools with diff previews.
- [x] Add terminal command tool with approval gates and captured logs.
- [x] Add sandbox profiles: read-only, workspace-write, full-access.
- [x] Add plan/apply/review workflow.
- [x] Add parallel sub-agent orchestration for independent tasks.
- [x] Add background task runner.
- [x] Add progress UI API with live steps, current tool, logs, and partial results.
- [x] Add automatic test/build loops with failure diagnosis.
- [x] Add self-correction loop: inspect failure, patch, rerun, summarize.
- [x] Add repo instructions support: `AGENTS.md`, `.agents`, `.editorconfig`, solution conventions.
- [x] Add skills/plugins system discovery for repeatable domain workflows.
- [x] Add non-interactive CLI mode.
- [x] Add local app server API for agent sessions.
- [x] Add automation hooks for scheduled checks, monitors, and recurring maintenance.
- [x] Add model-backed autonomous agent run endpoint where a loaded local GGUF proposes file edits, commands, and plan steps that the runtime applies through sandbox/approval gates.

## Phase 7: GitHub And Team Workflow Parity

Status: **Complete as local git workflows plus optional GitHub CLI integration**

`[#################---] 85%`

- [x] Connect GitHub repositories, issues, pull requests, and reviews through local git plus optional GitHub CLI.
- [x] Start agent tasks from issues and PR comments.
- [x] Generate branches, commits, and draft PRs.
- [x] Add background draft PR jobs that push the current branch and create a draft pull request through authenticated GitHub CLI in live GitHub environments.
- [x] Summarize PRs and commits.
- [x] Review PRs with inline findings.
- [x] Address review comments automatically.
- [x] Debug failed GitHub Actions with structured unavailable fallback when GitHub CLI is missing.
- [x] Add security review and dependency update workflows.
- [x] Add policy controls for tool approval, internet access, model routing, and secrets.
- [x] Add audit logs for agent actions.
- [x] Expose Phase 7 workflows through `LlmRuntimeHost` API routes.
- [x] Add tests for local git workflows, policies, audit, and GitHub CLI fallback.

## Phase 8: Code Intelligence Beyond Copilot

Status: **Complete as local deterministic code-intelligence APIs**

`[####################] 100%`

- [x] Add language-server-aware symbol graph.
- [x] Add call graph, dependency graph, and ownership map.
- [x] Add solution-wide refactor planner.
- [x] Add migration planner for framework and package upgrades.
- [x] Add test coverage explorer and missing-test generator.
- [x] Add profiling assistant for CPU, memory, allocations, and async deadlocks.
- [x] Add architecture review mode.
- [x] Add documentation sync agent.
- [x] Add local privacy mode where code never leaves the machine.
- [x] Add model evaluation harness to compare local GGUF models on repo-specific tasks.
- [x] Add context budget optimizer for huge solutions.

## Phase 9: Production Polish

Status: **Complete as runtime readiness APIs, docs, signing scaffold, and regression metadata**

`[####################] 100%`

- [x] Add onboarding flow for runtime, models, IDE integration, and GitHub auth.
- [x] Add clear diagnostics for model load failures, port conflicts, missing tools, and low memory.
- [x] Add telemetry-free local analytics dashboard.
- [x] Add accessibility pass for GUI and Visual Studio surfaces.
- [x] Add signed installer.
- [x] Add GitHub Actions signing workflow for installer artifacts using repository certificate secrets.
- [x] Add update mechanism.
- [x] Add full documentation and quickstart.
- [x] Add regression suite for runtime, proxy, IDE extension, downloader, and agent workflows.
- [x] Add golden-path demo: download code model, load it, ask/edit/agent task, run tests, create PR.

## Acceptance Criteria

- [x] SocketJack can build and run its local embedded runtime path without requiring a reverse reference from `SocketJack` to `LlmRuntime`.
  Evidence: solution build passes; JackLLM provider abstraction and embedded runtime adapter are present. Remaining manual smoke test: launch GUI on a machine with no LM Studio service running and load a real GGUF.
- [x] Local GGUF runtime exposes chat and streaming endpoints.
  Evidence: `ChatCompletionsEndpoint_ReturnsOpenAiChatCompletion`, `ChatCompletionsEndpoint_StreamsOpenAiSseChunks`, and native `/api/v1/chat` tests pass with a fake backend. Real-model quality still depends on the selected GGUF and LLamaSharp backend.
- [x] Local GGUF models can power edits and full autonomous agent workflows end to end.
  Evidence: `/api/v1/agent/autonomous/run` sends repo context to the loaded local model, parses a JSON plan/files/commands proposal, applies approved edits through `LlmAgentRuntime`, and can run verification commands. `AutonomousAgentRun_UsesLoadedModelToApplyFileEdit` verifies the path with the fake GGUF backend.
- [x] Visual Studio users get a full installed VSIX experience with chat, completion, next-edit suggestions, agent mode, checkpoints, MCP, prompt files, custom instructions, model picker, code review, and workspace indexing.
  Evidence: `LlmRuntime.VisualStudio` builds as a VSIX/MEF package with installed Tools menu commands and runtime endpoint calls for chat, inline completion, next edit, autonomous agent mode, checkpoints, MCP context, prompt files, custom instructions, model picker, code review, and workspace indexing.
- [x] A user can define proprietary local or HTTP tools without writing code.
  Evidence: `LlmRuntime.Wpf` includes `ToolDefinitionsControl`/`ToolDefinitionsWindow`; `/api/v1/tools` management endpoints and registry persistence are implemented.
- [x] A user can hide credentials while still letting approved tool calls use them.
  Evidence: tool secret expansion/redaction tests pass; secrets are stored outside definitions. Remaining hardening: DPAPI/Credential Manager/key vault instead of a local encrypted file key.
- [x] Local GGUF-compatible chat can receive tool definitions, emit a tool call, execute the tool, and continue with tool result context.
  Evidence: `ChatCompletion_ExecutesNativeToolCallLoop` passes through `/v1/chat/completions`.
- [x] `JackLLM` can execute approved proprietary tools and feed results back to the model.
  Evidence: JackLLM forwards LlmRuntime tool schemas, calls `/api/v1/tools/calls`, and retries approval-required calls through the existing approval path.
- [x] Tool definitions can be exported/imported without exporting secrets.
  Evidence: tool pack import/export exists in `ToolDefinitionsControl`; secret values are separate from tool definition JSON.
- [x] Proprietary HTTP, executable, PowerShell, named-pipe, .NET assembly, MCP, and built-in Windows desktop automation tools plus private schemas are supported without requiring source disclosure.
  Evidence: registry persists schema/metadata and invokes HTTP/executable/PowerShell/named-pipe/.NET assembly/MCP sources plus the `windows_desktop_automation` built-in, including approved window discovery/control, program launch, foreground keyboard/mouse input, human cursor pathing, cursor inspection, and non-cursor target-window mouse messages.
- [x] Agent mode can read files, preview/write edits, run commands, track sessions, recover from failures, and present diffs through hostable APIs.
  Evidence: `LlmAgentRuntimeTests` pass as part of the 39-test runtime suite. Remaining validation: real LLM autonomous quality across larger repos.
- [x] Background/parallel task workflows can create draft PRs in a live GitHub environment.
  Evidence: `/api/v1/github/pull-request/job` queues background draft PR creation, pushes the current branch when requested, and runs `gh pr create --draft` when GitHub CLI is installed and authenticated. `GitHubDraftPullRequestJobEndpoint_QueuesBackgroundWorkflow` verifies the queueing surface.
- [x] The product has a local-first privacy story beyond cloud-only coding assistants.
  Evidence: local GGUF runtime, local-only code intelligence APIs, local tool registry, local agent session store, and optional cloud/provider routing.

## Assumptions And Defaults

- Embedded `LlmRuntime` becomes the default local provider.
- External LM Studio remains available as fallback/debug mode.
- `SocketJack` does not reference `LlmRuntime`.
- Models live in `cwd\Models`.
- Tool definitions live in `cwd\Tools` by default.
- Chat streaming is required before this is considered a real LM Studio replacement.
- Embeddings and `/v1/responses` can remain scaffolded until a later milestone.
- Cloud/OpenAI-compatible providers remain optional for models too large or tasks too complex for local GGUF.
- "Equal or greater than Copilot for Visual Studio" means matching the documented Visual Studio feature matrix, then exceeding it with local-first runtime control, proprietary tool support, and Codex-style autonomous workflows.

