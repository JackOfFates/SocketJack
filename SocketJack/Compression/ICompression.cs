using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Text;

namespace SocketJack.Compression
{
    public interface ICompression {
        /// <summary>
        /// Compresses the input data.
        /// </summary>
        /// <param name="data">The data to compress.</param>
        /// <returns>The compressed data.</returns>
        public byte[] Compress(byte[] data);
        /// <summary>
        /// Decompresses the input data.
        /// </summary>
        /// <param name="data">The data to decompress.</param>
        /// <returns>The decompressed data.</returns>
        public byte[] Decompress(byte[] data);

        /// <summary>
        /// Sets the compression level.
        /// </summary>
        public CompressionLevel CompressionLevel { get; set; }
    }
}
