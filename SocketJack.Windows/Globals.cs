using SocketJack.Extensions;
using System.Collections.Generic;
using System;
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using SocketJack;

namespace SocketJack {

    public static class Globals {

        /// <summary>  
        /// ThreadManager.Shutdown will dispose all Tcp Client and Servers thus ending active threads.  
        /// </summary>  
        public static ThreadManager threadManager { get; } = new ThreadManager();

        public static TypeList IgnoreLoggedTypes { get; } = new TypeList(new[]{ typeof(Segment) });
        public static List<Type> BlacklistedRedirects = new List<Type>() { typeof(Identifier), typeof(PeerServer), typeof(PeerRedirect) };

        public static void RegisterClient(ref ISocket Client) {
            ThreadManager.TcpClients.AddOrUpdate(Client.InternalID, Client);
            if (!ThreadManager.RuntimesSet)
                threadManager.ModifyRuntimeConfig();
            if (!ThreadManager.Alive)
                ThreadManager.Alive = true;
        }

        public static void UnregisterClient(ref ISocket Client) {
            ThreadManager.TcpClients.Remove(Client.InternalID);
            if (ThreadManager.TcpClients.Count == 0 && ThreadManager.TcpServers.Count == 0)
                ThreadManager.Alive = false;
        }

        public static void RegisterServer(ref ISocket Server) {
            ThreadManager.TcpServers.AddOrUpdate(Server.InternalID, Server);
            if (!ThreadManager.RuntimesSet)
                threadManager.ModifyRuntimeConfig();
            if (!ThreadManager.Alive)
                ThreadManager.Alive = true;
        }

        public static void UnregisterServer(ref ISocket Server) {
            ThreadManager.TcpServers.Remove(Server.InternalID);
            if (ThreadManager.TcpServers.Count == 0 && ThreadManager.TcpClients.Count == 0)
                ThreadManager.Alive = false;
        }

    }
}