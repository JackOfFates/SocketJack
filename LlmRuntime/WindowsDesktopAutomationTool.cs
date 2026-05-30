using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LlmRuntime;

public sealed class WindowsDesktopAutomationTool : ILlmTool
{
    public const string ToolId = "windows_desktop_automation";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public string Id => ToolId;

    public LlmToolDefinition Definition => CreateDefinition();

    public static LlmToolDefinition CreateDefinition() => new()
    {
        Id = ToolId,
        Name = ToolId,
        Description = "Approved local Windows desktop automation: inspect visible windows, move/resize/focus windows, launch programs, send foreground keyboard input, and perform human-paced mouse movement/clicks.",
        Visibility = LlmToolVisibility.Proprietary,
        SourceType = LlmToolSourceType.BuiltInSocketJack,
        Source = ToolId,
        Version = "1.0.0",
        Vendor = "SocketJack LlmRuntime",
        LicenseNotes = "Local first-party automation only. Requires explicit approval. Does not install hooks, read passwords, or bypass OS security prompts.",
        Tags = ["windows", "desktop", "automation", "proprietary", "local-only", "input"],
        ApprovalMode = LlmToolApprovalMode.AskEveryTime,
        Permissions = LlmToolPermissions.DesktopAutomation | LlmToolPermissions.ShellExecution,
        TimeoutSeconds = 30,
        InputSchema = JsonDocument.Parse("""
        {
          "type": "object",
          "required": ["operation"],
          "properties": {
            "operation": {
              "type": "string",
              "enum": [
                "capabilities",
                "get_foreground_window",
                "get_cursor_position",
                "list_windows",
                "find_windows",
                "focus_window",
                "set_window_bounds",
                "window_state",
                "close_window",
                "open_program",
                "send_keys",
                "mouse_move",
                "mouse_click",
                "mouse_drag",
                "window_mouse_move",
                "window_mouse_click",
                "window_mouse_drag",
                "preview_cursor_path"
              ]
            },
            "target": {
              "type": "object",
              "properties": {
                "handle": { "type": "string" },
                "title": { "type": "string" },
                "process": { "type": "string" },
                "class_name": { "type": "string" }
              }
            },
            "bounds": {
              "type": "object",
              "properties": {
                "x": { "type": "integer" },
                "y": { "type": "integer" },
                "width": { "type": "integer" },
                "height": { "type": "integer" }
              }
            },
            "point": {
              "type": "object",
              "properties": {
                "x": { "type": "integer" },
                "y": { "type": "integer" }
              }
            },
            "start_point": {
              "type": "object",
              "properties": {
                "x": { "type": "integer" },
                "y": { "type": "integer" }
              }
            },
            "end_point": {
              "type": "object",
              "properties": {
                "x": { "type": "integer" },
                "y": { "type": "integer" }
              }
            },
            "button": { "type": "string", "enum": ["left", "right", "middle"] },
            "coordinate_space": { "type": "string", "enum": ["screen", "client"] },
            "clicks": { "type": "integer", "minimum": 1, "maximum": 2 },
            "text": { "type": "string" },
            "keys": { "type": "string" },
            "program": {
              "type": "object",
              "properties": {
                "file_name": { "type": "string" },
                "arguments": { "type": "string" },
                "working_directory": { "type": "string" }
              }
            },
            "query": { "type": "string" },
            "max_results": { "type": "integer", "minimum": 1, "maximum": 250 },
            "state": { "type": "string", "enum": ["minimize", "maximize", "restore"] },
            "human": {
              "type": "object",
              "properties": {
                "enabled": { "type": "boolean" },
                "duration_ms": { "type": "integer", "minimum": 0, "maximum": 10000 },
                "steps": { "type": "integer", "minimum": 2, "maximum": 240 },
                "jitter_pixels": { "type": "number", "minimum": 0, "maximum": 12 },
                "restore_cursor": { "type": "boolean" },
                "seed": { "type": "integer" }
              }
            }
          }
        }
        """).RootElement.Clone()
    };

