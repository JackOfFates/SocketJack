using System;
using System.IO;

namespace SocketJack.Net.Database {

    /// <summary>
    /// A read-only stream that reads from <paramref name="first"/> until it is
    /// exhausted, then seamlessly continues reading from <paramref name="second"/>.
    /// Used to prepend buffered leftover bytes (already consumed from the socket
    /// by ReceiveData) in front of the live NetworkStream so that the TDS read
    /// loop processes them in order without missing any data.
    /// </summary>
    internal class ConcatenatedStream : Stream {
        private readonly Stream _first;
        private readonly Stream _second;
        private bool _firstExhausted;

        public ConcatenatedStream(Stream first, Stream second) {
            _first = first ?? throw new ArgumentNullException(nameof(first));
            _second = second ?? throw new ArgumentNullException(nameof(second));
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) {
            if (!_firstExhausted) {
                int read = _first.Read(buffer, offset, count);
                if (read > 0) return read;
                _firstExhausted = true;
            }
            return _second.Read(buffer, offset, count);
        }
    }
}
