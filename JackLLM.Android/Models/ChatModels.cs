using System.Collections.ObjectModel;

namespace JackLLM.Mobile.Models;

public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _role = "user";
    private string _content = "";
    private string _reasoning = "";
    private string _status = "";
    private string _telemetry = "";
    private string _workSummary = "";
    private bool _isReasoningExpanded = true;
    private bool _isGenerating;
    private string _routeSummary = "";

    public string Role { get => _role; set { if (_role == value) return; _role = value; PropertyChanged?.Invoke(this, new(nameof(Role))); PropertyChanged?.Invoke(this, new(nameof(IsUser))); PropertyChanged?.Invoke(this, new(nameof(BubbleColor))); } }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); PropertyChanged?.Invoke(this, new(nameof(HasContent))); } }
    public string Reasoning { get => _reasoning; set { if (_reasoning == value) return; _reasoning = value; PropertyChanged?.Invoke(this, new(nameof(Reasoning))); PropertyChanged?.Invoke(this, new(nameof(HasReasoning))); PropertyChanged?.Invoke(this, new(nameof(ShowReasoning))); } }
    public string Status { get => _status; set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); PropertyChanged?.Invoke(this, new(nameof(HasStatus))); } }
    public string Telemetry { get => _telemetry; set { if (_telemetry == value) return; _telemetry = value; PropertyChanged?.Invoke(this, new(nameof(Telemetry))); PropertyChanged?.Invoke(this, new(nameof(HasTelemetry))); } }
    public string WorkSummary { get => _workSummary; set { if (_workSummary == value) return; _workSummary = value; PropertyChanged?.Invoke(this, new(nameof(WorkSummary))); PropertyChanged?.Invoke(this, new(nameof(HasWorkSummary))); } }
    public bool IsReasoningExpanded { get => _isReasoningExpanded; set { if (_isReasoningExpanded == value) return; _isReasoningExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsReasoningExpanded))); PropertyChanged?.Invoke(this, new(nameof(ReasoningChevron))); } }
    public bool IsGenerating { get => _isGenerating; set { if (_isGenerating == value) return; _isGenerating = value; PropertyChanged?.Invoke(this, new(nameof(IsGenerating))); PropertyChanged?.Invoke(this, new(nameof(ReasoningHeader))); PropertyChanged?.Invoke(this, new(nameof(ShowReasoning))); } }
    public string RouteSummary { get => _routeSummary; set { if (_routeSummary == value) return; _routeSummary = value; PropertyChanged?.Invoke(this, new(nameof(RouteSummary))); PropertyChanged?.Invoke(this, new(nameof(HasRouteSummary))); } }
    public bool HasRouteSummary => !string.IsNullOrWhiteSpace(RouteSummary);
    public ObservableCollection<ToolActivity> Tools { get; } = new();
    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public bool ShowReasoning => IsGenerating || HasReasoning;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasTelemetry => !string.IsNullOrWhiteSpace(Telemetry);
    public bool HasWorkSummary => !string.IsNullOrWhiteSpace(WorkSummary);
    public string ReasoningHeader => IsGenerating ? "Thinking…" : "Thinking process";
    public string ReasoningChevron => IsReasoningExpanded ? "⌃" : "⌄";
    public bool IsCapturingEmbeddedReasoning { get; set; }
    public bool IsLocalOnly { get; set; }
    public string GenerationId { get; set; } = "";
    public Color BubbleColor => IsUser ? Color.FromArgb("#2563EB") : Color.FromArgb("#1F2937");
    public Color TextColor => Colors.White;
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ToolActivity
{
    public string Name { get; set; } = "Tool";
    public string Status { get; set; } = "running";
    public string Detail { get; set; } = "";
    public override string ToString() => string.Join(" · ", new[] { Name, Status, Detail }.Where(value => !string.IsNullOrWhiteSpace(value)));
}

public sealed class JackhammerPlanStep
{
    public string Status { get; init; } = "pending";
    public string Action { get; init; } = "";
}

