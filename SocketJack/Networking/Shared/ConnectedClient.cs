﻿using System;
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

namespace SocketJack.Networking.Shared {
    public class ConnectedClient : IDisposable {

        #region Properties
        public Socket Socket { get; set; }

        /// <summary>
        /// Whether the data is compressed or not.
        /// </summary>
        public bool Compressed { get; }

        public IPEndPoint EndPoint { get; set; }
        public Guid ID { get; set; }
        public object Parent {
            get {
                return _Parent;
            }
        }
        private object _Parent;

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

        public bool IsReceiving {
            get {
                return _IsReceiving == 1;
            }
            set {
                Interlocked.Exchange(ref _IsReceiving, value ? 1 : 0);
            }
        }
        private int _IsReceiving = 0;

        public bool IsSending {
            get {
                return _IsSending == 1;
            }
            set {
                Interlocked.Exchange(ref _IsSending, value ? 1 : 0);
            }
        }
        private int _IsSending = 0;

        public byte[] DownloadBuffer {
            get {
                return _DownloadBuffer;
            }
        }
        protected internal byte[] _DownloadBuffer;

        public byte[] UploadBuffer {
            get {
                return _UploadBuffer;
            }
        }
        protected internal byte[] _UploadBuffer;

        public bool Closed {
            get {
                return _Closed;
            }
        }
        #endregion

        #region Internal
        protected internal bool _Closed = false;

        protected internal ConcurrentQueue<SendState> SendQueue = new ConcurrentQueue<SendState>();

        /// <summary>
        /// <see langword="True"/> if created by a TcpServer.
        /// </summary>
        protected internal bool IsServer = false;

        /// <summary>
        /// Set the Tag for the connection.
        /// <para>Used for idendtification by other peers with for example a Username.</para>
        /// <para>Can only be set from server.</para>
        /// </summary>
        /// <param name="server"></param>
        public void SetTag(string Tag) {
            if (Parent != null) {
                if(Parent.GetType() == typeof(TcpServer)) {
                    TcpServer server = (TcpServer)Parent;
                    RemoteIdentity.SetTag(server, Tag);
                }
            }
        }

        protected internal void CloseClient(object sender) {
            if(!_Closed) {
                _Closed = true;
                InvokeDisconnected(sender, new DisconnectedEventArgs(sender, this, DisconnectionReason.LocalSocketClosed));
                MethodExtensions.TryInvoke(() => { Socket.Shutdown(SocketShutdown.Both); });
                SendQueue.Clear();
                _UploadBuffer = null;
                _DownloadBuffer = null;
                _Parent = null;
                EndPoint = null;
                MethodExtensions.TryInvoke(Socket.Close);
                MethodExtensions.TryInvoke(Socket.Dispose);
                Socket = null;
                UnsubscribePeerUpdate();
            }
        }

        #endregion

        #region Peer To Peer

        private void SetPeerID(object sender, PeerIdentification RemotePeer) {
            switch (RemotePeer.Action) {
                case PeerAction.LocalIdentity: {
                        _RemoteIdentity = RemotePeer;
                        TcpClient Client = (TcpClient)Parent;
                        if (Client != null) {
                            Client.LogFormatAsync("[{0}] Local Identity = {1}", new[] { Client.Name, RemotePeer.ID.ToUpper() });
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
        public event ClientDisconnectedEventHandler ClientDisonnected;
        public delegate void ClientDisconnectedEventHandler(ConnectedClient sender, DisconnectedEventArgs e);

        protected internal void InvokeDisconnected(object sender, DisconnectedEventArgs e) {
            if (sender is TcpClient) 
                ((TcpClient)sender).InvokeOnDisconnected(e);
            if (sender is TcpServer) {
                TcpServer server = ((TcpServer)sender);
                server.InvokeOnDisconnected(e);
            }
                
            ClientDisonnected?.Invoke(this, e);
        }

        #endregion

        #region IDisposable
        private bool isDisposed = false;
        public void Dispose() {
            if(!isDisposed) {
                isDisposed = true;
                UnsubscribePeerUpdate();
                CloseClient(Parent);
                GC.SuppressFinalize(this);
            } else {
                throw new ObjectDisposedException(ID.ToString().ToUpper());
            }

        }

        #endregion

        /// <summary>
        /// Sends the remote client their remote Identity and IP.
        /// </summary>
        public void SendLocalIdentity() {
            Send(PeerIdentification.Create(this, true, EndPoint.Address.ToString()));
        }

        public ConnectedClient(TcpServer Server, Socket Socket, bool UseCompression) {
            _Parent = Server;
            IsServer = true;
            this.Socket = Socket;
            Compressed = UseCompression;
            EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
        }

        public ConnectedClient(TcpClient Client, Socket Socket, bool UseCompression) {
            _Parent = Client;
            SubscribePeerUpdate();
            IsServer = false;
            this.Socket = Socket;
            Compressed = UseCompression;
            EndPoint = (IPEndPoint)Socket.RemoteEndPoint;
        }

        public void Send(object Obj) {
            if (IsServer) {
                TcpServer Server = (TcpServer)Parent;
                if (Server is null) return;
                Server.Send(this, Obj);
            } else {
                TcpClient Client = (TcpClient)Parent;
                Client.Send(Client.BaseConnection, Obj);
            }
        }

        public void Send(PeerIdentification Recipient, object Obj) {
            if (IsServer) {
                TcpServer Server = (TcpServer)Parent;
                if (Server is null) return;
                PeerRedirect Wrapper = (PeerRedirect)Obj;
                Server.Send(Recipient.ID, Wrapper.Obj);
            } else {
                TcpClient Client = (TcpClient)Parent;
                if (ID == default) {
                    Client.InvokeOnError(this, new PeerToPeerException("P2P Client not yet initialized." + Environment.NewLine +
                                                                      "ConnectedSocket.Identifier property cannot equal null." + Environment.NewLine + 
                                                                      "Invoke via TcpClient.OnIdentified Event instead of TcpClient.OnConnected."));
                } else {
                    Client.Send(Client.BaseConnection, new PeerRedirect(RemoteIdentity, Recipient, Obj));
                }
            }
        }

    }
}