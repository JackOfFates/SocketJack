using SocketJack.Net;
using SocketJack.Net.AgentBuilder;
using SocketJack.Net.Database;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SocketJack.MagicMasterList;

internal sealed partial class MasterListServerHost {
    private const string AgentBuilderDatabaseName = "AgentBuilder";
    private const string AgentBuilderWorkflowsTableName = "Workflows";
    private const string AgentBuilderApisTableName = "CustomApis";
    private const string AgentBuilderRunsTableName = "Runs";
    private const string AgentBuilderSchedulesTableName = "Schedules";
    private const string AgentBuilderReflectionObjectsTableName = "NamedReflectionObjects";
    private const string AgentBuilderAuditTableName = "AuditEvents";

    private static readonly string[] AgentBuilderWorkflowColumns = {
        "Id", "OwnerUserName", "Name", "Description", "NodesJson", "EdgesJson", "VariablesJson",
        "ApiName", "ApiEnabled", "Enabled", "CreatedUtc", "UpdatedUtc"
    };

    private static readonly string[] AgentBuilderApiColumns = {
        "Id", "WorkflowId", "OwnerUserName", "ApiName", "Route", "Enabled", "RequireAuthentication", "CreatedUtc", "UpdatedUtc"
    };

    private static readonly string[] AgentBuilderRunColumns = {
        "Id", "WorkflowId", "ApiName", "OwnerUserName", "TriggerKind", "Status", "InputJson", "OutputJson",
        "Error", "NodeResultsJson", "DurationMs", "StartedUtc", "CompletedUtc"
    };

    private static readonly string[] AgentBuilderScheduleColumns = {
        "Id", "WorkflowId", "OwnerUserName", "Enabled", "PreventOverlap", "Kind", "IntervalSeconds", "TimeOfDay",
        "TimeZone", "CriteriaJson", "LastRunUtc", "NextRunUtc", "CreatedUtc", "UpdatedUtc"
    };

    private static readonly string[] AgentBuilderReflectionObjectColumns = {
        "Id", "WorkflowId", "OwnerUserName", "ObjectName", "TypeName", "ConfigJson", "CreatedUtc", "UpdatedUtc"
    };

    private static readonly string[] AgentBuilderAuditColumns = {
        "Id", "WorkflowId", "RunId", "OwnerUserName", "EventType", "Message", "PayloadJson", "CreatedUtc"
    };

