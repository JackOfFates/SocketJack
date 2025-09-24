#if WINDOWS
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Drawing;
using System.Drawing.Imaging;

namespace SocketJack.Serialization.Json.Converters {
    public partial class BitmapConverter : JsonConverter<Bitmap> {

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

        public string ImageToBase64(Bitmap image) {
            return Convert.ToBase64String(ImageToBytes(image));
        }

        public byte[] ImageToBytes(Bitmap image) {
            using (var ms = new MemoryStream()) {
                EncoderParameters encoderParameters = new EncoderParameters(1);
                EncoderParameter qualityParameter = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Quality);
                encoderParameters.Param[0] = qualityParameter;
                if (imageCodecInfo == null) { imageCodecInfo = GetEncoderInfo(_imageFormat); }
                image.Save(ms, imageCodecInfo, encoderParameters);
                return ms.ToArray();
            }
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
            var base64 = reader.GetString();
            var bytes = Convert.FromBase64String(base64);
            return ImageFromBytes(new MemoryStream(bytes));
        }

        public override void Write(Utf8JsonWriter writer, Bitmap value, JsonSerializerOptions options) {
            if (value == null) {
                writer.WriteNullValue();
                return;
            }
            writer.WriteStringValue(ImageToBase64(value));
        }
    }
}
#endif