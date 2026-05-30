using System.Collections.Concurrent;

namespace SocketJack.Net.Database {

    public class Database {
        public string Name { get; set; }
        public ConcurrentDictionary<string, Table> Tables { get; set; } = new ConcurrentDictionary<string, Table>(System.StringComparer.OrdinalIgnoreCase);
        public string OwnerUsername { get; set; }
        public string SqlAdminUsername { get; set; }
        public string SqlAdminPassword { get; set; }
        public bool HasSqlAdminCredentials => !string.IsNullOrWhiteSpace(SqlAdminUsername) && SqlAdminPassword != null;

        public Database() { }

        public Database(string name) {
            Name = name;
        }

        public Database(string name, string sqlAdminUsername, string sqlAdminPassword) : this(name) {
            OwnerUsername = sqlAdminUsername;
            SqlAdminUsername = sqlAdminUsername;
            SqlAdminPassword = sqlAdminPassword ?? "";
        }
    }
}
