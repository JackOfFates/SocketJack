# SocketJack Peer Inference Roadmap

Overall platform maturity for "rent peer hardware to build/run local or remote LLM projects": **100% complete**.

SocketJack already has a serious foundation: transport multiplexing, P2P metadata, reverse forwarding, LM Studio proxying, web auth, chat sessions, permissions, remote admin, hardware/profile APIs, token-rate requests, Stripe checkout scaffolding, and local finance/payout records. The next leap is turning those pieces into a reliable marketplace: discovery, leases, metering, sandboxing, verified hardware, payment settlement, project-oriented remote workspaces, and remote model routing that feels local to developer tools.

This roadmap is intended to drive source-code and project-file changes.

## Top Priority: Visual Studio Copilot Remote Model Duplicator

- [x] **Copilot Port Duplicator on `11433`**: create a dedicated local duplicator listener that Visual Studio Copilot can target as if it were talking to a local LM Studio/OpenAI-compatible endpoint.
- [x] **Remote Model Routing Through JackLLM**: route Copilot requests through the selected remote server so chat completions and OpenAI-compatible paths behave like a local LM Studio model.
- [x] **Remote Session File Cloner**: when Copilot requests a remote file path for read/write/replace/rename work, clone the remote file locally first; delete actions intentionally do not download the file.
- [x] **Session Root Swap**: map remote file roots into the current working directory at `Sessions/<session name>/...`, preserving the remote path beneath that session folder.
- [x] **Cancellable File Mirror UI**: add a WPF Copilot Files tab with live clone rows, cancel controls, hash/status tracking, folder open actions, and right-click commands.
- [x] **Filesystem Watch + Hash Tracking**: watch the local mirror root, update clone status dynamically when files change/delete, and track SHA-256 hashes.
- [x] **Browser Servers Dashboard**: expose server-list browsing/filtering in the JackLLM browser UI backed by the SocketJack server list API.
- [x] **WPF Servers Tab Filtering**: mirror the browser Servers experience in the WPF GUI with filters for hardware/GPU/VRAM, title, owner username, and external IP.
- [x] **WPF Copilot Server Picker**: select a server in the WPF Servers tab and route `localhost:11433` through that peer as the active Copilot duplicator target.
- [x] **Advanced WPF Server Filters**: add dedicated filters for available models, availability, and price/payment status.
- [x] **Lease-Aware Local-Model Illusion**: tie the duplicator to an active paid/free lease so Visual Studio Copilot can use remote peer hardware with metering, expiration, and reconnect behavior.

## Current Feature Completion

