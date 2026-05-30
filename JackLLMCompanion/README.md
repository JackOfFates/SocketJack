# SocketJack Companion User Guide

`JackLLMCompanion` is the SocketJack/JackLLM companion service. It is a tray-first WPF app with a local web dashboard that lets JACK observe the PC, learn reusable skills from recordings, and perform approval-gated remote-control actions.

V1 is a skill-library trainer, not a model-weight fine-tuner. JACK learns by turning recordings into reviewed skills. Those skills can later be used as context for the LLM runner, but they never bypass permission gates.

## Quick Start

1. Build the app:

   ```powershell
   dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\JackLLMCompanion.csproj
   ```

2. Run `JackLLMCompanion.exe` from the build output:

   ```text
   C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\bin\Debug\net8.0-windows7.0\JackLLMCompanion.exe
   ```

3. Open the web dashboard:

   ```text
   http://localhost/Workspace
   ```

   If port `80` is unavailable, use:

   ```text
   http://localhost:8091/Workspace
   ```

4. Keep `Ctrl+Esc` in mind. It is the emergency stop hotkey.

5. Start with permissions disabled. Enable only the gates needed for the task you are doing.

## Main Concepts

### Companion

The Companion is a local WPF application that usually runs in the tray. It owns the local HTTP server, stores data, records work sessions, shows the web dashboard, and runs the JACK desktop-control loop.

### JACK

JACK is the default companion template. The template stores JACK's name, interests, and personality context. You can edit it from the WPF app or `/Workspace`.

### Recording

A recording is a work session. While recording, Companion stores ordered session events, foreground apps/windows, URLs, people/chat cues, files, control actions, and training keyframes.

### Skill Draft

A skill draft is an inferred workflow created from a recording. It is not used by JACK until reviewed and enabled, unless you explicitly select a more permissive activation mode.

### Permission Gate

A permission gate blocks sensitive capabilities until the user approves them. Skills, remote desktop actions, and LLM tasks all still obey these gates.

## Safety And Emergency Stop

### Emergency Stop

Use emergency stop when JACK is doing something you want stopped immediately.

Emergency stop does this:

1. Disables Live Input.
2. Stops queued/running LLM desktop tasks.
3. Stops active recording.
4. Audits the stop event.

Ways to trigger it:

- Press `Ctrl+Esc`.
- Click `Emergency Stop` in the WPF window.
- Click `Emergency Stop` in `/Workspace`.
- Use the tray menu emergency stop item.

### Permission Gates

The Companion has these gates:

| Gate | What It Allows |
|---|---|
| Human interaction | Sending messages or interacting with real people in chats, websites, email, etc. |
| Spend money | Any attempt to buy, pay, subscribe, donate, bid, or otherwise spend money. |
| Account login | Logging into accounts or interacting with authentication flows. |
| Use real files | Reading, copying, sharing, downloading, or indexing real files. |
| PC settings | Changing system settings or application settings that affect the PC. |
| Internet access | Browsing or using internet-connected sites/services. |
| Live input | Mouse and keyboard control of the desktop. |

Recommended use:

1. Leave gates disabled by default.
2. Start a task or recording.
3. Review pending approvals when they appear.
4. Approve only the specific capability needed.
5. Use emergency stop if behavior is unexpected.

## WPF Application Guide

### Opening The App

1. Launch `JackLLMCompanion.exe`.
2. The WPF window opens unless started hidden.
3. The tray icon remains available while the app is running.
4. Use `Hide` to send the app to the tray.
5. Double-click the tray icon or choose `Show Companion` to bring it back.

### Top Buttons

| Button | Use |
|---|---|
| Open Workspace | Opens `/Workspace` in the browser. |
| Open Files | Opens `/file` in the browser. |
| Emergency Stop | Immediately disables live input and stops active tasks. |
| Hide | Hides the window to the tray. |

### Workspace Tab

Use this tab for a quick local overview.

