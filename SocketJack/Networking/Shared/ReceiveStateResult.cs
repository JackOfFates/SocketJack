using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.Shared
{
    internal class ReceiveStateResult {
        public ReceiveStateResult(byte[] remainingBytes, List<DeserializedObject> Objects, int LastIndexOf) {
            this.remainingBytes = remainingBytes;
            this.Objects = Objects;
            this.LastIndexOf = LastIndexOf;
        }

        public ReceiveStateResult(byte[] remainingBytes, List<DeserializedObject> Objects, int LastIndexOf, List<Exception> Errors) {
            this.remainingBytes = remainingBytes;
            this.Objects = Objects;
            this.LastIndexOf = LastIndexOf;
            this.Errors = Errors;
        }

        public byte[] remainingBytes { get; private set; }
        public List<DeserializedObject> Objects { get; private set; }
        public List<Exception> Errors { get; private set; }
        public readonly int LastIndexOf = 0;
    }
}
