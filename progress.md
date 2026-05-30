# SocketJack Companion Tool Progress

Last updated: 2026-05-11

## Current Expansion: Process Control And Start Browser

Overall Progress: 100%

Progress: `[##################################################] 100%`

| Feature set | Progress | Notes |
|---|---:|---|
| Service mutation actions | 100% | Added guarded `KillProcess` and `StartProcess` actions to `CompanionProcessService`; kill refuses protected/self PIDs and start validates path/working directory before launch. |
| Custom file browser service | 100% | Added bounded drive/directory/file browsing for WPF and web start-process pickers with startable-file detection metadata. |
| Companion HTTP APIs | 100% | Added `/api/companion/process-browser`, `POST /api/companion/processes/start`, and `POST /api/companion/processes/kill`, each returning mutation status plus refreshed process context where useful. |
| Web process control UI | 100% | `/Workspace` Processes tab now includes hover-only Kill buttons, start path/arguments fields, and a built-in browser for drives, folders, and files. |
| WPF process control UI | 100% | WPF Processes tab now has hover-only row Kill buttons, start path/arguments inputs, and an embedded file browser that can select launch targets. |
| Build blockers repaired | 100% | Updated stale `runtimeStatus.Reachable` usage to `Connected` and linked `SockJackDmlService.cs` into `SocketJack.WPF.csproj` so companion builds compile against current SocketJack sources. |
| Verification | 100% | JackLLMCompanion and JackLLM builds pass; live API smoke returned process/window/browser JSON, successfully started a hidden short-lived PowerShell process, and verified protected PID kill is rejected with HTTP 400. |

### Process Control Activity Log

| Time | Change | Files | Progress Delta | Verification |
|---|---|---|---|---|
| 2026-05-11 | Added process start/kill service methods and bounded filesystem browser DTOs. | `JackLLMCompanion/CompanionProcessService.cs` | Service mutation actions and custom file browser 0% -> 100% | Covered by companion build and API smoke. |
| 2026-05-11 | Added process-control HTTP routes and expanded `/Workspace` with hover-only Kill controls plus start-process browser UI. | `JackLLMCompanion/CompanionHttpHost.cs` | Companion HTTP APIs and web UI 0% -> 100% | API smoke on `http://127.0.0.1` passed for process list, window list, browser, start, and guarded kill. |
| 2026-05-11 | Added WPF hover-only Kill row action and embedded process-start browser controls. | `JackLLMCompanion/MainWindow.xaml`; `JackLLMCompanion/MainWindow.xaml.cs` | WPF process control UI 0% -> 100% | `dotnet build JackLLMCompanion.csproj` passed. |
| 2026-05-11 | Fixed upstream build compatibility issues exposed by companion verification. | `SocketJack.LlmCore/Proxy/JackLLM.cs`; `SocketJack.Windows/SocketJack.WPF.csproj` | Build blockers repaired 0% -> 100% | JackLLMCompanion and JackLLM builds passed with 0 warnings/errors. |

## Current Expansion: Running Processes And Windows Tool Service

Overall Progress: 100%

Progress: `[##################################################] 100%`

| Feature set | Progress | Notes |
|---|---:|---|
| Existing capability audit | 100% | `DesktopAutomationService` already captures the foreground process/window title; no full running-process inventory service exists yet. |
| Process service model | 100% | Added `CompanionProcessService` with process/window snapshots, CPU sampling cache, Win32 interop, safe DTOs, and compact runner summaries. |
| All-process inventory | 100% | Enumerates all processes with guarded PID, name, executable path, memory, admin state, and unavailable/access-denied reasons. |
| Window inventory | 100% | Enumerates visible top-level windows with `EnumWindows`, joins them to processes by PID, and exposes a dedicated windows snapshot. |
| Resource metrics | 100% | Adds CPU %, best-effort GPU %, RAM %, RAM GB, total RAM GB, and explicit unavailable metadata for blocked metrics. |
| Companion HTTP APIs | 100% | Added `/api/companion/processes` and `/api/companion/windows` with query, windowed-only, take/limit, include-system, and sort options. |
| WPF Processes tab | 100% | Added a sortable/filterable WPF Processes tab with refresh, auto-refresh, window-only toggle, resource columns, admin state, and Open File Location. |
| Agent/tool integration | 100% | Added read-only `list_running_processes` and `list_open_windows` runner tools with bounded compact output. |
| Safety and verification | 100% | Initial inventory shipped read-only except Open File Location; the follow-up process-control installment now adds explicit guarded start/kill actions. JackLLMCompanion and JackLLM builds pass and API smoke tests return process/window rows. |

