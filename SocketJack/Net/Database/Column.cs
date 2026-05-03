using System;

namespace SocketJack.Net.Database {

    public class Column {
        public string Name { get; set; }
        public Type DataType { get; set; }
        public int MaxLength { get; set; }

        public Column() { }

        public Column(string name, Type dataType, int maxLength = -1) {
            Name = name;
            DataType = dataType;
            MaxLength = maxLength;
        }
    }
}
