# SocketJack Updates

Last updated: 2026-05-11

This change log is synthesized from repository progress files that contain "progress" in the filename. It focuses on product-facing and engineering milestones rather than every individual code edit.

## 2026-05-11

### Documentation And Inventory

- Added this root-level update log from the progress trackers.
- Added `FEATURES.md` as a root-level feature inventory.
- Prepared the repository documentation set for a `/Docs` HTML browser backed by Markdown files from the Git root.

### SockJackDml On SocketJack.com

- Completed the SocketJack.com SockJackDml surface.
- Added `/sockjackdml/*` routes that load the mission-control application.
- Added durable SockJackDml storage and backend APIs in `JackLLM`.
- Added chat header navigation into SockJackDml.
- Added service catalog integration and project content copy for the SockJackDml resource.
- Completed Mission Control, mission packs, Live AI Operator, zero-trust remote assist, capability scoring, evidence packets, JSON evidence export, and realtime accessibility artifacts.

### SockJackDml Plan, Progress, And Execution Tools

- Added durable workflow state for plans, progress documents, and execution records.
- Added `sockjackdml_plan_create`, `sockjackdml_progress_document_create`, and `sockjackdml_plan_execute` tool schemas.
- Added local `/api/sockjackdml/tools/*` endpoints.
- Added approval-gated execution modes for preview, approved apply, and auto approval-gated file, Git, and terminal actions.
- Mirrored proprietary HTTP tool definitions into embedded LlmRuntime export through `/api/v1/tools/openai`.
- Verification recorded passing builds for `SocketJack`, `JackLLM`, and `LlmRuntime`, plus passing `LlmRuntime.Tests`.

### Companion Process Control And Start Browser

- Completed guarded process mutation actions in `CompanionProcessService`.
- Added `KillProcess` with protected PID and self-process refusal.
- Added `StartProcess` with path and working-directory validation.
- Added a bounded custom filesystem browser for WPF and web start-process pickers.
- Added `GET /api/companion/process-browser`, `POST /api/companion/processes/start`, and `POST /api/companion/processes/kill`.
- Added hover-only Kill buttons, start path and argument fields, and embedded file browsing in the `/Workspace` Processes tab.
- Added matching WPF process row actions and browser controls.
- Smoke verification started a hidden short-lived PowerShell process and rejected a protected PID kill with HTTP 400.

### Companion Running Processes And Windows Tool Service

- Completed a process/window inventory service for `JackLLMCompanion`.
- Added process snapshots with PID, name, executable path, memory, admin state, denied/unavailable reasons, CPU, RAM, total RAM, and best-effort GPU metadata.
- Added visible top-level window enumeration with Win32 `EnumWindows`, title/class reads, and PID joins.
- Added `GET /api/companion/processes` and `GET /api/companion/windows`.
- Added query, windowed-only, take/limit, include-system, and sort options.
- Added a sortable/filterable WPF Processes tab with refresh, auto-refresh, window-only toggle, resource columns, admin state, and Open File Location.
- Added a web Processes tab in `/Workspace`.
- Added bounded read-only runner tools: `list_running_processes` and `list_open_windows`.
- Verification recorded passing `JackLLMCompanion` and `JackLLM` builds plus local API smoke checks.

### Companion Self-Training Skills

- Completed the companion self-training pipeline.
- Added training settings, runs, evidence, draft skills, review helpers, skill execution rows, and enabled-skill ranking in the repository layer.
- Added recording evidence capture and minimized replay keyframes.
- Added redaction and sensitivity tagging for account/login, money, chat, file, settings, internet, keyframe, and secret-like evidence.
- Added background training runs with progress, cancellation, evidence packing, draft generation, malformed model output fallback, and `needs_model` fallback.
- Added repository, web, and WPF review flows for approve, enable, reject, auto-enable low-risk, and enable-all warning behavior.
- Connected reviewed enabled skills into the LLM runner prompt through context ranking.
- Added WPF and `/Workspace` Training tabs.
- Added training state/start/cancel/settings, skill review, replay index, and replay frame routes.
- Added a detailed `JackLLMCompanion/README.md` user guide.
- Replaced companion model-name text entry with WPF and web dropdowns populated from JackLLM/runtime model lists.

### Companion Remote Desktop, File Sharing, And LLM Control

