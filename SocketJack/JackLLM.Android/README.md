# JackLLM Mobile

JackLLM Mobile is the Android companion app for JackLLM Workstation. It gives a phone or tablet a direct chat and session-management surface for a workstation running JackLLM, while keeping the same model, tool, and session context available on the desktop side.

![JackLLM Mobile chat](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-chat.png)

![JackLLM Mobile sessions](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-sessions.png)

## Features

| Surface | What it does |
|---|---|
| Mobile chat | Send prompts to JackLLM Workstation and read assistant replies from Android. |
| Session list | Browse workstation conversations and reopen them from the mobile client. |
| Advanced mode | Use the workstation as an agent surface for tool-using prompts and longer tasks. |
| Voice hooks | Mobile microphone and speech services are wired for Android-specific voice workflows. |
| Workstation telemetry | Show model, token, context, GPU, CPU, VRAM, and RAM status when the workstation reports it. |

## Project

| Item | Value |
|---|---|
| Path | `SocketJack/JackLLM.Android/` |
| App title | `JackLLM Mobile` |
| Assembly | `JackLLM.Mobile` |
| Target framework | `net10.0-android` |
| Minimum Android SDK | `21` |
| Package id | `com.socketjack.jackllm.mobile` |

## Related Packages

- [SocketJack](https://www.nuget.org/packages/SocketJack)
- [SocketJack.WPF](https://www.nuget.org/packages/SocketJack.WPF)
- [SocketJack repository](https://github.com/JackOfFates/SocketJack)
- [JackLLM Workstation README](https://github.com/JackOfFates/SocketJack/blob/master/JackLLM/README.md)
