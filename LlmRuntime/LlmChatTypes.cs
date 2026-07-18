using System.Diagnostics;
using System.Text.Json;

namespace LlmRuntime;

public sealed class LlmChatRequest
{
    public const int DefaultMaxCompletionTokens = 4096;

    public string Model { get; set; } = "";

    public IReadOnlyList<LlmChatMessage> Messages { get; set; } = [];

    public bool Stream { get; set; }

    public int MaxTokens { get; set; } = DefaultMaxCompletionTokens;

    public bool MaxTokensSpecified { get; set; }

    public float Temperature { get; set; } = 0.7f;

    public float TopP { get; set; } = 0.95f;

    public IReadOnlyList<string> Stop { get; set; } = [];

    public string? User { get; set; }

    public IReadOnlyList<JsonElement> Tools { get; set; } = [];

    public JsonElement? ToolChoice { get; set; }

    public string ProjectPath { get; set; } = "";

    public string SessionId { get; set; } = "";

    public string SessionTitle { get; set; } = "";

    public bool? SessionLocked { get; set; }

    public bool? SessionSaved { get; set; }

    public int? PromptTokenBudget { get; set; }

    public JsonElement? Metadata { get; set; }

    public string ReasoningLevel { get; set; } = "auto";

    public ModelRouteDecision? RouteDecision { get; set; }

    public static LlmChatRequest FromJson(JsonElement root)
    {
        int? requestedMaxTokens =
            GetInt32(root, "max_tokens") ??
            GetInt32(root, "max_completion_tokens") ??
            GetInt32(root, "max_output_tokens");

        var request = new LlmChatRequest
        {
            Model = GetString(root, "model") ?? "",
            Stream = GetBool(root, "stream") ?? false,
            MaxTokens = Math.Clamp(requestedMaxTokens ?? DefaultMaxCompletionTokens, 1, 131072),
            MaxTokensSpecified = requestedMaxTokens.HasValue,
            Temperature = Clamp(GetSingle(root, "temperature") ?? 0.7f, 0f, 10f),
            TopP = Clamp(GetSingle(root, "top_p") ?? 0.95f, 0f, 1f),
            Stop = GetStop(root),
            User = GetString(root, "user"),
            ProjectPath = GetString(root, "project_path") ?? GetString(root, "projectPath") ?? "",
            SessionId = GetString(root, "session_id") ?? GetString(root, "sessionId") ?? GetString(root, "id") ?? "",
            SessionTitle = GetString(root, "session_title") ?? GetString(root, "sessionTitle") ?? "",
            SessionLocked = GetBool(root, "session_locked") ?? GetBool(root, "sessionLocked"),
            SessionSaved = GetBool(root, "session_saved") ?? GetBool(root, "sessionSaved"),
            PromptTokenBudget = GetInt32(root, "prompt_token_budget") ?? GetInt32(root, "promptTokenBudget") ?? GetInt32(root, "context_length") ?? GetInt32(root, "contextLength") ?? GetInt32(root, "n_ctx") ?? GetInt32(root, "nCtx")
        };
        request.ReasoningLevel = GetString(root, "reasoning_level") ?? GetString(root, "reasoningLevel") ?? ReadReasoningEffort(root) ?? "auto";

        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            request.Metadata = metadata.Clone();
            ApplySessionMetadata(request, metadata);
        }

        if (root.TryGetProperty("session", out var session) && session.ValueKind == JsonValueKind.Object)
            ApplySessionMetadata(request, session);

        if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            request.Messages = messages.EnumerateArray().Select(ParseMessage).Where(message => !string.IsNullOrWhiteSpace(message.Content)).ToArray();

        if (root.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            request.Tools = tools.EnumerateArray().Select(tool => tool.Clone()).ToArray();

        if (root.TryGetProperty("tool_choice", out var toolChoice) || root.TryGetProperty("toolChoice", out toolChoice))
            request.ToolChoice = toolChoice.Clone();

        return request;
    }

