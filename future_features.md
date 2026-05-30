# Future Features

Created: 2026-05-19

This list captures useful improvements noticed while working on the proxy, Web Chat UI, JackLLM Workstation, LlmRuntime, model downloads, and agent/tool-call flows. Items are grouped by practical product value rather than by project folder.

## High-Value Near-Term Features

### 1. Workstation Health Doctor

**Problem:** Many issues show up as symptoms in different places: proxy latency, tunnel disconnects, model runtime waiting, CPU fallback, bad tool-call formatting, missing model files, or stale JackLLM processes locking builds.

**Feature:** Add a single "Health Doctor" panel in JackLLM Workstation that runs checks and gives pass/fail/action rows.

**Checks to include:**

- Master-list registration status.
- WebSocket tunnel connected, fallback TCP state, tunnel round-trip latency.
- Local web server reachable at `127.0.0.1:11436`.
- Public proxy reachable through `https://socketjack.com/proxy/{server}/`.
- LlmRuntime loaded model, backend, GPU layers, CUDA/Vulkan/DirectML state.
- Tool-call parser/provider conformance smoke.
- Image generation capability and selected image backend.
- Model/session/artifact folders writable.
- Build lock detection for running `JackLLM.exe`, `VBCSCompiler`, and updater processes.

**First implementation hook:** Add a diagnostics service in `JackLLM` that aggregates existing `/api/health`, runtime status, proxy status, model status, and filesystem checks into one `GET /api/workstation/doctor` endpoint.

### 2. Provider Conformance Tester For Tool Calls

**Problem:** LM Studio and LlmRuntime can return tool calls in different formats. When LlmRuntime emits raw XML-ish tool call text, the chat UI becomes noisy and tools do not behave like LM Studio.

**Feature:** Add a tester that runs the same small agent tasks against each provider and verifies normalized tool-call behavior.

**Test cases:**

- Write one file.
- Edit one file.
- Create folder and file.
- Read file then answer.
- Reject unsafe/destructive command.
- Stream response while tool calls are pending.
- Return final natural-language answer without raw tool transcript leakage.

**First implementation hook:** Add `LlmRuntime.Tests` cases around the provider output normalizer, then expose a WPF/Web "Run tool-call smoke" button.

### 3. Proxy Transport Timeline

**Problem:** Proxy latency has been hard to reason about without a visible breakdown. The master-side `Server-Timing` idea is strong, but it should be surfaced to the user.

**Feature:** Add a small debug timeline panel in Web Chat that shows timing for recent requests.

**Metrics to show:**

- Browser request start.
- Master auth/rewrite time.
- Tunnel wait time.
- WebSocket send time.
- Workstation local loopback time.
- First byte.
- Total time.
- Transport path: WebSocket tunnel vs TCP fallback.

**First implementation hook:** Parse `Server-Timing` headers from Web Chat fetch/WebSocket envelopes and render a collapsible diagnostics table.

### 4. Persistent Tunnel Warmup And Keepalive Strategy

**Problem:** The first proxy page load and first API calls can still feel slow if the tunnel, local runtime, or browser assets are cold.

**Feature:** Add an opt-in warmup routine when JackLLM registers a shell proxy.

**Warmup calls:**

- `/api/health`
- `/api/chat-sessions`
- `/api/server-hardware`
- Web Chat HTML/static asset preload
- currently selected runtime status

**First implementation hook:** After shell proxy registration and WebSocket tunnel ready, schedule low-cost local loopback calls and send master a "warm" state.

### 5. Model Capability Matrix

**Problem:** The UI can let users pick impossible combinations, such as image mode for a text-only model, or CUDA-only mode for a backend that silently falls back.

**Feature:** Add a capability matrix per downloaded model.

**Capabilities to track:**

- Text chat.
- Tool-use reliability.
- Agent mode.
- Image generation.
- Embeddings.
- Vision input.
- Streaming support.
- CUDA/Vulkan/DirectML/CPU support.
- Required base models/adapters.

**First implementation hook:** Extend model registry metadata with `capabilities`, then have Web Chat and WPF disable incompatible modes with a short reason.

### 6. CUDA-Only Runtime Verification

**Problem:** LlmRuntime can appear to run while CPU is doing the real work. The app needs a fail-closed GPU validation path.

**Feature:** Add a "CUDA only verification" mode that refuses generation unless CUDA is confirmed active.

**Checks to include:**

- Native CUDA backend library loaded.
- Requested GPU layers actually applied.
- Runtime reports GPU offload.
- Optional sampled GPU utilization during a short token generation.
- CPU fallback explicitly blocked when CUDA-only is selected.

**First implementation hook:** Add a backend verification result to LlmRuntime model-load responses and show it beside the selected model.

### 7. Session Conflict Resolver

**Problem:** Random "Session save failed: Session belongs to another client" messages indicate session ownership races. Hiding one message helps UX, but the real fix is lease-aware saving.

**Feature:** Add session leases and conflict recovery.

**Behavior:**

- Each browser tab gets a client id.
- Session saves include lease version.
- Server can merge append-only chat events when safe.
- UI shows "session updated in another tab" only when user action is required.
- Benign stale saves are silently ignored.

**First implementation hook:** Move chat persistence toward append-only events with monotonically increasing versions.

### 8. Build And Restart Orchestrator

