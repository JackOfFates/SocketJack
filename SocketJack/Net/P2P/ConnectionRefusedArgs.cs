using System;

namespace SocketJack.Net.P2P {

    /// <summary>
    /// Event Arguments for when a connection is refused.
    /// </summary>
    /// <remarks></remarks>
    public class ConnectionRefusedArgs {
        public ConnectionRefusedArgs(string Message, PeerServer Reference) {
            this.Message = Message;
            this.Reference = Reference;
        }

        public string Message { get; set; }

        public PeerServer Reference { get; set; }
    }

}