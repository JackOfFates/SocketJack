# Changelog

## 2026-06-04 - LlmRuntime.VisualStudio2026 0.2.36

### Root cause

- The VSIX packaged the preview extension with the Visual Studio 17.14+ compatibility range, which made the VS 2026 Insiders installer reject the package as targeting an older product lane.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.36`.
- Changed the extension target range to Visual Studio `18.x`.
- Updated the VSIX manifest postprocessor to emit `Community`, `Pro`, and `Enterprise` installation targets for Visual Studio `18.x`, all restricted to `amd64`.
- Fixed VS model discovery fallback parsing so MasterList `modelCapabilitiesJson` array strings preserve per-model `supportsTools` flags when `/api/models` and `/api/model-runtime/models` are unavailable.

### Verification

- Inspect the built VSIX `extension.vsixmanifest` and install it with the Visual Studio 2026 Insiders `VSIXInstaller.exe`.
- `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotServicesTests" --nologo -v:minimal`

## 2026-06-02 - LlmRuntime.VisualStudio2026 0.2.35

### Root cause

- VS Copilot could duplicate SocketJack LLM responses when consuming Responses API SSE output from the local `SocketJack.CopilotMcpBridge`.
- The bridge streamed visible text correctly through `response.output_text.delta`, then repeated the completed assistant text in terminal Responses events such as `response.output_text.done`, `response.content_part.done`, `response.output_item.done`, and `response.completed`.
- Copilot treated those terminal snapshots as additional visible text instead of final-state metadata.

### Changes made

- Updated `LlmRuntime.VisualStudio2026` package versioning to `0.2.35`.
- Updated marketplace release notes for the duplicated-response bridge fix.
- Changed the Responses API stream finish path so terminal events close the stream structurally without replaying assistant text.
- Added regression coverage to verify streamed Responses output contains the visible assistant text exactly once.

### Verification

- `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --filter "FullyQualifiedName~SocketJackCopilotBridgeTests" --nologo -v:minimal`
- `dotnet build ..\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj -c Release --nologo -v:minimal`
- `dotnet build ..\LlmRuntime.VisualStudio2026\LlmRuntime.VisualStudio2026.csproj -c Release --nologo -v:minimal`
