using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualBasic;
using SocketJack.Extensions;
using SocketJack.Networking.Shared;
using SocketJack.Serialization.Json;

namespace SocketJack.Serialization {

    [Serializable]
    public class ObjectWrapper {

        public event OnSerializationErrorEventHandler OnSerializationError;

        public delegate void OnSerializationErrorEventHandler(Exception ex);
        public event OnDeserializationErrorEventHandler OnDeserializationError;

        public delegate void OnDeserializationErrorEventHandler(Exception ex);

        public static BindingFlags ReflectionFlags { get; set; } = BindingFlags.Instance | BindingFlags.Public;

        public ObjectWrapper() {
        }

        public ObjectWrapper(object Obj, TcpBase sender) {
            Type = Obj.GetType().AssemblyQualifiedName;
            if (Obj != null)
                Data = Wrap_Internal(Obj, sender);
        }

        public string Type { get; set; }
        public object Data { get; set; }

        public static object Wrap(object Obj, TcpBase sender) {
            return new ObjectWrapper(Obj, sender);
        }

        private object Wrap_Internal(object Obj, TcpBase sender) {
            var Type = Obj.GetType();
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            GetPropertyReferences(Type).ForEach(r => MethodExtensions.TryInvoke(() => SetProperty(Obj, ref instance, r, sender)));
            if (!ReferenceEquals(Type, typeof(PingObj))) {
                Console.Write("");
            }
            return instance;
        }

        public object Unwrap(TcpBase sender) {
            var Type = System.Type.GetType(this.Type);
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            var references = GetPropertyReferences(Type);
            int index = 0;

            for (int i = 0, loopTo = references.Count - 1; i <= loopTo; i++) {
                index = i;
                var reference = references[i];
                try {
                    SetProperty(ref instance, reference, sender);
                } catch (Exception ex) {
                    var r = references[index];
                    if (ex.Message.Contains("Object of type 'System.Int64' cannot be converted to type 'System.Int32'.")) {
                        var NotSupportedEx = new Int32NotSupportedException(instance.GetType().Name + "." + r.Info.Name);
                        Debug.WriteLine("Deserialization Error @ " + r.Index + " (" + instance.GetType().Name + "." + r.Info.Name + ")" + Environment.NewLine + "     " + NotSupportedEx.Message + Environment.NewLine + ex.StackTrace);
                        OnDeserializationError?.Invoke(NotSupportedEx);
                    } else {
                        Debug.WriteLine("Deserialization Error @ " + r.Index + " (" + instance.GetType().Name + "." + r.Info.Name + ")" + Environment.NewLine + "     " + ex.Message + Environment.NewLine + ex.StackTrace);
                        OnDeserializationError?.Invoke(ex);
                    }
                }
            }
            return instance;
        }

        private object GetValue(TcpBase sender, ref object Instance, PropertyReference Reference) {
            var argValue = Data;
            return sender.Serializer.GetPropertyValue(new PropertyValueArgs(Reference.Info.Name, ref argValue, Reference, ref Instance));
            Data = argValue;
        }

        public void SetProperty(ref object Instance, PropertyReference Reference, TcpBase sender) {
            if (Reference.Info.CanWrite) {
                var v = GetValue(sender, ref Instance, Reference);

                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                if (Reference.Info.PropertyType.IsEnum) {
                    var enumType = Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                    v = Enum.ToObject(enumType, v);
                }

                Reference.Info.SetValue(Instance, v, (object[])null);
            }
        }

        public void SetProperty(object ValueInstance, ref object Instance, PropertyReference Reference, TcpBase sender) {
            if (Reference.Info.CanWrite) {
                var v = Reference.GetValue(ValueInstance); // GetValue(sender, ValueInstance, Reference)

                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                if (Reference.Info.PropertyType.IsEnum) {
                    var enumType = Reference.Info.Module.Assembly.GetType(PropertyTypeName);
                    v = Enum.ToObject(enumType, v);
                }

                Reference.Info.SetValue(Instance, v, (object[])null);
            }
        }

