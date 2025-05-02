using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SocketJack.Serialization;

namespace SocketJack.NewtonsoftJson {

    /// <summary>
    /// The Newtonsoft Json Serializer.
    /// </summary>
    public class NewtonsoftJsonSerializer : ISerializer {

        /// <summary>
        /// Gets or sets the JSON serializer settings.
        /// </summary>
        public static JsonSerializerSettings JsonSettings { get; set; } = new JsonSerializerSettings();

        /// <summary>
        /// Initializes a new instance of the <see cref="NewtonsoftJsonSerializer"/> class.
        /// </summary>
        public NewtonsoftJsonSerializer() {

        }

        /// <summary>
        /// Serializes the object to a byte array.
        /// </summary>
        /// <param name="obj"></param>
        /// <returns></returns>
        public byte[] Serialize(object obj) {
            string JsonString = JsonConvert.SerializeObject(obj, JsonSettings);
            return Encoding.UTF8.GetBytes(JsonString);
        }

        /// <summary>
        /// Deserializes the byte array to an ObjectWrapper.
        /// </summary>
        /// <param name="Data"></param>
        /// <returns></returns>
        public ObjectWrapper Deserialize(byte[] Data) {
            return JsonConvert.DeserializeObject<ObjectWrapper>(Encoding.UTF8.GetString(Data), JsonSettings);
        }

        /// <summary>
        /// Gets the property value from the object.
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public object GetPropertyValue(PropertyValueArgs e) {
            try {
                JValue v = (JValue)((JObject)e.Value)[e.Name];
                if (v is not null)
                    return v.Value;
                else
                    return null;
            } catch {
                JObject jobj = (JObject)((JObject)e.Value)[e.Name];
                string PropertyType = e.Reference.Info.PropertyType.AssemblyQualifiedName;
                var InitializedProperty = jobj.ToObject(Type.GetType(PropertyType));
                return InitializedProperty;
            }
        }

    }
}