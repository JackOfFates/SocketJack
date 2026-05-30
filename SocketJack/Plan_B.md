# Plan_B: Web Chat Assistant And Agent Reliability

## Progress

| Area | Progress | Status |
|---|---:|---|
| Baseline inspection | `[##########] 100%` | Stream, tool, filesystem, session file, and load-time paths identified. |
| Chat assistant mode | `[#########-] 90%` | Plain Chat uses the non-tool path and avoids raw `(empty response)` saves; browser recheck remains. |
| Agent file tools | `[#########-] 92%` | Agent create, rename, copy, delete, read, write, search, and download paths are bounded to accessible/session roots. |
| Filesystem boundary safety | `[########--] 85%` | Server preview/download/file tools resolve through accessible roots; targeted escape tests still need browser coverage. |
| Downloads | `[#########-] 92%` | Direct Agent download flow emits live tool rows and writes into the session Downloads area. |
| Runtime compatibility API | `[##########] 100%` | Direct `/api/v1/runtime/compatibility` aliases route correctly and return compatibility JSON. |
| Thinking panel autoscroll | `[#########-] 90%` | Thought text pins to bottom when the inner panel is at least 90% scrolled. |
| Empty response handling | `[########--] 85%` | Null/empty runtime streams fail visibly instead of silently saving `(empty response)`; one long-stream browser soak remains. |
| Web file browser opening | `[########--] 85%` | Accessible web-language files now have browser-open affordances routed through `/api/chat-file-preview`; browser UI verification remains. |
| Message copy controls | `[#########-] 90%` | Prompt, response, error, and visible assistant reasoning fallback text can be copied with the same copy button style. |
| Proxy health latency | `[#########-] 92%` | Web Chat now prefers shell-proxy WebSocket health/hardware reads; master list labels split workstation-to-SocketJack Internet latency from browser-to-SocketJack latency. |
| Load time | `[######----] 65%` | Current API timings are acceptable except runtime compatibility cold calls, which can take a few seconds. |
| End-to-end verification | `[#######---] 70%` | API and direct Agent smoke tests passed; Codex browser pane still needs a full visible UI pass with auth token. |

## Next Steps

1. Rebuild and restart JackLLM from the current workstation location so the updated Web Chat HTML is served.
2. Open the Codex app browser with the auth-token URL and verify Chat mode, Agent mode, file preview, browser-open, and copy controls.
3. Run an Agent file boundary test: accessible file opens, inaccessible path is rejected, and downloaded HTML opens only after preview access succeeds.
4. Run a long streamed Agent response to confirm the input stream no longer drops halfway through.
5. Re-measure startup and tab switch load times after correctness verification.

## Verification Notes

- Inline Web Chat JavaScript syntax check passed with `node` against the single inline script block.
- Latest local timing sample after the reliability fixes:
  - `/api/models`: ~29ms
  - `/api/model-runtime/models`: ~48ms
  - `/api/chat-services`: ~20ms
  - `/api/chat-sessions?take=20`: ~61ms
  - `/api/chat-filesystem-access`: ~31ms
  - `/api/v1/runtime/compatibility`: ~3281ms
- Direct Agent download smoke passed: the stream returned tool-call events, no stream errors, and saved `socketjack.com.html` under the session `Downloads` area.
- New UI refinement: web-language file browser opens use `/api/chat-file-preview` as the safety gate, so files outside accessible/session roots should not be opened.
- New proxy transport refinement: `/proxy/<server>/api/health` and `/proxy/<server>/api/server-hardware` now have a raw JSON WebSocket bridge in MagicMasterList. Local `/api/health` and `/api/server-hardware` returned HTTP 200 after rebuild/restart, and both Web Chat and master-list inline scripts passed `node --check`.