    private readonly object _agentBuilderSync = new();
    private Table _agentBuilderWorkflowsTable = null!;
    private Table _agentBuilderApisTable = null!;
    private Table _agentBuilderRunsTable = null!;
    private Table _agentBuilderSchedulesTable = null!;
    private Table _agentBuilderReflectionObjectsTable = null!;
    private Table _agentBuilderAuditTable = null!;
    private readonly ConcurrentDictionary<string, AgentBuilderWorkflow> _agentBuilderWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentBuilderApiDefinition> _agentBuilderApis = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentBuilderSchedule> _agentBuilderSchedules = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _agentBuilderRunningWorkflows = new(StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _agentBuilderScheduleTimer;
    private int _agentBuilderScheduleLoopActive;

    private void EnsureAgentBuilderStorage() {
        lock (_agentBuilderSync) {
            _agentBuilderWorkflowsTable = EnsureAgentBuilderTable(AgentBuilderWorkflowsTableName, AgentBuilderWorkflowColumns);
            _agentBuilderApisTable = EnsureAgentBuilderTable(AgentBuilderApisTableName, AgentBuilderApiColumns);
            _agentBuilderRunsTable = EnsureAgentBuilderTable(AgentBuilderRunsTableName, AgentBuilderRunColumns);
            _agentBuilderSchedulesTable = EnsureAgentBuilderTable(AgentBuilderSchedulesTableName, AgentBuilderScheduleColumns);
            _agentBuilderReflectionObjectsTable = EnsureAgentBuilderTable(AgentBuilderReflectionObjectsTableName, AgentBuilderReflectionObjectColumns);
            _agentBuilderAuditTable = EnsureAgentBuilderTable(AgentBuilderAuditTableName, AgentBuilderAuditColumns);
            LoadAgentBuilderCachesLocked();
        }

        StartAgentBuilderScheduler();
    }

    private Table EnsureAgentBuilderTable(string tableName, IReadOnlyList<string> columns) {
        Database database = _dataServer.Databases.GetOrAdd(AgentBuilderDatabaseName, name => new Database(name));
        Table table = database.Tables.GetOrAdd(tableName, name => new Table(name));
        bool changed = EnsureColumns(table, columns);
        if (changed)
            _dataServer.Save();
        return table;
    }

    private void LoadAgentBuilderCachesLocked() {
        _agentBuilderWorkflows.Clear();
        foreach (object[] row in _agentBuilderWorkflowsTable.Rows) {
            AgentBuilderWorkflow workflow = AgentBuilderWorkflowFromRow(row);
            if (!string.IsNullOrWhiteSpace(workflow.Id))
                _agentBuilderWorkflows[workflow.Id] = workflow;
        }

        _agentBuilderApis.Clear();
        foreach (object[] row in _agentBuilderApisTable.Rows) {
            AgentBuilderApiDefinition api = AgentBuilderApiFromRow(row);
            if (!string.IsNullOrWhiteSpace(api.ApiName))
                _agentBuilderApis[api.ApiName] = api;
        }

        _agentBuilderSchedules.Clear();
        foreach (object[] row in _agentBuilderSchedulesTable.Rows) {
            AgentBuilderSchedule schedule = AgentBuilderScheduleFromRow(row);
            if (!string.IsNullOrWhiteSpace(schedule.Id))
                _agentBuilderSchedules[schedule.Id] = schedule;
        }
    }

    private void MapAgentBuilderRoutes(MutableTcpServer server) {
        foreach (string builderPath in new[] { "/Builder", "/Builder/", "/builder", "/builder/" }) {
            server.Map("GET", builderPath, (connection, request, cancellationToken) => {
                return Html(request, BuildAgentBuilderHtml());
            });
            server.Map("OPTIONS", builderPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string outputPath in new[] { "/Builder/output/*", "/builder/output/*" }) {
            server.Map("GET", outputPath, (connection, request, cancellationToken) => HandleAgentBuilderOutputPage(request));
            server.Map("OPTIONS", outputPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string sessionPath in new[] { "/api/agentbuilder/session", "/api/agentbuilder/session/" }) {
            server.Map("GET", sessionPath, (connection, request, cancellationToken) => HandleAgentBuilderSession(request));
            server.Map("OPTIONS", sessionPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string workflowsPath in new[] { "/api/agentbuilder/workflows", "/api/agentbuilder/workflows/" }) {
            server.Map("GET", workflowsPath, (connection, request, cancellationToken) => HandleAgentBuilderWorkflows(request, save: false));
            server.Map("POST", workflowsPath, (connection, request, cancellationToken) => HandleAgentBuilderWorkflows(request, save: true));
            server.Map("OPTIONS", workflowsPath, (connection, request, cancellationToken) => NoContent(request));
        }

        server.Map("DELETE", "/api/agentbuilder/workflows/*", (connection, request, cancellationToken) => HandleAgentBuilderDeleteWorkflow(request));

        foreach (string apisPath in new[] { "/api/agentbuilder/apis", "/api/agentbuilder/apis/" }) {
            server.Map("GET", apisPath, (connection, request, cancellationToken) => HandleAgentBuilderApis(request, save: false));
            server.Map("POST", apisPath, (connection, request, cancellationToken) => HandleAgentBuilderApis(request, save: true));
            server.Map("OPTIONS", apisPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string publishPath in new[] { "/api/agentbuilder/apis/publish", "/api/agentbuilder/apis/publish/" }) {
            server.Map("POST", publishPath, (connection, request, cancellationToken) => HandleAgentBuilderPublish(request));
            server.Map("OPTIONS", publishPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string runPath in new[] { "/api/agentbuilder/run", "/api/agentbuilder/run/" }) {
            server.Map("POST", runPath, (connection, request, cancellationToken) => HandleAgentBuilderRun(connection, request, cancellationToken));
            server.Map("OPTIONS", runPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string runsPath in new[] { "/api/agentbuilder/runs", "/api/agentbuilder/runs/" }) {
            server.Map("GET", runsPath, (connection, request, cancellationToken) => HandleAgentBuilderRuns(request));
            server.Map("OPTIONS", runsPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string schedulesPath in new[] { "/api/agentbuilder/schedules", "/api/agentbuilder/schedules/" }) {
            server.Map("GET", schedulesPath, (connection, request, cancellationToken) => HandleAgentBuilderSchedules(request, save: false));
            server.Map("POST", schedulesPath, (connection, request, cancellationToken) => HandleAgentBuilderSchedules(request, save: true));
            server.Map("OPTIONS", schedulesPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string actionPath in new[] { "/api/agentbuilder/schedules/action", "/api/agentbuilder/schedules/action/" }) {
            server.Map("POST", actionPath, (connection, request, cancellationToken) => HandleAgentBuilderScheduleAction(connection, request, cancellationToken));
            server.Map("OPTIONS", actionPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string catalogPath in new[] { "/api/agentbuilder/reflection/catalog", "/api/agentbuilder/reflection/catalog/" }) {
            server.Map("GET", catalogPath, (connection, request, cancellationToken) => HandleAgentBuilderReflectionCatalog(request));
            server.Map("OPTIONS", catalogPath, (connection, request, cancellationToken) => NoContent(request));
        }

        foreach (string testPath in new[] { "/api/agentbuilder/reflection/test", "/api/agentbuilder/reflection/test/" }) {
            server.Map("POST", testPath, (connection, request, cancellationToken) => HandleAgentBuilderReflectionTest(request, cancellationToken));
            server.Map("OPTIONS", testPath, (connection, request, cancellationToken) => NoContent(request));
        }

        server.Map("GET", "/api/*", (connection, request, cancellationToken) => HandleAgentBuilderDynamicApi(connection, request, cancellationToken));
        server.Map("POST", "/api/*", (connection, request, cancellationToken) => HandleAgentBuilderDynamicApi(connection, request, cancellationToken));
        server.Map("OPTIONS", "/api/*", (connection, request, cancellationToken) => NoContent(request));
    }

    private object HandleAgentBuilderSession(HttpRequest request) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        return AgentBuilderJsonResponse(request, new {
            ok = true,
            authenticated = true,
            username = account.UserName,
            isAdministrator = account.IsAdministrator,
            database = AgentBuilderDatabaseName,
            workflowCount = _agentBuilderWorkflows.Values.Count(item => IsAgentBuilderOwner(item.OwnerUserName, account)),
            apiCount = _agentBuilderApis.Values.Count(item => IsAgentBuilderOwner(item.OwnerUserName, account)),
            scheduleCount = _agentBuilderSchedules.Values.Count(item => IsAgentBuilderOwner(item.OwnerUserName, account))
        });
    }

    private object HandleAgentBuilderWorkflows(HttpRequest request, bool save) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        if (!save) {
            List<AgentBuilderWorkflow> workflows = _agentBuilderWorkflows.Values
                .Where(item => IsAgentBuilderOwner(item.OwnerUserName, account))
                .OrderByDescending(item => item.UpdatedUtc)
                .ToList();
            return AgentBuilderJsonResponse(request, new {
                ok = true,
                workflows,
                inputs = workflows.ToDictionary(item => item.Id, item => AgentBuilderWorkflowEngine.GetInputDefinitions(item), StringComparer.OrdinalIgnoreCase)
            });
        }

        try {
            AgentBuilderWorkflow workflow = ParseAgentBuilderWorkflowBody(request);
            workflow.OwnerUserName = account.UserName;
            workflow.Normalize();
            AgentBuilderWorkflowValidationResult validation = AgentBuilderWorkflowEngine.ValidateWorkflow(workflow);
            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            AgentBuilderWorkflow? existing = _agentBuilderWorkflows.TryGetValue(workflow.Id, out AgentBuilderWorkflow? found) ? found : null;
            if (existing != null && !IsAgentBuilderOwner(existing.OwnerUserName, account))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow belongs to another user." }, 403, "Forbidden");

            workflow.CreatedUtc = string.IsNullOrWhiteSpace(existing?.CreatedUtc) ? now : existing.CreatedUtc;
            workflow.UpdatedUtc = now;
            UpsertAgentBuilderWorkflow(workflow);
            AppendAgentBuilderAudit(workflow.Id, "", account.UserName, "workflow.saved", "Workflow saved.", new { workflow.Id, workflow.Name });

            return AgentBuilderJsonResponse(request, new {
                ok = true,
                workflow,
                validation,
                inputs = AgentBuilderWorkflowEngine.GetInputDefinitions(workflow)
            });
        } catch (Exception ex) {
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderDeleteWorkflow(HttpRequest request) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        string id = request.PathVariables.Count > 0 ? request.PathVariables[0] : GetQuery(request, "id");
        if (string.IsNullOrWhiteSpace(id))
            return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow id is required." }, 400, "Bad Request");

        lock (_agentBuilderSync) {
            if (!_agentBuilderWorkflows.TryGetValue(id, out AgentBuilderWorkflow? workflow))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
            if (!IsAgentBuilderOwner(workflow.OwnerUserName, account))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow belongs to another user." }, 403, "Forbidden");

            RemoveAgentBuilderRows(_agentBuilderWorkflowsTable, "Id", id);
            _agentBuilderWorkflows.TryRemove(id, out _);
            _dataServer.Save();
        }

        AppendAgentBuilderAudit(id, "", account.UserName, "workflow.deleted", "Workflow deleted.", new { id });
        return NoContent(request);
    }

    private object HandleAgentBuilderApis(HttpRequest request, bool save) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        if (!save) {
            return AgentBuilderJsonResponse(request, new {
                ok = true,
                apis = _agentBuilderApis.Values
                    .Where(item => IsAgentBuilderOwner(item.OwnerUserName, account))
                    .OrderBy(item => item.ApiName)
                    .ToList()
            });
        }

        return HandleAgentBuilderPublish(request);
    }

    private object HandleAgentBuilderPublish(HttpRequest request) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        try {
            JsonObject body = ParseAgentBuilderJsonObject(request);
            string workflowId = AgentBuilderJsonText(body, "workflowId", AgentBuilderJsonText(body, "id", ""));
            if (string.IsNullOrWhiteSpace(workflowId))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "workflowId is required." }, 400, "Bad Request");
            if (!_agentBuilderWorkflows.TryGetValue(workflowId, out AgentBuilderWorkflow? workflow))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
            if (!IsAgentBuilderOwner(workflow.OwnerUserName, account))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow belongs to another user." }, 403, "Forbidden");

            AgentBuilderWorkflowValidationResult workflowValidation = AgentBuilderWorkflowEngine.ValidateWorkflow(workflow);
            if (!workflowValidation.Ok)
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow validation failed.", validation = workflowValidation }, 400, "Bad Request");

            string requestedName = FirstNonEmpty(
                AgentBuilderJsonText(body, "apiName", ""),
                AgentBuilderJsonText(body, "name", ""),
                workflow.ApiName,
                workflow.Name);
            IEnumerable<string> existingNames = _agentBuilderApis.Values
                .Where(api => !string.Equals(api.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
                .Select(api => api.ApiName);
            AgentBuilderApiValidationResult validation = AgentBuilderWorkflowEngine.ValidateApiName(requestedName, existingNames);
            if (!validation.Ok || AgentBuilderRouteConflicts(validation.Slug))
                return AgentBuilderJsonResponse(request, new {
                    ok = false,
                    apiName = validation.Slug,
                    error = validation.Ok ? "API name conflicts with an existing SocketJack API route." : validation.Error
                }, 409, "Conflict");

            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            AgentBuilderApiDefinition api = _agentBuilderApis.TryGetValue(validation.Slug, out AgentBuilderApiDefinition? existingApi)
                ? existingApi
                : new AgentBuilderApiDefinition { Id = "api_" + Guid.NewGuid().ToString("N"), CreatedUtc = now };
            api.WorkflowId = workflowId;
            api.OwnerUserName = account.UserName;
            api.ApiName = validation.Slug;
            api.Route = "/api/" + validation.Slug;
            api.Enabled = true;
            api.RequireAuthentication = true;
            api.UpdatedUtc = now;

            workflow.ApiName = validation.Slug;
            workflow.ApiEnabled = true;
            workflow.UpdatedUtc = now;

            UpsertAgentBuilderWorkflow(workflow);
            UpsertAgentBuilderApi(api);
            AppendAgentBuilderAudit(workflow.Id, "", account.UserName, "api.published", "Custom API published.", new { api.ApiName, api.Route });

            return AgentBuilderJsonResponse(request, new {
                ok = true,
                api,
                workflow,
                url = BuildAbsoluteWebsiteUrl(api.Route),
                outputUrl = BuildAbsoluteWebsiteUrl("/Builder/output/" + api.ApiName)
            });
        } catch (Exception ex) {
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderRun(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        try {
            JsonObject body = ParseAgentBuilderJsonObject(request);
            AgentBuilderWorkflow workflow;
            if (body.TryGetPropertyValue("workflow", out JsonNode? workflowNode) && workflowNode != null) {
                workflow = AgentBuilderJson.Deserialize<AgentBuilderWorkflow>(workflowNode.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderWorkflow();
                workflow.OwnerUserName = account.UserName;
                workflow.Normalize();
            } else {
                string workflowId = FirstNonEmpty(AgentBuilderJsonText(body, "workflowId", ""), GetQuery(request, "workflowId"));
                if (string.IsNullOrWhiteSpace(workflowId) || !_agentBuilderWorkflows.TryGetValue(workflowId, out workflow!))
                    return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
                if (!IsAgentBuilderOwner(workflow.OwnerUserName, account))
                    return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow belongs to another user." }, 403, "Forbidden");
            }

            Dictionary<string, object> inputs = ExtractAgentBuilderInputs(request);
            AgentBuilderExecutionResult result = ExecuteAgentBuilderWorkflowAsync(connection, request, workflow, inputs, "manual", workflow.ApiName, account, cancellationToken)
                .GetAwaiter()
                .GetResult();
            return AgentBuilderJsonResponse(request, new {
                ok = result.Ok,
                result,
                renderedOutput = AgentBuilderWorkflowEngine.RenderOutputSchemaTemplate(workflow, result),
                runs = ListAgentBuilderRunsFor(account, workflow.Id, 20)
            }, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");
        } catch (Exception ex) {
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderRuns(HttpRequest request) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        string workflowId = FirstNonEmpty(GetQuery(request, "workflowId"), GetQuery(request, "id"));
        return AgentBuilderJsonResponse(request, new {
            ok = true,
            runs = ListAgentBuilderRunsFor(account, workflowId, 100)
        });
    }

    private object HandleAgentBuilderSchedules(HttpRequest request, bool save) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        if (!save) {
            return AgentBuilderJsonResponse(request, new {
                ok = true,
                schedules = _agentBuilderSchedules.Values
                    .Where(item => IsAgentBuilderOwner(item.OwnerUserName, account))
                    .OrderBy(item => item.NextRunUtc)
                    .ToList()
            });
        }

        try {
            JsonObject body = ParseAgentBuilderJsonObject(request);
            AgentBuilderSchedule schedule = AgentBuilderJson.Deserialize<AgentBuilderSchedule>(body.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderSchedule();
            if (string.IsNullOrWhiteSpace(schedule.Id))
                schedule.Id = "schedule_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(schedule.WorkflowId))
                schedule.WorkflowId = AgentBuilderJsonText(body, "workflowId", "");
            if (!_agentBuilderWorkflows.TryGetValue(schedule.WorkflowId, out AgentBuilderWorkflow? workflow))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
            if (!IsAgentBuilderOwner(workflow.OwnerUserName, account))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow belongs to another user." }, 403, "Forbidden");

            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            AgentBuilderSchedule? existing = _agentBuilderSchedules.TryGetValue(schedule.Id, out AgentBuilderSchedule? found) ? found : null;
            schedule.OwnerUserName = account.UserName;
            schedule.CreatedUtc = string.IsNullOrWhiteSpace(existing?.CreatedUtc) ? now : existing.CreatedUtc;
            schedule.UpdatedUtc = now;
            if (string.IsNullOrWhiteSpace(schedule.NextRunUtc))
                schedule.NextRunUtc = AgentBuilderWorkflowEngine.CalculateNextRunUtc(schedule, DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
            UpsertAgentBuilderSchedule(schedule);
            AppendAgentBuilderAudit(schedule.WorkflowId, "", account.UserName, "schedule.saved", "Schedule saved.", new { schedule.Id, schedule.Kind, schedule.NextRunUtc });

            return AgentBuilderJsonResponse(request, new { ok = true, schedule });
        } catch (Exception ex) {
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderScheduleAction(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        try {
            JsonObject body = ParseAgentBuilderJsonObject(request);
            string id = FirstNonEmpty(AgentBuilderJsonText(body, "id", ""), AgentBuilderJsonText(body, "scheduleId", ""));
            string action = FirstNonEmpty(AgentBuilderJsonText(body, "action", ""), "toggle").ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(id) || !_agentBuilderSchedules.TryGetValue(id, out AgentBuilderSchedule? schedule))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Schedule not found." }, 404, "Not Found");
            if (!IsAgentBuilderOwner(schedule.OwnerUserName, account))
                return AgentBuilderJsonResponse(request, new { ok = false, error = "Schedule belongs to another user." }, 403, "Forbidden");

            if (action == "delete") {
                lock (_agentBuilderSync) {
                    RemoveAgentBuilderRows(_agentBuilderSchedulesTable, "Id", id);
                    _agentBuilderSchedules.TryRemove(id, out _);
                    _dataServer.Save();
                }
                return NoContent(request);
            }

            if (action == "run") {
                if (!_agentBuilderWorkflows.TryGetValue(schedule.WorkflowId, out AgentBuilderWorkflow? workflow))
                    return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found." }, 404, "Not Found");
                AgentBuilderExecutionResult run = ExecuteAgentBuilderWorkflowAsync(connection, request, workflow, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), "schedule.manual", workflow.ApiName, account, cancellationToken)
                    .GetAwaiter()
                    .GetResult();
                return AgentBuilderJsonResponse(request, new { ok = run.Ok, result = run }, run.Ok ? 200 : 400, run.Ok ? "OK" : "Bad Request");
            }

            schedule.Enabled = action switch {
                "enable" => true,
                "disable" => false,
                "stop" => false,
                "start" => true,
                _ => !schedule.Enabled
            };
            schedule.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            UpsertAgentBuilderSchedule(schedule);
            return AgentBuilderJsonResponse(request, new { ok = true, schedule });
        } catch (Exception ex) {
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderReflectionCatalog(HttpRequest request) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        int take = int.TryParse(FirstNonEmpty(GetQuery(request, "take"), "200"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 200;
        return AgentBuilderJsonResponse(request, new AgentBuilderReflectionExecutor().GetCatalog(take));
    }

    private object HandleAgentBuilderReflectionTest(HttpRequest request, CancellationToken cancellationToken) {
        AccountRecord? account = RequireAgentBuilderAccount(request, out object unauthorized);
        if (account == null)
            return unauthorized;

        try {
            JsonObject body = ParseAgentBuilderJsonObject(request);
            AgentBuilderNode node = AgentBuilderJson.Deserialize<AgentBuilderNode>(body.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderNode();
            node.Normalize();
            var executor = new AgentBuilderReflectionExecutor();
            object output = executor.ExecuteAsync(node, ExtractAgentBuilderInputs(request), new Dictionary<string, AgentBuilderNodeResult>(StringComparer.OrdinalIgnoreCase), cancellationToken)
                .GetAwaiter()
                .GetResult();
            AppendAgentBuilderAudit("", "", account.UserName, "reflection.test", "Reflection test executed.", new { node.Id, node.Type, node.Name });
            return AgentBuilderJsonResponse(request, new { ok = true, output });
        } catch (Exception ex) {
            AppendAgentBuilderAudit("", "", account.UserName, "reflection.rejected", ex.Message, new { body = AgentBuilderJson.Truncate(request.Body ?? "", 5000) });
            return AgentBuilderJsonResponse(request, new { ok = false, error = ex.Message }, 400, "Bad Request");
        }
    }

    private object HandleAgentBuilderOutputPage(HttpRequest request) {
        AccountRecord? account = AuthenticateRequest(request, out _);
        if (account == null)
            return Redirect(request, BuildSocketJackLoginPath(request.Path ?? "/Builder"));

        string apiName = request.PathVariables.Count > 0 ? AgentBuilderSlug.Normalize(request.PathVariables[0]) : "";
        if (string.IsNullOrWhiteSpace(apiName) || !_agentBuilderApis.TryGetValue(apiName, out AgentBuilderApiDefinition? api) || !api.Enabled)
            return Html(request, BuildAgentBuilderMessageHtml("Agent not found", "That Builder output page is not published."), 404, "Not Found");
        if (!_agentBuilderWorkflows.TryGetValue(api.WorkflowId, out AgentBuilderWorkflow? workflow))
            return Html(request, BuildAgentBuilderMessageHtml("Workflow missing", "The published API exists, but its workflow could not be found."), 404, "Not Found");
        if (!IsAgentBuilderOwner(api.OwnerUserName, account))
            return Html(request, BuildAgentBuilderMessageHtml("Access denied", "This published agent belongs to another user."), 403, "Forbidden");

        return Html(request, BuildAgentBuilderOutputHtml(api, workflow, null));
    }

    private object HandleAgentBuilderDynamicApi(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken) {
        if (request.Method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
            return NoContent(request);

        string apiName = request.PathVariables.Count > 0 ? request.PathVariables[0] : "";
        if (apiName.Contains('/'))
            return AgentBuilderJsonResponse(request, new { ok = false, error = "Unknown API route." }, 404, "Not Found");
        apiName = AgentBuilderSlug.Normalize(apiName);
        if (string.IsNullOrWhiteSpace(apiName) || !_agentBuilderApis.TryGetValue(apiName, out AgentBuilderApiDefinition? api) || !api.Enabled)
            return AgentBuilderJsonResponse(request, new { ok = false, error = "Unknown API route." }, 404, "Not Found");
        if (!_agentBuilderWorkflows.TryGetValue(api.WorkflowId, out AgentBuilderWorkflow? workflow) || !workflow.Enabled)
            return AgentBuilderJsonResponse(request, new { ok = false, error = "Workflow not found or disabled." }, 404, "Not Found");

        AccountRecord? account = AuthenticateRequest(request, out string authError);
        if (api.RequireAuthentication && account == null) {
            if (AgentBuilderWantsHtml(request))
                return Redirect(request, BuildSocketJackLoginPath(request.Path ?? "/Builder"));
            return AgentBuilderJsonResponse(request, new { ok = false, error = string.IsNullOrWhiteSpace(authError) ? "SocketJack login required." : authError }, 401, "Unauthorized");
        }
        if (account != null && !IsAgentBuilderOwner(api.OwnerUserName, account))
            return AgentBuilderJsonResponse(request, new { ok = false, error = "This API belongs to another user." }, 403, "Forbidden");

        Dictionary<string, object> inputs = ExtractAgentBuilderInputs(request);
        IReadOnlyList<AgentBuilderInputDefinition> definitions = AgentBuilderWorkflowEngine.GetInputDefinitions(workflow);
        List<AgentBuilderInputDefinition> missing = AgentBuilderWorkflowEngine.GetMissingRequiredInputs(definitions, inputs);
        if (missing.Count > 0) {
            if (AgentBuilderWantsHtml(request))
                return Html(request, BuildAgentBuilderOutputHtml(api, workflow, null));
            return AgentBuilderJsonResponse(request, new {
                ok = false,
                code = "missing_required_inputs",
                missing = missing.Select(item => new { item.Key, item.Label, item.Type, item.IsFileLike }).ToArray()
            }, 400, "Bad Request");
        }

        AgentBuilderExecutionResult result = ExecuteAgentBuilderWorkflowAsync(connection, request, workflow, inputs, "api", api.ApiName, account!, cancellationToken)
            .GetAwaiter()
            .GetResult();

        if (AgentBuilderWantsHtml(request))
            return Html(request, BuildAgentBuilderOutputHtml(api, workflow, result), result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");

        return AgentBuilderJsonResponse(request, new {
            ok = result.Ok,
            result,
            renderedOutput = AgentBuilderWorkflowEngine.RenderOutputSchemaTemplate(workflow, result)
        }, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");
    }

    private async Task<AgentBuilderExecutionResult> ExecuteAgentBuilderWorkflowAsync(
        NetworkConnection? connection,
        HttpRequest? request,
        AgentBuilderWorkflow workflow,
        Dictionary<string, object> inputs,
        string triggerKind,
        string apiName,
        AccountRecord? account,
        CancellationToken cancellationToken) {

        workflow.Normalize();
        var engine = new AgentBuilderWorkflowEngine();
        var runRequest = new AgentBuilderExecutionRequest {
            Workflow = workflow,
            Inputs = inputs ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase),
            TriggerKind = triggerKind,
            UserName = account?.UserName ?? workflow.OwnerUserName,
            ReflectionExecutor = new AgentBuilderReflectionExecutor(),
            AgentRunner = new MasterListAgentBuilderAgentRunner(this, connection, request)
        };

        bool overlapGuard = triggerKind.StartsWith("schedule", StringComparison.OrdinalIgnoreCase);
        if (overlapGuard && !_agentBuilderRunningWorkflows.TryAdd(workflow.Id, 0))
            return new AgentBuilderExecutionResult {
                Ok = false,
                WorkflowId = workflow.Id,
                Status = "skipped",
                Error = "Workflow is already running.",
                TriggerKind = triggerKind,
                StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                CompletedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

        try {
            AgentBuilderExecutionResult result = await engine.ExecuteAsync(runRequest, cancellationToken).ConfigureAwait(false);
            SaveAgentBuilderRun(result, workflow, apiName, account?.UserName ?? workflow.OwnerUserName);
            AppendAgentBuilderAudit(workflow.Id, result.RunId, account?.UserName ?? workflow.OwnerUserName, result.Ok ? "run.completed" : "run.failed", result.Ok ? "Workflow run completed." : result.Error, new { triggerKind, apiName });
            return result;
        } finally {
            if (overlapGuard)
                _agentBuilderRunningWorkflows.TryRemove(workflow.Id, out _);
        }
    }

    private object ExecuteAgentBuilderAutoNode(NetworkConnection? connection, HttpRequest? sourceRequest, AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults, string prompt, CancellationToken cancellationToken) {
        if (sourceRequest == null) {
            return new {
                ok = true,
                prepared = true,
                message = "Auto node prepared. A live request context is required to route SocketJack Auto.",
                mode = FirstNonEmpty(AgentBuilderConfig(node, "mode"), "text"),
                prompt
            };
        }
        NetworkConnection liveConnection = connection ?? sourceRequest.Context?.Connection ?? throw new InvalidOperationException("A live request connection is required to route SocketJack Auto.");

        bool wantsTerminal = AgentBuilderConfig(node, "allowTerminal").Equals("true", StringComparison.OrdinalIgnoreCase) ||
                             AgentBuilderConfig(node, "allowTerminalCommands").Equals("true", StringComparison.OrdinalIgnoreCase) ||
                             !string.IsNullOrWhiteSpace(FirstNonEmpty(AgentBuilderConfig(node, "terminalCommand"), AgentBuilderConfig(node, "command")));
        string mode = wantsTerminal ? "tools" : FirstNonEmpty(AgentBuilderConfig(node, "mode"), "text");
        bool strictServer = IsTruthyText(FirstNonEmpty(AgentBuilderConfig(node, "strictServer"), AgentBuilderConfig(node, "requireServer"), AgentBuilderConfig(node, "lockServer")));
        bool strictModel = IsTruthyText(FirstNonEmpty(AgentBuilderConfig(node, "strictModel"), AgentBuilderConfig(node, "requireModel"), AgentBuilderConfig(node, "lockModel")));
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            ["mode"] = mode
        };
        CopyAgentBuilderConfigToQuery(node, query, "model", "modelId", "model_id");
        CopyAgentBuilderConfigToQuery(node, query, "server", "serverId", "server_id");
        CopyAgentBuilderConfigToQuery(node, query, "minParamsB", "min_parameters_b");
        CopyAgentBuilderConfigToQuery(node, query, "maxParamsB", "max_parameters_b");
        CopyAgentBuilderConfigToQuery(node, query, "premium", "premiumModels", "premium_models");
        CopyAgentBuilderConfigToQuery(node, query, "disabledModels", "disabled_models", "excludedModels", "excluded_models");

        var bodyPayload = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase) {
            ["prompt"] = prompt,
            ["q"] = prompt,
            ["inputs"] = inputs
        };
        if (wantsTerminal) {
            bodyPayload["toolIntent"] = "terminal";
            bodyPayload["allowTerminalCommands"] = true;
            bodyPayload["terminalCommand"] = FirstNonEmpty(AgentBuilderConfig(node, "terminalCommand"), AgentBuilderConfig(node, "command"));
            bodyPayload["terminalShell"] = FirstNonEmpty(AgentBuilderConfig(node, "terminalShell"), AgentBuilderConfig(node, "shell"), "powershell");
            bodyPayload["terminalWorkingDirectory"] = FirstNonEmpty(AgentBuilderConfig(node, "terminalWorkingDirectory"), AgentBuilderConfig(node, "workingDirectory"));
        }

        string body = AgentBuilderJson.Serialize(bodyPayload);

        var candidates = new List<(string Label, Dictionary<string, string> Query)>();
        void AddCandidate(string label, Dictionary<string, string> candidate) {
            string key = string.Join("&", candidate.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase).Select(item => item.Key + "=" + item.Value));
            if (candidates.Any(item => string.Equals(string.Join("&", item.Query.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value)), key, StringComparison.OrdinalIgnoreCase)))
                return;
            candidates.Add((label, candidate));
        }

        static Dictionary<string, string> CopyQuery(Dictionary<string, string> source) =>
            new(source, StringComparer.OrdinalIgnoreCase);

        static bool HasAny(Dictionary<string, string> source, params string[] keys) =>
            keys.Any(key => source.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value));

        static Dictionary<string, string> WithoutKeys(Dictionary<string, string> source, params string[] keys) {
            Dictionary<string, string> copy = CopyQuery(source);
            foreach (string key in keys)
                copy.Remove(key);
            return copy;
        }

        string[] serverKeys = { "server", "serverId", "server_id" };
        string[] modelKeys = { "model", "modelId", "model_id" };
        bool hasServerLock = HasAny(query, serverKeys);
        bool hasModelLock = HasAny(query, modelKeys);
        AddCandidate("configured", CopyQuery(query));
        if (hasServerLock && !strictServer)
            AddCandidate("without-server", WithoutKeys(query, serverKeys));
        if (hasModelLock && !strictModel)
            AddCandidate("without-model", WithoutKeys(query, modelKeys));
        if (hasServerLock && hasModelLock && !strictServer && !strictModel)
            AddCandidate("without-server-and-model", WithoutKeys(query, serverKeys.Concat(modelKeys).ToArray()));

        var attempts = new List<object>();
        (HttpContext Context, string RawText, JsonNode? Parsed, Dictionary<string, string> Query, string Label) finalAttempt = default;
        foreach ((string label, Dictionary<string, string> candidateQuery) in candidates) {
            finalAttempt = RunAutoNodeAttempt(label, candidateQuery);
            attempts.Add(new {
                label,
                statusCode = finalAttempt.Context.StatusCodeNumber,
                reason = finalAttempt.Context.ReasonPhrase,
                model = FirstNonEmpty(candidateQuery.TryGetValue("model", out string? candidateModel) ? candidateModel : "", candidateQuery.TryGetValue("modelId", out string? candidateModelId) ? candidateModelId : "", candidateQuery.TryGetValue("model_id", out string? candidateModelSnake) ? candidateModelSnake : ""),
                server = FirstNonEmpty(candidateQuery.TryGetValue("server", out string? candidateServer) ? candidateServer : "", candidateQuery.TryGetValue("serverId", out string? candidateServerId) ? candidateServerId : "", candidateQuery.TryGetValue("server_id", out string? candidateServerSnake) ? candidateServerSnake : ""),
                error = AgentBuilderAutoJsonText(finalAttempt.Parsed, "error"),
                code = AgentBuilderAutoJsonText(finalAttempt.Parsed, "code")
            });

            if (AgentBuilderAutoAttemptSucceeded(finalAttempt.Context, finalAttempt.Parsed) ||
                !AgentBuilderAutoHasCode(finalAttempt.Parsed, "auto_no_eligible_server"))
                break;
        }

        (HttpContext Context, string RawText, JsonNode? Parsed, Dictionary<string, string> Query, string Label) RunAutoNodeAttempt(string label, Dictionary<string, string> candidateQuery) {
            var autoContext = new HttpContext {
                Connection = liveConnection,
                cancellationToken = cancellationToken
            };
            var autoRequest = new HttpRequest {
                Context = autoContext,
                Method = "POST",
                Path = "/auto/api",
                Version = sourceRequest.Version,
                Host = sourceRequest.Host,
                HostName = sourceRequest.HostName,
                QueryString = string.Join("&", candidateQuery.Select(item => WebUtility.UrlEncode(item.Key) + "=" + WebUtility.UrlEncode(item.Value))),
                QueryParameters = candidateQuery,
                Headers = new Dictionary<string, string>(sourceRequest.Headers ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase),
                Body = body
            };
            autoRequest.Headers["Content-Type"] = "application/json";
            autoContext.Request = autoRequest;

            object raw = HandleAutoApiRequest(liveConnection, autoRequest, cancellationToken);
            string rawText = raw as string ?? AgentBuilderJson.SafeSerialize(raw);
            JsonNode? parsed = null;
            try {
                parsed = JsonNode.Parse(rawText);
            } catch {
            }
            return (autoContext, rawText, parsed, CopyQuery(candidateQuery), label);
        }

        HttpContext finalContext = finalAttempt.Context ?? new HttpContext();
        string finalRawText = finalAttempt.RawText ?? "";
        return new {
            ok = AgentBuilderAutoAttemptSucceeded(finalContext, finalAttempt.Parsed),
            statusCode = finalContext.StatusCodeNumber,
            reason = finalContext.ReasonPhrase,
            contentType = finalContext.Response.ContentType,
            response = finalAttempt.Parsed,
            raw = finalAttempt.Parsed == null ? AgentBuilderJson.Truncate(finalRawText, 20000) : null,
            route = finalAttempt.Label,
            attempts = attempts.Count > 1 ? attempts.ToArray() : null
        };
    }

    private void StartAgentBuilderScheduler() {
        _agentBuilderScheduleTimer ??= new System.Threading.Timer(_ => {
            _ = Task.Run(RunDueAgentBuilderSchedulesAsync);
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private async Task RunDueAgentBuilderSchedulesAsync() {
        if (Interlocked.Exchange(ref _agentBuilderScheduleLoopActive, 1) == 1)
            return;

        try {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (AgentBuilderSchedule schedule in _agentBuilderSchedules.Values.Where(item => item.Enabled).ToList()) {
                if (!DateTimeOffset.TryParse(schedule.NextRunUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset nextRun) || nextRun > now)
                    continue;
            if (!_agentBuilderWorkflows.TryGetValue(schedule.WorkflowId, out AgentBuilderWorkflow? workflow))
                continue;
                if (!AgentBuilderWorkflowEngine.EvaluateScheduleCriteria(schedule, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase))) {
                    schedule.NextRunUtc = AgentBuilderWorkflowEngine.CalculateNextRunUtc(schedule, DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
                    schedule.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    UpsertAgentBuilderSchedule(schedule);
                    AppendAgentBuilderAudit(workflow.Id, "", workflow.OwnerUserName, "schedule.criteria_skipped", "Schedule criteria was not met.", new { schedule.Id, schedule.CriteriaJson });
                    continue;
                }

                if (schedule.PreventOverlap && !_agentBuilderRunningWorkflows.TryAdd(workflow.Id, 0))
                    continue;

                try {
                    AgentBuilderExecutionResult result = await ExecuteAgentBuilderWorkflowAsync(null, null, workflow, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), "schedule", workflow.ApiName, null, CancellationToken.None).ConfigureAwait(false);
                    schedule.LastRunUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    schedule.NextRunUtc = AgentBuilderWorkflowEngine.CalculateNextRunUtc(schedule, DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
                    schedule.UpdatedUtc = schedule.LastRunUtc;
                    UpsertAgentBuilderSchedule(schedule);
                    AppendAgentBuilderAudit(workflow.Id, result.RunId, workflow.OwnerUserName, "schedule.run", result.Ok ? "Scheduled run completed." : result.Error, new { schedule.Id, result.Ok });
                } finally {
                    if (schedule.PreventOverlap)
                        _agentBuilderRunningWorkflows.TryRemove(workflow.Id, out _);
                }
            }
        } finally {
            Interlocked.Exchange(ref _agentBuilderScheduleLoopActive, 0);
        }
    }

    private void DisposeAgentBuilder() {
        _agentBuilderScheduleTimer?.Dispose();
        _agentBuilderScheduleTimer = null;
    }

    private sealed class MasterListAgentBuilderAgentRunner : IAgentBuilderAgentRunner {
        private readonly MasterListServerHost _host;
        private readonly NetworkConnection? _connection;
        private readonly HttpRequest? _request;

        public MasterListAgentBuilderAgentRunner(MasterListServerHost host, NetworkConnection? connection, HttpRequest? request) {
            _host = host;
            _connection = connection;
            _request = request;
        }

        public Task<object> RunAgentAsync(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults, string prompt, CancellationToken cancellationToken) {
            return Task.FromResult(_host.ExecuteAgentBuilderAutoNode(_connection, _request, node, inputs, nodeResults, prompt, cancellationToken));
        }
    }

    private AgentBuilderWorkflow ParseAgentBuilderWorkflowBody(HttpRequest request) {
        JsonObject body = ParseAgentBuilderJsonObject(request);
        JsonNode? source = body;
        if (body.TryGetPropertyValue("workflow", out JsonNode? workflowNode) && workflowNode != null)
            source = workflowNode;
        AgentBuilderWorkflow workflow = AgentBuilderJson.Deserialize<AgentBuilderWorkflow>(source.ToJsonString(AgentBuilderJson.Options)) ?? new AgentBuilderWorkflow();
        workflow.Normalize();
        return workflow;
    }

    private Dictionary<string, object> ExtractAgentBuilderInputs(HttpRequest request) {
        var inputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        if (request.QueryParameters != null) {
            foreach (KeyValuePair<string, string> pair in request.QueryParameters)
                inputs[pair.Key] = pair.Value ?? "";
        }

        string contentType = GetHeaderValue(request, "Content-Type");
        if (!string.IsNullOrWhiteSpace(request.Body)) {
            if (contentType.IndexOf("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase) >= 0) {
                foreach (KeyValuePair<string, string> pair in ParseUrlEncoded(request.Body))
                    inputs[pair.Key] = pair.Value;
            } else {
                try {
                    JsonObject body = ParseAgentBuilderJsonObject(request);
                    JsonObject source = body;
                    if (body.TryGetPropertyValue("inputs", out JsonNode? nested) && nested is JsonObject nestedObject)
                        source = nestedObject;

                    foreach (KeyValuePair<string, JsonNode?> pair in source) {
                        if (IsAgentBuilderControlField(pair.Key))
                            continue;
                        inputs[pair.Key] = AgentBuilderPlainValue(pair.Value);
                    }
                } catch {
                }
            }
        } else if (contentType.IndexOf("multipart/form-data", StringComparison.OrdinalIgnoreCase) >= 0 && request.BodyBytes != null && request.BodyBytes.Length > 0) {
            foreach (KeyValuePair<string, object> pair in ParseAgentBuilderMultipart(request))
                inputs[pair.Key] = pair.Value;
        }

        return inputs;
    }

    private static bool IsAgentBuilderControlField(string key) {
        return key.Equals("workflow", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("workflowId", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("apiName", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("schedule", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("action", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonObject ParseAgentBuilderJsonObject(HttpRequest request) {
        if (string.IsNullOrWhiteSpace(request.Body))
            return new JsonObject();
        JsonNode? node = JsonNode.Parse(request.Body);
        return node as JsonObject ?? new JsonObject();
    }

    private static object AgentBuilderPlainValue(JsonNode? node) {
        if (node == null)
            return null!;
        if (node is JsonValue value) {
            if (value.TryGetValue<string>(out string? text))
                return text ?? "";
            if (value.TryGetValue<bool>(out bool b))
                return b;
            if (value.TryGetValue<long>(out long l))
                return l;
            if (value.TryGetValue<decimal>(out decimal dec))
                return dec;
            return value.ToJsonString(AgentBuilderJson.Options);
        }
        if (node is JsonArray array)
            return array.Select(AgentBuilderPlainValue).ToList();
        if (node is JsonObject obj)
            return obj.ToDictionary(pair => pair.Key, pair => AgentBuilderPlainValue(pair.Value), StringComparer.OrdinalIgnoreCase);
        return node.ToJsonString(AgentBuilderJson.Options);
    }

    private static Dictionary<string, string> ParseUrlEncoded(string body) {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (body ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            string[] pieces = part.Split(new[] { '=' }, 2);
            string key = WebUtility.UrlDecode(pieces[0] ?? "");
            string value = pieces.Length > 1 ? WebUtility.UrlDecode(pieces[1] ?? "") : "";
            if (!string.IsNullOrWhiteSpace(key))
                values[key] = value ?? "";
        }
        return values;
    }

    private static Dictionary<string, object> ParseAgentBuilderMultipart(HttpRequest request) {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        string contentType = GetHeaderValue(request, "Content-Type");
        int boundaryIndex = contentType.IndexOf("boundary=", StringComparison.OrdinalIgnoreCase);
        if (boundaryIndex < 0)
            return values;
        string boundary = contentType.Substring(boundaryIndex + "boundary=".Length).Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(boundary))
            return values;

        string raw = Encoding.UTF8.GetString(request.BodyBytes ?? Array.Empty<byte>());
        string[] parts = raw.Split(new[] { "--" + boundary }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string part in parts) {
            int headerEnd = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headerEnd < 0)
                continue;
            string headers = part.Substring(0, headerEnd);
            string body = part.Substring(headerEnd + 4).TrimEnd('\r', '\n', '-');
            string name = ExtractMultipartDispositionValue(headers, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;
            string fileName = ExtractMultipartDispositionValue(headers, "filename");
            if (!string.IsNullOrWhiteSpace(fileName)) {
                values[name] = new {
                    fileName,
                    size = Encoding.UTF8.GetByteCount(body),
                    textPreview = AgentBuilderJson.Truncate(body, 1000)
                };
            } else {
                values[name] = body;
            }
        }

        return values;
    }

    private static string ExtractMultipartDispositionValue(string headers, string name) {
        string marker = name + "=\"";
        int start = headers.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return "";
        start += marker.Length;
        int end = headers.IndexOf('"', start);
        return end > start ? headers.Substring(start, end - start) : "";
    }

    private void UpsertAgentBuilderWorkflow(AgentBuilderWorkflow workflow) {
        workflow.Normalize();
        lock (_agentBuilderSync) {
            UpsertAgentBuilderRow(_agentBuilderWorkflowsTable, "Id", workflow.Id, AgentBuilderWorkflowToValues(workflow));
            SyncAgentBuilderReflectionObjectsLocked(workflow);
            _agentBuilderWorkflows[workflow.Id] = workflow;
            _dataServer.Save();
        }
    }

    private void SyncAgentBuilderReflectionObjectsLocked(AgentBuilderWorkflow workflow) {
        RemoveAgentBuilderRows(_agentBuilderReflectionObjectsTable, "WorkflowId", workflow.Id);
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (AgentBuilderNode node in workflow.Nodes.Where(node => string.Equals(node.Type, "reflectionObject", StringComparison.OrdinalIgnoreCase))) {
            string objectName = AgentBuilderConfig(node, "objectName");
            if (string.IsNullOrWhiteSpace(objectName))
                objectName = node.Name;
            string typeName = AgentBuilderConfig(node, "typeName");
            if (string.IsNullOrWhiteSpace(objectName) || string.IsNullOrWhiteSpace(typeName))
                continue;

            _agentBuilderReflectionObjectsTable.Rows.Add(CreateAgentBuilderRow(_agentBuilderReflectionObjectsTable, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                ["Id"] = "reflection_object_" + Guid.NewGuid().ToString("N"),
                ["WorkflowId"] = workflow.Id,
                ["OwnerUserName"] = workflow.OwnerUserName,
                ["ObjectName"] = objectName,
                ["TypeName"] = typeName,
                ["ConfigJson"] = AgentBuilderJson.SafeSerialize(node.Config),
                ["CreatedUtc"] = now,
                ["UpdatedUtc"] = now
            }));
        }
    }

    private void UpsertAgentBuilderApi(AgentBuilderApiDefinition api) {
        lock (_agentBuilderSync) {
            UpsertAgentBuilderRow(_agentBuilderApisTable, "ApiName", api.ApiName, AgentBuilderApiToValues(api));
            _agentBuilderApis[api.ApiName] = api;
            _dataServer.Save();
        }
    }

    private void UpsertAgentBuilderSchedule(AgentBuilderSchedule schedule) {
        lock (_agentBuilderSync) {
            UpsertAgentBuilderRow(_agentBuilderSchedulesTable, "Id", schedule.Id, AgentBuilderScheduleToValues(schedule));
            _agentBuilderSchedules[schedule.Id] = schedule;
            _dataServer.Save();
        }
    }

    private void SaveAgentBuilderRun(AgentBuilderExecutionResult result, AgentBuilderWorkflow workflow, string apiName, string ownerUserName) {
        var run = new AgentBuilderRun {
            Id = result.RunId,
            WorkflowId = workflow.Id,
            ApiName = apiName ?? "",
            OwnerUserName = ownerUserName ?? workflow.OwnerUserName,
            TriggerKind = result.TriggerKind,
            Status = result.Status,
            InputJson = AgentBuilderJson.SafeSerialize(result.Inputs),
            OutputJson = AgentBuilderJson.SafeSerialize(result.Output),
            Error = result.Error,
            NodeResultsJson = AgentBuilderJson.SafeSerialize(result.NodeResults),
            DurationMs = result.DurationMs,
            StartedUtc = result.StartedUtc,
            CompletedUtc = result.CompletedUtc
        };

        lock (_agentBuilderSync) {
            _agentBuilderRunsTable.Rows.Add(CreateAgentBuilderRow(_agentBuilderRunsTable, AgentBuilderRunToValues(run)));
            while (_agentBuilderRunsTable.Rows.Count > 1000)
                _agentBuilderRunsTable.Rows.RemoveAt(0);
            _dataServer.Save();
        }
    }

    private void AppendAgentBuilderAudit(string workflowId, string runId, string ownerUserName, string eventType, string message, object payload) {
        var audit = new AgentBuilderAuditEvent {
            Id = "audit_" + Guid.NewGuid().ToString("N"),
            WorkflowId = workflowId ?? "",
            RunId = runId ?? "",
            OwnerUserName = ownerUserName ?? "",
            EventType = eventType ?? "",
            Message = message ?? "",
            PayloadJson = AgentBuilderJson.SafeSerialize(payload),
            CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };

        lock (_agentBuilderSync) {
            _agentBuilderAuditTable.Rows.Add(CreateAgentBuilderRow(_agentBuilderAuditTable, AgentBuilderAuditToValues(audit)));
            while (_agentBuilderAuditTable.Rows.Count > 1000)
                _agentBuilderAuditTable.Rows.RemoveAt(0);
            _dataServer.Save();
        }
    }

    private List<AgentBuilderRun> ListAgentBuilderRunsFor(AccountRecord account, string workflowId, int take) {
        take = Math.Max(1, Math.Min(take, 200));
        lock (_agentBuilderSync) {
            return _agentBuilderRunsTable.Rows
                .Select(AgentBuilderRunFromRow)
                .Where(run => IsAgentBuilderOwner(run.OwnerUserName, account))
                .Where(run => string.IsNullOrWhiteSpace(workflowId) || string.Equals(run.WorkflowId, workflowId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(run => run.StartedUtc)
                .Take(take)
                .ToList();
        }
    }

    private static Dictionary<string, string> AgentBuilderWorkflowToValues(AgentBuilderWorkflow workflow) => new(StringComparer.OrdinalIgnoreCase) {
        ["Id"] = workflow.Id,
        ["OwnerUserName"] = workflow.OwnerUserName,
        ["Name"] = workflow.Name,
        ["Description"] = workflow.Description,
        ["NodesJson"] = AgentBuilderJson.Serialize(workflow.Nodes),
        ["EdgesJson"] = AgentBuilderJson.Serialize(workflow.Edges),
        ["VariablesJson"] = AgentBuilderJson.Serialize(workflow.Variables),
        ["ApiName"] = workflow.ApiName,
        ["ApiEnabled"] = workflow.ApiEnabled ? "true" : "false",
        ["Enabled"] = workflow.Enabled ? "true" : "false",
        ["CreatedUtc"] = workflow.CreatedUtc,
        ["UpdatedUtc"] = workflow.UpdatedUtc
    };

    private AgentBuilderWorkflow AgentBuilderWorkflowFromRow(object[] row) {
        var workflow = new AgentBuilderWorkflow {
            Id = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "Id"),
            OwnerUserName = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "OwnerUserName"),
            Name = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "Name"),
            Description = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "Description"),
            ApiName = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "ApiName"),
            ApiEnabled = ParseBool(AgentBuilderCell(_agentBuilderWorkflowsTable, row, "ApiEnabled"), false),
            Enabled = ParseBool(AgentBuilderCell(_agentBuilderWorkflowsTable, row, "Enabled"), true),
            CreatedUtc = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "CreatedUtc"),
            UpdatedUtc = AgentBuilderCell(_agentBuilderWorkflowsTable, row, "UpdatedUtc")
        };
        workflow.Nodes = AgentBuilderJson.Deserialize<List<AgentBuilderNode>>(AgentBuilderCell(_agentBuilderWorkflowsTable, row, "NodesJson")) ?? new List<AgentBuilderNode>();
        workflow.Edges = AgentBuilderJson.Deserialize<List<AgentBuilderEdge>>(AgentBuilderCell(_agentBuilderWorkflowsTable, row, "EdgesJson")) ?? new List<AgentBuilderEdge>();
        workflow.Variables = AgentBuilderJson.Deserialize<Dictionary<string, string>>(AgentBuilderCell(_agentBuilderWorkflowsTable, row, "VariablesJson")) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        workflow.Normalize();
        return workflow;
    }

    private static Dictionary<string, string> AgentBuilderApiToValues(AgentBuilderApiDefinition api) => new(StringComparer.OrdinalIgnoreCase) {
        ["Id"] = api.Id,
        ["WorkflowId"] = api.WorkflowId,
        ["OwnerUserName"] = api.OwnerUserName,
        ["ApiName"] = api.ApiName,
        ["Route"] = api.Route,
        ["Enabled"] = api.Enabled ? "true" : "false",
        ["RequireAuthentication"] = api.RequireAuthentication ? "true" : "false",
        ["CreatedUtc"] = api.CreatedUtc,
        ["UpdatedUtc"] = api.UpdatedUtc
    };

    private AgentBuilderApiDefinition AgentBuilderApiFromRow(object[] row) => new() {
        Id = AgentBuilderCell(_agentBuilderApisTable, row, "Id"),
        WorkflowId = AgentBuilderCell(_agentBuilderApisTable, row, "WorkflowId"),
        OwnerUserName = AgentBuilderCell(_agentBuilderApisTable, row, "OwnerUserName"),
        ApiName = AgentBuilderCell(_agentBuilderApisTable, row, "ApiName"),
        Route = AgentBuilderCell(_agentBuilderApisTable, row, "Route"),
        Enabled = ParseBool(AgentBuilderCell(_agentBuilderApisTable, row, "Enabled"), true),
        RequireAuthentication = ParseBool(AgentBuilderCell(_agentBuilderApisTable, row, "RequireAuthentication"), true),
        CreatedUtc = AgentBuilderCell(_agentBuilderApisTable, row, "CreatedUtc"),
        UpdatedUtc = AgentBuilderCell(_agentBuilderApisTable, row, "UpdatedUtc")
    };

    private static Dictionary<string, string> AgentBuilderRunToValues(AgentBuilderRun run) => new(StringComparer.OrdinalIgnoreCase) {
        ["Id"] = run.Id,
        ["WorkflowId"] = run.WorkflowId,
        ["ApiName"] = run.ApiName,
        ["OwnerUserName"] = run.OwnerUserName,
        ["TriggerKind"] = run.TriggerKind,
        ["Status"] = run.Status,
        ["InputJson"] = run.InputJson,
        ["OutputJson"] = run.OutputJson,
        ["Error"] = run.Error,
        ["NodeResultsJson"] = run.NodeResultsJson,
        ["DurationMs"] = run.DurationMs.ToString(CultureInfo.InvariantCulture),
        ["StartedUtc"] = run.StartedUtc,
        ["CompletedUtc"] = run.CompletedUtc
    };

    private AgentBuilderRun AgentBuilderRunFromRow(object[] row) => new() {
        Id = AgentBuilderCell(_agentBuilderRunsTable, row, "Id"),
        WorkflowId = AgentBuilderCell(_agentBuilderRunsTable, row, "WorkflowId"),
        ApiName = AgentBuilderCell(_agentBuilderRunsTable, row, "ApiName"),
        OwnerUserName = AgentBuilderCell(_agentBuilderRunsTable, row, "OwnerUserName"),
        TriggerKind = AgentBuilderCell(_agentBuilderRunsTable, row, "TriggerKind"),
        Status = AgentBuilderCell(_agentBuilderRunsTable, row, "Status"),
        InputJson = AgentBuilderCell(_agentBuilderRunsTable, row, "InputJson"),
        OutputJson = AgentBuilderCell(_agentBuilderRunsTable, row, "OutputJson"),
        Error = AgentBuilderCell(_agentBuilderRunsTable, row, "Error"),
        NodeResultsJson = AgentBuilderCell(_agentBuilderRunsTable, row, "NodeResultsJson"),
        DurationMs = long.TryParse(AgentBuilderCell(_agentBuilderRunsTable, row, "DurationMs"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long duration) ? duration : 0,
        StartedUtc = AgentBuilderCell(_agentBuilderRunsTable, row, "StartedUtc"),
        CompletedUtc = AgentBuilderCell(_agentBuilderRunsTable, row, "CompletedUtc")
    };

    private static Dictionary<string, string> AgentBuilderScheduleToValues(AgentBuilderSchedule schedule) => new(StringComparer.OrdinalIgnoreCase) {
        ["Id"] = schedule.Id,
        ["WorkflowId"] = schedule.WorkflowId,
        ["OwnerUserName"] = schedule.OwnerUserName,
        ["Enabled"] = schedule.Enabled ? "true" : "false",
        ["PreventOverlap"] = schedule.PreventOverlap ? "true" : "false",
        ["Kind"] = schedule.Kind,
        ["IntervalSeconds"] = schedule.IntervalSeconds.ToString(CultureInfo.InvariantCulture),
        ["TimeOfDay"] = schedule.TimeOfDay,
        ["TimeZone"] = schedule.TimeZone,
        ["CriteriaJson"] = schedule.CriteriaJson,
        ["LastRunUtc"] = schedule.LastRunUtc,
        ["NextRunUtc"] = schedule.NextRunUtc,
        ["CreatedUtc"] = schedule.CreatedUtc,
        ["UpdatedUtc"] = schedule.UpdatedUtc
    };

    private AgentBuilderSchedule AgentBuilderScheduleFromRow(object[] row) => new() {
        Id = AgentBuilderCell(_agentBuilderSchedulesTable, row, "Id"),
        WorkflowId = AgentBuilderCell(_agentBuilderSchedulesTable, row, "WorkflowId"),
        OwnerUserName = AgentBuilderCell(_agentBuilderSchedulesTable, row, "OwnerUserName"),
        Enabled = ParseBool(AgentBuilderCell(_agentBuilderSchedulesTable, row, "Enabled"), true),
        PreventOverlap = ParseBool(AgentBuilderCell(_agentBuilderSchedulesTable, row, "PreventOverlap"), true),
        Kind = AgentBuilderCell(_agentBuilderSchedulesTable, row, "Kind"),
        IntervalSeconds = int.TryParse(AgentBuilderCell(_agentBuilderSchedulesTable, row, "IntervalSeconds"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ? seconds : 300,
        TimeOfDay = AgentBuilderCell(_agentBuilderSchedulesTable, row, "TimeOfDay"),
        TimeZone = AgentBuilderCell(_agentBuilderSchedulesTable, row, "TimeZone"),
        CriteriaJson = AgentBuilderCell(_agentBuilderSchedulesTable, row, "CriteriaJson"),
        LastRunUtc = AgentBuilderCell(_agentBuilderSchedulesTable, row, "LastRunUtc"),
        NextRunUtc = AgentBuilderCell(_agentBuilderSchedulesTable, row, "NextRunUtc"),
        CreatedUtc = AgentBuilderCell(_agentBuilderSchedulesTable, row, "CreatedUtc"),
        UpdatedUtc = AgentBuilderCell(_agentBuilderSchedulesTable, row, "UpdatedUtc")
    };

    private static Dictionary<string, string> AgentBuilderAuditToValues(AgentBuilderAuditEvent audit) => new(StringComparer.OrdinalIgnoreCase) {
        ["Id"] = audit.Id,
        ["WorkflowId"] = audit.WorkflowId,
        ["RunId"] = audit.RunId,
        ["OwnerUserName"] = audit.OwnerUserName,
        ["EventType"] = audit.EventType,
        ["Message"] = audit.Message,
        ["PayloadJson"] = audit.PayloadJson,
        ["CreatedUtc"] = audit.CreatedUtc
    };

    private static void UpsertAgentBuilderRow(Table table, string keyColumn, string keyValue, Dictionary<string, string> values) {
        for (int i = 0; i < table.Rows.Count; i++) {
            if (!string.Equals(AgentBuilderCell(table, table.Rows[i], keyColumn), keyValue, StringComparison.OrdinalIgnoreCase))
                continue;
            table.Rows[i] = CreateAgentBuilderRow(table, values);
            return;
        }

        table.Rows.Add(CreateAgentBuilderRow(table, values));
    }

    private static object[] CreateAgentBuilderRow(Table table, Dictionary<string, string> values) {
        var row = new object[table.Columns.Count];
        for (int i = 0; i < table.Columns.Count; i++) {
            string column = table.Columns[i].Name;
            row[i] = values.TryGetValue(column, out string? value) ? value ?? "" : "";
        }
        return row;
    }

    private static void RemoveAgentBuilderRows(Table table, string keyColumn, string keyValue) {
        for (int i = table.Rows.Count - 1; i >= 0; i--) {
            if (string.Equals(AgentBuilderCell(table, table.Rows[i], keyColumn), keyValue, StringComparison.OrdinalIgnoreCase))
                table.Rows.RemoveAt(i);
        }
    }

    private static string AgentBuilderCell(Table table, object[] row, string columnName) {
        for (int i = 0; i < table.Columns.Count && i < row.Length; i++) {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
                return row[i]?.ToString() ?? "";
        }
        return "";
    }

    private AccountRecord? RequireAgentBuilderAccount(HttpRequest request, out object unauthorized) {
        AccountRecord? account = AuthenticateRequest(request, out string authError);
        if (account == null) {
            unauthorized = AgentBuilderJsonResponse(request, new {
                ok = false,
                error = string.IsNullOrWhiteSpace(authError) ? "SocketJack login required." : authError,
                loginUrl = BuildSocketJackLoginPath(request.Path ?? "/Builder")
            }, 401, "Unauthorized");
            return null;
        }

        unauthorized = "";
        return account;
    }

    private static bool IsAgentBuilderOwner(string ownerUserName, AccountRecord account) {
        return account != null &&
               (account.IsAdministrator || string.Equals(ownerUserName, account.UserName, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AgentBuilderWantsHtml(HttpRequest request) {
        if (string.Equals(GetQuery(request, "format"), "json", StringComparison.OrdinalIgnoreCase))
            return false;
        string accept = GetHeaderValue(request, "Accept");
        return accept.IndexOf("text/html", StringComparison.OrdinalIgnoreCase) >= 0 ||
               (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(request.QueryString));
    }

    private bool AgentBuilderRouteConflicts(string slug) {
        if (AgentBuilderWorkflowEngine.ReservedApiNames.Contains(slug))
            return true;
        string firstSegment = slug.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? slug;
        return _server.GetMappedRoutes()
            .Select(route => route.Path ?? "")
            .Concat(_websiteServer.GetMappedRoutes().Select(route => route.Path ?? ""))
            .Any(path => path.StartsWith("/api/" + firstSegment + "/", StringComparison.OrdinalIgnoreCase) ||
                         path.Equals("/api/" + firstSegment, StringComparison.OrdinalIgnoreCase));
    }

    private static string AgentBuilderJsonText(JsonObject? obj, string name, string fallback) {
        if (obj == null || !obj.TryGetPropertyValue(name, out JsonNode? node) || node == null)
            return fallback;
        string value = node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out string? text) ? text ?? "" : node.ToString();
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string AgentBuilderConfig(AgentBuilderNode node, string key) {
        return node.Config != null && node.Config.TryGetValue(key, out string? value) ? value ?? "" : "";
    }

    private static void CopyAgentBuilderConfigToQuery(AgentBuilderNode node, Dictionary<string, string> query, params string[] keys) {
        foreach (string key in keys) {
            string value = AgentBuilderConfig(node, key);
            if (!string.IsNullOrWhiteSpace(value))
                query[key] = value;
        }
    }

    private static bool AgentBuilderAutoAttemptSucceeded(HttpContext context, JsonNode? parsed) =>
        context != null &&
        context.StatusCodeNumber >= 200 &&
        context.StatusCodeNumber < 400 &&
        !AgentBuilderAutoJsonOkFalse(parsed);

    private static bool AgentBuilderAutoJsonOkFalse(JsonNode? parsed) {
        string ok = AgentBuilderAutoJsonText(parsed, "ok");
        return bool.TryParse(ok, out bool value) && !value;
    }

    private static bool AgentBuilderAutoHasCode(JsonNode? parsed, string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        AgentBuilderAutoJsonText(parsed, "code").Equals(code, StringComparison.OrdinalIgnoreCase);

    private static string AgentBuilderAutoJsonText(JsonNode? parsed, string name) {
        if (parsed is not JsonObject obj || string.IsNullOrWhiteSpace(name))
            return "";
        foreach (KeyValuePair<string, JsonNode?> pair in obj) {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return JsonNodeText(pair.Value);
        }
        return "";
    }

    private static string AgentBuilderJsonResponse(HttpRequest request, object value, int statusCode = 200, string reasonPhrase = "OK") {
        request.Context.StatusCodeNumber = statusCode;
        request.Context.ReasonPhrase = reasonPhrase;
        AddCors(request);
        request.Context.Response.ContentType = "application/json";
        return AgentBuilderJson.Serialize(value);
    }

    private static string BuildAgentBuilderHtml() {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SocketJack.MagicMasterList.Html.Builder.html");
        if (stream == null)
            return BuildAgentBuilderMessageHtml("Builder unavailable", "Builder.html is not embedded in this build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string BuildAgentBuilderMessageHtml(string title, string message) {
        return "<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>" +
               HtmlEncode(title) +
               "</title><style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b1020;color:#f8fbff;font:15px/1.5 Segoe UI,system-ui}.box{max-width:620px;padding:24px;border:1px solid rgba(255,255,255,.14);border-radius:8px;background:#111827}a{color:#67e8f9}</style></head><body><main class=\"box\"><h1>" +
               HtmlEncode(title) +
               "</h1><p>" +
               HtmlEncode(message) +
               "</p><p><a href=\"/Builder\">Open Builder</a></p></main></body></html>";
    }

    private static string BuildAgentBuilderOutputHtml(AgentBuilderApiDefinition api, AgentBuilderWorkflow workflow, AgentBuilderExecutionResult? result) {
        IReadOnlyList<AgentBuilderInputDefinition> inputs = AgentBuilderWorkflowEngine.GetInputDefinitions(workflow);
        var form = new StringBuilder();
        foreach (AgentBuilderInputDefinition input in inputs) {
            string type = input.IsFileLike ? "file" : input.Type.Equals("number", StringComparison.OrdinalIgnoreCase) ? "number" : "text";
            form.Append("<label><span>")
                .Append(HtmlEncode(input.Label))
                .Append(input.Required ? " <b>*</b>" : "")
                .Append("</span><input name=\"")
                .Append(HtmlEncode(input.Key))
                .Append("\" type=\"")
                .Append(type)
                .Append("\" data-file=\"")
                .Append(input.IsFileLike ? "true" : "false")
                .Append("\" value=\"")
                .Append(input.IsFileLike ? "" : HtmlEncode(input.DefaultValue))
                .Append("\"")
                .Append(input.Required ? " required" : "")
                .Append("></label>");
        }

        string renderedOutput = result == null ? "" : AgentBuilderWorkflowEngine.RenderOutputSchemaTemplate(workflow, result);
        string resultJson = result == null ? "" : HtmlEncode(AgentBuilderJson.Serialize(result, indented: true));
        string schemaResult = HtmlEncode(renderedOutput);
        return """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
*{box-sizing:border-box}body{margin:0;min-height:100vh;background:#0b1020;color:#f8fbff;font:15px/1.5 "Segoe UI",system-ui,sans-serif}.shell{width:min(920px,calc(100% - 28px));margin:0 auto;padding:24px 0 42px}.top{display:flex;justify-content:space-between;gap:12px;align-items:center;margin-bottom:16px}.top h1{margin:0;font-size:1.35rem}.pill{border:1px solid rgba(125,211,252,.35);border-radius:999px;padding:6px 10px;color:#bae6fd;font:800 .72rem Consolas,monospace}.panel{border:1px solid rgba(255,255,255,.14);border-radius:8px;background:#111827;padding:16px;box-shadow:0 20px 70px rgba(0,0,0,.28)}form{display:grid;gap:12px}label{display:grid;gap:6px}label span{font-weight:800;color:#dbeafe}b{color:#fbbf24}input,textarea{width:100%;border:1px solid rgba(255,255,255,.18);border-radius:8px;background:#060b16;color:#f8fbff;padding:10px;font:inherit}button{border:1px solid rgba(46,230,170,.45);border-radius:8px;background:#0f2a26;color:#eafff6;padding:10px 14px;font-weight:900;cursor:pointer}pre{white-space:pre-wrap;overflow:auto;border:1px solid rgba(255,255,255,.12);border-radius:8px;background:#050914;padding:12px;min-height:160px}.actions{display:flex;gap:10px;align-items:center;flex-wrap:wrap}.status{color:#9ca3af}.rich-output{display:grid;gap:12px;margin-top:14px}.preview-head{display:flex;justify-content:space-between;gap:8px;align-items:center;flex-wrap:wrap}.preview-title{font-weight:950}.preview-meta{color:#9ca3af;font:800 11px Consolas,monospace}.preview-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:12px}.preview-card{display:grid;gap:9px;min-width:0;border:1px solid rgba(255,255,255,.13);border-radius:8px;background:linear-gradient(145deg,rgba(17,26,40,.98),rgba(5,9,20,.98));padding:12px}.preview-card h3{margin:0;font-size:14px;overflow-wrap:anywhere}.preview-card small{color:#9ca3af;font:800 10px Consolas,monospace;overflow-wrap:anywhere}.preview-media{display:grid;place-items:center;min-height:150px;border:1px solid rgba(255,255,255,.10);border-radius:8px;background:#050914;overflow:hidden}.preview-media img,.preview-media video{display:block;max-width:100%;max-height:420px;border-radius:6px}.preview-media audio{width:100%}.preview-file-icon{font:950 36px Consolas,monospace;color:#baf7e6}.preview-text,.preview-json{max-height:420px;overflow:auto;border:1px solid rgba(255,255,255,.10);border-radius:8px;background:#050914;color:#dbeafe;padding:10px;font:12px/1.5 Consolas,monospace;white-space:pre-wrap;overflow-wrap:anywhere}.preview-frame{width:100%;height:420px;border:0;background:#fff;border-radius:6px}.preview-actions{display:flex;gap:8px;flex-wrap:wrap}.preview-actions a{display:inline-flex;align-items:center;min-height:32px;border:1px solid rgba(46,230,170,.42);border-radius:8px;background:#0f2a26;color:#eafff6;text-decoration:none;padding:6px 10px;font-weight:900}.preview-raw{border:1px solid rgba(255,255,255,.10);border-radius:8px;background:#080f1b}.preview-raw summary{cursor:pointer;padding:9px 10px;color:#bfdbfe;font-weight:900}.preview-raw pre{margin:0;border-top:1px solid rgba(255,255,255,.10);max-height:420px}
</style>
</head>
<body>
<main class="shell">
  <div class="top"><div><h1>__NAME__</h1><div class="status">__ROUTE__</div></div><a class="pill" href="/Builder">Builder</a></div>
  <section class="panel">
    <form id="agentForm">__FORM__<div class="actions"><button type="submit">Run Agent</button><span class="status" id="status"></span></div></form>
    <div id="output" class="rich-output"></div>
    <pre id="initialResult" hidden>__RESULT__</pre>
    <pre id="initialSchema" hidden>__SCHEMA_RESULT__</pre>
  </section>
</main>
<script>
const api='__API__';
const form=document.getElementById('agentForm'),out=document.getElementById('output'),status=document.getElementById('status');
function sameOriginWindow(win){try{return !!win&&win.location&&win.location.origin===location.origin}catch{return false}}
function authWindows(){const wins=[window];try{if(parent&&parent!==window&&sameOriginWindow(parent))wins.push(parent)}catch{}try{if(top&&top!==window&&sameOriginWindow(top)&&!wins.includes(top))wins.push(top)}catch{}try{if(opener&&sameOriginWindow(opener)&&!wins.includes(opener))wins.push(opener)}catch{}return wins}
function readCookieFrom(win,name){try{return String(win.document.cookie||'').split(';').map(x=>x.trim()).reduce((found,part)=>{if(found)return found;const eq=part.indexOf('=');if(eq<0)return'';return part.slice(0,eq)===name?decodeURIComponent(part.slice(eq+1)):''},'')}catch{return''}}
function readCookie(name){for(const win of authWindows()){const value=readCookieFrom(win,name);if(value)return value}return''}
function writeCookie(name,value,days){try{document.cookie=name+'='+encodeURIComponent(value)+'; Path=/; Max-Age='+(days*86400)+'; SameSite=Lax'+(location.protocol==='https:'?'; Secure':'')}catch{}}
function safeStorageGet(name){for(const win of authWindows()){for(const key of ['localStorage','sessionStorage']){try{const store=win[key];const value=store&&store.getItem(name);if(value)return value}catch{}}}return''}
function safeStorageSet(name,value){let saved=false;for(const key of ['localStorage','sessionStorage']){try{const store=window[key];if(store){store.setItem(name,value);saved=true}}catch{}}return saved}
function urlAuthToken(){try{const qs=new URLSearchParams(location.search);const token=qs.get('socketjack_auth')||qs.get('access_token')||qs.get('authToken')||qs.get('token')||'';if(!token)return'';safeStorageSet('SocketJackAccessToken',token);writeCookie('SocketJackAuth',token,1);['socketjack_auth','access_token','authToken','token'].forEach(k=>qs.delete(k));history.replaceState(null,'',location.pathname+(qs.toString()?'?'+qs.toString():'')+location.hash);return token}catch{return''}}
function rememberSocketJackAuth(data){if(!data)return;const token=String(data.accessToken||data.access_token||data.authToken||data.token||'');if(token){safeStorageSet('SocketJackAccessToken',token);writeCookie('SocketJackAuth',token,365)}const user=String(data.username||data.userName||data.loginName||'').trim();if(user)writeCookie('SocketJackLoginName',user,365)}
function readSocketJackToken(){const token=urlAuthToken()||safeStorageGet('SocketJackAccessToken')||readCookie('SocketJackAuth')||readCookie('LmVsProxyAuth');if(token){safeStorageSet('SocketJackAccessToken',token);writeCookie('SocketJackAuth',token,1)}return token}
function readSocketJackUser(){return readCookie('SocketJackLoginName')||safeStorageGet('SocketJackLoginName')||''}
function authHeaders(extra){const headers=Object.assign({},extra||{});const token=readSocketJackToken();const user=readSocketJackUser();if(token){headers.Authorization='Bearer '+token;headers['X-SocketJack-Auth']=token}if(user){headers['X-SocketJack-User']=user;headers['X-SocketJack-Username']=user}return headers}
let authRefreshPromise=null;
async function refreshSocketJackAuth(force=false){if(!force&&readSocketJackToken())return null;if(authRefreshPromise)return authRefreshPromise;authRefreshPromise=fetch('/api/web-auth/session',{cache:'no-store',credentials:'include',headers:authHeaders({'Accept':'application/json'})}).then(async res=>{const text=await res.text();let data;try{data=JSON.parse(text)}catch{data=null}if(res.ok&&data&&data.authenticated!==false)rememberSocketJackAuth(data);return data}).catch(()=>null).finally(()=>{authRefreshPromise=null});return authRefreshPromise}
async function socketJackFetch(path,options){await refreshSocketJackAuth(false);const next=Object.assign({},options||{});next.credentials='include';next.headers=authHeaders(next.headers||{});let res=await fetch(path,next);if(res.status===401){await refreshSocketJackAuth(true);next.headers=authHeaders(options&&options.headers||{});res=await fetch(path,next)}return res}
function plainObject(v){return v&&typeof v==='object'&&!Array.isArray(v)}
function firstProp(obj,names){if(!plainObject(obj))return'';for(const name of names){const value=obj[name];if(value!==undefined&&value!==null&&String(value).trim()!=='')return value}return''}
function isDataUrl(value){return /^data:[^,]+,/i.test(String(value||''))}
function mimeFromDataUrl(value){const m=String(value||'').match(/^data:([^;,]+)/i);return m?m[1]:''}
function extFromMime(mime){mime=String(mime||'').toLowerCase();if(mime.includes('png'))return'.png';if(mime.includes('jpeg')||mime.includes('jpg'))return'.jpg';if(mime.includes('gif'))return'.gif';if(mime.includes('webp'))return'.webp';if(mime.includes('svg'))return'.svg';if(mime.includes('mp4'))return'.mp4';if(mime.includes('webm'))return'.webm';if(mime.includes('quicktime'))return'.mov';if(mime.includes('mpeg'))return'.mp3';if(mime.includes('wav'))return'.wav';if(mime.includes('ogg'))return'.ogg';if(mime.includes('pdf'))return'.pdf';if(mime.includes('html'))return'.html';if(mime.includes('json'))return'.json';if(mime.includes('csv'))return'.csv';if(mime.startsWith('text/'))return'.txt';return'.bin'}
function safeDownloadName(name,mime,index){name=String(name||'').split(/[?#]/)[0].split(/[\\/]/).pop().replace(/[^a-z0-9._ -]+/gi,'-').replace(/^-+|-+$/g,'')||('agent-output-'+index);if(!/\.[a-z0-9]{1,8}$/i.test(name))name+=extFromMime(mime);return name}
function inferKind(type,mime,name,src,value){const text=[type,mime,name,src].map(x=>String(x||'').toLowerCase()).join(' ');if(/^image\//i.test(mime)||/\bimage\b|\.png\b|\.jpe?g\b|\.gif\b|\.webp\b|\.svg\b/.test(text))return'image';if(/^video\//i.test(mime)||/\bvideo\b|\.mp4\b|\.webm\b|\.mov\b|\.m4v\b/.test(text))return'video';if(/^audio\//i.test(mime)||/\baudio\b|\.mp3\b|\.wav\b|\.ogg\b|\.m4a\b/.test(text))return'audio';if(/html/.test(text))return'html';if(/json/.test(text))return'json';if(/plain|text/.test(text)&&typeof value==='string')return'text';if(src||/file|download|attachment|\.pdf\b|\.zip\b|\.csv\b|\.bin\b/.test(text))return'file';return''}
function sourceFrom(obj,mime){const direct=firstProp(obj,['dataUrl','contentDataUrl','downloadUrl','fileUrl','contentUrl','url','href','src','uri']);if(typeof direct==='string'&&direct.trim())return direct.trim();const base64=firstProp(obj,['base64','contentBase64']);if(typeof base64==='string'&&base64.trim())return 'data:'+(mime||'application/octet-stream')+';base64,'+base64.trim();const value=firstProp(obj,['value','data','content']);if(typeof value==='string'){const trimmed=value.trim();if(isDataUrl(trimmed)||/^(https?:|\/)/i.test(trimmed))return trimmed;if(mime&&!/^text\//i.test(mime)&&!/(json|html|xml)/i.test(mime)&&trimmed.length>80&&/^[a-z0-9+/=\s]+$/i.test(trimmed))return 'data:'+mime+';base64,'+trimmed}return''}
function textFrom(obj){const value=firstProp(obj,['value','text','content','body','html','raw']);if(typeof value==='string')return value;return''}
function mediaItem(obj,path,index){if(!plainObject(obj))return null;let mime=String(firstProp(obj,['mimeType','contentType','mediaType'])||'');let type=String(firstProp(obj,['outputType','returnType','type','kind'])||'');let name=String(firstProp(obj,['fileName','filename','name','title','path'])||path||'');let src=sourceFrom(obj,mime);if(!mime&&isDataUrl(src))mime=mimeFromDataUrl(src);let text=textFrom(obj);let value=firstProp(obj,['value','data','content']);let kind=inferKind(type,mime,name,src,text||value);if(!kind)return null;if((kind==='html'||kind==='text'||kind==='json')&&!text&&value!==''&&value!==undefined&&value!==null)text=typeof value==='string'?value:JSON.stringify(value,null,2);if(!mime){if(kind==='html')mime='text/html';else if(kind==='json')mime='application/json';else if(kind==='text')mime='text/plain';else if(kind==='file')mime='application/octet-stream'}return{kind,mime,name:safeDownloadName(name,mime,index),src,text,path}}
function collectGeneratedContent(root){const items=[],seen=new Set();function add(item){if(!item)return;const sig=[item.kind,item.name,item.src,item.text&&item.text.slice(0,80),item.path].join('|');if(seen.has(sig)||items.length>=36)return;seen.add(sig);items.push(item)}function walk(value,path,depth){if(value==null||depth>7||items.length>=36)return;if(Array.isArray(value)){value.forEach((x,i)=>walk(x,path+'['+i+']',depth+1));return}if(plainObject(value)){add(mediaItem(value,path,items.length+1));for(const [key,next] of Object.entries(value)){if(['inputs'].includes(key))continue;walk(next,path?path+'.'+key:key,depth+1)}return}if(typeof value==='string'){const trimmed=value.trim();if(isDataUrl(trimmed)||/^(https?:|\/).+\.(png|jpe?g|gif|webp|svg|mp4|webm|mov|m4v|mp3|wav|ogg|pdf|zip|json|txt|html|csv)([?#].*)?$/i.test(trimmed)){const mime=isDataUrl(trimmed)?mimeFromDataUrl(trimmed):'';add({kind:inferKind('',mime,path,trimmed,''),mime,name:safeDownloadName(path,mime,items.length+1),src:trimmed,text:'',path})}}}walk(root,'output',0);return items}
function makeDownloadUrl(item){if(item.src)return item.src;if(item.text!=null){try{return URL.createObjectURL(new Blob([item.text],{type:item.mime||'text/plain'}))}catch{}}return''}
function renderGeneratedCard(item,index){const card=document.createElement('article');card.className='preview-card';const title=document.createElement('h3');title.textContent=item.name||('Output '+index);card.appendChild(title);const meta=document.createElement('small');meta.textContent=(item.kind||'file')+(item.mime?' | '+item.mime:'')+(item.path?' | '+item.path:'');card.appendChild(meta);const media=document.createElement('div');media.className='preview-media';if(item.kind==='image'&&item.src){const img=document.createElement('img');img.alt=item.name;img.src=item.src;media.appendChild(img)}else if(item.kind==='video'&&item.src){const video=document.createElement('video');video.controls=true;video.src=item.src;media.appendChild(video)}else if(item.kind==='audio'&&item.src){const audio=document.createElement('audio');audio.controls=true;audio.src=item.src;media.appendChild(audio)}else if(item.kind==='html'&&(item.text||item.src)){const frame=document.createElement('iframe');frame.className='preview-frame';frame.sandbox='allow-forms allow-popups allow-scripts';if(item.src)frame.src=item.src;else frame.srcdoc=item.text;media.appendChild(frame)}else if(item.kind==='json'||item.kind==='text'){const pre=document.createElement('pre');pre.className=item.kind==='json'?'preview-json':'preview-text';pre.textContent=item.text||'';media.appendChild(pre)}else{const icon=document.createElement('div');icon.className='preview-file-icon';icon.textContent=(item.kind||'file').slice(0,4).toUpperCase();media.appendChild(icon)}card.appendChild(media);const actions=document.createElement('div');actions.className='preview-actions';const url=makeDownloadUrl(item);if(url){const a=document.createElement('a');a.href=url;a.download=item.name||('agent-output-'+index);a.textContent='Download';actions.appendChild(a);const open=document.createElement('a');open.href=url;open.target='_blank';open.rel='noopener';open.textContent='Open';actions.appendChild(open)}card.appendChild(actions);return card}
function attachSchemaResult(data,schemaText){schemaText=String(schemaText||'').trim();if(!schemaText)return data;const result=data&&data.result?data.result:data;if(plainObject(result))result.renderedOutput={outputType:'html',mimeType:'text/html',fileName:'builder-output.html',value:schemaText};return data}
function renderOutputPreview(target,data){target.textContent='';const result=data&&data.result?data.result:data;const content=result&&Object.prototype.hasOwnProperty.call(result,'output')?result.output:result;const items=collectGeneratedContent(content).concat(collectGeneratedContent(result&&result.renderedOutput?result.renderedOutput:null)).concat(collectGeneratedContent(result&&result.nodeResults?result.nodeResults:[]));const head=document.createElement('div');head.className='preview-head';const title=document.createElement('div');title.className='preview-title';title.textContent=items.length?'Generated content':'JSON output';const meta=document.createElement('div');meta.className='preview-meta';meta.textContent=(result&&result.status?result.status:'ready')+(result&&result.durationMs!=null?' | '+result.durationMs+' ms':'');head.append(title,meta);target.appendChild(head);if(items.length){const grid=document.createElement('div');grid.className='preview-grid';items.slice(0,24).forEach((item,i)=>grid.appendChild(renderGeneratedCard(item,i+1)));target.appendChild(grid)}else{const pre=document.createElement('pre');pre.className='preview-json';pre.textContent=typeof content==='string'?content:JSON.stringify(content,null,2);target.appendChild(pre)}const details=document.createElement('details');details.className='preview-raw';const summary=document.createElement('summary');summary.textContent='Raw result JSON';const raw=document.createElement('pre');raw.textContent=JSON.stringify(result,null,2);details.append(summary,raw);target.appendChild(details)}
async function readFile(input){const file=input.files&&input.files[0];if(!file)return null;const data=await new Promise((resolve,reject)=>{const r=new FileReader();r.onload=()=>resolve(r.result);r.onerror=()=>reject(r.error);r.readAsDataURL(file)});return {fileName:file.name,size:file.size,type:file.type,dataUrl:data}}
form.addEventListener('submit',async e=>{e.preventDefault();status.textContent='Running...';const inputs={};for(const el of form.elements){if(!el.name)continue;if(el.dataset.file==='true')inputs[el.name]=await readFile(el);else inputs[el.name]=el.value}try{const res=await socketJackFetch('/api/'+api,{method:'POST',headers:{'Content-Type':'application/json','Accept':'application/json'},body:JSON.stringify({inputs})});const text=await res.text();let data;try{data=JSON.parse(text)}catch{data={raw:text}}if(data&&data.renderedOutput)attachSchemaResult(data,data.renderedOutput);renderOutputPreview(out,data.result||data);status.textContent=res.ok?'Complete':'Failed'}catch(err){out.textContent=String(err&&err.message||err);status.textContent='Failed'}});
try{const raw=document.getElementById('initialResult').textContent.trim();if(raw){const initial=JSON.parse(raw);attachSchemaResult(initial,document.getElementById('initialSchema').textContent);renderOutputPreview(out,initial)}}catch{}
</script>
</body>
</html>
""".Replace("__TITLE__", HtmlEncode(workflow.Name))
            .Replace("__NAME__", HtmlEncode(workflow.Name))
            .Replace("__ROUTE__", HtmlEncode("/api/" + api.ApiName))
            .Replace("__API__", HtmlEncode(api.ApiName))
            .Replace("__FORM__", form.ToString())
            .Replace("__RESULT__", resultJson)
            .Replace("__SCHEMA_RESULT__", schemaResult);
    }
}
