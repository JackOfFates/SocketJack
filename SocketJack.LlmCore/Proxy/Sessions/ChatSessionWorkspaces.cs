using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using LmVs;
using SocketJack.Net.Database;
using SocketJack.Sandbox;

namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
        private const string WorkspaceRolePrimary = "primary";
        private const string WorkspaceRoleAttached = "attached";
        private const string WorkspaceRoleGlobal = "global";
        private const string WorkspaceAccessReadOnly = "read-only";
        private const string WorkspaceAccessReadWrite = "read-write";
        private static readonly TimeSpan WorkspaceRegexTimeout = TimeSpan.FromMilliseconds(250);

        private Table GetChatWorkspaceRootsTable()
        {
            SocketJack.Net.Database.Database db = _chatSessionData.Databases.GetOrAdd(
                "SocketJack",
                _ => new SocketJack.Net.Database.Database("SocketJack"));
            Table table = db.Tables.GetOrAdd(
                "LmVsProxyWorkspaceRoots",
                _ => new Table("LmVsProxyWorkspaceRoots"));
            if (table.Columns == null)
                table.Columns = new List<Column>();
            EnsureColumn(table, 0, "Id", 96);
            EnsureColumn(table, 1, "OwnerKey", 160);
            EnsureColumn(table, 2, "SessionId", 160);
            EnsureColumn(table, 3, "Role", 24);
            EnsureColumn(table, 4, "DisplayName", 240);
            EnsureColumn(table, 5, "DirectoryPath", 1024);
            EnsureColumn(table, 6, "AccessMode", 24);
            EnsureColumn(table, 7, "ParentId", 96);
            EnsureColumn(table, 8, "CreatedUtc", 80);
            EnsureColumn(table, 9, "UpdatedUtc", 80);
            if (table.Rows == null)
                table.Rows = new List<object[]>();
            for (int i = 0; i < table.Rows.Count; i++)
                table.Rows[i] = NormalizeChatWorkspaceRootRow(table.Rows[i]);
            return table;
        }

        private Table GetChatWorkspaceIgnoreRulesTable()
        {
            SocketJack.Net.Database.Database db = _chatSessionData.Databases.GetOrAdd(
                "SocketJack",
                _ => new SocketJack.Net.Database.Database("SocketJack"));
            Table table = db.Tables.GetOrAdd(
                "LmVsProxyWorkspaceIgnoreRules",
                _ => new Table("LmVsProxyWorkspaceIgnoreRules"));
            if (table.Columns == null)
                table.Columns = new List<Column>();
            EnsureColumn(table, 0, "Id", 96);
            EnsureColumn(table, 1, "OwnerKey", 160);
            EnsureColumn(table, 2, "SessionId", 160);
            EnsureColumn(table, 3, "RootId", 96);
            EnsureColumn(table, 4, "Name", 240);
            EnsureColumn(table, 5, "Pattern", 4096);
            EnsureColumn(table, 6, "Target", 32);
            EnsureColumn(table, 7, "CaseSensitive", 16);
            EnsureColumn(table, 8, "Enabled", 16);
            EnsureColumn(table, 9, "BuilderMode", 32);
            EnsureColumn(table, 10, "BuilderStateJson", 8192);
            EnsureColumn(table, 11, "CreatedUtc", 80);
            EnsureColumn(table, 12, "UpdatedUtc", 80);
            if (table.Rows == null)
                table.Rows = new List<object[]>();
            for (int i = 0; i < table.Rows.Count; i++)
                table.Rows[i] = NormalizeChatWorkspaceIgnoreRuleRow(table.Rows[i]);
            return table;
        }

        private object[] NormalizeChatWorkspaceRootRow(object[] row)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            object[] normalized = new object[10]
            {
                "", "", "", WorkspaceRoleAttached, "", "",
                WorkspaceAccessReadWrite, "", now, now
            };
            if (row != null)
            {
                for (int i = 0; i < Math.Min(row.Length, normalized.Length); i++)
                    if (row[i] != null)
                        normalized[i] = row[i];
            }
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 0)))
                normalized[0] = "wsroot_" + Guid.NewGuid().ToString("N");
            normalized[2] = (GetRowValue(normalized, 2) ?? "").Trim();
            normalized[3] = NormalizeWorkspaceRole(GetRowValue(normalized, 3), GetRowValue(normalized, 2));
            normalized[4] = NormalizeWorkspaceDisplayName(GetRowValue(normalized, 4), GetRowValue(normalized, 5));
            normalized[6] = string.Equals(GetRowValue(normalized, 3), WorkspaceRoleGlobal, StringComparison.OrdinalIgnoreCase)
                ? WorkspaceAccessReadOnly
                : NormalizeWorkspaceAccessMode(GetRowValue(normalized, 6));
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 8)))
                normalized[8] = now;
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 9)))
                normalized[9] = GetRowValue(normalized, 8);
            return normalized;
        }

        private object[] NormalizeChatWorkspaceIgnoreRuleRow(object[] row)
        {
            string now = DateTimeOffset.UtcNow.ToString("O");
            object[] normalized = new object[13]
            {
                "", "", "", "", "", "", "path", "false",
                "true", "raw", "{}", now, now
            };
            if (row != null)
            {
                for (int i = 0; i < Math.Min(row.Length, normalized.Length); i++)
                    if (row[i] != null)
                        normalized[i] = row[i];
            }
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 0)))
                normalized[0] = "wsignore_" + Guid.NewGuid().ToString("N");
            normalized[6] = NormalizeWorkspaceIgnoreTarget(GetRowValue(normalized, 6));
            normalized[7] = ParseStoredBool(GetRowValue(normalized, 7), false).ToString().ToLowerInvariant();
            normalized[8] = ParseStoredBool(GetRowValue(normalized, 8), true).ToString().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 9)))
                normalized[9] = "raw";
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 10)))
                normalized[10] = "{}";
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 11)))
                normalized[11] = now;
            if (string.IsNullOrWhiteSpace(GetRowValue(normalized, 12)))
                normalized[12] = GetRowValue(normalized, 11);
            return normalized;
        }

        private static string NormalizeWorkspaceRole(string role, string sessionId)
        {
            role = (role ?? "").Trim().ToLowerInvariant();
            if (role == WorkspaceRolePrimary || role == WorkspaceRoleAttached)
                return role;
            return string.IsNullOrWhiteSpace(sessionId) ? WorkspaceRoleGlobal : WorkspaceRoleAttached;
        }

        private static string NormalizeWorkspaceAccessMode(string accessMode)
        {
            accessMode = (accessMode ?? "").Trim().ToLowerInvariant();
            return accessMode == WorkspaceAccessReadOnly ? WorkspaceAccessReadOnly : WorkspaceAccessReadWrite;
        }

        private static string NormalizeWorkspaceIgnoreTarget(string target)
        {
            target = (target ?? "").Trim().ToLowerInvariant();
            return target == "filename" || target == "extension" || target == "directory"
                ? target
                : "path";
        }

        private static string NormalizeWorkspaceDisplayName(string displayName, string path)
        {
            displayName = (displayName ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(displayName))
                return displayName.Length <= 120 ? displayName : displayName.Substring(0, 120);
            try
            {
                string full = Path.GetFullPath(path ?? "").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string name = Path.GetFileName(full);
                return string.IsNullOrWhiteSpace(name) ? full : name;
            }
            catch
            {
                return "Workspace";
            }
        }

        private void EnsureLegacyGlobalWorkspaceRoots(string ownerKey)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            if (string.IsNullOrWhiteSpace(ownerKey))
                return;
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceRootsTable();
                bool changed = false;
                foreach (ChatFilesystemAccessEntry legacy in GetChatFilesystemAccess(ownerKey))
                {
                    if (legacy == null || string.IsNullOrWhiteSpace(legacy.path))
                        continue;
                    bool exists = table.Rows.Any(row =>
                    {
                        object[] normalized = NormalizeChatWorkspaceRootRow(row);
                        return ChatOwnerKeysMatch(ownerKey, GetRowValue(normalized, 1)) &&
                               string.IsNullOrWhiteSpace(GetRowValue(normalized, 2)) &&
                               PathsEqual(GetRowValue(normalized, 5), legacy.path);
                    });
                    if (exists)
                        continue;
                    string now = DateTimeOffset.UtcNow.ToString("O");
                    table.Rows.Add(new object[10]
                    {
                        "wsroot_" + Guid.NewGuid().ToString("N"),
                        ownerKey,
                        "",
                        WorkspaceRoleGlobal,
                        NormalizeWorkspaceDisplayName("", legacy.path),
                        Path.GetFullPath(legacy.path),
                        WorkspaceAccessReadOnly,
                        "",
                        string.IsNullOrWhiteSpace(legacy.createdUtc) ? now : legacy.createdUtc,
                        now
                    });
                    changed = true;
                }
                if (changed)
                    SaveChatSessionDataAndInvalidateCaches();
            }
        }

        private void RemoveLegacyGlobalWorkspaceRoot(string ownerKey, string directoryPath)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            if (string.IsNullOrWhiteSpace(ownerKey) || string.IsNullOrWhiteSpace(directoryPath))
                return;
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceRootsTable();
                HashSet<string> removedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                table.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceRootRow(source);
                    bool match = ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                                 string.IsNullOrWhiteSpace(GetRowValue(row, 2)) &&
                                 PathsEqual(GetRowValue(row, 5), directoryPath);
                    if (match)
                        removedIds.Add(GetRowValue(row, 0));
                    return match;
                });
                if (removedIds.Count == 0)
                    return;
                Table rules = GetChatWorkspaceIgnoreRulesTable();
                rules.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceIgnoreRuleRow(source);
                    return removedIds.Contains(GetRowValue(row, 3));
                });
                SaveChatSessionDataAndInvalidateCaches();
            }
        }

        public IReadOnlyList<ChatWorkspaceRootSnapshot> GetChatWorkspaceRootsDiagnostics(string ownerKey, string sessionId)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            EnsureLegacyGlobalWorkspaceRoots(ownerKey);
            List<ChatWorkspaceRootSnapshot> roots = new List<ChatWorkspaceRootSnapshot>();
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceRootsTable();
                foreach (object[] source in table.Rows)
                {
                    object[] row = NormalizeChatWorkspaceRootRow(source);
                    if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)))
                        continue;
                    string rowSessionId = GetRowValue(row, 2);
                    if (!string.IsNullOrWhiteSpace(rowSessionId) &&
                        !string.Equals(rowSessionId, sessionId, StringComparison.Ordinal))
                        continue;
                    ChatWorkspaceRootSnapshot snapshot = ChatWorkspaceRootFromRow(row);
                    snapshot.IsInherited = string.IsNullOrWhiteSpace(rowSessionId);
                    roots.Add(snapshot);
                }
            }
            bool hasPrimary = roots.Any(root =>
                string.Equals(root.Role, WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(root.SessionId, sessionId, StringComparison.Ordinal));
            if (!hasPrimary && !string.IsNullOrWhiteSpace(sessionId))
            {
                string sandboxPath = GetChatSessionFilesDirectory(sessionId);
                roots.Insert(0, new ChatWorkspaceRootSnapshot
                {
                    Id = "sandbox_" + ComputeStableShortHash(sessionId),
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    Role = WorkspaceRolePrimary,
                    DisplayName = "Project Files",
                    Path = sandboxPath ?? "",
                    AccessMode = WorkspaceAccessReadWrite,
                    Exists = true,
                    IsSandbox = true,
                    IsInherited = false
                });
            }
            roots.Sort((left, right) =>
            {
                int leftOrder = WorkspaceRoleOrder(left.Role);
                int rightOrder = WorkspaceRoleOrder(right.Role);
                int order = leftOrder.CompareTo(rightOrder);
                return order != 0 ? order : string.Compare(left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
            });
            return roots;
        }

        private static int WorkspaceRoleOrder(string role)
        {
            if (string.Equals(role, WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(role, WorkspaceRoleAttached, StringComparison.OrdinalIgnoreCase))
                return 1;
            return 2;
        }

        private ChatWorkspaceRootSnapshot ChatWorkspaceRootFromRow(object[] row)
        {
            string path = GetRowValue(row, 5);
            return new ChatWorkspaceRootSnapshot
            {
                Id = GetRowValue(row, 0),
                OwnerKey = GetRowValue(row, 1),
                SessionId = GetRowValue(row, 2),
                Role = GetRowValue(row, 3),
                DisplayName = GetRowValue(row, 4),
                Path = path,
                AccessMode = GetRowValue(row, 6),
                ParentId = GetRowValue(row, 7),
                CreatedUtc = GetRowValue(row, 8),
                UpdatedUtc = GetRowValue(row, 9),
                Exists = Directory.Exists(path)
            };
        }

        public ChatWorkspaceRootSnapshot SaveChatWorkspaceRootDiagnostics(
            string ownerKey,
            string sessionId,
            string rootId,
            string role,
            string displayName,
            string directoryPath,
            string accessMode,
            string parentId = "")
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            role = NormalizeWorkspaceRole(role, sessionId);
            if (role == WorkspaceRoleGlobal)
                sessionId = "";
            if (role != WorkspaceRoleGlobal && string.IsNullOrWhiteSpace(sessionId))
                throw new InvalidOperationException("A session is required for primary and attached workspace folders.");
            if (!TryNormalizeAccessibleDirectory(directoryPath, requireExists: true, out string normalizedPath, out string pathError))
                throw new InvalidOperationException(pathError);
            displayName = NormalizeWorkspaceDisplayName(displayName, normalizedPath);
            accessMode = role == WorkspaceRoleGlobal ? WorkspaceAccessReadOnly : NormalizeWorkspaceAccessMode(accessMode);
            string now = DateTimeOffset.UtcNow.ToString("O");
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceRootsTable();
                int existingIndex = -1;
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    object[] candidate = NormalizeChatWorkspaceRootRow(table.Rows[i]);
                    if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(candidate, 1)))
                        continue;
                    if (!string.IsNullOrWhiteSpace(rootId) &&
                        string.Equals(GetRowValue(candidate, 0), rootId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = i;
                        break;
                    }
                }
                foreach (object[] source in table.Rows)
                {
                    object[] candidate = NormalizeChatWorkspaceRootRow(source);
                    if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(candidate, 1)) ||
                        !string.Equals(GetRowValue(candidate, 2), sessionId, StringComparison.Ordinal))
                        continue;
                    if (existingIndex >= 0 &&
                        string.Equals(GetRowValue(candidate, 0), rootId, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (string.Equals(GetRowValue(candidate, 4), displayName, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Workspace display names must be unique within the session.");
                    if (PathsEqual(GetRowValue(candidate, 5), normalizedPath))
                        throw new InvalidOperationException("That folder is already attached to this session.");
                }
                if (role == WorkspaceRolePrimary)
                {
                    for (int i = 0; i < table.Rows.Count; i++)
                    {
                        object[] candidate = NormalizeChatWorkspaceRootRow(table.Rows[i]);
                        if (ChatOwnerKeysMatch(ownerKey, GetRowValue(candidate, 1)) &&
                            string.Equals(GetRowValue(candidate, 2), sessionId, StringComparison.Ordinal) &&
                            string.Equals(GetRowValue(candidate, 3), WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(GetRowValue(candidate, 0), rootId, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate[3] = WorkspaceRoleAttached;
                            candidate[9] = now;
                            table.Rows[i] = candidate;
                        }
                    }
                }
                string id = existingIndex >= 0 ? GetRowValue(table.Rows[existingIndex], 0) : "wsroot_" + Guid.NewGuid().ToString("N");
                string created = existingIndex >= 0 ? GetRowValue(table.Rows[existingIndex], 8) : now;
                object[] row = new object[10]
                {
                    id, ownerKey, sessionId, role, displayName, normalizedPath,
                    accessMode, parentId ?? "", created, now
                };
                if (existingIndex >= 0)
                    table.Rows[existingIndex] = row;
                else
                    table.Rows.Add(row);
                SaveChatSessionDataAndInvalidateCaches();
                return ChatWorkspaceRootFromRow(row);
            }
        }

        public bool RemoveChatWorkspaceRootDiagnostics(string ownerKey, string sessionId, string rootId)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            if (string.IsNullOrWhiteSpace(rootId) || rootId.StartsWith("sandbox_", StringComparison.OrdinalIgnoreCase))
                return false;
            lock (_chatSessionLock)
            {
                Table roots = GetChatWorkspaceRootsTable();
                int removed = roots.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceRootRow(source);
                    return ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                           string.Equals(GetRowValue(row, 0), rootId, StringComparison.OrdinalIgnoreCase) &&
                           (string.IsNullOrWhiteSpace(GetRowValue(row, 2)) ||
                            string.Equals(GetRowValue(row, 2), sessionId, StringComparison.Ordinal));
                });
                if (removed == 0)
                    return false;
                Table rules = GetChatWorkspaceIgnoreRulesTable();
                rules.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceIgnoreRuleRow(source);
                    return ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                           string.Equals(GetRowValue(row, 3), rootId, StringComparison.OrdinalIgnoreCase);
                });
                SaveChatSessionDataAndInvalidateCaches();
                return true;
            }
        }

        public ChatWorkspaceRootSnapshot MoveChatWorkspaceRootDiagnostics(
            string ownerKey,
            string sessionId,
            string rootId,
            string destinationPath)
        {
            ChatWorkspaceRootSnapshot root = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId)
                .FirstOrDefault(item => string.Equals(item.Id, rootId, StringComparison.OrdinalIgnoreCase));
            if (root == null || root.IsSandbox || root.IsInherited)
                throw new InvalidOperationException("Only session-owned workspace folders can be moved on disk.");
            string source = Path.GetFullPath(root.Path);
            string destination = Path.GetFullPath(destinationPath ?? "");
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("Workspace folder does not exist: " + source);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("Move destination already exists: " + destination);
            if (IsPathInsideRoot(destination, source))
                throw new InvalidOperationException("Move destination cannot be inside the workspace being moved.");
            string sourceVolume = Path.GetPathRoot(source);
            string destinationVolume = Path.GetPathRoot(destination);
            if (!string.Equals(sourceVolume, destinationVolume, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Move on disk is limited to the same drive. Use Relink or Fork for cross-drive changes.");
            string parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("Move destination parent does not exist: " + parent);
            Directory.Move(source, destination);
            try
            {
                return SaveChatWorkspaceRootDiagnostics(
                    ownerKey, sessionId, root.Id, root.Role, root.DisplayName,
                    destination, root.AccessMode, root.ParentId);
            }
            catch
            {
                if (Directory.Exists(destination) && !Directory.Exists(source))
                    Directory.Move(destination, source);
                throw;
            }
        }

        public IReadOnlyList<ChatWorkspaceIgnoreRuleSnapshot> GetChatWorkspaceIgnoreRulesDiagnostics(string ownerKey, string sessionId)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            List<ChatWorkspaceIgnoreRuleSnapshot> rules = new List<ChatWorkspaceIgnoreRuleSnapshot>();
            lock (_chatSessionLock)
            {
                foreach (object[] source in GetChatWorkspaceIgnoreRulesTable().Rows)
                {
                    object[] row = NormalizeChatWorkspaceIgnoreRuleRow(source);
                    if (!ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)))
                        continue;
                    string ruleSession = GetRowValue(row, 2);
                    if (!string.IsNullOrWhiteSpace(ruleSession) &&
                        !string.Equals(ruleSession, sessionId, StringComparison.Ordinal))
                        continue;
                    rules.Add(ChatWorkspaceIgnoreRuleFromRow(row));
                }
            }
            return rules.OrderBy(rule => rule.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private ChatWorkspaceIgnoreRuleSnapshot ChatWorkspaceIgnoreRuleFromRow(object[] row)
        {
            return new ChatWorkspaceIgnoreRuleSnapshot
            {
                Id = GetRowValue(row, 0),
                OwnerKey = GetRowValue(row, 1),
                SessionId = GetRowValue(row, 2),
                RootId = GetRowValue(row, 3),
                Name = GetRowValue(row, 4),
                Pattern = GetRowValue(row, 5),
                Target = GetRowValue(row, 6),
                CaseSensitive = ParseStoredBool(GetRowValue(row, 7), false),
                Enabled = ParseStoredBool(GetRowValue(row, 8), true),
                BuilderMode = GetRowValue(row, 9),
                BuilderStateJson = GetRowValue(row, 10),
                CreatedUtc = GetRowValue(row, 11),
                UpdatedUtc = GetRowValue(row, 12)
            };
        }

        public ChatWorkspaceIgnoreRuleSnapshot SaveChatWorkspaceIgnoreRuleDiagnostics(
            string ownerKey,
            string sessionId,
            string ruleId,
            string rootId,
            string name,
            string pattern,
            string target,
            bool caseSensitive,
            bool enabled,
            string builderMode,
            string builderStateJson)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            pattern = (pattern ?? "").Trim();
            if (string.IsNullOrWhiteSpace(pattern))
                throw new InvalidOperationException("Ignore regex is required.");
            ValidateWorkspaceRegex(pattern, caseSensitive);
            target = NormalizeWorkspaceIgnoreTarget(target);
            if (!string.IsNullOrWhiteSpace(rootId))
            {
                ChatWorkspaceRootSnapshot root = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId)
                    .FirstOrDefault(item => string.Equals(item.Id, rootId, StringComparison.OrdinalIgnoreCase));
                if (root == null)
                    throw new InvalidOperationException("The selected workspace folder is unavailable.");
            }
            if (!string.IsNullOrWhiteSpace(builderStateJson))
            {
                using JsonDocument ignored = JsonDocument.Parse(builderStateJson);
            }
            string now = DateTimeOffset.UtcNow.ToString("O");
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceIgnoreRulesTable();
                int existingIndex = -1;
                for (int i = 0; i < table.Rows.Count; i++)
                {
                    object[] candidate = NormalizeChatWorkspaceIgnoreRuleRow(table.Rows[i]);
                    if (ChatOwnerKeysMatch(ownerKey, GetRowValue(candidate, 1)) &&
                        string.Equals(GetRowValue(candidate, 0), ruleId, StringComparison.OrdinalIgnoreCase))
                    {
                        existingIndex = i;
                        break;
                    }
                }
                string id = existingIndex >= 0 ? GetRowValue(table.Rows[existingIndex], 0) : "wsignore_" + Guid.NewGuid().ToString("N");
                string created = existingIndex >= 0 ? GetRowValue(table.Rows[existingIndex], 11) : now;
                object[] row = new object[13]
                {
                    id, ownerKey, sessionId ?? "", rootId ?? "",
                    string.IsNullOrWhiteSpace(name) ? "Ignore rule" : name.Trim(),
                    pattern, target,
                    caseSensitive.ToString().ToLowerInvariant(),
                    enabled.ToString().ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(builderMode) ? "raw" : builderMode.Trim().ToLowerInvariant(),
                    string.IsNullOrWhiteSpace(builderStateJson) ? "{}" : builderStateJson,
                    created, now
                };
                if (existingIndex >= 0)
                    table.Rows[existingIndex] = row;
                else
                    table.Rows.Add(row);
                SaveChatSessionDataAndInvalidateCaches();
                return ChatWorkspaceIgnoreRuleFromRow(row);
            }
        }

        public bool RemoveChatWorkspaceIgnoreRuleDiagnostics(string ownerKey, string sessionId, string ruleId)
        {
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            sessionId = NormalizeOptionalDeveloperProjectSessionId(sessionId);
            lock (_chatSessionLock)
            {
                Table table = GetChatWorkspaceIgnoreRulesTable();
                int removed = table.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceIgnoreRuleRow(source);
                    return ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                           string.Equals(GetRowValue(row, 0), ruleId, StringComparison.OrdinalIgnoreCase) &&
                           (string.IsNullOrWhiteSpace(GetRowValue(row, 2)) ||
                            string.Equals(GetRowValue(row, 2), sessionId, StringComparison.Ordinal));
                });
                if (removed > 0)
                    SaveChatSessionDataAndInvalidateCaches();
                return removed > 0;
            }
        }

        public bool TestChatWorkspaceIgnoreRegex(string pattern, string target, bool caseSensitive, string testPath, out string error)
        {
            error = "";
            try
            {
                Regex regex = CreateWorkspaceRegex(pattern, caseSensitive);
                string normalized = NormalizeWorkspaceRelativePath(testPath);
                return regex.IsMatch(GetWorkspaceIgnoreMatchValue(normalized, NormalizeWorkspaceIgnoreTarget(target)));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void ValidateWorkspaceRegex(string pattern, bool caseSensitive)
        {
            _ = CreateWorkspaceRegex(pattern, caseSensitive);
        }

        private static Regex CreateWorkspaceRegex(string pattern, bool caseSensitive)
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (!caseSensitive)
                options |= RegexOptions.IgnoreCase;
            return new Regex(pattern ?? "", options, WorkspaceRegexTimeout);
        }

        private bool IsChatWorkspacePathIgnored(string ownerKey, string sessionId, string fullPath, out string ruleName)
        {
            ruleName = "";
            ChatWorkspaceRootSnapshot root = FindEffectiveChatWorkspaceRoot(ownerKey, sessionId, fullPath);
            if (root == null)
                return false;
            string relative;
            try
            {
                relative = NormalizeWorkspaceRelativePath(SafeRelativePath(root.Path, fullPath));
            }
            catch
            {
                return false;
            }
            foreach (ChatWorkspaceIgnoreRuleSnapshot rule in GetChatWorkspaceIgnoreRulesDiagnostics(ownerKey, sessionId))
            {
                if (!rule.Enabled)
                    continue;
                if (!string.IsNullOrWhiteSpace(rule.RootId) &&
                    !string.Equals(rule.RootId, root.Id, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    string value = GetWorkspaceIgnoreMatchValue(relative, rule.Target);
                    if (CreateWorkspaceRegex(rule.Pattern, rule.CaseSensitive).IsMatch(value))
                    {
                        ruleName = rule.Name;
                        return true;
                    }
                }
                catch
                {
                }
            }
            return false;
        }

        private static string NormalizeWorkspaceRelativePath(string path)
        {
            return (path ?? "").Replace('\\', '/').TrimStart('/');
        }

        private static string GetWorkspaceIgnoreMatchValue(string relativePath, string target)
        {
            relativePath = NormalizeWorkspaceRelativePath(relativePath);
            switch (NormalizeWorkspaceIgnoreTarget(target))
            {
                case "filename":
                    return Path.GetFileName(relativePath) ?? "";
                case "extension":
                    return (Path.GetExtension(relativePath) ?? "").TrimStart('.');
                case "directory":
                    return NormalizeWorkspaceRelativePath(Path.GetDirectoryName(relativePath) ?? "");
                default:
                    return relativePath;
            }
        }

        private ChatWorkspaceRootSnapshot FindEffectiveChatWorkspaceRoot(string ownerKey, string sessionId, string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return null;
            ChatWorkspaceRootSnapshot best = null;
            foreach (ChatWorkspaceRootSnapshot root in GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId))
            {
                if (root == null || string.IsNullOrWhiteSpace(root.Path))
                    continue;
                try
                {
                    if (IsPathInsideRoot(fullPath, root.Path) &&
                        (best == null || root.Path.Length > best.Path.Length))
                        best = root;
                }
                catch
                {
                }
            }
            return best;
        }

        private bool EnsureChatWorkspacePathAccess(
            string ownerKey,
            string sessionId,
            string fullPath,
            bool requireWrite,
            out string error)
        {
            error = "";
            ChatWorkspaceRootSnapshot root = FindEffectiveChatWorkspaceRoot(ownerKey, sessionId, fullPath);
            if (root == null)
            {
                if ((!string.IsNullOrWhiteSpace(sessionId) &&
                     IsPathInsideRoot(fullPath, GetRemoteSessionCloneDirectory(sessionId))) ||
                    GetTemporaryFilesystemAccess(ownerKey).Any(path => IsPathInsideRoot(fullPath, path)))
                {
                    return true;
                }
                error = "path is outside the effective session workspace.";
                return false;
            }
            if (requireWrite &&
                string.Equals(root.AccessMode, WorkspaceAccessReadOnly, StringComparison.OrdinalIgnoreCase))
            {
                error = "workspace is read-only: " + root.DisplayName;
                return false;
            }
            if (IsChatWorkspacePathIgnored(ownerKey, sessionId, fullPath, out string ruleName))
            {
                error = "path is blocked by workspace ignore rule: " + ruleName;
                return false;
            }
            return true;
        }

        private bool TryAcceptResolvedChatWorkspacePath(
            string ownerKey,
            string sessionId,
            string candidate,
            out string fullPath,
            out string error)
        {
            fullPath = null;
            if (!EnsureChatWorkspacePathAccess(ownerKey, sessionId, candidate, requireWrite: false, out error))
                return false;
            fullPath = candidate;
            return true;
        }

        private List<string> GetEffectiveChatWorkspacePaths(string ownerKey, string sessionId, bool writableOnly)
        {
            List<string> paths = new List<string>();
            foreach (ChatWorkspaceRootSnapshot root in GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId))
            {
                if (root == null || string.IsNullOrWhiteSpace(root.Path) ||
                    (writableOnly && string.Equals(root.AccessMode, WorkspaceAccessReadOnly, StringComparison.OrdinalIgnoreCase)))
                    continue;
                AddAllowedRoot(paths, root.Path, requireExists: !root.IsSandbox);
            }
            return paths;
        }

        private string GetChatSessionDefaultWriteDirectory(string ownerKey, string sessionId)
        {
            ChatWorkspaceRootSnapshot primary = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId)
                .FirstOrDefault(root =>
                    string.Equals(root.Role, WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase));
            return primary?.Path ?? GetChatSessionFilesDirectory(sessionId);
        }

        private bool TryResolveChatWorkspaceLogicalPath(
            string requestedPath,
            string ownerKey,
            string sessionId,
            bool requireFile,
            bool mustExist,
            out string fullPath)
        {
            fullPath = "";
            string logical = NormalizeWorkspaceRelativePath(
                NormalizeLoosePathEscapes(requestedPath ?? "").Trim().Trim('"', '\'', ',', ' '));
            if (string.IsNullOrWhiteSpace(logical) || Path.IsPathRooted(logical))
                return false;
            int slash = logical.IndexOf('/');
            string prefix = slash >= 0 ? logical.Substring(0, slash).Trim() : logical.Trim();
            string remainder = slash >= 0 ? logical.Substring(slash + 1) : "";
            IReadOnlyList<ChatWorkspaceRootSnapshot> roots = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId);
            string primaryName = roots.FirstOrDefault(root =>
                string.Equals(root.Role, WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? "";
            ChatWorkspaceRootSnapshot matched = roots.FirstOrDefault(root =>
            {
                if (root == null)
                    return false;
                string hierarchical = root.IsInherited
                    ? "Global > " + root.DisplayName
                    : string.Equals(root.Role, WorkspaceRoleAttached, StringComparison.OrdinalIgnoreCase) &&
                      !string.IsNullOrWhiteSpace(primaryName)
                        ? primaryName + " > " + root.DisplayName
                        : root.DisplayName;
                return string.Equals(prefix, root.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(prefix, hierarchical, StringComparison.OrdinalIgnoreCase);
            });
            if (matched == null || string.IsNullOrWhiteSpace(matched.Path))
                return false;
            try
            {
                string candidate = string.IsNullOrWhiteSpace(remainder)
                    ? Path.GetFullPath(matched.Path)
                    : Path.GetFullPath(Path.Combine(
                        matched.Path,
                        remainder.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathInsideRoot(candidate, matched.Path))
                    return false;
                if (mustExist)
                {
                    bool exists = requireFile
                        ? File.Exists(candidate) || ChatSessionSandboxFileExists(sessionId, ownerKey, candidate)
                        : Directory.Exists(candidate) || ChatSessionManagedDirectoryExists(sessionId, ownerKey, candidate);
                    if (!exists)
                        return false;
                }
                fullPath = candidate;
                return true;
            }
            catch
            {
                fullPath = "";
                return false;
            }
        }

        private object BuildChatWorkspacePayload(string ownerKey, string sessionId, bool canEdit)
        {
            IReadOnlyList<ChatWorkspaceRootSnapshot> roots = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId);
            return new
            {
                ok = true,
                canEdit,
                ownerKey,
                sessionId,
                fallbackSandbox = roots.Any(root => root.IsSandbox),
                primary = roots.FirstOrDefault(root => root.Role == WorkspaceRolePrimary),
                sessionDirectories = roots.Where(root => !root.IsInherited).ToList(),
                globalDirectories = roots.Where(root => root.IsInherited).ToList(),
                effectiveRoots = roots,
                ignoreRules = GetChatWorkspaceIgnoreRulesDiagnostics(ownerKey, sessionId)
            };
        }

        private string HandleChatWorkspaceListRequest(NetworkConnection connection, HttpRequest request)
        {
            try
            {
                string ownerKey = GetChatSessionOwnerKey(connection, request);
                string sessionId = EnsureChatUiSessionId(GetQueryParameter(request, "sessionId"));
                return JsonSerializer.Serialize(BuildChatWorkspacePayload(
                    ownerKey,
                    sessionId,
                    IsDatabaseAdministrator(connection, request)));
            }
            catch (Exception ex)
            {
                LogMessage("[Chat Workspace] Load failed: " + ex.Message);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        private string HandleChatWorkspaceMutationRequest(NetworkConnection connection, HttpRequest request)
        {
            try
            {
                if (!IsDatabaseAdministrator(connection, request))
                {
                    return JsonSerializer.Serialize(new
                    {
                        ok = false,
                        canEdit = false,
                        error = "Session workspaces can only be changed by a Workstation administrator or configured server owner."
                    });
                }

                string ownerKey = GetChatSessionOwnerKey(connection, request);
                using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
                JsonElement root = document.RootElement;
                string sessionId = EnsureChatUiSessionId(
                    ExtractStringProperty(root, "sessionId") ?? GetQueryParameter(request, "sessionId"));
                string action = (ExtractStringProperty(root, "action") ?? "").Trim().ToLowerInvariant();
                object result = null;

                switch (action)
                {
                    case "save-root":
                    case "attach":
                    case "relink":
                        result = SaveChatWorkspaceRootDiagnostics(
                            ownerKey,
                            string.Equals(ExtractStringProperty(root, "role"), WorkspaceRoleGlobal, StringComparison.OrdinalIgnoreCase) ? "" : sessionId,
                            ExtractStringProperty(root, "rootId"),
                            ExtractStringProperty(root, "role"),
                            ExtractStringProperty(root, "displayName"),
                            ExtractStringProperty(root, "path"),
                            ExtractStringProperty(root, "accessMode"),
                            ExtractStringProperty(root, "parentId"));
                        RecordObservabilityEvent(
                            "security", "session workspace", action,
                            ExtractStringProperty(root, "path") ?? "",
                            ownerKey, "/api/chat-workspaces", 0L);
                        break;

                    case "remove-root":
                        result = RemoveChatWorkspaceRootDiagnostics(
                            ownerKey, sessionId, ExtractStringProperty(root, "rootId"));
                        RecordObservabilityEvent(
                            "security", "session workspace", "removed",
                            ExtractStringProperty(root, "rootId") ?? "",
                            ownerKey, "/api/chat-workspaces", 0L);
                        break;

                    case "move-root":
                        result = MoveChatWorkspaceRootDiagnostics(
                            ownerKey, sessionId,
                            ExtractStringProperty(root, "rootId"),
                            ExtractStringProperty(root, "destinationPath"));
                        RecordObservabilityEvent(
                            "security", "session workspace", "moved",
                            ExtractStringProperty(root, "destinationPath") ?? "",
                            ownerKey, "/api/chat-workspaces", 0L);
                        break;

                    case "save-ignore":
                        result = SaveChatWorkspaceIgnoreRuleDiagnostics(
                            ownerKey,
                            ParseJsonBoolean(root, "global", false) ? "" : sessionId,
                            ExtractStringProperty(root, "ruleId"),
                            ExtractStringProperty(root, "rootId"),
                            ExtractStringProperty(root, "name"),
                            ExtractStringProperty(root, "pattern"),
                            ExtractStringProperty(root, "target"),
                            ParseJsonBoolean(root, "caseSensitive", false),
                            ParseJsonBoolean(root, "enabled", true),
                            ExtractStringProperty(root, "builderMode"),
                            ExtractStringProperty(root, "builderStateJson"));
                        break;

                    case "remove-ignore":
                        result = RemoveChatWorkspaceIgnoreRuleDiagnostics(
                            ownerKey, sessionId, ExtractStringProperty(root, "ruleId"));
                        break;

                    case "test-ignore":
                        bool matched = TestChatWorkspaceIgnoreRegex(
                            ExtractStringProperty(root, "pattern"),
                            ExtractStringProperty(root, "target"),
                            ParseJsonBoolean(root, "caseSensitive", false),
                            ExtractStringProperty(root, "testPath"),
                            out string regexError);
                        if (!string.IsNullOrWhiteSpace(regexError))
                            throw new InvalidOperationException("Invalid ignore regex: " + regexError);
                        return JsonSerializer.Serialize(new { ok = true, matched });

                    case "merge-sandbox":
                        result = MergeChatSessionSandboxIntoWorkspace(
                            ownerKey, sessionId,
                            ParseJsonBoolean(root, "copy", true));
                        break;

                    case "fork":
                        result = ForkChatSessionWorkspace(
                            ownerKey,
                            sessionId,
                            ExtractStringProperty(root, "targetSessionId"),
                            ExtractStringProperty(root, "destinationPath"),
                            ExtractStringArray(root, "rootIds"));
                        break;

                    default:
                        throw new InvalidOperationException("Unknown workspace action.");
                }

                return JsonSerializer.Serialize(new
                {
                    ok = true,
                    result,
                    workspace = BuildChatWorkspacePayload(ownerKey, sessionId, true)
                });
            }
            catch (Exception ex)
            {
                LogMessage("[Chat Workspace] Update failed: " + ex.Message);
                return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
            }
        }

        private static bool ParseJsonBoolean(JsonElement root, string propertyName, bool fallback)
        {
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(propertyName, out JsonElement value))
                return fallback;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
                return parsed;
            return fallback;
        }

        private static List<string> ExtractStringArray(JsonElement root, string propertyName)
        {
            List<string> values = new List<string>();
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty(propertyName, out JsonElement array) ||
                array.ValueKind != JsonValueKind.Array)
                return values;
            foreach (JsonElement item in array.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    values.Add(item.GetString().Trim());
            }
            return values;
        }

        private object MergeChatSessionSandboxIntoWorkspace(
            string ownerKey,
            string sessionId,
            bool copy)
        {
            ChatWorkspaceRootSnapshot primary = GetChatWorkspaceRootsDiagnostics(ownerKey, sessionId)
                .FirstOrDefault(item =>
                    string.Equals(item.Role, WorkspaceRolePrimary, StringComparison.OrdinalIgnoreCase) &&
                    !item.IsSandbox);
            if (primary == null)
                throw new InvalidOperationException("Assign a primary workspace before merging sandbox files.");
            if (!copy)
                return new { copied = 0, skipped = 0, retained = true };

            string sandboxRoot = GetChatSessionFilesDirectory(sessionId);
            List<(string FullPath, SandboxFileEntry Entry)> sandboxFiles =
                EnumerateChatSessionSandboxHostFiles(sandboxRoot, ownerKey, sessionId, recursive: true).ToList();
            int copied = 0;
            List<string> skipped = new List<string>();
            foreach ((string fullPath, SandboxFileEntry entry) in sandboxFiles)
            {
                string relative = SafeRelativePath(sandboxRoot, fullPath);
                string target = Path.GetFullPath(Path.Combine(primary.Path, relative));
                if (!IsPathInsideRoot(target, primary.Path) ||
                    File.Exists(target) ||
                    IsChatWorkspacePathIgnored(ownerKey, sessionId, target, out _))
                {
                    skipped.Add(relative);
                    continue;
                }
                string directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                if (!TryReadChatSessionSandboxFileBytes(sessionId, ownerKey, fullPath, out byte[] bytes))
                {
                    skipped.Add(relative);
                    continue;
                }
                File.WriteAllBytes(target, bytes);
                copied++;
            }
            return new { copied, skipped = skipped.Count, skippedPaths = skipped.Take(100).ToArray(), retained = true };
        }

        public object MergeChatSessionSandboxIntoWorkspaceDiagnostics(string ownerKey, string sessionId)
        {
            return MergeChatSessionSandboxIntoWorkspace(ownerKey, sessionId, copy: true);
        }

        private object ForkChatSessionWorkspace(
            string ownerKey,
            string sourceSessionId,
            string targetSessionId,
            string destinationPath,
            List<string> selectedRootIds)
        {
            targetSessionId = EnsureChatUiSessionId(targetSessionId);
            if (string.Equals(sourceSessionId, targetSessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("Fork target session must differ from the source session.");
            string destinationRoot = Path.GetFullPath(destinationPath ?? "");
            if (Directory.Exists(destinationRoot) || File.Exists(destinationRoot))
                throw new IOException("Fork destination already exists: " + destinationRoot);
            string parent = Path.GetDirectoryName(destinationRoot);
            if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
                throw new DirectoryNotFoundException("Fork destination parent does not exist: " + parent);

            IReadOnlyList<ChatWorkspaceRootSnapshot> sourceRoots =
                GetChatWorkspaceRootsDiagnostics(ownerKey, sourceSessionId);
            HashSet<string> selected = new HashSet<string>(
                selectedRootIds ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);
            List<ChatWorkspaceRootSnapshot> sessionRoots = sourceRoots
                .Where(item => !item.IsSandbox && !item.IsInherited)
                .ToList();
            if (selected.Count == 0)
            {
                foreach (ChatWorkspaceRootSnapshot root in sessionRoots)
                    selected.Add(root.Id);
            }

            Directory.CreateDirectory(destinationRoot);
            List<string> createdPaths = new List<string> { destinationRoot };
            List<ChatWorkspaceRootSnapshot> saved = new List<ChatWorkspaceRootSnapshot>();
            try
            {
                foreach (ChatWorkspaceRootSnapshot source in sessionRoots)
                {
                    string targetPath = source.Path;
                    if (selected.Contains(source.Id))
                    {
                        string folderName = MakeWorkspaceForkFolderName(source.DisplayName);
                        targetPath = Path.Combine(destinationRoot, folderName);
                        if (Directory.Exists(targetPath) || File.Exists(targetPath))
                            throw new IOException("Fork target already exists: " + targetPath);
                        CopyWorkspaceDirectory(source.Path, targetPath);
                        createdPaths.Add(targetPath);
                    }
                    saved.Add(SaveChatWorkspaceRootDiagnostics(
                        ownerKey, targetSessionId, "", source.Role,
                        source.DisplayName, targetPath, source.AccessMode, source.ParentId));
                }
                foreach (ChatWorkspaceIgnoreRuleSnapshot rule in GetChatWorkspaceIgnoreRulesDiagnostics(ownerKey, sourceSessionId)
                    .Where(item => !string.IsNullOrWhiteSpace(item.SessionId)))
                {
                    string mappedRootId = "";
                    if (!string.IsNullOrWhiteSpace(rule.RootId))
                    {
                        ChatWorkspaceRootSnapshot sourceRoot = sessionRoots.FirstOrDefault(item =>
                            string.Equals(item.Id, rule.RootId, StringComparison.OrdinalIgnoreCase));
                        ChatWorkspaceRootSnapshot savedRoot = sourceRoot == null ? null : saved.FirstOrDefault(item =>
                            string.Equals(item.DisplayName, sourceRoot.DisplayName, StringComparison.OrdinalIgnoreCase));
                        mappedRootId = savedRoot?.Id ?? "";
                    }
                    SaveChatWorkspaceIgnoreRuleDiagnostics(
                        ownerKey, targetSessionId, "", mappedRootId,
                        rule.Name, rule.Pattern, rule.Target, rule.CaseSensitive,
                        rule.Enabled, rule.BuilderMode, rule.BuilderStateJson);
                }
            }
            catch
            {
                RemoveChatSessionWorkspaceConfiguration(ownerKey, targetSessionId);
                try
                {
                    if (Directory.Exists(destinationRoot))
                        Directory.Delete(destinationRoot, recursive: true);
                }
                catch
                {
                }
                throw;
            }
            return new { targetSessionId, destinationPath = destinationRoot, roots = saved };
        }

        private void CloneChatSessionWorkspaceConfiguration(string ownerKey, string sourceSessionId, string targetSessionId)
        {
            if (string.IsNullOrWhiteSpace(sourceSessionId) ||
                string.IsNullOrWhiteSpace(targetSessionId) ||
                string.Equals(sourceSessionId, targetSessionId, StringComparison.Ordinal))
                return;
            List<ChatWorkspaceRootSnapshot> sourceRoots = GetChatWorkspaceRootsDiagnostics(ownerKey, sourceSessionId)
                .Where(item => !item.IsSandbox && !item.IsInherited)
                .ToList();
            Dictionary<string, string> rootMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ChatWorkspaceRootSnapshot source in sourceRoots)
            {
                ChatWorkspaceRootSnapshot saved = SaveChatWorkspaceRootDiagnostics(
                    ownerKey, targetSessionId, "", source.Role, source.DisplayName,
                    source.Path, source.AccessMode, source.ParentId);
                rootMap[source.Id] = saved.Id;
            }
            foreach (ChatWorkspaceIgnoreRuleSnapshot rule in GetChatWorkspaceIgnoreRulesDiagnostics(ownerKey, sourceSessionId)
                .Where(item => !string.IsNullOrWhiteSpace(item.SessionId)))
            {
                string mappedRootId = "";
                if (!string.IsNullOrWhiteSpace(rule.RootId))
                    rootMap.TryGetValue(rule.RootId, out mappedRootId);
                SaveChatWorkspaceIgnoreRuleDiagnostics(
                    ownerKey, targetSessionId, "", mappedRootId,
                    rule.Name, rule.Pattern, rule.Target, rule.CaseSensitive,
                    rule.Enabled, rule.BuilderMode, rule.BuilderStateJson);
            }
        }

        private void CopyWorkspaceDirectory(string sourcePath, string destinationPath)
        {
            if (!Directory.Exists(sourcePath))
                throw new DirectoryNotFoundException("Workspace folder does not exist: " + sourcePath);
            Directory.CreateDirectory(destinationPath);
            foreach (string directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
            {
                string relative = SafeRelativePath(sourcePath, directory);
                Directory.CreateDirectory(Path.Combine(destinationPath, relative));
            }
            foreach (string file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
            {
                string relative = SafeRelativePath(sourcePath, file);
                string target = Path.Combine(destinationPath, relative);
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(parent))
                    Directory.CreateDirectory(parent);
                File.Copy(file, target, overwrite: false);
            }
        }

        private static string MakeWorkspaceForkFolderName(string displayName)
        {
            string name = string.IsNullOrWhiteSpace(displayName) ? "Workspace" : displayName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "Workspace" : name;
        }

        private void RemoveChatSessionWorkspaceConfiguration(string ownerKey, string sessionId)
        {
            lock (_chatSessionLock)
            {
                Table roots = GetChatWorkspaceRootsTable();
                HashSet<string> rootIds = new HashSet<string>(
                    roots.Rows
                        .Select(NormalizeChatWorkspaceRootRow)
                        .Where(row =>
                            ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                            string.Equals(GetRowValue(row, 2), sessionId, StringComparison.Ordinal))
                        .Select(row => GetRowValue(row, 0)),
                    StringComparer.OrdinalIgnoreCase);
                roots.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceRootRow(source);
                    return ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                           string.Equals(GetRowValue(row, 2), sessionId, StringComparison.Ordinal);
                });
                Table rules = GetChatWorkspaceIgnoreRulesTable();
                rules.Rows.RemoveAll(source =>
                {
                    object[] row = NormalizeChatWorkspaceIgnoreRuleRow(source);
                    return ChatOwnerKeysMatch(ownerKey, GetRowValue(row, 1)) &&
                           (string.Equals(GetRowValue(row, 2), sessionId, StringComparison.Ordinal) ||
                            rootIds.Contains(GetRowValue(row, 3)));
                });
                SaveChatSessionDataAndInvalidateCaches();
            }
        }
    }
}
