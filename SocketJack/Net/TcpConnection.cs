using SocketJack.Extensions;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Net {
    public class TcpConnection : IDisposable {

        #region Properties

        /// <summary>
        /// The socket associated to the connection.
        /// </summary>
        public Socket Socket {
            get {
                return _Socket;
            }
        }
        private Socket _Socket = null;

        /// <summary>
        /// The stream associated to the connection.
        /// </summary>
        public Stream Stream {
            get {
                return SSL ? (Stream)SslStream : _Stream;
            }
        }
        public NetworkStream _Stream = null;
        public SslStream SslStream { get; set; }

        public static readonly byte[] Terminator = new[] { (byte)192, (byte)128 };

        /// <summary>
        /// Whether or not data is compressed.
        /// </summary>
        public bool Compressed {
            get {
                return Parent.Options.UseCompression;
            }
        }

        /// <summary>
        /// Whether or not data is encrypted.
        /// </summary>
        public bool SSL {
            get {
                return Parent.Options.UseSsl;
            }
        }

        /// <summary>
        /// The remote endpoint of the connection.
        /// </summary>
        public IPEndPoint EndPoint { get; set; }

        /// <summary>
        /// Unique identifier for this connection.
        /// </summary>
        public Guid ID { get; set; }

        /// <summary>
        /// Parent of this connection.
        /// </summary>
        public ISocket Parent {
            get {
                return _Parent;
            }
        }
        private ISocket _Parent;

        public bool IsWebSocket {
            get {
                return Parent.GetType().Name == "WebSocketClient" || Parent.GetType().Name == "WebSocketServer";
            }
        }

        /// <summary>
        /// True if connection sending or receiving.
        /// </summary>
        public bool Active {
            get {
                return IsReceiving || IsSending;
            }
        }

        /// <summary>
        /// Bytes per second sent on this connection.
        /// </summary>
        public int BytesPerSecondSent {
            get {
                return _SentBytesPerSecond;
            }
        }
        protected internal int _SentBytesPerSecond = 0;
        protected internal int SentBytesCounter = 0;

        /// <summary>
        /// Bytes per second received on this connection.
        /// </summary>
        public int BytesPerSecondReceived {
            get {
                return _ReceivedBytesPerSecond;
            }
        }
        protected internal int _ReceivedBytesPerSecond = 0;
        protected internal int ReceivedBytesCounter = 0;

        /// <summary>
        /// Remote Peer identifier for peer to peer interactions used to determine the Server's Client GUID.
        /// </summary>
        /// <returns>NULL if accessed before the Server identifies the Client.
        /// To avoid problems please do not acccess this via OnConnected Event.</returns>
        public Identifier Identity {
            get {
                return _Identity;
            }
        }
        public Identifier _Identity;

        /// <summary>
        /// True if the connection is receiving data.
        /// </summary>
        public bool IsReceiving {
            get {
                return _IsReceiving == 1;
            }
            set {
                Interlocked.Exchange(ref _IsReceiving, value ? 1 : 0);
            }
        }
        private int _IsReceiving = 0;

        /// <summary>
        /// True if the connection is sending data.
        /// </summary>
        public bool IsSending {
            get {
                return _IsSending == 1;
            }
            set {
                Interlocked.Exchange(ref _IsSending, value ? 1 : 0);
            }
        }
        private int _IsSending = 0;

        /// <summary>
        /// Buffer used to receive data.
        /// </summary>
        public List<byte> DownloadBuffer {
            get {
                return _DownloadBuffer;
            }
        }
        protected internal List<byte> _DownloadBuffer = new List<byte>();

        /// <summary>
        /// Buffer used to send data.
        /// </summary>
        public List<byte> UploadBuffer {
            get {
                return _UploadBuffer;
            }
        }
        protected internal List<byte> _UploadBuffer = new List<byte>();

        /// <summary>
        /// True if the connection is closed.
        /// </summary>
        public bool Closed {
            get {
                return _Closed;
            }
            set {
                _Closed = value;
            }
        }

        #endregion

        #region Internal
        private bool _Closed = false;

        protected internal ConcurrentQueue<SendQueueItem> SendQueue = new ConcurrentQueue<SendQueueItem>();

        /// <summary>
        /// <see langword="True"/> if created by a TcpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// Sends the remote client their remote Identity and IP.
        /// </summary>
        internal void SendLocalIdentity() {
            Send(Identifier.Create(ID, true, EndPoint.Address.ToString()));
        }

        /// <summary>
        /// Polls a socket to see if it is connected.
        /// </summary>
        /// <param name="socket"></param>
        /// <returns><see langword="True"/> if poll successful.</returns>
        public bool Poll() {
            try {
                if (Socket == null) {
                    return false;
                } else if (!Socket.Connected || Closed) {
                    return false;
                    //} else if (!Active && Socket.Poll(10000, SelectMode.SelectRead) && Socket.Available == 0) {
                    //   return false;
                } else {
                    return true;
                }
            } catch (SocketException) {
                return false;
            } catch (ObjectDisposedException) {
                return false;
            }
        }

        public void CloseConnection() {
            Close(Parent);
        }

        public void Close(ISocket sender, DisconnectionReason Reason = DisconnectionReason.LocalSocketClosed) {
            lock (this) {
                if (!Closed) {
                    var e = new DisconnectedEventArgs(sender, this, Reason);
                    if (Socket != null && Socket.Connected) {
                        MethodExtensions.TryInvoke(() => Socket.Shutdown(SocketShutdown.Both));
                        MethodExtensions.TryInvoke(() => Socket.Close());
                    }
                    InvokeDisconnected(sender, e);
                    SendQueue.Clear();
                    _UploadBuffer.Clear();
                    _DownloadBuffer.Clear();
                    sender.EndPoint = null;

                    UnsubscribePeerUpdate();
                }
            }
        }

        public void StartConnectionTester() {
            Task.Factory.StartNew(async () => {
                if (Parent.GetType().Name == "WebSocketClient")
                    return;

                int failedPollCount = 0;
                const int maxFailedPolls = 10;

                while (!isDisposed && !Closed && Socket.Connected) {
                    bool pollResult = Poll();
                    if (!pollResult) {
                        failedPollCount++;
                        if (failedPollCount >= maxFailedPolls) {
                            //Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                            break;
                        }
                    } else {
                        failedPollCount = 0;
                    }
                    await Task.Delay(1000);
                }
            }, TaskCreationOptions.LongRunning);
        }

        #endregion

        #region Peer To Peer

        private void SetPeerID(ISocket sender, Identifier RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                    _Identity = RemotePeer;
                    TcpClient Client = (TcpClient)Parent;
                    if (Client != null) {
                        Client.LogFormat("[{0}] Local Identity = {1}", new[] { Client.Name, RemotePeer.ID.ToUpper() });
                        Client.InvokeOnIdentified(sender, _Identity);
                    }
                    break;
                }
            }
        }
        private void ResetPeerID(DisconnectedEventArgs args) {
            _Identity = null;
            UnsubscribePeerUpdate();
        }
        private void SubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && !Client.isPeerUpdateSubscribed) {
                Client.isPeerUpdateSubscribed = true;
                Client.OnDisconnected += ResetPeerID;
                Client.PeerUpdate += SetPeerID;
            }
        }
        private void UnsubscribePeerUpdate() {
            if (IsServer) return;
            var Client = (TcpClient)Parent;
            if (Client != null && Client.isPeerUpdateSubscribed) {
                Client.OnDisconnected -= ResetPeerID;
                Client.PeerUpdate -= SetPeerID;
                Client.isPeerUpdateSubscribed = false;
            }
        }

        #endregion

        #region Events

        public event ClientDisconnectedEventHandler OnDisconnected;
        public delegate void ClientDisconnectedEventHandler(TcpConnection sender, DisconnectedEventArgs e);

        protected internal void InvokeDisconnected(ISocket sender, DisconnectedEventArgs e) {
            if (e.Connection.Closed) return;
            if (!(sender.GetType().Name == "WebSocketClient") && !(sender.GetType().Name == "WebSocketClient") && !(sender is TcpServer) && !(sender.GetType().Name == "WebSocketServer")) {
                ((ISocket)sender).InvokeOnDisconnected(sender, e.Connection);
                e.Connection.Closed = true;
            }

            OnDisconnected?.Invoke(this, e);
        }

        #endregion

        #region IDisposable

        private bool isDisposed = false;
        public void Dispose() {
            if (!isDisposed) {
                isDisposed = true;
                UnsubscribePeerUpdate();
                Close(Parent);
                GC.SuppressFinalize(this);
            } else {
                throw new ObjectDisposedException(ID.ToString().ToUpper());
            }

        }

        #endregion

        #region SSL

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="serverCertificate">The server certificate (required for server-side).</param>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(X509Certificate serverCertificate, string targetHost) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (serverCertificate == null)
                throw new ArgumentNullException(nameof(serverCertificate), "Server certificate required for SSL server mode.");

            SslStream = new SslStream(_Stream, true);
            SslStream.AuthenticateAsServer(serverCertificate, false, SslProtocols.Tls12 | SslProtocols.Tls12, false);

        }

        /// <summary>
        /// Initializes the SSL stream for this connection.
        /// </summary>
        /// <param name="targetHost">Target host name (required for client-side).</param>
        protected internal void InitializeSslStream(string targetHost) {
            if (_Stream == null)
                throw new InvalidOperationException("NetworkStream must be initialized before SSL.");

            if (SslStream != null)
                throw new InvalidOperationException("SSL stream already initialized.");

            if (string.IsNullOrEmpty(targetHost))
                throw new ArgumentNullException(nameof(targetHost), "Target host required for SSL client mode.");

            SslStream = new SslStream(_Stream, true, null);
            SslStream.AuthenticateAsClient(targetHost, null, SslProtocols.Tls12 | SslProtocols.Tls12, false);
        }
        #endregion

        #region Receive

        protected internal void StartReceiving() {
            Task.Factory.StartNew(async () => {
                while (!isDisposed && !Closed && Socket.Connected) {
                    await Receive();
                }
            }, TaskCreationOptions.LongRunning);
        }

        internal async Task Receive() {
            if (Socket != null && Stream != null && !IsReceiving && _Stream.DataAvailable) {
                IsReceiving = true;
                try {
                    await ReceiveData(_Stream.DataAvailable);
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                }
            } else {
                await Task.Delay(1);
            }
        }

        private async Task ReceiveData(bool DataAvailable) {
            DateTime DownloadStartTime = DateTime.UtcNow;
            long TotalBytesRead = 0L;

            // SSL does not support Socket.Available, so we need to read the data until no more bytes received.
            if (SSL | Parent.Options.isDownloadBuffered) {
                int bytesRead = 0;
                do {
                    // Not Yet Implemented
                    //if (Parent.Options.MaximumDownloadMbps > 0) {
                    // Limit download bandwidth.
                    //while (LimitDownloadBandwidth(TotalDownloadedBytes, DownloadStartTime))
                    //    Thread.Sleep(1);
                    //DownloadStartTime = DateTime.UtcNow;
                    //}

                    if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                        if (!Closed) Parent.CloseConnection(this);
                        return;
                    }

                    try {
                        byte[] temp = new byte[Parent.Options.DownloadBufferSize];
                        bytesRead = await Stream.ReadAsync(temp, 0, Parent.Options.DownloadBufferSize);
                        if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);
                        if (bytesRead > 0) _DownloadBuffer.AddRange(temp);
                        temp = null;
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    }

                    if (bytesRead > 0) {
                        TotalBytesRead += bytesRead;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    }
                } while (bytesRead > 0);
            } else {
                try {
                    if (Socket is null)
                        return;
                    int AvailableBytes = Socket.Available;
                    var temp = new byte[AvailableBytes];
                    if (Stream is null) return;
                    int bytesRead = await Stream.ReadAsync(temp, 0, AvailableBytes);
                    if (bytesRead != temp.Length) Array.Resize(ref temp, bytesRead);

                    if (bytesRead > 0) {
                        if (_DownloadBuffer == null)
                            _DownloadBuffer = new List<byte>(temp);
                        else
                            _DownloadBuffer.AddRange(temp);
                        temp = null;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        _DownloadBuffer = await ParseBuffer(_DownloadBuffer, this, Parent);
                    } else {
                        _DownloadBuffer.Clear();
                    }
                } catch (Exception ex) {
                    var Reason = ex.Interpret();
                    if (Reason.ShouldLogReason())
                        Parent.InvokeOnError(this, ex);
                    Parent.CloseConnection(this, Reason);
                }
            }
            IsReceiving = false;
        }
        private static async Task<List<byte>> ParseBuffer(List<byte> Buffer, TcpConnection Sender, ISocket Target) {
            int LastIndexOf = 0;
            if (Buffer != null && Buffer.Count > 0) {
                if (Target.Options.UseTerminatedStreams) {
                    var TerminatorIndices = Buffer.IndexOfAll(Terminator);

                    if (TerminatorIndices.Count > 0) {


                        List<Task> tasks = new List<Task>();
                        for (int i = 0, loopTo = TerminatorIndices.Count - 1; i <= loopTo; i++) {
                            int lastIndex = i > 0 ? TerminatorIndices[i - 1] : 0;
                            int Index = TerminatorIndices[i];
                            if (i == TerminatorIndices.Count - 1)
                                LastIndexOf = Index;

                            tasks.Add(Task.Run(() => {
                                if (Buffer is null || Buffer.Count < Index || Buffer.Count < lastIndex)
                                    return;
                                int MinusTerminator = lastIndex + Terminator.Length;
                                byte[] Bytes = Buffer.Part(lastIndex == 0 ? lastIndex : MinusTerminator, Index);
                                int Length = Bytes.Length;
                                if (Target.Options.UseCompression) {
                                    var decompressionResult = MethodExtensions.TryInvoke(Target.Options.CompressionAlgorithm.Decompress, ref Bytes);
                                    if (decompressionResult.Success) {
                                        Bytes = decompressionResult.Result;
                                    } else {
                                        Target.CloseConnection(Sender, DisconnectionReason.CompressionError);
                                        Target.InvokeOnError(Sender, decompressionResult.Exception);
                                        return;
                                    }
                                }
                                Wrapper wrapper = Target.Options.Serializer.Deserialize(Bytes);
                                if (wrapper == null) {
                                    Target.InvokeOnError(Sender, new P2PException("Deserialized object returned null."));
                                } else {
                                    var valueType = wrapper.GetValueType();
                                    if (wrapper.value != null || wrapper.Type != "") { //wrapper.Type != typeof(PingObject).AssemblyQualifiedName
                                        if (valueType == typeof(PeerRedirect)) {
                                            Byte[] redirectBytes = null;
                                            object val = wrapper.value;
                                            Type type = wrapper.value.GetType();
                                            if (type == typeof(string)) {
                                                redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes((string)val);
                                            } else if (type == typeof(JsonElement)) {
                                                string json = ((JsonElement)val).GetRawText();
                                                redirectBytes = System.Text.UTF8Encoding.UTF8.GetBytes(json);
                                            }
                                            PeerRedirect redirect = Target.Options.Serializer.DeserializeRedirect(Target, redirectBytes);
                                            Task.Run(() => { Target.HandleReceive(Sender, redirect, valueType, Length); });
                                        } else {
                                            object unwrapped = null;
                                            try {
                                                unwrapped = wrapper.Unwrap(Target);
                                            } catch (Exception ex) {
                                                Target.InvokeOnError(Sender, ex);
                                            }
                                            if (unwrapped != null)
                                                Task.Run(() => { Target.HandleReceive(Sender, unwrapped, valueType, Length); });
                                        }
                                    }
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);
                        if (LastIndexOf > 0) {
                            if (Buffer != null && (LastIndexOf + Terminator.Length) - Buffer.Count == 0) {
                                Buffer.Clear();
                                return Buffer;
                            } else {
                                int removeAt = LastIndexOf + Terminator.Length;
                                if (Buffer.Count > removeAt) {
                                    Buffer.RemoveRange(0, LastIndexOf + Terminator.Length);
                                } else if (Buffer.Count == removeAt) {
                                    Buffer.Clear();
                                }
                            }
                        }
                    }
                    return Buffer;
                } else {
                    var tempBuffer = new List<byte>(Buffer);
                    Buffer.Clear();
                    Task.Run(() => {
                        Target.HandleReceive(Sender, tempBuffer, typeof(byte[]), tempBuffer.Count);
                    });

                    return Buffer;
                }
            } else { return Buffer; }
        }

        #endregion

        #region Send

        protected internal void StartSending() {
            Task.Factory.StartNew(async () => {
                while (!isDisposed && !Closed && Socket.Connected) {
                    await ProcessQueue();
                }
            }, TaskCreationOptions.LongRunning);
        }

        protected async internal Task ProcessQueue() {
            if (Socket != null && Stream != null && (Socket.Connected) && !Closed && !IsSending && SendQueue.Count > 0) {
                IsSending = true;
                var Items = new List<SendQueueItem>();
                while (SendQueue.Count > 0) {
                    SendQueueItem Item;
                    SendQueue.TryDequeue(out Item);
                    if (Item != null)
                        Items.Add(Item);
                }

                if (Items.Count == 0) {
                    IsSending = false;
                    return;
                }

                for (int i = 0; i < Items.Count; i++) {
                    var item = Items[i];
                    if (Socket == null || !Socket.Connected || Closed)
                        break;
                    if (item != null) {
                        if (await TrySendQueueItem(item)) {
                            item.Complete = true;
                        }
                    }
                }
                IsSending = false;
            } else {
                await Task.Delay(1);
            }
        }

        protected internal async Task<bool> TrySendQueueItem(SendQueueItem Item) {
            if (isDisposed || Stream == null || Closed || Item.Complete) return false;
            bool sentSuccessful = false;
            try {
                byte[] SerializedBytes = default;
                Wrapper wrapped = default;
                Type type = default;
                lock (Item.Object) {
                    type = Item.Object.GetType();
                    wrapped = new Wrapper(Item.Object, Parent);
                    SerializedBytes = Parent.Options.Serializer.Serialize(wrapped);
                }
                if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                    //    Object smaller than MTU.
                    byte[] TerminatedBytes = Item.Connection.Compressed ? Parent.Options.CompressionAlgorithm.Compress(SerializedBytes).Terminate() : SerializedBytes.Terminate();
                    int totalBytes = TerminatedBytes.Length;
                    TcpConnection Client = Item.Connection;
                    DateTime UploadStartTime = DateTime.UtcNow;
                    byte[] SentBytes = new byte[totalBytes];
                    string sentTxt = string.Empty;

                    if (Parent.Options.isUploadBuffered) {
                        for (int offset = 0, loopTo = TerminatedBytes.Length - 1; Parent.Options.UploadBufferSize >= 0 ? offset <= loopTo : offset >= loopTo; offset += Parent.Options.UploadBufferSize) {
                            if (Socket is null || Stream is null || !Socket.Connected || Closed || (SSL && SslStream is null)) {
                                SerializedBytes = null;
                                TerminatedBytes = null;
                                wrapped = null;
                                if (!Closed) Parent.CloseConnection(this);
                                return false;
                            }
                            // Not fully implemented.
                            //if (Options.MaximumUploadMbps > 0) {
                            //    // Limit upload bandwidth.
                            //    while (LimitUploadBandwidth(TotalUploadedBytes, UploadStartTime))
                            //        Thread.Sleep(1);
                            //    UploadStartTime = DateTime.UtcNow;
                            //}
                            int chunkSize = Math.Min(Parent.Options.UploadBufferSize, totalBytes - offset);
                            if (SSL) {
                                await SslStream.WriteAsync(TerminatedBytes, offset, chunkSize);
                            } else {
                                await Stream.WriteAsync(TerminatedBytes, offset, chunkSize);
                            }
                            Parent.InvokeInternalSentByteCounter(Item.Connection, chunkSize);
                        }
                    } else {
                        if (SSL) {
                            await SslStream.WriteAsync(TerminatedBytes, 0, TerminatedBytes.Length);
                        } else {
                            await Stream.WriteAsync(TerminatedBytes, 0, TerminatedBytes.Length);
                        }
                        Parent.InvokeInternalSentByteCounter(Item.Connection, TerminatedBytes.Length);
                    }
                    sentSuccessful = true;
                    //if (!Globals.IgnoreLoggedTypes.Contains(type)) {
                    Parent.InvokeInternalSendEvent(Item.Connection, type, Item.Object, TerminatedBytes.Length);
                    Parent.InvokeOnSent(new SentEventArgs(this.Parent, Item.Connection, type, TerminatedBytes.Length));
                    //}

                } else if (SerializedBytes.Length > NIC.MTU) {
                    // Object larger than MTU, we have to Segment the object.
                    Item.Connection.Parent.SendSegmented(Item.Connection, SerializedBytes);
                }
            } catch (Exception ex) {
                if (Closed) return false;
                var Reason = ex.Interpret();
                if (Reason.ShouldLogReason()) {
                    Parent.InvokeOnError(this, ex);
                }
                Parent.CloseConnection(this, Reason);
            }
            return sentSuccessful;
        }
        #endregion

        /// <summary>
        /// Set metadata for the connection serialized with Json.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> to retain private metadata only on server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <paramref name="value">Metadata Value.</paramref>
        /// <paramref name="Private"><see langword="true"/> to keep the metadata only on server; <see langword="false"/> shares with all peers.</paramref>
        /// <paramref name="Restricted"><see langword="true"/> to restrict the client from updating the metadata value.</paramref>
        public void SetMetaData(string key, string value, bool Private = false, bool Restricted = false) {
            if (Parent == null) return;
            if (string.IsNullOrEmpty(key)) return;
            if (Parent is TcpServer server) {
                if (Restricted && !server.RestrictedMetadataKeys.Contains(key.ToLower()))
                    server.RestrictedMetadataKeys.Add(key.ToLower());
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(server, key.ToLower(), value, Private);
                //Identity.SetMetaData(server, key.ToLower(), value, Private);
            } else if (Parent.GetType().Name == "WebSocketServer") {
                // Use reflection to locate the assembly, type, and members
                var wsServerType = Parent.GetType();
                var wsServerAssembly = wsServerType.Assembly;
                var restrictedKeysProp = wsServerType.GetProperty("RestrictedMetadataKeys");

                if (restrictedKeysProp != null) {
                    var restrictedKeys = restrictedKeysProp.GetValue(Parent) as List<string>;
                    if (restrictedKeys != null && Restricted) {
                        var containsMethod = restrictedKeys.GetType().GetMethod("Contains");
                        var addMethod = restrictedKeys.GetType().GetMethod("Add");
                        bool contains = false;
                        if (containsMethod != null)
                            contains = (bool)containsMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                        if (!contains && addMethod != null)
                            addMethod.Invoke(restrictedKeys, new object[] { key.ToLower() });
                    }
                }
                if (!Parent.Peers.ContainsKey(Identity.ID)) {
                    Parent.Peers.AddOrUpdate(Identity.RemoteReady(Parent));
                }
                Parent.Peers[Identity.ID].SetMetaData(Parent, key.ToLower(), value, Private);
                //Identity.SetMetaData(Parent, key.ToLower(), value, Private);
            } else if (Parent is TcpClient client) {
                client.Send(new MetadataKeyValue() { Key = key.ToLower(), Value = value });
            } else if (Parent.GetType().Name == "WebSocketClient") {
                // Use reflection to resolve and invoke Send on Parent
                var sendMethod = Parent.GetType().GetMethod("Send", new[] { typeof(object) });
                if (sendMethod != null) {
                    sendMethod.Invoke(Parent, new object[] { new MetadataKeyValue() { Key = key.ToLower(), Value = value } });
                } else {
                    Parent.InvokeOnError(this, new MissingMethodException("Send method not found on WebSocketClient."));
                }
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection.
        /// <para>Can only be called from server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <returns>Value as string.</returns>
        /// </summary>
        public async Task<string> GetMetaData(string key, bool Private = false, bool WaitForValueIfNull = true) {
            if (Identity == null) return default;
            return await Identity.GetMetaData(key, Private, WaitForValueIfNull);
        }

        public TcpConnection(ISocket Parent, Socket Socket) {
            _Parent = Parent;
            if (Parent is TcpClient) {
                SubscribePeerUpdate();
                IsServer = false;
            } else {
                IsServer = true;
            }
            if (Socket != null) {
                _Socket = Socket;
                EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
            }
        }

        public void Send(object Obj) {
            if (Parent is TcpServer Server) {
                if (Server is null) return;
                Server.Send(this, Obj);
            } else if (Parent is TcpClient Client) {
                Client.Send(Obj);
            }
        }

        /// <summary>
        /// Send an object to a Remote Client on the server.
        /// <para>This will <see langword="NOT"/> expose your remote IP.</para>
        /// </summary>
        /// <param name="Recipient"></param>
        /// <param name="Obj"></param>
        public void Send(Identifier Recipient, object Obj) {
            if (!IsServer) {
                if (Parent is TcpClient Client) {
                    if (ID == default) {
                        Client.InvokeOnError(this, new P2PException("P2P Client not yet initialized." + Environment.NewLine +
                                                                          "     ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                          "     Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                    } else {
                        Client.Send(Recipient, Obj);
                    }
                }
            }
        }
    }
}