using SocketJack.Net.P2P;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Net {
    public interface ISocket {
        public NetworkOptions Options { get; set; }
        public NetworkConnection Connection { get; }
        public PeerList Peers { get; set; }
         
        public bool PeerToPeerInstance { get; }
        protected internal ConcurrentDictionary<string, PeerServer> P2P_ServerInformation { get; set; }
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Servers { get; set; }
        protected internal ConcurrentDictionary<string, NetworkConnection> P2P_Clients { get; set; }
        string Name { get; set; }
        IPEndPoint EndPoint { get; set; }
        Socket Socket { get; set; }
        Stream Stream { get; set; }
        Guid InternalID { get; }
        public Identifier RemoteIdentity { get; set; } 
        public bool isDisposed { get; set; }
        public bool Connected { get; }
        public void RegisterCallback<T>(Action<ReceivedEventArgs<T>> Action);
        public void RemoveCallback<T>(Action<ReceivedEventArgs<T>> Action);
        public void RemoveCallback<T>();
        public void HandleReceive(NetworkConnection Connection, object obj, Type objType, int Length);
        public void InvokeOnError(NetworkConnection Connection, Exception e);
        public void CloseConnection(NetworkConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown);
        public void InvokeInternalReceivedByteCounter(NetworkConnection Connection, int BytesReceived);
        public void InvokeInternalSentByteCounter(NetworkConnection connection, int chunkSize);
        public void InvokeBytesPerSecondUpdate(NetworkConnection connection);
        public void InvokeInternalSendEvent(NetworkConnection connection, Type type, object @object, int length);
        public void InvokeOnSent(SentEventArgs sentEventArgs);
        public void InvokePeerUpdate(ISocket sender, Identifier Peer);
        public void InvokePeerConnected(ISocket sender, Identifier Peer);
        public void InvokePeerDisconnected(ISocket sender, Identifier Peer);
        public void InvokeOnDisconnected(ISocket sender, NetworkConnection Connection);
        public void InvokeOnConnected(ISocket sender, NetworkConnection Connection);
        public void InvokePeerServerShutdown(ISocket sender, PeerServer Server);
        public void InvokeBytesPerSecondUpdate(int ReceivedPerSecond, int SentPerSecond);
        public void InvokeLogOutput(string text);
        public void InvokePeerConnectionRequest(ISocket sender, ref PeerServer Server);
        public void InvokeOnReceive(ref IReceivedEventArgs e);
        public void SendSegmented(NetworkConnection Client, object Obj);
        public void Send(Identifier recipient, object Obj);
        public void Send(NetworkConnection Client, object Obj);
        public void SendBroadcast(object Obj);
        public void SendBroadcast(NetworkConnection[] Clients, object Obj, NetworkConnection Except = null);
        public void SendBroadcast(object Obj, NetworkConnection Except);
        public void InvokeOnDisposing();
        public Task<ISocket> StartServer(Identifier identifier, NetworkOptions options, string name);
        public void LogFormatAsync(string message, string[] values);
        public void LogFormat(string message, string[] values);
        public void Log(string[] messages);
        public void Dispose();
    }
}
