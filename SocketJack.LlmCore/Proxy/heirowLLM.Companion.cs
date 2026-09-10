using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using heirowLLM;
using SocketJack.Net;

namespace SocketJack.Net
{
    public partial class HeirowLlm
    {
        public Func<int, int, int, byte[]> CompanionCaptureJpeg { get; set; }
        public Func<int, int, int, byte[]> CompanionCaptureForegroundWindowJpeg { get; set; }
        public Action<string> CompanionInput { get; set; }
        public Func<string, string> CompanionLaunchApplication { get; set; }
        public Func<string> CompanionDesktopStatus { get; set; }
        public TimeSpan CompanionControlLeadTime { get; set; } = TimeSpan.FromSeconds(3);
        public TimeSpan CompanionControlLingerTime { get; set; } = TimeSpan.FromSeconds(5);
        public event EventHandler CompanionEmergencyStopRequested;
        public event Action<bool> CompanionControlStateChanged;

        private readonly object _companionGate = new object();
        private readonly Dictionary<string, CompanionConfirmation> _companionConfirmations =
            new Dictionary<string, CompanionConfirmation>(StringComparer.Ordinal);
        private string _companionTaskGoal = "";
        private string _companionTaskOwner = "";
        private string _companionTaskStatus = "idle";
        private string _companionLastAction = "";
        private bool _companionEmergencyStopped;
        private System.Threading.Timer _companionControlLingerTimer;
        private int _companionControlGeneration;
        private bool _companionControlOverlayActive;

        private const string CompanionFinancialConfirmationPhrase =
            "I UNDERSTAND THIS CAN CAUSE FINANCIAL LOSS";
        private const string CompanionSensitiveConfirmationPhrase =
            "I AUTHORIZE SAVING THIS SENSITIVE INFORMATION";

        private void RegisterCompanionRoutes(HttpServer server)
        {
            server.Map("GET", "/api/companion/status", (c, r, _) => HandleCompanionStatus(c, r));
            server.Map("POST", "/api/companion/task", (c, r, _) => HandleCompanionTask(c, r));
            server.Map("POST", "/api/companion/action", (c, r, _) => HandleCompanionAction(c, r));
            server.Map("POST", "/api/companion/confirm", (c, r, _) => HandleCompanionConfirm(c, r));
            server.Map("POST", "/api/companion/emergency-stop", (c, r, _) => HandleCompanionEmergencyStop(c, r));
        }

