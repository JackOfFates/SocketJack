# JackLLM Workstation on Wine/Linux

## 2026-05-22 Sable image-generation crash

Observed on sable after an image-generation run:

- `wine-jackllm.log` showed `System.ComponentModel.Win32Exception (6): Invalid handle`.
- The failing stack was inside `System.Windows.Threading.Dispatcher.RequestBackgroundProcessing()`.
- Wine then terminated `JackLLM.exe` with CLR exception `0xe0434352`.
- The same log also showed repeated probes of `/usr/local/bin/nvidia-smi`, which does not exist on sable.

The Wine-safe WPF path now avoids two fragile behaviors:

- Image/bootstrap and JackONNX progress callbacks use guarded dispatcher posting, so a Wine dispatcher handle failure is logged and dropped instead of being allowed to terminate the process.
- The WPF proxy prefers the native Linux workstation hardware endpoint for GPU metrics before trying local Wine `nvidia-smi` probes.

Crash logs are written to `JACKLLM_CRASH_LOG` when that environment variable is set by the launcher; otherwise they fall back to the user's local JackLLM log folder.
