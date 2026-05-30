# JackLLM Session Management Refresh Progress

Last updated: 2026-05-11

Overall Progress: 96%

Progress: `[################################################--] 96%`

## Current State

The Web Chat sessions and comments surfaces started as a side-panel refresh and are now a broader session-management installment. The work covers save, lock/unlock, clone, rename, regex search with highlighting/help, prompt-token progress, modern session-item actions, and support surfaces across the JackLLM master/server APIs, JackLLM Web UI, JackLLM WPF UI, SockJackDml, LlmRuntime, and LM Studio-facing metadata where applicable.

## Feature Set Installment Plan

| Area | Weight | Percent | Weighted Result | Status |
| --- | ---: | ---: | ---: | --- |
| Repo audit and cross-surface plan | 12% | 100% | 12% | Complete |
| Progress tracking | 4% | 100% | 4% | Complete |
| Backend session actions and model | 22% | 95% | 21% | Complete pending live API smoke |
| Web Chat session drawer and item UX | 18% | 85% | 15% | Complete pending browser polish pass |
| Regex search, highlighting, and helper UI | 14% | 100% | 14% | Complete |
| Prompt-token progress accounting | 12% | 95% | 11% | Complete pending live runtime sample |
| JackLLM WPF session controls | 10% | 90% | 9% | Complete pending full referenced build |
| SockJackDml, LlmRuntime, and LM Studio compatibility | 4% | 100% | 4% | Complete |
| Build, browser, and WPF verification | 4% | 85% | 3% | Targeted builds complete |

## Planned Deliverables

| Task | Status | Notes |
| --- | --- | --- |
| Add rename API | Complete | `POST /api/chat-session-rename` validates ownership, persists a renamed title, and title-locks manual names. |
| Preserve manual titles | Complete | `TitleLocked` prevents later autosaves/background completions from replacing a user-provided session name. |
| Add lock/unlock API | Complete | `POST /api/chat-session-action` supports `lock` and `unlock`; locked sessions reject content updates, deletes, and public message appends. |
| Add clone API | Complete | `POST /api/chat-session-action` supports `clone`, copies messages/files/runtime metadata, clears public share state, and records `ClonedFromSessionId`. |
| Add explicit save action | Complete | `save`/`checkpoint` action stamps `SavedUtc`; Web and WPF expose a Save control. |
| Add regex search API/UI | Complete | Web and WPF support literal or regex search over title/model/owner/state/token/session metadata, with invalid-regex feedback. |
| Add regex helper | Complete | Web Chat has an expandable regex helper with quick insert recipes and a concise read-more note. |
| Add prompt-token progress | Complete | Sessions now store and display total prompt tokens and budget; active prompts expose token counts too. |
| Add edge toggle buttons | Complete | Sessions and Comments get fixed edge buttons with icon-only collapsed state, hover-expanded button labels, and click-to-open drawers. |
| Modernize Sessions drawer | Complete | Desktop Sessions is a left-bound animated drawer with updated header/tools/list/item styling. |
| Modernize Comments drawer | Complete | Comments uses the same drawer pattern with refreshed comment rows/forms/share controls. |
| Fix clipping | Complete | Session rows now use auto height/min-height instead of mismatched fixed rows, fixing the final-card clipping artifact. |
| Add inline rename UI | Complete | Per-session rename opens an inline editor and saves through the Master server API. |
| Add WPF controls | Complete | JackLLM WPF Sessions tab now shows stored sessions, regex search, save/lock/clone/rename buttons, and prompt-token progress. |
| Wire compatibility surfaces | Complete | `jackllm.chat-session.compat.v1` metadata now flows through Master ping payloads, SockJackDml capabilities/tool results, chat session and active prompt APIs, LlmRuntime tool-call payloads/responses, and LM Studio/LlmRuntime system hints. |
| Verify | Complete for targeted checks | `dotnet build SocketJack.csproj`, `dotnet build ..\LlmRuntime\LlmRuntime.csproj`, embedded Web Chat JS syntax check, and focused `JackLLM` build pass. Full referenced JackLLM build previously timed out before completion. |

## Progress Log