Steps:

1. Click `Start Recording` to begin a work session.
2. Do the task you want JACK to learn from.
3. Click `Add Manual Event` when something important happens.
4. Click `Stop Recording` to end the session.
5. Companion will start a self-training run from that recording.
6. Click `Refresh` to update the visible state.

What it shows:

- Web workspace URL.
- Database path.
- Current recording state.
- Current projects.
- Recent sessions.
- Recent events.

### LLM Control Tab

Use this tab to queue a desktop-control task for JACK.

Steps:

1. Enter the goal in `Goal`.
2. Choose a mode, such as `assistive`.
3. Set the model endpoint.

   Default:

   ```text
   http://localhost:11435/v1/chat/completions
   ```

4. Click `Refresh` beside the model dropdown to load models from the local JackLLM server.
5. Choose a model from the dropdown.
6. Set max steps.
7. Click `Start Runner`.
8. Click `Queue LLM Task`.
9. Watch the task queue/status.
10. Click `Stop LLM Task` if needed.

Important behavior:

- The runner observes the desktop.
- It asks the model for one small action at a time.
- It executes only approved/gated actions.
- It includes enabled learned skills when relevant.
- Draft/rejected skills are excluded.

### Remote Desktop Tab

Use this tab to view and control the PC through Companion.

Before using it:

1. Go to `Permissions`.
2. Enable `Live mouse/keyboard control`.
3. Return to `Remote Desktop`.

Actions:

- `Capture Frame` captures one screen frame.
- `Live view` starts repeated screen capture.
- `Click Center` clicks the middle of the desktop.
- `Esc` sends Escape.
- `Send Text` types text into the active window.
- Clicking the preview sends a normalized desktop click.

Safety:

- Remote desktop actions are blocked unless Live Input is enabled.
- `Ctrl+Esc` stops live input immediately.
- Screen capture and input attempts are audited.

### File Sharing Tab

Use this tab to share files or folders with Companion and JACK.

Before using it:

1. Go to `Permissions`.
2. Enable `Use real files`.
3. Return to `File Sharing`.

Share a file:

1. Click `File`.
2. Pick a file.
3. Add a note.
4. Click `Share Selected File`.

Share a folder:

1. Click `Folder`.
2. Pick a folder.
3. Add a note.
4. Click `Share Selected File`.

Drag/drop:

1. Drag files or folders onto the File Sharing area.
2. Confirm any sensitive-file approval prompt.

Sensitive approvals:

Companion asks for explicit approval for:

- Executable/script-like files, such as `.exe`, `.bat`, `.cmd`, `.ps1`, `.js`, `.vbs`, `.msi`.
- Sensitive system/app/credential paths, such as `Windows`, `Program Files`, `AppData`, `.ssh`, `.gnupg`, `System32`.

### Training Tab

Use this tab to manage self-training.

Training settings:

1. Choose `Learning`:
   - `Enabled`
   - `Disabled`
2. Choose `Approval mode`:
   - `Review first`
   - `Auto-enable low risk`
   - `Enable all learned skills`
3. Set `Max replay frames`.
4. Set `Max replay MB`.
5. Click `Save Training Settings`.

Approval modes:

| Mode | Behavior |
|---|---|
| Review first | Draft skills must be reviewed before JACK can use them. This is the default. |
| Auto-enable low risk | Low-risk drafts are enabled automatically. Medium/high-risk drafts still need review. |
| Enable all learned skills | All drafts can be enabled automatically after a warning. This is dangerous. |

Train latest session:

1. Record a session from the Workspace tab.
2. Stop the recording.
3. Go to `Training`.
4. Click `Train Latest Session`.
5. Watch `Training runs`.

Cancel training:

1. Click `Cancel Training`.
2. The active run moves to cancelled.

Review a draft skill:

1. Select a draft in `Draft skill review`.
2. Read the trigger, prerequisites, safety gates, evidence refs, and steps.
3. Click one:
   - `Approve Draft` to mark it reviewed but not active.
   - `Enable Draft` to let JACK use it.
   - `Reject Draft` to prevent use.
4. Non-low-risk skills require warning confirmation before enabling.

Replay evidence:

1. Review keyframe evidence in the replay section.
2. Click `Open Replay Folder` to inspect stored keyframes.

### JACK Template Tab

Use this tab to edit JACK's identity and interests.

Fields:

- `Companion name`
- `Hobbies and interests`
- `Template text`

Buttons:

- `AI Name` generates a name from the local template helper.
- `AI Interests` infers interests from saved sessions, apps, files, and notes.
- `Save Template` stores the template.
- `Reload` refreshes the current state.

### Approvals Tab

Use this tab to review pending approvals.

Steps:

1. Select an approval request.
2. Read the capability, source, recommendation, and detail.
3. Click `Approve Selected` or `Deny Selected`.

Approving a Live Input request will allow paused LLM desktop tasks to continue.

### Permissions Tab

Use this tab to turn capability gates on or off.

Steps:

1. Check only the capabilities needed for the current task.
2. Click `Save Permissions`.
3. Return to the task, training, file, or remote desktop tab.

Every permission save is audited.

## Web Workspace Guide

Open:

```text
http://localhost/Workspace
```

Fallback:

```text
http://localhost:8091/Workspace
```

### Header Controls

| Control | Use |
|---|---|
| Start/Stop Recording | Starts or stops the active recording. Stopping starts self-training. |
| Emergency Stop | Stops live input, active tasks, and recording. |
| Files | Opens `/file`. |
| Refresh | Manually refreshes state. |

The page also auto-refreshes.

### Plan Tab

Shows:

- Implementation plan summary.
- Current projects.
- Approval gate status.
- Pending approvals summary.

### Approvals Tab

Use this to approve or deny pending requests from the browser.

Steps:

1. Open `Approvals`.
2. Read the request.
3. Click `Approve` or `Deny`.

### LLM Control Tab

Use this to queue a JACK desktop task from the browser.

Steps:

1. Enter a goal.
2. Pick a control mode.
3. Set the model endpoint.
4. Click `Refresh Models`.
5. Choose a model from the dropdown.
6. Set max steps.
7. Click `Start Runner`.
8. Click `Queue LLM Task`.
9. Watch the task queue.

### Remote Desktop Tab

Before using:

1. Enable Live Input in permissions.
2. Return to Remote Desktop.

Use:

1. Choose transport:
   - `WebSocket`
   - `Chunked stream`
   - `Polling`
2. Choose encoding:
   - `Adaptive`
   - `JPEG`
   - `PNG`
3. Click `Capture Frame` or `Start Live View`.
4. Click the screen preview to send a click.
5. Drag on the screen preview to send a drag.
6. Use the keyboard buttons or text box for input.

### File Sharing Tab

Before using:

1. Enable Use Files.

Upload files:

1. Select one or more files.
2. Optionally select a folder.
3. Add a note.
4. Click `Upload To Companion Share`.

Share a local path:

1. Enter a local file or folder path.
2. Add a note.
3. Click `Share Local Path`.

Drag/drop:

1. Drop files or folders on the drop zone.
2. Confirm sensitive approval prompts.

Shared files appear with download links.

### Training Tab

Use this to manage self-training from the browser.

Save settings:

1. Choose learning enabled/disabled.
2. Choose approval mode.
3. Set max frames and max replay MB.
4. Click `Save Training Settings`.

Start training:

1. Make sure there is at least one recorded session.
2. Click `Train From Latest Session`.

Review drafts:

1. Read the draft skill card.
2. Click `Approve`, `Enable`, or `Reject`.
3. Confirm warnings for non-low-risk skills.

Replay:

1. Check `Replay Evidence`.
2. Click `open` on a keyframe to view it.

