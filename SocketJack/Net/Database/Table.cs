using System.Collections.Generic;

namespace SocketJack.Net.Database {

    public class Table {
        public string Name { get; set; }
        public List<Column> Columns { get; set; } = new List<Column>();
        public List<object[]> Rows { get; set; } = new List<object[]>();

        public Table() { }

        public Table(string name) {
            Name = name;
        }
    }
}