| Date | Percent | Update | Next Step |
| --- | ---: | --- | --- |
| 2026-05-11 | 15% | Audited `html/JackLLMWebChat.html` and `SocketJack.LlmCore/Proxy/JackLLM.cs`; confirmed Sessions/Comments are currently grid panels and chat sessions already persist through `JackLLM` APIs. | Implement rename API and title persistence. |
| 2026-05-11 | 12% | Expanded scope to save/lock/clone/rename, regex search, token progress, WPF controls, and SockJackDml/LlmRuntime/LM Studio compatibility. | Audit sibling JackLLM, master-list server, SockJackDml, and LlmRuntime session surfaces. |
| 2026-05-11 | 55% | Added durable session metadata columns, rename/title-lock preservation, lock/unlock/clone/save action API, and prompt-token accounting in `JackLLM`. | Wire the Web Chat session controls to the new API. |
| 2026-05-11 | 76% | Added Web Chat session action rail, regex search/highlighting/helper, accurate prompt-token progress bars, and locked-session save safeguards. | Add matching WPF controls. |
| 2026-05-11 | 88% | Added WPF stored-session search/actions/progress and verified SocketJack build, embedded Web JS syntax, and focused WPF build. | Run live browser/API smoke and investigate full referenced WPF build timeout if needed. |
| 2026-05-11 | 96% | Finished Wire compatibility surfaces with shared session-management metadata across Master ping, SockJackDml, LlmRuntime, LM Studio-facing prompts, active prompt diagnostics, and tool-call payloads. | Optional live API/browser smoke against a running local server. |

---

# SockJackDml SocketJack.com Progress

Last updated: 2026-05-11

Overall Progress: 100%

Progress: `[##################################################] 100%`

## Current State

SockJackDml is implemented on the SocketJack.com web surface. The site now has `/sockjackdml` mission-control routes, backend APIs in `JackLLM`, durable SockJackDml storage, chat header navigation, a service catalog entry, and a release-ready progress model aligned to `plan.md`.

## Feature Progress

| Feature | Phase | Percent | Status | Next Step | Dependencies | Notes |
| --- | --- | ---: | --- | --- | --- | --- |
| SocketJack.com SockJackDml Website Shell | Phase 5 | 100% | Complete | Monitor feedback from live use | `html/SockJackDml.html`, `JackLLM` routes | `/sockjackdml/*` routes load the mission-control app. |
| SockJackDml Mission Control | Phase 5 | 100% | Complete | Add richer timeline filters if needed | Chat session owner identity and storage | Missions, progress phase, events, and decisions are implemented. |
| SockJackDml Mission Packs | Phase 5 | 100% | Complete | Add user-authored custom packs later | Mission Control | Built-in incident, launch, remote rescue, audit lockbox, and accessibility packs are available. |
| Live AI Operator | Phase 5 | 100% | Complete | Connect proposals to deeper model-generated action plans later | Agent permission context | Observation/action/risk/confidence proposals become pending timeline decisions. |
| Zero-Trust Remote Assist | Phase 5 | 100% | Complete | Bridge approved sessions into deeper remote input workflows later | Mission Control and existing LLM client APIs | Consent scope, redaction policy, expiry, approval, revoke, and audit hash are implemented. |
| Capability Router | Phase 5 | 100% | Complete | Add live latency benchmarks when peer runtime telemetry expands | Permissions, model runtime, server profile, peer selection | `/api/sockjackdml/capabilities` scores all SockJackDml pillars. |
| Evidence Vault | Phase 5 | 100% | Complete | Add signed exports if external trust roots are added | Mission Control and storage | Packets include hash, classification, related events, and JSON export manifest. |
| Realtime Accessibility Layer | Phase 5 | 100% | Complete | Replace fallback processing with model-backed caption/OCR/translation when desired | Mission Control | Caption, summary, OCR, and translation artifacts are saved and linked to missions. |
| JackCast AI Media Baseline | Phase 5 | 100% | Shipped/documented | Keep as separate baseline | JackCast.Live/wShare | Baseline remains separate from this SocketJack.com implementation. |

## Progress Log

| Date | Percent | Update | Next Step |
| --- | ---: | --- | --- |
| 2026-05-11 | 10% | Created SocketJack.com-specific roadmap in `plan.md`. | Define service data contracts. |
| 2026-05-11 | 25% | Added `Net/SockJackDmlService.cs` contracts and durable storage skeleton. | Wire backend APIs. |
| 2026-05-11 | 45% | Registered SockJackDml routes and JSON handlers in `SocketJack.LlmCore/Proxy/JackLLM.cs`. | Build SocketJack.com UI. |
| 2026-05-11 | 65% | Added `html/SockJackDml.html` and linked it from the main chat header. | Add approval/privacy/export polish. |
| 2026-05-11 | 85% | Implemented decision approvals, assist revoke, evidence export, capability scoring, and accessibility event linkage. | Verify build and docs. |
| 2026-05-11 | 100% | Added service catalog integration, project content copy, docs, and build verification target. | Release or run live smoke testing. |

