using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocketJack.Net.AgentBuilder;

namespace SocketJack.Net;

public interface IPictureBankMediaExecutor
{
    Task<PictureBankCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<PictureBankMediaResult> ExecuteAsync(PictureBankMediaRequest request, IProgress<PictureBankProgress> progress, CancellationToken cancellationToken = default);
}

public sealed class PictureBankCapabilities
{
    public bool AiInstalled { get; set; }
    public bool AiReady { get; set; }
    public string Backend { get; set; } = "not-installed";
    public string CompatibilityMessage { get; set; } = "PictureBank editing is ready. Install the optional signed PictureBank AI bundle for local generation and assisted tools.";
    public bool PhotoshopParity { get; set; }
    public List<string> DocumentOperations { get; set; } = new() { "image-import", "aspect-ratio-preserving-import", "layers", "layer-order", "layer-translation", "layer-resize", "layer-rotation", "layer-flip", "brightness-hue-saturation", "document-resize", "visibility", "locking", "opacity", "blend-modes", "text-layers", "shape-layers", "solid-fill", "brush-strokes", "raster-erasing", "rectangular-selection", "selection-constrained-painting", "document-colors", "history", "undo-redo" };
    public List<string> AiOperations { get; set; } = new() { "generate", "image-to-image", "inpaint", "outpaint", "segment", "background-removal", "restore", "upscale" };
    public List<string> Formats { get; set; } = new() { "pbank-json-manifest" };
    public List<string> MissingProfessionalFeatures { get; set; } = new() { "pixel-accurate tiled raster engine", "masks and channels", "adjustment layers and filters", "smart objects", "typography shaping", "vector path editing", "ICC CMYK and Lab rendering", "PSD and PSB import/export", "RAW workflow", "animation and video", "3D", "production AI execution", "plugin SDK" };
    public string FeatureLevel { get; set; } = "Core editor foundation (not Photoshop parity)";
}

