using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;

namespace SocketJack.Net.Database {

    /// <summary>
    /// A stream adapter that wraps TLS handshake bytes inside TDS Pre-Login
    /// (0x12) packets during the TDS encryption handshake phase.
    /// <para>
    /// TLS ClientHello/ServerHello exchange is encapsulated in TDS packets
    /// of type 0x12 (Pre-Login). The <see cref="System.Net.Security.SslStream"/> sees a plain
    /// TLS byte stream while this adapter handles the TDS framing on the wire.
    /// </para>
    /// </summary>
    internal class TdsWrapperStream : Stream {

        private readonly Stream _inner;
        private byte[] _readBuffer;
        private int _readOffset;
        private int _readCount;

        // Pre-seed buffer: bytes that were already read from the NetworkStream
        // by ReceiveData and buffered in ProcessTdsBuffer.  These must be
        // consumed before reading from _inner so that the TLS handshake sees
        // data in the correct order.
        private byte[] _preseed;
        private int _preseedOffset;

        // Feed-buffer for post-handshake mode:
        // MutableTcpServer's ReceiveData loop owns the NetworkStream reads.
        // After the TLS handshake, raw TLS record bytes arriving via
        // ProcessReceive are pushed here so that SslStream.Read can
        // consume them without competing for the NetworkStream.
        private readonly ConcurrentQueue<byte[]> _feedQueue = new ConcurrentQueue<byte[]>();
        private readonly ManualResetEventSlim _feedReady = new ManualResetEventSlim(false);
        private byte[] _feedCurrent;
        private int _feedOffset;
        private volatile bool _disposed;

        /// <summary>
        /// When <see langword="true"/>, <see cref="Read"/> pulls from the
        /// internal feed buffer (populated by <see cref="Feed"/>) and
        /// <see cref="Write"/> delegates directly to the inner stream
        /// without TDS 0x12 framing.
        /// </summary>
        internal bool Passthrough { get; set; }

        /// <summary>
        /// When <see langword="true"/>, passthrough reads use the feed buffer
        /// (for MutableTcpServer mode where ReceiveData owns the NetworkStream).
        /// When <see langword="false"/>, passthrough reads go directly to the
        /// inner stream (for standalone DataServer mode).
        /// </summary>
        internal bool UseFeedBuffer { get; set; }

        public TdsWrapperStream(Stream innerStream) {
            _inner = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        }

        /// <summary>
        /// Pre-seeds the wrapper with bytes that were already read from the
        /// network by the ReceiveData loop.  These bytes are consumed by
        /// <see cref="Read"/> before falling back to the inner stream.
        /// </summary>
        internal void Preseed(byte[] data) {
            if (data == null || data.Length == 0) return;
            _preseed = data;
            _preseedOffset = 0;
        }

        /// <summary>
        /// Pushes raw bytes (TLS records) into the feed buffer so that
        /// <see cref="System.Net.Security.SslStream.Read"/> can decrypt them.  Called from
        /// <see cref="TdsProtocolHandler.ProcessReceive"/> when TLS is active.
        /// </summary>
        internal void Feed(byte[] data) {
            if (data == null || data.Length == 0) return;
            _feedQueue.Enqueue(data);
            _feedReady.Set();
        }

