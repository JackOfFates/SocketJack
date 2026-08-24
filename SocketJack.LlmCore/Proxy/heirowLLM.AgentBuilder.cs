using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Net.AgentBuilder;
using SocketJack.Net.Database;

namespace SocketJack.Net
{
    public partial class HeirowLlm
    {
        private const string LocalAgentBuilderDatabaseName = "AgentBuilder";
        private readonly object _localAgentBuilderLock = new object();
        private Table _localAgentBuilderWorkflows;
        private Table _localAgentBuilderApis;
        private Table _localAgentBuilderRuns;
        private readonly ConcurrentDictionary<string, AgentBuilderWorkflow> _localAgentBuilderWorkflowCache = new ConcurrentDictionary<string, AgentBuilderWorkflow>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, AgentBuilderApiDefinition> _localAgentBuilderApiCache = new ConcurrentDictionary<string, AgentBuilderApiDefinition>(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] LocalAgentBuilderWorkflowColumns =
        {
            "Id", "OwnerUserName", "Name", "Description", "NodesJson", "EdgesJson", "VariablesJson",
            "ApiName", "ApiEnabled", "Enabled", "CreatedUtc", "UpdatedUtc", "SchemaVersion", "Revision",
            "PresetId", "PresetVersion", "BasePresetHash", "ApplicationJson"
        };

        private static readonly string[] LocalAgentBuilderApiColumns =
        {
            "Id", "WorkflowId", "OwnerUserName", "ApiName", "Route", "Enabled", "RequireAuthentication", "CreatedUtc", "UpdatedUtc"
        };

        private static readonly string[] LocalAgentBuilderRunColumns =
        {
            "Id", "WorkflowId", "ApiName", "OwnerUserName", "TriggerKind", "Status", "InputJson", "OutputJson",
            "Error", "NodeResultsJson", "DurationMs", "StartedUtc", "CompletedUtc"
        };

        private void EnsureLocalAgentBuilderStorage()
        {
            lock (_localAgentBuilderLock)
            {
                SocketJack.Net.Database.Database database = _chatSessionData.Databases.GetOrAdd(LocalAgentBuilderDatabaseName, name => new SocketJack.Net.Database.Database(name));
                _localAgentBuilderWorkflows = EnsureLocalAgentBuilderTable(database, "Workflows", LocalAgentBuilderWorkflowColumns);
                _localAgentBuilderApis = EnsureLocalAgentBuilderTable(database, "CustomApis", LocalAgentBuilderApiColumns);
                _localAgentBuilderRuns = EnsureLocalAgentBuilderTable(database, "Runs", LocalAgentBuilderRunColumns);
                _localAgentBuilderWorkflowCache.Clear();
                foreach (object[] row in _localAgentBuilderWorkflows.Rows)
                {
                    AgentBuilderWorkflow workflow = LocalAgentBuilderWorkflowFromRow(row);
                    if (!string.IsNullOrWhiteSpace(workflow.Id))
                        _localAgentBuilderWorkflowCache[workflow.Id] = workflow;
                }
                _localAgentBuilderApiCache.Clear();
                foreach (object[] row in _localAgentBuilderApis.Rows)
                {
                    AgentBuilderApiDefinition api = LocalAgentBuilderApiFromRow(row);
                    if (!string.IsNullOrWhiteSpace(api.ApiName))
                        _localAgentBuilderApiCache[api.ApiName] = api;
                }
            }
        }

        private static Table EnsureLocalAgentBuilderTable(SocketJack.Net.Database.Database database, string name, IReadOnlyList<string> columns)
        {
            Table table = database.Tables.GetOrAdd(name, tableName => new Table(tableName));
            if (table.Columns == null)
                table.Columns = new List<Column>();
            for (int i = 0; i < columns.Count; i++)
            {
                while (table.Columns.Count <= i)
                    table.Columns.Add(new Column(columns[table.Columns.Count], typeof(string), -1));
                if (!string.Equals(table.Columns[i].Name, columns[i], StringComparison.OrdinalIgnoreCase))
                    table.Columns[i] = new Column(columns[i], typeof(string), -1);
            }
            return table;
        }

