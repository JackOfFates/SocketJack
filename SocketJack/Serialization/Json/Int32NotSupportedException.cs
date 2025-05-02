using SocketJack;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;
using System;

namespace SocketJack.Serialization.Json {
    public class Int32NotSupportedException : Exception {

        public override string Message { get; } = "Change the Class's Property listed in Int32NotSupportedException.AffectedProperty to Int64.";

        /// <summary>
    /// This property should be type of Int64.
    /// </summary>
    /// <returns></returns>
        public string AffectedProperty {
            get {
                return _AffectedProperty;
            }
        }
        private string _AffectedProperty;
        public Int32NotSupportedException(string AffectedProperty) {
            _AffectedProperty = AffectedProperty;
        }
    }
}