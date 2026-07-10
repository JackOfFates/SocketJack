using SocketJack.Net.Database;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.SocketChat {
    public interface ISocketChatSyncProvider {
        Task<IReadOnlyList<SocketChatEnvelope>> PullAsync(string lobbyId, string cursor, CancellationToken cancellationToken);
        Task<string> PushAsync(string lobbyId, IReadOnlyList<SocketChatEnvelope> records, string revision, CancellationToken cancellationToken);
    }

    public interface ISocketChatRelayHost {
        SocketChatTransportKind Transport { get; }
        ValueTask SendAsync(string recipientFingerprint, ReadOnlyMemory<byte> encryptedFrame, SocketChatRelayPriority priority, CancellationToken cancellationToken);
    }

    public enum SocketChatRelayPriority { ControlAndText = 0, Voice = 1, File = 2 }

    public static class SocketChatHostElection {
        public static SocketChatHostCandidate SelectBest(IEnumerable<SocketChatHostCandidate> candidates, DateTimeOffset now, TimeSpan freshness) =>
            candidates.Where(c => now - c.ObservedUtc <= freshness)
                .OrderByDescending(c => c.PubliclyReachable)
                .ThenByDescending(c => c.TunnelReachable)
                .ThenByDescending(c => c.UpstreamMbps)
                .ThenBy(c => c.LatencyMs)
                .ThenBy(c => c.PacketLossPercent)
                .ThenByDescending(c => c.Uptime)
                .ThenBy(c => c.Fingerprint, StringComparer.Ordinal)
                .FirstOrDefault();
    }

    public sealed class SocketChatManagedDatabase {
        private readonly DataServer _server;
        private readonly object _gate = new object();
        private const string DatabaseName = "SocketChat";
        private const string RecordsTable = "Records";

        public SocketChatManagedDatabase(string path) {
            _server = new DataServer(0, "SocketChat Managed Database", false) { DataPath = path, AutoSave = false, EnableCacheOptimizing = true };
            _server.Load();
            EnsureSchema();
        }

        public void Append(SocketChatEnvelope envelope) {
            if (envelope == null) throw new ArgumentNullException(nameof(envelope));
            lock (_gate) {
                var table = GetTable();
                if (table.Rows.Any(row => string.Equals(row[0]?.ToString(), envelope.LobbyId, StringComparison.Ordinal) && Convert.ToInt64(row[2]) == envelope.Sequence && string.Equals(row[1]?.ToString(), envelope.SenderFingerprint, StringComparison.Ordinal))) return;
                table.Rows.Add(new object[] { envelope.LobbyId, envelope.SenderFingerprint, envelope.Sequence, envelope.CreatedUtc.ToString("O"), JsonSerializer.Serialize(envelope) });
                _server.Save();
            }
        }

        public IReadOnlyList<SocketChatEnvelope> Read(string lobbyId, int limit = 250) {
            lock (_gate) return GetTable().Rows.Where(r => string.Equals(r[0]?.ToString(), lobbyId, StringComparison.Ordinal))
                .OrderBy(r => Convert.ToInt64(r[2])).TakeLast(Math.Max(1, limit))
                .Select(r => JsonSerializer.Deserialize<SocketChatEnvelope>(r[4]?.ToString() ?? "{}"))
                .Where(r => r != null).ToList();
        }

        private void EnsureSchema() {
            lock (_gate) {
                var db = _server.Databases.GetOrAdd(DatabaseName, name => new Database(name));
                db.Tables.GetOrAdd(RecordsTable, name => new Table(name) { Columns = new List<Column> {
                    new Column("LobbyId", typeof(string)), new Column("Sender", typeof(string)), new Column("Sequence", typeof(long)), new Column("CreatedUtc", typeof(string)), new Column("Envelope", typeof(string)) } });
                _server.Save();
            }
        }

        private Table GetTable() => _server.Databases[DatabaseName].Tables[RecordsTable];
    }
}
