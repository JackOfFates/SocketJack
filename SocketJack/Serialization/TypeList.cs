using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SocketJack.Serialization
{
    public class TypeList : List<string> {

        public TypeList() {

        }

        public TypeList(Type[] Types) {
            foreach (Type Type in Types) {
                if (Type == null) continue;
                if (!Contains(Type)) Add($"{Type.Namespace}.{Type.Name}");
            }
                
        }

        public TypeList(string[] Types) {
            foreach (string Type in Types) {
                if (Type == null) continue;
                if (!Contains(Type)) Add(Type);
            }
        }

        public bool Contains(Type Type) {
            if (Type == null) return false;
            if(Type.IsArray) {
                return Contains($"{Type.Namespace}.{Type.GetElementType().Name}");
            } else {
                return Contains($"{Type.Namespace}.{Type.Name}");
            }
        }

        public void Add(Type Type) {
            if (Type == null) return;
            if (!Contains(Type))
                Add($"{Type.Namespace}.{Type.Name}");
        }

        internal void Remove(Type Type) {
            if (Type == null) return;
            if (!Contains(Type)) {
                return;
            } else {
                Remove($"{Type.Namespace}.{Type.Name}");
            }
        }
    }
}
