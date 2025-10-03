using SocketJack.Net.P2P;
using System;
using System.ComponentModel;

namespace SocketJack.Net {

    public interface IReceivedEventArgs {

        /// <summary>
        /// The Remote Client identity that sent this object.
        /// </summary>
        /// <returns><see langword="null"/> if from the server.</returns>
        public Identifier From { get; set; }

        /// <summary>
        /// Set to False to stop the object from being sent to the Recipient (if exists the 'obj' Property in this object will be type of 'PeerRedirect')
        /// </summary>
        /// <returns></returns>
        public bool CancelPeerRedirect { get; set; }
        public bool IsPeerRedirect { get; set; }
        public ISocket sender { get; set; }
        public TcpConnection Connection { get; set; }
        public Type Type { get; set; }
        public int BytesReceived { get; set; }
        public object Obj { get; set; }

        public void Initialize(ISocket sender, TcpConnection Client, object obj, int BytesReceived, string From = null) {
            this.sender = sender;
            this.Connection = Client;
            this.Type = obj.GetType();
            IsPeerRedirect = Type == typeof(PeerRedirect);
            if(IsPeerRedirect) {
                var redirect = (PeerRedirect)obj;
                this.Obj = redirect.Value;
                this.Type = redirect.Value.GetType();
            } else {
                this.Obj = obj;
            }

            this.BytesReceived = BytesReceived;

            if (From != null)
                this.From = Connection.Parent.Peers[From];
        }

        protected internal IReceivedEventArgs WithIdentity(string From) {
            if (From != null)
                this.From = Connection.Parent.Peers[From];
            return this;
        }
    }

    public class ReceivedEventArgs<T> : IReceivedEventArgs {

        /// <summary>
        /// The Remote Peer information.
        /// </summary>
        /// <returns>Null if from the server.</returns>
        public Identifier From { get; set; }

        /// <summary>
        /// Set to False to stop the object from being sent to the Recipient (if exists the 'obj' Property in this object will be type of 'PeerRedirect')
        /// </summary>
        /// <returns></returns>
        public bool CancelPeerRedirect { get; set; }
        public bool IsPeerRedirect { get; set; }

        public ISocket sender { get; set; }
        public TcpConnection Connection { get; set; }
        public Type Type { get; set; }
        public int BytesReceived { get; set; }

        public T Object { get { return (T)_obj; } private set { _obj = value; } }
        object IReceivedEventArgs.Obj { get => _obj; set => _obj = value; }

        private object _obj = null;

        public ReceivedEventArgs() { }

        public ReceivedEventArgs(ISocket sender, TcpConnection Connection, object obj, int BytesReceived, string From = null) {
            this.sender = sender;
            this.Connection = Connection;
            this.Object = (T)obj;

            this.Type = obj.GetType();
            IsPeerRedirect = Type == typeof(PeerRedirect);
            if (IsPeerRedirect) {
                var redirect = (PeerRedirect)obj;
                this.Object = (T)redirect.Value;
                this.Type = redirect.Value.GetType();
            } else {
                this.Object = (T)obj;
            }

            this.BytesReceived = BytesReceived;
            if (From != null)
                this.From = Connection.Parent.Peers[From];
        }

        protected internal ReceivedEventArgs<T> WithIdentity(string From) {
            if (From != null)
                this.From = Connection.Parent.Peers[From];
            return this;
        }
    }

    public class SentEventArgs {
        public SentEventArgs(ISocket sender, TcpConnection Connection, Type Type, int BytesSent) {
            this.sender = sender;
            this.Connection = Connection;
            this.BytesSent = BytesSent;
            this.Type = Type;
        }
        public ISocket sender { get; private set; }
        public TcpConnection Connection { get; private set; }
        public int BytesSent { get; private set; }
        public Type Type { get; private set; }
    }

    public class DisconnectedEventArgs {
        public DisconnectedEventArgs(ISocket sender, TcpConnection Connection, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            this.sender = sender;
            this.Connection = Connection;
            this.Reason = Reason;
        }

        public ISocket sender { get; private set; }
        public TcpConnection Connection { get; private set; }
        public DisconnectionReason Reason { get; private set; }
    }

    /// <summary>
    /// <para>The reason for the disconnection.</para>
    /// <para>RemoteSocketClosed can be due to connection timeout.</para>
    /// <para>LocalSocketClosed can be due to connection timeout.</para>
    /// </summary>
    public enum DisconnectionReason {
        Unknown,
        RemoteSocketClosed,
        LocalSocketClosed,
        InternetNotAvailable,
        ObjectDisposed,
        CompressionError
    }

    public class ConnectedEventArgs {
        public ConnectedEventArgs(ISocket sender, TcpConnection Connection) {
            this.sender = sender;
            this.Connection = Connection;
        }

        public ISocket sender { get; private set; }
        public TcpConnection Connection { get; private set; }
    }

    public class ErrorEventArgs {
        public ErrorEventArgs(ISocket sender, TcpConnection Connection, Exception e) {
            this.sender = sender;
            this.Connection = Connection;
            Exception = e;
        }

        public ISocket sender { get; private set; }
        public TcpConnection Connection { get; private set; }
        public Exception Exception { get; private set; }
    }
}