        private string HandleCompanionStatus(NetworkConnection connection, HttpRequest request)
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            ChatPermissionState permissions = GetChatPermissions(ownerKey);
            lock (_companionGate)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    integrated = true,
                    ownerKey,
                    status = _companionTaskStatus,
                    goal = permissions.companionActivityTranscriptStorage ? _companionTaskGoal : "",
                    lastAction = permissions.companionActivityTranscriptStorage ? _companionLastAction : "",
                    desktopController = ReadCompanionDesktopStatus(),
                    permissions = BuildChatClientPermissionSnapshot(ownerKey, permissions),
                    financialConfirmationPhrase = permissions.companionFinancialActions
                        ? CompanionFinancialConfirmationPhrase
                        : ""
                });
            }
        }

        private string HandleCompanionTask(NetworkConnection connection, HttpRequest request)
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            ChatPermissionState permissions = GetChatPermissions(ownerKey);
            if (!permissions.companionEnabled)
                return BuildJsonError(request, 403, "Forbidden", "Companion is disabled for this owner.");

            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                string action = CompanionJsonString(document.RootElement, "action").ToLowerInvariant();
                string goal = CompanionJsonString(document.RootElement, "goal").Trim();
                lock (_companionGate)
                {
                    if (action is "stop" or "cancel")
                    {
                        _companionTaskStatus = "stopped";
                        _companionTaskGoal = "";
                        _companionTaskOwner = "";
                        _companionConfirmations.Clear();
                    }
                    else
                    {
                        if (string.IsNullOrWhiteSpace(goal))
                            return BuildJsonError(request, 400, "Bad Request", "A Companion goal is required.");
                        _companionTaskGoal = goal;
                        _companionTaskOwner = ownerKey;
                        _companionTaskStatus = "ready";
                        _companionLastAction = "";
                        _companionEmergencyStopped = false;
                    }
                }
                StopCompanionDesktopControlImmediately();
                return HandleCompanionStatus(connection, request);
            }
            catch (JsonException ex)
            {
                return BuildJsonError(request, 400, "Bad Request", "Invalid Companion task JSON: " + ex.Message);
            }
        }

        private string HandleCompanionAction(NetworkConnection connection, HttpRequest request)
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            return ExecuteCompanionAction(ownerKey, request, false);
        }

        private string ExecuteCompanionToolAction(string ownerKey, string argumentsJson, bool observationApprovedOnce = false)
        {
            return ExecuteCompanionAction(
                NormalizeChatFilesystemOwnerKey(ownerKey),
                new HttpRequest { Body = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson },
                observationApprovedOnce);
        }

        private string ExecuteCompanionAction(string ownerKey, HttpRequest request, bool observationApprovedOnce)
        {
            ChatPermissionState permissions = GetChatPermissions(ownerKey);
            lock (_companionGate)
            {
                if (_companionEmergencyStopped)
                    return BuildJsonError(request, 423, "Companion Stopped", "Companion control was cancelled locally. Start a new Companion task to resume.");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                JsonElement root = document.RootElement;
                string type = NormalizeCompanionActionType(CompanionJsonString(root, "type"));
                // Natural-language models commonly name an application target "app" or
                // "application". Normalize those aliases here so native tool calls and
                // recovered/loose tool calls share the same permission-checked path.
                string value = FirstNonEmpty(
                    CompanionJsonString(root, "value"),
                    CompanionJsonString(root, "app"),
                    CompanionJsonString(root, "application"),
                    CompanionJsonString(root, "program"),
                    CompanionJsonString(root, "target"));
                string confirmationToken = CompanionJsonString(root, "confirmationToken");
                string actionMaterial = type + "\n" + (request?.Body ?? value);
                if (!permissions.companionEnabled)
                    return BuildJsonError(request, 403, "Forbidden", "Companion is disabled in the Workstation Companion tab.");
                if (type == "text")
                {
                    string requestedText = FirstNonEmpty(CompanionJsonString(root, "text"), value);
                    if (CompanionTextContainsInternalReasoning(requestedText))
                        return AppendCompanionDesktopStatus(BuildJsonError(request, 422, "Companion Text Rejected", "The text payload contained model reasoning or tool narration instead of only user-facing content. Nothing was typed. Retry type=text with only the final requested document or text."));
                }

                if (IsCompanionFinancialAction(actionMaterial))
                {
                    if (!permissions.companionFinancialActions)
                        return BuildJsonError(request, 403, "Financial Action Blocked", "Financial actions are disabled. Companion cannot spend money.");
                    if (!ConsumeCompanionConfirmation(ownerKey, actionMaterial, "financial", confirmationToken))
                        return BuildJsonError(request, 428, "Confirmation Required",
                            "This action may cause charges, recurring billing, account loss, irreversible transfers, or severe financial damage. Confirm the exact action locally in heirowLLM Workstation.");
                }

                bool controlsDesktop = type is "move" or "click" or "draw" or "key" or "shortcut" or "text" or "scroll" or "clipboard" or "launch" or "terminal";
                if (controlsDesktop)
                    BeginCompanionDesktopControl();

                string result;
                try
                {
                    switch (type)
                    {
                        case "observe":
                        case "screen":
                            if (!permissions.companionScreenView && !observationApprovedOnce)
                                return BuildJsonError(request, 403, "Forbidden", "Companion Screen View permission is disabled.");
                            if (CompanionCaptureJpeg == null)
                                return BuildJsonError(request, 501, "Not Supported", "Screen capture is unavailable on this host.");
                            // Keep desktop observations legible while bounding projector
                            // work on local 12 GB vision hosts. The captured JPEG remains a
                            // full-screen frame; only its encoded resolution is reduced.
                            bool foregroundWindow = type == "screen" && CompanionCaptureForegroundWindowJpeg != null;
                            byte[] jpeg = foregroundWindow
                                ? CompanionCaptureForegroundWindowJpeg(1280, 960, 75)
                                : CompanionCaptureJpeg(768, 432, 60);
                            result = JsonSerializer.Serialize(new
                            {
                                ok = true,
                                type = "screen",
                                requestedType = type,
                                scope = foregroundWindow ? "foreground-window" : "desktop",
                                contentType = "image/jpeg",
                                data = Convert.ToBase64String(jpeg)
                            });
                            break;

                    case "move":
                        if (!permissions.companionCursorControl)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Cursor Control permission is disabled.");
                        if (CompanionInput == null)
                            return BuildJsonError(request, 501, "Not Supported", "Desktop input is unavailable on this host.");
                        CompanionInput(request.Body);
                        result = JsonSerializer.Serialize(new { ok = true, type });
                        break;

                    case "click":
                        if (!permissions.companionCursorControl)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Cursor Control permission is disabled.");
                        goto case "key";

                    case "key":
                    case "shortcut":
                    case "text":
                    case "scroll":
                    case "clipboard":
                        if (!permissions.companionApplicationControl)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Application Control permission is disabled.");
                        if (CompanionInput == null)
                            return BuildJsonError(request, 501, "Not Supported", "Desktop input is unavailable on this host.");
                        CompanionInput(request.Body);
                        result = JsonSerializer.Serialize(new { ok = true, type });
                        break;

                    case "draw":
                        if (!permissions.companionCursorControl)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Cursor Control permission is disabled.");
                        if (!permissions.companionApplicationControl)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Application Control permission is disabled.");
                        if (CompanionInput == null)
                            return BuildJsonError(request, 501, "Not Supported", "Desktop input is unavailable on this host.");
                        CompanionInput(request.Body);
                        result = JsonSerializer.Serialize(new { ok = true, type, shape = CompanionJsonString(root, "shape") });
                        break;

                    case "launch":
                        if (!permissions.companionApplicationLaunch)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Application Launch permission is disabled.");
                        if (CompanionLaunchApplication == null)
                            return BuildJsonError(request, 501, "Not Supported", "Application launching is unavailable on this host.");
                        result = JsonSerializer.Serialize(new { ok = true, type, message = CompanionLaunchApplication(value) });
                        break;

                    case "terminal":
                        if (!permissions.companionTerminalCommands || !permissions.terminalCommands)
                            return BuildJsonError(request, 403, "Forbidden", "Both Companion Terminal Commands and Terminal Commands permissions are required.");
                        result = ExecuteTerminalCommandToolAsync(
                            "run_command_in_terminal",
                            JsonSerializer.Serialize(new { command = value, summary = "Companion command" }),
                            ownerKey).GetAwaiter().GetResult();
                        result = JsonSerializer.Serialize(new { ok = !result.Contains("blocked:", StringComparison.OrdinalIgnoreCase), type, output = RedactCompanionSensitiveText(result) });
                        break;

                    case "save-sensitive-memory":
                        if (!permissions.companionSensitiveMemory)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Sensitive Memory permission is disabled.");
                        if (!ConsumeCompanionConfirmation(ownerKey, value, "sensitive", confirmationToken))
                            return BuildJsonError(request, 428, "Confirmation Required", "Saving sensitive information requires a fresh local per-item confirmation.");
                        if (!TrySaveChatMemory(ownerKey, value, "", "sensitive-confirmed", "Sensitive", out ChatMemoryRecord savedMemory, out string saveError))
                            return BuildJsonError(request, 400, "Sensitive Memory Rejected", saveError);
                        result = JsonSerializer.Serialize(new { ok = true, type, memoryId = savedMemory.id, text = "[encrypted sensitive memory]" });
                        break;

                        default:
                            return BuildJsonError(request, 400, "Bad Request", "Unknown Companion action type.");
                    }
                }
                finally
                {
                    if (controlsDesktop)
                        EndCompanionDesktopControl();
                }

                result = AppendCompanionDesktopStatus(result);
                lock (_companionGate)
                {
                    _companionTaskStatus = "active";
                    _companionLastAction = permissions.companionActivityTranscriptStorage ? RedactCompanionSensitiveText(type + ": " + value) : type;
                }
                RecordObservabilityEvent("companion", type, "accepted", "", ownerKey, "/api/companion/action", 0L);
                return result;
            }
            catch (JsonException ex)
            {
                return AppendCompanionDesktopStatus(BuildJsonError(request, 400, "Bad Request", "Invalid Companion action JSON: " + ex.Message));
            }
            catch (Exception ex)
            {
                return AppendCompanionDesktopStatus(BuildJsonError(request, 500, "Companion Action Failed", RedactCompanionSensitiveText(ex.Message)));
            }
        }

        private const string CompanionToolInstruction = "[heirow Companion tool] Companion is a real callable function because its independent Workstation permission is enabled. For visible Windows interaction call companion_action; do not claim desktop control is unavailable or describe the tool in prose. Launch with the ordinary app/file name a person would enter in Windows Start search. Do not invent executable names, append .exe, or use aliases. heirowLLM owns a desktop controller outside model context and returns a controllerStatus snapshot after every action. It separately reports targetIsForeground, focusedControl and targetControlReady. After launch, inspect type=screen. Use the screenshot to choose the intended textbox/editor. If multiple inputs exist or targetControlReady is false, use Tab, Shift+Tab, arrow keys, or a click, observe again, and only then type. A sole Document/Edit control may be focused automatically. For type=text, put only the complete final user-facing content in text (or value), including the full title and paragraphs. Never put hidden reasoning, <think> tags, plans, tool names, or tool-call syntax in text; the host rejects it without typing. Blank/placeholder text fails. Prefer keyboard navigation and exact text. For Paint snake requests use type=draw, shape=snake, and the visible canvas rectangle. Verify state-changing actions. Permissions remain independent; denials are final and cannot be self-approved.";

        private bool TryBuildExplicitCompanionLaunchToolCall(string requestBody, out ToolCallData toolCall)
        {
            toolCall = null;
            if (string.IsNullOrWhiteSpace(requestBody) || !IsToolAdvertised(requestBody, "companion_action") ||
                HasCompanionActionInRequest(requestBody, "launch"))
                return false;
            string prompt = FirstNonEmpty(ExtractChatUiLastUserPromptText(requestBody), ExtractLastUserMessage(requestBody) ?? "");
            if (string.IsNullOrWhiteSpace(prompt) || Regex.IsMatch(prompt, @"(?i)\b(?:do\s+not|don't|dont|never)\s+(?:open|launch|start)\b"))
                return false;
            string target = ExtractCompanionLaunchTarget(prompt);
            if (string.IsNullOrWhiteSpace(target) || target.Length > 180)
                return false;
            toolCall = new ToolCallData
            {
                Id = "call_companion_" + Guid.NewGuid().ToString("N").Substring(0, 16),
                Name = "companion_action",
                ArgumentsJson = JsonSerializer.Serialize(new { type = "launch", value = target }),
                ArgumentsWereMalformed = false
            };
            return true;
        }

        private static string ExtractCompanionLaunchTarget(string prompt)
        {
            Match match = Regex.Match(prompt ?? "",
                @"(?ix)(?:^|[.!?]\s*)(?:please\s+)?(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?(?:open|launch|start|use)\s+(?:the\s+)?(?<target>.+?)(?=\s*(?:(?:,|\band\b|\bthen\b)\s+(?:please\s+)?(?:look\s+up|search|find|browse|visit|navigate|go|read|show|tell|get|write|type|enter|paste|press|click|draw|verify|make|create|open|use)\b|[.!?](?:\s|$)|$))",
                RegexOptions.CultureInvariant);
            return match.Success
                ? match.Groups["target"].Value.Trim().Trim('"', '\'', ',', ';')
                : "";
        }

        private bool TryBuildExplicitCompanionScreenToolCall(string requestBody, out ToolCallData toolCall)
        {
            toolCall = null;
            if (string.IsNullOrWhiteSpace(requestBody) || !IsToolAdvertised(requestBody, "companion_action") ||
                !HasCompanionActionInRequest(requestBody, "launch") || HasCompanionActionInRequest(requestBody, "screen"))
                return false;
            toolCall = new ToolCallData
            {
                Id = "call_companion_" + Guid.NewGuid().ToString("N").Substring(0, 16),
                Name = "companion_action",
                ArgumentsJson = "{\"type\":\"screen\"}",
                ArgumentsWereMalformed = false
            };
            return true;
        }

        private bool HasCompanionActionInRequest(string requestBody, string actionType)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
                    return false;
                foreach (JsonElement message in messages.EnumerateArray())
                {
                    if (!message.TryGetProperty("tool_calls", out JsonElement calls) || calls.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (JsonElement call in calls.EnumerateArray())
                    {
                        if (!call.TryGetProperty("function", out JsonElement function) || function.ValueKind != JsonValueKind.Object ||
                            !function.TryGetProperty("name", out JsonElement name) || !string.Equals(name.GetString(), "companion_action", StringComparison.Ordinal))
                            continue;
                        if (!function.TryGetProperty("arguments", out JsonElement arguments))
                            continue;
                        string argumentsJson = arguments.ValueKind == JsonValueKind.String ? arguments.GetString() : arguments.GetRawText();
                        using JsonDocument action = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                        string actualType = NormalizeCompanionActionType(CompanionJsonString(action.RootElement, "type"));
                        if (actualType.Equals(actionType, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool IsCompanionActionToolCallType(ToolCallData call, string actionType)
        {
            if (call == null || !string.Equals(call.Name, "companion_action", StringComparison.Ordinal))
                return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                string actualType = NormalizeCompanionActionType(CompanionJsonString(document.RootElement, "type"));
                return actualType.Equals(actionType, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool TryBuildCompanionActionDraftRequest(string requestBody, out string compactRequest)
        {
            compactRequest = "";
            if (string.IsNullOrWhiteSpace(requestBody) || requestBody.Contains("\"heirowllm_companion_action_draft\":true", StringComparison.Ordinal) ||
                !HasCompanionActionInRequest(requestBody, "screen") || HasCompanionActionInRequest(requestBody, "text"))
                return false;
            string prompt = FirstNonEmpty(ExtractChatUiLastUserPromptText(requestBody), ExtractLastUserMessage(requestBody) ?? "");
            if (string.IsNullOrWhiteSpace(prompt))
                return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                JsonElement companionSchema = default;
                if (document.RootElement.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement tool in tools.EnumerateArray())
                    {
                        if (string.Equals(ExtractToolSchemaName(tool), "companion_action", StringComparison.Ordinal))
                        {
                            companionSchema = tool.Clone();
                            break;
                        }
                    }
                }
                if (companionSchema.ValueKind == JsonValueKind.Undefined)
                    return false;

                string latestStatus = "";
                string latestImageUrl = "";
                if (document.RootElement.TryGetProperty("messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement message in messages.EnumerateArray())
                    {
                        if (message.TryGetProperty("role", out JsonElement role) && role.ValueKind == JsonValueKind.String &&
                            string.Equals(role.GetString(), "tool", StringComparison.OrdinalIgnoreCase) &&
                            message.TryGetProperty("content", out JsonElement toolContent) && toolContent.ValueKind == JsonValueKind.String &&
                            toolContent.GetString()?.Contains("controllerStatus", StringComparison.Ordinal) == true)
                            latestStatus = toolContent.GetString() ?? "";
                        if (!message.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (JsonElement part in content.EnumerateArray())
                        {
                            if (!part.TryGetProperty("type", out JsonElement partType) || !string.Equals(partType.GetString(), "image_url", StringComparison.OrdinalIgnoreCase) ||
                                !part.TryGetProperty("image_url", out JsonElement imageUrl) || imageUrl.ValueKind != JsonValueKind.Object ||
                                !imageUrl.TryGetProperty("url", out JsonElement url) || url.ValueKind != JsonValueKind.String)
                                continue;
                            latestImageUrl = url.GetString() ?? "";
                        }
                    }
                }

                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("model") || property.NameEquals("temperature") || property.NameEquals("top_p") ||
                        property.NameEquals("max_tokens") || property.NameEquals("max_completion_tokens") ||
                        property.NameEquals("reasoningLevel") || property.NameEquals("reasoning_level") || property.NameEquals("service"))
                        property.WriteTo(writer);
                }
                writer.WriteBoolean("stream", false);
                writer.WriteBoolean("heirowllm_companion_action_draft", true);
                writer.WriteString("tool_choice", "required");
                writer.WritePropertyName("tools");
                writer.WriteStartArray();
                companionSchema.WriteTo(writer);
                writer.WriteEndArray();
                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", "Return exactly one companion_action tool call and no prose. The intended app is already open and the screenshot is current, so do not launch or merely refocus it again. Use controllerStatus plus visible screenshot evidence to choose the single next action that materially advances the user's goal. Select the intended control with a screenshot-grounded shortcut, Tab, Shift+Tab, arrow key, or click before typing. For a browser, use the visible address/search control and type only the actual destination or search query, never the user's entire instruction. If the user's goal asks for a document, compose the complete polished final document and put only that document in type=text text. Never put reasoning, plans, think tags, or tool narration in text. Do not claim completion until a later screenshot visibly verifies the requested outcome.");
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WritePropertyName("content");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("type", "text");
                writer.WriteString("text", "User goal: " + prompt + (string.IsNullOrWhiteSpace(latestStatus) ? "" : "\nLatest controller status: " + TruncateForLog(latestStatus, 3200)));
                writer.WriteEndObject();
                if (!string.IsNullOrWhiteSpace(latestImageUrl))
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", latestImageUrl);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                compactRequest = Encoding.UTF8.GetString(stream.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                LogMessage("[Companion] Could not build compact post-screenshot action request: " + RedactCompanionSensitiveText(ex.Message));
                return false;
            }
        }

        private bool TryBuildCompanionDocumentDraftRequest(string requestBody, bool screenAlreadyVerified, string originalPrompt, out string draftRequest)
        {
            draftRequest = "";
            if (string.IsNullOrWhiteSpace(requestBody) || (!screenAlreadyVerified && !HasCompanionActionInRequest(requestBody, "screen")))
                return false;
            bool successfulScreen = screenAlreadyVerified || ExtractLatestProxyToolResultText(requestBody, 4).Any(result =>
                result.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase) &&
                result.Contains("\"type\":\"screen\"", StringComparison.OrdinalIgnoreCase));
            if (!successfulScreen)
                return false;
            string prompt = FirstNonEmpty(originalPrompt, ExtractChatUiLastUserPromptText(requestBody), ExtractLastUserMessage(requestBody) ?? "");
            if (!Regex.IsMatch(prompt, @"(?i)\b(?:write|type|enter|paste)\b") ||
                !Regex.IsMatch(prompt, @"(?i)\b(?:essay|article|letter|story|report|document|paragraph)\b"))
                return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                bool wroteMaxTokens = false;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("max_tokens") || property.NameEquals("max_completion_tokens"))
                    {
                        // The web UI serializes its Auto choice as zero. Passing that through
                        // makes LlmRuntime clamp the draft to one output token. Conversely, the
                        // full 2,048-token context cannot safely reserve a 2,048-token completion
                        // after its prompt. Preserve a smaller user cap and bound larger/Auto
                        // drafts to a complete but KV-safe document budget.
                        if (!wroteMaxTokens && property.Value.ValueKind == JsonValueKind.Number &&
                            property.Value.TryGetInt32(out int requestedMaxTokens) && requestedMaxTokens > 0)
                        {
                            writer.WriteNumber(property.Name, Math.Min(requestedMaxTokens, 768));
                            wroteMaxTokens = true;
                        }
                        continue;
                    }
                    if (property.NameEquals("model") ||
                        property.NameEquals("reasoningLevel") || property.NameEquals("reasoning_level") ||
                        property.NameEquals("heirowForgeModels"))
                        property.WriteTo(writer);
                }
                if (!wroteMaxTokens)
                    writer.WriteNumber("max_tokens", 768);
                writer.WriteNumber("temperature", 0.2);
                writer.WriteNumber("top_p", 0.9);
                writer.WriteBoolean("stream", false);
                writer.WriteString("tool_choice", "none");
                writer.WritePropertyName("messages");
                writer.WriteStartArray();
                writer.WriteStartObject();
                writer.WriteString("role", "system");
                writer.WriteString("content", "Write the complete final document requested by the user in fewer than 650 tokens. Your response must begin with the exact line FINAL_DOCUMENT_START, then the document title and the requested full paragraphs. Do not include analysis, thinking tags, plans, prefaces, code fences, tool names, or explanations before or after the document. /no_think");
                writer.WriteEndObject();
                writer.WriteStartObject();
                writer.WriteString("role", "user");
                writer.WriteString("content", prompt);
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.Flush();
                draftRequest = Encoding.UTF8.GetString(stream.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                LogMessage("[Companion] Could not build document-only drafting request: " + RedactCompanionSensitiveText(ex.Message));
                return false;
            }
        }

        private static string NormalizeCompanionGeneratedDocument(string content)
        {
            string text = (content ?? "").Trim();
            Match fenced = Regex.Match(text, @"(?is)^```(?:markdown|text)?\s*\n(?<body>.*)\n```$");
            if (fenced.Success)
                text = fenced.Groups["body"].Value.Trim();

            MatchCollection markers = Regex.Matches(text, @"(?im)^\s*FINAL_DOCUMENT_(?:START|SECTION_[1-9][0-9]*)\s*:?[ \t]*$");
            if (markers.Count > 0)
            {
                Match marker = markers[markers.Count - 1];
                text = text.Substring(marker.Index + marker.Length).Trim();
            }
            return text;
        }

        private string BuildCompanionDocumentSectionDraftRequest(string baseDraftRequest, string prompt, int sectionIndex, string priorDocument)
        {
            using JsonDocument document = JsonDocument.Parse(baseDraftRequest);
            using var stream = new System.IO.MemoryStream();
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("messages") || property.NameEquals("max_tokens") ||
                    property.NameEquals("max_completion_tokens") || property.NameEquals("stream") ||
                    property.NameEquals("tool_choice") || property.NameEquals("temperature") ||
                    property.NameEquals("top_p"))
                    continue;
                property.WriteTo(writer);
            }
            writer.WriteNumber("max_tokens", sectionIndex == 0 ? 220 : 200);
            writer.WriteNumber("temperature", 0);
            writer.WriteNumber("top_p", 1);
            writer.WriteBoolean("stream", false);
            writer.WriteString("tool_choice", "none");
            writer.WritePropertyName("messages");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("role", "system");
            string marker = "FINAL_DOCUMENT_SECTION_" + (sectionIndex + 1).ToString(CultureInfo.InvariantCulture);
            string sectionInstruction = sectionIndex == 0
                ? "Begin with the exact line " + marker + ". Then write only a concise title, one blank line, and the first polished body paragraph of 65 to 95 words."
                : "Begin with the exact line " + marker + ". Then write only body paragraph " + (sectionIndex + 1).ToString(CultureInfo.InvariantCulture) + " in 65 to 95 words, with no title and no extra paragraphs.";
            writer.WriteString("content", "You are a bounded heirowForge document section writer. " + sectionInstruction + " Do not include analysis, thinking tags, plans, prefaces, code fences, tool names, or explanations. /no_think");
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WriteString("content", prompt + (string.IsNullOrWhiteSpace(priorDocument) ? "" : "\n\nDocument written so far (continue without repeating it):\n" + priorDocument));
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string NormalizeCompanionGeneratedDocumentSection(string content, int sectionIndex)
        {
            string text = NormalizeCompanionGeneratedDocument(content);
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // Local models sometimes wrap a single requested paragraph across physical lines,
            // or omit the blank line between the title and first paragraph. Normalize those
            // presentation differences without admitting extra sections or internal narration.
            string[] lines = text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (lines.Length == 0)
                return "";
            if (sectionIndex == 0)
            {
                if (lines.Length < 2)
                    return text;
                return lines[0] + "\r\n\r\n" + string.Join(" ", lines.Skip(1));
            }
            return string.Join(" ", lines);
        }

        private async Task<string> ApplyCompanionDocumentForgeModelAsync(string requestBody, int sectionIndex, CancellationToken cancellationToken)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("heirowForgeModels", out JsonElement models) || models.ValueKind != JsonValueKind.Object)
                    return requestBody;
                string tier = sectionIndex switch { 0 => "low", 1 => "medium", _ => "high" };
                string selectedModel = CompanionJsonString(models, tier).Trim();
                if (string.IsNullOrWhiteSpace(selectedModel) || selectedModel.Length > 512)
                    return requestBody;
                string currentModel = CompanionJsonString(root, "model").Trim();
                if (string.Equals(selectedModel, currentModel, StringComparison.OrdinalIgnoreCase))
                {
                    LogMessage("[Chat UI] Companion document section " + (sectionIndex + 1).ToString(CultureInfo.InvariantCulture) + " reuses the already-loaded inference model for the configured heirowForge " + tier + " tier.");
                    return requestBody;
                }

                if (IsEffectiveLocalRuntimeLlmRuntime())
                {
                    bool selectedModelIsLoaded;
                    try
                    {
                        selectedModelIsLoaded = await IsWebChatRuntimeModelLoadedAsync(selectedModel, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        LogMessage("[Chat UI] Could not inspect the configured heirowForge " + tier + " model; reusing the active inference model. " + ex.Message);
                        return requestBody;
                    }
                    if (!selectedModelIsLoaded)
                    {
                        LogMessage("[Chat UI] Configured heirowForge " + tier + " model is not already loaded; reusing the active inference model instead of loading another model.");
                        return requestBody;
                    }
                }

                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                bool wroteModel = false;
                foreach (JsonProperty property in root.EnumerateObject())
                {
                    if (property.NameEquals("model"))
                    {
                        writer.WriteString("model", selectedModel);
                        wroteModel = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                if (!wroteModel)
                    writer.WriteString("model", selectedModel);
                writer.WriteEndObject();
                writer.Flush();
                LogMessage("[Chat UI] Companion document section " + (sectionIndex + 1).ToString(CultureInfo.InvariantCulture) + " uses the configured heirowForge " + tier + " model.");
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                return requestBody;
            }
        }

        private static bool CompanionGeneratedDocumentSectionLooksUsable(string content, int sectionIndex)
        {
            string text = NormalizeCompanionGeneratedDocumentSection(content, sectionIndex);
            if (text.Length < 90 || CompanionTextContainsInternalReasoning(text) || Regex.IsMatch(text, @"(.)\1{24,}", RegexOptions.Singleline))
                return false;
            string opening = text.Substring(0, Math.Min(text.Length, 320));
            if (Regex.IsMatch(opening, @"(?im)^\s*(?:analysis|reasoning|plan|the\s+user\s+(?:asks|wants)|we\s+need\s+to|i\s+need\s+to|let\s+me)\b"))
                return false;
            string[] blocks = Regex.Split(text, @"\r?\n\s*\r?\n").Where(block => !string.IsNullOrWhiteSpace(block)).ToArray();
            if (sectionIndex == 0)
                return blocks.Length == 2 && blocks[0].Length <= 140 && blocks[1].Length >= 80;
            return blocks.Length == 1 && blocks[0].Length >= 80;
        }

        private static bool CompanionGeneratedDocumentLooksUsable(string content, string prompt)
        {
            string text = NormalizeCompanionGeneratedDocument(content);
            if (text.Length < 180 || CompanionTextContainsInternalReasoning(text) || Regex.IsMatch(text, @"(.)\1{24,}", RegexOptions.Singleline))
                return false;
            string opening = text.Substring(0, Math.Min(text.Length, 420));
            if (Regex.IsMatch(opening, @"(?im)^\s*(?:analysis|reasoning|plan|the\s+user\s+(?:asks|wants)|we\s+need\s+to|i\s+need\s+to|let\s+me)\b") ||
                Regex.IsMatch(opening, @"(?i)\b(?:system\s+prompt|tool\s+schema|assistant\s+response)\b"))
                return false;
            if (Regex.IsMatch(prompt ?? "", @"(?i)\b(?:at\s+least\s+)?three\s+paragraphs?\b"))
            {
                int paragraphs = Regex.Split(text, @"\r?\n\s*\r?\n")
                    .Count(block => !string.IsNullOrWhiteSpace(block));
                if (paragraphs < 4) // title plus at least three body paragraphs
                    return false;
            }
            return true;
        }

        private static bool CompanionTextContainsInternalReasoning(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string probe = text
                .Replace("\\u003c", "<", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003e", ">", StringComparison.OrdinalIgnoreCase)
                .Replace("\\n", "\n", StringComparison.Ordinal);
            if (Regex.IsMatch(probe, @"(?is)<\s*/?\s*think\s*>") ||
                Regex.IsMatch(probe, @"(?im)^\s*(?:the\s+user\s+wants\s+me\s+to|i\s+need\s+to|let\s+me\s+(?:start|use|call|open))\b") &&
                Regex.IsMatch(probe, @"(?i)\b(?:companion[-_ ]?action|tool\s+call|type\s*=\s*(?:text|launch))\b"))
                return true;
            return Regex.IsMatch(probe, @"(?is)\b(?:companion[-_ ]?action|tool\s+call)\b.{0,240}\b(?:provide|send|type)\b.{0,80}\b(?:essay|text|content)\b") &&
                   Regex.IsMatch(probe, @"(?i)\b(?:the\s+user|i\s+(?:need|will|should|use))\b");
        }

        private object ReadCompanionDesktopStatus()
        {
            string json = CompanionDesktopStatus?.Invoke();
            if (string.IsNullOrWhiteSpace(json))
                return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.Clone();
            }
            catch
            {
                return new { phase = "status_unavailable", message = "heirowLLM returned an invalid desktop controller status." };
            }
        }

        private string AppendCompanionDesktopStatus(string resultJson)
        {
            object status = ReadCompanionDesktopStatus();
            if (status == null)
                return resultJson;
            try
            {
                using JsonDocument result = JsonDocument.Parse(string.IsNullOrWhiteSpace(resultJson) ? "{}" : resultJson);
                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                if (result.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in result.RootElement.EnumerateObject())
                    {
                        if (!property.NameEquals("controllerStatus"))
                            property.WriteTo(writer);
                    }
                }
                else
                {
                    writer.WriteString("result", result.RootElement.ToString());
                }
                writer.WritePropertyName("controllerStatus");
                JsonSerializer.Serialize(writer, status);
                writer.WriteEndObject();
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch
            {
                return resultJson;
            }
        }

        private string AddCompanionTools(string requestBody, ChatPermissionState permissions, string ownerKey)
        {
            if (permissions == null || !permissions.companionEnabled || string.IsNullOrWhiteSpace(requestBody))
                return requestBody;
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                bool requireDesktopAction = CompanionPromptRequiresDesktopAction(requestBody, permissions);
                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                bool wroteTools = false;
                bool wroteMessages = false;
                bool wroteToolChoice = false;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("tool_choice"))
                    {
                        wroteToolChoice = true;
                        if (requireDesktopAction)
                            writer.WriteString("tool_choice", "required");
                        else
                            property.WriteTo(writer);
                    }
                    else if (property.NameEquals("tools") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        wroteTools = true;
                        writer.WritePropertyName("tools");
                        writer.WriteStartArray();
                        if (!requireDesktopAction)
                        {
                            foreach (JsonElement tool in property.Value.EnumerateArray())
                            {
                                string existingName = ExtractToolSchemaName(tool) ?? "";
                                if (!existingName.Equals("companion_action", StringComparison.Ordinal))
                                    tool.WriteTo(writer);
                            }
                        }
                        WriteCompanionToolSchema(writer, permissions);
                        writer.WriteEndArray();
                    }
                    else if (property.NameEquals("messages") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        wroteMessages = true;
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", CompanionToolInstruction);
                        writer.WriteEndObject();
                        foreach (JsonElement message in property.Value.EnumerateArray())
                            message.WriteTo(writer);
                        writer.WriteEndArray();
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }
                if (!wroteTools)
                {
                    writer.WritePropertyName("tools");
                    writer.WriteStartArray();
                    WriteCompanionToolSchema(writer, permissions);
                    writer.WriteEndArray();
                }
                if (!wroteMessages)
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("role", "system");
                    writer.WriteString("content", CompanionToolInstruction);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
                if (requireDesktopAction && !wroteToolChoice)
                    writer.WriteString("tool_choice", "required");
                writer.WriteEndObject();
                writer.Flush();
                return Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (Exception ex)
            {
                LogMessage("[Companion] Could not add Companion tool schema: " + RedactCompanionSensitiveText(ex.Message));
                return requestBody;
            }
        }

        private bool CompanionPromptRequiresDesktopAction(string requestBody, ChatPermissionState permissions)
        {
            string prompt = ExtractChatUiLastUserPromptText(requestBody);
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = ExtractLastUserMessage(requestBody) ?? "";
            if (string.IsNullOrWhiteSpace(prompt) || Regex.IsMatch(prompt, @"(?ix)\b(?:do\s+not|don't|dont|never)\s+(?:open|launch|start|find|locate|click|type|enter|draw|use)\b"))
                return false;

            string requestPrefix = @"(?ix)(?:^|[.!?]\s*)(?:please\s+)?(?:(?:can|could|would|will)\s+you\s+(?:please\s+)?)?";
            if (permissions?.companionApplicationLaunch == true &&
                Regex.IsMatch(prompt, requestPrefix + @"(?:open|launch|start|find|locate|show\s+me)\b"))
                return true;
            if (permissions?.companionApplicationControl == true &&
                Regex.IsMatch(prompt, requestPrefix + @"(?:type|enter|press|scroll|paste|copy)\b"))
                return true;
            if (permissions?.companionCursorControl == true &&
                Regex.IsMatch(prompt, requestPrefix + @"(?:click|move|drag|draw)\b"))
                return true;
            return false;
        }

        private void WriteCompanionToolSchema(Utf8JsonWriter writer, ChatPermissionState permissions)
        {
            var allowedActions = new List<string> { "screen", "observe" };
            if (permissions?.companionApplicationLaunch == true)
                allowedActions.Add("launch");
            if (permissions?.companionApplicationControl == true)
                allowedActions.AddRange(new[] { "shortcut", "key", "text", "scroll", "clipboard" });
            if (permissions?.companionCursorControl == true)
                allowedActions.Add("move");
            if (permissions?.companionCursorControl == true && permissions?.companionApplicationControl == true)
                allowedActions.AddRange(new[] { "click", "draw" });
            if (permissions?.companionTerminalCommands == true && permissions.terminalCommands)
                allowedActions.Add("terminal");
            if (permissions?.companionSensitiveMemory == true)
                allowedActions.Add("save-sensitive-memory");
            string allowedActionText = string.Join(", ", allowedActions.Distinct(StringComparer.OrdinalIgnoreCase));
            WriteProxyResearchToolSchema(
                writer,
                "companion_action",
                "Use permission-gated Companion actions when the user asks to interact with visible Windows apps or files. Launch searches Windows by the ordinary user-facing name, observe confirms visible state, and input manipulates the foreground window.",
                new[] { "type" },
                new[]
                {
                    new ProxyToolParameter("type", "string", "Allowed: " + allowedActionText + ". observe returns the full desktop for visual decisions; screen captures the foreground application window as requested evidence. Pointer actions are human-paced."),
                    new ProxyToolParameter("value", "string", "For launch, the exact ordinary app or file name a person should type into Windows Start search; do not add .exe or use an alias. Also used for a shortcut chord or terminal command. For type=text or clipboard, text is preferred but value is accepted as the exact content."),
                    new ProxyToolParameter("x", "integer", "Screen X."),
                    new ProxyToolParameter("y", "integer", "Screen Y."),
                    new ProxyToolParameter("normalizedX", "number", "X from 0 to 1."),
                    new ProxyToolParameter("normalizedY", "number", "Y from 0 to 1."),
                    new ProxyToolParameter("button", "string", "left or right"),
                    new ProxyToolParameter("delta", "integer", "Scroll delta."),
                    new ProxyToolParameter("keyCode", "integer", "Windows virtual key. Common navigation: Tab=9, Enter=13, Escape=27, Left=37, Up=38, Right=39, Down=40."),
                    new ProxyToolParameter("down", "boolean", "Key-down state."),
                    new ProxyToolParameter("text", "string", "Complete literal non-whitespace user-facing text to type or copy. For a requested document, include its full title and body. Never include reasoning, think tags, tool names, or tool-call narration."),
                    new ProxyToolParameter("replaceExisting", "boolean", "For type=text, select all content in the verified input before typing. Use true when creating a new document so restored or existing text is replaced."),
                    new ProxyToolParameter("shape", "string", "For type=draw, the shape to draw. Currently snake."),
                    new ProxyToolParameter("width", "integer", "For type=draw, bounding width in screen pixels."),
                    new ProxyToolParameter("height", "integer", "For type=draw, bounding height in screen pixels."),
                    new ProxyToolParameter("durationMs", "integer", "Optional human motion duration in milliseconds."),
                    new ProxyToolParameter("steps", "integer", "Optional motion samples, 2 through 240."),
                    new ProxyToolParameter("jitterPixels", "number", "Optional subtle cursor variation in pixels, 0 through 12."),
                    new ProxyToolParameter("seed", "integer", "Optional deterministic motion seed for testing."),
                    new ProxyToolParameter("confirmationToken", "string", "Local confirmation token.")
                });
        }

        private bool WriteCompanionChatUserMessage(Utf8JsonWriter writer, JsonElement originalMessage, ChatPermissionState permissions)
        {
            if (writer == null)
                return false;
            BeginCompanionControlSession();
            string text = ExtractChatUiMessageContentText(originalMessage);
            if (string.IsNullOrWhiteSpace(text))
                text = "Observe the current desktop and continue the user's Companion request.";

            writer.WriteStartObject();
            writer.WriteString("role", "user");
            writer.WritePropertyName("content");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("type", "text");
            writer.WriteString("text", text);
            writer.WriteEndObject();

            // Start from the user's natural-language goal. The model can launch a named
            // application without spending its limited context on stale pre-action pixels,
            // then call observe to receive a fresh screenshot after the launch completes.
            // This also keeps the visible action ordering honest for Companion tasks.
            writer.WriteEndArray();
            writer.WriteEndObject();
            return true;
        }

        private void BeginCompanionControlSession()
        {
            lock (_companionGate)
            {
                _companionEmergencyStopped = false;
                if (_companionTaskStatus is "idle" or "stopped" or "emergency-stopped")
                    _companionTaskStatus = "ready";
            }
        }

        private string HandleCompanionConfirm(NetworkConnection connection, HttpRequest request)
        {
            if (!IsLocalAdmin(connection, request))
                return BuildJsonError(request, 403, "Forbidden", "Companion confirmations must be entered locally at the Workstation.");
            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                string ownerKey = NormalizeChatFilesystemOwnerKey(CompanionJsonString(document.RootElement, "ownerKey"));
                string action = CompanionJsonString(document.RootElement, "action");
                string kind = CompanionJsonString(document.RootElement, "kind").Trim().ToLowerInvariant();
                string phrase = CompanionJsonString(document.RootElement, "phrase");
                ChatPermissionState permissions = GetChatPermissions(ownerKey);
                if (kind == "sensitive")
                {
                    if (!permissions.companionSensitiveMemory)
                        return BuildJsonError(request, 403, "Forbidden", "Companion Sensitive Memory permission is disabled.");
                    if (!string.Equals(phrase, CompanionSensitiveConfirmationPhrase, StringComparison.Ordinal))
                        return BuildJsonError(request, 400, "Confirmation Rejected", "The exact sensitive-memory confirmation phrase is required.");
                }
                else
                {
                    kind = "financial";
                    if (!permissions.companionFinancialActions)
                        return BuildJsonError(request, 403, "Forbidden", "Companion Financial Actions permission is disabled.");
                    if (!string.Equals(phrase, CompanionFinancialConfirmationPhrase, StringComparison.Ordinal))
                        return BuildJsonError(request, 400, "Confirmation Rejected", "The exact financial-risk confirmation phrase is required.");
                }
                string token = CompanionRandomToken();
                lock (_companionGate)
                {
                    _companionConfirmations[token] = new CompanionConfirmation
                    {
                        OwnerKey = ownerKey,
                        ActionHash = CompanionHash(action),
                        Kind = kind,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(2)
                    };
                }
                return JsonSerializer.Serialize(new { ok = true, confirmationToken = token, expiresUtc = DateTimeOffset.UtcNow.AddMinutes(2) });
            }
            catch (JsonException ex)
            {
                return BuildJsonError(request, 400, "Bad Request", ex.Message);
            }
        }

        private string HandleCompanionEmergencyStop(NetworkConnection connection, HttpRequest request)
        {
            EmergencyStopCompanionControl();
            CompanionEmergencyStopRequested?.Invoke(this, EventArgs.Empty);
            RecordObservabilityEvent("companion", "emergency stop", "stopped", "", GetChatSessionOwnerKey(connection, request), "/api/companion/emergency-stop", 0L);
            return JsonSerializer.Serialize(new { ok = true, status = "emergency-stopped" });
        }

        public void EmergencyStopCompanionControl()
        {
            lock (_companionGate)
            {
                _companionTaskGoal = "";
                _companionTaskOwner = "";
                _companionTaskStatus = "emergency-stopped";
                _companionLastAction = "";
                _companionEmergencyStopped = true;
                _companionConfirmations.Clear();
            }
            StopCompanionDesktopControlImmediately();
        }

        private void BeginCompanionDesktopControl()
        {
            bool showOverlay;
            TimeSpan leadTime;
            lock (_companionGate)
            {
                _companionControlGeneration++;
                _companionControlLingerTimer?.Dispose();
                _companionControlLingerTimer = null;
                showOverlay = !_companionControlOverlayActive;
                _companionControlOverlayActive = true;
                leadTime = CompanionControlLeadTime < TimeSpan.Zero ? TimeSpan.Zero : CompanionControlLeadTime;
            }
            if (!showOverlay)
                return;

            CompanionControlStateChanged?.Invoke(true);
            if (leadTime > TimeSpan.Zero)
                System.Threading.Thread.Sleep(leadTime);
        }

        private static string NormalizeCompanionActionType(string type)
        {
            string normalized = (type ?? "").Trim().Replace('_', '-').ToLowerInvariant();
            return normalized is "open" or "start" or "find" or "search" or "find-open" or "search-open" or "open-app" or "open-file"
                ? "launch"
                : normalized;
        }

        private void EndCompanionDesktopControl()
        {
            int generation;
            TimeSpan lingerTime;
            lock (_companionGate)
            {
                generation = ++_companionControlGeneration;
                _companionControlLingerTimer?.Dispose();
                lingerTime = CompanionControlLingerTime < TimeSpan.Zero ? TimeSpan.Zero : CompanionControlLingerTime;
                if (lingerTime > TimeSpan.Zero)
                {
                    _companionControlLingerTimer = new System.Threading.Timer(
                        _ => HideCompanionDesktopControlAfterLinger(generation),
                        null,
                        lingerTime,
                        System.Threading.Timeout.InfiniteTimeSpan);
                    return;
                }
                _companionControlLingerTimer = null;
            }
            HideCompanionDesktopControlAfterLinger(generation);
        }

        private void HideCompanionDesktopControlAfterLinger(int generation)
        {
            bool hideOverlay = false;
            lock (_companionGate)
            {
                if (generation != _companionControlGeneration || !_companionControlOverlayActive)
                    return;
                _companionControlOverlayActive = false;
                _companionControlLingerTimer?.Dispose();
                _companionControlLingerTimer = null;
                hideOverlay = true;
            }
            if (hideOverlay)
                CompanionControlStateChanged?.Invoke(false);
        }

        private void StopCompanionDesktopControlImmediately()
        {
            bool hideOverlay;
            lock (_companionGate)
            {
                _companionControlGeneration++;
                _companionControlLingerTimer?.Dispose();
                _companionControlLingerTimer = null;
                hideOverlay = _companionControlOverlayActive;
                _companionControlOverlayActive = false;
            }
            if (hideOverlay)
                CompanionControlStateChanged?.Invoke(false);
        }

        private bool ConsumeCompanionConfirmation(string ownerKey, string action, string kind, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            lock (_companionGate)
            {
                if (!_companionConfirmations.TryGetValue(token, out CompanionConfirmation confirmation)) return false;
                _companionConfirmations.Remove(token);
                return confirmation.ExpiresUtc >= DateTimeOffset.UtcNow &&
                    string.Equals(confirmation.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(confirmation.Kind, kind, StringComparison.Ordinal) &&
                    string.Equals(confirmation.ActionHash, CompanionHash(action), StringComparison.Ordinal);
            }
        }

        private void ApplyCompanionPermissionJson(JsonElement root, ChatPermissionState permissions)
        {
            if (root.ValueKind != JsonValueKind.Object || permissions == null) return;
            Apply("companionEnabled", value => permissions.companionEnabled = value, permissions.companionEnabled);
            Apply("companionScreenView", value => permissions.companionScreenView = value, permissions.companionScreenView);
            Apply("companionCursorControl", value => permissions.companionCursorControl = value, permissions.companionCursorControl);
            Apply("companionApplicationLaunch", value => permissions.companionApplicationLaunch = value, permissions.companionApplicationLaunch);
            Apply("companionApplicationControl", value => permissions.companionApplicationControl = value, permissions.companionApplicationControl);
            Apply("companionTerminalCommands", value => permissions.companionTerminalCommands = value, permissions.companionTerminalCommands);
            Apply("companionActivityTranscriptStorage", value => permissions.companionActivityTranscriptStorage = value, permissions.companionActivityTranscriptStorage);
            Apply("companionSensitiveMemory", value => permissions.companionSensitiveMemory = value, permissions.companionSensitiveMemory);
            Apply("companionFinancialActions", value => permissions.companionFinancialActions = value, permissions.companionFinancialActions);
            void Apply(string name, Action<bool> setter, bool fallback)
            {
                if (root.TryGetProperty(name, out JsonElement element)) setter(ReadJsonBool(element, fallback));
            }
        }

        public static bool IsCompanionFinancialAction(string text)
        {
            return Regex.IsMatch(text ?? "",
                @"(?ix)\b(checkout|buy|purchase|pay(?:ment)?|subscribe|subscription|donat(?:e|ion)|bid|wire\s+transfer|bank(?:ing)?|brokerage|trade|trading|crypto(?:currency)?|bitcoin|ethereum|wager|gambl(?:e|ing)|casino|paid\s+trial|billing|invoice|credit\s+card|debit\s+card|paypal|venmo|cashapp|stripe|authorize\.net)\b");
        }

        public static bool IsCompanionSensitiveText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text,
                @"(?ix)(password|passwd|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|bearer\s+[a-z0-9._~+/=-]+|private[_ -]?key|recovery[_ -]?code|seed[_ -]?phrase|session[_ -]?cookie|authorization\s*[:=]|credit[_ -]?card|routing[_ -]?number|social[_ -]?security|medical[_ -]?record)");
        }

        public static string RedactCompanionSensitiveText(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return Regex.Replace(text,
                @"(?ix)\b(password|passwd|api[_ -]?key|access[_ -]?token|refresh[_ -]?token|authorization|cookie|secret|private[_ -]?key)\b\s*[:=]\s*[^,\s;]+",
                "$1=[redacted]");
        }

        private static string CompanionJsonString(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
                ? value.ToString()
                : "";

        private static string CompanionHash(string value)
        {
            using SHA256 sha = SHA256.Create();
            return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }

        private static string CompanionRandomToken()
        {
            byte[] bytes = new byte[32];
            using RandomNumberGenerator rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToBase64String(bytes);
        }

        private sealed class CompanionConfirmation
        {
            public string OwnerKey { get; set; } = "";
            public string ActionHash { get; set; } = "";
            public string Kind { get; set; } = "";
            public DateTimeOffset ExpiresUtc { get; set; }
        }
    }
}
