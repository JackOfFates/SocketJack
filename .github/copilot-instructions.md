# Copilot Instructions

## General Guidelines
- For samples in this repo, do not use the Unity project/library or WebSocket APIs; use the base `SocketJack` library with `TcpClient` and `TcpServer` instead.
- Do not use the C# null-coalescing operator `??` in this repo/project.
- When validating builds in this repo, ignore failures from WebSocketTestApplication (missing node.exe) and Unity projects (missing UnityEngine refs); focus on building SocketJack and WpfBasicGame targets.
- Readme_Gen.md files are markdown — do not use HTML tags like `<p align="center">`, `<a>`, `<img>`, `<strong>`, or `<br/>`. Use only standard markdown syntax.
- Every time SocketJack\Readme_Gen.md is updated, sync its contents to the root Readme.md file (the GitHub project readme).

## WPF Specific Instructions
- When implementing the WPF shared-control viewer, do not hide the system cursor (avoid overriding cursor visibility).

## Jack-Debug Project Instructions
- In the DrawTimeline spatial rendering, do not pair consecutive points into rectangles. Always render individual points (Point, Vector types) as circles (Ellipse), never as rectangles.