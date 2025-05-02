using System;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace SocketJack.Serialization.Json.Converters {
    public partial class JsonTypeConverter : JsonConverter<Type> {

        public override Type Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            string assemblyQualifiedName = reader.GetString();
            return Type.GetType(assemblyQualifiedName);
        }

        public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options) {
            string assemblyQualifiedName = value.AssemblyQualifiedName;
            writer.WriteStringValue(assemblyQualifiedName);
        }

    }
}