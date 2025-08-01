//using System;
//using System.IO;
//using System.Text;
//using System.Text.Json;
//using System.Text.Json.Serialization;
//using System.Drawing;

//namespace SocketJack.Serialization.Json.Converters {
//    public partial class BitmapConverter : JsonConverter<Bitmap> {

//        public static Bitmap ImageFromBytes(Stream inputStream) {
//            // Automatically detects the image format (e.g., BMP, PNG, JPG)
//            var image = Image.Load<Rgba32>(inputStream);
//            return image;
//        }

//        public static byte[] ImageToBytes(Bitmap image) {
//            using (var ms = new MemoryStream()) {
//                image.Save(ms, new PngEncoder()); // or BmpEncoder(), JpegEncoder()
//                return ms.ToArray();
//            }
//        }

//        public override Type Read(ref Utf8JsonReader reader, Bitmap value, JsonSerializerOptions options) {
//            return ImageToBytes(value);
//        }

//        public override void Write(Utf8JsonWriter writer, Bitmap value, JsonSerializerOptions options) {
//            writer.WriteStringValue(assemblyQualifiedName);
//        }

//    }
//}