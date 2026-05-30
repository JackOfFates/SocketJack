# SocketJack - SockJackDmlWorkflowExpansion Progress

- Plan: `SockJackDml Workflow Expansion Plan`
- Status: Complete. Backend workflow tools, mirrored LlmRuntime definitions, GUI controls, endpoint/UI smoke coverage, and full verification are implemented and passing.
- Overall: 100%
- Updated: 2026-05-11

## Summary
Implemented the follow-up workflow layer around the existing SockJackDml plan/progress/execution tools. The new layer adds workflow status aggregation, progress discovery, strict action validation, preview packet persistence, execution cursor controls, evidence linkage, mirrored LlmRuntime HTTP tools, and visible GUI controls across `/sockjackdml`, Web Chat, and the WPF shell.

## Feature Changes
| Area | Status | Percent | Detail |
| --- | --- | ---: | --- |
| Workflow service storage | complete | 100% | Added persisted workflow status snapshots through plans/progress/executions/evidence-link aggregation under the existing SockJackDml owner/session workflow storage. Execution records store validation results, preview packets, cursor index, pause state, and full action/result history. |
| Workflow status tool | complete | 100% | Added `sockjackdml_workflow_status` plus `GET` and `POST /api/sockjackdml/tools/workflow/status`. Returns plan, progress, execution, evidence, latest blocker, next action, approval state, validation, and preview summaries. |
| Progress discovery tool | complete | 100% | Added `sockjackdml_progress_document_find` and `POST /api/sockjackdml/tools/progress/find`. Finds trackers by progress id, plan id, project/session name, feature name, explicit path, or file name. |
| Execution control tool | complete | 100% | Added `sockjackdml_execution_control` and `POST /api/sockjackdml/tools/execution/control`. Supports status, pause, resume, cancel, retry, retry blocked step, skip, and mark skipped against the latest or specified execution cursor. |
| Evidence linkage tool | complete | 100% | Added `sockjackdml_evidence_link` and `POST /api/sockjackdml/tools/evidence/link`. Links existing evidence packet ids or creates a new evidence packet from summary/source fields, then de-dupes durable workflow evidence links. |
| Strict action validation | complete | 100% | `sockjackdml_plan_execute` validates supported action types and required fields before mutation. Missing file paths/content/commands/operations and unsupported action types return structured errors and suggested repair fields. |
| Preview packets | complete | 100% | `sockjackdml_plan_execute` returns and persists preview packets containing `filesToWrite`, `filesToEdit`, `commandsToRun`, `gitMutations`, `approvalRequired`, and `riskNotes`. |
| Auto approval-gated cursor | complete | 100% | Execution records persist cursor state and action status. `auto_approval_gated` respects existing VS/file, Git, and terminal approval gates; pause/resume/cancel can stop and continue persisted executions. |
| Proxy tool exposure | complete | 100% | JackLLM Agent mode advertises all seven SockJackDml workflow tools, routes calls locally, and de-dupes proxy-owned names from mirrored LlmRuntime schemas. |
| LlmRuntime mirror | complete | 100% | Embedded LlmRuntime registers all seven HTTP-backed SockJackDml tool definitions against local JackLLM endpoints with conservative approval and permission flags. |
| `/sockjackdml` Workflow tab | complete | 100% | Added a Workflow tab with summary stats, plan/progress/execution/evidence lists, latest blocker/next action, preview JSON, execution controls, and evidence link form. |
| Web Chat controls | complete | 100% | Added Agent-mode SockJackDml workflow strip with status refresh, execution control action selector, and link to the full Workflow tab for the active chat session. |
| WPF controls | complete | 100% | Added desktop buttons for opening Magic Workflow, refreshing workflow status, selecting an execution-control action, and applying it through the same local endpoints. |
| Tests | complete | 100% | Added focused service, endpoint, invalid-payload, GUI smoke, and mirrored-definition coverage. Full SocketJack and LlmRuntime test suites pass. |
| Protocol stabilization | complete | 100% | Fixed two existing networking issues exposed by full verification: literal IPv4 hosts now bypass reverse DNS in `TcpClient`, and raw assigned protocol traffic is forwarded in the expected raw-list shape so WebSocket frames continue after handshake. |

## Public API
- `GET /api/sockjackdml/tools/workflow/status`
- `POST /api/sockjackdml/tools/workflow/status`
- `POST /api/sockjackdml/tools/progress/find`
- `POST /api/sockjackdml/tools/execution/control`
- `POST /api/sockjackdml/tools/evidence/link`

## Model-Callable Tools
- `sockjackdml_plan_create`
- `sockjackdml_progress_document_create`
- `sockjackdml_plan_execute`
- `sockjackdml_workflow_status`
- `sockjackdml_progress_document_find`
- `sockjackdml_execution_control`
- `sockjackdml_evidence_link`

## Progress Log
- 2026-05-11 - implemented - Extended `SockJackDmlWorkflowService` with status, find, control, evidence link, validation, preview packet, and cursor persistence.
- 2026-05-11 - implemented - Routed new workflow tools through JackLLM with Agent access checks and existing VS/Git/terminal permission gates intact.
- 2026-05-11 - implemented - Added GET/POST workflow status plus POST progress find, execution control, and evidence link endpoints.
- 2026-05-11 - implemented - Registered mirrored embedded LlmRuntime HTTP tool definitions for all seven SockJackDml tools.
- 2026-05-11 - implemented - Added `/sockjackdml/workflow` UI, Web Chat Agent workflow controls, and WPF workflow status/control buttons.
- 2026-05-11 - verified - `dotnet build SocketJack.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - `dotnet build ..\JackLLM\JackLLM.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - `dotnet build ..\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --nologo -v:minimal --filter SockJackDmlWorkflowServiceTests` passed.
- 2026-05-11 - implemented - Added SockJackDml workflow endpoint and UI/mirror smoke tests covering local HTTP tool routes, invalid payload rejection, `/sockjackdml`, Web Chat, WPF controls, and mirrored LlmRuntime registration strings.
- 2026-05-11 - fixed - Stabilized full SocketJack protocol verification by preserving literal IPv4 connection targets and forwarding post-handshake WebSocket raw frames to the assigned protocol handler.
- 2026-05-11 - verified - `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --nologo -v:minimal --filter SockJackDmlWorkflow` passed with 7 tests.
- 2026-05-11 - verified - `dotnet test ..\SocketJack.Tests\SocketJack.Tests.csproj --no-restore --nologo -v:minimal` passed with 64 tests.
- 2026-05-11 - verified - Final `dotnet build SocketJack.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - Final `dotnet build ..\JackLLM\JackLLM.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - Final `dotnet build ..\LlmRuntime\LlmRuntime.csproj --no-restore --nologo -v:minimal` passed.
- 2026-05-11 - verified - Final `dotnet test ..\LlmRuntime.Tests\LlmRuntime.Tests.csproj --no-restore --nologo -v:minimal` passed with 57 tests.

## Next Step
Ready for user review or commit. No remaining feature work is open in this tracker.

## Verification Notes
Feature-specific tests, full SocketJack tests, GUI/runtime builds, and LlmRuntime tests pass. The SocketJack test project still emits dependency version warnings for `System.Text.Json` and `System.Text.Encodings.Web` due mixed 9.x/10.x references, but no tests fail.