| Area | Completion | Status |
|---|---:|---|
| Core networking, HTTP, WebSocket, TCP/UDP, multiplexing | 100% | Mature multi-protocol base plus marketplace lease APIs, developer SDK routes, hardware attestation, route telemetry, CORS/auth-aware JSON endpoints, package refresh, documented service inclusion policy, and local/remote OpenAI-compatible routing |
| Local LLM proxy and browser console | 100% | OpenAI-compatible proxy, Responses bridge, streaming keepalives, tool gates, browser console, runtime loaded-assembly type IntelliSense, reflection APIs, and lease-aware remote routing exist |
| Remote hardware/server browser | 100% | Profile/checkout/browser list, WPF filtered picker, signed hardware attestation, advanced model/availability/payment/price filters, trust metadata, per-server monitoring payloads, health scoring, and lease-broker selection exist |
| Visual Studio Copilot remote-model duplicator | 100% | Port 11433, remote routing, WPF server picker, lease-aware local-model illusion, broker lease enforcement, file cloning, WPF clone monitor, and hash/watch tracking implemented |
| Peer rental marketplace | 100% | Persistent lease broker, quote/reserve/queue/schedule/activate/heartbeat/end lifecycle, marketplace lease checkout, host finance profiles, payouts, trust/health metadata, sandboxed developer workspaces, and compute-linked usage metering exist |
| Billing, token accounting, payouts | 100% | Stripe checkout/webhook token crediting, marketplace lease checkout activation, append-only usage ledger, payout records, weekly/direct/instant payout flows, fee/tax accounting, payout webhook reconciliation, and GPU/CPU compute-time token accounting exist |
| Security and permissions | 100% | Permission gates include trust/abuse command blocking, SQL Admin owner-username tenant isolation, secure SQL Admin mutation/session controls, admin intervention records, live security monitoring, permission-risk scoring, marketplace policy payloads, and observability events |
| Trust, reputation, and abuse operations | 100% | Persistent cases, reputation scoring, host verification, signed hardware attestation, renter identity status, command/content abuse controls, GUI/web admin tooling, risk scoring, stale/high-severity monitoring, and lease policy integration exist |
| SQL Admin and database management panel | 100% | SA bootstrap enforcement, constructor credential seeding, strict owner-username tenant isolation, API Creator reflection/query-builder support, endpoint policies, designer restore points, event sandboxing, typed destructive confirmation, audit logging, backups, and operations dashboard exist |
| Observability and diagnostics | 100% | Live admin dashboard, typed snapshot API, recent event ledger, route counters, hardware/service/security/marketplace/workflow health, and Prometheus-style export exist |
| Developer project workflow | 100% | Saved project workspace API, developer SDK rent/run/sync/stop surface, mirror-root linking, active marketplace lease context, Copilot target summary, clone summary, browser project panel, filesystem-context persistence, auto-detected Codex-style Git commands and file services, sandbox quotas/policies/secrets/cleanup metadata, and readiness monitoring exist |
| Agent filesystem context controls | 100% | Agent Mode dropdown, `None`/`All`/multi-root selection, accessible-root discovery, project persistence, backend system-prompt context injection, and owner-scoped policy metadata exist |

## Already Built

