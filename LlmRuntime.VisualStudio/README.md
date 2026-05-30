# LlmRuntime.VisualStudio

Visual Studio VSIX integration shell for SocketJack LlmRuntime.

This project is the dedicated extension surface for full Visual Studio parity:

- Inline completions through `/api/v1/ide/completions/inline`.
- Next-edit suggestions through `/api/v1/ide/next-edit`.
- Ask/Edit/Agent/Plan mode calls through the hostable LlmRuntime IDE APIs.
- Installed Visual Studio Tools menu commands for chat, inline completion, next edit, agent mode, checkpoints, MCP context, prompt files, custom instructions, model picker, code review, and workspace indexing.
- Commands call the configured LlmRuntime HTTP API and surface the returned JSON/status in Visual Studio. Configure the endpoint in **Tools > Options > SocketJack LlmRuntime > Runtime Endpoint** with `Host` set to a local/external IP address or domain, and `Port` set to the runtime port.
- Feature catalog entries for prompt files, custom instructions, MCP context, model picker, code review, and workspace indexing.

The current scaffold registers an async package, command table, Tools menu command surface, feature catalog, and HTTP client. Feature-specific editor adorners, inline completion source providers, and tool windows can now bind to these command and endpoint surfaces without coupling Visual Studio SDK assemblies into the core `LlmRuntime` library.

## Build

```powershell
dotnet build .\LlmRuntime.VisualStudio\LlmRuntime.VisualStudio.csproj
```

The build produces an installable VSIX at:

```text
LlmRuntime.VisualStudio\bin\Debug\net472\SocketJack.LlmRuntime.VisualStudio.vsix
```

The VSIX contains the package assembly, generated `LlmRuntime.VisualStudio.pkgdef`, and `extension.vsixmanifest`. It targets Visual Studio 2022 17.x on `amd64`.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>9,375 lines / 12 files</code></summary>

<br>

<strong>Scope:</strong> <code>LlmRuntime.VisualStudio</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| XML | 1 | 6,866 |
| C# | 7 | 2,031 |
| Markdown | 3 | 416 |
| MSBuild/XML | 1 | 62 |
| **Total** | **12** | **9,375** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
