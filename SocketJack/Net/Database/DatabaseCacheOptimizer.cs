using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SocketJack.Net.Database {

    internal class DatabaseCacheOptimizer {

        private const int MetadataVersion = 1;
        private const char KeySeparator = '\u001f';
        private const string NullValueKey = "\u0000NULL";

        private readonly object _lock = new object();
        private readonly Dictionary<string, CachedRows> _rowCache = new Dictionary<string, CachedRows>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CacheMetadataEntry> _metadata = new Dictionary<string, CacheMetadataEntry>(StringComparer.OrdinalIgnoreCase);

        private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public bool TryGetRowIndexes(
            string databaseName,
            string tableName,
            Table table,
            string whereClause,
            int maxCachedKeys,
            out List<int> rowIndexes) {

            rowIndexes = null;

            if (table == null || !TryParseEqualityWhere(whereClause, out var columnName, out var value))
                return false;

            int columnIndex = table.Columns.FindIndex(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (columnIndex < 0)
                return false;

            databaseName = databaseName ?? "";
            tableName = tableName ?? table.Name ?? "";
            value = NormalizeValue(value);
            string cacheKey = MakeKey(databaseName, tableName, columnName, value);

            lock (_lock) {
                if (_rowCache.TryGetValue(cacheKey, out var cached) && cached.RowCountAtBuild == table.Rows.Count) {
                    Touch(cacheKey, databaseName, tableName, columnName, value, cached.Rows.Count);
                    rowIndexes = new List<int>(cached.Rows);
                    return true;
                }

                var rows = BuildRowIndex(table, columnIndex, value);
                _rowCache[cacheKey] = new CachedRows {
                    Rows = rows,
                    RowCountAtBuild = table.Rows.Count
                };
                Touch(cacheKey, databaseName, tableName, columnName, value, rows.Count);
                Trim(maxCachedKeys);

                rowIndexes = new List<int>(rows);
                return true;
            }
        }

        public void InvalidateAll() {
            lock (_lock) {
                _rowCache.Clear();
            }
        }

        public void InvalidateTable(string databaseName, string tableName) {
            databaseName = databaseName ?? "";
            tableName = tableName ?? "";

            lock (_lock) {
                var keys = _rowCache.Keys
                    .Where(k => KeyMatchesTable(k, databaseName, tableName))
                    .ToList();

                foreach (var key in keys)
                    _rowCache.Remove(key);
            }
        }

        public void LoadMetadata(string metadataPath) {
            if (string.IsNullOrWhiteSpace(metadataPath) || !File.Exists(metadataPath))
                return;

            try {
                string json = File.ReadAllText(metadataPath, Encoding.UTF8);
                var snapshot = JsonSerializer.Deserialize<CacheMetadataSnapshot>(json, _jsonOptions);
                if (snapshot == null || snapshot.Entries == null)
                    return;

                lock (_lock) {
                    _metadata.Clear();
                    foreach (var entry in snapshot.Entries) {
                        if (entry == null || string.IsNullOrWhiteSpace(entry.ColumnName))
                            continue;

                        string key = MakeKey(entry.DatabaseName, entry.TableName, entry.ColumnName, NormalizeValue(entry.Value));
                        _metadata[key] = entry;
                    }
                    _rowCache.Clear();
                }
            } catch {
                // Cache metadata is an optimization only. Corrupt or old files
                // should never block database startup.
            }
        }

        public void SaveMetadata(string metadataPath, int maxCachedKeys) {
            if (string.IsNullOrWhiteSpace(metadataPath))
                return;

            try {
                CacheMetadataSnapshot snapshot;
                lock (_lock) {
                    Trim(maxCachedKeys);
                    snapshot = new CacheMetadataSnapshot {
                        Version = MetadataVersion,
                        Entries = _metadata.Values
                            .OrderByDescending(e => e.Hits)
                            .ThenByDescending(e => e.LastAccessUtc)
                            .Take(Math.Max(1, maxCachedKeys))
                            .ToList()
                    };
                }

                string dir = Path.GetDirectoryName(Path.GetFullPath(metadataPath));
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                File.WriteAllText(metadataPath, json, Encoding.UTF8);
            } catch {
                // The primary data file remains authoritative.
            }
        }

        public void Warm(ConcurrentDictionary<string, Database> databases, int maxCachedKeys) {
            if (databases == null)
                return;

            List<CacheMetadataEntry> entries;
            lock (_lock) {
                _rowCache.Clear();
                entries = _metadata.Values
                    .OrderByDescending(e => e.Hits)
                    .ThenByDescending(e => e.LastAccessUtc)
                    .Take(Math.Max(1, maxCachedKeys))
                    .ToList();
            }

            foreach (var entry in entries) {
                if (entry == null)
                    continue;

                if (!databases.TryGetValue(entry.DatabaseName ?? "", out var database))
                    continue;

                if (!TryFindTable(database, entry.TableName, out var table))
                    continue;

                int columnIndex = table.Columns.FindIndex(c => c.Name.Equals(entry.ColumnName, StringComparison.OrdinalIgnoreCase));
                if (columnIndex < 0)
                    continue;

                string normalizedValue = NormalizeValue(entry.Value);
                string key = MakeKey(entry.DatabaseName, table.Name, entry.ColumnName, normalizedValue);
                var rows = BuildRowIndex(table, columnIndex, normalizedValue);

                lock (_lock) {
                    _rowCache[key] = new CachedRows {
                        Rows = rows,
                        RowCountAtBuild = table.Rows.Count
                    };
                }
            }
        }

        private static List<int> BuildRowIndex(Table table, int columnIndex, string normalizedValue) {
            var rows = new List<int>();
            for (int i = 0; i < table.Rows.Count; i++) {
                var row = table.Rows[i];
                var cell = columnIndex < row.Length ? row[columnIndex] : null;
                if (ValuesEqual(cell, normalizedValue))
                    rows.Add(i);
            }
            return rows;
        }

        private void Touch(string cacheKey, string databaseName, string tableName, string columnName, string value, int cachedRowCount) {
            if (!_metadata.TryGetValue(cacheKey, out var entry)) {
                entry = new CacheMetadataEntry {
                    DatabaseName = databaseName,
                    TableName = tableName,
                    ColumnName = columnName,
                    Value = value,
                    Hits = 0
                };
                _metadata[cacheKey] = entry;
            }

            entry.Hits++;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            entry.CachedRowCount = cachedRowCount;
        }

        private void Trim(int maxCachedKeys) {
            maxCachedKeys = Math.Max(1, maxCachedKeys);
            if (_metadata.Count <= maxCachedKeys)
                return;

            var removeKeys = _metadata
                .OrderBy(kvp => kvp.Value.Hits)
                .ThenBy(kvp => kvp.Value.LastAccessUtc)
                .Take(_metadata.Count - maxCachedKeys)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in removeKeys) {
                _metadata.Remove(key);
                _rowCache.Remove(key);
            }
        }

        private static bool TryParseEqualityWhere(string whereClause, out string columnName, out string value) {
            columnName = null;
            value = null;

            if (string.IsNullOrWhiteSpace(whereClause))
                return false;

            string cond = whereClause.Trim().TrimEnd(';');
            while (cond.StartsWith("(", StringComparison.Ordinal) && cond.EndsWith(")", StringComparison.Ordinal))
                cond = cond.Substring(1, cond.Length - 2).Trim();

            if (ContainsTopLevelLogicalOperator(cond))
                return false;

            int eqIdx = FindSingleEquals(cond);
            if (eqIdx < 0)
                return false;

            columnName = StripBrackets(cond.Substring(0, eqIdx).Trim());
            value = UnquoteString(cond.Substring(eqIdx + 1).Trim());

            return IsSimpleIdentifier(columnName);
        }

        private static bool ContainsTopLevelLogicalOperator(string clause) {
            bool inString = false;
            int depth = 0;

            for (int i = 0; i < clause.Length; i++) {
                char ch = clause[i];
                if (ch == '\'') {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (depth == 0) {
                    if (IsKeywordAt(clause, i, "AND") || IsKeywordAt(clause, i, "OR"))
                        return true;
                }
            }

            return false;
        }

        private static int FindSingleEquals(string cond) {
            bool inString = false;
            for (int i = 0; i < cond.Length; i++) {
                char ch = cond[i];
                if (ch == '\'') {
                    inString = !inString;
                    continue;
                }

                if (inString || ch != '=')
                    continue;

                char prev = i > 0 ? cond[i - 1] : '\0';
                char next = i + 1 < cond.Length ? cond[i + 1] : '\0';
                if (prev == '!' || prev == '<' || prev == '>' || next == '=')
                    return -1;

                return i;
            }

            return -1;
        }

        private static bool IsKeywordAt(string text, int index, string keyword) {
            if (index + keyword.Length > text.Length)
                return false;

            if (!string.Equals(text.Substring(index, keyword.Length), keyword, StringComparison.OrdinalIgnoreCase))
                return false;

            bool leftOk = index == 0 || !char.IsLetterOrDigit(text[index - 1]) && text[index - 1] != '_';
            int after = index + keyword.Length;
            bool rightOk = after >= text.Length || !char.IsLetterOrDigit(text[after]) && text[after] != '_';
            return leftOk && rightOk;
        }

        private static bool IsSimpleIdentifier(string value) {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            for (int i = 0; i < value.Length; i++) {
                char ch = value[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_' || ch == '.'))
                    return false;
            }
            return true;
        }

        private static bool ValuesEqual(object cell, string normalizedValue) {
            if (cell == null)
                return normalizedValue == NullValueKey;

            return string.Equals(NormalizeValue(cell.ToString()), normalizedValue, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeValue(string value) {
            if (value == null || value.Equals("NULL", StringComparison.OrdinalIgnoreCase))
                return NullValueKey;

            return value;
        }

        private static string MakeKey(string databaseName, string tableName, string columnName, string value) {
            return string.Join(KeySeparator.ToString(), new[] {
                databaseName ?? "",
                tableName ?? "",
                columnName ?? "",
                value ?? NullValueKey
            });
        }

        private static bool KeyMatchesTable(string key, string databaseName, string tableName) {
            var parts = key.Split(KeySeparator);
            return parts.Length >= 2
                && parts[0].Equals(databaseName, StringComparison.OrdinalIgnoreCase)
                && parts[1].Equals(tableName, StringComparison.OrdinalIgnoreCase);
        }

        private static string StripBrackets(string name) {
            if (string.IsNullOrEmpty(name)) return name;
            name = name.Trim().TrimEnd(';');
            if (name.StartsWith("[", StringComparison.Ordinal) && name.EndsWith("]", StringComparison.Ordinal))
                return name.Substring(1, name.Length - 2);
            if (name.StartsWith("\"", StringComparison.Ordinal) && name.EndsWith("\"", StringComparison.Ordinal))
                return name.Substring(1, name.Length - 2);
            return name;
        }

        private static string UnquoteString(string value) {
            if (string.IsNullOrEmpty(value))
                return value;

            value = value.TrimEnd(';').Trim();
            if (value.StartsWith("N'", StringComparison.OrdinalIgnoreCase) && value.EndsWith("'", StringComparison.Ordinal))
                value = value.Substring(2, value.Length - 3);
            else if (value.StartsWith("'", StringComparison.Ordinal) && value.EndsWith("'", StringComparison.Ordinal))
                value = value.Substring(1, value.Length - 2);

            return value.Replace("''", "'");
        }

        private static bool TryFindTable(Database database, string tableName, out Table table) {
            table = null;
            if (database == null || string.IsNullOrEmpty(tableName))
                return false;

            if (database.Tables.TryGetValue(tableName, out table))
                return true;

            if (!tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)
                && database.Tables.TryGetValue("dbo." + tableName, out table))
                return true;

            if (tableName.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)
                && database.Tables.TryGetValue(tableName.Substring(4), out table))
                return true;

            return false;
        }

        private class CachedRows {
            public List<int> Rows { get; set; }
            public int RowCountAtBuild { get; set; }
        }

        private class CacheMetadataSnapshot {
            public int Version { get; set; }
            public List<CacheMetadataEntry> Entries { get; set; } = new List<CacheMetadataEntry>();
        }

        private class CacheMetadataEntry {
            public string DatabaseName { get; set; }
            public string TableName { get; set; }
            public string ColumnName { get; set; }
            public string Value { get; set; }
            public long Hits { get; set; }
            public DateTimeOffset LastAccessUtc { get; set; }
            public int CachedRowCount { get; set; }
        }
    }
}
