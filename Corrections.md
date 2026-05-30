# JackLLM Workstation Linux LLM Corrections

Date: 2026-05-25

Server tested: sable (`wintergrasped@216.235.101.12`, SSH port 25)

## Corrections Applied

- `LlmRuntime` Hugging Face search now accepts `query` and `q` aliases in addition to `search`, so `/Workstation` downloader calls do not silently return unrelated default/popular models.
- Text GGUF models now report `chat_completion` in runtime service metadata, so model/mode discovery can identify normal chat models directly.
- JackONNX LlmRuntime image/video tools now accept per-call `deviceId`, `cudaDevice`, `preferredProvider`, and `devicePolicy` fields.
- JackONNX image/video Python runners now inspect free VRAM with `nvidia-smi`; when no CUDA device has enough free memory and CUDA is not strictly required, they fall back to CPU instead of hard-failing with CUDA OOM.
- `Deploy-JackLLMWorkstationSable.ps1` now understands the packaged Linux install path and uses `jackllm-workstation` / `jackllm-workstation-stop` when installed. Its fallback native bridge also uses chat port `11436`.
- Rebuilt and installed the Linux Debian package version `2026.0.0` on sable.

## Installed Package

- Local installer: `C:\Users\Vin\Documents\GitHub\SocketJack\artifacts\linux-installer\LlmWorkstation_Linux64.deb`
- Size: `1,630,979,464` bytes
- SHA256: `33615B0A1B65149860C3FFA841D07551DCE15AE5637266A0350F288F00A24DEA`
- Remote build artifact: `/stor2/JackLLMDebBuild/llmworkstation-deb-20260525-120140/out/LlmWorkstation_Linux64.deb`
- Installed package: `jackllm-workstation 2026.0.0`
- Running native backend: `/opt/jackllm/workstation/native/JackLLM.Workstation`
- Running WPF/Wine app: `/opt/jackllm/workstation/wpf/JackLLM.exe`

## Live Sable Runtime State

- Ports listening:
  - Chat UI: `11436`
  - Model runtime: `12435`
  - Proxy: `12434`
  - Copilot duplicator: `12433`
- Model roots:
  - Models: `/stor2/Models`
  - CompleteModels: `/stor2/CompleteModels`
- GPU/PyTorch:
  - `nvidia-smi` sees 4x `Tesla V100-PCIE-16GB`.
  - Bundled runtime Python reports `torch 2.6.0+cu124`, CUDA `12.4`, `torch.cuda.is_available() == true`, 4 devices.
  - The title/page reports current total GPU memory usage as `NVIDIA Tesla (x4) 60.3GB/64GB`, which matches the active VLLM workers occupying most VRAM.

## Final Test Pass

Result: `23 passed, 0 failed, 0 warnings`

- Debian package installed.
- Workstation native backend and WPF/Wine process running.
- Expected ports listening.
- `/Workstation` route returns HTTP 200.
- `/api/model-runtime/compatibility` proxy works.
- `/api/v1/runtime/compatibility` works.
- NVIDIA GPU metrics work through `nvidia-smi`.
- Bundled PyTorch CUDA works.
- Runtime model inventory reports the text, image, and video models from `/stor2`.
- Chat UI model proxy reports `/stor2` roots.
- Hugging Face search with `query=stable-diffusion` returns stable-diffusion results.
- Repository scan uses `/stor2/CompleteModels` and reports about `166 GB` free.
- Download status and cancel endpoints work.
- JackONNX tools are present in proprietary, OpenAI, and MCP exports.
- GGUF text model load works with the Qwen model.
- Chat completion returns `sable`.
- Text model unload works.
- Image generation smoke test works with `preferredProvider=cpu`.

Image artifact produced:

`/var/lib/jackllm/Artifacts/JackONNX/job_d64b8646f27c41ce808e9db7e528684b/image_job_d64b8646f27c41ce808e9db7e528684b.png`

## Video Generation Note

The video tool and model registration were verified, but I did not run a full video generation job in the final pass. The installed `ali-vilab-text-to-video-ms-1.7b-pytorch` bundle is about `20.5 GB`, which is larger than a single 16 GB V100, and the running `vllm.service` currently reserves roughly 15 GB on each GPU. Full video generation should use a smaller video model, run after freeing a GPU, or use a future multi-GPU/offload path.

## Remaining Non-Blocking Log Notes

- Wine still logs that `wine32` is missing. The 64-bit WPF app is running anyway; this is a Wine diagnostic, not a blocker for the tested app.
- The launch log contains two old shell quoting errors from a manual restart attempt during testing. The packaged app is running after that and the final API/runtime tests passed.
