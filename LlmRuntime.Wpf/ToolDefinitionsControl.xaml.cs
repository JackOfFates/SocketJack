using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using LlmRuntime;

namespace LlmRuntime.Wpf;

public partial class ToolDefinitionsControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly ObservableCollection<LlmToolDefinition> _definitions = new();
    private LlmToolRegistry _registry;

    public ToolDefinitionsControl()
    {
        InitializeComponent();
        _registry = new LlmToolRegistry(new LlmRuntimeOptions());
        ToolList.ItemsSource = _definitions;
        VisibilityCombo.ItemsSource = Enum.GetValues<LlmToolVisibility>();
        SourceTypeCombo.ItemsSource = Enum.GetValues<LlmToolSourceType>();
        ApprovalCombo.ItemsSource = Enum.GetValues<LlmToolApprovalMode>();
        Loaded += (_, _) => RefreshDefinitions();
        ClearEditor();
    }

    public LlmToolRegistry Registry
    {
        get => _registry;
        set
        {
            _registry = value ?? throw new ArgumentNullException(nameof(value));
            RefreshDefinitions();
        }
    }

    public void Configure(LlmRuntimeOptions options) => Registry = new LlmToolRegistry(options);

    private void RefreshDefinitions()
    {
        _definitions.Clear();
        foreach (var definition in Registry.ListDefinitions())
            _definitions.Add(definition);
        StatusText.Text = $"{_definitions.Count} tool definition(s)";
    }

    private void ClearEditor()
    {
        IdBox.Text = "";
        NameBox.Text = "";
        DescriptionBox.Text = "";
        VisibilityCombo.SelectedItem = LlmToolVisibility.Proprietary;
        SourceTypeCombo.SelectedItem = LlmToolSourceType.Http;
        ApprovalCombo.SelectedItem = LlmToolApprovalMode.AskEveryTime;
        TimeoutBox.Text = "60";
        SourceBox.Text = "";
        VendorBox.Text = "";
        LicenseBox.Text = "";
        TagsBox.Text = "";
        AllowedProjectsBox.Text = "";
        SecretsBox.Text = "";
        SecretValuesBox.Password = "";
        HeadersBox.Text = "";
        EnvironmentBox.Text = "";
        InputSchemaBox.Text = "{\r\n  \"type\": \"object\",\r\n  \"properties\": {}\r\n}";
        ResultSchemaBox.Text = "";
        TestOutputBox.Text = "";
        SetPermissions(LlmToolPermissions.None);
        UpdateBadges(null);
        ToolList.SelectedItem = null;
    }

    private void PopulateEditor(LlmToolDefinition definition)
    {
        IdBox.Text = definition.Id;
        NameBox.Text = definition.Name;
        DescriptionBox.Text = definition.Description;
        VisibilityCombo.SelectedItem = definition.Visibility;
        SourceTypeCombo.SelectedItem = definition.SourceType;
        ApprovalCombo.SelectedItem = definition.ApprovalMode;
        TimeoutBox.Text = definition.TimeoutSeconds.ToString();
        SourceBox.Text = definition.Source;
        VendorBox.Text = definition.Vendor;
        LicenseBox.Text = definition.LicenseNotes;
        TagsBox.Text = string.Join(", ", definition.Tags);
        AllowedProjectsBox.Text = string.Join(Environment.NewLine, definition.AllowedProjects);
        SecretsBox.Text = string.Join(Environment.NewLine, definition.RequiredSecrets.Select(secret => $"{secret.Name}={secret.SecretId}"));
        SecretValuesBox.Password = "";
        HeadersBox.Text = FormatDictionary(definition.HttpHeaders);
        EnvironmentBox.Text = FormatDictionary(definition.EnvironmentVariables);
        InputSchemaBox.Text = FormatJson(definition.InputSchema);
        ResultSchemaBox.Text = definition.ResultSchema.HasValue ? FormatJson(definition.ResultSchema.Value) : "";
        TestOutputBox.Text = "";
        SetPermissions(definition.Permissions);
        UpdateBadges(definition);
    }

    private LlmToolDefinition BuildDefinitionFromEditor()
    {
        var definition = new LlmToolDefinition
        {
            Id = IdBox.Text.Trim(),
            Name = NameBox.Text.Trim(),
            Description = DescriptionBox.Text.Trim(),
            Visibility = VisibilityCombo.SelectedItem is LlmToolVisibility visibility ? visibility : LlmToolVisibility.Proprietary,
            SourceType = SourceTypeCombo.SelectedItem is LlmToolSourceType sourceType ? sourceType : LlmToolSourceType.Http,
            ApprovalMode = ApprovalCombo.SelectedItem is LlmToolApprovalMode approval ? approval : LlmToolApprovalMode.AskEveryTime,
            Source = SourceBox.Text.Trim(),
            Vendor = VendorBox.Text.Trim(),
            LicenseNotes = LicenseBox.Text.Trim(),
            Tags = SplitList(TagsBox.Text),
            RequiredSecrets = ParseSecrets(SecretsBox.Text),
            AllowedProjects = SplitLines(AllowedProjectsBox.Text),
            HttpHeaders = ParseKeyValues(HeadersBox.Text),
            EnvironmentVariables = ParseKeyValues(EnvironmentBox.Text),
            InputSchema = ParseSchema(InputSchemaBox.Text, required: true),
            Permissions = GetPermissions(),
            TimeoutSeconds = int.TryParse(TimeoutBox.Text, out int timeout) ? timeout : 60
        };

        if (!string.IsNullOrWhiteSpace(ResultSchemaBox.Text))
            definition.ResultSchema = ParseSchema(ResultSchemaBox.Text, required: false);

        return definition;
    }

    private static JsonElement ParseSchema(string json, bool required)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            if (!required)
                return JsonDocument.Parse("null").RootElement.Clone();
            json = "{\"type\":\"object\",\"properties\":{}}";
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static List<string> SplitList(string text) =>
        text.Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> SplitLines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static Dictionary<string, string> ParseKeyValues(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]))
                values[parts[0]] = parts[1];
        }
        return values;
    }

    private static string FormatDictionary(IReadOnlyDictionary<string, string> values) =>
        values == null || values.Count == 0
            ? ""
            : string.Join(Environment.NewLine, values.Select(pair => $"{pair.Key}={pair.Value}"));

    private static List<LlmToolSecretReference> ParseSecrets(string text)
    {
        var secrets = new List<LlmToolSecretReference>();
        foreach (string line in text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            string name = parts[0];
            string secretId = parts.Length > 1 ? parts[1] : "";
            if (!string.IsNullOrWhiteSpace(name))
                secrets.Add(new LlmToolSecretReference { Name = name, SecretId = secretId });
        }
        return secrets;
    }

    private void SetPermissions(LlmToolPermissions permissions)
    {
        FsReadCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.FileSystemRead);
        FsWriteCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.FileSystemWrite);
        ShellCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.ShellExecution);
        NetworkCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.NetworkAccess);
        BrowserCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.BrowserAccess);
        RepoCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.RepositoryAccess);
        SecretsCheck.IsChecked = permissions.HasFlag(LlmToolPermissions.SecretsAccess);
    }

    private LlmToolPermissions GetPermissions()
    {
        LlmToolPermissions permissions = LlmToolPermissions.None;
        if (FsReadCheck.IsChecked == true) permissions |= LlmToolPermissions.FileSystemRead;
        if (FsWriteCheck.IsChecked == true) permissions |= LlmToolPermissions.FileSystemWrite;
        if (ShellCheck.IsChecked == true) permissions |= LlmToolPermissions.ShellExecution;
        if (NetworkCheck.IsChecked == true) permissions |= LlmToolPermissions.NetworkAccess;
        if (BrowserCheck.IsChecked == true) permissions |= LlmToolPermissions.BrowserAccess;
        if (RepoCheck.IsChecked == true) permissions |= LlmToolPermissions.RepositoryAccess;
        if (SecretsCheck.IsChecked == true) permissions |= LlmToolPermissions.SecretsAccess;
        return permissions;
    }

    private static string FormatJson(JsonElement element) => JsonSerializer.Serialize(element, JsonOptions);

    private void UpdateBadges(LlmToolDefinition? definition)
    {
        if (definition == null)
        {
            BadgesText.Text = "";
            CompatibilityText.Text = "";
            return;
        }

        var badges = new List<string> { definition.Visibility.ToString() };
        if (!string.IsNullOrWhiteSpace(definition.LicenseNotes))
            badges.Add(definition.LicenseNotes);
        if (definition.RequiredSecrets.Count > 0)
            badges.Add("Secrets");
        if (definition.AllowedProjects.Count > 0)
            badges.Add("Scoped");

        BadgesText.Text = string.Join(" | ", badges);

        var compatibility = new List<string> { "OpenAI tools" };
        if (definition.SourceType == LlmToolSourceType.McpServer)
            compatibility.Add("MCP");
        if (definition.SourceType == LlmToolSourceType.BuiltInSocketJack)
            compatibility.Add("SocketJack agent");
        if (definition.SourceType is LlmToolSourceType.Executable or LlmToolSourceType.PowerShell or LlmToolSourceType.DotNetAssembly)
            compatibility.Add("Local-only");
        CompatibilityText.Text = string.Join(" | ", compatibility);
    }

    private void NewButton_Click(object sender, RoutedEventArgs e) => ClearEditor();

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDefinitions();

    private void ToolList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ToolList.SelectedItem is LlmToolDefinition definition)
            PopulateEditor(definition);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = Registry.UpsertDefinition(BuildDefinitionFromEditor());
            SaveSecretValues(saved);
            RefreshDefinitions();
            ToolList.SelectedItem = _definitions.FirstOrDefault(definition => definition.Id == saved.Id);
            UpdateBadges(saved);
            StatusText.Text = $"Saved {saved.Name}";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Tool Definition", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        string id = IdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(id))
            return;

        if (MessageBox.Show($"Delete {id}?", "Tool Definition", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        Registry.DeleteDefinition(id);
        RefreshDefinitions();
        ClearEditor();
        StatusText.Text = $"Deleted {id}";
    }

    private void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var definition = ToolList.SelectedItem as LlmToolDefinition ?? BuildDefinitionFromEditor();
            Clipboard.SetText(JsonSerializer.Serialize(definition, JsonOptions));
            StatusText.Text = "Tool JSON copied";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string json = Clipboard.GetText();
            var definition = JsonSerializer.Deserialize<LlmToolDefinition>(json, JsonOptions);
            if (definition == null)
                throw new InvalidOperationException("Clipboard does not contain a tool definition.");
            PopulateEditor(definition);
            StatusText.Text = "Tool JSON imported";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Import Tool", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = Registry.UpsertDefinition(BuildDefinitionFromEditor());
            SaveSecretValues(saved);
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(TestInputBox.Text) ? "{}" : TestInputBox.Text);
            var invoker = new LlmToolInvoker(Registry);
            var result = await invoker.InvokeAsync(new LlmToolInvocationRequest
            {
                ToolId = saved.Id,
                Approved = ApprovedCheck.IsChecked == true,
                Input = document.RootElement.Clone()
            });

            TestOutputBox.Text = JsonSerializer.Serialize(result, JsonOptions);
            RefreshDefinitions();
            StatusText.Text = result.Success ? "Tool test completed" : "Tool test failed";
        }
        catch (Exception ex)
        {
            TestOutputBox.Text = ex.ToString();
            StatusText.Text = ex.Message;
        }
    }

    private void SchemaFromExampleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(TestInputBox.Text) ? "{}" : TestInputBox.Text);
            InputSchemaBox.Text = JsonSerializer.Serialize(BuildSchema(document.RootElement), JsonOptions);
            StatusText.Text = "Input schema generated";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void SaveSecretsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var saved = Registry.UpsertDefinition(BuildDefinitionFromEditor());
            int count = SaveSecretValues(saved);
            RefreshDefinitions();
            ToolList.SelectedItem = _definitions.FirstOrDefault(definition => definition.Id == saved.Id);
            StatusText.Text = count == 0 ? "No secret value entered" : $"Saved {count} secret value(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Tool Secrets", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private int SaveSecretValues(LlmToolDefinition definition)
    {
        string text = SecretValuesBox.Password;
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        int count = 0;
        foreach (string entry in text.Split([';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2)
            {
                string secretId = ResolveSecretId(definition, parts[0]);
                Registry.SetSecret(secretId, parts[1]);
                count++;
            }
            else if (definition.RequiredSecrets.Count == 1)
            {
                Registry.SetSecret(definition.RequiredSecrets[0].SecretId, entry);
                count++;
            }
        }

        if (count > 0)
            SecretValuesBox.Password = "";
        return count;
    }

    private static string ResolveSecretId(LlmToolDefinition definition, string nameOrId)
    {
        var match = definition.RequiredSecrets.FirstOrDefault(secret =>
            string.Equals(secret.Name, nameOrId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(secret.SecretId, nameOrId, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(match?.SecretId) ? nameOrId : match.SecretId;
    }

    private static object BuildSchema(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => new
            {
                type = "object",
                properties = element.EnumerateObject().ToDictionary(property => property.Name, property => BuildSchema(property.Value))
            },
            JsonValueKind.Array => new
            {
                type = "array",
                items = element.GetArrayLength() > 0 ? BuildSchema(element.EnumerateArray().First()) : new { type = "string" }
            },
            JsonValueKind.Number => new { type = "number" },
            JsonValueKind.True or JsonValueKind.False => new { type = "boolean" },
            _ => new { type = "string" }
        };
    }
}
