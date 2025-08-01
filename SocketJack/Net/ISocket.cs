using SocketJack.Net.P2P;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Net {
    public interface ISocket {
        public TcpOptions Options { get; set; }
        public TcpConnection Connection { get; }
        public PeerList Peers { get; set; }

        public bool PeerToPeerInstance { get; }
        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation { get; set; }
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Servers { get; set; }
        protected internal ConcurrentDictionary<string, TcpConnection> P2P_Clients { get; set; }
        string Name { get; set; }
        Guid InternalID { get; }
        public Identifier RemoteIdentity { get; internal set; }
        public bool isDisposed { get; internal set; }
        public bool Connected { get; }
        public void HandleReceive(TcpConnection Connection, object obj, Type objType, int Length);
        protected internal void InvokeOnError(TcpConnection Connection, Exception e);
        protected internal void CloseConnection(TcpConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown);
        protected internal void InvokeInternalReceivedByteCounter(TcpConnection Connection, int BytesReceived);
        protected internal void InvokeInternalSentByteCounter(TcpConnection connection, int chunkSize);
        protected internal void InvokeInternalSendEvent(TcpConnection connection, Type type, object @object, int length);
        protected internal void InvokeOnSent(SentEventArgs sentEventArgs);
        protected internal void InvokePeerUpdate(object sender, Identifier Peer);
        protected internal void InvokePeerConnected(object sender, Identifier Peer);
        protected internal void InvokePeerDisconnected(object sender, Identifier Peer);
        protected internal void InvokePeerServerShutdown(object sender, PeerServer Server);
        protected internal void InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond);
        protected internal void InvokeLogOutput(string text);
        protected internal void InvokePeerConnectionRequest(object sender, ref PeerServer Server);
        protected internal void InvokeOnReceive(ref IReceivedEventArgs e);
        public void SendSegmented(TcpConnection Client, object Obj);
        public void Send(TcpConnection Client, Identifier recipient, object Obj);
        public void Send(TcpConnection Client, object Obj);
        public void SendBroadcast(object Obj);
        public void SendBroadcast(TcpConnection[] Clients, object Obj, TcpConnection Except = null);
        public void SendBroadcast(object Obj, TcpConnection Except);
        protected internal void InvokeOnDisposing();
        public Task<ISocket> StartServer(Identifier identifier, TcpOptions options, string name);
        public void LogFormatAsync(string message, string[] values);
        public void LogFormat(string message, string[] values);
        public void Log(string[] messages);
        public void Dispose();
        protected internal void InvokeBytesPerSecondUpdate(TcpConnection connection);
    }
}
