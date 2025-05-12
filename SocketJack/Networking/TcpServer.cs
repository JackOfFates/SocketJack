using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;

namespace SocketJack.Networking {

    /// <summary>
    /// Multithreaded TCP Server.
    /// </summary>
    /// <remarks></remarks>
    public class TcpServer : TcpBase {

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public override string Name {
            get {
                return PeerToPeerInstance ? _Name + @"\P2P" : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name;

        public bool isListening { get; set; } = false;

        /// <summary>
        /// Connected Clients.
        /// </summary>
        /// <returns></returns>
        public ConcurrentDictionary<Guid, ConnectedClient> ConnectedClients {
            get {
                return _ConnectedClients;
            }
        }

        public override int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }

        /// <summary>
        /// Maximum concurrent pending connections.
        /// </summary>
        /// <returns>9999 is default. Lower to reduce processing time.</returns>
        public int Backlog {
            get {
                return _Backlog;
            }
            set {
                _Backlog = value;
            }
        }
        private int _Backlog = DefaultOptions.Backlog;

        #endregion

        #region Events

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event PortUnavailableEventHandler PortUnavailable;
        public delegate void PortUnavailableEventHandler(ref TcpServer Server, int Port);

        protected internal event StoppedListeningEventHandler StoppedListening;
        protected internal delegate void StoppedListeningEventHandler(TcpServer sender);

        private void TcpServer_InternalReceiveEvent(ConnectedClient ConnectedSocket, Type objType, object obj, int BytesReceived) {
            if (LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name + @"\" + ConnectedSocket.RemoteIdentity.ID.ToUpper(), string.Format("PeerRedirect (Of {0}", Redirect.Obj.GetType().Name), BytesReceived.ByteToString() });
                } else {
                    LogFormatAsync("[{0}] Received {1} - {2}", new[] { Name + @"\" + ConnectedSocket.RemoteIdentity.ID.ToUpper(), objType.ToString(), BytesReceived.ByteToString() });
                }
            }
        }
        private void TcpServer_InternalReceiveByteCounter(ConnectedClient ConnectedSocket, int BytesReceived) {
            Interlocked.Add(ref ReceivedBytesCounter, BytesReceived);
        }
        private void TcpServer_InternalSendEvent(ConnectedClient ConnectedSocket, Type objType, object obj, int BytesSent) {
            if (LogSendEvents) {
                LogFormatAsync("[{0}] Sent {1} - {2}", new[] { Name + @"\" + ConnectedSocket.RemoteIdentity.ID.ToUpper(), objType.ToString(), BytesSent.ByteToString() });
            }
        }
        private void TcpServer_InternalSentByteCounter(ConnectedClient ConnectedSocket, int BytesSent) {
            Interlocked.Add(ref SentBytesCounter, BytesSent);
        }
        private void TcpServer_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, BytesPerSecondSent.ByteToString(2), BytesPerSecondReceived.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }
        private void TcpServer_OnDisconnected(DisconnectedEventArgs args) {
            string id = new Func<string>(() => { if (args.Client.RemoteIdentity == null) { return "null"; } else { return args.Client.RemoteIdentity.ID.ToUpper(); } }).Invoke();
            LogFormatAsync("[{0}] Client Disconnected.", new[] { Name + @"\" + id, Port.ToString() });
            _ConnectedClients.Remove(args.Client.ID);
            if (args.Client.RemoteIdentity != null && ContainsPeer(args.Client.RemoteIdentity)) {
                RemovePeer(args.Client.RemoteIdentity);
                SendBroadcast(new PeerIdentification(args.Client.ID.ToString(), PeerAction.Dispose));
            }
            if (args.Client.RemoteIdentity != null && PeerServers.ContainsKey(args.Client.RemoteIdentity.ID)) {
                List<PeerServer> ServerList;
                lock (PeerServers)
                    ServerList = PeerServers[args.Client.RemoteIdentity.ID];
                for (int i = 0, loopTo = ServerList.Count - 1; i <= loopTo; i++) {
                    var Server = ServerList[i];
                    Send(Server.RemotePeer.ID, Server.ShutdownSignal());
                }
                lock (PeerServers)
                    PeerServers[args.Client.RemoteIdentity.ID] = ServerList;
            }
        }
        #endregion

        #region Peer To Peer
        private void TcpServer_PeerServerShutdown(object sender, PeerServer Server) {
            Guid ClientID = default;

            Guid.TryParse(Server.RemotePeer.ID, out ClientID);
            if (ClientID != default) {
                if (ConnectedClients.ContainsKey(ClientID)) {
                    if (Server.Shutdown) {
                        if (PeerServers.ContainsKey(Server.HostPeer.ID)) {
                            List<PeerServer> ServerList;
                            lock (PeerServers)
                                ServerList = PeerServers[Server.HostPeer.ID];
                            for (int i = ServerList.Count - 1; i >= 0; i -= 1) {
                                if ((ServerList[i].HostPeer.ID ?? "") == (Server.HostPeer.ID ?? "") && (ServerList[i].RemotePeer.ID ?? "") == (Server.RemotePeer.ID ?? "")) {
                                    ServerList.RemoveAt(i);
                                }
                                lock (PeerServers)
                                    PeerServers[Server.HostPeer.ID] = ServerList;
                            }
                        }
                    }
                    var Client = ConnectedClients[ClientID];
                    Client.Send(Server);
                }
            }

        }
        private void TcpServer_PeerRefusedConnection(object sender, ConnectionRefusedArgs e) {
            Send(e.Reference.Host, e);
        }
        private void TcpServer_PeerConnectionRequest(object sender, PeerServer Server) {
            Guid ClientID = default;

            Guid.TryParse(Server.RemotePeer.ID, out ClientID);
            if (ClientID != default) {
                if (ConnectedClients.ContainsKey(ClientID)) {
                    if (Server.Shutdown) {
                        if (PeerServers.ContainsKey(Server.HostPeer.ID)) {
                            List<PeerServer> ServerList;
                            lock (PeerServers)
                                ServerList = PeerServers[Server.HostPeer.ID];
                            for (int i = ServerList.Count - 1; i >= 0; i -= 1) {
                                if ((ServerList[i].HostPeer.ID ?? "") == (Server.HostPeer.ID ?? "") && (ServerList[i].RemotePeer.ID ?? "") == (Server.RemotePeer.ID ?? "")) {
                                    ServerList.RemoveAt(i);
                                }
                                lock (PeerServers)
                                    PeerServers[Server.HostPeer.ID] = ServerList;
                            }
                        }
                    } else {
                        lock (PeerServers) {
                            if (!PeerServers.ContainsKey(Server.HostPeer.ID)) {
                                PeerServers.Add(Server.HostPeer.ID, new List<PeerServer>() { Server });
                            } else if (!PeerServers[Server.HostPeer.ID].Contains(Server)) {
                                PeerServers[Server.HostPeer.ID].Add(Server);
                            }
                        }
                    }
                    var Client = ConnectedClients[ClientID];
                    Client.Send(Server);
                }
            }

        }
        private void TcpServer_InternalSendToClient(PeerIdentification Recipient, PeerIdentification Sender, object Obj, int BytesReceived) {
            Guid ClientID = default;

            Guid.TryParse(Recipient.ID, out ClientID);
            if (ClientID != default) {
                if (ConnectedClients.ContainsKey(ClientID)) {
                    var Client = ConnectedClients[ClientID];
                    Client.Send(Obj);
                }
            }
        }
        private void SyncPeers(ConnectedClient Client) {
            Peers.ValuesForAll((p) => Client.Send(p));
        }
        private void InitializePeer(ConnectedClient Client) {
            Task.Run(() => {
                AddPeer(Client.RemoteIdentity);
                Client.SendLocalIdentity();
                SyncPeers(Client);
                SendBroadcast(Client.RemoteIdentity, Client);
            });
        }
        #endregion

        #region Private/Internal

        private Dictionary<string, List<PeerServer>> PeerServers = new Dictionary<string, List<PeerServer>>();
        private bool DelayListen = false;
        protected internal int ActiveClients = 0;
        protected internal bool AcceptNextClient = true;

        private void DelayListenDelegate(int MTU, IPAddress LocalIP) => InvokeDelayedListen();
        private void Init(int Port, string Name = "TcpServer") {
            this.Name = Name;
            if (!NIC.PortAvailable(Port)) {
                var argServer = this;
                PortUnavailable?.Invoke(ref argServer, Port);
            } else {
                this.Port = Port;
            }
            var argServer1 = this;
            Globals.RegisterServer(ref argServer1);
        }
        protected internal void AcceptCallback(IAsyncResult ar) {
            if (!isListening)
                return;
            var handler = BaseSocket.EndAccept(ar);

            if (handler.Connected) {
                var newClient = NewConnection(handler);
                LogFormatAsync("[{0}] Client Connected.", new[] { Name + @"\" + newClient.RemoteIdentity.ID.ToUpper(), Port.ToString() });
                ClientConnected?.Invoke(new ConnectedEventArgs(this, newClient));
                AcceptNextClient = true;
            }
        }
        private ConnectedClient NewConnection(Socket handler) {
            var cs = new ConnectedClient(this, handler);
            bool Success = false;
            while (!Success) {
                cs.ID = Guid.NewGuid();
                Success = ConnectedClients.TryAdd(cs.ID, cs);
                Task.Delay(1);
            }
            cs._RemoteIdentity = PeerIdentification.Create(cs);

            if (PeerToPeerEnabled)
                InitializePeer(cs);
            return cs;
        }
        private void InvokeDelayedListen() {
            if (DelayListen) {
                DelayListen = false;
                NIC.OnInterfaceDiscovered -= this.DelayListenDelegate;
                LogAsync("Network Interface Card Discovered.");
                Listen();
            }
        }

        #endregion

        #region IDisposable Implementation

        protected override void Dispose(bool disposing) {
            StopListening();
            var argServer = this;
            Globals.UnregisterServer(ref argServer);
            isListening = false;
            base.Dispose(disposing);
        }

        #endregion

        /// <summary>
        /// Initializes a new instance of TcpServer.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public TcpServer(int Port, string Name = "TcpServer") : base() {
            Init(Port, Name);
            PeerServerShutdown += TcpServer_PeerServerShutdown;
            PeerRefusedConnection += TcpServer_PeerRefusedConnection;
            PeerConnectionRequest += TcpServer_PeerConnectionRequest;
            InternalPeerRedirect += TcpServer_InternalSendToClient;
            InternalReceiveEvent += TcpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpServer_InternalReceiveByteCounter;
            InternalSendEvent += TcpServer_InternalSendEvent;
            InternalSentByteCounter += TcpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpServer_BytesPerSecondUpdate;
            OnDisconnected += TcpServer_OnDisconnected;
        }

        /// <summary>
        /// Initializes a new instance of TcpServer.
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public TcpServer(ISerializer Serializer, int Port, string Name = "TcpServer") : base() {
            this.Serializer = Serializer;
            Init(Port, Name);
            PeerServerShutdown += TcpServer_PeerServerShutdown;
            PeerRefusedConnection += TcpServer_PeerRefusedConnection;
            PeerConnectionRequest += TcpServer_PeerConnectionRequest;
            InternalPeerRedirect += TcpServer_InternalSendToClient;
            InternalReceiveEvent += TcpServer_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpServer_InternalReceiveByteCounter;
            InternalSendEvent += TcpServer_InternalSendEvent;
            InternalSentByteCounter += TcpServer_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpServer_BytesPerSecondUpdate;
            OnDisconnected += TcpServer_OnDisconnected;
        }

        /// <summary>
        /// Starts listening on the specified Port.
        /// </summary>
        public bool Listen() {
            if (!isListening) {
                AcceptNextClient = true;
                BaseSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                {
                    var withBlock = BaseSocket;
                    withBlock.NoDelay = true;
                    withBlock.ReceiveBufferSize = int.MaxValue;
                    withBlock.SendBufferSize = int.MaxValue;
                    withBlock.ReceiveTimeout = -1;
                    withBlock.SendTimeout = -1;
                }
                BaseConnection = new ConnectedClient(this, BaseSocket);
                try {
                    Bind(Port);
                    if (!NIC.InterfaceDiscovered) {
                        LogAsync("Waiting for Network Interface Card..");
                        DelayListen = true;
                        NIC.OnInterfaceDiscovered += this.DelayListenDelegate;
                        return false;
                    } else {
                        BaseSocket.Listen(Backlog);
                        isListening = true;
                        Task.Run(() => ServerLoop());
                        return true;
                    }
                } catch (Exception ex) {
                    InvokeOnError(null, ex, true);
                    return false;
                }
            } else {
                InvokeOnError(null, new Exception("Already listening."));
                return false;
            }
        }

        protected internal void ServerLoop() {
            LogFormatAsync("[{0}] Listening on port {1}.", new[] { Name, Port.ToString() });
            while (isListening) {
                if (AcceptNextClient) {
                    AcceptNextClient = false;
                    BaseSocket.BeginAccept(new AsyncCallback(AcceptCallback), null);
                }
                Thread.Sleep(1);
            }
            LogFormatAsync("[{0}] Shutdown Started *:{1}", new[] { Name, Port.ToString() });
            ConnectedClients.ValuesForAll((Client) => CloseConnection(Client));
            CloseConnection(BaseConnection);
            while (ConnectedClients.Count != 0)
                Thread.Sleep(1);
            LogFormatAsync("[{0}] Shutdown Complete *:{1}", new[] { Name, Port.ToString() });
        }

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void StopListening() {
            if (isListening) {
                isListening = false;
                BaseConnection.CloseClient(this);
                StoppedListening?.Invoke(this);
                Peers.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            } else {
                InvokeOnError(null, new Exception("Not listening."));
            }
        }
        List<object> l1 = new List<object>();
        /// <summary>
        /// Send a serializable object to a Client.
        /// </summary>
        /// <param name="Client">The ConnectedSocket.</param>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks>Send can also be accessed directly from ConnectedSocket.Send()</remarks>
        public new void Send(ConnectedClient Client, object Obj) {
            if (Client != null && !Client.Closed && Client.Socket.Connected) {
                l1.Add(Obj);
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Send a serializable object to a Client.
        /// </summary>
        /// <param name="ClientID">The ConnectedSocket GUID as string.</param>
        /// <param name="Obj">Serializable Object.</param>
        /// <remarks>Send can also be accessed directly from ConnectedSocket.Send()</remarks>
        public void Send(string ClientID, object Obj) {
            Guid ClientGuid = default;
            if (Guid.TryParse(ClientID, out ClientGuid)) {
                Send(ClientGuid, Obj);
            }
        }

        /// <summary>
        /// Send a serializable object to a Client.
        /// </summary>
        /// <param name="ClientGuid">The ConnectedSocket's GUID.</param>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks>Send can also be accessed directly from ConnectedSocket.Send()</remarks>
        public void Send(Guid ClientGuid, object Obj) {
            if (ConnectedClients.ContainsKey(ClientGuid)) {
                var Client = ConnectedClients[ClientGuid];
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Send a serializable object to an array of ConnectedSocket.
        /// </summary>
        /// <param name="Clients">An array of ConnectedSocket</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks>SendBroadcast can be accessed directly from TcpServer.ConnectedSockets.SendBroadcast()</remarks>
        public void SendBroadcast(ConnectedClient[] Clients, object Obj, ConnectedClient Except = null) {
            ConnectedClients.SendBroadcast(Obj, Except);
        }

        /// <summary>
        /// Send a serializable object all ConnectedSockets.
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks>Can be accessed directly from TcpServer.ConnectedSockets.SendBroadcast()</remarks>
        public void SendBroadcast(object Obj) {
            ConnectedClients.SendBroadcast(Obj);
        }

        /// <summary>
        /// Send a serializable object to all ConnectedSockets (Except)
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public void SendBroadcast(object Obj, ConnectedClient Except) {
            ConnectedClients.SendBroadcast(Obj, Except);
        }
    }
}