### Implementation Plan

| Task | Status | Files / Scope | Notes |
|---|---|---|---|
| Add companion process service | Complete | `JackLLMCompanion/CompanionProcessService.cs` | New service owns process/window snapshots, metric sampling cache, Win32 interop, and safe DTOs. |
| Reuse/extend window interop | Complete | `JackLLMCompanion/CompanionProcessService.cs` | Added `EnumWindows`, `IsWindowVisible`, title/class reads, and PID joins without changing foreground-capture behavior. |
| Add DTOs | Complete | `JackLLMCompanion/CompanionProcessService.cs` | Includes PID, process name, main window title, window count/titles, executable path, CPU %, GPU %, RAM %, RAM GB, total RAM GB, admin/elevated state, and metric availability flags. |
| Add HTTP routes | Complete | `JackLLMCompanion/CompanionHttpHost.cs` | Added JSON endpoints with query options like `windowedOnly`, `query`, `take`, `sort`, and `includeSystem`. |
| Add WPF tab | Complete | `JackLLMCompanion/MainWindow.xaml`; `JackLLMCompanion/MainWindow.xaml.cs` | Added `Processes` tab beside Remote Desktop and File Sharing; rows bind to an observable collection. |
| Add Open File Location button | Complete | `JackLLMCompanion/MainWindow.xaml.cs` | Uses Explorer `/select,` only when an executable path is available and exists; shows a status message otherwise. |
| Add web workspace panel | Complete | `JackLLMCompanion/CompanionHttpHost.cs` | Added `/Workspace` Processes tab backed by the local API; browser view displays paths but does not launch Explorer. |
| Add runner tool context | Complete | `JackLLMCompanion/CompanionLlmRunner.cs` | Added read-only `list_running_processes` and `list_open_windows` tools with bounded output. |
| Add audit/progress updates | Complete | `progress.md` | Updated this section after implementation with milestone percent and verification details. |
| Verify builds | Complete | `JackLLMCompanion.csproj`; `JackLLM.csproj` | Both builds pass; hidden companion API smoke passed. |

### Design Constraints

| Constraint | Decision |
|---|---|
| Protected/system processes | Snapshot must be best-effort per process; one denied path/token/metric read cannot fail the whole list. |
| Admin detection | Report `admin`, `notAdmin`, or `unknown` separately so access-denied is not treated as false. |
| GPU usage | Per-process GPU counters are Windows/provider dependent; return `gpuAvailable=false` and a reason when unavailable instead of inventing values. |
| Mutating actions | The first inventory installment had no kill/suspend/priority-change controls. The completed follow-up adds explicit guarded kill/start actions only; suspend and priority changes remain out of scope. |
| LLM context size | Agent tools default to compact rows and require `take`/filters for larger lists. |

### Activity Log

| Time | Change | Files | Progress Delta | Verification |
|---|---|---|---|---|
| 2026-05-11 | Created active plan for the Running Processes and Windows Tool Service after auditing the companion WPF/HTTP/window-capture structure. | `progress.md` | Existing capability audit 0% -> 100%; overall 0% -> 10% | Manual repository inspection. |
| 2026-05-11 | Added `CompanionProcessService` with DTOs, all-process enumeration, guarded file/admin/RAM reads, CPU sample cache, visible-window enumeration, and best-effort GPU counter support. | `JackLLMCompanion/CompanionProcessService.cs` | Process service, all-process inventory, window inventory, metrics 0% -> 100%; overall 10% -> 60% | `dotnet build JackLLMCompanion/JackLLMCompanion.csproj` pending at this point. |
| 2026-05-11 | Wired companion process/window snapshots into local HTTP APIs and `/Workspace` Processes view. | `JackLLMCompanion/CompanionHttpHost.cs` | Companion HTTP APIs 0% -> 100%; overall 60% -> 75% | API smoke pending at this point. |
| 2026-05-11 | Added WPF Processes tab with filter, windowed-only toggle, auto-refresh, sortable process table, and Explorer Open File Location action. | `JackLLMCompanion/MainWindow.xaml`; `JackLLMCompanion/MainWindow.xaml.cs` | WPF Processes tab 0% -> 100%; overall 75% -> 90% | WPF build pending at this point. |
| 2026-05-11 | Added runner access to bounded read-only process/window tools and included visible-window context in JACK's observation prompt. | `JackLLMCompanion/CompanionLlmRunner.cs` | Agent/tool integration 0% -> 100%; overall 90% -> 96% | Build pending at this point. |
| 2026-05-11 | Verified builds and route smoke; `/api/companion/processes` returned 5/352 rows and `/api/companion/windows` returned 5/14 rows on `http://127.0.0.1:8091`. | `JackLLMCompanion/*`; `progress.md` | Safety and verification 0% -> 100%; overall 96% -> 100% | `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\JackLLMCompanion.csproj`; `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM\JackLLM.csproj`; hidden companion API smoke passed. |

