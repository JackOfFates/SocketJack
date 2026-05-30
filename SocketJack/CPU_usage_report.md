# MasterList CPU Usage Report

Date: 2026-05-28

## Summary

The most likely cause of the MasterList app sitting at high CPU while "doing nothing" is its live-refresh design. The public MasterList page opens a WebSocket automatically, and the server starts a per-connection pump that rebuilds, serializes, and hashes the full MasterList snapshot every few seconds even when nothing has changed.

There is also a WPF/admin-side idle refresh loop that rebuilds the UI collections every 3 seconds. That makes the desktop app do regular foreground work even when the user is not interacting with it.

I did not find a single obvious tight `while(true)` CPU spin in the MasterList source. The CPU pattern is more consistent with repeated snapshot/UI refresh work multiplied by connected browser tabs, live sockets, and server-list size.

## Implementation Update

The idle-refresh hot paths have now been changed in the local source:

- `SocketJack-MagicMasterList/Program.cs` now maintains a server-list version and one cached live MasterList payload. Server and shell-relay changes invalidate that cache and broadcast the rebuilt payload to connected sockets; idle sockets no longer each run their own periodic rebuild/serialize/hash loop.
- `/masterlist/ws` is no longer opened during unauthenticated landing-page boot. The browser script syncs the socket only when authenticated and visible, closes it when the tab is hidden or the session is cleared, and suppresses background fallback polling while hidden.
- Auto live sockets are broadcast from server/shell/account/session change events instead of a per-socket timer.
- `SocketJack-MagicMasterList/MainWindow.xaml.cs` stops the 3-second WPF refresh timer while the dashboard is hidden or minimized to tray, while still allowing event-driven refreshes and manual refresh.
- `RefreshFromHostAsync()` and `RefreshShellRelaysAsync()` now diff keyed collections instead of clearing and re-adding every row.
- `EnsureShellRelayServerRowsLocked()` now compares meaningful published row fields before writing. Timestamp-only refreshes no longer force a data save.

## Current Local Process Check

I checked the current Windows process list from this workspace. There was no live `SocketJack-MagicMasterList.exe` process to attach to or stack-sample locally.

A short 3-second CPU delta sample showed current local CPU was from other processes:

| Process | Approx CPU over one core | Notes |
| --- | ---: | --- |
| `tar.exe` | 132.3% | Linux package build/archive still running |
| `JackLLM.exe` | 86.5% | Active JackLLM process, not MasterList |
| `powershell.exe` | 52.6% | Build/release helper process |
| `Codex.exe` | 24.5% | Current investigation tooling |

So the report below is source-based for MasterList, not a live stack capture from a currently running MasterList process.

## Primary Cause: Per-Connection WebSocket Snapshot Pump

Evidence:

- `SocketJack-MagicMasterList/Program.cs:20937` calls `connectMasterListLiveSocket()` immediately during page boot.
- `SocketJack-MagicMasterList/Program.cs:20871` opens `new WebSocket(masterLiveSocketUrl('/masterlist/ws'))`.
- `SocketJack-MagicMasterList/Program.cs:5957-5959` sends an initial snapshot and then starts the pump.
- `SocketJack-MagicMasterList/Program.cs:6065-6072` starts a background task per socket and runs it every 2.5 seconds for `masterlist` pages, or every 1.8 seconds for `auto` pages.
- `SocketJack-MagicMasterList/Program.cs:6084-6088` builds the snapshot, serializes it to JSON, hashes it, and only then skips sending if the signature is unchanged.
- `SocketJack-MagicMasterList/Program.cs:8180-8192` builds the MasterList live snapshot by calling `BuildResponse()` and serializing `response.Servers`.
- `SocketJack-MagicMasterList/Program.cs:1469-1479` `BuildResponse()` gets, filters, clones, and maps the server list.
- `SocketJack-MagicMasterList/Program.cs:1712-1730` `GetServers()` sorts, collapses duplicate listings, clones rows, and applies availability data.

Why this burns CPU:

