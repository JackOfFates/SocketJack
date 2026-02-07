using System;

namespace SocketJack.Serialization.Json.Converters {
    // Transport type used by JsonConverters to avoid building a single gigantic base64 string.
    // This is not a true streaming protocol; it is a chunked representation inside JSON.
    internal sealed class ChunkedBinaryPayload {
        public string ContentType { get; set; } = "application/octet-stream";

        // Total decoded byte size (for validation).
        public int Length { get; set; }

        // Base64 chunks.
        public string[] Chunks { get; set; } = Array.Empty<string>();
    }
}
