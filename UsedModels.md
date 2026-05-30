# Used Models

## /Auto validation - 2026-05-22 America/Chicago

Surface tested: `https://socketjack.com/Auto`

Published channel: `socketjack-magic-master-list`

Latest publisher log: `C:\Users\Vin\Documents\GitHub\SocketJack\log_sess05-22-2026--2053-02-51.txt`

Validation:
- `dotnet build ..\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore` passed.
- Embedded `/Auto` script passed `node --check`.
- Publisher reported server metadata matched the local payload after the lost complete response.

### Auto Router

Observed router server: `TitanX`

Observed router transport: `socketjack-master-relay`

Router model shown by `/Auto` progress: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`

Router behavior note: the live page still displayed `(fallback)` on the route decision when the light router did not answer quickly enough. The page now prints the attempted router server/model so this is visible instead of silent.

### Text Prompt

Prompt: `Reply with exactly: logged text ok`

Auto decision line observed: `GENERATE TEXT -> Text via TitanX / Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF (fallback)`

Text generation server: `TitanX`

Text generation transport: `socketjack-master-relay`

Text generation runtime: `LlmRuntime`

Text generation model used: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`

Available text models reported by status:
- `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`

Observed result: visible answer was `logged text ok`. No `cerspense`, `zeroscope`, or video-generation endpoint error appeared in the text route.

### Video Prompt

Prompt: `cat video`

Observed video generation server: `TitanX`

Observed video generation transport: `socketjack-master-relay`

Observed video generation runtime: `LlmRuntime`

Observed video generation model: `cerspense-zeroscope_v2_576w-pytorch`

Status endpoint model listing: `cerspense-zeroscope_v2_576w`

Observed result: the Auto page reached `Running video generation with cerspense-zeroscope_v2_576w-pytorch...`. The browser was reloaded after route verification instead of waiting for the long video render to finish.

### Image Eligibility

Probe: `GET https://socketjack.com/auto/api?mode=image&origin=hybrid&premiumModels=true`

Observed result: `503 Service Unavailable`, `eligible_servers: 0`, `No healthy Auto servers are available for image.`

Important check: the zeroscope video model is no longer advertised as an image-generation target.

### Final Status Probes

Text status selected `TitanX` and reported the Qwen text models.

Video status selected `TitanX` and reported the zeroscope video model.

Image status returned no eligible servers.

## /Auto validation - 2026-05-23 America/Chicago

Surface tested: `https://socketjack.com/Auto`

Published channel: `socketjack-magic-master-list`

Latest publisher log: `C:\Users\Vin\Documents\GitHub\SocketJack\log_sess05-23-2026--0625-33-99.txt`

Validation:
- `dotnet build .\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore` passed.
- Embedded `/Auto` script blocks passed `node --check`.
- Publisher reported server metadata matched the local payload after the lost complete response.
- `/auto/sessions` returned `ok: true` with an empty anonymous session list, confirming the new session endpoint is live.

### Browser Probe

The live `/Auto` page loaded the new script with:
- session drawer button present
- text parser support for `data`, `value`, and `token` event fields
- missing-text error text present
- detailed mode/origin tooltips present

The saved session drawer showed session rows with server metadata and `TitanX >` server-open links for previously saved Auto sessions.

The in-app browser connector then lost its active pane after a temporary API tab was opened and closed, so later verification used direct `curl.exe` probes against the public same-origin endpoints.

### Direct Auto Status

Final text status probe:

`GET https://socketjack.com/auto/api?mode=text`

Observed result: `503 Service Unavailable`, `eligible_servers: 0`, `No healthy benchmarked Auto servers are available for text.`

This is expected with the final rules because the live server inventory currently reports disconnected runtimes and no benchmark numbers for the visible text models.

### Server Inventory Observed

Sable:
- server id: `lmvs-shell-410c6a730b24f473`
- runtime status: `Disconnected`
- last status: `Shell proxy online - JackLLM reverse agent connected`
- model runtime connected flags: false
- models observed:
  - `ali-vilab-text-to-video-ms-1.7b-pytorch`, video generation, loaded/enabled, benchmark numbers all `0`
  - `Qwen3-4B-Thinking-2507-Distill-Claude-Opus-4.6-Reasoning-Abliterated.Q4_K_M`, text/tools/vision, loaded/enabled, benchmark numbers all `0`
  - `crynux-network-stable-diffusion-v1-5-pytorch`, image generation, enabled, benchmark numbers all `0`

