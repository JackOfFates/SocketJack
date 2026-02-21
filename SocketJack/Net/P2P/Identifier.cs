using SocketJack.Extensions;
using SocketJack.Net.WebSockets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Principal;
using System.Threading.Tasks;

namespace SocketJack.Net.P2P {
    public class Identifier {

        private static ConcurrentDictionary<string, ISocket> _identifiers = new ConcurrentDictionary<string, ISocket>();

        public string ID { get; set; }

        /// <summary>
        /// Only available when this instance is owner.
        /// </summary>
        /// <returns>Your remote IP.</returns>
        public string IP { get; set; }

        /// <summary>
        /// Metadata is used to store additional information about the connection.
        /// <para>Do not read/write this dictionary directly.</para>
        /// <para>Instead, use GetMetaData/SetMetaData.</para>
        /// <remarks>Only changable from server.</remarks>
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public ConcurrentDictionary<string, string> Metadata { get; set; } = new ConcurrentDictionary<string, string>();
        private ConcurrentDictionary<string, string> PrivateMetadata { get; set; } = new ConcurrentDictionary<string, string>();

        /// <summary>
        /// Set metadata key/value for this Peer.
        /// <para>WARNING: This information will be sent to all connected clients.</para>
        /// <para>Set the `Private` <see langword="bool"/> parameter to <see langword="true"/> for private server metadata.</para>
        /// </summary>
        /// <param name="server"></param>
        /// <param name="key"></param>
        /// <param name="value">Value of key;Removes value if equal to null or String.Empty</param>
        public void SetMetaData(TcpServer server, string key, string value, bool Private = false, bool broadcastUpdate = true) {
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

                if (broadcastUpdate) {
                    var update = server.Peers[ID].Clone<Identifier>();
                    update.Action = PeerAction.MetadataUpdate;
                    update.Metadata = metadata;
                    update.IP = string.Empty;
                    server.SendBroadcast(update);
                }
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
        public void SetMetaData(ISocket server, string key, string value, bool Private = false, bool broadcastUpdate = true) {
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

                if (broadcastUpdate) {
                    var update = server.Peers[ID].Clone<Identifier>();
                    update.Action = PeerAction.MetadataUpdate;
                    update.Metadata = metadata;
                    update.IP = string.Empty;
                    server.SendBroadcast(update);
                }
            }
        }

