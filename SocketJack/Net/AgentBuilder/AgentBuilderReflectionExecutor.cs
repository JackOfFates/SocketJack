using SocketJack.Net.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Net.AgentBuilder {

    public sealed class AgentBuilderReflectionExecutor : IAgentBuilderReflectionExecutor {
        private readonly ConcurrentDictionary<string, object> _namedObjects = new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public Task<object> ExecuteAsync(
            AgentBuilderNode node,
            IReadOnlyDictionary<string, object> inputs,
            IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults,
            CancellationToken cancellationToken) {

            cancellationToken.ThrowIfCancellationRequested();
            node.Normalize();
            string nodeType = (node.Type ?? "").Replace(" ", "").Replace("-", "").ToLowerInvariant();
            object result = nodeType == "reflectionobject"
                ? CreateObject(node, inputs, nodeResults)
                : Invoke(node, inputs, nodeResults);
            return Task.FromResult(result);
        }

        public object GetCatalog(int take = 200) {
            take = Math.Max(1, Math.Min(take, 1000));
            var types = ReflectionService.GetLoadedAssemblies()
                .SelectMany(assembly => assembly.PublicTypes.Select(typeName => new { assembly.Name, TypeName = typeName }))
                .Where(item => IsAllowedTypeName(item.TypeName))
                .Take(take)
                .Select(item => {
                    ReflectionService.TryResolveLoadedType(item.TypeName, IsAllowedType, out Type type, out _);
                    return new {
                        assembly = item.Name,
                        type = item.TypeName,
                        members = type == null
                            ? Array.Empty<object>()
                            : type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static)
                                .Where(member => member.MemberType == System.Reflection.MemberTypes.Method || member.MemberType == System.Reflection.MemberTypes.Property || member.MemberType == System.Reflection.MemberTypes.Field)
                                .Where(member => !member.Name.StartsWith("get_", StringComparison.Ordinal) && !member.Name.StartsWith("set_", StringComparison.Ordinal))
                                .Take(40)
                                .Select(member => (object)BuildCatalogMember(member))
                                .ToArray()
                    };
                })
                .ToArray();

            return new {
                ok = true,
                allowlist = "SocketJack assemblies and SocketJack.* namespaces only",
                namedObjects = _namedObjects.Keys.OrderBy(name => name).ToArray(),
                count = types.Length,
                types
            };
        }

        private static object BuildCatalogMember(System.Reflection.MemberInfo member) {
            if (member is System.Reflection.MethodInfo method) {
                return new {
                    name = method.Name,
                    kind = "Method",
                    returnType = method.ReturnType.FullName ?? method.ReturnType.Name,
                    parameters = method.GetParameters().Select(parameter => new {
                        name = parameter.Name ?? "",
                        type = parameter.ParameterType.FullName ?? parameter.ParameterType.Name,
                        optional = parameter.IsOptional,
                        fileLike = ReflectionService.IsFileType(parameter.ParameterType)
                    }).ToArray()
                };
            }

            if (member is System.Reflection.PropertyInfo property) {
                return new {
                    name = property.Name,
                    kind = "Property",
                    returnType = property.PropertyType.FullName ?? property.PropertyType.Name,
                    parameters = Array.Empty<object>()
                };
            }

            if (member is System.Reflection.FieldInfo field) {
                return new {
                    name = field.Name,
                    kind = "Field",
                    returnType = field.FieldType.FullName ?? field.FieldType.Name,
                    parameters = Array.Empty<object>()
                };
            }

            return new {
                name = member.Name,
                kind = member.MemberType.ToString(),
                returnType = "",
                parameters = Array.Empty<object>()
            };
        }

        private object CreateObject(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            string typeName = Config(node, "typeName", "type");
            if (string.IsNullOrWhiteSpace(typeName))
                throw new InvalidOperationException("Reflection object node requires a typeName.");
            if (!ReflectionService.TryResolveLoadedType(typeName, IsAllowedType, out Type type, out string error) || type == null)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Type is not allowed." : error);

            string[] args = ResolveArgs(node, inputs, nodeResults);
            object instance = ReflectionService.CreateInstance(type, args);
            string objectName = Config(node, "objectName", "name", "handle");
            if (string.IsNullOrWhiteSpace(objectName))
                objectName = node.Name;
            objectName = string.IsNullOrWhiteSpace(objectName) ? node.Id : objectName.Trim();
            _namedObjects[objectName] = instance;

            return new {
                ok = true,
                objectName,
                type = type.FullName ?? type.Name,
                json = AgentBuilderJson.SafeSerialize(instance)
            };
        }

        private object Invoke(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            string memberName = Config(node, "memberName", "method", "function", "property", "member");
            if (string.IsNullOrWhiteSpace(memberName))
                throw new InvalidOperationException("Reflection call node requires a memberName.");

            string objectName = Config(node, "objectName", "targetObject", "handle");
            string typeName = Config(node, "typeName", "type", "targetType");
            string[] args = ResolveArgs(node, inputs, nodeResults);

            ReflectionService.ExecutionResult execution;
            if (!string.IsNullOrWhiteSpace(objectName) && _namedObjects.TryGetValue(objectName, out object instance)) {
                if (!IsAllowedType(instance.GetType()))
                    throw new InvalidOperationException("Named object type is not allowed.");
                execution = new ReflectionService(instance).Execute(memberName, args);
            } else {
                if (string.IsNullOrWhiteSpace(typeName))
                    throw new InvalidOperationException("Reflection call requires objectName or typeName.");
                execution = ReflectionService.ExecuteStatic(typeName, memberName, args, IsAllowedType);
            }

            if (!execution.Success)
                throw new InvalidOperationException(execution.Error ?? "Reflection invocation failed.");

            return new {
                ok = true,
                member = execution.MemberName,
                kind = execution.MemberKind,
                returnType = execution.ReturnType,
                value = execution.Value,
                json = execution.Json
            };
        }

        private static string[] ResolveArgs(AgentBuilderNode node, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            string raw = Config(node, "args", "arguments", "parameters");
            var args = new List<string>();
            if (!string.IsNullOrWhiteSpace(raw)) {
                foreach (string part in raw.Split(new[] { '\n', '\r', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    args.Add(ResolveText(part.Trim(), inputs, nodeResults));
                if (args.Count == 0) {
                    foreach (string part in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        args.Add(ResolveText(part.Trim(), inputs, nodeResults));
                }
            }

            foreach (KeyValuePair<string, string> pair in node.Config.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!pair.Key.StartsWith("arg.", StringComparison.OrdinalIgnoreCase) &&
                    !pair.Key.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
                    continue;
                args.Add(ResolveText(pair.Value, inputs, nodeResults));
            }

            return args.ToArray();
        }

        private static string ResolveText(string value, IReadOnlyDictionary<string, object> inputs, IReadOnlyDictionary<string, AgentBuilderNodeResult> nodeResults) {
            if (string.IsNullOrWhiteSpace(value))
                return "";
            if (value.StartsWith("$input.", StringComparison.OrdinalIgnoreCase)) {
                string key = value.Substring("$input.".Length);
                return inputs != null && inputs.TryGetValue(key, out object inputValue) ? ValueText(inputValue) : "";
            }
            if (value.StartsWith("$", StringComparison.Ordinal)) {
                string key = value.Substring(1);
                if (nodeResults != null && nodeResults.TryGetValue(key, out AgentBuilderNodeResult result))
                    return ValueText(result.Value);
            }
            return value;
        }

        private static string Config(AgentBuilderNode node, params string[] names) {
            foreach (string name in names) {
                if (node.Config != null && node.Config.TryGetValue(name, out string value) && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private static bool IsAllowedTypeName(string typeName) =>
            !string.IsNullOrWhiteSpace(typeName) &&
            (typeName.StartsWith("SocketJack.", StringComparison.Ordinal) ||
             typeName.StartsWith("SocketJack", StringComparison.Ordinal));

        private static bool IsAllowedType(Type type) {
            if (type == null)
                return false;
            string fullName = type.FullName ?? type.Name;
            string assemblyName = type.Assembly.GetName().Name ?? "";
            if (fullName.StartsWith("SocketJack.Net.Services.ReflectionService", StringComparison.Ordinal))
                return false;
            return IsAllowedTypeName(fullName) || assemblyName.StartsWith("SocketJack", StringComparison.OrdinalIgnoreCase);
        }

        private static string ValueText(object value) {
            if (value == null)
                return "";
            if (value is string text)
                return text;
            if (value is IFormattable formattable)
                return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString() ?? "";
        }
    }
}
