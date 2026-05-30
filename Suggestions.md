# Suggestions

Generated from a local scan on 2026-05-15.

Checks run:

- `dotnet build SocketJack/SocketJack.csproj --no-restore -v:minimal` passed.
- `dotnet build SocketJack/SocketJack.csproj --no-restore -v:minimal /p:WarningLevel=4` passed.
- `dotnet list SocketJack/SocketJack.csproj package --vulnerable --include-transitive` reported no vulnerable packages from the configured NuGet sources.
- `dotnet test SocketJack.Tests/SocketJack.Tests.csproj --no-restore -v:minimal` ran 67 tests: 66 passed, 1 failed.

## High-priority bug fixes

### Fix the failing workflow GUI test

`SocketJack.Tests/SockJackDmlWorkflowEndpointTests.cs` still reads:

- `SocketJack/Resources/SockJackDml.html`
- `SocketJack/Resources/JackLLMWebChat.html`

Those files now live under `SocketJack/html/`, and the project embeds `html/*.html` through `SocketJack/HtmlPageResources.cs`. Update the test to read `html`, or better, test through `HtmlPageResources.TryGetHtml(...)` so future file moves do not break the test.

### Enforce `NetworkOptions.MaximumBufferSize`

`SocketJack/Net/NetworkConnection.cs` reads the message length header and allocates `new byte[Length]` before checking it against `NetworkOptions.MaximumBufferSize`. A malformed or hostile client can request a huge allocation and cause memory pressure or an out-of-memory crash.

Suggested fix: reject lengths less than zero or greater than `MaximumBufferSize` before allocating the body array, close the connection, and record an endpoint security event.

### Make the serialization whitelist/blacklist actually block types

`SocketJack/Serialization/Wrapper.cs` has `IsTypeAllowed(Type, ISocket)` checks, but for most non-`object` types it returns `true` even when the type is missing from the whitelist or appears in the blacklist. That makes the allowlist much weaker than the API implies.

Suggested fix: return `false` or throw for any blacklisted type, and for any non-whitelisted type unless an explicit compatibility option allows legacy permissive behavior.

### Correct endpoint security profile ordering

`SocketJack/Net/EndpointSecurity.cs` labels the default profile as `Firm`, but the default values are often more permissive than `Loose`:

- `AdministrativeRequestsPerMinute`: Loose `180`, Firm `240`, Strict `45`
- `GeneralRequestsPerMinute`: Loose `600`, Firm `900`, Strict `120`
- `SocketJackFramesPerMinute`: Loose `7200`, Firm `12000`, Strict `1800`

Suggested fix: either rename the current default to something like `Permissive`, or rebalance the values so `Loose >= Firm >= Strict` in permissiveness.

### Do not trust every HTTPS upstream certificate in host proxying

`SocketJack/Net/HttpServer.cs` creates an `SslStream` for proxy targets with a certificate callback that always returns `true`. That defeats TLS verification for HTTPS proxy targets.

Suggested fix: verify certificates by default, expose an explicit `AllowInvalidUpstreamCertificate` option for development, and log when it is enabled.

### Avoid process mutation and self-restart from a library

`SocketJack/ThreadManager.cs` has `ModifyRuntimeConfig()` that edits the entry assembly runtimeconfig file, starts a replacement process, and kills the current process. That is surprising and risky behavior for a networking package consumed by other apps.

Suggested fix: move this to an explicit host/CLI tool, make it opt-in, and document exactly when it restarts the app.

## Security hardening

### Change default CORS behavior

`HttpServer` adds `Access-Control-Allow-Origin: *` to responses when the handler did not set one. That is convenient for demos, but it is too broad for admin, filesystem, SQL, and cookie-backed surfaces.

Suggested fix: default to no CORS header, or add `NetworkOptions.HttpCorsPolicy` with safe defaults and per-route overrides.

### Strengthen SQL Admin cookie security detection