        /// <summary>
        /// Get metadata value by key for the connection.
        /// <paramref name="key">Metadata Key.</paramref>
        /// <returns>Value as string.</returns>
        /// </summary>
        public async Task<string> GetMetaData(string key, bool Private = false, bool WaitForValueIfNull = true) {
            var Key = key.ToLower();
            Identifier p = null;
            while (p == null) {
                
                await Task.Delay(50); // Wait 50ms before checking again
                if(Parent == null && WaitForValueIfNull) {
                    foreach(var client in ThreadManager.TcpClients.Values) {
                        if(client.RemoteIdentity == null) continue;
                        if (client.RemoteIdentity.ID == ID) {
                            Parent = client;
                            break;
                        }
                    }
                    if(Parent == null) {
                        foreach (var client in ThreadManager.TcpServers.Values) {
                            if (client.RemoteIdentity == null) continue;
                            if (client.RemoteIdentity.ID == ID) {
                                Parent = client;
                                break;
                            }
                        }
                        continue;
                    }
                } else if(Parent == null && !WaitForValueIfNull) {
                    foreach (var client in ThreadManager.TcpClients.Values) {
                        if (client.RemoteIdentity == null) continue;
                        if (client.RemoteIdentity.ID == ID) {
                            Parent = client;
                            break;
                        }
                    }
                    if (Parent == null) {
                        foreach (var client in ThreadManager.TcpServers.Values) {
                            if (client.RemoteIdentity == null) continue;
                            if (client.RemoteIdentity.ID == ID) {
                                Parent = client;
                                break;
                            }
                        }
                        return default;
                    }
                }
                p = Parent.Peers.Where(p => p.Value.ID == ID).FirstOrDefault().Value;
                if (!Parent.Connected && !(Parent is TcpServer)) return default;
            }
            if (Private ? p.PrivateMetadata.ContainsKey(Key) : p.Metadata.ContainsKey(Key)) {
                string value = default;
                    while (Private ? !p.PrivateMetadata.TryGetValue(Key, out value) : !p.Metadata.TryGetValue(Key, out value)) {
                    await Task.Delay(50); // Wait 50ms before checking again
                    p = Parent.Peers[ID];
                    if (!Parent.Connected) return default;
                }
                return value;
            } else if (WaitForValueIfNull) {
                // Wait asynchronously until the key is present in the dictionary
                while (Private ? !p.PrivateMetadata.ContainsKey(Key) : !p.Metadata.ContainsKey(Key)) {
                    await Task.Delay(50); // Wait 50ms before checking again
                  
                    if (Parent.Peers.ContainsKey(ID)) {
                        p = Parent.Peers[ID];
                    } else {
                        return default;
                    }
                    if (!Parent.Connected) return default;
                }
                string value = default;
                while (Private ? !p.PrivateMetadata.TryGetValue(Key, out value) : !p.Metadata.TryGetValue(Key, out value)) {
                    await Task.Delay(50); // Wait 50ms before checking again
                    if (!Parent.Connected) return default;
                }
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
        public async Task<T> GetMetaData<T>(string key, bool Private = false) {
            string value = await GetMetaData(key, Private);
            if (string.IsNullOrEmpty(value)) {
                return default(T);
            } else {
                return System.Text.Json.JsonSerializer.Deserialize<T>(value);
            }
        }

        public PeerAction Action { get; set; }

        protected internal ISocket Parent {
            get {
                if(_Parent == null) {
                    if (_identifiers.ContainsKey(ID)) {
                        _Parent = _identifiers[ID];
                    } else {
                        var parent = null as ISocket;
                        foreach (var client in ThreadManager.TcpClients.Values) {
                            if(client.RemoteIdentity == null) continue;
                            if (client.RemoteIdentity.ID == ID) {
                                parent = client;
                            }
                        }
                        if(parent == null) {
                            foreach (var server in ThreadManager.TcpServers.Values) {
                                if (server.GetType() == typeof(TcpServer)) {
                                    if (server.AsTcpServer().Clients.ContainsKey(Guid.Parse(ID))) {
                                        parent = server;
                                        break;
                                    }
                                } else if (server.GetType() == typeof(WebSocketServer)) {
                                    if (server.AsWsServer().Clients.ContainsKey(Guid.Parse(ID))) {
                                        parent = server;
                                        break;
                                    }
                                }

                            }
                        }

                        //if(parent == null) {
                        //    parent = ThreadManager.TcpServers.Values.FirstOrDefault(server => server.AsTcpServer().Clients.ContainsKey(Guid.Parse(ID)));
                        //    if (parent == null) {
                        //        throw new Exception("No parent found for this Identifier.");
                        //    }
                        //}
                        _Parent = parent;
                        _identifiers.Add(ID, parent);
                        return _Parent;
                    }
                }

                return _Parent;
            }
            set {
                _Parent = value;
            }
        }
        private ISocket _Parent;

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

        public void Send(object Obj) {
            Parent.Send(this, Obj);
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
        protected internal async static Task<ISocket> StartServer(Identifier RemotePeer, TcpClient Client, NetworkOptions Options, string Name = "TcpServer") {
            return await Client.StartServer(RemotePeer, Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns></returns>
        public async Task<ISocket> StartServer(string Name = "TcpServer") {
            return await Parent.StartServer(this, Parent.Options, Name);
        }

        /// <summary>
        /// Start a connection with this Remote Client.
        /// </summary>
        /// <param name="Serializer">Serializer used for this connection.</param>
        /// <param name="Name">Name of the TcpServer (Used for logging)</param>
        /// <returns>new TcpServer</returns>
        public async Task<ISocket> StartServer(NetworkOptions Options, string Name = "TcpServer") {
            return await Parent.StartServer(this, Options, Name);
        }

        public Identifier WithParent(ISocket reference) {
            Parent = reference;
            return this;
        }

        public Identifier RemoteReady(ISocket reference) {
            Parent = reference;
            IP = string.Empty;
            Action = PeerAction.RemoteIdentity;
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

        public static Identifier Create(NetworkConnection Client) {
            return Create(Client.ID);
        }

        public static Identifier Create(NetworkConnection Client, bool IsLocalIdentity, string IP = "") {
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