## Verification Plan

| Check | Status |
| --- | --- |
| `plan.md` exists at SocketJack repo root | Complete |
| `progress.md` tracks SockJackDml feature percentages | Complete |
| SockJackDml routes registered on SocketJack.com chat server | Complete |
| SockJackDml UI included in project output resources | Complete |
| Each feature in `plan.md` has a matching progress row | Complete |
| Build verification | Complete |

---

# Process Control And Start Browser Progress

Last updated: 2026-05-11

Overall Progress: 100%

Progress: `[##################################################] 100%`

## Current State

The companion running-processes feature now includes explicit process control. The service, WPF tab, and `/Workspace` web UI can browse for launch targets, start a chosen process with optional arguments, and kill listed processes through guarded actions.

## Feature Set Installment Plan

| Area | Weight | Percent | Weighted Result | Status |
| --- | ---: | ---: | ---: | --- |
| Service start/kill actions | 20% | 100% | 20% | Complete |
| Built-in filesystem browser | 18% | 100% | 18% | Complete |
| Companion HTTP mutation APIs | 18% | 100% | 18% | Complete |
| Web process-control UI | 16% | 100% | 16% | Complete |
| WPF process-control UI | 16% | 100% | 16% | Complete |
| Build repair and verification | 12% | 100% | 12% | Complete |

## Completed

| Task | Status | Notes |
| --- | --- | --- |
| Add process kill support | Complete | `CompanionProcessService.KillProcess` kills by PID/tree while refusing protected low PIDs and the companion's own process. |
| Add process start support | Complete | `CompanionProcessService.StartProcess` validates the target path and working directory, launches via shell execution, and returns a status/result payload. |
| Add custom file browser | Complete | `BrowseFileSystem` returns bounded drive, folder, and file entries for WPF/web start-process pickers. |
| Add HTTP routes | Complete | Added `GET /api/companion/process-browser`, `POST /api/companion/processes/start`, and `POST /api/companion/processes/kill`. |
| Add web controls | Complete | `/Workspace` Processes has hover-only Kill buttons per row plus start path, arguments, and embedded drive/folder/file browsing. |
| Add WPF controls | Complete | WPF Processes rows expose Kill only on row hover and include start path, arguments, and embedded browser controls. |
| Repair build blockers | Complete | Replaced stale `runtimeStatus.Reachable` usage with `Connected` and linked `SockJackDmlService.cs` into the WPF project. |
| Verify | Complete | JackLLMCompanion and JackLLM builds pass; API smoke returned process/window/browser JSON, started a hidden short-lived PowerShell process, and rejected protected PID kill with HTTP 400. |

## Progress Log

| Date | Percent | Update |
| --- | ---: | --- |
| 2026-05-11 | 35% | Added guarded service-level start/kill methods and process-browser DTOs. |
| 2026-05-11 | 60% | Added browser/start/kill HTTP routes and refreshed process snapshots after mutations. |
| 2026-05-11 | 80% | Added `/Workspace` hover-only Kill controls and built-in start-process browser. |
| 2026-05-11 | 92% | Added WPF hover-only Kill controls, start inputs, and embedded file browser. |
| 2026-05-11 | 100% | JackLLMCompanion/JackLLM builds and live API smoke checks passed. |

---

# Running Processes Tool Service Progress

Last updated: 2026-05-11

Overall Progress: 100%

Progress: `[##################################################] 100%`

## Current State

The running-processes and windows tool service installment is complete in the sibling `JackLLMCompanion` WPF app. It now has a dedicated process/window inventory service, WPF Processes tab, local HTTP APIs, `/Workspace` Processes view, JACK runner tools, and a follow-up guarded start/kill control surface.

## Feature Set Installment Plan

| Area | Weight | Percent | Weighted Result | Status |
| --- | ---: | ---: | ---: | --- |
| Repo audit and integration design | 10% | 100% | 10% | Complete |
| Backend process snapshot service | 18% | 100% | 18% | Complete |
| Window enumeration and process/window joining | 12% | 100% | 12% | Complete |
| Metrics enrichment: CPU, RAM, GPU, totals | 18% | 100% | 18% | Complete |
| Admin/elevation and executable-path detection | 10% | 100% | 10% | Complete |
| HTTP/API and agent tool integration | 14% | 100% | 14% | Complete |
| WPF process viewer UI | 14% | 100% | 14% | Complete |
| Verification, safety review, and docs | 4% | 100% | 4% | Complete |

## Planned Deliverables

