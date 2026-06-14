# LLM Consistency Test Summary

Date: 2026-06-10
Machine: TitanX server, this PC
Workspace: `C:\Users\Vin\Documents\GitHub\SocketJack\SocketJack`

## Scope

Validated the LLM consistency fix against real LM Studio and the local LlmRuntime-backed JackLLM server. The goal was to stop visible clipping, avoid prompt-specific shortcuts, preserve LM Studio-compatible `finish_reason` behavior, and allow recursive continuation without a fixed pass cap while tokens/server capacity remain available.

## Changed Areas

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
  - Removed the fixed auto-continuation pass limiter.
  - Continuation is now driven by output-limit/abrupt-stream finish reasons plus token-capacity checks and cancellation.
  - `/api/chat-stream` emits `auto_continuation` progress events whenever it recursively continues a clipped response.

- `LlmRuntime/LlamaSharpBackend.cs`
  - Fixed the live `cuda12`/LLamaSharp path, which previously hardcoded `FinishReason = "stop"`.
  - Tracks generated tokens and emits final stream finish metadata.
  - Treats hidden-reasoning-only output as `length`, so JackLLM can continue instead of accepting an empty visible answer as complete.

- `LlmRuntime/DirectMlRunner/DirectMlRunnerProgram.cs`
  - Emits `length` when generated tokens reach the cap.
  - Treats unfinished or hidden-reasoning-only output as output-limited.

- `LlmRuntime/DirectMlGgufBackend.cs` and `LlmRuntime/LlmRuntimeHost.cs`
  - Preserve runner/OpenAI finish reasons.
  - Strip hidden reasoning tags from visible content.
  - Collapse whitespace-only remnants to empty visible content.

## Automated Verification

```powershell
dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal
```

Result: 274 passed, 0 failed, 0 skipped.

```powershell
dotnet build ..\JackLLM\JackLLM.csproj --no-restore --nologo -v:minimal -maxcpucount:1
```

Result: build succeeded, 0 warnings, 0 errors.

The DirectML runner was also rebuilt with:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\Install-DirectMlGgufRunner.ps1 -Configuration Debug -SkipNativeSockJackDml
```

## Live TitanX Verification

- JackLLM PID: `20868`
- Health endpoint: `http://127.0.0.1:11436/api/health`
- Runtime endpoint: `http://127.0.0.1:11435/v1/chat/completions`
- LM Studio comparison endpoint: `http://127.0.0.1:1234/v1/chat/completions`
- Model: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Live backend observed for LlmRuntime: `cuda12` / `cuda`

## LM Studio Comparison

Same model, same prompts, same low token caps:

| Endpoint | Test | max_tokens | finish_reason | completion_tokens | visible chars |
| --- | --- | ---: | --- | ---: | ---: |
| LM Studio | exact | 5 | length | 5 | 0 |
| LM Studio | forced paragraph | 5 | length | 5 | 0 |
| LM Studio | numbered list | 16 | length | 16 | 0 |
| LlmRuntime | exact | 5 | length | 5 | 0 |
| LlmRuntime | forced paragraph | 5 | length | 7 | 0 |
| LlmRuntime | numbered list | 16 | length | 18 | 0 |

Result: LlmRuntime now matches LM Studio's visible-content behavior and output-limit finish reason for the clipping probes.

## Recursive Continuation Probe

Endpoint:

```text
POST http://127.0.0.1:11436/api/chat-stream
```

Forced low-budget request:

- `max_tokens`: 5
- Prompt requested a detailed paragraph.

Result:

- Observed `auto_continuation` progress events: 4
- The client intentionally cancelled after the fourth event.
- JackLLM stayed healthy after cancellation.
- This confirms continuation is not stopped by a fixed 3-pass cap.

## Conclusion

The TitanX server now treats clipped/hidden-only low-budget model output like LM Studio, surfaces `finish_reason: "length"` through both CUDA and DirectML paths, and recursively continues clipped JackLLM chat streams without a hardcoded pass limit.
