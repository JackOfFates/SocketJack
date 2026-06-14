# Changelog

## 2026-06-12 - LlmRuntime.VisualStudio2026 0.2.52

### Root cause

- The Configure button wrote the solution-local MCP config first, then waited on direct TitanX chat and fallback route probes before writing Visual Studio Copilot BYOM.
- TitanX's selected proxy routes timed out during those probes, and the internal timeout surfaced as `OperationCanceledException`, which bypassed the view model's normal error-status handler and left the UI showing `Configuring Visual Studio Copilot for SocketJack...`.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.52`.
- Made SocketJack `/proxy/{server}` selections prefer the packaged local SocketJack bridge immediately instead of blocking Configure on remote preflight probes.
- Converted internal model/fallback route-probe timeouts into normal negative probe results.
- Added a visible timeout/cancel status path in the Copilot server browser instead of leaving the busy text behind.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotBridgeTests|FullyQualifiedName~SocketJackCopilotServicesTests" --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Built `C:\Users\Vin\Documents\GitHub\SocketJack\LlmRuntime.VisualStudio2026\bin\Release\net8.0-windows8.0\LlmRuntime.VisualStudio2026.vsix`.
- Installed/refreshed the active per-user VS 18 extension payload at `C:\Users\Vin\AppData\Local\Microsoft\VisualStudio\18.0_dab6f9cc\Extensions\SocketJack\LlmRuntime.VisualStudio2026` and verified `LlmRuntime.VisualStudio2026.dll` file version `0.2.52.0`.
- Verified Visual Studio BYOM now points at `http://127.0.0.1:11575` for TitanX `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`.
- Verified `C:\Users\Vin\source\repos\Maf Scale\.vs\mcp.json` points at the `0.2.52` per-user packaged bridge.
- Started the packaged bridge and verified `http://127.0.0.1:11575/socketjack-proxy-health` reports TitanX/Qwen3.5.
- Live `/v1/chat/completions` probe returned HTTP 200 with the bridge's visible 60-second upstream-timeout message, `HAS_RAW_503=False`, and `HAS_OLD_PLACEHOLDER=False`.

## 2026-06-11 - LlmRuntime.VisualStudio2026 0.2.51

### Root cause

- Visual Studio tool-capable Copilot turns open the BYOM SSE response before forwarding to the selected SocketJack OpenAI-compatible model-runtime route.
- When both direct OpenAI-compatible paths returned `503`, the bridge treated the last endpoint diagnostic as visible assistant text and ended the turn before reaching the existing friendly server-offline recovery.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.51`.
- Preserved the last direct OpenAI-compatible upstream status code inside the streaming bridge path.
- Recognized bridge-generated `HTTP 502/503/504` endpoint diagnostics as offline selected-server failures.
- Streamed the existing SocketJack server-offline assistant message for those failures and kept the Copilot Servers window auto-open behavior.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotBridgeTests|FullyQualifiedName~SocketJackCopilotServicesTests" --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Built `C:\Users\Vin\Documents\GitHub\SocketJack\LlmRuntime.VisualStudio2026\bin\Release\net8.0-windows8.0\LlmRuntime.VisualStudio2026.vsix`.
- Installed the `0.2.51.0` VSIX payload into the active per-user VS 18 extension folder at `C:\Users\Vin\AppData\Local\Microsoft\VisualStudio\18.0_dab6f9cc\Extensions\SocketJack\LlmRuntime.VisualStudio2026`.
- Started the packaged `0.2.51` bridge on `http://127.0.0.1:11575` for Sable/Qwen3.6 and verified `/socketjack-proxy-health`.
- Live VS-shaped `/v1/chat/completions` probe returned a `get_projects_in_solution` tool call with `HAS_RAW_503=False`.
- Live plain `/v1/chat/completions` probe returned HTTP 200 with the friendly `Sable server is offline` assistant text and `HAS_RAW_503=False`.

## 2026-06-10 - LlmRuntime.VisualStudio2026 0.2.50

### Root cause