### Memory Tab

Shows:

- Work sessions.
- Recent events.
- JACK template.
- Learned skills.
- People memory.

Use this tab to edit JACK's template and inspect what the Companion remembers.

### Audit Tab

Shows recent audit events, including:

- Permission saves.
- Approval decisions.
- Recording events.
- Control actions.
- Training actions.
- Emergency stop actions.

## `/file` Guide

Open:

```text
http://localhost/file
```

The file browser shows:

- Shared files.
- Session files.
- File kind.
- Session association.
- Notes.
- Download links for shared files.

Downloads require the Use Files gate.

## Self-Training Flow

### Recommended Training Workflow

1. Open WPF or `/Workspace`.
2. Click `Start Recording`.
3. Perform the task naturally.
4. Add manual events at important moments.
5. Stop the recording.
6. Companion captures stop keyframes and starts a training run.
7. Open the Training tab.
8. Wait for the run to finish.
9. Review the generated draft skill.
10. Enable only skills you want JACK to reuse.
11. Queue an LLM task that matches the skill.
12. JACK will include the enabled skill in its prompt if the current context is relevant.

### What Gets Captured

Training evidence can include:

- Session start/stop.
- Ordered session events.
- Foreground app name.
- Foreground window title.
- Browser URL cue.
- Person/chat cue.
- Recent files.
- Control action traces.
- Minimized replay keyframes.
- Sensitivity flags.

### What Gets Redacted Or Tagged

Companion tags or redacts evidence involving:

- Account login.
- Password-like text.
- Tokens/API keys/secrets.
- Money/payment/checkout/banking cues.
- Human chat cues.
- Real files.
- PC settings/system paths.
- Internet/browser use.

### `needs_model` Status

A training run becomes `needs_model` when evidence is ready but a model endpoint is missing or unavailable.

To resolve:

1. Start or configure an OpenAI-compatible model endpoint.
2. Set the runner endpoint in WPF or `/Workspace`.
3. Start training again for the same session.

## LLM Runner And Learned Skills

Only enabled reviewed skills are included in runner prompts.

The runner ranks skills against:

- User goal.
- Foreground app.
- Foreground window.
- URL cue.
- Person/chat cue.
- Recent files.

The runner excludes:

- Draft skills.
- Approved but not enabled skills.
- Rejected skills.
- Skills without matching context.

Permission gates still apply even when a skill is enabled.

## Local Data Layout

Companion data is stored under:

```text
%LOCALAPPDATA%\SocketJack\Companion
```

Important files/folders:

| Path | Purpose |
|---|---|
| `companion-data.json` | SocketJack `DataServer` database. |
| `companion-cache.json` | Data cache metadata. |
| `SharedFiles` | Files/folders copied into the Companion share. |
| `Training\ReplayFrames` | Minimized replay JPEG keyframes and metadata. |

## HTTP API Reference

### Workspace

| Route | Purpose |
|---|---|
| `GET /Workspace` | Main web dashboard. |
| `GET /file` | File/session browser. |
| `GET /api/workspace` | Full workspace state. |
| `GET /api/files` | File/session/share state. |

### Permissions And Approvals

| Route | Purpose |
|---|---|
| `POST /api/companion/permissions` | Save permission gates. |
| `POST /api/companion/approvals/decide` | Approve or deny pending requests. |

### Recording

| Route | Purpose |
|---|---|
| `POST /api/companion/recording/start` | Start a recording session. |
| `POST /api/companion/recording/stop` | Stop recording and start training. |
| `POST /api/companion/action` | Record/gate a control action. |

### LLM Runner

| Route | Purpose |
|---|---|
| `GET /api/companion/llm/runner` | Get runner status. |
| `POST /api/companion/llm/runner/start` | Start/reconfigure runner. |
| `POST /api/companion/llm/runner/stop` | Stop runner. |
| `POST /api/companion/llm/config` | Save runner config. |
| `POST /api/companion/llm/task` | Queue LLM desktop task. |
| `POST /api/companion/llm/stop` | Stop queued/running LLM tasks. |

