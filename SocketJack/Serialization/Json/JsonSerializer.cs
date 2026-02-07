using SocketJack.Net;
using SocketJack.Net.P2P;
using SocketJack.Serialization.Json.Converters;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Serialization.Json {
    public class JsonSerializer : ISerializer {

        public event Action<Exception>? DeserializationError;

        public JsonSerializer() {
            JsonOptions.Converters.Add(new TypeConverter());
            JsonOptions.Converters.Add(new ByteArrayConverter());
#if NET6_0_OR_GREATER
            JsonOptions.Converters.Add(new BitmapConverter());
#endif
        }

        public JsonSerializerOptions JsonOptions { get; set; } = new JsonSerializerOptions() {
            DefaultBufferSize = 1048576,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            MaxDepth = 0,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        public bool HasConverter(Type type) {
            foreach (var converter in JsonOptions.Converters) {
                if (converter.CanConvert(type))
                    return true;
            }
            return false;
        }

        public byte[] Serialize(object Obj) {
            // JsonSerializerOptions contains mutable collections (e.g., Converters).
            // SocketJack can mutate these at runtime (whitelisting/callback registration),
            // which can race with System.Text.Json enumerating the options and throw
            // "Collection was modified". Snapshot options for thread-safe serialization.
            var opts = new JsonSerializerOptions(JsonOptions);
            string Json = System.Text.Json.JsonSerializer.Serialize(Obj, opts);
            return Encoding.UTF8.GetBytes(Json);
        }

        public Wrapper Deserialize(byte[] bytes) {
            try {
                string json = Encoding.UTF8.GetString(bytes);
                return (Wrapper)System.Text.Json.JsonSerializer.Deserialize(json, typeof(Wrapper), JsonOptions);
            } catch (Exception ex) {
                DeserializationError?.Invoke(ex);
                return null;
            }
        }

        public PeerRedirect DeserializeRedirect(ISocket Target, byte[] bytes) {
            string json = Encoding.UTF8.GetString(bytes);
            try {
                PeerRedirect redirect = (PeerRedirect)System.Text.Json.JsonSerializer.Deserialize(json, typeof(PeerRedirect), JsonOptions);
                Type T = Wrapper.ResolveTypeCached(redirect.Type);
                if (Target.Options.Serializer.GetType() == typeof(JsonSerializer)) {
                    JsonSerializer serializer = (JsonSerializer)Target.Options.Serializer;
                    if (serializer.HasConverter(T)) {
                        object obj = serializer.GetValue(redirect.Value, T, true);
                        if (obj.GetType() == typeof(string))
                            redirect.Value = System.Text.Json.JsonSerializer.Deserialize((JsonElement)redirect.Value, T, JsonOptions);
                        return redirect;
                    } else {
                        //var txt = ((JsonElement)redirect.value).GetRawText();
                        var js = (JsonElement)redirect.Value;
                        if (js.TryGetProperty("Type", out var t)) {
                            Type valueType = Wrapper.GetValueType(t.GetString());
                            redirect.Value = System.Text.Json.JsonSerializer.Deserialize(js.GetProperty("value").GetRawText(), valueType, JsonOptions);
                        } else {
                            redirect.Value = System.Text.Json.JsonSerializer.Deserialize(js.GetRawText(), T, JsonOptions);
                        }
                    }
                } else {
                    redirect.Value = ((Wrapper)System.Text.Json.JsonSerializer.Deserialize(redirect.Value.ToString(), typeof(Wrapper), JsonOptions)).Unwrap(Target);
                }
                return redirect;
            } catch (Exception ex) {
                DeserializationError?.Invoke(ex);
                return null;
            }
        }

        public object GetPropertyValue(PropertyValueArgs e) {
            if (e == null || e.Value is null)
                return null;
            JsonElement jsonElement = (JsonElement)e.Value;
            if (jsonElement.TryGetProperty(e.Name, out jsonElement)) {
                var v = GetValue(jsonElement, e.Reference.Info.PropertyType, true);
                return v;
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
                        } else if (T == typeof(byte[])) {
                            return jsonElement.GetBytesFromBase64();
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
                        string jsonTxt = jsonElement.GetRawText();
                        object obj = System.Text.Json.JsonSerializer.Deserialize(jsonTxt, typeof(Wrapper), JsonOptions);
                        Type objType = obj.GetType();
                        if (((Wrapper)obj).Type == null) {
#if !NETSTANDARD1_6_OR_GREATER
                            return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
                            //foreach (var converterType in ConverterElementTypes) {
                            //    if (converterType == T) {

                            //    }
                            //}
                            //return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
#else
                            return System.Text.Json.JsonSerializer.Deserialize(jsonTxt, T, JsonOptions);
#endif
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