# Acceptance Criteria Recovery Plan

## Goal

Close the roadmap acceptance gaps with evidence, not optimism. Each criterion should move to done only when it has an implementation path, a test or manual verification path, and known failure points documented in `ISSUES.md`.

## 1. Remove LM Studio As A Runtime Requirement

- Update `JackLLM` README/status text so Embedded LlmRuntime is the primary path.
- Audit `JackLLM` log/error strings that still say "LM Studio" when they mean "local model runtime."
- Add startup smoke test: GUI/proxy starts with embedded runtime selected and no LM Studio process/server running.
- Acceptance target: SocketJack starts, lists local models, loads one, and serves `/v1/chat/completions` without LM Studio installed.

## 2. Finish Native LlmRuntime Tool Calling

- Extend `LlmChatRequest` to accept OpenAI-style `tools` and `tool_choice`.
- Add tool-call prompt formatting for GGUF models.
- Add response parser for common JSON/function-call formats.
- Add `LlmRuntimeHost` chat loop: model response -> tool call -> `LlmToolCallLoop` -> continuation.
- Add tests with fake backend returning tool calls.
- Acceptance target: local GGUF-compatible chat can request a tool and receive tool result context.

## 3. Add JackLLM Approval Flow For Proprietary Tools

- Change `ExecuteLlmRuntimeToolAsync` to detect approval-required denial.
- Create pending LlmRuntime tool approval records in `JackLLM`.
- Surface pending proprietary tool approvals in GUI next to terminal/filesystem approvals.
- On approve, rerun `/api/v1/tools/calls` with `approved=true`.
- Acceptance target: an `AskEveryTime` proprietary tool can be called from chat, approved in GUI, executed, and fed back to model.

## 4. Turn Agent Primitives Into Autonomous Loops

- Add `RunAgentLoopAsync`: plan -> inspect files -> preview edits -> approved writes -> run checks -> diagnose -> retry.
- Persist each loop step in agent session history.
- Add cancellation, max-iteration, and sandbox enforcement.
- Add diff bundle endpoint for review.
- Acceptance target: agent can modify a small repo, run tests, recover from one failure, and summarize final changes.

## 5. Fix IDE Checkpoint/Rollback Hang

- Reproduce `IdeService_ProvidesCompletionPlanIndexCheckpointAndRollback` with diagnostic logging.
- Inspect checkpoint file enumeration and rollback path handling.
- Add timeouts or bounded filesystem scans where needed.
- Add regression test for checkpoint/rollback on small workspace.
- Acceptance target: full `LlmIdeIntegrationTests` class passes reliably.

## 6. Build Real Visual Studio Integration

- Create `LlmRuntime.VisualStudio` VSIX/MEF project.
- Wire editor context capture: active file, selection, cursor, diagnostics, solution/project metadata.
- Implement commands for Ask/Edit/Agent/Plan, inline completion, next-edit suggestions, checkpoints, rollback, model picker, prompt files, custom instructions, and MCP context.
- Add integration smoke tests where feasible.
- Acceptance target: actual Visual Studio users can invoke the runtime from editor UI, not only HTTP endpoints.

## 7. Complete Tool Platform Adapters

- Implement named pipe invocation.
- Implement .NET assembly/plugin invocation with allowlisted entry points.
- Implement MCP server transport invocation.
- Implement built-in SocketJack tool invocation.
- Add tool pack import/export format with version, multiple definitions, and explicit secret exclusion.
- Acceptance target: proprietary binaries/private schemas work without source disclosure across all declared source types.

## 8. Harden Secret Storage

- Replace current local key-file encryption with DPAPI on Windows.
- Keep current file store as fallback only.
- Add migration from existing `.secrets` format.
- Add tests proving secret values are absent from definitions, exports, logs, prompts, audit, and tool results.
- Acceptance target: "hidden credentials" is defensible beyond accidental plaintext avoidance.

## 9. Finish GitHub/PR Workflow

- Add GitHub CLI detection UI and setup diagnostics.
- Add graceful connector/CLI configuration guide.
- Implement local branch/commit path fully without GitHub.
- Gate draft PR creation behind authenticated `gh`.
- Add tests for unavailable and available paths where possible.
- Acceptance target: branch/commit works locally; draft PR works when `gh` is installed/authenticated.

## 10. Update Acceptance Criteria Continuously

- Add an `ACCEPTANCE.md` matrix with pass/fail/evidence/test command for each criterion.
- Link each row to the test or manual verification.
- Update `roadmap.md` only when acceptance evidence exists.
- Keep `ISSUES.md` for blockers discovered during implementation.