`SqlAdminPanel.BuildSqlAdminCookie(...)` adds `Secure` only when request headers say `X-Forwarded-Proto: https` or similar. Direct TLS via `Options.UseSsl` is not considered, and untrusted clients can spoof forwarded headers unless the server is explicitly behind a trusted proxy.

Suggested fix: base `Secure` on the actual listener TLS state, and only honor forwarded scheme headers when a trusted-proxy option is enabled.

### Hash stored SQL passwords

`DataServer.Users` and the `Users` table store raw passwords. Payload encryption helps at rest, but it does not protect passwords after load, in logs, memory dumps, or accidental table exposure.

Suggested fix: store password hashes with per-user salts and a modern password KDF. Keep a migration path for existing JSON persistence.

### Revisit terminal execution defaults

`Net/Services/TerminalService.cs` intentionally runs user-approved shell commands, but it also uses PowerShell `-ExecutionPolicy Bypass` and broad working-directory fallback behavior.

Suggested fix: require an allowed-root list, log command approvals with cwd and duration, allow environment scrubbing, and reserve `ExecutionPolicy Bypass` for an explicit option.

### Treat `TrustForwardedForHeaders` as a proxy trust boundary

`EndpointSecurityMonitor` can use `X-Forwarded-For` and `X-Real-IP`. That is only safe behind a known reverse proxy that overwrites those headers.

Suggested fix: accept forwarded IP headers only from configured trusted proxy IPs.

## Reliability and performance

### Stream static files and directory ZIPs instead of buffering everything

`HttpServer.TryResolveDirectoryFile(...)` uses `File.ReadAllBytes(...)`, and `CreateDirectoryZipBytes(...)` builds the full ZIP in memory before responding. This can become expensive for large downloads.

Suggested fix: return `FileResponse.FromFile(...)` for mapped files, stream ZIP creation to the response stream, and add optional size limits for directory ZIP downloads.

### Remove forced garbage collections from normal disconnect paths

`TcpClient.Disconnect()` and `TcpServer.StopListening()` call `GC.Collect()` and `GC.WaitForPendingFinalizers()`. For a library, this can pause the host process and hurt throughput.

Suggested fix: rely on deterministic disposal and let the runtime schedule GC. Keep manual GC only behind a diagnostic/debug option.

### Add backpressure to send queues

`NetworkConnection.SendQueueRaw` can grow without a clear per-connection cap. A slow client or a broadcast spike can cause memory growth.

Suggested fix: add max queued bytes, reject or drop according to policy, and expose queue length metrics.

### Make IPv6 a first-class transport path

`TcpClient` and `TcpServer` use `AddressFamily.InterNetwork`, and DNS resolution filters to IPv4. This blocks IPv6-only networks and dual-stack server binding.

Suggested fix: add `AddressFamily` or dual-mode socket options, test IPv4, IPv6, and localhost resolution paths.

### Use cancellation tokens in long-running loops

Many loops depend on `Closed`, `Socket.Connected`, or background task lifetimes. A shared cancellation-token pattern would make shutdown more predictable and easier to test.

Suggested fix: give each connection/server a linked `CancellationTokenSource` and pass it through receive, send, ping, and HTTP streaming loops.

## Project and packaging cleanup

### Simplify `SocketJack.csproj`

`SocketJack.csproj` currently targets only `netstandard2.1`, but it contains many conditional `WarningLevel` and define blocks for inactive `net6.0`, `net8.0`, `net9.0`, Windows, browser, iOS, macOS, and tvOS TFMs.

Suggested fix: remove stale target blocks or reintroduce explicit target frameworks. This will make packaging intent easier to understand.

### Re-enable useful compiler warnings

The project sets `WarningLevel` to `0` in many configurations. Even though the current strict build did not surface warnings from the command line, keeping warning level zero in the project makes future issues easier to miss.

Suggested fix: use the default warning level or `WarningLevel=4`, then add narrow `NoWarn` entries only where there is a known reason.

### Fix package metadata drift

