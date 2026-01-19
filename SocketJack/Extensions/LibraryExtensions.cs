using SocketJack.Net;
using System;
using System.IO;
using System.Net.Sockets;

namespace SocketJack.Extensions
{
    internal static class LibraryExtensions {

        internal static DisconnectionReason Interpret(this Exception ex) {
            var Reason = DisconnectionReason.Unknown;
            if (ex is IOException ioEx && ioEx.InnerException is SocketException innerSock)
                ex = innerSock;

            if (ex is SocketException sock)
            {
                if (sock.SocketErrorCode == SocketError.ConnectionReset)
                    return DisconnectionReason.RemoteSocketClosed;
                if (sock.SocketErrorCode == SocketError.ConnectionAborted)
                    return DisconnectionReason.LocalSocketClosed;
                if (sock.SocketErrorCode == SocketError.Shutdown)
                    return DisconnectionReason.LocalSocketClosed;
                if (sock.SocketErrorCode == SocketError.OperationAborted)
                    return DisconnectionReason.LocalSocketClosed;
                if (sock.SocketErrorCode == SocketError.NotConnected)
                    return DisconnectionReason.LocalSocketClosed;
            }

            string msg = ex.Message.ToLower();
            if (msg.Contains("the i/o operation has been aborted because of either a thread exit or an application request")) {
                Reason = DisconnectionReason.LocalSocketClosed;
            } else if (msg.Contains("an established connection was aborted by the software in your host machine")) {
                Reason = DisconnectionReason.LocalSocketClosed;
            } else if (msg.Contains("an existing connection was forcibly closed by the remote host")) {
                Reason = DisconnectionReason.RemoteSocketClosed;
            } else if (ex.GetType() == typeof(ObjectDisposedException)) {
                Reason = DisconnectionReason.ObjectDisposed;
            } else if (!NIC.InternetAvailable()) {
                Reason = DisconnectionReason.InternetNotAvailable;
            }
            return Reason;
        }

        internal static bool ShouldLogReason(this DisconnectionReason Reason) {
            bool log = true;
            if (Reason == (DisconnectionReason.RemoteSocketClosed)) {
                log = false;
            } else if (Reason == DisconnectionReason.LocalSocketClosed) {
                log = false;
            } else if (Reason == DisconnectionReason.ObjectDisposed) {
                log = false;
            } else if (Reason == DisconnectionReason.InternetNotAvailable) {
                log = false;
            }
            return log;
        }
    }
}