### Remote Desktop

| Route | Purpose |
|---|---|
| `GET /api/companion/screen` | Screen capture file response. |
| `GET /api/companion/screen.json` | Base64 JSON screen frame. |
| `GET /api/companion/desktop/transport` | Transport discovery. |
| `GET /api/companion/desktop/stream` | Chunked NDJSON desktop stream. |
| `GET /api/companion/desktop/ws` | WebSocket desktop stream. |
| `POST /api/companion/input` | Mouse/keyboard input. |

### File Sharing

| Route | Purpose |
|---|---|
| `GET /api/share` | Shared file metadata. |
| `POST /api/share/upload` | Upload file/folder data. |
| `POST /api/share/register-path` | Copy a local file/folder path into share. |
| `GET /api/share/download` | Download shared file by id. |
| `POST /api/companion/file/register` | Register a file with a work session. |

### Self-Training

| Route | Purpose |
|---|---|
| `GET /api/companion/training/state` | Training settings, runs, evidence, drafts, executions. |
| `POST /api/companion/training/start` | Start a training run. |
| `POST /api/companion/training/cancel` | Cancel active training. |
| `POST /api/companion/training/settings` | Save training settings. |
| `POST /api/companion/skills/review` | Approve, enable, or reject a draft. |
| `GET /api/companion/replay` | List replay keyframe evidence. |
| `GET /api/companion/replay/frame` | Open replay keyframe by evidence id. |

## Build And Verification

Build Companion:

```powershell
dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLMCompanion\JackLLMCompanion.csproj
```

Build JackLLM:

```powershell
dotnet build C:\Users\Vin\Documents\GitHub\SocketJack\JackLLM\JackLLM.csproj
```

Expected result:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

## Troubleshooting

### `/Workspace` Does Not Open

1. Try `http://localhost/Workspace`.
2. Try `http://localhost:8091/Workspace`.
3. Confirm the WPF app is running.
4. Check whether another service is using port `80`.

### Remote Desktop Is Blocked

1. Open `Permissions`.
2. Enable `Live mouse/keyboard control`.
3. Save permissions.
4. Try capture/live view again.

### File Sharing Is Blocked

1. Open `Permissions`.
2. Enable `Use real files`.
3. Save permissions.
4. Retry file/folder share.
5. Confirm any sensitive-file warning.

### Training Says `needs_model`

1. Start an OpenAI-compatible model endpoint.
2. Enter the endpoint in LLM Control.
3. Click `Start Runner`.
4. Restart training from the Training tab.

### JACK Does Not Use A Learned Skill

1. Confirm the skill draft is `enabled`, not just `approved`.
2. Confirm the task goal/app/window resembles the skill trigger.
3. Confirm the required permission gates are enabled.
4. Queue the LLM task again.

### Something Unexpected Happens

1. Press `Ctrl+Esc`.
2. Review the Audit tab.
3. Disable Live Input if it is still enabled.
4. Review pending approvals before continuing.

## Implementation Notes

- Main WPF app: `MainWindow.xaml` and `MainWindow.xaml.cs`.
- Local web server and embedded web UI: `CompanionHttpHost.cs`.
- Data and tables: `CompanionRepository.cs`.
- Self-training service: `CompanionTrainingService.cs`.
- Desktop capture/input: `DesktopAutomationService.cs`.
- LLM task loop: `CompanionLlmRunner.cs`.
- Active implementation ledger: root `progress.md`.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>9,817 lines / 14 files</code></summary>

<br>

<strong>Scope:</strong> <code>JackLLMCompanion</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 9 | 8,043 |
| XAML | 2 | 1,116 |
| Markdown | 2 | 636 |
| MSBuild/XML | 1 | 22 |
| **Total** | **14** | **9,817** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
