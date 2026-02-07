using System;
using System.Collections.Concurrent;
using System.Linq;
using SocketJack.Net;

namespace SocketJack.Extensions {

    public static class ConnectedClientExtensions {

        /// <summary>
        /// Send a serializable object to all ConnectedSocket Except
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public static void SendBroadcast(this ConcurrentDictionary<Guid, TcpConnection> Clients, object Obj, TcpConnection Except) {
            var clients = Clients.ToArray();
            for (int i = 0; i < clients.Length; i++) {
                var client = clients.ElementAt(i);
                if (!ReferenceEquals(client.Value, Except)) {
                    client.Value?.Send(Obj);
                }
            }
        }

        /// <summary>
        /// Send a serializable object to all ConnectedSocket.
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks></remarks>
        public static void SendBroadcast(this ConcurrentDictionary<Guid, TcpConnection> Clients, object Obj) {
            Clients.SendBroadcast(Obj, null);
        }
    }
}