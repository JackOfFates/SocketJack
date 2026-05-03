using System;
using System.Collections.Generic;

namespace SocketJack.Net.Database {

    internal class DatabaseSnapshot {
        public string Name { get; set; }
        public Dictionary<string, TableSnapshot> Tables { get; set; } = new Dictionary<string, TableSnapshot>(StringComparer.OrdinalIgnoreCase);
    }
}
