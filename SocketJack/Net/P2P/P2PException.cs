using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Net.P2P
{
    public class P2PException : Exception {
        public P2PException(string message) : base(message) {
        }
    }
}
