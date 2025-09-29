using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SocketJack.Net;

namespace SocketJack.Extensions {
    public static class ByteExtensions {

        public static Segment[] GetSegments(this byte[] Bytes) {
            int MTU = 4000;// NIC.MTU <= 0 ? 4096 : NIC.MTU;
            var Segments = new List<Segment>();

            double SegmentCountDbl = (double)((double)Bytes.Length / (double)MTU);
            int SegmentCount = (int)Math.Round(Math.Floor(SegmentCountDbl));
            bool AddExtra = SegmentCountDbl - SegmentCount > 0d;
            if (AddExtra)
                SegmentCount += 1;
            string ID = Guid.NewGuid().ToString().ToUpper();
            for (int i = 0, loopTo = SegmentCount - 1; i <= loopTo; i++) {
                int ByteIndex = i * MTU;
                int Length = ByteIndex + MTU > Bytes.Length ? Bytes.Length - ByteIndex : MTU;

                byte[] CroppedData = new byte[Length];
                Buffer.BlockCopy(Bytes, ByteIndex, CroppedData, 0, Length);
                var s = new Segment(ID, CroppedData, i + 1, SegmentCount);
                Segments.Add(s);
            }
            return Segments.ToArray();
        }

        public static Segment[] GetSegments<T>(this byte[] SerializedBytes) {
            return SerializedBytes.GetSegments();
        }

        public static byte[] Terminate(this byte[] Data) {
            return ByteExtensions.Concat(new[] { Data, TcpConnection.Terminator });
        }

        /// <summary>
        /// Remove bytes from source Array.
        /// </summary>
        /// <param name="byteArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns>New byte array with removed bytes between startIndex and length.</returns>
        public static byte[] Remove(this byte[] byteArray, int startIndex, int length) {
            if (startIndex < 0 || length < 0) {
                throw new ArgumentOutOfRangeException("Invalid start index or length.");
            } else if(startIndex + length >= byteArray.Length) {
                return null;
            }
            byte[] newArray = new byte[(byteArray.Length - length)];
            Array.Copy(byteArray, 0, newArray, 0, startIndex);
            Array.Copy(byteArray, startIndex + length, newArray, startIndex, byteArray.Length - (startIndex + length));
            return newArray;
        }

