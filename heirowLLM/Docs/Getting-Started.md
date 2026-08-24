# heirowLLM Workstation: Getting Started

## First run

1. Start heirow from the Workstation home tab.
2. Open **Models** to review installed models, each model's size, total storage used, and free disk space.
3. Download or select a model, enable it for Web Chat, and load it.
4. Open **Web UI**, then sign in with a Workstation account.
5. Create a project and session. Attach files or images only when the signed-in user has permission.

## Where heirowLLM keeps your data

All portable per-user state is under `%LOCALAPPDATA%\SocketJack\heirowLLM`:

- `Data\Chat` - encrypted chat/session database
- `SessionFiles` - project and session files
- `RemoteSessions` - cloned remote session files
- `Models` and `CompleteModels` - model storage
- `Logs` and `Backups` - diagnostics and migration backups
- `heirowLLM.settings.json` and model-manager JSON files - application settings

Copy or move this complete folder when transferring the same Windows user's heirowLLM projects and settings. Do not move only `SocketJackDatabase.json`; its encryption is path-bound and heirowLLM performs the supported migration.

## GPU capability notices

The installer and Diagnostics warnings report CUDA and Vulkan availability, VRAM, a PassMark-based performance range, and accelerated BF16/FP8/FP4 support. GGUF Q4 models can run without native FP4 tensor hardware; Q4 model quantization is not the same as accelerated FP4 arithmetic.

## Hints and guides

Use **Help > Getting Started Guide** or **Help > Show Application Hints** in Workstation. Web Chat has the same commands in its Help menu. "Do not show again" hides the guide; "Do not show Hints" independently disables hints.

## Troubleshooting

- Open **Diagnostics > Warnings** for GPU/runtime limitations.
- Confirm the model fits VRAM; CPU offload works but reduces speed.
- Keep file, terminal, PC Access, and Companion permissions narrow and user-owned.
- Use the Logs folder above when reporting a startup or runtime issue.
