﻿using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json.Converters {
    internal class ByteArrayConverter : JsonConverter<byte[]> {
        public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected string token for byte[]");
            string base64 = reader.GetString();
            return base64 == null ? null : Convert.FromBase64String(base64);
        }

        public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options) {
            if (value == null) {
                writer.WriteNullValue();
            } else {
                writer.WriteStringValue(Convert.ToBase64String(value));
            }
        }
    }
}