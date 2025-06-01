using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Mono.Nat;
using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;

namespace SocketJack.Networking {

    /// <summary>
    /// Multithreaded TCP Client.
    /// </summary>
    /// <remarks></remarks>
    public class TcpClient : TcpBase {

        #region Peer To Peer

        protected internal static ConcurrentDictionary<string, PeerServer> P2P_ServerInformation = new ConcurrentDictionary<string, PeerServer>();
        protected internal static ConcurrentDictionary<string, TcpServer> P2P_Servers = new ConcurrentDictionary<string, TcpServer>();
        protected internal static ConcurrentDictionary<string, TcpClient> P2P_Clients = new ConcurrentDictionary<string, TcpClient>();

        public bool PeerIsHost(PeerIdentification RemotePeer) {
            if (ContainsPeer(RemotePeer)) {
                return PeerIsHost(RemotePeer.ID);
            } else {
                return false;
            }
        }
        public bool PeerIsHost(string RemotePeerID) {
            lock (P2P_ServerInformation) {
                lock (base.Peers) {
                    if (base.Peers.ContainsKey(RemotePeerID) && P2P_ServerInformation.ContainsKey(RemotePeerID) && !P2P_ServerInformation[RemotePeerID].Shutdown) {
                        return true;
                    } else {
                        return false;
                    }
                }
            }
        }
        public PeerServer GetPeerServerInfo(PeerIdentification RemotePeer) {
            if (PeerIsHost(RemotePeer)) {
                lock (P2P_ServerInformation)
                    return P2P_ServerInformation[RemotePeer.ID];
            } else {
                return null;
            }
        }
        public PeerServer GetPeerServerInfo(string RemotePeerID) {
            lock (P2P_ServerInformation) {
                lock (base.Peers) {
                    if (base.Peers.ContainsKey(RemotePeerID) && P2P_ServerInformation.ContainsKey(RemotePeerID)) {
                        return P2P_ServerInformation[RemotePeerID];
                    } else {
                        return null;
                    }
                }
            }
        }
        public bool PeerClientExists(string RemotePeerID) {
            lock (base.Peers)
                return base.Peers.ContainsKey(RemotePeerID) && P2P_Clients.ContainsKey(RemotePeerID);
        }
        public TcpClient GetPeerClient(PeerIdentification RemotePeer) {
            return GetPeerClient(RemotePeer.ID);
        }
        public TcpClient GetPeerClient(string RemotePeerID) {
            if (PeerClientExists(RemotePeerID)) {
                var peerClient = P2P_Clients[RemotePeerID];
                return peerClient == null || peerClient.isDisposed ? null : P2P_Clients[RemotePeerID];
            } else {
                return null;
            }
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        public TcpServer StartServer(PeerIdentification RemotePeer, string Name = "TcpServer") {
            return StartServer(RemotePeer, this.Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="RemotePeer">The Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        public TcpServer StartServer(PeerIdentification RemotePeer, TcpOptions Options, string Name = "TcpServer") {
            return StartServer(RemotePeer.ID, Options, Name);
        }

        /// <summary>
        /// Start a connection with the specified Remote Client.
        /// </summary>
        /// <param name="ID">The GUID as String of the Remote Client.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        private TcpServer StartServer(string ID, TcpOptions Options, string Name = "TcpServer") {
            if (!Options.PeerToPeerEnabled) {
                InvokeOnError(Connection, new Exception("P2P is not enabled."));
                return null;
            }
            if (!NIC.InterfaceDiscovered) {
                InvokeOnError(Connection, new PeerToPeerException("Failed to start P2P Server." + Environment.NewLine + "Network interface card has not yet been discovered."));
                return null;
            }
            if (RemoteIdentity is null) {
                InvokeOnError(null, new PeerToPeerException("Client Identity Not initialized." + Environment.NewLine + "ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine + "Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                return null;
            }
            string RemoteIP = RemoteIdentity.IP; // Await HttpClientExtensions.DownloadStringAsync("https: //JackOfAllFates.com/IP.aspx")
            var ServerInfo = new PeerServer(RemoteIP, PeerIdentification.Create(ID), PeerIdentification.Create(RemoteIdentity.ID), Options.Serializer);
            var newServer = new TcpServer(Options, (int)ServerInfo.Port, Name) { _PeerToPeerInstance = true };
            ServerInfo.OnNicError += (ex) => LogFormat("[{0}] NAT Error '{1}'", new[] { newServer.Name, ex.Message });
            newServer.StoppedListening += P2pServer_StoppedListening;
            newServer.ClientConnected += P2pServer_ClientConnected;
            newServer.ClientDisconnected += P2pServer_ClientDisconnected;
            if (P2P_Servers.ContainsKey(ID))
                P2P_Servers[ID].Dispose();
            P2P_ServerInformation.AddOrUpdate(ID, ServerInfo);
            if (newServer.Listen()) {
                LogFormat(new[] { "[{0}] Started P2P Server.", "    EndPoint      = {1}:{2}" }, new[] { newServer.Name + @"\P2P}", RemoteIP, ServerInfo.Port.ToString() });
                P2P_Servers.AddOrUpdate(ID, newServer);
                Send(ServerInfo);
                return newServer;
            } else {
                InvokeOnError(Connection, new PeerToPeerException("Failed to start P2P Server." + Environment.NewLine + "Port may be in use." + Environment.NewLine + "Check if the port is already in use."));
                return null;
            }
        }

        private void P2pServer_StoppedListening(TcpServer sender) {
            lock (P2P_Servers) {
                for (int i = 0, loopTo = P2P_Servers.Count - 1; i <= loopTo; i++) {
                    string key = P2P_Servers.Keys.ElementAtOrDefault(i);
                    var value = P2P_Servers[key];
                    if (ReferenceEquals(value, sender)) {
                        StopP2P(key);
                    }
                }
            }
        }

        private void P2pServer_ClientConnected(ConnectedEventArgs args) {
            TcpServer server = (TcpServer)args.sender;
            LogFormat("[{0}] Client Connected -> {1}", new[] { string.Format(@"{0}\P2P\{1}", new[] { server.Name, args.Connection.RemoteIdentity.ID.ToUpper() }), args.Connection.EndPoint.ToString() });
        }

        private void P2pServer_ClientDisconnected(DisconnectedEventArgs args) {
            TcpServer server = (TcpServer)args.sender;
            LogFormat("[{0}] Client Disconnected -> {1}", new[] { string.Format(@"{0}\P2P\{1}", new[] { server.Name, args.Connection.RemoteIdentity.ID.ToUpper() }), args.Connection.EndPoint.ToString() });
        }

        private void StopP2P(string ID) {
            if (P2P_ServerInformation.ContainsKey(ID)) {
                var Server = P2P_ServerInformation[ID];
                Send(Server.ShutdownSignal());
                if(NIC.NAT != null)
                    Task.Run(() => MethodExtensions.TryInvoke(() => NIC.NAT.DeletePortMap(new Mapping(Protocol.Tcp, (int)Server.Port, (int)Server.Port))));

                lock (P2P_ServerInformation)
                    P2P_ServerInformation.Remove(ID);
                lock (P2P_Servers) {
                    var TcpServer = P2P_Servers[ID];
                    TcpServer.StopListening();
                    TcpServer.Dispose();
                    P2P_Servers.Remove(ID);
                }
            }
        }

        private void C_PeerServerShutdown(object sender, PeerServer Server) {
            if (P2P_Clients.ContainsKey(Server.HostPeer.ID)) {
                LogFormat("[{0}] Shutdown Server -> {1}:{2}", new[] { string.Format(@"{0}\P2P\{1}", new[] { Name, Server.HostPeer.ID }), Server.Host, Server.Port.ToString() });
                P2P_Clients[Server.HostPeer.ID].Disconnect();
                P2P_Clients[Server.HostPeer.ID].Dispose();
                P2P_Clients[Server.HostPeer.ID] = null;
                P2P_Clients.Remove(Server.HostPeer.ID);
            }
        }

        protected internal bool isPeerUpdateSubscribed = false;

        private void TcpClient_PeerUpdate(object sender, PeerIdentification RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.TagUpdate: {
                        UpdatePeerTag(RemotePeer);
                        break;
                    }
                case PeerAction.RemoteIdentity: {
                        LogFormat(@"[{0}\{1}] Connected To Server.", new[] { Name, RemotePeer.ID.ToUpper() });
                        if (!ContainsPeer(RemotePeer)) {
                            var RefPeer = RemotePeer.WithReference(this);
                            AddPeer(RefPeer);
                            PeerConnected?.Invoke(this, RefPeer);
                        }
                        break;
                    }
                case PeerAction.Dispose: {
                        LogFormat(@"[{0}\{1}] Disconnected From Server.", new[] { Name, RemotePeer.ID.ToUpper() });
                        if (ContainsPeer(RemotePeer)) {
                            RemovePeer(RemotePeer);
                            PeerDisconnected?.Invoke(this, RemotePeer);
                        }
                        break;
                    }
            }
        }

        private void TcpClient_InternalPeerConnectionRequest(object sender, ref PeerServer Server) {
            Server.HostPeer = Server.HostPeer.WithReference(this);
        }

        private void TcpClient_PeerConnectionRequest(object sender, PeerServer Server) {
            LogFormat(@"[{0}\{1}] Requested Connection -> {2}:{3}", new[] { Name, Server.HostPeer.ID.ToUpper(), Server.Host, Server.Port.ToString() });
        }

        #endregion

        #region Properties

        /// <summary>
        /// (Optional) Name used for logging purposes.
        /// </summary>
        /// <returns></returns>
        public override string Name {
            get {
                return PeerToPeerInstance ? string.Format(@"{0}\P2P", new[] { _Name }) : _Name;
            }
            set {
                _Name = value;
            }
        }
        private string _Name = "TcpClient";

        public override bool Connected {
            get {
                return _Connected;
            }
        }
        private bool _Connected = false;

        public bool Connecting {
            get {
                return _Connecting;
            }
        }
        private bool _Connecting = false;

        /// <summary>
        /// Connected clients on the Server. (Includes your connection)
        /// </summary>
        /// <returns></returns>
        public new ConcurrentDictionary<string, PeerIdentification> Peers {
            get {
                lock (base.Peers)
                    return base.Peers;
            }
        }

        private bool ResetAutoReconnect = false;

        /// <summary>
        /// Remote identifier used for peer-to-peer interactions used to determine the server-side client ID.
        /// </summary>
        /// <returns><see langword="null"/> if accessed before the server identifies the client.
        /// <para>To avoid problems please do not acccess this via OnConnected Event.</para></returns>
        public PeerIdentification RemoteIdentity {
            get {
                if(Connection != null) {
                    return Connection.RemoteIdentity;
                } else { 
                    return null; 
                }
            }
        }

        #endregion

        #region Private/Internal

        private string LastHost;
        private int LastPort;
        private bool DelayConnect;
        private string DelayHost;
        private int DelayPort;
        private void DelayDelegate(int MTU, IPAddress LocalIP) => ConnectDelayed(DelayHost, DelayPort);
        protected internal static ConcurrentDictionary<string, IPEndPoint> CachedHosts = new ConcurrentDictionary<string, IPEndPoint>();

        private async void ConnectDelayed(string Host, int Port) {
            if (DelayConnect) {
                DelayConnect = false;
                Log("Network Interface Card Discovered.");
                NIC.OnInterfaceDiscovered -= this.DelayDelegate;
                await Connect(Host, Port);
            }
        }
        protected internal async Task<bool> ConnectAsync(Socket socket, EndPoint endpoint, TimeSpan timeout) {
            return await Task.Run(() => { try { if (isDisposed) throw new ObjectDisposedException(Name + " is disposed, cannot connect."); if (_Connecting) return false; _Connecting = true; var result = socket.BeginConnect(endpoint, null, null); bool success = result.AsyncWaitHandle.WaitOne(timeout, isDisposed); if (success) { socket.EndConnect(result); } else { socket.Close(); } _Connecting = false; return success; } catch (Exception) { _Connecting = false; return false; } });
        }
        private async Task<bool> DoConnect(EndPoint EndPoint, string Host, int Port) {
            LogFormat("[{0}] Connecting -> {1}:{2}...", new[] { Name, Host, Port.ToString() });
            Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Socket.NoDelay = true;
            Socket.ReceiveBufferSize = int.MaxValue;
            Socket.SendBufferSize = int.MaxValue;
            Socket.ReceiveTimeout = -1;
            Socket.SendTimeout = -1;

            Connection = new TcpConnection(this, Socket);

            try {
                Bind(_Port);
            } catch (Exception ex) {
                InvokeOnError(null, ex);
            }

            if (await ConnectAsync(Socket, EndPoint, Options.ConnectionTimeout)) {
                _Connected = true;
                Connection._Stream = new NetworkStream(Connection.Socket);
                if(Options.UseSsl)
                    Connection.InitializeSslStream(SslTargetHost);
                //StartByteCounter();
                StartReceiving();
                StartSending();
                StartConnectionTester();
                LogFormat("[{0}] Connected  -> {1}:{2}", new[] { Name, Host, Port.ToString() });
                LastHost = Host;
                LastPort = Port;
                if (ResetAutoReconnect) {
                    ResetAutoReconnect = false;
                    Options.AutoReconnect = true;
                }
                Connection.EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
                OnConnected?.Invoke(this);
                return true;
            }
            return false;
        }
        protected internal async Task<bool> Reconnect(string Host, int Port) {
            return await Connect(Host, Port);
        }
        private IPAddress[] ResolveAllIPv4FromHostEntry(string Host) {
            var hostEntry = Dns.GetHostEntry(Host);
            var IPv4Addresses = new List<IPAddress>();
            foreach (IPAddress IP in hostEntry.AddressList) {
                if (!IP.ToString().Contains(":"))
                    IPv4Addresses.Add(IP);
            }
            if (IPv4Addresses.Count == 0) {
                var address = IPAddress.Parse(Host);
                return new[] { address };
            } else {
                return IPv4Addresses.ToArray();
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Fired when connected to the remote server.
        /// </summary>
        /// <param name="sender"></param>
        public event OnConnectedEventHandler OnConnected;
        public delegate void OnConnectedEventHandler(object sender);

        /// <summary>
        /// Fired when disconnected from the remote server.
        /// </summary>
        public event OnDisconnectedEventHandler OnDisconnected;
        public delegate void OnDisconnectedEventHandler(DisconnectedEventArgs e);

        /// <summary>
        /// Fired when connecting takes longer than ConnectionTimeout timespan.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="Host"></param>
        /// <param name="Port"></param>
        public event ConnectionFailedEventHandler ConnectionFailed;
        public delegate void ConnectionFailedEventHandler(object sender, string Host, int Port);

        /// <summary>
        /// Fired when another user connects to the remote server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="RemotePeer"></param>
        public event PeerConnectedEventHandler PeerConnected;
        public delegate void PeerConnectedEventHandler(object sender, PeerIdentification RemotePeer);

        /// <summary>
        /// Fired when the local client is identified.
        /// </summary>
        public event OnIdentifiedEventHandler OnIdentified;
        public delegate void OnIdentifiedEventHandler(ref PeerIdentification LocalIdentity);

        /// <summary>
        /// Fired when another user disconnects from the remote server.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="RemotePeer"></param>
        public event PeerDisconnectedEventHandler PeerDisconnected;
        public delegate void PeerDisconnectedEventHandler(object sender, PeerIdentification RemotePeer);

        protected internal void InvokeOnIdentified(ref PeerIdentification Identity) {
            OnIdentified?.Invoke(ref Identity);
        }
        protected internal void InvokeOnDisconnected(DisconnectedEventArgs e) {
            OnDisconnected?.Invoke(e);
        }

        private void TcpClient_InternalPeerRedirect(PeerIdentification Recipient, PeerIdentification Sender, object Obj, int ReceivedBytes) {
            PeerRedirect Redirect = (PeerRedirect)Obj;
            Type RedirectType = Type.GetType(Redirect.Type);
            var genericType = typeof(ReceivedEventArgs<>).MakeGenericType(RedirectType);

            var receivedEventArgs = ((IReceivedEventArgs)Activator.CreateInstance(genericType));
            receivedEventArgs.Initialize(this, Connection, Redirect.Redirect, ReceivedBytes, Sender);
            InvokeOnReceive(receivedEventArgs);

            InvokeAllCallbacks(RedirectType, Redirect.Redirect, receivedEventArgs);
            InvokeOnReceive(new ReceivedEventArgs<object>(this, Connection, Redirect.Redirect, ReceivedBytes, Sender));
        }
        private async void TcpClient_OnDisconnected(DisconnectedEventArgs args) {
            base.Peers.Clear();
            //await Task.Delay(100);
            LogFormat("[{0}] Disconnected -> {1}:{2}", new[] { string.Format(@"{0}{1}", new[] { Name, RemoteIdentity == null ? "" : "\\" + RemoteIdentity.ID.ToUpper() }), LastHost, LastPort.ToString() });
            if (Options.AutoReconnect && !isDisposed)
                await Reconnect();
        }
        private void TcpClient_InternalReceiveEvent(TcpConnection Connection, Type objType, object obj, int BytesReceived) {
            if (Options.LogReceiveEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.RemoteIdentity == null ? "Null" : Connection.RemoteIdentity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName()), BytesReceived.ByteToString() });
                } else {
                    LogFormat("[{0}] Received {1} - {2}", new[] { Name + @"\" + (Connection.RemoteIdentity == null ? "Null" : Connection.RemoteIdentity.ID.ToUpper()), objType.Name, BytesReceived.ByteToString() });
                }
            }
        }
        private void TcpClient_InternalReceivedByteCounter(TcpConnection Connection, int BytesReceived) {
            Interlocked.Add(ref base.Connection.ReceivedBytesCounter, BytesReceived);
        }
        private void TcpClient_InternalSendEvent(TcpConnection Connection, Type objType, object obj, int BytesSent) {
            if (Options.LogSendEvents) {
                if (ReferenceEquals(objType, typeof(PeerRedirect))) {
                    PeerRedirect Redirect = (PeerRedirect)obj;
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.RemoteIdentity == null ? "Null" : Connection.RemoteIdentity.ID.ToUpper()), string.Format("PeerRedirect<{0}>", Redirect.CleanTypeName()), BytesSent.ByteToString() });
                } else {
                    LogFormat("[{0}] Sent {1} - {2}", new[] { Name + @"\" + (Connection.RemoteIdentity == null ? "Null" : Connection.RemoteIdentity.ID.ToUpper()), objType.Name, BytesSent.ByteToString() });
                }
            }
        }
        private void TcpClient_InternalSentByteCounter(TcpConnection Connection, int BytesSent) {
            Interlocked.Add(ref Connection.SentBytesCounter, BytesSent);
        }
        private void TcpClient_BytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond) {
            if (Environment.UserInteractive && Options.UpdateConsoleTitle) {
                try {
                    Console.Title = string.Format("{0} - Sent {1}/s Received {2}/s", new[] { Name, Connection.BytesPerSecondSent.ByteToString(2), Connection.BytesPerSecondReceived.ByteToString(2) });
                } catch (Exception) {
                }
            }
        }

        #endregion

        #region Static Methods
        /// <summary>
        /// Check a Port on a remote Server for availability.
        /// </summary>
        /// <param name="Host">Remote Host</param>
        /// <param name="Port">Remote Port</param>
        /// <param name="Timeout">Timeout in milliseconds.</param>
        /// <returns>True if Port is Open, False if Closed.</returns>
        public static async Task<bool> CheckPort(string Host, int Port, int Timeout = 500) {
            TcpOptions opts = new TcpOptions();
            opts.ConnectionTimeout = TimeSpan.FromMilliseconds(Timeout);
            var PortChecker = new TcpClient(opts, "PortChecker");
            var success = await PortChecker.Connect(Host, Port);
            PortChecker.Disconnect();
            PortChecker.Dispose();
            return success;
        }
        #endregion

        #region"IDisposable Implementation"
        protected override void Dispose(bool disposing) {
            Options.AutoReconnect = false;
            var argClient = this;
            Globals.UnregisterClient(ref argClient);
            base.Dispose(disposing);
        }
        #endregion

        /// <summary>
        /// Initializes a new instance of TcpClient.
        /// <para>Uses System.Text.Json as the default serializer.</para>
        /// </summary>
        /// <param name="AutoReconnect">Auto Reconnect on Disconnect/Failed Connection to last Host / Port.</param>
        /// <param name="Name">Name used for logging. </param>
        public TcpClient(string Name = "TcpClient") : base() {
            this.Name = Name;

            var argClient = this;
            Globals.RegisterClient(ref argClient);
            PeerServerShutdown += C_PeerServerShutdown;
            PeerUpdate += TcpClient_PeerUpdate;
            PeerConnectionRequest += TcpClient_PeerConnectionRequest;
            Internal_PeerConnectionRequest += TcpClient_InternalPeerConnectionRequest;
            InternalPeerRedirect += TcpClient_InternalPeerRedirect;
            OnDisconnected += TcpClient_OnDisconnected;
            InternalReceiveEvent += TcpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpClient_InternalReceivedByteCounter;
            InternalSendEvent += TcpClient_InternalSendEvent;
            InternalSentByteCounter += TcpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Initializes a new instance of TcpClient.
        /// </summary>
        /// <param name="Serializer">Serializer for serialization and deserialization.</param>
        /// <param name="AutoReconnect">Auto Reconnect on Disconnect/Failed Connection to last Host / Port.</param>
        /// <param name="Name">Name used for logging. </param>
        public TcpClient(TcpOptions Options, string Name = "TcpClient") : base() {
            this.Options = Options;
            this.Name = Name;
            var argClient = this;
            Globals.RegisterClient(ref argClient);
            PeerServerShutdown += C_PeerServerShutdown;
            PeerUpdate += TcpClient_PeerUpdate;
            PeerConnectionRequest += TcpClient_PeerConnectionRequest;
            Internal_PeerConnectionRequest += TcpClient_InternalPeerConnectionRequest;
            InternalPeerRedirect += TcpClient_InternalPeerRedirect;
            OnDisconnected += TcpClient_OnDisconnected;
            InternalReceiveEvent += TcpClient_InternalReceiveEvent;
            InternalReceivedByteCounter += TcpClient_InternalReceivedByteCounter;
            InternalSendEvent += TcpClient_InternalSendEvent;
            InternalSentByteCounter += TcpClient_InternalSentByteCounter;
            BytesPerSecondUpdate += TcpClient_BytesPerSecondUpdate;
        }

        /// <summary>
        /// Poll the connection to see if it is still connected.
        /// </summary>
        /// <returns></returns>
        public bool Poll() {
            return Socket != null && Connection.Poll();
        }

        /// <summary>
        /// Connect to a remote server.
        /// <para>Awaitable</para>
        /// <para>Configurable from this.ConnectionTimeout or DefaultOptions.ConnectionTimeout</para>
        /// </summary>
        /// <param name="Host">The host you intend to try and connect to (e.g. localhost, 127.0.0.1 etc..)</param>
        /// <param name="Port">The port the host uses</param>
        /// <remarks></remarks>
        public async Task<bool> Connect(string Host, int Port) {
            if (isDisposed)
                throw new ObjectDisposedException(Name + " is disposed, cannot connect.");
            if (Connecting)
                return false;
            if (!NIC.InterfaceDiscovered) {
                DelayConnect = true;
                NIC.OnInterfaceDiscovered += this.DelayDelegate;
                DelayHost = Host;
                DelayPort = Port;

                return false;
            } else {
                string cachedHost = string.Format("{0}:{1}", new object[] { Host, Port });
                if (CachedHosts.ContainsKey(cachedHost)) {
                    await DoConnect(CachedHosts[cachedHost], Host, Port);
                } else {
                    IPAddress[] Addresses = ResolveAllIPv4FromHostEntry(Host);

                    foreach (var address in Addresses) {
                        var endPoint = new IPEndPoint(address, Port);
                        if (await DoConnect(endPoint, Host, Port)) {
                            CachedHosts.AddOrUpdate(cachedHost, endPoint);
                            break;
                        }
                    }
                }

                if (!Socket.Connected) {
                    LogFormat("[{0}] Connection -> {1}:{2} failed!", new[] { Name, Host, Port.ToString() });
                    ConnectionFailed?.Invoke(this, Host, Port);
                    if (Options.AutoReconnect && !isDisposed && !Connecting) {
                        return await Reconnect(Host, Port);
                    }
                }
                return Socket.Connected;
            }

        }

        /// <summary>
        /// Attempt to connect to the last host and port that was Connected.
        /// </summary>
        /// <returns></returns>
        public async Task<bool> Reconnect() {
            return await Reconnect(LastHost, LastPort);
        }

        /// <summary>
        /// Disconnect from the server.
        /// </summary>
        public void Disconnect() {
            if (Options.AutoReconnect) {
                Options.AutoReconnect = false;
                ResetAutoReconnect = true;
            }
            if (Connected) {
                _Connected = false;
                CloseConnection(Connection, DisconnectionReason.LocalSocketClosed);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        /// <summary>
        /// Send an object to the server.
        /// </summary>
        /// <param name="Obj">An object.</param>
        public void Send(object Obj) {
            Send(Connection, Obj);
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Recipient"></param>
        /// <param name="Obj"></param>
        public void Send(PeerIdentification Recipient, object Obj) {
            if (RemoteIdentity is null) {
                InvokeOnError(Connection, new PeerToPeerException("P2P Client not yet initialized." + Environment.NewLine + 
                                                                      "    ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                      "    Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
            } else {
                Send(new PeerRedirect(RemoteIdentity, Recipient,this, Obj));
            }
        }

    }

}