Each idle connected browser page gets its own loop. Every 2.5 seconds that loop still does the expensive work before it can decide that nothing changed. With multiple users, tabs, bots, or left-open browser sessions, this scales linearly:

`connected sockets * full snapshot rebuilds * JSON serialization * SHA256 hashing`

For example, 50 idle MasterList tabs means roughly 1,200 full snapshot rebuild/hash attempts per minute. The Auto page is even more aggressive at about 33 attempts per minute per socket, and its snapshot path also includes session discovery work.

## Secondary Cause: WPF Admin Refresh Timer

Evidence:

- `SocketJack-MagicMasterList/MainWindow.xaml.cs:34-37` creates a `DispatcherTimer` with a 3-second interval.
- `SocketJack-MagicMasterList/MainWindow.xaml.cs:122-129` calls `RefreshFromHostAsync()` on every tick.
- `SocketJack-MagicMasterList/MainWindow.xaml.cs:981-991` gets all servers, clears the `ObservableCollection`, re-adds every server, refreshes the collection view, refreshes status/counts, and then refreshes shell relays.
- `SocketJack-MagicMasterList/MainWindow.xaml.cs:994-1004` clears and repopulates shell relays and raises dependent property changes.

Why this burns CPU:

Even if the app is minimized or sitting in the tray, the timer still rebuilds the WPF-bound collections every 3 seconds. On a small server list this is just wasteful; on a larger live list it can create steady CPU from collection resets, WPF layout/binding work, sorting/filtering, and property-change notifications.

## Disk/Database Amplifier

Evidence:

- `SocketJack-MagicMasterList/Program.cs:1715-1718` calls `EnsureShellRelayServerRowsLocked(now)` during `GetServers()` and saves the data server if shell rows changed.
- `SocketJack-MagicMasterList/Program.cs:1734-1800` can update shell-backed server row timestamps/status and mark the data changed.
- `SocketJack/Net/Database/DataServer.cs:609-614` saves by building a full snapshot and writing storage.
- `SocketJack/Net/Database/DataServer.cs:855-897` split storage writes table files and the manifest.

Why this matters:

If shell relay rows are in a state that keeps being considered changed, then a read-side refresh can turn into repeated full persistence writes. That would amplify CPU and disk activity during what should be a read-only refresh.

## Not The Main Culprit

I reviewed the obvious loops. The WebSocket frame parsers and streaming proxy loops are request-driven. The TCP accept loops have delays or await network activity. I did not see a classic zero-delay CPU spin inside the MasterList code path.

The issue is repeated scheduled work, not an accidental infinite loop.

## Recommended Fixes

1. Change live MasterList updates from per-socket polling to event-driven broadcasting. Rebuild the server snapshot only when servers, shell relays, or relevant account/session data changes.

2. If polling is kept, compute one shared cached snapshot per interval, then fan it out to connected sockets. Do not rebuild and hash the same global server list separately for every socket.

3. Move the unchanged-check earlier. Maintain a server-list version or change stamp so the pump can skip `BuildResponse()`, JSON serialization, and SHA256 hashing when nothing changed.

4. Do not open the `/masterlist/ws` socket on every landing page boot. Delay it until the user is authenticated and actually viewing the MasterList surface, or at least stop the pump when the page is hidden.

5. Reduce or pause the WPF 3-second refresh timer when the window is hidden/minimized to tray. Prefer event-driven updates from `ServersChanged`, `ShellRelaysChanged`, and manual refresh.

6. Make `RefreshFromHostAsync()` diff the current collection instead of clearing and re-adding every row on every tick.

7. Guard `EnsureShellRelayServerRowsLocked()` so it only writes when meaningful fields changed, not merely because a refresh happened.

## Bottom Line

The CPU is almost certainly being spent on "idle" refresh work:

- Server side: every connected MasterList/Auto WebSocket repeatedly rebuilds snapshots.
- Desktop side: the WPF admin timer repeatedly rebuilds bound collections.
- Persistence side: shell row reconciliation can turn refreshes into full data saves.

The first fix I would make is to cache/version the live snapshot and broadcast only on actual changes. That should cut the largest constant CPU cost without changing the user-visible behavior.
