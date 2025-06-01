using System;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Microsoft.VisualBasic;
using SocketJack.Extensions;
using SocketJack.Networking.P2P;
using System.Runtime.InteropServices.ComTypes;
using SocketJack.Management;
using System.Threading.Tasks;
using System.Collections.Generic;
using SocketJack.Serialization;
using System.Diagnostics;
using System.Text;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace SocketJack.Networking.Shared {
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
        internal NetworkStream _Stream = null;
        public SslStream SslStream { get; private set; }

        public static readonly byte[] Terminator = new[] { (byte)226, (byte)128, (byte)161 };

        /// <summary>
        /// Whether or not data is compressed.
        /// </summary>
        public bool Compressed { get {
                return Parent.Options.UseCompression;
            }
        }

        /// <summary>
        /// Whether or not data is encrypted.
        /// </summary>
        public bool SSL { get {
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
        public TcpBase Parent {
            get {
                return _Parent;
            }
        }
        private TcpBase _Parent;

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
        public PeerIdentification RemoteIdentity {
            get {
                return _RemoteIdentity;
            }
        }
        protected internal PeerIdentification _RemoteIdentity;

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
        public byte[] DownloadBuffer {
            get {
                return _DownloadBuffer;
            }
        }
        protected internal byte[] _DownloadBuffer;

        /// <summary>
        /// Buffer used to send data.
        /// </summary>
        public byte[] UploadBuffer {
            get {
                return _UploadBuffer;
            }
        }
        protected internal byte[] _UploadBuffer;

        /// <summary>
        /// True if the connection is closed.
        /// </summary>
        public bool Closed {
            get {
                return _Closed;
            }
        }

        #endregion

        #region Internal
        protected internal bool _Closed = false;

        protected internal ConcurrentQueue<SendQueueItem> SendQueue = new ConcurrentQueue<SendQueueItem>();

        /// <summary>
        /// <see langword="True"/> if created by a TcpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// Sends the remote client their remote Identity and IP.
        /// </summary>
        internal void SendLocalIdentity() {
            Send(PeerIdentification.Create(ID, true, EndPoint.Address.ToString()));
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
                } else if (!Active && Socket.Poll(1000, SelectMode.SelectRead) && Socket.Available == 0) {
                    return false;
                } else {
                    return true;
                }
            } catch (SocketException) {
                return false;
            } catch (ObjectDisposedException) {
                return false;
            }
        }

        protected internal void Close(object sender) {
            if(!_Closed) {
                _Closed = true;
                InvokeDisconnected(sender, new DisconnectedEventArgs(sender, this, DisconnectionReason.LocalSocketClosed));
                MethodExtensions.TryInvoke(() => { Socket.Shutdown(SocketShutdown.Both); });
                MethodExtensions.TryInvoke(() => { Socket.Close(); });
                SendQueue.Clear();
                _UploadBuffer = null;
                _DownloadBuffer = null;
                EndPoint = null;
                if(Stream != null) {
                    MethodExtensions.TryInvoke(Stream.Close);
                    MethodExtensions.TryInvoke(Stream.Dispose);
                    _Stream = null;
                }
                if(Socket != null) {
                    MethodExtensions.TryInvoke(Socket.Close);
                    MethodExtensions.TryInvoke(Socket.Dispose);
                    _Socket = null;
                }
                UnsubscribePeerUpdate();
            }
        }

        protected internal bool isConnectionValid() {
            if (Socket != null)
                Send(new PingObject());

            if (!Poll()) {
                return false;
            } else {
                return true;
            }
        }

        protected internal void StartConnectionTester() {
            Task.Factory.StartNew(async () => {
                while (!isDisposed && !Closed && Socket.Connected) {
                    if (!isConnectionValid()) {
                        Parent.CloseConnection(this, DisconnectionReason.LocalSocketClosed);
                    }
                    await Task.Delay(1000);
                }
            }, TaskCreationOptions.LongRunning);
        }

        #endregion

        #region Peer To Peer

        private void SetPeerID(object sender, PeerIdentification RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                        _RemoteIdentity = RemotePeer;
                        TcpClient Client = (TcpClient)Parent;
                        if (Client != null) {
                            Client.LogFormat("[{0}] Local Identity = {1}", new[] { Client.Name, RemotePeer.ID.ToUpper() });
                            Client.InvokeOnIdentified(ref _RemoteIdentity);
                        }
                        break;
                    }
            }
        }
        private void ResetPeerID(DisconnectedEventArgs args) {
            _RemoteIdentity = null;
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
        public event ClientDisconnectedEventHandler ClientDisconnected;
        public delegate void ClientDisconnectedEventHandler(TcpConnection sender, DisconnectedEventArgs e);

        protected internal void InvokeDisconnected(object sender, DisconnectedEventArgs e) {
            if (sender is TcpClient) 
                ((TcpClient)sender).InvokeOnDisconnected(e);
            if (sender is TcpServer) {
                TcpServer server = ((TcpServer)sender);
                server.InvokeOnDisconnected(e);
            }
                
            ClientDisconnected?.Invoke(this, e);
        }

        #endregion

        #region IDisposable

        private bool isDisposed = false;
        public void Dispose() {
            if(!isDisposed) {
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

        #region"Receive"

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
                    // Not Fully Implemented
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

                    int BufferSize = Parent.Options.DownloadBufferSize;

                    try {
                        if (_DownloadBuffer == null) {
                            _DownloadBuffer = new byte[BufferSize];

                            bytesRead = await Stream.ReadAsync(_DownloadBuffer, 0, BufferSize);
                            if (bytesRead > 0) {
                                Array.Resize(ref _DownloadBuffer, bytesRead);
                            }
                        } else {
                            byte[] temp = new byte[BufferSize];
                            bytesRead = await Stream.ReadAsync(temp, 0, BufferSize);

                            if (bytesRead > 0) {
                                var txt = Encoding.UTF8.GetString(temp);
                                Array.Resize(ref _DownloadBuffer, _DownloadBuffer.Length + bytesRead);
                                Array.Resize(ref temp, bytesRead);
                                temp.CopyTo(_DownloadBuffer, _DownloadBuffer.Length - bytesRead);
                            }

                            temp = null;
                        }
                    } catch (Exception ex) {
                        var Reason = ex.Interpret();
                        if (Reason.ShouldLogReason())
                            Parent.InvokeOnError(this, ex);
                        Parent.CloseConnection(this, Reason);
                    }

                    if (bytesRead > 0) {
                        TotalBytesRead += bytesRead;
                        Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                        await ParseBuffer();
                    }
                } while (bytesRead > 0);
            } else {
                if (Socket is null)
                    return;
                int AvailableBytes = Socket.Available;
                var temp = new byte[AvailableBytes];
                if (Stream is null) return;
                int bytesRead = await Stream.ReadAsync(temp, 0, AvailableBytes);

                if (bytesRead > 0) {
                    _DownloadBuffer = new byte[_DownloadBuffer.Length + AvailableBytes];
                    temp.CopyTo(_DownloadBuffer, _DownloadBuffer.Length);
                    Parent.InvokeInternalReceivedByteCounter(this, bytesRead);
                    temp = null;
                    await ParseBuffer();
                } else {
                    _DownloadBuffer = null;
                }
            }
            IsReceiving = false;
        }
        private async Task ParseBuffer() {
            int LastIndexOf = 0;
            List<Task> tasks = new List<Task>();
            if (DownloadBuffer != null && DownloadBuffer.Length > 0) {
                var TerminatorIndices = DownloadBuffer.IndexOfAll(Terminator);
                for (int i = 0, loopTo = TerminatorIndices.Count - 1; i <= loopTo; i++) {
                    int lastIndex = i > 0 ? TerminatorIndices[i - 1] : 0;
                    int Index = TerminatorIndices[i];
                    if (i == TerminatorIndices.Count - 1)
                        LastIndexOf = Index;

                    tasks.Add(Task.Run(() => {
                        if (DownloadBuffer is null || DownloadBuffer.Length < Index || DownloadBuffer.Length < lastIndex)
                            return;
                        int MinusTerminator = lastIndex + Terminator.Length;
                        byte[] Bytes = DownloadBuffer.Part(lastIndex == 0 ? lastIndex : MinusTerminator, Index);
                        int Length = Bytes.Length;
                        if (Compressed) {
                            var decompressionResult = MethodExtensions.TryInvoke(Parent.Options.CompressionAlgorithm.Decompress, ref Bytes);
                            if (decompressionResult.Success) {
                                Bytes = decompressionResult.Result;
                            } else {
                                Parent.CloseConnection(this, DisconnectionReason.CompressionError);
                                Parent.InvokeOnError(this, decompressionResult.Exception);
                                return;
                            }
                        }
                        Wrapper wrapper = Parent.Options.Serializer.Deserialize(Bytes);
                        var wrapperType = wrapper.GetValueType();
                        if (wrapper.Value == null || wrapper.Type == "" || wrapper.Type != typeof(PingObject).AssemblyQualifiedName) {
                            object unwrapped = wrapper.Unwrap(Parent);
                            if (unwrapped != null)
                                Task.Run(() => { Parent.HandleReceive(this, unwrapped, unwrapped.GetType(), Length); });
                        }
                    }));
                }
                await Task.WhenAll(tasks);
            }
            if (LastIndexOf > 0) {
                if (DownloadBuffer != null && (LastIndexOf + Terminator.Length) - DownloadBuffer.Length == 0) {
                    _DownloadBuffer = null;
                } else {
                    int bufferLength = DownloadBuffer != null ? DownloadBuffer.Length : 0;
                    byte[] remainingBytes = new byte[bufferLength - (LastIndexOf + Terminator.Length)];
                    int minusTerminator = LastIndexOf + Terminator.Length;
                    Array.Copy(DownloadBuffer, minusTerminator, remainingBytes, 0, bufferLength - (minusTerminator));
                    _DownloadBuffer = remainingBytes;
                }
            }
        }

        #endregion

        #region"Sending"

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
                        if(await TrySendQueueItem(item)) {
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
                var type = Item.Object.GetType();
                var wrapped = new Wrapper(Item.Object, Parent);
                byte[] SerializedBytes = Parent.Options.Serializer.Serialize(wrapped);
                // Deprecated.
                if (NIC.MTU == -1 || SerializedBytes.Length < NIC.MTU && SerializedBytes.Length < 65535) {
                //    Object smaller than MTU.
                if (Item.Object.GetType() == typeof(PeerRedirect)) {
                    Item.Object = Item.Object;
                }
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
                        if(SSL) {
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
                if (!Globals.IgnoreLoggedTypes.Contains(type)) {
                    Parent.InvokeInternalSendEvent(Item.Connection, type, Item.Object, TerminatedBytes.Length);
                    Parent.InvokeOnSent(new SentEventArgs(this, Item.Connection, TerminatedBytes.Length));
                }

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
        /// Set the Tag for the connection.
        /// <para>Used for idendtification by other peers with for example a Username.</para>
        /// <para>Can only be set from server.</para>
        /// </summary>
        /// <param name="server"></param>
        public void SetTag(string Tag) {
            if (Parent != null && Parent is TcpServer server) {
                RemoteIdentity.SetTag(server, Tag);
            }
        }

        public TcpConnection(TcpBase Parent, Socket Socket) {
            _Parent = Parent;
            if(Parent is TcpClient) {
                SubscribePeerUpdate();
                IsServer = false;
            } else {
                IsServer = true;
            }
             _Socket = Socket;
            EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
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
        public void Send(PeerIdentification Recipient, object Obj) {
            if (!IsServer) {
                if (Parent is TcpClient Client) {
                    if (ID == default) {
                        Client.InvokeOnError(this, new PeerToPeerException("P2P Client not yet initialized." + Environment.NewLine +
                                                                          "ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine +
                                                                          "Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                    } else {
                        Client.Send(Client.Connection, Obj);
                    }
                }
            }
        }
    }
}