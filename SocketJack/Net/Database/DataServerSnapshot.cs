using System.Collections.Generic;

namespace SocketJack.Net.Database {

    /// <summary>
    /// Serializable snapshot of the entire DataServer state.
    /// </summary>
    internal class DataServerSnapshot {
        public Dictionary<string, string> Users { get; set; }
        public Dictionary<string, DatabaseSnapshot> Databases { get; set; }
    }
}