    private static string? ReadReasoningEffort(JsonElement root)
    {
        if (root.TryGetProperty("reasoning", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.Object)
            return GetString(reasoning, "effort");
        return null;
    }

    private static void ApplySessionMetadata(LlmChatRequest request, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        JsonElement source = metadata;
        if (metadata.TryGetProperty("lmvsproxy", out var lmvsproxy) && lmvsproxy.ValueKind == JsonValueKind.Object)
            source = lmvsproxy;
        if (source.TryGetProperty("session", out var nestedSession) && nestedSession.ValueKind == JsonValueKind.Object)
            source = nestedSession;

        request.SessionId = FirstNonEmpty(request.SessionId, GetString(source, "sessionId"), GetString(source, "session_id"), GetString(source, "id"));
        request.SessionTitle = FirstNonEmpty(request.SessionTitle, GetString(source, "title"), GetString(source, "sessionTitle"), GetString(source, "session_title"));
        request.SessionLocked ??= GetBool(source, "locked") ?? GetBool(source, "sessionLocked") ?? GetBool(source, "session_locked");
        if (!request.SessionSaved.HasValue)
            request.SessionSaved = GetBool(source, "saved") ?? !string.IsNullOrWhiteSpace(GetString(source, "savedUtc") ?? GetString(source, "saved_utc"));
        request.PromptTokenBudget ??= GetInt32(source, "promptTokenBudget") ?? GetInt32(source, "prompt_token_budget") ?? GetInt32(source, "contextLength") ?? GetInt32(source, "context_length");
    }

    private static LlmChatMessage ParseMessage(JsonElement element)
    {
        string role = GetString(element, "role") ?? "user";
        string content = "";
        JsonElement? structuredContent = null;
        bool hasImageContent = false;
        if (element.TryGetProperty("content", out var contentElement))
        {
            content = ReadContent(contentElement);
            hasImageContent = ContentContainsImage(contentElement);
            if (hasImageContent)
                structuredContent = contentElement.Clone();
        }

        if (role.Equals("tool", StringComparison.OrdinalIgnoreCase))
        {
            content = ReadToolResultContent(element, content);
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            if (role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                TryReadAssistantToolCalls(element, out string toolCallSummary))
            {
                content = toolCallSummary;
            }
        }

        return new LlmChatMessage(role, content, structuredContent, hasImageContent);
    }

    private static bool ContentContainsImage(JsonElement content)
    {
        if (content.ValueKind == JsonValueKind.Array)
            return content.EnumerateArray().Any(ContentContainsImage);
        if (content.ValueKind != JsonValueKind.Object)
            return false;
        string type = GetString(content, "type") ?? "";
        return type.Equals("image_url", StringComparison.OrdinalIgnoreCase)
            || type.Equals("input_image", StringComparison.OrdinalIgnoreCase)
            || content.TryGetProperty("image_url", out _);
    }

    private static bool TryReadAssistantToolCalls(JsonElement element, out string content)
    {
        content = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("tool_calls", out var toolCalls) ||
            toolCalls.ValueKind != JsonValueKind.Array ||
            toolCalls.GetArrayLength() == 0)
            return false;

        var summaries = new List<string>();
        foreach (JsonElement toolCall in toolCalls.EnumerateArray())
        {
            if (toolCall.ValueKind != JsonValueKind.Object)
                continue;

            string id = GetString(toolCall, "id") ?? "";
            string name = GetString(toolCall, "name") ?? "";
            string arguments = "{}";

            if (toolCall.TryGetProperty("arguments", out var argumentsElement))
                arguments = ReadJsonArgumentText(argumentsElement);

            if (toolCall.TryGetProperty("function", out var function) &&
                function.ValueKind == JsonValueKind.Object)
            {
                name = GetString(function, "name") ?? name;
                if (function.TryGetProperty("arguments", out var functionArguments))
                    arguments = ReadJsonArgumentText(functionArguments);
            }

            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
                continue;

            summaries.Add("tool_call id=" + (string.IsNullOrWhiteSpace(id) ? "<missing>" : id) +
                          " name=" + (string.IsNullOrWhiteSpace(name) ? "<missing>" : name) +
                          " arguments=" + (string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments));
        }

        if (summaries.Count == 0)
            return false;

        content = "Assistant requested tool call(s):" + Environment.NewLine + string.Join(Environment.NewLine, summaries);
        return true;
    }

