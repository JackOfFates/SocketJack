# RuntimeLLM Chat UI Feature Test Report

Date: 2026-05-17  
Target: `http://127.0.0.1:11436/`  
Process: `JackLLM.exe` from `C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM\bin\Debug\net8.0-windows7.0\JackLLM.exe`  
Browser: Codex in-app browser, portrait viewport during UI pass

## Summary

RuntimeLLM / JackLLM Chat loads and most non-destructive API-backed surfaces respond. The main shell, heartbeat, auth/account, hardware, usage, model lists, session list, solution explorer data, marketplace server list, Copilot duplicator status, payouts, and chat service discovery all returned successfully.

The highest-impact UI issue is the portrait drawer overlay: the transparent portrait backdrop sits above the actual drawer panels in hit testing, so the user sees panels but the backdrop can intercept clicks. This explains the observed behavior where panels appear to be behind the transparent background.

The highest-impact runtime issues are chat/agent/image mode reliability: plain chat saved a long unrelated answer despite a tiny `max_tokens` cap and exact-output prompt; agent mode saved an empty assistant message; image generation failed inside the bundled Python Diffusers/Torch runtime.

## Fix Progress Update

Updated: 2026-05-17 15:30:20 -05:00

- Portrait drawer stacking is fixed in `html/JackLLMWebChat.html` and verified against the rebuilt JackLLM server. Controls, Sessions, Files, and Share all opened above the backdrop; `elementFromPoint()` inside each drawer returned the drawer or a child, not `#portraitDrawerBackdrop`.
- Chat mode dock switching is fixed and verified from browser clicks. Chat, Agent, Companion, media/image generation, and Chat-return clicks all updated `serviceSelect.value`, body mode class, and pressed button state correctly.
- Free-session sign-in warning is fixed and verified. It now sits above the composer, spans the center content width, and does not overlap the prompt, Send button, messages, or workspace.
- Live tool-call visibility has been implemented in code and is pending browser/runtime verification. `/api/chat-stream` now emits safe `toolCall` events for agent proxy tools and media generation, and the Web Chat UI renders them both inline on assistant messages and in a session activity panel.
- Session file display has been updated in code and is pending browser verification. Created file changes stay distinct, new session files are marked with a green `+`, and long paths now preserve the filename side by truncating the leading path.
- Browser verification loaded a saved `Codex live tool UI verification` session through the real Web Chat renderer. It showed the global Live activity panel, inline assistant tool rows, completed and failed statuses, expandable safe details, green `+` created-file badge, and filename-preserving leading path truncation with no console errors.
- Live agent/model verification remains blocked by current agent behavior: two Agent prompts did not emit tool calls. The first answered from filesystem context without calling a tool, and the second stalled at `Receiving response... 79%` until stopped. This is tracked under the existing Agent stream reliability issue.
- Rebuilt and restarted JackLLM from `C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM`; latest build passed with 0 errors and 2 nullable-context warnings from existing project warning areas.
- Mode switching reversion is fixed in `html/JackLLMWebChat.html` and verified on 2026-05-17 15:17:44 -05:00 after closing JackLLM, rebuilding, restarting, and reloading the in-app browser. Agent stayed selected after 20 seconds, Companion stayed selected after 7 seconds, Chat stayed selected after 7 seconds, and no console errors were reported.
- Agent filesystem access is now restricted to current session files, remote session clones, and explicitly accessible directories. Implicit solution-root access was removed, drive/share roots such as `C:\` are rejected for accessible-directory approval, root-level permission prompts are suppressed, and slash-root paths like `\file.txt` are treated as session-relative instead of resolving to `C:\file.txt`.
- Rebuilt and restarted JackLLM after the filesystem boundary fix. Build passed with 0 errors and the same 2 nullable-context warnings. Direct SockJackDml/VS-tool smoke verified `\codex-root-boundary-smoke-*.txt` writes into the current session file store, while explicit `C:\codex-root-boundary-abs-*.txt` is blocked and no `C:\` file is created.
- Video model smoke/download guidance now targets `cerspense/zeroscope_v2_576w` instead of broad popularity picks or LTX-2-family bases. Hugging Face reports 7.9 GB VRAM for 30 frames at 576x320, and the LlmRuntime repository scan reports a 7.89 GiB complete-model bundle, which fits the 12 GB TITAN X test target with headroom.
- Rebuilt and restarted JackLLM after the video-model pin. Build passed with 0 warnings and 0 errors; `/health` returned `ok: true`. Focused scanner test `IdealModelScanner_GroupsHubSuggestionsByCategory` passed.
- Direct shell-started video download was stopped by closing JackLLM because LlmRuntime currently has no download-cancel endpoint. The partial bundle remains resumable under `CompleteModels\cerspense\zeroscope_v2_576w\main`.
- Rewired the JackLLM-hosted `/Workstation` browser control page away from Companion. It now uses JackLLM-native WPF remote-control endpoints at `/api/workstation/state`, `/api/workstation/screen.json`, `/api/workstation/input`, `/api/workstation/permissions`, and `/api/workstation/emergency-stop`; those endpoints are loopback-only and use the Workstation app's registered `SocketJack.WPF` provider.
- The earlier Companion `/Workspace` flicker reduction remains as copied source material, but `/Workstation` no longer depends on a Companion process, port `8091`, or any `/api/workstation/companion/*` proxy route.
- Web Chat model/mode gating is fixed in code. Agent mode is disabled for models that do not advertise tool support, media mode only exposes the selected model's supported image/video/audio generation types, and stale unsupported selections fall back to Chat before sending.

## Tested Feature Matrix

| Feature Area | Result | Evidence / Notes |
| --- | --- | --- |
| Page shell | Pass | `/` returned `200`, title `JackLLM Chat`, page mounted in the in-app browser. |
| Host heartbeat | Pass | `/health` returned `ok: true`; UI showed heartbeat live for `TitanX - Vin`. |
| Auth/session | Pass | `/api/web-auth/session` returned authenticated localhost user state. |
| Account/costs | Pass | `/api/account` and `/api/costs` returned `ok: true`. |
| Hardware panel | Pass | `/api/server-hardware` returned CPU/RAM/GPU/VRAM/network/I/O data; UI text populated. |
| Usage/token meter | Pass | Usage panel populated with free/no-token-meter text, token count, and cost rows. |
| Runtime services | Pass | `/api/chat-services` returned Chat, Agent, Companion, Image, Audio, Video, and service entries. |
| Model list | Pass | `/api/models` and `/api/model-runtime/models` returned loaded text and image models. |
| Runtime compatibility | Warning | `/api/model-runtime/compatibility` says CUDA/PyTorch compatible, but image generation later fails importing Diffusers/Torch. |
| Composer | Pass | Prompt, Send, Retry, IMG, FILE, MIC, and TTS controls are present and enabled where expected. |
| Free-session sign-in warning | Pass/Fixed | Warning is compact, full-width to the center content, positioned above the composer, and verified not to overlap messages, prompt, or Send. |
| Live tool activity UI | Pass/Implemented | Saved-session verification rendered global and inline tool rows, completed/failed states, safe expandable previews, source counts, and no console errors. |
| Live model tool-call smoke | Blocked | Agent prompts did not emit tool calls; one answered without tools and one stalled until stopped. Backend stream support is implemented, but live model behavior still needs the Agent stream reliability fix. |
| Created-file plus/path truncation | Pass/Implemented | Saved-session verification showed green `+`, `created` metadata, and leading path truncation while keeping `new-tool-call-proof.txt` visible. |
| Portrait drawers | Pass/Fixed | Controls/Sessions/Files/Share open above the transparent backdrop after the stacking fix; hit-testing lands inside each drawer. |
| Chat mode strip | Pass/Fixed | Chat, Agent, Companion, and media/image mode buttons now update `serviceSelect`, body class, and pressed state from browser clicks. |
| Mode switch persistence | Pass/Fixed | User-selected Chat, Agent, and Companion modes no longer snap back after model/service refresh; verified after stopped-app rebuild and restart. |
| Model capability mode gating | Pass/Implemented | Agent is now model-tool gated, and image/video/audio modes are selected and enabled only when the selected model advertises that media generation capability. |
| Agent filesystem boundary | Pass/Verified | Backend file/tool roots now exclude implicit solution roots and reject drive/share root access. Rebuilt smoke verified slash-root paths land in current session files and explicit `C:\` writes are blocked with no root file created. |
| Workstation browser control | In Progress | `/Workstation` was changed to JackLLM-native WPF remote-control endpoints instead of Companion proxy endpoints. Build/browser verification is next. |
| Plain chat stream | Fail | Smoke prompt `Reply with exactly OK` saved a long unrelated analysis; `max_tokens: 8` did not constrain output. |
| Agent stream | Fail | Agent smoke session saved an empty assistant message; earlier stream emitted usage/progress without assistant content. |
| Image generation stream | Fail | Image prompt failed with bundled Python Diffusers/Torch import error. |
| Sessions | Pass | `/api/chat-sessions` returned saved sessions; UI session list mounted with search, regex, seed, zip controls. |
| Active stream list | Pass/Warning | `/api/chat-active-sessions` reports running streams; stop endpoint cancelled a stuck image stream. Needs stronger disconnect cleanup. |
| Solution explorer | Pass/Blocked UI | `/api/chat-solution-explorer` returned roots/session files; drawer access is affected by backdrop layering. |
| Project workflow | Pass | `/api/developer-project-workflow` returned project workspace metadata. |
| Share/comments | Pass/Gated | UI mounted; comments disabled until public/auth state allows them. |
| Permissions/access panel | Expected gated | `/api/chat-permissions` returned `403` for current user state; access panel is hidden/disabled. |
| SQL admin | Expected gated | `/api/chat-sql-admin/status` returned `ok: true`, `sqlAdminEnabled: false`. |
| Remote admin / LLM client | Expected gated | `/api/llm-client/status` returned `403`; remote admin control hidden/disabled. |
| Terminal approvals | Expected gated | `/api/terminal-approvals` returned `403`; terminal approval panel hidden. |
| Trust / observability dashboards | Expected gated | `/api/trust-abuse` and `/api/observability` returned `403`. |
| Marketplace / Copilot duplicator | Pass | `/api/jackllm/servers` and `/api/copilot-duplicator` returned `ok: true`. |
| Payouts | Pass | `/api/payouts?...` returned payout policy and summary data. |
| Token purchase products | Fail/Incomplete | `/api/socketjack/token-products` returned `404`; token purchase UI exists but is hidden/disabled in current state. |

## Known Issues

### 1. Portrait Drawer Backdrop Intercepts Drawer Panels

Severity: High  
Area: Portrait layout, Sessions/Files/Controls/Share drawers
Status: Fixed and verified

Observed:

- Opening the Sessions drawer sets:
  - `body` classes: `portrait-layout portrait-drawer-open drawer-sessions-open`
  - `#portraitDrawerBackdrop`: `z-index: 9000`, `opacity: 1`, `pointer-events: auto`
  - `#sessionSidebar`: `z-index: 9100`, `opacity: 1`, `pointer-events: auto`
- Despite the panel's higher computed z-index, `document.elementFromPoint(80, midViewport)` returned `#portraitDrawerBackdrop`, not `#sessionSidebar`.
- Root cause is likely the `.workspace { position: relative; z-index: 2; }` ancestor stacking context trapping drawer children below the backdrop, which is a direct child of `.shell` at `z-index: 9000`.

Impact:

- Drawers look open but clicks hit the transparent backdrop.
- This blocks Sessions, Files, Share, and Controls interactions in portrait layout.
- It can also make mode switching and panel testing appear broken.

### 2. Chat Mode Buttons Do Not Switch Mode In Tested Portrait State

Severity: High  
Area: Chat/Agent/Companion/Media mode strip
Status: Fixed and verified

Observed:

- Mode buttons are mounted and enabled.
- Clicking Agent and media buttons left:
  - `serviceSelect` value: `""` (Chat)
  - Chat icon `aria-pressed="true"`
  - Agent/media `aria-pressed="false"`

Impact:

- User cannot reliably enter Agent or image/video/audio mode from the visible mode dock.
- This blocks the primary workflow being tested.

Likely related:

- The portrait backdrop/panel stacking bug may interfere with hit testing.
- Also verify `setServiceSelection()` and `isChatModeServiceSelectable()` behavior independently after fixing the overlay stack.

Follow-up fix:

- Added manual service-selection tracking so model/runtime refreshes respect the user's chosen mode for the current model.
- Automatic service changes still occur when the selected model changes or permissions make the chosen mode unavailable.
- Verified Agent, Companion, and Chat do not revert after delayed refresh windows.

### 3. Free-Session Sign-In Warning Overlaps Chat Content

Severity: Medium  
Area: Composer, center chat panel  
Status: Fixed and verified

Observed:

- The free-session sign-in warning was previously inside the prompt/composer area and could cover composer controls.
- After widening it to the center content, the strip sat above the composer but still overlapped the lower message/workspace area.

Impact:

- Users could lose access to visible controls or have the lower chat content hidden by the warning strip.

Fix:

- Moved the warning to an absolute strip above the composer while reserving vertical room with `.composer.has-premium-hint`.
- Verified current bounds: warning is above the composer, matches the center content width, and does not overlap messages, workspace, prompt, or Send.

### 3.1. Agent File Tools Could Escape To Implicit Roots

Severity: High  
Area: Agent filesystem tools, session file safety  
Status: Fixed and verified

Observed:

- File read/write/rename/delete/search/list tools used a shared resolver that included implicit active solution roots.
- Rooted-without-drive paths such as `\name.txt` could be normalized by Windows as a drive-root path like `C:\name.txt`.
- Accessible directory approval did not reject drive/share roots such as `C:\`.

Impact:

- Agent file tools could try to save or read outside the files that are visibly accessible for the current session.
- A mistaken root path could create confusing permission prompts or resolve to a risky location.

Fix:

- Removed implicit solution roots from Agent tool authorization.
- Limited Agent filesystem roots to current session files, remote session clones, and explicitly accessible directories.
- Rejected drive/share roots when adding or approving accessible directories.
- Treated leading-slash local paths as session-relative paths instead of drive-root paths.
- Default SockJackDml progress files now land in current session files.
- Verified after rebuilding: `\codex-root-boundary-smoke-*.txt` wrote under `JackLLMChat\SessionFiles\codex-boundary-smoke`, and `C:\codex-root-boundary-abs-*.txt` was blocked with no file created in `C:\`.

### 4. Plain Chat Ignores Exact Prompt And Tiny Max Token Cap

Severity: High  
Area: `/api/chat-stream`, text generation

Observed:

- Prompt: `CHAT ROUTE SMOKE TEST. Reply with exactly OK.`
- Request used `max_tokens: 8`.
- Saved assistant response began: `Here is a detailed analysis of the provided text...`
- Response was far longer than the cap and unrelated to the prompt.

Impact:

- Smoke tests cannot trust prompt isolation or token limits.
- Users can get stale/unrelated model behavior from new short prompts.

### 5. Agent Mode Saves Empty Assistant Message

Severity: High  
Area: Agent mode stream

Observed:

- Prompt: `AGENT ROUTE SMOKE TEST. Do not edit files. Reply with exactly AGENT_OK.`
- Saved session has two messages, but assistant content is empty.
- Earlier stream behavior emitted `usage` and `progress` events without assistant content before timing out.

Impact:

- Agent mode can appear to complete while producing no answer.
- Empty assistant messages pollute sessions and hide the actual failure state.

### 6. Image Generation Fails In Bundled Python Runtime

Severity: High  
Area: Image generation, LlmRuntime / JackONNX Python integration

Observed:

- Runtime compatibility endpoint reports CUDA-enabled PyTorch is compatible.
- Image generation failed with:
  - `No compatible Python image generation runtime completed the request`
  - bundled `Python\python.exe exited with code 2`
  - Diffusers/Torch import chain failure ending around `cannot import name 'is_offline_mode'`

Impact:

- Image mode is advertised and selectable, but cannot complete generation.
- Compatibility check gives a false green signal.

### 7. Image Stream Can Keep Running After Client-Side Timeout

Severity: Medium  
Area: Streaming lifecycle / cancellation

Observed:

- After a client/tool timeout, `/api/chat-active-sessions` showed image generation still running after more than a minute.
- Manual POST to `/api/chat-stream/stop` returned `{"ok":true,"cancelled":true}` and cleared active sessions.

Impact:

- Client disconnects/timeouts can leave GPU/Python work running.
- Users may unknowingly burn compute after closing or losing a page.

### 8. Token Products Endpoint Missing

Severity: Medium  
Area: Token purchase UI

Observed:

- `/api/socketjack/token-products` returned `404 Not Found`.
- Token purchase UI controls are present in markup but hidden/disabled in the current state.

Impact:

- If the UI exposes token purchase, the product grid cannot load.
- The route should either exist or the UI should remain explicitly unavailable with a clear reason.

### 9. Typo In Stream Action Button

Severity: Low  
Area: Composer streaming controls

Observed:

- Button text is `Interupt / Next`.

Expected:

- `Interrupt / Next`.

### 10. Tool Calls And Service Accesses Were Not Visible Live

Severity: High  
Area: Agent mode, media generation, `/api/chat-stream`, Web Chat UI  
Status: Implemented, pending runtime verification

Observed:

- Agent and service work was represented only as progress text, final text, or hidden search/tool result content.
- Internet searches and other service accesses were not shown as live activity rows like Codex.

Impact:

- Users could not see what the agent was accessing while it was happening.
- Internet search, runtime tools, terminal tools, VS/file tools, SockJackDml, Git, downloads, and media generation lacked a clear audit trail in the chat session.

Fix:

- Added a `toolCall` stream event with `started`, `completed`, and `failed` statuses.
- Added safe fields for label, summary, kind, timing, redacted arguments, result previews, source counts, and errors.
- Added inline assistant activity and a mirrored session-level Live activity panel.

### 11. Tool Call Details Needed Redaction And Size Limits

Severity: Medium  
Area: Tool-call transparency / privacy  
Status: Implemented, pending runtime verification

Observed:

- Showing full raw tool payloads would risk leaking tokens, cookies, credentials, or overly large result bodies.

Fix:

- Arguments are parsed as JSON and sensitive field names are replaced with `[redacted]`.
- Result previews are redacted and capped before being streamed to the browser.
- Full raw search/tool output stays out of normal assistant text.

### 12. New Session Files Were Not Visually Distinguished

Severity: Medium  
Area: Session files, assistant file changes, solution explorer  
Status: Implemented, pending browser verification

Observed:

- Created files were normalized into `modified`, so the UI could not show a new-file marker.
- Newly discovered session files looked the same as existing files.

Fix:

- `created`, `create`, `added`, `add`, and `new` now stay as `created`.
- Assistant file changes and recently discovered session files show a green `+`.
- Session file refresh detects files that were not previously in the session list.

### 13. Long Session File Paths Hid The Filename

Severity: Medium  
Area: Session files, solution explorer, assistant file changes  
Status: Implemented, pending browser verification

Observed:

- Path/file labels used ordinary end ellipsis, which can hide the filename and leave only the beginning of a path visible.

Fix:

- File labels now render parent path first and filename on the right.
- The parent path truncates from the leading side, keeping the filename visible.

### 14. Video Smoke Model Was Too Large For The Test GPU

Severity: High  
Area: Video generation model download/test planning  
Status: Fixed in model suggestions, execution still blocked by video pipeline scaffold

Observed:

- The LTX-2 recursive base model required by the downloaded adapter chain scanned as about 314 GB of source files.
- Current free disk was about 147.77 GiB, and the model is not appropriate for a 12 GB TITAN X smoke test.
- Broad Hugging Face popularity searches can surface modern video stacks whose file size or runtime memory is too large for the current GPU.

Fix:

- Pinned the ideal video-model suggestion to `cerspense/zeroscope_v2_576w`.
- Verified the model card's VRAM note reports 7.9 GB at 30 frames and 576x320.
- Verified LlmRuntime's repository scan reports one `download_pytorch_bundle` candidate, task `video`, size 7.89 GiB, downloadable.
- Kept video generation execution tracked separately because `JackOnnxVideoPipeline` still reports that frame generation and encoding are not implemented.

### 15. Workstation Control Needed One Browser Window

Severity: Medium  
Area: JackLLM Workstation control, MCP visibility, remote desktop UI  
Status: Implemented, pending browser verification

Observed:

- Codex could read JackLLM Workstation state through MCP, but mutation/control still had to happen through separate HTTP calls, shell commands, or the WPF window.
- The first `/Workstation` draft proxied Companion `/Workspace` APIs, which required an extra process and port `8091`.

Fix:

- Added `/Workstation` to JackLLM Web Chat server.
- Replaced the Companion proxy routes with JackLLM-native `/api/workstation/*` routes backed by the existing `SocketJack.WPF` remote-control provider.
- Restricted the native Workstation control endpoints to loopback clients.
- Added a browser UI with a stable remote frame, fake pointer overlay with `pointer-events: none`, MCP-style live activity log, and UI buttons to scan/start the 12 GB-friendly video model download.
- Kept the copied Companion frame-stability idea, but `/Workstation` no longer calls Companion or depends on a Companion process.

## Fix Plan And Progress

| Issue | Progress | Bar | Plan |
| --- | ---: | --- | --- |
| Portrait drawer backdrop intercepts panels | 100% | `[####################]` | Fixed by allowing portrait `.workspace` to use `z-index: auto`, so fixed drawer panels stack against the shell backdrop instead of being trapped in the workspace stacking context. Verified Controls, Sessions, Files, and Share hit-testing. |
| Mode buttons do not switch mode | 100% | `[####################]` | Fixed hidden dock hit-testing and focus retention: hidden dock is click-through, visible/focused dock accepts clicks, and prompt focus keeps the strip stable. Verified Chat, Agent, Companion, media/image, and Chat-return switching. |
| Mode selection reverts after switching | 100% | `[####################]` | Fixed by treating user mode clicks as manual selections for the current model, so model/runtime refreshes cannot auto-select a suggested service over the user's choice. Verified Agent, Companion, and Chat hold after waits. |
| Unsupported modes remain selectable | 100% | `[####################]` | Agent mode is disabled unless the selected model advertises tools. Media services are disabled per selected-model image/video/audio generation capability, and send-time guards fall back to Chat with a clear error if a stale unsupported mode is reached. |
| Free-session sign-in warning overlap | 100% | `[####################]` | Fixed warning placement by reserving a margin slot above the composer and lifting the strip within it. Verified no overlap with messages, workspace, prompt, or Send, and width matches the center content. |
| Plain chat ignores prompt/max tokens | 5% | `[#...................]` | Reproduced with saved session evidence. Next: trace `max_tokens` from UI payload through `/api/chat-stream` into LlmRuntime/LM Studio request, verify context reset/session isolation, and add a deterministic smoke model/test route. |
| Agent mode empty assistant message | 5% | `[#...................]` | Reproduced with saved session evidence. Next: ensure agent stream emits a terminal error/status when no answer is produced, avoid saving empty assistant messages, and add timeout/finalization tests. |
| Image generation Python runtime failure | 10% | `[##..................]` | Error captured. Next: repair bundled Python package versions, pin compatible `diffusers`/`transformers`/Torch dependencies, and make compatibility endpoint import the actual pipeline modules used at generation time. |
| Image stream cleanup after timeout | 20% | `[####................]` | Stop endpoint works. Next: bind generation lifetime to HTTP client disconnect/cancellation token and add server-side max runtime cleanup for orphaned streams. |
| Token products endpoint missing | 0% | `[....................]` | Verify intended product source. Implement `/api/socketjack/token-products` or hide/disable token purchase with a clear capability reason. |
| Stream action typo | 0% | `[....................]` | Rename button text from `Interupt / Next` to `Interrupt / Next`; add a quick DOM text assertion. |
| Live tool-call stream events | 90% | `[##################..]` | Implemented `toolCall` events for agent proxy tools and media generation with safe redacted previews. Build passed. Remaining: live model/tool-call smoke after Agent stream reliability is fixed. |
| Inline and global tool activity UI | 100% | `[####################]` | Verified through a saved Web Chat session: global Live activity and inline assistant rows render completed/failed tool calls, safe expandable previews, source counts, and persisted history with no console errors. |
| New-file green plus indicators | 95% | `[###################.]` | Verified assistant file-change green `+` for a stored `created` file. Remaining: verify a real Agent-created session file once Agent tool-call emission is reliable. |
| Path-left filename-right truncation | 95% | `[###################.]` | Verified long path display keeps `new-tool-call-proof.txt` visible while truncating the leading parent path. Remaining: portrait-width spot check after live-agent file creation. |
| 12 GB-friendly video smoke model | 100% | `[####################]` | Pinned video ideal-model guidance to `cerspense/zeroscope_v2_576w`; verified Hugging Face VRAM note and LlmRuntime scan size. Remaining video execution work stays under the video pipeline implementation issue. |
| Unified Workstation control page | 90% | `[##################..]` | Implemented `/Workstation` with JackLLM-native loopback `/api/workstation/*` endpoints, fake cursor overlay, activity feed, UI-driven model scan/download, and stable-frame remote rendering. Remaining: rebuild/restart and in-browser verification. |

## Recommended Fix Order

1. Fix portrait drawer stacking first because it blocks direct use of many UI features.
2. Re-test mode switching after the drawer/backdrop fix.
3. Fix stream lifecycle cleanup so failed tests do not leave background GPU work running.
4. Fix text chat and agent stream correctness.
5. Repair image generation Python runtime and strengthen compatibility checks.
6. Fill in token purchase route or hide it cleanly.
7. Sweep small text/UI polish issues.

## Retest Checklist

| Retest | Target |
| --- | --- |
| Drawer hit testing | For Controls, Sessions, Files, Share: `elementFromPoint()` inside the drawer must return the drawer or a child, never `#portraitDrawerBackdrop`. |
| Mode switching | Click Chat, Agent, Companion, media; verify `serviceSelect.value`, body mode class, and pressed button state. |
| Free-session sign-in warning | Verify strip is visible above the composer, spans the center content, and does not overlap messages or controls. |
| Chat stream | Prompt `Reply exactly OK`; verify short output and `max_tokens` respected. |
| Agent stream | Prompt no-op agent smoke; verify non-empty assistant response or explicit error. |
| Image generation | Generate a small safe image prompt; verify completion artifact or clear preflight error before starting. |
| Stream cancellation | Abort client mid-generation; verify `/api/chat-active-sessions` clears automatically. |
| Token products | Open token purchase UI; verify products load or UI explains unavailable state. |
| Live tool calls | In Agent mode, trigger internet search and verify a running tool row appears inline and in Live activity before the final answer. |
| Failed tool row | Trigger a blocked/failing tool and verify a failed row with a redacted error summary. |
| Tool history persistence | Reload the session and verify saved assistant messages still show their tool activity rows. |
| New session file badge | Create a new file through Agent/file tools and verify a green `+` appears beside the file change and session/solution file row. |
| Long path truncation | Use a deeply nested file path and verify the path begins with leading truncation while the filename remains visible. |
| Video smoke model | Open Hugging Face video model suggestions and verify `cerspense/zeroscope_v2_576w` is first; scan it and verify a downloadable 7.89 GiB video bundle. |
| Workstation page | Open `/Workstation`, verify remote frame updates without DOM flicker, fake cursor does not intercept clicks, activity rows appear, and the video download can be started from the UI button. |
