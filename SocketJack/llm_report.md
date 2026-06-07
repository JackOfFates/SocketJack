# SocketJack /Auto LLM Tool Report

Date: 2026-06-02 02:21 CDT

Target tested:

- Live page: `https://socketjack.com/Auto?mode=tools&origin=hybrid&pool=1`
- Live API: `https://socketjack.com/auto/api`
- Browser test session: Codex in-app browser
- Primary routed server during UI tests: `TitanX` / `lmvs-shell-05d29369622672e5`
- Model actually selected by the page during UI tests: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Requested 35B/Sable target checked separately: `lmvs-shell-410c6a730b24f473` with `Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled.Q5_K_M`

## Executive Summary

`/Auto` is not currently producing consistent clean answers.

Several tools do execute through the live `/Auto` tools route, including `internet_search`, `browser_open`, `vs_write_file`, `vs_read_file`, `run_command_in_terminal`, and `download_file`. However, the visible responses frequently fail the requested final-answer criteria and leak tool internals, reasoning text, raw JSON-like fragments, CLIXML, and tool result digests into the chat output.

The original 35B/Sable route is not usable right now: pinned `/auto/api` status for the Sable server/model returns HTTP 503. The live router falls back to TitanX.

## Route And Status Checks

### Authentication

- `GET /api/web-auth/session` from plain HTTP returned `authenticated:false`.
- In the browser UI, `/Auto` initially showed `Login required`.
- After sending through the UI, the page changed to `Signed In`, allowing protected tools-mode tests to run.

### Model Cache

`GET /auto/model-cache` returned two relevant servers:

- `TitanX` (`lmvs-shell-05d29369622672e5`): model cache says online, shell proxy connected, tools/vision/media supported.
- `sable` (`lmvs-shell-410c6a730b24f473`): model cache says offline, but has the 35B Claude-style model loaded.

### Pinned 35B/Sable Check

Request:

```text
GET /auto/api?mode=tools&origin=hybrid&pool=true&server_id=lmvs-shell-410c6a730b24f473&model=Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled.Q5_K_M
```

Result:

```text
HTTP 503
elapsedMs=10876
```

Conclusion: 35B/Sable could not be tested through `/Auto` because it is not routable.

### Per-Mode Status Matrix

Plain `GET /auto/api` status checks:

| Mode | HTTP | Eligible | Selected | Selected status | Result |
| --- | ---: | ---: | --- | --- | --- |
| text | 200 | 1 | TitanX | Offline - JackLLM did not pong: The operation was canceled. | Inconsistent |
| tools | 200 | 1 | TitanX | Offline - JackLLM did not pong: The operation was canceled. | Inconsistent |
| image | 503 | n/a | n/a | n/a | Broken |
| audio | 200 | 1 | TitanX | Offline - JackLLM did not pong: The operation was canceled. | Inconsistent |
| video | 200 | 1 | TitanX | Offline - JackLLM did not pong: The operation was canceled. | Inconsistent |
| multimodal | 200 | 1 | TitanX | Offline - JackLLM did not pong: The operation was canceled. | Inconsistent |
| embedding | 503 | n/a | n/a | n/a | Broken |

The status surface is contradictory: it reports an eligible selected server but also says the selected server is offline / did not pong.

### Direct POST Checks

Direct POST requests from PowerShell timed out:

| Request | Timeout | Result |
| --- | ---: | --- |
| `POST /auto/api?mode=tools` exact string prompt | 120s | Timed out |
| `POST /auto/api?mode=text` exact string prompt | 90s | Timed out |
| `POST /auto/route` route classifier | 60s | Timed out |

The browser UI path did return results, so the timeout issue appears specific to direct non-browser request context and/or auth/routing.

## UI Capability State

After `/Auto` loaded:

- Auth: `Login required`
- Pool: eventually `Ready`
- Server: `TitanX | socketjack-master-relay`
- Model: `Qwen3.5-2B-Claude-4.6-Opus-Reasoning-Distilled-GGUF`
- Send button HTML showed both capability icons disabled:

```html
<span class="send-cap tool disabled" title="Tools not advertised by selected server"></span>
<span class="send-cap vision disabled" title="Vision not advertised by selected model/server"></span>
```

