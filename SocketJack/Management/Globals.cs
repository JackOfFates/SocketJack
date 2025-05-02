using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking;

namespace SocketJack {

    public static class Globals {

        /// <summary>
        /// ThreadManager.Shutdown will dispose all Tcp Client and Servers thus ending active threads.
        /// </summary>
        public static ThreadManager threadManager { get; set; } = new ThreadManager();

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
            ThreadManager.TcpServers.AddOrUpdate( Server.InternalID, Server);
            if (!ThreadManager.RuntimesSet)
                threadManager.ModifyRuntimeConfig();
            if (!ThreadManager.Alive)
                ThreadManager.Alive = true;
        }

        internal static void UnregisterServer(ref TcpServer Server) {
            ThreadManager.TcpServers.Remove( Server.InternalID);
            if (ThreadManager.TcpServers.Count == 0 && ThreadManager.TcpClients.Count == 0)
                ThreadManager.Alive = false;
        }

    }
}