# SocketJack Companion Web UI Plan

Last updated: 2026-05-10

## Goal

Turn `/Workspace` from a status dashboard into the Companion command center: the place where the user can queue LLM desktop work, watch or control the PC through a remote desktop view, share files, review session memory, and hit an emergency stop that immediately disables live input.

## Navigation

The web UI is organized as tabs so each major control surface has a permanent home.

| Tab | Purpose | V1 status | Next work |
|---|---|---:|---|
| Plan | Shows the implementation roadmap, active projects, and approval gates. | Implemented | Optional database-backed progress sync. |
| LLM Control | Queue goals for JACK and show whether desktop control is approved. | Implemented with runner loop, tool schema, action memory, and output repair. | Optional replay visualizer and richer task analytics. |
| Remote Desktop | Live frame polling/streaming, click-to-control, drag, keyboard/text input, cursor echo, adaptive capture, and emergency stop. | Implemented with polling, chunked NDJSON, and WebSocket transport. | Optional frame diffing, WebP, multi-monitor, and zoom/pan polish. |
| File Sharing | Upload files/folders into the Companion share, register local paths, download shared files, and index session files. | Implemented with drag/drop, folder support, and per-file approval. | Optional file-open actions, previews, and quarantine hooks. |
| Memory | Sessions, events, JACK template, learned skills, and people memory. | Implemented | Optional evidence drill-down, confidence editing, and skill promotion. |
| Audit | Recent approval, control, file, LLM, and emergency events. | Implemented | Optional filtering, export, and event replay links. |

## Remote Desktop Design

The remote desktop behaves like a lightweight local RDP panel, not a screenshot button.

1. Frame source
   - Current: `GET /api/companion/screen.json` returns a PNG or JPEG frame as base64.
   - Current: `GET /api/companion/desktop/stream` streams newline-delimited JSON frame objects over chunked HTTP.
   - Current: `GET /api/companion/desktop/ws` streams frame objects over WebSocket, with automatic fallback to chunked HTTP or polling.
   - Current: `GET /api/companion/desktop/transport` advertises available transports.
   - Current: frame objects include cursor echo metadata and support adaptive PNG/JPEG quality modes for faster updates.
   - Optional backlog: frame diffing, WebP, multi-monitor selection, and zoom/pan.

2. Input path
   - Current: `POST /api/companion/input` sends mouse click, drag, wheel, text, and key actions after Live Input approval.
   - Current: cursor echo is shown over the live frame when the capture layer can read cursor position.
   - Optional backlog: modifier key combos and clipboard paste.
   - Required safety: every live input action remains blocked unless `LiveInput` is enabled.

3. Emergency stop
   - Current: web button and WPF `Ctrl+Esc` both disable Live Input, stop queued LLM tasks, and stop active recording.
   - Optional backlog: persistent emergency-stop banner and a hardware-style confirmation key chord for resuming live input.

4. Remote desktop UX
   - Current: click or drag the frame to send normalized coordinates.
   - Current: frame age, transport, encoding, cursor position, and approved/blocked state are visible in the panel.
   - Optional backlog: multi-monitor selector, zoom/pan, click ripples, and inspect mode where the LLM can describe the screen without controlling it.

## LLM Control Design

The Companion has an actual control loop, not just manual remote-control buttons.

1. Task intake
   - Current: `POST /api/companion/llm/task` stores a goal, mode, status, and runner plan.
   - Current: `CompanionLlmRunner` watches queued tasks, observes the desktop, calls the configured model endpoint, parses or repairs an action, and executes only permitted actions.
   - Runner inputs: JACK template, current permissions, current remote frame, active app/window, recent session memory, shared files, action history, and user goal.

2. Model integration
   - Current: calls the existing OpenAI-compatible JackLLM/LM Studio endpoint so Companion can run as another JackLLM web-suite service.
   - Current: exposes a structured `companion_desktop_action` tool schema with action, coordinates, confidence, and expected observation fields.
   - Current: repairs malformed output with a retry pass and falls back to `ask_user` when the action cannot be made safe.
   - The runner executes only if the required capability is enabled; otherwise it records `approval_required`.

