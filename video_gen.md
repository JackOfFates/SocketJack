# JackLLM / LlmRuntime Video Generation Plan

## Goal
Replace the JackONNX video scaffold with a real local video generation path so LlmRuntime `jackonnx_video_generate` returns a generated artifact instead of `Video pipeline scaffold is ready; model execution is not implemented yet.`

## Progress

| Feature / Function | Progress | Status |
| --- | ---: | --- |
| Trace current scaffold and integration path | `████████████████████` 100% | Complete |
| Implement `JackOnnxVideoPipeline` model resolution/artifact save | `████████████████████` 100% | Complete |
| Add Diffusers Python video runner | `████████████████████` 100% | Implemented, built, and smoke-tested |
| Prefer Titan X compatible CUDA legacy Python | `████████████████████` 100% | Wired through Python command preference |
| Encode generated frames to playable media | `████████████████████` 100% | MP4 smoke passed; GIF fallback remains available |
| Preserve LlmRuntime tool/API compatibility | `████████████████████` 100% | Tool args and chat payload options wired and verified |
| Build JackONNX/JackLLM | `████████████████████` 100% | JackONNX and JackLLM builds passed |
| Smoke test local video generation | `████████████████████` 100% | Local LlmRuntime tool call succeeded |
| Browser/proxy verification | `██████████████░░░░░░` 70% | Public proxy health/page passed; direct public HTTP stream POST timed out before reaching JackLLM |

## Implementation Notes
- Use the same registered CompleteModels manifests as image generation.
- Resolve `video.*` model manifests, with `cerspense/zeroscope_v2_576w` as the current local smoke-test model.
- Invoke Diffusers locally through Python instead of duplicating model handlers in C#.
- Prefer `PythonCudaLegacy\Scripts\python.exe` because it has PyTorch CUDA 11.8 and supports the Titan X (`sm_50`), unlike the CUDA 12.8 runtime.
- Clamp video smoke defaults to conservative dimensions/frame counts to avoid exhausting VRAM.
- Save output under JackONNX artifacts and let the existing Chat UI materializer copy it into the current session folder.

## Verification Log
- Found placeholder in `JackONNX/Media/Video/JackOnnxVideoPipeline.cs`.
- Confirmed `jackonnx_video_generate` already routes through LlmRuntime tools.
- Confirmed local video model manifest: `CompleteModels/cerspense/zeroscope_v2_576w/main/manifest.json`.
- Confirmed CUDA legacy Python: torch `2.1.2+cu118`, CUDA available, arch list includes `sm_50`.
- Added `JackOnnxPythonDiffusersVideoRunner` and swapped the video pipeline away from the scaffold response.
- Added video request options: width, height, frames, seconds, fps, steps, guidance scale, seed.
- Updated Chat UI media materialization so video jobs can store MP4/WebM output and animated GIF fallbacks in the session folder.
- `dotnet build JackONNX/JackONNX.csproj -c Debug` passed with existing nullable warnings only.
- `dotnet build JackLLM/JackLLM.csproj -c Debug` passed with 0 warnings and 0 errors after the OpenCV MP4 repair hook was added.
- Restarted JackLLM and confirmed `http://127.0.0.1:11436/api/health` returns `ok: true`.
- Local smoke call to `http://127.0.0.1:11435/api/v1/tools/calls` with `jackonnx_video_generate`, model `cerspense-zeroscope_v2_576w-pytorch`, `128x72`, `frames=2`, `steps=1`, `seed=11` succeeded in ~20s.
- Smoke artifact: `Artifacts/JackONNX/job_d064be96432d4adb94678956b221b5d6/video_job_d064be96432d4adb94678956b221b5d6.gif`.
- MP4 export requested OpenCV (`opencv-python`); initial fallback generated animated GIF instead of failing the video job.
- Fixed the Python video runner to repair legacy CUDA Torch environments by pinning NumPy back to `<2` before Torch loads.
- Changed OpenCV repair installs to use `--no-deps` so `opencv-python` does not upgrade NumPy past the CUDA Torch compatibility range.
- Rebuilt `JackLLM/JackLLM.csproj` after the NumPy/OpenCV repair patch; build passed with 0 warnings and 0 errors.
- Restarted JackLLM and confirmed `http://127.0.0.1:11436/api/health` returns `ok: true`.
- Local smoke call to `http://127.0.0.1:11435/api/v1/tools/calls` with `jackonnx_video_generate`, model `cerspense-zeroscope_v2_576w-pytorch`, `128x72`, `frames=2`, `steps=1`, `seed=14` succeeded through CUDA.
- MP4 smoke artifact: `Artifacts/JackONNX/job_1fa3e3d48cd0457cb8028da7bca103e0/video_job_1fa3e3d48cd0457cb8028da7bca103e0.mp4`.
- Verified the MP4 with OpenCV: opened successfully, 2 frames, 8 FPS, `128x72`.
- Public proxy health check `https://socketjack.com/proxy/TitanX/api/health` returned `ok: true` after restart.
- Refreshed the in-app browser at `https://socketjack.com/proxy/TitanX/`; the TitanX page loaded and showed the older pre-fix failed video message in chat history.
- Browser automation could not type a replacement prompt because the Codex browser virtual clipboard hook is unavailable.
- Local Chat UI stream route `POST http://127.0.0.1:11436/api/chat-stream` with `service=video_generation`, model `cerspense-zeroscope_v2_576w-pytorch`, `128x72`, `frames=2`, `steps=1`, `seed=19` succeeded.
- Chat UI session artifact: `SocketJack/JackLLMChat/SessionFiles/codex-video-chat-smoke-20260519/GeneratedMedia/video_job_fd142fc06d6646f9b5d623d0800f4590_artifact_05a353b57f324383b9752a5a952356c0.mp4`.
- Verified the session MP4 with OpenCV: opened successfully, 2 frames, 8 FPS, `128x72`.
- Direct public HTTP `POST https://socketjack.com/proxy/TitanX/api/chat-stream` timed out before a response and did not create the requested proxy smoke session artifact locally. This looks separate from the LlmRuntime video implementation, because the local Chat UI route succeeds and the public health/page requests still load.
