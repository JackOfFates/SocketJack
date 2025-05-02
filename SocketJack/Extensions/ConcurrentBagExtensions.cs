using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SocketJack.Extensions {
    static class ConcurrentBagExtensions {

        public static void AddRange<T>(this ConcurrentBag<T> bag, IEnumerable<T> items) {
            if (bag is null)
                throw new ArgumentNullException(nameof(bag));
            if (items is null)
                throw new ArgumentNullException(nameof(items));

            foreach (var item in items)
                bag.Add(item);
        }
    }
}