using SocketJack;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;
using SocketJack.Serialization.Json.Converters;
using System;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json {
    public class JsonSerializer : ISerializer {

        public JsonSerializer() {
            JsonOptions.Converters.Add(new JsonTypeConverter());
        }

        public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions() { DefaultBufferSize = 1048576 };

        public byte[] Serialize(object Obj) {
            string Json = System.Text.Json.JsonSerializer.Serialize(Obj, JsonOptions);
            //if (Obj.GetType() == typeof(Wrapper)) {
            //    Wrapper wrapper = (Wrapper)Obj;
            //    if (wrapper.Type != "SocketJack.Networking.Shared.PingObject, SocketJack, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null") {
            //        Json = Json;
            //    }
            //}
                
            return Encoding.UTF8.GetBytes(Json);
        }

        public Wrapper Deserialize(byte[] bytes) {
            try {
                return (Wrapper)System.Text.Json.JsonSerializer.Deserialize(Encoding.UTF8.GetString(bytes), typeof(Wrapper), JsonOptions);
            } catch (Exception ex) {
                string txt = UTF8Encoding.UTF8.GetString(bytes);
                ex = ex;
                return null;
            }
        }

        public object GetPropertyValue(PropertyValueArgs e) {
            if (e == null|| e.Value is null)
                return null;
            JsonElement jsonElement = (JsonElement)e.Value;
            if (jsonElement.TryGetProperty(e.Name, out jsonElement)) {
                return GetValue(jsonElement, e.Reference.Info.PropertyType, true);
            }

            return default;
        }

        private JsonElement ParseBytes(byte[] bytes) {
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes)).RootElement;
        }

        public object GetValue(object source, Type T, bool Parse) {
            JsonElement jsonElement = Parse && source.GetType() == typeof(byte[]) ? ParseBytes((byte[])source) : (JsonElement)source;
            switch (jsonElement.ValueKind) {
                case JsonValueKind.Null: {
                        return null;
                    }
                case JsonValueKind.String: {
                        if (T == typeof(DateTime)) {
                            return jsonElement.GetDateTime();
                        } else if (T == typeof(Guid)) {
                            return jsonElement.GetGuid();
                        } else {
                            return jsonElement.GetString();
                        }
                    }
                case JsonValueKind.Number: {
                        if (T.IsEnum) {
                            // Idk why i was jumping through hoops here, maybe it was for a reason.
                            // string PropertyTypeName = e.Reference.Info.PropertyType.FullName;
                            // var enumType = e.Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                            return Enum.ToObject(T, jsonElement.GetInt32());
                        } else {
                            return GetJsonNumericValue(jsonElement, T);
                        }
                    }
                case JsonValueKind.True:
                case JsonValueKind.False: {
                        return jsonElement.GetBoolean();
                    }
                case JsonValueKind.Object: {
                        object obj = System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), typeof(Wrapper), JsonOptions);
                        Type objType = obj.GetType();
                        if (((Wrapper)obj).Type == null) {
                            return System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), T, JsonOptions);
                        } else {
                            return obj;
                        }
                    }
                case JsonValueKind.Array: {
                        return System.Text.Json.JsonSerializer.Deserialize(jsonElement.GetRawText(), T, JsonOptions);
                    }
            }
            return default;
        }

        private static object GetJsonNumericValue(JsonElement JsonObject, Type Type) {
            if (Type == typeof(int)) {
                return JsonObject.GetInt32();
            } else if (Type == typeof(long)) {
                return JsonObject.GetInt64();
            } else if (Type == typeof(double)) {
                return JsonObject.GetDouble();
            } else if (Type == typeof(decimal)) {
                return JsonObject.GetDecimal();
            } else if (Type == typeof(byte)) {
                return JsonObject.GetByte();
            } else if (Type == typeof(sbyte)) {
                return JsonObject.GetSByte();
            } else if (Type == typeof(short)) {
                return JsonObject.GetInt16();
            } else if (Type == typeof(float)) {
                return JsonObject.GetSingle();
            } else if (Type == typeof(ushort)) {
                return JsonObject.GetUInt16();
            } else if (Type == typeof(uint)) {
                return JsonObject.GetUInt32();
            } else if (Type == typeof(ulong)) {
                return JsonObject.GetUInt64();
            }

            return default;
        }
    }

}