The package description says it targets `.NET Standard 2.1, .NET 8`, while the project currently targets only `netstandard2.1`.

Suggested fix: align the description, README package table, and actual `TargetFrameworks`.

### Move service namespaces out of `EasyYoloOcr.Example.Wpf.Services`

`SocketJack/Net/Services/TerminalService.cs` and `SocketJack/Net/Services/GitService.cs` are included in the SocketJack package but use the namespace `EasyYoloOcr.Example.Wpf.Services`.

Suggested fix: move them to a SocketJack namespace, or split them into a companion/agent package if they are not core networking APIs.

### Do not generate NuGet packages on every build by default

`GeneratePackageOnBuild` is enabled. That can slow normal development builds and produce package artifacts when the user only wanted compile/test feedback.

Suggested fix: pack only in Release or CI, or gate packing behind a property such as `/p:BuildPackage=true`.

## Testing suggestions

### Add security regression tests

Add focused tests for:

- rejecting oversized SocketJack length headers before allocation
- whitelist/blacklist enforcement for non-whitelisted user types
- blocked `.htaccess`, `.env`, `.git`, and path traversal static-file requests
- HTTPS proxy certificate validation failure
- CORS defaults on SQL/admin routes

### Add protocol fuzz tests

`MutableTcpServer` now multiplexes HTTP, WebSocket, SocketJack, RTMP, TDS, and custom protocols. It would benefit from fuzzing the first 32 to 512 bytes of a connection to verify that unknown probes are rejected without exceptions or leaked resources.

### Add lifecycle stress tests

Create tests that rapidly connect/disconnect many TCP, WebSocket, and HTTP clients while broadcasting messages. Watch for leaked tasks, queue growth, false disconnects, and stuck pending accepts.

### Add package validation tests

Add CI checks that pack the NuGet package and validate:

- expected embedded HTML resources exist
- README and icon are included
- no log files, probe HTML, `bin`, `obj`, or local artifacts are packed
- public APIs have XML docs where appropriate

## Feature ideas

### Route explorer and generated API docs

`HttpServer` already tracks mapped route metadata. Expose an optional `/__socketjack/routes` or generated OpenAPI-like document for HTTP routes, body schemas, response examples, and required auth.

### Per-route middleware

Add reusable middleware for auth, CORS, rate limits, request logging, body-size limits, and exception handling. This would reduce custom checks scattered across admin and dynamic API surfaces.

### Built-in metrics endpoint

Expose connection counts, bytes/sec, total bytes, protocol mix, queue depth, endpoint security decisions, and active streams as JSON. This would be useful for WPF dashboards and headless servers.

### Safer file-server features

Add optional ETag/range request support, max file size, max ZIP size, directory ZIP streaming, and configurable hidden-file rules.

### TLS and certificate helpers

Add first-class APIs for loading certificates from PFX, Windows cert store, PEM files, and SNI maps. Include diagnostics when a cert has no private key or no matching host name.

### Typed client generation

Use route metadata to generate small C# or TypeScript clients for mapped HTTP APIs and WebSocket messages.

### Connection policy presets

Offer presets such as `LocalDevelopment`, `PublicHttp`, `PublicAdmin`, `GameRealtime`, and `LanOnly`. Each could configure CORS, endpoint security, host allowlists, TLS requirements, compression, and queue limits.

### Better operational logs

Add structured JSON logs for connect, disconnect, request, response, rate-limit, auth, proxy, and serialization errors, with correlation IDs across HTTP, WebSocket, and SocketJack frames.

## LLM-related project suggestions

This pass covered `LlmRuntime`, `LlmRuntime.Tests`, `LlmRuntime.Wpf`, `LlmRuntime.VisualStudio`, `JackONNX`, `JackONNX.Tests`, `JackLLM`, `JackLLMCompanion`, `SocketJack.LlmCore`, `SocketJack-MagicMasterList`, and `SocketJack/JackLLM.Android`. Updater, installer, and publisher projects were intentionally skipped except where a reviewed app directly depends on them.

