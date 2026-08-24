# heirowLLM Workstation for Visual Studio

Use supported Hugging Face models and other local LLMs inside Visual Studio Copilot through a local heirowLLM Workstation. The extension has no hosted account, remote server directory, or cloud inference requirement. Sign-in is handled only by the heirowLLM Workstation running on this PC.

## Local architecture

```text
Local model
    |
    v
heirowLLM Workstation at http://127.0.0.1:11436
    |
    +-- OpenAI-compatible API for Visual Studio Copilot BYOM
    +-- Local MCP bridge for tools and solution context
    `-- Local Session Sync API
```

## Requirements

Build and run [heirowLLM Workstation](https://github.com/JackOfFates/SocketJack/tree/master/heirowLLM) on the same PC as Visual Studio. The extension checks:

```text
http://127.0.0.1:11436/api/health
```

A Hugging Face access token may be required only when downloading gated models.

## Setup

1. Start heirowLLM Workstation.
2. Download or import a compatible local model and load it.
3. Confirm `http://127.0.0.1:11436/api/health` responds.
4. Install the VSIX and restart Visual Studio.
5. Open `Extensions > SocketJack > Copilot Servers`.
6. Sign in with an account created inside heirowLLM Workstation.
7. Select the local model and click `Configure`.

The extension writes solution-local MCP configuration to `.vs/mcp.json`, configures Visual Studio BYOM against the loopback Workstation API, and uses only the local Workstation Session Sync routes.