**Problem:** Live JackLLM processes lock build outputs. We have repeatedly had to stop the app, build, then restart.

**Feature:** Add a repo script that safely closes JackLLM, builds selected projects, restarts Workstation, and records what happened.

**Script modes:**

- `workstation`
- `masterlist`
- `publisher-masterlist`
- `webchat-only`
- `runtime-only`

**First implementation hook:** Add `tools/Restart-JackLLM.ps1` with lock detection, process stop, `dotnet build`, restart, and final PID/status output.

### 9. Publisher Deployment Receipt

**Problem:** When master-list changes happen, it is easy to forget whether the publisher was run in the right mode.

**Feature:** After a publisher run, write a deployment receipt.

**Receipt fields:**

- mode, especially `masterlist`
- commit/hash or local file timestamp summary
- build result
- publish target
- started/completed time
- relevant process ids
- public smoke URL

**First implementation hook:** Extend `SocketJack.Update.Publisher` to emit `artifacts/publisher-receipts/*.json`.

### 10. Session Artifact Gallery

**Problem:** Generated files and images are now closer to the chat session, but browsing outputs can become messy as sessions grow.

**Feature:** Add a session artifact gallery with filters and previews.

**Views:**

- Images.
- Code files.
- Archives.
- Logs.
- Renames/deletions timeline.
- Download/open actions.

**First implementation hook:** Add an `/api/session-artifacts` endpoint that indexes the current session folder and returns typed artifact metadata.

## Web UI Improvements

### 11. Layout Profiles

Save and switch named UI layouts such as "Agent coding", "Browser expanded", "Model testing", and "Minimal chat". This would build on the existing remembered panel sizes and show/hide state.

### 12. Request/Response Inspector

Add a developer-only inspector for WebSocket API calls, HTTP fallback calls, tool calls, and file events. It should redact secrets and support copying a compact bug report.

### 13. Inline Image Quality Feedback

For image generation, show the actual generated image inline with metadata: model, backend, seed, steps, resolution, session path, and whether generation used GPU. Add quick retry buttons for "more cat-like", "higher resolution", and "same seed".

### 14. Web Chat Offline Mode

Cache the last working session list, selected session, UI layout, and static assets so the page can load enough UI to show "Workstation unreachable" instead of blank or endless loading.

## Runtime And Model Features

### 15. Model Install Recipes

Let users install a named "recipe" that can include base model, adapter, tokenizer/config files, preferred backend settings, and post-download validation. This would make multi-file downloads feel intentional rather than just a queue of files.

### 16. Per-Model Benchmark History

Store benchmark runs over time and show trend lines for tokens/sec, first token latency, VRAM estimate, CPU/GPU utilization, and backend. This would make CUDA regressions obvious.

### 17. Model Compatibility Warnings Before Load

Before loading, estimate required VRAM/RAM/context and warn if current settings are likely to fallback, thrash, or fail. Include one-click "safe CUDA", "max speed", and "low memory" presets.

### 18. Runtime Output Normalization Layer

Create one strict normalized internal representation for assistant messages, reasoning, tool calls, tool results, files, images, and final answers. All providers would be adapted into that shape before reaching Web Chat.

## Proxy And Transport Features

### 19. Tunnel Replay Harness

Record a short anonymized set of proxied request envelopes and replay them locally through the WebSocket tunnel manager. Useful for reproducing latency, streaming, cancellation, and fallback bugs.

### 20. Live Transport Switch

Expose a read-only indicator plus advanced toggle for:

- Browser API WebSocket.
- Browser HTTP fallback.
- Master-to-workstation WebSocket tunnel.
- TCP reverse-agent fallback.

The UI should make it immediately clear which path is currently carrying traffic.

### 21. Streaming Quality Monitor

Measure inter-chunk gaps for chat/model streams. Show when chunks are buffered, delayed, or blocked behind other requests.

## Engineering Cleanup Worth Doing

### A. Consolidate Progress Documents

`roadmap.md`, `progress.md`, `Suggestions.md`, and feature-specific docs overlap. A single generated status index would reduce drift and make it easier to know what is truly complete.

### B. Add End-To-End Smoke Suites

Several features are best validated only by running the whole stack. Add scripts for:

- Local Web Chat startup.
- Public proxy load.
- WebSocket tunnel traffic.
- LlmRuntime agent file-write task.
- Image generation with inline artifact.
- WPF model browser download queue.

### C. Treat "Complete" As Tested In The Real UI

Some docs mark things complete when the project builds, but the user-facing flow still needs live validation. Future progress docs should distinguish:

- compiled
- unit tested
- API smoke tested
- browser tested
- WPF UI tested
- public proxy tested

### D. Improve Build Lock Handling

The app should either build into versioned output folders or the restart script should own process shutdown before building. This would remove recurring DLL copy failures.

### E. Centralize Provider Formatting Rules

The UI should not have to keep patching around raw provider quirks. Provider adapters should normalize messages before they reach chat rendering, history persistence, or tool execution.

## Suggested Next Three

1. **Workstation Health Doctor** because it would speed up almost every future debugging loop.
2. **Provider Conformance Tester For Tool Calls** because LlmRuntime agent reliability is currently the biggest parity gap with LM Studio.
3. **Build And Restart Orchestrator** because it removes a repetitive source of failed builds and stale testing.