Additional checks run:

- `dotnet test LlmRuntime.Tests/LlmRuntime.Tests.csproj --no-restore -v:minimal` passed 79 tests.
- `dotnet test JackONNX.Tests/JackONNX.Tests.csproj --no-restore -v:minimal` passed 9 tests and skipped 2 native provider smoke tests because DirectML/CUDA native runtimes were not available.
- `dotnet build SocketJack.LlmCore/SocketJack.LlmCore.csproj --no-restore -v:minimal` passed.
- `dotnet build LlmRuntime.Wpf/LlmRuntime.Wpf.csproj --no-restore -v:minimal` passed.
- `dotnet build JackLLMCompanion/JackLLMCompanion.csproj --no-restore -v:minimal` passed.
- `dotnet build SocketJack-MagicMasterList/SocketJack-MagicMasterList.csproj --no-restore -v:minimal` passed.
- `dotnet build JackLLM/JackLLM.csproj --no-restore -v:minimal` passed.
- `dotnet build LlmRuntime.VisualStudio/LlmRuntime.VisualStudio.csproj --no-restore -v:minimal` passed and produced the VSIX output.
- `dotnet build SocketJack/JackLLM.Android/JackLLM.Android.csproj --no-restore -v:minimal` failed under the installed .NET 10 SDK because `net8.0-android` is out of support, and Android/MAUI types did not resolve.
- Vulnerability scans reported no vulnerable packages for `LlmRuntime`, `JackONNX`, `JackLLM`, `JackLLMCompanion`, `LlmRuntime.Wpf`, `LlmRuntime.VisualStudio`, `SocketJack.LlmCore`, and `SocketJack-MagicMasterList`. `JackLLM.Android` could not complete the vulnerable-package command because the unsupported Android workload error stops evaluation.

## LLM high-priority bug fixes

### Repair `JackLLM.Android` build and support baseline

`SocketJack/JackLLM.Android/JackLLM.Android.csproj` targets `net8.0-android` while referencing MAUI `10.0.60` packages. Under the installed .NET 10 SDK, the build fails with `NETSDK1202`, then fails to resolve Android and MAUI types such as `ActivityAttribute`, `MauiAppCompatActivity`, and `MauiApplication`.

Suggested fix: either retarget the app to a supported Android TFM and matching MAUI package set, or add a `global.json` and documented workload install path that pins a supported SDK for this project. Also move `android:usesCleartextTraffic="true"` and `UsesCleartextTraffic = true` behind a debug-only or localhost-only network-security config.

### Align JackONNX ONNX Runtime provider versions

`JackONNX/JackONNX.csproj` references `Microsoft.ML.OnnxRuntime` `1.26.0`, `Microsoft.ML.OnnxRuntime.Gpu` `1.26.0`, and `Microsoft.ML.OnnxRuntime.DirectML` `1.24.4`. It also copies native DLLs from hardcoded NuGet package paths.

Suggested fix: align ONNX Runtime package versions, centralize native provider resolution, and add CI tests that fail if the DirectML/CUDA/CPU provider package versions or copied native DLL versions drift.

### Gate Companion process control routes

`JackLLMCompanion/CompanionHttpHost.cs` binds to loopback and has a local-only request gate, but `/api/companion/processes/kill` and `/api/companion/processes/start` call directly into `CompanionProcessService` without a separate capability check. Any local process or browser page that can reach the loopback port can attempt process start/kill requests.

Suggested fix: require an unguessable per-run token, validate `Origin`/CSRF for browser requests, and add a distinct approval gate for process start/kill. Prefer binding a high random loopback port instead of trying port 80 first.

### Put hard size limits on Companion file sharing

`CompanionRepository.SaveSharedUpload(...)` decodes the full base64 upload into memory before writing it, and `ShareExistingPath(...)` copies up to 500 files with no total byte cap. `RegisterFile(...)` also records arbitrary paths through the HTTP API.

