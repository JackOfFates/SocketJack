using System;
using System.Linq;

namespace SocketJack.Extensions {
    public static class ArrayExtensions {

        public static T[] Add<T>(this T[] arr, T item) {
            Array.Resize(ref arr, arr.Length + 1);
            arr[arr.Length - 1] = item;
            return arr;
        }

        public static T[] AddRange<T>(this T[] arr, T[] item) {
            if (arr is null)
                arr = (T[])item.Clone();
            for (int i = 0, loopTo = item.Length - 1; i <= loopTo; i++)
                arr = Add(arr, item[i]);
            return arr;
        }

        public static T[] Remove<T>(this T[] arr, T item) {
            for (int i = 0, loopTo = arr.Length - 1; i <= loopTo; i++) {
                if (arr[i].Equals(item)) {
                    arr = RemoveAt(arr, i);
                    break;
                }
            }
            return arr;
        }

        public static T[] RemoveAll<T>(this T[] arr, T item) {
            for (int i = 0, loopTo = arr.Length - 1; i <= loopTo; i++) {
                if (arr[i].Equals(item)) {
                    arr = RemoveAt(arr, i);
                }
            }
            return arr;
        }

        public static T[] RemoveAt<T>(this T[] arr, int index) {
            int uBound = arr.GetUpperBound(0);
            int lBound = arr.GetLowerBound(0);
            int arrLen = uBound - lBound;

            if (index < lBound || index > uBound) {
                throw new ArgumentOutOfRangeException(string.Format("Index must be from {0} to {1}.", lBound, uBound));
            } else {
                var outArr = new T[arrLen];
                Array.Copy(arr, 0, outArr, 0, index);
                Array.Copy(arr, index + 1, outArr, index, uBound - index);
                return outArr;
            }
        }

        public static T[] InsertAt<T>(this T[] arr, T item, int index) {
            int uBound = arr.GetUpperBound(0);
            int lBound = arr.GetLowerBound(0);
            int arrLen = uBound - lBound;

            if (index < lBound || index > uBound) {
                throw new ArgumentOutOfRangeException(string.Format("Index must be from {0} to {1}.", lBound, uBound));
            } else {
                var outArr = new T[arrLen + 1 + 1];
                Array.Copy(arr, 0, outArr, 0, index);
                Add(outArr, item);
                Array.Copy(arr, index + 1, outArr, index, uBound - index);
                return outArr;
            }
        }
    }
}