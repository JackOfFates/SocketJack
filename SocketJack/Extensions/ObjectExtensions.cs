using System.ComponentModel;

namespace SocketJack.Extensions {
    public static class ObjectExtensions {

        public static bool ConvertableFromString<T>(this T obj) {
            if (obj is null) {
                return false;
            } else {
                return TypeDescriptor.GetConverter(obj.GetType()).CanConvertFrom(typeof(string));
            }
        }

        public static bool ConvertableFromString(object obj) {
            return obj.ConvertableFromString<object>();
        }
    }
}