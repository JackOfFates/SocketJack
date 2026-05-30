# Shell Proxy Load Performance Changelog

Date: 2026-05-13

## Affected areas analyzed

- `SocketJack-MagicMasterList/Program.cs`
  - Handles public shell session routes such as `/proxy/{servername}`.
  - Resolves registered `MasterShellRelay` instances, checks relay health, rewrites proxied HTML for the `/proxy/{servername}` prefix, and forwards browser/API traffic into the reverse TCP relay.
- `SocketJack/Net/HttpServer.cs`
  - Contains generic host proxy support. It also opens upstream connections per request, but `/proxy/{servername}` shell sessions do not primarily use this path.
- `SocketJack/html/JackLLMWebChat.html`
  - Detects `/proxy/{servername}` and prefixes local `/api/*` calls so the browser routes API calls through the shell proxy.
- `SocketJack/Net/Services/TerminalService.cs`
  - Executes approved terminal commands after the proxied UI is loaded. It is not the startup bottleneck for loading `/proxy/{servername}` pages.

## Root cause

`ForwardShellProxyRequest` created and disposed a new `HttpClientHandler` and `HttpClient` for every forwarded shell proxy request.

That made a proxied shell session pay setup cost for every HTML, API, and polling request. Since `/proxy/{servername}` pages issue many small same-origin calls through the reverse relay, this prevented connection pooling and caused visibly slow page startup.

## Changes made

- Added a shared static `ShellProxyHttpClient` in `SocketJack-MagicMasterList/Program.cs`.
- Backed the shared client with `SocketsHttpHandler` configured for:
  - `AllowAutoRedirect = false`, preserving the previous proxy behavior.
  - `ConnectTimeout = 10s`, so bad local relay connects fail promptly.
  - `PooledConnectionIdleTimeout = 5m`, allowing bursty browser loads to reuse warm relay connections.
  - `PooledConnectionLifetime = 15m`, preventing stale relay sockets from living forever.
  - `MaxConnectionsPerServer = 64`, allowing concurrent page assets, API calls, and polling without serializing behind a tiny pool.
- Kept `HttpClient.Timeout = InfiniteTimeSpan` and moved non-streaming request limits into a per-request linked `CancellationTokenSource`.
- Preserved infinite streaming behavior for chat/SSE requests while keeping the existing `65s` timeout for non-streaming proxy calls.
- Updated the non-streaming body read and streaming handoff to use the same per-request cancellation token.

## Expected effect

- `/proxy/{servername}` shell pages should load faster because consecutive browser requests can reuse pooled connections to the local reverse relay listener.
- Chat streams and event streams remain long-lived.
- Non-streaming hung requests still time out after 65 seconds.
- Redirect handling, HTML rewriting, CORS, and response header copying behavior remain unchanged.

## Files changed

- `SocketJack-MagicMasterList/Program.cs`
- `SocketJack/changelog_proxy.md`

## 2026-05-13 follow-up: LM Studio/native runtime loading bar

### Affected areas analyzed

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
  - Handles chat UI streaming, native `/api/v1/chat` runtime events, model-load progress events, prompt-processing progress events, and usage metering emitted to the browser.
- `SocketJack.LlmCore/Proxy/Sessions/ChatSessionModels.cs`
  - Stores per-native-stream state used while reading LM Studio/LlmRuntime stream events.
- `SocketJack/Net/HttpServer.cs`
  - `ChunkedStream` flushes each streamed line immediately. That is useful for token deltas, but expensive when the upstream sends very frequent progress ticks.
- `SocketJack/html/JackLLMWebChat.html`
  - Renders `model_load` and `prompt_processing` progress events into the visible loading/progress bar.

### Root cause

Native runtime progress events were forwarded one-for-one to the browser. Each `model_load.progress` or `prompt_processing.progress` event performed JSON serialization, usage-meter snapshot work, prompt-session phase updates, one HTTP chunk write, and an immediate socket flush.

