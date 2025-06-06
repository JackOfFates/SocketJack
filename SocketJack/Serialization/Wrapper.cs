﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.VisualBasic;
using SocketJack.Extensions;
using SocketJack.Management;
using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;

namespace SocketJack.Serialization {

    public class Wrapper{

        public string Type { get; set; }
        public object Value { get; set; }

        public Type GetValueType() {
            return System.Type.GetType(Type);
        }

        public static BindingFlags ReflectionFlags { get; set; } = BindingFlags.Instance | BindingFlags.Public;

        public Wrapper() {
        }

        public Wrapper(object Obj, TcpBase sender) {
            if (Obj == null) return;
            Type T = Obj.GetType();
            Type = T.AssemblyQualifiedName;
            if (IsTypeAllowed(Obj, sender)) {
                if (T.IsArray || T.IsValueType) {
                    Value = Obj;
                } else {
                    object stripped = Strip(T, Obj, sender);
                    Value = stripped;
                }
            }
        }

        private object Strip(Type T, object obj, TcpBase sender) {
            var instance = FormatterServices.GetSafeUninitializedObject(T);
            GetPropertyReferences(T).ForEach(r => MethodExtensions.TryInvoke(() => SetProperty(obj, ref instance, r, sender)));
            return instance;
        }

        private TypeNotAllowedException CreateTypeException(ref TcpBase sender, string Type, bool isBlacklisted = false) {
            var exception = new TypeNotAllowedException(Type, isBlacklisted);
            sender.InvokeOnError(sender.Connection, exception);
            return exception;
        }

        private TypeNotAllowedException CreateTypeException(ref TcpBase sender, Type Type, bool isBlacklisted = false) {
            return CreateTypeException(ref sender, Type.AssemblyQualifiedName, isBlacklisted);
        }

        private bool IsTypeAllowed(Type Type, TcpBase sender) {
            if (sender.Options.Blacklist.Contains(Type)) {
                if (Type == typeof(object) && this.Type != typeof(PeerRedirect).AssemblyQualifiedName)
                    throw CreateTypeException(ref sender, Type, true);
            } else if(!sender.Options.Whitelist.Contains(Type) ){
                if(Type == typeof(object) && this.Type != typeof(PeerRedirect).AssemblyQualifiedName)
                    throw CreateTypeException(ref sender, Type);
            }
            return true;
        }

        private bool IsTypeAllowed(object Obj, TcpBase sender) {
            Type T = Obj.GetType();
            if (T == typeof(PeerRedirect)) {
                PeerRedirect peerRedirect = (PeerRedirect)Obj;
                Type redirectType = System.Type.GetType(peerRedirect.Type);
                if (redirectType == null) {
                    Exception exception = new Exception("Type '" + redirectType.Name + "' is not found in any referenced assembly.");
                    throw exception;
                }
                return IsTypeAllowed(redirectType, sender);
            } else {
                return IsTypeAllowed(T, sender);
            }
        }

        public object Unwrap(TcpBase sender) {
            Type Type = GetValueType();
            if (this.Type == typeof(PingObject).AssemblyQualifiedName) return PingObject.StaticInstance;
            bool isAllowed = IsTypeAllowed(Type, sender);
            if (Type == null) {
                Exception exception = new Exception("Type '" + Type.Name + "' is not found in any referenced assembly.");
                throw exception;
            } else if(!isAllowed) {
                return null;
            }
            if (Type.IsValueType | Type.IsArray) {
                return sender.Options.Serializer.GetValue(Value, Type, true);
            } else {
                var instance = FormatterServices.GetSafeUninitializedObject(Type);
                var references = GetPropertyReferences(Type);
                int index = 0;

                for (int i = 0, loopTo = references.Count - 1; i <= loopTo; i++) {
                    index = i;
                    var reference = references[i];
                    try {
                        if (IsTypeAllowed(reference.Info.PropertyType, sender))
                            SetProperty(ref instance, reference, sender);
                    } catch (Exception ex) {
                        var r = references[index];
                        if (ex.Message.Contains("Object of type 'System.Int64' cannot be converted to type 'System.Int32'.")) {
                            throw new Int32NotSupportedException(instance.GetType().Name + "." + r.Info.Name);
                        } else {
                            string errorMessage = "Deserialization Error @ " + r.Index + " (" + instance.GetType().Name + "." + r.Info.Name + ")" + Environment.NewLine +
                                                  (ex.Message == string.Empty ? string.Empty : "     " + ex.Message) +
                                                  (ex.StackTrace == string.Empty ? string.Empty : Environment.NewLine + ex.StackTrace) + Environment.NewLine;
                            Exception exception = new Exception(errorMessage, ex);
                            throw exception;
                        }
                    }
                }
                return instance;
            }
        }

        public object GetPropertyValue(TcpBase sender, PropertyReference Reference) {
            return sender.Options.Serializer.GetPropertyValue(new PropertyValueArgs(Reference.Info.Name, Value, Reference));
        }

        public void SetProperty(ref object Instance, PropertyReference Reference, TcpBase sender) {
            if (Reference.Info.CanWrite) {
                var v = GetPropertyValue(sender, Reference);
                if (v == null) return;
                string PropertyTypeName = Reference.Info.PropertyType.FullName;
                if (v != null && v.GetType() == typeof(Wrapper)) {
                    Wrapper wrapper = (Wrapper)v;
                    if (wrapper.Type == null || wrapper.Value == null) {
                        return;
                    }
                    v = wrapper.Unwrap(sender);
                }
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
                if (Reference.Info.PropertyType == typeof(object)) {
                    var wrappedValue = new Wrapper(v, sender);
                    v = wrappedValue;
                } else if (Reference.Info.PropertyType.IsEnum) {
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

        public static PropertyReference GetPropertyReference(Type type, string PropertyName) {
            var properties = GetPropertyReferences(type, ReflectionFlags);
            return properties.Where((pr) => string.Equals(pr.Info.Name, PropertyName, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
        }

        public static PropertyReference GetPropertyReference(object target, string PropertyName) {
            return GetPropertyReference(target.GetType(), PropertyName);
        }

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
            } catch (Exception) {
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

        public PropertyValueArgs(string Name, object Value, PropertyReference Reference) {
            this.Name = Name;
            this.Value = Value;
            this.Reference = Reference;
        }

        public string Name { get; set; }
        public object Value { get; set; }
        public PropertyReference Reference { get; set; }

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