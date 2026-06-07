# SocketJack for Visual Studio 2026

Connect Visual Studio Copilot to SocketJack tools-capable model servers and keep your coding sessions connected to the same SocketJack.com account, server picker, MCP configuration, Ollama BYOM routing, and Session Sync workflow.

![SocketJack Visual Studio extension](images/socketjack-visualstudio-extension.png)

## Features

- Browse SocketJack.com MasterList servers from inside Visual Studio.
- Select enabled tools-capable models and configure Copilot-compatible MCP entries.
- Configure Visual Studio's Ollama Bring Your Own Model route to the selected SocketJack server when supported.
- Fall back to the bundled SocketJack bridge for local MCP and model proxy routing.
- Use Session Sync to push and pull solution files between Visual Studio and SocketJack Auto Tools sessions.
- Share SocketJack sign-in state between the Copilot Servers and Session Sync windows.

## Requirements

- Visual Studio 2026 Insiders, or Visual Studio 2022 17.14+ using the stable Visual Studio extension API compatibility model.
- A SocketJack.com account.
- A tools-capable SocketJack server and model from the MasterList.
- GitHub Copilot features enabled in Visual Studio when using Copilot/MCP workflows.

## Getting Started

1. Install the extension in Visual Studio 2026.
2. Open `Extensions > SocketJack > Sign-in` and sign in to SocketJack.com.
3. Open `Extensions > SocketJack > Copilot Servers`.
4. Refresh the server list, choose an online tools-capable server, choose an enabled model, and select `Configure`.
5. Open `Extensions > SocketJack > Session Sync` when you want to push or pull solution files for an Auto Tools session.

## Links

- SocketJack: https://socketjack.com/
- Source: https://github.com/JackOfFates/SocketJack
- License: https://github.com/JackOfFates/SocketJack/blob/master/LICENSE
