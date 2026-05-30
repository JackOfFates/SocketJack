using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace JackLLMCompanion;

public sealed class CompanionLlmRunner : IDisposable
{
    private readonly CompanionRepository _repository;
    private readonly DesktopAutomationService _desktop;
    private readonly CompanionProcessService _processes;
    private readonly HttpClient _httpClient = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private CompanionLlmRunnerStatus _status = new();

    public CompanionLlmRunner(CompanionRepository repository, DesktopAutomationService desktop, CompanionProcessService processes)
    {
        _repository = repository;
        _desktop = desktop;
        _processes = processes;
        _status.ModelEndpoint = ModelEndpoint;
        _status.Model = Model;
    }

    public string ModelEndpoint { get; private set; } = "http://localhost:11435/v1/chat/completions";
    public string Model { get; private set; } = "local-model";
    public int MaxSteps { get; private set; } = 8;

    public CompanionLlmRunnerStatus GetStatus()
    {
        lock (_sync)
        {
            return _status.Clone();
        }
    }

    public void Configure(string modelEndpoint, string model, int maxSteps)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(modelEndpoint))
                ModelEndpoint = NormalizeChatCompletionsEndpoint(modelEndpoint);
            if (!string.IsNullOrWhiteSpace(model))
                Model = model.Trim();
            if (maxSteps > 0)
                MaxSteps = Math.Max(1, Math.Min(25, maxSteps));

            _status.ModelEndpoint = ModelEndpoint;
            _status.Model = Model;
            _status.MaxSteps = MaxSteps;
        }
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_loopTask != null && !_loopTask.IsCompleted)
                return;

            _cts = new CancellationTokenSource();
            _status.IsRunning = true;
            _status.Message = "Runner started. Waiting for queued Companion LLM tasks.";
            _status.ModelEndpoint = ModelEndpoint;
            _status.Model = Model;
            _status.MaxSteps = MaxSteps;
            _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
        }
    }

    public void Stop(string reason)
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            cts = _cts;
            _cts = null;
            _status.IsRunning = false;
            _status.Message = string.IsNullOrWhiteSpace(reason) ? "Runner stopped." : reason.Trim();
        }

        try { cts?.Cancel(); } catch { }
        _repository.StopLlmTask(string.IsNullOrWhiteSpace(reason) ? "Companion LLM runner stopped." : reason);
    }

    public void Dispose()
    {
        Stop("Companion LLM runner disposed.");
        _httpClient.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        string runnerId = "runner_" + Guid.NewGuid().ToString("N");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _repository.MarkApprovalRequiredLlmTasksQueuedIfApproved();
                CompanionLlmTask? task = _repository.ClaimNextQueuedLlmTask(runnerId, ModelEndpoint);
                if (task == null)
                {
                    SetStatus("", "Waiting for queued Companion LLM tasks.", false);
                    await Task.Delay(1200, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RunTaskAsync(task, runnerId, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            SetStatus("", "Runner failed: " + ex.Message, false, ex.Message);
        }
        finally
        {
            lock (_sync)
            {
                _status.IsRunning = false;
                if (string.IsNullOrWhiteSpace(_status.Message))
                    _status.Message = "Runner stopped.";
            }
        }
    }

    private async Task RunTaskAsync(CompanionLlmTask task, string runnerId, CancellationToken cancellationToken)
    {
        int maxSteps = task.MaxSteps > 0 ? task.MaxSteps : MaxSteps;
        var actionHistory = new List<CompanionRunnerActionMemory>();
        SetStatus(task.Id, "Running task: " + task.Goal, true);
        _repository.RecordLlmRunnerEvent(task.Id, "llm_runner_started", task.Goal);

        for (int step = 1; step <= maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_repository.IsLlmTaskCancellationRequested(task.Id))
                return;

            CompanionPermissions permissions = _repository.GetPermissions();
            if (!permissions.LiveInput && !IsRecordOnly(task.Mode))
            {
                _repository.UpdateLlmTaskProgress(
                    task.Id,
                    "approval_required",
                    "Live Input is disabled. Runner paused before desktop control.",
                    Progress(step, maxSteps),
                    step,
                    maxSteps,
                    "approval_required");
                SetStatus(task.Id, "Paused for Live Input approval.", false);
                return;
            }

            bool includeFiles = permissions.UseFiles;
            CompanionEnvironmentSnapshot snapshot = _desktop.CaptureEnvironmentSnapshot(includeFiles);
            DesktopScreenCapture? capture = null;
            if (permissions.LiveInput)
            {
                try { capture = _desktop.CaptureScreen(); }
                catch (Exception ex) { _repository.RecordLlmRunnerEvent(task.Id, "llm_observation_warning", "Screen capture failed: " + ex.Message, snapshot); }
            }

            _repository.UpdateLlmTaskProgress(
                task.Id,
                "running",
                "Step " + step.ToString() + ": observed desktop; asking model for next action.",
                Progress(step - 1, maxSteps),
                step,
                maxSteps,
                "observe");
            _repository.RecordLlmRunnerEvent(task.Id, "llm_observed_desktop", BuildObservationDetail(snapshot, capture), snapshot);

            CompanionRunnerAction action;
            try
            {
                action = await AskModelForActionAsync(task, snapshot, capture, permissions, step, maxSteps, actionHistory, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _repository.UpdateLlmTaskProgress(
                    task.Id,
                    "failed",
                    "Model request failed. Start JackLLM/LM Studio or configure the endpoint, then queue the task again.",
                    Progress(step, maxSteps),
                    step,
                    maxSteps,
                    "model_error",
                    ex.Message);
                _repository.RecordLlmRunnerEvent(task.Id, "llm_model_error", ex.Message, snapshot);
                SetStatus(task.Id, "Model request failed: " + ex.Message, false, ex.Message);
                return;
            }

            _repository.RecordLlmRunnerEvent(task.Id, "llm_action_proposed", action.Action + ": " + action.Reason, snapshot);

            CompanionRunnerActionResult result = ExecuteAction(task, action, permissions);
            actionHistory.Add(new CompanionRunnerActionMemory(step, action.Action, action.Reason, result.Status, result.Message));
            if (actionHistory.Count > 10)
                actionHistory.RemoveAt(0);
            _repository.UpdateLlmTaskProgress(
                task.Id,
                result.Status,
                result.Message,
                result.Progress >= 0 ? result.Progress : Progress(step, maxSteps),
                step,
                maxSteps,
                action.Action,
                result.Ok ? "" : result.Message);
            SetStatus(task.Id, result.Message, string.Equals(result.Status, "running", StringComparison.OrdinalIgnoreCase), result.Ok ? "" : result.Message);

            if (!string.Equals(result.Status, "running", StringComparison.OrdinalIgnoreCase))
                return;

            await Task.Delay(650, cancellationToken).ConfigureAwait(false);
        }

        _repository.UpdateLlmTaskProgress(
            task.Id,
            "awaiting_user",
            "Runner reached the configured step limit. Review the remote desktop and queue another task if more work is needed.",
            95,
            maxSteps,
            maxSteps,
            "step_limit");
        SetStatus(task.Id, "Step limit reached.", false);
    }

    private CompanionRunnerActionResult ExecuteAction(CompanionLlmTask task, CompanionRunnerAction action, CompanionPermissions permissions)
    {
        string normalized = (action.Action ?? "").Trim().ToLowerInvariant();
        if (normalized is "finish" or "done" or "complete")
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_task_completed", FirstNonEmpty(action.Reason, "Task marked complete by model."));
            return new CompanionRunnerActionResult(true, "completed", FirstNonEmpty(action.Reason, "Task completed."), 100);
        }

        if (normalized is "ask_user" or "ask" or "pause")
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_task_awaiting_user", FirstNonEmpty(action.Reason, "Model asked for user input."));
            return new CompanionRunnerActionResult(true, "awaiting_user", FirstNonEmpty(action.Reason, "Model asked for user input."), -1);
        }

        if (normalized is "observe" or "wait" or "none")
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_wait", FirstNonEmpty(action.Reason, "Model requested another observation."));
            return new CompanionRunnerActionResult(true, "running", FirstNonEmpty(action.Reason, "Waiting before next observation."), -1);
        }

        if (IsRecordOnly(task.Mode))
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_action_blocked", "Record-only mode blocked action: " + normalized);
            return new CompanionRunnerActionResult(false, "awaiting_user", "Record-only mode blocked proposed action: " + normalized, -1);
        }

        if (!permissions.LiveInput)
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_action_blocked", "Live Input disabled for action: " + normalized);
            return new CompanionRunnerActionResult(false, "approval_required", "Live Input approval is required for action: " + normalized, -1);
        }

        var input = new DesktopInputRequest
        {
            Action = normalized,
            Button = FirstNonEmpty(action.Button, "left"),
            X = action.X,
            Y = action.Y,
            EndX = action.EndX,
            EndY = action.EndY,
            HasPoint = action.HasPoint,
            HasEndPoint = action.HasEndPoint,
            Normalized = true,
            Delta = action.Delta,
            Text = action.Text ?? "",
            Key = action.Key ?? ""
        };

        DesktopInputResult inputResult = _desktop.ExecuteInput(input);
        _repository.RecordControlResult(normalized, "LLM runner: " + inputResult.Message, inputResult.Ok);
        _repository.RecordLlmRunnerEvent(task.Id, inputResult.Ok ? "llm_action_executed" : "llm_action_failed", normalized + ": " + inputResult.Message);
        return new CompanionRunnerActionResult(inputResult.Ok, inputResult.Ok ? "running" : "failed", inputResult.Message, -1);
    }

    private async Task<CompanionRunnerAction> AskModelForActionAsync(
        CompanionLlmTask task,
        CompanionEnvironmentSnapshot snapshot,
        DesktopScreenCapture? capture,
        CompanionPermissions permissions,
        int step,
        int maxSteps,
        IReadOnlyList<CompanionRunnerActionMemory> actionHistory,
        CancellationToken cancellationToken)
    {
        object userContent = BuildUserContent(task, snapshot, capture, permissions, step, maxSteps, actionHistory);
        var payload = new
        {
            model = Model,
            temperature = 0.1,
            max_tokens = 700,
            stream = false,
            tools = BuildToolDefinitions(),
            tool_choice = "auto",
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = userContent }
            }
        };

        string json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _httpClient.PostAsync(ModelEndpoint, content, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("Model endpoint returned HTTP " + (int)response.StatusCode + ": " + Trim(body, 260));

        if (TryExtractReadOnlyProcessToolCall(body, out CompanionRunnerToolCall processToolCall))
        {
            string toolResult = ExecuteReadOnlyProcessTool(processToolCall);
            _repository.RecordLlmRunnerEvent(task.Id, "llm_process_tool", processToolCall.Name + ": " + Trim(toolResult, 220), snapshot);
            body = await AskModelForActionAfterToolAsync(task, userContent, processToolCall, toolResult, cancellationToken).ConfigureAwait(false);
        }

        string modelText = ExtractOpenAiContent(body);
        if (TryParseModelAction(modelText, out CompanionRunnerAction action, out string parseNote))
        {
            if (!string.IsNullOrWhiteSpace(parseNote))
                _repository.RecordLlmRunnerEvent(task.Id, "llm_action_repaired", parseNote, snapshot);
            return action;
        }

        _repository.RecordLlmRunnerEvent(task.Id, "llm_action_parse_retry", "First model action was not valid JSON/tool arguments: " + Trim(modelText, 180), snapshot);
        string repairPrompt = BuildRepairPrompt(task, modelText);
        var repairPayload = new
        {
            model = Model,
            temperature = 0,
            max_tokens = 420,
            stream = false,
            tools = BuildToolDefinitions(),
            tool_choice = new
            {
                type = "function",
                function = new { name = "companion_desktop_action" }
            },
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = repairPrompt }
            }
        };

        using var repairContent = new StringContent(JsonSerializer.Serialize(repairPayload), Encoding.UTF8, "application/json");
        using HttpResponseMessage repairResponse = await _httpClient.PostAsync(ModelEndpoint, repairContent, cancellationToken).ConfigureAwait(false);
        string repairBody = await repairResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (repairResponse.IsSuccessStatusCode &&
            TryParseModelAction(ExtractOpenAiContent(repairBody), out action, out parseNote))
        {
            _repository.RecordLlmRunnerEvent(task.Id, "llm_action_repaired", FirstNonEmpty(parseNote, "Repaired invalid model action through schema retry."), snapshot);
            return action;
        }

        return new CompanionRunnerAction
        {
            Action = "ask_user",
            Reason = "The model response could not be repaired into a safe desktop action. Please clarify the next step."
        };
    }

    private async Task<string> AskModelForActionAfterToolAsync(
        CompanionLlmTask task,
        object userContent,
        CompanionRunnerToolCall toolCall,
        string toolResult,
        CancellationToken cancellationToken)
    {
        var followupPayload = new
        {
            model = Model,
            temperature = 0.1,
            max_tokens = 700,
            stream = false,
            tools = BuildToolDefinitions(),
            tool_choice = new
            {
                type = "function",
                function = new { name = "companion_desktop_action" }
            },
            messages = new object[]
            {
                new { role = "system", content = BuildSystemPrompt() },
                new { role = "user", content = userContent },
                new
                {
                    role = "assistant",
                    content = "",
                    tool_calls = new[]
                    {
                        new
                        {
                            id = toolCall.Id,
                            type = "function",
                            function = new { name = toolCall.Name, arguments = toolCall.ArgumentsJson }
                        }
                    }
                },
                new { role = "tool", tool_call_id = toolCall.Id, content = toolResult },
                new { role = "user", content = "Use the process/window tool result above. Now choose exactly one companion_desktop_action tool call or one JSON desktop action for the next small step." }
            }
        };

        using var followupContent = new StringContent(JsonSerializer.Serialize(followupPayload), Encoding.UTF8, "application/json");
        using HttpResponseMessage followupResponse = await _httpClient.PostAsync(ModelEndpoint, followupContent, cancellationToken).ConfigureAwait(false);
        string followupBody = await followupResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!followupResponse.IsSuccessStatusCode)
            throw new InvalidOperationException("Model endpoint returned HTTP " + (int)followupResponse.StatusCode + " after process tool: " + Trim(followupBody, 260));
        return followupBody;
    }

    private object BuildUserContent(
        CompanionLlmTask task,
        CompanionEnvironmentSnapshot snapshot,
        DesktopScreenCapture? capture,
        CompanionPermissions permissions,
        int step,
        int maxSteps,
        IReadOnlyList<CompanionRunnerActionMemory> actionHistory)
    {
        string text = BuildRunnerPrompt(task, snapshot, permissions, step, maxSteps, actionHistory);
        if (capture == null)
            return text;

        string dataUrl = "data:" + capture.MimeType + ";base64," + Convert.ToBase64String(capture.Bytes);
        return new object[]
        {
            new { type = "text", text },
            new { type = "image_url", image_url = new { url = dataUrl } }
        };
    }

    private string BuildRunnerPrompt(CompanionLlmTask task, CompanionEnvironmentSnapshot snapshot, CompanionPermissions permissions, int step, int maxSteps, IReadOnlyList<CompanionRunnerActionMemory> actionHistory)
    {
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        CompanionTemplate template = state.Templates.FirstOrDefault(item => string.Equals(item.Id, "JACK", StringComparison.OrdinalIgnoreCase))
            ?? state.Templates.FirstOrDefault()
            ?? new CompanionTemplate { CompanionName = "JACK" };

        var sb = new StringBuilder();
        sb.AppendLine("Companion name: " + template.CompanionName);
        sb.AppendLine("Task goal: " + task.Goal);
        sb.AppendLine("Mode: " + task.Mode);
        sb.AppendLine("Step: " + step.ToString() + " of " + maxSteps.ToString());
        sb.AppendLine("Permissions: liveInput=" + permissions.LiveInput + ", useFiles=" + permissions.UseFiles + ", internet=" + permissions.InternetAccess + ", humans=" + permissions.HumanInteraction + ", login=" + permissions.AccountLogin + ", spendMoney=" + permissions.SpendMoney + ", pcSettings=" + permissions.PcSettings);
        sb.AppendLine("Foreground app: " + snapshot.Application);
        sb.AppendLine("Foreground window: " + snapshot.Window);
        if (!string.IsNullOrWhiteSpace(snapshot.Url))
            sb.AppendLine("Browser cue: " + snapshot.Url);
        if (!string.IsNullOrWhiteSpace(snapshot.Person))
            sb.AppendLine("Person/chat cue: " + snapshot.Person);
        if (snapshot.Files.Count > 0)
            sb.AppendLine("Recent files: " + string.Join("; ", snapshot.Files.Take(6)));
        try
        {
            CompanionWindowSnapshot windows = _processes.GetWindowSnapshot(new CompanionProcessQuery { Take = 6, Sort = "process" });
            if (windows.Windows.Count > 0)
            {
                sb.AppendLine("Visible windows: " + string.Join(" | ", windows.Windows.Select(window =>
                    window.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                    FirstNonEmpty(window.ProcessName, "unknown") + ": " + Trim(window.Title, 80))));
            }
        }
        catch
        {
        }
        List<CompanionSkill> enabledSkills = _repository.RankEnabledSkills(task.Goal, snapshot, 4);
        if (enabledSkills.Count > 0)
            sb.AppendLine("Enabled learned skills: " + string.Join("; ", enabledSkills.Select(skill => skill.Name + " => " + skill.Trigger + " | " + Trim(skill.Steps, 140))));
        if (state.RecentEvents.Count > 0)
            sb.AppendLine("Recent memory: " + string.Join(" | ", state.RecentEvents.Take(8).Select(item => item.EventType + ": " + Trim(item.Detail, 90))));
        if (actionHistory.Count > 0)
            sb.AppendLine("This task's recent actions: " + string.Join(" | ", actionHistory.Select(item => "#" + item.Step + " " + item.Action + " -> " + item.Status + " (" + Trim(item.Message, 80) + ")")));
        sb.AppendLine();
        sb.AppendLine("You may call list_running_processes or list_open_windows once if process/window state would materially improve the next action. Otherwise return exactly one JSON object or call companion_desktop_action. Do not include markdown.");
        return sb.ToString();
    }

    private static string BuildSystemPrompt()
    {
        return """
You are JACK, the SocketJack Companion desktop-control planner.
You must choose exactly one small next action.
Never claim that an action was performed unless you request a concrete action.
Respect approval gates. Do not request money, account login, human interaction, internet, file, PC settings, or live input actions unless the prompt says the gate is enabled.
Prefer safe observation or ask_user when the screen is ambiguous.
If process/window state is needed, call list_running_processes or list_open_windows once, then choose a desktop action from the result.

Return only JSON:
{
  "action": "observe|wait|click|doubleclick|rightclick|move|wheel|type|key|ask_user|finish",
  "reason": "short reason",
  "x": 0.0,
  "y": 0.0,
  "endX": 0.0,
  "endY": 0.0,
  "button": "left",
  "text": "",
  "key": "",
  "delta": 0,
  "confidence": 0.0,
  "expectedObservation": ""
}

For mouse coordinates, use normalized screen coordinates from 0.0 to 1.0.
Use finish only when the user's goal is actually complete.
""";
    }

    private static object[] BuildToolDefinitions()
    {
        return
        [
            new
            {
                type = "function",
                function = new
                {
                    name = "companion_desktop_action",
                    description = "Choose exactly one safe next desktop-control action for SocketJack Companion.",
                    parameters = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            action = new
                            {
                                type = "string",
                                @enum = new[] { "observe", "wait", "click", "doubleclick", "rightclick", "move", "drag", "wheel", "type", "key", "ask_user", "finish" }
                            },
                            reason = new { type = "string" },
                            x = new { type = "number", minimum = 0, maximum = 1 },
                            y = new { type = "number", minimum = 0, maximum = 1 },
                            endX = new { type = "number", minimum = 0, maximum = 1 },
                            endY = new { type = "number", minimum = 0, maximum = 1 },
                            button = new { type = "string", @enum = new[] { "left", "right" } },
                            text = new { type = "string" },
                            key = new { type = "string" },
                            delta = new { type = "integer" },
                            confidence = new { type = "number", minimum = 0, maximum = 1 },
                            expectedObservation = new { type = "string" }
                        },
                        required = new[] { "action", "reason" }
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "list_running_processes",
                    description = "Read-only, bounded inventory of running Windows processes with PID, windows, file path, CPU/GPU/RAM, and admin state.",
                    parameters = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            query = new { type = "string" },
                            windowedOnly = new { type = "boolean" },
                            take = new { type = "integer", minimum = 1, maximum = 50 },
                            sort = new { type = "string", @enum = new[] { "name", "pid", "cpu", "gpu", "ram", "window", "path" } }
                        },
                        required = Array.Empty<string>()
                    }
                }
            },
            new
            {
                type = "function",
                function = new
                {
                    name = "list_open_windows",
                    description = "Read-only, bounded list of visible top-level Windows windows joined to process IDs.",
                    parameters = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            query = new { type = "string" },
                            take = new { type = "integer", minimum = 1, maximum = 50 },
                            sort = new { type = "string", @enum = new[] { "title", "pid", "process", "class" } }
                        },
                        required = Array.Empty<string>()
                    }
                }
            }
        ];
    }

    private static string BuildRepairPrompt(CompanionLlmTask task, string modelText)
    {
        return "Repair the previous response into one valid companion_desktop_action tool call or one JSON object only. " +
               "Goal: " + task.Goal + Environment.NewLine +
               "Previous response:" + Environment.NewLine +
               Trim(modelText, 2400);
    }

    private static bool TryExtractReadOnlyProcessToolCall(string body, out CompanionRunnerToolCall toolCall)
    {
        toolCall = new CompanionRunnerToolCall("", "", "{}");
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement choice in choices.EnumerateArray())
                {
                    if (!choice.TryGetProperty("message", out JsonElement message))
                        continue;

                    if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement item in toolCalls.EnumerateArray())
                        {
                            string id = ReadString(item, "id", "call_process_info");
                            JsonElement function = ReadObject(item, "function", default);
                            string name = ReadString(function, "name", "");
                            string arguments = ReadString(function, "arguments", "{}");
                            if (IsReadOnlyProcessToolName(name))
                            {
                                toolCall = new CompanionRunnerToolCall(string.IsNullOrWhiteSpace(id) ? "call_process_info" : id, name, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
                                return true;
                            }
                        }
                    }

                    if (message.TryGetProperty("function_call", out JsonElement functionCall))
                    {
                        string name = ReadString(functionCall, "name", "");
                        string arguments = ReadString(functionCall, "arguments", "{}");
                        if (IsReadOnlyProcessToolName(name))
                        {
                            toolCall = new CompanionRunnerToolCall("call_process_info", name, string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments);
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private string ExecuteReadOnlyProcessTool(CompanionRunnerToolCall toolCall)
    {
        try
        {
            CompanionProcessQuery query = BuildProcessToolQuery(toolCall.ArgumentsJson, toolCall.Name.Equals("list_open_windows", StringComparison.Ordinal) ? "process" : "cpu");
            return toolCall.Name.Equals("list_open_windows", StringComparison.Ordinal)
                ? _processes.BuildCompactWindowSummary(query)
                : _processes.BuildCompactProcessSummary(query);
        }
        catch (Exception ex)
        {
            return toolCall.Name + " failed: " + ex.Message;
        }
    }

    private static CompanionProcessQuery BuildProcessToolQuery(string argumentsJson, string defaultSort)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = document.RootElement;
            return new CompanionProcessQuery
            {
                Query = ReadString(root, "query", ReadString(root, "q", "")),
                WindowedOnly = ReadBool(root, "windowedOnly", false),
                IncludeSystem = true,
                Take = Math.Max(1, Math.Min(50, ReadInt(root, "take", ReadInt(root, "limit", 20)))),
                Sort = ReadString(root, "sort", defaultSort)
            };
        }
        catch
        {
            return new CompanionProcessQuery { Take = 20, Sort = defaultSort };
        }
    }

    private static bool IsReadOnlyProcessToolName(string name)
    {
        return name.Equals("list_running_processes", StringComparison.Ordinal) ||
               name.Equals("list_open_windows", StringComparison.Ordinal);
    }

    private static string ExtractOpenAiContent(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out JsonElement message) &&
                    TryReadToolOrContent(message, out string messageContent))
                    return messageContent;
                if (choice.TryGetProperty("delta", out JsonElement delta) &&
                    delta.TryGetProperty("content", out JsonElement deltaContent))
                    return deltaContent.ToString();
                if (choice.TryGetProperty("text", out JsonElement text))
                    return text.ToString();
            }
        }

        if (root.TryGetProperty("content", out JsonElement directContent))
            return directContent.ToString();

        return body;
    }

    private static bool TryReadToolOrContent(JsonElement message, out string content)
    {
        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement toolCall in toolCalls.EnumerateArray())
            {
                if (toolCall.TryGetProperty("function", out JsonElement function) &&
                    function.TryGetProperty("arguments", out JsonElement arguments))
                {
                    content = arguments.ToString();
                    return true;
                }
            }
        }

        if (message.TryGetProperty("function_call", out JsonElement functionCall) &&
            functionCall.TryGetProperty("arguments", out JsonElement legacyArguments))
        {
            content = legacyArguments.ToString();
            return true;
        }

        if (message.TryGetProperty("content", out JsonElement directContent))
        {
            content = directContent.ToString();
            return true;
        }

        content = "";
        return false;
    }

    private static bool TryParseModelAction(string modelText, out CompanionRunnerAction action, out string repairNote)
    {
        repairNote = "";
        action = new CompanionRunnerAction
        {
            Action = "ask_user",
            Reason = "Unable to parse model action."
        };

        string json = ExtractJsonObject(modelText, out bool extracted);
        if (!string.IsNullOrWhiteSpace(json))
        {
            if (TryParseActionJson(json, out action))
            {
                repairNote = extracted ? "Extracted JSON action from surrounding model text." : "";
                return true;
            }

            string repairedJson = RepairJson(json);
            if (!string.Equals(repairedJson, json, StringComparison.Ordinal) &&
                TryParseActionJson(repairedJson, out action))
            {
                repairNote = "Applied JSON cleanup to model action.";
                return true;
            }
        }

        if (TryInferActionFromText(modelText, out action))
        {
            repairNote = "Inferred a safe action from non-JSON model text.";
            return true;
        }

        return false;
    }

    private static bool TryParseActionJson(string json, out CompanionRunnerAction action)
    {
        action = new CompanionRunnerAction();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = NormalizeActionRoot(document.RootElement);
            JsonElement args = ReadObject(root, "arguments", ReadObject(root, "args", ReadObject(root, "parameters", default)));
            if (args.ValueKind == JsonValueKind.Undefined)
                args = root;

            action = new CompanionRunnerAction
            {
                Action = NormalizeAction(FirstNonEmpty(
                    ReadString(root, "action", ""),
                    ReadString(root, "tool", ""),
                    ReadString(root, "name", ""),
                    ReadString(root, "command", ""),
                    ReadString(args, "action", ""),
                    "ask_user")),
                Reason = FirstNonEmpty(ReadString(root, "reason", ""), ReadString(args, "reason", ""), ReadString(root, "rationale", "")),
                Button = FirstNonEmpty(ReadString(args, "button", ""), ReadString(root, "button", ""), "left"),
                Text = FirstNonEmpty(ReadString(args, "text", ""), ReadString(root, "text", ""), ReadString(args, "value", "")),
                Key = FirstNonEmpty(ReadString(args, "key", ""), ReadString(root, "key", "")),
                Delta = ReadInt(args, "delta", ReadInt(root, "delta", 0)),
                Confidence = ReadDouble(args, "confidence", ReadDouble(root, "confidence", 0)),
                ExpectedObservation = FirstNonEmpty(ReadString(args, "expectedObservation", ""), ReadString(root, "expectedObservation", ""))
            };

            JsonElement point = ReadObject(args, "point", ReadObject(args, "coordinates", default));
            JsonElement endPoint = ReadObject(args, "endPoint", ReadObject(args, "end", default));

            if (TryReadDouble(args, "x", out double x) || TryReadDouble(root, "x", out x) || TryReadDouble(point, "x", out x))
            {
                action.X = Math.Max(0, Math.Min(1, x));
                action.HasPoint = true;
            }
            if (TryReadDouble(args, "y", out double y) || TryReadDouble(root, "y", out y) || TryReadDouble(point, "y", out y))
            {
                action.Y = Math.Max(0, Math.Min(1, y));
                action.HasPoint = true;
            }
            if (TryReadDouble(args, "endX", out double endX) || TryReadDouble(root, "endX", out endX) || TryReadDouble(args, "x2", out endX) || TryReadDouble(root, "x2", out endX) || TryReadDouble(endPoint, "x", out endX))
            {
                action.EndX = Math.Max(0, Math.Min(1, endX));
                action.HasEndPoint = true;
            }
            if (TryReadDouble(args, "endY", out double endY) || TryReadDouble(root, "endY", out endY) || TryReadDouble(args, "y2", out endY) || TryReadDouble(root, "y2", out endY) || TryReadDouble(endPoint, "y", out endY))
            {
                action.EndY = Math.Max(0, Math.Min(1, endY));
                action.HasEndPoint = true;
            }

            if (action.Action is "click" or "doubleclick" or "rightclick" or "move" && !action.HasPoint)
            {
                action.Action = "ask_user";
                action.Reason = FirstNonEmpty(action.Reason, "Mouse action was missing coordinates.");
            }
            if (action.Action == "drag" && !(action.HasPoint && action.HasEndPoint))
            {
                action.Action = "ask_user";
                action.Reason = FirstNonEmpty(action.Reason, "Drag action was missing start or end coordinates.");
            }
            if (action.Action == "type" && string.IsNullOrWhiteSpace(action.Text))
            {
                action.Action = "ask_user";
                action.Reason = FirstNonEmpty(action.Reason, "Type action was missing text.");
            }
            if (action.Action == "key" && string.IsNullOrWhiteSpace(action.Key))
            {
                action.Action = "ask_user";
                action.Reason = FirstNonEmpty(action.Reason, "Key action was missing a key name.");
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryInferActionFromText(string modelText, out CompanionRunnerAction action)
    {
        string text = (modelText ?? "").Trim();
        string lower = text.ToLowerInvariant();
        action = new CompanionRunnerAction { Action = "ask_user", Reason = Trim(text, 160) };
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (lower.Contains("finish", StringComparison.Ordinal) || lower.Contains("done", StringComparison.Ordinal) || lower.Contains("complete", StringComparison.Ordinal))
        {
            action.Action = "finish";
            return true;
        }

        if (lower.Contains("observe", StringComparison.Ordinal) || lower.Contains("wait", StringComparison.Ordinal))
        {
            action.Action = "observe";
            return true;
        }

        if (lower.Contains("ask", StringComparison.Ordinal) || lower.Contains("clarify", StringComparison.Ordinal) || lower.Contains("permission", StringComparison.Ordinal))
        {
            action.Action = "ask_user";
            return true;
        }

        return false;
    }

    private static JsonElement NormalizeActionRoot(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in root.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    return item;
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return root;

        if (TryGetProperty(root, "actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in actions.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object)
                    return item;
        }

        if (TryGetProperty(root, "tool_calls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement toolCall in toolCalls.EnumerateArray())
            {
                JsonElement function = ReadObject(toolCall, "function", default);
                JsonElement arguments = ReadObject(function, "arguments", default);
                if (arguments.ValueKind == JsonValueKind.Object)
                    return arguments;
            }
        }

        if (TryGetProperty(root, "function", out JsonElement functionRoot))
        {
            JsonElement arguments = ReadObject(functionRoot, "arguments", default);
            if (arguments.ValueKind == JsonValueKind.Object)
                return arguments;
        }

        return root;
    }

    private static JsonElement ReadObject(JsonElement root, string name, JsonElement fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Object)
            return value;
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(value.GetString() ?? "{}");
                return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : fallback;
            }
            catch
            {
            }
        }

        return fallback;
    }

    private static string ExtractJsonObject(string text, out bool extracted)
    {
        extracted = false;
        text = (text ?? "").Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine)
            {
                text = text[(firstLine + 1)..lastFence].Trim();
                extracted = true;
            }
        }

        if (text.StartsWith("{", StringComparison.Ordinal) && text.EndsWith("}", StringComparison.Ordinal))
            return text;

        int depth = 0;
        int start = -1;
        bool inString = false;
        bool escaped = false;
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString)
                continue;
            if (ch == '{')
            {
                if (depth == 0)
                    start = i;
                depth++;
            }
            else if (ch == '}' && depth > 0)
            {
                depth--;
                if (depth == 0 && start >= 0)
                {
                    extracted = true;
                    return text[start..(i + 1)];
                }
            }
        }

        return "";
    }

    private void SetStatus(string activeTaskId, string message, bool busy, string lastError = "")
    {
        lock (_sync)
        {
            _status.IsRunning = _cts != null && !_cts.IsCancellationRequested;
            _status.IsBusy = busy;
            _status.ActiveTaskId = activeTaskId ?? "";
            _status.Message = message ?? "";
            _status.LastError = lastError ?? "";
            _status.ModelEndpoint = ModelEndpoint;
            _status.Model = Model;
            _status.MaxSteps = MaxSteps;
            _status.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
        }
    }

    private static string NormalizeChatCompletionsEndpoint(string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0)
            return "http://localhost:11435/v1/chat/completions";
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return value.TrimEnd('/') + "/chat/completions";
        if (!value.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
            return value.TrimEnd('/') + "/v1/chat/completions";
        return value;
    }

    private static string BuildObservationDetail(CompanionEnvironmentSnapshot snapshot, DesktopScreenCapture? capture)
    {
        string size = capture == null ? "no screen frame" : capture.Width.ToString() + "x" + capture.Height.ToString();
        return "Observed " + FirstNonEmpty(snapshot.Application, "desktop") + " | " + FirstNonEmpty(snapshot.Window, "no title") + " | " + size;
    }

    private static int Progress(int step, int maxSteps)
    {
        return Math.Max(0, Math.Min(95, (int)Math.Round(step / Math.Max(1.0, maxSteps) * 90.0)));
    }

    private static bool IsRecordOnly(string mode)
    {
        return (mode ?? "").IndexOf("record", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (mode ?? "").IndexOf("observe", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private static string Trim(string value, int length)
    {
        value ??= "";
        return value.Length <= length ? value : value[..length] + "...";
    }

    private static string NormalizeAction(string value)
    {
        string action = (value ?? "").Trim().Replace("-", "_", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        return action switch
        {
            "done" or "complete" or "completed" => "finish",
            "left_click" or "mouse_click" or "tap" => "click",
            "double_click" => "doubleclick",
            "right_click" or "context_click" => "rightclick",
            "mouse_move" => "move",
            "scroll" or "mouse_wheel" => "wheel",
            "press" or "hotkey" or "press_key" or "keypress" => "key",
            "keyboard" or "input_text" or "write" or "text" => "type",
            "pause" or "none" => "wait",
            "ask" or "clarify" => "ask_user",
            "observe" or "wait" or "click" or "doubleclick" or "rightclick" or "move" or "drag" or "wheel" or "type" or "key" or "ask_user" or "finish" => action,
            _ => "ask_user"
        };
    }

    private static string RepairJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        string repaired = json.Trim()
            .Replace("\u201c", "\"", StringComparison.Ordinal)
            .Replace("\u201d", "\"", StringComparison.Ordinal)
            .Replace("\u2018", "'", StringComparison.Ordinal)
            .Replace("\u2019", "'", StringComparison.Ordinal);

        if (repaired.Contains('\'', StringComparison.Ordinal) && !repaired.Contains('"', StringComparison.Ordinal))
            repaired = repaired.Replace('\'', '"');

        return repaired;
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, name, out JsonElement value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
        return fallback;
    }

    private static bool TryReadDouble(JsonElement root, string name, out double result)
    {
        result = 0;
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, name, out JsonElement value))
            return false;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out result))
            return true;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static double ReadDouble(JsonElement root, string name, double fallback)
    {
        return TryReadDouble(root, name, out double result) ? result : fallback;
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : fallback;
    }

    private static bool ReadBool(JsonElement root, string name, bool fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        return value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) ? parsed : fallback;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