- The VS 2026 SocketJack Copilot Servers browser only populated from live refresh state, so a slow or unavailable MasterList/model route left the picker empty even when Visual Studio already had a usable recent server/model selection.
- The server and model lists were rendered as single concatenated strings, which made endpoint, eligibility, model count, load state, and capability details hard to scan.
- Visual Studio Copilot was configured to call `http://127.0.0.1:11575`, but no local bridge listener was running there after the previous install/process cleanup.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.50`.
- Added a persistent SocketJack Copilot browser cache for normalized server candidates and per-server model candidates under local app data.
- Updated the VS 2026 server browser view model to hydrate cached servers/models immediately, synthesize model lists from cached MasterList metadata, and save fresh live discovery results back to cache.
- Polished the server browser UI with server/model summary headers, endpoint rows, advertised model counts, capability labels, load state, and token detail.
- Started the live packaged bridge on `127.0.0.1:11575` for the current Sable/Qwen3.6 BYOM selection so Copilot Retry has a listener.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotServicesTests|FullyQualifiedName~SocketJackCopilotBridgeTests" --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Installed `0.2.50.0` into the VS 18 Insiders extension cache.
- Verified the active installed bridge on `http://127.0.0.1:11575/socketjack-proxy-health` for Sable/Qwen3.6.
- Live `/v1/chat/completions` probe on `11575` returned HTTP 200 with `oldFallback=False` and `newFallback=False`.
- After the Visual Studio update completed, verified `0.2.50.0` was still installed, restarted the packaged `11575` bridge, and seeded `%LOCALAPPDATA%\SocketJack\VisualStudio\CopilotServerBrowserCache.json` with cached servers and Sable model data.

## 2026-06-09 - LlmRuntime.VisualStudio2026 0.2.49

### Root cause

- The `0.2.48` bridge replaced the old Visual Studio phrase, but still emitted SocketJack diagnostic fallback text when a VS tool-capable stream closed before visible assistant content.
- The VS project-summary detector matched `readme.md` but not the user's `summary.md` prompt, so the empty-stream recovery path did not reliably start project discovery for that Copilot request shape.
- One OpenAI-compatible WebSocket fallback branch could synthesize no-visible text without first trying the Visual Studio tool-call recovery.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.49`.
- Made the VS recovery writer open the SSE stream itself before emitting recovery tool calls.
- Changed empty VS tool-capable turns to start with `get_projects_in_solution`, then continue from project-list results to `get_files_in_project`.
- Recognized SocketJack's new no-visible diagnostic as recoverable history and expanded project-summary detection to include `summary.md`.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Installed `0.2.49.0` into the VS 18 Insiders extension cache.
- Reset Visual Studio Copilot BYOM to `http://127.0.0.1:11574` for Sable and the current Qwen2.5 selection.
- Live `/v1/chat/completions` probes on the active `11574` bridge returned normal chat and VS project-summary tool-call output with `oldFallback=False` and `newFallback=False`.

## 2026-06-09 - LlmRuntime.VisualStudio2026 0.2.48

### Root cause

- The SocketJack bridge included the exact Visual Studio Copilot no-visible-reply placeholder as its own synthetic fallback text, making it impossible to tell whether the message came from Copilot or from the bridge.
- Repeated local bridge test processes occupied `11574`, `11575`, and `11576`, so the VS configurator wrote the next free BYOM port even when a matching bridge was already healthy.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.48`.
- Replaced the bridge's no-visible-assistant fallback with SocketJack-specific diagnostic text.
- Kept recovery detection for the legacy Copilot placeholder so existing bad turns can still be recognized.
- Changed the VS 2026 configurator to reuse a healthy matching proxy port before selecting the next free port.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Installed `0.2.48.0` into the VS 18 Insiders extension cache.
- Reset Visual Studio Copilot BYOM to `http://127.0.0.1:11574` for Sable and stopped the extra local bridge processes on higher ports.
- Live `/v1/chat/completions` probes on the active `11574` bridge returned streamed chat and agent tool-call output with `oldFallback=False`.

## 2026-06-09 - LlmRuntime.VisualStudio2026 0.2.47

### Root cause

- Visual Studio Copilot's Ollama BYOM path can call `/v1/chat/completions`, not only `/v1/responses`.
- The bridge still tried `/api/chat-stream` before the OpenAI-compatible model-runtime route for streamed Chat Completions, so Copilot could receive `Working on it...` followed by the bridge's no-visible-reply fallback even when the server could answer through model-runtime.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.47`.
- Made streamed Chat Completions requests use the direct OpenAI-compatible route before falling back to `/api/chat-stream`.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Installed `0.2.47.0` into the VS 18 Insiders extension cache.
- Live `/v1/chat/completions` probes on TitanX `127.0.0.1:11575` and Sable `127.0.0.1:11574` returned real streamed content with `containsWorking=False` and `containsFallback=False`.
- Agent-shaped `/v1/chat/completions` and `/v1/responses` probes on the active TitanX bridge with a `get_projects_in_solution` tool returned tool-call events with `containsWorking=False` and `containsFallback=False`.