- [x] Multi-protocol server surface through `MutableTcpServer` for HTTP, WebSocket, SocketJack, RTMP/custom protocol routing.
- [x] Local LM Studio proxy, OpenAI-compatible chat routes, streaming chat, stop/steer controls, model discovery, and browser UI.
- [x] Browser prompt IntelliSense indexes all loadable runtime assembly types and namespaces, including non-public and partially loadable assemblies.
- [x] `/reflect types` and `/reflect namespaces` command completions for discovering loaded assembly symbols from the local proxy/browser console.
- [x] Browser console IntelliSense requests larger completion sets, shows hover detail, and allows slash/reflection suggestions when the prompt starts with `/`.
- [x] WebAuth, registration approval, admin roles, bearer/basic auth, CORS-aware auth routes.
- [x] Per-client permission gates for terminal, files, internet/search tools, VS tools, FTP, SQL admin, uploads, downloads, images.
- [x] Session persistence, share links, comments, file uploads/downloads, filesystem access allow/deny lists.
- [x] Server browser profile endpoint, hardware endpoint, advertised token rate, available models/resources, payment-required metadata.
- [x] Stripe checkout service and server-browser checkout endpoint.
- [x] Local finance dashboard records for account profile, usage, token operations, and payout requests.
- [x] Stripe Checkout Session retrieval/webhook flow credits local token purchases and records paid marketplace lease usage in the finance ledger.
- [x] Persistent marketplace lease broker at `/api/marketplace/leases` with quote, reserve, activate/paid, heartbeat/renew, end/cancel/expire, and Copilot-select actions.
- [x] Marketplace lease checkout through the SocketJack payment surface using Stripe Checkout Sessions and lease metadata for webhook/session activation.
- [x] Finance token-operation records now capture GPU seconds, CPU compute seconds, RAM/system/I/O deltas, component electricity costs, and per-token GPU/CPU compute ratios for more accurate token pricing.
- [x] Weekly direct-deposit payout preference, weekly period preview, admin weekly payout batch creation, and connected-account weekly payout schedule configuration.
- [x] Direct-deposit payout records now transfer payable host balances to connected Stripe accounts and track transfer metadata.
- [x] Instant payout requests now deduct a fixed `$1.00` standard fee plus configured applicable tax, store the delivery-fee ledger fields, and call connected-account instant payout creation with optional debit-card/external-account destination.
- [x] JackLLM browser Payouts UI lets hosts choose weekly direct deposit, direct deposit, or instant debit-card payout and shows method, fee, transfer/payout ids, and final payout amount.
- [x] Reverse TCP forwarding and public IP resolution primitives.
- [x] GPU TDP detection heuristics and cost settings for electricity/storage estimates.
- [x] Visual Studio Copilot duplicator listener on `localhost:11433`.
- [x] Remote model server selection and OpenAI-compatible routing for Copilot duplicator traffic.
- [x] Browser Servers dashboard with SocketJack server-list API filters.
- [x] Remote session file clone manager with cancellation, byte progress, SHA-256 hash tracking, local file watch updates, and session-root path swapping into `Sessions/<session name>/...`.
- [x] WPF Copilot Files tab styled like a Windows folder view with extended selection, drag range selection, right-click commands, cancel buttons, live status, and open-folder actions.
- [x] WPF Servers tab for browsing the SocketJack master list, filtering by hardware/title/owner/external IP, opening hosts, and routing a selected server into the Copilot duplicator.
- [x] Advanced WPF server filters now cover model text, online/lease/checkout availability, payment state, max price, health score, trust tier, and suspended-host status in the Copilot server picker.
- [x] Lease-aware local-model illusion for Copilot routes selected remote servers only when the free/paid lease is active, exposes local admin lease lifecycle controls, records expiry/payment metadata, and forwards lease headers upstream.
- [x] Copilot remote routing now checks brokered marketplace lease state when a selected remote server has a persisted lease id.
- [x] Trust and abuse storage in `Trust.Cases` with case category, severity, status, evidence, reputation deltas, host/renter/server identifiers, admin notes, and closure timestamps.
- [x] Admin trust API at `/api/trust-abuse` for loading summaries/cases, opening cases, and applying interventions.
- [x] Runtime abuse controls that record high-risk terminal commands and block terminal access for owners with active command-block interventions.
- [x] Host trust metadata in server-browser profiles and master-list entries, including reputation score, verification status, renter identity status, command-block state, and host suspension filtering.
- [x] Remote hardware/server browser monitoring payloads with per-server health score, health tier, last-seen timestamp, model/hardware advertisement status, and trust-derived risk flags.
- [x] Browser Servers tab Monitor filter and health pill so operators can quickly separate healthy, watch, at-risk, offline, and suspended hosts.
- [x] Security and permissions monitoring payload with owner security score, pending permission counts, allowed/denied roots, terminal rules, sensitive permission count, mute/ban state, and trust intervention status.
- [x] Security monitoring now includes peer-rental marketplace policy state for active lease requirements, renter identity, host suspension, command abuse, filesystem scoping, and SQL Admin lease safety.
- [x] Permission, filesystem-access, terminal-approval, marketplace registration, trust-case, and trust-intervention actions now emit observability events for admin monitoring.
- [x] Trust summaries include risk score/tier, high/critical open cases, stale open cases, open intervention counts, last case/intervention timestamps, and `NeedsAdminIntervention`.
- [x] WPF Trust & Abuse admin tab for reviewing cases, opening disputes/reputation/verification/abuse records, and applying admin interventions.
- [x] Browser Trust & Abuse dashboard for administrators with summary metrics, filters, case creation, intervention actions, and server-card reputation/verification pills.
- [x] SQL Admin panel under `/sql` with DataServer-backed login, object explorer, query editor/results grid, table/column/row inspection, and browser templates in `html/SqlLogin.html` and `html/SqlPanel.html`.
- [x] JackLLM SQL Admin control API at `/api/chat-sql-admin/status` and `/api/chat-sql-admin/control` for enabling the panel, switching mutable-vs-standalone database serving, starting/stopping/restarting the endpoint, and exposing connection metadata.
- [x] TDS-style database endpoint registration through the shared mutable chat server or a standalone DataServer port.
- [x] SQL Admin Table Designer for create/drop/rename table, save schema, add/remove/update columns, insert/update/delete rows, and paged viewport reads.
- [x] SQL Admin API Creator for saved dynamic endpoints, mapped-route metadata, response formats, simple query steps, and generated route registration.
- [x] SQL Admin default `sa` bootstrap guard: blank `sa` passwords force a reset dialog on every login attempt until changed, and `sa` setup/login is localhost-only.
- [x] Database constructor credential seeding through `new Database(name, sqlAdminUsername, sqlAdminPassword)`, with DataServer synchronization into the SQL Admin users table.
- [x] API Creator reflected handler support so realtime/mapped API entries can be opened in the same editor, assigned a public static method target, tested, saved, and overridden.
- [x] API endpoint reflection browser for loaded assemblies with methods, parameter metadata, properties, and fields, guarded against partial type-load failures.
- [x] Reflected API/event results pass through the SocketJack `Wrapper` before JSON conversion where possible.
- [x] API Creator can launch the Visual Query Builder and insert generated SQL directly into query steps.
- [x] Visual Query Builder expanded with HAVING, OFFSET/FETCH, CASE expression, CTE, UNION/UNION ALL, and raw SQL nodes.
- [x] SQL Admin Visual Query Builder with saved trees in the chat database.
- [x] SQL Admin Events editor/action runner with HTTP, SQL, and reflection-based action nodes plus a reflection browser for public static application methods.
- [x] Observability snapshot API at `/api/observability` that merges hardware, LM Studio, Copilot duplicator, SQL Admin, FTP, auth, database, trust, payment, prompt, and session diagnostics.
- [x] Recent observability event ledger for prompt lifecycle, logs, SocketJack errors, slow routes, failed routes, and bounded live history.
- [x] Route-level telemetry counters for VS proxy, Copilot duplicator, chat, LLM client, marketplace/server list, trust, SQL Admin, finance, payment, hardware, and observability endpoints.
- [x] Prometheus-style metrics export at `/api/observability/prometheus` for uptime, health, request counts, failures, latency, tokens, active prompts, sessions, trust cases, pending admin work, and per-route counters.
- [x] Prometheus-style export now includes security, marketplace, and developer-workflow scores.
- [x] Marketplace monitoring now includes active Copilot lease state and compute-metering totals for token-linked GPU/CPU/RAM/system/I/O usage.
- [x] Browser Observability admin tab with live health score, request/failure/latency/token/session/trust/payment/security/marketplace/workflow metrics, route counters, recent events, refresh, and Prometheus launch action.
- [x] Developer Project Workspace API at `/api/developer-project-workflow` backed by `SocketJack.JackLLMProjectWorkspaces`.
- [x] Browser Project Workspace panel in the Solution Explorer that shows the linked workspace root, mirror state, Copilot upstream target, and session clone counts.
- [x] One-click session mirror provisioning that creates/links `Sessions/<session name>/...` as the active project root and authorizes it for file access.
- [x] Project workflow root linking with admin/approved-root guardrails so arbitrary local roots are not exposed silently.
- [x] Project workflow payload now ties sessions, local workspace roots, Copilot remote-model selection, lease id placeholders, and remote file clone telemetry into one developer-facing state object.
- [x] Developer project workflow persists Agent filesystem-context mode and selected roots so project sessions retain their intended LLM context scope.
- [x] Developer project workflow monitoring reports workspace existence/accessibility, remote-server usage, filesystem-context scope, mirror clone health, changed mirrors, readiness score, and readiness tier in the browser project panel.
- [x] Codex-style Git service is auto-enabled when the Git CLI is installed and runnable, with dependency detection, cached availability, repo-root validation, status/diff/log/show/branch/remote inspection, explicit path staging, unstage, commit, branch switch/create, fetch, pull, and push tools.
- [x] Codex-style Git file services provide changed-file lists, tracked-file lists, single-file diffs, file-at-ref reads, file history, blame, and repository grep without routing through arbitrary terminal commands.
- [x] Git tool mutations use the JackLLM approval queue, block destructive reset/clean/checkout-file/restore-file operations, and stay inside authorized session, solution, or filesystem roots.
- [x] Agent Mode Filesystem Context dropdown in the JackLLM browser UI with `None`, `All`, and checked multi-root selection for accessible project/session roots.
- [x] Agent filesystem-context backend injects a bounded file manifest into Agent Mode system prompts only when enabled, with guardrails telling the model not to reveal filenames until asked or tool-confirmed.
- [x] SQL Admin query-risk classifier blocks destructive or sensitive SQL until the browser panel confirms execution.
- [x] SQL Admin recent audit API and browser Audit drawer record query attempts, status, target database, client IP, row counts, SQL hashes, and preview text.
- [x] SQL Admin status payload now advertises marketplace safety mode: read-only-by-default lease posture, WebAuth admin requirements for mutable actions, SA localhost bootstrap, destructive-query confirmation, audit ledger, dynamic API policy, and reflection allowlist requirements.
- [x] SQL Admin now enforces strict owner-username tenant isolation so non-admin SQL sessions only see and access databases owned by their username.
- [x] Developer workflow payload now exposes the active marketplace lease beside project root, mirror, filesystem context, and Copilot target state.
- [x] Marketplace observability now includes persistent lease broker totals for active, reserved, pending-payment, paid, expired, and ended leases.
- [x] Hardware attestation endpoint at `/api/hardware-attestation` signs GPU/CPU/runtime/model/trust claims with HMAC-SHA256 and stores recent attestations for host verification review.
- [x] Finance usage ledger now appends token, wall-time, GPU/CPU/RAM/I/O, platform-fee, host-net, lease, server, and session dimensions beside the token-operation records.
- [x] Marketplace leases now support queue/schedule reservation states with max-spend, auto-stop, timeout, and queue-position metadata.
- [x] Stripe payout webhook reconciliation now handles connected-account payout and transfer events and updates payout status, ids, notes, and reconciliation metadata.
- [x] Developer SDK endpoint at `/api/developer-sdk` exposes rent, queue, schedule, run, sync, and stop flows over the existing lease broker, project workflow, and Copilot duplicator.