3. Tool loop
   - Observe screen.
   - Ask model for next step using the structured desktop-action schema.
   - Repair or retry malformed output, then fall back safely when needed.
   - Validate action against approval gates.
   - Execute one small action.
   - Capture new screen/environment.
   - Repeat until complete, blocked, or emergency-stopped.

4. Stop behavior
   - `Ctrl+Esc`, web Emergency Stop, and WPF Emergency Stop cancel queued/running LLM tasks.
   - The runner checks cancellation before every observation, model call, and input action.

## File Sharing Design

File sharing supports both human uploads and LLM-accessible work-session files.

1. Companion share
   - Current: files uploaded through `/Workspace` are stored in local app data under `SocketJack\Companion\SharedFiles`.
   - Current: WPF can copy a selected local file or folder into the same share.
   - Current: web drag/drop can upload files and folders, and `/api/share/register-path` can register a local file or folder path.
   - Current: shared files are indexed in `/api/share` and downloadable through `/api/share/download?id=...`.

2. Session file index
   - Current: all shared/uploaded files also register into the session file browser.
   - Current: shared entries store a kind/role such as uploaded file, folder upload, shared file, or shared folder file.
   - Optional backlog: thumbnails, file previews, and "give this file to JACK" toggles.

3. Safety
   - Current: upload/download/share is blocked unless Use Files is enabled.
   - Current: sensitive directories and executable/script-like files require explicit per-file or per-folder approval before entering the share.
   - Optional backlog: quarantine/scanning hooks for downloaded or model-created files.

## Database Additions

| Table | Purpose |
|---|---|
| `CompanionLlmTasks` | Queued/stopped LLM desktop tasks with goal, mode, status, runner metadata, and progress. |
| `CompanionSharedFiles` | Shared file index with id, name, copied path, relative path, kind, size, note, approval metadata, and timestamp. |

## API Surface

| Route | Purpose | Gate |
|---|---|---|
| `POST /api/companion/emergency-stop` | Disable live input, stop recording, stop queued LLM tasks. | Always available locally. |
| `POST /api/companion/llm/task` | Queue an LLM desktop-control task. | Queues without input; execution requires Live Input. |
| `POST /api/companion/llm/stop` | Stop queued/running LLM task. | Always available locally. |
| `GET /api/companion/llm/runner` | Return runner state, endpoint, busy state, and active task. | Local only. |
| `POST /api/companion/llm/runner/start` | Start/reconfigure runner. | Local only. |
| `POST /api/companion/llm/runner/stop` | Stop runner loop and current task. | Local only. |
| `GET /api/companion/desktop/transport` | Return supported remote desktop transports and URLs. | Local only. |
| `GET /api/companion/desktop/stream` | Stream live desktop frames as NDJSON. | Live Input. |
| `GET /api/companion/desktop/ws` | Stream live desktop frames over WebSocket. | Live Input. |
| `GET /api/companion/screen.json` | Return current desktop frame. | Live Input. |
| `POST /api/companion/input` | Send desktop mouse/keyboard action. | Live Input. |
| `GET /api/share` | Return shared/session file metadata. | Metadata visible locally. |
| `POST /api/share/upload` | Upload file/folder data into Companion share. | Use Files plus per-file approval when sensitive. |
| `POST /api/share/register-path` | Copy a local file or folder path into the Companion share. | Use Files plus per-file approval when sensitive. |
| `GET /api/share/download?id=...` | Download a shared file. | Use Files. |

## Acceptance Criteria

- `Ctrl+Esc` works while the Companion is hidden and disables live input immediately.
- `/Workspace` has visible LLM Control, Remote Desktop, File Sharing, Memory, Audit, and Plan tabs.
- Remote Desktop can refresh frames continuously over polling, chunked HTTP, or WebSocket, echo cursor position, and click-to-control only when Live Input is enabled.
- File upload/download/local folder share is blocked until Use Files is enabled, and sensitive files require explicit approval.
- LLM task queue records goals and the model runner can execute gated desktop actions with tool-schema parsing, repair retry, action memory, and cancellation.
- Emergency stop is audited and visible in both WPF and web surfaces.