## 2026-06-09 - LlmRuntime.VisualStudio2026 0.2.46

### Root cause

- The local Copilot BYOM bridge tried the SocketJack web-chat stream adapter before Sable's OpenAI-compatible model-runtime route for Responses API streaming requests.
- On Sable, the model-runtime route could serve the selected model directly, while the web-chat adapter could leave Copilot with only the bridge wait placeholder even though GPU inference was active.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.46`.
- Reordered OpenAI chat forwarding paths to prefer `api/model-runtime/v1/chat/completions`.
- Made streamed Copilot Responses requests try the OpenAI-compatible route before falling back to `/api/chat-stream`.
- Adapted non-streaming Responses requests from direct OpenAI-compatible model-runtime output back into Responses API payloads.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Installed `0.2.46.0` into the VS 18 Insiders per-user extension folder and restarted the packaged bridge on `http://127.0.0.1:11574`.
- Live `/v1/responses` probe against the selected Sable BYOM bridge returned real `response.output_text.delta` events with `containsWorking=False`.

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.45

### Root cause

- Session Sync could push normal files but failed on a zero-byte solution file because the public Auto session-file endpoint treated an explicitly empty `base64`/`dataUrl` value as a missing payload.
- The VS Copilot configuration status surfaced JackLLM's optional local duplicator endpoint as an error even when the packaged VSIX bridge was already configured for BYOM.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.45`.
- Updated VS Session Sync uploads to use the same `dataUrl` envelope as the SocketJack Auto web uploader.
- Updated the Auto session-file upload and restore handlers to accept explicitly empty file payloads.
- Changed the Copilot configuration status to say the local JackLLM duplicator was skipped while the packaged VSIX bridge remains configured.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj -c Release --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.44

### Root cause

- Visual Studio Copilot BYOM stores a loopback URL, but Copilot itself does not start the local SocketJack bridge when that URL is used.
- The VS extension packaged the bridge, but launch paths still preferred `dotnet Bridge\SocketJack.CopilotMcpBridge.dll`, which can fail if Visual Studio does not inherit a usable `dotnet` PATH or if the extension has not restarted the bridge after VS startup.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.44`.
- Added bridge launch helpers that run the packaged `SocketJack.CopilotMcpBridge.exe` directly.
- Updated the VS 2026 Copilot configurator and startup supervisor to prefer the packaged bridge executable and fall back to DLL/project launch only when the EXE is unavailable.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.43

### Root cause

- The SocketJack direct chat-stream relay could receive bare HTTP chunk-size framing lines from an upstream proxy.
- Those hex length markers were parsed as assistant text and forwarded to VS Copilot alongside the real model output.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.43`.
- Filtered bare HTTP chunk-size lines before parsing chat-stream text or accepting immediate raw text chunks.

### Verification

- Live selected BYOM port no longer emits bare chunk-size deltas in the Responses stream.
- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.42

### Root cause

- SocketJack proxy endpoints already carry the selected server/model context, but the bridge still performed remote model-catalog resolution before opening the local OpenAI-compatible stream.
- If that remote catalog call was slow, VS Copilot could show its no-visible-reply placeholder before the bridge sent any visible output.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.42`.
- Skipped remote canonical model resolution for `/proxy/{server}` endpoints and used the selected model id directly.

### Verification

- Live selected BYOM port emits a visible Responses delta promptly after the bridge opens.
- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.41

### Root cause

- The bridge still waited for upstream chat-stream response headers before opening the local Responses stream.
- If the selected SocketJack server took too long to return headers, VS Copilot could show its no-visible-reply placeholder before the bridge had a chance to send the visible wait-status delta.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.41`.
- Moved the direct chat-stream wait-status delta into the upstream-header wait loop.
- Kept offline/error handling valid after the local Responses stream has already been opened.

### Verification

- Live probe confirms the selected Copilot BYOM port emits a visible `Working on it...` delta before the upstream model finishes.
- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.40

### Root cause

- VS Copilot does not treat SSE comments or empty deltas as visible assistant output.
- Long direct SocketJack chat-stream turns could keep the GPU busy but leave Copilot with no visible text before its own no-reply placeholder logic fired.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.40`.
- Changed the direct chat-stream heartbeat path to emit one visible wait-status delta if no model text has arrived after the first heartbeat.
- Preserved normal model output handling so the actual answer is appended when upstream text arrives.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`
- Live Copilot BYOM ports restarted from the installed `0.2.40` bridge.

## 2026-06-08 - LlmRuntime.VisualStudio2026 0.2.39