This contradicts `/auto/model-cache`, which says TitanX and the selected Qwen models support tools and vision.

## Answer Consistency Test

Prompt:

```text
Return exactly: AUTO_UI_GATE_SMOKE_OK
```

Expected:

```text
AUTO_UI_GATE_SMOKE_OK
```

Observed visible/raw response excerpt:

```text
AUTO_UI_GATE_SMOKE_OK/no_thinkDonotwriteathoughtprocess,analysis,or<analysisAUTO_UI_GATE_SMOKE_OK
/no_think
Do not write a thought process, analysis, or
AUTO_UI_GATE_SMOKE_OK
/no_think
Do not write a thought process, analysis, or
```

Result: FAIL

Issues:

- Exact-output instruction was not followed.
- Hidden instruction fragments such as `/no_think` leaked.
- `<analysis` leaked into visible response.
- The output repeated itself.
- The response included thought/verification text instead of only the requested string.

## Tool Test Matrix

| Tool / family | Runtime result | Final answer quality | Status |
| --- | --- | --- | --- |
| `internet_search` | Executed. Search completed in about 853ms and returned sources including GitHub and NuGet. | Failed exact two-line final; visible `Reasoning` section and tool event/result preview leaked. | PARTIAL |
| Browser tools (`browser_open`) | Executed. Opened `https://example.com` and observed `Example Domain`. | Did not answer `BROWSER_TOOL_OK`; stopped at "Browser Skill opened the page" and leaked digest/tool context. | PARTIAL |
| File tools (`vs_write_file`, `vs_read_file`) | Executed. Wrote 12 chars and read back `FILE_TOOL_OK`. | Did not complete final answer within test window; path became `ool_smoke.txt`, losing the requested leading `t`. | PARTIAL |
| Terminal (`run_command_in_terminal`) | Executed. `Write-Output TERMINAL_TOOL_OK` returned output and exit code 0. | Final answer was verbose instead of exact; visible output leaked CLIXML/JSON/tool-event internals. | PARTIAL |
| `download_file` | Executed. Downloaded `https://example.com` into `Downloads`. | Did not call `read_file`; treated "in the session Downloads folder" as part of the filename/path; leaked JSON/delta/tool event text. | PARTIAL |
| Image generation mode | Status endpoint returned HTTP 503. | Generation not attempted because status is broken. | FAIL |
| Embedding mode | Status endpoint returned HTTP 503. | Embedding not attempted because status is broken. | FAIL |
| Audio/video status | Status returned HTTP 200 but selected server simultaneously reported offline/pong failure. | Generation not proven. | UNPROVEN |
| `nuget_search`, `github_code_search` | Not run in this pass. | Unknown. | UNTESTED |
| Browser interaction subtools (`browser_click_link`, `browser_click`, `browser_type`, `browser_select`, `browser_press`, `browser_find_text`) | Not individually confirmed. `browser_open` did work. | Unknown per subtool. | UNTESTED |
| VS mutation subtools (`vs_replace_in_file`, `vs_copy_file`, `vs_rename_file`, `vs_delete_file`, `vs_search_files`, `vs_list_files`) | Not individually confirmed. `vs_write_file` and `vs_read_file` did work. | Unknown per subtool. | UNTESTED |
| Git/workstation/SockJackDML tools | Not run in this pass. | Unknown. | UNTESTED |

## Detailed Tool Evidence

### `internet_search`

Prompt:

```text
Use the internet_search tool with action=search for "SocketJack .NET Library GitHub".
Then answer in exactly two short lines: INTERNET_SEARCH_TOOL_OK and the first source URL you found.
```

Evidence:

```text
internet_search completed 853ms
Internet search results for: SocketJack .NET Library GitHub
[1] GitHub - JackOfFates/SocketJack
[2] NuGet Gallery | SocketJack 2026.0.0
```

Broken response excerpt:

```text
INTERNET_SEARCH_TOOL_OK https://github.com/JackOfFates/SocketJack

Reasoning: The user requested a tool smoke test...
Tool event ... resultPreview ... </tool_call>
```

