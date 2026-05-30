# SocketJack.WorkstationMcp

Local MCP bridge for JackLLM Workstation / SocketJack HTTP APIs.

## Stdio transport

Use this for Codex MCP configuration when the client launches the server as a child process. It opens no listening socket.

```powershell
dotnet run --project .\SocketJack.WorkstationMcp\SocketJack.WorkstationMcp.csproj -- --stdio
```

## Loopback HTTP transport

Use this when you need a local Streamable HTTP MCP endpoint:

```powershell
dotnet run --project .\SocketJack.WorkstationMcp\SocketJack.WorkstationMcp.csproj -- --http --port 11573
```

HTTP mode:

- listens only on `http://127.0.0.1:<port>/mcp`
- refuses to start unless `codex.exe` is running
- stops itself after `codex.exe` exits
- proxies only to loopback JackLLM URLs

Health probe:

```powershell
Invoke-WebRequest -UseBasicParsing http://127.0.0.1:11573/healthz
```

Available tools:

- `socketjack_workstation_summary`
- `socketjack_get_endpoint`
- `socketjack_get_path`
- `socketjack_stop_stream`

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>738 lines / 9 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack.WorkstationMcp</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 6 | 672 |
| Markdown | 1 | 41 |
| MSBuild/XML | 1 | 13 |
| JSON | 1 | 12 |
| **Total** | **9** | **738** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
