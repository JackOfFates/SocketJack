using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.Net.AgentBuilder;

public sealed class AgentBuilderApplicationDefinition
{
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "Untitled application";
    public string Description { get; set; } = "";
    public string StartRoute { get; set; } = "";
    public string Theme { get; set; } = "dark";
    public Dictionary<string, string> ThemeTokens { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> State { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Permissions { get; set; } = new();
    public List<AgentBuilderUiComponent> Components { get; set; } = new();
    public List<AgentBuilderAppRoute> Routes { get; set; } = new();
    public List<AgentBuilderDataSource> DataSources { get; set; } = new();
    public List<AgentBuilderAsset> Assets { get; set; } = new();
    public List<AgentBuilderLayoutPreset> LayoutPresets { get; set; } = new();

    public void Normalize()
    {
        Slug = AgentBuilderSlug.Normalize(Slug);
        Title = string.IsNullOrWhiteSpace(Title) ? "Untitled application" : Title.Trim();
        Theme = string.IsNullOrWhiteSpace(Theme) ? "dark" : Theme.Trim().ToLowerInvariant();
        ThemeTokens ??= new(StringComparer.OrdinalIgnoreCase);
        State ??= new(StringComparer.OrdinalIgnoreCase);
        Permissions ??= new(); Components ??= new(); Routes ??= new(); DataSources ??= new(); Assets ??= new(); LayoutPresets ??= new();
        foreach (AgentBuilderUiComponent component in Components) component.Normalize();
    }
}

public sealed class AgentBuilderUiComponent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "panel";
    public string ParentId { get; set; } = "";
    public string Slot { get; set; } = "main";
    public int Order { get; set; }
    public bool Movable { get; set; }
    public bool Resizable { get; set; }
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Bindings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<AgentBuilderUiComponent> Children { get; set; } = new();
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(Id)) Id = "component_" + Guid.NewGuid().ToString("N");
        Type = (Type ?? "panel").Trim().ToLowerInvariant(); ParentId = (ParentId ?? "").Trim(); Slot = (Slot ?? "main").Trim().ToLowerInvariant();
        Properties ??= new(StringComparer.OrdinalIgnoreCase); Bindings ??= new(StringComparer.OrdinalIgnoreCase); Events ??= new(StringComparer.OrdinalIgnoreCase); Children ??= new();
        foreach (AgentBuilderUiComponent child in Children) child.Normalize();
    }
}

public sealed class AgentBuilderAppRoute { public string Path { get; set; } = ""; public string ComponentId { get; set; } = ""; }
public sealed class AgentBuilderDataSource { public string Id { get; set; } = ""; public string Kind { get; set; } = "api"; public string Endpoint { get; set; } = ""; public List<string> Methods { get; set; } = new(); }
public sealed class AgentBuilderAsset { public string Id { get; set; } = ""; public string Kind { get; set; } = "image"; public string Uri { get; set; } = ""; public string Sha256 { get; set; } = ""; }
public sealed class AgentBuilderLayoutPreset { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public Dictionary<string, string> PanelSlots { get; set; } = new(StringComparer.OrdinalIgnoreCase); }

public sealed class AgentBuilderPresetDefinition
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool ReadOnly { get; set; } = true;
    public string BaseHash { get; set; } = "";
    public AgentBuilderWorkflow Workflow { get; set; } = new();
}

public static class AgentBuilderApplicationValidator
{
    public static readonly HashSet<string> SafeComponentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "app-shell", "menu", "toolbar", "form", "tree", "grid", "dock-host", "canvas", "media-viewer", "timeline", "property-inspector", "status-bar", "agent-panel", "panel", "tabs", "button", "text", "picturebank-studio"
    };

    public static List<AgentBuilderWorkflowValidationIssue> Validate(AgentBuilderApplicationDefinition application)
    {
        var issues = new List<AgentBuilderWorkflowValidationIssue>();
        if (application == null) return issues;
        application.Normalize();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AgentBuilderUiComponent component in Flatten(application.Components))
        {
            if (!ids.Add(component.Id)) issues.Add(new() { Code = "duplicate_component_id", Message = "Duplicate UI component id: " + component.Id });
            if (!SafeComponentTypes.Contains(component.Type)) issues.Add(new() { Code = "unsafe_component_type", Message = "UI component type is not registered: " + component.Type });
            foreach (string value in component.Properties.Values.Concat(component.Events.Values))
                if (ContainsExecutableMarkup(value)) issues.Add(new() { Code = "executable_ui_content", Message = "Scripts and executable HTML are not allowed in application definitions." });
        }
        foreach (AgentBuilderDataSource source in application.DataSources)
            if (!string.IsNullOrWhiteSpace(source.Endpoint) && !source.Endpoint.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                issues.Add(new() { Code = "external_data_source", Message = "Application data sources must use authenticated local /api routes." });
        return issues;
    }

    private static IEnumerable<AgentBuilderUiComponent> Flatten(IEnumerable<AgentBuilderUiComponent> components)
    {
        foreach (AgentBuilderUiComponent item in components ?? Array.Empty<AgentBuilderUiComponent>()) { yield return item; foreach (AgentBuilderUiComponent child in Flatten(item.Children)) yield return child; }
    }
    private static bool ContainsExecutableMarkup(string value) => !string.IsNullOrWhiteSpace(value) && (value.Contains("<script", StringComparison.OrdinalIgnoreCase) || value.Contains("javascript:", StringComparison.OrdinalIgnoreCase) || value.Contains("onerror=", StringComparison.OrdinalIgnoreCase));
}