Conclusion: search tool works, but final response rendering is not clean.

### Browser Tool

Prompt:

```text
Use browser_open to open https://example.com, then use browser_find_text or browser_read_page to confirm the page contains "Example Domain".
Final answer should be exactly BROWSER_TOOL_OK.
```

Evidence:

```text
browser_open completed 242ms
Example Domain ... This domain is for use in documentation examples...
```

Broken response excerpt:

```text
Browser Skill opened the page.
Tool event ... [LmVsProxy tool result digest] ...
```

Conclusion: `browser_open` works, but the loop stopped before producing the requested final answer.

### File Tools

Prompt:

```text
Use vs_write_file to create a session file named tool_smoke.txt containing exactly FILE_TOOL_OK.
Then use read_file or vs_read_file to read it back.
```

Evidence:

```text
vs_write_file completed 6083ms
vs_write_file wrote 12 chars to ool_smoke.txt.
vs_read_file completed 635ms
1: FILE_TOOL_OK
```

Issues:

- Tool execution worked.
- Filename/path was wrong: `ool_smoke.txt`, not `tool_smoke.txt`.
- The model did not produce a clean final answer within the 90s test window.

Conclusion: file write/read tools are callable, but argument parsing and finalization are broken.

### Terminal Tool

Prompt:

```text
Use run_command_in_terminal to run: Write-Output TERMINAL_TOOL_OK
```

Evidence:

```text
run_command_in_terminal completed 1543ms
command: Write-Output TERMINAL_TOOL_OK
stdout: TERMINAL_TOOL_OK
exit code: 0
```

Broken response excerpt:

```text
Tool Smoke Test Result: PASSED
...
stdout: "TERMINAL_TOOL_OK\<Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04">
...
Tool event ...
```

Conclusion: terminal tool works, but stdout/result rendering leaks raw PowerShell CLIXML and internal JSON.

### `download_file`

Prompt:

```text
Use download_file to save https://example.com as example-smoke.html in the session Downloads folder,
then use read_file on the savedPath and confirm it contains "Example Domain".
```

Evidence:

```text
download_file completed 3372ms
Downloaded file to the current session Downloads folder.
savedPath: \Downloads\20260602071954_2a7f4f3d_example-smoke.html in the session Downloads folder
```

Issues:

- Download executed.
- The instruction phrase `in the session Downloads folder` was included in the generated filename/path.
- The model did not continue to `read_file`.
- Final answer did not include `DOWNLOAD_FILE_TOOL_OK`.
- Tool event/delta JSON leaked into visible output.

Conclusion: download works, but argument extraction and tool-loop continuation are broken.

## Broken Response Patterns

1. Hidden reasoning leaks into visible answer.
   Examples: `Thought process`, `<analysis`, `/no_think`, "Do not write a thought process".

2. Tool result internals leak into the visible response.
   Examples: `Tool event`, `resultPreview`, raw JSON, escaped `</tool_call>`, CLIXML, `chat.completion` bodies.

3. Tool loop often stops after the first completed tool result.
   Examples: browser tool stopped after `browser_open`; download stopped after `download_file`; file test stalled after write/read.

4. Exact final-answer instructions are not respected.
   All exact sentinel prompts produced extra text or the wrong content except the raw terminal command itself.

5. Tool argument extraction is brittle.
   Examples: `tool_smoke.txt` became `ool_smoke.txt`; `example-smoke.html in the session Downloads folder` became the filename.

6. Capability/status signals disagree.
   Model cache says TitanX supports tools/vision/media, but the UI send button greyed out tools/vision. `/auto/api` status says eligible server selected while also reporting the selected server is offline.

7. Direct POST API testing is unreliable from plain HTTP context.
   Direct text/tools/router POSTs timed out, while browser UI prompts returned.

## Recommended Fix Targets

1. Sanitize assistant visible text before rendering and during streaming for:
   - `<analysis...`
   - `Thought process`
   - `/no_think`
   - `Tool event`
   - `resultPreview`
   - raw `chat.completion` bodies
   - CLIXML `http://schemas.microsoft.com/powershell/2004/04`

2. Keep the agent loop running after a tool result until it produces a clean final answer satisfying the user request.