        public static string GetVariableName<T>(Expression<Func<T>> expr) {
            MemberExpression body = (MemberExpression)expr.Body;

            return body.Member.Name;
        }

        #region GetEnumReferences

        public static List<EnumReference> GetEnumReferences(Type EnumType, BindingFlags ReflectionFlags) {
            string[] enums = (string[])Enum.GetValues(typeof(Type));
            var pRefs = new List<EnumReference>();
            for (int index = 0, loopTo = enums.Length - 1; index <= loopTo; index++)
                pRefs.Add(new EnumReference(enums[index], index));
            return pRefs;
        }

        public static List<EnumReference> GetEnumReferences(Type EnumType) {
            return GetEnumReferences(EnumType, ReflectionFlags);
        }

        #endregion

        #region GetFieldReferences

        public static List<FieldReference> GetFieldReferences(object target) {
            return GetFieldReferences(target.GetType(), ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(Type Type) {
            return GetFieldReferences(Type, ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(object target, BindingFlags ReflectionFlags) {
            return GetFieldReferences(target.GetType(), ReflectionFlags);
        }

        public static List<FieldReference> GetFieldReferences(Type Type, BindingFlags ReflectionFlags) {
            FieldInfo[] objProperties = Type.GetFields(ReflectionFlags);
            var References = new List<FieldReference>();

            for (int Index = 0, loopTo = objProperties.Length - 1; Index <= loopTo; Index++)
                References.Add(new FieldReference(objProperties[Index], Index));

            return References;
        }

        #endregion

        #region GetPropertyReferences

        public static List<PropertyReference> GetPropertyReferences(object target) {
            return GetPropertyReferences(target.GetType(), ReflectionFlags);
        }

        public static List<PropertyReference> GetPropertyReferences(Type Type) {
            return GetPropertyReferences(Type, ReflectionFlags);
        }

        public static List<PropertyReference> GetPropertyReferences(Type Type, BindingFlags ReflectionFlags) {
            PropertyInfo[] objProperties = Type.GetProperties(ReflectionFlags);
            var References = new List<PropertyReference>();
            try {
                var instance = FormatterServices.GetSafeUninitializedObject(Type);
                for (int Index = 0, loopTo = objProperties.Length - 1; Index <= loopTo; Index++) {
                    var p = objProperties[Index];
                    References.Add(new PropertyReference(p, Index));
                }
            } catch (Exception ex) {
            }
            return References;
        }

        public static List<PropertyReference> GetPropertyReferences(object Instance, BindingFlags ReflectionFlags, bool CatchAllProperties = false) {
            return GetPropertyReferences(Instance.GetType(), ReflectionFlags, CatchAllProperties);
        }

        #endregion

        #region GetMethodReferences

        public static List<string> ExcludedMethods = new List<string>() { "GETTYPE", "TOSTRING", "GETHASHCODE", "EQUALS", "GETDBCONTEXT" };

        public static List<MethodReference> GetMethodReferences(object target) {
            return GetMethodReferences(target.GetType(), ReflectionFlags);
        }

        public static List<MethodReference> GetMethodReferences(object target, bool includeFunctions) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, includeFunctions);
        }

        public static List<MethodReference> GetMethodReferences(object target, BindingFlags ReflectionFlags) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, false);
        }

        public static List<MethodReference> GetMethodReferences(object target, BindingFlags ReflectionFlags, bool includeFunctions) {
            return GetMethodReferences(target.GetType(), ReflectionFlags, includeFunctions, false);
        }

        public static List<MethodReference> GetMethodReferences(Type Type) {
            return GetMethodReferences(Type, ReflectionFlags);
        }

        public static List<MethodReference> GetMethodReferences(Type Type, bool includeFunctions) {
            return GetMethodReferences(Type, ReflectionFlags, includeFunctions);
        }

