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
        public async static void SendBroadcast(this ConcurrentDictionary<Guid, ConnectedClient> Clients, object Obj, ConnectedClient Except) {
            await Task.Run(() => Clients.Values.ToList().ForEach(c => { if (!ReferenceEquals(c, Except) && c != null) c.Send(Obj); }));
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