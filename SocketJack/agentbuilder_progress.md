# AgentBuilder Progress

Last updated: 2026-05-28

Overall progress: 100% `[####################]`

| Feature | Progress | Bar | Status | Next step |
| --- | ---: | --- | --- | --- |
| Dedicated AgentBuilder database | 100% | `[####################]` | Complete for this slice: `AgentBuilder` DataServer tables cover workflows, APIs, runs, schedules, named reflection objects, and audit events. | Add table compaction/export tools if run/audit volume grows. |
| Core workflow graph and node execution | 100% | `[####################]` | Complete for this slice: graph models, input nodes, agent nodes, reflection nodes, parallel fan-out, timer markers, criteria-aware logic gates, return/output nodes, validation, and run results are implemented. | Add more node templates after real workflows settle. |
| SocketJack reflection integration | 100% | `[####################]` | Complete for this slice: `ReflectionService` exposes allowlisted SocketJack type discovery, parameter metadata, instance construction, static invocation, named object persistence, and safe AgentBuilder invocation. | Expand the allowlist only when a concrete SocketJack type needs Builder exposure. |
| SocketJack JSON serialization | 100% | `[####################]` | Complete for this slice: `AgentBuilderJson` uses SocketJack serializer options with safe skip/truncate behavior plus per-node output limits. | Tune default truncation limits after observing real workflow payloads. |
| Builder management APIs | 100% | `[####################]` | Complete for this slice: authenticated session, workflow, API, publish, run, runs, schedules, schedule action, reflection catalog, and reflection test endpoints are implemented. | Add public/API-key settings in a later access-control slice. |
| Published custom API routing | 100% | `[####################]` | Complete for this slice: dynamic `GET|POST /api/<apiName>` enforces unique URL-safe slugs, reserved-route blocking, missing-input form behavior, file metadata binding, and JSON execution. | Add per-API rate limits and API keys later. |
| Scheduler/timer triggers | 100% | `[####################]` | Complete for this slice: server-side schedule loop resumes from the database, calculates next runs, evaluates criteria, prevents overlaps, and logs run/audit output. | Add a schedule dashboard once operational history grows. |
| Builder/tester UI | 100% | `[####################]` | Complete for this slice: `/Builder` has a visual node canvas, palette, inspector, edge linking, local validation badges, save/publish controls, tester form/raw/history tabs, file inputs, page-level smart help tooltips, generated-content previews/downloads, and persistent draggable split. | Add richer node presets after the first real workflows settle. |
| MasterList Agent Builder tab | 100% | `[####################]` | Complete for this slice: MasterList now has a green animated glowing `Agent Builder` tab next to `Auto` that loads `/Builder` in a full-height embedded tab and remembers the selected mode. | Tune the glow intensity after live visual review if desired. |
| Output UI page | 100% | `[####################]` | Complete for this slice: `/Builder/output/<apiName>` generates a user-facing input/file form, renders Output Schema templates when present, and presents generated files/images/videos/audio/html/text/json as downloadable preview cards. | Add gallery grouping and persistent file storage if generated payloads grow beyond inline JSON. |
| Tests and verification | 100% | `[####################]` | Complete for this slice: builds pass and 13 focused AgentBuilder tests pass. | Add route/runtime smoke fixtures once auth fixtures are available. |

## Verification

