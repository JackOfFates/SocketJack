using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
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

        /// <summary>
        /// Server certificate for SSL connections.
        /// </summary>
        public X509Certificate SslCertificate { get; set; }

        public bool isListening { get; set; } = false;

        /// <summary>
        /// Connected Clients.
        /// </summary>
        /// <returns></returns>
        public ConcurrentDictionary<Guid, TcpConnection> Clients {
            get {
                return _Clients;
            }
        }
        protected internal ConcurrentDictionary<Guid, TcpConnection> _Clients = new ConcurrentDictionary<Guid, TcpConnection>();

        public override int Port {
            get {
                return _Port;
            }
            set {
                _Port = value;
            }
        }

        #endregion

        #region Events

        public event ClientConnectedEventHandler ClientConnected;
        public delegate void ClientConnectedEventHandler(ConnectedEventArgs e);

        public event OnClientDisconnectedEventHandler ClientDisconnected;
        public delegate void OnClientDisconnectedEventHandler(DisconnectedEventArgs e);

        public event PortUnavailableEventHandler PortUnavailable;
        public delegate void PortUnavailableEventHandler(ref TcpServer Server, int Port);

        protected internal event StoppedListeningEventHandler StoppedListening;
        protected internal delegate void StoppedListeningEventHandler(TcpServer sender);

        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            if (e.Connection != Connection) ClientDisconnected?.Invoke(e);
        }

        private void TcpServer_InternalReceiveEvent(TcpConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + Connection.RemoteIdentity.ID.ToUpper(), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName()), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + Connection.RemoteIdentity.ID.ToUpper(), objType.Name, BytesReceived.ByteToString() });
                }
            }
        }
        private void TcpServer_InternalReceiveByteCounter(TcpConnection Connection, int BytesReceived) {
            Interlocked.Add(ref Connection.ReceivedBytesCounter, BytesReceived);
        }
        private void TcpServer_InternalSendEvent(TcpConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + Connection.RemoteIdentity.ID.ToUpper(), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName()), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + Connection.RemoteIdentity.ID.ToUpper(), objType.Name, BytesSent.ByteToString() });
                }
            }
        }
        private void TcpServer_InternalSentByteCounter(TcpConnection Connection, int BytesSent) {
            Interlocked.Add(ref Connection.SentBytesCounter, BytesSent);
        }
        private void TcpServer_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, Connection.BytesPerSecondSent.ByteToString(2), 
                                                                                                   Connection.BytesPerSecondReceived.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }
        private void TcpServer_OnClientDisconnected(DisconnectedEventArgs args) {
            string id = new Func<string>(() => { if (args.Connection.RemoteIdentity == null) { return "null"; } else { return args.Connection.RemoteIdentity.ID.ToUpper(); } }).Invoke();
            LogFormat("[{0}] Client Disconnected.", new[] { Name + @"\" + id, Port.ToString() });
            _Clients.Remove(args.Connection.ID);
            if (args.Connection.RemoteIdentity != null && ContainsPeer(args.Connection.RemoteIdentity)) {
                RemovePeer(args.Connection.RemoteIdentity);
                SendBroadcast(new PeerIdentification(args.Connection.ID.ToString(), PeerAction.Dispose));
            }
            if (args.Connection.RemoteIdentity != null && PeerServers.ContainsKey(args.Connection.RemoteIdentity.ID)) {
                List<PeerServer> ServerList;
                lock (PeerServers)
                    ServerList = PeerServers[args.Connection.RemoteIdentity.ID];
                for (int i = 0, loopTo = ServerList.Count - 1; i <= loopTo; i++) {
                    var Server = ServerList[i];
                    Send(Server.RemotePeer.ID, Server.ShutdownSignal());
                }
                lock (PeerServers)
                    PeerServers[args.Connection.RemoteIdentity.ID] = ServerList;
            }
        }
        #endregion

        #region Peer To Peer
        private void TcpServer_PeerServerShutdown(object sender, PeerServer Server) {
            Guid ClientID = default;

            Guid.TryParse(Server.RemotePeer.ID, out ClientID);
            if (ClientID != default) {
                if (Clients.ContainsKey(ClientID)) {
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
                    var Client = Clients[ClientID];
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
                if (Clients.ContainsKey(ClientID)) {
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
                    var Client = Clients[ClientID];
                    Client.Send(Server);
                }
            }

        }
        private void TcpServer_InternalSendToClient(PeerIdentification Recipient, PeerIdentification Sender, object Obj, int BytesReceived) {
            Guid ClientID = default;

            Guid.TryParse(Recipient.ID, out ClientID);
            if (ClientID != default) {
                if (Clients.ContainsKey(ClientID)) {
                    var Client = Clients[ClientID];
                    Client.Send(Obj);
                }
            }
        }
        private void SyncPeer(TcpConnection Client) {
            //SendBroadcast(Client.RemoteIdentity, Client);
            //peerList.ForEach(Client.Send);
            Task.Run(() => {
                Client.Send(Peers.ToArrayWithLocal(Client));
                Clients.SendBroadcast(Client.RemoteIdentity);
            });
        }
        private void InitializePeer(TcpConnection Client) {
            Task.Run(() => {
                AddPeer(Client.RemoteIdentity);
                SyncPeer(Client);
            });
        }
        #endregion

        #region Private/Internal

        private Dictionary<string, List<PeerServer>> PeerServers = new Dictionary<string, List<PeerServer>>();
        private bool DelayListen = false;
        protected internal int ActiveClients = 0;
        protected internal int PendingConnections = 0;

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
        protected internal void StartServerLoop() {
            Task.Factory.StartNew(async () => {
                while (isListening) {
                    int AcceptNext = Options.Backlog - PendingConnections;
                    if (AcceptNext > 0) {
                        Parallel.For(1, AcceptNext, (i) => {
                            if (PendingConnections + 1 <= Options.Backlog) {
                                Interlocked.Increment(ref PendingConnections);
                                Socket.BeginAccept(new AsyncCallback(AcceptCallback), null);
                            }
                        });
                    }
                    await Task.Delay(1);
                }
                LogFormat("[{0}] Shutdown Started *:{1}", new[] { Name, Port.ToString() });
                Clients.ToList().ForEach((KeyValuePair<Guid, TcpConnection> kvp) => {
                    if (kvp.Value != null && !kvp.Value.Closed) {
                        CloseConnection(kvp.Value);
                    }
                });
                CloseConnection(Connection);
                while (Clients.Count != 0)
                   await Task.Delay(1);
                LogFormat("[{0}] Shutdown Complete *:{1}", new[] { Name, Port.ToString() });
            }, TaskCreationOptions.LongRunning );
        }
        protected internal void AcceptCallback(IAsyncResult ar) {
            if (!isListening)
                return;
            var tryResult = MethodExtensions.TryInvoke(Socket.EndAccept, ref ar);
            if (PendingConnections >= 1) Interlocked.Decrement(ref PendingConnections);
            if (tryResult.Success) {
                var newSocket = tryResult.Result;
                if (newSocket.Connected) {
                    var newConnection = NewConnection(ref newSocket);
                    LogFormat("[{0}] Client Connected.", new[] { Name + @"\" + newConnection.RemoteIdentity.ID.ToUpper(), Port.ToString() });
                    ClientConnected?.Invoke(new ConnectedEventArgs(this, newConnection));
                }
            }
        }
        private TcpConnection NewConnection(ref Socket handler) {
            var newConnection = new TcpConnection(this, handler);
            newConnection._Stream = new NetworkStream(newConnection.Socket);
            if (Options.UseSsl)
                newConnection.InitializeSslStream(SslCertificate, SslTargetHost);
            bool Success = false;
            while (!Success) {
                newConnection.ID = Guid.NewGuid();
                Success = Clients.TryAdd(newConnection.ID, newConnection);
                Thread.Sleep(1);
            }
            newConnection._RemoteIdentity = PeerIdentification.Create(newConnection);
            newConnection.StartReceiving();
            newConnection.StartSending();
            newConnection.StartConnectionTester();
            if (Options.PeerToPeerEnabled)
                InitializePeer(newConnection);
            return newConnection;
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
            ClientDisconnected += TcpServer_OnClientDisconnected;
        }

        /// <summary>
        /// Initializes a new instance of TcpServer.
        /// </summary>
        /// <param name="Port">Socket Listen Port.</param>
        /// <param name="Name">Name used for Logging.</param>
        public TcpServer(TcpOptions Options, int Port, string Name = "TcpServer") : base() {
            this.Options = Options;
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
            ClientDisconnected += TcpServer_OnClientDisconnected;
        }

        /// <summary>
        /// Starts listening on the specified Port.
        /// </summary>
        public bool Listen() {
            if (!isListening) {
                PendingConnections = 0;
                Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                {
                    var withBlock = Socket;
                    withBlock.NoDelay = true;
                    withBlock.ReceiveBufferSize = int.MaxValue;
                    withBlock.SendBufferSize = int.MaxValue;
                    withBlock.ReceiveTimeout = -1;
                    withBlock.SendTimeout = -1;
                }
                Connection = new TcpConnection(this, Socket);
                try {
                    Bind(Port);
                    if (!NIC.InterfaceDiscovered) {
                        Log("Waiting for Network Interface Card..");
                        DelayListen = true;
                        NIC.OnInterfaceDiscovered += this.DelayListenDelegate;
                        return false;
                    } else {
                        Socket.Listen(Options.Backlog);
                        LogFormat("[{0}] Listening on port {1}.", new[] { Name, Port.ToString() });
                        isListening = true;
                        StartServerLoop();
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

        /// <summary>
        /// Stops listening.
        /// </summary>
        public void StopListening() {
            if (isListening) {
                isListening = false;
                Connection.Close(this);
                StoppedListening?.Invoke(this);
                Peers.Clear();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            } else {
                InvokeOnError(null, new Exception("Not listening."));
            }
        }

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="Client">The Client's TcpConnection.</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <remarks>Can also be accessed directly via TcpConnection.Send()</remarks>
        public new void Send(TcpConnection Client, object Obj) {
            if (Client != null && !Client.Closed && Client.Socket.Connected) {
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="ID">The clients's ID.</param>
        /// <param name="Obj">An object.</param>
        /// <remarks>Can also be accessed directly via TcpConnection.Send()</remarks>
        public void Send(string ID, object Obj) {
            Guid ClientGuid = default;
            if (Guid.TryParse(ID, out ClientGuid)) {
                Send(ClientGuid, Obj);
            }
        }

        /// <summary>
        /// Sends an object to a client.
        /// </summary>
        /// <param name="ID">The clients's ID.</param>
        /// <param name="Obj">An Object.</param>
        /// <remarks>Can also be accessed directly from TcpConnection.Send()</remarks>
        public void Send(Guid ID, object Obj) {
            if (Clients.ContainsKey(ID)) {
                var Client = Clients[ID];
                base.Send(Client, Obj);
            }
        }

        /// <summary>
        /// Send an object to an array of TcpConnection.
        /// </summary>
        /// <param name="Clients">An array of ConnectedSocket</param>
        /// <param name="Obj">Object to send to the client.</param>
        /// <param name="Except">The client to exclude.</param>
        /// <remarks>Can be accessed directly from TcpServer.ConnectedSockets.SendBroadcast()</remarks>
        public void SendBroadcast(TcpConnection[] Clients, object Obj, TcpConnection Except = null) {
            this.Clients.SendBroadcast(Obj, Except);
        }

        /// <summary>
        /// Send a serializable object all ConnectedSockets.
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <remarks>Can be accessed directly from TcpServer.ConnectedSockets.SendBroadcast()</remarks>
        public void SendBroadcast(object Obj) {
            Clients.SendBroadcast(Obj);
        }

        /// <summary>
        /// Send a serializable object to all ConnectedSockets (Except)
        /// </summary>
        /// <param name="Obj">Serializable Object to send to the client.</param>
        /// <param name="Except">The socket to exclude.</param>
        /// <remarks></remarks>
        public void SendBroadcast(object Obj, TcpConnection Except) {
            Clients.SendBroadcast(Obj, Except);
        }
    }
}