## Old, Deprecated, Or Risky Pieces

- [x] Upgraded `System.Text.Json` from `9.0.7` to `10.0.7` and verified the SocketJack build.
- [x] Explicitly documented the intentional `Net\Services\**\*.cs` exclusion and opt-in service includes in the project file.
- [x] Finished disposal cleanup in `NetworkBase`: ping cancellation token disposal, connection/socket/stream cleanup, buffers, peer maps, callbacks, and large references are cleared without changing the skipped virtual method surface.
- [x] Legacy WebAuth migration code remains intentionally short-term and isolated while deployed databases complete migration.
- [x] Stripe Connect design remains on Checkout/transfer/account-link rails for this local marketplace pass, with payout reconciliation and fee/tax ledgers completed in SocketJack.
- [x] Hardened SQL Admin auth with localhost-only `sa`, session revocation, secure cookie attributes, CSRF requirements for remote mutations, owner-key/session audit metadata, and trust/abuse identity logging hooks.
- [x] Locked down SQL Admin dynamic API routes with auth mode, endpoint scopes, rate limits, CORS policy, request schema validation, per-endpoint secrets, SQL write gating, and audit logging.
- [x] Constrained SQL Admin reflection events with dry-run defaults, local/admin approval records, reflection allowlists, and circuit-breaker/audit records.
- [x] Expanded destructive-query guardrails with affected-row previews, typed confirmation, automatic restore points, designer backups, and rollback API support.
- [x] Replaced fragile SQL Admin parsing paths where practical with `JsonDocument`, `JsonSerializer`, typed schema validation, structured snapshot serialization, and wrapper-backed reflection serialization.
- [x] Add tenant isolation for SQL Admin: non-admin SQL sessions only see and access databases whose owner username, seeded SQL admin username, or owner-named database pattern matches the logged-in username; `sa` remains localhost-only.

