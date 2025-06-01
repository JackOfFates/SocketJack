using System;
using System.Linq;
using System.Threading.Tasks;
using SocketJack;
using SocketJack.Management;
using SocketJack.Networking;
using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;

namespace SocketJack.Networking.P2P {
    public class PeerIdentification {

        public string ID { get; set; }

        /// <summary>
        /// Only available when this instance is your LocalIdentity.
        /// </summary>
        /// <returns>Your remote IP.</returns>
        public string IP { get; set; }

        /// <summary>
        /// <para>Used to store any additional information about the Peer.</para>
        /// <para>For example, the username of the Peer.</para>
        /// <para>CURRENTLY UNDER DEVELOPMENT. DOES NOT WORK.</para>
        /// </summary>
        /// <returns></returns>
        public string Tag { 
            get {
                return _Tag;
            }
        }
        protected internal string _Tag;

        protected internal void SetTag(TcpServer server, string Tag) {
            if (server != null) {
                server.SendBroadcast(Create(this));
                _Tag = Tag;
                server.Peers[ID]._Tag = Tag;
            }
        }

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
        /// Start a connection with a Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        protected internal static TcpServer StartServer(PeerIdentification RemotePeer, TcpClient Client, string Name = "TcpServer") {
            return Client.StartServer(RemotePeer, Client.Options, Name);
        }

        /// <summary>
        /// Start a connection with a Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <returns></returns>
        protected internal static TcpServer StartServer(PeerIdentification RemotePeer, TcpClient Client, TcpOptions Options, string Name = "TcpServer") {
            return Client.StartServer(RemotePeer, Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        public TcpServer StartServer(string Name = "TcpServer") {
            if (ReferenceClient == null) FindClient();
            return ReferenceClient.StartServer(this, ReferenceClient.Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        public TcpServer StartServer(TcpOptions Options, string Name = "TcpServer") {
            if( ReferenceClient == null) FindClient();
            return ReferenceClient.StartServer(this, Options, Name);
        }

        private void FindClient() {
            ThreadManager.TcpClients.Values.ToList().ForEach(client => {
                if (client.RemoteIdentity.ID == ID) {
                    ReferenceClient = client;
                }
            });
        }

        public PeerIdentification WithReference(TcpClient TcpClient) {
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

        public static PeerIdentification Create(PeerIdentification Identity) {
            return new PeerIdentification(Identity.ID) { Action = PeerAction.TagUpdate };
        }

        public static PeerIdentification Create(Guid ID, bool IsLocalIdentity, string IP = "") {
            return Create(ID.ToString(), IsLocalIdentity, IP);
        }

        public static PeerIdentification Create(TcpConnection Client) {
            return Create(Client.ID);
        }

        public static PeerIdentification Create(TcpConnection Client, bool IsLocalIdentity, string IP = "") {
            return Create(Client.ID, IsLocalIdentity, IP);
        }

    }

    public enum PeerAction {
        RemoteIdentity,
        Dispose,
        LocalIdentity,
        TagUpdate
    }

}