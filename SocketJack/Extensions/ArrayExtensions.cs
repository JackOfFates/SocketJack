using System;
using System.Linq;

namespace SocketJack.Extensions {
    public static class ArrayExtensions {

        public static void Add<T>(ref T[] arr, T item) {
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = item;
        }

        public static void AddRange<T>(ref T[] arr, T[] item) {
            if (arr is null)
                arr = (T[])item.Clone();
            for (int i = 0, loopTo = item.Length - 1; i <= loopTo; i++)
                Add(ref arr, item[i]);
        }

        public static void Remove<T>(ref T[] arr, T item) {
            for (int i = 0, loopTo = arr.Length - 1; i <= loopTo; i++) {
                if (arr[i].Equals(item)) {
                    RemoveAt(ref arr, i);
                    break;
                }
            }
        }

        public static void RemoveAll<T>(ref T[] arr, T item) {
            for (int i = 0, loopTo = arr.Length - 1; i <= loopTo; i++) {
                if (arr[i].Equals(item)) {
                    RemoveAt(ref arr, i);
                }
            }
        }

        public static void RemoveAt<T>(ref T[] arr, int index) {
            int uBound = arr.GetUpperBound(0);
            int lBound = arr.GetLowerBound(0);
            int arrLen = uBound - lBound;

            if (index < lBound || index > uBound) {
                throw new ArgumentOutOfRangeException(string.Format("Index must be from {0} to {1}.", lBound, uBound));
            } else {
                var outArr = new T[arrLen];
                Array.Copy(arr, 0, outArr, 0, index);
                Array.Copy(arr, index + 1, outArr, index, uBound - index);
                arr = outArr;
            }
        }

        public static void InsertAt<T>(ref T[] arr, T item, int index) {
            int uBound = arr.GetUpperBound(0);
            int lBound = arr.GetLowerBound(0);
            int arrLen = uBound - lBound;

            if (index < lBound || index > uBound) {
                throw new ArgumentOutOfRangeException(string.Format("Index must be from {0} to {1}.", lBound, uBound));
            } else {
                var outArr = new T[arrLen + 1 + 1];
                Array.Copy(arr, 0, outArr, 0, index);
                Add(ref outArr, item);
                Array.Copy(arr, index + 1, outArr, index, uBound - index);
                arr = outArr;
            }
        }
    }
}