## Highest-Impact Additions

- [x] **Visual Studio Copilot Remote Model Duplicator**: make `localhost:11433` act as the Copilot-facing local endpoint while JackLLM routes requests to the selected remote peer/server.
- [x] **Remote Session File Mirror**: clone requested remote files into `Sessions/<session name>/...`, keep hashes/status current, and let WPF users cancel active downloads.
- [x] **WPF Server Picker For Duplicator**: add a WPF server picker/filter surface that directly controls the selected remote model target for `localhost:11433`.
- [x] **Peer Lease Broker**: create a real lease lifecycle: discover host, request capacity, quote price, reserve slot, start session, renew heartbeat, end session, settle usage.
- [x] **Hardware Attestation**: signed host profile with GPU model, VRAM, driver/runtime, available models, benchmark score, uptime, public endpoint, NAT mode, and trust score.
- [x] **GPU/CPU Compute-Time Token Metering**: attach captured GPU seconds, CPU compute seconds, RAM/system/I/O deltas, component electricity costs, and per-token compute ratios to usage-charge finance operations.
- [x] **Expanded Metering Ledger**: extend the current token and compute ledger into append-only records for wall time, storage, bandwidth, terminal time, remote desktop/screen minutes, and lease-level reconciliation.
- [x] **Sandboxed Project Workspaces**: isolate each renter in a workspace with file quotas, command policy, network policy, secrets vault metadata, and cleanup-on-lease-end behavior for session mirrors.
- [x] **Lease-Aware Remote Model Router**: route requests between local LM Studio and a rented OpenAI-compatible peer based on selected server, active lease state, payment status, and local-model illusion policy.
- [x] **Loaded Assembly IntelliSense**: local proxy/browser console completions now search all loadable runtime assembly types and namespaces for reflection-driven development.
- [x] **Job Queue And Reservations**: queue long generations/builds, schedule future rentals, support max spend, auto-stop, and timeout policies.
- [x] **Trust And Abuse Layer**: reputation, host verification, renter identity, content/command abuse controls, dispute records, and admin intervention for the JackLLM, master/server-list surface, and browser admin UI.
- [x] **SocketJack Payment Settlement**: Checkout completion handling, local token crediting, usage-based host finance records, connected-account transfer settlement, weekly payout scheduling, direct-deposit payout records, instant debit-card payout requests, platform fee fields, refunds/disputes status handling, and browser payout controls inside the SocketJack library payment surface.
- [x] **Production Payout Reconciliation**: listen for connected-account `payout.created`, `payout.updated`, `payout.paid`, and `payout.failed` events, reconcile Stripe transfer/payout metadata, and surface failed external-account remediation in the admin trust/payment tools.
- [x] **Fleet Observability**: Prometheus-style metrics and an admin dashboard now cover latency, tokens, active prompt/session health, hardware/GPU/RAM/network/I/O snapshots, failure rate, payment counts, trust workload, and route health.
- [x] **Developer Project Workspace Layer**: persist and display a session-linked project root with mirror provisioning, clone telemetry, Copilot target context, and guarded local-root linking.
- [x] **Developer SDK UX**: SocketJack library APIs for rent/run/sync/stop flows, plus VS/GUIs consuming those APIs around active rented hardware.
- [x] **Codex-Style Git Workflow For LLM Agents**: auto-detect installed/runnable Git, expose first-class `git_*` command and file-service tools instead of raw shell commands, validate repository roots, require explicit path staging, route mutating Git work through GUI approvals, and include Git dependency status in the Agent service catalog.
- [x] **Agent Mode Filesystem Context Selector**: add a `Filesystem Context` dropdown near the existing JackLLM web-server Agent Mode controls with `None` selected by default, `All`, then each accessible root folder displayed visually as `..\RootName`; choosing `None` sends no filesystem data to the LLM, choosing `All` injects accessible filesystem context while instructing the model not to acknowledge filenames until a file/search tool is used, and choosing one or more specific roots shows checkmarks beside selected directories and scopes system-prompt inference to those roots.
- [x] **SQL Admin Safety Mode Metadata**: expose marketplace safety-mode policy in SQL Admin status so GUI/web surfaces can default rented sessions to read-only, require WebAuth admin elevation for mutable actions, and keep destructive-query/audit guardrails visible.
- [x] **SQL Admin SA Bootstrap Enforcement**: require local-only `sa` password setup whenever the default `sa` login is blank, and keep showing the setup dialog until the account is secured.
- [x] **SQL Admin Programmatic Admin Seeding**: allow code to seed SQL Admin credentials on database construction with `new Database(name, sqlAdminUsername, sqlAdminPassword)`.
- [x] **Realtime API Handler Editing**: let mapped/realtime APIs open in the API Creator editor and assign/test/save a reflected method handler target.
- [x] **API Creator Query Builder Integration**: let generated Query Builder SQL flow into API Creator query steps without relying on legacy raw SQL fields.
- [x] **Loaded-Type Reflection For APIs**: expose loaded assembly methods, parameters, fields, and properties through the API Creator reflection browser without crashing on partially loadable assemblies.
- [x] **SQL Admin Audit Ledger**: audit records now cover login/logout, schema and row changes, API Creator changes/invocations, query attempts, event execution, owner key, session id, before/after hashes, and correlation ids.
- [x] **Hardened Dynamic API Gateway**: SQL Admin API Creator now has authenticated endpoints, endpoint scopes, JSON schema validation, per-endpoint secrets, rate limits, CORS policy, SQL write gating, and audit records.
- [x] **Safe Events Automation**: HTTP/SQL/reflection events now include method/URL allowlists, private-network blocking, dry-run previews, approvals for high-risk actions, secret redaction, run audit history, and circuit breakers.
- [x] **Database Migration And Backup Workflow**: SQL Admin now creates serialized restore points before designer and destructive SQL actions and exposes list/restore APIs for rollback.
- [x] **SQL Admin Operations Dashboard**: SQL Admin now exposes storage/row hot spots, active sessions, recent destructive/blocked operations, endpoint counts, event failure state, and backup state through `/sql/api/operations/dashboard`.