public sealed class ChatStreamEvent
{
    public string Type { get; init; } = "unknown";
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public string Status { get; init; } = "";
    public double? Progress { get; init; }
    public string ToolName { get; init; } = "";
    public string ToolStatus { get; init; } = "";
    public string ToolDetail { get; init; } = "";
    public long TokenDelta { get; init; }
    public long TokensUsed { get; init; }
    public double GpuSecondsUsed { get; init; }
    public double CpuComputeSecondsUsed { get; init; }
    public double RamGbSecondsUsed { get; init; }
    public long PromptTokensLoaded { get; init; }
    public long PromptTokensTotal { get; init; }
    public string RawJson { get; init; } = "";
    public string RoutedModel { get; init; } = "";
    public string ReasoningLevel { get; init; } = "";
    public string RouteReason { get; init; } = "";
    public string PromptFingerprint { get; init; } = "";
    public IReadOnlyList<JackhammerPlanStep> JackhammerSteps { get; init; } = Array.Empty<JackhammerPlanStep>();
    public MobileAlignmentSnapshot? Alignment { get; init; }
}

public sealed class MobileAlignmentSnapshot
{
    public int Score { get; set; }
    public string Tier { get; set; } = "Neutral";
    public string Edge { get; set; } = "top";
    public string Theme { get; set; } = "neutral";
    public string LastReason { get; set; } = "Every Hero chooses a path.";
    public Dictionary<string, int> CharacterTraits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string AssessmentModel { get; set; } = "";
    public string ChecksAndBalancesStatus { get; set; } = "waiting-for-dream";
    public string ChecksAndBalancesDreamId { get; set; } = "";
    public string ChecksAndBalancesCompletedUtc { get; set; } = "";
    public string[] DisabledFeatures { get; set; } = Array.Empty<string>();
    public string[] HighlightedFeatures { get; set; } = Array.Empty<string>();
    public bool DreamsEnabled { get; set; } = true;
    public bool PendingReview { get; set; }
    public bool Locked { get; set; }
    public string RecoveryGuidance { get; set; } = "You can only help others after you help yourself.";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class ChatSessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string Runtime { get; set; } = "";
    public string ProjectId { get; set; } = "unsorted";
    public string ProjectName { get; set; } = "Unsorted";
    public bool Pinned { get; set; }
    public string PinnedUtc { get; set; } = "";
    public int MessageCount { get; set; }
    public int FileCount { get; set; }
    public int CommentCount { get; set; }
    public long PromptTokenCount { get; set; }
    public long PromptTokenBudget { get; set; }
    public long TokensUsed { get; set; }
    public double GpuSeconds { get; set; }
    public double CpuComputeSeconds { get; set; }
    public double RamGbSeconds { get; set; }
    public long IoBytes { get; set; }
    public string UpdatedDisplay => UpdatedAt == default ? "Unknown date" : UpdatedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    public string ActivityDisplay => $"{MessageCount:N0} messages  ·  {FileCount:N0} files  ·  {CommentCount:N0} comments";
    public string TokenDisplay => $"{Math.Max(TokensUsed, PromptTokenCount):N0} tokens  ·  {PromptTokenCount:N0}/{PromptTokenBudget:N0} context";
    public string ComputeDisplay => GpuSeconds > 0 || CpuComputeSeconds > 0 || RamGbSeconds > 0
        ? $"GPU {GpuSeconds:0.##}s  ·  CPU {CpuComputeSeconds:0.##}s  ·  RAM {RamGbSeconds:0.##} GB·s"
        : "Compute metrics not recorded for this session";
}

public sealed class ChatSessionDetail
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public string Model { get; set; } = "";
    public string ReasoningLevel { get; set; } = "inherit";
    public string InteractionMode { get; set; } = "chat";
    public string ProjectId { get; set; } = "unsorted";
    public string ProjectName { get; set; } = "Unsorted";
    public bool Pinned { get; set; }
    public List<ChatMessage> Messages { get; } = new();
    public List<AttachmentInfo> Files { get; } = new();
}

