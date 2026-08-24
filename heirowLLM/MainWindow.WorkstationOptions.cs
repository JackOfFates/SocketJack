using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JackONNX.LlmRuntime;

namespace heirowLLM;

public partial class MainWindow
{
    private static readonly JsonSerializerOptions WorkstationOptionsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<string> BuildWorkstationOptionsCatalogJsonAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(BuildWorkstationOptionsCatalogJson).Task.ConfigureAwait(false);
    }

    private async Task<string> UpdateWorkstationOptionsCatalogJsonAsync(string body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await Dispatcher.InvokeAsync(() => UpdateWorkstationOptionsCatalogJson(body)).Task.ConfigureAwait(false);
    }

    private string BuildWorkstationOptionsCatalogJson()
    {
        List<WorkstationOptionBinding> bindings = BuildWorkstationOptionBindings();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            options = bindings.Select(binding => binding.Option).ToArray()
        }, WorkstationOptionsJson);
    }

    private string UpdateWorkstationOptionsCatalogJson(string body)
    {
        WorkstationOptionsUpdateRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<WorkstationOptionsUpdateRequest>(body ?? "{}", WorkstationOptionsJson);
        }
        catch (JsonException ex)
        {
            return JsonSerializer.Serialize(new { ok = false, error = "Invalid options request: " + ex.Message }, WorkstationOptionsJson);
        }

        WorkstationOptionChange[] changes = request?.Changes ?? [];
        if (changes.Length == 0)
            return JsonSerializer.Serialize(new { ok = false, error = "No option changes were supplied." }, WorkstationOptionsJson);

        Dictionary<string, WorkstationOptionBinding> bindings = BuildWorkstationOptionBindings()
            .ToDictionary(binding => binding.Option.Id, StringComparer.OrdinalIgnoreCase);
        var prepared = new List<(WorkstationOptionBinding Binding, object? Value)>();
        foreach (WorkstationOptionChange change in changes)
        {
            if (string.IsNullOrWhiteSpace(change.Id) || !bindings.TryGetValue(change.Id, out WorkstationOptionBinding? binding))
                return JsonSerializer.Serialize(new { ok = false, error = "Unknown Workstation option: " + (change.Id ?? "") }, WorkstationOptionsJson);
            if (!binding.Option.Editable || binding.Option.Sensitive)
                return JsonSerializer.Serialize(new { ok = false, error = binding.Option.DisplayName + " is read-only." }, WorkstationOptionsJson);
            if (!TryConvertWorkstationOptionValue(change.Value ?? "", binding.Property.PropertyType, out object? converted, out string error))
                return JsonSerializer.Serialize(new { ok = false, error = binding.Option.DisplayName + ": " + error }, WorkstationOptionsJson);
            prepared.Add((binding, converted));
        }

        foreach ((WorkstationOptionBinding binding, object? value) in prepared)
        {
            binding.Property.SetValue(binding.Target, value);
            if (!binding.IsSetting)
            {
                _settings.ServicePropertyOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                _settings.ServicePropertyOverrides[binding.OverrideKey] = FormatServicePropertyValue(value);
            }
        }

        if (prepared.Any(item => item.Binding.IsSetting))
            ApplySettingsToUi(_settings);
        if (prepared.Any(item => ReferenceEquals(item.Binding.Target, _jackOnnxToolOptions)))
        {
            _jackOnnxToolDefinitions = JackOnnxLlmRuntimeToolRegistration.CreateDefinitions(_jackOnnxToolOptions);
            UpdateLlmRuntimeCapabilityLine();
        }

        PersistSettingsToDisk(_settings, applyToServices: true);
        AppendServiceEventLine("Workstation", prepared.Count.ToString(CultureInfo.InvariantCulture) + " option(s) updated from Web Chat.");
        RefreshServicesPanel();

        List<WorkstationOptionBinding> refreshed = BuildWorkstationOptionBindings();
        return JsonSerializer.Serialize(new
        {
            ok = true,
            saved = prepared.Count,
            options = refreshed.Select(binding => binding.Option).ToArray()
        }, WorkstationOptionsJson);
    }

    private List<WorkstationOptionBinding> BuildWorkstationOptionBindings()
    {
        var result = new List<WorkstationOptionBinding>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var defaults = new heirowLLMSettings();

        foreach (PropertyInfo property in typeof(heirowLLMSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (property.GetMethod?.IsPublic != true || property.GetIndexParameters().Length != 0)
                continue;

            string id = "setting:" + property.Name;
            bool sensitive = IsSensitiveWorkstationOption(property);
            bool isJson = !IsDisplayableServicePropertyType(property.PropertyType);
            bool editable = property.SetMethod?.IsPublic == true && !sensitive &&
                            !string.Equals(property.Name, nameof(heirowLLMSettings.ServicePropertyOverrides), StringComparison.Ordinal);
            bool isDreamModel = string.Equals(property.Name, nameof(heirowLLMSettings.DreamChecksAndBalancesModel), StringComparison.Ordinal);
            bool isDwmBlurGlass = string.Equals(property.Name, nameof(heirowLLMSettings.DwmBlurGlassEnabled), StringComparison.Ordinal);
            object? value = SafeGetPropertyValue(property, _settings);
            object? defaultValue = SafeGetPropertyValue(property, defaults);
            var option = new WorkstationOptionDto
            {
                Id = id,
                Name = property.Name,
                DisplayName = isDreamModel ? "Dream / Checks and Balances model" : isDwmBlurGlass ? "DWMBlurGlass effects" : SplitWorkstationOptionName(property.Name),
                Category = isDreamModel ? "Dream and Checks" : isDwmBlurGlass ? "Appearance" : CategorizeWorkstationSetting(property.Name),
                Type = GetWorkstationOptionTypeName(property.PropertyType),
                TypeGroup = GetWorkstationOptionTypeGroup(property.PropertyType),
                Value = sensitive ? "[redacted]" : FormatWorkstationOptionValue(value, property.PropertyType, isJson),
                DefaultValue = sensitive ? "[redacted]" : FormatWorkstationOptionValue(defaultValue, property.PropertyType, isJson),
                Editable = editable,
                Sensitive = sensitive,
                Source = "Workstation settings",
                Description = isDreamModel
                    ? "Dedicated model used by both Dream reflection and Checks and Balances. Local 1.5B through 6B models are recommended for low-resource background work on this GPU."
                    : isDwmBlurGlass
                    ? "Default-on compatibility for Maplespe DWMBlurGlass and the Windows DWM blur API. Official project: https://github.com/Maplespe/DWMBlurGlass. Disabling it removes heirowLLM's native blur request; it does not install or uninstall the system-wide utility."
                    : BuildWorkstationOptionDescription(property.Name, "Workstation settings", editable, sensitive),
                Choices = isDreamModel ? BuildDreamModelOptionChoices() : GetWorkstationOptionChoices(property.PropertyType),
                Priority = isDreamModel ? 1000 : isDwmBlurGlass ? 990 : 0,
                LearnMoreUrl = isDwmBlurGlass ? "https://github.com/Maplespe/DWMBlurGlass" : null
            };
            if (ids.Add(id))
                result.Add(new WorkstationOptionBinding(option, _settings, property, true, ""));
        }

        foreach (ServicePropertySource source in BuildKnownServicePropertySources())
        {
            if (ReferenceEquals(source.Target, _settings))
                continue;

            PropertyInfo[] properties;
            try
            {
                properties = source.Target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                continue;
            }

            foreach (PropertyInfo property in properties.OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!IsInspectableServiceProperty(property) || (source.Filter != null && !source.Filter(property)))
                    continue;

                string overrideKey = BuildServicePropertyOverrideKey(source.Category, property.Name);
                string id = "reflection:" + overrideKey;
                bool sensitive = IsSensitiveServiceProperty(property);
                bool editable = source.AllowWrites && !sensitive && property.SetMethod?.IsPublic == true &&
                                IsEditableServicePropertyType(property.PropertyType);
                object? value = SafeGetPropertyValue(property, source.Target);
                var option = new WorkstationOptionDto
                {
                    Id = id,
                    Name = property.Name,
                    DisplayName = SplitWorkstationOptionName(property.Name),
                    Category = "Reflection / " + source.Category,
                    Type = GetWorkstationOptionTypeName(property.PropertyType),
                    TypeGroup = GetWorkstationOptionTypeGroup(property.PropertyType),
                    Value = sensitive ? "[redacted]" : FormatServicePropertyValue(value),
                    DefaultValue = "",
                    Editable = editable,
                    Sensitive = sensitive,
                    Source = source.Target.GetType().Name,
                    Description = BuildWorkstationOptionDescription(property.Name, source.Category, editable, sensitive),
                    Choices = GetWorkstationOptionChoices(property.PropertyType)
                };
                if (ids.Add(id))
                    result.Add(new WorkstationOptionBinding(option, source.Target, property, false, overrideKey));
            }
        }

        return result;
    }

    private static object? SafeGetPropertyValue(PropertyInfo property, object target)
    {
        try { return property.GetValue(target); }
        catch { return null; }
    }

    private static bool IsSensitiveWorkstationOption(PropertyInfo property)
    {
        string name = property.Name;
        return name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("AuthToken", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("OwnerToken", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("PrivateKey", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatWorkstationOptionValue(object? value, Type type, bool asJson)
    {
        if (value == null)
            return "";
        if (asJson)
            return JsonSerializer.Serialize(value, type, WorkstationOptionsJson);
        return FormatServicePropertyValue(value);
    }

    private static bool TryConvertWorkstationOptionValue(string rawValue, Type targetType, out object? convertedValue, out string error)
    {
        if (IsDisplayableServicePropertyType(targetType))
            return TryConvertServicePropertyValue(rawValue, targetType, out convertedValue, out error);

        try
        {
            convertedValue = JsonSerializer.Deserialize(rawValue, targetType, WorkstationOptionsJson);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            convertedValue = null;
            error = "Enter valid JSON for " + GetWorkstationOptionTypeName(targetType) + ": " + TrimForDisplay(ex.Message, 140);
            return false;
        }
    }

    private static string GetWorkstationOptionTypeName(Type type)
    {
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        if (effective.IsGenericType)
        {
            string baseName = effective.Name.Split('`')[0];
            return baseName + "<" + string.Join(", ", effective.GetGenericArguments().Select(GetWorkstationOptionTypeName)) + ">";
        }
        return effective.Name;
    }

    private static string GetWorkstationOptionTypeGroup(Type type)
    {
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        if (effective == typeof(bool)) return "Boolean";
        if (effective.IsEnum) return "Choice";
        if (effective == typeof(string) || effective == typeof(Uri)) return "String";
        if (effective == typeof(DateTime) || effective == typeof(DateTimeOffset) || effective == typeof(TimeSpan)) return "Date and time";
        TypeCode code = Type.GetTypeCode(effective);
        if (code is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.UInt16 or TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Single or TypeCode.Double or TypeCode.Decimal)
            return "Numeric";
        return "JSON";
    }

    private static string[]? GetWorkstationOptionChoices(Type type)
    {
        Type effective = Nullable.GetUnderlyingType(type) ?? type;
        return effective.IsEnum ? Enum.GetNames(effective) : null;
    }

    private static string SplitWorkstationOptionName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Option";
        var builder = new StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) && (char.IsLower(name[index - 1]) || (index + 1 < name.Length && char.IsLower(name[index + 1]))))
                builder.Append(' ');
            builder.Append(current);
        }
        return builder.ToString();
    }

    private static string CategorizeWorkstationSetting(string name)
    {
        if (name.StartsWith("Window", StringComparison.OrdinalIgnoreCase) || name.Contains("Startup", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Start", StringComparison.OrdinalIgnoreCase) || name.Contains("Tray", StringComparison.OrdinalIgnoreCase))
            return "Startup and Window";
        if (name.StartsWith("WpfChat", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WebChat", StringComparison.OrdinalIgnoreCase))
            return "Web Chat";
        if (name.Contains("Model", StringComparison.OrdinalIgnoreCase) || name.Contains("Runtime", StringComparison.OrdinalIgnoreCase) || name.StartsWith("RemoteLm", StringComparison.OrdinalIgnoreCase))
            return "Models and Runtime";
        if (name.StartsWith("Shell", StringComparison.OrdinalIgnoreCase) || name.Contains("PortForward", StringComparison.OrdinalIgnoreCase) || name.Contains("Server", StringComparison.OrdinalIgnoreCase))
            return "Shell and Network";
        if (name.StartsWith("Host", StringComparison.OrdinalIgnoreCase) || name.Contains("Publish", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Stripe", StringComparison.OrdinalIgnoreCase))
            return "Publishing and Server";
        if (name.Contains("Cost", StringComparison.OrdinalIgnoreCase) || name.Contains("Storage", StringComparison.OrdinalIgnoreCase) || name.Contains("TokenRate", StringComparison.OrdinalIgnoreCase))
            return "Costs and Storage";
        if (name.Contains("Auth", StringComparison.OrdinalIgnoreCase) || name.Contains("Registration", StringComparison.OrdinalIgnoreCase) || name.Contains("User", StringComparison.OrdinalIgnoreCase) || name.Contains("Owner", StringComparison.OrdinalIgnoreCase))
            return "Accounts and Security";
        if (name.Contains("Guide", StringComparison.OrdinalIgnoreCase) || name.Contains("Hint", StringComparison.OrdinalIgnoreCase) || name.Contains("Coachmark", StringComparison.OrdinalIgnoreCase) || name.Contains("Splash", StringComparison.OrdinalIgnoreCase))
            return "Interface and Guidance";
        return "Advanced";
    }

    private static string BuildWorkstationOptionDescription(string propertyName, string source, bool editable, bool sensitive)
    {
        if (sensitive) return "Sensitive value from " + source + "; its contents are never sent to the browser.";
        return SplitWorkstationOptionName(propertyName) + " from " + source + (editable ? "." : "; shown for reference.");
    }

    private sealed record WorkstationOptionBinding(WorkstationOptionDto Option, object Target, PropertyInfo Property, bool IsSetting, string OverrideKey);

    private sealed class WorkstationOptionDto
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Category { get; init; } = "";
        public string Type { get; init; } = "";
        public string TypeGroup { get; init; } = "";
        public string Value { get; init; } = "";
        public string DefaultValue { get; init; } = "";
        public bool Editable { get; init; }
        public bool Sensitive { get; init; }
        public string Source { get; init; } = "";
        public string Description { get; init; } = "";
        public string[]? Choices { get; init; }
        public int Priority { get; init; }
        public string? LearnMoreUrl { get; init; }
    }

    private sealed class WorkstationOptionsUpdateRequest
    {
        public WorkstationOptionChange[]? Changes { get; set; }
    }

    private sealed class WorkstationOptionChange
    {
        public string? Id { get; set; }
        public string? Value { get; set; }
    }
}
