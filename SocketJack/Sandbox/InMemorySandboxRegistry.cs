using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SocketJack.Sandbox {

    public sealed class InMemorySandboxRegistry : ISandboxRegistry {
        private readonly object _sync = new object();
        private readonly SandboxOptions _options;
        private readonly ISandboxAuditLog _audit;
        private readonly Func<SandboxSessionState> _stateGetter;
        private readonly string _sessionId;
        private readonly Dictionary<string, Dictionary<string, SandboxRegistryValue>> _keys;
        private long _memoryBytes;
        private long _lastLoggedMemoryBytes = long.MinValue;

        public InMemorySandboxRegistry(SandboxOptions options, ISandboxAuditLog audit, Func<SandboxSessionState> stateGetter, string sessionId) {
            _options = options ?? new SandboxOptions();
            _audit = audit ?? new InMemorySandboxAuditLog(_options.Audit);
            _stateGetter = stateGetter ?? (() => SandboxSessionState.Active);
            _sessionId = sessionId ?? "";
            StringComparer comparer = _options.Registry.WindowsCaseInsensitiveKeys ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _keys = new Dictionary<string, Dictionary<string, SandboxRegistryValue>>(comparer);
        }

        public long MemoryBytes {
            get {
                lock (_sync) {
                    return _memoryBytes;
                }
            }
        }

        public SandboxRegistryValue SetValue(string key, string name, object value, SandboxRegistryValueKind kind = SandboxRegistryValueKind.String) {
            EnsureWritable("set", key);
            string normalizedKey = NormalizeKey(key);
            string valueName = NormalizeName(name);
            object safeValue = NormalizeValue(value, kind);
            long size = EstimateValueBytes(safeValue, kind);
            EnforceRegistryQuota(normalizedKey, valueName, size);

            SandboxRegistryValue result;
            long previousSize = 0;
            long delta;
            lock (_sync) {
                if (!_keys.TryGetValue(normalizedKey, out Dictionary<string, SandboxRegistryValue> values)) {
                    values = new Dictionary<string, SandboxRegistryValue>(_options.Registry.WindowsCaseInsensitiveKeys ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                    _keys[normalizedKey] = values;
                }

                if (values.TryGetValue(valueName, out SandboxRegistryValue existing))
                    previousSize = existing.SizeBytes;

                result = new SandboxRegistryValue {
                    Key = normalizedKey,
                    Name = valueName,
                    Kind = kind,
                    Value = SandboxValueCloner.CloneValue(safeValue),
                    SizeBytes = size,
                    CreatedUtc = values.TryGetValue(valueName, out existing) ? existing.CreatedUtc : Now(),
                    UpdatedUtc = Now()
                };
                values[valueName] = result.Clone();
                delta = size - previousSize;
                _memoryBytes += delta;
            }

            RecordRegistry("set", normalizedKey + "\\" + valueName, true, true, "set virtual registry value", size, MemoryBytes);
            RecordMemoryIfNeeded("registry_set", normalizedKey + "\\" + valueName, delta);
            return result;
        }

        public bool TryGetValue(string key, string name, out SandboxRegistryValue value) {
            EnsureReadable("get", key);
            string normalizedKey = NormalizeKey(key);
            string valueName = NormalizeName(name);

            lock (_sync) {
                if (_keys.TryGetValue(normalizedKey, out Dictionary<string, SandboxRegistryValue> values) &&
                    values.TryGetValue(valueName, out SandboxRegistryValue existing)) {
                    value = existing.Clone();
                    RecordRegistry("get", normalizedKey + "\\" + valueName, true, false, "read virtual registry value", existing.SizeBytes, _memoryBytes);
                    return true;
                }
            }

            value = null;
            RecordRegistry("get", normalizedKey + "\\" + valueName, true, false, "virtual registry value missing", 0, MemoryBytes);
            return false;
        }

        public bool DeleteValue(string key, string name) {
            EnsureWritable("delete_value", key);
            string normalizedKey = NormalizeKey(key);
            string valueName = NormalizeName(name);
            long delta = 0;
            bool deleted = false;

            lock (_sync) {
                if (_keys.TryGetValue(normalizedKey, out Dictionary<string, SandboxRegistryValue> values) &&
                    values.TryGetValue(valueName, out SandboxRegistryValue existing)) {
                    delta = -existing.SizeBytes;
                    values.Remove(valueName);
                    _memoryBytes += delta;
                    deleted = true;
                    if (values.Count == 0)
                        _keys.Remove(normalizedKey);
                }
            }

            RecordRegistry("delete_value", normalizedKey + "\\" + valueName, deleted, true, deleted ? "deleted virtual registry value" : "virtual registry value missing", 0, MemoryBytes);
            if (deleted)
                RecordMemoryIfNeeded("registry_delete_value", normalizedKey + "\\" + valueName, delta);
            return deleted;
        }

        public bool DeleteKey(string key, bool recursive = false) {
            EnsureWritable("delete_key", key);
            string normalizedKey = NormalizeKey(key);
            List<string> keysToDelete;
            long delta = 0;

            lock (_sync) {
                if (recursive) {
                    keysToDelete = _keys.Keys
                        .Where(item => item.Equals(normalizedKey, KeyComparison) || item.StartsWith(normalizedKey + "\\", KeyComparison))
                        .ToList();
                } else {
                    keysToDelete = _keys.ContainsKey(normalizedKey) ? new List<string> { normalizedKey } : new List<string>();
                    bool hasChildren = _keys.Keys.Any(item => item.StartsWith(normalizedKey + "\\", KeyComparison));
                    if (hasChildren)
                        throw new InvalidOperationException("Registry key has child keys. Set recursive to true to delete it.");
                }

                foreach (string item in keysToDelete) {
                    delta -= _keys[item].Values.Sum(value => value.SizeBytes);
                    _keys.Remove(item);
                }
                _memoryBytes += delta;
            }

            bool deleted = keysToDelete.Count > 0;
            RecordRegistry("delete_key", normalizedKey, deleted, true, deleted ? "deleted virtual registry key" : "virtual registry key missing", keysToDelete.Count, MemoryBytes);
            if (deleted)
                RecordMemoryIfNeeded("registry_delete_key", normalizedKey, delta);
            return deleted;
        }

        public IReadOnlyList<string> EnumerateKeys(string key = "") {
            EnsureReadable("enumerate", key);
            string normalizedKey = NormalizeKey(key);
            List<string> keys;
            lock (_sync) {
                if (string.IsNullOrWhiteSpace(normalizedKey)) {
                    keys = _keys.Keys.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
                } else {
                    keys = _keys.Keys
                        .Where(item => item.Equals(normalizedKey, KeyComparison) || item.StartsWith(normalizedKey + "\\", KeyComparison))
                        .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
            RecordRegistry("enumerate", normalizedKey, true, false, "enumerated " + keys.Count.ToString(CultureInfo.InvariantCulture) + " virtual registry keys", keys.Count, MemoryBytes);
            return keys;
        }

        public SandboxRegistrySnapshot Snapshot() {
            lock (_sync) {
                var values = _keys.Values
                    .SelectMany(item => item.Values)
                    .Select(item => item.Clone())
                    .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return new SandboxRegistrySnapshot {
                    SessionId = _sessionId,
                    GeneratedUtc = Now(),
                    KeyCount = _keys.Count,
                    ValueCount = values.Count,
                    MemoryBytes = _memoryBytes,
                    Values = values
                };
            }
        }

        private void EnsureReadable(string operation, string target) {
            if (_options.Registry.Mode == SandboxRegistryMode.DenyAll) {
                RecordRegistry(operation, target, false, false, "registry sandbox is deny-all", 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Registry access is denied by this sandbox.");
            }
        }

        private void EnsureWritable(string operation, string target) {
            SandboxSessionState state = _stateGetter();
            if (state != SandboxSessionState.Active && state != SandboxSessionState.Created) {
                RecordRegistry(operation, target, false, true, "sandbox session is not writable while " + state, 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Sandbox session is not writable while " + state + ".");
            }
            if (_options.Registry.Mode == SandboxRegistryMode.DenyAll ||
                _options.Registry.Mode == SandboxRegistryMode.ReadOnly ||
                _options.Registry.WritePolicy == SandboxRegistryWritePolicy.Deny) {
                RecordRegistry(operation, target, false, true, "registry writes are denied by sandbox policy", 0, MemoryBytes);
                throw new SandboxPolicyException(operation, target, "Registry writes are denied by this sandbox.");
            }
        }

        private void EnforceRegistryQuota(string key, string valueName, long valueBytes) {
            SandboxLimits limits = _options.Limits ?? new SandboxLimits();
            if (limits.MaxRegistryValueBytes > 0 && valueBytes > limits.MaxRegistryValueBytes) {
                RecordQuotaFailure("registry_value_bytes", key + "\\" + valueName, limits.MaxRegistryValueBytes, valueBytes);
                throw new SandboxQuotaException("MaxRegistryValueBytes", limits.MaxRegistryValueBytes, valueBytes, "Sandbox registry value exceeds the per-value limit.");
            }

            lock (_sync) {
                bool keyExists = _keys.ContainsKey(key);
                if (!keyExists && limits.MaxRegistryKeys > 0 && _keys.Count + 1 > limits.MaxRegistryKeys) {
                    RecordQuotaFailure("registry_key_count", key, limits.MaxRegistryKeys, _keys.Count + 1);
                    throw new SandboxQuotaException("MaxRegistryKeys", limits.MaxRegistryKeys, _keys.Count + 1, "Sandbox registry key count limit has been reached.");
                }

                long previousBytes = 0;
                if (_keys.TryGetValue(key, out Dictionary<string, SandboxRegistryValue> values) &&
                    values.TryGetValue(valueName, out SandboxRegistryValue existing))
                    previousBytes = existing.SizeBytes;
                long requestedMemory = _memoryBytes - previousBytes + valueBytes;
                if (limits.MaxMemoryBytes > 0 && requestedMemory > limits.MaxMemoryBytes) {
                    RecordQuotaFailure("memory_bytes", key + "\\" + valueName, limits.MaxMemoryBytes, requestedMemory);
                    throw new SandboxQuotaException("MaxMemoryBytes", limits.MaxMemoryBytes, requestedMemory, "Sandbox memory limit has been reached.");
                }
            }
        }

        private void RecordQuotaFailure(string quota, string target, long limit, long requested) {
            RecordRegistry("quota", target, false, true, quota + " limit exceeded", requested, MemoryBytes);
            RecordMemory("quota_" + quota, target, requested, "limit=" + limit.ToString(CultureInfo.InvariantCulture));
        }

        private void RecordRegistry(string operation, string target, bool allowed, bool mutation, string reason, long length, long memoryBytes) {
            _audit.Record(new SandboxAuditEvent {
                SessionId = _sessionId,
                Category = "registry",
                Operation = operation ?? "",
                Target = target ?? "",
                Allowed = allowed,
                Mutation = mutation,
                Reason = reason ?? "",
                Length = length,
                MemoryBytes = memoryBytes
            });
        }

        private void RecordMemoryIfNeeded(string operation, string target, long deltaBytes) {
            if (!_options.Audit.LogMemory)
                return;

            long current = MemoryBytes;
            long minimumDelta = Math.Max(0, _options.Audit.MemoryLogMinimumDeltaBytes);
            bool shouldLog = _lastLoggedMemoryBytes == long.MinValue ||
                             minimumDelta == 0 ||
                             Math.Abs(current - _lastLoggedMemoryBytes) >= minimumDelta ||
                             Math.Abs(deltaBytes) >= minimumDelta;
            if (!shouldLog)
                return;

            _lastLoggedMemoryBytes = current;
            RecordMemory(operation, target, current, "delta=" + deltaBytes.ToString(CultureInfo.InvariantCulture));
        }

        private void RecordMemory(string operation, string target, long memoryBytes, string detail) {
            _audit.Record(new SandboxAuditEvent {
                SessionId = _sessionId,
                Category = "memory",
                Operation = operation ?? "",
                Target = target ?? "",
                Allowed = true,
                Mutation = false,
                Reason = "sandbox memory telemetry",
                Detail = detail ?? "",
                MemoryBytes = memoryBytes
            });
        }

        private string NormalizeKey(string key) {
            string value = (key ?? "").Trim().Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(value))
                return "HKCU\\Software\\SocketJack\\Sandbox";
            while (value.Contains("\\\\", StringComparison.Ordinal))
                value = value.Replace("\\\\", "\\");
            return value.Trim('\\');
        }

        private static string NormalizeName(string name) {
            return string.IsNullOrWhiteSpace(name) ? "(Default)" : name.Trim();
        }

        private static object NormalizeValue(object value, SandboxRegistryValueKind kind) {
            if (value == null)
                return "";
            if (kind == SandboxRegistryValueKind.Binary && value is byte[] bytes)
                return (byte[])bytes.Clone();
            if (kind == SandboxRegistryValueKind.MultiString && value is string[] strings)
                return (string[])strings.Clone();
            if (kind == SandboxRegistryValueKind.DWord)
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            if (kind == SandboxRegistryValueKind.QWord)
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            return value.ToString() ?? "";
        }

        private static long EstimateValueBytes(object value, SandboxRegistryValueKind kind) {
            if (value == null)
                return 0;
            if (value is byte[] bytes)
                return bytes.LongLength;
            if (value is string[] strings)
                return strings.Sum(item => Encoding.UTF8.GetByteCount(item ?? ""));
            if (kind == SandboxRegistryValueKind.DWord)
                return 4;
            if (kind == SandboxRegistryValueKind.QWord)
                return 8;
            return Encoding.UTF8.GetByteCount(value.ToString() ?? "");
        }

        private StringComparison KeyComparison => _options.Registry.WindowsCaseInsensitiveKeys ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        private static string Now() {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }
}