        public static List<MethodReference> GetMethodReferences(Type Type, BindingFlags ReflectionFlags, bool includeFunctions, bool CatchAllMethods = false) {
            MethodInfo[] objMethods = Type.GetMethods(ReflectionFlags | ~BindingFlags.SetProperty | ~BindingFlags.GetProperty);
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            int index = 0;
            var References = new List<MethodReference>();
            if (!includeFunctions) {
                foreach (MethodInfo m in objMethods) {
                    if (m.ReturnType is null) {
                        /* TODO ERROR: Skipped IfDirectiveTrivia
                        #If NET20 Then
                        *//* TODO ERROR: Skipped DisabledTextTrivia
                                                Dim all As String = Join(ExcludedMethods.ToArray, "").ToUpper
                                                If Not CatchAllMethods AndAlso (all.IndexOf(m.Name.ToUpper) <> -1) Then Continue For
                        *//* TODO ERROR: Skipped ElseDirectiveTrivia
                        #Else
                        */
                        if (!CatchAllMethods && ExcludedMethods.Contains(m.Name.ToUpper()))
                            continue;
                        /* TODO ERROR: Skipped EndIfDirectiveTrivia
                        #End If
                        */
                        References.Add(new MethodReference(m, index));
                        index += 1;
                    }
                }
            } else {
                foreach (MethodInfo m in objMethods) {
                    /* TODO ERROR: Skipped IfDirectiveTrivia
                    #If NET20 Then
                    *//* TODO ERROR: Skipped DisabledTextTrivia
                                        Dim all As String = Join(ExcludedMethods.ToArray, "").ToUpper
                                        If Not CatchAllMethods AndAlso (all.IndexOf(m.Name.ToUpper) <> -1) Then Continue For
                    *//* TODO ERROR: Skipped ElseDirectiveTrivia
                    #Else
                    */
                    if (!CatchAllMethods && ExcludedMethods.Contains(m.Name.ToUpper()))
                        continue;
                    /* TODO ERROR: Skipped EndIfDirectiveTrivia
                    #End If
                    */
                    References.Add(new MethodReference(m, index));
                    index += 1;
                }
            }

            return References;
        }

        #endregion

        #region GetEventReferences
        public static List<EventReference> GetEventReferences(Type Type) {
            EventInfo[] objProperties = Type.GetEvents(ReflectionFlags);
            var instance = FormatterServices.GetSafeUninitializedObject(Type);
            int index = 0;
            var References = new List<EventReference>();
            foreach (EventInfo p in objProperties) {
                References.Add(new EventReference(p, index));
                index += 1;
            }
            return References;
        }
        #endregion

    }

    public class PropertyValueArgs {

        public PropertyValueArgs(string Name, ref object Value, PropertyReference Reference, ref object Instance) {
            this.Name = Name;
            this.Value = Value;
            this.Reference = Reference;
            this.Instance = Instance;
        }

        public string Name { get; set; }
        public object Value { get; set; }
        public PropertyReference Reference { get; set; }
        public object Instance { get; set; }

    }

    public abstract class BaseReference<T> {

        public BaseReference(T Info, int Index) {
            this.Info = Info;
            this.Index = Index;
        }

        public T Info { get; set; }
        public int Index { get; set; }

    }

    public class EventReference : BaseReference<EventInfo> {

        public EventReference(EventInfo EventInfo, int Index) : base(EventInfo, Index) {
        }

        public override string ToString() {
            return Info.Name + "(Event)";
        }
    }

    public class PropertyReference : BaseReference<PropertyInfo> {

        public PropertyReference(PropertyInfo PropertyInfo, int Index) : base(PropertyInfo, Index) {
        }

        public object GetValue(object Instance) {
            return Info.GetValue(Instance);
        }

        public override string ToString() {
            return Info.Name;
        }
    }

    public class MethodReference : BaseReference<MethodInfo> {

        public MethodReference(MethodInfo MethodInfo, int Index) : base(MethodInfo, Index) {
        }

        public override string ToString() {
            return Info.Name + "()";
        }
    }

    public class FieldReference : BaseReference<FieldInfo> {

        public FieldReference(FieldInfo FieldInfo, int Index) : base(FieldInfo, Index) {
        }

        public override string ToString() {
            return Info.Name;
        }
    }

    public class EnumReference : BaseReference<string> {

        public EnumReference(string EnumName, int Index) : base(EnumName, Index) {
        }
    }

}