Suggested fix: require `UseFiles` for every file-register/share/download path, stream uploads instead of base64 payloads, add per-file and total-byte limits, and show the total copy size before approving a folder share.

### Make JackONNX Python bootstrap explicitly opt-in

`JackOnnxPythonDiffusersImageRunner` can download Python, `get-pip.py`, and install or upgrade `torch`, `transformers`, and `diffusers`; it can also force-reinstall CUDA PyTorch from an index URL. That is powerful, but surprising for an image-generation call path.

Suggested fix: require explicit user/admin consent before any Python, pip, or package mutation; verify downloaded installer/archive hashes or signatures; record exactly which packages were installed; and provide a dry-run/status command.

### Remove chat tab from JackLLM Workstation WPF project

The JackLLM Workstation WPF shell still exposes a `Chat` tab even though chat is now available through the newer workstation flow and web chat surfaces. Keeping the duplicate tab makes navigation noisier and increases the chance that stale chat UI code remains wired into runtime behavior.

Suggested fix: remove the `Chat` tab from `JackLLM/MainWindow.xaml` and its associated event handlers/view-state code in `JackLLM/MainWindow.xaml.cs`. Verify startup, provider selection, model management, and any web chat/admin entry points still work without the tab.

### Fix clipped right-side tabs in JackLLM Workstation

The JackLLM Workstation WPF tab strip is visually clipped/cramped at the right side of the UI. In the attached screenshot, the tab row runs through `Diagnostics`, `Chat`, `Copilot Files`, `Servers`, `Rent My PC`, `Models`, `Trust & Abuse`, and `Server Management`, with the right-side tabs pressed against the container edge.

Suggested fix: inspect the tab strip/control template and parent layout constraints, including padding, margins, clipping, min widths, scroll behavior, and DPI scaling. The active/inactive tab labels and borders should render fully at common window sizes without being cut off.

### Polish JackLLM Workstation web chat adaptive theming

In the JackLLM Workstation web chat UI, buttons should change color with the animated background. Panels should also change with the background color, animate in sync with the background, and render with `66%` transparency so the animated theme remains visible behind them.

Suggested fix: move the background animation colors into shared CSS variables, derive button and panel colors from those variables, and apply synchronized transitions/animations to panel backgrounds. Verify contrast, hover/focus states, readability, and reduced-motion behavior.

### Fix LlmRuntime decoding errors and truncated chat output

Chat output can fail with a decoding error and return truncated text. The runtime was partially decoding responses, but an error path still appears to cut off generated text before the message is complete.

Suggested fix: audit the LlmRuntime chat completion streaming and non-streaming decode paths, including tokenizer detokenization, UTF-8/SSE chunk handling, partial multibyte characters, stop-sequence handling, and exception recovery. Add a regression test that feeds fragmented output and verifies the final chat text is complete.

### Add JackLLM Workstation startup loading window and async startup pipeline

JackLLM Workstation should show a dedicated database/loading window while startup services initialize. Database loading and everything JackLLM uses on startup, including SocketJack, LlmRuntime, JackONNX, and Python asset preparation, should run asynchronously so the WPF shell does not freeze on load.

Suggested fix: add a centered borderless startup loading window with minimize and close controls, require confirmation before canceling startup, show app startup/database/runtime/Python asset progress in one place, and report progress through cancellation-aware async initialization paths.

## LLM security hardening

### Require HTTPS and checksum verification for model downloads

The Hugging Face UI accepts both `https://huggingface.co/` and `http://huggingface.co/`, and `ModelDownloadService` accepts any model-looking URL. It computes SHA-256 after download, but it does not verify that hash against a trusted manifest.

Suggested fix: require HTTPS when cookies or bearer tokens are present, add host allowlists for authenticated downloads, pin Hugging Face revisions instead of defaulting to moving branches, and verify model files against expected SHA-256 values from a signed or user-confirmed manifest.

### Strengthen local tool secret storage