| Task | Status | Notes |
| --- | --- | --- |
| Add `CompanionProcessService.cs` | Complete | New Windows-aware service in `../JackLLMCompanion` owns process/window snapshots, CPU sampling, GPU counter probing, RAM totals, path lookup, and elevation checks. |
| Define process DTOs | Complete | Includes PID, process name, windows, executable path, CPU percent, GPU percent/availability, RAM percent, RAM GB, total RAM GB, admin/elevated state, access-denied flags, and sample timestamp. |
| Enumerate all processes | Complete | Uses `System.Diagnostics.Process.GetProcesses()` with guarded per-process reads so protected/system processes do not break the snapshot. |
| Enumerate visible top-level windows | Complete | Uses Win32 `EnumWindows`, `GetWindowThreadProcessId`, `IsWindowVisible`, title/class reads, and joins windows to process snapshots by PID. |
| Compute CPU percent | Complete | Maintains a short-lived process CPU sample cache by PID and normalizes by processor count. |
| Compute RAM values | Complete | Uses process working set plus total physical memory from `GlobalMemoryStatusEx`, deriving RAM percent and GB values. |
| Compute GPU percent | Complete | Uses best-effort reflected GPU Engine performance counters when available; reports unavailable metadata instead of fake values when counters are missing. |
| Detect admin/elevated processes | Complete | Uses guarded process-token elevation checks and reports `admin`, `notAdmin`, or `unknown`. |
| Add open-file-location support | Complete | WPF process rows open Explorer with `/select,` for available executable paths. |
| Add HTTP endpoints | Complete | Added `GET /api/companion/processes` and `GET /api/companion/windows` with query, windowed-only, take/limit, include-system, and sort options. |
| Add agent tools | Complete | Added bounded `list_running_processes` and `list_open_windows` runner tools. |
| Add workspace surface | Complete | Added a `/Workspace` Processes tab backed by the local process API. |
| Add WPF tab/view | Complete | Added sortable/filterable process table with refresh/auto-refresh, windowed-only toggle, admin/resource columns, and Open File Location. |
| Add progress updates while building | Complete | Updated this file and the companion root `progress.md` with milestone logs. |
| Verify build | Complete | `JackLLMCompanion` and `JackLLM` builds pass; hidden companion API smoke passed on `http://127.0.0.1:8091`. |

## Integration Notes

| Integration Point | Current Finding | Plan |
| --- | --- | --- |
| Service folder | The active companion service lives in sibling project `../JackLLMCompanion`. | Added `CompanionProcessService.cs` there so SDK-style project inclusion picks it up automatically. |
| Existing tool services | `TerminalService` and `GitService` remain JackLLM services; companion runner tools are local to `CompanionLlmRunner`. | Added read-only companion runner tools; process mutation is limited to explicit local start/kill service and UI controls. |
| Process metrics today | Existing JackLLM code samples only the current process for compute metering. | Companion now has all-process sampling with a dedicated cache. |
| WPF ownership | The active companion WPF app is in sibling project `../JackLLMCompanion`, with broader JackLLM integration in `../JackLLM`. | Implemented companion process/window inventory in the sibling WPF project and verified both JackLLMCompanion and JackLLM builds. |
| GPU usage | Per-process GPU usage can be unavailable depending on Windows counter/provider support and permissions. | Implemented `gpuPercent`, `gpuAvailable`, and `gpuUnavailableReason` reporting rather than guessing. |

## Safety And UX Requirements

| Requirement | Plan |
| --- | --- |
| No fragile all-or-nothing snapshots | Implemented: every process read is best-effort and independently guarded. |
| Clear privilege state | Implemented: reports `admin`, `notAdmin`, and `unknown/access denied`. |
| No accidental process mutation | Updated: inventory remains safe by default; start/kill are explicit guarded controls with protected/self PID refusal and no suspend/priority mutation. |
| WPF usability | Implemented: sortable table, text filter, windowed-only toggle, auto-refresh, and stable observable row source. |
| Agent usability | Implemented: bounded tool output with `take`, `query`, and `windowedOnly` parameters. |

## Progress Log

