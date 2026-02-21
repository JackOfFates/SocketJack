using SocketJack;
using SocketJack.Extensions;
using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;

namespace SocketJack {

    public static class Globals {

        /// <summary>  
        /// ThreadManager.Shutdown will dispose all Tcp Client and Servers thus ending active threads.  
        /// </summary>  
        public static ThreadManager threadManager { get; } = new ThreadManager();

        public static List<Type> IgnoreLoggedTypes { get; } = new List<Type>(new[]{ typeof(Segment) });
        public static List<Type> BlacklistedRedirects = new List<Type>() { typeof(Identifier), typeof(PeerServer), typeof(PeerRedirect), typeof(Socket), typeof(NetworkConnection) };

        static string HashAssembly(Assembly a) {
            var path = string.IsNullOrEmpty(a.Location) ? "<no file on disk>" : a.Location;
            var bytes = File.ReadAllBytes(path);
            var sha256 = BitConverter.ToString(SHA256.Create().ComputeHash(bytes))
                         .Replace("-", "").ToUpperInvariant();
            return sha256;
        }

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