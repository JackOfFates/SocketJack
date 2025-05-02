using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.Shared
{
    public class TypeNotAllowedException : Exception {
        public string Type;
        public override string Message { get; } = "Change the Class's Property listed in Int32NotSupportedException.AffectedProperty to Int64.";
        public TypeNotAllowedException(string Type) {
            this.Type = Type;
            this.Message = "Type '" + Type + "' has not been white-listed to be deserialized.";
        }

        public TypeNotAllowedException(Type Type) {
            this.Type = Type.AssemblyQualifiedName;
            this.Message = "Type '" + Type + "' has not been white-listed to be deserialized.";
        }
    }
}
