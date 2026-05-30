# Context and WebSocket Health Report

Date: 2026-05-17 America/Chicago

## Summary

- Rebuilt and restarted JackLLM so `http://127.0.0.1:11436/` serves the current `JackLLMWebChat.html`.
- Verified the chat context expander is present, wired, and opens in the in-app browser.
- Verified the local WebSocket health bridge code path with a temporary local MasterList instance.
- Ran a direct `/api/chat-stream` smoke request and did not see `Error in input stream`.

## Build and Restart

- `dotnet build ..\SocketJack.LlmCore\SocketJack.LlmCore.csproj --no-restore`: passed.
- `dotnet build ..\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore`: passed with 6 existing nullable field warnings in `SocketJack-MagicMasterList\Program.cs`.
- `dotnet build ..\JackLLM\JackLLM.csproj --no-restore`: passed after stopping the old running JackLLM process.
- Restarted JackLLM from `JackLLM\bin\Debug\net8.0-windows7.0\JackLLM.exe`.
- Post-restart health: `http://127.0.0.1:11436/api/health` returned HTTP 200 with `"ok": true`.

## Context Expander

Initial live-page result before restart:

- `CTX` markup was present.
- The served JavaScript was stale and did not include `renderContextExpander`, `updateContextToggleMeta`, or `setContextExpanderOpen`.
- Result: the button was visible but did not open.

Post-restart in-app browser result:

- `#contextToggle`: present.
- `#contextExpander`: present.
- Loaded script includes `renderContextExpander`, `updateContextToggleMeta`, and `setContextExpanderOpen`.
- Clicking `CTX` opened the expander and set `aria-expanded="true"`.

Observed context contents:

- Summary: `7 messages | ~271 tokens`.
- Meta:
  - Model: `Unsloth_Gguf_4Yqurthx (Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF) - tools (loaded)`
  - Mode: `Chat`
  - Messages: `7`
  - Tokens: `~271`
  - Files: `none`
  - FS Context: `off`
- Message rows: `7`.
- Total visible context body chars: `1,274`.
- Largest single row: `1,120` chars.
- Filename/path-like matches in message bodies: `0`.
- No row was trimmed.

Finding: the context expander is showing a bounded chat-message preview, not a filesystem filename dump.

## WebSocket Health Monitor

Source and live page checks:

- `SocketJack-MagicMasterList\Program.cs` registers `ShellProxyJsonWebSocketHandler`.
- The handler accepts `/proxy/{route}/api/health`, `/proxy/{route}/health`, and `/proxy/{route}/api/server-hardware`.
- `JackLLMWebChat.html` includes `requestLocalJsonViaWebSocket()` and routes proxied `/api/health` reads through it before falling back to HTTP.
- The restarted local JackLLM page includes the WebSocket bridge code and the heartbeat pill is connected.

Runtime WebSocket probe:

- Started a temporary local MasterList instance on loopback high ports:
  - API: `127.0.0.1:18494`
  - Website: `127.0.0.1:18080`
- `http://127.0.0.1:18080/MasterList` served the updated script and contained `requestWebSocketJson`.
- Probed `ws://127.0.0.1:18080/proxy/nope/api/health`.
- The socket connected and returned:
  - `hello` with `socketJackTransport: "websocket"`.
  - `snapshot` with `socketJackTransport: "websocket"`, `socketJackForwardedPath: "/api/health"`, and expected `statusCode: 404` because route `nope` does not exist.
- Stopped the temporary MasterList process after the probe.

Public deployment note:

- `https://socketjack.com/proxy/TitanX/api/health` still returns HTTP 200.
- `https://socketjack.com/MasterList` did not contain the new `requestWebSocketJson` or `healthWebSocketUrlForServer` code when checked.
- `wss://socketjack.com/proxy/TitanX/api/health` timed out from this machine.

Finding: the local checkout's WebSocket handler and web UI code work after rebuild/restart. The public `socketjack.com` deployment appears older than the local checkout and likely still needs deployment before public WSS health checks work.

## Chat Stream Smoke

Request:

- `POST http://127.0.0.1:11436/api/chat-stream`
- Model: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Mode: `chat`
- Prompt: `Reply exactly: stream-ok`
- `max_tokens`: `24`

Result:

- HTTP status: `200`.
- Stream response length: `67,230` bytes.
- No `"type": "error"` event found.
- No `Error in input stream` text found.
- Active stream list after completion: `[]`.

Finding: the stream transport completed without the reported input-stream error. The exact text check was not useful with this reasoning model and tiny cap, but the transport failure did not reproduce.