3. Tighten tool argument extraction for file paths and filenames.

4. Make the send-button tool/vision capability state use the same source of truth as `/auto/model-cache` and `/auto/api`.

5. Fix `/auto/api` status eligibility so an `ok:true` selected route does not simultaneously report `Offline - JackLLM did not pong`.

6. Investigate direct POST hangs for `/auto/api` and `/auto/route`.

7. Re-test the pinned 35B/Sable route after Sable is responding through shell again.

## Current Completion State

This report confirms that the live `/Auto` tools route can execute several major tool families, but it does not confirm that every individual tool works. It also confirms multiple broken response/rendering patterns. Additional targeted tests are still needed for media generation outputs and any mutating Git/SockJackDML/workstation flows that should only be run after the read-only tool surface is healthy.

## Continuation Pass 2

Date: 2026-06-02 02:45 CDT

Method: live `/Auto` page, URL autorun prompts, signed-in browser session, same TitanX route/model family.

### Additional Tool Results

| Tool / family | Prompt shape | Observed result | Status |
| --- | --- | --- | --- |
| `nuget_search` + `nuget_package_info` + `github_code_search` batch | Required all three tools in order. | Stalled at `Connected to Auto server. Waiting for tools progress...`; after stop/recovery it had used `internet_search` for the wrong query, `which tools returned data`, not the requested NuGet/GitHub tools. | FAIL |
| `nuget_search` single | Required only `nuget_search`, query `SocketJack`, take `3`, no `internet_search`. | No evidence of a tool call. Visible response copied the literal phrase `NUGET_SEARCH_TOOL_OK plus package count`. | FAIL |
| `github_code_search` single | Required only `github_code_search`, query `repo:JackOfFates/SocketJack MutableTcpServer`. | Stalled with no tool result: `Connected to Auto server. Waiting for tools progress...`. | FAIL |
| Browser interaction subtools | Required `browser_open`, `browser_find_text`, and `browser_click_link` on `https://example.com`. | Stalled with no tool result in this pass. Earlier pass proved `browser_open` alone can work, but chained browser subtools are still unconfirmed. | FAIL / UNCONFIRMED |
| VS mutation subtools | Required `vs_write_file`, `vs_list_files`, `vs_search_files`, `vs_replace_in_file`, `vs_copy_file`, `vs_rename_file`, `vs_delete_file`, `vs_read_file` on disposable session files. | Stalled during model processing; no mutation tool result surfaced in this pass. Earlier pass proved `vs_write_file`/`vs_read_file` can work individually. | FAIL / UNCONFIRMED |
| Git read-only tools | Required `git_dependency_check` and `git_status`. | `git_dependency_check` eventually returned: `git_dependency_check error: no authorized session or accessible filesystem root is available for Git.` | FAIL |
| `workstation_list_models` single | Required only `workstation_list_models`. | Stalled with no tool result. | FAIL / UNCONFIRMED |
| `sockjackdml_workflow_status` single | Required only `sockjackdml_workflow_status`. | Stalled with no tool result. | FAIL / UNCONFIRMED |
| Image mode artifact smoke | `mode=image`, requested a simple 256x256 red square PNG. | No artifact surfaced. The run stalled at `Connected to Auto server. Waiting for tools progress...` for about 60s. | FAIL |

### New Broken Response Patterns

1. Tool selection ignores explicit tool names.
   - The NuGet/GitHub batch requested `nuget_search`, `nuget_package_info`, and `github_code_search`.
   - `/Auto` instead ran `internet_search` for the unrelated query `which tools returned data`.

2. Final-answer literal copying.
   - The `nuget_search` single-tool prompt returned `NUGET_SEARCH_TOOL_OK plus package count` instead of calling the tool and producing a count.

3. Long "connected" stalls with no tool event.
   - `github_code_search`, chained browser subtools, VS mutation subtools, `workstation_list_models`, `sockjackdml_workflow_status`, and image mode all sat at `Connected to Auto server. Waiting for tools progress...` or `Processing prompt...` until the test window expired.

4. Git has no authorized root in this `/Auto` session.
   - Read-only `git_dependency_check` failed before `git_status` could be proven.

