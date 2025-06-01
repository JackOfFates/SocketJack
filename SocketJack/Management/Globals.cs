using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking;
using System.Collections.Generic;
using System;
using SocketJack.Networking.Shared;
using SocketJack.Networking.P2P;
using SocketJack.Serialization;

namespace SocketJack {

    public static class Globals {

        /// <summary>  
        /// ThreadManager.Shutdown will dispose all Tcp Client and Servers thus ending active threads.  
        /// </summary>  
        public static ThreadManager threadManager { get; } = new ThreadManager();

        public static TypeList IgnoreLoggedTypes { get; } = new TypeList(new[]{ typeof(PingObject) });
        public static List<Type> BlacklistedRedirects = new List<Type>() { typeof(PeerIdentification), typeof(PeerServer), typeof(PeerRedirect) };

        internal static void RegisterClient(ref TcpClient Client) {
            ThreadManager.TcpClients.AddOrUpdate(Client.InternalID, Client);
            if (!ThreadManager.RuntimesSet)
                threadManager.ModifyRuntimeConfig();
            if (!ThreadManager.Alive)
                ThreadManager.Alive = true;
        }

        internal static void UnregisterClient(ref TcpClient Client) {
            ThreadManager.TcpClients.Remove(Client.InternalID);
            if (ThreadManager.TcpClients.Count == 0 && ThreadManager.TcpServers.Count == 0)
                ThreadManager.Alive = false;
        }

        internal static void RegisterServer(ref TcpServer Server) {
            ThreadManager.TcpServers.AddOrUpdate(Server.InternalID, Server);
            if (!ThreadManager.RuntimesSet)
                threadManager.ModifyRuntimeConfig();
            if (!ThreadManager.Alive)
                ThreadManager.Alive = true;
        }

        internal static void UnregisterServer(ref TcpServer Server) {
            ThreadManager.TcpServers.Remove(Server.InternalID);
            if (ThreadManager.TcpServers.Count == 0 && ThreadManager.TcpClients.Count == 0)
                ThreadManager.Alive = false;
        }

    }
}