        private void RegisterLocalAgentBuilderRoutes(HttpServer server)
        {
            foreach (string path in new[] { "/Builder", "/Builder/", "/builder", "/builder/" })
                server.Map("GET", path, (connection, request, cancellationToken) => HandleLocalAgentBuilderPage(connection, request));

            server.Map("GET", "/Builder/output/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderOutput(connection, request));
            server.Map("GET", "/builder/output/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderOutput(connection, request));
            server.Map("GET", "/api/agentbuilder/session", (connection, request, cancellationToken) => HandleLocalAgentBuilderSession(connection, request));
            server.Map("GET", "/api/agentbuilder/workflows", (connection, request, cancellationToken) => HandleLocalAgentBuilderWorkflows(connection, request, false));
            server.Map("POST", "/api/agentbuilder/workflows", (connection, request, cancellationToken) => HandleLocalAgentBuilderWorkflows(connection, request, true));
            server.Map("DELETE", "/api/agentbuilder/workflows/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderDelete(connection, request));
            server.Map("GET", "/api/agentbuilder/apis", (connection, request, cancellationToken) => HandleLocalAgentBuilderApis(connection, request));
            server.Map("POST", "/api/agentbuilder/apis/publish", (connection, request, cancellationToken) => HandleLocalAgentBuilderPublish(connection, request));
            server.Map("POST", "/api/agentbuilder/run", (connection, request, cancellationToken) => HandleLocalAgentBuilderRun(connection, request, cancellationToken));
            server.Map("GET", "/api/agentbuilder/runs", (connection, request, cancellationToken) => HandleLocalAgentBuilderRuns(connection, request));
            server.Map("GET", "/api/agentbuilder/reflection/catalog", (connection, request, cancellationToken) => HandleLocalAgentBuilderReflectionCatalog(connection, request));
            server.Map("POST", "/api/agentbuilder/reflection/test", (connection, request, cancellationToken) => HandleLocalAgentBuilderReflectionTest(connection, request, cancellationToken));
            server.Map("GET", "/api/agentbuilder/presets", (connection, request, cancellationToken) => HandleLocalAgentBuilderPresets(connection, request));
            server.Map("POST", "/api/agentbuilder/presets/instantiate", (connection, request, cancellationToken) => HandleLocalAgentBuilderPresetMutation(connection, request, false));
            server.Map("POST", "/api/agentbuilder/presets/reset", (connection, request, cancellationToken) => HandleLocalAgentBuilderPresetMutation(connection, request, true));
            server.Map("GET", "/api/agentbuilder/applications/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderApplication(connection, request));
            server.Map("POST", "/api/agentbuilder/applications/propose", (connection, request, cancellationToken) => HandleLocalAgentBuilderApplicationProposal(connection, request, cancellationToken));
            server.Map("GET", "/api/builder/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderPublishedApi(connection, request, cancellationToken));
            server.Map("POST", "/api/builder/*", (connection, request, cancellationToken) => HandleLocalAgentBuilderPublishedApi(connection, request, cancellationToken));
        }

        private object HandleLocalAgentBuilderPresets(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error)) return error;
            AgentBuilderPresetDefinition preset = PictureBankAgentBuilderPreset.Create();
            AgentBuilderWorkflow instance = EnsurePictureBankPresetInstance(ownerKey);
            return LocalAgentBuilderJson(request, new { ok = true, presets = new[] { preset }, instances = new[] { instance } });
        }

        private object HandleLocalAgentBuilderPresetMutation(NetworkConnection connection, HttpRequest request, bool reset)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error)) return error;
            JsonObject body = LocalAgentBuilderJsonObject(request);
            string presetId = LocalAgentBuilderText(body, "presetId", PictureBankAgentBuilderPreset.PresetId);
            if (!presetId.Equals(PictureBankAgentBuilderPreset.PresetId, StringComparison.OrdinalIgnoreCase))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Preset not found." }, 404, "Not Found");
            AgentBuilderWorkflow workflow = reset ? ResetPictureBankPresetInstance(ownerKey) : EnsurePictureBankPresetInstance(ownerKey);
            return LocalAgentBuilderJson(request, new { ok = true, workflow, reset });
        }