### Updated Tool Coverage Summary

Confirmed callable at least once:

- `internet_search`
- `browser_open`
- `download_file`
- `vs_write_file`
- `vs_read_file`
- `run_command_in_terminal`

Advertised but failed, ignored, stalled, or remains unconfirmed:

- `nuget_search`
- `nuget_package_info`
- `github_code_search`
- `browser_read_page`
- `browser_click_link`
- `browser_click`
- `browser_type`
- `browser_select`
- `browser_press`
- `browser_find_text`
- `read_file` after `download_file`
- `vs_list_files`
- `vs_search_files`
- `vs_replace_in_file`
- `vs_copy_file`
- `vs_rename_file`
- `vs_delete_file`
- `git_dependency_check`
- `git_status`
- other `git_*` tools
- `workstation_list_models`
- `workstation_run_model`
- `sockjackdml_*` tools
- image generation output

Net result after pass 2: `/Auto` does not currently satisfy "make sure each tool works." The route can execute some individual tools, but many advertised tools are ignored, blocked, or stall, and the response renderer still leaks internal tool/result content.

## Continuation Pass 3

Date: 2026-06-02 03:03 CDT

Method: live `/Auto` page, URL autorun prompts, signed-in browser session, same TitanX route/model family.

### Mode Output Smokes

| Lane | Prompt shape | Observed result | Status |
| --- | --- | --- | --- |
| text | `mode=text`, exact sentinel `TEXT_MODE_EXACT_OK`. | No final answer. Stalled around 56s at `Connected to Auto server. Waiting for tools progress...`. | FAIL |
| audio | `mode=audio`, requested a one-second beep artifact. | No audio artifact. Stalled around 50s at `Connected to Auto server. Waiting for tools progress...`. | FAIL |
| video | `mode=video`, requested a one-second simple video artifact. | No video artifact. Stalled around 52s at `Connected to Auto server. Waiting for tools progress...`. | FAIL |
| multimodal | `mode=multimodal`, text-only exact sentinel `MULTIMODAL_MODE_SMOKE_OK`. | No final answer. Stalled around 51s at `Connected to Auto server. Waiting for tools progress...`. | FAIL |

### Pass 3 Notes

- These tests used the same URL-autorun path as pass 2.
- Each lane displayed tools-route progress text (`Routing tools through /auto/api`, `Connected to Auto server. Waiting for tools progress...`) even when the requested mode was `text`, `audio`, `video`, or `multimodal`.
- No artifacts appeared for audio or video.
- The active prompt stop control did not clear cleanly after the multimodal stall; the page still reported a stop button after the stop attempt.

### Updated Mode Summary

| Mode | Current evidence |
| --- | --- |
| text | Status endpoint returns 200, but direct POST timed out and UI autorun stalls. |
| tools | Some tools execute, but many advertised tools fail/stall/are ignored and output is dirty. |
| image | Status endpoint returned 503; UI image artifact smoke stalled. |
| audio | Status endpoint returned 200; UI audio artifact smoke stalled. |
| video | Status endpoint returned 200; UI video artifact smoke stalled. |
| multimodal | Status endpoint returned 200; UI text-only multimodal smoke stalled. |
| embedding | Status endpoint returned 503; no successful embedding output. |

Net result after pass 3: `/Auto` is not consistent across modes. The system reports readiness in several places, but actual prompt execution frequently hangs at the same connected/waiting state and does not produce the requested answer or artifact.

## Final Advertised Tool Coverage Audit

Date: 2026-06-02 03:18 CDT

Authoritative advertised tool source inspected:

- `SocketJack.LlmCore/Proxy/JackLLM.cs`
- `BuildProxyResearchToolSystemPrompt(...)`
- `WriteProxyResearchToolSchemas(...)`
- `WriteGitToolSchemas(...)`

The source advertises tools conditionally by permission and mode. The live `/Auto` session exposed enough Agent/tool behavior to attempt the major permission families, but not all advertised tools were individually executable because the live route repeatedly stalled, ignored explicit tool names, or lacked an authorized filesystem root.

### Inventory By Source Permission Group