public sealed class CompanionLlmRunnerStatus
{
    public bool IsRunning { get; set; }
    public bool IsBusy { get; set; }
    public string ActiveTaskId { get; set; } = "";
    public string Message { get; set; } = "";
    public string LastError { get; set; } = "";
    public string ModelEndpoint { get; set; } = "";
    public string Model { get; set; } = "";
    public int MaxSteps { get; set; } = 8;
    public string UpdatedUtc { get; set; } = "";

    public CompanionLlmRunnerStatus Clone()
    {
        return new CompanionLlmRunnerStatus
        {
            IsRunning = IsRunning,
            IsBusy = IsBusy,
            ActiveTaskId = ActiveTaskId,
            Message = Message,
            LastError = LastError,
            ModelEndpoint = ModelEndpoint,
            Model = Model,
            MaxSteps = MaxSteps,
            UpdatedUtc = UpdatedUtc
        };
    }
}

internal sealed class CompanionRunnerAction
{
    public string Action { get; set; } = "ask_user";
    public string Reason { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double EndX { get; set; }
    public double EndY { get; set; }
    public bool HasPoint { get; set; }
    public bool HasEndPoint { get; set; }
    public string Button { get; set; } = "left";
    public string Text { get; set; } = "";
    public string Key { get; set; } = "";
    public int Delta { get; set; }
    public double Confidence { get; set; }
    public string ExpectedObservation { get; set; } = "";
}

internal sealed record CompanionRunnerActionResult(bool Ok, string Status, string Message, int Progress);

internal sealed record CompanionRunnerActionMemory(int Step, string Action, string Reason, string Status, string Message);

internal sealed record CompanionRunnerToolCall(string Id, string Name, string ArgumentsJson);