        /// <summary>
        /// Signals the feed buffer that no more data will arrive,
        /// unblocking any pending <see cref="Read"/> call.
        /// </summary>
        internal void FeedClose() {
            _disposed = true;
            _feedReady.Set();
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        /// <summary>
        /// Reads a TDS 0x12 packet from the inner stream, strips the 8-byte
        /// TDS header, and returns the payload (raw TLS data) to the caller
        /// (i.e. <see cref="System.Net.Security.SslStream"/>).
        /// <para>
        /// Per [MS-TDS] 2.2.6.5, TLS handshake bytes <i>SHOULD</i> be wrapped
        /// in TDS 0x12 packets, but some clients (notably Microsoft.Data.SqlClient
        /// with ENCRYPT_ON) send raw TLS records without TDS framing.  This
        /// method auto-detects the framing by peeking at the first byte: if it
        /// is <c>0x12</c> the data is TDS-wrapped; otherwise (TLS content types
        /// <c>0x14</c>–<c>0x17</c>) it is raw TLS and the wrapper switches to
        /// passthrough for the remainder of the handshake.
        /// </para>
        /// </summary>
        public override int Read(byte[] buffer, int offset, int count) {
            if (count <= 0) return 0;

            if (Passthrough) {
                return ReadPassthrough(buffer, offset, count);
            }

            // If we have leftover payload from a previous TDS packet, return that first.
            if (_readBuffer != null && _readOffset < _readCount) {
                int available = _readCount - _readOffset;
                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, toCopy);
                _readOffset += toCopy;
                if (_readOffset >= _readCount) {
                    _readBuffer = null;
                    _readOffset = 0;
                    _readCount = 0;
                }
                return toCopy;
            }

            // Peek at the first byte to determine whether the handshake data
            // is wrapped in TDS 0x12 packets or sent as raw TLS records.
            byte[] peek = ReadExact(_inner, 1);
            if (peek == null) return 0;

            if (peek[0] != 0x12) {
                // Not a TDS Pre-Login wrapper — the client is sending raw TLS.
                // Switch to passthrough so SslStream sees the wire bytes directly.
                // Return the peeked byte now; subsequent Read calls use ReadPassthrough.
                Passthrough = true;
                buffer[offset] = peek[0];
                if (count == 1) return 1;

                // Fill the rest via ReadPassthrough which correctly drains any
                // remaining preseed bytes before falling through to the inner stream.
                int more = ReadPassthrough(buffer, offset + 1, count - 1);
                return 1 + more;
            }

            // TDS-wrapped: read the remaining 7 bytes of the 8-byte TDS header.
            byte[] headerRest = ReadExact(_inner, 7);
            if (headerRest == null) return 0;

            // bytes 2-3 (headerRest[1]-headerRest[2]) = total packet length (big-endian, including header)
            ushort packetLen = (ushort)((headerRest[1] << 8) | headerRest[2]);
            int payloadLen = packetLen - 8;
            if (payloadLen <= 0) return 0;

            // Read the payload (raw TLS bytes).
            byte[] payload = ReadExact(_inner, payloadLen);
            if (payload == null) return 0;

            int toCopyNow = Math.Min(payloadLen, count);
            Buffer.BlockCopy(payload, 0, buffer, offset, toCopyNow);

            // Stash any remaining bytes for subsequent Read calls.
            if (toCopyNow < payloadLen) {
                _readBuffer = payload;
                _readOffset = toCopyNow;
                _readCount = payloadLen;
            }

            return toCopyNow;
        }

        /// <summary>
        /// Wraps raw TLS data in a TDS 0x12 packet and writes it to the inner stream.
        /// </summary>
        public override void Write(byte[] buffer, int offset, int count) {
            if (Passthrough) {
                _inner.Write(buffer, offset, count);
                return;
            }

            // Build TDS 0x12 header
            ushort totalLen = (ushort)(count + 8);
            byte[] packet = new byte[totalLen];
            packet[0] = 0x12; // Pre-Login packet type
            packet[1] = 0x01; // Status: EOM
            packet[2] = (byte)(totalLen >> 8);
            packet[3] = (byte)(totalLen & 0xFF);
            // bytes 4-7: SPID, PacketID, Window — all zero

            Buffer.BlockCopy(buffer, offset, packet, 8, count);
            _inner.Write(packet, 0, packet.Length);
            _inner.Flush();
        }

        public override void Flush() {
            _inner.Flush();
        }

