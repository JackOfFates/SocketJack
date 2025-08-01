using System;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace SocketJack.Serialization.Json.Converters {
    public partial class TypeConverter : JsonConverter<Type> {

        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            string typeName = reader.GetString();
            return Type.GetType(typeName);
        }

        public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options) {
            string typeName = value.Namespace + "." + value.Name;
            writer.WriteStringValue(typeName);
        }

    }
}