public sealed class ChatProjectInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "Project";
    public string WorkspaceRoot { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
    public int SessionCount { get; set; }
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public bool BuiltIn { get; set; }
    public string Subtitle => string.IsNullOrWhiteSpace(WorkspaceRoot) ? $"{SessionCount:N0} chats" : WorkspaceRoot;
}

public sealed class PcAccessStreamSession
{
    public string SessionId { get; set; } = "";
    public string RtmpUrl { get; set; } = "";
    public string Encoder { get; set; } = "";
    public string Codec { get; set; } = "";
    public int BitrateKbps { get; set; }
    public PcDesktopBounds Desktop { get; set; } = new();
    public PcCursorState Cursor { get; set; } = new();
}

public sealed class PcAccessPointerSnapshot
{
    public PcDesktopBounds Desktop { get; set; } = new();
    public PcCursorState Cursor { get; set; } = new();
}

public sealed class PcAccessFtpConnection
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 2121;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string Root { get; set; } = "/";
    public bool AllowWrite { get; set; }
}

public sealed class PcDesktopBounds
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class PcCursorState
{
    public double X { get; set; }
    public double Y { get; set; }
    public bool Visible { get; set; }
}

public sealed class HardwareSnapshot
{
    public double? CpuPercent { get; init; }
    public double? RamPercent { get; init; }
    public ulong RamUsedBytes { get; init; }
    public ulong RamTotalBytes { get; init; }
    public double? GpuPercent { get; init; }
    public double? VramPercent { get; init; }
    public ulong VramUsedBytes { get; init; }
    public ulong VramTotalBytes { get; init; }
    public string GpuName { get; init; } = "";

    public string Display => $"GPU {Percent(GpuPercent)}  ·  VRAM {Bytes(VramUsedBytes)}/{Bytes(VramTotalBytes)}  ·  CPU {Percent(CpuPercent)}  ·  RAM {Percent(RamPercent)}";
    private static string Percent(double? value) => value.HasValue ? $"{value.Value:0}%" : "—";
    private static string Bytes(ulong value) => value == 0 ? "—" : $"{value / 1073741824d:0.#}G";
}

