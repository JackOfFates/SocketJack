# SocketJack WPF Basic Game

This project is a small WPF multiplayer demo for the SocketJack `2026` line. It uses the base SocketJack TCP types (`TcpServer` / `TcpClient`) to run a tiny "Click Race" and keeps the UI intentionally minimal so the network flow is easy to inspect.

For fuller TCP, UDP, WebSocket, WPF sharing, and `MutableTcpServer` snippets, see [examples.md](../examples.md).

## How to run

1. Set `SocketJack.WpfBasicGame` as startup project.
2. Start the app.
3. In the first instance: select **Host** and click `Start Round`.
4. In the second instance (Optional): select **Join** and click `Start Round`.
6. Both players click the moving `CLICK` target as fast as possible.

The host runs a `TcpServer` and broadcasts score updates. Each player uses a `TcpClient`.

## Linux-hosted build

This WPF app can be compiled from Linux as a Windows-targeted assembly:

```bash
dotnet build ./SocketJack.WpfBasicGame/SocketJack.WpfBasicGame.csproj --configuration Release
```

Running it still requires Windows because WPF is a Windows desktop stack.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>2,034 lines / 17 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack.WpfBasicGame</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 12 | 1,846 |
| XAML | 2 | 130 |
| Markdown | 1 | 32 |
| MSBuild/XML | 1 | 25 |
| XML | 1 | 1 |
| **Total** | **17** | **2,034** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
