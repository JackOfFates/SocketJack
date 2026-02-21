using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SocketJack.Extensions;
#if !UNITY_WEBGL
using SocketJack.Net.WebSockets;
#endif
using SocketJack.Serialization;

namespace SocketJack.Net.P2P {

    [Serializable]
    public class PeerServer {

        /// <summary>
        /// Represents the lower bound of the port range used for automatic port allocation.
        /// </summary>
        public static int PortLowerBound = 7800;

        /// <summary>
        /// Represents the upper bound of the port range used for automatic port allocation.
        /// </summary>
        public static int PortUpperBound = 8800;

        public long Port { get; set; }
        public string Host { get; set; }
        public Identifier RemotePeer { get; set; }
        public Identifier LocalClient { get; set; }
        public bool Shutdown { get; set; } = false;
        public string Serializer { get; set; }

        protected internal bool isWebSocket = false;

        public static Dictionary<string, object> InitializedProtocols = new Dictionary<string, object>();

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <param name="AutoReconnect">Reconnect automatically.</param>
        /// <returns>New TcpClient if successful; <see langword="null"/> if connection failed.</returns>
        public async Task<ISocket> Accept(string Name = "TcpServer", bool AutoReconnect = false) {
            if (LocalClient.Parent == null) return null;
            return await Accept(LocalClient.Parent.Options, Name, AutoReconnect);
        }

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <param name="AutoReconnect">Reconnect automatically.</param>
        /// <returns>New TcpClient or WebSocketClient if successful; <see langword="Nothing"/> if connection failed.</returns>
        public async Task<ISocket> Accept(NetworkOptions Options, string Name = "TcpClient", bool AutoReconnect = false) {
#if !UNITY_WEBGL
                        return await Task.Run<ISocket>(async () => {
                            if (LocalClient.Parent == null) return null;
                            if (Options.Logging)
                                LocalClient.Parent.LogFormatAsync(@"[{0}\{1}] Accepting {2} P2P Connection.", new[] { LocalClient.Parent.Name, LocalClient.ID.ToUpper(), RemotePeer.ID.ToUpper() });
                            if (LocalClient.Parent.Connection.IsWebSocket) {
                                var newClient = new WebSocketClient(Options, Name) { PeerToPeerInstance = true };
                                if (await newClient.ConnectAsync(new Uri("ws://" + Host + ":" + (int)Port))) {
                                    LocalClient.Parent.P2P_Clients.AddOrUpdate(LocalClient.ID, newClient.Connection);
                                    return newClient;
                                } else {
                                    return null;
                                }
                            } else {
                                var newClient = new TcpClient(Options, Name) { PeerToPeerInstance = true };
                                if (await newClient.Connect(Host, (int)Port)) {
                                    LocalClient.Parent.P2P_Clients.AddOrUpdate(LocalClient.ID, newClient.Connection);
                                    return newClient;
                                } else {
                                    return null;

                                }
                            }
                        });
#else
            throw new NotImplementedException();
#endif
        }

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <param name="Name">The TcpServer Name. (Used for Logging)</param>
        /// <returns>new TcpClient</returns>
        public async Task<ISocket> Accept(string Name) {
            return await Accept(LocalClient.Parent.Options, Name);
        }

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <returns>new TcpClient</returns>
        public async Task<ISocket> Accept() {
            return await Accept(LocalClient.Parent.Options);
        }

        protected internal PeerServer ShutdownSignal() {
            Shutdown = true;
            return this;
        }

        public static async Task<PeerServer> NewServer(Identifier RemotePeer, Identifier PeerHost, ISerializer Serializer, int Port) {
            var server = new PeerServer();
            server.Port = Port <= 0 ? await NIC.FindOpenPort(PortLowerBound, PortUpperBound, true) : Port;
            server.RemotePeer = RemotePeer;
            server.LocalClient = PeerHost;
            server.Serializer = Serializer.GetType().AssemblyQualifiedName;
            return server;
        }

        public PeerServer() {

        }
    }
}