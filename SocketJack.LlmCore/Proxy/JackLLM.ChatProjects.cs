using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using SocketJack.Net.Database;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    private const string ChatProjectsTableName = "JackLLMChatProjects";
    private const int ChatProjectColumnCount = 10;
    private const string ChatProjectUnsortedId = "unsorted";

    private sealed class ChatProjectRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Name { get; set; } = "Project";
        public string WorkspaceRoot { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public bool Pinned { get; set; }
        public string PinnedUtc { get; set; } = "";
        public bool Archived { get; set; }
        public string ArchivedUtc { get; set; } = "";
    }

    private Table GetChatProjectsTable()
    {
        SocketJack.Net.Database.Database db = _chatSessionData.Databases.GetOrAdd("SocketJack", _ => new SocketJack.Net.Database.Database("SocketJack"));
        Table table = db.Tables.GetOrAdd(ChatProjectsTableName, _ => new Table(ChatProjectsTableName));
        if (table.Columns == null) table.Columns = new List<Column>();
        EnsureColumn(table, 0, "Id", 96);
        EnsureColumn(table, 1, "OwnerKey", 180);
        EnsureColumn(table, 2, "Name", 240);
        EnsureColumn(table, 3, "WorkspaceRoot", 1024);
        EnsureColumn(table, 4, "CreatedUtc", 80);
        EnsureColumn(table, 5, "UpdatedUtc", 80);
        EnsureColumn(table, 6, "Pinned", 16);
        EnsureColumn(table, 7, "PinnedUtc", 80);
        EnsureColumn(table, 8, "Archived", 16);
        EnsureColumn(table, 9, "ArchivedUtc", 80);
        if (table.Rows == null) table.Rows = new List<object[]>();
        for (int i = 0; i < table.Rows.Count; i++) table.Rows[i] = NormalizeChatProjectRow(table.Rows[i]);
        return table;
    }

    private object[] NormalizeChatProjectRow(object[] row)
    {
        object[] normalized = new object[ChatProjectColumnCount];
        if (row != null)
        {
            for (int i = 0; i < Math.Min(row.Length, normalized.Length); i++) normalized[i] = row[i];
        }
        string now = DateTimeOffset.UtcNow.ToString("O");
        if (string.IsNullOrWhiteSpace(normalized[0]?.ToString())) normalized[0] = "project_" + Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(normalized[1]?.ToString())) normalized[1] = "legacy";
        if (string.IsNullOrWhiteSpace(normalized[2]?.ToString())) normalized[2] = "Project";
        normalized[3] = NormalizeChatProjectWorkspaceRoot(normalized[3]?.ToString());
        if (string.IsNullOrWhiteSpace(normalized[4]?.ToString())) normalized[4] = now;
        if (string.IsNullOrWhiteSpace(normalized[5]?.ToString())) normalized[5] = normalized[4];
        if (string.IsNullOrWhiteSpace(normalized[6]?.ToString())) normalized[6] = "false";
        if (normalized[7] == null) normalized[7] = "";
        if (string.IsNullOrWhiteSpace(normalized[8]?.ToString())) normalized[8] = "false";
        if (normalized[9] == null) normalized[9] = "";
        return normalized;
    }

    private ChatProjectRecord ChatProjectFromRow(object[] row)
    {
        row = NormalizeChatProjectRow(row);
        return new ChatProjectRecord
        {
            Id = GetRowValue(row, 0), OwnerKey = GetRowValue(row, 1), Name = GetRowValue(row, 2),
            WorkspaceRoot = GetRowValue(row, 3), CreatedUtc = GetRowValue(row, 4), UpdatedUtc = GetRowValue(row, 5),
            Pinned = ParseStoredBool(GetRowValue(row, 6), false), PinnedUtc = GetRowValue(row, 7),
            Archived = ParseStoredBool(GetRowValue(row, 8), false), ArchivedUtc = GetRowValue(row, 9)
        };
    }

    private object[] ChatProjectToRow(ChatProjectRecord project) => NormalizeChatProjectRow(new object[]
    {
        project.Id, project.OwnerKey, project.Name, project.WorkspaceRoot, project.CreatedUtc, project.UpdatedUtc,
        project.Pinned ? "true" : "false", project.PinnedUtc, project.Archived ? "true" : "false", project.ArchivedUtc
    });

    private static string NormalizeChatProjectId(string value)
    {
        string id = (value ?? "").Trim();
        return string.IsNullOrWhiteSpace(id) ? ChatProjectUnsortedId : id;
    }

    private static string NormalizeChatProjectWorkspaceRoot(string value)
    {
        string root = (value ?? "").Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(root)) return "";
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(root)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return ""; }
    }

    private static string DefaultChatProjectName(string workspaceRoot)
    {
        string root = NormalizeChatProjectWorkspaceRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(root)) return "Project";
        string name = Path.GetFileName(root);
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    private ChatProjectRecord FindChatProject(string ownerKey, string projectId, bool includeArchived = true)
    {
        projectId = NormalizeChatProjectId(projectId);
        if (projectId == ChatProjectUnsortedId) return null;
        Table table = GetChatProjectsTable();
        foreach (object[] row in table.Rows)
        {
            ChatProjectRecord project = ChatProjectFromRow(row);
            if (ChatOwnerKeysMatch(ownerKey, project.OwnerKey) && string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase) && (includeArchived || !project.Archived)) return project;
        }
        return null;
    }

    private ChatProjectRecord FindChatProjectByRoot(string ownerKey, string workspaceRoot)
    {
        string normalizedRoot = NormalizeChatProjectWorkspaceRoot(workspaceRoot);
        if (string.IsNullOrWhiteSpace(normalizedRoot)) return null;
        return GetChatProjectsTable().Rows.Select(ChatProjectFromRow).FirstOrDefault(project =>
            ChatOwnerKeysMatch(ownerKey, project.OwnerKey) && !project.Archived &&
            string.Equals(project.WorkspaceRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase));
    }

    private ChatProjectRecord UpsertChatProject(string ownerKey, string projectId, string name, string workspaceRoot, bool allowArchived = false)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        workspaceRoot = NormalizeChatProjectWorkspaceRoot(workspaceRoot);
        string now = DateTimeOffset.UtcNow.ToString("O");
        Table table = GetChatProjectsTable();
        int index = -1;
        ChatProjectRecord project = null;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            ChatProjectRecord candidate = ChatProjectFromRow(table.Rows[i]);
            bool sameId = !string.IsNullOrWhiteSpace(projectId) && string.Equals(candidate.Id, projectId, StringComparison.OrdinalIgnoreCase);
            bool sameRoot = !string.IsNullOrWhiteSpace(workspaceRoot) && string.Equals(candidate.WorkspaceRoot, workspaceRoot, StringComparison.OrdinalIgnoreCase);
            if (ChatOwnerKeysMatch(ownerKey, candidate.OwnerKey) && (sameId || sameRoot)) { index = i; project = candidate; break; }
        }
        if (project == null)
        {
            string requestedName = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(workspaceRoot) && table.Rows.Select(ChatProjectFromRow).Any(item => ChatOwnerKeysMatch(ownerKey, item.OwnerKey) && string.IsNullOrWhiteSpace(item.WorkspaceRoot) && string.Equals(item.Name, requestedName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A pathless project with that name already exists.");
            project = new ChatProjectRecord { Id = string.IsNullOrWhiteSpace(projectId) ? "project_" + Guid.NewGuid().ToString("N") : projectId.Trim(), OwnerKey = ownerKey, CreatedUtc = now };
        }
        if (!allowArchived && project.Archived) throw new InvalidOperationException("Archived projects cannot receive chats.");
        project.Name = string.IsNullOrWhiteSpace(name) ? (string.IsNullOrWhiteSpace(project.Name) || project.Name == "Project" ? DefaultChatProjectName(workspaceRoot) : project.Name) : name.Trim();
        project.WorkspaceRoot = workspaceRoot;
        project.UpdatedUtc = now;
        if (index >= 0) table.Rows[index] = ChatProjectToRow(project); else table.Rows.Add(ChatProjectToRow(project));
        return project;
    }

    private void EnsureChatProjectAssignmentsMigrated(string ownerKey)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        bool changed = false;
        lock (_chatSessionLock)
        {
            Table sessions = GetChatSessionsTable();
            Table workspaces = GetDeveloperProjectWorkspacesTable();
            foreach (object[] source in sessions.Rows)
            {
                object[] row = NormalizeChatSessionRow(source);
                if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 7)) || !string.Equals(GetRowValue(row, 23), ChatProjectUnsortedId, StringComparison.OrdinalIgnoreCase)) continue;
                string sessionId = GetRowValue(row, 0);
                DeveloperProjectWorkspaceRecord workspace = workspaces.Rows.Select(DeveloperProjectWorkspaceRecordFromRow).FirstOrDefault(item =>
                    string.Equals(item.SessionId, sessionId, StringComparison.Ordinal) && ChatOwnerKeysMatch(ownerKey, item.OwnerKey) && !string.IsNullOrWhiteSpace(item.WorkspaceRoot));
                if (workspace == null) continue;
                ChatProjectRecord project = FindChatProjectByRoot(ownerKey, workspace.WorkspaceRoot) ?? UpsertChatProject(ownerKey, "", workspace.Name, workspace.WorkspaceRoot);
                row[23] = project.Id;
                int index = sessions.Rows.IndexOf(source);
                if (index >= 0) sessions.Rows[index] = row;
                changed = true;
            }
            if (changed) SaveChatSessionDataAndInvalidateCaches();
        }
    }

    private string ResolveAssignableChatProjectId(string ownerKey, string requestedProjectId, string workspaceRoot = "")
    {
        string projectId = NormalizeChatProjectId(requestedProjectId);
        if (projectId != ChatProjectUnsortedId)
        {
            ChatProjectRecord existing = FindChatProject(ownerKey, projectId, includeArchived: false);
            if (existing == null) throw new InvalidOperationException("Project was not found or is archived.");
            return existing.Id;
        }
        string normalizedRoot = NormalizeChatProjectWorkspaceRoot(workspaceRoot);
        if (!string.IsNullOrWhiteSpace(normalizedRoot))
        {
            ChatProjectRecord byRoot = FindChatProjectByRoot(ownerKey, normalizedRoot);
            if (byRoot != null) return byRoot.Id;
            if (IsDeveloperProjectWorkspaceRootAccessible(ownerKey, normalizedRoot)) return UpsertChatProject(ownerKey, "", DefaultChatProjectName(normalizedRoot), normalizedRoot).Id;
        }
        return ChatProjectUnsortedId;
    }

    private object BuildChatProjectPayload(ChatProjectRecord project, int sessionCount, string lastActivityUtc)
    {
        if (project == null) return new { id = ChatProjectUnsortedId, name = "Unsorted", workspaceRoot = "", createdUtc = "", updatedUtc = lastActivityUtc ?? "", pinned = false, pinnedUtc = "", archived = false, archivedUtc = "", sessionCount, lastActivityUtc = lastActivityUtc ?? "", builtIn = true };
        return new { id = project.Id, name = project.Name, workspaceRoot = project.WorkspaceRoot, createdUtc = project.CreatedUtc, updatedUtc = project.UpdatedUtc, pinned = project.Pinned, pinnedUtc = project.PinnedUtc, archived = project.Archived, archivedUtc = project.ArchivedUtc, sessionCount, lastActivityUtc = lastActivityUtc ?? "", builtIn = false };
    }

    private string HandleChatProjectsListRequest(NetworkConnection connection, HttpRequest request)
    {
        try
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            bool includeArchived = ParseStoredBool(GetQueryParameter(request, "includeArchived"), false);
            EnsureChatProjectAssignmentsMigrated(ownerKey);
            List<ChatProjectRecord> projects;
            Dictionary<string, (int Count, string Last)> activity = new(StringComparer.OrdinalIgnoreCase);
            lock (_chatSessionLock)
            {
                projects = GetChatProjectsTable().Rows.Select(ChatProjectFromRow).Where(project => ChatOwnerKeysMatch(ownerKey, project.OwnerKey) && (includeArchived || !project.Archived)).ToList();
                foreach (object[] source in GetChatSessionsTable().Rows)
                {
                    object[] row = NormalizeChatSessionRow(source);
                    if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 7))) continue;
                    string projectId = NormalizeChatProjectId(GetRowValue(row, 23));
                    activity.TryGetValue(projectId, out var current);
                    string updated = GetRowValue(row, 3);
                    activity[projectId] = (current.Count + 1, string.CompareOrdinal(updated, current.Last) > 0 ? updated : current.Last);
                }
            }
            var payload = projects.Select(project =>
            {
                activity.TryGetValue(project.Id, out var value);
                return BuildChatProjectPayload(project, value.Count, value.Last);
            }).ToList();
            activity.TryGetValue(ChatProjectUnsortedId, out var unsorted);
            payload.Add(BuildChatProjectPayload(null, unsorted.Count, unsorted.Last));
            payload.Sort((left, right) =>
            {
                JsonElement l = JsonSerializer.SerializeToElement(left); JsonElement r = JsonSerializer.SerializeToElement(right);
                int pin = ReadJsonBool(r.GetProperty("pinned"), false).CompareTo(ReadJsonBool(l.GetProperty("pinned"), false));
                if (pin != 0) return pin;
                return string.CompareOrdinal(ExtractStringProperty(r, "lastActivityUtc") ?? ExtractStringProperty(r, "updatedUtc"), ExtractStringProperty(l, "lastActivityUtc") ?? ExtractStringProperty(l, "updatedUtc"));
            });
            return JsonSerializer.Serialize(new { ok = true, ownerKey, count = payload.Count, projects = payload });
        }
        catch (Exception ex) { return BuildJsonError(request, 500, "Internal Server Error", ex.Message); }
    }

    private string HandleChatProjectMutationRequest(NetworkConnection connection, HttpRequest request)
    {
        try
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            JsonElement root = document.RootElement;
            string action = (ExtractStringProperty(root, "action") ?? "create").Trim().ToLowerInvariant();
            string projectId = (ExtractStringProperty(root, "projectId") ?? ExtractStringProperty(root, "id") ?? "").Trim();
            if (projectId == ChatProjectUnsortedId) return BuildJsonError(request, 400, "Bad Request", "The Unsorted project cannot be modified.");
            string now = DateTimeOffset.UtcNow.ToString("O");
            ChatProjectRecord saved;
            lock (_chatSessionLock)
            {
                if (action is "create" or "upsert")
                {
                    string rootPath = ExtractStringProperty(root, "workspaceRoot") ?? "";
                    string normalizedRoot = NormalizeChatProjectWorkspaceRoot(rootPath);
                    if (!string.IsNullOrWhiteSpace(normalizedRoot) && !IsDeveloperProjectWorkspaceRootAccessible(ownerKey, normalizedRoot) && !IsDatabaseAdministrator(connection, request)) return BuildJsonError(request, 403, "Forbidden", "The workspace root is not approved for this owner.");
                    saved = UpsertChatProject(ownerKey, projectId, ExtractStringProperty(root, "name"), normalizedRoot);
                }
                else
                {
                    saved = FindChatProject(ownerKey, projectId, includeArchived: true);
                    if (saved == null) return BuildJsonError(request, 404, "Not Found", "Project was not found.");
                    switch (action)
                    {
                        case "rename":
                            string name = (ExtractStringProperty(root, "name") ?? "").Trim();
                            if (string.IsNullOrWhiteSpace(name)) return BuildJsonError(request, 400, "Bad Request", "Project name is required.");
                            if (string.IsNullOrWhiteSpace(saved.WorkspaceRoot) && GetChatProjectsTable().Rows.Select(ChatProjectFromRow).Any(item => ChatOwnerKeysMatch(ownerKey, item.OwnerKey) && item.Id != saved.Id && string.IsNullOrWhiteSpace(item.WorkspaceRoot) && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                                return BuildJsonError(request, 409, "Conflict", "A pathless project with that name already exists.");
                            saved.Name = name; break;
                        case "pin": saved.Pinned = true; saved.PinnedUtc = now; break;
                        case "unpin": saved.Pinned = false; saved.PinnedUtc = ""; break;
                        case "archive": saved.Archived = true; saved.ArchivedUtc = now; saved.Pinned = false; saved.PinnedUtc = ""; break;
                        case "restore": saved.Archived = false; saved.ArchivedUtc = ""; break;
                        default: return BuildJsonError(request, 400, "Bad Request", "Unknown project action.");
                    }
                    saved.UpdatedUtc = now;
                    Table table = GetChatProjectsTable();
                    for (int i = 0; i < table.Rows.Count; i++) if (string.Equals(GetRowValue(table.Rows[i], 0), saved.Id, StringComparison.OrdinalIgnoreCase)) { table.Rows[i] = ChatProjectToRow(saved); break; }
                }
                SaveChatSessionDataAndInvalidateCaches();
            }
            return JsonSerializer.Serialize(new { ok = true, project = BuildChatProjectPayload(saved, 0, saved.UpdatedUtc) });
        }
        catch (InvalidOperationException ex) { return BuildJsonError(request, 409, "Conflict", ex.Message); }
        catch (Exception ex) { return BuildJsonError(request, 500, "Internal Server Error", ex.Message); }
    }
}
