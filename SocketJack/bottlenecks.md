# SocketJack proxy latency bottlenecks

Date: 2026-05-18

Scope: `https://socketjack.com/proxy/TitanX/` to local JackLLM Workstation through the master-list reverse relay.

## Summary

The local JackLLM service was not the main latency source. The slow path was the public proxy relay. Before the WebSocket tunnel work, normal warmed API calls through the proxy were around 1.4-2.8 seconds, and repeated probes could spike into 5-14 seconds with intermittent 503s when the waiting reverse-agent pool drained or contained stale agents.

The biggest bottleneck is architectural: each small HTTP request consumes one preconnected reverse relay agent, signals it, waits for a ready ack, opens a fresh local TCP connection, bridges the request, closes it, and then waits for another agent to reconnect. Because the master also disables upstream keep-alive with `ConnectionClose = true`, even tiny health/session calls pay the full tunnel setup cost.

Update: the master-to-workstation path now has a preferred persistent WebSocket tunnel. The reverse TCP relay remains as fallback, but warmed proxy API calls now avoid the no-agent wait path when the tunnel is live.

## Measurements

Recent proxy checks against `TitanX` showed this pattern:

| Target | Result |
| --- | --- |
| Local `http://127.0.0.1:11436/api/health` | Verified 200 via `Invoke-WebRequest` and verbose `curl`; no local handler delay was visible. One earlier scripted `curl` timing reported `status=000` after ~2s, but that measurement was invalid. |
| Proxy `/api/health` cold/depleted pass | 503 in 3.351s, 503 in 6.067s, 503 in 13.894s, then 200 in 13.999s, 9.859s, 5.170s, 4.284s, 2.539s. |
| Proxy `/api/hardware` warmed pass | 200 in roughly 1.69s, 2.11s, 1.76s, 2.07s, 1.71s. |
| Proxy `/api/chat-sessions` warmed pass | 200 in roughly 1.405s, 2.804s, 1.806s, 2.182s, 1.823s. |
| Relay pool after the slow pass | `waitingAgents=39`, `waitingPublicClients=0`, `activeSessions=0`. |
| Earlier warmed browser heartbeat after direct HTTP change | Connected with heartbeat samples around 1061-2821ms. |
| WebSocket tunnel `/api/health` after fix | HTTP 200 with `Server-Timing: sj-tunnel-wait;dur=89, sj-ws-send;dur=1, sj-local;dur=2, sj-first-byte;dur=93, sj-total;dur=93`; five warmed curl samples completed in roughly 0.35-0.39s. |
| WebSocket tunnel `/api/chat-sessions` after fix | HTTP 200 in roughly 0.41s with tunnel timing. |
| Master tunnel state after final restart | `webSocketTunnelConnected=true`, `webSocketTunnelCount=1`, `waitingAgents=0`, `waitingPublicClients=0`. |

Those numbers point away from local JackLLM request handling and toward relay pairing, tunnel setup, agent availability, and public network overhead.

## Ranked bottlenecks

1. Per-request reverse-agent consumption

   `SocketJack/Net/ReverseTcpDuplicator.cs` pairs one public request with one waiting agent. On signal, the agent opens a new local connection to JackLLM, writes a ready byte, bridges traffic, then exits that relay session. This makes small API calls expensive and makes page startup fan-out drain the agent pool quickly.

2. Keep-alive is disabled at the master proxy

   `SocketJack-MagicMasterList/Program.cs` sets `upstreamRequest.Headers.ConnectionClose = true` when forwarding to the relay public port. This avoids leaking idle relay tunnels, but it also prevents the master from reusing a warm upstream connection. The result is a full relay/session setup for every CSS, HTML, health, and JSON call.

3. Master-side no-agent wait adds visible seconds

   `ShellProxyAgentCapacityWait` is 3 seconds. When `relay.WaitingAgents == 0`, `ShellProxy_OnHttpRequest` waits in 250ms increments before returning a waiting/503 response. Under repeated probes, this creates the 3s, 6s, and 13s behavior as callers retry into a depleted pool.

4. Stale-agent protection has a large worst-case

   The relay now protects against dead idle agents by requiring a ready ack, but `AgentReadyHandshakeTimeoutMs` is 6000ms. If a stale agent is selected, the public request can sit for up to six seconds before the relay discards that agent and tries to recover.

