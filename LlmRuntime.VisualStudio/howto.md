# How To Use Local JackLLM Workstation In Visual Studio

## Requirements

- Visual Studio 2022 with Copilot MCP support, or Visual Studio 2026 Insiders.
- GitHub Copilot installed and signed in.
- For Visual Studio 2026 Insiders, install `LlmRuntime.VisualStudio2026\bin\Release\net8.0-windows8.0\LlmRuntime.VisualStudio2026.vsix` version `0.2.57` or later.
- The SocketJack VSIX installed, then Visual Studio restarted or reloaded.
- JackLLM Workstation running locally at `http://127.0.0.1:11436`.
- A solution open in Visual Studio. The extension writes solution-local MCP config to `.vs/mcp.json`.

You do not need a SocketJack.com account, SocketJack.com sign-in, hosted SocketJack.com server, or public login token for local JackLLM Workstation configuration.

JackLLM Workstation does not currently have a standalone download for this flow. Build and run it from the GitHub project:

[JackLLM Workstation project](https://github.com/JackOfFates/SocketJack/tree/master/JackLLM)

## Open The Server Picker

1. Start or restart Visual Studio after installing the VSIX.
2. Open the solution you want to configure.
3. Select `Extensions > SocketJack > SocketJack Copilot Servers`.
4. The `SocketJack Copilot Servers` tool window opens docked in Visual Studio.

When JackLLM Workstation answers `http://127.0.0.1:11436/api/health`, the extension enters local mode and does not show the SocketJack.com sign-in flow.

## Create MCP Config From The Tools Menu

If `.vs\mcp.json` does not exist yet, select:

```text
Extensions > SocketJack > Create SocketJack MCP Config
```

The command creates:

```text
<solution>\.vs\mcp.json
```

It tries to discover the local JackLLM Workstation first. If the local workstation is running and has an enabled tools-capable chat model, the command writes a working local SocketJack MCP entry without SocketJack.com authentication.

If server/model discovery fails, it still creates a valid empty MCP file:

```json
{
  "servers": {}
}
```

After that, open `Extensions > SocketJack > SocketJack Copilot Servers`, choose the local workstation/model, and click `Configure Copilot`.

## Pick The Local Workstation

1. Click `Refresh`.
2. Select `Local JackLLM Workstation`.
3. The right side of the tool window shows the local endpoint, tools status, eligibility, and hardware summary.

The local workstation is selectable for Copilot when it:

- Is online or responding.
- Has a usable endpoint.
- Advertises tools support through the local JackLLM model/runtime metadata.

## Pick A Model

1. After selecting a server, wait for the model list to load.
2. Open the model dropdown.
3. Select an enabled tools-capable chat model.
4. Disabled models remain visible with the reason they cannot be used.

Models are eligible when they:

- Support chat.
- Support tools/tool calling.
- Are loaded, runtime-loadable, or web-chat dynamically loadable.
- Do not have a load-disabled reason.

## Test The Connection

Click `Test Connection` before configuring.

The extension checks whether the selected endpoint exposes an OpenAI-compatible chat route that Visual Studio Copilot can call:

- `/chat/completions`
- `/v1/chat/completions`
- `/api/model-runtime/v1/chat/completions` as a legacy local JackLLM Workstation alias.

Model list routes like `/api/models` and `/api/model-runtime/models` are used for discovery, but they are not enough by themselves for Copilot chat.

If the direct local chat route works, Visual Studio Ollama BYOM is configured to use the local JackLLM model-runtime endpoint directly:

```text
http://127.0.0.1:11436
```

Visual Studio may call that Ollama BYOM endpoint as `http://127.0.0.1:11436/v1`. JackLLM Workstation serves `/v1/models`, `/v1/chat/completions`, and the older `/api/model-runtime/v1/...` aliases directly.

If Visual Studio needs a loopback OpenAI-compatible bridge, the extension starts the packaged local bridge and configures Ollama BYOM to that local address instead. The local bridge routes through JackLLM Workstation at:

```text
http://127.0.0.1:11436
```

## Configure Copilot

Click `Configure Copilot`.

The extension does three things:

1. Writes or updates the solution-local MCP config at `.vs/mcp.json`.
2. Updates the Visual Studio Ollama BYOM configuration to use the local JackLLM workstation/model.
3. Tries to notify local JackLLM through `/api/copilot-duplicator`.

The MCP config preserves existing non-SocketJack entries. SocketJack entries use the key format:

```text
socketjack-<server-id>
```

## What Gets Configured

### MCP

The extension writes:

```text
<solution>\.vs\mcp.json
```

If the file does not exist, the minimum valid empty file is:

```json
{
  "servers": {}
}
```

When the server has a direct MCP URL, the extension writes a remote MCP entry.

Example remote MCP entry:

```json
{
  "servers": {
    "socketjack-titanx": {
      "url": "https://socketjack.com/proxy/TitanX/mcp",
      "transport": "http"
    }
  }
}
```

When no direct MCP URL is available, it writes a stdio MCP entry that launches the VSIX-local bridge:

```text
Bridge\SocketJack.CopilotMcpBridge.dll
```

Example bridge entry:

```json
{
  "servers": {
    "socketjack-local-jackllm-workstation": {
      "type": "stdio",
      "transport": "stdio",
      "command": "dotnet",
      "args": [
        "C:\\Path\\To\\Extension\\Bridge\\SocketJack.CopilotMcpBridge.dll",
        "--stdio",
        "--server-endpoint",
        "http://127.0.0.1:11436",
        "--server-id",
        "local-jackllm-workstation",
        "--server-name",
        "Local JackLLM Workstation",
        "--model",
        "selected-model-id"
      ],
      "env": {
        "SOCKETJACK_COPILOT_SERVER_ID": "local-jackllm-workstation",
        "SOCKETJACK_COPILOT_MODEL_ID": "selected-model-id"
      }
    }
  }
}
```

### Ollama BYOM

The extension updates:

```text
%LOCALAPPDATA%\Microsoft\VisualStudio\Copilot\BringYourOwnModel\ConfiguredBringYourOwnModel_v1.json
```

It updates or creates the `Ollama` provider entry and points the selected model at either:

- The direct local JackLLM model-runtime endpoint, when available.
- A local loopback proxy, when Visual Studio needs the packaged bridge.

This is the supported fallback when Visual Studio or GitHub Copilot does not expose a public API for directly replacing Copilot's own selected chat model.

## Options

Open:

```text
Tools > Options > SocketJack LlmRuntime > Copilot Servers
```

Useful settings:

- `MasterList URLs`: optional remote SocketJack server list endpoints. Local JackLLM Workstation detection runs first.
- `Update Ollama BYOM`: enables or disables Visual Studio Ollama config updates.
- `Use local WebSocket proxy fallback`: starts a loopback proxy when direct local model access needs the packaged bridge.
- `Preferred local proxy port`: preferred port for the local proxy.
- `Local JackLLM URL`: local JackLLM endpoint for copilot duplicator notification.
- `SocketJack auth token`: optional token for remote SocketJack.com workflows. Local JackLLM Workstation configuration does not require it.

## After Configuring

1. Restart Visual Studio or reload Copilot MCP tools if Visual Studio prompts you.
2. Open Copilot Chat.
3. Switch to an agent/tools mode if required by your Visual Studio Copilot version.
4. Confirm SocketJack tools appear from the configured MCP server.

## Troubleshooting

### Tool Window Does Not Appear

- Restart Visual Studio after installing the VSIX.
- Check `Extensions > SocketJack > SocketJack Copilot Servers`.
- Confirm the VSIX is installed under `Extensions > Manage Extensions`.
- In Visual Studio 2026 Insiders, confirm `SocketJack for Visual Studio 2026` version `0.2.57` or later is installed.

### Tool Window Shows A XAML Frame Exception

Install `SocketJack for Visual Studio 2026` version `0.2.2` or later and restart Visual Studio. Version `0.2.0` used ListBox/ComboBox theme resources that Visual Studio 2026 Remote UI does not expose.

### MCP Bridge Not Found

Install `SocketJack for Visual Studio 2026` version `0.2.2` or later. Earlier VS 2026 builds could resolve the MCP bridge from the extension host folder instead of the installed extension folder.

### No Servers Load

- Click `Refresh`.
- Confirm JackLLM Workstation is running.
- Open `http://127.0.0.1:11436/api/health` locally and confirm it responds.
- If you are using optional remote servers instead of the local workstation, check internet access to `https://socketjack.com/api/lmvsproxy/servers`.
- Open the options page and confirm the MasterList URL list for optional remote workflows.

### Server Is Disabled

The server must be online/responding and advertise tools support. Select another server or wait for the server to come online.

### Model Is Disabled

The model must support chat and tools, and it must be loaded or dynamically loadable. The dropdown shows the disabled reason.

### Copilot Does Not Show Tools

- Confirm `.vs/mcp.json` exists in the solution directory.
- Restart Visual Studio.
- Confirm GitHub Copilot MCP support is enabled in your Visual Studio version.
- Reopen `Extensions > SocketJack > SocketJack Copilot Servers` and click `Configure Copilot` again.

### Direct Local Address Fails

For local JackLLM Workstation, confirm `http://127.0.0.1:11436/v1/chat/completions` is reachable through the workstation and that the selected model is loaded or loadable.

If Copilot reports an OpenAI `404 (Not Found)` after configuring local JackLLM Workstation, install version `0.2.57` or later, restart the Workstation, and configure the local server again. Visual Studio may normalize the Ollama BYOM URL to `http://127.0.0.1:11436/v1`; JackLLM Workstation now serves that route directly through `/v1/models` and `/v1/chat/completions`.

For optional remote servers, the extension checks the actual OpenAI chat paths and SocketJack fallback API before writing BYOM. If `https://socketjack.com/proxy/<SERVERNAME>` cannot expose chat completions directly, keep `Use local WebSocket proxy fallback` enabled. The extension will write BYOM to a local address like `http://127.0.0.1:11574`, stream Visual Studio's OpenAI chat-completions requests through SocketJack `/api/chat-stream`, and route model access through:

```text
wss://socketjack.com/proxy/<SERVERNAME>/api/web-chat/ws
```

If the server returns `503` with a message like `JackLLM has not connected its reverse agent`, the SocketJack server is listed but not currently reachable for model traffic. Wait for that server to reconnect or select another responding server.

### Ollama BYOM Did Not Change

- Confirm `Update Ollama BYOM` is enabled in options.
- Check the status text after `Configure Copilot`.
- Restart Visual Studio so Copilot reloads the BYOM configuration.