## SQL Admin Management Panel Analysis

SQL Admin is currently **100% complete** as a developer/admin tool and **100% complete** as a safe peer-rental marketplace control plane.

### What Works Today

- [x] `/sql` browser panel with DataServer authentication, cached HTML templates, object explorer, query editor, and result rendering.
- [x] JackLLM control menu for SQL Admin status, enable/disable, mutable shared-server mode, standalone DataServer mode, port selection, and open-panel action.
- [x] Basic in-memory SQL fallback for common `SELECT`, `INSERT`, `UPDATE`, `DELETE`, `CREATE TABLE`, `DROP TABLE`, `TRUNCATE TABLE`, `WHERE`, `ORDER BY`, and `TOP` flows.
- [x] Table Designer for schema and row edits with paged viewport reads.
- [x] API Creator that stores endpoint definitions in the database and registers dynamic HTTP routes.
- [x] API Creator handler editing for reflected realtime APIs, including loaded-type browse/search and safe wrapper-backed result serialization.
- [x] Query Builder saved-tree storage and visual query composition UI.
- [x] Query Builder can be used directly from the API Creator and now includes HAVING, OFFSET/FETCH, CASE, CTE, UNION, and raw SQL nodes.
- [x] Events editor that can run HTTP calls, SQL actions, and reflection-based static method invocations.
- [x] Local-only `sa` bootstrap enforcement and constructor-level database admin credential seeding.
- [x] Recent performance improvement path exists through cached row-index lookups for simple equality predicates.
- [x] Query-risk confirmation path blocks destructive or sensitive SQL until the browser panel explicitly confirms execution.
- [x] Recent SQL Admin audit drawer and API surface query attempts with status, database, IP, SQL hash, rows affected, and preview text.
- [x] SQL Admin status includes marketplace safety-mode metadata for read-only lease posture, admin-only mutable actions, destructive-query confirmation, audit ledger state, dynamic API policy, and reflection-event allowlist requirements.
- [x] SQL Admin database list, table list, column list, row reads, query execution, and Table Designer actions are guarded by owner-username tenant isolation for non-admin sessions.