| Date | Percent | Update |
| --- | ---: | --- |
| 2026-05-11 | 10% | Completed repo audit and installment plan. Found no existing dedicated process/window service; identified `JackLLMCompanion/DesktopAutomationService.cs`, `JackLLMCompanion/MainWindow.xaml`, `JackLLMCompanion/CompanionHttpHost.cs`, plus SocketJack service/tool patterns as integration points. |
| 2026-05-11 | 25% | Added service model, DTOs, and all-process enumeration in `JackLLMCompanion/CompanionProcessService.cs`. |
| 2026-05-11 | 40% | Added visible top-level window enumeration and PID joining. |
| 2026-05-11 | 60% | Added CPU, RAM, GPU availability/percent, executable-path, and admin/elevation metadata. |
| 2026-05-11 | 75% | Added companion process/window HTTP APIs and `/Workspace` Processes view. |
| 2026-05-11 | 90% | Added WPF Processes tab with filter, auto-refresh, windowed-only view, resource columns, and Open File Location. |
| 2026-05-11 | 96% | Added JACK runner tools `list_running_processes` and `list_open_windows`. |
| 2026-05-11 | 100% | `dotnet build` passed for `JackLLMCompanion` and `JackLLM`; hidden companion API smoke returned process/window rows from `http://127.0.0.1:8091`. |

---

# Previous Completed Installment: Bot Filtering And Endpoint Security

Last updated: 2026-05-11

Overall Progress: 100%

Progress: `[##################################################] 100%`

| Area | Weight | Percent | Weighted Result | Status |
| --- | ---: | ---: | ---: | --- |
| Endpoint security monitor | 20% | 100% | 20% | Complete |
| NetworkOptions defaults/configuration | 15% | 100% | 15% | Complete |
| HttpServer request/response enforcement | 15% | 100% | 15% | Complete |
| SocketJack/WebSocket protocol enforcement | 10% | 100% | 10% | Complete |
| Filesystem path/security integration | 10% | 100% | 10% | Complete |
| HTTP access logging | 20% | 100% | 20% | Complete |
| Build/verification | 10% | 100% | 10% | Complete |

## Completed

| Task | Status | Notes |
| --- | --- | --- |
| Add shared endpoint security monitor | Complete | Added `Net/EndpointSecurity.cs` with per-IP score, throttle delay, disabled-until state, event-frequency multiplier, decay, snapshots, manual disable/enable, HTTP classification, protocol events, and filesystem events. |
| Enable moderate defaults through NetworkOptions | Complete | Added `NetworkOptions.EndpointSecurity` enabled by default with moderate request-rate, throttle, decay, and temporary-disable settings. |
| Exempt localhost by default | Complete | Loopback clients are not throttled/disabled unless configuration opts out, matching the remote-IP requirement. |
| Add HttpServer monitor surface | Complete | Added `HttpServer.EndpointSecurity` plus helper methods for HTTP decisions, protocol events, filesystem events, throttle delays, and blocked responses. |
| Wire HttpServer request gate | Complete | Parsed HTTP requests are evaluated before body/route handling, delayed when score warrants it, blocked with retry metadata when disabled, and recorded after response status is known. |
| Wire SocketJack networking | Complete | Protocol routing, unknown protocol probes, SocketJack frames, WebSocket handshakes, and WebSocket frames feed the same per-IP throttle/disable monitor. |
| Wire filesystem security | Complete | Static file resolution now normalizes mapped roots, rejects path escapes with a root-boundary check, and records traversal/.htaccess-style probes against the same monitor. |
| Verify build | Complete | `dotnet build SocketJack.csproj` completed with 0 warnings and 0 errors. |
| Fix normal-client slowdown | Complete | Benign traffic no longer accumulates delay just for being active; throttling starts after suspicious events, rate-limit violations, or sustained bursts past the grace threshold. |
| Add daily HTTP access logs | Complete | Added default-on JSONL-style daily HTTP access logs under `C:\JackLLM\Logs`, using `yyyy-MM-dd.log` filenames. |
| Add tuning profiles | Complete | Added `TuningProfile` and `BotTuningProfile` settings with `Loose`, `Firm`, and `Strict` strengths plus convenience methods. |
| Repair build blocker | Complete | Restored the missing server-location lookup DTO/cache entry that was already referenced by `SocketJack.LlmCore/Proxy/JackLLM.cs`, allowing the project build to verify this feature. |



# Endpoint Security Default Tuning Progress

Last updated: 2026-05-13

Overall Progress: 100%

| Task | Status | Notes |
| --- | --- | --- |
| Reopen default tuning | Complete | Default `Firm`/moderate profile was still aggressive enough to ban normal app usage after a few requests. |
| Soften default profile | Complete | Raised default rate/disable thresholds, reduced throttle growth, increased decay, disabled user-agent heuristics by default, and added a minimum scored-event count before auto-disable. |
| Verify build | Complete | `dotnet build SocketJack.csproj` completed with 0 warnings and 0 errors. |

---