- Completed the LLM remote desktop and file sharing installment.
- Added WPF and web emergency stop controls.
- Added a global `Ctrl+Esc` WPF hotkey path that disables Live Input, stops queued LLM tasks, and stops recording.
- Added WPF and `/Workspace` LLM Control tabs with goal/mode task queues and stop controls.
- Added a background LLM runner that observes the desktop, calls the configured OpenAI-compatible endpoint, parses structured actions, repairs malformed model output, executes one gated action at a time, records progress, and supports cancellation.
- Added WPF and web remote desktop controls with capture/live view, click-to-control, keyboard/text controls, and gated input.
- Added desktop frame polling, chunked NDJSON streaming, WebSocket streaming, cursor metadata, and adaptive frame quality.
- Added WPF and web file sharing with drag/drop, upload/download, folder upload, local path registration, approval retry flows, and sensitive/executable file approval gates.

### ONNX Model Browser And Conversion

- Completed ONNX-ready model browsing and conversion infrastructure.
- Added reusable `ModelRepositoryScanner`.
- Updated the WPF model browser to distinguish GGUF, ONNX, source tensor, format, action, and unsupported rows.
- Added direct ONNX registration through manifest writing.
- Added source tensor conversion jobs, bundle downloads, Optimum/Transformers/custom exporter attempts, job status/cancel tracking, conversion reports, and WPF status updates.
- Verification recorded `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` with 0 errors.
- `LlmRuntime.Tests` passed 53/53 and `JackONNX.Tests` passed 7/9 with 2 native provider tests self-skipped when unavailable.

### JackONNX Foundation

- Completed JackONNX project creation.
- Added project family, contracts, local manifest catalog, provider scaffolding, jobs, artifacts, progress streaming, CLI, docs, samples, and tests.
- Added CPU ONNX runtime support, DirectML provider support, and CUDA provider support.
- Added image, audio, and video generation foundations with model-specific execution deferred until local model layouts are selected.
- Connected JackONNX to LlmRuntime tools and SocketJack routes.
- Added artifact serving and SSE progress streaming.
- Latest validation recorded `JackONNX.Tests`, CLI manifest validation, and `JackLLM` build success.

### Endpoint Security And Bot Filtering

- Completed the endpoint security monitor and bot filtering installment.
- Added `Net/EndpointSecurity.cs` with per-IP scoring, throttle delay, temporary disablement, event frequency multipliers, decay, snapshots, and manual disable/enable.
- Added moderate `NetworkOptions.EndpointSecurity` defaults and localhost exemptions.
- Added HTTP request gating, post-response recording, and blocked responses with retry metadata.
- Wired SocketJack protocol routing, unknown protocol probes, SocketJack frames, WebSocket handshakes, and WebSocket frames into the shared monitor.
- Hardened static file resolution against mapped-root escapes and traversal probes.
- Added default-on daily HTTP access logs under `C:\JackLLM\Logs`.
- Added Loose, Firm, and Strict tuning profiles.
- Verification recorded `dotnet build SocketJack.csproj` with 0 warnings and 0 errors.

### SecureAuthority Update Routing

- Routed update publishing through `https://socketjack.com/SecureAuthority/`.
- Defaulted the private update target to `http://127.0.0.1:8500/`.
- Moved `SocketJack.Update` default bind to `127.0.0.1:8500`.
- Updated publisher defaults, update-server public URL, deploy repair script, GUI references, and public README references to the HTTPS SocketJack.com authority.
- Added SocketJack website proxy routes for `GET`, `POST`, and `OPTIONS` on `/SecureAuthority/*`.
- Verification recorded successful `SocketJack-MagicMasterList`, `SocketJack.Update`, and `SocketJack.Update.Publisher` builds plus clean PowerShell repair script parsing.

### JackLLM Session Management Refresh

- Expanded scope from side-panel refresh to a broader session-management installment.
- Added rename API work and title persistence work.
- Started edge toggle button, Sessions drawer, Comments drawer, clipping, and inline rename UI work.
- Remaining planned work includes save, lock/unlock, clone, regex search, regex helper, prompt-token progress, WPF controls, compatibility surfaces, and verification.
- Current overall progress in `SocketJack/progress.md`: 12%.

## Deferred Next Steps

- Finish the JackLLM session-management refresh.
- Select local model layouts for JackONNX image, audio, and video execution.
- Implement model-specific ONNX graph orchestration after those layouts are selected.
- Run a live Web Chat Agent-mode smoke test for the three SockJackDml workflow tools.