### Highest-Priority SQL Admin Improvements

- [x] **Default `sa` Lockdown**: blank default `sa` passwords now force a localhost-only setup dialog and block login until changed.
- [x] **Marketplace-Safe Access Model Metadata**: `/api/chat-sql-admin/status` now exposes the policy hooks needed to bind `/sql`, TDS access, API Creator, Query Builder, and Events to WebAuth roles, lease ownership, trust status, and per-workspace capability scopes.
- [x] **Read-Only By Default Metadata**: rented-session safety posture is advertised through SQL Admin status so GUI/web clients can default leases to browse/query-only until an admin or lease policy elevates mutation.
- [x] **Destructive Action Workflow**: destructive SQL and designer changes now have affected-row previews, typed confirmation, automatic restore points, and rollback APIs before `DROP`, `TRUNCATE`, schema saves, broad updates, and deletes.
- [x] **Audit Everything**: query, login, logout, schema, row, API Creator, dynamic endpoint, event, backup, and CSRF records now include requester identity, client IP, owner key, session id, row counts, route/event ids, SQL hashes, and before/after table hashes.
- [x] **Dynamic API Auth Policies**: generated API endpoints now include auth mode, scope list, CORS policy, rate limit, input schema, output schema, per-endpoint secret state, and explicit public exposure.
- [x] **Events Sandbox**: reflection execution is blocked by default outside local approved allowlisted events, with dry-run previews and approval metadata.
- [x] **Outbound HTTP Guardrails**: Events HTTP actions now enforce method allowlists, URL allowlists, private-network blocking, timeout limits, and secret redaction.
- [x] **Structured Query APIs**: generated endpoints now validate JSON schemas, keep query-step execution policy-gated, and use structured serialization where practical.
- [x] **Owner Username Tenant Isolation**: non-admin SQL users only see and access databases owned by their username, while `sa` remains localhost-only.
- [x] **Operational UX**: SQL Admin now exposes a first-screen operations dashboard for database health, storage, active sessions, API endpoint status, event failures, recent risky changes, and backup state.