TitanX:
- server id: `lmvs-shell-05d29369622672e5`
- runtime status: `Disconnected`
- last status: `Shell proxy online - JackLLM reverse agent connected`
- model runtime connected flags: false
- models observed:
  - `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`, text/tools/vision, loaded/enabled, benchmark numbers all `0`
  - `cerspense-zeroscope_v2_576w-pytorch`, video generation, enabled, benchmark numbers all `0`
  - `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`, text/tools/vision, enabled, benchmark numbers all `0`
  - `Qwen3.5-9B-Claude-4.6-Opus-Reasoning-Distilled-v2-GGUF`, text/tools/vision, disabled, benchmark numbers all `0`

### Failure Reproduction

Before the disconnected-runtime filter was added, a direct text POST selected Sable for text with header model:

`Qwen3-4B-Thinking-2507-Distill-Claude-Opus-4.6-Reasoning-Abliterated.Q4_K_M`

The Sable-side stream then reported:

`Checking LlmRuntime model ali-vilab-text-to-video-ms-1.7b-pytorch...`

and ended with:

`Connection refused. (127.0.0.1:12435)`

Fix applied: disconnected runtime inventory is no longer enough to make a server eligible for text/tools, generation-only models are rejected before chat/tool capability checks, and models with no benchmark signal are excluded from Auto.

## /Auto validation - 2026-05-23 09:32 America/Chicago

Surface tested: `https://socketjack.com/Auto`

Published channel: `socketjack-magic-master-list`

Latest publisher logs:
- `C:\Users\Vin\Documents\GitHub\SocketJack\log_sess05-23-2026--0930-55-79.txt`
- Earlier supporting publishes: `log_sess05-23-2026--0906-42-04.txt`, `log_sess05-23-2026--0912-30-17.txt`, `log_sess05-23-2026--0915-02-16.txt`, `log_sess05-23-2026--0927-02-17.txt`

Build checks:
- `dotnet build .\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj --no-restore` passed with the existing nullable warnings.
- Embedded `/Auto` script blocks passed `node --check` across 5 script blocks.
- Publisher verified server metadata matched the local payload after the lost complete response.

Live eligible status after the final patch:
- Text: `GET /auto/api?mode=text` returned `ok: true`, `eligible_servers: 1`, selected `TitanX`, models `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF, Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`.
- Tools: `GET /auto/api?mode=tools` returned `ok: true`, `eligible_servers: 1`, selected `TitanX`, models `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF, Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`.
- Video: `GET /auto/api?mode=video` returned `ok: true`, `eligible_servers: 1`, selected `TitanX`, model `cerspense-zeroscope_v2_576w`.
- Image: no compatible image-generation model is currently eligible in the live TitanX inventory, so image mode correctly does not select the video model.

Browser prompts tested:
- `Say only this exact word: OK` routed through Auto -> Text via TitanX and rendered `OK` as one response, not token-per-line.
- `Who is the president of the United States right now? Use tools if needed. Answer in one sentence.` routed Auto -> Tools via TitanX, performed a current lookup, and returned Donald J. Trump with source links.
- `Use agent tools to create a small text file named auto_codex_probe.txt...` routed Auto -> Tools via TitanX and wrote `auto_codex_probe.txt` with `vs_write_file`.
- `Use agent tools to download https://socketjack.com/robots.txt as auto_codex_robots.txt...` routed Auto -> Tools via TitanX and saved the file under the session `Downloads` folder.

Important failure found and fixed:
- A failed direct download of `https://socketjack.com/favicon.ico` returned 404 and emitted a bare `failed` stream line. Auto was incorrectly treating that tool failure as a model failure and disabled both Qwen text/tool models for benchmark.
- Fix: Auto now stops waiting when a terminal `failed` stream event arrives, but only flags models for benchmark when the failure is a real model/runtime failure. Generic tool/download failures no longer remove chat/tool models from the pool.
- The accidental benchmark-required flags were automatically cleared on the next tools/text eligibility check by treating `Auto tools request failed`, `download_file`, and `Downloading file` as non-benchmark tool failures.

Model/runtime behavior notes:
- Auto router model used: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF` on `TitanX`.
- Text model used: `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF` on `TitanX`.
- Tools models used: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF` and `Qwen3.5-4B-Claude-4.6-Opus-Reasoning-Distilled-GGUF` on `TitanX`.
- The Qwen reasoning models often stream private reasoning first. Auto now preserves token whitespace, extracts `<auto_final>` answers when present, and falls back without collapsing words together.
