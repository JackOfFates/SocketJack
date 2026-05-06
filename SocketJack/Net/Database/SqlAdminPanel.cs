using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace SocketJack.Net.Database {

    /// <summary>
    /// An HTTP-based SQL administration panel (similar to SQL Server Management Studio)
    /// that can be enabled on a <see cref="MutableTcpServer"/>. When enabled, it registers
    /// HTTP routes under <c>/sql</c> that provide:
    /// <list type="bullet">
    /// <item>A login page authenticated against <see cref="DataServer.Users"/>.</item>
    /// <item>An Object Explorer tree showing databases, tables and columns.</item>
    /// <item>A SQL query editor with syntax highlighting.</item>
    /// <item>A results grid for query output.</item>
    /// </list>
    /// <para>
    /// Enable via <see cref="MutableTcpServer.SqlAdminPanelEnabled"/> or access the
    /// panel instance through <see cref="MutableTcpServer.SqlAdminPanel"/>.
    /// </para>
    /// </summary>
    internal class SqlAdminPanel {

        private readonly MutableTcpServer _server;
        private readonly ConcurrentDictionary<string, SqlAdminSession> _sessions = new ConcurrentDictionary<string, SqlAdminSession>();
        private readonly ConcurrentDictionary<string, ApiEndpointDef> _apiEndpoints = new ConcurrentDictionary<string, ApiEndpointDef>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, ApiEndpointDef> _apiRouteSettings = new ConcurrentDictionary<string, ApiEndpointDef>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, EventDef> _eventDefs = new ConcurrentDictionary<string, EventDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _pageTemplateCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, string> _pageHashCache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _registered;

        /// <summary>
        /// Directory containing HTML page templates for the SQL Admin Panel.
        /// Templates use <c>$VariableName</c> placeholders that are replaced at runtime.
        /// Defaults to a <c>Resources</c> folder next to the running application.
        /// </summary>
        internal static string PagesFolder { get; set; } = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources");

        /// <summary>
        /// Base URL path for all admin panel routes.
        /// </summary>
        internal string BasePath { get; set; } = "/sql";

        internal SqlAdminPanel(MutableTcpServer server) {
            _server = server ?? throw new ArgumentNullException(nameof(server));
        }

        #region Route Registration

        internal void Register() {
            if (_registered) return;
            _registered = true;

            var basePath = BasePath.TrimEnd('/');

            // Main page (login or panel)
            _server.Map("GET", basePath, (conn, req, ct) => ServePage(req));
            _server.Map("GET", basePath + "/", (conn, req, ct) => ServePage(req));

            // Login / Logout
            _server.Map("POST", basePath + "/login", (conn, req, ct) => HandleLogin(req));
            _server.Map("GET", basePath + "/logout", (conn, req, ct) => HandleLogout(req));

            // API endpoints
            _server.Map("GET", basePath + "/api/databases", (conn, req, ct) => ApiDatabases(req));
            _server.Map("GET", basePath + "/api/tables", (conn, req, ct) => ApiTables(req));
            _server.Map("GET", basePath + "/api/columns", (conn, req, ct) => ApiColumns(req));
            _server.Map("GET", basePath + "/api/rows", (conn, req, ct) => ApiRows(req));
            _server.Map("POST", basePath + "/api/query", (conn, req, ct) => ApiQuery(req));
            _server.Map("GET", basePath + "/api/users", (conn, req, ct) => ApiUsers(req));
            _server.Map("GET", basePath + "/api/sessions", (conn, req, ct) => ApiSessions(req));

            // Table Designer API endpoints
            _server.Map("POST", basePath + "/api/designer/create-table", (conn, req, ct) => ApiDesignerCreateTable(req));
            _server.Map("POST", basePath + "/api/designer/drop-table", (conn, req, ct) => ApiDesignerDropTable(req));
            _server.Map("POST", basePath + "/api/designer/rename-table", (conn, req, ct) => ApiDesignerRenameTable(req));
            _server.Map("POST", basePath + "/api/designer/save-schema", (conn, req, ct) => ApiDesignerSaveSchema(req));
            _server.Map("POST", basePath + "/api/designer/add-column", (conn, req, ct) => ApiDesignerAddColumn(req));
            _server.Map("POST", basePath + "/api/designer/remove-column", (conn, req, ct) => ApiDesignerRemoveColumn(req));
            _server.Map("POST", basePath + "/api/designer/update-column", (conn, req, ct) => ApiDesignerUpdateColumn(req));
            _server.Map("POST", basePath + "/api/designer/insert-row", (conn, req, ct) => ApiDesignerInsertRow(req));
            _server.Map("POST", basePath + "/api/designer/update-cell", (conn, req, ct) => ApiDesignerUpdateCell(req));
            _server.Map("POST", basePath + "/api/designer/delete-row", (conn, req, ct) => ApiDesignerDeleteRow(req));
            _server.Map("GET", basePath + "/api/designer/viewport", (conn, req, ct) => ApiDesignerViewport(req));

            // API Creator endpoints
            _server.Map("GET", basePath + "/api/endpoints/list", (conn, req, ct) => ApiEndpointsList(req));
            _server.Map("POST", basePath + "/api/endpoints/save", (conn, req, ct) => ApiEndpointsSave(req));
            _server.Map("POST", basePath + "/api/endpoints/delete", (conn, req, ct) => ApiEndpointsDelete(req));
            _server.Map("POST", basePath + "/api/endpoints/test", (conn, req, ct) => ApiEndpointsTest(req));
            _server.Map("POST", basePath + "/api/endpoints/settings/save", (conn, req, ct) => ApiEndpointSettingsSave(req));

            // Query Builder saved trees
            _server.Map("GET", basePath + "/api/qb/list-trees", (conn, req, ct) => QbListTrees(req));
            _server.Map("GET", basePath + "/api/qb/load-tree", (conn, req, ct) => QbLoadTree(req));
            _server.Map("POST", basePath + "/api/qb/save-tree", (conn, req, ct) => QbSaveTree(req));
            _server.Map("POST", basePath + "/api/qb/delete-tree", (conn, req, ct) => QbDeleteTree(req));

            // Events system
            _server.Map("GET", basePath + "/api/events/list", (conn, req, ct) => ApiEventsList(req));
            _server.Map("POST", basePath + "/api/events/save", (conn, req, ct) => ApiEventsSave(req));
            _server.Map("POST", basePath + "/api/events/delete", (conn, req, ct) => ApiEventsDelete(req));
            _server.Map("POST", basePath + "/api/events/execute", (conn, req, ct) => ApiEventsExecute(req));
            _server.Map("GET", basePath + "/api/events/reflect", (conn, req, ct) => ApiEventsReflect(req));

            // Load and register saved API endpoints
            LoadApiEndpoints();
            LoadApiRouteSettings();
            LoadEventDefs();
        }

        internal void Unregister() {
            if (!_registered) return;
            _registered = false;

            var basePath = BasePath.TrimEnd('/');
            _server.RemoveRoute("GET", basePath);
            _server.RemoveRoute("GET", basePath + "/");
            _server.RemoveRoute("POST", basePath + "/login");
            _server.RemoveRoute("GET", basePath + "/logout");
            _server.RemoveRoute("GET", basePath + "/api/databases");
            _server.RemoveRoute("GET", basePath + "/api/tables");
            _server.RemoveRoute("GET", basePath + "/api/columns");
            _server.RemoveRoute("GET", basePath + "/api/rows");
            _server.RemoveRoute("POST", basePath + "/api/query");
            _server.RemoveRoute("GET", basePath + "/api/users");
            _server.RemoveRoute("GET", basePath + "/api/sessions");

            // Table Designer routes
            _server.RemoveRoute("POST", basePath + "/api/designer/create-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/drop-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/rename-table");
            _server.RemoveRoute("POST", basePath + "/api/designer/save-schema");
            _server.RemoveRoute("POST", basePath + "/api/designer/add-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/remove-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/update-column");
            _server.RemoveRoute("POST", basePath + "/api/designer/insert-row");
            _server.RemoveRoute("POST", basePath + "/api/designer/update-cell");
            _server.RemoveRoute("POST", basePath + "/api/designer/delete-row");
            _server.RemoveRoute("GET", basePath + "/api/designer/viewport");

            // API Creator routes
            _server.RemoveRoute("GET", basePath + "/api/endpoints/list");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/save");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/delete");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/test");
            _server.RemoveRoute("POST", basePath + "/api/endpoints/settings/save");

            // Query Builder tree routes
            _server.RemoveRoute("GET", basePath + "/api/qb/list-trees");
            _server.RemoveRoute("GET", basePath + "/api/qb/load-tree");
            _server.RemoveRoute("POST", basePath + "/api/qb/save-tree");
            _server.RemoveRoute("POST", basePath + "/api/qb/delete-tree");

            // Events routes
            _server.RemoveRoute("GET", basePath + "/api/events/list");
            _server.RemoveRoute("POST", basePath + "/api/events/save");
            _server.RemoveRoute("POST", basePath + "/api/events/delete");
            _server.RemoveRoute("POST", basePath + "/api/events/execute");
            _server.RemoveRoute("GET", basePath + "/api/events/reflect");

            // Remove dynamic API routes
            foreach (var ep in _apiEndpoints.Values) {
                try { _server.RemoveRoute(ep.HttpMethod.ToUpperInvariant(), ep.Route); } catch { }
            }
            _apiEndpoints.Clear();
            _apiRouteSettings.Clear();
            _eventDefs.Clear();

            _sessions.Clear();
        }

        #endregion

        #region Session Management

        private class SqlAdminSession {
            public string Token;
            public string Username;
            public DateTime Created;
            public DateTime LastActivity;
            public string CurrentDatabase;
        }

        private class ApiEndpointDef {
            public string Id;
            public string Name;
            public string Route;
            public string HttpMethod;
            public string Database;
            public string SqlQuery;
            public string QuerySteps; // JSON array of query steps
            public string ResponseFormat;
            public string ContentType;
            public string Variables; // Comma-separated list of $variable names
            public bool Enabled;
            public string BodyType;
            public string BodySchema;
            public string Parameters;
            public string OutputSchema;
            public string Description;
            public string Source;
            public bool ReadOnly;
        }

        /// <summary>
        /// Represents a saved event definition.
        /// Each event has an array of action nodes serialized as JSON.
        /// </summary>
        private class EventDef {
            public string Id;
            public string Name;
            public string Description;
            public bool Enabled;
            public string Nodes; // JSON – array of node objects (trigger, action, condition, etc.)
        }

        private string CreateSession(string username) {
            var token = GenerateToken();
            var session = new SqlAdminSession {
                Token = token,
                Username = username,
                Created = DateTime.UtcNow,
                LastActivity = DateTime.UtcNow,
                CurrentDatabase = "db"
            };
            _sessions[token] = session;
            return token;
        }

        private SqlAdminSession GetSession(HttpRequest req) {
            var cookie = GetCookie(req, "sqladmin_token");
            if (string.IsNullOrEmpty(cookie)) return null;
            if (_sessions.TryGetValue(cookie, out var session)) {
                // Expire sessions after 8 hours of inactivity
                if ((DateTime.UtcNow - session.LastActivity).TotalHours > 8) {
                    _sessions.TryRemove(cookie, out _);
                    return null;
                }
                session.LastActivity = DateTime.UtcNow;
                return session;
            }
            return null;
        }

        private static string GetCookie(HttpRequest req, string name) {
            if (req?.Headers == null) return null;
            if (!req.Headers.TryGetValue("Cookie", out var cookieHeader)) return null;
            if (string.IsNullOrEmpty(cookieHeader)) return null;
            foreach (var part in cookieHeader.Split(';')) {
                var trimmed = part.Trim();
                if (trimmed.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) {
                    return trimmed.Substring(name.Length + 1);
                }
            }
            return null;
        }

        private static string GenerateToken() {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) {
                rng.GetBytes(bytes);
            }
            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        }

        #endregion

        #region DataServer Access

        private DataServer GetDataServer() {
            // Use reflection-free access through the MutableTcpServer's helper
            // by searching registered handlers for the TdsProtocolHandler.
            return FindDataServer();
        }

        private DataServer FindDataServer() {
            // Access the _handlers list through the public Http property's server
            // The MutableTcpServer has a FindDataServer method, but it's private.
            // We replicate the logic here.
            try {
                var field = typeof(MutableTcpServer).GetField("_handlers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (field != null) {
                    var handlers = field.GetValue(_server) as List<IProtocolHandler>;
                    if (handlers != null) {
                        for (int i = 0; i < handlers.Count; i++) {
                            if (handlers[i] is TdsProtocolHandler tds)
                                return tds.Server;
                        }
                    }
                }
            } catch { }
            return null;
        }

        private bool Authenticate(string username, string password) {
            var ds = GetDataServer();
            if (ds == null) return false;
            return ds.Authenticate(username, password);
        }

        #endregion

        #region Route Handlers

        private object ServePage(HttpRequest req) {
            var session = GetSession(req);
            req.Context.ContentType = "text/html";
            if (session == null) {
                return LoginPageHtml();
            }
            return PanelPageHtml(session);
        }

        private object HandleLogin(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var body = req.Body ?? "";
            string username = null, password = null;

            // Parse JSON body: {"username":"...","password":"..."}
            // Simple manual parse to avoid dependency on System.Text.Json (netstandard2.1 compat)
            username = ExtractJsonString(body, "username");
            password = ExtractJsonString(body, "password");

            if (string.IsNullOrEmpty(username)) {
                req.Context.StatusCode = "401 Unauthorized";
                return "{\"error\":\"Username is required.\"}";
            }

            if (!Authenticate(username, password ?? "")) {
                req.Context.StatusCode = "401 Unauthorized";
                return "{\"error\":\"Invalid username or password.\"}";
            }

            var token = CreateSession(username);
            var resp = req.Context.Response;
            resp.Headers["Set-Cookie"] = "sqladmin_token=" + token + "; Path=" + BasePath + "; HttpOnly; SameSite=Strict";
            return "{\"success\":true,\"username\":\"" + EscapeJson(username) + "\"}";
        }

        private object HandleLogout(HttpRequest req) {
            var cookie = GetCookie(req, "sqladmin_token");
            if (!string.IsNullOrEmpty(cookie))
                _sessions.TryRemove(cookie, out _);
            req.Context.ContentType = "text/html";
            var resp = req.Context.Response;
            resp.Headers["Set-Cookie"] = "sqladmin_token=; Path=" + BasePath + "; HttpOnly; SameSite=Strict; Max-Age=0";
            resp.Headers["Location"] = BasePath;
            req.Context.StatusCode = "302 Found";
            return "<html><body>Redirecting...</body></html>";
        }

        private object ApiDatabases(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"databases\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"databases\":[");
            bool first = true;
            foreach (var kvp in ds.Databases.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(kvp.Key)).Append("\",\"tableCount\":").Append(kvp.Value.Tables.Count).Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiTables(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"tables\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"tables\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName)).Append("\",\"tables\":[");
            bool first = true;
            foreach (var kvp in db.Tables.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(kvp.Key))
                  .Append("\",\"columnCount\":").Append(kvp.Value.Columns.Count)
                  .Append(",\"rowCount\":").Append(kvp.Value.Rows.Count)
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiColumns(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            if (tableName == null) { return "{\"columns\":[]}"; }

            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"columns\":[]}"; }
            if (!db.Tables.TryGetValue(tableName, out var table)) { return "{\"columns\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"columns\":[");
            bool first = true;
            foreach (var col in table.Columns) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"name\":\"").Append(EscapeJson(col.Name))
                  .Append("\",\"type\":\"").Append(EscapeJson(col.DataType?.Name ?? "string"))
                  .Append("\",\"maxLength\":").Append(col.MaxLength)
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiRows(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[],\"rows\":[]}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            if (tableName == null) { return "{\"columns\":[],\"rows\":[]}"; }

            int top = 200;
            if (req.QueryParameters.ContainsKey("top") && int.TryParse(req.QueryParameters["top"], out var parsedTop))
                top = Math.Min(Math.Max(parsedTop, 1), 10000);

            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"columns\":[],\"rows\":[]}"; }
            if (!db.Tables.TryGetValue(tableName, out var table)) { return "{\"columns\":[],\"rows\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"totalRows\":").Append(table.Rows.Count)
              .Append(",\"columns\":[");
            for (int c = 0; c < table.Columns.Count; c++) {
                if (c > 0) sb.Append(",");
                sb.Append("\"").Append(EscapeJson(table.Columns[c].Name)).Append("\"");
            }
            sb.Append("],\"rows\":[");
            int rowCount = Math.Min(table.Rows.Count, top);
            for (int r = 0; r < rowCount; r++) {
                if (r > 0) sb.Append(",");
                sb.Append("[");
                var row = table.Rows[r];
                for (int c = 0; c < row.Length; c++) {
                    if (c > 0) sb.Append(",");
                    if (row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("]");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiQuery(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var sql = ExtractJsonString(body, "query");
            var dbName = ExtractJsonString(body, "database");
            if (!string.IsNullOrEmpty(dbName)) session.CurrentDatabase = dbName;

            if (string.IsNullOrWhiteSpace(sql)) {
                return "{\"error\":\"No query provided.\"}";
            }

            // Create a temporary SqlSession for query execution
            var sqlSession = new SqlSession {
                ConnectionId = Guid.NewGuid(),
                Username = session.Username,
                CurrentDatabase = session.CurrentDatabase ?? "db",
                ServerName = ds.ServerName,
                ServerVersion = ds.ServerVersion,
                IsAuthenticated = true
            };

            try {
                var result = ds.ExecuteQuery(sqlSession, sql);

                // If the QueryExecuting event didn't handle the query,
                // fall back to a basic in-memory table query.
                if (!result.HasResultSet && result.Columns.Count == 0 && result.Rows.Count == 0 && result.RowsAffected == 0) {
                    result = ExecuteInMemoryQuery(ds, sqlSession, sql);
                }

                var sb = new StringBuilder();
                sb.Append("{");

                if (result.HasResultSet && result.Columns.Count > 0) {
                    sb.Append("\"columns\":[");
                    for (int c = 0; c < result.Columns.Count; c++) {
                        if (c > 0) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(result.Columns[c])).Append("\"");
                    }
                    sb.Append("],\"rows\":[");
                    for (int r = 0; r < result.Rows.Count; r++) {
                        if (r > 0) sb.Append(",");
                        sb.Append("[");
                        var row = result.Rows[r];
                        for (int c = 0; c < row.Length; c++) {
                            if (c > 0) sb.Append(",");
                            if (row[c] == null) {
                                sb.Append("null");
                            } else {
                                sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                            }
                        }
                        sb.Append("]");
                    }
                    sb.Append("],\"rowsAffected\":").Append(result.RowsAffected);
                } else {
                    sb.Append("\"rowsAffected\":").Append(result.RowsAffected);
                    sb.Append(",\"message\":\"Query executed successfully.\"");
                }

                sb.Append("}");
                return sb.ToString();
            } catch (Exception ex) {
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// Basic in-memory SQL handler for SELECT, INSERT, UPDATE, DELETE,
        /// CREATE TABLE, and DROP TABLE when no <see cref="DataServer.QueryExecuting"/>
        /// handler is wired up.
        /// </summary>
        private static QueryResult ExecuteInMemoryQuery(DataServer ds, SqlSession session, string sql) {
            var result = new QueryResult();
            // Normalize whitespace so keyword searches (e.g. " FROM ") match
            // regardless of whether the SQL uses spaces, tabs or newlines.
            string trimmed = sql.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            string upper = trimmed.ToUpperInvariant();

            // ---- USE database ----
            if (upper.StartsWith("USE ")) {
                var dbName = trimmed.Substring(4).Trim().TrimEnd(';').Trim();
                dbName = StripBrackets(dbName);
                if (ds.Databases.ContainsKey(dbName)) {
                    session.CurrentDatabase = dbName;
                    result.RowsAffected = 0;
                } else {
                    throw new Exception("Database '" + dbName + "' does not exist.");
                }
                return result;
            }

            // ---- SELECT ----
            if (upper.StartsWith("SELECT")) {
                // Parse TOP N
                int topN = int.MaxValue;
                string afterSelect = trimmed.Substring(6).TrimStart();
                string afterSelectUpper = afterSelect.ToUpperInvariant();
                if (afterSelectUpper.StartsWith("TOP ")) {
                    afterSelect = afterSelect.Substring(4).TrimStart();
                    // Handle TOP (N) or TOP N
                    string numStr;
                    if (afterSelect.StartsWith("(")) {
                        int closeParen = afterSelect.IndexOf(')');
                        if (closeParen > 0) {
                            numStr = afterSelect.Substring(1, closeParen - 1).Trim();
                            afterSelect = afterSelect.Substring(closeParen + 1).TrimStart();
                        } else {
                            numStr = "";
                        }
                    } else {
                        int spaceIdx = afterSelect.IndexOf(' ');
                        numStr = spaceIdx > 0 ? afterSelect.Substring(0, spaceIdx) : afterSelect;
                        afterSelect = spaceIdx > 0 ? afterSelect.Substring(spaceIdx).TrimStart() : "";
                    }
                    if (int.TryParse(numStr, out var parsed)) topN = parsed;
                }

                // Find table name from the FROM clause
                string tableName = null;
                string dbQualifier = null;
                int fromIdx = upper.IndexOf(" FROM ", StringComparison.Ordinal);
                if (fromIdx >= 0) {
                    string afterFrom = trimmed.Substring(fromIdx + 6).TrimStart();
                    tableName = ParseTableName(afterFrom, out dbQualifier);
                }

                if (tableName == null) {
                    // Could be something like SELECT 1, SELECT @@VERSION, etc.
                    result.HasResultSet = true;
                    result.Columns.Add("result");
                    result.RowsAffected = 0;
                    return result;
                }

                Database db;
                var table = ResolveTable(ds, session.CurrentDatabase, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Parse column list (between SELECT [TOP N] and FROM)
                string colsPart = null;
                string selectSection = trimmed.Substring(6); // after "SELECT"
                int fromInSelect = selectSection.ToUpperInvariant().IndexOf(" FROM ", StringComparison.Ordinal);
                if (fromInSelect >= 0) {
                    colsPart = selectSection.Substring(0, fromInSelect).Trim();
                    // Strip TOP N from colsPart
                    string colsUpper = colsPart.ToUpperInvariant().TrimStart();
                    if (colsUpper.StartsWith("TOP ")) {
                        colsPart = colsPart.TrimStart();
                        colsPart = colsPart.Substring(4).TrimStart();
                        if (colsPart.StartsWith("(")) {
                            int cp = colsPart.IndexOf(')');
                            if (cp > 0) colsPart = colsPart.Substring(cp + 1).TrimStart();
                        } else {
                            int sp = colsPart.IndexOf(' ');
                            if (sp > 0) colsPart = colsPart.Substring(sp).TrimStart();
                            else colsPart = "*";
                        }
                    }
                }

                bool selectAll = string.IsNullOrWhiteSpace(colsPart) || colsPart.Trim() == "*";
                int[] colIndices;

                if (selectAll) {
                    result.Columns.AddRange(table.Columns.ConvertAll(c => c.Name));
                    colIndices = new int[table.Columns.Count];
                    for (int i = 0; i < colIndices.Length; i++) colIndices[i] = i;
                } else {
                    var requestedCols = colsPart.Split(',');
                    var indices = new List<int>();
                    foreach (var rc in requestedCols) {
                        var colName = StripBrackets(rc.Trim().TrimEnd(';'));
                        // Handle alias: col AS alias
                        int asIdx = colName.ToUpperInvariant().IndexOf(" AS ", StringComparison.Ordinal);
                        string alias = null;
                        if (asIdx >= 0) {
                            alias = colName.Substring(asIdx + 4).Trim();
                            colName = colName.Substring(0, asIdx).Trim();
                            colName = StripBrackets(colName);
                            alias = StripBrackets(alias);
                        }
                        int ci = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                        if (ci >= 0) {
                            indices.Add(ci);
                            result.Columns.Add(alias ?? table.Columns[ci].Name);
                        }
                    }
                    colIndices = indices.ToArray();
                }

                result.HasResultSet = true;

                // Parse WHERE clause if present
                string selectWhereClause = null;
                int selectWhereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                if (selectWhereIdx >= 0) {
                    string afterWhere = trimmed.Substring(selectWhereIdx + 7);
                    // Trim trailing ORDER BY, GROUP BY, HAVING clauses
                    string afterWhereUpper = afterWhere.ToUpperInvariant();
                    int endIdx = afterWhere.Length;
                    foreach (var kw in new[] { " ORDER BY ", " GROUP BY ", " HAVING " }) {
                        int kwIdx = afterWhereUpper.IndexOf(kw, StringComparison.Ordinal);
                        if (kwIdx >= 0 && kwIdx < endIdx) endIdx = kwIdx;
                    }
                    selectWhereClause = afterWhere.Substring(0, endIdx).Trim().TrimEnd(';');
                }

                // Parse ORDER BY clause if present
                string orderByClause = null;
                int orderByIdx = upper.IndexOf(" ORDER BY ", StringComparison.Ordinal);
                if (orderByIdx >= 0) {
                    string afterOrder = trimmed.Substring(orderByIdx + 10).Trim().TrimEnd(';');
                    orderByClause = afterOrder;
                }

                // Collect all matching rows (without TOP limit) so ORDER BY works correctly
                for (int r = 0; r < table.Rows.Count; r++) {
                    var srcRow = table.Rows[r];
                    if (selectWhereClause != null && !RowMatchesWhere(table, srcRow, selectWhereClause))
                        continue;
                    var outRow = new object[colIndices.Length];
                    for (int c = 0; c < colIndices.Length; c++) {
                        int ci = colIndices[c];
                        outRow[c] = ci < srcRow.Length ? srcRow[ci] : null;
                    }
                    result.Rows.Add(outRow);
                }

                // Apply ORDER BY sorting before TOP limit
                if (!string.IsNullOrWhiteSpace(orderByClause)) {
                    ApplyOrderBy(result, table, orderByClause);
                }

                // Apply TOP N limit after sorting
                if (topN < result.Rows.Count) {
                    result.Rows.RemoveRange(topN, result.Rows.Count - topN);
                }

                result.RowsAffected = result.Rows.Count;
                return result;
            }

            // ---- INSERT ----
            if (upper.StartsWith("INSERT")) {
                string tableName = null;
                string dbQualifier = null;
                int intoIdx = upper.IndexOf("INTO ", StringComparison.Ordinal);
                if (intoIdx >= 0) {
                    string afterInto = trimmed.Substring(intoIdx + 5).TrimStart();
                    tableName = ParseTableName(afterInto, out dbQualifier);
                }
                if (tableName == null) throw new Exception("Could not parse INSERT statement.");
                Database db;
                var table = ResolveTable(ds, session.CurrentDatabase, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Parse VALUES(...)
                int valuesIdx = upper.IndexOf("VALUES", StringComparison.Ordinal);
                if (valuesIdx < 0) throw new Exception("INSERT without VALUES is not supported.");
                string valuesSection = trimmed.Substring(valuesIdx + 6).Trim();
                int openParen = valuesSection.IndexOf('(');
                int closeParen = valuesSection.LastIndexOf(')');
                if (openParen < 0 || closeParen < 0) throw new Exception("Could not parse VALUES clause.");
                string valuesList = valuesSection.Substring(openParen + 1, closeParen - openParen - 1);
                var values = SplitValuesList(valuesList);

                var newRow = new object[table.Columns.Count];
                for (int i = 0; i < Math.Min(values.Count, table.Columns.Count); i++) {
                    string v = values[i].Trim().TrimStart('N'); // handle N'...'
                    if (v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) {
                        newRow[i] = null;
                    } else if (v.StartsWith("'") && v.EndsWith("'")) {
                        newRow[i] = ConvertValue(v.Substring(1, v.Length - 2), table.Columns[i].DataType);
                    } else {
                        newRow[i] = ConvertValue(v, table.Columns[i].DataType);
                    }
                }
                table.Rows.Add(newRow);
                ds.ScheduleSave();
                result.RowsAffected = 1;
                return result;
            }

            // ---- DELETE ----
            if (upper.StartsWith("DELETE")) {
                int fromIdx = upper.IndexOf("FROM ", StringComparison.Ordinal);
                string afterFrom = fromIdx >= 0 ? trimmed.Substring(fromIdx + 5).TrimStart() : trimmed.Substring(6).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterFrom, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse DELETE statement.");
                Database db;
                var table = ResolveTable(ds, session.CurrentDatabase, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                // Simple WHERE support: DELETE FROM T WHERE col = 'value'
                int whereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                if (whereIdx >= 0) {
                    string whereClause = trimmed.Substring(whereIdx + 7).Trim().TrimEnd(';');
                    int removed = RemoveMatchingRows(table, whereClause);
                    result.RowsAffected = removed;
                } else {
                    result.RowsAffected = table.Rows.Count;
                    table.Rows.Clear();
                }
                ds.ScheduleSave();
                return result;
            }

            // ---- UPDATE ----
            if (upper.StartsWith("UPDATE")) {
                string afterUpdate = trimmed.Substring(6).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterUpdate, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse UPDATE statement.");
                Database db;
                var table = ResolveTable(ds, session.CurrentDatabase, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");

                int setIdx = upper.IndexOf(" SET ", StringComparison.Ordinal);
                if (setIdx < 0) throw new Exception("UPDATE without SET clause.");
                int whereIdx = upper.IndexOf(" WHERE ", StringComparison.Ordinal);
                string setClause = whereIdx >= 0
                    ? trimmed.Substring(setIdx + 5, whereIdx - setIdx - 5).Trim()
                    : trimmed.Substring(setIdx + 5).Trim().TrimEnd(';');

                // Parse SET assignments: col1 = val1, col2 = val2
                var assignments = ParseAssignments(setClause, table);

                int updated = 0;
                for (int r = 0; r < table.Rows.Count; r++) {
                    bool match = true;
                    if (whereIdx >= 0) {
                        string whereClause = trimmed.Substring(whereIdx + 7).Trim().TrimEnd(';');
                        match = RowMatchesWhere(table, table.Rows[r], whereClause);
                    }
                    if (match) {
                        var row = table.Rows[r];
                        if (row.Length < table.Columns.Count) {
                            var expanded = new object[table.Columns.Count];
                            Array.Copy(row, expanded, row.Length);
                            row = expanded;
                            table.Rows[r] = row;
                        }
                        foreach (var kvp in assignments) {
                            if (kvp.Key < row.Length) row[kvp.Key] = kvp.Value;
                        }
                        updated++;
                    }
                }
                ds.ScheduleSave();
                result.RowsAffected = updated;
                return result;
            }

            // ---- CREATE TABLE ----
            if (upper.StartsWith("CREATE TABLE")) {
                string afterCreate = trimmed.Substring(12).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterCreate, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse CREATE TABLE statement.");
                Database db;
                if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) { }
                else if (!ds.Databases.TryGetValue(session.CurrentDatabase, out db))
                    throw new Exception("Database '" + session.CurrentDatabase + "' not found.");
                if (db.Tables.ContainsKey(tableName))
                    throw new Exception("There is already an object named '" + tableName + "' in the database.");

                var newTable = new Table(tableName);
                int pOpen = afterCreate.IndexOf('(');
                int pClose = afterCreate.LastIndexOf(')');
                if (pOpen >= 0 && pClose > pOpen) {
                    string colDefs = afterCreate.Substring(pOpen + 1, pClose - pOpen - 1);
                    foreach (var colDef in SplitValuesList(colDefs)) {
                        var parts = colDef.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2) {
                            string colName = StripBrackets(parts[0]);
                            string typeName = parts[1].TrimEnd(',');
                            int maxLen = -1;
                            if (typeName.Contains("(")) {
                                int lp = typeName.IndexOf('(');
                                int rp = typeName.IndexOf(')');
                                if (rp > lp) {
                                    string lenStr = typeName.Substring(lp + 1, rp - lp - 1);
                                    if (lenStr.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                                        maxLen = -1;
                                    else
                                        int.TryParse(lenStr, out maxLen);
                                    typeName = typeName.Substring(0, lp);
                                }
                            }
                            newTable.Columns.Add(new Column(colName, ResolveType(typeName), maxLen));
                        }
                    }
                }
                if (newTable.Columns.Count == 0)
                    newTable.Columns.Add(new Column("Id", typeof(int), -1));

                db.Tables[tableName] = newTable;
                ds.ScheduleSave();
                result.RowsAffected = 0;
                return result;
            }

            // ---- DROP TABLE ----
            if (upper.StartsWith("DROP TABLE")) {
                string afterDrop = trimmed.Substring(10).TrimStart();
                if (afterDrop.ToUpperInvariant().StartsWith("IF EXISTS "))
                    afterDrop = afterDrop.Substring(10).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterDrop, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse DROP TABLE statement.");
                Database db;
                if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) { }
                else if (!ds.Databases.TryGetValue(session.CurrentDatabase, out db))
                    throw new Exception("Database '" + session.CurrentDatabase + "' not found.");
                db.Tables.TryRemove(tableName, out _);
                ds.ScheduleSave();
                result.RowsAffected = 0;
                return result;
            }

            // ---- TRUNCATE TABLE ----
            if (upper.StartsWith("TRUNCATE TABLE")) {
                string afterTrunc = trimmed.Substring(14).TrimStart();
                string dbQualifier;
                string tableName = ParseTableName(afterTrunc, out dbQualifier);
                if (tableName == null) throw new Exception("Could not parse TRUNCATE TABLE statement.");
                Database db;
                var table = ResolveTable(ds, session.CurrentDatabase, dbQualifier, tableName, out db);
                if (table == null)
                    throw new Exception("Invalid object name '" + (dbQualifier != null ? dbQualifier + "." : "") + tableName + "'.");
                result.RowsAffected = table.Rows.Count;
                table.Rows.Clear();
                ds.ScheduleSave();
                return result;
            }

            return result;
        }

        #region In-Memory SQL Helpers

        private static string StripBrackets(string name) {
            if (string.IsNullOrEmpty(name)) return name;
            name = name.Trim().TrimEnd(';');
            if (name.StartsWith("[") && name.EndsWith("]"))
                name = name.Substring(1, name.Length - 2);
            if (name.StartsWith("\"") && name.EndsWith("\""))
                name = name.Substring(1, name.Length - 2);
            return name;
        }

        private static string ParseTableName(string fragment) {
            return ParseTableName(fragment, out _);
        }

        private static string ParseTableName(string fragment, out string dbQualifier) {
            dbQualifier = null;
            if (string.IsNullOrWhiteSpace(fragment)) return null;
            fragment = fragment.TrimStart();

            // Collect all dot-separated identifier parts: e.g. [Accounts].[dbo].[Uploads] or Accounts.Uploads
            var parts = new List<string>();
            int pos = 0;
            while (pos < fragment.Length) {
                string part;
                if (fragment[pos] == '[') {
                    int close = fragment.IndexOf(']', pos + 1);
                    if (close < 0) break;
                    part = fragment.Substring(pos + 1, close - pos - 1);
                    pos = close + 1;
                } else {
                    int end = pos;
                    while (end < fragment.Length && fragment[end] != '.' && fragment[end] != ' '
                           && fragment[end] != '\t' && fragment[end] != '(' && fragment[end] != ';'
                           && fragment[end] != ',' && fragment[end] != '\r' && fragment[end] != '\n')
                        end++;
                    part = fragment.Substring(pos, end - pos);
                    pos = end;
                }
                parts.Add(StripBrackets(part));
                // Consume a dot separator
                if (pos < fragment.Length && fragment[pos] == '.') { pos++; continue; }
                break;
            }

            if (parts.Count == 0) return null;
            if (parts.Count == 1) return parts[0];
            if (parts.Count == 2) {
                // Could be schema.table or database.table ? return the first
                // part as the qualifier so the caller can check it as a db name.
                dbQualifier = parts[0];
                return parts[1];
            }
            // 3+ parts: database.schema.table ? first is db, last is table
            dbQualifier = parts[0];
            return parts[parts.Count - 1];
        }

        /// <summary>
        /// Resolves a potentially qualified table reference against the DataServer.
        /// When <paramref name="dbQualifier"/> names an existing database, the table
        /// is looked up there; otherwise the qualifier is treated as a schema (ignored)
        /// and the table is looked up in <paramref name="defaultDbName"/>.
        /// Automatically tries <c>dbo.</c>-prefixed and unprefixed variants so that
        /// <c>SELECT * FROM Uploads</c> finds <c>dbo.Uploads</c> and vice-versa.
        /// </summary>
        private static Table ResolveTable(DataServer ds, string defaultDbName, string dbQualifier, string tableName, out Database db) {
            db = null;
            if (tableName == null) return null;

            // If there's a qualifier and it matches a known database, use that database.
            if (!string.IsNullOrEmpty(dbQualifier) && ds.Databases.TryGetValue(dbQualifier, out db)) {
                var tbl = FindTableFuzzy(db, tableName);
                return tbl;
            }

            // Fall back to the default (current session) database.
            if (!ds.Databases.TryGetValue(defaultDbName, out db)) {
                // Also search all databases if the default doesn't contain the table
                foreach (var kvp in ds.Databases) {
                    var found = FindTableFuzzy(kvp.Value, tableName);
                    if (found != null) { db = kvp.Value; return found; }
                }
                return null;
            }
            var fallback = FindTableFuzzy(db, tableName);
            if (fallback != null) return fallback;

            // Table not in default database — search all databases
            foreach (var kvp in ds.Databases) {
                if (kvp.Key.Equals(defaultDbName, StringComparison.OrdinalIgnoreCase)) continue;
                var found = FindTableFuzzy(kvp.Value, tableName);
                if (found != null) { db = kvp.Value; return found; }
            }
            return null;
        }

        /// <summary>
        /// Looks up a table by name, automatically trying <c>dbo.</c>-prefixed
        /// and unprefixed variants to handle schema-qualified storage.
        /// </summary>
        private static Table FindTableFuzzy(Database db, string tableName) {
            // Exact match first
            if (db.Tables.TryGetValue(tableName, out var table)) return table;

            // Try with dbo. prefix
            if (!tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) {
                if (db.Tables.TryGetValue("dbo." + tableName, out table)) return table;
            }

            // Try without dbo. prefix
            if (tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) {
                if (db.Tables.TryGetValue(tableName.Substring(4), out table)) return table;
            }

            return null;
        }

        private static List<string> SplitValuesList(string list) {
            var result = new List<string>();
            int depth = 0;
            bool inString = false;
            var current = new StringBuilder();
            for (int i = 0; i < list.Length; i++) {
                char ch = list[i];
                if (ch == '\'' && !inString) { inString = true; current.Append(ch); }
                else if (ch == '\'' && inString) {
                    current.Append(ch);
                    // Check for escaped quote ''
                    if (i + 1 < list.Length && list[i + 1] == '\'') { current.Append('\''); i++; }
                    else inString = false;
                }
                else if (inString) { current.Append(ch); }
                else if (ch == '(') { depth++; current.Append(ch); }
                else if (ch == ')') { depth--; current.Append(ch); }
                else if (ch == ',' && depth == 0) { result.Add(current.ToString()); current.Clear(); }
                else { current.Append(ch); }
            }
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private static bool RowMatchesWhere(Table table, object[] row, string whereClause) {
            if (string.IsNullOrWhiteSpace(whereClause)) return true;
            whereClause = whereClause.Trim().TrimEnd(';');

            // Split on AND / OR (top-level only, outside quotes)
            var parts = SplitWhereOnLogical(whereClause, out var operators);
            if (parts.Count == 0) return true;

            bool result = EvalSingleCondition(table, row, parts[0]);
            for (int i = 0; i < operators.Count; i++) {
                bool next = EvalSingleCondition(table, row, parts[i + 1]);
                if (operators[i]) // true = AND, false = OR
                    result = result && next;
                else
                    result = result || next;
            }
            return result;
        }

        /// <summary>
        /// Splits a WHERE clause on top-level AND / OR keywords (outside string literals).
        /// Returns the condition parts and a parallel list of operators (true=AND, false=OR).
        /// </summary>
        private static List<string> SplitWhereOnLogical(string clause, out List<bool> operators) {
            operators = new List<bool>();
            var parts = new List<string>();
            int depth = 0;
            bool inStr = false;
            var current = new StringBuilder();

            for (int i = 0; i < clause.Length; i++) {
                char ch = clause[i];
                if (ch == '\'' && !inStr) { inStr = true; current.Append(ch); continue; }
                if (ch == '\'' && inStr) {
                    current.Append(ch);
                    if (i + 1 < clause.Length && clause[i + 1] == '\'') { current.Append('\''); i++; }
                    else inStr = false;
                    continue;
                }
                if (inStr) { current.Append(ch); continue; }
                if (ch == '(') { depth++; current.Append(ch); continue; }
                if (ch == ')') { depth--; current.Append(ch); continue; }

                if (depth == 0) {
                    // Check for AND / OR keyword boundary
                    string remaining = clause.Substring(i);
                    string remainUpper = remaining.ToUpperInvariant();
                    if (remainUpper.StartsWith("AND ") || remainUpper.StartsWith("AND\t") || remainUpper.StartsWith("AND\r") || remainUpper.StartsWith("AND\n")) {
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                        operators.Add(true);
                        i += 2; // skip "AND", loop will advance past the last char
                        continue;
                    }
                    if (remainUpper.StartsWith("OR ") || remainUpper.StartsWith("OR\t") || remainUpper.StartsWith("OR\r") || remainUpper.StartsWith("OR\n")) {
                        parts.Add(current.ToString().Trim());
                        current.Clear();
                        operators.Add(false);
                        i += 1; // skip "OR", loop will advance past the last char
                        continue;
                    }
                }
                current.Append(ch);
            }
            if (current.Length > 0) parts.Add(current.ToString().Trim());
            return parts;
        }

        /// <summary>
        /// Evaluates a single WHERE condition (no AND/OR) against a row.
        /// Supports: =, !=, &lt;&gt;, &gt;, &lt;, &gt;=, &lt;=, LIKE, NOT LIKE, IS NULL, IS NOT NULL, IN.
        /// </summary>
        private static bool EvalSingleCondition(Table table, object[] row, string cond) {
            if (string.IsNullOrWhiteSpace(cond)) return true;
            cond = cond.Trim();

            // Strip surrounding parentheses
            while (cond.StartsWith("(") && cond.EndsWith(")")) {
                cond = cond.Substring(1, cond.Length - 2).Trim();
            }

            string condUpper = cond.ToUpperInvariant();

            // IS NOT NULL / IS NULL
            int isIdx = FindKeyword(condUpper, " IS ");
            if (isIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, isIdx).Trim());
                string rest = cond.Substring(isIdx + 4).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length) return false;
                bool isNull = row[colIdx] == null;
                if (rest.ToUpperInvariant().StartsWith("NOT"))
                    return !isNull;
                return isNull;
            }

            // NOT LIKE
            int notLikeIdx = FindKeyword(condUpper, " NOT LIKE ");
            if (notLikeIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, notLikeIdx).Trim());
                string pattern = cond.Substring(notLikeIdx + 10).Trim();
                pattern = UnquoteString(pattern);
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                return !MatchLikePattern(row[colIdx].ToString(), pattern);
            }

            // LIKE
            int likeIdx = FindKeyword(condUpper, " LIKE ");
            if (likeIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, likeIdx).Trim());
                string pattern = cond.Substring(likeIdx + 6).Trim();
                pattern = UnquoteString(pattern);
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                return MatchLikePattern(row[colIdx].ToString(), pattern);
            }

            // NOT IN
            int notInIdx = FindKeyword(condUpper, " NOT IN ");
            if (notInIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, notInIdx).Trim());
                string rest = cond.Substring(notInIdx + 8).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                var inValues = ParseInList(rest);
                string cellStr = row[colIdx].ToString();
                return !inValues.Any(v => v.Equals(cellStr, StringComparison.OrdinalIgnoreCase));
            }

            // IN
            int inIdx = FindKeyword(condUpper, " IN ");
            if (inIdx >= 0) {
                string colName = StripBrackets(cond.Substring(0, inIdx).Trim());
                string rest = cond.Substring(inIdx + 4).Trim();
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length || row[colIdx] == null) return false;
                var inValues = ParseInList(rest);
                string cellStr = row[colIdx].ToString();
                return inValues.Any(v => v.Equals(cellStr, StringComparison.OrdinalIgnoreCase));
            }

            // Comparison operators: !=, <>, >=, <=, >, <, =
            string op;
            int opIdx;
            if ((opIdx = FindOperator(cond, "!=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<>", out op)) >= 0 ||
                (opIdx = FindOperator(cond, ">=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<=", out op)) >= 0 ||
                (opIdx = FindOperator(cond, ">", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "<", out op)) >= 0 ||
                (opIdx = FindOperator(cond, "=", out op)) >= 0) {

                string colName = StripBrackets(cond.Substring(0, opIdx).Trim());
                string valStr = cond.Substring(opIdx + op.Length).Trim().TrimEnd(';');

                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0 || colIdx >= row.Length) return false;

                valStr = UnquoteString(valStr);
                var cellVal = row[colIdx];

                if (cellVal == null)
                    return (op == "=" || op == "==") && valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        || (op == "!=" || op == "<>") && !valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase);

                string cellStr = cellVal.ToString();

                switch (op) {
                    case "=":
                        return cellStr.Equals(valStr, StringComparison.OrdinalIgnoreCase);
                    case "!=":
                    case "<>":
                        return !cellStr.Equals(valStr, StringComparison.OrdinalIgnoreCase);
                    case ">":
                    case ">=":
                    case "<":
                    case "<=":
                        if (double.TryParse(cellStr, out var cellNum) && double.TryParse(valStr, out var valNum)) {
                            switch (op) {
                                case ">": return cellNum > valNum;
                                case ">=": return cellNum >= valNum;
                                case "<": return cellNum < valNum;
                                case "<=": return cellNum <= valNum;
                            }
                        }
                        int cmp = string.Compare(cellStr, valStr, StringComparison.OrdinalIgnoreCase);
                        switch (op) {
                            case ">": return cmp > 0;
                            case ">=": return cmp >= 0;
                            case "<": return cmp < 0;
                            case "<=": return cmp <= 0;
                        }
                        break;
                }
            }

            return true; // can't parse, match all
        }

        /// <summary>
        /// Finds the position of a comparison operator in a condition string,
        /// skipping characters inside string literals.
        /// </summary>
        private static int FindOperator(string cond, string op, out string foundOp) {
            foundOp = op;
            bool inStr = false;
            for (int i = 0; i < cond.Length - op.Length + 1; i++) {
                char ch = cond[i];
                if (ch == '\'') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (cond.Substring(i, op.Length) == op) {
                    foundOp = op;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Finds a keyword (e.g. " LIKE ") in the uppercase condition, skipping string literals.
        /// </summary>
        private static int FindKeyword(string condUpper, string keyword) {
            bool inStr = false;
            for (int i = 0; i <= condUpper.Length - keyword.Length; i++) {
                char ch = condUpper[i];
                if (ch == '\'') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (condUpper.Substring(i, keyword.Length) == keyword)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Removes surrounding single quotes or N'' prefix from a SQL value string.
        /// </summary>
        private static string UnquoteString(string val) {
            if (string.IsNullOrEmpty(val)) return val;
            val = val.TrimEnd(';').Trim();
            if (val.StartsWith("N'") && val.EndsWith("'"))
                val = val.Substring(2, val.Length - 3);
            else if (val.StartsWith("'") && val.EndsWith("'"))
                val = val.Substring(1, val.Length - 2);
            return val.Replace("''", "'");
        }

        /// <summary>
        /// SQL LIKE pattern matching. Supports % (any characters) and _ (single character).
        /// </summary>
        private static bool MatchLikePattern(string value, string pattern) {
            // Convert SQL LIKE pattern to simple matching
            // % = match any sequence, _ = match single char
            int vi = 0, pi = 0;
            int starVi = -1, starPi = -1;

            while (vi < value.Length) {
                if (pi < pattern.Length && pattern[pi] == '%') {
                    starPi = pi++;
                    starVi = vi;
                } else if (pi < pattern.Length && (pattern[pi] == '_' || char.ToUpperInvariant(pattern[pi]) == char.ToUpperInvariant(value[vi]))) {
                    pi++;
                    vi++;
                } else if (starPi >= 0) {
                    pi = starPi + 1;
                    vi = ++starVi;
                } else {
                    return false;
                }
            }
            while (pi < pattern.Length && pattern[pi] == '%') pi++;
            return pi == pattern.Length;
        }

        /// <summary>
        /// Parses an IN (...) value list, returning unquoted string values.
        /// </summary>
        private static List<string> ParseInList(string rest) {
            var values = new List<string>();
            rest = rest.Trim();
            if (rest.StartsWith("(")) rest = rest.Substring(1);
            if (rest.EndsWith(")")) rest = rest.Substring(0, rest.Length - 1);
            foreach (var item in SplitValuesList(rest)) {
                values.Add(UnquoteString(item.Trim()));
            }
            return values;
        }

        private static int RemoveMatchingRows(Table table, string whereClause) {
            int removed = 0;
            for (int i = table.Rows.Count - 1; i >= 0; i--) {
                if (RowMatchesWhere(table, table.Rows[i], whereClause)) {
                    table.Rows.RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>
        /// Sorts result rows by an ORDER BY clause. Supports multiple columns
        /// and ASC/DESC direction per column. Uses the original table column names
        /// for lookup and falls back to the result column list when needed.
        /// </summary>
        private static void ApplyOrderBy(QueryResult result, Table table, string orderByClause) {
            if (result.Rows.Count <= 1 || string.IsNullOrWhiteSpace(orderByClause)) return;

            var parts = SplitValuesList(orderByClause);
            var orderSpecs = new List<KeyValuePair<int, bool>>(); // result column index, descending

            foreach (var part in parts) {
                var token = part.Trim().TrimEnd(';');
                if (string.IsNullOrWhiteSpace(token)) continue;

                bool desc = false;
                string colToken = token;
                string tokenUpper = token.ToUpperInvariant();

                if (tokenUpper.EndsWith(" DESC")) {
                    desc = true;
                    colToken = token.Substring(0, token.Length - 5).Trim();
                } else if (tokenUpper.EndsWith(" ASC")) {
                    colToken = token.Substring(0, token.Length - 4).Trim();
                }

                colToken = StripBrackets(colToken);

                // Find column in result set by name
                int colIdx = -1;
                for (int i = 0; i < result.Columns.Count; i++) {
                    if (result.Columns[i].Equals(colToken, StringComparison.OrdinalIgnoreCase)) {
                        colIdx = i;
                        break;
                    }
                }
                if (colIdx >= 0) {
                    orderSpecs.Add(new KeyValuePair<int, bool>(colIdx, desc));
                }
            }

            if (orderSpecs.Count == 0) return;

            result.Rows.Sort((a, b) => {
                foreach (var spec in orderSpecs) {
                    int ci = spec.Key;
                    bool descending = spec.Value;
                    var va = ci < a.Length ? a[ci] : null;
                    var vb = ci < b.Length ? b[ci] : null;

                    int cmp;
                    if (va == null && vb == null) cmp = 0;
                    else if (va == null) cmp = -1;
                    else if (vb == null) cmp = 1;
                    else if (double.TryParse(va.ToString(), out var na) && double.TryParse(vb.ToString(), out var nb))
                        cmp = na.CompareTo(nb);
                    else
                        cmp = string.Compare(va.ToString(), vb.ToString(), StringComparison.OrdinalIgnoreCase);

                    if (cmp != 0) return descending ? -cmp : cmp;
                }
                return 0;
            });
        }

        private static Dictionary<int, object> ParseAssignments(string setClause, Table table) {
            var assignments = new Dictionary<int, object>();
            var parts = SplitValuesList(setClause);
            foreach (var part in parts) {
                int eqIdx = part.IndexOf('=');
                if (eqIdx < 0) continue;
                string colName = StripBrackets(part.Substring(0, eqIdx).Trim());
                string valStr = part.Substring(eqIdx + 1).Trim().TrimEnd(';');
                int colIdx = table.Columns.FindIndex(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase));
                if (colIdx < 0) continue;

                object val;
                if (valStr.Equals("NULL", StringComparison.OrdinalIgnoreCase)) {
                    val = null;
                } else if (valStr.StartsWith("'") && valStr.EndsWith("'")) {
                    val = ConvertValue(valStr.Substring(1, valStr.Length - 2), table.Columns[colIdx].DataType);
                } else if (valStr.StartsWith("N'") && valStr.EndsWith("'")) {
                    val = ConvertValue(valStr.Substring(2, valStr.Length - 3), table.Columns[colIdx].DataType);
                } else {
                    val = ConvertValue(valStr, table.Columns[colIdx].DataType);
                }
                assignments[colIdx] = val;
            }
            return assignments;
        }

        #endregion

        private object ApiUsers(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"users\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"users\":[");
            bool first = true;
            foreach (var user in ds.Users.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase)) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("\"").Append(EscapeJson(user)).Append("\"");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiSessions(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"sessions\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"tdsSessions\":[");
            bool first = true;
            foreach (var kvp in ds.Sessions) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"connectionId\":\"").Append(kvp.Key.ToString("N").Substring(0, 8))
                  .Append("\",\"username\":\"").Append(EscapeJson(kvp.Value.Username ?? ""))
                  .Append("\",\"database\":\"").Append(EscapeJson(kvp.Value.CurrentDatabase ?? ""))
                  .Append("\",\"authenticated\":").Append(kvp.Value.IsAuthenticated ? "true" : "false")
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region Table Designer API

        private Table FindTable(DataServer ds, string dbName, string tableName, out Database db) {
            db = null;
            if (ds == null || string.IsNullOrEmpty(dbName) || string.IsNullOrEmpty(tableName)) return null;
            if (!ds.Databases.TryGetValue(dbName, out db)) return null;
            return FindTableFuzzy(db, tableName);
        }

        private static Type ResolveType(string typeName) {
            if (string.IsNullOrEmpty(typeName)) return typeof(string);
            switch (typeName.ToLowerInvariant()) {
                case "string": case "nvarchar": case "varchar": case "text": case "ntext": case "char": case "nchar":
                    return typeof(string);
                case "int": case "int32":
                    return typeof(int);
                case "bigint": case "long": case "int64":
                    return typeof(long);
                case "smallint": case "short": case "int16":
                    return typeof(short);
                case "tinyint": case "byte":
                    return typeof(byte);
                case "bit": case "bool": case "boolean":
                    return typeof(bool);
                case "float": case "double":
                    return typeof(double);
                case "real": case "single":
                    return typeof(float);
                case "decimal": case "numeric": case "money": case "smallmoney":
                    return typeof(decimal);
                case "datetime": case "datetime2": case "date": case "smalldatetime":
                    return typeof(DateTime);
                case "uniqueidentifier": case "guid":
                    return typeof(Guid);
                case "binary": case "varbinary": case "image": case "byte[]":
                    return typeof(byte[]);
                default:
                    return typeof(string);
            }
        }

        private static string TypeToDisplayName(Type t) {
            if (t == null) return "nvarchar";
            if (t == typeof(string)) return "nvarchar";
            if (t == typeof(int)) return "int";
            if (t == typeof(long)) return "bigint";
            if (t == typeof(short)) return "smallint";
            if (t == typeof(byte)) return "tinyint";
            if (t == typeof(bool)) return "bit";
            if (t == typeof(double)) return "float";
            if (t == typeof(float)) return "real";
            if (t == typeof(decimal)) return "decimal";
            if (t == typeof(DateTime)) return "datetime";
            if (t == typeof(Guid)) return "uniqueidentifier";
            if (t == typeof(byte[])) return "varbinary";
            return t.Name.ToLowerInvariant();
        }

        private static object ConvertValue(string value, Type targetType) {
            if (value == null) return null;
            if (targetType == null || targetType == typeof(string)) return value;
            try {
                if (targetType == typeof(int)) return int.Parse(value);
                if (targetType == typeof(long)) return long.Parse(value);
                if (targetType == typeof(short)) return short.Parse(value);
                if (targetType == typeof(byte)) return byte.Parse(value);
                if (targetType == typeof(bool)) return bool.Parse(value);
                if (targetType == typeof(double)) return double.Parse(value);
                if (targetType == typeof(float)) return float.Parse(value);
                if (targetType == typeof(decimal)) return decimal.Parse(value);
                if (targetType == typeof(DateTime)) return DateTime.Parse(value);
                if (targetType == typeof(Guid)) return Guid.Parse(value);
                return value;
            } catch {
                return value;
            }
        }

        private object ApiDesignerCreateTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            if (string.IsNullOrWhiteSpace(tableName)) { return "{\"error\":\"Table name is required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (db.Tables.ContainsKey(tableName)) { return "{\"error\":\"Table already exists.\"}"; }

            var table = new Table(tableName);
            table.Columns.Add(new Column("Id", typeof(int), -1));
            db.Tables[tableName] = table;
            ds.ScheduleSave();
            return "{\"success\":true}";
        }

        private object ApiDesignerDropTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            if (string.IsNullOrWhiteSpace(tableName)) { return "{\"error\":\"Table name is required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (!db.Tables.TryRemove(tableName, out _)) { return "{\"error\":\"Table not found.\"}"; }

            ds.ScheduleSave();
            return "{\"success\":true}";
        }

        private object ApiDesignerRenameTable(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var oldName = ExtractJsonString(body, "table");
            var newName = ExtractJsonString(body, "newName");
            if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName)) { return "{\"error\":\"Table name and new name are required.\"}"; }
            if (!ds.Databases.TryGetValue(dbName, out var db)) { return "{\"error\":\"Database not found.\"}"; }
            if (!db.Tables.TryRemove(oldName, out var table)) { return "{\"error\":\"Table not found.\"}"; }
            if (db.Tables.ContainsKey(newName)) {
                db.Tables[oldName] = table;
                return "{\"error\":\"A table with that name already exists.\"}";
            }
            table.Name = newName;
            db.Tables[newName] = table;
            ds.ScheduleSave();
            return "{\"success\":true}";
        }

        private object ApiDesignerSaveSchema(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colNames = ExtractJsonString(body, "colNames");
            var colTypes = ExtractJsonString(body, "colTypes");
            var colMaxLens = ExtractJsonString(body, "colMaxLengths");

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (string.IsNullOrEmpty(colNames)) { return "{\"error\":\"Column names are required.\"}"; }

            var names = colNames.Split('|');
            var types = (colTypes ?? "").Split('|');
            var maxLens = (colMaxLens ?? "").Split('|');

            var oldCount = table.Columns.Count;
            var newCols = new List<Column>();
            for (int i = 0; i < names.Length; i++) {
                if (string.IsNullOrWhiteSpace(names[i])) continue;
                var t = i < types.Length ? types[i] : "nvarchar";
                int ml = -1;
                if (i < maxLens.Length) int.TryParse(maxLens[i], out ml);
                newCols.Add(new Column(names[i].Trim(), ResolveType(t), ml));
            }

            // Adjust existing rows to match new column count
            var newCount = newCols.Count;
            if (newCount != oldCount) {
                for (int r = 0; r < table.Rows.Count; r++) {
                    var oldRow = table.Rows[r];
                    var newRow = new object[newCount];
                    for (int c = 0; c < Math.Min(oldRow.Length, newCount); c++)
                        newRow[c] = oldRow[c];
                    table.Rows[r] = newRow;
                }
            }

            table.Columns.Clear();
            table.Columns.AddRange(newCols);
            ds.ScheduleSave();
            return "{\"success\":true,\"columnCount\":" + newCols.Count + "}";
        }

        private object ApiDesignerAddColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colName = ExtractJsonString(body, "name");
            var colType = ExtractJsonString(body, "type") ?? "nvarchar";
            var colMaxLenStr = ExtractJsonString(body, "maxLength");
            int colMaxLen = -1;
            if (colMaxLenStr != null) int.TryParse(colMaxLenStr, out colMaxLen);

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (string.IsNullOrWhiteSpace(colName)) { return "{\"error\":\"Column name is required.\"}"; }

            table.Columns.Add(new Column(colName.Trim(), ResolveType(colType), colMaxLen));

            // Extend existing rows with a null value for the new column
            for (int r = 0; r < table.Rows.Count; r++) {
                var oldRow = table.Rows[r];
                var newRow = new object[table.Columns.Count];
                Array.Copy(oldRow, newRow, oldRow.Length);
                table.Rows[r] = newRow;
            }

            ds.ScheduleSave();
            return "{\"success\":true,\"columnCount\":" + table.Columns.Count + "}";
        }

        private object ApiDesignerRemoveColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colIndexStr = ExtractJsonString(body, "colIndex");
            if (!int.TryParse(colIndexStr, out var colIndex)) { return "{\"error\":\"Column index is required.\"}"; }

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (colIndex < 0 || colIndex >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }

            table.Columns.RemoveAt(colIndex);

            // Remove corresponding value from each row
            for (int r = 0; r < table.Rows.Count; r++) {
                var oldRow = table.Rows[r];
                var newRow = new object[table.Columns.Count];
                int dest = 0;
                for (int c = 0; c < oldRow.Length; c++) {
                    if (c == colIndex) continue;
                    if (dest < newRow.Length) newRow[dest++] = oldRow[c];
                }
                table.Rows[r] = newRow;
            }

            ds.ScheduleSave();
            return "{\"success\":true,\"columnCount\":" + table.Columns.Count + "}";
        }

        private object ApiDesignerUpdateColumn(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var colIndexStr = ExtractJsonString(body, "colIndex");
            if (!int.TryParse(colIndexStr, out var colIndex)) { return "{\"error\":\"Column index is required.\"}"; }

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (colIndex < 0 || colIndex >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }

            var col = table.Columns[colIndex];
            var newName = ExtractJsonString(body, "newName");
            var newType = ExtractJsonString(body, "newType");
            var newMaxLenStr = ExtractJsonString(body, "newMaxLength");

            if (!string.IsNullOrWhiteSpace(newName)) col.Name = newName.Trim();
            if (!string.IsNullOrWhiteSpace(newType)) col.DataType = ResolveType(newType);
            if (newMaxLenStr != null && int.TryParse(newMaxLenStr, out var newMaxLen)) col.MaxLength = newMaxLen;

            ds.ScheduleSave();
            return "{\"success\":true}";
        }

        private object ApiDesignerInsertRow(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }

            var colCount = table.Columns.Count;
            var newRow = new object[colCount];
            // Parse optional pipe-delimited values
            var valuesStr = ExtractJsonString(body, "values");
            if (!string.IsNullOrEmpty(valuesStr)) {
                var parts = valuesStr.Split('|');
                for (int i = 0; i < Math.Min(parts.Length, colCount); i++) {
                    if (parts[i] == "\0" || parts[i] == "NULL") {
                        newRow[i] = null;
                    } else {
                        newRow[i] = ConvertValue(parts[i], table.Columns[i].DataType);
                    }
                }
            }
            table.Rows.Add(newRow);
            ds.ScheduleSave();
            return "{\"success\":true,\"rowIndex\":" + (table.Rows.Count - 1) + ",\"totalRows\":" + table.Rows.Count + "}";
        }

        private object ApiDesignerUpdateCell(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var rowStr = ExtractJsonString(body, "row");
            var colStr = ExtractJsonString(body, "col");
            var value = ExtractJsonString(body, "value");
            var isNull = ExtractJsonString(body, "isNull");

            if (!int.TryParse(rowStr, out var rowIdx) || !int.TryParse(colStr, out var colIdx)) {
                return "{\"error\":\"Row and column indices are required.\"}";
            }

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (rowIdx < 0 || rowIdx >= table.Rows.Count) { return "{\"error\":\"Row index out of range.\"}"; }
            if (colIdx < 0 || colIdx >= table.Columns.Count) { return "{\"error\":\"Column index out of range.\"}"; }

            // Ensure row has enough slots
            var row = table.Rows[rowIdx];
            if (row.Length <= colIdx) {
                var expanded = new object[table.Columns.Count];
                Array.Copy(row, expanded, row.Length);
                row = expanded;
                table.Rows[rowIdx] = row;
            }

            if (isNull == "true") {
                row[colIdx] = null;
            } else {
                row[colIdx] = ConvertValue(value, table.Columns[colIdx].DataType);
            }

            ds.ScheduleSave();
            return "{\"success\":true}";
        }

        private object ApiDesignerDeleteRow(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            var body = req.Body ?? "";
            var dbName = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var tableName = ExtractJsonString(body, "table");
            var rowStr = ExtractJsonString(body, "row");

            if (!int.TryParse(rowStr, out var rowIdx)) { return "{\"error\":\"Row index is required.\"}"; }

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"error\":\"Table not found.\"}"; }
            if (rowIdx < 0 || rowIdx >= table.Rows.Count) { return "{\"error\":\"Row index out of range.\"}"; }

            table.Rows.RemoveAt(rowIdx);
            ds.ScheduleSave();
            return "{\"success\":true,\"totalRows\":" + table.Rows.Count + "}";
        }

        private object ApiDesignerViewport(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"columns\":[],\"rows\":[],\"totalRows\":0}"; }

            var dbName = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : session.CurrentDatabase;
            var tableName = req.QueryParameters.ContainsKey("table") ? req.QueryParameters["table"] : null;
            int offset = 0, limit = 50;
            if (req.QueryParameters.ContainsKey("offset") && int.TryParse(req.QueryParameters["offset"], out var o)) offset = Math.Max(o, 0);
            if (req.QueryParameters.ContainsKey("limit") && int.TryParse(req.QueryParameters["limit"], out var l)) limit = Math.Min(Math.Max(l, 1), 1000);

            var table = FindTable(ds, dbName, tableName, out _);
            if (table == null) { return "{\"columns\":[],\"rows\":[],\"totalRows\":0}"; }

            var sb = new StringBuilder();
            sb.Append("{\"database\":\"").Append(EscapeJson(dbName))
              .Append("\",\"table\":\"").Append(EscapeJson(tableName))
              .Append("\",\"offset\":").Append(offset)
              .Append(",\"limit\":").Append(limit)
              .Append(",\"totalRows\":").Append(table.Rows.Count)
              .Append(",\"columns\":[");
            for (int c = 0; c < table.Columns.Count; c++) {
                if (c > 0) sb.Append(",");
                sb.Append("{\"name\":\"").Append(EscapeJson(table.Columns[c].Name))
                  .Append("\",\"type\":\"").Append(EscapeJson(TypeToDisplayName(table.Columns[c].DataType)))
                  .Append("\",\"maxLength\":").Append(table.Columns[c].MaxLength)
                  .Append("}");
            }
            sb.Append("],\"rows\":[");
            int end = Math.Min(offset + limit, table.Rows.Count);
            for (int r = offset; r < end; r++) {
                if (r > offset) sb.Append(",");
                sb.Append("[");
                var row = table.Rows[r];
                for (int c = 0; c < table.Columns.Count; c++) {
                    if (c > 0) sb.Append(",");
                    if (c >= row.Length || row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("]");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region API Creator

        private void LoadApiEndpoints() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("APIs", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ep = new ApiEndpointDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    Name = row.Length > 1 ? row[1]?.ToString() : null,
                    Route = row.Length > 2 ? row[2]?.ToString() : null,
                    HttpMethod = row.Length > 3 ? row[3]?.ToString() : "GET",
                    Database = row.Length > 4 ? row[4]?.ToString() : "db",
                    SqlQuery = row.Length > 5 ? row[5]?.ToString() : null,
                    ResponseFormat = row.Length > 6 ? row[6]?.ToString() : "json",
                    ContentType = row.Length > 7 ? row[7]?.ToString() : null,
                    Enabled = row.Length > 8 && (row[8]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    Variables = row.Length > 9 ? row[9]?.ToString() : null,
                    QuerySteps = row.Length > 10 ? row[10]?.ToString() : null,
                    BodyType = row.Length > 11 ? row[11]?.ToString() : null,
                    BodySchema = row.Length > 12 ? row[12]?.ToString() : null,
                    Parameters = row.Length > 13 ? row[13]?.ToString() : null,
                    OutputSchema = row.Length > 14 ? row[14]?.ToString() : null,
                    Description = row.Length > 15 ? row[15]?.ToString() : null,
                    Source = "creator",
                    ReadOnly = false
                };
                if (string.IsNullOrEmpty(ep.Id) || string.IsNullOrEmpty(ep.Route)) continue;
                _apiEndpoints[ep.Id] = ep;
                if (ep.Enabled) {
                    try { _server.Map(ep.HttpMethod.ToUpperInvariant(), ep.Route, (conn, req, ct) => HandleDynamicApiEndpoint(req, ep)); } catch { }
                }
            }
        }

        private void SaveApiEndpointsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("APIs");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Route", typeof(string), -1));
            table.Columns.Add(new Column("HttpMethod", typeof(string), 10));
            table.Columns.Add(new Column("Database", typeof(string), -1));
            table.Columns.Add(new Column("SqlQuery", typeof(string), -1));
            table.Columns.Add(new Column("ResponseFormat", typeof(string), 20));
            table.Columns.Add(new Column("ContentType", typeof(string), -1));
            table.Columns.Add(new Column("Enabled", typeof(string), 5));
            table.Columns.Add(new Column("Variables", typeof(string), -1));
            table.Columns.Add(new Column("QuerySteps", typeof(string), -1));
            table.Columns.Add(new Column("BodyType", typeof(string), 20));
            table.Columns.Add(new Column("BodySchema", typeof(string), -1));
            table.Columns.Add(new Column("Parameters", typeof(string), -1));
            table.Columns.Add(new Column("OutputSchema", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));

            foreach (var ep in _apiEndpoints.Values) {
                table.Rows.Add(new object[] {
                    ep.Id, ep.Name, ep.Route, ep.HttpMethod, ep.Database,
                    ep.SqlQuery, ep.ResponseFormat, ep.ContentType,
                    ep.Enabled ? "true" : "false", ep.Variables ?? "", ep.QuerySteps ?? "",
                    ep.BodyType ?? "", ep.BodySchema ?? "", ep.Parameters ?? "",
                    ep.OutputSchema ?? "", ep.Description ?? ""
                });
            }

            configDb.Tables["APIs"] = table;
            ds.ScheduleSave();
        }

        private void LoadApiRouteSettings() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("ApiRouteSettings", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ep = new ApiEndpointDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    HttpMethod = row.Length > 1 ? row[1]?.ToString() : "GET",
                    Route = row.Length > 2 ? row[2]?.ToString() : null,
                    Name = row.Length > 3 ? row[3]?.ToString() : null,
                    Description = row.Length > 4 ? row[4]?.ToString() : null,
                    Variables = row.Length > 5 ? row[5]?.ToString() : null,
                    BodyType = row.Length > 6 ? row[6]?.ToString() : null,
                    BodySchema = row.Length > 7 ? row[7]?.ToString() : null,
                    Parameters = row.Length > 8 ? row[8]?.ToString() : null,
                    OutputSchema = row.Length > 9 ? row[9]?.ToString() : null,
                    ResponseFormat = row.Length > 10 ? row[10]?.ToString() : "handler",
                    ContentType = row.Length > 11 ? row[11]?.ToString() : null,
                    Enabled = true,
                    Source = "mapped",
                    ReadOnly = true
                };
                if (string.IsNullOrEmpty(ep.Route)) continue;
                ep.HttpMethod = (ep.HttpMethod ?? "GET").ToUpperInvariant();
                if (string.IsNullOrEmpty(ep.Id)) ep.Id = MappedEndpointId(ep.HttpMethod, ep.Route);
                _apiRouteSettings[ApiEndpointRouteKey(ep.HttpMethod, ep.Route)] = ep;
            }
        }

        private void SaveApiRouteSettingsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("ApiRouteSettings");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("HttpMethod", typeof(string), 10));
            table.Columns.Add(new Column("Route", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));
            table.Columns.Add(new Column("Variables", typeof(string), -1));
            table.Columns.Add(new Column("BodyType", typeof(string), 20));
            table.Columns.Add(new Column("BodySchema", typeof(string), -1));
            table.Columns.Add(new Column("Parameters", typeof(string), -1));
            table.Columns.Add(new Column("OutputSchema", typeof(string), -1));
            table.Columns.Add(new Column("ResponseFormat", typeof(string), 20));
            table.Columns.Add(new Column("ContentType", typeof(string), -1));

            foreach (var ep in _apiRouteSettings.Values
                .OrderBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.HttpMethod, StringComparer.OrdinalIgnoreCase)) {
                table.Rows.Add(new object[] {
                    ep.Id, ep.HttpMethod, ep.Route, ep.Name ?? "", ep.Description ?? "",
                    ep.Variables ?? "", ep.BodyType ?? "", ep.BodySchema ?? "",
                    ep.Parameters ?? "", ep.OutputSchema ?? "", ep.ResponseFormat ?? "handler",
                    ep.ContentType ?? ""
                });
            }

            configDb.Tables["ApiRouteSettings"] = table;
            ds.ScheduleSave();
        }

        private object ApiEndpointsList(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return JsonSerializer.Serialize(new { error = "Not authenticated." }); }

            var endpoints = GetApiEndpointList()
                .Select(ep => new {
                    id = ep.Id ?? "",
                    name = ep.Name ?? "",
                    route = ep.Route ?? "",
                    httpMethod = ep.HttpMethod ?? "GET",
                    database = ep.Database ?? "",
                    sqlQuery = ep.SqlQuery ?? "",
                    querySteps = ep.QuerySteps ?? "",
                    responseFormat = ep.ResponseFormat ?? "",
                    contentType = ep.ContentType ?? "",
                    variables = ep.Variables ?? "",
                    bodyType = string.IsNullOrWhiteSpace(ep.BodyType) ? "none" : ep.BodyType,
                    bodySchema = ep.BodySchema ?? "",
                    parameters = ep.Parameters ?? "",
                    outputSchema = ep.OutputSchema ?? "",
                    description = ep.Description ?? "",
                    enabled = ep.Enabled,
                    source = string.IsNullOrEmpty(ep.Source) ? "creator" : ep.Source,
                    readOnly = ep.ReadOnly
                })
                .ToList();

            return JsonSerializer.Serialize(new { endpoints });
        }

        private List<ApiEndpointDef> GetApiEndpointList() {
            var endpoints = new List<ApiEndpointDef>();
            var seenRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ep in _apiEndpoints.Values
                .OrderBy(e => e.Route, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.HttpMethod, StringComparer.OrdinalIgnoreCase)) {
                ep.Source = "creator";
                ep.ReadOnly = false;
                endpoints.Add(ep);
                seenRoutes.Add(ApiEndpointRouteKey(ep.HttpMethod, ep.Route));
            }

            foreach (var route in _server.GetMappedRoutes()) {
                if (!IsRestApiMethod(route.Method) || IsAdminPanelRoute(route.Path))
                    continue;

                var routeKey = ApiEndpointRouteKey(route.Method, route.Path);
                if (seenRoutes.Contains(routeKey))
                    continue;

                _apiRouteSettings.TryGetValue(routeKey, out var settings);
                var ep = new ApiEndpointDef {
                    Id = MappedEndpointId(route.Method, route.Path),
                    Name = route.Method + " " + route.Path,
                    Route = route.Path,
                    HttpMethod = route.Method,
                    Database = "",
                    SqlQuery = "",
                    QuerySteps = "",
                    ResponseFormat = "handler",
                    ContentType = "",
                    Variables = route.InputVariables ?? "",
                    BodyType = string.IsNullOrWhiteSpace(route.RequestBodyKind) ? "none" : route.RequestBodyKind,
                    BodySchema = route.RequestBodySchema ?? "",
                    Parameters = route.ParametersSchema ?? "",
                    OutputSchema = route.ResponseSchema ?? "",
                    Description = BuildMappedRouteDescription(route),
                    Enabled = true,
                    Source = "mapped",
                    ReadOnly = true
                };
                ApplyApiMetadata(ep, settings);
                endpoints.Add(ep);
                seenRoutes.Add(routeKey);
            }

            return endpoints;
        }

        private static string BuildMappedRouteDescription(HttpMappedRoute route) {
            if (route == null)
                return "";
            if (!string.IsNullOrWhiteSpace(route.HandlerSignature))
                return "Runtime handler: " + route.HandlerSignature;
            if (!string.IsNullOrWhiteSpace(route.HandlerType) || !string.IsNullOrWhiteSpace(route.HandlerName))
                return "Runtime handler: " + ((route.HandlerType ?? "") + "." + (route.HandlerName ?? "")).Trim('.');
            return "Mapped at runtime with HttpServer.Map.";
        }
        private static void ApplyApiMetadata(ApiEndpointDef target, ApiEndpointDef settings) {
            if (target == null || settings == null)
                return;

            if (!string.IsNullOrWhiteSpace(settings.Name)) target.Name = settings.Name;
            target.Description = settings.Description ?? "";
            target.Variables = settings.Variables ?? "";
            target.BodyType = string.IsNullOrWhiteSpace(settings.BodyType) ? "none" : settings.BodyType;
            target.BodySchema = settings.BodySchema ?? "";
            target.Parameters = settings.Parameters ?? "";
            target.OutputSchema = settings.OutputSchema ?? "";
            target.ResponseFormat = string.IsNullOrWhiteSpace(settings.ResponseFormat) ? target.ResponseFormat : settings.ResponseFormat;
            target.ContentType = settings.ContentType ?? "";
        }

        private static void AppendApiEndpointJson(StringBuilder sb, ApiEndpointDef ep) {
            sb.Append(@"{""id"":""").Append(EscapeJson(ep.Id))
              .Append(@""",""name"":""").Append(EscapeJson(ep.Name))
              .Append(@""",""route"":""").Append(EscapeJson(ep.Route))
              .Append(@""",""httpMethod"":""").Append(EscapeJson(ep.HttpMethod))
              .Append(@""",""database"":""").Append(EscapeJson(ep.Database ?? ""))
              .Append(@""",""sqlQuery"":""").Append(EscapeJson(ep.SqlQuery ?? ""))
              .Append(@""",""querySteps"":""").Append(EscapeJson(ep.QuerySteps ?? ""))
              .Append(@""",""responseFormat"":""").Append(EscapeJson(ep.ResponseFormat ?? ""))
              .Append(@""",""contentType"":""").Append(EscapeJson(ep.ContentType ?? ""))
              .Append(@""",""variables"":""").Append(EscapeJson(ep.Variables ?? ""))
              .Append(@""",""bodyType"":""").Append(EscapeJson(ep.BodyType ?? "none"))
              .Append(@""",""bodySchema"":""").Append(EscapeJson(ep.BodySchema ?? ""))
              .Append(@""",""parameters"":""").Append(EscapeJson(ep.Parameters ?? ""))
              .Append(@""",""outputSchema"":""").Append(EscapeJson(ep.OutputSchema ?? ""))
              .Append(@""",""description"":""").Append(EscapeJson(ep.Description ?? ""))
              .Append(@""",""enabled"":").Append(ep.Enabled ? "true" : "false")
              .Append(@",""source"":""").Append(EscapeJson(string.IsNullOrEmpty(ep.Source) ? "creator" : ep.Source))
              .Append(@""",""readOnly"":").Append(ep.ReadOnly ? "true" : "false")
              .Append("}");
        }

        private bool IsAdminPanelRoute(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var basePath = BasePath.TrimEnd('/');
            if (string.IsNullOrEmpty(basePath))
                basePath = "/";

            if (path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
                || path.Equals(basePath + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            var adminSuffixes = new[] {
                "/login",
                "/logout",
                "/api/databases",
                "/api/tables",
                "/api/columns",
                "/api/rows",
                "/api/query",
                "/api/users",
                "/api/sessions",
                "/api/designer/create-table",
                "/api/designer/drop-table",
                "/api/designer/rename-table",
                "/api/designer/save-schema",
                "/api/designer/add-column",
                "/api/designer/remove-column",
                "/api/designer/update-column",
                "/api/designer/insert-row",
                "/api/designer/update-cell",
                "/api/designer/delete-row",
                "/api/designer/viewport",
                "/api/endpoints/list",
                "/api/endpoints/save",
                "/api/endpoints/delete",
                "/api/endpoints/test",
                "/api/endpoints/settings/save",
                "/api/qb/list-trees",
                "/api/qb/load-tree",
                "/api/qb/save-tree",
                "/api/qb/delete-tree",
                "/api/events/list",
                "/api/events/save",
                "/api/events/delete",
                "/api/events/execute",
                "/api/events/reflect"
            };

            return adminSuffixes.Any(suffix =>
                path.Equals(AppendAdminRouteSuffix(basePath, suffix), StringComparison.OrdinalIgnoreCase));
        }

        private static string AppendAdminRouteSuffix(string basePath, string suffix) {
            if (basePath == "/")
                return suffix;
            return basePath + suffix;
        }

        private static bool IsRestApiMethod(string method) {
            if (string.IsNullOrWhiteSpace(method))
                return false;

            switch (method.ToUpperInvariant()) {
                case "GET":
                case "POST":
                case "PUT":
                case "PATCH":
                case "DELETE":
                case "HEAD":
                case "OPTIONS":
                case "TRACE":
                case "CONNECT":
                    return true;
                default:
                    return false;
            }
        }

        private static string ApiEndpointRouteKey(string method, string route) {
            return (method ?? "GET").ToUpperInvariant() + " " + NormalizeApiEndpointRoute(route);
        }

        private static string NormalizeApiEndpointRoute(string route) {
            if (string.IsNullOrWhiteSpace(route))
                return "/";
            var q = route.IndexOf('?');
            if (q >= 0)
                route = route.Substring(0, q);
            if (!route.StartsWith("/"))
                route = "/" + route;
            if (route.Length > 1 && route.EndsWith("/", StringComparison.Ordinal))
                route = route.Substring(0, route.Length - 1);
            return route;
        }

        private static string MappedEndpointId(HttpMappedRoute route) {
            return MappedEndpointId(route.Method, route.Path);
        }

        private static string MappedEndpointId(string method, string path) {
            var raw = (method ?? "") + ":" + (path ?? "");
            return "mapped-" + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');
        }

        private object ApiEndpointsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonProperty(body, "id");
            var name = ExtractJsonProperty(body, "name");
            var route = ExtractJsonProperty(body, "route");
            var httpMethod = ExtractJsonProperty(body, "httpMethod") ?? "GET";
            var database = ExtractJsonProperty(body, "database") ?? "db";
            var sqlQuery = ExtractJsonProperty(body, "sqlQuery");
            var querySteps = ExtractJsonProperty(body, "querySteps");
            var responseFormat = ExtractJsonProperty(body, "responseFormat") ?? "json";
            var contentType = ExtractJsonProperty(body, "contentType");
            var variables = ExtractJsonProperty(body, "variables");
            var bodyType = ExtractJsonProperty(body, "bodyType") ?? "none";
            var bodySchema = ExtractJsonProperty(body, "bodySchema");
            var parameters = ExtractJsonProperty(body, "parameters");
            var outputSchema = ExtractJsonProperty(body, "outputSchema");
            var description = ExtractJsonProperty(body, "description");
            var enabledStr = ExtractJsonProperty(body, "enabled") ?? "true";

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Endpoint name is required.\"}";
            if (string.IsNullOrWhiteSpace(route)) return "{\"error\":\"Route path is required.\"}";
            // Allow empty sqlQuery if querySteps is provided
            if (string.IsNullOrWhiteSpace(sqlQuery) && string.IsNullOrWhiteSpace(querySteps)) return "{\"error\":\"SQL query is required.\"}";

            if (!route.StartsWith("/")) route = "/" + route;
            httpMethod = httpMethod.ToUpperInvariant();
            if (httpMethod != "GET" && httpMethod != "POST") httpMethod = "GET";

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            // If updating, remove old route first
            if (!isNew && _apiEndpoints.TryGetValue(id, out var oldEp)) {
                try { _server.RemoveRoute(oldEp.HttpMethod.ToUpperInvariant(), oldEp.Route); } catch { }
            }

            var ep = new ApiEndpointDef {
                Id = id,
                Name = name,
                Route = route,
                HttpMethod = httpMethod,
                Database = database,
                SqlQuery = sqlQuery,
                QuerySteps = querySteps,
                ResponseFormat = responseFormat,
                ContentType = contentType,
                Variables = variables,
                BodyType = bodyType,
                BodySchema = bodySchema,
                Parameters = parameters,
                OutputSchema = outputSchema,
                Description = description,
                Source = "creator",
                ReadOnly = false,
                Enabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase)
            };

            _apiEndpoints[id] = ep;

            // Register the dynamic route
            if (ep.Enabled) {
                try { _server.Map(httpMethod, route, (conn, r, ct) => HandleDynamicApiEndpoint(r, ep)); } catch { }
            }

            SaveApiEndpointsTable();
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object ApiEndpointSettingsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return @"{""error"":""Not authenticated.""}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonProperty(body, "id");
            var route = ExtractJsonProperty(body, "route");
            var httpMethod = (ExtractJsonProperty(body, "httpMethod") ?? "GET").ToUpperInvariant();
            var name = ExtractJsonProperty(body, "name");
            var description = ExtractJsonProperty(body, "description");
            var variables = ExtractJsonProperty(body, "variables");
            var bodyType = ExtractJsonProperty(body, "bodyType") ?? "none";
            var bodySchema = ExtractJsonProperty(body, "bodySchema");
            var parameters = ExtractJsonProperty(body, "parameters");
            var outputSchema = ExtractJsonProperty(body, "outputSchema");
            var responseFormat = ExtractJsonProperty(body, "responseFormat") ?? "handler";
            var contentType = ExtractJsonProperty(body, "contentType");

            ApiEndpointDef existingCreator = null;
            if (!string.IsNullOrEmpty(id)) {
                _apiEndpoints.TryGetValue(id, out existingCreator);
            }

            if (existingCreator != null) {
                if (!string.IsNullOrWhiteSpace(name)) existingCreator.Name = name;
                existingCreator.Description = description;
                existingCreator.Variables = variables;
                existingCreator.BodyType = bodyType;
                existingCreator.BodySchema = bodySchema;
                existingCreator.Parameters = parameters;
                existingCreator.OutputSchema = outputSchema;
                existingCreator.ResponseFormat = string.IsNullOrWhiteSpace(responseFormat) ? existingCreator.ResponseFormat : responseFormat;
                existingCreator.ContentType = contentType;
                SaveApiEndpointsTable();
                return "{\"success\":true,\"id\":\"" + EscapeJson(existingCreator.Id) + "\",\"source\":\"creator\"}";
            }

            if (string.IsNullOrWhiteSpace(route)) return @"{""error"":""Route path is required.""}";
            route = NormalizeApiEndpointRoute(route);
            if (string.IsNullOrEmpty(id)) id = MappedEndpointId(httpMethod, route);

            var setting = new ApiEndpointDef {
                Id = id,
                Name = name,
                Route = route,
                HttpMethod = httpMethod,
                Database = "",
                SqlQuery = "",
                QuerySteps = "",
                ResponseFormat = responseFormat,
                ContentType = contentType,
                Variables = variables,
                BodyType = bodyType,
                BodySchema = bodySchema,
                Parameters = parameters,
                OutputSchema = outputSchema,
                Description = description,
                Enabled = true,
                Source = "mapped",
                ReadOnly = true
            };

            _apiRouteSettings[ApiEndpointRouteKey(httpMethod, route)] = setting;
            SaveApiRouteSettingsTable();
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\",\"source\":\"mapped\"}";
        }

        private object ApiEndpointsDelete(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Endpoint ID is required.\"}";

            if (_apiEndpoints.TryRemove(id, out var ep)) {
                try { _server.RemoveRoute(ep.HttpMethod.ToUpperInvariant(), ep.Route); } catch { }
            }

            SaveApiEndpointsTable();
            return "{\"success\":true}";
        }

        private object ApiEndpointsTest(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var tempEp = new ApiEndpointDef {
                Database = ExtractJsonString(body, "database") ?? "db",
                SqlQuery = ExtractJsonString(body, "sqlQuery") ?? "",
                ResponseFormat = ExtractJsonString(body, "responseFormat") ?? "json",
                ContentType = ExtractJsonString(body, "contentType"),
                HttpMethod = ExtractJsonString(body, "httpMethod") ?? "GET",
                Variables = ExtractJsonString(body, "variables")
            };

            var paramsJson = ExtractJsonString(body, "parameters") ?? "{}";
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            ExtractAllJsonValues(paramsJson, parameters);

            return ExecuteEndpointQuery(req, tempEp, parameters);
        }

        private object HandleDynamicApiEndpoint(HttpRequest req, ApiEndpointDef endpoint) {
            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Gather querystring parameters
            if (req.QueryParameters != null) {
                foreach (var kvp in req.QueryParameters)
                    parameters[kvp.Key] = kvp.Value;
            }

            // Gather POST body parameters
            if (!string.IsNullOrEmpty(req.Body)) {
                var ct = "";
                if (req.Headers != null && req.Headers.TryGetValue("Content-Type", out var ctVal))
                    ct = (ctVal ?? "").ToLowerInvariant();

                if (ct.Contains("application/x-www-form-urlencoded")) {
                    foreach (var pair in req.Body.Split('&')) {
                        var eqIdx = pair.IndexOf('=');
                        if (eqIdx > 0) {
                            var key = Uri.UnescapeDataString(pair.Substring(0, eqIdx));
                            var val = Uri.UnescapeDataString(pair.Substring(eqIdx + 1).Replace('+', ' '));
                            parameters[key] = val;
                        }
                    }
                } else {
                    ExtractAllJsonValues(req.Body, parameters);
                }
            }

            return ExecuteEndpointQuery(req, endpoint, parameters);
        }

        private object ExecuteEndpointQuery(HttpRequest req, ApiEndpointDef endpoint, Dictionary<string, string> parameters) {
            var ds = GetDataServer();
            if (ds == null) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "503 Service Unavailable";
                return "{\"error\":\"DataServer not available.\"}";
            }

            var sqlSession = new SqlSession {
                ConnectionId = Guid.NewGuid(),
                Username = "api",
                CurrentDatabase = endpoint.Database ?? "db",
                ServerName = ds.ServerName,
                ServerVersion = ds.ServerVersion,
                IsAuthenticated = true
            };

            // Create a working copy of parameters that we can add lookup results to
            var workingParams = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);

            // Check if we have query steps to process
            if (!string.IsNullOrEmpty(endpoint.QuerySteps)) {
                try {
                    return ExecuteQuerySteps(req, endpoint, workingParams, ds, sqlSession);
                } catch (Exception ex) {
                    req.Context.ContentType = "application/json";
                    req.Context.StatusCode = "500 Internal Server Error";
                    return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
                }
            }

            // Legacy: single SQL query execution
            var sql = SubstituteParameters(endpoint.SqlQuery ?? "", workingParams, endpoint.Variables);

            try {
                var result = ds.ExecuteQuery(sqlSession, sql);
                if (!result.HasResultSet && result.Columns.Count == 0 && result.Rows.Count == 0 && result.RowsAffected == 0) {
                    result = ExecuteInMemoryQuery(ds, sqlSession, sql);
                }

                switch ((endpoint.ResponseFormat ?? "json").ToLowerInvariant()) {
                    case "plaintext":
                        return FormatResultAsPlaintext(result);
                    case "binary":
                        return FormatResultAsBinary(result, endpoint.ContentType ?? "application/octet-stream");
                    default:
                        return FormatResultAsJson(result);
                }
            } catch (Exception ex) {
                req.Context.ContentType = "application/json";
                req.Context.StatusCode = "500 Internal Server Error";
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        private object ExecuteQuerySteps(HttpRequest req, ApiEndpointDef endpoint, Dictionary<string, string> workingParams, DataServer ds, SqlSession sqlSession) {
            // Parse query steps JSON
            var stepsJson = endpoint.QuerySteps;
            var steps = ParseQuerySteps(stepsJson);
            if (steps.Count == 0) {
                return "{\"error\":\"No query steps defined.\"}";
            }

            QueryResult lastQueryResult = null;

            foreach (var step in steps) {
                var sql = SubstituteParameters(step.Sql ?? "", workingParams, endpoint.Variables);

                var result = ds.ExecuteQuery(sqlSession, sql);
                if (!result.HasResultSet && result.Columns.Count == 0 && result.Rows.Count == 0 && result.RowsAffected == 0) {
                    result = ExecuteInMemoryQuery(ds, sqlSession, sql);
                }

                switch (step.Type.ToLowerInvariant()) {
                    case "lookup":
                        // Extract first row, first column value and store as variable
                        if (!string.IsNullOrEmpty(step.OutputVar)) {
                            var varName = step.OutputVar.StartsWith("$") ? step.OutputVar.Substring(1) : step.OutputVar;
                            string extractedValue = null;

                            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                                extractedValue = result.Rows[0][0]?.ToString() ?? "";
                            }

                            if (extractedValue != null) {
                                workingParams[varName] = extractedValue;
                            } else {
                                // Lookup returned no results
                                req.Context.ContentType = "application/json";
                                req.Context.StatusCode = "404 Not Found";
                                return "{\"error\":\"Lookup step '" + EscapeJson(step.Name) + "' returned no results.\"}";
                            }
                        }
                        break;

                    case "validate":
                        // Check validation condition
                        bool valid = EvaluateValidationCondition(result, step);
                        if (!valid) {
                            req.Context.ContentType = "application/json";
                            req.Context.StatusCode = "403 Forbidden";
                            var errorMsg = string.IsNullOrEmpty(step.ErrorMsg) ? "Validation failed" : step.ErrorMsg;
                            return "{\"error\":\"" + EscapeJson(errorMsg) + "\"}";
                        }
                        break;

                    case "modify":
                        // Modify steps don't return data, just execute
                        // Continue to next step
                        break;

                    case "query":
                        // This is the main query that returns data
                        lastQueryResult = result;
                        break;
                }
            }

            // Return the last query result (or success message if no query step)
            if (lastQueryResult != null) {
                switch ((endpoint.ResponseFormat ?? "json").ToLowerInvariant()) {
                    case "plaintext":
                        return FormatResultAsPlaintext(lastQueryResult);
                    case "binary":
                        return FormatResultAsBinary(lastQueryResult, endpoint.ContentType ?? "application/octet-stream");
                    default:
                        return FormatResultAsJson(lastQueryResult);
                }
            }

            return "{\"success\":true,\"message\":\"All steps executed successfully.\"}";
        }

        private string SubstituteParameters(string sql, Dictionary<string, string> parameters, string variablesList) {
            // Substitute @param placeholders
            foreach (var kvp in parameters) {
                sql = sql.Replace("@" + kvp.Key, "'" + (kvp.Value ?? "").Replace("'", "''") + "'");
            }

            // Substitute $Variable placeholders from parameters
            foreach (var kvp in parameters) {
                sql = sql.Replace("$" + kvp.Key, "'" + (kvp.Value ?? "").Replace("'", "''") + "'");
            }

            // Also check declared variables list
            if (!string.IsNullOrEmpty(variablesList)) {
                foreach (var varEntry in variablesList.Split(',')) {
                    var varName = varEntry.Trim();
                    if (string.IsNullOrEmpty(varName)) continue;
                    var lookupKey = varName.StartsWith("$") ? varName.Substring(1) : varName;
                    var placeholder = varName.StartsWith("$") ? varName : "$" + varName;
                    if (parameters.TryGetValue(lookupKey, out var val)) {
                        sql = sql.Replace(placeholder, "'" + (val ?? "").Replace("'", "''") + "'");
                    }
                }
            }

            return sql;
        }

        private class QueryStep {
            public int Id;
            public string Type;
            public string Name;
            public string Sql;
            public string OutputVar;
            public string Condition;
            public string ConditionOp;
            public string ConditionVal;
            public string ErrorMsg;
        }

        private List<QueryStep> ParseQuerySteps(string stepsJson) {
            var steps = new List<QueryStep>();
            if (string.IsNullOrEmpty(stepsJson)) return steps;

            // Simple JSON array parsing for query steps
            // Format: [{"id":1,"type":"lookup","name":"...","sql":"...","outputVar":"$Var",...},...]
            try {
                var trimmed = stepsJson.Trim();
                if (!trimmed.StartsWith("[")) return steps;

                // Find each object in the array
                int depth = 0;
                int objStart = -1;
                for (int i = 0; i < trimmed.Length; i++) {
                    char c = trimmed[i];
                    if (c == '{') {
                        if (depth == 1) objStart = i;
                        depth++;
                    } else if (c == '}') {
                        depth--;
                        if (depth == 1 && objStart >= 0) {
                            var objJson = trimmed.Substring(objStart, i - objStart + 1);
                            var step = new QueryStep {
                                Id = int.TryParse(ExtractJsonString(objJson, "id"), out var id) ? id : 0,
                                Type = ExtractJsonString(objJson, "type") ?? "query",
                                Name = ExtractJsonString(objJson, "name") ?? "",
                                Sql = ExtractJsonString(objJson, "sql") ?? "",
                                OutputVar = ExtractJsonString(objJson, "outputVar") ?? "",
                                Condition = ExtractJsonString(objJson, "condition") ?? "",
                                ConditionOp = ExtractJsonString(objJson, "conditionOp") ?? "",
                                ConditionVal = ExtractJsonString(objJson, "conditionVal") ?? "",
                                ErrorMsg = ExtractJsonString(objJson, "errorMsg") ?? ""
                            };
                            steps.Add(step);
                            objStart = -1;
                        }
                    } else if (c == '[') {
                        depth++;
                    } else if (c == ']') {
                        depth--;
                    }
                }
            } catch {
                // If parsing fails, return empty steps
            }

            return steps;
        }

        private bool EvaluateValidationCondition(QueryResult result, QueryStep step) {
            double checkValue = 0;

            switch ((step.Condition ?? "").ToLowerInvariant()) {
                case "rowcount":
                    checkValue = result.Rows.Count;
                    break;
                case "value":
                case "scalar":
                    if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                        var val = result.Rows[0][0];
                        if (val != null) {
                            double.TryParse(val.ToString(), out checkValue);
                        }
                    }
                    break;
                default:
                    checkValue = result.Rows.Count;
                    break;
            }

            double compareValue = 0;
            double.TryParse(step.ConditionVal ?? "0", out compareValue);

            switch (step.ConditionOp ?? ">") {
                case ">": return checkValue > compareValue;
                case ">=": return checkValue >= compareValue;
                case "=": return Math.Abs(checkValue - compareValue) < 0.0001;
                case "==": return Math.Abs(checkValue - compareValue) < 0.0001;
                case "!=": return Math.Abs(checkValue - compareValue) >= 0.0001;
                case "<>": return Math.Abs(checkValue - compareValue) >= 0.0001;
                case "<": return checkValue < compareValue;
                case "<=": return checkValue <= compareValue;
                default: return checkValue > compareValue;
            }
        }

        private static string FormatResultAsJson(QueryResult result) {
            if (!result.HasResultSet || result.Columns.Count == 0) {
                return "{\"rowsAffected\":" + result.RowsAffected + ",\"message\":\"Query executed successfully.\"}";
            }
            var sb = new StringBuilder();
            sb.Append("[");
            for (int r = 0; r < result.Rows.Count; r++) {
                if (r > 0) sb.Append(",");
                sb.Append("{");
                var row = result.Rows[r];
                for (int c = 0; c < result.Columns.Count; c++) {
                    if (c > 0) sb.Append(",");
                    sb.Append("\"").Append(EscapeJson(result.Columns[c])).Append("\":");
                    if (c >= row.Length || row[c] == null) {
                        sb.Append("null");
                    } else {
                        sb.Append("\"").Append(EscapeJson(row[c].ToString())).Append("\"");
                    }
                }
                sb.Append("}");
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static string FormatResultAsPlaintext(QueryResult result) {
            if (!result.HasResultSet || result.Columns.Count == 0) {
                return "Query executed successfully. Rows affected: " + result.RowsAffected;
            }
            var sb = new StringBuilder();
            for (int c = 0; c < result.Columns.Count; c++) {
                if (c > 0) sb.Append("\t");
                sb.Append(result.Columns[c]);
            }
            sb.Append("\n");
            for (int r = 0; r < result.Rows.Count; r++) {
                var row = result.Rows[r];
                for (int c = 0; c < result.Columns.Count; c++) {
                    if (c > 0) sb.Append("\t");
                    sb.Append(c < row.Length && row[c] != null ? row[c].ToString() : "NULL");
                }
                sb.Append("\n");
            }
            return sb.ToString();
        }

        private static object FormatResultAsBinary(QueryResult result, string contentType) {
            byte[] data;
            if (result.HasResultSet && result.Rows.Count > 0 && result.Rows[0].Length > 0) {
                var val = result.Rows[0][0];
                if (val is byte[] bytes) {
                    data = bytes;
                } else {
                    data = Encoding.UTF8.GetBytes(val?.ToString() ?? "");
                }
            } else {
                data = Array.Empty<byte>();
            }
            return new FileResponse(data, contentType);
        }

        private static void ExtractAllJsonValues(string json, Dictionary<string, string> parameters) {
            if (string.IsNullOrEmpty(json)) return;
            json = json.Trim();
            if (!json.StartsWith("{")) return;
            int pos = json.IndexOf('{') + 1;
            while (pos < json.Length) {
                while (pos < json.Length && " \r\n\t,".IndexOf(json[pos]) >= 0) pos++;
                if (pos >= json.Length || json[pos] == '}') break;
                if (json[pos] != '"') break;
                pos++;
                var keySb = new StringBuilder();
                while (pos < json.Length && json[pos] != '"') {
                    if (json[pos] == '\\' && pos + 1 < json.Length) { pos++; keySb.Append(json[pos]); }
                    else keySb.Append(json[pos]);
                    pos++;
                }
                var key = keySb.ToString();
                pos++;
                while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':')) pos++;
                if (pos >= json.Length) break;
                if (json[pos] == '"') {
                    pos++;
                    var valSb = new StringBuilder();
                    while (pos < json.Length && json[pos] != '"') {
                        if (json[pos] == '\\' && pos + 1 < json.Length) { pos++; valSb.Append(json[pos]); }
                        else valSb.Append(json[pos]);
                        pos++;
                    }
                    pos++;
                    parameters[key] = valSb.ToString();
                } else {
                    int valStart = pos;
                    while (pos < json.Length && json[pos] != ',' && json[pos] != '}') pos++;
                    parameters[key] = json.Substring(valStart, pos - valStart).Trim();
                }
            }
        }

        #endregion

        #region Query Builder Trees

        private void EnsureQueryTreesTable(DataServer ds) {
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (configDb.Tables.ContainsKey("QueryTrees")) return;
            var table = new Table("QueryTrees");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Database", typeof(string), -1));
            table.Columns.Add(new Column("TreeJson", typeof(string), -1));
            table.Columns.Add(new Column("GeneratedSql", typeof(string), -1));
            table.Columns.Add(new Column("CreatedAt", typeof(string), -1));
            configDb.Tables["QueryTrees"] = table;
            ds.ScheduleSave();
        }

        private object QbListTrees(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"trees\":[]}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"trees\":[]}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"trees\":[]}"; }

            var sb = new StringBuilder();
            sb.Append("{\"trees\":[");
            bool first = true;
            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":\"").Append(EscapeJson(row.Length > 0 ? row[0]?.ToString() : ""))
                  .Append("\",\"name\":\"").Append(EscapeJson(row.Length > 1 ? row[1]?.ToString() : ""))
                  .Append("\",\"database\":\"").Append(EscapeJson(row.Length > 2 ? row[2]?.ToString() : ""))
                  .Append("\",\"treeJson\":\"").Append(EscapeJson(row.Length > 3 ? row[3]?.ToString() : ""))
                  .Append("\",\"generatedSql\":\"").Append(EscapeJson(row.Length > 4 ? row[4]?.ToString() : ""))
                  .Append("\",\"createdAt\":\"").Append(EscapeJson(row.Length > 5 ? row[5]?.ToString() : ""))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object QbLoadTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var name = req.QueryParameters.ContainsKey("name") ? req.QueryParameters["name"] : null;
            var dbFilter = req.QueryParameters.ContainsKey("db") ? req.QueryParameters["db"] : null;

            if (string.IsNullOrEmpty(name)) { return "{\"error\":\"Query name is required.\"}"; }

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var rowName = row.Length > 1 ? row[1]?.ToString() : "";
                var rowDb = row.Length > 2 ? row[2]?.ToString() : "";

                if (name.Equals(rowName, StringComparison.OrdinalIgnoreCase)) {
                    // If db filter is specified, check it matches
                    if (!string.IsNullOrEmpty(dbFilter) && !dbFilter.Equals(rowDb, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var sb = new StringBuilder();
                    sb.Append("{\"id\":\"").Append(EscapeJson(row.Length > 0 ? row[0]?.ToString() : ""))
                      .Append("\",\"name\":\"").Append(EscapeJson(rowName))
                      .Append("\",\"database\":\"").Append(EscapeJson(rowDb))
                      .Append("\",\"tree\":\"").Append(EscapeJson(row.Length > 3 ? row[3]?.ToString() : ""))
                      .Append("\",\"generatedSql\":\"").Append(EscapeJson(row.Length > 4 ? row[4]?.ToString() : ""))
                      .Append("\",\"createdAt\":\"").Append(EscapeJson(row.Length > 5 ? row[5]?.ToString() : ""))
                      .Append("\"}");
                    return sb.ToString();
                }
            }

            return "{\"error\":\"Query not found.\"}";
        }

        private object QbSaveTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            var name = ExtractJsonString(body, "name");
            var database = ExtractJsonString(body, "database") ?? session.CurrentDatabase;
            var treeJson = ExtractJsonString(body, "treeJson");
            var generatedSql = ExtractJsonString(body, "generatedSql");

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Tree name is required.\"}";
            if (string.IsNullOrWhiteSpace(treeJson)) return "{\"error\":\"Tree data is required.\"}";

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            // Update existing or insert new
            bool found = false;
            if (!isNew) {
                for (int r = 0; r < table.Rows.Count; r++) {
                    if (table.Rows[r].Length > 0 && id.Equals(table.Rows[r][0]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                        table.Rows[r] = new object[] { id, name, database, treeJson, generatedSql, table.Rows[r].Length > 5 ? table.Rows[r][5] : DateTime.UtcNow.ToString("o") };
                        found = true;
                        break;
                    }
                }
            }
            if (!found) {
                table.Rows.Add(new object[] { id, name, database, treeJson, generatedSql, DateTime.UtcNow.ToString("o") });
            }

            ds.ScheduleSave();
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object QbDeleteTree(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var ds = GetDataServer();
            if (ds == null) { return "{\"error\":\"DataServer not available.\"}"; }

            EnsureQueryTreesTable(ds);
            if (!ds.Databases.TryGetValue("db", out var configDb)) { return "{\"error\":\"Config database not found.\"}"; }
            if (!configDb.Tables.TryGetValue("QueryTrees", out var table)) { return "{\"error\":\"QueryTrees table not found.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Tree ID is required.\"}";

            bool removed = false;
            for (int r = table.Rows.Count - 1; r >= 0; r--) {
                if (table.Rows[r].Length > 0 && id.Equals(table.Rows[r][0]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                    table.Rows.RemoveAt(r);
                    removed = true;
                    break;
                }
            }

            ds.ScheduleSave();
            return "{\"success\":true,\"removed\":" + (removed ? "true" : "false") + "}";
        }

        #endregion

        #region Events System

        private void LoadEventDefs() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;
            if (!configDb.Tables.TryGetValue("Events", out var table)) return;

            for (int r = 0; r < table.Rows.Count; r++) {
                var row = table.Rows[r];
                var ev = new EventDef {
                    Id = row.Length > 0 ? row[0]?.ToString() : null,
                    Name = row.Length > 1 ? row[1]?.ToString() : null,
                    Description = row.Length > 2 ? row[2]?.ToString() : null,
                    Enabled = row.Length > 3 && (row[3]?.ToString() ?? "").Equals("true", StringComparison.OrdinalIgnoreCase),
                    Nodes = row.Length > 4 ? row[4]?.ToString() : null
                };
                if (string.IsNullOrEmpty(ev.Id)) continue;
                _eventDefs[ev.Id] = ev;
            }
        }

        private void SaveEventsTable() {
            var ds = GetDataServer();
            if (ds == null) return;
            if (!ds.Databases.ContainsKey("db")) ds.Databases.TryAdd("db", new Database("db"));
            if (!ds.Databases.TryGetValue("db", out var configDb)) return;

            var table = new Table("Events");
            table.Columns.Add(new Column("Id", typeof(string), -1));
            table.Columns.Add(new Column("Name", typeof(string), -1));
            table.Columns.Add(new Column("Description", typeof(string), -1));
            table.Columns.Add(new Column("Enabled", typeof(string), 5));
            table.Columns.Add(new Column("Nodes", typeof(string), -1));

            foreach (var ev in _eventDefs.Values) {
                table.Rows.Add(new object[] {
                    ev.Id, ev.Name, ev.Description ?? "",
                    ev.Enabled ? "true" : "false", ev.Nodes ?? "[]"
                });
            }

            configDb.Tables["Events"] = table;
            ds.ScheduleSave();
        }

        private object ApiEventsList(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var sb = new StringBuilder();
            sb.Append("{\"events\":[");
            bool first = true;
            foreach (var ev in _eventDefs.Values) {
                if (!first) sb.Append(",");
                first = false;
                sb.Append("{\"id\":\"").Append(EscapeJson(ev.Id))
                  .Append("\",\"name\":\"").Append(EscapeJson(ev.Name))
                  .Append("\",\"description\":\"").Append(EscapeJson(ev.Description ?? ""))
                  .Append("\",\"enabled\":").Append(ev.Enabled ? "true" : "false")
                  .Append(",\"nodes\":\"").Append(EscapeJson(ev.Nodes ?? "[]"))
                  .Append("\"}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private object ApiEventsSave(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            var name = ExtractJsonString(body, "name");
            var description = ExtractJsonString(body, "description") ?? "";
            var nodes = ExtractJsonString(body, "nodes") ?? "[]";
            var enabledStr = ExtractJsonString(body, "enabled") ?? "true";

            if (string.IsNullOrWhiteSpace(name)) return "{\"error\":\"Event name is required.\"}";

            bool isNew = string.IsNullOrEmpty(id);
            if (isNew) id = Guid.NewGuid().ToString("N").Substring(0, 12);

            var ev = new EventDef {
                Id = id,
                Name = name,
                Description = description,
                Enabled = enabledStr.Equals("true", StringComparison.OrdinalIgnoreCase),
                Nodes = nodes
            };

            _eventDefs[id] = ev;
            SaveEventsTable();
            return "{\"success\":true,\"id\":\"" + EscapeJson(id) + "\"}";
        }

        private object ApiEventsDelete(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var id = ExtractJsonString(body, "id");
            if (string.IsNullOrEmpty(id)) return "{\"error\":\"Event ID is required.\"}";

            _eventDefs.TryRemove(id, out _);
            SaveEventsTable();
            return "{\"success\":true}";
        }

        /// <summary>
        /// Executes a single action node: supports HTTP API calls, SQL queries, and
        /// reflection-based method invocation on the server.
        /// </summary>
        private object ApiEventsExecute(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var body = req.Body ?? "";
            var actionType = ExtractJsonString(body, "actionType") ?? "";
            var sb = new StringBuilder();

            try {
                switch (actionType.ToLowerInvariant()) {
                    case "http": {
                        // Invoke an HTTP request
                        var url = ExtractJsonString(body, "url") ?? "";
                        var method = (ExtractJsonString(body, "method") ?? "GET").ToUpperInvariant();
                        var payload = ExtractJsonString(body, "payload") ?? "";
                        if (string.IsNullOrWhiteSpace(url)) return "{\"error\":\"URL is required for HTTP action.\"}";

                        using (var client = new System.Net.WebClient()) {
                            client.Headers["Content-Type"] = "application/json";
                            string result;
                            if (method == "POST") {
                                result = client.UploadString(url, "POST", payload);
                            } else {
                                result = client.DownloadString(url);
                            }
                            sb.Append("{\"success\":true,\"result\":\"").Append(EscapeJson(result)).Append("\"}");
                        }
                        return sb.ToString();
                    }
                    case "sql": {
                        // Execute a SQL query on the DataServer
                        var database = ExtractJsonString(body, "database") ?? session.CurrentDatabase ?? "db";
                        var sql = ExtractJsonString(body, "sql") ?? "";
                        if (string.IsNullOrWhiteSpace(sql)) return "{\"error\":\"SQL query is required.\"}";

                        var ds = GetDataServer();
                        if (ds == null) return "{\"error\":\"DataServer not available.\"}";

                        var sqlSession = new SqlSession {
                            ConnectionId = Guid.NewGuid(),
                            Username = session.Username,
                            CurrentDatabase = database,
                            ServerName = ds.ServerName,
                            ServerVersion = ds.ServerVersion,
                            IsAuthenticated = true
                        };
                        var result = ds.ExecuteQuery(sqlSession, sql);
                        if (!result.HasResultSet && result.Columns.Count == 0 && result.Rows.Count == 0 && result.RowsAffected == 0) {
                            result = ExecuteInMemoryQuery(ds, sqlSession, sql);
                        }
                        sb.Append("{\"success\":true,\"result\":\"").Append(EscapeJson(FormatResultAsJson(result))).Append("\"}");
                        return sb.ToString();
                    }
                    case "reflect": {
                        // Invoke a method on a loaded type via reflection
                        var typeName = ExtractJsonString(body, "typeName") ?? "";
                        var methodName = ExtractJsonString(body, "methodName") ?? "";
                        var argsJson = ExtractJsonString(body, "args") ?? "[]";
                        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(methodName))
                            return "{\"error\":\"typeName and methodName are required.\"}";

                        // Search across application assemblies (skip system/framework DLLs)
                        Type targetType = null;
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                            if (!IsApplicationAssembly(asm)) continue;
                            try { targetType = asm.GetType(typeName, false, true); } catch { }
                            if (targetType != null) break;
                        }
                        if (targetType == null) return "{\"error\":\"Type not found: " + EscapeJson(typeName) + "\"}";

                        // Parse simple string args from JSON array (needed early for overload resolution)
                        var args = ParseSimpleJsonArray(argsJson);

                        // Use GetMethods + filter to avoid AmbiguousMatchException on overloaded methods
                        var candidates = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase | BindingFlags.DeclaredOnly)
                            .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                            .ToArray();
                        if (candidates.Length == 0) {
                            // Fall back to include inherited methods
                            candidates = targetType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.IgnoreCase)
                                .Where(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))
                                .ToArray();
                        }
                        if (candidates.Length == 0) return "{\"error\":\"Static method not found: " + EscapeJson(methodName) + "\"}";

                        // Pick the best overload: prefer exact param count match, then allow optional params
                        MethodInfo mi = null;
                        if (candidates.Length == 1) {
                            mi = candidates[0];
                        } else {
                            // Exact match on argument count
                            mi = candidates.FirstOrDefault(m => m.GetParameters().Length == args.Count);
                            if (mi == null) {
                                // Match methods where required param count <= args.Count <= total param count
                                mi = candidates.FirstOrDefault(m => {
                                    var p = m.GetParameters();
                                    var required = p.Count(pp => !pp.HasDefaultValue);
                                    return args.Count >= required && args.Count <= p.Length;
                                });
                            }
                            if (mi == null) mi = candidates[0]; // last resort: take first
                        }
                        var parameters = mi.GetParameters();
                        var convertedArgs = new object[parameters.Length];
                        for (int i = 0; i < parameters.Length; i++) {
                            if (i < args.Count) {
                                var paramType = parameters[i].ParameterType;
                                try {
                                    if (paramType.IsArray) {
                                        // Parse JSON array string into the correct array type
                                        var elemType = paramType.GetElementType();
                                        var parsed = ParseSimpleJsonArray(args[i]);
                                        var arr = Array.CreateInstance(elemType, parsed.Count);
                                        for (int j = 0; j < parsed.Count; j++) {
                                            try { arr.SetValue(Convert.ChangeType(parsed[j], elemType), j); }
                                            catch { arr.SetValue(parsed[j], j); }
                                        }
                                        convertedArgs[i] = arr;
                                    } else if (paramType.IsGenericType && (paramType.GetGenericTypeDefinition() == typeof(List<>)
                                        || paramType.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                                        || paramType.GetGenericTypeDefinition() == typeof(ICollection<>)
                                        || paramType.GetGenericTypeDefinition() == typeof(IList<>)
                                        || paramType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
                                        || paramType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))) {
                                        // Parse JSON array string into a List<T>
                                        var elemType = paramType.GetGenericArguments()[0];
                                        var listType = typeof(List<>).MakeGenericType(elemType);
                                        var list = (System.Collections.IList)Activator.CreateInstance(listType);
                                        var parsed = ParseSimpleJsonArray(args[i]);
                                        foreach (var item in parsed) {
                                            try { list.Add(Convert.ChangeType(item, elemType)); }
                                            catch { list.Add(item); }
                                        }
                                        convertedArgs[i] = list;
                                    } else if (string.Equals(args[i], "null", StringComparison.OrdinalIgnoreCase) && !paramType.IsValueType) {
                                        convertedArgs[i] = null;
                                    } else {
                                        convertedArgs[i] = Convert.ChangeType(args[i], paramType);
                                    }
                                }
                                catch { convertedArgs[i] = args[i]; }
                            } else if (parameters[i].HasDefaultValue) {
                                convertedArgs[i] = parameters[i].DefaultValue;
                            }
                        }

                        var returnVal = mi.Invoke(null, parameters.Length > 0 ? convertedArgs : null);

                        // Await Task-returning (async) methods
                        if (returnVal is System.Threading.Tasks.Task task) {
                            task.GetAwaiter().GetResult();
                            // Extract the result from Task<T> if applicable
                            var taskType = returnVal.GetType();
                            if (taskType.IsGenericType) {
                                var resultProp = taskType.GetProperty("Result");
                                if (resultProp != null)
                                    returnVal = resultProp.GetValue(returnVal);
                                else
                                    returnVal = null;
                            } else {
                                returnVal = null; // void Task
                            }
                        }

                        var serialized = SerializeReflectResult(returnVal);
                        sb.Append("{\"success\":true,\"result\":").Append(serialized).Append("}");
                        return sb.ToString();
                    }
                    default:
                        return "{\"error\":\"Unknown actionType: " + EscapeJson(actionType) + ". Expected http, sql, or reflect.\"}";
                }
            } catch (Exception ex) {
                return "{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// Serializes an arbitrary return value from a reflected method call into JSON.
        /// Handles null, primitives, strings, enums, collections, and objects with public properties.
        /// </summary>
        private static string SerializeReflectResult(object value, int depth = 0) {
            if (depth > 8) return "\"(max depth)\"";
            if (value == null) return "null";

            var type = value.GetType();

            // Primitives and simple types
            if (value is string s) return "\"" + EscapeJson(s) + "\"";
            if (value is bool b) return b ? "true" : "false";
            if (value is char c) return "\"" + EscapeJson(c.ToString()) + "\"";
            if (value is DateTime dt) return "\"" + EscapeJson(dt.ToString("o")) + "\"";
            if (value is DateTimeOffset dto) return "\"" + EscapeJson(dto.ToString("o")) + "\"";
            if (value is Guid g) return "\"" + g.ToString() + "\"";
            if (type.IsEnum) return "\"" + EscapeJson(value.ToString()) + "\"";

            // Numeric types
            if (value is int || value is long || value is short || value is byte
                || value is uint || value is ulong || value is ushort || value is sbyte
                || value is float || value is double || value is decimal) {
                return value.ToString();
            }

            // Dictionaries (IDictionary<string,?> or IDictionary)
            if (value is System.Collections.IDictionary dict) {
                var dsb = new StringBuilder("{");
                bool dFirst = true;
                foreach (System.Collections.DictionaryEntry entry in dict) {
                    if (!dFirst) dsb.Append(",");
                    dFirst = false;
                    dsb.Append("\"").Append(EscapeJson(entry.Key?.ToString() ?? "")).Append("\":");
                    dsb.Append(SerializeReflectResult(entry.Value, depth + 1));
                }
                dsb.Append("}");
                return dsb.ToString();
            }

            // Collections (IEnumerable)
            if (value is System.Collections.IEnumerable enumerable && !(value is string)) {
                var asb = new StringBuilder("[");
                bool aFirst = true;
                int itemCount = 0;
                foreach (var item in enumerable) {
                    if (itemCount >= 500) { asb.Append(",\"...(truncated)\""); break; }
                    if (!aFirst) asb.Append(",");
                    aFirst = false;
                    asb.Append(SerializeReflectResult(item, depth + 1));
                    itemCount++;
                }
                asb.Append("]");
                return asb.ToString();
            }

            // Objects: serialize public instance properties
            if (!type.IsPrimitive && type.IsClass) {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (props.Length == 0) return "\"" + EscapeJson(value.ToString()) + "\"";
                var osb = new StringBuilder("{");
                bool oFirst = true;
                foreach (var prop in props) {
                    if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                    object propVal;
                    try { propVal = prop.GetValue(value); } catch { continue; }
                    if (!oFirst) osb.Append(",");
                    oFirst = false;
                    osb.Append("\"").Append(EscapeJson(prop.Name)).Append("\":");
                    osb.Append(SerializeReflectResult(propVal, depth + 1));
                }
                osb.Append("}");
                return osb.ToString();
            }

            // Structs with properties
            if (type.IsValueType && !type.IsPrimitive && !type.IsEnum) {
                var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                if (props.Length > 0) {
                    var ssb = new StringBuilder("{");
                    bool sFirst = true;
                    foreach (var prop in props) {
                        if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                        object propVal;
                        try { propVal = prop.GetValue(value); } catch { continue; }
                        if (!sFirst) ssb.Append(",");
                        sFirst = false;
                        ssb.Append("\"").Append(EscapeJson(prop.Name)).Append("\":");
                        ssb.Append(SerializeReflectResult(propVal, depth + 1));
                    }
                    ssb.Append("}");
                    return ssb.ToString();
                }
            }

            // Fallback
            return "\"" + EscapeJson(value.ToString()) + "\"";
        }

        /// <summary>
        /// Returns true if the assembly is likely an application assembly rather than a
        /// .NET runtime / framework / system library.
        /// </summary>
        private static bool IsApplicationAssembly(Assembly asm) {
            if (asm.IsDynamic) return false;
            var name = asm.GetName().Name;
            if (string.IsNullOrEmpty(name)) return false;

            // Skip well-known system / framework prefixes
            string[] systemPrefixes = {
                "System", "Microsoft", "mscorlib", "netstandard",
                "WindowsBase", "PresentationCore", "PresentationFramework",
                "UIAutomation", "DirectWriteForwarder", "ReachFramework",
                "Accessibility", "NuGet", "Newtonsoft", "testhost"
            };
            foreach (var p in systemPrefixes) {
                if (name.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Skip anything that lives inside the runtime directory
            try {
                var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                var loc = asm.Location;
                if (!string.IsNullOrEmpty(loc) && !string.IsNullOrEmpty(runtimeDir)
                    && loc.StartsWith(runtimeDir, StringComparison.OrdinalIgnoreCase))
                    return false;
            } catch { }

            return true;
        }

        /// <summary>
        /// Returns discoverable types and their public static methods from loaded assemblies
        /// so the UI can offer reflection-based invocation targets.
        /// </summary>
        private object ApiEventsReflect(HttpRequest req) {
            req.Context.ContentType = "application/json";
            var session = GetSession(req);
            if (session == null) { req.Context.StatusCode = "401 Unauthorized"; return "{\"error\":\"Not authenticated.\"}"; }

            var query = "";
            if (req.QueryString != null) {
                var qs = System.Web.HttpUtility.ParseQueryString(req.QueryString);
                query = qs["q"] ?? "";
            }

            var sb = new StringBuilder();
            sb.Append("{\"types\":[");
            bool first = true;
            int count = 0;
            const int limit = 200;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) {
                if (count >= limit) break;
                if (!IsApplicationAssembly(asm)) continue;
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                foreach (var t in types) {
                    if (count >= limit) break;
                    if (t.IsAbstract && t.IsSealed) { /* static class – always include */ }
                    else if (!t.IsPublic) continue;

                    MethodInfo[] methods;
                    try { methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
                    if (methods.Length == 0) continue;

                    var fullName = t.FullName ?? t.Name;
                    if (!string.IsNullOrEmpty(query) && fullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                        && !methods.Any(m => m.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;

                    if (!first) sb.Append(",");
                    first = false;
                    sb.Append("{\"type\":\"").Append(EscapeJson(fullName)).Append("\",\"methods\":[");
                    bool mFirst = true;
                    foreach (var m in methods) {
                        if (!mFirst) sb.Append(",");
                        mFirst = false;
                        sb.Append("{\"name\":\"").Append(EscapeJson(m.Name));
                        sb.Append("\",\"returnType\":\"").Append(EscapeJson(m.ReturnType.Name));
                        sb.Append("\",\"params\":[");
                        bool pFirst = true;
                        foreach (var p in m.GetParameters()) {
                            if (!pFirst) sb.Append(",");
                            pFirst = false;
                            sb.Append("{\"name\":\"").Append(EscapeJson(p.Name));
                            sb.Append("\",\"type\":\"").Append(EscapeJson(p.ParameterType.Name));
                            sb.Append("\",\"fullType\":\"").Append(EscapeJson(p.ParameterType.FullName ?? p.ParameterType.Name));
                            sb.Append("\",\"optional\":").Append(p.HasDefaultValue ? "true" : "false");
                            if (p.HasDefaultValue && p.DefaultValue != null) {
                                sb.Append(",\"defaultValue\":\"").Append(EscapeJson(p.DefaultValue.ToString())).Append("\"");
                            }
                            // Detect if param type is an enum and include values
                            var pType = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                            if (pType.IsEnum) {
                                sb.Append(",\"isEnum\":true,\"enumValues\":[");
                                var names = Enum.GetNames(pType);
                                for (int ei = 0; ei < names.Length; ei++) {
                                    if (ei > 0) sb.Append(",");
                                    sb.Append("\"").Append(EscapeJson(names[ei])).Append("\"");
                                }
                                sb.Append("]");
                            }
                            // Detect if param type is an application type and include provider methods
                            if (!pType.IsEnum && !pType.IsPrimitive && pType != typeof(string) && pType != typeof(decimal)
                                && pType != typeof(DateTime) && pType != typeof(Guid) && pType != typeof(char)
                                && pType != typeof(Uri) && pType != typeof(object)
                                && !pType.IsArray && !(pType.IsGenericType && pType.GetGenericTypeDefinition() == typeof(Nullable<>))) {
                                var pTypeAsm = pType.Assembly;
                                if (IsApplicationAssembly(pTypeAsm)) {
                                    sb.Append(",\"isAppType\":true,\"appMethods\":[");
                                    bool amFirst = true;
                                    // Find static methods across all app assemblies that return this type or collections of it
                                    foreach (var provAsm in AppDomain.CurrentDomain.GetAssemblies()) {
                                        if (!IsApplicationAssembly(provAsm)) continue;
                                        Type[] provTypes;
                                        try { provTypes = provAsm.GetTypes(); } catch { continue; }
                                        foreach (var pt in provTypes) {
                                            if (!pt.IsPublic && !(pt.IsAbstract && pt.IsSealed)) continue;
                                            MethodInfo[] provMethods;
                                            try { provMethods = pt.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly); } catch { continue; }
                                            foreach (var pm in provMethods) {
                                                var retType = pm.ReturnType;
                                                if (typeof(System.Threading.Tasks.Task).IsAssignableFrom(retType) && retType.IsGenericType)
                                                    retType = retType.GetGenericArguments()[0];
                                                bool match = retType == pType
                                                    || (retType.IsArray && retType.GetElementType() == pType)
                                                    || (retType.IsGenericType && retType.GetGenericArguments().Length == 1 && retType.GetGenericArguments()[0] == pType);
                                                if (!match) continue;
                                                if (!amFirst) sb.Append(",");
                                                amFirst = false;
                                                sb.Append("{\"type\":\"").Append(EscapeJson(pt.FullName ?? pt.Name));
                                                sb.Append("\",\"method\":\"").Append(EscapeJson(pm.Name));
                                                sb.Append("\",\"returnType\":\"").Append(EscapeJson(pm.ReturnType.Name));
                                                sb.Append("\",\"params\":[");
                                                bool ppFirst = true;
                                                foreach (var pp in pm.GetParameters()) {
                                                    if (!ppFirst) sb.Append(",");
                                                    ppFirst = false;
                                                    sb.Append("{\"name\":\"").Append(EscapeJson(pp.Name));
                                                    sb.Append("\",\"type\":\"").Append(EscapeJson(pp.ParameterType.Name));
                                                    sb.Append("\",\"optional\":").Append(pp.HasDefaultValue ? "true" : "false");
                                                    sb.Append("}");
                                                }
                                                sb.Append("]}");
                                            }
                                        }
                                    }
                                    sb.Append("]");
                                }
                            }
                            sb.Append("}");
                        }
                        sb.Append("]}");
                    }
                    sb.Append("]}");
                    count++;
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static List<string> ParseSimpleJsonArray(string json) {
            var list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            json = json.Trim();
            if (!json.StartsWith("[")) return list;
            int i = 1;
            while (i < json.Length) {
                while (i < json.Length && (json[i] == ' ' || json[i] == ',')) i++;
                if (i >= json.Length || json[i] == ']') break;
                if (json[i] == '"') {
                    i++;
                    var sb2 = new StringBuilder();
                    while (i < json.Length && json[i] != '"') {
                        if (json[i] == '\\' && i + 1 < json.Length) { i++; sb2.Append(json[i]); }
                        else sb2.Append(json[i]);
                        i++;
                    }
                    i++; // closing quote
                    list.Add(sb2.ToString());
                } else {
                    int start = i;
                    while (i < json.Length && json[i] != ',' && json[i] != ']') i++;
                    list.Add(json.Substring(start, i - start).Trim());
                }
            }
            return list;
        }

        #endregion

        #region JSON Helpers

        private static string ExtractJsonProperty(string json, string key) {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            try {
                using (var document = JsonDocument.Parse(json)) {
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        return null;
                    if (!document.RootElement.TryGetProperty(key, out var value))
                        return null;

                    switch (value.ValueKind) {
                        case JsonValueKind.String:
                            return value.GetString();
                        case JsonValueKind.True:
                            return "true";
                        case JsonValueKind.False:
                            return "false";
                        case JsonValueKind.Null:
                        case JsonValueKind.Undefined:
                            return null;
                        default:
                            return value.GetRawText();
                    }
                }
            } catch {
                return ExtractJsonString(json, key);
            }
        }

        private static string ExtractJsonString(string json, string key) {
            if (string.IsNullOrEmpty(json)) return null;
            var search = "\"" + key + "\"";
            int idx = json.IndexOf(search, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            idx += search.Length;
            // Skip whitespace and colon
            while (idx < json.Length && (json[idx] == ' ' || json[idx] == ':')) idx++;
            if (idx >= json.Length || json[idx] != '"') return null;
            idx++; // skip opening quote
            var sb = new StringBuilder();
            while (idx < json.Length) {
                char c = json[idx];
                if (c == '\\' && idx + 1 < json.Length) {
                    idx++;
                    char next = json[idx];
                    switch (next) {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        default: sb.Append(next); break;
                    }
                } else if (c == '"') {
                    break;
                } else {
                    sb.Append(c);
                }
                idx++;
            }
            return sb.ToString();
        }

        private static string EscapeJson(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s) {
                switch (c) {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        #endregion

        #region Page Template Loading

        /// <summary>
        /// Loads an HTML page template from <see cref="PagesFolder"/> with file-hash-based caching.
        /// Returns the file content, or an empty string if the file does not exist.
        /// </summary>
        private static string LoadPageTemplate(string filename) {
            var filePath = Path.Combine(PagesFolder, filename);
            if (!File.Exists(filePath)) return "";

            var currentHash = ComputeFileHash(filePath);
            if (_pageHashCache.TryGetValue(filename, out var cachedHash) && cachedHash == currentHash) {
                if (_pageTemplateCache.TryGetValue(filename, out var cached)) return cached;
            }

            var content = File.ReadAllText(filePath);
            _pageTemplateCache[filename] = content;
            _pageHashCache[filename] = currentHash;
            return content;
        }

        private static string ComputeFileHash(string filePath) {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                var hash = md5.ComputeHash(stream);
                return BitConverter.ToString(hash);
            }
        }

        /// <summary>
        /// Extracts an embedded HTML resource to <see cref="PagesFolder"/> if the file does not
        /// already exist on disk. The resource is written once; users may then customise the
        /// on-disk copy and it will not be overwritten.
        /// </summary>
        private static void LoadHtmlFromBinary(string filename) {
            var filePath = Path.Combine(PagesFolder, filename);
            if (File.Exists(filePath)) return;

            var asm = typeof(SqlAdminPanel).Assembly;
            var resourceName = "SocketJack." + filename;
            using (var stream = asm.GetManifestResourceStream(resourceName)) {
                if (stream == null) return;
                Directory.CreateDirectory(PagesFolder);
                using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None)) {
                    stream.CopyTo(fs);
                }
            }
        }

        #endregion

        #region HTML Generation

        private string LoginPageHtml() {
            LoadHtmlFromBinary("SqlLogin.html");
            var template = LoadPageTemplate("SqlLogin.html");
            if (!string.IsNullOrEmpty(template)) {
                return template.Replace("$BasePath", BasePath.TrimEnd('/'));
            }
            return "";
        }

        private string PanelPageHtml(SqlAdminSession session) {
            LoadHtmlFromBinary("SqlPanel.html");
            var template = LoadPageTemplate("SqlPanel.html");
            if (!string.IsNullOrEmpty(template)) {
                var basePath = BasePath.TrimEnd('/');
                return template
                    .Replace("$BasePath", basePath)
                    .Replace("$Username", EscapeHtml(session.Username))
                    .Replace("$CurrentDatabase", EscapeJson(session.CurrentDatabase ?? "db"));
            }
            return "";
        }

        private static string EscapeHtml(string s) {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        #endregion
    }
}
