using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SocketJack.Extensions {

    public static class ConcurrentDictionaryExtensions {

        public static List<Task> ValuesForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<T2> action) {
            return ForAll(Dict, keypair => action.Invoke(keypair.Value));
        }

        public static List<Task> KeysForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<T> action) {
            return ForAll(Dict, keypair => action.Invoke(keypair.Key));
        }

        public static List<Task> ForAll<T, T2>(this ConcurrentDictionary<T, T2> Dict, Action<System.Collections.Generic.KeyValuePair<T, T2>> action) {
            // Dict.AsParallel.ForAll(action)
            var Tasks = new List<Task>();
            lock (Dict) {
                foreach (var keyValuePair in Dict) {
                    var task = Task.Run(() => action.Invoke(keyValuePair));
                    Tasks.Add(task);
                }
            }
            return Tasks;
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