When LM Studio or LlmRuntime emits tiny progress deltas quickly, that creates many small writes and UI updates without making the loading bar meaningfully smoother.

### Changes made

- Added per-native-stream progress tracking to `ChatUiNativeStreamState`.
- Added `WriteNativeProgressIfDue` in `JackLLM.cs`.
- Coalesced native progress updates so repeated tiny deltas are skipped unless:
  - the phase changes,
  - the update is a forced start/end event,
  - at least 150 ms has elapsed and the progress changed by at least 0.5%, or
  - at least 150 ms has elapsed and the visible status text changed.
- Kept token/content deltas immediate; only model-load and prompt-processing progress telemetry is throttled.

### Expected effect

- LM Studio/LlmRuntime loading bars should remain responsive without flooding the proxy with tiny chunks.
- CPU cost should drop during model loading and prompt preprocessing.
- Chat text streaming remains low-latency because content deltas still flush immediately.

### Files changed

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
- `SocketJack.LlmCore/Proxy/Sessions/ChatSessionModels.cs`
- `SocketJack/changelog_proxy.md`

## 2026-05-13 follow-up: show live thoughts while tokens continue

### Root cause

The chat stream could be alive and still emitting thousands of tokens, but once only progress events were visible the UI kept emphasizing the large `Processing prompt ...%` bar. Reasoning/thought deltas were stored, but the thinking panel started collapsed and progress updates could continue to dominate the assistant message.

### Changes made

- Updated `JackLLMWebChat.html` so live reasoning opens the thinking panel while the response is still streaming.
- Once any answer or reasoning output exists, progress events no longer restore the large percentage bar.
- Progress is condensed to a compact strip with `Thinking...` or `Receiving response...` while content/thoughts are visible.
- Final responses still collapse into the normal completed assistant message behavior.

### Files changed

- `SocketJack/html/JackLLMWebChat.html`
- `SocketJack/changelog_proxy.md`

## 2026-05-13 follow-up: shell waiting page relay race

### Root cause

The public `/proxy/{servername}` route immediately returned the waiting page when `WaitingAgents == 0`. Reverse agents intentionally refresh idle master connections, so a browser navigation could arrive during a short refresh gap and get stuck looking at the waiting page even though an agent was about to reconnect.

### Changes made

- Added a short bounded grace wait before returning the shell waiting page.
- The master route now re-checks relay runtime state for up to about 3 seconds before showing the waiting page.
- If an agent reconnects during that window, the request proceeds into the normal proxy forwarding path instead of returning the static waiting page.

### Files changed

- `SocketJack-MagicMasterList/Program.cs`
- `SocketJack/changelog_proxy.md`

## 2026-05-13 follow-up: JackLLM security and bot filtering defaults

### Affected areas analyzed

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
  - Creates the JackLLM/LmVsProxy chat UI server and configures request gating, robots policy, and SocketJack endpoint security.
- `SocketJack-MagicMasterList/Program.cs`
  - Creates the MagicMasterList API, website, HTTPS, and Jackcast proxy listeners used by JackLLM project publishing and shell proxy flows.
- `SocketJack/Net/EndpointSecurity.cs`
  - Provides the shared SocketJack endpoint security and bot filtering options. The core default remains enabled for non-JackLLM consumers.

### Root cause

JackLLM-facing servers inherited SocketJack endpoint security defaults unless each listener opted out individually. The chat UI server also installed an access request gate and advertised a crawler-disallow robots policy.

That meant JackLLM projects could still pay for bot/security classification and could present blocking behavior by default even when the intended JackLLM behavior is a permissive local/project proxy.

### Changes made

- Added a JackLLM chat-server security disable path that:
  - removes the default `RequestGate`,
  - disables `EndpointSecurity.Enabled`,
  - disables known-bad-path blocking,
  - disables suspicious user-agent treatment, and
  - switches the chat UI robots policy from `Disallow: /` to `Allow: /`.
