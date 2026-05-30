# Windows Desktop Automation Tool Plan

## Tool Name

`windows_desktop_automation`

## Purpose

Provide a proprietary, local-only desktop control tool for LlmRuntime agents that can inspect visible Windows desktop state and perform approved UI automation in a human-paced, auditable way. The tool is designed for first-party workstation automation, demos, test harnesses, and local agent workflows where the user has explicitly granted control.

## Safety Boundaries

- Every invocation defaults to `AskEveryTime` approval.
- The tool is local-only and Windows-only.
- The tool does not read password fields, scrape hidden text, hook keyboard input, install drivers, persist background services, bypass OS security prompts, or capture credentials.
- Mouse and keyboard actions target the active desktop and are audit logged through the existing tool registry.
- Background window support includes metadata inspection, window management, and direct posted mouse messages to a target window. Text input and OS-cursor pointer input use foreground-aware OS input so actions remain visible to the user.
- Human-like movement means smooth timing and path generation for usability, not stealth or evasion.

## Feature Set

### Window Discovery

- `capabilities`: report supported operations and safety constraints.
- `get_foreground_window`: return foreground handle, title, process name, process id, class name, and bounds.
- `list_windows`: return visible top-level windows, including foreground/background title, process, class, minimized/maximized state, and bounds.
- `find_windows`: filter windows by title substring, process name, class name, or handle.

### Window Control

- `focus_window`: restore and foreground a target window.
- `set_window_bounds`: move and resize a target window by handle/title/process.
- `window_state`: minimize, maximize, or restore a target window.
- `close_window`: request a normal `WM_CLOSE` on a target window.

### Program Launch

- `open_program`: launch an approved executable, document, URL, or shell verb through `ProcessStartInfo`.
- Supports arguments and working directory.
- Always requires explicit tool approval.

### Keyboard Input

- `send_keys`: send text and simple key chords to the foreground window.
- Supports Unicode text input.
- Supports common chords such as `Ctrl+L`, `Ctrl+C`, `Ctrl+V`, `Alt+Tab`, `Enter`, `Escape`, arrows, function keys, and modifiers.
- Optional `target` can focus a window before sending keys.

### Mouse Input

- `mouse_move`: move the OS cursor to a screen coordinate.
- `mouse_click`: left/right/middle click at a screen coordinate with optional double click.
- `mouse_drag`: drag from one coordinate to another.
- Optional `target` focuses a window before input.
- Optional `restore_cursor` returns the cursor to its original coordinate after the action.
- `get_cursor_position`: read the current OS cursor coordinate.

### Target-Window Mouse Input

- `window_mouse_move`: post a mouse move message to a selected window without moving the OS cursor.
- `window_mouse_click`: post button down/up messages to a selected window without moving the OS cursor.
- `window_mouse_drag`: post a move/button path to a selected window without moving the OS cursor.
- Supports `coordinate_space` as `screen` or `client`; screen coordinates are converted with `ScreenToClient`.
- Returns before/after cursor coordinates and `real_cursor_unchanged`.

### Human Cursor Driver

- `preview_cursor_path`: generate a path without moving the cursor.
- `human.enabled`: use eased Bezier paths instead of jumps.
- `human.duration_ms`: total duration.
- `human.steps`: number of intermediate coordinates.
- `human.jitter_pixels`: bounded micro-variation.
- `human.seed`: deterministic path generation for tests/replay.
- `human.restore_cursor`: return to original position after click/drag.

## Input Shape

```json
{
  "operation": "list_windows",
  "target": {
    "handle": "0x0000000000010204",
    "title": "Visual Studio",
    "process": "devenv",
    "class_name": "HwndWrapper"
  },
  "bounds": { "x": 100, "y": 100, "width": 1280, "height": 720 },
  "point": { "x": 500, "y": 400 },
  "start_point": { "x": 500, "y": 400 },
  "end_point": { "x": 900, "y": 600 },
  "coordinate_space": "screen",
  "button": "left",
  "clicks": 1,
  "text": "hello",
  "keys": "Ctrl+L",
  "program": {
    "file_name": "notepad.exe",
    "arguments": "",
    "working_directory": ""
  },
  "query": "notepad",
  "max_results": 50,
  "human": {
    "enabled": true,
    "duration_ms": 450,
    "steps": 36,
    "jitter_pixels": 1.5,
    "restore_cursor": false,
    "seed": 123
  }
}
```

## Implementation Milestones

- Add `DesktopAutomation` scoped permission and built-in invocation path.
- Add `WindowsDesktopAutomationTool` as a self-contained tool class.
- Add `HumanCursorDriver` path generation and foreground-safe mouse execution.
- Add target-window `window_mouse_*` operations that leave the real cursor untouched.
- Register the built-in tool definition from `LlmToolRegistry`.
- Add tests for schema registration, approval gating, and cursor path generation.
- Document safety boundaries and verified status in the LlmRuntime README.
