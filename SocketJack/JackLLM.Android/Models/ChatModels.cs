using System.Collections.ObjectModel;

namespace JackLLM.Mobile.Models;

public sealed class ChatMessage : System.ComponentModel.INotifyPropertyChanged
{
    private string _role = "user";
    private string _content = "";
    private string _reasoning = "";
    private string _status = "";
    private string _telemetry = "";
    private bool _isReasoningExpanded = true;
    private bool _isGenerating;

    public string Role { get => _role; set { if (_role == value) return; _role = value; PropertyChanged?.Invoke(this, new(nameof(Role))); PropertyChanged?.Invoke(this, new(nameof(IsUser))); PropertyChanged?.Invoke(this, new(nameof(BubbleColor))); } }
    public string Content { get => _content; set { if (_content == value) return; _content = value; PropertyChanged?.Invoke(this, new(nameof(Content))); PropertyChanged?.Invoke(this, new(nameof(HasContent))); } }
    public string Reasoning { get => _reasoning; set { if (_reasoning == value) return; _reasoning = value; PropertyChanged?.Invoke(this, new(nameof(Reasoning))); PropertyChanged?.Invoke(this, new(nameof(HasReasoning))); PropertyChanged?.Invoke(this, new(nameof(ShowReasoning))); } }
    public string Status { get => _status; set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); PropertyChanged?.Invoke(this, new(nameof(HasStatus))); } }
    public string Telemetry { get => _telemetry; set { if (_telemetry == value) return; _telemetry = value; PropertyChanged?.Invoke(this, new(nameof(Telemetry))); PropertyChanged?.Invoke(this, new(nameof(HasTelemetry))); } }
    public bool IsReasoningExpanded { get => _isReasoningExpanded; set { if (_isReasoningExpanded == value) return; _isReasoningExpanded = value; PropertyChanged?.Invoke(this, new(nameof(IsReasoningExpanded))); PropertyChanged?.Invoke(this, new(nameof(ReasoningChevron))); } }
    public bool IsGenerating { get => _isGenerating; set { if (_isGenerating == value) return; _isGenerating = value; PropertyChanged?.Invoke(this, new(nameof(IsGenerating))); PropertyChanged?.Invoke(this, new(nameof(ReasoningHeader))); PropertyChanged?.Invoke(this, new(nameof(ShowReasoning))); } }
    public ObservableCollection<ToolActivity> Tools { get; } = new();
    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    public bool HasContent => !string.IsNullOrWhiteSpace(Content);
    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);
    public bool ShowReasoning => IsGenerating || HasReasoning;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasTelemetry => !string.IsNullOrWhiteSpace(Telemetry);
    public string ReasoningHeader => IsGenerating ? "Thinking…" : "Thinking process";
    public string ReasoningChevron => IsReasoningExpanded ? "⌃" : "⌄";
    public bool IsCapturingEmbeddedReasoning { get; set; }
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

public sealed class ChatStreamEvent
{
    public string Type { get; init; } = "unknown";
    public string Content { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public string Status { get; init; } = "";
    public double? Progress { get; init; }
    public string ToolName { get; init; } = "";
    public string ToolStatus { get; init; } = "";
    public long TokenDelta { get; init; }
    public long TokensUsed { get; init; }
    public double GpuSecondsUsed { get; init; }
    public double CpuComputeSecondsUsed { get; init; }
    public double RamGbSecondsUsed { get; init; }
    public long PromptTokensLoaded { get; init; }
    public long PromptTokensTotal { get; init; }
    public string RawJson { get; init; } = "";
}

public sealed class ChatSessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string Model { get; set; } = "";
    public string Runtime { get; set; } = "";
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
    public List<ChatMessage> Messages { get; } = new();
    public List<AttachmentInfo> Files { get; } = new();
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
    public bool SupportsAudioGeneration { get; set; }
    public bool SupportsImageGeneration { get; set; }
    public bool SupportsVideoGeneration { get; set; }
    public bool IsGeneralChatCandidate => SupportsChat && !SupportsAudioGeneration && !SupportsImageGeneration && !SupportsVideoGeneration;
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class AttachmentInfo
{
    public string Name { get; init; } = "attachment";
    public string ContentType { get; init; } = "application/octet-stream";
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public string DataUrl => $"data:{ContentType};base64,{Convert.ToBase64String(Data)}";
}