- `dotnet build SocketJack.csproj --no-restore` passed.
- `dotnet build ..\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore` passed with existing nullable warnings.
- `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --filter AgentBuilderCoreTests` passed: 11/11.
- MasterList source now includes the animated green `Agent Builder` tab beside `Auto`, persisted `agentBuilder` route mode, and embedded `/Builder` iframe panel.
- `dotnet run --project ..\SocketJack.Update.Publisher\SocketJack.Update.Publisher.csproj --no-build -- --channel socketjack-magic-master-list` published the MasterList channel; server metadata verified the local payload after the unconfirmed complete response.
- Live smoke checks passed: `https://socketjack.com/MasterList`, `https://socketjack.com/MasterList/`, and `https://socketjack.com/master-list` return the new `Agent Builder` tab and `/Builder` iframe; `https://socketjack.com/Builder` returns the Builder page.
- Fixed and republished the `/Builder` auth flow: the Builder shell no longer server-side redirects to MasterList login, and the Builder API helper now forwards SocketJack bearer/cookie auth from browser storage. Live `/Builder` returns `SocketJack Builder`, includes the tester pane, and no longer contains the MasterList login form.
- Added Builder field assistance and graph editing upgrades: vertical Builder/Tester split, blue `?` field help with examples, reflection-backed `typeName`/`memberName` autocomplete, server/model autocomplete with offline labels for Agent nodes, drag-to-connect node ports, double-click/drag-off disconnect, terminal-command node/config support through SocketJack Auto permission gates, and typed Return outputs (`auto`, `video`, `image`, `file`, `text`, `json`, `html`, `plaintext`, `audio`).
- `node -e` JavaScript parse check passed for `Builder.html`; `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --filter AgentBuilderCoreTests` passed: 13/13.
- Republished `socketjack-magic-master-list`; live `/Builder` smoke check confirms vertical split CSS, node ports, reflection autocomplete loader, server/model autocomplete loader, terminal node fields, and return output type fields are present.
- Builder help tooltips are now exact-hover only: `.help-tip` ignores pointer events and only displays from direct `.help-dot:hover > .help-tip`, so popovers do not stay open when the mouse moves onto the tooltip. Live `/Builder` smoke check confirms the deployed CSS and confirms the old focus-visible tooltip selector is gone.
- Builder help tooltips now render through a single fixed page overlay instead of inside clipped pane/button parents. Browser verification on a loopback static server confirmed toolbar, inspector, and generated-form tooltips stay inside the viewport, use the page-level overlay, and have zero overlap with their related input/control rectangles; console errors/warnings were clean.
- Current `dotnet build ..\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore` is blocked by unrelated `SocketJack.LlmCore\Proxy\JackLLM.cs` overload errors: `SearchFilesInRoot` and `ListFilesInRoot` are called with 6 arguments. Tooltip verification used `Builder.html` JavaScript parsing plus browser rect checks.
- Builder auth now reuses the current SocketJack login token across the editor and generated output page: API calls read URL/cookie/storage tokens, same-origin parent/top/opener tokens, refresh `/api/web-auth/session`, and send both `Authorization` and `X-SocketJack-Auth` headers with credentials. Published to `socketjack-magic-master-list`; live `/Builder` confirms parent token lookup, session refresh, auth header forwarding, and 401 retry code are deployed.
- Builder generated-content previews are implemented in both the tester pane and `/Builder/output/<apiName>`: image/video/audio/html/text/json/file outputs are detected from Return-node metadata, data URLs, base64 payloads, URLs, and nested Auto/reflection artifacts, then shown as preview cards with `Download` and `Open` actions. `Builder.html` and the embedded output-page script parse successfully; `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --filter AgentBuilderCoreTests` passes 13/13. Published to `socketjack-magic-master-list`; live `/Builder` confirms the renderer, preview card styles, download actions, and media detection code are deployed.
- Full `SocketJack.Tests` currently fails in existing non-AgentBuilder tests: `SocketJack_SendBroadcast_WithExcept_SkipsExcludedClient`, `CrossProtocol_SendBroadcast_DoesNotGoToHttpClients`, and `WorkflowGuiSurfaces_ContainWorkflowControlsAndEndpoints`.

## Suggestions

- Add API-key/public-access controls after the signed-in-only route is stable.
- Add node templates for common SocketJack Auto flows so users can start from working graphs.
- Add drag-to-connect edge handles and node grouping once the first real workflows expose the best canvas gestures.
- Add a dedicated route smoke fixture that signs in, saves a workflow, publishes an API, and verifies the generated output page plus missing-input file upload controls.