public sealed class ModelInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Service { get; set; } = "chat";
    public bool SupportsChat { get; set; } = true;
    public bool SupportsTools { get; set; }
    public bool SupportsImages { get; set; }
    public bool SupportsAudioGeneration { get; set; }
    public bool SupportsImageGeneration { get; set; }
    public bool SupportsVideoGeneration { get; set; }
    public bool IsLoaded { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsGeneralChatCandidate => SupportsChat && !SupportsAudioGeneration && !SupportsImageGeneration && !SupportsVideoGeneration;
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class AttachmentInfo : System.ComponentModel.INotifyPropertyChanged
{
    private double _uploadProgress;
    private string _uploadState = "pending";
    private string _uploadError = "";
    private string _uploadedPath = "";

    public string Name { get; init; } = "attachment";
    public string ContentType { get; init; } = "application/octet-stream";
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public double UploadProgress { get => _uploadProgress; set { if (Math.Abs(_uploadProgress - value) < .001) return; _uploadProgress = value; PropertyChanged?.Invoke(this, new(nameof(UploadProgress))); PropertyChanged?.Invoke(this, new(nameof(UploadPercent))); } }
    public string UploadState { get => _uploadState; set { if (_uploadState == value) return; _uploadState = value; PropertyChanged?.Invoke(this, new(nameof(UploadState))); PropertyChanged?.Invoke(this, new(nameof(IsUploaded))); PropertyChanged?.Invoke(this, new(nameof(IsUploading))); PropertyChanged?.Invoke(this, new(nameof(NeedsAttention))); } }
    public string UploadError { get => _uploadError; set { if (_uploadError == value) return; _uploadError = value; PropertyChanged?.Invoke(this, new(nameof(UploadError))); } }
    public string UploadedPath { get => _uploadedPath; set { if (_uploadedPath == value) return; _uploadedPath = value; PropertyChanged?.Invoke(this, new(nameof(UploadedPath))); } }
    public bool IsUploaded => UploadState == "complete";
    public bool IsUploading => UploadState == "uploading";
    public bool NeedsAttention => UploadState == "failed";
    public string UploadPercent => $"{Math.Clamp(UploadProgress, 0, 1):P0}";
    public string MediaType
    {
        get
        {
            string declared = (ContentType ?? "").Split(';', 2)[0].Trim().ToLowerInvariant();
            if (declared.StartsWith("image/", StringComparison.Ordinal)) return declared;
            if (Data.Length >= 8 && Data[0] == 0x89 && Data[1] == 0x50 && Data[2] == 0x4E && Data[3] == 0x47) return "image/png";
            if (Data.Length >= 3 && Data[0] == 0xFF && Data[1] == 0xD8 && Data[2] == 0xFF) return "image/jpeg";
            if (Data.Length >= 6 && Data[0] == (byte)'G' && Data[1] == (byte)'I' && Data[2] == (byte)'F') return "image/gif";
            if (Data.Length >= 12 && Data[0] == (byte)'R' && Data[1] == (byte)'I' && Data[2] == (byte)'F' && Data[8] == (byte)'W' && Data[9] == (byte)'E' && Data[10] == (byte)'B' && Data[11] == (byte)'P') return "image/webp";
            return Path.GetExtension(Name).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" or ".jfif" => "image/jpeg",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                ".heic" => "image/heic",
                ".heif" => "image/heif",
                _ => string.IsNullOrWhiteSpace(declared) ? "application/octet-stream" : declared
            };
        }
    }
    public bool IsImage => MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
    public string DataUrl => $"data:{MediaType};base64,{Convert.ToBase64String(Data)}";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public sealed class ProjectFileEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "session";
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = "file";
    public bool Exists { get; set; }
    public bool HasChildren { get; set; }
    public string Extension { get; set; } = "";
    public long Size { get; set; }
    public DateTimeOffset ModifiedUtc { get; set; }
    public bool IsDirectory => Type.Equals("directory", StringComparison.OrdinalIgnoreCase);
    public string Icon => IsDirectory ? "📁" : "📄";
    public string Detail => IsDirectory ? "Folder" : $"{FormatBytes(Size)} · {ModifiedUtc.LocalDateTime:g}";
    private static string FormatBytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.#} GB" : value >= 1048576 ? $"{value / 1048576d:0.#} MB" : value >= 1024 ? $"{value / 1024d:0.#} KB" : $"{value} B";
}

public sealed class ProjectStorageSnapshot
{
    public long UsedBytes { get; set; }
    public long TotalUsedBytes { get; set; }
    public long CurrentSessionUsedBytes { get; set; }
    public long LimitBytes { get; set; }
    public long RemainingBytes { get; set; }
    public bool Unlimited { get; set; }
    public double UsageRatio => Unlimited || LimitBytes <= 0 ? 0 : Math.Clamp((double)TotalUsedBytes / LimitBytes, 0, 1);
}

public sealed class ProjectFilesSnapshot
{
    public string SessionId { get; set; } = "";
    public ProjectStorageSnapshot Storage { get; set; } = new();
    public ProjectFileEntry? Current { get; set; }
    public List<ProjectFileEntry> Roots { get; set; } = new();
    public List<ProjectFileEntry> Children { get; set; } = new();
    public List<ProjectFileEntry> Results { get; set; } = new();
}

public sealed class ProjectFileVersion
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public int FileCount { get; set; }
    public string Detail => $"{FileCount} file{(FileCount == 1 ? "" : "s")} · {CreatedUtc.LocalDateTime:g}";
}

public sealed class ProjectFileVersionsSnapshot
{
    public string SessionId { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public bool Shared { get; set; }
    public List<ProjectFileVersion> Versions { get; set; } = new();
}