### Root cause

- Direct bridge calls joined relative paths against endpoints like `https://socketjack.com/proxy/TitanX` without first normalizing the endpoint as a directory.
- In .NET URI resolution, `api/chat-stream` against that base drops the `TitanX` segment, so direct chat-stream/OpenAI-compatible fallback calls could hit the wrong proxy route and surface `Chat UI request body was empty` even while the selected model was running.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.39`.
- Changed bridge endpoint URI construction to force a trailing slash before appending relative API paths.

### Verification

- `dotnet build SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj --no-restore -v:minimal`
- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- Live probes against the selected Copilot BYOM port verify streamed visible text and non-empty terminal Responses metadata.

## 2026-06-07 - LlmRuntime.VisualStudio2026 0.2.38

### Root cause

- VS Copilot could receive visible text deltas from the SocketJack bridge, then still decide the turn had no visible reply because the terminal Responses API events reported an empty `output_text` and empty completed message content.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.38`.
- Changed the bridge Responses stream finish path to carry the accumulated assistant text into `response.output_text.done`, `response.content_part.done`, `response.output_item.done`, and `response.completed`.
- Updated regression coverage so streamed Responses output must include the visible text in final terminal metadata.

### Verification

- Live patched-bridge probe against `127.0.0.1:11574` previously reproduced the bad shape: `response.output_text.delta` contained `visible-ok`, but final `response.completed.output_text` was empty.
- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj --no-restore -v:minimal`
- `dotnet build LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --no-restore -v:minimal`

## 2026-06-07 - LlmRuntime.VisualStudio2026 0.2.37

### Root cause

- The SocketJack Copilot MCP bridge opened a Responses API assistant text message item before it knew whether the upstream model would emit text or a function call.
- For Visual Studio tool turns, a valid model-produced function call could arrive after that empty message prelude, making Copilot treat the turn as a completed assistant message with no visible reply instead of executing the tool call.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.37`.
- Changed the bridge Responses stream path to emit only lifecycle events at stream start, then lazily open a text message item only when visible text is actually streamed.
- Changed Responses tool-call streams so function-call items can follow the lifecycle events without an empty assistant message/content part first.
- Added regression coverage for Responses tool-call streams without a blank message prelude.

### Verification

- `dotnet test LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter FullyQualifiedName~SocketJackCopilotBridgeTests --no-restore -v:minimal`
- `dotnet build SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj --no-restore -v:minimal`
- Live patched-bridge smoke against `127.0.0.1:11575` confirmed `messagePrelude=False`, `contentPartAdded=False`, and `functionCall=True` for a VS-like `/v1/responses` tool turn.

## 2026-06-04 - LlmRuntime.VisualStudio2026 0.2.36

### Root cause

- The VSIX packaged the preview extension with the Visual Studio 17.14+ compatibility range, which made the VS 2026 Insiders installer reject the package as targeting an older product lane.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.36`.
- Changed the extension target range to Visual Studio `18.x`.
- Updated the VSIX manifest postprocessor to emit `Community`, `Pro`, and `Enterprise` installation targets for Visual Studio `18.x`, all restricted to `amd64`.
- Fixed VS model discovery fallback parsing so MasterList `modelCapabilitiesJson` array strings preserve per-model `supportsTools` flags when `/api/models` and `/api/model-runtime/models` are unavailable.

### Verification

- Inspect the built VSIX `extension.vsixmanifest` and install it with the Visual Studio 2026 Insiders `VSIXInstaller.exe`.
- `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotServicesTests" --nologo -v:minimal`

## 2026-06-02 - LlmRuntime.VisualStudio2026 0.2.35

### Root cause

- VS Copilot could duplicate SocketJack LLM responses when consuming Responses API SSE output from the local `SocketJack.CopilotMcpBridge`.
- The bridge streamed visible text correctly through `response.output_text.delta`, then repeated the completed assistant text in terminal Responses events such as `response.output_text.done`, `response.content_part.done`, `response.output_item.done`, and `response.completed`.
- Copilot treated those terminal snapshots as additional visible text instead of final-state metadata.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.35`.
- Updated marketplace release notes for the duplicated-response bridge fix.
- Changed the Responses API stream finish path so terminal events close the stream structurally without replaying assistant text.
- Added regression coverage to verify streamed Responses output contains the visible assistant text exactly once.

### Verification

- `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotBridgeTests" --nologo -v:minimal`
- `dotnet build ..\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj -c Release --nologo -v:minimal`
- `dotnet build ..\LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --nologo -v:minimal`