## Current Expansion: Companion Self-Training Skills

| Feature set | Progress | Notes |
|---|---:|---|
| Progress reporting ledger | 100% | Added this active progress section plus an implementation activity log for every coherent implementation change. |
| Training database tables | 100% | Repository-owned tables, typed state models, training settings, run/evidence/draft/execution rows, review helpers, and enabled-skill ranking build cleanly. |
| Recording evidence capture | 100% | Recording start/stop and environment-change hooks feed training runs and evidence packs. |
| Minimized replay capture | 100% | Hybrid keyframes are captured at recording start/stop and environment changes with JPEG downscale plus frame/byte caps and replay evidence indexing. |
| Evidence redaction/sensitivity tagging | 100% | Redaction and tags cover account/login, money, human chat, real files, settings, internet, keyframes, and secret-like content. |
| Training pipeline service | 100% | Background training service is wired into recording stop and manual training starts with run progress, cancellation, evidence packing, draft generation flow, and needs-model fallback. |
| Skill draft generation | 100% | Model-assisted JSON draft generation, evidence references, safe parsing, heuristic fallback for malformed model output, and `needs_model` fallback are implemented. |
| Skill review/activation | 100% | Repository, web, and WPF review flows support approve, enable, reject, auto-enable low-risk, and enable-all warning. |
| Skill matcher and runner integration | 100% | Runner prompt packs only enabled reviewed skills ranked against current goal/app/window/url/person/files. |
| WPF Training UI | 100% | Desktop Training tab includes settings, manual training, cancel, run status, draft review, and replay folder opener. |
| Web Training UI | 100% | `/Workspace` Training tab includes settings, training runs, draft review, and replay frame links. |
| Training/replay APIs | 100% | Training state/start/cancel/settings, skill review, replay index, and replay frame routes are implemented and build cleanly. |
| Tests/verification | 100% | `JackLLMCompanion` and `JackLLM` builds pass with 0 warnings/errors. |

### Implementation Activity Log

