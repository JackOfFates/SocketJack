using System.Collections.Generic;

namespace SocketJack.Net.Database {

    internal class TableSnapshot {
        public string Name { get; set; }
        public List<ColumnSnapshot> Columns { get; set; } = new List<ColumnSnapshot>();
        public List<string[]> Rows { get; set; } = new List<string[]>();
    }
}
