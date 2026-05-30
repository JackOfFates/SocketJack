using System;
using System.Collections.Generic;
using System.Globalization;

namespace SocketJack.Sandbox {

    public sealed class InMemorySandboxAuditLog : ISandboxAuditLog {
        private readonly object _sync = new object();
        private readonly SandboxAuditOptions _options;
        private readonly List<SandboxAuditEvent> _events = new List<SandboxAuditEvent>();

        public InMemorySandboxAuditLog(SandboxAuditOptions options) {
            _options = options ?? new SandboxAuditOptions();
        }

        public event EventHandler<SandboxAuditEventArgs> EventRecorded;

        public IReadOnlyList<SandboxAuditEvent> Events {
            get {
                lock (_sync) {
                    return _events.ConvertAll(item => item.Clone());
                }
            }
        }

        public void Record(SandboxAuditEvent auditEvent) {
            if (auditEvent == null || !ShouldRecord(auditEvent))
                return;

            SandboxAuditEvent snapshot = auditEvent.Clone();
            if (string.IsNullOrWhiteSpace(snapshot.Id))
                snapshot.Id = "audit_" + Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(snapshot.CreatedUtc))
                snapshot.CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            snapshot.Detail = TrimDetail(snapshot.Detail);

            lock (_sync) {
                _events.Add(snapshot);
                int max = Math.Max(1, _options.MaxEvents);
                if (_events.Count > max)
                    _events.RemoveRange(0, _events.Count - max);
            }

            EventRecorded?.Invoke(this, new SandboxAuditEventArgs(snapshot.Clone()));
        }

        private bool ShouldRecord(SandboxAuditEvent auditEvent) {
            if (_options.DecisionLogging == SandboxDecisionLogging.Off)
                return false;

            string category = (auditEvent.Category ?? "").Trim();
            if (category.Equals("filesystem", StringComparison.OrdinalIgnoreCase) && !_options.LogFileSystem)
                return false;
            if (category.Equals("registry", StringComparison.OrdinalIgnoreCase) && !_options.LogRegistry)
                return false;
            if (category.Equals("memory", StringComparison.OrdinalIgnoreCase) && !_options.LogMemory)
                return false;

            switch (_options.DecisionLogging) {
                case SandboxDecisionLogging.DeniedOnly:
                    return !auditEvent.Allowed;
                case SandboxDecisionLogging.MutationsOnly:
                    return auditEvent.Mutation;
                case SandboxDecisionLogging.DeniedAndMutations:
                    return !auditEvent.Allowed || auditEvent.Mutation;
                default:
                    return true;
            }
        }

        private string TrimDetail(string detail) {
            if (string.IsNullOrEmpty(detail))
                return "";
            int max = Math.Max(0, _options.MaxDetailLength);
            if (max == 0 || detail.Length <= max)
                return detail;
            return detail.Substring(0, max);
        }
    }
}