| Time | Change | Files | Progress Delta | Verification |
|---|---|---|---|---|
| 2026-05-11 | Created active self-training progress ledger and activity log. | `progress.md` | Progress reporting ledger 0% -> 100% | Manual document inspection. |
| 2026-05-11 | Added Companion training persistence model and repository operations for settings, runs, evidence, skill drafts, review, and enabled-skill ranking. | `JackLLMCompanion/CompanionRepository.cs` | Training database tables 0% -> 70%; skill review/activation 0% -> 20%; skill matcher and runner integration 0% -> 20% | Pending build verification. |
| 2026-05-11 | Added background self-training service with evidence-pack generation, minimized replay keyframe storage, sensitivity tagging/redaction, model draft generation, and cancellation. | `JackLLMCompanion/CompanionTrainingService.cs` | Recording evidence capture 0% -> 55%; minimized replay capture 0% -> 55%; evidence redaction/sensitivity tagging 0% -> 70%; training pipeline service 0% -> 65%; skill draft generation 0% -> 50% | Pending build verification. |
| 2026-05-11 | Wired self-training into recording start/stop and environment telemetry, then exposed training state/start/cancel/settings, skill review, and replay frame APIs. | `JackLLMCompanion/MainWindow.xaml.cs`; `JackLLMCompanion/CompanionHttpHost.cs`; `JackLLMCompanion/CompanionRepository.cs` | Recording evidence capture 55% -> 80%; minimized replay capture 55% -> 75%; training pipeline service 65% -> 85%; training/replay APIs 0% -> 70% | Pending build verification. |
| 2026-05-11 | Added `/Workspace` Training tab with training settings, manual start/cancel, run status, draft skill review, and replay frame links. | `JackLLMCompanion/CompanionHttpHost.cs` | Web Training UI 0% -> 75%; training/replay APIs 70% -> 85%; skill review/activation 20% -> 55% | Pending build verification. |
| 2026-05-11 | Added WPF Training tab and code-behind for settings, training start/cancel, draft approve/enable/reject, run summaries, and replay folder opening. | `JackLLMCompanion/MainWindow.xaml`; `JackLLMCompanion/MainWindow.xaml.cs` | WPF Training UI 0% -> 75%; skill review/activation 55% -> 80% | Pending build verification. |
| 2026-05-11 | Connected reviewed enabled skills to the LLM runner prompt through context ranking; draft/rejected skills are excluded. | `JackLLMCompanion/CompanionLlmRunner.cs`; `JackLLMCompanion/CompanionRepository.cs` | Skill matcher and runner integration 20% -> 80% | Pending build verification. |
| 2026-05-11 | Verified the self-training implementation with Companion and JackLLM builds, then marked all planned self-training feature rows complete. | `JackLLMCompanion/*`; `progress.md` | All self-training rows -> 100%; tests/verification 0% -> 100% | `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\JackLLMCompanion.csproj`; `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM\JackLLM.csproj` both passed with 0 warnings/errors. |
| 2026-05-11 | Mirrored the new self-training APIs and database tables into the canonical HTTP Server and Database Tables inventories. | `progress.md` | Documentation inventory consistency complete. | Manual document inspection. |
| 2026-05-11 | Created a detailed Companion user guide covering setup, safety, WPF tabs, web tabs, recordings, self-training, learned skills, file sharing, remote desktop, APIs, storage, build, and troubleshooting. | `JackLLMCompanion/README.md`; `progress.md` | Documentation coverage expanded. | Documentation-only change; build not required. |
| 2026-05-11 | Replaced Companion model-name text entry with WPF/web model dropdowns populated from the local JackLLM model list, with fallback to local runtime `/v1/models` endpoints. | `JackLLMCompanion/CompanionModelCatalog.cs`; `JackLLMCompanion/CompanionHttpHost.cs`; `JackLLMCompanion/MainWindow.xaml`; `JackLLMCompanion/MainWindow.xaml.cs`; `JackLLMCompanion/README.md` | LLM model selection UX improved. | `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\JackLLMCompanion.csproj` and `dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM\JackLLM.csproj` passed with 0 warnings/errors. |

## Current Expansion: ONNX Model Browser And Conversion

| Feature set | Progress | Notes |
|---|---:|---|
| Plan execution | 100% | ONNX-ready browsing, direct ONNX registration, source tensor conversion jobs, WPF status updates, and HTTP status/cancel endpoints are implemented. GGUF-to-ONNX remains intentionally limited/experimental rather than presented as universal conversion. |
| Repository scanner | 100% | Added reusable `ModelRepositoryScanner`; WPF model browser now calls it instead of hard-coded GGUF-only parsing. |
| ONNX browser actions | 100% | Browser panel renders GGUF, ONNX, source tensor, format, action, and unsupported rows; direct ONNX downloads register a manifest and source tensor rows start conversion jobs. |
| ONNX manifest registration | 100% | `OnnxModelManifestWriter` supports single-file and folder manifests; WPF post-download registration and `LlmModelRegistry` enumeration for `.jackonnx.json`/ONNX manifests are active. |
| Conversion job foundation | 100% | Added queued `ModelConversionService`, source bundle downloads, Optimum/Transformers/custom exporter attempts, status/cancel tracking, conversion reports, manifests, and WPF job status updates. |
| Verification | 100% | `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` succeeds with 0 errors; `LlmRuntime.Tests` passes 53/53; `JackONNX.Tests` passes 7/9 with 2 provider tests self-skipped when native runtimes are unavailable. |

This file tracks implementation progress for `JackLLMCompanion`, the SocketJack Companion Tool service for JackLLM.