public sealed class PictureBankMediaRequest
{
    public string Operation { get; set; } = "generate";
    public string Prompt { get; set; } = "";
    public string NegativePrompt { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string MaskPath { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public double Strength { get; set; } = .75;
    public int OutpaintTop { get; set; }
    public int OutpaintRight { get; set; }
    public int OutpaintBottom { get; set; }
    public int OutpaintLeft { get; set; }
    public string ControlModel { get; set; } = "";
    public string TargetRegionJson { get; set; } = "";
    public int BatchSize { get; set; } = 1;
}

public sealed class PictureBankMediaResult { public bool Success { get; set; } public string Error { get; set; } = ""; public List<PictureBankArtifact> Artifacts { get; set; } = new(); }
public sealed class PictureBankArtifact { public string Id { get; set; } = "artifact_" + Guid.NewGuid().ToString("N"); public string FilePath { get; set; } = ""; public string MediaType { get; set; } = "image/png"; public string Operation { get; set; } = ""; }
public sealed class PictureBankProgress { public double Percent { get; set; } public string Message { get; set; } = ""; }

public sealed class PictureBankDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "pbank_" + Guid.NewGuid().ToString("N");
    public string OwnerId { get; set; } = "";
    public string Title { get; set; } = "Untitled";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public string ColorMode { get; set; } = "RGB";
    public int BitDepth { get; set; } = 8;
    public string IccProfile { get; set; } = "sRGB IEC61966-2.1";
    public string ForegroundColor { get; set; } = "#111827";
    public string BackgroundColor { get; set; } = "#ffffff";
    public int Revision { get; set; }
    public string SelectedLayerId { get; set; } = "";
    public PictureBankRegion Selection { get; set; }
    public List<PictureBankLayer> Layers { get; set; } = new();
    public List<PictureBankHistoryEntry> History { get; set; } = new();
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PictureBankLayer
{
    public string Id { get; set; } = "layer_" + Guid.NewGuid().ToString("N");
    public string Type { get; set; } = "raster";
    public string Name { get; set; } = "Layer";
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
    public double Opacity { get; set; } = 1;
    public string BlendMode { get; set; } = "normal";
    public string Content { get; set; } = "";
    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PictureBankRegion { public double X { get; set; } public double Y { get; set; } public double Width { get; set; } public double Height { get; set; } }
public sealed class PictureBankBrushStroke { public double X { get; set; } public double Y { get; set; } public double Size { get; set; } = 20; public double Opacity { get; set; } = 1; public string Color { get; set; } = "#111827"; public bool Erase { get; set; } public PictureBankRegion Clip { get; set; } }
public sealed class PictureBankHistoryEntry { public int Revision { get; set; } public string BatchId { get; set; } = ""; public string Label { get; set; } = ""; public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow; }
public sealed class PictureBankCommand { public string Type { get; set; } = ""; public string LayerId { get; set; } = ""; public Dictionary<string, string> Arguments { get; set; } = new(StringComparer.OrdinalIgnoreCase); }
public sealed class PictureBankCommandBatch { public string Id { get; set; } = "batch_" + Guid.NewGuid().ToString("N"); public string DocumentId { get; set; } = ""; public int BaseRevision { get; set; } public string Label { get; set; } = "Edit"; public List<PictureBankCommand> Commands { get; set; } = new(); }
public sealed class PictureBankAgentRequest { public string DocumentId { get; set; } = ""; public int BaseRevision { get; set; } public string Prompt { get; set; } = ""; public string SelectedLayerId { get; set; } = ""; public PictureBankRegion Selection { get; set; } }
public sealed class PictureBankAgentPlan { public string PlanId { get; set; } = "plan_" + Guid.NewGuid().ToString("N"); public string DocumentId { get; set; } = ""; public int BaseRevision { get; set; } public string Summary { get; set; } = ""; public string ApprovalToken { get; set; } = ""; public DateTimeOffset ExpiresUtc { get; set; } public PictureBankCommandBatch Batch { get; set; } = new(); public object ModelAnalysis { get; set; } }

internal sealed class PictureBankService
{
    private readonly string _root;
    private readonly object _sync = new();
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly ConcurrentDictionary<string, PictureBankApproval> _approvals = new(StringComparer.Ordinal);
    private readonly IPictureBankMediaExecutor _executor;
    private static readonly JsonSerializerOptions RegionJson = new() { PropertyNameCaseInsensitive = true };

    public PictureBankService(string root, IPictureBankMediaExecutor executor) { _root = Path.GetFullPath(root); _executor = executor; Directory.CreateDirectory(_root); }
    public async Task<PictureBankCapabilities> CapabilitiesAsync(CancellationToken token) => _executor == null ? new() : await _executor.GetCapabilitiesAsync(token).ConfigureAwait(false);
    public object List(string ownerKey) => new { ok = true, documents = Directory.Exists(OwnerRoot(ownerKey)) ? Directory.GetFiles(OwnerRoot(ownerKey), "*.pbank.json").Select(Read).Where(d => d != null).OrderByDescending(d => d.UpdatedUtc).Select(Redact).ToList() : new List<PictureBankDocument>() };

    public PictureBankDocument Create(PictureBankDocument requested, string ownerKey)
    {
        requested ??= new(); requested.Id = "pbank_" + Guid.NewGuid().ToString("N"); requested.OwnerId = OwnerId(ownerKey); requested.Title = Limit(requested.Title, 160, "Untitled"); requested.Width = Math.Clamp(requested.Width, 1, 300000); requested.Height = Math.Clamp(requested.Height, 1, 300000); requested.BitDepth = requested.BitDepth is 8 or 16 or 32 ? requested.BitDepth : 8; requested.ForegroundColor = NormalizeColor(requested.ForegroundColor, "#111827"); requested.BackgroundColor = NormalizeColor(requested.BackgroundColor, "#ffffff"); requested.Revision = 1; requested.CreatedUtc = requested.UpdatedUtc = DateTimeOffset.UtcNow;
        requested.Layers ??= new(); if (requested.Layers.Count == 0) requested.Layers.Add(new() { Name = "Background", Type = "raster" }); requested.SelectedLayerId = requested.Layers.Last().Id;
        requested.History = new() { new() { Revision = 1, BatchId = "create", Label = "Create document" } }; Save(requested, ownerKey, true); return Redact(requested);
    }

    public PictureBankDocument Get(string id, string ownerKey) { PictureBankDocument doc = Read(DocumentPath(id, ownerKey)) ?? throw new FileNotFoundException("PictureBank document not found."); Authorize(doc, ownerKey); return doc; }
    public PictureBankDocument Apply(PictureBankCommandBatch batch, string ownerKey)
    {
        if (batch == null || string.IsNullOrWhiteSpace(batch.DocumentId)) throw new InvalidOperationException("Document id is required.");
        lock (_sync)
        {
            PictureBankDocument doc = Get(batch.DocumentId, ownerKey); if (batch.BaseRevision != doc.Revision) throw new InvalidOperationException($"Revision conflict. Expected {doc.Revision}, received {batch.BaseRevision}.");
            SaveRevision(doc, ownerKey); foreach (PictureBankCommand command in batch.Commands ?? new()) ApplyCommand(doc, command);
            doc.Revision++; doc.UpdatedUtc = DateTimeOffset.UtcNow; doc.History.Add(new() { Revision = doc.Revision, BatchId = batch.Id, Label = Limit(batch.Label, 120, "Edit") }); Save(doc, ownerKey, false); return Redact(doc);
        }
    }

    public PictureBankDocument Rollback(string documentId, int revision, string ownerKey)
    {
        lock (_sync)
        {
            PictureBankDocument current = Get(documentId, ownerKey); string path = RevisionPath(documentId, revision, ownerKey); PictureBankDocument prior = Read(path) ?? throw new FileNotFoundException("Revision not found."); Authorize(prior, ownerKey); SaveRevision(current, ownerKey); prior.Revision = current.Revision + 1; prior.UpdatedUtc = DateTimeOffset.UtcNow; prior.History = current.History.ToList(); prior.History.Add(new() { Revision = prior.Revision, BatchId = "rollback_" + Guid.NewGuid().ToString("N"), Label = "Rollback to revision " + revision }); Save(prior, ownerKey, false); return Redact(prior);
        }
    }

    public PictureBankAgentPlan CreatePlan(PictureBankAgentRequest request, string ownerKey, object modelAnalysis)
    {
        PictureBankDocument doc = Get(request.DocumentId, ownerKey); if (request.BaseRevision != doc.Revision) throw new InvalidOperationException($"Revision conflict. Expected {doc.Revision}, received {request.BaseRevision}.");
        string layerId = string.IsNullOrWhiteSpace(request.SelectedLayerId) ? doc.SelectedLayerId : request.SelectedLayerId; PictureBankCommand command = BuildAgentCommand(request.Prompt, layerId, request.Selection ?? doc.Selection);
        var batch = new PictureBankCommandBatch { DocumentId = doc.Id, BaseRevision = doc.Revision, Label = "Agent: " + Limit(request.Prompt, 80, "edit"), Commands = new() { command } };
        byte[] tokenBytes = new byte[32]; using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(tokenBytes); string token = Hex(tokenBytes); var approval = new PictureBankApproval { Token = token, OwnerId = OwnerId(ownerKey), DocumentId = doc.Id, Revision = doc.Revision, ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10), Batch = batch }; _approvals[token] = approval;
        return new() { DocumentId = doc.Id, BaseRevision = doc.Revision, Summary = "Preview only: " + batch.Label, ApprovalToken = token, ExpiresUtc = approval.ExpiresUtc, Batch = batch, ModelAnalysis = modelAnalysis };
    }

    public PictureBankDocument ApplyPlan(string token, string ownerKey)
    {
        if (string.IsNullOrWhiteSpace(token) || !_approvals.TryRemove(token, out PictureBankApproval approval)) throw new UnauthorizedAccessException("Approval token is invalid or has already been used.");
        if (approval.ExpiresUtc <= DateTimeOffset.UtcNow || approval.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("Approval token is expired or belongs to another owner.");
        PictureBankDocument current = Get(approval.DocumentId, ownerKey); if (current.Revision != approval.Revision) throw new InvalidOperationException("The document changed after preview. Replan before applying.");
        return Apply(approval.Batch, ownerKey);
    }

    private static PictureBankCommand BuildAgentCommand(string prompt, string layerId, PictureBankRegion selection)
    {
        string text = (prompt ?? "").ToLowerInvariant();
        if (text.Contains("remove") && selection != null) return new() { Type = "contentAwareFill", LayerId = layerId, Arguments = new() { ["region"] = JsonSerializer.Serialize(selection), ["mode"] = "preview-approved" } };
        if (text.Contains("hide")) return new() { Type = "setVisibility", LayerId = layerId, Arguments = new() { ["visible"] = "false" } };
        if (text.Contains("duplicate")) return new() { Type = "duplicateLayer", LayerId = layerId };
        return new() { Type = "addLayer", Arguments = new() { ["name"] = "Agent edit", ["layerType"] = "generated", ["prompt"] = Limit(prompt, 2000, "") } };
    }

    private static void ApplyCommand(PictureBankDocument doc, PictureBankCommand command)
    {
        command ??= new(); command.Arguments ??= new(); PictureBankLayer layer = doc.Layers.FirstOrDefault(item => item.Id == command.LayerId);
        switch ((command.Type ?? "").Trim().ToLowerInvariant())
        {
            case "addlayer":
                var added = new PictureBankLayer { Name = Limit(Arg(command, "name", "Layer"), 160, "Layer"), Type = Limit(Arg(command, "layerType", "raster"), 40, "raster"), Content = Limit(Arg(command, "content", Arg(command, "prompt", "")), 10000, "") };
                foreach (string key in new[] { "fillColor", "strokeColor", "strokeWidth", "x", "y", "width", "height", "fontSize", "fontFamily" }) if (command.Arguments.TryGetValue(key, out string value)) added.Properties[key] = Limit(value, 200, "");
                doc.Layers.Add(added); doc.SelectedLayerId = added.Id; break;
            case "importimage":
                string imageData = ValidateImageDataUrl(Arg(command, "content", ""));
                var imported = new PictureBankLayer { Name = Limit(Arg(command, "name", "Imported image"), 160, "Imported image"), Type = "raster", Content = imageData };
                imported.Properties["sourceName"] = Limit(Arg(command, "sourceName", imported.Name), 260, imported.Name);
                imported.Properties["mimeType"] = Limit(Arg(command, "mimeType", "image/png"), 80, "image/png");
                double sourceWidth = Math.Clamp(ParseDouble(command, "width", doc.Width), 1, 100000);
                double sourceHeight = Math.Clamp(ParseDouble(command, "height", doc.Height), 1, 100000);
                double containScale = Math.Min(1d, Math.Min(doc.Width / sourceWidth, doc.Height / sourceHeight));
                imported.Properties["sourceWidth"] = sourceWidth.ToString(System.Globalization.CultureInfo.InvariantCulture);
                imported.Properties["sourceHeight"] = sourceHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);
                imported.Properties["width"] = (sourceWidth * containScale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                imported.Properties["height"] = (sourceHeight * containScale).ToString(System.Globalization.CultureInfo.InvariantCulture);
                imported.Properties["aspectRatioLocked"] = "true";
                imported.Properties["offsetX"] = "0"; imported.Properties["offsetY"] = "0";
                doc.Layers.Add(imported); doc.SelectedLayerId = imported.Id; break;
            case "duplicatelayer": if (layer == null) throw new InvalidOperationException("Selected layer not found."); var copy = new PictureBankLayer { Name = layer.Name + " copy", Type = layer.Type, Visible = layer.Visible, Locked = false, Opacity = layer.Opacity, BlendMode = layer.BlendMode, Content = layer.Content, Properties = new(layer.Properties, StringComparer.OrdinalIgnoreCase) }; doc.Layers.Add(copy); doc.SelectedLayerId = copy.Id; break;
            case "deletelayer": if (layer == null) throw new InvalidOperationException("Layer not found."); doc.Layers.Remove(layer); doc.SelectedLayerId = doc.Layers.LastOrDefault()?.Id ?? ""; break;
            case "renamelayer": RequireEditable(layer); layer.Name = Limit(Arg(command, "name", layer.Name), 160, layer.Name); break;
            case "setvisibility": if (layer == null) throw new InvalidOperationException("Layer not found."); layer.Visible = bool.TryParse(Arg(command, "visible", "true"), out bool visible) && visible; break;
            case "setopacity": RequireEditable(layer); layer.Opacity = double.TryParse(Arg(command, "opacity", "1"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double opacity) ? Math.Clamp(opacity, 0, 1) : layer.Opacity; break;
            case "setblendmode": RequireEditable(layer); string blendMode = Arg(command, "blendMode", "normal").Trim().ToLowerInvariant(); if (!AllowedBlendModes.Contains(blendMode)) throw new InvalidOperationException("Unsupported blend mode."); layer.BlendMode = blendMode; break;
            case "setlocked": if (layer == null) throw new InvalidOperationException("Layer not found."); layer.Locked = bool.TryParse(Arg(command, "locked", "true"), out bool locked) && locked; break;
            case "movelayer":
                if (layer == null) throw new InvalidOperationException("Layer not found."); int oldIndex = doc.Layers.IndexOf(layer); string direction = Arg(command, "direction", "up").ToLowerInvariant(); int newIndex = direction switch { "front" => doc.Layers.Count - 1, "back" => 0, "down" => Math.Max(0, oldIndex - 1), _ => Math.Min(doc.Layers.Count - 1, oldIndex + 1) }; doc.Layers.RemoveAt(oldIndex); doc.Layers.Insert(newIndex, layer); break;
            case "translatelayer":
                RequireEditable(layer); layer.Properties["offsetX"] = (ParsePropertyDouble(layer, "offsetX") + ParseDouble(command, "deltaX", 0)).ToString(System.Globalization.CultureInfo.InvariantCulture); layer.Properties["offsetY"] = (ParsePropertyDouble(layer, "offsetY") + ParseDouble(command, "deltaY", 0)).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "resizelayer":
                RequireEditable(layer);
                double oldWidth = Math.Max(1, ParsePropertyDouble(layer, "width"));
                double oldHeight = Math.Max(1, ParsePropertyDouble(layer, "height"));
                double requestedWidth = Math.Clamp(ParseDouble(command, "width", oldWidth), 1, 300000);
                double requestedHeight = Math.Clamp(ParseDouble(command, "height", oldHeight), 1, 300000);
                bool preserveAspect = !bool.TryParse(Arg(command, "preserveAspect", "true"), out bool parsedPreserveAspect) || parsedPreserveAspect;
                if (preserveAspect)
                {
                    double ratio = oldWidth / oldHeight;
                    if (bool.TryParse(Arg(command, "widthChanged", "true"), out bool widthChanged) && widthChanged) requestedHeight = requestedWidth / ratio;
                    else requestedWidth = requestedHeight * ratio;
                }
                layer.Properties["width"] = Math.Clamp(requestedWidth, 1, 300000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                layer.Properties["height"] = Math.Clamp(requestedHeight, 1, 300000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                layer.Properties["aspectRatioLocked"] = preserveAspect.ToString().ToLowerInvariant(); break;
            case "rotatelayer": RequireEditable(layer); layer.Properties["rotation"] = NormalizeDegrees(ParseDouble(command, "degrees", ParsePropertyDouble(layer, "rotation"))).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "fliplayer":
                RequireEditable(layer); string flipAxis = Arg(command, "axis", "horizontal").ToLowerInvariant(); string flipKey = flipAxis == "vertical" ? "flipY" : "flipX"; layer.Properties[flipKey] = (!(layer.Properties.TryGetValue(flipKey, out string flipValue) && bool.TryParse(flipValue, out bool flipped) && flipped)).ToString().ToLowerInvariant(); break;
            case "resetlayertransform": RequireEditable(layer); layer.Properties["rotation"] = "0"; layer.Properties["flipX"] = "false"; layer.Properties["flipY"] = "false"; break;
            case "setlayercolor":
                RequireEditable(layer); layer.Properties["brightness"] = Math.Clamp(ParseDouble(command, "brightness", ParsePropertyDouble(layer, "brightness", 1)), 0, 4).ToString(System.Globalization.CultureInfo.InvariantCulture); layer.Properties["hue"] = NormalizeDegrees(ParseDouble(command, "hue", ParsePropertyDouble(layer, "hue"))).ToString(System.Globalization.CultureInfo.InvariantCulture); layer.Properties["saturation"] = Math.Clamp(ParseDouble(command, "saturation", ParsePropertyDouble(layer, "saturation", 1)), 0, 4).ToString(System.Globalization.CultureInfo.InvariantCulture); break;
            case "resetlayercolor": RequireEditable(layer); layer.Properties["brightness"] = "1"; layer.Properties["hue"] = "0"; layer.Properties["saturation"] = "1"; break;
            case "resizedocument": doc.Width = Math.Clamp((int)Math.Round(ParseDouble(command, "width", doc.Width)), 1, 300000); doc.Height = Math.Clamp((int)Math.Round(ParseDouble(command, "height", doc.Height)), 1, 300000); break;
            case "selectlayer": if (layer == null) throw new InvalidOperationException("Layer not found."); doc.SelectedLayerId = layer.Id; break;
            case "setselection": doc.Selection = JsonSerializer.Deserialize<PictureBankRegion>(Arg(command, "region", "{}"), RegionJson); break;
            case "clearselection": doc.Selection = null; break;
            case "setdocumentcolors": doc.ForegroundColor = NormalizeColor(Arg(command, "foreground", doc.ForegroundColor), doc.ForegroundColor); doc.BackgroundColor = NormalizeColor(Arg(command, "background", doc.BackgroundColor), doc.BackgroundColor); break;
            case "setlayerfill": RequireEditable(layer); layer.Properties["fillColor"] = NormalizeColor(Arg(command, "color", doc.ForegroundColor), doc.ForegroundColor); break;
            case "appendbrushstroke":
                RequireEditable(layer); if (!layer.Type.Equals("raster", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Brush and eraser edits require a rasterized layer."); List<PictureBankBrushStroke> strokes; try { strokes = JsonSerializer.Deserialize<List<PictureBankBrushStroke>>(layer.Properties.TryGetValue("strokes", out string json) ? json : "[]") ?? new(); } catch { strokes = new(); }
                PictureBankRegion clip = null; string clipJson = Arg(command, "clip", ""); if (!string.IsNullOrWhiteSpace(clipJson)) { try { clip = JsonSerializer.Deserialize<PictureBankRegion>(clipJson, RegionJson); } catch { clip = null; } }
                if (strokes.Count >= 10000) throw new InvalidOperationException("This layer has reached the brush-stroke safety limit."); strokes.Add(new() { X = ParseDouble(command, "x", 0), Y = ParseDouble(command, "y", 0), Size = Math.Clamp(ParseDouble(command, "size", 20), .5, 2000), Opacity = Math.Clamp(ParseDouble(command, "opacity", 1), 0, 1), Color = NormalizeColor(Arg(command, "color", doc.ForegroundColor), doc.ForegroundColor), Erase = Arg(command, "mode", "paint").Equals("erase", StringComparison.OrdinalIgnoreCase), Clip = clip != null && clip.Width > 0 && clip.Height > 0 ? clip : null }); layer.Properties["strokes"] = JsonSerializer.Serialize(strokes); break;
            case "contentawarefill": RequireEditable(layer); layer.Properties["lastContentAwareRegion"] = Arg(command, "region", "{}"); break;
            default: throw new InvalidOperationException("Unsupported PictureBank command: " + command.Type);
        }
    }

    private static readonly HashSet<string> AllowedBlendModes = new(StringComparer.OrdinalIgnoreCase) { "normal", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn", "hard-light", "soft-light", "difference", "exclusion", "hue", "saturation", "color", "luminosity" };
    private static void RequireEditable(PictureBankLayer layer) { if (layer == null) throw new InvalidOperationException("Layer not found."); if (layer.Locked) throw new InvalidOperationException("Unlock the layer before editing it."); }
    private static double ParseDouble(PictureBankCommand command, string name, double fallback) => double.TryParse(Arg(command, name, fallback.ToString(System.Globalization.CultureInfo.InvariantCulture)), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : fallback;
    private static double ParsePropertyDouble(PictureBankLayer layer, string name, double fallback = 0) => layer != null && layer.Properties.TryGetValue(name, out string value) && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;
    private static double NormalizeDegrees(double value) { value %= 360; return value > 180 ? value - 360 : value <= -180 ? value + 360 : value; }
    private static string NormalizeColor(string value, string fallback) { value = (value ?? "").Trim(); return System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{6}$") ? value.ToLowerInvariant() : fallback; }
    private static string ValidateImageDataUrl(string value)
    {
        string dataUrl = (value ?? "").Trim(); var match = System.Text.RegularExpressions.Regex.Match(dataUrl, "^data:(image/(?:png|jpeg|webp|gif|bmp|avif));base64,([A-Za-z0-9+/=\\r\\n]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Import supports PNG, JPEG, WebP, GIF, BMP, and AVIF images.");
        byte[] bytes; try { bytes = Convert.FromBase64String(match.Groups[2].Value); } catch { throw new InvalidOperationException("The imported image data is malformed."); }
        if (bytes.Length == 0 || bytes.Length > 25 * 1024 * 1024) throw new InvalidOperationException("Imported images must be between 1 byte and 25 MiB.");
        return "data:" + match.Groups[1].Value.ToLowerInvariant() + ";base64," + Convert.ToBase64String(bytes);
    }

    private void Save(PictureBankDocument doc, string ownerKey, bool failIfExists) { string path = DocumentPath(doc.Id, ownerKey); if (failIfExists && File.Exists(path)) throw new IOException("Document already exists."); AtomicWrite(path, JsonSerializer.Serialize(doc, _json)); }
    private void SaveRevision(PictureBankDocument doc, string ownerKey) { string path = RevisionPath(doc.Id, doc.Revision, ownerKey); Directory.CreateDirectory(Path.GetDirectoryName(path)!); AtomicWrite(path, JsonSerializer.Serialize(doc, _json)); }
    private PictureBankDocument Read(string path) { try { return File.Exists(path) ? JsonSerializer.Deserialize<PictureBankDocument>(File.ReadAllText(path), _json) : null; } catch { return null; } }
    private string OwnerRoot(string ownerKey) { string path = Path.Combine(_root, OwnerId(ownerKey)); Directory.CreateDirectory(path); return path; }
    private string DocumentPath(string id, string ownerKey) { ValidateId(id); return Path.Combine(OwnerRoot(ownerKey), id + ".pbank.json"); }
    private string RevisionPath(string id, int revision, string ownerKey) { ValidateId(id); return Path.Combine(OwnerRoot(ownerKey), "revisions", id, revision.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"); }
    private static void ValidateId(string id) { if (string.IsNullOrWhiteSpace(id) || id.Any(ch => !(char.IsLetterOrDigit(ch) || ch == '_' || ch == '-'))) throw new InvalidOperationException("Invalid document id."); }
    private static string OwnerId(string ownerKey) { using SHA256 sha = SHA256.Create(); return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(ownerKey ?? ""))); }
    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    private static void Authorize(PictureBankDocument doc, string ownerKey) { if (doc.OwnerId != OwnerId(ownerKey)) throw new UnauthorizedAccessException("PictureBank document belongs to another owner."); }
    private static string Arg(PictureBankCommand command, string name, string fallback) => command.Arguments.TryGetValue(name, out string value) ? value : fallback;
    private static string Limit(string value, int max, string fallback) { value = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim(); return value.Length <= max ? value : value[..max]; }
    private static PictureBankDocument Redact(PictureBankDocument doc) => JsonSerializer.Deserialize<PictureBankDocument>(JsonSerializer.Serialize(doc))!;
    private static void AtomicWrite(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temp, path, null, true);
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
    private sealed class PictureBankApproval { public string Token { get; set; } = ""; public string OwnerId { get; set; } = ""; public string DocumentId { get; set; } = ""; public int Revision { get; set; } public DateTimeOffset ExpiresUtc { get; set; } public PictureBankCommandBatch Batch { get; set; } = new(); }
}

public partial class HeirowLlm
{
    private PictureBankService _pictureBank;
    public IPictureBankMediaExecutor PictureBankMediaExecutor { get; set; }
    public Func<CancellationToken, Task<string>> PictureBankInstallRequestedAsync { get; set; }
    private static readonly JsonSerializerOptions PictureBankJsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private void RegisterPictureBankRoutes(HttpServer server)
    {
        _pictureBank ??= new PictureBankService(Path.Combine(_chatSessionRoot, "PictureBank"), PictureBankMediaExecutor);
        string html = HtmlPageResources.GetHtml("PictureBank.html");
        foreach (string path in new[] { "/PictureBank", "/PictureBank/", "/picturebank", "/picturebank/", "/apps/picturebank" }) server.Map("GET", path, (_, request, _) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/assets/picturebank-chicken-chaser-sprites.png", (_, request, _) => BuildEmbeddedChatAssetResponse(request, "picturebank-chicken-chaser-sprites.png", "image/png"));
        server.Map("GET", "/api/picturebank/capabilities", (_, request, token) => PictureBankJson(() => _pictureBank.CapabilitiesAsync(token).GetAwaiter().GetResult(), request));
        server.Map("POST", "/api/picturebank/install", (connection, request, token) => PictureBankJson(() => { _ = GetChatSessionOwnerKey(connection, request); var installer = PictureBankInstallRequestedAsync ?? throw new InvalidOperationException("The PictureBank AI installer is unavailable in this host."); return new { ok = true, message = installer(token).GetAwaiter().GetResult() }; }, request));
        server.Map("GET", "/api/picturebank/documents", (connection, request, _) => PictureBankJson(() => _pictureBank.List(GetChatSessionOwnerKey(connection, request)), request));
        server.Map("POST", "/api/picturebank/documents", (connection, request, _) => PictureBankJson(() => new { ok = true, document = _pictureBank.Create(JsonSerializer.Deserialize<PictureBankDocument>(request.Body ?? "{}", PictureBankJsonOptions), GetChatSessionOwnerKey(connection, request)) }, request));
        server.Map("GET", "/api/picturebank/documents/*", (connection, request, _) => PictureBankJson(() => new { ok = true, document = _pictureBank.Get(request.PathVariables?.FirstOrDefault(), GetChatSessionOwnerKey(connection, request)) }, request));
        server.Map("POST", "/api/picturebank/commands", (connection, request, _) => PictureBankJson(() => new { ok = true, document = _pictureBank.Apply(JsonSerializer.Deserialize<PictureBankCommandBatch>(request.Body ?? "{}", PictureBankJsonOptions), GetChatSessionOwnerKey(connection, request)) }, request));
        server.Map("POST", "/api/picturebank/rollback", (connection, request, _) => PictureBankJson(() => { using JsonDocument body = JsonDocument.Parse(request.Body ?? "{}"); return new { ok = true, document = _pictureBank.Rollback(body.RootElement.GetProperty("documentId").GetString(), body.RootElement.GetProperty("revision").GetInt32(), GetChatSessionOwnerKey(connection, request)) }; }, request));
        server.Map("POST", "/api/picturebank/agent/preview", (connection, request, _) => PictureBankJson(() => { PictureBankAgentRequest planRequest = JsonSerializer.Deserialize<PictureBankAgentRequest>(request.Body ?? "{}", PictureBankJsonOptions) ?? new(); PictureBankDocument document = _pictureBank.Get(planRequest.DocumentId, GetChatSessionOwnerKey(connection, request)); object analysis = AnalyzePictureBankRequest(connection, request, document, planRequest); return new { ok = true, plan = _pictureBank.CreatePlan(planRequest, GetChatSessionOwnerKey(connection, request), analysis) }; }, request));
        server.Map("POST", "/api/picturebank/agent/apply", (connection, request, _) => PictureBankJson(() => { using JsonDocument body = JsonDocument.Parse(request.Body ?? "{}"); return new { ok = true, document = _pictureBank.ApplyPlan(body.RootElement.GetProperty("approvalToken").GetString(), GetChatSessionOwnerKey(connection, request)) }; }, request));
        server.Map("GET", "/api/picturebank/application", (connection, request, _) => PictureBankJson(() => { string owner = GetChatSessionOwnerKey(connection, request); AgentBuilderWorkflow workflow = EnsurePictureBankPresetInstance(owner); return new { ok = true, workflowId = workflow.Id, revision = workflow.Revision, presetVersion = workflow.PresetVersion, application = workflow.Application }; }, request));
    }

    private object AnalyzePictureBankRequest(NetworkConnection connection, HttpRequest sourceRequest, PictureBankDocument document, PictureBankAgentRequest request)
    {
        var context = new { document.Id, document.Revision, document.Title, document.Width, document.Height, document.ColorMode, document.BitDepth, document.IccProfile, document.ForegroundColor, document.BackgroundColor, selectedLayerId = request.SelectedLayerId, selection = request.Selection ?? document.Selection, layers = document.Layers.Select(layer => new { layer.Id, layer.Name, layer.Type, layer.Visible, layer.Locked, layer.Opacity, layer.BlendMode, propertyKeys = layer.Properties.Keys.Take(24) }), history = document.History.TakeLast(12) };
        string mediatedPrompt = "You are the PictureBank editing planner. Inspect the current document context before the request. Do not use terminal, filesystem, internet, or raw JavaScript. Return a concise explanation of safe, reversible PictureBank commands; the server will validate and preview them.\nContext:\n" + JsonSerializer.Serialize(context, PictureBankJsonOptions) + "\nUser request:\n" + request.Prompt;
        try { return RunLocalAgentBuilderPrompt(connection, sourceRequest, new AgentBuilderNode { Type = "agent", Config = new() { ["model"] = "auto" } }, mediatedPrompt, sourceRequest.Context?.cancellationToken ?? default); }
        catch (Exception ex) { return new { offline = true, message = "The structured preview was created without model commentary.", error = ex.Message }; }
    }

    private object PictureBankJson(Func<object> action, HttpRequest request)
    {
        AddWebAuthCorsHeaders(request); try { request.Context.Response.ContentType = "application/json; charset=utf-8"; return JsonSerializer.Serialize(action(), PictureBankJsonOptions); }
        catch (UnauthorizedAccessException ex) { return BuildJsonError(request, 403, "Forbidden", ex.Message); } catch (FileNotFoundException ex) { return BuildJsonError(request, 404, "Not Found", ex.Message); } catch (InvalidOperationException ex) { return BuildJsonError(request, 409, "Conflict", ex.Message); } catch (Exception ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
    }
}
