using SocketJack;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;
using SocketJack.Serialization.Json.Converters;
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json {
    public class JsonSerializer : ISerializer {

        public JsonSerializer() {
            JsonOptions.Converters.Add(new JsonTypeConverter());
        }

        public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions() { DefaultBufferSize = 1048576 };

        public byte[] Serialize(object Obj) {
            string Json = System.Text.Json.JsonSerializer.Serialize(Obj, JsonOptions);
            return Encoding.UTF8.GetBytes(Json);
        }

        public ObjectWrapper Deserialize(byte[] bytes) {
            return (ObjectWrapper)System.Text.Json.JsonSerializer.Deserialize(Encoding.UTF8.GetString(bytes), typeof(ObjectWrapper), JsonOptions);
        }

        public object GetPropertyValue(PropertyValueArgs e) {
            if (e == null|| e.Value is null)
                return null;
            JsonElement jsonObject = (JsonElement)e.Value;
            if (jsonObject.TryGetProperty(e.Name, out jsonObject)) {
                switch (jsonObject.ValueKind) {
                    case JsonValueKind.Null: {
                            return null;
                        }
                    case JsonValueKind.String: {
                            if (ReferenceEquals(e.Reference.Info.PropertyType, typeof(DateTime))) {
                                return jsonObject.GetDateTime();
                            } else if (ReferenceEquals(e.Reference.Info.PropertyType, typeof(Guid))) {
                                return jsonObject.GetGuid();
                            } else {
                                return jsonObject.GetString();
                            }
                        }
                    case JsonValueKind.Number: {
                            var val = GetJsonNumericValue(jsonObject, e.Reference.Info.PropertyType);
                            if (e.Reference.Info.PropertyType.IsEnum) {
                                return GetEnumValue(jsonObject.GetInt32(), e);
                            } else {
                                return val;
                            }
                        }
                    case JsonValueKind.True:
                    case JsonValueKind.False: {
                            return jsonObject.GetBoolean();
                        }
                    case JsonValueKind.Object: {
                            var obj = (ObjectWrapper)System.Text.Json.JsonSerializer.Deserialize(jsonObject.GetRawText(), typeof(ObjectWrapper), JsonOptions);
                            if(obj.Type == null) {
                                return System.Text.Json.JsonSerializer.Deserialize(jsonObject.GetRawText(), e.Reference.Info.PropertyType, JsonOptions);
                            } else {
                                return obj;
                            }
                        }
                    case JsonValueKind.Array: {
                            return System.Text.Json.JsonSerializer.Deserialize(jsonObject.GetRawText(), e.Reference.Info.PropertyType, JsonOptions);
                        }
                }
            }

            return default;
        }

        private static object GetEnumValue(object Val, PropertyValueArgs e) {
            string PropertyTypeName = e.Reference.Info.PropertyType.FullName;
            var enumType = e.Reference.Info.Module.Assembly.GetType(PropertyTypeName);
            return Enum.ToObject(enumType, Val);
        }

        private static object GetJsonNumericValue(JsonElement JsonObject, Type Type) {
            if (ReferenceEquals(Type, typeof(int))) {
                return JsonObject.GetInt32();
            } else if (ReferenceEquals(Type, typeof(long))) {
                return JsonObject.GetInt64();
            } else if (ReferenceEquals(Type, typeof(double))) {
                return JsonObject.GetDouble();
            } else if (ReferenceEquals(Type, typeof(decimal))) {
                return JsonObject.GetDecimal();
            } else if (ReferenceEquals(Type, typeof(byte))) {
                return JsonObject.GetByte();
            } else if (ReferenceEquals(Type, typeof(sbyte))) {
                return JsonObject.GetSByte();
            } else if (ReferenceEquals(Type, typeof(short))) {
                return JsonObject.GetInt16();
            } else if (ReferenceEquals(Type, typeof(float))) {
                return JsonObject.GetSingle();
            } else if (ReferenceEquals(Type, typeof(ushort))) {
                return JsonObject.GetUInt16();
            } else if (ReferenceEquals(Type, typeof(uint))) {
                return JsonObject.GetUInt32();
            } else if (ReferenceEquals(Type, typeof(ulong))) {
                return JsonObject.GetUInt64();
            }

            return default;
        }
    }

}