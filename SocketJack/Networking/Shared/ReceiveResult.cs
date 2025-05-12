using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.Shared
{
    public class ReceiveResult {

        public ReceiveResult() {

        }

        public ReceiveResult(byte[] remainingBytes) {
            RemainingBytes = remainingBytes;
        }

        /// <summary>
        /// No Bytes Available.
        /// </summary>
        public static ReceiveResult NotAvailable { get { return _None; } }

        public byte[] RemainingBytes { get; }

        private static readonly ReceiveResult _None = new ReceiveResult();
    }
}