    private static string ReadToolResultContent(JsonElement element, string content)
    {
        string id = GetString(element, "tool_call_id") ?? GetString(element, "toolCallId") ?? GetString(element, "id") ?? "";
        string name = GetString(element, "name") ?? "";
        string output = string.IsNullOrWhiteSpace(content)
            ? "(empty tool result)"
            : content;

        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
            return output;

        return "Tool result" +
               (string.IsNullOrWhiteSpace(name) ? "" : " for " + name) +
               (string.IsNullOrWhiteSpace(id) ? "" : " (" + id + ")") +
               ":" + Environment.NewLine + output;
    }

    private static string ReadJsonArgumentText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return CompactJsonArgumentText(element.GetString() ?? "");

        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : CompactJsonArgumentText(element.GetRawText());
    }

    private static string CompactJsonArgumentText(string value)
    {
        string text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        }
    }

    private static string ReadContent(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";

        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Join(Environment.NewLine, element.EnumerateArray().Select(part =>
            {
                if (part.ValueKind == JsonValueKind.String)
                    return part.GetString() ?? "";
                if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    return text.GetString() ?? "";
                // Binary image data is retained as structured content, not copied
                // into the text prompt where base64 would consume the context.
                return part.ValueKind == JsonValueKind.Object && ContentContainsImage(part) ? "" :
                    part.ValueKind == JsonValueKind.Object ? part.GetRawText() : "";
            }).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : element.GetRawText();
    }

    private static IReadOnlyList<string> GetStop(JsonElement root)
    {
        if (!root.TryGetProperty("stop", out var value))
            return [];
        if (value.ValueKind == JsonValueKind.String)
            return [value.GetString() ?? ""];
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(item => !string.IsNullOrEmpty(item))
                .ToArray();
        return [];
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }

    private static int? GetInt32(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : null;

    private static float? GetSingle(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out float result) ? result : null;

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    private static float Clamp(float value, float min, float max) => Math.Min(Math.Max(value, min), max);
}

public sealed record LlmChatMessage(string Role, string Content, JsonElement? StructuredContent = null, bool HasImageContent = false);

public sealed record LlmChatToken(string Text, string FinishReason = "");

public sealed class LlmChatResult
{
    public string Model { get; init; } = "";

    public string Content { get; init; } = "";

    public string FinishReason { get; init; } = "stop";

    public LlmInferenceMetrics Metrics { get; init; } = new();

    public IReadOnlyList<LlmToolCall> ToolCalls { get; init; } = [];

    public IReadOnlyList<LlmToolCallResult> ToolResults { get; init; } = [];
}

public sealed class LlmInferenceMetrics
{
    public int PromptTokens { get; init; }

    public int CompletionTokens { get; init; }

    public int TotalTokens => PromptTokens + CompletionTokens;

    public double ElapsedSeconds { get; init; }

    public double TokensPerSecond { get; init; }

    public long ManagedMemoryBytes { get; init; }

    public static LlmInferenceMetrics FromText(LlmChatRequest request, string output, TimeSpan elapsed)
    {
        int promptTokens = EstimateTokens(string.Join(Environment.NewLine, request.Messages.Select(message => message.Content)));
        int completionTokens = EstimateTokens(output);
        double elapsedSeconds = Math.Max(elapsed.TotalSeconds, 0.001d);
        return new LlmInferenceMetrics
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            ElapsedSeconds = elapsed.TotalSeconds,
            TokensPerSecond = completionTokens / elapsedSeconds,
            ManagedMemoryBytes = GC.GetTotalMemory(false)
        };
    }

    public static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0d));
    }
}

public sealed class LlmRuntimeException : Exception
{
    public LlmRuntimeException(string message, string type, string code)
        : base(message)
    {
        Type = type;
        Code = code;
    }

    public string Type { get; }

    public string Code { get; }
}
