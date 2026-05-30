# Connection Revamp

## Goals

- Keep the JackLLM Workstation proxy loader honest: the large line always reports the overall proxy percentage, while a softer detail line shows the actual phase currently loading.
- Make the loader progress gradient feel anchored to the full track instead of stretching and shrinking as the percentage changes.
- Calm down the Workstation background motion so loading does not feel like a zoom-out.
- Reduce false offline states through the proxy. A single slow heartbeat should look like reconnecting, not a full disconnect.

## Implemented

- Added `startupLoaderDetail`, a greyed current-phase line under `Loading proxy ##%`.
- Animated the detail line with a slow downward fall whenever the startup phase changes.
- Changed the progress fill to clip a full-width gradient. The visible area changes with progress, but the gradient itself keeps the track width.
- Slowed the progress sheen so it reads as motion across the track rather than a fast shimmer.
- Reduced startup/background scale and translation ranges.
- Added a slow anchor drift to the background gradient so motion is anchored but not static.
- Smoothed heartbeat status:
  - `/api/health` heartbeat budget is now 9 seconds through the proxy.
  - heartbeat uses the plain HTTP health route first instead of the WebSocket JSON bridge, because direct proxy health is consistently lower latency.
  - heartbeat polls are single-flight and run every 5 seconds.
  - the UI only marks `Offline` after 3 consecutive failures or more than 30 seconds since the last live heartbeat.
  - successful heartbeat title text includes measured browser-side latency.
- Added whole-proxy WebSocket tunnel transport:
  - Browser URLs remain normal `https://socketjack.com/proxy/{server}/...` HTTP URLs.
  - Master-list server now exposes `wss://socketjack.com/api/shell/proxies/{relayId}/tunnel`.
  - JackLLM Workstation connects a persistent multiplexed tunnel by default and falls back to TCP relay if unavailable.
  - Master forwards proxy HTTP requests over the WebSocket tunnel first, including headers, body, status, and response chunks.
  - JackLLM serves tunnel requests through local loopback HTTP to `127.0.0.1:11436`, so existing handlers remain shared.
  - Master adds `Server-Timing` for tunnel requests: `sj-tunnel-wait`, `sj-ws-send`, `sj-local`, `sj-first-byte`, and `sj-total`.
  - Master refreshes relay owner-token hashes on authenticated shell registration, avoiding stale-token tunnel rejection after workstation restarts.
  - Master chooses the freshest heartbeating tunnel when multiple connections briefly overlap after reconnects.
  - JackLLM guards shell startup against overlapping start calls and clones tunnel request JSON before dispatching async local work.

## Acceptance Target

- Loader reaches the chat surface without a stuck overlay.
- The heartbeat pill remains live or reconnecting during transient proxy slowness, not repeatedly offline.
- Direct proxy health should be in the low-second range after the relay pool has warmed.
- Browser state should show: prompt visible, startup overlay gone, no red startup timeout banner, no console errors.

## Verification Notes

- Rebuilt JackLLM Workstation successfully after closing the running process first.
- Local health reached HTTP 200 after startup.
- Direct proxy checks after relay warmup:
  - `/proxy/TitanX/api/health`: HTTP 200 in 1356 ms.
  - `/proxy/TitanX/api/chat-sessions`: HTTP 200 in 1978 ms.
  - relay pool returned to 64 waiting agents, 0 waiting public clients, 0 active sessions.
- Codex browser loader samples showed:
  - main line stayed as `Loading proxy ##%`.
  - detail line showed the real phase, including `Checking proxy`, `Preparing chat`, `Loading permissions`, `Loading services`, and `Loading activity`.
  - detail line color was greyed: `rgba(203, 213, 225, 0.58)`.
  - progress track width variable remained fixed at `420px` while fill width changed by percentage.
- Final Codex browser state:
  - title: `JackLLM Chat`.
  - prompt visible: yes.
  - startup overlay gone: yes.
  - error banners: none.
  - console warnings/errors: none.
  - heartbeat class: `status-connected`.
  - heartbeat offline body state: false.
  - heartbeat observed live latency: 1061 ms to 2821 ms.
- Relay while browser stayed open remained stable at 64 waiting agents, 0 waiting public clients, and 0 active sessions across five polls.

## WebSocket Tunnel Verification

- Master-list channel was built and published with `SocketJack.Update.Publisher --channel socketjack-magic-master-list`; the publisher verified server metadata after the expected restart disconnect.
- JackLLM Workstation was closed before rebuild/restart.
- Master status settled at `webSocketTunnelConnected=true`, `webSocketTunnelCount=1`, and `waitingAgents=0`.
- Public proxy health after the tunnel fix:
  - `/proxy/TitanX/api/health`: HTTP 200, `Server-Timing: sj-tunnel-wait;dur=89, sj-ws-send;dur=1, sj-local;dur=2, sj-first-byte;dur=93, sj-total;dur=93`.
  - Five warmed `/api/health` samples completed in roughly 0.35-0.39s from curl.
  - `/proxy/TitanX/api/chat-sessions`: HTTP 200 in roughly 0.41s with tunnel timing.
- Codex browser final smoke:
  - title: `JackLLM Chat`.
  - heartbeat visible as live for `TitanX - JACK`.
  - hardware line visible and live.
  - startup overlay disappeared after settle and chat content populated.
