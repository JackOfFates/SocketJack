using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json.Converters {
    internal class ByteArrayConverter : JsonConverter<byte[]> {
        private const int DefaultChunkSize = 64 * 1024;
        private const int MaxBytes = 1024 * 1024 * 1024;

        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            // Legacy format: a single base64 string.
            if (reader.TokenType == JsonTokenType.String) {
                string base64 = reader.GetString();
                if (base64 == null)
                    return null;
                var data = Convert.FromBase64String(base64);
                if (data.Length > MaxBytes)
                    throw new JsonException("byte[] too large");
                return data;
            }

            // New format: ChunkedBinaryPayload.
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected string or object token for byte[]");

            var payload = System.Text.Json.JsonSerializer.Deserialize<ChunkedBinaryPayload>(ref reader, options);
            if (payload == null)
                return null;

            if (payload.Length < 0 || payload.Length > MaxBytes)
                throw new JsonException("byte[] too large");

            if (payload.Chunks == null || payload.Chunks.Length == 0)
                return Array.Empty<byte>();

            var result = new byte[payload.Length];
            var offset = 0;

            for (var i = 0; i < payload.Chunks.Length; i++) {
                var chunkText = payload.Chunks[i];
                if (string.IsNullOrEmpty(chunkText))
                    continue;

                var chunk = Convert.FromBase64String(chunkText);
                if (offset + chunk.Length > result.Length)
                    throw new JsonException("Invalid chunked payload length");

                Buffer.BlockCopy(chunk, 0, result, offset, chunk.Length);
                offset += chunk.Length;
            }

            if (offset != result.Length)
                throw new JsonException("Invalid chunked payload length");

            return result;
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) {
            if (value == null) {
                writer.WriteNullValue();
            } else {
                if (value.Length > MaxBytes)
                    throw new JsonException("byte[] too large");

                // For small payloads, keep legacy compact representation.
                if (value.Length <= DefaultChunkSize) {
                    writer.WriteStringValue(Convert.ToBase64String(value));
                    return;
                }

                var chunkCount = (value.Length + DefaultChunkSize - 1) / DefaultChunkSize;
                var chunks = new string[chunkCount];

                for (var i = 0; i < chunkCount; i++) {
                    var offset = i * DefaultChunkSize;
                    var len = Math.Min(DefaultChunkSize, value.Length - offset);
                    chunks[i] = Convert.ToBase64String(value, offset, len);
                }

                var payload = new ChunkedBinaryPayload {
                    Length = value.Length,
                    Chunks = chunks
                };

                System.Text.Json.JsonSerializer.Serialize(writer, payload, options);
            }
        }
    }
}