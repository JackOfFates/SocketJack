# /Auto + /MasterList WebSocket Live Update Plan

Last updated: 2026-05-26

Overall progress: 100% [####################]

## Goal

Add WebSocket API equivalents for `/Auto` and `/MasterList` so both pages can receive low-latency, server-pushed updates while keeping the existing HTTP APIs as compatibility fallback.

The same pass also fixes intermittent `/Auto` session loading by making session lists live, retryable, server-categorized, and searchable.

## Success Criteria

- `/Auto` and `/MasterList` open a same-origin secure WebSocket on `socketjack.com`.
- HTTP APIs remain available and are used as fallback when WebSocket is unavailable.
- Server list, runtime status, proxy health, auth/usage, and session list changes appear without manual refresh.
- `/Auto` sessions load reliably on first page load and recover after reconnect.
- `/Auto` sessions are grouped by server.
- Master-list-owned `/Auto` sessions render in normal white text.
- Sessions discovered only from an LLM server render in slightly greyed text.
- Clicking a server-only session imports/opens it as a normal `/Auto` session.
- Server groups are expandable/collapsible.
- Each server group can be searched/filtered independently, with a global search remaining available.

## Progress By Feature

| Feature | Progress | Bar | Status |
| --- | ---: | --- | --- |
| Current API and session-flow inventory | 100% | [####################] | Existing HTTP routes and WebSocket hooks mapped |
| Shared WebSocket protocol design | 100% | [####################] | Snapshot, heartbeat, reconnect, session import, and HTTP fallback complete |
| Master-side live WebSocket hub | 100% | [####################] | Built, published, and WSS verified |
| `/Auto` WebSocket endpoint and frames | 100% | [####################] | Signed-in snapshots, grouped sessions, and import frames verified |
| `/MasterList` WebSocket endpoint and frames | 100% | [####################] | Server snapshots verified on primary and alias routes |
| Auth, usage, token, and login-state live updates | 100% | [####################] | Authenticated WSS snapshot verified without unauthenticated token flicker |
| Live server/proxy/runtime update events | 100% | [####################] | Coalesced live snapshots active for v1 |
| Auto session snapshot + delta events | 100% | [####################] | Live snapshot refresh plus session-import acknowledgement implemented |
| LLM-server session discovery and merge | 100% | [####################] | Authenticated server-only discovery verified |
| Server-grouped session UI | 100% | [####################] | Browser DOM verified |
| Per-server expand/collapse and search | 100% | [####################] | Per-server filters and remembered collapse added |
| Server-only session import/open flow | 100% | [####################] | WebSocket import verified with HTTP fallback |
| Client reconnect, heartbeat, and HTTP fallback | 100% | [####################] | Reconnect, ping/pong, and existing HTTP fallback wired |
| Build, publish, and Codex-browser verification | 100% | [####################] | Build, publisher, WSS probes, and browser DOM checks complete |

## Proposed WebSocket Surfaces

Use same-origin secure WebSockets through the existing `socketjack.com` TLS listener:

- `wss://socketjack.com/auto/ws` and case-equivalent `/Auto/ws`
- `wss://socketjack.com/masterlist/ws` and case-equivalent `/MasterList/ws`
- `wss://socketjack.com/master-list/ws`

Both should share the same backend hub/protocol code, with page-specific subscriptions.

## Protocol Draft

Frames are JSON text frames unless binary payloads become necessary later.

| Frame | Direction | Purpose |
| --- | --- | --- |
| `hello` | client -> server | Sends page, auth token hint, selected server, session id, mode, filters. |
| `ready` | server -> client | Confirms connection, auth state, protocol version. |
| `subscribe` | client -> server | Subscribes to `servers`, `auto-sessions`, `server-sessions`, `usage`, or `runtime`. |
| `snapshot` | server -> client | Full initial state for subscribed topics. |
| `delta` | server -> client | Incremental server/session/status changes. |
| `session-import` | client -> server | Imports a greyed server-only session into `/Auto` storage. |
| `session-imported` | server -> client | Returns the new canonical `/Auto` session id. |
| `ping` / `pong` | both | Keeps connection alive and measures latency. |
| `error` | server -> client | Structured recoverable error. |

## Backend Work Items

- [x] Add shared live WebSocket hub in `SocketJack-MagicMasterList/Program.cs`.
- [x] Register custom WebSocket routes before generic/static handlers.
- [x] Reuse existing auth extraction, account usage, server list, shell proxy, and auto-session builders.
- [x] Add per-connection state for page, query, headers, last client request, snapshot signature, send lock, and pump cancellation.
- [x] Push coalesced server-list/runtime/proxy snapshots over the live master-list socket.
- [x] Push coalesced auto-session snapshots after live activity and on a short pump interval.
- [x] Add authenticated server-session discovery payload by querying eligible shell routes through the existing proxy/tunnel.
- [x] Add throttling/coalescing through snapshot signatures and page-specific pump intervals.
- [x] Add WebSocket `session-import` handling and `session-imported` acknowledgement frames.

## Frontend Work Items

- [x] Add `connectAutoLiveSocket()` to the `/Auto` embedded client.
- [x] Add `connectMasterListLiveSocket()` to the `/MasterList` embedded client.
- [x] Keep current HTTP load calls as initial fallback and reconnect recovery.
- [x] Replace session-list rendering with a grouped model containing server id/title, master sessions, server-only sessions, collapsed/expanded state, and per-server search term.
- [x] Render server-only sessions with muted grey text and a source badge.
- [x] On server-only session click, import through the live WebSocket when connected, then open it.
- [x] Keep HTTP session-save import as fallback when the live socket is unavailable.
- [x] Refresh stale sessions after reconnect, search changes, import, and auth changes.

## Verification Plan

- Build `SocketJack-MagicMasterList`.
- Publish only `socketjack-magic-master-list`.
- Open `/Auto` in Codex browser and confirm:
  - WebSocket connects;
  - auth/usage arrives live;
  - sessions load without manual refresh;
  - groups expand/collapse;
  - per-server filtering works;
  - server-only sessions are grey and import on click.
- Open `/MasterList` and confirm:
  - server/proxy/runtime status updates without refresh;
  - existing HTTP fallback still works if WebSocket is closed.

## Current Notes

- Existing `/Auto` HTML, CSS, JS, routing, and session logic live primarily in `SocketJack-MagicMasterList/Program.cs`.
- Keep changes compatible with existing `/auto/api`, `/auto/route`, `/api/web-auth/session`, `/api/lmvsproxy/servers`, and `/api/auto-sessions` style HTTP flows.
- Do not remove HTTP routes; WebSocket is an accelerator and live-update layer.
- 2026-05-26 implementation slice: `wss://socketjack.com/masterlist/ws` returns live server snapshots; `wss://socketjack.com/auto/ws` returns live Auto session snapshots.
- Server-only LLM sessions are only included for authenticated Auto users and render as muted/greyed imported candidates.
- Publisher was run in `socketjack-magic-master-list` mode and verified server metadata after upload completion response loss.
- 2026-05-26 completion slice: deployed `/Auto` includes `importServerOnlySessionViaSocket` and `autoLiveImportResolvers`; deployed `/MasterList` includes `connectMasterListLiveSocket`.
- WSS probes verified `wss://socketjack.com/masterlist/ws`, `wss://socketjack.com/master-list/ws`, and signed `wss://socketjack.com/auto/ws`.
- Signed `/auto/ws` returned 25 master Auto sessions, 41 server-only sessions, and 3 server groups for the TitanX probe.
- WebSocket `session-import` returned `session-imported`; the temporary smoke session was deleted successfully through `/auto/sessions/action`.
- Browser DOM verification showed `/Auto` rendering grouped session UI with 3 server groups.
