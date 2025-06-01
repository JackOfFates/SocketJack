using SocketJack;
using SocketJack.Networking;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace SocketJack.Serialization
{
    public class TypeList : List<string> {

        protected internal List<string> Types { get; set; } = new List<string>();

        public ReadOnlyCollection<string> List { get {  return Types.AsReadOnly(); } }
        public TypeList() {

        }

        public TypeList(Type[] Types) {
            foreach (Type Type in Types) {
                if(!Contains(Type)) Add(Type.AssemblyQualifiedName);
            }
                
        }

        public TypeList(string[] Types) {
            foreach (string Type in Types) {
                if(!Contains(Type)) Add(Type);
            }
        }

        public bool Contains(Type Type) {
            if(Type.IsArray) {
                return Contains(Type.GetElementType().AssemblyQualifiedName);
            } else {
                return Contains(Type.AssemblyQualifiedName);
            }
        }

        public void Add(Type Type) {
            if (!Contains(Type))
                Add(Type.AssemblyQualifiedName);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="index"></param>
        /// <returns><see langword="Null"/> if type not referenced.</returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public Type ElementAt(int index) {
            if (index < 0 || index >= Types.Count)
                throw new ArgumentOutOfRangeException(nameof(index), "Index is out of range.");
            return Type.GetType(Types[index]);
        }

        internal void Remove(Type Type) {
            if (!Contains(Type)) {
                return;
            } else {
                Remove(Type.AssemblyQualifiedName);
            }
        }
    }
}
