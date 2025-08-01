﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SocketJack.Extensions;

namespace SocketJack.Net.P2P {
    public class PeerList : ConcurrentDictionary<string, Identifier> {

        public Identifier[] FindPeersByMetaData(string key, string value) {
            if (Parent.Options.UsePeerToPeer) {
                List<Identifier> vals = null;
                lock (Parent.Peers.Values) {
                    vals = Parent.Peers.Values.ToList();
                }
                var connections = vals.Where(Peer => Peer.Metadata.ContainsKey(key) && Peer.Metadata[key] == value);
                if (connections.Count() > 0)
                    return connections.ToArray();
            }
            return Array.Empty<Identifier>();
        }
        public Identifier FindPeerByMetaData(string key, string value) {
            if (Parent.Options.UsePeerToPeer) {
                List<Identifier> vals = null;
                lock (Parent.Peers.Values) {
                    vals = Parent.Peers.Values.ToList();
                }
                var peer = vals.Where(Peer => Peer.Metadata.ContainsKey(key) && Peer.Metadata[key] == value).FirstOrDefault();
                return peer;
            }
            return null;
        }

        public bool PeerIsHost(Identifier Peer) {
            if (Contains(Peer)) {
                return PeerIsHost(Peer.ID);
            } else {
                return false;
            }
        }
        public bool PeerIsHost(string ID) {
            lock (Parent.P2P_ServerInformation) {
                lock (Parent.Peers) {
                    if (Parent.Peers.ContainsKey(ID) && Parent.P2P_ServerInformation.ContainsKey(ID) && !Parent.P2P_ServerInformation[ID].Shutdown) {
                        return true;
                    } else {
                        return false;
                    }
                }
            }
        }
        public PeerServer GetPeerServerInfo(Identifier Peer) {
            if (PeerIsHost(Peer)) {
                lock (Parent.P2P_ServerInformation)
                    return Parent.P2P_ServerInformation[Peer.ID];
            } else {
                return null;
            }
        }
        public PeerServer GetPeerServerInfo(string ID) {
            lock (Parent.P2P_ServerInformation) {
                lock (Parent.Peers) {
                    if (Parent.Peers.ContainsKey(ID) && Parent.P2P_ServerInformation.ContainsKey(ID)) {
                        return Parent.P2P_ServerInformation[ID];
                    } else {
                        return null;
                    }
                }
            }
        }
        public bool PeerClientExists(string ID) {
            lock (Parent.Peers)
                return Parent.Peers.ContainsKey(ID) && Parent.P2P_Clients.ContainsKey(ID);
        }
        public TcpConnection GetPeerClient(Identifier Peer) {
            return GetPeerClient(Peer.ID);
        }
        public TcpConnection GetPeerClient(string ID) {
            if (PeerClientExists(ID)) {
                TcpConnection peerClient = (TcpConnection)Parent.P2P_Clients[ID];
                return peerClient == null || peerClient.Closed ? null : (TcpConnection)Parent.P2P_Clients[ID];
            } else {
                return null;
            }
        }

        public ISocket Parent { get; set; }
        public PeerList(ISocket Parent) {
            this.Parent = Parent;
        }
        public bool Add( Identifier identifier) {
            return ConcurrentDictionaryExtensions.Add(this, identifier.ID, identifier);
        }

        public bool Remove(Identifier identifier) {
            return ConcurrentDictionaryExtensions.Remove(this, identifier.ID);
        }

        public void AddOrUpdate(Identifier identifier) {
            ConcurrentDictionaryExtensions.AddOrUpdate(this, identifier.ID, identifier);
        }

        public bool Contains(Identifier identifier) {
            return ContainsKey(identifier.ID);
        }

        public void UpdateMetaData(Identifier p) {
            if (ContainsKey(p.ID))
                this[p.ID].Metadata = p.Metadata;
            else
                TryAdd(p.ID, p);
        }

    }
}