`EncryptedLocalToolSecretStore` uses DPAPI on Windows, but falls back to AES-CBC with a local key file beside the encrypted JSON on other platforms. AES-CBC is not authenticated, and hidden files are not a security boundary.

Suggested fix: use authenticated encryption such as AES-GCM or Encrypt-then-MAC, apply OS file permissions/ACLs, and offer Windows Credential Manager, Keychain, libsecret, or a vault-backed `ILlmToolSecretStore`.

### Harden LLM tool execution defaults

`LlmToolInvoker` can run executable and PowerShell tools, including PowerShell with `-ExecutionPolicy Bypass`. The safety policy checks approval and declared permissions, but the most dangerous tool class deserves defense in depth.

Suggested fix: keep shell tools disabled by default, require an executable allowlist or signed tool package, capture cwd/env/exit code/duration in audit logs, and add timeout handling that kills the entire process tree.

### Redact secrets beyond exact value replacement

`LlmToolRedactor` redacts exact sensitive values, but it does not catch common secret shapes unless the secret was already known. Companion training has regex-based redaction, but screenshots and event summaries can still preserve sensitive context.

Suggested fix: add pattern-based redaction for common key/token/password formats, scrub URLs and headers before audit persistence, and add tests proving tool output, HTTP errors, training drafts, and replay metadata do not leak secrets.

### Add auth to loopback LLM and Companion control surfaces

Loopback binding is helpful, but browser-based local services are still reachable by other local apps and by web pages through CSRF-style POSTs. The Companion workspace, LlmRuntime OpenAI-compatible host, desktop input stream, file sharing, and process control endpoints should not rely on loopback alone.

Suggested fix: issue a random runtime token, require it on mutating routes and WebSocket/stream endpoints, reject unexpected `Origin` headers, and expose a visible "copy local URL" that includes the token only when needed.

## LLM reliability and maintainability

### Split the largest orchestration files

`SocketJack.LlmCore/Proxy/JackLLM.cs` is about 38,937 lines and 2.1 MB. `JackLLM/MainWindow.xaml.cs` is about 13,892 lines and 764 KB. These files mix UI, routing, model proxying, usage accounting, sessions, payments, remote control, and tool orchestration.

Suggested fix: split by ownership: OpenAI-compatible proxy, chat sessions, file/session storage, trust/usage accounting, remote model leasing, UI view models, and WPF event handlers. Add narrow tests around each extracted service.

### Re-enable nullability pressure in LLM projects

`JackLLM.csproj` suppresses many nullable warnings, and `SocketJack.LlmCore` has nullable disabled. These projects handle prompts, secrets, files, sessions, payment-adjacent data, and remote execution, so null-handling bugs can become security or data-loss bugs.

Suggested fix: enable nullable in new files first, then retire broad `NoWarn` entries by folder. Keep suppressions local with comments where the code has a real interop or framework reason.

### Pin VSIX package versions

`LlmRuntime.VisualStudio.csproj` uses `Microsoft.VisualStudio.SDK` and `Microsoft.VSSDK.BuildTools` version `17.*`. Wildcard versions make VSIX builds less reproducible.

Suggested fix: pin exact versions, update intentionally, and add a CI job that builds the VSIX from a clean restore.

### Decouple JackLLM from updater build artifacts

`JackLLM.csproj` has a `ProjectReference` to `JackLLMUpdater` with `ReferenceOutputAssembly="false"` and a copy target. Even though updater projects are out of scope for this pass, building the main app currently pulls updater output into the build flow.

Suggested fix: gate updater-copy behavior behind a release/publish property so normal app builds and tests do not depend on updater artifacts.

### Add lifecycle cancellation to model and desktop operations

There is good cancellation coverage in places, but several UI and companion paths still rely on synchronous waits, `Thread.Sleep`, full-memory buffers, or long operations kicked off from request handlers.