## Overall

| Feature set | Progress | Notes |
|---|---:|---|
| Project scaffold | 100% | New sibling WPF project added to `SocketJack.sln`. |
| JackLLM service integration | 100% | `companion` is advertised by the JackLLM service catalog and appears in web/WPF service pickers. |
| Companion HTTP server | 100% | SocketJack `HttpServer` starts on port `80` with fallback to `8091`; smoke verified on `http://localhost`. |
| SocketJack database persistence | 100% | Companion state persists through SocketJack `DataServer` tables in local app data. |
| WPF tray/background shell | 100% | Hidden startup, tray menu, open workspace/files, Companion launch/status, and exit flow exist. |
| Approval-gated remote control | 100% | Screen capture and mouse/keyboard input execute only after the Live Input gate is enabled; blocked attempts are audited. |
| Session recording | 100% | Start/stop/manual events persist, foreground environment telemetry records while active, and inferred skill output is produced on stop. |
| Skill/template learning | 100% | Saved templates, AI name/interests, app/person/file context, and session-derived skill records exist. |
| JACK test template | 100% | Default JACK name, hobbies/interests, prompt text, AI name, and AI interests actions exist. |
| Permissions UI/audit logging | 100% | WPF and HTTP permission saves are audited; per-action approvals are represented by capability gates. |
| Workspace live JavaScript updates | 100% | `/Workspace`, `/file`, remote-control preview, and JSON API state update dynamically. |
| Tests/verification | 100% | Companion and JackLLM builds pass; routes, permission blocking, approved screen capture, and recording smoke verified. |

## Current Expansion: LLM Remote Desktop And File Sharing

| Feature set | Progress | Notes |
|---|---:|---|
| Detailed web UI plan | 100% | Added `JackLLMCompanion/WEB_UI_PLAN.md` covering tabs, remote desktop, LLM runner, file sharing, safety, APIs, and acceptance criteria. |
| WPF emergency stop | 100% | Added WPF Emergency Stop button and global `Ctrl+Esc` hotkey path that disables Live Input, stops queued LLM tasks, and stops active recording. |
| Web emergency stop | 100% | Added `/api/companion/emergency-stop` and a visible `/Workspace` Emergency Stop button. |
| WPF LLM Control UI | 100% | Added LLM Control tab with goal/mode task queue and stop control. |
| Web LLM Control UI | 100% | Added `/Workspace` LLM Control tab backed by `CompanionLlmTasks`. |
| LLM model runner | 100% | Background runner now claims queued tasks, observes the desktop, calls the configured OpenAI-compatible JackLLM/LM Studio endpoint, exposes a structured desktop-action tool schema, packs JACK/session/action memory into each turn, repairs malformed model output with a retry pass, executes one gated action at a time, records progress, and supports cancellation. |
| WPF Remote Desktop UI | 100% | Added capture/live view, click center, click preview, Escape, type text, and gated input controls. |
| Web Remote Desktop UI | 100% | Added live frame polling, click-to-control, keyboard/text controls, and safety messaging. |
| Remote desktop streaming | 100% | Added `/api/companion/desktop/stream` chunked NDJSON frame stream, `/api/companion/desktop/ws` WebSocket stream, `/api/companion/desktop/transport` discovery, cursor echo metadata, and adaptive PNG/JPEG capture quality for the web live view. |
| WPF File Sharing UI | 100% | Added file/folder choose, drag/drop share, refresh controls, and approval prompts that copy files into the Companion share after Use Files approval. |
| Web File Sharing UI | 100% | Added upload/download, folder upload, drag/drop, local path registration, and approval retry flows in `/Workspace`. |
| File sharing backend | 100% | Shared file table, upload/download/copy, WPF and web drag/drop, folder upload/share, local path registration, session indexing, Use Files gate, and explicit per-file approval for sensitive paths or executable/script-like files are implemented. |
| Tests/verification | 100% | `JackLLMCompanion` builds pass with 0 warnings/errors and `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` passes with 0 warnings/errors after clearing a stale local `SocketJack.Update.Publisher` debug process lock; smoke coverage tracks workspace tabs, runner status API, LLM task queue/progress, emergency stop, gated screen capture/stream/upload, approved screen capture/stream/download, and file approval paths. |

