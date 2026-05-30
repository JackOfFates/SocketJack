# Master List Server API for SocketJack.Com AI Chat

This fixes the "No SocketJack.Com server is available with the required chat/vision features" path by giving AI chat clients a stable public server-list API and by making the client understand the current master-list payload.

## Endpoints

The canonical endpoint remains:

```text
GET /api/lmvsproxy/servers
```

Two aliases now return the same payload for AI chat integrations that should not depend on the older LM/VS proxy name:

```text
GET /api/socketjack-com/servers
GET /api/socketjack/ai-servers
```

All three endpoints support `OPTIONS` for browser preflight. The standalone `SocketJack-MagicMasterList` service exposes the aliases on both its API listener and website listener. The embedded JackLLM chat server exposes the same aliases through `SocketJack.LlmCore.Proxy.JackLLM`.

Registered shell servers can also request browser presentation video conversion:

```text
POST /api/socketjack/video/presentation
POST /api/socketjack/ffmpeg/video/presentation
```

The conversion endpoint accepts a `dataUrl`, `videoDataUrl`, `contentBase64`, `videoBase64`, or `base64` video payload and returns a `video/mp4` data URL encoded with FFmpeg for chat playback. Requests must identify a registered shell relay/server with `relayId`, `shellRelayId`, `routeName`, or `serverId`, and must pass the same owner authorization used for the shell registration.

## Payload Shape

The response is a JSON object:

```json
{
  "ok": true,
  "count": 1,
  "updatedUtc": "2026-05-13T00:00:00.0000000Z",
  "servers": [
    {
      "id": "server-id",
      "title": "Display name",
      "endpoint": "https://host.example/chat",
      "connectHost": "host.example",
      "proxyPort": 11434,
      "chatPort": 11436,
      "availableModels": "model-a, model-b",
      "modelCapabilitiesJson": "[{\"id\":\"model-a\",\"supportsImages\":true}]",
      "toolsAllowed": "vision, tools",
      "isOnline": true
    }
  ]
}
```

The SocketJack.Com resolver accepts both camelCase and PascalCase fields. It derives an OpenAI-compatible base URL from `connectHost` plus `proxyPort` when `openAiBaseUrl` is not supplied, and it reads `modelCapabilitiesJson` / `modelBenchmarksJson` to detect vision, image analysis, and media generation support.

Public responses are sanitized before they leave the master list. Browser-facing entries should use `/proxy/<server-route>` endpoints and must not include direct public IPs, private IPs, raw registration JSON, or direct OpenAI-compatible URLs. Clients should treat `id` / `serverId` as an opaque selector and should not parse host information out of it.

## Registration Process

1. A JackLLM host publishes to `POST /api/lmvsproxy/servers`.
2. The master list normalizes the registration into a `RegisteredServer` record.
3. Public list requests read the visible, non-hidden records with current health fields.
4. AI chat clients call one of the GET endpoints above.
5. The SocketJack.Com client filters for the required features:
   - Text chat requires `chat`.
   - Image prompts require `chat`, `vision`, and `image-analysis`.
   - Media generation requires the matching generation capability.
6. If no `openAiBaseUrl` exists, the client builds one from `connectHost` or the endpoint host and `proxyPort`.

## Operational Check

For JackCast, verify the public API before testing chat:

```powershell
Invoke-RestMethod https://jackcast.live/api/socketjack-com/servers
```

At least one visible server should have a reachable endpoint or `connectHost` plus `proxyPort`, and its capability metadata should advertise vision if the chat question includes an image.
