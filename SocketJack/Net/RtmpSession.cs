
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SocketJack.Net {

    /// <summary>
    /// Manages a single RTMP connection — handshake, chunk-stream de-mux,
    /// AMF0 command processing, and media forwarding for OBS-style publishers.
    /// </summary>
    internal sealed class RtmpSession {

        #region State

        private enum HandshakeState { WaitC0C1, WaitC2, Done }

        private HandshakeState _hsState = HandshakeState.WaitC0C1;
        private readonly Stream _out;
        private readonly NetworkConnection _connection;
        private readonly object _lock = new object();

        // Receive buffer
        private byte[] _buf = new byte[8192];
        private int _bufLen;

        // RTMP chunk state
        private int _inChunkSize = 128;
        private int _outChunkSize = 4096;
        private readonly Dictionary<int, ChunkCtx> _chunkCtx = new Dictionary<int, ChunkCtx>();

        // Publish state
        private string _app;
        private string _streamKey;
        private bool _publishing;
        private UploadStream _upload;

        internal string App => _app;
        internal string StreamKey => _streamKey;
        internal bool IsPublishing => _publishing;

        /// <summary>Fired when a client sends a <c>publish</c> command. Args: app, streamKey.</summary>
        internal Action<string, string> OnPublishStart;

        /// <summary>Fired when media data arrives. Args: messageTypeId (8=audio, 9=video, 18=data), payload.</summary>
        internal Action<byte, byte[]> OnMediaData;

        /// <summary>Fired when the publish session ends.</summary>
        internal Action OnPublishStop;

        #endregion

        internal RtmpSession(NetworkConnection connection) {
            _connection = connection;
            _out = connection.Stream;
        }

        internal void AttachUploadStream(UploadStream upload) {
            _upload = upload;
        }

        /// <summary>
        /// Feed raw bytes received from the network into the session.
        /// Thread-safe — callers may invoke from the receive-event thread.
        /// </summary>
        internal void ProcessData(byte[] data) {
            if (data == null || data.Length == 0) return;
            lock (_lock) {
                EnsureCapacity(data.Length);
                Buffer.BlockCopy(data, 0, _buf, _bufLen, data.Length);
                _bufLen += data.Length;
                Drain();
            }
        }

        #region Buffer Helpers

        private void EnsureCapacity(int additional) {
            int needed = _bufLen + additional;
            if (needed <= _buf.Length) return;
            int newSize = _buf.Length;
            while (newSize < needed) newSize *= 2;
            var nb = new byte[newSize];
            if (_bufLen > 0) Buffer.BlockCopy(_buf, 0, nb, 0, _bufLen);
            _buf = nb;
        }

        private void Consume(int count) {
            _bufLen -= count;
            if (_bufLen > 0)
                Buffer.BlockCopy(_buf, count, _buf, 0, _bufLen);
        }

        #endregion

        #region Drain

        private void Drain() {
            while (true) {
                switch (_hsState) {
                    case HandshakeState.WaitC0C1:
                        if (!DoHandshakeC0C1()) return;
                        break;
                    case HandshakeState.WaitC2:
                        if (!DoHandshakeC2()) return;
                        break;
                    case HandshakeState.Done:
                        if (!ParseNextChunk()) return;
                        break;
                }
            }
        }

        #endregion

        #region Handshake

        private bool DoHandshakeC0C1() {
            // C0 (1 byte) + C1 (1536 bytes) = 1537
            if (_bufLen < 1537) return false;

            byte ver = _buf[0];
            if (ver != 0x03) return false;

            var c1 = new byte[1536];
            Buffer.BlockCopy(_buf, 1, c1, 0, 1536);
            Consume(1537);

            int ts = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() & 0x7FFFFFFF);

            // S0
            _out.WriteByte(0x03);

            // S1 (server timestamp + zero + random)
            var s1 = new byte[1536];
            new Random().NextBytes(s1);
            s1[0] = (byte)(ts >> 24); s1[1] = (byte)(ts >> 16);
            s1[2] = (byte)(ts >> 8);  s1[3] = (byte)ts;
            s1[4] = 0; s1[5] = 0; s1[6] = 0; s1[7] = 0;
            _out.Write(s1, 0, 1536);

            // S2 (echo C1 with server time in bytes 4-7)
            var s2 = new byte[1536];
            Buffer.BlockCopy(c1, 0, s2, 0, 1536);
            s2[4] = (byte)(ts >> 24); s2[5] = (byte)(ts >> 16);
            s2[6] = (byte)(ts >> 8);  s2[7] = (byte)ts;
            _out.Write(s2, 0, 1536);
            _out.Flush();

            _hsState = HandshakeState.WaitC2;
            return true;
        }

        private bool DoHandshakeC2() {
            // C2 = 1536 bytes
            if (_bufLen < 1536) return false;
            Consume(1536);
            _hsState = HandshakeState.Done;
            return true;
        }

        #endregion

        #region Chunk Stream Parsing

        private sealed class ChunkCtx {
            public int Timestamp;
            public int TimestampDelta;
            public int MsgLength;
            public byte MsgTypeId;
            public int MsgStreamId;
            public MemoryStream Pending;
        }

        private bool ParseNextChunk() {
            if (_bufLen < 1) return false;

            int pos = 0;
            byte first = _buf[pos++];
            int fmt = (first >> 6) & 3;
            int csid = first & 0x3F;

            if (csid == 0) {
                if (_bufLen < pos + 1) return false;
                csid = _buf[pos++] + 64;
            } else if (csid == 1) {
                if (_bufLen < pos + 2) return false;
                csid = _buf[pos + 1] * 256 + _buf[pos] + 64;
                pos += 2;
            }

            int mhSize = fmt == 0 ? 11 : fmt == 1 ? 7 : fmt == 2 ? 3 : 0;
            if (_bufLen < pos + mhSize) return false;

            if (!_chunkCtx.TryGetValue(csid, out var ctx)) {
                ctx = new ChunkCtx();
                _chunkCtx[csid] = ctx;
            }

            if (fmt <= 2) {
                int ts = (_buf[pos] << 16) | (_buf[pos + 1] << 8) | _buf[pos + 2];
                pos += 3;
                if (fmt == 0) ctx.Timestamp = ts; else ctx.TimestampDelta = ts;
            }
            if (fmt <= 1) {
                ctx.MsgLength = (_buf[pos] << 16) | (_buf[pos + 1] << 8) | _buf[pos + 2];
                pos += 3;
                ctx.MsgTypeId = _buf[pos++];
            }
            if (fmt == 0) {
                // Message Stream ID is little-endian in RTMP
                ctx.MsgStreamId = _buf[pos] | (_buf[pos + 1] << 8) | (_buf[pos + 2] << 16) | (_buf[pos + 3] << 24);
                pos += 4;
            }

            // Extended timestamp
            bool extTs = (fmt == 0 && ctx.Timestamp == 0xFFFFFF) || (fmt != 0 && ctx.TimestampDelta == 0xFFFFFF);
            if (extTs) {
                if (_bufLen < pos + 4) return false;
                int ext = (_buf[pos] << 24) | (_buf[pos + 1] << 16) | (_buf[pos + 2] << 8) | _buf[pos + 3];
                pos += 4;
                if (fmt == 0) ctx.Timestamp = ext; else ctx.TimestampDelta = ext;
            }

            // Start new message assembly when needed
            if (ctx.Pending == null || ctx.Pending.Length >= ctx.MsgLength)
                ctx.Pending = new MemoryStream();

            int remaining = ctx.MsgLength - (int)ctx.Pending.Length;
            int chunkData = Math.Min(remaining, _inChunkSize);
            if (_bufLen < pos + chunkData) return false;

            ctx.Pending.Write(_buf, pos, chunkData);
            pos += chunkData;
            Consume(pos);

            if (ctx.Pending.Length >= ctx.MsgLength) {
                var msgData = ctx.Pending.ToArray();
                ctx.Pending = null;
                // Compute absolute timestamp for this message
                int absTs = ctx.Timestamp;
                if (fmt != 0) absTs = ctx.Timestamp + ctx.TimestampDelta;
                ctx.Timestamp = absTs;
                HandleMessage(ctx.MsgTypeId, ctx.MsgStreamId, absTs, msgData);
            }

            return true;
        }

        #endregion

        #region Message Dispatch

        private void HandleMessage(byte typeId, int streamId, int timestamp, byte[] data) {
            switch (typeId) {
                case 1: // Set Chunk Size
                    if (data.Length >= 4)
                        _inChunkSize = ((data[0] & 0x7F) << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                    break;
                case 3: break; // Acknowledgement
                case 4: break; // User Control
                case 5: break; // Window Ack Size
                case 6: break; // Set Peer Bandwidth
                case 8: // Audio
                case 9: // Video
                    OnMediaData?.Invoke(typeId, data);
                    EnqueueMedia(typeId, timestamp, data);
                    break;
                case 18: // AMF0 Data (@setDataFrame, metadata)
                    OnMediaData?.Invoke(typeId, data);
                    EnqueueMedia(typeId, timestamp, data);
                    break;
                case 20: // AMF0 Command
                    HandleAmf0Command(data, streamId);
                    break;
            }
        }

        /// <summary>
        /// Enqueue format: [typeId:1][timestamp_ms big-endian:4][payload]
        /// </summary>
        private void EnqueueMedia(byte typeId, int timestamp, byte[] data) {
            if (_upload == null) return;
            var prefixed = new byte[5 + data.Length];
            prefixed[0] = typeId;
            prefixed[1] = (byte)(timestamp >> 24);
            prefixed[2] = (byte)(timestamp >> 16);
            prefixed[3] = (byte)(timestamp >> 8);
            prefixed[4] = (byte)timestamp;
            Buffer.BlockCopy(data, 0, prefixed, 5, data.Length);
            _upload.Enqueue(prefixed);
        }

        #endregion

        #region AMF0 Command Handling

        private void HandleAmf0Command(byte[] data, int streamId) {
            int pos = 0;
            var values = Amf0ReadAll(data, ref pos);
            if (values.Count == 0) return;

            string cmd = values[0] as string;
            if (cmd == null) return;

            double txId = values.Count > 1 && values[1] is double d ? d : 0;

            switch (cmd) {
                case "connect":
                    CmdConnect(txId, values);
                    break;
                case "releaseStream":
                case "FCPublish":
                    SendAmfResult(txId);
                    break;
                case "createStream":
                    CmdCreateStream(txId);
                    break;
                case "publish":
                    CmdPublish(txId, values);
                    break;
                case "FCUnpublish":
                case "deleteStream":
                    _publishing = false;
                    if (_upload != null) { _upload.Complete(); _upload = null; }
                    OnPublishStop?.Invoke();
                    break;
            }
        }

        private void CmdConnect(double txId, List<object> values) {
            if (values.Count > 2 && values[2] is Dictionary<string, object> p && p.TryGetValue("app", out var a))
                _app = a as string;

            // Window Acknowledgement Size = 2 500 000
            SendControl(5, BigEndian32(2500000));

            // Set Peer Bandwidth = 2 500 000, Dynamic
            var bw = new byte[5];
            var bwVal = BigEndian32(2500000);
            Buffer.BlockCopy(bwVal, 0, bw, 0, 4);
            bw[4] = 0x02;
            SendControl(6, bw);

            // Set Chunk Size
            SendControl(1, BigEndian32(_outChunkSize));

            // Stream Begin (User Control type 0, stream 0)
            var sb = new byte[6];
            sb[0] = 0; sb[1] = 0;
            sb[2] = 0; sb[3] = 0; sb[4] = 0; sb[5] = 0;
            SendControl(4, sb);

            // _result for connect
            using (var ms = new MemoryStream()) {
                Amf0WriteString(ms, "_result");
                Amf0WriteNumber(ms, txId);
                Amf0WriteObject(ms, new Dictionary<string, object> {
                    { "fmsVer", "FMS/3,5,7,7009" },
                    { "capabilities", 31.0 }
                });
                Amf0WriteObject(ms, new Dictionary<string, object> {
                    { "level", "status" },
                    { "code", "NetConnection.Connect.Success" },
                    { "description", "Connection succeeded." },
                    { "objectEncoding", 0.0 }
                });
                SendRtmpMessage(3, 0, 20, ms.ToArray());
            }
        }

        private void CmdCreateStream(double txId) {
            using (var ms = new MemoryStream()) {
                Amf0WriteString(ms, "_result");
                Amf0WriteNumber(ms, txId);
                Amf0WriteNull(ms);
                Amf0WriteNumber(ms, 1.0);
                SendRtmpMessage(3, 0, 20, ms.ToArray());
            }
        }

        private void CmdPublish(double txId, List<object> values) {
            if (values.Count > 3) _streamKey = values[3] as string;
            _publishing = true;

            // onStatus
            using (var ms = new MemoryStream()) {
                Amf0WriteString(ms, "onStatus");
                Amf0WriteNumber(ms, 0);
                Amf0WriteNull(ms);
                Amf0WriteObject(ms, new Dictionary<string, object> {
                    { "level", "status" },
                    { "code", "NetStream.Publish.Start" },
                    { "description", "Publishing" }
                });
                SendRtmpMessage(5, 1, 20, ms.ToArray());
            }

            OnPublishStart?.Invoke(_app, _streamKey);
        }

        private void SendAmfResult(double txId) {
            using (var ms = new MemoryStream()) {
                Amf0WriteString(ms, "_result");
                Amf0WriteNumber(ms, txId);
                Amf0WriteNull(ms);
                SendRtmpMessage(3, 0, 20, ms.ToArray());
            }
        }

        #endregion

        #region Sending Helpers

        private void SendControl(byte typeId, byte[] data) => SendRtmpMessage(2, 0, typeId, data);

        private void SendRtmpMessage(int csid, int streamId, byte typeId, byte[] data) {
            int off = 0;
            bool first = true;
            while (off < data.Length) {
                int sz = Math.Min(_outChunkSize, data.Length - off);
                using (var ms = new MemoryStream()) {
                    if (first) {
                        WriteBH(ms, 0, csid);
                        // Timestamp 0
                        ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0);
                        // Message length
                        ms.WriteByte((byte)(data.Length >> 16));
                        ms.WriteByte((byte)(data.Length >> 8));
                        ms.WriteByte((byte)data.Length);
                        // Message type
                        ms.WriteByte(typeId);
                        // Stream ID (little-endian)
                        ms.WriteByte((byte)streamId);
                        ms.WriteByte((byte)(streamId >> 8));
                        ms.WriteByte((byte)(streamId >> 16));
                        ms.WriteByte((byte)(streamId >> 24));
                        first = false;
                    } else {
                        WriteBH(ms, 3, csid);
                    }
                    ms.Write(data, off, sz);
                    var pkt = ms.ToArray();
                    _out.Write(pkt, 0, pkt.Length);
                }
                off += sz;
            }
            _out.Flush();
        }

        private static void WriteBH(MemoryStream ms, int fmt, int csid) {
            if (csid < 64) {
                ms.WriteByte((byte)((fmt << 6) | csid));
            } else if (csid < 320) {
                ms.WriteByte((byte)(fmt << 6));
                ms.WriteByte((byte)(csid - 64));
            } else {
                ms.WriteByte((byte)((fmt << 6) | 1));
                int v = csid - 64;
                ms.WriteByte((byte)(v & 0xFF));
                ms.WriteByte((byte)((v >> 8) & 0xFF));
            }
        }

        private static byte[] BigEndian32(int v) =>
            new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

        #endregion

        #region AMF0

        private static List<object> Amf0ReadAll(byte[] d, ref int p) {
            var r = new List<object>();
            while (p < d.Length) { try { r.Add(Amf0Read(d, ref p)); } catch { break; } }
            return r;
        }

        private static object Amf0Read(byte[] d, ref int p) {
            byte t = d[p++];
            switch (t) {
                case 0x00: return ReadDouble(d, ref p);
                case 0x01: return d[p++] != 0;
                case 0x02: return ReadShortString(d, ref p);
                case 0x03: return ReadObj(d, ref p);
                case 0x05: return null;
                case 0x06: return null;
                case 0x08: p += 4; return ReadObj(d, ref p); // ECMA Array
                case 0x0A:
                    int c = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; p += 4;
                    var a = new List<object>(); for (int i = 0; i < c; i++) a.Add(Amf0Read(d, ref p)); return a;
                case 0x0B: p += 10; return null; // Date
                case 0x0C:
                    int ll = (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3]; p += 4;
                    var ls = Encoding.UTF8.GetString(d, p, ll); p += ll; return ls;
                default: return null;
            }
        }

        private static double ReadDouble(byte[] d, ref int p) {
            var b = new byte[8]; Array.Copy(d, p, b, 0, 8);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            p += 8; return BitConverter.ToDouble(b, 0);
        }

        private static string ReadShortString(byte[] d, ref int p) {
            int l = (d[p] << 8) | d[p + 1]; p += 2;
            var s = Encoding.UTF8.GetString(d, p, l); p += l; return s;
        }

        private static Dictionary<string, object> ReadObj(byte[] d, ref int p) {
            var o = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (p < d.Length - 2) {
                int kl = (d[p] << 8) | d[p + 1]; p += 2;
                if (kl == 0 && p < d.Length && d[p] == 0x09) { p++; break; }
                var k = Encoding.UTF8.GetString(d, p, kl); p += kl;
                o[k] = Amf0Read(d, ref p);
            }
            return o;
        }

        internal static void Amf0WriteString(MemoryStream ms, string v) {
            ms.WriteByte(0x02);
            var b = Encoding.UTF8.GetBytes(v);
            ms.WriteByte((byte)(b.Length >> 8)); ms.WriteByte((byte)b.Length);
            ms.Write(b, 0, b.Length);
        }

        internal static void Amf0WriteNumber(MemoryStream ms, double v) {
            ms.WriteByte(0x00);
            var b = BitConverter.GetBytes(v);
            if (BitConverter.IsLittleEndian) Array.Reverse(b);
            ms.Write(b, 0, 8);
        }

        internal static void Amf0WriteNull(MemoryStream ms) { ms.WriteByte(0x05); }

        internal static void Amf0WriteObject(MemoryStream ms, Dictionary<string, object> o) {
            ms.WriteByte(0x03);
            foreach (var kv in o) {
                var kb = Encoding.UTF8.GetBytes(kv.Key);
                ms.WriteByte((byte)(kb.Length >> 8)); ms.WriteByte((byte)kb.Length);
                ms.Write(kb, 0, kb.Length);
                Amf0WriteVal(ms, kv.Value);
            }
            ms.WriteByte(0x00); ms.WriteByte(0x00); ms.WriteByte(0x09);
        }

        internal static void Amf0WriteVal(MemoryStream ms, object v) {
            if (v == null) { Amf0WriteNull(ms); return; }
            if (v is string s) { Amf0WriteString(ms, s); return; }
            if (v is double d) { Amf0WriteNumber(ms, d); return; }
            if (v is int i) { Amf0WriteNumber(ms, i); return; }
            if (v is bool b) { ms.WriteByte(0x01); ms.WriteByte(b ? (byte)1 : (byte)0); return; }
            if (v is Dictionary<string, object> o) { Amf0WriteObject(ms, o); return; }
            Amf0WriteNull(ms);
        }

        #endregion

        internal void Stop() {
            _publishing = false;
            if (_upload != null) { _upload.Complete(); _upload = null; }
        }
    }
}