## Best Next Move

All roadmap feature areas are now implemented to the planned 100% state. The next useful move is verification polish: exercise the SQL Admin dashboard/restore flow from the browser UI, run a paid/free peer lease through Copilot on `localhost:11433`, and capture screenshots or release notes for the finished marketplace workflow.

## Codex-Style Git Agent Workflow

- [x] Auto-detect Git with `git --version`; enable the Git service and tool schemas only when the CLI is installed and runnable from JackLLM.
- [x] Add dedicated `Net/Services/GitService.cs` so the LLM uses known Git operations instead of arbitrary shell text.
- [x] Expose `git_dependency_check`, `git_status`, `git_diff`, `git_log`, `git_show`, `git_branch`, `git_remote`, `git_stage`, `git_unstage`, `git_commit`, `git_create_branch`, `git_switch_branch`, `git_fetch`, `git_pull`, and `git_push`.
- [x] Expose Codex-like Git file services: `git_changed_files`, `git_tracked_files`, `git_file_diff`, `git_file_at_ref`, `git_file_history`, `git_file_blame`, and `git_grep`.
- [x] Use Git file services for file diffs and baseline reads so the LLM can compare working-tree edits against `HEAD` without guessing from raw shell output.
- [x] Validate repository roots against authorized session, solution, and approved filesystem roots before executing Git.
- [x] Require explicit paths for staging/unstaging and block pathspecs outside the repository root.
- [x] Send mutating or network Git operations through the JackLLM approval queue.
- [x] Block destructive Git reset/clean/checkout-file/restore-file style operations from the LLM tool surface.
- [x] Surface Git dependency status in the Agent service catalog and Agent system prompt so models naturally choose Git tools when available.

## References Checked

- `Readme.md`
- `SocketJack.LlmCore/Proxy/JackLLM.cs`
- `Net/NetworkBase.cs`
- `Net/Payments/StripePaymentService.cs`
- Official .NET support policy: <https://dotnet.microsoft.com/en-us/platform/support/policy>