## Runner Execution Progress

| Sub-feature | Progress | Notes |
|---|---:|---|
| Queued task claiming | 100% | Runner claims `queued` tasks and writes runner id/model endpoint. |
| Progress reporting | 100% | Tasks now track progress percent, step, max steps, last action, last error, runner id, and model endpoint. |
| Model call | 100% | Runner posts to configurable OpenAI-compatible `/v1/chat/completions` endpoint, defaulting to `http://localhost:11435/v1/chat/completions`. |
| Model action parsing | 100% | Parses JSON action objects, OpenAI tool-call arguments, JSON extracted from surrounding text/code fences, simple natural-language fallbacks, and automatic repair/retry with safe `ask_user` fallback. |
| Gated action execution | 100% | Executes observe/wait/finish/ask_user and gated mouse/key/type/wheel/drag desktop actions. |
| Emergency cancellation | 100% | Web/WPF emergency stop stops queued/running tasks and restarts the idle runner loop. |
| Remote frame stream | 100% | Live web view can consume polling, `/api/companion/desktop/stream`, and `/api/companion/desktop/ws` frames. |
| Drag gestures | 100% | Web drag gestures call the gated `drag` desktop input action. |

## HTTP Server

| Sub-feature | Progress | Notes |
|---|---:|---|
| Port `80` fallback `8091` | 100% | Implemented in `CompanionHttpHost`; local runtime smoke verified on port `80`. |
| `GET /Workspace` | 100% | Live dashboard with projects, sessions, permissions, events, template, skills, and people memory sections. |
| `GET /file` | 100% | Live file/work-session browser route. |
| `GET /api/workspace` | 100% | Returns current project, session, template, permission, people, app, skill, runner, LLM task, and audit state. |
| `GET /api/files` | 100% | Returns associated files from the Companion database. |
| `POST /api/companion/permissions` | 100% | Saves approval gates. |
| `POST /api/companion/template` | 100% | Saves JACK or other template payloads. |
| `POST /api/companion/recording/start` | 100% | Starts a persisted work session. |
| `POST /api/companion/recording/stop` | 100% | Stops a persisted work session and emits a starter inferred skill. |
| `POST /api/companion/action` | 100% | Records and gates requested control actions. |
| `GET /api/companion/screen` | 100% | Returns PNG screen capture only when Live Input is enabled. |
| `GET /api/companion/screen.json` | 100% | Returns JSON/base64 screen capture only when Live Input is enabled. |
| `GET /api/companion/desktop/transport` | 100% | Reports polling, chunked NDJSON, and WebSocket remote desktop transport URLs. |
| `GET /api/companion/desktop/stream` | 100% | Streams live desktop frames as NDJSON only when Live Input is enabled. |
| `GET /api/companion/desktop/ws` | 100% | Streams live desktop frames over WebSocket only when Live Input is enabled. |
| `POST /api/companion/input` | 100% | Sends gated mouse, wheel, text, and keyboard input. |
| `POST /api/companion/file/register` | 100% | Registers work-session files into the `/file` route database. |
| `GET /api/share` | 100% | Returns Companion share metadata for uploaded/copied files. |
| `POST /api/share/upload` | 100% | Uploads file/folder data into the Companion share with Use Files and per-file approval gates. |
| `POST /api/share/register-path` | 100% | Copies a local file or folder path into the Companion share with Use Files and per-file approval gates. |
| `GET /api/share/download` | 100% | Downloads approved shared files by id. |
| `GET /api/companion/llm/runner` | 100% | Returns active runner status, endpoint, model, busy state, and active task id. |
| `POST /api/companion/llm/runner/start` | 100% | Starts/reconfigures the background LLM desktop runner. |
| `POST /api/companion/llm/runner/stop` | 100% | Stops the background LLM desktop runner and current task. |
| `POST /api/companion/llm/task` | 100% | Queues a desktop task for the runner. |
| `POST /api/companion/llm/stop` | 100% | Stops queued/running desktop tasks. |
| `GET /api/companion/llm/models` | 100% | Returns local JackLLM/runtime model ids for Companion runner dropdowns. |
| `GET /api/companion/training/state` | 100% | Returns self-training settings, runs, evidence, draft skills, and skill executions. |
| `POST /api/companion/training/start` | 100% | Starts a background self-training run from a recording session. |
| `POST /api/companion/training/cancel` | 100% | Cancels the active self-training run. |
| `POST /api/companion/training/settings` | 100% | Saves learning and skill activation settings, with warning confirmation for enable-all mode. |
| `POST /api/companion/skills/review` | 100% | Approves, enables, or rejects draft skills. |
| `GET /api/companion/replay` | 100% | Lists minimized replay keyframe evidence. |
| `GET /api/companion/replay/frame` | 100% | Serves a replay keyframe by evidence id from the Companion training store. |