5. Fresh local TCP connect on every relay hit

   Each signaled agent creates a new `TcpClient` to `127.0.0.1:11436`. The local service is healthy, but the repeated connect/bridge/close cycle is still a fixed cost on every public API request.

6. Startup still fans out into many relay sessions

   The loading page and app boot sequence request several pieces of state: health, hardware, sessions, config, and page assets. Even after making heartbeat single-flight and less eager, each request still consumes a reverse agent. Sequential boot phases amplify the perceived load time.

7. Full response buffering and HTML rewrite

   `ForwardShellProxyRequest` reads full non-streaming responses into memory before responding, and HTML responses go through proxy URL rewriting. This is not the main source for small JSON APIs, but it matters for the large app shell and first page load.

8. WebSocket JSON bridge remains a risky path

   The page heartbeat was moved to direct HTTP because the previous WebSocket JSON bridge could stack a 2500ms WebSocket fetch timeout with fallback HTTP. Any remaining JSON-over-WebSocket polling paths can still add delay and should be audited.

9. Agent pool warmup and recovery lag

   The relay pool can take tens of seconds to fully repopulate after restart or depletion. JackLLM's shell relay recovery loop also waits before restart decisions. That is safer than flapping, but it means cold starts and degraded pools are very visible to the browser.

10. Public internet/TLS overhead is a real floor, not the spike source

   The route through `socketjack.com` adds unavoidable network and TLS overhead. This explains part of a 1s floor, but it does not explain 5-14s spikes or 503s.

## Highest-impact fixes

1. Stop spending one reverse agent per small HTTP request. Done for the preferred path.

   Move toward a persistent logical tunnel with multiplexing, or keep a long-lived authenticated control/data channel for the workstation. The current one-request-one-agent model will keep creating latency spikes under normal app startup.

2. Add a cheap health path that does not consume relay slots.

   Heartbeat/status should ride a persistent control channel or master-side last-seen signal. The browser should not need to burn a full reverse relay agent to learn that JackLLM is alive.

3. Split wait policy by request type.

   Health/API calls should fail fast or wait far less than 3 seconds when no agents are ready. Full page navigations can tolerate a short wait because they are user-visible loads; polling endpoints should not queue behind agent starvation.

4. Shorten stale-agent timeouts.

   A preconnected idle agent should be able to acknowledge readiness quickly. Consider reducing the ready-ack timeout from 6000ms to roughly 500-1000ms and immediately discarding agents that miss it.

5. Reintroduce safe reuse instead of unconditional `ConnectionClose`.

   Keep-alive was disabled for correctness, but the durable fix is explicit tunnel leases, idle expiry, and clean close semantics so warm relay paths can be reused without leaking active sessions.

6. Batch boot data.

   Add a single `/api/bootstrap` or equivalent response for health, hardware, app settings, current session summary, and capability flags. Reducing startup request count directly reduces relay-agent churn.

7. Instrument the relay path with server timings.

   Add timing fields for master wait, agent dequeue, signal write, ready ack, local connect, first byte, body read, HTML rewrite, and response send. Expose them as `Server-Timing` headers and structured logs so future latency work can identify the exact segment instead of guessing from wall-clock totals.

## WebSocket tunnel findings

- Stale owner-token hash: the existing relay kept an old `OwnerTokenHash`, so a newly restarted workstation could register the shell route but the WebSocket tunnel was rejected. Fix: authenticated shell registration now refreshes the relay token hash when the workstation supplies a current owner token.
- Stale tunnel selection: after reconnects, the master could briefly hold more than one ready tunnel and select an older connection. Fix: tunnel routing now counts/selects only fresh heartbeating tunnels and prefers the most recently seen connection.
- Async JSON lifetime bug: JackLLM received tunnel request envelopes, but cloned the `JsonElement` inside the scheduled task after the source `JsonDocument` had been disposed. Fix: clone the request element before dispatching async local loopback work.
- Duplicate tunnel starts: workstation startup could overlap shell start calls and open more than one tunnel. Fix: added a shell-start guard so the final state settles at one WebSocket tunnel.

## What not to chase first

Loader animation, progress gradient behavior, and local JackLLM handler performance are not the main latency bottlenecks. They can improve perceived polish, but the current measurements show the public proxy path is dominated by relay setup, agent availability, and retry/wait behavior.
