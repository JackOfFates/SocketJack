using System;
using System.Collections.Generic;

namespace SocketJack.Net.Database {

    internal class DataServerManifest {
        public int Version { get; set; } = 2;

        public string SavedUtc { get; set; }

        public string SnapshotHash { get; set; }

        public Dictionary<string, string> Users { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, DatabaseManifest> Databases { get; set; } = new Dictionary<string, DatabaseManifest>(StringComparer.OrdinalIgnoreCase);
    }

    internal class DatabaseManifest {
        public string Name { get; set; }

        public string OwnerUsername { get; set; }

        public string SqlAdminUsername { get; set; }

        public string SqlAdminPassword { get; set; }

        public Dictionary<string, string> Tables { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> TableHashes { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
