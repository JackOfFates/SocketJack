using System;
using System.Threading.Tasks;
using SocketJack.Serialization;

namespace SocketJack.Networking.Shared {
    [Serializable]
    public class PeerIdentification {

        public string ID { get; set; }

        /// <summary>
        /// Only available when this instance is your LocalIdentity.
        /// </summary>
        /// <returns>Your remote IP.</returns>
        public string IP { get; set; }

        /// <summary>
        /// <para>Can be used to store any additional information about the Peer.</para>
        /// <para>For example, the username of the Peer.</para>
        /// </summary>
        /// <returns></returns>
        public string Tag { get; set; }

        public PeerAction Action { get; set; }

        protected internal TcpClient ReferenceClient;

        public PeerIdentification(string ID) {
            this.ID = ID;
            Action = PeerAction.RemoteIdentity;
        }

        public PeerIdentification(string ID, PeerAction Action) {
            this.ID = ID;
            this.Action = Action;
        }

        public PeerIdentification() {

        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <returns></returns>
        public static TcpServer StartServer(PeerIdentification RemotePeer, TcpClient Client, string Name = "TcpServer") {
            if (!Client.PeerToPeerEnabled)
                throw new Exception("P2P is not enabled.");
            return Client.StartServer(RemotePeer, null, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <param name="Protocol">Serialization Protocol used for this connection.</param>
        /// <returns></returns>
        public static async Task<TcpServer> StartServer(PeerIdentification RemotePeer, TcpClient Client, ISerializer Protocol, string Name = "TcpServer") {
            if (!Client.PeerToPeerEnabled)
                throw new Exception("P2P is not enabled.");
            return Client.StartServer(RemotePeer, Protocol, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        public TcpServer StartServer(string Name = "TcpServer") {
            if (!ReferenceClient.PeerToPeerEnabled)
                throw new Exception("P2P is not enabled.");
            return ReferenceClient.StartServer(this, null, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Protocol">Serialization Protocol used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        public TcpServer P2P(ISerializer Protocol, string Name = "TcpServer") {
            if (!ReferenceClient.PeerToPeerEnabled)
                throw new Exception("P2P is not enabled.");
            return ReferenceClient.StartServer(this, Protocol, Name);
        }

        protected internal PeerIdentification WithReference(TcpClient TcpClient) {
            ReferenceClient = TcpClient;
            return this;
        }

        public static PeerIdentification Create(string ID) {
            return new PeerIdentification(ID);
        }

        public static PeerIdentification Create(Guid ID) {
            return Create(ID.ToString());
        }

        public static PeerIdentification Create(string ID, bool IsLocalIdentity, string IP = "") {
            return new PeerIdentification(ID) { Action = IsLocalIdentity ? PeerAction.LocalIdentity : PeerAction.RemoteIdentity, IP = IsLocalIdentity ? IP : string.Empty };
        }

        public static PeerIdentification Create(Guid ID, bool IsLocalIdentity, string IP = "") {
            return Create(ID.ToString(), IsLocalIdentity, IP);
        }

        public static PeerIdentification Create(ConnectedClient Client) {
            return Create(Client.ID);
        }

        public static PeerIdentification Create(ConnectedClient Client, bool IsLocalIdentity, string IP = "") {
            return Create(Client.ID, IsLocalIdentity, IP);
        }
    }

    [Serializable]
    public enum PeerAction {
        RemoteIdentity,
        Dispose,
        LocalIdentity
    }

}