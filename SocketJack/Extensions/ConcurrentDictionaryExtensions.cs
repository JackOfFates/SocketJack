using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using SocketJack.Extensions;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Extensions {

    public static class ConcurrentDictionaryExtensions {

        public static List<Task> ValuesForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<T2> action) {
            return ForAll(Dict, keypair => action(keypair.Value));
        }

        public static List<Task> KeysForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<T> action) {
            return ForAll(Dict, keypair => action(keypair.Key));
        }

        public static List<Task> ForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<KeyValuePair<T, T2>> action) {
            var Tasks = new List<Task>();
            lock (Dict) {
                foreach (var keyValuePair in Dict) {
                    Tasks.Add(Task.Run(() => action(keyValuePair)));
                }
            }
            return Tasks;
        }

        public static PeerIdentification[] ToArrayWithLocal<T, T2>(this ConcurrentDictionary<T, T2> Dict, TcpConnection LocalClient) where T2 : PeerIdentification {
            var peers = Array.Empty<PeerIdentification>();
            foreach (var keyValuePair in Dict) {
                if (keyValuePair.Value is PeerIdentification peer) {
                    if(peer.ID == LocalClient.RemoteIdentity.ID) {
                        peer = PeerIdentification.Create(LocalClient.RemoteIdentity.ID, true, LocalClient.EndPoint.Address.ToString());
                    }
                    peers = peers.Add(peer);
                }
            }
            return peers;
        }

        /// <summary>
        /// Removes a key from a ConcurrentDictionary.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="T2"></typeparam>
        /// <param name="Dict"></param>
        /// <param name="Key"></param>
        /// <returns><see langword="true"/> if removed successfully; <see langword="false"/> if does not exist</returns>
        public static bool Remove<T, T2>(this ConcurrentDictionary<T, T2> Dict, object Key) {
            T2 value = default;
            Dict.TryGetValue((T)Key, out value);
            if (Dict.ContainsKey((T)Key))
                return Dict.Remove((T)Key, out value);
            return false;
        }

        /// <summary>
        /// Adds a key and value to a ConcurrentDictionary.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="T2"></typeparam>
        /// <param name="Dict"></param>
        /// <param name="Key"></param>
        /// <param name="Value"></param>
        /// <returns><see langword="true"/> if key does not already exist; <see langword="false"/> if it exists</returns>
        public static bool Add<T, T2>(this ConcurrentDictionary<T, T2> Dict, T Key, T2 Value) {
            return Dict.TryAdd(Key, Value);
        }

        /// <summary>
        /// Adds a key and value to a ConcurrentDictionary, or updates if it already exists.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <typeparam name="T2"></typeparam>
        /// <param name="Dict"></param>
        /// <param name="Key"></param>
        /// <param name="Value"></param>
        public static void AddOrUpdate<T, T2>(this ConcurrentDictionary<T, T2> Dict, T Key, T2 Value) {
            if (Dict.ContainsKey(Key)) {
                Dict[Key] = Value;
            } else {
                Add(Dict, Key, Value);
            }
        }
    }
}