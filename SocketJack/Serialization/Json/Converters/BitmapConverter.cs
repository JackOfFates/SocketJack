#if WINDOWS
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Drawing;
using System.Drawing.Imaging;

namespace SocketJack.Serialization.Json.Converters {
    public partial class BitmapConverter : JsonConverter<Bitmap> {

        private const int DefaultChunkSize = 64 * 1024;
        private const int MaxBytes = 32 * 1024 * 1024;

        public long Quality { get; set; } = 90L;

        private ImageFormat _imageFormat = ImageFormat.Jpeg;
        public ImageFormat ImageFormat {
            get => _imageFormat;
            set {
                _imageFormat = value;
                imageCodecInfo = GetEncoderInfo(_imageFormat);
            }
        }

        private ImageCodecInfo imageCodecInfo;

        public BitmapConverter() {
            imageCodecInfo = GetEncoderInfo(_imageFormat);
        }

        public static Bitmap ImageFromBytes(Stream inputStream) {
            return Image.FromStream(inputStream) as Bitmap;
        }   

        private MemoryStream EncodeToStream(Bitmap image) {
            var ms = new MemoryStream();
            EncoderParameters encoderParameters = new EncoderParameters(1);
            EncoderParameter qualityParameter = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Quality);
            encoderParameters.Param[0] = qualityParameter;
            if (imageCodecInfo == null) {
                imageCodecInfo = GetEncoderInfo(_imageFormat);
            }
            image.Save(ms, imageCodecInfo, encoderParameters);
            ms.Position = 0;
            return ms;
        }

        private ImageCodecInfo GetEncoderInfo(ImageFormat format) {
            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
            foreach (ImageCodecInfo codec in codecs) {
                if (codec.FormatID == format.Guid) {
                    return codec;
                }
            }
            return null;
        }

        public override Bitmap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            // Legacy format: a single base64 string.
            if (reader.TokenType == JsonTokenType.String) {
                var base64 = reader.GetString();
                if (base64 == null)
                    return null;
                var legacyBytes = Convert.FromBase64String(base64);
                return ImageFromBytes(new MemoryStream(legacyBytes));
            }

            // New format: ChunkedBinaryPayload.
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected string or object token for Bitmap");

            var payload = System.Text.Json.JsonSerializer.Deserialize<ChunkedBinaryPayload>(ref reader, options);
            if (payload == null)
                return null;

            if (payload.Length < 0 || payload.Length > MaxBytes)
                throw new JsonException("Bitmap payload too large");

            // Decode into a single stream for System.Drawing.
            // This still buffers decoded bytes but avoids building one enormous base64 string.
            var bytes = new byte[payload.Length];
            var offset = 0;
            for (var i = 0; i < payload.Chunks.Length; i++) {
                var chunkText = payload.Chunks[i];
                if (string.IsNullOrEmpty(chunkText))
                    continue;
                var chunk = Convert.FromBase64String(chunkText);
                if (offset + chunk.Length > bytes.Length)
                    throw new JsonException("Invalid Bitmap payload length");
                Buffer.BlockCopy(chunk, 0, bytes, offset, chunk.Length);
                offset += chunk.Length;
            }
            if (offset != bytes.Length)
                throw new JsonException("Invalid Bitmap payload length");

            return ImageFromBytes(new MemoryStream(bytes));
        }

        public override void Write(Utf8JsonWriter writer, Bitmap value, JsonSerializerOptions options) {
            if (value == null) {
                writer.WriteNullValue();
                return;
            }

            using (var ms = EncodeToStream(value)) {
                if (ms.Length > MaxBytes)
                    throw new JsonException("Bitmap payload too large");

                var chunkCount = (int)((ms.Length + DefaultChunkSize - 1) / DefaultChunkSize);
                var chunks = new string[chunkCount];

                var buffer = new byte[DefaultChunkSize];
                for (var i = 0; i < chunkCount; i++) {
                    var read = ms.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                        break;
                    chunks[i] = Convert.ToBase64String(buffer, 0, read);
                }

                var payload = new ChunkedBinaryPayload {
                    ContentType = _imageFormat == ImageFormat.Png ? "image/png" : "image/jpeg",
                    Length = (int)ms.Length,
                    Chunks = chunks
                };

                System.Text.Json.JsonSerializer.Serialize(writer, payload, options);
            }
        }
    }
}
#endif