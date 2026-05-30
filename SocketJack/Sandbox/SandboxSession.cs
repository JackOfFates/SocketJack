using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Sandbox {

    public sealed class SandboxSession : ISandboxSession {
        private readonly object _sync = new object();
        private readonly string _createdUtc;
        private SandboxSessionState _state = SandboxSessionState.Created;

        private SandboxSession(SandboxOptions options) {
            Options = options ?? SandboxOptions.CreateMemoryOverlay();
            Id = string.IsNullOrWhiteSpace(Options.SessionId) ? "sandbox_" + Guid.NewGuid().ToString("N") : Options.SessionId.Trim();
            Options.SessionId = Id;
            _createdUtc = Now();
            Audit = new InMemorySandboxAuditLog(Options.Audit);
            FileSystem = new InMemorySandboxFileSystem(Options, Audit, () => State, Id);
            Registry = new InMemorySandboxRegistry(Options, Audit, () => State, Id);
            _state = SandboxSessionState.Active;
            Audit.Record(new SandboxAuditEvent {
                SessionId = Id,
                Category = "session",
                Operation = "create",
                Target = Options.ProfileName,
                Allowed = true,
                Mutation = true,
                Reason = "sandbox session created"
            });
        }

        public string Id { get; }
        public SandboxOptions Options { get; }
        public SandboxSessionState State {
            get {
                lock (_sync) {
                    return _state;
                }
            }
        }
        public ISandboxFileSystem FileSystem { get; }
        public ISandboxRegistry Registry { get; }
        public ISandboxAuditLog Audit { get; }

        public static SandboxSession Create(SandboxOptions options = null) {
            return new SandboxSession(options ?? SandboxOptions.CreateMemoryOverlay());
        }

        public Task<SandboxSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) {
            cancellationToken.ThrowIfCancellationRequested();
            SandboxSnapshot snapshot = new SandboxSnapshot {
                SessionId = Id,
                ProfileName = Options.ProfileName ?? "",
                State = State.ToString(),
                CreatedUtc = _createdUtc,
                GeneratedUtc = Now(),
                Files = FileSystem.GetManifest(),
                Registry = Registry.Snapshot()
            };
            foreach (SandboxAuditEvent auditEvent in Audit.Events)
                snapshot.AuditEvents.Add(auditEvent.Clone());

            Audit.Record(new SandboxAuditEvent {
                SessionId = Id,
                Category = "session",
                Operation = "snapshot",
                Target = Id,
                Allowed = true,
                Mutation = false,
                Reason = "sandbox snapshot generated",
                MemoryBytes = FileSystem.MemoryBytes + Registry.MemoryBytes
            });
            return Task.FromResult(snapshot);
        }

        public void Freeze() {
            SetState(SandboxSessionState.Frozen, "freeze");
        }

        public void Commit() {
            SetState(SandboxSessionState.Committed, "commit");
        }

        public void Discard() {
            SetState(SandboxSessionState.Discarded, "discard");
        }

        public ValueTask DisposeAsync() {
            lock (_sync) {
                if (_state != SandboxSessionState.Disposed)
                    _state = SandboxSessionState.Disposed;
            }
            Audit.Record(new SandboxAuditEvent {
                SessionId = Id,
                Category = "session",
                Operation = "dispose",
                Target = Id,
                Allowed = true,
                Mutation = true,
                Reason = "sandbox session disposed"
            });
            return default;
        }

        private void SetState(SandboxSessionState state, string operation) {
            lock (_sync) {
                if (_state == SandboxSessionState.Disposed)
                    throw new ObjectDisposedException(nameof(SandboxSession));
                _state = state;
            }
            Audit.Record(new SandboxAuditEvent {
                SessionId = Id,
                Category = "session",
                Operation = operation ?? "",
                Target = Id,
                Allowed = true,
                Mutation = true,
                Reason = "sandbox session state changed to " + state
            });
        }

        private static string Now() {
            return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }
}
