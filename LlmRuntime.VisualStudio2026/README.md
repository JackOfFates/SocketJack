# SocketJack for Visual Studio 2026

Use supported Hugging Face models and other local LLMs inside Visual Studio Copilot through the SocketJack BYOM + MCP integration. JackLLM Workstation downloads, loads, and runs the model locally; the SocketJack extension connects Visual Studio Copilot to that local model and configures the SocketJack MCP tools for the solution.

> [!IMPORTANT]
> **JackLLM Workstation is required for local use as of June 29, 2026. SocketJack.com is not live yet.** Run JackLLM Workstation on the same PC as Visual Studio. The local workflow does not require a SocketJack.com account, sign-in, hosted MasterList server, or public SocketJack.com login token.

## Hugging Face Models in Visual Studio Copilot

The local workflow is:

```text
Supported Hugging Face model or other local LLM
                      |
                      v
JackLLM Workstation downloads, loads, and runs the model
                      |
                      v
Local OpenAI-compatible API at http://127.0.0.1:11436
                      |
                      v
SocketJack Visual Studio extension
  |-- GitHub Copilot BYOM routes prompts to the local model
  `-- SocketJack MCP gives Copilot local tools and solution context
```

The model runs on your workstation. Model prompts and inference do not need to be sent to SocketJack.com.

## Local Requirement

Build and run JackLLM Workstation from the SocketJack GitHub project before configuring the extension:

[JackLLM Workstation project](https://github.com/JackOfFates/SocketJack/tree/master/JackLLM)

The Workstation must be available at:

```text
http://127.0.0.1:11436
```

The extension detects JackLLM through:

```text
http://127.0.0.1:11436/api/health
```

JackLLM Workstation does not currently have a standalone download for this flow. A Hugging Face access token may also be required when downloading gated or authenticated models.

## Setup

1. Build and start JackLLM Workstation from the GitHub project.
2. Download or import a supported Hugging Face model or another compatible local model in JackLLM Workstation.
3. Load the model and confirm `http://127.0.0.1:11436/api/health` responds locally.
4. Install the VSIX and restart Visual Studio.
5. Open `Extensions > SocketJack > Copilot Servers`.
6. Select `Local JackLLM Workstation`, choose the loaded tools-capable model, and click `Configure`.
7. Open Visual Studio Copilot and use the selected local model with the SocketJack MCP tools.

When the local workstation is detected, the extension hides the SocketJack.com sign-in flow and writes local Copilot/MCP configuration with no SocketJack.com auth token.

## What Gets Configured

- Solution-local MCP config at `.vs/mcp.json`.
- Visual Studio Ollama BYOM routing to `http://127.0.0.1:11436`, which JackLLM serves through `/v1/models` and `/v1/chat/completions`, or the packaged loopback bridge when needed.
- Local JackLLM copilot duplicator state through `http://127.0.0.1:11436/api/copilot-duplicator` when available.

Until SocketJack.com is live, use the local JackLLM Workstation workflow described above. SocketJack.com account features are not required for local model configuration.
