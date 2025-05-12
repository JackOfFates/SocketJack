using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace SocketJack.Networking.Shared
{
    public class WhitelistedTypes {

        protected internal List<string> Types { get; set; } = new List<string>();

        public ReadOnlyCollection<string> List { get {  return Types.AsReadOnly(); } }

        public WhitelistedTypes(Type[] Types) {
            foreach (Type Type in Types)
                AddType(Type);
        }

        public WhitelistedTypes(string[] Types) {
            foreach (string Type in Types)
                AddType(Type);
        }

        public WhitelistedTypes() {

        }

        public bool Contains(Type Type) {
            return Contains(Type.AssemblyQualifiedName);
        }

        public bool Contains(string AssemblyQualifiedTypeName) {
            return Types.Contains(AssemblyQualifiedTypeName);
        }

        public void AddType(Type Type) {
            AddType(Type.AssemblyQualifiedName);
        }

        public void AddType(string AssemblyQualifiedTypeName) {
            if (Types.Contains(AssemblyQualifiedTypeName))
                return;
            Types.Add(AssemblyQualifiedTypeName);
        }

        internal void RemoveType(Type Type) {
            if (!Types.Contains(Type.AssemblyQualifiedName))
                return;
            Types.Remove(Type.AssemblyQualifiedName);
        }
    }
}
