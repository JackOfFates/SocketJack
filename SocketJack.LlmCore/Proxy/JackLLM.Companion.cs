using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LmVs;
using SocketJack.Net;

namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
        public Func<int, int, int, byte[]> CompanionCaptureJpeg { get; set; }
        public Action<string> CompanionInput { get; set; }
        public Func<string, string> CompanionLaunchApplication { get; set; }
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
                return BuildJsonError(request, 403, "Forbidden", "Companion mode is disabled for this owner.");

            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                string action = CompanionJsonString(document.RootElement, "action").ToLowerInvariant();
                string goal = CompanionJsonString(document.RootElement, "goal").Trim();
                bool controlActive;
                lock (_companionGate)
                {
                    if (action is "stop" or "cancel")
                    {
                        _companionTaskStatus = "stopped";
                        _companionTaskGoal = "";
                        _companionTaskOwner = "";
                        _companionConfirmations.Clear();
                        controlActive = false;
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
                        controlActive = true;
                    }
                }
                CompanionControlStateChanged?.Invoke(controlActive);
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
                string type = CompanionJsonString(root, "type").Trim().ToLowerInvariant();
                string value = CompanionJsonString(root, "value");
                string confirmationToken = CompanionJsonString(root, "confirmationToken");
                string actionMaterial = type + "\n" + (request?.Body ?? value);
                bool observation = type == "observe" || type == "screen";
                if (!permissions.companionEnabled && !(observationApprovedOnce && observation))
                    return BuildJsonError(request, 403, "Forbidden", "Companion mode is disabled.");

                if (IsCompanionFinancialAction(actionMaterial))
                {
                    if (!permissions.companionFinancialActions)
                        return BuildJsonError(request, 403, "Financial Action Blocked", "Financial actions are disabled. Companion cannot spend money.");
                    if (!ConsumeCompanionConfirmation(ownerKey, actionMaterial, "financial", confirmationToken))
                        return BuildJsonError(request, 428, "Confirmation Required",
                            "This action may cause charges, recurring billing, account loss, irreversible transfers, or severe financial damage. Confirm the exact action locally in JackLLM Workstation.");
                }

                string result;
                switch (type)
                {
                    case "observe":
                    case "screen":
                        if (!permissions.companionScreenView && !observationApprovedOnce)
                            return BuildJsonError(request, 403, "Forbidden", "Companion Screen View permission is disabled.");
                        if (CompanionCaptureJpeg == null)
                            return BuildJsonError(request, 501, "Not Supported", "Screen capture is unavailable on this host.");
                        byte[] jpeg = CompanionCaptureJpeg(1280, 720, 65);
                        result = JsonSerializer.Serialize(new { ok = true, type = "screen", contentType = "image/jpeg", data = Convert.ToBase64String(jpeg) });
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
                    case "key":
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

                lock (_companionGate)
                {
                    _companionTaskStatus = "active";
                    _companionLastAction = permissions.companionActivityTranscriptStorage ? RedactCompanionSensitiveText(type + ": " + value) : type;
                }
                CompanionControlStateChanged?.Invoke(true);
                RecordObservabilityEvent("companion", type, "accepted", "", ownerKey, "/api/companion/action", 0L);
                return result;
            }
            catch (JsonException ex)
            {
                return BuildJsonError(request, 400, "Bad Request", "Invalid Companion action JSON: " + ex.Message);
            }
            catch (Exception ex)
            {
                return BuildJsonError(request, 500, "Companion Action Failed", RedactCompanionSensitiveText(ex.Message));
            }
        }

        private string AddCompanionTools(string requestBody, ChatPermissionState permissions, string ownerKey)
        {
            bool consentUi = RequestSupportsContextConsentUi(requestBody);
            if (permissions == null || (!permissions.companionEnabled && !consentUi) || string.IsNullOrWhiteSpace(requestBody))
                return requestBody;
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                using var stream = new System.IO.MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                bool wroteTools = false;
                bool wroteMessages = false;
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    if (property.NameEquals("tools") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        wroteTools = true;
                        writer.WritePropertyName("tools");
                        writer.WriteStartArray();
                        foreach (JsonElement tool in property.Value.EnumerateArray())
                            tool.WriteTo(writer);
                        WriteCompanionToolSchema(writer);
                        writer.WriteEndArray();
                    }
                    else if (property.NameEquals("messages") && property.Value.ValueKind == JsonValueKind.Array)
                    {
                        wroteMessages = true;
                        writer.WritePropertyName("messages");
                        writer.WriteStartArray();
                        foreach (JsonElement message in property.Value.EnumerateArray())
                            message.WriteTo(writer);
                        writer.WriteStartObject();
                        writer.WriteString("role", "system");
                        writer.WriteString("content", "Companion observation is available for owner " + NormalizeChatFilesystemOwnerKey(ownerKey) + ". If standing Companion Screen View is disabled, type=observe or screen requests fresh user consent for that call. Active desktop control remains unavailable unless its existing standing permissions are enabled. Permission denials are final. Never attempt to approve financial or sensitive-memory confirmations yourself.");
                        writer.WriteEndObject();
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
                    WriteCompanionToolSchema(writer);
                    writer.WriteEndArray();
                }
                if (!wroteMessages)
                {
                    writer.WritePropertyName("messages");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("role", "system");
                    writer.WriteString("content", "Companion observation may require fresh per-call user consent. Active desktop control still requires the existing standing Companion permissions. Honor every denial.");
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                }
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

        private void WriteCompanionToolSchema(Utf8JsonWriter writer)
        {
            WriteProxyResearchToolSchema(
                writer,
                "companion_action",
                "Observe or operate the Windows desktop through integrated JackLLM Companion. Every action is independently permissioned. Use observe before and after state-changing actions.",
                new[] { "type" },
                new[]
                {
                    new ProxyToolParameter("type", "string", "One of observe, move, click, key, text, scroll, clipboard, launch, terminal, or save-sensitive-memory."),
                    new ProxyToolParameter("value", "string", "Application target, terminal command, sensitive-memory value, or fallback action payload."),
                    new ProxyToolParameter("x", "integer", "Absolute screen X coordinate for move/click."),
                    new ProxyToolParameter("y", "integer", "Absolute screen Y coordinate for move/click."),
                    new ProxyToolParameter("normalizedX", "number", "Optional horizontal position from 0 to 1."),
                    new ProxyToolParameter("normalizedY", "number", "Optional vertical position from 0 to 1."),
                    new ProxyToolParameter("button", "string", "Mouse button: left or right."),
                    new ProxyToolParameter("delta", "integer", "Scroll-wheel delta."),
                    new ProxyToolParameter("keyCode", "integer", "Windows virtual key code for a key action."),
                    new ProxyToolParameter("down", "boolean", "Whether a key action presses rather than releases the key."),
                    new ProxyToolParameter("text", "string", "Text for text or clipboard actions."),
                    new ProxyToolParameter("confirmationToken", "string", "Short-lived exact-action token returned only after a local Workstation confirmation.")
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

            if (permissions != null && permissions.companionEnabled && permissions.companionScreenView && CompanionCaptureJpeg != null)
            {
                byte[] jpeg = CompanionCaptureJpeg(1280, 720, 65);
                if (jpeg != null && jpeg.Length > 0)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type", "image_url");
                    writer.WritePropertyName("image_url");
                    writer.WriteStartObject();
                    writer.WriteString("url", "data:image/jpeg;base64," + Convert.ToBase64String(jpeg));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
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
            CompanionControlStateChanged?.Invoke(true);
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
