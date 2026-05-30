using SocketJackJsonSerializer = SocketJack.Serialization.Json.JsonSerializer;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Net.AgentBuilder {

    public static class AgentBuilderJson {
        private static readonly SocketJackJsonSerializer SocketJackSerializer = new SocketJackJsonSerializer();

        public static readonly JsonSerializerOptions Options = CreateOptions();

        private static JsonSerializerOptions CreateOptions() {
            var options = new JsonSerializerOptions(SocketJackSerializer.JsonOptions) {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                ReferenceHandler = ReferenceHandler.IgnoreCycles,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                MaxDepth = 16
            };
            return options;
        }

        public static string Serialize(object value, bool indented = false) {
            var options = indented ? new JsonSerializerOptions(Options) { WriteIndented = true } : Options;
            return JsonSerializer.Serialize(value, options);
        }

        public static T Deserialize<T>(string json) {
            if (string.IsNullOrWhiteSpace(json))
                return default;
            return JsonSerializer.Deserialize<T>(json, Options);
        }

        public static string SafeSerialize(object value, int maxChars = 50000) {
            if (value == null)
                return "null";

            try {
                string json = JsonSerializer.Serialize(value, value.GetType(), Options);
                if (json.Length <= maxChars)
                    return json;

                string preview = json.Substring(0, Math.Min(1000, json.Length));
                return JsonSerializer.Serialize(new {
                    skipped = false,
                    truncated = true,
                    type = value.GetType().FullName ?? value.GetType().Name,
                    preview,
                    totalLength = json.Length
                }, Options);
            } catch (Exception ex) {
                try {
                    return JsonSerializer.Serialize(new {
                        skipped = true,
                        reason = ex.GetType().Name,
                        message = ex.Message,
                        type = value.GetType().FullName ?? value.GetType().Name,
                        text = Truncate(value.ToString(), 500)
                    }, Options);
                } catch {
                    return "{\"skipped\":true}";
                }
            }
        }

        public static object ToPlainValue(JsonElement element) {
            switch (element.ValueKind) {
                case JsonValueKind.String:
                    return element.GetString();
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long l))
                        return l;
                    if (element.TryGetDecimal(out decimal dec))
                        return dec;
                    return element.GetDouble();
                case JsonValueKind.True:
                case JsonValueKind.False:
                    return element.GetBoolean();
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in element.EnumerateArray())
                        list.Add(ToPlainValue(item));
                    return list;
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty property in element.EnumerateObject())
                        dict[property.Name] = ToPlainValue(property.Value);
                    return dict;
                default:
                    return element.ToString();
            }
        }

        public static Dictionary<string, object> ReadInputDictionary(string json) {
            var inputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(json))
                return inputs;

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return inputs;

            JsonElement source = root;
            if (root.TryGetProperty("inputs", out JsonElement nested) && nested.ValueKind == JsonValueKind.Object)
                source = nested;

            foreach (JsonProperty property in source.EnumerateObject())
                inputs[property.Name] = ToPlainValue(property.Value);

            return inputs;
        }

        public static string Truncate(string value, int maxChars) {
            value ??= "";
            return value.Length <= maxChars ? value : value.Substring(0, maxChars) + "...";
        }

        public static string ToUtf8String(byte[] bytes) {
            return bytes == null || bytes.Length == 0 ? "" : Encoding.UTF8.GetString(bytes);
        }
    }
}
