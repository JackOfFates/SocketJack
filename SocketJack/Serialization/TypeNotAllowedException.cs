using SocketJack;
using SocketJack.Networking;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Text;

namespace SocketJack.Serialization
{
    public class TypeNotAllowedException : Exception {
        public string Type;
        public bool Blacklisted { get; set; } = false;

        private string _message = "Type cannot be deserialized.";
        public override string Message => _message;

        public TypeNotAllowedException(string Type, bool isBlacklisted = false) {
            Initialize(Type, isBlacklisted);
        }

        public TypeNotAllowedException(Type Type, bool isBlacklisted = false) {
            Initialize(Type.AssemblyQualifiedName, isBlacklisted);
        }

        private void Initialize(string Type, bool isBlacklisted = false) {
            this.Type = Type;
            Type t = System.Type.GetType(Type);
            if (t != null) {
                if (isBlacklisted) {
                    Blacklisted = true;
                    _message = $"Type '{t.Namespace}+{t.Name}' is blacklisted.";
                } else {
                    _message = $"Type '{t.Namespace}+{t.Name}' has not been white-listed and cannot be deserialized.";
                }
            }
        }
    }
}