| Source group | Advertised tools | Coverage result |
| --- | --- | --- |
| Internet/search | `internet_search`, `nuget_search`, `nuget_package_info`, `github_code_search` | `internet_search` executed. `nuget_search` was ignored/literal-copied. `nuget_package_info` and `github_code_search` did not produce tool results; GitHub single stalled. |
| Downloads | `download_file` | Executed once and saved a file, but did not continue to `read_file` and polluted the filename/path. |
| Browser | `browser_open`, `browser_read_page`, `browser_click_link`, `browser_click`, `browser_type`, `browser_select`, `browser_press`, `browser_find_text` | `browser_open` executed once. Chained browser subtool tests stalled; other browser subtools remain unconfirmed and should be treated as failing until proven. |
| VS/session files | `read_file`, `vs_read_file`, `vs_write_file`, `vs_replace_in_file`, `vs_copy_file`, `vs_rename_file`, `vs_delete_file`, `vs_search_files`, `vs_list_files` | `vs_write_file` and `vs_read_file` executed once, with path parsing/finalization problems. `read_file` after `download_file` did not happen. Remaining VS tools stalled in a disposable session-file batch. |
| Git | `git_dependency_check`, `git_status`, `git_changed_files`, `git_tracked_files`, `git_diff`, `git_file_diff`, `git_file_at_ref`, `git_file_history`, `git_file_blame`, `git_grep`, `git_log`, `git_show`, `git_branch`, `git_remote`, `git_stage`, `git_unstage`, `git_commit`, `git_create_branch`, `git_switch_branch`, `git_fetch`, `git_pull`, `git_push` | `git_dependency_check` returned `no authorized session or accessible filesystem root`. `git_status` and all other Git tools are blocked/unproven in this `/Auto` session. Mutating Git tools were not attempted after the read-only root failure. |
| Workstation delegation | `workstation_list_models`, `workstation_run_model` | `workstation_list_models` stalled with no tool result. `workstation_run_model` was not attempted because listing models never succeeded. |
| SockJackDML | `sockjackdml_plan_create`, `sockjackdml_progress_document_create`, `sockjackdml_plan_execute`, `sockjackdml_workflow_status`, `sockjackdml_progress_document_find`, `sockjackdml_execution_control`, `sockjackdml_evidence_link` | `sockjackdml_workflow_status` stalled with no tool result. Other SockJackDML tools were not attempted because the safe read-only status tool failed/stalled. |
| Terminal | `run_command_in_terminal` | Executed successfully with exit code 0, but the visible response leaked CLIXML/tool internals and ignored the exact final-answer requirement. |
| Dynamic LlmRuntime tools | `llmruntime:*` dynamic names from `GetLlmRuntimeToolNamesSnapshot()` | Not enumerable from static source in this report. No dynamic LlmRuntime tool was positively confirmed through `/Auto`. |

### Every Explicitly Named Advertised Tool

