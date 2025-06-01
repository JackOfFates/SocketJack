using SocketJack.Networking.Shared;
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

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

        public static T Clone<T>(this object Original) {
            T c = (T)Activator.CreateInstance(typeof(T));
            var @type = typeof(T);
            foreach (PropertyInfo p in type.GetProperties()) {
                if (p.CanRead && type.GetProperty(p.Name).CanWrite) {
                    var val = p.GetValue(Original, (object[])null);
                    type.GetProperty(p.Name).SetValue(c, val, (object[])null);
                }
            }

            foreach (FieldInfo p in type.GetFields()) {
                var val = p.GetValue(Original);
                type.GetField(p.Name).SetValue(c, val);
            }
            return c;
        }
    }
}