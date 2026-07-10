# JackLLM Workstation

`JackLLM.Workstation` is the cross-platform backend host for JackLLM Workstation. It runs the SocketJack/LmVsProxy backend, serves the browser workstation UI, and can start an embedded `LlmRuntime`.

The Windows `JackLLM/` project remains the WPF tray/window application. This project is a headless service/terminal host and does not create a native desktop GUI.

## Run On Linux

```bash
dotnet run --project ./JackLLM.Workstation/JackLLM.Workstation.csproj -- --runtime llmruntime
```

Default endpoints:

| Endpoint | URL |
|---|---|
| Web console | `http://localhost:11436/` |
| OpenAI chat completions | `http://localhost:11434/v1/chat/completions` |
| VS/Copilot responses | `http://localhost:11434/v1/responses` |
| Embedded LlmRuntime | `http://127.0.0.1:11435/` |

## Publish

```bash
dotnet publish ./JackLLM.Workstation/JackLLM.Workstation.csproj --configuration Release --runtime linux-x64 --self-contained false
```

For a standalone Linux binary, publish with `--self-contained true`.

## Options

```bash
dotnet run --project ./JackLLM.Workstation/JackLLM.Workstation.csproj -- --help
```

Useful examples:

```bash
dotnet run --project ./JackLLM.Workstation/JackLLM.Workstation.csproj -- --runtime llmruntime --model-root /srv/jackllm/Models --data-root /var/lib/jackllm
dotnet run --project ./JackLLM.Workstation/JackLLM.Workstation.csproj -- --runtime lmstudio --lmstudio-url http://127.0.0.1:1234
dotnet run --project ./JackLLM.Workstation/JackLLM.Workstation.csproj -- --no-copilot-duplicator --chat-port 11436
```

WPF remote-admin screen capture/input is not available in this Linux host. The browser workstation, chat, proxy, model runtime, sessions, files, permissions, and diagnostics APIs are hosted by the shared backend.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>1,143 lines / 4 files</code></summary>

<br>

<strong>Scope:</strong> <code>JackLLM.Workstation</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 2 | 1,070 |
| Markdown | 1 | 44 |
| MSBuild/XML | 1 | 29 |
| **Total** | **4** | **1,143** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