        /// <summary>
        /// Passthrough read that forwards raw bytes from the underlying stream
        /// (or preseed buffer) to <see cref="System.Net.Security.SslStream"/> without any framing.
        /// <para>
        /// <see cref="System.Net.Security.SslStream"/> handles TLS record parsing internally.
        /// This method simply provides the raw wire bytes, draining the
        /// preseed buffer first, then reading from the inner stream.
        /// </para>
        /// </summary>
        private int ReadPassthrough(byte[] buffer, int offset, int count) {
            // Drain any leftover bytes from the non-passthrough _readBuffer.
            if (_readBuffer != null && _readOffset < _readCount) {
                int available = _readCount - _readOffset;
                int toCopy = Math.Min(available, count);
                Buffer.BlockCopy(_readBuffer, _readOffset, buffer, offset, toCopy);
                _readOffset += toCopy;
                if (_readOffset >= _readCount) {
                    _readBuffer = null;
                    _readOffset = 0;
                    _readCount = 0;
                }
                return toCopy;
            }

            // Drain any remaining pre-seeded bytes first.
            if (_preseed != null && _preseedOffset < _preseed.Length) {
                int avail = _preseed.Length - _preseedOffset;
                int toCopy = Math.Min(avail, count);
                Buffer.BlockCopy(_preseed, _preseedOffset, buffer, offset, toCopy);
                _preseedOffset += toCopy;
                if (_preseedOffset >= _preseed.Length)
                    _preseed = null;
                return toCopy;
            }

            try {
                if (UseFeedBuffer) {
                    return ReadFromFeed(buffer, offset, count);
                } else {
                    return _inner.Read(buffer, offset, count);
                }
            } catch (IOException ex) {
                System.Diagnostics.Debug.WriteLine($"[TdsWrapperStream] ReadPassthrough IOException: {ex.Message}");
                return 0;
            } catch (ObjectDisposedException ex) {
                System.Diagnostics.Debug.WriteLine($"[TdsWrapperStream] ReadPassthrough ObjectDisposedException: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Reads decrypted-ready bytes from the feed buffer.  Blocks until
        /// data is available or the stream is closed via <see cref="FeedClose"/>.
        /// </summary>
        private int ReadFromFeed(byte[] buffer, int offset, int count) {
            while (true) {
                // Drain current chunk first.
                if (_feedCurrent != null && _feedOffset < _feedCurrent.Length) {
                    int avail = _feedCurrent.Length - _feedOffset;
                    int toCopy = Math.Min(avail, count);
                    Buffer.BlockCopy(_feedCurrent, _feedOffset, buffer, offset, toCopy);
                    _feedOffset += toCopy;
                    if (_feedOffset >= _feedCurrent.Length)
                        _feedCurrent = null;
                    return toCopy;
                }

                // Try to dequeue the next chunk.
                if (_feedQueue.TryDequeue(out var chunk)) {
                    _feedCurrent = chunk;
                    _feedOffset = 0;
                    continue;
                }

                // Nothing available — wait.
                if (_disposed) return 0;
                _feedReady.Reset();
                // Double-check after reset to avoid missed signal.
                if (_feedQueue.TryDequeue(out chunk)) {
                    _feedCurrent = chunk;
                    _feedOffset = 0;
                    continue;
                }
                if (_disposed) return 0;
                _feedReady.Wait();
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        /// <summary>
        /// Reads exactly <paramref name="count"/> bytes, draining the
        /// pre-seed buffer first then falling back to <paramref name="stream"/>.
        /// Returns <see langword="null"/> if the stream ends prematurely.
        /// </summary>
        private byte[] ReadExact(Stream stream, int count) {
            byte[] buf = new byte[count];
            int totalRead = 0;

            // Drain pre-seeded bytes first.
            if (_preseed != null && _preseedOffset < _preseed.Length) {
                int avail = _preseed.Length - _preseedOffset;
                int toCopy = Math.Min(avail, count);
                Buffer.BlockCopy(_preseed, _preseedOffset, buf, 0, toCopy);
                _preseedOffset += toCopy;
                totalRead = toCopy;
                if (_preseedOffset >= _preseed.Length)
                    _preseed = null;
            }

            while (totalRead < count) {
                int read;
                try {
                    read = stream.Read(buf, totalRead, count - totalRead);
                } catch (IOException) {
                    return null;
                } catch (ObjectDisposedException) {
                    return null;
                }
                if (read <= 0) return null;
                totalRead += read;
            }
            return buf;
        }
    }
}