- Moved MagicMasterList listener security defaults into `CreateServer` so the API listener, website listener, HTTPS listener, and Jackcast dedicated listeners all start with endpoint security disabled.
- Removed the previous one-off HTTPS-only endpoint security disable because all MagicMasterList-created listeners now share the same JackLLM-facing default.
- Left the shared SocketJack `EndpointSecurityOptions` default unchanged so unrelated SocketJack hosts can still opt into the existing protective defaults.

### Expected effect

- JackLLM projects no longer run SocketJack bot filtering or endpoint security by default.
- Unlisted JackLLM chat servers are no longer blocked by the default request gate; listing visibility can remain a discovery concept without also acting as a transport block.
- MagicMasterList shell/proxy listeners use a consistent permissive default across HTTP, HTTPS, and Jackcast listeners.

### Files changed

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
- `SocketJack-MagicMasterList/Program.cs`
- `SocketJack/changelog_proxy.md`

## 2026-05-13 follow-up: shell relay agents for unlisted JackLLM routes

### Affected areas analyzed

- `JackLLM/MainWindow.xaml.cs`
  - Starts the JackLLM local chat server, controls the Published/Unlisted state, requests `/api/shell/proxies`, and starts `ReverseTcpRelayClient` agents.
- `SocketJack/Net/ReverseTcpDuplicator.cs`
  - Maintains the reverse TCP relay agent pool, connects desktop agents to master relay ports, and bridges public socket sessions to the local JackLLM chat server.
- `SocketJack-MagicMasterList/Program.cs`
  - Reports relay runtime counts and forwards `/proxy/{servername}` traffic only when reverse agents are available.

### Root cause

JackLLM restored the Shell publishing checkbox as checked by default, but startup only started the reverse shell relay through the Published listing path.

When a host was unlisted, or auto-publish was disabled, the master route could still exist and accept authenticated `/proxy/{servername}` navigation, but the desktop app never parked reverse agents on the route's agent port. The public page then stayed on the waiting screen with `Server not responding ...%` because MagicMasterList correctly saw `WaitingAgents = 0`.

Relay connection failures were also too easy to miss: `ReverseTcpRelayClient` used unbounded `TcpClient.ConnectAsync` calls, so a blocked or unreachable agent/local port could stall without promptly surfacing a useful reconnect message.

### Changes made

- Changed JackLLM startup so a checked Shell publishing control starts shell mode even when the listing is Unlisted.
- Changed the unlisted publish path so it starts the shell relay and keeps the route reachable while leaving website visibility off.
- Preserved Published as a discovery/listing setting instead of letting it decide whether the transport relay exists.
- Added bounded reverse-agent connection timeouts:
  - 10 seconds when connecting to the master relay agent port.
  - 5 seconds when pairing to the local JackLLM chat server.
- Added master-side pruning for disconnected queued relay sockets before reporting runtime counts or pairing sockets, so stale waiting public clients do not make relay status misleading.

### Expected effect

- Authenticated `/proxy/{servername}` URLs should work for unlisted JackLLM projects as long as Shell publishing is enabled and the local JackLLM chat server is running.
- If the desktop cannot reach the master relay port, the Shell debug log should show reconnect timeout messages instead of silently looking enabled forever.
- MagicMasterList relay counts should recover from abandoned browser/public sockets more cleanly.

### Files changed

- `JackLLM/MainWindow.xaml.cs`
- `SocketJack/Net/ReverseTcpDuplicator.cs`
- `SocketJack-MagicMasterList/Program.cs`
- `SocketJack/changelog_proxy.md`

## 2026-05-17 follow-up: relay queue backpressure

### Root cause

The master shell proxy treated `ActiveSessions > 0` as enough evidence to forward a new `/proxy/{servername}` request into the relay. A reverse relay session is one TCP tunnel, so active sessions do not provide capacity for another browser/API request. During a bursty page load or a flaky reverse-agent period, new public clients could queue behind no idle agent and wait until the shell proxy's 65 second non-streaming timeout.

