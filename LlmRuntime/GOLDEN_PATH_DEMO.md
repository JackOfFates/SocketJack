# Golden Path Demo

Goal: download a local code model, load it into LlmRuntime, run an approved agent edit, test it, and prepare a PR path without requiring LM Studio.

1. Build `JackLLM`.
2. Launch the GUI.
3. Choose `Embedded LlmRuntime` in the provider dropdown.
4. Open `Model Browser`.
5. Download a GGUF code/instruct model that fits memory and disk.
6. Keep `Load after download` checked.
7. Confirm `GET /v1/models` lists the model.
8. Create an agent session with `POST /api/v1/agent/sessions`.
9. Use `POST /api/v1/agent/files/preview` and `POST /api/v1/agent/files/write` for a focused edit.
10. Run checks with `POST /api/v1/agent/checks/run`.
11. Use GitHub workflow endpoints to create a branch, commit, and draft PR when GitHub CLI is installed.
12. Capture `/api/v1/production/analytics/local` and `/api/v1/production/diagnostics` as the final demo health snapshot.

Expected result: the local runtime completes a small repo workflow with an auditable model, tool, test, and GitHub path.
