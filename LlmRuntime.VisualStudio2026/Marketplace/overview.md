# SocketJack for Visual Studio 2026

Connect Visual Studio Copilot to a local JackLLM Workstation. The local workstation flow does not require a SocketJack.com account, SocketJack.com sign-in, hosted server lease, or public login token.

![SocketJack Visual Studio extension](images/socketjack-visualstudio-extension.png)

## Features

- Detect a local JackLLM Workstation at `http://127.0.0.1:11436`.
- Configure Copilot-compatible MCP entries from local workstation models.
- Configure Visual Studio's Ollama Bring Your Own Model route to the local JackLLM model-runtime endpoint when supported.
- Fall back to the bundled SocketJack bridge for local MCP and model proxy routing when Visual Studio needs a loopback OpenAI-compatible bridge.
- Keep local Copilot/MCP setup independent of SocketJack.com login.
- Optional remote/server workflows can still use SocketJack.com account features when you choose to use them.

## Requirements

- Visual Studio 2026 Insiders, or Visual Studio 2022 17.14+ using the stable Visual Studio extension API compatibility model.
- A local JackLLM Workstation running on the same PC at `http://127.0.0.1:11436`.
- A tools-capable local model loaded or loadable in JackLLM Workstation.
- GitHub Copilot features enabled in Visual Studio when using Copilot/MCP workflows.

JackLLM Workstation does not currently have a standalone download for this flow. Build and run it from the GitHub project:

[JackLLM Workstation project](https://github.com/JackOfFates/SocketJack/tree/master/JackLLM)

## Getting Started

1. Install the extension in Visual Studio 2026.
2. Build and start JackLLM Workstation from the GitHub project.
3. Confirm `http://127.0.0.1:11436/api/health` responds locally.
4. Open `Extensions > SocketJack > Copilot Servers`.
5. Choose `Local JackLLM Workstation`, choose a tools-capable model, and select `Configure`.

When the local workstation is detected, the SocketJack.com sign-in command is hidden and local configuration proceeds without a SocketJack.com token.

## Links

- SocketJack: https://socketjack.com/
- Source: https://github.com/JackOfFates/SocketJack
- JackLLM Workstation project: https://github.com/JackOfFates/SocketJack/tree/master/JackLLM
- License: https://github.com/JackOfFates/SocketJack/blob/master/LICENSE