Those timed-out public clients could remain in the relay queue long enough to consume future reverse agents, creating a feedback loop where current requests felt slow or intermittent even after fresh agents reconnected.

### Changes made

- Forwarding now waits briefly for an idle reverse agent and only enters the relay when `WaitingAgents > 0`.
- Active sessions still count as useful status, but they no longer imply capacity for a new proxied request.
- Shell relay health no longer uses the recent-agent grace window as "online" when public clients are already backed up.
- The reverse relay now caps queued public clients and drops older overflow sockets so stale requests cannot starve newer ones.

### Expected effect

- `/proxy/{servername}` should fail quickly with the waiting page when no reverse agent is available instead of hanging for about 65 seconds.
- A burst of abandoned browser requests should not leave dozens of stale public clients queued on the master relay.
- Server cards should be less likely to show a shell route as healthy while the relay has no idle agents and a public-client backlog.

## 2026-05-17 follow-up: reject public relay sockets without idle agents

### Root cause

Even with the queue cap, public relay sockets could remain connected while waiting for future reverse agents. When the browser emitted many parallel page/API requests, those stale public sockets consumed the next arriving reverse agents before fresh requests could use them.

### Changes made

- The master relay now rejects a public socket immediately if no reverse agent is waiting at accept time.
- Public sockets still pair normally when an idle reverse agent is already parked on the relay.

### Expected effect

- Stale browser requests should no longer occupy the relay queue while waiting for future agents.
- Fresh proxy requests should either pair with a ready agent or fail fast enough for the shell proxy waiting UI to retry cleanly.

## 2026-05-17 follow-up: validate reverse agents before pairing

### Root cause

Idle reverse agents refresh themselves periodically. If the master relay did not notice a refreshed-away socket was already closed, a new browser request could be paired with that stale agent and fail after it had already left the public waiting queue.

### Changes made

- The master relay now sends the one-byte start signal before committing a public client to an agent. Stale agents are dropped and the relay tries the next waiting agent.
- If no usable reverse agent remains, the relay closes the public client and drains any waiting public clients instead of leaving them to hang.
- Workstation reverse agents now refresh idle sockets every five minutes instead of every 25 seconds to reduce churn while a browser page is loading.

### Expected effect

- Fresh proxy requests should not be wasted on stale idle-agent sockets.
- The relay's public-client queue should stay near zero during failed pair attempts instead of turning into a slow backlog.

## 2026-05-17 follow-up: close master relay upstream tunnels after requests

### Root cause

The master proxy's upstream `HttpClient` could keep TCP connections to the relay public port alive after a proxied request completed. Those idle pooled TCP tunnels still occupied reverse-agent sessions, so the relay could report active sessions with few idle agents even when no browser page was intentionally loading.

### Changes made

- Master shell proxy HTTP requests now send `Connection: close` to the local relay upstream.
- The JSON WebSocket bridge uses the same close-after-response behavior for its internal `/api/*` fetches.

### Expected effect

- Completed HTML/API/heartbeat requests should release their reverse relay sessions promptly.
- The idle agent pool should recover faster after page loads and health probes.

## 2026-05-17 follow-up: add reverse-agent ready acknowledgement

### Root cause

A half-closed idle agent socket can sometimes accept the relay's one-byte start signal before the relay notices the client is gone. That made the signal write check insufficient: the public browser request could still be attached to an agent that would never connect to local JackLLM.

### Changes made

- After the relay sends the start signal, the workstation agent now connects to local JackLLM and writes a one-byte ready acknowledgement back to the relay.
- The relay only starts bridging the public client after receiving that acknowledgement within a short timeout.
- Agents that do not acknowledge readiness are dropped before they can consume a public request.

### Expected effect

- Stale waiting-agent sockets should no longer create browser requests that hang with zero bytes received.
- Page-load bursts should consume only agents that have already proven they can reach the local JackLLM server.
