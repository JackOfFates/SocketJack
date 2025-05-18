using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace SocketJack.Networking.Shared
{
    public class TypeList : List<string> {

        protected internal List<string> Types { get; set; } = new List<string>();

        public ReadOnlyCollection<string> List { get {  return Types.AsReadOnly(); } }

        public TypeList(Type[] Types) {
            foreach (Type Type in Types)
                AddType(Type);
        }

        public TypeList(string[] Types) {
            foreach (string Type in Types)
                Add(Type);
        }

        public TypeList() {

        }

        public bool Contains(Type Type) {
            return Contains(Type.AssemblyQualifiedName);
        }

        public void AddType(Type Type) {
            Add(Type.AssemblyQualifiedName);
        }

        internal void RemoveType(Type Type) {
            if (!Contains(Type))
                return;
            Remove(Type.AssemblyQualifiedName);
        }
    }
}
