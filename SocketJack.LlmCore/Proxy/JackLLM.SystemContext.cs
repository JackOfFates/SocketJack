using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LmVs;

namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
        private const string RunningApplicationsToolName = "list_running_applications";
        private const string WindowsServicesToolName = "list_windows_services";
        private const string EventViewerToolName = "query_event_viewer";
        private const string InspectFilesToolName = "inspect_files";
        private readonly object _systemContextPermissionLock = new object();
        private readonly Dictionary<string, PendingSystemContextPermissionRequest> _pendingSystemContextPermissionRequests =
            new Dictionary<string, PendingSystemContextPermissionRequest>(StringComparer.Ordinal);

        public Func<SystemContextQuery, CancellationToken, Task<SystemContextResult>> SystemContextProvider { get; set; }
        public event EventHandler<SystemContextPermissionRequestEventArgs> SystemContextPermissionRequested;

        public IReadOnlyList<SystemContextPermissionRequestSnapshot> GetPendingSystemContextPermissionRequestsDiagnostics(string ownerKey, string sessionId = "")
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            lock (_systemContextPermissionLock)
                return _pendingSystemContextPermissionRequests.Values
                    .Where(item => item?.Request != null && ChatOwnerKeysMatch(ownerKey, item.Request.OwnerKey) &&
                        (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, item.Request.SessionId, StringComparison.OrdinalIgnoreCase)))
                    .Select(item => CopySystemContextPermissionRequest(item.Request, true))
                    .ToList();
        }

        public Task<string> ExecuteSystemContextToolDiagnosticsAsync(string toolName, string argumentsJson, string ownerKey, string sessionId, CancellationToken cancellationToken = default) =>
            ExecuteSystemContextToolAsync(toolName, argumentsJson, ownerKey, sessionId, cancellationToken);

        public bool DecideSystemContextPermissionDiagnostics(string requestId, bool approved, bool always = false)
        {
            PendingSystemContextPermissionRequest pending = FindPendingSystemContextPermissionRequest(requestId);
            if (pending == null) return false;
            if (approved && always) EnableStandingSystemContextPermission(pending.Request.OwnerKey, pending.Request.Capability);
            RemovePendingSystemContextPermissionRequest(requestId);
            pending.Completion.TrySetResult(new SystemContextPermissionDecision(approved, always,
                approved ? (always ? "Standing access approved." : "Allowed once.") : "The user denied this context request."));
            return true;
        }

        public string GetJackCapabilityContextDiagnostics(string ownerKey, bool consentUi) =>
            BuildJackCapabilityContextSystemHint(GetChatPermissions(ownerKey), consentUi);

        public string AttachSystemContextToolsDiagnostics(string requestBody, string ownerKey) =>
            AddProxyResearchTools(requestBody, GetChatPermissions(ownerKey), false, false, false, ownerKey);

        private void RegisterSystemContextRoutes(HttpServer server)
        {
            server.Map("GET", "/api/context-approvals", (connection, request, _) => HandleSystemContextApprovalsListRequest(connection, request));
            server.Map("POST", "/api/context-approvals", (connection, request, _) => HandleSystemContextApprovalDecisionRequest(connection, request));
            server.Map("OPTIONS", "/api/context-approvals", (connection, request, _) => HandleWebAuthCorsPreflight(request));
        }

        private string HandleSystemContextApprovalsListRequest(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeSystemContextApprovalClient(connection, request, out string ownerKey, out bool isAdmin, out string error))
                return BuildJsonError(request, 401, "Unauthorized", error);

            string sessionId = (GetQueryParameter(request, "sessionId") ?? "").Trim();
            List<SystemContextPermissionRequestSnapshot> approvals;
            lock (_systemContextPermissionLock)
            {
                approvals = _pendingSystemContextPermissionRequests.Values
                    .Where(item => item?.Request != null &&
                        ChatOwnerKeysMatch(ownerKey, item.Request.OwnerKey) &&
                        (string.IsNullOrWhiteSpace(sessionId) || string.Equals(sessionId, item.Request.SessionId, StringComparison.OrdinalIgnoreCase)))
                    .Select(item => CopySystemContextPermissionRequest(item.Request, isAdmin))
                    .OrderBy(item => item.CreatedUtc, StringComparer.Ordinal)
                    .ToList();
            }
            return JsonSerializer.Serialize(new { ok = true, ownerKey, canAlwaysAllow = isAdmin, approvals });
        }

        private string HandleSystemContextApprovalDecisionRequest(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeSystemContextApprovalClient(connection, request, out string ownerKey, out bool isAdmin, out string error))
                return BuildJsonError(request, 401, "Unauthorized", error);
            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                string requestId = ContextJsonString(document.RootElement, "requestId", "id");
                string action = ContextJsonString(document.RootElement, "action").Trim().ToLowerInvariant();
                string sessionId = ContextJsonString(document.RootElement, "sessionId").Trim();
                PendingSystemContextPermissionRequest pending = FindPendingSystemContextPermissionRequest(requestId);
                if (pending == null)
                    return BuildJsonError(request, 404, "Not Found", "Context approval request was not found or is no longer pending.");

                if (!ChatOwnerKeysMatch(ownerKey, pending.Request.OwnerKey))
                    return BuildJsonError(request, 403, "Forbidden", "This context approval belongs to another owner.");
                if (string.IsNullOrWhiteSpace(sessionId) || !string.Equals(sessionId, pending.Request.SessionId, StringComparison.OrdinalIgnoreCase))
                    return BuildJsonError(request, 403, "Forbidden", "This context approval belongs to another session.");

                bool always = action == "allow_always";
                if (always && !isAdmin)
                    return BuildJsonError(request, 403, "Forbidden", "Only a Workstation administrator can create standing context access.");
                if (action != "allow_once" && action != "allow_always" && action != "deny")
                    return BuildJsonError(request, 400, "Bad Request", "Unknown context approval action.");

                if (always)
                    EnableStandingSystemContextPermission(pending.Request.OwnerKey, pending.Request.Capability);
                RemovePendingSystemContextPermissionRequest(pending.Request.Id);
                bool approved = action != "deny";
                pending.Completion.TrySetResult(new SystemContextPermissionDecision(approved, always,
                    approved ? (always ? "Standing access approved." : "Allowed once.") : "The user denied this context request."));
                RecordObservabilityEvent("security", "context permission", approved ? (always ? "always allowed" : "allowed once") : "denied",
                    pending.Request.QuerySummary, pending.Request.OwnerKey, pending.Request.ToolName, 0L);
                return JsonSerializer.Serialize(new { ok = true, request = CopySystemContextPermissionRequest(pending.Request, isAdmin), approved, always });
            }
            catch (JsonException ex)
            {
                return BuildJsonError(request, 400, "Bad Request", "Invalid context approval JSON: " + ex.Message);
            }
        }

        private bool TryAuthorizeSystemContextApprovalClient(NetworkConnection connection, HttpRequest request, out string ownerKey, out bool isAdmin, out string error)
        {
            ownerKey = "";
            isAdmin = false;
            if (TryAuthenticateWebAuthRequest(request, out WebAuthPrincipal principal, out error))
            {
                ownerKey = GetLlmClientOwnerKey(connection, request, principal);
                isAdmin = principal != null && principal.IsAdministrator;
                return true;
            }

            MobileDeviceRecord mobile = AuthenticateMobileDevice(request);
            if (mobile != null)
            {
                ownerKey = NormalizeChatFilesystemOwnerKey(string.IsNullOrWhiteSpace(mobile.OwnerKey) ? "mobile:" + mobile.Id : mobile.OwnerKey);
                isAdmin = IsMobileDeviceAdministrator(mobile);
                error = "";
                return true;
            }
            error = "Sign in with a Workstation account or use a currently paired mobile device.";
            return false;
        }

        private static SystemContextPermissionRequestSnapshot CopySystemContextPermissionRequest(SystemContextPermissionRequestSnapshot source, bool canAlwaysAllow) =>
            new SystemContextPermissionRequestSnapshot
            {
                Id = source.Id,
                OwnerKey = source.OwnerKey,
                SessionId = source.SessionId,
                Capability = source.Capability,
                ToolName = source.ToolName,
                QuerySummary = source.QuerySummary,
                ArgumentsJson = source.ArgumentsJson,
                CreatedUtc = source.CreatedUtc,
                CanAlwaysAllow = canAlwaysAllow
            };

        private async Task<SystemContextPermissionDecision> QueueSystemContextPermissionRequestAsync(
            string ownerKey, string sessionId, string capability, string toolName, string argumentsJson, CancellationToken cancellationToken)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = EnsureChatUiSessionId(sessionId);
            ChatPermissionState permissions = GetChatPermissions(ownerKey);
            if (IsStandingSystemContextPermissionEnabled(permissions, capability))
                return new SystemContextPermissionDecision(true, true, "Standing permission is enabled.");

            string normalizedArguments = NormalizeSystemContextArguments(argumentsJson);
            string dedupeKey = ownerKey + "\n" + sessionId + "\n" + capability + "\n" + ComputeSystemContextHash(normalizedArguments);
            PendingSystemContextPermissionRequest pending;
            lock (_systemContextPermissionLock)
            {
                pending = _pendingSystemContextPermissionRequests.Values.FirstOrDefault(existing => existing != null &&
                    string.Equals(existing.Request.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Request.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.Request.Capability, capability, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ComputeSystemContextRequestKey(existing.Request), dedupeKey, StringComparison.Ordinal));
                if (pending == null)
                {
                    var snapshot = new SystemContextPermissionRequestSnapshot
                    {
                        Id = "ctxperm_" + Guid.NewGuid().ToString("N"),
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        Capability = capability,
                        ToolName = toolName ?? "",
                        QuerySummary = BuildSystemContextQuerySummary(toolName, normalizedArguments),
                        ArgumentsJson = normalizedArguments,
                        CreatedUtc = DateTimeOffset.UtcNow.ToString("O")
                    };
                    pending = new PendingSystemContextPermissionRequest(snapshot);
                    _pendingSystemContextPermissionRequests[snapshot.Id] = pending;
                }
            }

            LogMessage("[Context Permission] Approval required for " + ownerKey + ": " + pending.Request.QuerySummary);
            RecordObservabilityEvent("security", "context permission", "pending", pending.Request.QuerySummary, ownerKey, toolName, 0L);
            SystemContextPermissionRequested?.Invoke(this, new SystemContextPermissionRequestEventArgs(pending.Request));
            if (await Task.WhenAny(pending.Completion.Task, Task.Delay(TimeSpan.FromMinutes(10), cancellationToken)).ConfigureAwait(false) == pending.Completion.Task)
                return await pending.Completion.Task.ConfigureAwait(false);

            RemovePendingSystemContextPermissionRequest(pending.Request.Id);
            string reason = cancellationToken.IsCancellationRequested
                ? "Context access was cancelled before approval."
                : "Context access approval timed out.";
            pending.Completion.TrySetResult(new SystemContextPermissionDecision(false, false, reason));
            RecordObservabilityEvent("security", "context permission", cancellationToken.IsCancellationRequested ? "cancelled" : "timed out",
                pending.Request.QuerySummary, ownerKey, toolName, 0L);
            return new SystemContextPermissionDecision(false, false, reason);
        }

        private PendingSystemContextPermissionRequest FindPendingSystemContextPermissionRequest(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return null;
            lock (_systemContextPermissionLock)
                return _pendingSystemContextPermissionRequests.TryGetValue(requestId, out PendingSystemContextPermissionRequest pending) ? pending : null;
        }

        private void RemovePendingSystemContextPermissionRequest(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId)) return;
            lock (_systemContextPermissionLock) _pendingSystemContextPermissionRequests.Remove(requestId);
        }

        private static string ComputeSystemContextRequestKey(SystemContextPermissionRequestSnapshot request) =>
            (request?.OwnerKey ?? "") + "\n" + (request?.SessionId ?? "") + "\n" + (request?.Capability ?? "") + "\n" + ComputeSystemContextHash(request?.ArgumentsJson ?? "{}");

        private static string ComputeSystemContextHash(string value)
        {
            using SHA256 sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static string NormalizeSystemContextArguments(string argumentsJson)
        {
            string value = string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson.Trim();
            if (value.Length > 4096) value = value.Substring(0, 4096);
            try
            {
                using JsonDocument document = JsonDocument.Parse(value);
                return JsonSerializer.Serialize(document.RootElement);
            }
            catch
            {
                return "{}";
            }
        }

        private static string BuildSystemContextQuerySummary(string toolName, string argumentsJson)
        {
            string label = toolName switch
            {
                RunningApplicationsToolName => "Inspect running applications",
                WindowsServicesToolName => "Inspect Windows services",
                EventViewerToolName => "Read Application/System Event Viewer logs",
                "read_file" or "vs_read_file" or "vs_search_files" or "vs_list_files" => "Inspect files",
                "companion_action" => "Observe the Windows desktop",
                _ => "Read additional workstation context"
            };
            string compact = RegexWhitespace(argumentsJson);
            if (compact == "{}") return label;
            if (compact.Length > 260) compact = compact.Substring(0, 260) + "…";
            return label + ": " + compact;
        }

        private static string RegexWhitespace(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var builder = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) { space = true; continue; }
                if (space && builder.Length > 0) builder.Append(' ');
                builder.Append(c);
                space = false;
            }
            return builder.ToString();
        }

        private static bool IsStandingSystemContextPermissionEnabled(ChatPermissionState permissions, string capability)
        {
            if (permissions == null) return false;
            return capability switch
            {
                "runningApplications" => permissions.runningApplications,
                "windowsServices" => permissions.windowsServices,
                "eventViewer" => permissions.eventViewer,
                "fileAccess" => permissions.fileAccess,
                "companionObservation" => permissions.companionEnabled && permissions.companionScreenView,
                _ => false
            };
        }

        private void EnableStandingSystemContextPermission(string ownerKey, string capability)
        {
            ChatPermissionState permissions = GetChatPermissions(ownerKey);
            switch (capability)
            {
                case "runningApplications": permissions.runningApplications = true; break;
                case "windowsServices": permissions.windowsServices = true; break;
                case "eventViewer": permissions.eventViewer = true; break;
                case "fileAccess": permissions.fileAccess = true; break;
                case "companionObservation": permissions.companionEnabled = true; permissions.companionScreenView = true; break;
                default: throw new InvalidOperationException("Unknown context capability.");
            }
            SaveChatPermissions(ownerKey, permissions);
            ApplyRuntimeChatPermissions(GetChatPermissions());
        }

        private static bool IsSystemContextTool(string toolName) =>
            string.Equals(toolName, RunningApplicationsToolName, StringComparison.Ordinal) ||
            string.Equals(toolName, WindowsServicesToolName, StringComparison.Ordinal) ||
            string.Equals(toolName, EventViewerToolName, StringComparison.Ordinal) ||
            string.Equals(toolName, InspectFilesToolName, StringComparison.Ordinal);

        private static bool IsReadOnlyFileContextTool(string toolName) =>
            string.Equals(toolName, "read_file", StringComparison.Ordinal) ||
            string.Equals(toolName, "vs_read_file", StringComparison.Ordinal) ||
            string.Equals(toolName, "vs_search_files", StringComparison.Ordinal) ||
            string.Equals(toolName, "vs_list_files", StringComparison.Ordinal);

        private static string SystemContextCapabilityForTool(string toolName) => toolName switch
        {
            RunningApplicationsToolName => "runningApplications",
            WindowsServicesToolName => "windowsServices",
            EventViewerToolName => "eventViewer",
            InspectFilesToolName => "fileAccess",
            _ => ""
        };

        private async Task<string> ExecuteSystemContextToolAsync(string toolName, string argumentsJson, string ownerKey, string sessionId, CancellationToken cancellationToken)
        {
            string capability = SystemContextCapabilityForTool(toolName);
            if (string.IsNullOrWhiteSpace(capability))
                return JsonSerializer.Serialize(new { ok = false, error = "Unknown system context tool." });
            SystemContextPermissionDecision decision = await QueueSystemContextPermissionRequestAsync(ownerKey, sessionId, capability, toolName, argumentsJson, cancellationToken).ConfigureAwait(false);
            if (!decision.Approved)
                return JsonSerializer.Serialize(new { ok = false, status = "denied", capability, error = decision.Reason });
            if (toolName == InspectFilesToolName)
                return ExecuteApprovedFileInspection(argumentsJson, ownerKey, sessionId);
            if (SystemContextProvider == null)
                return JsonSerializer.Serialize(new { ok = false, status = "unavailable", capability, error = "Windows context is unavailable on this host." });

            SystemContextQuery query;
            try { query = ParseSystemContextQuery(toolName, argumentsJson); }
            catch (Exception ex) { return JsonSerializer.Serialize(new { ok = false, status = "invalid_request", capability, error = ex.Message }); }
            try
            {
                SystemContextResult result = await SystemContextProvider(query, cancellationToken).ConfigureAwait(false) ?? new SystemContextResult { Ok = false, Kind = query.Kind, Error = "The Windows provider returned no result." };
                return SerializeBoundedSystemContextResult(result);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { ok = false, status = "failed", capability, error = ex.Message });
            }
        }

        private string ExecuteApprovedFileInspection(string argumentsJson, string ownerKey, string sessionId)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                JsonElement root = document.RootElement;
                string action = ContextJsonString(root, "action").Trim().ToLowerInvariant();
                string innerTool = action switch
                {
                    "read" => "vs_read_file",
                    "list" => "vs_list_files",
                    "search" => "vs_search_files",
                    _ => ""
                };
                if (string.IsNullOrWhiteSpace(innerTool))
                    return JsonSerializer.Serialize(new { ok = false, status = "invalid_request", capability = "fileAccess", error = "action must be read, list, or search." });

                // The underlying reader retains all session-root and approved-root checks.
                // This wrapper deliberately exposes no file mutation operation.
                string result = ExecuteProxyVsTool(innerTool, argumentsJson, ownerKey, sessionId);
                if (result.Length > 65536)
                    result = result.Substring(0, 65536) + "\n[truncated at 64 KB]";
                return JsonSerializer.Serialize(new { ok = true, capability = "fileAccess", action, result });
            }
            catch (JsonException ex)
            {
                return JsonSerializer.Serialize(new { ok = false, status = "invalid_request", capability = "fileAccess", error = ex.Message });
            }
        }

        private static SystemContextQuery ParseSystemContextQuery(string toolName, string argumentsJson)
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            JsonElement root = document.RootElement;
            int defaultTake = toolName == RunningApplicationsToolName ? 40 : toolName == WindowsServicesToolName ? 60 : 30;
            int take = Math.Clamp(ContextJsonInt(root, defaultTake, "take"), 1, 100);
            var query = new SystemContextQuery
            {
                Kind = toolName,
                Query = ContextJsonString(root, "query"),
                IncludeBackground = ContextJsonBool(root, false, "includeBackground"),
                Status = ContextJsonString(root, "status"),
                LogName = ContextJsonString(root, "logName", "log"),
                Provider = ContextJsonString(root, "provider"),
                EventId = ContextJsonNullableInt(root, "eventId"),
                SinceMinutes = Math.Clamp(ContextJsonInt(root, 120, "sinceMinutes"), 1, 10080),
                Take = take,
                Levels = ContextJsonStringArray(root, "levels")
            };
            if (string.IsNullOrWhiteSpace(query.LogName)) query.LogName = "both";
            if (toolName == EventViewerToolName && query.LogName != "both" &&
                !query.LogName.Equals("Application", StringComparison.OrdinalIgnoreCase) &&
                !query.LogName.Equals("System", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Event Viewer access is limited to Application, System, or both.");
            return query;
        }

        private static string SerializeBoundedSystemContextResult(SystemContextResult result)
        {
            result.CapturedUtc = string.IsNullOrWhiteSpace(result.CapturedUtc) ? DateTimeOffset.UtcNow.ToString("O") : result.CapturedUtc;
            string json = JsonSerializer.Serialize(result);
            while (Encoding.UTF8.GetByteCount(json) > 65536 && result.Items.Count > 0)
            {
                result.Items.RemoveAt(result.Items.Count - 1);
                result.Truncated = true;
                json = JsonSerializer.Serialize(result);
            }
            if (Encoding.UTF8.GetByteCount(json) <= 65536) return json;
            return JsonSerializer.Serialize(new { ok = false, kind = result.Kind, error = "Windows context result exceeded the 64 KB safety limit.", truncated = true });
        }

        private string BuildJackCapabilityContextSystemHint(ChatPermissionState permissions, bool consentUi)
        {
            permissions ??= new ChatPermissionState();
            var enabled = new List<string>();
            var disabled = new List<string>();
            void Add(string name, bool value) => (value ? enabled : disabled).Add(name);
            Add("agent", permissions.agentAccess);
            Add("fileAccess", permissions.fileAccess);
            Add("internetSearch", permissions.internetSearch);
            Add("downloads", permissions.fileDownloads);
            Add("terminal", permissions.terminalCommands);
            Add("companion", permissions.companionEnabled);
            Add("companionObservation", permissions.companionEnabled && permissions.companionScreenView);
            Add("runningApplications", permissions.runningApplications);
            Add("windowsServices", permissions.windowsServices);
            Add("eventViewer", permissions.eventViewer);
            string requestable = consentUi
                ? "Unchecked read-only capabilities remain requestable with a fresh user approval for each tool call: fileAccess, companionObservation, runningApplications, windowsServices, eventViewer."
                : "This client has no interactive context-consent UI; disabled capabilities are unavailable.";
            return "[JACK capability context] Enabled: " + (enabled.Count == 0 ? "none" : string.Join(", ", enabled)) +
                ". Disabled: " + (disabled.Count == 0 ? "none" : string.Join(", ", disabled)) + ". " + requestable +
                " Never claim access or results until the corresponding tool call succeeds.";
        }

        private bool RequestSupportsContextConsentUi(string requestBody)
        {
            if (string.IsNullOrWhiteSpace(requestBody)) return false;
            if (!string.IsNullOrWhiteSpace(ExtractChatUiShareKey(requestBody))) return false;
            try
            {
                using JsonDocument document = JsonDocument.Parse(requestBody);
                return document.RootElement.TryGetProperty("contextConsentUi", out JsonElement value) &&
                    (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed);
            }
            catch { return false; }
        }

        private static void WriteSystemContextToolSchemas(LmVsProxy proxy, Utf8JsonWriter writer)
        {
            proxy.WriteProxyResearchToolSchema(writer, RunningApplicationsToolName,
                "List running Windows applications as bounded read-only context. If standing access is disabled, JACK asks the user before this call runs.",
                Array.Empty<string>(), new[]
                {
                    new ProxyToolParameter("query", "string", "Optional process or window-title filter."),
                    new ProxyToolParameter("includeBackground", "boolean", "Include background processes without visible windows. Default false."),
                    new ProxyToolParameter("take", "integer", "Maximum grouped applications. Default 40, max 100.")
                });
            proxy.WriteProxyResearchToolSchema(writer, WindowsServicesToolName,
                "List Windows services as bounded read-only context. If standing access is disabled, JACK asks the user before this call runs.",
                Array.Empty<string>(), new[]
                {
                    new ProxyToolParameter("query", "string", "Optional service-name or display-name filter."),
                    new ProxyToolParameter("status", "string", "Optional status filter such as running, stopped, paused, or pending."),
                    new ProxyToolParameter("take", "integer", "Maximum services. Default 60, max 100.")
                });
            proxy.WriteProxyResearchToolSchema(writer, EventViewerToolName,
                "Query recent Windows Application and System Event Viewer entries as bounded read-only context. Security and other logs are unavailable.",
                Array.Empty<string>(), new[]
                {
                    new ProxyToolParameter("logName", "string", "Application, System, or both. Default both."),
                    new ProxyToolParameter("levels", "array", "Optional levels: critical, error, warning, information, verbose."),
                    new ProxyToolParameter("provider", "string", "Optional provider-name filter."),
                    new ProxyToolParameter("eventId", "integer", "Optional exact event id."),
                    new ProxyToolParameter("sinceMinutes", "integer", "Lookback minutes. Default 120, max 10080."),
                    new ProxyToolParameter("query", "string", "Optional message text filter."),
                    new ProxyToolParameter("take", "integer", "Maximum events. Default 30, max 100.")
                });
            proxy.WriteProxyResearchToolSchema(writer, InspectFilesToolName,
                "Read, list, or search files as read-only JACK context. Every unchecked call asks the user first. Existing session-root and approved-root restrictions still apply; this tool cannot change files.",
                new[] { "action" }, new[]
                {
                    new ProxyToolParameter("action", "string", "read, list, or search."),
                    new ProxyToolParameter("path", "string", "File or directory path for read/list."),
                    new ProxyToolParameter("query", "string", "File-name or content query for search."),
                    new ProxyToolParameter("startLine", "integer", "Optional first line for read."),
                    new ProxyToolParameter("endLine", "integer", "Optional last line for read."),
                    new ProxyToolParameter("take", "integer", "Optional bounded result count."),
                    new ProxyToolParameter("recursive", "boolean", "Whether list/search may recurse within an approved root.")
                });
        }

        private static string ContextJsonString(JsonElement root, params string[] names)
        {
            foreach (string name in names)
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                    return value.GetString() ?? "";
            return "";
        }

        private static int ContextJsonInt(JsonElement root, int fallback, params string[] names)
        {
            foreach (string name in names)
            {
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value)) continue;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int numeric)) return numeric;
                if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)) return parsed;
            }
            return fallback;
        }

        private static int? ContextJsonNullableInt(JsonElement root, params string[] names)
        {
            int value = ContextJsonInt(root, int.MinValue, names);
            return value == int.MinValue ? null : value;
        }

        private static bool ContextJsonBool(JsonElement root, bool fallback, params string[] names)
        {
            foreach (string name in names)
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value))
                {
                    if (value.ValueKind == JsonValueKind.True) return true;
                    if (value.ValueKind == JsonValueKind.False) return false;
                    if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
                }
            return fallback;
        }

        private static string[] ContextJsonStringArray(JsonElement root, string name)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value)) return Array.Empty<string>();
            if (value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString() ?? "").Where(item => item.Length > 0).Take(8).ToArray();
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString().Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Take(8).ToArray();
            return Array.Empty<string>();
        }
    }

    public sealed class SystemContextPermissionRequestEventArgs : EventArgs
    {
        public SystemContextPermissionRequestEventArgs(SystemContextPermissionRequestSnapshot request) => Request = request;
        public SystemContextPermissionRequestSnapshot Request { get; }
    }
}