        /// <summary>
        /// Remove bytes from source Array.
        /// </summary>
        /// <param name="byteArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="length"></param>
        /// <returns>New byte array with removed bytes from startIndex to end.</returns>
        public static byte[] Remove(this byte[] byteArray, int startIndex) {
            int length = byteArray.Length - startIndex;
            if (startIndex < 0 || length < 0 || startIndex + length > byteArray.Length) {
                throw new ArgumentOutOfRangeException("Invalid start index or length.");
            }
            byte[] newArray = new byte[(byteArray.Length - length)];
            Array.Copy(byteArray, 0, newArray, 0, startIndex);
            Array.Copy(byteArray, startIndex + length, newArray, startIndex, byteArray.Length - (startIndex + length));
            return newArray;
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <returns>Byte array between startIndex to the end of the array.</returns>
        public static byte[] Part(this byte[] sourceArray, int startIndex) {
            return sourceArray.Part(startIndex, sourceArray.Length);
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <returns>Byte array between startIndex to the end of the array.</returns>
        public static byte[] Part(this List<byte> sourceArray, int startIndex) {
            return sourceArray.Part(startIndex, sourceArray.Count);
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="Length"></param>
        /// <returns>Byte array From startIndex to Length.</returns>
        public static byte[] Part(this byte[] sourceArray, int startIndex, int endIndex) {
            int newLength = endIndex - startIndex;
            byte[] Bytes = new byte[newLength];
            Buffer.BlockCopy(sourceArray, startIndex, Bytes, 0, newLength);
            return Bytes;
        }

        /// <summary>
        /// Byte Array equivalent of Substring.
        /// </summary>
        /// <param name="SourceArray"></param>
        /// <param name="startIndex"></param>
        /// <param name="Length"></param>
        /// <returns>Byte array From startIndex to Length.</returns>
        public static byte[] Part(this List<byte> sourceArray, int startIndex, int endIndex) {
            int newLength = endIndex - startIndex;
            byte[] Bytes = new byte[newLength];
            sourceArray.CopyTo(startIndex, Bytes, 0, newLength);
            //Buffer.BlockCopy(sourceArray, startIndex, Bytes, 0, newLength);
            return Bytes;
        }

        /// <summary>
        /// Searches for a byte array in the source array.
        /// </summary>
        /// <param name="sourceArray">Source byte array</param>
        /// <param name="byteArray">Search byte array</param>
        /// <returns></returns>
        public static int IndexOf(this List<byte> sourceArray, byte[] byteArray) {
            return sourceArray.IndexOf(byteArray, 0);
        }

        /// <summary>
        /// Searches for a byte array in the source array.
        /// </summary>
        /// <param name="sourceArray">Source byte array</param>
        /// <param name="byteArray">Search byte array</param>
        /// <returns></returns>
        public static int IndexOf(this byte[] sourceArray, byte[] byteArray) {
            return sourceArray.IndexOf(byteArray, 0);
        }

        /// <summary>
        /// <para>Searches for a byte in the source array.</para>
        /// <para>Array.IndexOf() Wrapper</para>
        /// </summary>
        /// <param name="sourceArray">Source byte</param>
        /// <param name="[byte]">Search byte</param>
        /// <returns></returns>
        public static int IndexOf(this byte[] sourceArray, byte @byte) {
            return Array.IndexOf(sourceArray, @byte);
        }

        public static int IndexOf(this List<byte> byteArray, byte[] subArray, int startIndex) {
            for (int i = startIndex, loopTo = byteArray.Count - subArray.Length; i <= loopTo; i++) {
                bool match = true;
                for (int j = 0, loopTo1 = subArray.Length - 1; j <= loopTo1; j++) {
                    if (byteArray[i + j] != subArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }

        public static int IndexOf(this byte[] byteArray, byte[] subArray, int startIndex) {
            for (int i = startIndex, loopTo = byteArray.Length - subArray.Length; i <= loopTo; i++) {
                bool match = true;
                for (int j = 0, loopTo1 = subArray.Length - 1; j <= loopTo1; j++) {
                    if (byteArray[i + j] != subArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    return i;
            }
            return -1;
        }


        public static List<int> IndexOfAll(this List<byte> sourceArray, byte[] searchArray) {
            return sourceArray.IndexOfAll(searchArray, 0);
        }

        public static List<int> IndexOfAll(this List<byte> sourceArray, byte[] searchArray, int StartIndex) {
            if (sourceArray == null || searchArray is null)
                throw new ArgumentNullException("Source or subarray cannot be null.");
            if (searchArray.Length == 0 || searchArray.Length > sourceArray.Count)
                return new List<int>();

            int range = sourceArray.Count - searchArray.Length + 1;
            int processorCount = Environment.ProcessorCount;
            int chunkSize = Math.Max(range / processorCount, 1);

            var results = new ConcurrentBag<int>();
            var tasks = new List<Task>();

            for (int t = 0; t < processorCount; t++) {
                int chunkStart = StartIndex + t * chunkSize;
                int chunkEnd = (t == processorCount - 1) ? range : chunkStart + chunkSize;
                if (chunkStart >= range) break;

                tasks.Add(Task.Run(() => {
                    for (int i = chunkStart; i < chunkEnd && i < range; i++) {
                        bool match = true;
                        for (int j = 0; j < searchArray.Length; j++) {
                            if (sourceArray[i + j] != searchArray[j]) {
                                match = false;
                                break;
                            }
                        }
                        if (match)
                            results.Add(i);
                    }
                }));
            }

            Task.WaitAll(tasks.ToArray());
            return results.OrderBy(index => index).ToList();
        }

        public static List<int> IndexOfAll(this byte[] sourceArray, byte[] searchArray) {
            return sourceArray.IndexOfAll(searchArray, 0);
        }

        public static List<int> IndexOfAll(this byte[] sourceArray, byte[] searchArray, int StartIndex) {
            if (sourceArray == null || searchArray is null)
                throw new ArgumentNullException("Source or subarray cannot be null.");
            if (searchArray.Length == 0 || searchArray.Length > sourceArray.Length)
                return new List<int>();

            var results = new ConcurrentBag<int>();

            Parallel.For(StartIndex, sourceArray.Length - searchArray.Length + 1, i => {
                bool match = true;
                for (int j = 0, loopTo = searchArray.Length - 1; j <= loopTo; j++) {
                    if (sourceArray[i + j] != searchArray[j]) {
                        match = false;
                        break;
                    }
                }
                if (match)
                    results.Add(i);
            });

            return results.OrderBy(index => index).ToList();
        }

        public static byte[] Concat(this byte[] A, byte[] B) {
            return Concat(new[] { A, B });
        }

        public static byte[] Concat(this byte[][] arrays) {
            return arrays.SelectMany(x => x).ToArray();
        }

        private const long OneKb = 1024L;
        private const long OneMb = OneKb * 1024L;
        private const long OneGb = OneMb * 1024L;
        private const long OneTb = OneGb * 1024L;

        public static string ByteToString(this int value, int decimalPlaces = 0) {
            return ((long)value).ByteToString(decimalPlaces);
        }

        public static string ByteToString(this long value, int decimalPlaces = 0) {
            string formatString = "{0:F" + decimalPlaces + "}"; // Format string for decimal places

            if (value >= OneTb) {
                return string.Format(formatString + " TB", value / (double)OneTb);
            } else if (value >= OneGb) {
                return string.Format(formatString + " GB", value / (double)OneGb);
            } else if (value >= OneMb) {
                return string.Format(formatString + " MB", value / (double)OneMb);
            } else if (value >= OneKb) {
                return string.Format(formatString + " KB", value / (double)OneKb);
            } else {
                return string.Format("{0} Bytes", value);
            }
        }
    }
}