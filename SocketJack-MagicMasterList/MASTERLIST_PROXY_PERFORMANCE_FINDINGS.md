# SocketJack MasterList and Proxy Performance Findings

Date: 2026-05-16

## Summary

The slow feeling is most likely caused by work that happens after the initial HTML arrives: server-list hydration, per-server latency pings, cold shell-proxy handoff, and Workstation-side reverse relay availability. The raw public HTML route was not extremely slow from this workstation, but the previous code could still make the page feel slow because it performed synchronous health/proxy work while building the server list and then re-rendered the full list after each browser ping.

## Live Timing Snapshot

Measured from this workspace against the currently deployed `socketjack.com` service:

| Route | Result | Time to first byte | Total time | Size |
| --- | ---: | ---: | ---: | ---: |
| `https://socketjack.com/masterlist` | `200` | `0.339s - 0.462s` | `0.554s - 0.842s` | `95,588 bytes` |
| `https://socketjack.com/api/lmvsproxy/servers` | `200` | `0.362s - 0.627s` | `0.362s - 0.639s` | `7,141 bytes` |
| `https://socketjack.com/proxy` | `404` | `0.338s - 0.388s` | `0.338s - 0.388s` | `25 bytes` |
| `https://socketjack.com/proxy/1070Ti` | `503` | `3.502s` | `3.502s` | waiting/error |
| `https://socketjack.com/proxy/TitanX` | `503` | `3.601s` | `3.601s` | waiting/error |

The bare `/proxy` route is not the real proxy session path. Real sessions are under `/proxy/{serverName}` and can be slower because they need a reverse relay agent.

The exact reported proxy routes currently resolve in the server-list API, but both are marked offline:

- `1070Ti`: `Server is not responding through shell.`
- `TitanX`: `Server is not responding through shell.`

That means the master route exists, but no usable JackLLM reverse relay agent is currently connected for those sessions.

## Main Findings

1. Server-list API could block on live health probes.

   `BuildResponse()` called `GetServers()`, and `GetServers()` called `UpdateServerAvailabilityLocked()`. That path could call `CheckServerHealth()` for each registered server while holding the master-list lock. Each check used an HTTP ping with an 1800 ms timeout. A few slow/offline entries could make the list API wait serially, and the lock also delayed registrations and shell-proxy updates.

2. Shell-proxy ping requests could wait up to 3 seconds.

   `ShellProxy_OnHttpRequest()` waited for a relay agent whenever `ActiveSessions == 0` and `WaitingAgents == 0`. That was useful for first-page navigation, but it also affected non-navigation API pings such as `/proxy/{server}/api/master-list/ping`.

3. Proxy forwarding could begin without a currently available agent.

   `ShouldAttemptShellProxyForward()` treated a recently seen agent as enough to forward the request. If the agent had refreshed or disappeared and no agent was actually waiting, the HTTP request could sit in the relay path until timeout.

4. The shell waiting page could create its own retry load.

   The waiting page polled `location.href` every 1300 ms with `Accept: text/html`, then forced a full-page reload every 4800 ms. When the route had no agent, each probe hit the slow navigation path again, so the page looked like it was loading forever and kept adding pressure to the proxy route.

5. Browser latency checks were too eager.

   The MasterList page scheduled runtime pings while updating every card, including cards not currently visible. Each ping completion forced `syncServerCards(true)` and `render(true)`, causing a full-card refresh per ping.

6. MasterList HTML was rebuilt per request.

   `BuildIndexHtml()` creates a large inline HTML/CSS/JS string. The server regenerated it for each `/masterlist` and `/login` request even though the result is effectively static for a running process.

7. Health probes created new `HttpClient` instances.

   `TryHttpGet()` constructed a new `HttpClient` per probe. This prevents connection pooling and adds avoidable socket/client setup overhead.

8. JackLLM Workstation proxy pings were not cheap pings.

   The Workstation `/api/master-list/ping` route called `BuildMasterListPingPayload()`, which called `ProbeLocalModelRuntimeStatus()`. That probe synchronously requests the local model runtime `/v1/models` endpoint with a 900 ms timeout. Through `/proxy/{server}`, every ping also consumes a reverse relay session, so repeated wait-page/browser pings could be slowed by a local LM Studio/runtime probe even when the real question is only "is the shell route alive?"

9. Reverse relay agents refreshed in synchronized bursts.

   Each `ReverseTcpRelayClient` slot waited the same 25 seconds before refreshing an idle master connection. With an agent pool started at the same time, all agents can disconnect/reconnect in the same window, briefly dropping `waitingAgents` to zero. That makes the public proxy route look offline or cold even though the Workstation is trying to stay connected.

10. Workstation recovery treated "recently seen" as fully healthy.

   `RefreshShellRelayHealthAsync()` previously reset the no-agent timer when the master had a recent `lastAgentSeenUtc`, even if `waitingAgents == 0` and `activeSessions == 0`. With the 90 second recent-agent grace plus the 45 second recovery delay, a broken route could remain unavailable for roughly two minutes before restart.

