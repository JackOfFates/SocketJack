using System.Collections.Concurrent;

namespace SocketJack.Net.Database {

    public class Database {
        public string Name { get; set; }
        public ConcurrentDictionary<string, Table> Tables { get; set; } = new ConcurrentDictionary<string, Table>(System.StringComparer.OrdinalIgnoreCase);

        public Database() { }

        public Database(string name) {
            Name = name;
        }
    }
}
