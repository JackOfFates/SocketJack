using System;

namespace SocketJack.Networking.Shared {

    public class ReceivedEventArgs {
        public ReceivedEventArgs(object sender, ConnectedClient Client, object obj, int BytesReceived, PeerIdentification From = null) {
            this.sender = sender;
            this.Client = Client;
            this.obj = obj;
            this.BytesReceived = BytesReceived;
            @type = obj.GetType();
            _From = From;
        }

        public ReceivedEventArgs(object sender, ConnectedClient Client, object obj, Type objType, int BytesReceived, PeerIdentification From = null) {
            this.sender = sender;
            this.Client = Client;
            this.obj = obj;
            this.BytesReceived = BytesReceived;
            @type = objType;
            _From = From;
        }

        public object sender { get; private set; }
        public ConnectedClient Client { get; private set; }
        public object obj { get; private set; }
        public Type @type { get; private set; }
        public int BytesReceived { get; private set; }
        /// <summary>
        /// The Remote Client idendity that sent this object.
        /// </summary>
        /// <returns>Empty if from the server.</returns>
        public PeerIdentification From {
            get {
                return _From;
            }
        }
        private PeerIdentification _From;

        /// <summary>
        /// Set to False to stop the object from being sent to the Recipient (if exists the 'obj' Property in this object will be type of 'PeerRedirect')
        /// </summary>
        /// <returns></returns>
        public bool CancelPeerRedirect { get; set; }

        protected internal ReceivedEventArgs WithIdentity(PeerIdentification From) {
            _From = From;
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