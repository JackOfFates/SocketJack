using System.Collections.ObjectModel;

namespace JackLLM.Mobile.Models;

public sealed class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public string Reasoning { get; set; } = "";
    public string Status { get; set; } = "";
    public ObservableCollection<ToolActivity> Tools { get; } = new();
    public bool IsUser => Role.Equals("user", StringComparison.OrdinalIgnoreCase);
    public Color BubbleColor => IsUser ? Color.FromArgb("#2563EB") : Color.FromArgb("#1F2937");
    public Color TextColor => Colors.White;
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
    public string RawJson { get; init; } = "";
}

public sealed class ChatSessionInfo
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ChatSessionDetail
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "New chat";
    public string Model { get; set; } = "";
    public List<ChatMessage> Messages { get; } = new();
    public List<AttachmentInfo> Files { get; } = new();
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
