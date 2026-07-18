# JackLLM Mobile

JackLLM Mobile is the shared Android and iOS companion app for JackLLM Workstation. It gives a phone or tablet a direct chat and session-management surface for a workstation running JackLLM, while keeping the same model, tool, and session context available on the desktop side.

<p align="center">
  <img src="https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-chat.png" alt="JackLLM Mobile chat UI" width="360">
  <img src="https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-sessions.png" alt="JackLLM Mobile sessions UI" width="360">
</p>

## Features

| Surface | What it does |
|---|---|
| Mobile chat | Send prompts to JackLLM Workstation and read assistant replies from Android or iOS. |
| Session list | Browse workstation conversations and reopen them from the mobile client. |
| Advanced mode | Use the workstation as an agent surface for tool-using prompts and longer tasks. |
| Voice hooks | Native microphone recording and generated-speech playback on Android and iOS. |
| PC Access | Stream the Workstation desktop, send touch/pointer input, and transfer approved files. |
| Workstation telemetry | Show model, token, context, GPU, CPU, VRAM, and RAM status when the workstation reports it. |

## Project

| Item | Value |
|---|---|
| Path | `SocketJack/JackLLM.Android/` |
| App title | `JackLLM Mobile` |
| Assembly | `JackLLM.Mobile` |
| Target frameworks | `net10.0-android`; `net10.0-ios` |
| Minimum OS | Android SDK `21`; iOS `15.0` |
| Package id | `com.socketjack.jackllm.mobile` |

## One shared Mobile app

Android and iOS are targets of the same MAUI project. `App.cs`, `Models/`, `Pages/`, `Services/`, and most of `Controls/` compile into both applications, so normal JackLLM Mobile feature changes land on both platforms automatically. Native integrations live under `Platforms/Android/` and `Platforms/iOS/`; a platform-specific change should keep the shared interface stable and update both implementations when the behavior applies to both phones.

The iOS Files workflow intentionally uses Apple's document picker and share/save sheet. Android keeps its persistent Storage Access Framework folder browser. Secure server tokens use Android Keystore-backed storage on Android and Keychain-backed storage on iOS through MAUI Secure Storage.

## Build

Android can be built on Windows:

```powershell
dotnet build .\JackLLM.Android\JackLLM.Android.csproj -f net10.0-android -c Release
```

The iOS code can be restored and compile-checked from Windows when the .NET iOS workload is installed, but creating, signing, and running an `.app` or `.ipa` requires Xcode on a paired Mac:

```powershell
dotnet build .\JackLLM.Android\JackLLM.Android.csproj -f net10.0-ios -c Release -r ios-arm64
```

Set the Apple Team/provisioning profile in Visual Studio or on the Mac before producing a device archive. The bundle identifier is `com.socketjack.jackllm.mobile`.

## Related Packages

- [SocketJack](https://www.nuget.org/packages/SocketJack)
- [SocketJack.WPF](https://www.nuget.org/packages/SocketJack.WPF)
- [SocketJack repository](https://github.com/JackOfFates/SocketJack)
- [JackLLM Workstation README](https://github.com/JackOfFates/SocketJack/blob/master/JackLLM/README.md)
