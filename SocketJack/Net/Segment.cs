using System;
using System.Collections.Generic;
using SocketJack.Extensions;

namespace SocketJack {
    /// <summary>
    /// Segments add support transfering objects above the network interface card's maximum transmission unit.
    /// </summary>
    public class Segment {

        protected internal static Dictionary<string, List<Segment>> Cache = new Dictionary<string, List<Segment>>();

        protected internal static bool SegmentComplete(Segment segment) {
            if (Cache.ContainsKey(segment.SID)) {
                return Cache[segment.SID].Count == segment.Count;
            } else {
                return false;
            }
        }

        protected internal static byte[] Rebuild(Segment segment) {
            byte[] Combined = new byte[] { };
            for (int i = 0, loopTo = Cache[segment.SID].Count - 1; i <= loopTo; i++) {
                var s = Cache[segment.SID][i];
                byte[] Data = System.Text.Encoding.UTF8.GetBytes(s.Data);
                Combined = ByteExtensions.Concat(new[] { Combined, Data });
            }
            Cache.Remove(segment.SID);
            return Combined;
        }

        public Segment() {

        }

        public Segment(string SID, byte[] Data, int Index, int Count) {
            this.SID = SID;
            this.Data = System.Text.Encoding.UTF8.GetString(Data);
            this.Index = Index;
            this.Count = Count;
        }

        public string Data { get; set; }

        public long Index { get; set; } = 0L;

        public long Count { get; set; } = 0L;

        public string SID { get; set; }

    }
}