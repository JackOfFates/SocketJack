# Copilot Instructions

## General Guidelines
- For samples in this repo, do not use the Unity project/library or WebSocket APIs; use the base `SocketJack` library with `TcpClient` and `TcpServer` instead.
- Do not use the C# null-coalescing operator `??` in this repo/project.
- When validating builds in this repo, ignore failures from WebSocketTestApplication (missing node.exe) and Unity projects (missing UnityEngine refs); focus on building SocketJack and WpfBasicGame targets.