    public Task<LlmToolInvocationResult> InvokeAsync(LlmToolInvocationRequest request, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            return Task.FromResult(Failed("Windows desktop automation is only available on Windows."));

        try
        {
            string operation = GetString(request.Input, "operation")?.Trim().ToLowerInvariant() ?? "";
            object output = operation switch
            {
                "capabilities" => BuildCapabilities(),
                "get_foreground_window" => GetForegroundWindowInfo(),
                "get_cursor_position" => new { cursor = GetCursorPosition() },
                "list_windows" => new { windows = ListWindows(GetInt32(request.Input, "max_results") ?? 100) },
                "find_windows" => new { windows = FindWindows(request.Input, GetInt32(request.Input, "max_results") ?? 50) },
                "focus_window" => FocusWindow(request.Input),
                "set_window_bounds" => SetWindowBounds(request.Input),
                "window_state" => SetWindowState(request.Input),
                "close_window" => CloseWindow(request.Input),
                "open_program" => OpenProgram(request.Input),
                "send_keys" => SendKeys(request.Input, cancellationToken),
                "mouse_move" => MouseMove(request.Input, cancellationToken),
                "mouse_click" => MouseClick(request.Input, cancellationToken),
                "mouse_drag" => MouseDrag(request.Input, cancellationToken),
                "window_mouse_move" => WindowMouseMove(request.Input, cancellationToken),
                "window_mouse_click" => WindowMouseClick(request.Input, cancellationToken),
                "window_mouse_drag" => WindowMouseDrag(request.Input, cancellationToken),
                "preview_cursor_path" => PreviewCursorPath(request.Input),
                _ => throw new InvalidOperationException("Unknown desktop automation operation: " + operation)
            };

            return Task.FromResult(Succeeded(output));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Failed(ex.Message));
        }
    }

    private static object BuildCapabilities() => new
    {
        tool = ToolId,
        platform = "windows",
        operations = new[]
        {
            "capabilities",
            "get_foreground_window",
            "get_cursor_position",
            "list_windows",
            "find_windows",
            "focus_window",
            "set_window_bounds",
            "window_state",
            "close_window",
            "open_program",
            "send_keys",
            "mouse_move",
            "mouse_click",
            "mouse_drag",
            "window_mouse_move",
            "window_mouse_click",
            "window_mouse_drag",
            "preview_cursor_path"
        },
        safety = new
        {
            approval_required = true,
            foreground_input_only = true,
            non_cursor_window_mouse_input = true,
            credential_capture = false,
            installs_hooks_or_drivers = false,
            note = "OS cursor mouse and keyboard actions use visible desktop input. window_mouse_* posts mouse messages to an explicitly targeted window without moving the real cursor."
        }
    };

    private static object GetForegroundWindowInfo()
    {
        IntPtr handle = NativeMethods.GetForegroundWindow();
        return new { window = CreateWindowInfo(handle) };
    }

    private static IReadOnlyList<DesktopWindowInfo> ListWindows(int maxResults)
    {
        maxResults = Math.Clamp(maxResults, 1, 250);
        var windows = new List<DesktopWindowInfo>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (windows.Count >= maxResults)
                return false;

            var info = CreateWindowInfo(handle);
            if (info.Visible && !string.IsNullOrWhiteSpace(info.Title))
                windows.Add(info);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private static IReadOnlyList<DesktopWindowInfo> FindWindows(JsonElement input, int maxResults)
    {
        string query = GetString(input, "query") ?? "";
        var target = GetObject(input, "target");
        string title = target.HasValue ? GetString(target.Value, "title") ?? "" : "";
        string process = target.HasValue ? GetString(target.Value, "process") ?? "" : "";
        string className = target.HasValue ? GetString(target.Value, "class_name") ?? GetString(target.Value, "className") ?? "" : "";

        return ListWindows(Math.Max(maxResults, 1))
            .Where(window => Matches(window.Title, query)
                             || Matches(window.ProcessName, query)
                             || Matches(window.ClassName, query)
                             || Matches(window.Title, title)
                             || Matches(window.ProcessName, process)
                             || Matches(window.ClassName, className))
            .Take(Math.Clamp(maxResults, 1, 250))
            .ToList();
    }

    private static object FocusWindow(JsonElement input)
    {
        IntPtr handle = ResolveWindow(input);
        NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        bool focused = NativeMethods.SetForegroundWindow(handle);
        return new { focused, window = CreateWindowInfo(handle) };
    }

    private static object SetWindowBounds(JsonElement input)
    {
        IntPtr handle = ResolveWindow(input);
        JsonElement bounds = GetObject(input, "bounds") ?? throw new InvalidOperationException("bounds is required.");
        int x = GetInt32(bounds, "x") ?? throw new InvalidOperationException("bounds.x is required.");
        int y = GetInt32(bounds, "y") ?? throw new InvalidOperationException("bounds.y is required.");
        int width = Math.Max(1, GetInt32(bounds, "width") ?? throw new InvalidOperationException("bounds.width is required."));
        int height = Math.Max(1, GetInt32(bounds, "height") ?? throw new InvalidOperationException("bounds.height is required."));
        bool moved = NativeMethods.MoveWindow(handle, x, y, width, height, true);
        return new { moved, window = CreateWindowInfo(handle) };
    }

    private static object SetWindowState(JsonElement input)
    {
        IntPtr handle = ResolveWindow(input);
        string state = GetString(input, "state")?.Trim().ToLowerInvariant() ?? "restore";
        int command = state switch
        {
            "minimize" => NativeMethods.SW_MINIMIZE,
            "maximize" => NativeMethods.SW_MAXIMIZE,
            "restore" => NativeMethods.SW_RESTORE,
            _ => throw new InvalidOperationException("Unknown window state: " + state)
        };
        bool changed = NativeMethods.ShowWindow(handle, command);
        return new { changed, state, window = CreateWindowInfo(handle) };
    }

    private static object CloseWindow(JsonElement input)
    {
        IntPtr handle = ResolveWindow(input);
        bool posted = NativeMethods.PostMessage(handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        return new { close_requested = posted, handle = ToHandleString(handle) };
    }

    private static object OpenProgram(JsonElement input)
    {
        JsonElement program = GetObject(input, "program") ?? throw new InvalidOperationException("program is required.");
        string fileName = GetString(program, "file_name") ?? GetString(program, "fileName") ?? "";
        if (string.IsNullOrWhiteSpace(fileName))
            throw new InvalidOperationException("program.file_name is required.");

        var start = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = GetString(program, "arguments") ?? "",
            WorkingDirectory = GetString(program, "working_directory") ?? GetString(program, "workingDirectory") ?? "",
            UseShellExecute = true
        };

        var process = Process.Start(start);
        return new
        {
            started = process != null,
            process_id = process?.Id,
            process_name = process?.ProcessName,
            file_name = fileName
        };
    }

    private static object SendKeys(JsonElement input, CancellationToken cancellationToken)
    {
        FocusTargetIfProvided(input);
        string text = GetString(input, "text") ?? "";
        string keys = GetString(input, "keys") ?? "";
        int sentTextChars = 0;
        int sentKeyChords = 0;

        if (!string.IsNullOrEmpty(text))
        {
            foreach (char ch in text)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SendUnicodeChar(ch);
                sentTextChars++;
                Thread.Sleep(5);
            }
        }

        if (!string.IsNullOrWhiteSpace(keys))
        {
            foreach (string chord in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                cancellationToken.ThrowIfCancellationRequested();
                SendKeyChord(chord);
                sentKeyChords++;
                Thread.Sleep(25);
            }
        }

        return new { sent_text_chars = sentTextChars, sent_key_chords = sentKeyChords, foreground = GetForegroundWindowInfo() };
    }

    private static object MouseMove(JsonElement input, CancellationToken cancellationToken)
    {
        var point = ReadPoint(input, "point");
        var human = ReadHumanOptions(input);
        var original = GetCursorPosition();
        MoveCursor(original, point, human, cancellationToken);
        return new { moved = true, from = original, to = point, path = HumanCursorDriver.GeneratePath(original, point, human) };
    }

    private static object MouseClick(JsonElement input, CancellationToken cancellationToken)
    {
        FocusTargetIfProvided(input);
        var point = ReadPoint(input, "point");
        var human = ReadHumanOptions(input);
        var original = GetCursorPosition();
        MoveCursor(original, point, human, cancellationToken);

        string button = GetString(input, "button")?.Trim().ToLowerInvariant() ?? "left";
        int clicks = Math.Clamp(GetInt32(input, "clicks") ?? 1, 1, 2);
        for (int i = 0; i < clicks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SendMouseButton(button);
            if (i + 1 < clicks)
                Thread.Sleep(90);
        }

        if (human.RestoreCursor)
            MoveCursor(point, original, human with { RestoreCursor = false }, cancellationToken);

        return new { clicked = true, button, clicks, point, restored_cursor = human.RestoreCursor };
    }

    private static object MouseDrag(JsonElement input, CancellationToken cancellationToken)
    {
        FocusTargetIfProvided(input);
        var start = ReadPoint(input, "point");
        var end = ReadPoint(input, "end_point");
        var human = ReadHumanOptions(input);
        var original = GetCursorPosition();
        MoveCursor(original, start, human, cancellationToken);
        SendMouseButtonDown(GetString(input, "button") ?? "left");
        MoveCursor(start, end, human, cancellationToken);
        SendMouseButtonUp(GetString(input, "button") ?? "left");

        if (human.RestoreCursor)
            MoveCursor(end, original, human with { RestoreCursor = false }, cancellationToken);

        return new { dragged = true, from = start, to = end, restored_cursor = human.RestoreCursor };
    }

    private static object WindowMouseMove(JsonElement input, CancellationToken cancellationToken)
    {
        IntPtr handle = ResolveWindow(input);
        var destination = ReadPoint(input, "point");
        var human = ReadHumanOptions(input);
        var source = GetObject(input, "start_point").HasValue ? ReadPoint(input, "start_point") : destination;
        var cursorBefore = GetCursorPosition();
        var path = source == destination ? [destination] : HumanCursorDriver.GeneratePath(source, destination, human with { Enabled = true });
        PostMousePath(handle, path, input, 0, cancellationToken);
        var cursorAfter = GetCursorPosition();
        return new
        {
            posted = true,
            handle = ToHandleString(handle),
            real_cursor_unchanged = cursorBefore == cursorAfter,
            cursor_before = cursorBefore,
            cursor_after = cursorAfter,
            path
        };
    }

    private static object WindowMouseClick(JsonElement input, CancellationToken cancellationToken)
    {
        IntPtr handle = ResolveWindow(input);
        var destination = ReadPoint(input, "point");
        var human = ReadHumanOptions(input);
        var source = GetObject(input, "start_point").HasValue ? ReadPoint(input, "start_point") : destination;
        var cursorBefore = GetCursorPosition();
        var path = source == destination ? [destination] : HumanCursorDriver.GeneratePath(source, destination, human with { Enabled = true });
        PostMousePath(handle, path, input, 0, cancellationToken);

        string button = GetString(input, "button")?.Trim().ToLowerInvariant() ?? "left";
        int clicks = Math.Clamp(GetInt32(input, "clicks") ?? 1, 1, 2);
        var clientPoint = ToClientPoint(handle, destination, input);
        for (int i = 0; i < clicks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PostWindowMouseButton(handle, clientPoint, button, down: true);
            Thread.Sleep(35);
            PostWindowMouseButton(handle, clientPoint, button, down: false);
            if (i + 1 < clicks)
                Thread.Sleep(90);
        }

        var cursorAfter = GetCursorPosition();
        return new
        {
            posted = true,
            button,
            clicks,
            handle = ToHandleString(handle),
            real_cursor_unchanged = cursorBefore == cursorAfter,
            cursor_before = cursorBefore,
            cursor_after = cursorAfter,
            path
        };
    }

    private static object WindowMouseDrag(JsonElement input, CancellationToken cancellationToken)
    {
        IntPtr handle = ResolveWindow(input);
        var start = ReadPoint(input, "point");
        var end = ReadPoint(input, "end_point");
        var human = ReadHumanOptions(input);
        string button = GetString(input, "button")?.Trim().ToLowerInvariant() ?? "left";
        var cursorBefore = GetCursorPosition();
        var path = HumanCursorDriver.GeneratePath(start, end, human with { Enabled = true });
        var startClient = ToClientPoint(handle, start, input);
        var endClient = ToClientPoint(handle, end, input);
        PostWindowMouseButton(handle, startClient, button, down: true);
        PostMousePath(handle, path, input, MouseButtonWParam(button), cancellationToken);
        PostWindowMouseButton(handle, endClient, button, down: false);
        var cursorAfter = GetCursorPosition();
        return new
        {
            posted = true,
            button,
            handle = ToHandleString(handle),
            real_cursor_unchanged = cursorBefore == cursorAfter,
            cursor_before = cursorBefore,
            cursor_after = cursorAfter,
            path
        };
    }

    private static object PreviewCursorPath(JsonElement input)
    {
        var start = GetObject(input, "point").HasValue ? ReadPoint(input, "point") : GetCursorPosition();
        var end = ReadPoint(input, "end_point");
        var human = ReadHumanOptions(input) with { Enabled = true };
        var path = HumanCursorDriver.GeneratePath(start, end, human);
        return new { from = start, to = end, path };
    }

    private static void FocusTargetIfProvided(JsonElement input)
    {
        if (GetObject(input, "target").HasValue)
            FocusWindow(input);
    }

    private static void MoveCursor(DesktopPoint from, DesktopPoint to, HumanCursorOptions options, CancellationToken cancellationToken)
    {
        var path = options.Enabled ? HumanCursorDriver.GeneratePath(from, to, options) : [to];
        int delay = path.Count <= 1 ? 0 : Math.Max(0, options.DurationMs / path.Count);
        foreach (var point in path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeMethods.SetCursorPos(point.X, point.Y);
            if (delay > 0)
                Thread.Sleep(delay);
        }
    }

    private static void PostMousePath(IntPtr handle, IReadOnlyList<DesktopPoint> path, JsonElement input, int wParam, CancellationToken cancellationToken)
    {
        int delay = path.Count <= 1 ? 0 : Math.Max(0, ReadHumanOptions(input).DurationMs / path.Count);
        foreach (var point in path)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clientPoint = ToClientPoint(handle, point, input);
            NativeMethods.PostMessage(handle, NativeMethods.WM_MOUSEMOVE, new IntPtr(wParam), MakeLParam(clientPoint.X, clientPoint.Y));
            if (delay > 0)
                Thread.Sleep(delay);
        }
    }

    private static DesktopPoint ToClientPoint(IntPtr handle, DesktopPoint point, JsonElement input)
    {
        string coordinateSpace = GetString(input, "coordinate_space") ?? GetString(input, "coordinateSpace") ?? "screen";
        if (coordinateSpace.Equals("client", StringComparison.OrdinalIgnoreCase))
            return point;

        var nativePoint = new NativePoint { X = point.X, Y = point.Y };
        NativeMethods.ScreenToClient(handle, ref nativePoint);
        return new DesktopPoint(nativePoint.X, nativePoint.Y);
    }

    private static DesktopPoint GetCursorPosition()
    {
        NativeMethods.GetCursorPos(out NativePoint point);
        return new DesktopPoint(point.X, point.Y);
    }

    private static DesktopPoint ReadPoint(JsonElement input, string propertyName)
    {
        JsonElement point = GetObject(input, propertyName) ?? throw new InvalidOperationException(propertyName + " is required.");
        return new DesktopPoint(
            GetInt32(point, "x") ?? throw new InvalidOperationException(propertyName + ".x is required."),
            GetInt32(point, "y") ?? throw new InvalidOperationException(propertyName + ".y is required."));
    }

    private static HumanCursorOptions ReadHumanOptions(JsonElement input)
    {
        var human = GetObject(input, "human");
        if (!human.HasValue)
            return HumanCursorOptions.Default;

        return new HumanCursorOptions(
            Enabled: GetBool(human.Value, "enabled") ?? true,
            DurationMs: Math.Clamp(GetInt32(human.Value, "duration_ms") ?? GetInt32(human.Value, "durationMs") ?? 450, 0, 10000),
            Steps: Math.Clamp(GetInt32(human.Value, "steps") ?? 36, 2, 240),
            JitterPixels: Math.Clamp(GetDouble(human.Value, "jitter_pixels") ?? GetDouble(human.Value, "jitterPixels") ?? 1.5d, 0d, 12d),
            RestoreCursor: GetBool(human.Value, "restore_cursor") ?? GetBool(human.Value, "restoreCursor") ?? false,
            Seed: GetInt32(human.Value, "seed"));
    }

    private static IntPtr ResolveWindow(JsonElement input)
    {
        var target = GetObject(input, "target") ?? input;
        string handleValue = GetString(target, "handle") ?? "";
        if (!string.IsNullOrWhiteSpace(handleValue) && TryParseHandle(handleValue, out IntPtr handle) && NativeMethods.IsWindow(handle))
            return handle;

        string title = GetString(target, "title") ?? "";
        string process = GetString(target, "process") ?? "";
        string className = GetString(target, "class_name") ?? GetString(target, "className") ?? "";
        var match = ListWindows(250).FirstOrDefault(window =>
            Matches(window.Title, title) &&
            Matches(window.ProcessName, process) &&
            Matches(window.ClassName, className));

        if (match == null)
            throw new InvalidOperationException("Target window was not found.");
        return ParseHandle(match.Handle);
    }

    private static DesktopWindowInfo CreateWindowInfo(IntPtr handle)
    {
        string title = GetWindowText(handle);
        string className = GetClassName(handle);
        NativeMethods.GetWindowThreadProcessId(handle, out uint processId);
        string processName = "";
        try
        {
            processName = processId == 0 ? "" : Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
        }

        NativeMethods.GetWindowRect(handle, out NativeRect rect);
        bool visible = NativeMethods.IsWindowVisible(handle);
        bool iconic = NativeMethods.IsIconic(handle);
        bool zoomed = NativeMethods.IsZoomed(handle);
        bool foreground = handle == NativeMethods.GetForegroundWindow();
        return new DesktopWindowInfo(
            ToHandleString(handle),
            title,
            processName,
            (int)processId,
            className,
            visible,
            foreground,
            iconic,
            zoomed,
            new DesktopBounds(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top)));
    }

    private static string GetWindowText(IntPtr handle)
    {
        int length = NativeMethods.GetWindowTextLength(handle);
        var builder = new StringBuilder(Math.Max(length + 1, 256));
        NativeMethods.GetWindowText(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static string GetClassName(IntPtr handle)
    {
        var builder = new StringBuilder(256);
        NativeMethods.GetClassName(handle, builder, builder.Capacity);
        return builder.ToString();
    }

    private static bool Matches(string value, string filter) =>
        string.IsNullOrWhiteSpace(filter) || (!string.IsNullOrEmpty(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseHandle(string value, out IntPtr handle)
    {
        handle = IntPtr.Zero;
        value = (value ?? "").Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return nint.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out nint parsedHex) && Assign(out handle, parsedHex);
        return nint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out nint parsed) && Assign(out handle, parsed);
    }

    private static bool Assign(out IntPtr handle, nint value)
    {
        handle = value;
        return handle != IntPtr.Zero;
    }

    private static IntPtr ParseHandle(string value) => TryParseHandle(value, out IntPtr handle) ? handle : IntPtr.Zero;

    private static string ToHandleString(IntPtr handle) => "0x" + handle.ToInt64().ToString("X", CultureInfo.InvariantCulture);

    private static void SendUnicodeChar(char ch)
    {
        var inputs = new[]
        {
            NativeInput.KeyboardUnicode(ch, keyUp: false),
            NativeInput.KeyboardUnicode(ch, keyUp: true)
        };
        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    private static void SendKeyChord(string chord)
    {
        var parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return;

        var modifiers = new List<ushort>();
        ushort key = 0;
        foreach (string part in parts)
        {
            ushort vk = KeyNameToVirtualKey(part);
            if (vk is NativeMethods.VK_CONTROL or NativeMethods.VK_MENU or NativeMethods.VK_SHIFT or NativeMethods.VK_LWIN)
                modifiers.Add(vk);
            else
                key = vk;
        }

        if (key == 0)
            throw new InvalidOperationException("Key chord did not include a non-modifier key.");

        var inputs = new List<NativeInput>();
        inputs.AddRange(modifiers.Select(modifier => NativeInput.KeyboardVirtualKey(modifier, keyUp: false)));
        inputs.Add(NativeInput.KeyboardVirtualKey(key, keyUp: false));
        inputs.Add(NativeInput.KeyboardVirtualKey(key, keyUp: true));
        for (int i = modifiers.Count - 1; i >= 0; i--)
            inputs.Add(NativeInput.KeyboardVirtualKey(modifiers[i], keyUp: true));

        NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<NativeInput>());
    }

    private static ushort KeyNameToVirtualKey(string key)
    {
        key = key.Trim().ToUpperInvariant();
        if (key.Length == 1)
        {
            char ch = key[0];
            if (ch is >= 'A' and <= 'Z')
                return ch;
            if (ch is >= '0' and <= '9')
                return ch;
        }

        if (key.StartsWith("F", StringComparison.Ordinal) && int.TryParse(key[1..], out int function) && function is >= 1 and <= 24)
            return (ushort)(NativeMethods.VK_F1 + function - 1);

        return key switch
        {
            "CTRL" or "CONTROL" => NativeMethods.VK_CONTROL,
            "ALT" => NativeMethods.VK_MENU,
            "SHIFT" => NativeMethods.VK_SHIFT,
            "WIN" or "WINDOWS" => NativeMethods.VK_LWIN,
            "ENTER" or "RETURN" => NativeMethods.VK_RETURN,
            "ESC" or "ESCAPE" => NativeMethods.VK_ESCAPE,
            "TAB" => NativeMethods.VK_TAB,
            "SPACE" => NativeMethods.VK_SPACE,
            "BACKSPACE" => NativeMethods.VK_BACK,
            "DELETE" or "DEL" => NativeMethods.VK_DELETE,
            "HOME" => NativeMethods.VK_HOME,
            "END" => NativeMethods.VK_END,
            "PAGEUP" or "PGUP" => NativeMethods.VK_PRIOR,
            "PAGEDOWN" or "PGDN" => NativeMethods.VK_NEXT,
            "LEFT" => NativeMethods.VK_LEFT,
            "RIGHT" => NativeMethods.VK_RIGHT,
            "UP" => NativeMethods.VK_UP,
            "DOWN" => NativeMethods.VK_DOWN,
            _ => throw new InvalidOperationException("Unsupported key name: " + key)
        };
    }

    private static void SendMouseButton(string button)
    {
        SendMouseButtonDown(button);
        Thread.Sleep(35);
        SendMouseButtonUp(button);
    }

    private static void SendMouseButtonDown(string button) => SendMouse(button, down: true);

    private static void SendMouseButtonUp(string button) => SendMouse(button, down: false);

    private static void SendMouse(string button, bool down)
    {
        uint flags = button.Trim().ToLowerInvariant() switch
        {
            "right" => down ? NativeMethods.MOUSEEVENTF_RIGHTDOWN : NativeMethods.MOUSEEVENTF_RIGHTUP,
            "middle" => down ? NativeMethods.MOUSEEVENTF_MIDDLEDOWN : NativeMethods.MOUSEEVENTF_MIDDLEUP,
            _ => down ? NativeMethods.MOUSEEVENTF_LEFTDOWN : NativeMethods.MOUSEEVENTF_LEFTUP
        };
        var input = NativeInput.Mouse(flags);
        NativeMethods.SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    }

    private static void PostWindowMouseButton(IntPtr handle, DesktopPoint clientPoint, string button, bool down)
    {
        uint message = button.Trim().ToLowerInvariant() switch
        {
            "right" => down ? NativeMethods.WM_RBUTTONDOWN : NativeMethods.WM_RBUTTONUP,
            "middle" => down ? NativeMethods.WM_MBUTTONDOWN : NativeMethods.WM_MBUTTONUP,
            _ => down ? NativeMethods.WM_LBUTTONDOWN : NativeMethods.WM_LBUTTONUP
        };
        int wParam = down ? MouseButtonWParam(button) : 0;
        NativeMethods.PostMessage(handle, message, new IntPtr(wParam), MakeLParam(clientPoint.X, clientPoint.Y));
    }

    private static int MouseButtonWParam(string button) =>
        button.Trim().ToLowerInvariant() switch
        {
            "right" => NativeMethods.MK_RBUTTON,
            "middle" => NativeMethods.MK_MBUTTON,
            _ => NativeMethods.MK_LBUTTON
        };

    private static IntPtr MakeLParam(int low, int high) =>
        new((high << 16) | (low & 0xFFFF));

    private static LlmToolInvocationResult Succeeded(object output)
    {
        string json = JsonSerializer.Serialize(output, JsonOptions);
        using var document = JsonDocument.Parse(json);
        return new LlmToolInvocationResult
        {
            Success = true,
            ToolId = ToolId,
            OutputText = json,
            OutputJson = document.RootElement.Clone()
        };
    }

    private static LlmToolInvocationResult Failed(string error) => new()
    {
        Success = false,
        ToolId = ToolId,
        Error = error ?? ""
    };

    private static JsonElement? GetObject(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;

    private static double? GetDouble(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result) ? result : null;

    private static bool? GetBool(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static class NativeMethods
    {
        public const int SW_RESTORE = 9;
        public const int SW_MINIMIZE = 6;
        public const int SW_MAXIMIZE = 3;
        public const int WM_CLOSE = 0x0010;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_RBUTTONDOWN = 0x0204;
        public const uint WM_RBUTTONUP = 0x0205;
        public const uint WM_MBUTTONDOWN = 0x0207;
        public const uint WM_MBUTTONUP = 0x0208;

        public const int MK_LBUTTON = 0x0001;
        public const int MK_RBUTTON = 0x0002;
        public const int MK_MBUTTON = 0x0010;

        public const ushort VK_BACK = 0x08;
        public const ushort VK_TAB = 0x09;
        public const ushort VK_RETURN = 0x0D;
        public const ushort VK_SHIFT = 0x10;
        public const ushort VK_CONTROL = 0x11;
        public const ushort VK_MENU = 0x12;
        public const ushort VK_ESCAPE = 0x1B;
        public const ushort VK_SPACE = 0x20;
        public const ushort VK_PRIOR = 0x21;
        public const ushort VK_NEXT = 0x22;
        public const ushort VK_END = 0x23;
        public const ushort VK_HOME = 0x24;
        public const ushort VK_LEFT = 0x25;
        public const ushort VK_UP = 0x26;
        public const ushort VK_RIGHT = 0x27;
        public const ushort VK_DOWN = 0x28;
        public const ushort VK_DELETE = 0x2E;
        public const ushort VK_LWIN = 0x5B;
        public const ushort VK_F1 = 0x70;

        public const uint INPUT_MOUSE = 0;
        public const uint INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out NativePoint point);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint numberOfInputs, NativeInput[] inputs, int size);
    }
}

public sealed record DesktopWindowInfo(
    string Handle,
    string Title,
    string ProcessName,
    int ProcessId,
    string ClassName,
    bool Visible,
    bool Foreground,
    bool Minimized,
    bool Maximized,
    DesktopBounds Bounds);

public sealed record DesktopBounds(int X, int Y, int Width, int Height);

public readonly record struct DesktopPoint(int X, int Y);

public readonly record struct HumanCursorOptions(bool Enabled, int DurationMs, int Steps, double JitterPixels, bool RestoreCursor, int? Seed)
{
    public static HumanCursorOptions Default { get; } = new(true, 450, 36, 1.5d, false, null);
}

public static class HumanCursorDriver
{
    public static IReadOnlyList<DesktopPoint> GeneratePath(DesktopPoint from, DesktopPoint to, HumanCursorOptions options)
    {
        int steps = Math.Clamp(options.Steps <= 0 ? HumanCursorOptions.Default.Steps : options.Steps, 2, 240);
        double jitter = Math.Clamp(options.JitterPixels, 0d, 12d);
        var random = options.Seed.HasValue ? new Random(options.Seed.Value) : new Random();
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        double normalX = distance <= 0.001d ? 0d : -dy / distance;
        double normalY = distance <= 0.001d ? 0d : dx / distance;
        double arc = Math.Min(120d, Math.Max(8d, distance * 0.18d));

        var c1 = new FloatingPoint(
            from.X + dx * 0.33d + normalX * arc,
            from.Y + dy * 0.33d + normalY * arc);
        var c2 = new FloatingPoint(
            from.X + dx * 0.66d - normalX * arc * 0.55d,
            from.Y + dy * 0.66d - normalY * arc * 0.55d);

        var path = new List<DesktopPoint>(steps + 1);
        for (int i = 0; i <= steps; i++)
        {
            double t = i / (double)steps;
            double eased = EaseInOutCubic(t);
            var point = CubicBezier(from, c1, c2, to, eased);
            if (i != 0 && i != steps && jitter > 0)
            {
                double taper = Math.Sin(Math.PI * t);
                point = point with
                {
                    X = point.X + (random.NextDouble() - 0.5d) * jitter * taper,
                    Y = point.Y + (random.NextDouble() - 0.5d) * jitter * taper
                };
            }

            var rounded = new DesktopPoint((int)Math.Round(point.X), (int)Math.Round(point.Y));
            if (path.Count == 0 || path[^1] != rounded)
                path.Add(rounded);
        }

        if (path[^1] != to)
            path.Add(to);
        return path;
    }

    private static double EaseInOutCubic(double t) => t < 0.5d
        ? 4d * t * t * t
        : 1d - Math.Pow(-2d * t + 2d, 3d) / 2d;

    private static FloatingPoint CubicBezier(DesktopPoint p0, FloatingPoint p1, FloatingPoint p2, DesktopPoint p3, double t)
    {
        double u = 1d - t;
        double tt = t * t;
        double uu = u * u;
        double uuu = uu * u;
        double ttt = tt * t;
        return new FloatingPoint(
            uuu * p0.X + 3d * uu * t * p1.X + 3d * u * tt * p2.X + ttt * p3.X,
            uuu * p0.Y + 3d * uu * t * p1.Y + 3d * u * tt * p2.Y + ttt * p3.Y);
    }

    private readonly record struct FloatingPoint(double X, double Y);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePoint
{
    public int X;
    public int Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeInput
{
    public uint Type;
    public NativeInputUnion Union;

    public static NativeInput KeyboardUnicode(char ch, bool keyUp) => new()
    {
        Type = WindowsDesktopAutomationToolNativeConstants.InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = 0,
                Scan = ch,
                Flags = WindowsDesktopAutomationToolNativeConstants.KeyEventUnicode | (keyUp ? WindowsDesktopAutomationToolNativeConstants.KeyEventKeyUp : 0)
            }
        }
    };

    public static NativeInput KeyboardVirtualKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = WindowsDesktopAutomationToolNativeConstants.InputKeyboard,
        Union = new NativeInputUnion
        {
            Keyboard = new NativeKeyboardInput
            {
                VirtualKey = virtualKey,
                Scan = 0,
                Flags = keyUp ? WindowsDesktopAutomationToolNativeConstants.KeyEventKeyUp : 0
            }
        }
    };

    public static NativeInput Mouse(uint flags) => new()
    {
        Type = WindowsDesktopAutomationToolNativeConstants.InputMouse,
        Union = new NativeInputUnion
        {
            Mouse = new NativeMouseInput
            {
                Flags = flags
            }
        }
    };
}

[StructLayout(LayoutKind.Explicit)]
internal struct NativeInputUnion
{
    [FieldOffset(0)]
    public NativeMouseInput Mouse;

    [FieldOffset(0)]
    public NativeKeyboardInput Keyboard;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeMouseInput
{
    public int X;
    public int Y;
    public uint MouseData;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeKeyboardInput
{
    public ushort VirtualKey;
    public ushort Scan;
    public uint Flags;
    public uint Time;
    public UIntPtr ExtraInfo;
}

internal static class WindowsDesktopAutomationToolNativeConstants
{
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint KeyEventKeyUp = 0x0002;
    public const uint KeyEventUnicode = 0x0004;
}