11. Shell recovery was gated by the main local proxy listener.

   `RefreshShellRelayHealthIfDue()` returned early when `_proxy.IsListening` was false. If shell mode was marked running but the main local proxy listener was down, the recovery loop could skip the very situation it needed to repair.

12. Some Workstation UI navigations escaped the `/proxy/{server}` prefix.

   The global `fetch` wrapper correctly rewrites most `/api/*` calls to `/proxy/{server}/api/*`, but non-fetch navigations do not pass through that wrapper. `window.open('/api/observability/prometheus')` and `window.open('/sql')` would open root `socketjack.com` paths instead of the Workstation route when used from `/proxy/1070Ti` or `/proxy/TitanX`.

## Workstation Code Reference Highlights

- `SocketJack.LlmCore/Proxy/JackLLM.cs`: `/api/master-list/ping` now uses `GetCachedModelRuntimeStatus()` instead of probing the model runtime on every request.
- `SocketJack/Net/ReverseTcpDuplicator.cs`: reverse agent idle refresh now uses `GetWaitingAgentRefreshInterval(slot)` to stagger agent reconnections.
- `JackLLM/MainWindow.xaml.cs`: shell mode now starts the local Chat UI listener before relay registration, and relay recovery keys off currently waiting/active agents.
- `SocketJack/html/JackLLMWebChat.html`: non-fetch navigations now use proxy-aware paths for Prometheus export and SQL admin.

## Changes Made

- Cached MasterList HTML in a process-local `Lazy<string>` so `/masterlist` and `/login` reuse the generated page.
- Changed the server-list response path to use stored health/relay state instead of doing live network probes during list generation.
- Kept live latency dynamic in the browser: visible cards ping after render, show a spinner next to latency while the ping is in flight, and update the individual card when the ping finishes.
- Limited automatic browser pings to the first 24 visible cards instead of scheduling pings for every card immediately.
- Removed full-list re-rendering after each latency ping.
- Skipped the shell relay wait loop for non-navigation proxy requests, so health/API pings do not spend up to 3 seconds waiting for a relay agent.
- Required a currently waiting/active reverse agent before forwarding proxy traffic, instead of forwarding based only on a recently seen agent.
- Changed the shell waiting page to poll `/proxy/{server}/api/master-list/ping` with `Accept: application/json` instead of repeatedly fetching and reloading the full document.
- Reused a pooled `HttpClient` for remaining server-side health probes.
- Cached the Workstation model-runtime status used by `/api/master-list/ping` for 5 seconds, so proxy liveness checks do not hammer `/v1/models`.
- Added per-slot jitter to reverse relay idle refreshes so the full agent pool does not drop and reconnect at the same instant.
- Changed Workstation shell recovery to require a currently waiting/active agent before marking the relay healthy. Recent agent sightings now start the no-agent timer instead of resetting it.
- Allowed shell recovery to run when shell mode is already marked running, even if the main local proxy listener is down.
- Ensured shell mode starts the local Chat UI listener before registering/starting reverse relay agents.
- Rewrote Workstation UI `window.open()` paths for Prometheus export and SQL admin so they stay under `/proxy/{server}`.

## Files Changed

- `SocketJack-MagicMasterList/Program.cs`
- `SocketJack.LlmCore/Proxy/JackLLM.cs`
- `SocketJack/Net/ReverseTcpDuplicator.cs`
- `JackLLM/MainWindow.xaml.cs`
- `SocketJack/html/JackLLMWebChat.html`

## Verification

Completed successfully:

- `dotnet build .\SocketJack.LlmCore.csproj --no-restore --nologo -v:minimal`
- `dotnet build .\SocketJack.csproj --no-restore --nologo -v:minimal`
- `dotnet build .\JackLLM.csproj --no-restore --nologo -v:minimal -p:OutDir=C:\Users\Vin\AppData\Local\Temp\SocketJackBuild\JackLLM\`
- `dotnet build .\SocketJack-MagicMasterList.csproj --no-restore --nologo -v:minimal`

The normal `JackLLM` build output directory was locked by the running `JackLLM Workstation` process, so the first normal build failed during DLL copy. Rebuilding to a temporary output directory succeeded. The remaining warnings are existing nullable-context warnings in `SocketJack.LlmCore` and non-nullable table-field warnings in `SocketJack-MagicMasterList`; there were no build errors in the verified builds.

## Remaining Risks

- The deployed site will not reflect these improvements until this local project is deployed.
- Real `/proxy/{serverName}` performance still depends on whether the JackLLM desktop reverse agent is already connected.
- The exact live `1070Ti` and `TitanX` proxy routes were still offline during testing because no usable reverse relay agent was connected to the deployed master route.
- If the MasterList grows far beyond the current size, a small client-side ping queue could further smooth latency checks. The current patch already avoids pinging every hidden card at once.
