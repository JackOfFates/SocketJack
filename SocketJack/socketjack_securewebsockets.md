# SocketJack Secure WebSockets

## Summary

SocketJack WebSocket APIs support secure WebSockets (`wss://`) by using the same TLS listener that serves HTTPS. For `socketjack.com`, WebSocket upgrades terminate on the existing HTTPS listener and the configured `socketjack.com` certificate. No separate WSS port or certificate is required.

## SocketJack.com Proxy Flow

- Browser page: `https://socketjack.com/proxy/{server}/`
- Web Chat API socket: `wss://socketjack.com/proxy/{server}/api/web-chat/ws`
- TLS termination: existing SocketJack.com HTTPS listener and certificate
- Master-to-workstation transport: existing persistent shell proxy WebSocket tunnel
- Fallback: if the browser WSS socket fails, Web Chat falls back to HTTP APIs; those HTTP APIs still prefer the master-to-workstation WebSocket tunnel and then the TCP reverse-agent path.

## Local Flow

- Browser page: `http://127.0.0.1:11436/`
- Web Chat API socket: `ws://127.0.0.1:11436/api/web-chat/ws`
- The local socket is registered on the same `MutableTcpServer` as the HTTP APIs.
- WebSocket request envelopes dispatch through the same mapped HTTP routes, including streaming routes.

## Protocol Shape

- Text frames carry control messages: `ready`, `request`, `response-start`, `response-end`, `cancel`, `error`, `ping`, and `pong`.
- Binary response frames carry body chunks.
- The first 32 bytes of each binary frame are the request id, padded with spaces; the rest is response body data.
- Streaming routes flush chunks immediately as binary WebSocket frames so chat/model output can render without waiting for a complete HTTP response body.

## Secure Client Support

`SocketJack.Net.WebSockets.WebSocketClient.ConnectAsync(Uri)` supports both:

- `ws://host/path`
- `wss://host/path`

For `wss://`, the client wraps the connected TCP stream in `SslStream`, authenticates using the URI host, defaults to port `443`, and then sends the normal HTTP WebSocket upgrade request through the encrypted stream. Existing `ws://` behavior remains unchanged and defaults to port `80`.

## Verification Checklist

- `dotnet build SocketJack/SocketJack.csproj`
- `dotnet build SocketJack-MagicMasterList/SocketJack-MagicMasterList.csproj`
- `dotnet build SocketJack.LlmCore/SocketJack.LlmCore.csproj`
- Local browser: confirm `ws://127.0.0.1:11436/api/web-chat/ws` connects and `/api/health` succeeds.
- Public proxy browser: confirm `wss://socketjack.com/proxy/TitanX/api/web-chat/ws` connects with the valid `socketjack.com` certificate.
- Streaming: confirm `/api/chat-stream` emits incremental chunks over the WebSocket response stream.
- Fallback: close or block the WebSocket and confirm Web Chat falls back to HTTP.

