# SocketJack for Visual Studio 2026

SocketJack for Visual Studio 2026 configures GitHub Copilot and MCP against a local JackLLM Workstation. For the local workstation flow, it does not require a SocketJack.com account, SocketJack.com sign-in, hosted MasterList server, or public SocketJack.com login token.

## Local Requirement

Run JackLLM Workstation on the same PC before configuring the extension:

```text
http://127.0.0.1:11436
```

The extension detects JackLLM through:

```text
http://127.0.0.1:11436/api/health
```

JackLLM Workstation does not currently have a standalone download for this flow. Build and run it from the GitHub project:

[JackLLM Workstation project](https://github.com/JackOfFates/SocketJack/tree/master/JackLLM)

## Setup

1. Build and start JackLLM Workstation from the GitHub project.
2. Confirm `http://127.0.0.1:11436/api/health` responds locally.
3. Install the VSIX and restart Visual Studio.
4. Open `Extensions > SocketJack > Copilot Servers`.
5. Select `Local JackLLM Workstation`, choose a tools-capable model, and click `Configure`.

When the local workstation is detected, the extension hides the SocketJack.com sign-in flow and writes local Copilot/MCP configuration with no SocketJack.com auth token.

## What Gets Configured

- Solution-local MCP config at `.vs/mcp.json`.
- Visual Studio Ollama BYOM routing to the local JackLLM model-runtime endpoint or the packaged loopback bridge when needed.
- Local JackLLM copilot duplicator state through `http://127.0.0.1:11436/api/copilot-duplicator` when available.

SocketJack.com account features are only for optional remote/server workflows. They are not required for local JackLLM Workstation configuration.
