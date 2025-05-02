using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.Shared
{
    internal class ReceiveStateResult {
        public ReceiveStateResult(ReceiveState State, List<DeserializedObject> Objects, int LastIndexOf) {
            this.State = State;
            this.Objects = Objects;
            this.LastIndexOf = LastIndexOf;
        }

        public ReceiveStateResult(ReceiveState State, List<DeserializedObject> Objects, int LastIndexOf, List<Exception> Errors) {
            this.State = State;
            this.Objects = Objects;
            this.LastIndexOf = LastIndexOf;
            this.Errors = Errors;
        }

        public ReceiveState State { get; private set; }
        public List<DeserializedObject> Objects { get; private set; }
        public List<Exception> Errors { get; private set; }
        public readonly int LastIndexOf = 0;
    }
}
