using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.P2P
{
    public class PeerToPeerException : Exception {
        public PeerToPeerException(string message) : base(message) {
        }
    }
}
