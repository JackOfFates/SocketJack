using SocketJack.Extensions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;

namespace SocketJack.Net.P2P {
    public class Identifier {

        public string ID { get; set; }

        /// <summary>
        /// Only available when this instance is owner.
        /// </summary>
        /// <returns>Your remote IP.</returns>
        public string IP { get; set; }

        /// <summary>
        /// Metadata is used to store additional information about the connection.
        /// <para>Do not update this dictionary directly.</para>
        /// <para>Instead, use SetMetaData.</para>
        /// <remarks>Only changable from server.</remarks>
        /// </summary>
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
        private Dictionary<string, string> PrivateMetadata { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Set metadata key/value for this Peer.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> for private server metadata.</para>
        /// </summary>
        /// <param name="server"></param>
        /// <param name="key"></param>
        /// <param name="value">Value of key;Removes value if equal to null or String.Empty</param>
        protected internal void SetMetaData(TcpServer server, string key, string value, bool Private = false) {
            if (server != null) {
                var metadata = Private ? PrivateMetadata : Metadata;
                if (string.IsNullOrEmpty(value)) {
                    if (metadata.ContainsKey(key))
                        metadata.Remove(key);
                } else {
                    if (metadata.ContainsKey(key)) {
                        metadata[key] = value;
                    } else {
                        metadata.Add(key, value);
                    }
                }

                var update = this.Clone<Identifier>();
                update.Action = PeerAction.MetadataUpdate;
                update.Metadata = metadata;
                update.IP = string.Empty;
                server.SendBroadcast(update);
            }
        }

        /// <summary>
        /// Set metadata key/value for this Peer.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> for private server metadata.</para>
        /// </summary>
        /// <param name="server"></param>
        /// <param name="key"></param>
        /// <param name="value">Value of key;Removes value if equal to null or String.Empty</param>
        protected internal void SetMetaData(WebSocketServer server, string key, string value, bool Private = false) {
            if (server != null) {
                var metadata = Private ? PrivateMetadata : Metadata;
                if (string.IsNullOrEmpty(value)) {
                    if (metadata.ContainsKey(key))
                        metadata.Remove(key);
                } else {
                    if (metadata.ContainsKey(key)) {
                        metadata[key] = value;
                    } else {
                        metadata.Add(key, value);
                    }
                }

                var update = this.Clone<Identifier>();
                update.Action = PeerAction.MetadataUpdate;
                update.Metadata = metadata;
                update.IP = string.Empty;
                server.SendBroadcast(update);
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection.
        /// <para>Can only be called from server.</para>
        /// <paramref name="key">Metadata Key.</paramref>
        /// <returns>Value as string.</returns>
        /// </summary>
        public string GetMetaData(string key, bool Private = false) {
            var metadata = Private ? PrivateMetadata : Metadata;
            if(metadata.ContainsKey(key)) {
                metadata.TryGetValue(key, out string value);
                return value;
            } else {
                return default;
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection and deserialize it using Json.
        /// </summary>
        /// <typeparam name="T">The type to deserialize to.</typeparam>
        /// <param name="key"></param>
        /// <returns>Value as T.</returns>
        public T GetMetaData<T>(string key, bool Private = false) {
            string value = GetMetaData(key, Private);
            if (string.IsNullOrEmpty(value)) {
                return default(T);
            } else {
                return System.Text.Json.JsonSerializer.Deserialize<T>(value);
            }
        }

        public PeerAction Action { get; set; }

        protected internal ISocket Parent;

        public Identifier(string ID) {
            this.ID = ID;
            Action = PeerAction.RemoteIdentity;
        }

        public Identifier(string ID, PeerAction Action) {
            this.ID = ID;
            this.Action = Action;
        }

        public Identifier() {

        }

        /// <summary>
        /// Start a connection with a Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        protected async internal static Task<ISocket> StartServer(Identifier RemotePeer, TcpClient Client, string Name = "TcpServer") {
            return await Client.StartServer(RemotePeer, Client.Options, Name);
        }

        /// <summary>
        /// Start a connection with a Remote Client.
        /// </summary>
        /// <param name="RemotePeer">PeerIdentity to request a connection with.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <param name="Client">TcpClient associated with the RemotePeer.</param>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <returns></returns>
        protected internal async static Task<ISocket> StartServer(Identifier RemotePeer, TcpClient Client, TcpOptions Options, string Name = "TcpServer") {
            return await Client.StartServer(RemotePeer, Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        public async Task<ISocket> StartServer(string Name = "TcpServer") {
            if (Parent == null) FindClient();
            return await Parent.StartServer(this, Parent.Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        public async Task<ISocket> StartServer(TcpOptions Options, string Name = "TcpServer") {
            if( Parent == null) FindClient();
            return await Parent.StartServer(this, Options, Name);
        }

        private void FindClient() {
            if (Parent == null) {
                ThreadManager.TcpClients.Values.ToList().ForEach(client => {
                    if (client.RemoteIdentity.ID == ID) {
                        Parent = client;
                    }
                });
            }
        }

        public Identifier WithParent(ISocket reference) {
            Parent = reference;
            return this;
        }

        public static Identifier Create(string ID) {
            return new Identifier(ID);
        }

        public static Identifier Create(Guid ID) {
            return Create(ID.ToString());
        }

        public static Identifier Create(string ID, bool IsLocalIdentity, string IP = "") {
            return new Identifier(ID) { Action = IsLocalIdentity ? PeerAction.LocalIdentity : PeerAction.RemoteIdentity, IP = IsLocalIdentity ? IP : string.Empty };
        }

        public static Identifier Create(Guid ID, bool IsLocalIdentity, string IP = "") {
            return Create(ID.ToString(), IsLocalIdentity, IsLocalIdentity ? IP : string.Empty);
        }

        public static Identifier Create(TcpConnection Client) {
            return Create(Client.ID);
        }

        public static Identifier Create(TcpConnection Client, bool IsLocalIdentity, string IP = "") {
            return Create(Client.ID, IsLocalIdentity, IsLocalIdentity ? IP : string.Empty);
        }

    }

    public enum PeerAction {
        RemoteIdentity,
        Dispose,
        LocalIdentity,
        MetadataUpdate,
    }

}