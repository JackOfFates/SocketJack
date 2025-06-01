using System;

namespace SocketJack.Networking.Shared {
    /// <summary>
    /// Used as a dummy class to check if the connection is alive.
    /// </summary>
    [Serializable]
    public class PingObject {
        public static PingObject StaticInstance { get; } = new PingObject();
        public PingObject() {

        }
    }
}