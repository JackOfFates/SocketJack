using System;
using System.ComponentModel;

namespace SocketJack.Networking.Shared {

    public interface IReceivedEventArgs {

        /// <summary>
        /// The Remote Client idendity that sent this object.
        /// </summary>
        /// <returns>Empty if from the server.</returns>
        public PeerIdentification From { get; set; }

        /// <summary>
        /// Set to False to stop the object from being sent to the Recipient (if exists the 'obj' Property in this object will be type of 'PeerRedirect')
        /// </summary>
        /// <returns></returns>
        public bool CancelPeerRedirect { get; set; }

        public object sender { get; set; }
        public ConnectedClient Client { get; set; }

        public Type Type { get; set; }
        public int BytesReceived { get; set; }

        public object Obj { get; set; }


        protected internal void Initialize(object sender, ConnectedClient Client, object obj, int BytesReceived, PeerIdentification From = null) {
            this.sender = sender;
            this.Client = Client;
            this.Obj = obj;
            this.BytesReceived = BytesReceived;
            this.Type = obj.GetType();
            this.From = From;
        }

        protected internal IReceivedEventArgs WithIdentity(PeerIdentification From) {
            this.From = From;
            return this;
        }
    }

    public class ReceivedEventArgs<T> : IReceivedEventArgs {

        /// <summary>
        /// The Remote Peer information.
        /// </summary>
        /// <returns>Null if from the server.</returns>
        public PeerIdentification From { get; set; }

        /// <summary>
        /// Set to False to stop the object from being sent to the Recipient (if exists the 'obj' Property in this object will be type of 'PeerRedirect')
        /// </summary>
        /// <returns></returns>
        public bool CancelPeerRedirect { get; set; }

        public object sender { get; set; }
        public ConnectedClient Client { get; set; }
        public Type Type { get; set; }
        public int BytesReceived { get; set; }

        public T Object { get { return (T)_obj; } private set { _obj = value; } }
        object IReceivedEventArgs.Obj { get => _obj; set => _obj = value; }

        private object _obj = null;

        public ReceivedEventArgs() { }

        public ReceivedEventArgs(object sender, ConnectedClient Client, object obj, int BytesReceived, PeerIdentification From = null) {
            this.sender = sender;
            this.Client = Client;
            this.Object = (T)obj;
            this.BytesReceived = BytesReceived;
            this.Type = Object.GetType();
            this.From = From;
        }

        protected internal ReceivedEventArgs<T> WithIdentity(PeerIdentification From) {
            this.From = From;
            return this;
        }
    }

    public class SentEventArgs {
        public SentEventArgs(object sender, ConnectedClient Client, int BytesSent) {
            this.sender = sender;
            this.Client = Client;
            this.BytesSent = BytesSent;
        }
        public object sender { get; private set; }
        public ConnectedClient Client { get; private set; }
        public int BytesSent { get; private set; }
    }

    public class DisconnectedEventArgs {
        public DisconnectedEventArgs(object sender, ConnectedClient Client, DisconnectionReason Reason = DisconnectionReason.Unknown) {
            this.sender = sender;
            this.Client = Client;
            this.Reason = Reason;
        }

        public object sender { get; private set; }
        public ConnectedClient Client { get; private set; }
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
        ObjectDisposed
    }

    public class ConnectedEventArgs {
        public ConnectedEventArgs(object sender, ConnectedClient Client) {
            this.sender = sender;
            this.Client = Client;
        }

        public object sender { get; private set; }
        public ConnectedClient Client { get; private set; }
    }

    public class ErrorEventArgs {
        public ErrorEventArgs(object sender, ConnectedClient Client, Exception e) {
            this.sender = sender;
            this.Client = Client;
            Exception = e;
        }

        public object sender { get; private set; }
        public ConnectedClient Client { get; private set; }
        public Exception Exception { get; private set; }
    }
}