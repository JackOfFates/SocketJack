# SocketJack.WPF 2026

![SocketJack WPF Icon](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack.Windows/SocketJackWpfIcon.png)

[![NuGet](https://img.shields.io/nuget/v/SocketJack.WPF.svg)](https://www.nuget.org/packages/SocketJack.WPF)
[![NuGet Downloads](https://img.shields.io/nuget/dt/SocketJack.WPF.svg)](https://www.nuget.org/packages/SocketJack.WPF)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://github.com/JackOfFates/SocketJack/blob/master/LICENSE)

SocketJack.WPF 2026 adds live WPF control sharing, remote input, and browser-friendly remote administration to the SocketJack networking platform.

Share any `FrameworkElement` as a live image stream, view it from another peer, and route remote mouse, wheel, text, and keyboard input back into WPF without moving the real Windows cursor.

<a id="versions"></a>
## #Versions

| Package / surface | Version | Target |
|---|---:|---|
| [`SocketJack.WPF`](https://www.nuget.org/packages/SocketJack.WPF) | `2026.4` | `net8.0-windows7.0`, `net10.0-windows7.0` |
| [`SocketJack`](https://www.nuget.org/packages/SocketJack) | `2026.8` | `.NET Standard 2.1` |
| `JackLLM Workstation` | `2026.0` | WPF app metadata |
| `JackLLM Mobile` | `1.0` | Android app |
| `JackLLM Workstation Linux` | `1:26.0.1` | Debian-compatible package version for the 2026 line |

<details open>
<summary><strong>#Install</strong> - add WPF remote control to a SocketJack app</summary>

```powershell
dotnet add package SocketJack.WPF
```

Or with Package Manager:

```powershell
Install-Package SocketJack.WPF
```

Use this package when the network should see, share, or control a WPF UI. Use the core `SocketJack` package by itself when you only need TCP, UDP, HTTP, WebSockets, SQL/data, streaming, and peer routing.

</details>

<details>
<summary><strong>#What It Does</strong> - live capture, remote viewing, and direct WPF input</summary>

| Capability | What it means |
|---|---|
| Live capture | Stream any `FrameworkElement`, including panels, canvases, controls, or windows. |
| Viewer integration | Decode incoming frames into WPF image controls with a small extension-method surface. |
| Remote input | Forward pointer movement, clicks, wheel events, text, and keyboard commands to WPF. |
| Peer transport | Ride on SocketJack connections, peer identity, and metadata flows. |
| Browser admin | Power JackLLM Workstation Remote Admin through the web console. |
| Shared core stack | Benefit from SocketJack 2026 TCP, UDP, HTTP, WebSocket, `MutableTcpServer`, RTMP, SQL/TDS, TLS, compression, and P2P support. |

</details>

<details>
<summary><strong>#How It Works</strong> - host, stream, view, and control</summary>

1. A WPF host shares a `FrameworkElement` over a connected SocketJack peer.
2. SocketJack.WPF captures the element as JPEG frames at the configured frame rate.
3. A viewer renders frames into an `Image`.
4. Viewer input is translated into WPF actions and sent back to the original element.
5. JackLLM Remote Admin uses the same path to let the browser operate the WPF JackLLM Workstation UI.

```csharp
using SocketJack.WPF;

IDisposable shareHandle = GameCanvas.Share(client, remotePeer, fps: 10);
IDisposable viewerHandle = client.ViewShare(SharedImage, sharerPeer);
```

</details>

<details>
<summary><strong>#JackLLM Remote Admin</strong> - browser control for the workstation UI</summary>

JackLLM Workstation can register a WPF host with SocketJack.WPF so the browser console can view and operate the desktop GUI. This is useful for remote workstations where the model server, proxy, billing, permissions, diagnostics, and node metadata are managed from a browser.

![JackLLM Remote Admin](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/3.jpg)

</details>

<details>
<summary><strong>#JackLLM Mobile</strong> - Android companion for workstation sessions</summary>

JackLLM Mobile extends the JackLLM Workstation flow to Android. It can send prompts, view assistant responses, monitor model and compute status, and reopen workstation sessions from a phone or tablet.

![JackLLM Mobile chat](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-chat.png)

![JackLLM Mobile sessions](https://raw.githubusercontent.com/JackOfFates/SocketJack/master/SocketJack/JackLLM.Android/docs/images/jackllm-mobile-sessions.png)

Project README: [SocketJack/JackLLM.Android/README.md](https://github.com/JackOfFates/SocketJack/blob/master/SocketJack/JackLLM.Android/README.md)

</details>

<details>
<summary><strong>#Documentation</strong> - examples and related packages</summary>

- [Examples](https://github.com/JackOfFates/SocketJack/blob/master/examples.md)
- [SocketJack.WPF package](https://www.nuget.org/packages/SocketJack.WPF)
- [SocketJack package](https://www.nuget.org/packages/SocketJack)
- [JackLLM Mobile README](https://github.com/JackOfFates/SocketJack/blob/master/SocketJack/JackLLM.Android/README.md)
- [GitHub repository](https://github.com/JackOfFates/SocketJack)

</details>

## License

SocketJack.WPF is open source and licensed under the [MIT License](https://github.com/JackOfFates/SocketJack/blob/master/LICENSE).

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>9,050 lines / 16 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack.Windows</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| XML | 1 | 5,161 |
| C# | 12 | 3,200 |
| MSBuild/XML | 1 | 466 |
| EditorConfig | 1 | 143 |
| Markdown | 1 | 80 |
| **Total** | **16** | **9,050** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
