using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace EasyYoloOcr.Example.Wpf.Services;

/// <summary>
/// Provides reflection-based execution of properties, fields, and methods on a target object.
/// Results are serialized to JSON using SocketJack-compatible settings with safe fallback
/// for non-serializable types.
/// Supports scanning all loaded assemblies and parsing method parameters.
/// </summary>
public sealed class ReflectionService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 8
    };

    /// <summary>Describes a loaded assembly with its types.</summary>
    public sealed class AssemblyInfo
    {
        public string Name { get; set; } = "";
        public string Location { get; set; } = "";
        public List<string> PublicTypes { get; set; } = new();
    }

    /// <summary>Describes a discoverable member on the target object.</summary>
    public sealed class MemberInfo
    {
        public string Name { get; set; } = "";
        public string Kind { get; set; } = ""; // "Property", "Field", "Method"
        public string ReturnType { get; set; } = "";
        public string Description { get; set; } = "";
        public ParameterDetail[]? Parameters { get; set; }
        public bool IsReadOnly { get; set; }
    }

    /// <summary>Describes a parameter of a method.</summary>
    public sealed class ParameterDetail
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public bool IsOptional { get; set; }
        public string? DefaultValue { get; set; }
        public bool IsFileLike { get; set; }
    }

    /// <summary>Result of executing a reflection command.</summary>
    public sealed class ExecutionResult
    {
        public bool Success  { get; set; } 
        public string MemberName  { get; set; }  = "";
        public string MemberKind  { get; set; }  = "";
        public string? ReturnType { get; set; }  
        public object? Value { get; set; } 
        public string? Json  { get; set; } 
        public string? Error  { get; set; } 
        public bool IsVoidMethod  { get; set; } 
    }

    private readonly object _target;
    private readonly Type _targetType;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                                      | BindingFlags.Instance | BindingFlags.Static
                                      | BindingFlags.FlattenHierarchy;

    public ReflectionService(object target)
    {
        _target = target;
        _targetType = target.GetType();
    }

    /// <summary>
    /// Gets all loaded assemblies with their public types.
    /// Includes framework and application assemblies so /reflect can inspect the full runtime.
    /// </summary>
    public static List<AssemblyInfo> GetLoadedAssemblies()
    {
        var assemblies = new List<AssemblyInfo>();
        
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var name = assembly.GetName().Name ?? "";

                var publicTypes = assembly.GetExportedTypes()
                    .Where(t => !t.IsNested && !t.Name.Contains('<'))
                    .Select(t => t.FullName ?? t.Name)
                    .OrderBy(n => n)
                    .Take(100) // Limit to prevent overwhelming output
                    .ToList();

                if (publicTypes.Count > 0)
                {
                    assemblies.Add(new AssemblyInfo
                    {
                        Name = name,
                        Location = assembly.Location,
                        PublicTypes = publicTypes
                    });
                }
            }
            catch
            {
                // Skip assemblies that can't be inspected
            }
        }

        return assemblies.OrderBy(a => a.Name).ToList();
    }

    /// <summary>
    /// Navigate a dot-separated member path and return a ReflectionService for the resolved object.
    /// Returns null if any segment is null or not found.
    /// </summary>
    public ReflectionService? NavigateTo(string memberPath)
    {
        object? current = _target;
        Type currentType = _targetType;

        foreach (var segment in memberPath.Split('.'))
        {
            if (current == null) return null;

            var prop = currentType.GetProperty(segment, Flags);
            if (prop != null && prop.CanRead)
            {
                current = prop.GetValue(current);
                if (current == null) return null;
                currentType = current.GetType();
                continue;
            }

            var field = currentType.GetField(segment, Flags);
            if (field != null)
            {
                current = field.GetValue(current);
                if (current == null) return null;
                currentType = current.GetType();
                continue;
            }

            return null; // segment not found
        }

        return current != null ? new ReflectionService(current) : null;
    }

    /// <summary>
    /// Read the current value of a member by name (property or field).
    /// </summary>
    public object? ReadMember(string memberName)
    {
        var prop = _targetType.GetProperty(memberName, Flags);
        if (prop != null && prop.CanRead)
            return prop.GetValue(_target);

        var field = _targetType.GetField(memberName, Flags);
        if (field != null)
            return field.GetValue(_target);

        return null;
    }

    /// <summary>
    /// Discovers all accessible members (properties, fields, methods) on the target.
    /// Excludes compiler-generated, WPF infrastructure, and common noise members.
    /// </summary>
    public List<MemberInfo> DiscoverMembers()
    {
        var members = new List<MemberInfo>();

        // Properties
        foreach (var prop in _targetType.GetProperties(Flags))
        {
            if (IsExcluded(prop.Name)) continue;
            if (prop.GetIndexParameters().Length > 0) continue; // skip indexers

            members.Add(new MemberInfo
            {
                Name = prop.Name,
                Kind = "Property",
                ReturnType = FriendlyTypeName(prop.PropertyType),
                Description = prop.CanRead && prop.CanWrite ? "get/set" : prop.CanRead ? "get" : "set",
                IsReadOnly = !prop.CanWrite
            });
        }

        // Fields
        foreach (var field in _targetType.GetFields(Flags))
        {
            if (IsExcluded(field.Name)) continue;
            if (field.Name.Contains("__BackingField")) continue;
            if (field.IsSpecialName) continue;

            members.Add(new MemberInfo
            {
                Name = field.Name,
                Kind = "Field",
                ReturnType = FriendlyTypeName(field.FieldType),
                Description = field.IsStatic ? "static" : field.IsInitOnly ? "readonly" : "mutable",
                IsReadOnly = field.IsInitOnly || field.IsLiteral
            });
        }

        // Methods
        foreach (var method in _targetType.GetMethods(Flags))
        {
            if (IsExcluded(method.Name)) continue;
            if (method.IsSpecialName) continue; // property accessors, operators
            if (method.DeclaringType == typeof(object)) continue;
#if !NETSTANDARD
            if (method.DeclaringType == typeof(System.Windows.DependencyObject)) continue;
            if (method.DeclaringType == typeof(System.Windows.UIElement)) continue;
            if (method.DeclaringType == typeof(System.Windows.FrameworkElement)) continue;
            if (method.DeclaringType == typeof(System.Windows.Controls.Control)) continue;
            if (method.DeclaringType == typeof(System.Windows.Window)) continue;
            if (method.DeclaringType == typeof(System.Windows.Media.Visual)) continue;
#endif
            if (method.GetGenericArguments().Length > 0) continue; // skip generic methods

            var parameters = method.GetParameters();
            var paramDetails = parameters.Select(p => new ParameterDetail
            {
                Name = p.Name ?? "",
                Type = FriendlyTypeName(p.ParameterType),
                IsOptional = p.IsOptional,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                IsFileLike = IsFileType(p.ParameterType)
            }).ToArray();

            // Build friendly parameter signature
            string paramSig = parameters.Length == 0 
                ? "()" 
                : $"({string.Join(", ", parameters.Select(p => 
                    {
                        var typeName = FriendlyTypeName(p.ParameterType);
                        var pName = p.Name ?? "arg";
                        var defaultVal = p.HasDefaultValue ? $" = {p.DefaultValue}" : "";
                        return $"{typeName} {pName}{defaultVal}";
                    }))})";

            members.Add(new MemberInfo
            {
                Name = method.Name,
                Kind = "Method",
                ReturnType = FriendlyTypeName(method.ReturnType),
                Description = paramSig,
                Parameters = paramDetails.Length > 0 ? paramDetails : null
            });
        }

        return members.OrderBy(m => m.Kind).ThenBy(m => m.Name).ToList();
    }

    /// <summary>
    /// Get members filtered by a prefix for intellisense.
    /// </summary>
    public List<MemberInfo> GetMatchingMembers(string prefix)
    {
        var all = DiscoverMembers();
        if (string.IsNullOrEmpty(prefix)) return all;

        return all.Where(m => m.Name.Contains(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Get parameter info for a specific method by name.
    /// Returns null if the method is not found.
    /// </summary>
    public MemberInfo? GetMethodInfo(string methodName)
    {
        var methods = _targetType.GetMethods(Flags)
            .Where(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase)
                     && !m.IsSpecialName && !IsExcluded(m.Name))
            .ToList();

        if (methods.Count == 0) return null;

        // Return the first overload (or the one with most parameters for richest intellisense)
        var method = methods.OrderByDescending(m => m.GetParameters().Length).First();
        var parameters = method.GetParameters();

        string paramSig = parameters.Length == 0 
            ? "()" 
            : $"({string.Join(", ", parameters.Select(p => 
                {
                    var typeName = FriendlyTypeName(p.ParameterType);
                    var pName = p.Name ?? "arg";
                    var defaultVal = p.HasDefaultValue ? $" = {p.DefaultValue}" : "";
                    return $"{typeName} {pName}{defaultVal}";
                }))})";

        return new MemberInfo
        {
            Name = method.Name,
            Kind = "Method",
            ReturnType = FriendlyTypeName(method.ReturnType),
            Description = paramSig,
            Parameters = parameters.Select(p => new ParameterDetail
            {
                Name = p.Name ?? "",
                Type = FriendlyTypeName(p.ParameterType),
                IsOptional = p.IsOptional,
                DefaultValue = p.HasDefaultValue ? p.DefaultValue?.ToString() : null,
                IsFileLike = IsFileType(p.ParameterType)
            }).ToArray()
        };
    }

    /// <summary>
    /// Parse parameter string from /reflect command. 
    /// Parameters are in format (param1, param2, ...) where strings are quoted and numerics are not.
    /// Example: (123, "hello world", 45.6, "test")
    /// </summary>
    private static string[]? ParseParameters(string input)
    {
        // Check if input looks like a method call with parameters: name(...)
        var match = Regex.Match(input, @"^(\w+)\s*\((.*)\)\s*$", RegexOptions.Singleline);
        if (!match.Success)
            return null; // No parameters format detected

        string paramsContent = match.Groups[2].Value.Trim();
        if (string.IsNullOrEmpty(paramsContent))
            return Array.Empty<string>(); // Empty parameter list ()

        var parameters = new List<string>();
        int pos = 0;
        
        while (pos < paramsContent.Length)
        {
            // Skip whitespace and commas
            while (pos < paramsContent.Length && (char.IsWhiteSpace(paramsContent[pos]) || paramsContent[pos] == ','))
                pos++;

            if (pos >= paramsContent.Length)
                break;

            // Check for quoted string
            if (paramsContent[pos] == '"')
            {
                pos++; // skip opening quote
                int start = pos;
                while (pos < paramsContent.Length && paramsContent[pos] != '"')
                {
                    if (paramsContent[pos] == '\\' && pos + 1 < paramsContent.Length)
                        pos += 2; // skip escaped char
                    else
                        pos++;
                }
                
                if (pos < paramsContent.Length)
                {
                    parameters.Add(paramsContent[start..pos]);
                    pos++; // skip closing quote
                }
            }
            else
            {
                // Unquoted value (numeric, identifier, etc.)
                int start = pos;
                while (pos < paramsContent.Length && paramsContent[pos] != ',' && !char.IsWhiteSpace(paramsContent[pos]))
                    pos++;
                
                parameters.Add(paramsContent[start..pos].Trim());
            }
        }

        return parameters.ToArray();
    }

    /// <summary>
    /// Execute a member by name, optionally with string arguments.
    /// Properties/fields return their value; methods are invoked.
    /// Supports parsing parameters from method call syntax: method(arg1, arg2, ...)
    /// </summary>
    public ExecutionResult Execute(string memberName, string[]? args = null)
    {
        try
        {
            // Check if memberName contains parameter syntax
            string actualMemberName = memberName;
            string[]? parsedArgs = args;
            
            if (memberName.Contains('(') && memberName.Contains(')'))
            {
                var match = Regex.Match(memberName, @"^(\w+)\s*\((.*)\)\s*$", RegexOptions.Singleline);
                if (match.Success)
                {
                    actualMemberName = match.Groups[1].Value;
                    parsedArgs = ParseParameters(memberName);
                }
            }

            // Try property first
            var prop = _targetType.GetProperty(actualMemberName, Flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
            {
                if (parsedArgs is { Length: > 0 } && prop.CanWrite)
                {
                    // Setting a property value
                    object? converted = ConvertArg(parsedArgs[0], prop.PropertyType);
                    prop.SetValue(_target, converted);
                    return new ExecutionResult
                    {
                        Success = true,
                        MemberName = actualMemberName,
                        MemberKind = "Property (set)",
                        ReturnType = FriendlyTypeName(prop.PropertyType),
                        Json = "\"Property set successfully.\""
                    };
                }

                if (prop.CanRead)
                {
                    object? value = prop.GetValue(_target);
                    return BuildResult(actualMemberName, "Property", prop.PropertyType, value);
                }
            }

            // Try field
            var field = _targetType.GetField(actualMemberName, Flags);
            if (field != null)
            {
                if (parsedArgs is { Length: > 0 } && !field.IsInitOnly && !field.IsLiteral)
                {
                    object? converted = ConvertArg(parsedArgs[0], field.FieldType);
                    field.SetValue(_target, converted);
                    return new ExecutionResult
                    {
                        Success = true,
                        MemberName = actualMemberName,
                        MemberKind = "Field (set)",
                        ReturnType = FriendlyTypeName(field.FieldType),
                        Json = "\"Field set successfully.\""
                    };
                }

                object? fieldValue = field.GetValue(_target);
                return BuildResult(actualMemberName, "Field", field.FieldType, fieldValue);
            }

            // Try method
            var methods = _targetType.GetMethods(Flags)
                .Where(m => m.Name.Equals(actualMemberName, StringComparison.OrdinalIgnoreCase)
                         && !m.IsSpecialName)
                .ToList();

            if (methods.Count > 0)
            {
                // Find best overload matching arg count
                int argCount = parsedArgs?.Length ?? 0;
                var method = methods
                    .OrderBy(m => Math.Abs(m.GetParameters().Length - argCount))
                    .ThenBy(m => m.GetParameters().Count(p => !p.IsOptional))
                    .First();

                var parameters = method.GetParameters();
                object?[] invokeArgs = new object?[parameters.Length];

                for (int i = 0; i < parameters.Length; i++)
                {
                    if (parsedArgs != null && i < parsedArgs.Length)
                    {
                        invokeArgs[i] = ConvertArg(parsedArgs[i], parameters[i].ParameterType);
                    }
                    else if (parameters[i].HasDefaultValue)
                    {
                        invokeArgs[i] = parameters[i].DefaultValue;
                    }
                    else if (parameters[i].ParameterType.IsValueType)
                    {
                        invokeArgs[i] = Activator.CreateInstance(parameters[i].ParameterType);
                    }
                    else
                    {
                        invokeArgs[i] = null;
                    }
                }

                object? result = method.Invoke(_target, invokeArgs);

                // Handle async methods
                if (result is Task task)
                {
                    // For Task<T>, get the result
                    var taskType = task.GetType();
                    if (taskType.IsGenericType)
                    {
                        task.GetAwaiter().GetResult(); // block until complete
                        var resultProp = taskType.GetProperty("Result");
                        object? taskResult = resultProp?.GetValue(task);
                        return BuildResult(actualMemberName, "Method (async)", resultProp?.PropertyType ?? typeof(object), taskResult);
                    }
                    else
                    {
                        task.GetAwaiter().GetResult();
                        return new ExecutionResult
                        {
                            Success = true,
                            MemberName = actualMemberName,
                            MemberKind = "Method (async)",
                            IsVoidMethod = true,
                            Json = "\"Code executed.\""
                        };
                    }
                }

                if (method.ReturnType == typeof(void))
                {
                    return new ExecutionResult
                    {
                        Success = true,
                        MemberName = actualMemberName,
                        MemberKind = "Method",
                        IsVoidMethod = true,
                        Json = "\"Code executed.\""
                    };
                }

                return BuildResult(actualMemberName, "Method", method.ReturnType, result);
            }

            return new ExecutionResult
            {
                Success = false,
                MemberName = actualMemberName,
                Error = $"Member '{actualMemberName}' not found."
            };
        }
        catch (TargetInvocationException tie)
        {
            return new ExecutionResult
            {
                Success = false,
                MemberName = memberName,
                Error = tie.InnerException?.Message ?? tie.Message
            };
        }
        catch (Exception ex)
        {
            return new ExecutionResult
            {
                Success = false,
                MemberName = memberName,
                Error = ex.Message
            };
        }
    }

    private ExecutionResult BuildResult(string name, string kind, Type returnType, object? value)
    {
        string? json = SafeSerialize(value, returnType);

        return new ExecutionResult
        {
            Success = true,
            MemberName = name,
            MemberKind = kind,
            ReturnType = FriendlyTypeName(returnType),
            Value = value,
            Json = json
        };
    }

    /// <summary>
    /// Safely serialize a value to JSON. Skips non-serializable objects gracefully.
    /// Uses SocketJack-compatible serializer options.
    /// </summary>
    private static string? SafeSerialize(object? value, Type declaredType)
    {
        if (value == null) return "null";

        try
        {
            // Simple types — serialize directly
            if (IsSimpleType(declaredType) || IsSimpleType(value.GetType()))
                return JsonSerializer.Serialize(value, _jsonOptions);

            // Try serialization; if it fails or produces garbage, return a summary
            string json = JsonSerializer.Serialize(value, value.GetType(), _jsonOptions);

            // Guard against enormous outputs
            if (json.Length > 50_000)
            {
                return JsonSerializer.Serialize(new
                {
                    _truncated = true,
                    _type = FriendlyTypeName(value.GetType()),
                    _preview = json[..500] + "...",
                    _totalLength = json.Length
                }, _jsonOptions);
            }

            return json;
        }
        catch
        {
            // Non-serializable — return a type description
            try
            {
                return JsonSerializer.Serialize(new
                {
                    _nonSerializable = true,
                    _type = FriendlyTypeName(value.GetType()),
                    _toString = value.ToString()?[..Math.Min(200, value.ToString()?.Length ?? 0)]
                }, _jsonOptions);
            }
            catch
            {
                return $"\"<{FriendlyTypeName(value.GetType())}>\"";
            }
        }
    }

    private static object? ConvertArg(string arg, Type targetType)
    {
        if (targetType == typeof(string)) return arg;
        if (targetType == typeof(int) && int.TryParse(arg, out int i)) return i;
        if (targetType == typeof(long) && long.TryParse(arg, out long l)) return l;
        if (targetType == typeof(float) && float.TryParse(arg, out float f)) return f;
        if (targetType == typeof(double) && double.TryParse(arg, out double d)) return d;
        if (targetType == typeof(decimal) && decimal.TryParse(arg, out decimal dec)) return dec;
        if (targetType == typeof(bool) && bool.TryParse(arg, out bool b)) return b;
        if (targetType == typeof(byte) && byte.TryParse(arg, out byte by)) return by;
        if (targetType == typeof(short) && short.TryParse(arg, out short sh)) return sh;
        if (targetType == typeof(ushort) && ushort.TryParse(arg, out ushort ush)) return ush;
        if (targetType == typeof(uint) && uint.TryParse(arg, out uint ui)) return ui;
        if (targetType == typeof(ulong) && ulong.TryParse(arg, out ulong ul)) return ul;
        if (targetType == typeof(Guid) && Guid.TryParse(arg, out Guid g)) return g;
        if (targetType == typeof(DateTime) && DateTime.TryParse(arg, out DateTime dt)) return dt;
        if (targetType == typeof(TimeSpan) && TimeSpan.TryParse(arg, out TimeSpan ts)) return ts;

        // File path ? byte[]
        if (targetType == typeof(byte[]) && File.Exists(arg))
            return File.ReadAllBytes(arg);

        // Enum parsing
        if (targetType.IsEnum && Enum.TryParse(targetType, arg, true, out object? enumVal))
            return enumVal;

        // Nullable<T>
        var underlying = Nullable.GetUnderlyingType(targetType);
        if (underlying != null)
        {
            if (string.IsNullOrEmpty(arg) || arg.Equals("null", StringComparison.OrdinalIgnoreCase))
                return null;
            return ConvertArg(arg, underlying);
        }

        return arg; // fall back to string
    }

    /// <summary>Returns true for types that represent file-like inputs (byte[], Stream, FileInfo, etc.).</summary>
    public static bool IsFileType(Type type)
    {
        if (type == typeof(byte[])) return true;
        if (type == typeof(Stream) || type.IsSubclassOf(typeof(Stream))) return true;
        if (type == typeof(FileInfo)) return true;
        if (type == typeof(string)) return false; // string could be path but don't default to file picker
        return false;
    }

    private static bool IsSimpleType(Type type)
    {
        return type.IsPrimitive || type.IsEnum
            || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(DateTimeOffset);
    }

    private static readonly HashSet<string> _excludedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common WPF noise
        "GetHashCode", "GetType", "ToString", "Equals", "ReferenceEquals",
        "MemberwiseClone", "Finalize",
        // DependencyObject
        "GetValue", "SetValue", "SetCurrentValue", "ClearValue", "CoerceValue",
        "ReadLocalValue", "GetLocalValueEnumerator", "InvalidateProperty",
        // UIElement / Visual infrastructure
        "AddHandler", "RemoveHandler", "RaiseEvent", "AddToEventRoute",
        "OnCreateAutomationPeer", "OnRender", "OnDpiChanged",
        "HitTestCore", "GetLayoutClip", "ArrangeCore", "MeasureCore",
        "OnPropertyChanged", "OnVisualParentChanged", "OnVisualChildrenChanged",
        // FrameworkElement
        "FindResource", "TryFindResource", "FindName", "RegisterName", "UnregisterName",
        "ApplyTemplate", "OnApplyTemplate", "BeginStoryboard", "GetBindingExpression",
        "SetBinding", "SetResourceReference", "BringIntoView",
        "GetTemplateChild", "MoveFocus", "PredictFocus",
        // Control
        "OnTemplateChanged",
        // Window
        "DragMove", "Show", "Hide", "Close", "Activate",
        "ShowDialog", "GetWindow",
        // Generated event handlers from XAML
        "InitializeComponent",
        // Common compiler-generated
        "add_", "remove_",
    };

    private static bool IsExcluded(string name)
    {
        if (_excludedNames.Contains(name)) return true;
        if (name.StartsWith("get_") || name.StartsWith("set_")) return true;
        if (name.StartsWith("add_") || name.StartsWith("remove_")) return true;
        if (name.StartsWith("__")) return true;
        if (name.Contains("BackingField")) return true;
        return false;
    }

    internal static string FriendlyTypeName(Type type)
    {
        if (type == typeof(void)) return "void";
        if (type == typeof(string)) return "string";
        if (type == typeof(int)) return "int";
        if (type == typeof(long)) return "long";
        if (type == typeof(float)) return "float";
        if (type == typeof(double)) return "double";
        if (type == typeof(decimal)) return "decimal";
        if (type == typeof(bool)) return "bool";
        if (type == typeof(byte)) return "byte";
        if (type == typeof(byte[])) return "byte[]";
        if (type == typeof(char)) return "char";
        if (type == typeof(object)) return "object";

        var underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null) return FriendlyTypeName(underlying) + "?";

        if (type == typeof(Task)) return "Task";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return $"Task<{FriendlyTypeName(type.GetGenericArguments()[0])}>";

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return $"List<{FriendlyTypeName(type.GetGenericArguments()[0])}>";

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Dictionary<,>))
        {
            var args = type.GetGenericArguments();
            return $"Dictionary<{FriendlyTypeName(args[0])}, {FriendlyTypeName(args[1])}>";
        }

        if (type.IsArray)
            return FriendlyTypeName(type.GetElementType()!) + "[]";

        return type.Name;
    }
}