| Tool | Final status in this report |
| --- | --- |
| `internet_search` | PARTIAL: executes, returns sources, dirty final rendering. |
| `nuget_search` | FAIL: ignored/no tool call evidence; literal answer copied. |
| `nuget_package_info` | FAIL/UNCONFIRMED: requested in batch, no tool result surfaced. |
| `github_code_search` | FAIL: single-tool prompt stalled. |
| `download_file` | PARTIAL: executes, but argument parsing and follow-up `read_file` failed. |
| `browser_open` | PARTIAL: executes, but final answer/tool-loop failed. |
| `browser_read_page` | FAIL/UNCONFIRMED: requested in browser tests, no successful result surfaced. |
| `browser_click_link` | FAIL/UNCONFIRMED: chained browser test stalled. |
| `browser_click` | FAIL/UNCONFIRMED: not individually proven; browser chain already stalls. |
| `browser_type` | FAIL/UNCONFIRMED: not individually proven; browser chain already stalls. |
| `browser_select` | FAIL/UNCONFIRMED: not individually proven; browser chain already stalls. |
| `browser_press` | FAIL/UNCONFIRMED: not individually proven; browser chain already stalls. |
| `browser_find_text` | FAIL/UNCONFIRMED: chained browser test stalled. |
| `read_file` | FAIL/UNCONFIRMED: expected after `download_file`, but the loop did not call it. |
| `vs_read_file` | PARTIAL: executes after `vs_write_file`, but final answer still failed. |
| `vs_write_file` | PARTIAL: executes, but filename/path parsing failed in one test. |
| `vs_replace_in_file` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `vs_copy_file` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `vs_rename_file` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `vs_delete_file` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `vs_search_files` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `vs_list_files` | FAIL/UNCONFIRMED: disposable mutation batch stalled. |
| `git_dependency_check` | FAIL: no authorized session/access root. |
| `git_status` | BLOCKED/UNCONFIRMED: read-only Git access blocked after dependency/root failure. |
| `git_changed_files` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_tracked_files` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_diff` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_file_diff` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_file_at_ref` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_file_history` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_file_blame` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_grep` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_log` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_show` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_branch` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_remote` | BLOCKED/UNCONFIRMED: Git root unavailable. |
| `git_stage` | NOT ATTEMPTED: mutating Git action; not safe to run after read-only Git root failure. |
| `git_unstage` | NOT ATTEMPTED: mutating Git action; not safe to run after read-only Git root failure. |
| `git_commit` | NOT ATTEMPTED: mutating Git action; not safe to run after read-only Git root failure. |
| `git_create_branch` | NOT ATTEMPTED: mutating Git action; not safe to run after read-only Git root failure. |
| `git_switch_branch` | NOT ATTEMPTED: mutating Git action; not safe to run after read-only Git root failure. |
| `git_fetch` | NOT ATTEMPTED: network/mutating Git action; not safe to run after read-only Git root failure. |
| `git_pull` | NOT ATTEMPTED: network/mutating Git action; not safe to run after read-only Git root failure. |
| `git_push` | NOT ATTEMPTED: mutating/publishing Git action; outside a smoke test without an explicit user request. |
| `workstation_list_models` | FAIL/UNCONFIRMED: stalled with no result. |
| `workstation_run_model` | NOT ATTEMPTED: depends on a successful model list; listing stalled. |
| `sockjackdml_plan_create` | NOT ATTEMPTED: status/read-only SockJackDML check stalled first. |
| `sockjackdml_progress_document_create` | NOT ATTEMPTED: status/read-only SockJackDML check stalled first. |
| `sockjackdml_plan_execute` | NOT ATTEMPTED: execution/mutation-class tool; status check stalled first. |
| `sockjackdml_workflow_status` | FAIL/UNCONFIRMED: stalled with no result. |
| `sockjackdml_progress_document_find` | NOT ATTEMPTED: status/read-only SockJackDML check stalled first. |
| `sockjackdml_execution_control` | NOT ATTEMPTED: execution-control tool; status check stalled first. |
| `sockjackdml_evidence_link` | NOT ATTEMPTED: mutation/linking tool; status check stalled first. |
| `run_command_in_terminal` | PARTIAL: command ran and returned expected stdout; response rendering dirty. |

### Completion Audit For The Requested Test

Requirement: test `/Auto` mode for consistent answers.

- Evidence: exact sentinel tests in tools and text mode; pass 3 mode smokes.
- Result: FAIL. `/Auto` gives inconsistent dirty output or stalls.

Requirement: confirm tools and report what works.

- Evidence: source inventory plus live tests above.
- Result: PARTIAL/FAIL. Confirmed callable: `internet_search`, `browser_open`, `download_file`, `vs_write_file`, `vs_read_file`, `run_command_in_terminal`. None produced a fully clean final answer in the tested prompts.

Requirement: report broken responses in `llm_report.md`.

- Evidence: detailed broken excerpts and final coverage tables in this file.
- Result: COMPLETE.

Requirement: make sure each advertised tool works.

- Evidence: final inventory table.
- Result: FAIL. The live `/Auto` session does not make each tool work. Many advertised tools are ignored, stalled, blocked by missing authorized roots, or unsafe to attempt after prerequisite read-only failures.

Final audit conclusion: the testing/reporting objective is complete as a diagnostic report. The product behavior under test is not complete or healthy: `/Auto` currently fails the "each tool works" expectation.