        private object HandleLocalAgentBuilderApplication(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error)) return error;
            string slug = request.PathVariables.Count > 0 ? AgentBuilderSlug.Normalize(request.PathVariables[0]) : "";
            AgentBuilderWorkflow workflow = _localAgentBuilderWorkflowCache.Values.FirstOrDefault(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey) && item.Application?.Slug.Equals(slug, StringComparison.OrdinalIgnoreCase) == true);
            if (workflow == null && slug.Equals(PictureBankAgentBuilderPreset.PresetId, StringComparison.OrdinalIgnoreCase)) workflow = EnsurePictureBankPresetInstance(ownerKey);
            return workflow == null
                ? LocalAgentBuilderJson(request, new { ok = false, error = "Application not found." }, 404, "Not Found")
                : LocalAgentBuilderJson(request, new { ok = true, workflowId = workflow.Id, revision = workflow.Revision, presetId = workflow.PresetId, presetVersion = workflow.PresetVersion, application = workflow.Application });
        }

        private object HandleLocalAgentBuilderApplicationProposal(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error)) return error;
            try
            {
                JsonObject body = LocalAgentBuilderJsonObject(request);
                string workflowId = LocalAgentBuilderText(body, "workflowId", "");
                string instruction = LocalAgentBuilderText(body, "instruction", LocalAgentBuilderText(body, "prompt", ""));
                string selectedComponentId = LocalAgentBuilderText(body, "selectedComponentId", "");
                if (!_localAgentBuilderWorkflowCache.TryGetValue(workflowId, out AgentBuilderWorkflow workflow) || !LocalAgentBuilderOwnedBy(workflow.OwnerUserName, ownerKey))
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Application workflow not found." }, 404, "Not Found");
                if (string.IsNullOrWhiteSpace(instruction)) return LocalAgentBuilderJson(request, new { ok = false, error = "Builder instruction is required." }, 400, "Bad Request");
                AgentBuilderApplicationDefinition before = AgentBuilderJson.Deserialize<AgentBuilderApplicationDefinition>(AgentBuilderJson.Serialize(workflow.Application)) ?? new();
                AgentBuilderApplicationDefinition after = AgentBuilderJson.Deserialize<AgentBuilderApplicationDefinition>(AgentBuilderJson.Serialize(workflow.Application)) ?? new();
                var patch = new List<object>();
                string lower = instruction.ToLowerInvariant();
                if (lower.Contains("add") && lower.Contains("panel"))
                {
                    AgentBuilderUiComponent root = after.Components.FirstOrDefault();
                    string title = instruction.Trim(); if (title.Length > 80) title = title.Substring(0, 80);
                    var component = new AgentBuilderUiComponent { Id = "panel_" + Guid.NewGuid().ToString("N"), Type = "panel", Slot = "right", Movable = true, Resizable = true, Properties = new() { ["title"] = title } };
                    if (root != null) root.Children.Add(component); else after.Components.Add(component);
                    patch.Add(new { op = "add", path = "/components/0/children/-", value = component });
                }
                if (lower.StartsWith("set title to ", StringComparison.Ordinal))
                {
                    string title = instruction.Substring("set title to ".Length).Trim().Trim('"'); if (title.Length > 120) title = title.Substring(0, 120);
                    after.Title = title; patch.Add(new { op = "replace", path = "/title", value = title });
                }
                after.Normalize(); List<AgentBuilderWorkflowValidationIssue> applicationErrors = AgentBuilderApplicationValidator.Validate(after);
                object context = new { workflowId = workflow.Id, workflowRevision = workflow.Revision, selectedComponentId, errors = AgentBuilderWorkflowEngine.ValidateWorkflow(workflow), previewContext = body["previewContext"], application = before };
                string mediatedPrompt = "You are heirowLLM UI Builder. Inspect the complete current application schema, selected component, validation errors, and preview context before the user's instruction. Suggest only safe registered components and structured schema operations. Never emit JavaScript or HTML.\nContext:\n" + AgentBuilderJson.Serialize(context) + "\nInstruction:\n" + instruction;
                object modelAnalysis;
                try { modelAnalysis = RunLocalAgentBuilderPrompt(connection, request, new AgentBuilderNode { Type = "agent", Config = new() { ["model"] = "auto" } }, mediatedPrompt, cancellationToken); }
                catch (Exception ex) { modelAnalysis = new { offline = true, error = ex.Message }; }
                return LocalAgentBuilderJson(request, new { ok = applicationErrors.Count == 0, proposalId = "ui_patch_" + Guid.NewGuid().ToString("N"), baseRevision = workflow.Revision, before, after, patch, applicationErrors, modelAnalysis, requiresExplicitApply = true });
            }
            catch (Exception ex) { return LocalAgentBuilderJson(request, new { ok = false, error = ex.Message }, 400, "Bad Request"); }
        }

        private AgentBuilderWorkflow EnsurePictureBankPresetInstance(string ownerKey)
        {
            AgentBuilderWorkflow existing = _localAgentBuilderWorkflowCache.Values.FirstOrDefault(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey) && item.PresetId.Equals(PictureBankAgentBuilderPreset.PresetId, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            return ResetPictureBankPresetInstance(ownerKey);
        }

        private AgentBuilderWorkflow ResetPictureBankPresetInstance(string ownerKey)
        {
            AgentBuilderPresetDefinition preset = PictureBankAgentBuilderPreset.Create();
            AgentBuilderWorkflow prior = _localAgentBuilderWorkflowCache.Values.FirstOrDefault(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey) && item.PresetId.Equals(PictureBankAgentBuilderPreset.PresetId, StringComparison.OrdinalIgnoreCase));
            AgentBuilderWorkflow workflow = PictureBankAgentBuilderPreset.CreateWorkflow(ownerKey);
            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            workflow.Id = prior?.Id ?? ("app_picturebank_" + LocalAgentBuilderSha256(ownerKey ?? "").Substring(0, 16));
            workflow.OwnerUserName = ownerKey; workflow.PresetVersion = preset.Version; workflow.BasePresetHash = preset.BaseHash;
            workflow.Revision = Math.Max(1, (prior?.Revision ?? 0) + 1); workflow.CreatedUtc = prior?.CreatedUtc ?? now; workflow.UpdatedUtc = now;
            SaveLocalAgentBuilderWorkflow(workflow); return workflow;
        }

        private static string LocalAgentBuilderSha256(string value)
        {
            using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private bool TryAuthorizeLocalAgentBuilder(NetworkConnection connection, HttpRequest request, out string ownerKey, out object error)
        {
            ownerKey = GetChatSessionOwnerKey(connection, request);
            if (!GetChatPermissions(ownerKey).agentBuilder)
            {
                error = BuildJsonError(request, 403, "Forbidden", "Agent Builder permission is disabled for this Workstation account.");
                return false;
            }
            error = null;
            return true;
        }

        private object HandleLocalAgentBuilderPage(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out _, out object error))
                return error;
            string html = HtmlPageResources.GetHtml("Builder.html");
            if (string.IsNullOrWhiteSpace(html))
                return BuildJsonError(request, 500, "Internal Server Error", "The embedded Agent Builder page is unavailable.");
            request.Context.Response.ContentType = "text/html; charset=utf-8";
            return html;
        }

        private object HandleLocalAgentBuilderSession(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            return LocalAgentBuilderJson(request, new
            {
                ok = true,
                authenticated = true,
                username = LocalAgentBuilderOwnerName(ownerKey),
                isAdministrator = IsDatabaseAdministrator(connection, request),
                database = LocalAgentBuilderDatabaseName,
                workflowCount = _localAgentBuilderWorkflowCache.Values.Count(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey)),
                apiCount = _localAgentBuilderApiCache.Values.Count(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey))
            });
        }

        private object HandleLocalAgentBuilderWorkflows(NetworkConnection connection, HttpRequest request, bool save)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            if (!save)
            {
                List<AgentBuilderWorkflow> workflows = _localAgentBuilderWorkflowCache.Values
                    .Where(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey))
                    .OrderByDescending(item => item.UpdatedUtc)
                    .ToList();
                return LocalAgentBuilderJson(request, new { ok = true, workflows });
            }

            try
            {
                JsonObject body = LocalAgentBuilderJsonObject(request);
                JsonNode source = body.TryGetPropertyValue("workflow", out JsonNode workflowNode) && workflowNode != null ? workflowNode : body;
                AgentBuilderWorkflow workflow = AgentBuilderJson.Deserialize<AgentBuilderWorkflow>(source.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderWorkflow();
                workflow.Normalize();
                if (_localAgentBuilderWorkflowCache.TryGetValue(workflow.Id, out AgentBuilderWorkflow existing) && !LocalAgentBuilderOwnedBy(existing.OwnerUserName, ownerKey))
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow belongs to another Workstation account." }, 403, "Forbidden");
                string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                workflow.OwnerUserName = ownerKey;
                workflow.CreatedUtc = string.IsNullOrWhiteSpace(existing?.CreatedUtc) ? now : existing.CreatedUtc;
                workflow.Revision = Math.Max(1, (existing?.Revision ?? 0) + 1);
                workflow.UpdatedUtc = now;
                AgentBuilderWorkflowValidationResult validation = AgentBuilderWorkflowEngine.ValidateWorkflow(workflow);
                SaveLocalAgentBuilderWorkflow(workflow);
                return LocalAgentBuilderJson(request, new { ok = true, workflow, validation, inputs = AgentBuilderWorkflowEngine.GetInputDefinitions(workflow) });
            }
            catch (Exception ex)
            {
                return LocalAgentBuilderJson(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
            }
        }

        private object HandleLocalAgentBuilderDelete(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            string id = request.PathVariables.Count > 0 ? request.PathVariables[0] : GetQueryParameter(request, "id");
            if (!_localAgentBuilderWorkflowCache.TryGetValue(id ?? "", out AgentBuilderWorkflow workflow))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
            if (!LocalAgentBuilderOwnedBy(workflow.OwnerUserName, ownerKey))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow belongs to another Workstation account." }, 403, "Forbidden");
            lock (_localAgentBuilderLock)
            {
                RemoveLocalAgentBuilderRows(_localAgentBuilderWorkflows, "Id", id);
                foreach (AgentBuilderApiDefinition api in _localAgentBuilderApiCache.Values.Where(api => api.WorkflowId.Equals(id, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    RemoveLocalAgentBuilderRows(_localAgentBuilderApis, "ApiName", api.ApiName);
                    _localAgentBuilderApiCache.TryRemove(api.ApiName, out _);
                }
                _localAgentBuilderWorkflowCache.TryRemove(id, out _);
                SaveChatSessionDataAndInvalidateCaches();
            }
            request.Context.StatusCodeNumber = 204;
            request.Context.ReasonPhrase = "No Content";
            return "";
        }

        private object HandleLocalAgentBuilderApis(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            return LocalAgentBuilderJson(request, new { ok = true, apis = _localAgentBuilderApiCache.Values.Where(item => LocalAgentBuilderOwnedBy(item.OwnerUserName, ownerKey)).OrderBy(item => item.ApiName).ToList() });
        }

        private object HandleLocalAgentBuilderPublish(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            try
            {
                JsonObject body = LocalAgentBuilderJsonObject(request);
                string workflowId = LocalAgentBuilderText(body, "workflowId", LocalAgentBuilderText(body, "id", ""));
                if (!_localAgentBuilderWorkflowCache.TryGetValue(workflowId, out AgentBuilderWorkflow workflow))
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
                if (!LocalAgentBuilderOwnedBy(workflow.OwnerUserName, ownerKey))
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow belongs to another Workstation account." }, 403, "Forbidden");
                AgentBuilderWorkflowValidationResult workflowValidation = AgentBuilderWorkflowEngine.ValidateWorkflow(workflow);
                if (!workflowValidation.Ok)
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow validation failed.", validation = workflowValidation }, 400, "Bad Request");
                string requestedName = LocalAgentBuilderText(body, "apiName", string.IsNullOrWhiteSpace(workflow.ApiName) ? workflow.Name : workflow.ApiName);
                AgentBuilderApiValidationResult validation = AgentBuilderWorkflowEngine.ValidateApiName(requestedName, _localAgentBuilderApiCache.Values.Where(item => item.WorkflowId != workflow.Id).Select(item => item.ApiName));
                if (!validation.Ok)
                    return LocalAgentBuilderJson(request, new { ok = false, error = validation.Error }, 409, "Conflict");
                string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var api = new AgentBuilderApiDefinition
                {
                    Id = "api_" + Guid.NewGuid().ToString("N"),
                    WorkflowId = workflow.Id,
                    OwnerUserName = ownerKey,
                    ApiName = validation.Slug,
                    Route = "/api/builder/" + validation.Slug,
                    Enabled = true,
                    RequireAuthentication = true,
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                workflow.ApiName = api.ApiName;
                workflow.ApiEnabled = true;
                workflow.UpdatedUtc = now;
                SaveLocalAgentBuilderWorkflow(workflow);
                SaveLocalAgentBuilderApi(api);
                string origin = ChatServerUrl.TrimEnd('/');
                return LocalAgentBuilderJson(request, new { ok = true, api, workflow, url = origin + api.Route, outputUrl = origin + "/Builder/output/" + api.ApiName });
            }
            catch (Exception ex)
            {
                return LocalAgentBuilderJson(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
            }
        }

        private object HandleLocalAgentBuilderRun(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            try
            {
                JsonObject body = LocalAgentBuilderJsonObject(request);
                AgentBuilderWorkflow workflow;
                if (body.TryGetPropertyValue("workflow", out JsonNode workflowNode) && workflowNode != null)
                    workflow = AgentBuilderJson.Deserialize<AgentBuilderWorkflow>(workflowNode.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderWorkflow();
                else if (!_localAgentBuilderWorkflowCache.TryGetValue(LocalAgentBuilderText(body, "workflowId", ""), out workflow))
                    return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
                workflow.Normalize();
                workflow.OwnerUserName = ownerKey;
                Dictionary<string, object> inputs = LocalAgentBuilderInputs(body);
                AgentBuilderExecutionResult result = ExecuteLocalAgentBuilderWorkflow(connection, request, workflow, inputs, "manual", cancellationToken).GetAwaiter().GetResult();
                return LocalAgentBuilderJson(request, new { ok = result.Ok, result, renderedOutput = AgentBuilderWorkflowEngine.RenderOutputSchemaTemplate(workflow, result) }, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");
            }
            catch (Exception ex)
            {
                return LocalAgentBuilderJson(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
            }
        }

        private object HandleLocalAgentBuilderRuns(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            string workflowId = GetQueryParameter(request, "workflowId") ?? "";
            List<AgentBuilderRun> runs;
            lock (_localAgentBuilderLock)
            {
                runs = _localAgentBuilderRuns.Rows.Select(LocalAgentBuilderRunFromRow)
                    .Where(run => LocalAgentBuilderOwnedBy(run.OwnerUserName, ownerKey))
                    .Where(run => string.IsNullOrWhiteSpace(workflowId) || run.WorkflowId.Equals(workflowId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(run => run.StartedUtc).Take(100).ToList();
            }
            return LocalAgentBuilderJson(request, new { ok = true, runs });
        }

        private object HandleLocalAgentBuilderReflectionCatalog(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out _, out object error))
                return error;
            int take = int.TryParse(GetQueryParameter(request, "take"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : 200;
            return LocalAgentBuilderJson(request, new AgentBuilderReflectionExecutor().GetCatalog(Math.Max(1, Math.Min(take, 1000))));
        }

        private object HandleLocalAgentBuilderReflectionTest(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out _, out object error))
                return error;
            try
            {
                AgentBuilderNode node = AgentBuilderJson.Deserialize<AgentBuilderNode>(request.Body ?? "{}") ?? new AgentBuilderNode();
                node.Normalize();
                object output = new AgentBuilderReflectionExecutor().ExecuteAsync(node, new Dictionary<string, object>(), new Dictionary<string, AgentBuilderNodeResult>(), cancellationToken).GetAwaiter().GetResult();
                return LocalAgentBuilderJson(request, new { ok = true, output });
            }
            catch (Exception ex)
            {
                return LocalAgentBuilderJson(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
            }
        }

        private object HandleLocalAgentBuilderPublishedApi(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            string apiName = request.PathVariables.Count > 0 ? AgentBuilderSlug.Normalize(request.PathVariables[0]) : "";
            if (!_localAgentBuilderApiCache.TryGetValue(apiName, out AgentBuilderApiDefinition api) || !api.Enabled || !LocalAgentBuilderOwnedBy(api.OwnerUserName, ownerKey))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Published Agent Builder API not found." }, 404, "Not Found");
            if (!_localAgentBuilderWorkflowCache.TryGetValue(api.WorkflowId, out AgentBuilderWorkflow workflow))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
            Dictionary<string, object> inputs = LocalAgentBuilderInputs(LocalAgentBuilderJsonObject(request));
            AgentBuilderExecutionResult result = ExecuteLocalAgentBuilderWorkflow(connection, request, workflow, inputs, "api", cancellationToken).GetAwaiter().GetResult();
            return LocalAgentBuilderJson(request, new { ok = result.Ok, result, renderedOutput = AgentBuilderWorkflowEngine.RenderOutputSchemaTemplate(workflow, result) }, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");
        }

        private object HandleLocalAgentBuilderOutput(NetworkConnection connection, HttpRequest request)
        {
            if (!TryAuthorizeLocalAgentBuilder(connection, request, out string ownerKey, out object error))
                return error;
            string apiName = request.PathVariables.Count > 0 ? AgentBuilderSlug.Normalize(request.PathVariables[0]) : "";
            if (!_localAgentBuilderApiCache.TryGetValue(apiName, out AgentBuilderApiDefinition api) || !LocalAgentBuilderOwnedBy(api.OwnerUserName, ownerKey))
                return LocalAgentBuilderJson(request, new { ok = false, error = "Published Agent Builder API not found." }, 404, "Not Found");
            request.Context.Response.ContentType = "text/html; charset=utf-8";
            string route = WebUtility.HtmlEncode(api.Route);
            string name = WebUtility.HtmlEncode(api.ApiName);
            return "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" + name + "</title><style>body{background:#0b1020;color:#eef6ff;font:15px Segoe UI;margin:0;padding:30px}main{max-width:760px;margin:auto;padding:24px;background:#111827;border:1px solid #334155;border-radius:12px}textarea{width:100%;min-height:160px;background:#071018;color:#fff}button{padding:10px 16px}</style></head><body><main><h1>" + name + "</h1><p>POST JSON inputs to <code>" + route + "</code>.</p><textarea id=\"input\">{}</textarea><p><button onclick=\"run()\">Run</button></p><pre id=\"output\"></pre><script>async function run(){const r=await fetch('" + route + "',{method:'POST',headers:{'Content-Type':'application/json'},body:document.getElementById('input').value});document.getElementById('output').textContent=await r.text()}</script></main></body></html>";
        }

        private async Task<AgentBuilderExecutionResult> ExecuteLocalAgentBuilderWorkflow(NetworkConnection connection, HttpRequest request, AgentBuilderWorkflow workflow, Dictionary<string, object> inputs, string triggerKind, CancellationToken cancellationToken)
        {
            var execution = new AgentBuilderExecutionRequest
            {
                Workflow = workflow,
                Inputs = inputs,
                TriggerKind = triggerKind,
                UserName = workflow.OwnerUserName,
                ReflectionExecutor = new AgentBuilderReflectionExecutor(),
                AgentRunner = new LocalAgentBuilderAgentRunner(this, connection, request)
            };
            AgentBuilderExecutionResult result = await new AgentBuilderWorkflowEngine().ExecuteAsync(execution, cancellationToken).ConfigureAwait(false);
            SaveLocalAgentBuilderRun(result, workflow);
            return result;
        }

        private sealed class LocalAgentBuilderAgentRunner : IAgentBuilderAgentRunner
        {
            private readonly HeirowLlm _proxy;
            private readonly NetworkConnection _connection;
            private readonly HttpRequest _sourceRequest;

            public LocalAgentBuilderAgentRunner(HeirowLlm proxy, NetworkConnection connection, HttpRequest sourceRequest)
            {
                _proxy = proxy;
                _connection = connection;
                _sourceRequest = sourceRequest;
            }

            public Task<object> RunAgentAsync(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults, string prompt, CancellationToken cancellationToken)
            {
                return Task.FromResult(_proxy.RunLocalAgentBuilderPrompt(_connection, _sourceRequest, node, prompt, cancellationToken));
            }
        }

        private object RunLocalAgentBuilderPrompt(NetworkConnection connection, HttpRequest sourceRequest, AgentBuilderNode node, string prompt, CancellationToken cancellationToken, JsonNode userContent = null, bool promptAsSystem = false, bool enableMemoryRecall = false)
        {
            string model = node.Config != null && node.Config.TryGetValue("model", out string configuredModel) && !string.IsNullOrWhiteSpace(configuredModel) ? configuredModel : "auto";
            JsonNode messageContent = userContent?.DeepClone() ?? JsonValue.Create(prompt);
            JsonArray messages = promptAsSystem
                ? new JsonArray(
                    new JsonObject { ["role"] = "system", ["content"] = prompt },
                    new JsonObject { ["role"] = "user", ["content"] = messageContent })
                : new JsonArray(new JsonObject { ["role"] = "user", ["content"] = messageContent });
            var payload = new JsonObject
            {
                ["prompt"] = prompt,
                ["model"] = model,
                ["selectedService"] = "chat",
                ["service"] = "chat",
                ["messages"] = messages
            };
            if (enableMemoryRecall) payload["memoryRecall"] = true;
            if (node.Config != null)
            {
                if (node.Config.TryGetValue("temperature", out string temperature) && double.TryParse(temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTemperature)) payload["temperature"] = Math.Clamp(parsedTemperature, 0, 2);
                if (node.Config.TryGetValue("top_p", out string topP) && double.TryParse(topP, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTopP)) payload["top_p"] = Math.Clamp(parsedTopP, 0, 1);
                if (node.Config.TryGetValue("max_tokens", out string maxTokens) && int.TryParse(maxTokens, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxTokens) && parsedMaxTokens > 0) payload["max_tokens"] = Math.Clamp(parsedMaxTokens, 64, 32768);
                if (node.Config.TryGetValue("reasoningLevel", out string reasoningLevel) && !string.IsNullOrWhiteSpace(reasoningLevel)) payload["reasoningLevel"] = reasoningLevel;
            }
            var context = new HttpContext { Connection = connection, cancellationToken = cancellationToken };
            var request = new HttpRequest
            {
                Context = context,
                Method = "POST",
                Path = "/api/chat",
                Headers = new Dictionary<string, string>(sourceRequest?.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                Body = payload.ToJsonString()
            };
            context.Request = request;
            string response = HandleChatUiRequest(request, cancellationToken);
            try
            {
                JsonNode parsed = JsonNode.Parse(response);
                if (parsed is JsonObject obj && obj.TryGetPropertyValue("ok", out JsonNode okNode) && okNode != null && bool.TryParse(okNode.ToString(), out bool ok) && !ok)
                    throw new InvalidOperationException(obj["error"]?.ToString() ?? "Agent Builder model request failed.");
                return parsed ?? response;
            }
            catch (JsonException)
            {
                return response;
            }
        }

        private void SaveLocalAgentBuilderWorkflow(AgentBuilderWorkflow workflow)
        {
            lock (_localAgentBuilderLock)
            {
                UpsertLocalAgentBuilderRow(_localAgentBuilderWorkflows, "Id", workflow.Id, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = workflow.Id, ["OwnerUserName"] = workflow.OwnerUserName, ["Name"] = workflow.Name, ["Description"] = workflow.Description,
                    ["NodesJson"] = AgentBuilderJson.Serialize(workflow.Nodes), ["EdgesJson"] = AgentBuilderJson.Serialize(workflow.Edges), ["VariablesJson"] = AgentBuilderJson.Serialize(workflow.Variables),
                    ["ApiName"] = workflow.ApiName, ["ApiEnabled"] = workflow.ApiEnabled ? "true" : "false", ["Enabled"] = workflow.Enabled ? "true" : "false",
                    ["CreatedUtc"] = workflow.CreatedUtc, ["UpdatedUtc"] = workflow.UpdatedUtc,
                    ["SchemaVersion"] = workflow.SchemaVersion.ToString(CultureInfo.InvariantCulture), ["Revision"] = workflow.Revision.ToString(CultureInfo.InvariantCulture),
                    ["PresetId"] = workflow.PresetId, ["PresetVersion"] = workflow.PresetVersion.ToString(CultureInfo.InvariantCulture), ["BasePresetHash"] = workflow.BasePresetHash,
                    ["ApplicationJson"] = AgentBuilderJson.Serialize(workflow.Application)
                });
                _localAgentBuilderWorkflowCache[workflow.Id] = workflow;
                SaveChatSessionDataAndInvalidateCaches();
            }
        }

        private void SaveLocalAgentBuilderApi(AgentBuilderApiDefinition api)
        {
            lock (_localAgentBuilderLock)
            {
                UpsertLocalAgentBuilderRow(_localAgentBuilderApis, "ApiName", api.ApiName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = api.Id, ["WorkflowId"] = api.WorkflowId, ["OwnerUserName"] = api.OwnerUserName, ["ApiName"] = api.ApiName, ["Route"] = api.Route,
                    ["Enabled"] = api.Enabled ? "true" : "false", ["RequireAuthentication"] = api.RequireAuthentication ? "true" : "false", ["CreatedUtc"] = api.CreatedUtc, ["UpdatedUtc"] = api.UpdatedUtc
                });
                _localAgentBuilderApiCache[api.ApiName] = api;
                SaveChatSessionDataAndInvalidateCaches();
            }
        }

        private void SaveLocalAgentBuilderRun(AgentBuilderExecutionResult result, AgentBuilderWorkflow workflow)
        {
            lock (_localAgentBuilderLock)
            {
                _localAgentBuilderRuns.Rows.Add(CreateLocalAgentBuilderRow(_localAgentBuilderRuns, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Id"] = result.RunId, ["WorkflowId"] = workflow.Id, ["ApiName"] = workflow.ApiName, ["OwnerUserName"] = workflow.OwnerUserName,
                    ["TriggerKind"] = result.TriggerKind, ["Status"] = result.Status, ["InputJson"] = AgentBuilderJson.SafeSerialize(result.Inputs), ["OutputJson"] = AgentBuilderJson.SafeSerialize(result.Output),
                    ["Error"] = result.Error, ["NodeResultsJson"] = AgentBuilderJson.SafeSerialize(result.NodeResults), ["DurationMs"] = result.DurationMs.ToString(CultureInfo.InvariantCulture),
                    ["StartedUtc"] = result.StartedUtc, ["CompletedUtc"] = result.CompletedUtc
                }));
                while (_localAgentBuilderRuns.Rows.Count > 1000)
                    _localAgentBuilderRuns.Rows.RemoveAt(0);
                SaveChatSessionDataAndInvalidateCaches();
            }
        }

        private AgentBuilderWorkflow LocalAgentBuilderWorkflowFromRow(object[] row)
        {
            var workflow = new AgentBuilderWorkflow
            {
                Id = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "Id"), OwnerUserName = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "OwnerUserName"),
                Name = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "Name"), Description = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "Description"),
                ApiName = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "ApiName"), ApiEnabled = LocalAgentBuilderBool(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "ApiEnabled")),
                Enabled = LocalAgentBuilderBool(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "Enabled"), true), CreatedUtc = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "CreatedUtc"), UpdatedUtc = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "UpdatedUtc"),
                SchemaVersion = int.TryParse(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "SchemaVersion"), out int schemaVersion) ? schemaVersion : 2,
                Revision = int.TryParse(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "Revision"), out int revision) ? revision : 1,
                PresetId = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "PresetId"), PresetVersion = int.TryParse(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "PresetVersion"), out int presetVersion) ? presetVersion : 0,
                BasePresetHash = LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "BasePresetHash")
            };
            workflow.Nodes = AgentBuilderJson.Deserialize<List<AgentBuilderNode>>(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "NodesJson")) ?? new List<AgentBuilderNode>();
            workflow.Edges = AgentBuilderJson.Deserialize<List<AgentBuilderEdge>>(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "EdgesJson")) ?? new List<AgentBuilderEdge>();
            workflow.Variables = AgentBuilderJson.Deserialize<Dictionary<string, string>>(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "VariablesJson")) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            workflow.Application = AgentBuilderJson.Deserialize<AgentBuilderApplicationDefinition>(LocalAgentBuilderCell(_localAgentBuilderWorkflows, row, "ApplicationJson")) ?? new AgentBuilderApplicationDefinition();
            workflow.Normalize();
            return workflow;
        }

        private AgentBuilderApiDefinition LocalAgentBuilderApiFromRow(object[] row) => new AgentBuilderApiDefinition
        {
            Id = LocalAgentBuilderCell(_localAgentBuilderApis, row, "Id"), WorkflowId = LocalAgentBuilderCell(_localAgentBuilderApis, row, "WorkflowId"), OwnerUserName = LocalAgentBuilderCell(_localAgentBuilderApis, row, "OwnerUserName"),
            ApiName = LocalAgentBuilderCell(_localAgentBuilderApis, row, "ApiName"), Route = LocalAgentBuilderCell(_localAgentBuilderApis, row, "Route"), Enabled = LocalAgentBuilderBool(LocalAgentBuilderCell(_localAgentBuilderApis, row, "Enabled"), true),
            RequireAuthentication = LocalAgentBuilderBool(LocalAgentBuilderCell(_localAgentBuilderApis, row, "RequireAuthentication"), true), CreatedUtc = LocalAgentBuilderCell(_localAgentBuilderApis, row, "CreatedUtc"), UpdatedUtc = LocalAgentBuilderCell(_localAgentBuilderApis, row, "UpdatedUtc")
        };

        private AgentBuilderRun LocalAgentBuilderRunFromRow(object[] row) => new AgentBuilderRun
        {
            Id = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "Id"), WorkflowId = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "WorkflowId"), ApiName = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "ApiName"), OwnerUserName = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "OwnerUserName"),
            TriggerKind = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "TriggerKind"), Status = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "Status"), InputJson = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "InputJson"), OutputJson = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "OutputJson"),
            Error = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "Error"), NodeResultsJson = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "NodeResultsJson"), DurationMs = long.TryParse(LocalAgentBuilderCell(_localAgentBuilderRuns, row, "DurationMs"), out long duration) ? duration : 0,
            StartedUtc = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "StartedUtc"), CompletedUtc = LocalAgentBuilderCell(_localAgentBuilderRuns, row, "CompletedUtc")
        };

        private static Dictionary<string, object> LocalAgentBuilderInputs(JsonObject body)
        {
            JsonObject source = body != null && body.TryGetPropertyValue("inputs", out JsonNode nested) && nested is JsonObject nestedObject ? nestedObject : body ?? new JsonObject();
            var inputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonNode> pair in source)
            {
                if (pair.Key.Equals("workflow", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("workflowId", StringComparison.OrdinalIgnoreCase))
                    continue;
                inputs[pair.Key] = pair.Value is JsonValue value && value.TryGetValue<string>(out string text) ? text : pair.Value?.Deserialize<object>(AgentBuilderJson.Options);
            }
            return inputs;
        }

        private static JsonObject LocalAgentBuilderJsonObject(HttpRequest request)
        {
            if (string.IsNullOrWhiteSpace(request?.Body))
                return new JsonObject();
            return JsonNode.Parse(request.Body) as JsonObject ?? new JsonObject();
        }

        private static string LocalAgentBuilderText(JsonObject body, string name, string fallback)
        {
            if (body == null || !body.TryGetPropertyValue(name, out JsonNode node) || node == null)
                return fallback;
            string value = node.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string LocalAgentBuilderOwnerName(string ownerKey) => (ownerKey ?? "").StartsWith("webauth:", StringComparison.OrdinalIgnoreCase) ? ownerKey.Substring("webauth:".Length) : ownerKey;
        private static bool LocalAgentBuilderOwnedBy(string storedOwner, string ownerKey) => string.Equals(storedOwner ?? "", ownerKey ?? "", StringComparison.OrdinalIgnoreCase);
        private static bool LocalAgentBuilderBool(string value, bool fallback = false) => bool.TryParse(value, out bool parsed) ? parsed : fallback;

        private static string LocalAgentBuilderJson(HttpRequest request, object value, int statusCode = 200, string reasonPhrase = "OK")
        {
            request.Context.StatusCodeNumber = statusCode;
            request.Context.ReasonPhrase = reasonPhrase;
            request.Context.Response.ContentType = "application/json; charset=utf-8";
            return AgentBuilderJson.Serialize(value);
        }

        private static void UpsertLocalAgentBuilderRow(Table table, string keyColumn, string keyValue, Dictionary<string, string> values)
        {
            for (int i = 0; i < table.Rows.Count; i++)
            {
                if (!LocalAgentBuilderCell(table, table.Rows[i], keyColumn).Equals(keyValue ?? "", StringComparison.OrdinalIgnoreCase))
                    continue;
                table.Rows[i] = CreateLocalAgentBuilderRow(table, values);
                return;
            }
            table.Rows.Add(CreateLocalAgentBuilderRow(table, values));
        }

        private static object[] CreateLocalAgentBuilderRow(Table table, Dictionary<string, string> values)
        {
            var row = new object[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
                row[i] = values.TryGetValue(table.Columns[i].Name, out string value) ? value ?? "" : "";
            return row;
        }

        private static void RemoveLocalAgentBuilderRows(Table table, string keyColumn, string keyValue)
        {
            for (int i = table.Rows.Count - 1; i >= 0; i--)
                if (LocalAgentBuilderCell(table, table.Rows[i], keyColumn).Equals(keyValue ?? "", StringComparison.OrdinalIgnoreCase))
                    table.Rows.RemoveAt(i);
        }

        private static string LocalAgentBuilderCell(Table table, object[] row, string columnName)
        {
            for (int i = 0; i < table.Columns.Count && i < row.Length; i++)
                if (table.Columns[i].Name.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                    return row[i]?.ToString() ?? "";
            return "";
        }
    }
}
