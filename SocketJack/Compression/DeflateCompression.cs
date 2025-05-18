using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace SocketJack.Compression
{
    public class DeflateCompression : ICompression {

        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

        public DeflateCompression() { }
        public DeflateCompression(CompressionLevel CompressionLevel) { 
            this.CompressionLevel = CompressionLevel;
        }

        public byte[] Compress(byte[] data) {
            if (data == null || data.Length == 0)
                return data;

            using (var output = new MemoryStream()) {
                using (var deflate = new DeflateStream(output, CompressionLevel, leaveOpen: true)) {
                    deflate.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }

        public byte[] Decompress(byte[] data) {
            if (data == null || data.Length == 0)
                return data;

            using (var input = new MemoryStream(data))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream()) {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
