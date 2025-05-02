using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SocketJack.Extensions;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;

namespace SocketJack.Networking.P2P {

    [Serializable]
    public class PeerServer {

        public static int PortLowerBound = 7700;
        public static int PortUpperBound = 7800;

        public event InternalVersionErrorEventHandler InternalVersionError;

        public delegate void InternalVersionErrorEventHandler();
        public long Port { get; set; }
        public string Host { get; set; }
        public PeerIdentification RemotePeer { get; set; }
        public PeerIdentification HostPeer { get; set; }
        public bool Shutdown { get; set; } = false;
        public string Protocol { get; set; }

        public static Dictionary<string, object> InitializedProtocols = new Dictionary<string, object>();

        public event OnNicErrorEventHandler OnNicError;

        public delegate void OnNicErrorEventHandler(Exception ex);

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <param name="AutoReconnect">Reconnect automatically.</param>
        /// <returns>New TcpClient if successful; <see langword="Nothing"/> if connection failed.</returns>
        public async Task<TcpClient> Accept(string Name = "TcpServer", bool AutoReconnect = false) {
            return await Task.Run<TcpClient>(async () => {
                if (HostPeer.ReferenceClient != null && HostPeer.ReferenceClient.Logging) {
                    HostPeer.ReferenceClient.LogFormatAsync(@"[{0}\{1}] Accepting {2} P2P Connection.", new[] { HostPeer.ReferenceClient.Name, HostPeer.ID.ToUpper(), RemotePeer.ID.ToUpper() });
                }
                var newClient = new TcpClient(AutoReconnect, Name) { _PeerToPeerInstance = true };
                // If TcpClient.P2P_Clients.ContainsKey(HostPeer.ID) AndAlso Not TcpClient.P2P_Clients(HostPeer.ID).isDisposed Then TcpClient.P2P_Clients(HostPeer.ID).Dispose()
                if (await newClient.Connect(Host, (int)Port)) {
                    TcpClient.P2P_Clients.AddOrUpdate(HostPeer.ID, newClient);
                }
                return newClient;
            });
        }

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <param name="Name">The TcpServer Name. (Used for Logging)</param>
        /// <returns>new TcpClient</returns>
        public async Task<TcpClient> Accept(string Name) {
            return await Accept(Name);
        }

        /// <summary>
        /// Accept the requested Peer to Peer connection.
        /// </summary>
        /// <returns>new TcpClient</returns>
        public async Task<TcpClient> Accept() {
            return await Accept();
        }

        protected internal PeerServer ShutdownSignal() {
            Shutdown = true;
            return this; // New PeerServer(Host, RemotePeer, HostPeer, Protocol) With {.Shutdown = True}
        }

        private void InvokeNicError(Exception ex) {
            OnNicError?.Invoke(ex);
            NIC.OnError -= InvokeNicError;
        }

        public PeerServer(string IP, PeerIdentification RemotePeer, PeerIdentification PeerHost, ISerializer Protocol) {
            NIC.OnError += InvokeNicError;
            Host = IP;
            Port = NIC.FindOpenPort(PortLowerBound, PortUpperBound, true);
            this.RemotePeer = RemotePeer;
            HostPeer = PeerHost;
            this.Protocol = Protocol.GetType().AssemblyQualifiedName;
        }

        public PeerServer(string IP, PeerIdentification RemotePeer, PeerIdentification PeerHost, string SerializerTypeAssemblyQualifiedName) {
            NIC.OnError += InvokeNicError;
            Host = IP;
            Port = NIC.FindOpenPort(PortLowerBound, PortUpperBound, true);
            this.RemotePeer = RemotePeer;
            HostPeer = PeerHost;
            Protocol = SerializerTypeAssemblyQualifiedName;
        }

        public PeerServer() {

        }
    }
}