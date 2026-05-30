# SocketJack Features

Last updated: 2026-05-11

This file summarizes the feature surface described by the repository progress trackers:

- `progress.md`
- `SocketJack/progress.md`
- `SocketJack/SocketJack_SockJackDmlPlanProgressExecutionTools_Progress.md`
- `JackONNX/Progress.md`

## Core SocketJack Networking

| Feature area | Status | Highlights |
| --- | --- | --- |
| Transport core | Available | Unified TCP, UDP, HTTP, WebSocket, protocol multiplexing, callbacks, connection lifecycle events, peer metadata, and direct object messaging. |
| Mutable protocol hosting | Available | `MutableTcpServer` hosts HTTP, SocketJack, WebSocket, RTMP, TDS/SQL, FTP configuration, and custom protocol handlers on one listener. |
| HTTP application hosting | Available | Route mapping, typed request bodies, static files, directory hosting, uploads, stream routes, redirects, caching headers, `.htaccess` controls, and default index routing. |
| Browser WebSockets | Available | RFC 6455 WebSocket client and server support sharing SocketJack serialization, compression, callbacks, and peer flows. |
| Peer routing | Available | Peer discovery, host/client roles, relay-assisted routing, metadata propagation, broadcasts, and identifier-based sends. |
| Compression and throughput | Available | GZip/Deflate compression, configurable buffers, async I/O, automatic segmentation, outbound chunking, and bandwidth throttling. |
| Security controls | Available | TLS through `SslStream`, X.509 auth, HTTP Basic auth, bearer tokens, host-local checks, allow/deny rules, and endpoint security monitoring. |
| Embedded data | Available | JSON-backed data server, SQL admin panel, TDS protocol handler, cache optimization, snapshots, and generated context helpers. |
| File transfer | Available | FTP/SFTP helpers, upload/download routes, directory hashing, remote session file clone tracking, and filesystem allowlists. |
| Streaming media | Available | `BroadcastServer` accepts RTMP or HTTP ingest and relays FLV data to browser or VLC viewers. |
| WPF remoting | Available | Live WPF control capture with JPEG streaming plus remote mouse, wheel, text, and keyboard input. |

## SocketJack.com And SockJackDml

| Feature area | Status | Highlights |
| --- | --- | --- |
| SockJackDml website shell | Complete | `/sockjackdml/*` routes load the mission-control app from `html/SockJackDml.html`. |
| Mission control | Complete | Missions, phases, progress events, timeline decisions, and durable owner/session storage. |
| DML mission packs | Complete | Built-in incident, launch, remote rescue, audit lockbox, and accessibility packs. |
| Live AI operator | Complete | Observation, action, risk, and confidence proposals become pending mission timeline decisions. |
| Zero-trust remote assist | Complete | Consent scope, redaction policy, expiry, approve/revoke flow, and audit hash tracking. |
| Capability router | Complete | `/api/sockjackdml/capabilities` scores mission-control, operator, assist, runtime, evidence, and accessibility readiness. |
| Evidence vault | Complete | Evidence packets include hash, classification, related events, and JSON export manifests. |
| Realtime accessibility layer | Complete | Caption, summary, OCR, and translation artifacts can be saved and linked to missions. |
| SockJackDml workflow tools | Complete | `sockjackdml_plan_create`, `sockjackdml_progress_document_create`, and `sockjackdml_plan_execute` are available through JackLLM Agent tools and local API routes. |
| Approval-gated execution | Complete | Plan execution supports preview, approved apply, and auto approval-gated modes routed through existing file, Git, and terminal permission gates. |
| LlmRuntime tool mirror | Complete | SockJackDml tool definitions are mirrored for embedded LlmRuntime export through `/api/v1/tools/openai`. |

## JackLLM Chat And Session Management

| Feature area | Status | Highlights |
| --- | --- | --- |
| Browser chat UI | Available | SocketJack-hosted chat server, model endpoint wiring, chat sessions, files, comments, sharing, SQL admin, account page, and Magic navigation. |
| Session rename | In progress | `POST /api/chat-session-rename` validates ownership and persists renamed titles. |
| Manual title preservation | In progress | A `TitleLocked` flag preserves user-provided session names across autosave cycles. |
| Session drawers | In progress | Sessions and comments are being refreshed into edge buttons, left-bound drawers, inline rename, and modernized card layouts. |
| Save, lock, and clone | Planned | Explicit save, lock/unlock, and clone actions are planned for session management. |
| Regex search | Planned | Literal or regex search over session title, model, owner, date, message, file, and comment metadata is planned with highlighting and invalid-regex feedback. |
| Prompt-token progress | Planned | Session prompt-token accounting and progress bars are planned. |
| WPF session controls | Planned | Matching session actions, search, and progress controls are planned for JackLLM WPF. |
| SockJackDml and LlmRuntime compatibility | Planned | Session metadata compatibility will be surfaced through SockJackDml, LlmRuntime, and LM Studio-facing metadata where applicable. |

## JackLLM Companion