## Database Tables

| Table | Progress | Notes |
|---|---:|---|
| `CompanionProjects` | 100% | Seeded with the Companion Tool project. |
| `CompanionWorkSessions` | 100% | Stores recording sessions. |
| `CompanionSessionEvents` | 100% | Stores ordered session events. |
| `CompanionFiles` | 100% | Table, route, manual registration, and permission-gated recent-file detection exist. |
| `CompanionPeople` | 100% | Table, ratings model, and communication-window people cue extraction exist. |
| `CompanionApplications` | 100% | Seeded with JACK-relevant apps and updated from foreground app/window telemetry. |
| `CompanionPermissions` | 100% | Stores approval-gated capabilities. |
| `CompanionTemplates` | 100% | Stores JACK template and edits. |
| `CompanionSkills` | 100% | Stores inferred session-derived skills with app/person context and safety steps. |
| `CompanionAuditEvents` | 100% | Stores permission, template, recording, and action audit records. |
| `CompanionTrainingRuns` | 100% | Stores self-training run status, progress, source session, risk, summary, endpoint, and errors. |
| `CompanionTrainingEvidence` | 100% | Stores ordered training evidence, minimized replay keyframes, redacted context, and sensitivity flags. |
| `CompanionSkillDrafts` | 100% | Stores reviewed draft skills with trigger, prerequisites, steps, safety gates, evidence refs, risk, confidence, and status. |
| `CompanionSkillExecutions` | 100% | Stores future skill execution result/rating records. |
| `CompanionTrainingSettings` | 100% | Stores learning enabled, approval mode, capture profile, and replay frame/storage caps. |

## Completed Verification

- `dotnet build .\JackLLMCompanion\JackLLMCompanion.csproj`
- `dotnet build .\JackLLMCompanion\JackLLMCompanion.csproj --no-restore --nologo -v:minimal` passed with 0 warnings/errors.
- `dotnet build .\SocketJack.sln --no-restore --nologo -v:minimal` passed with 0 warnings/errors after stopping stale local PID 10516 that was locking `SocketJack.Update.Publisher.exe`.
- `dotnet build .\JackLLM\JackLLM.csproj`
- Smoke verified `/Workspace`, `/file`, `/api/workspace`, `/api/files`.
- Smoke verified screen capture is blocked by default and succeeds only after enabling Live Input.
- Smoke verified recording start/stop persists a work session.

## SecureAuthority Update Routing

| Step | Status | Notes |
|---|---:|---|
| Plan | Complete | Route update publishing through `https://socketjack.com/SecureAuthority/` using the existing SocketJack.com HTTPS certificate configuration in `SocketJack-MagicMasterList`. |
| Internal port | Complete | Added master-list config plumbing for `SecureAuthorityTargetUrl`, defaulting to private target `http://127.0.0.1:8500/`; moved `SocketJack.Update` default bind to `127.0.0.1:8500`. |
| Publisher base URL | Complete | Changed publisher default server URL and update-server public URL to `https://socketjack.com/SecureAuthority/`. |
| HTTPS proxy | Complete | Added SocketJack website routes for `GET`, `POST`, and `OPTIONS` on `/SecureAuthority/*`, stripping the prefix and forwarding bodies/headers to the private update server. |
| Old address cleanup | Complete | Replaced old update authority fallback/defaults in `SocketJack.Update`, the deploy repair script, publisher default, and public README references with the HTTPS SocketJack.com authority; `rg` sweep found no old public update authority references in active update/master/publisher/GUI files. |
| Verification | Complete | `SocketJack-MagicMasterList`, `SocketJack.Update`, and `SocketJack.Update.Publisher` builds passed with 0 warnings/errors; PowerShell repair script parses cleanly. |

