using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using SocketJack.Networking.Shared;

namespace SocketJack.Extensions {

    public static class ConnectedClientExtensions {

        /// <summary>
        /// Send a serializable object to all ConnectedSocket Except
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public static void SendBroadcast(this ConcurrentDictionary<Guid, ConnectedClient> Clients, object Obj, ConnectedClient Except) {
            for (int i = 0; i < Clients.Count; i++) {
                var client = Clients.ElementAt(i);
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
        public static void SendBroadcast(this ConcurrentDictionary<Guid, ConnectedClient> Clients, object Obj) {
            Clients.SendBroadcast(Obj, null);
        }
    }
}