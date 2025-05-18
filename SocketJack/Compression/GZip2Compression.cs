using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Text;

namespace SocketJack.Compression
{
    public class GZip2Compression : ICompression {

        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Optimal;

        public GZip2Compression() { }
        public GZip2Compression(CompressionLevel CompressionLevel) {
            this.CompressionLevel = CompressionLevel;
        }

        public byte[] Compress(byte[] data) {
            if (data == null || data.Length == 0)
                return data;

            using (var output = new MemoryStream()) {
                using (var gzip = new GZipStream(output, CompressionLevel, leaveOpen: true)) {
                    gzip.Write(data, 0, data.Length);
                }
                return output.ToArray();
            }
        }
        public byte[] Decompress(byte[] compressedData) {
            if (compressedData == null || compressedData.Length == 0)
                return compressedData;

            using (var input = new MemoryStream(compressedData))
            using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream()) {
                gzip.CopyTo(output);
                return output.ToArray();
            }
        }
    }
}