Suggested fix: pass cancellation tokens through model discovery, desktop streaming, training, folder copy, Python provisioning, process launch, and model conversion. Prefer async wait/delay and bounded channels for long-running work.

## LLM testing suggestions

### Add provider matrix tests

Keep the current lightweight JackONNX tests, but add optional CI lanes for CPU-only, DirectML, and CUDA runners. Verify that provider probing, native DLL resolution, and a tiny identity ONNX model work with the package versions actually shipped.

### Add Companion permission regression tests

Test that desktop input, process start/kill, process browsing, file upload/register/download, training capture, and shared-path copy all require the intended approval gate and fail without the runtime token.

### Add model download safety tests

Add tests for HTTPS-only authenticated downloads, path traversal in bundle source paths, resume behavior after partial downloads, maximum file/folder size limits, checksum mismatch handling, and HTML/error-page rejection.

### Add tool safety and redaction tests

Cover shell tools, HTTP tools, named pipes, required secrets, allowed-project scopes, `AskEveryTime`, `AskOnDestructiveOperations`, audit persistence, exact secret redaction, and regex-based secret redaction.

### Add prompt and tool-call regression suites

Store small representative prompts and expected tool-call decisions for JackLLM/LlmRuntime. Run them against a deterministic mock model so parser, schema, approval, and continuation behavior can be tested without depending on a live model.

## LLM feature ideas

### Unified local model registry

Create one registry view for GGUF, ONNX, safetensors, Hugging Face bundles, LM Studio models, and OpenAI-compatible remote models. Show format, provider support, VRAM/RAM estimate, context length, license, checksum, and last health-check result.

### Provider health dashboard

Add a JackONNX/LlmRuntime diagnostics panel for CPU, CUDA, Vulkan, DirectML, and Python Diffusers providers. Include installed runtime versions, native DLL paths, GPU memory, test result, and recommended next action.

### Align SocketJack.com MasterList with the landing page theme

Update the `SocketJack.com/MasterList` experience so it uses the same visual style, spacing, colors, typography, navigation treatment, and component language as the main `SocketJack.com` landing page. The MasterList should feel like part of the same site rather than a separate utility surface.

Suggested fix: identify the landing page's shared design tokens/components and apply them to `SocketJack-MagicMasterList` or the MasterList route. Verify desktop and mobile layouts, especially list density, cards/tables, buttons, filters, and empty/loading/error states.

### Clean up SocketJack.com MasterList server listings

Organize the server listings on the `SocketJack.com/MasterList` page so they look cleaner, sharper, and show more useful information without repeating the same details. Offline servers should be visibly greyed out. Online servers should show internet latency and proxy latency in milliseconds, for example `180ms (Internet)` and `12ms (proxy)`.

Suggested fix: redesign each server row/card around distinct fields such as server name, status, region/location, capabilities, current load, internet latency, proxy latency, last seen, and action buttons. Use an interpolated latency color scale for both latency metrics: less than or equal to `33ms` is green, around `100ms` is orange, and greater than or equal to `175ms` is red.

### Safer model download manager

Add resumable downloads with a queue, verified checksums, revision pinning, disk-space checks, license prompts, and per-model provenance notes. Surface private-model auth as a scoped credential, not a global text field.

### Tool marketplace with trust levels

Build a local tool catalog with signed tool definitions, required permissions, last audit entries, sample input/output, and per-project approval defaults. Make destructive tools visually distinct and easy to disable.

### Companion safety timeline

Show a chronological timeline of screenshots, approvals, desktop actions, process/file actions, model decisions, and emergency stops. This would make it much easier to review what the companion did and why.

### Local eval harness

Add an evaluation runner for prompt quality, tool-call correctness, JSON validity, latency, and token usage across local and remote models. Save baselines so model/runtime changes can be compared before release.

### Visual Studio extension workflow polish

For `LlmRuntime.VisualStudio`, add commands for "Explain selection", "Generate tests", "Review diff", and "Send project context to local runtime", with a small status panel showing selected model, endpoint, and privacy mode.