public static class PictureBankAgentBuilderPreset
{
    public const string PresetId = "picturebank";
    public const int Version = 3;
    public static AgentBuilderPresetDefinition Create()
    {
        AgentBuilderWorkflow workflow = CreateWorkflow("");
        string canonical = AgentBuilderJson.Serialize(workflow.Application);
        string hash;
        using (SHA256 sha = SHA256.Create())
            hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical))).Replace("-", "").ToLowerInvariant();
        workflow.BasePresetHash = hash;
        return new() { Id = PresetId, Version = Version, Name = "PictureBank", Description = "Local non-destructive image studio with a context-aware heirowLLM agent.", BaseHash = hash, Workflow = workflow };
    }

    public static AgentBuilderWorkflow CreateWorkflow(string owner)
    {
        var app = new AgentBuilderApplicationDefinition
        {
            Slug = "picturebank", Title = "PictureBank", Description = "Agent-built local image studio", StartRoute = "/PictureBank",
            Permissions = new() { "PictureBank" },
            ThemeTokens = new() { ["accent"] = "#8b5cf6", ["surface"] = "#11131a", ["canvas"] = "#20232b" },
            DataSources = new() { new() { Id = "picturebank-api", Endpoint = "/api/picturebank", Methods = new() { "GET", "POST" } } },
            LayoutPresets = new()
            {
                new() { Id = "essentials", Name = "Essentials", PanelSlots = new() { ["history"] = "left", ["layers"] = "right", ["properties"] = "right", ["agent"] = "right" } },
                new() { Id = "focus", Name = "Canvas Focus", PanelSlots = new() { ["history"] = "left", ["layers"] = "right", ["properties"] = "hidden", ["agent"] = "hidden" } }
            },
            Components = new()
            {
                new() { Id = "picturebank-shell", Type = "app-shell", Slot = "root", Children = new()
                {
                    new() { Id = "app-menu", Type = "menu", Slot = "top" }, new() { Id = "shortcut-toolbar", Type = "toolbar", Slot = "top", Movable = true, Properties = new() { ["tools"] = "move,select,brush,eraser,eyedropper,text,shape,crop,zoom", ["colors"] = "foreground,background,swap,default" } },
                    new() { Id = "studio", Type = "picturebank-studio", Slot = "main", Resizable = true, Properties = new() { ["featureLevel"] = "core-editor-foundation", ["photoshopParity"] = "false" } },
                    new() { Id = "layers", Type = "tree", Slot = "right", Movable = true, Resizable = true, Properties = new() { ["title"] = "Layers" } },
                    new() { Id = "properties", Type = "property-inspector", Slot = "right", Movable = true, Resizable = true, Properties = new() { ["title"] = "Properties" } },
                    new() { Id = "history", Type = "panel", Slot = "left", Movable = true, Resizable = true, Properties = new() { ["title"] = "History" } },
                    new() { Id = "agent", Type = "agent-panel", Slot = "right", Movable = true, Resizable = true, Properties = new() { ["title"] = "heirowLLM Agent" } },
                    new() { Id = "timeline", Type = "timeline", Slot = "bottom", Movable = true, Resizable = true }, new() { Id = "status", Type = "status-bar", Slot = "bottom" }
                } }
            }
        };
        var workflow = new AgentBuilderWorkflow
        {
            Id = "app_picturebank", OwnerUserName = owner, Name = "PictureBank", Description = app.Description, PresetId = PresetId, PresetVersion = Version, Application = app,
            Nodes = new() { new() { Id = "picturebank_request", Type = "input", Name = "PictureBank request", Config = new() { ["key"] = "request" } }, new() { Id = "picturebank_agent", Type = "agent", Name = "PictureBank agent", Config = new() { ["prompt"] = "Use the supplied PictureBank document context and propose only structured PictureBank commands for: {{request}}" } }, new() { Id = "picturebank_return", Type = "return", Name = "Commands", Config = new() { ["source"] = "$picturebank_agent" } } },
            Edges = new() { new() { SourceId = "picturebank_request", TargetId = "picturebank_agent" }, new() { SourceId = "picturebank_agent", TargetId = "picturebank_return" } }
        };
        workflow.Normalize(); return workflow;
    }
}