| Feature area | Status | Highlights |
| --- | --- | --- |
| Companion app shell | Complete | Sibling WPF project, tray/background startup, open workspace/files commands, launch/status flow, and solution integration. |
| Companion HTTP server | Complete | Local `HttpServer` starts on port `80` with fallback to `8091` and hosts `/Workspace`, `/file`, JSON APIs, and remote-control surfaces. |
| Database persistence | Complete | Companion projects, sessions, files, people, apps, permissions, templates, skills, audit events, training runs, evidence, drafts, executions, and settings persist through SocketJack `DataServer` tables. |
| Permission gates | Complete | Live input, files, training, and mutating actions are approval-gated and audited in WPF and web surfaces. |
| Session recording | Complete | Start/stop/manual events persist, foreground telemetry records while active, and inferred starter skill output is produced on stop. |
| Remote desktop | Complete | WPF and web capture/live view, click center, click preview, Escape, type text, mouse/wheel/key input, and safety gates. |
| Remote desktop streaming | Complete | Polling, chunked NDJSON, and WebSocket frame streams with cursor metadata and adaptive PNG/JPEG quality. |
| File sharing | Complete | Upload/download/copy, drag/drop, folder upload/share, local path registration, session indexing, Use Files gate, and per-file approvals. |
| LLM control | Complete | Goal/mode task queue, background runner, model endpoint selection, stop controls, emergency stop, and progress tracking. |
| Runner tools | Complete | Structured desktop actions, JACK/session/action memory, malformed output repair, gated action execution, and bounded process/window tools. |
| Self-training | Complete | Training evidence capture, minimized replay keyframes, sensitivity tagging, redaction, draft skill generation, review, activation, and skill ranking for runner prompts. |
| Process inventory | Complete | Process/window snapshots with PID, names, executable paths, visible windows, CPU, RAM, GPU availability, elevation state, and guarded access-denied metadata. |
| Process control | Complete | Guarded process start/kill actions, protected/self PID refusal, start target validation, WPF controls, web controls, and companion HTTP mutation APIs. |

## JackONNX And Local Runtime Work

| Feature area | Status | Highlights |
| --- | --- | --- |
| JackONNX project family | Complete | Projects, contracts, local manifest catalog, providers, jobs, artifacts, progress streaming, LlmRuntime bridge, SocketJack bridge, CLI, docs, samples, and tests are in place. |
| ONNX Runtime layer | Complete | CPU session factory, catalog shell, job lookup/cancel/update, artifact store, progress history, provider checks, clean build, and CPU ONNX smoke test. |
| DirectML provider | Complete | DirectML package reference, session options factory, Windows baseline probe, fail-closed compatibility checks, and clean build. |
| CUDA provider | Complete | CUDA package reference, session options factory, `nvidia-smi` probing, fail-closed compatibility checks, and clean build. |
| Image/audio/video foundations | Complete | Request/job scaffolds and LlmRuntime/SocketJack invocation paths are in place; model-specific execution is deferred until local model layouts are selected. |
| LlmRuntime connection | Complete | Built-in tool bridge for devices, models, jobs, cancellation, and scaffolded generation. |
| SocketJack bridge | Complete | Route constants, route mapper, status/devices/models/jobs/artifact handlers, scaffolded generation handlers, artifact serving, and SSE progress streaming. |
| ONNX model browser and conversion | Complete | ONNX-ready browsing, direct ONNX registration, source tensor conversion jobs, WPF status updates, HTTP status/cancel endpoints, and intentionally limited GGUF-to-ONNX handling. |

## Security, Operations, And Updates

| Feature area | Status | Highlights |
| --- | --- | --- |
| Endpoint security monitor | Complete | Per-IP scoring, throttle delay, disabled-until state, event-frequency multipliers, decay, snapshots, manual disable/enable, and HTTP classification. |
| Network defaults | Complete | `NetworkOptions.EndpointSecurity` defaults to a moderate profile and exempts localhost unless configured otherwise. |
| HTTP enforcement | Complete | Request gates evaluate parsed HTTP requests before body/route handling and record results after response status is known. |
| Protocol enforcement | Complete | Protocol routing, unknown protocol probes, SocketJack frames, WebSocket handshakes, and WebSocket frames feed the shared monitor. |
| Filesystem hardening | Complete | Static file resolution normalizes mapped roots, rejects path escapes, and records traversal or `.htaccess`-style probes. |
| HTTP access logs | Complete | Default-on daily JSONL-style access logs are written under `C:\JackLLM\Logs`. |
| Tuning profiles | Complete | Loose, Firm, and Strict bot tuning profiles are available through profile settings and convenience methods. |
| SecureAuthority update routing | Complete | Update publishing routes through `https://socketjack.com/SecureAuthority/`, proxied to the private update service with old public authority references removed. |

## Verification Signals From Progress Trackers

- `SocketJack`, `JackLLM`, `JackLLMCompanion`, `LlmRuntime`, and update projects have passing build records in the progress logs.
- `LlmRuntime.Tests` passed 57/57 in the SockJackDml workflow-tool verification pass.
- `JackONNX.Tests` passed 7 tests with 2 native-provider tests self-skipped when matching runtimes were unavailable.
- Companion process and window APIs were smoke-tested on `http://127.0.0.1:8091`.
- Process control smoke tests started a hidden short-lived PowerShell process and verified protected PID kill rejection with HTTP 400.
- SockJackDml route, project resource copy, service catalog, and progress tracking checks are marked complete.

## Deferred Or Active Work

- Complete the JackLLM session-management refresh: save, lock/unlock, clone, regex search, token progress, WPF controls, and metadata compatibility.
- Select local model layouts for JackONNX image, audio, and video execution before implementing model-specific ONNX graph orchestration.
- Run a live Web Chat Agent-mode smoke test for SockJackDml workflow tools after launching JackLLM.



