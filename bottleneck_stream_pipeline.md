# LLM Stream Pipeline Bottleneck Report

## Current Stream Rate

**Current JackLLM stream speed: 94.88 chars/sec** on `http://127.0.0.1:11436/api/chat-stream` as of `2026-05-29 07:05 America/Chicago`.

- Test artifact: `artifacts/stream-bench-jackllm-11436-current-20260529.json`
- Model: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- First content: `1,853 ms`
- Total stream time: `11,286 ms`
- Usage frames: `4`

## Goal

Make JackLLM/LlmRuntime responses stream at the rate the model infers tokens, across:

- Direct runtime: `http://127.0.0.1:11435/v1/chat/completions`
- Local JackLLM web chat: `http://127.0.0.1:11436/api/chat-stream`
- VS Copilot bridge: `http://127.0.0.1:11574/v1/chat/completions`
- Source bridge test port: `http://127.0.0.1:11576/v1/chat/completions`
- SocketJack networking primitives used by streaming routes

## Models Used

- Benchmarked model: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Direct LlmRuntime reported runtime model: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF@cuda-0`
- Not included in benchmark deltas: the installed sable bridge on `11575` advertised `Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled.Q5_K_M`, but its configured upstream `127.0.0.1:12435` was unreachable during the initial probe.

## Benchmark Method

- Tool: `tools/Benchmark-LlmStreamPipeline.ps1`
- Prompt: `Write 40 numbered lines. Each line must say: stream speed test line N. Do not add anything else.`
- `max_tokens`: `256`
- Formats:
  - `OpenAiSse` for direct runtime and bridge paths.
  - `JackLlmNdjson` for JackLLM web chat.
- `chars/sec` is measured from first content to stream end and includes content plus reasoning text.
- Percent speed change:
  - Total time decrease: `(oldTotalMs - newTotalMs) / oldTotalMs`
  - Throughput increase: `(newCharsPerSecond - oldCharsPerSecond) / oldCharsPerSecond`

## Benchmark Results

| Test | Goal | Artifact | Endpoint | Model | First content | Total | Frames | Usage frames | Chars/sec | Result |
| --- | --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| Initial direct probe | Confirm runtime availability before patching | `artifacts/stream-bench-direct-11435.json` | `11435` | `Qwen3.5...` | n/a | 2,084 ms | 0 | 0 | 0.00 | Failed before headers; JackLLM/runtime ports were down. |
| Direct runtime baseline | Establish raw inference stream speed | `artifacts/stream-bench-direct-11435-long-after.json` | `11435` | `Qwen3.5...` | 962 ms | 9,159 ms | 259 | 0 | 117.60 | Runtime itself streams normally. |
| JackLLM slow path | Reproduce local web chat bottleneck | `artifacts/stream-bench-jackllm-11436-long-after.json` | `11436` | `Qwen3.5...` | 3,231 ms | 94,224 ms | 184 | 87 | 10.81 | Bottleneck reproduced. |
| Direct runtime repeat | Compare with same model after restart | `artifacts/stream-bench-direct-11435-long-after2.json` | `11435` | `Qwen3.5...` | 964 ms | 9,058 ms | 259 | 0 | 129.73 | Runtime remained fast. |
| JackLLM after accounting fix | Verify usage/accounting coalescing | `artifacts/stream-bench-jackllm-11436-long-after2.json` | `11436` | `Qwen3.5...` | 2,852 ms | 11,922 ms | 215 | 4 | 116.32 | Main bottleneck fixed. |
| JackLLM repeat after accounting fix | Confirm result was repeatable | `artifacts/stream-bench-jackllm-11436-long-after3.json` | `11436` | `Qwen3.5...` | 1,917 ms | 11,057 ms | 202 | 4 | 112.36 | Fix held on repeat. |
| Installed VS bridge | Measure installed bridge after JackLLM core fix | `artifacts/stream-bench-vsbridge-11574-long-after2.json` | `11574` | `Qwen3.5...` | 6,065 ms | 19,301 ms | 213 | 0 | 77.14 | Still had high first-content latency. |
| Source bridge local path | Test bridge source patch using local JackLLM | `artifacts/stream-bench-vsbridge-11576-localdirect-after2.json` | `11576` | `Qwen3.5...` | 2,293 ms | 19,345 ms | 222 | 0 | 64.63 | First-content latency improved sharply. |
| Direct after low-latency/cache | Check direct runtime after low-latency/cache changes | `artifacts/stream-bench-direct-11435-lowlatency-cache.json` | `11435` | `Qwen3.5...` | 1,270 ms | 9,930 ms | 259 | 0 | 114.43 | Direct path stayed in same range. |
| JackLLM after low-latency/cache | Verify local web chat after final pass | `artifacts/stream-bench-jackllm-11436-lowlatency-cache.json` | `11436` | `Qwen3.5...` | 2,841 ms | 14,294 ms | 224 | 4 | 73.43 | Usage frames stayed coalesced; model output varied. |
| Installed VS bridge after final pass | Compare installed bridge without reinstalling extension DLL | `artifacts/stream-bench-vsbridge-11574-lowlatency-cache.json` | `11574` | `Qwen3.5...` | 3,872 ms | 20,388 ms | 224 | 0 | 69.39 | Installed bridge still uses old installed DLL. |
| Source bridge after final pass | Measure patched bridge source again | `artifacts/stream-bench-vsbridge-11576-localdirect-lowlatency-cache.json` | `11576` | `Qwen3.5...` | 1,639 ms | 17,365 ms | 229 | 0 | 57.68 | Better first-content and total time than installed bridge on this run. |
| Documented direct run | Persist model/goal fields in benchmark artifact | `artifacts/stream-bench-direct-11435-documented.json` | `11435` | `Qwen3.5...` | 1,183 ms | 19,101 ms | 259 | 0 | 52.24 | Metadata capture verified; wall time varied due reasoning output. |
| Documented JackLLM run | Persist model/goal fields in benchmark artifact | `artifacts/stream-bench-jackllm-11436-documented.json` | `11436` | `Qwen3.5...` | 3,370 ms | 23,055 ms | 250 | 5 | 54.46 | Metadata capture verified; usage stayed low. |
| Documented source bridge run | Persist model/goal fields in benchmark artifact | `artifacts/stream-bench-vsbridge-11576-documented.json` | `11576` | `Qwen3.5...` | 2,118 ms | 21,687 ms | 241 | 0 | 55.39 | Metadata capture verified. |
| Current JackLLM stream rate | Highlight live local web chat chars/sec at top of report | `artifacts/stream-bench-jackllm-11436-current-20260529.json` | `11436` | `Qwen3.5...` | 1,853 ms | 11,286 ms | 240 | 4 | 94.88 | Current measured stream rate for the top callout. |

## Percent Changes

| Comparison | Total time change | Throughput change | First-content change | Notes |
| --- | ---: | ---: | ---: | --- |
| JackLLM slow path -> JackLLM after accounting fix | 87.3% decrease | 976.0% increase | 11.7% decrease | `94,224 ms` -> `11,922 ms`, `10.81` -> `116.32 chars/sec`. |
| JackLLM slow path -> best JackLLM repeat | 88.3% decrease | 939.4% increase | 40.7% decrease | `94,224 ms` -> `11,057 ms`; best stable post-fix repeat. |
| JackLLM usage frames | 95.4% decrease | n/a | n/a | `87` usage frames -> `4`; this was the root event-amplification bottleneck. |
| Direct runtime repeat -> JackLLM after accounting fix | 31.6% total overhead | 10.3% lower throughput | 195.9% later first content | Remaining proxy cost after the main fix. |
| Installed VS bridge -> source bridge local path | 0.2% total increase | 16.2% lower throughput | 62.2% decrease | Source bridge cut first-content latency, but total varied with generated text. |
| Installed VS bridge final -> source bridge final | 14.8% decrease | 16.9% lower throughput | 57.7% decrease | Source bridge total improved on the final comparable run; chars/sec varied with output length. |

Overall, the reproduced JackLLM bottleneck changed from a 94.2 second stream to an 11.1-11.9 second stream on repeat tests. That is an 87-88% total time reduction and roughly a 9.4x to 9.8x streaming throughput increase. The remaining spread in later documented runs comes from the reasoning model producing different mixes of visible answer versus reasoning content, so the most stable bottleneck indicator is the usage-frame collapse from 87 to 4-5.

## Root Cause

The common slow path was event amplification plus synchronous accounting during streaming:

1. LlmRuntime emitted fast OpenAI-compatible SSE deltas.
2. JackLLM converted those into NDJSON deltas.
3. After nearly every content delta, JackLLM also updated chat costs/session data and emitted a large `usage` event.
4. Each event flushed as a chunked HTTP response.
5. Browser chat and the VS bridge both paid JSON serialization, chunk framing, TCP flush, parsing, and dispatch overhead for non-content telemetry.

LM Studio stayed fast because it did not traverse this JackLLM accounting/proxy path.

## Changes Made

- Added `tools/Benchmark-LlmStreamPipeline.ps1`.
  - Supports `OpenAiSse` and `JackLlmNdjson`.
  - Captures first byte, first frame, first content, total time, frame counts, usage/progress/done frames, content/reasoning chars, throughput, errors, model, max tokens, and test goal.
- Coalesced JackLLM chat usage telemetry in `SocketJack.LlmCore/Proxy/JackLLM.cs`.
  - Before: every emitted content delta could be followed by a full `usage` event.
  - After: usage events are written immediately for the first update, then at most every 250 ms or every 32 charged tokens, with a forced final usage flush before `done`.
- Batched chat usage charging/resource accounting.
  - Queues token/resource deltas in `ChatUsageMeter`.
  - Flushes every 5 seconds, every 128 tokens, or at stream end.
  - Preserves token-limit checks by counting pending tokens before allowing another delta.
- Added dynamic caches in JackLLM.
  - `ChatCostSettings` cache keyed by `_chatDataVersion`, cloned on read, invalidated on settings/session saves.
  - `ChatUsageSnapshot` cache keyed by owner/session, versioned by `_chatDataVersion`, max 512 entries with oldest-entry eviction.
  - Cache invalidation clears session lists, solution storage, usage snapshots, and cost settings when chat data changes.
- Added low-latency streaming support to SocketJack networking.
  - `HttpResponse.LowLatencyStreaming`
  - `ChunkedStream.LowLatencyMode`
  - `ChunkedStream.SetHeader(...)`
  - Low-latency headers: `X-Accel-Buffering: no`, `Pragma: no-cache`, `Cache-Control: no-cache, no-store, no-transform`.
- Optimized `SocketJack/Net/HttpServer.cs` chunk writes.
  - Before: each chunk used three stream writes (`size`, `body`, `CRLF`) and then flushed.
  - After: each chunk is assembled into one packet, written once, and flushed once.
- Opted stream routes into low-latency mode.
  - `LlmRuntime/LlmRuntimeHost.cs` `/v1/chat/completions`
  - `SocketJack.LlmCore/Proxy/JackLLM.cs` `/api/chat-stream`
  - `SocketJack.LlmCore/Proxy/JackLLM.cs` `/api/llm-client/chat-stream`
- Patched `SocketJack.CopilotMcpBridge/Program.cs`.
  - Defaults model discovery/chat stream forwarding to local JackLLM `http://127.0.0.1:11436/` when available.
  - Added `--local-webchat-endpoint`, `--disable-local-webchat`, and `SOCKETJACK_COPILOT_DISABLE_LOCAL_WEBCHAT=1` support.

## Verification

- `dotnet build .\LlmRuntime\LlmRuntime.csproj -c Debug --nologo -v:minimal` succeeded.
- `dotnet build .\JackLLM\JackLLM.csproj -c Debug --nologo -v:minimal` succeeded.
- `dotnet build .\SocketJack.CopilotMcpBridge\SocketJack.CopilotMcpBridge.csproj -c Debug --nologo -v:minimal` succeeded.
- Restarted rebuilt `JackLLM.exe`; verified listeners on `11435` and `11436`.
- Ran direct runtime, JackLLM, installed bridge, and source bridge streaming benchmarks.

## Remaining Notes

- The installed VS bridge on `11574` is still the installed extension DLL. The source bridge patch was tested on `11576`; installing/updating the VS extension is required for `11574` to pick up that bridge code.
- The reasoning model does not produce identical text across runs. For that reason, percent changes based on total time/chars/sec should be read together with frame counts. The frame count change is the strongest signal: JackLLM usage telemetry dropped from 87 frames to 4-5 frames.
- The current biggest remaining overhead is bridge/proxy first-content latency and per-frame conversion cost, not the original per-token accounting delay.
