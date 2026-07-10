using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JackLLM.Mobile.Models;

namespace JackLLM.Mobile.Services;

public sealed class JackLlmClient : IDisposable
{
    private readonly SecureCredentialStore _credentials;
    private HttpClient? _http;
    private ServerInfo? _server;
    public string ActiveStreamId { get; private set; } = "";

    public JackLlmClient(SecureCredentialStore credentials) => _credentials = credentials;

    public async Task ConnectAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        _server = server;
        _http?.Dispose();
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(server.CertificateFingerprint))
        {
            string expected = NormalizeFingerprint(server.CertificateFingerprint);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                certificate is not null && NormalizeFingerprint(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(NormalizeBaseUrl(server.Endpoint)), Timeout = TimeSpan.FromMinutes(30) };
        string? token = await _credentials.GetServerTokenAsync(server.LaunchKey);
        if (string.IsNullOrWhiteSpace(token)) token = await _credentials.GetSocketJackTokenAsync();
        if (!string.IsNullOrWhiteSpace(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.GetAsync("/api/health", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/models", cancellationToken);
        JsonElement root = json.RootElement;
        JsonElement list = root.ValueKind == JsonValueKind.Array ? root : TryProperty(root, "models", out var models) ? models : default;
        var result = new List<ModelInfo>();
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in list.EnumerateArray())
            {
                string id = ReadString(item, "id", "model", "name");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    bool supportsAudio = ReadBool(item, "supportsAudioGeneration", "audioGeneration", "isAudioGeneration");
                    bool supportsImage = ReadBool(item, "supportsImageGeneration", "imageGeneration", "isImageGeneration");
                    bool supportsVideo = ReadBool(item, "supportsVideoGeneration", "videoGeneration", "isVideoGeneration");
                    string service = ReadString(item, "service", "type");
                    result.Add(new ModelInfo
                    {
                        Id = id,
                        Name = ReadString(item, "displayName", "name", "id"),
                        Service = service,
                        SupportsTools = ReadBool(item, "supportsTools", "tools", "toolUse"),
                        SupportsAudioGeneration = supportsAudio,
                        SupportsImageGeneration = supportsImage,
                        SupportsVideoGeneration = supportsVideo,
                        SupportsChat = !supportsAudio && !supportsImage && !supportsVideo && !service.Equals("audio", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        return result;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(string model, string service, string sessionId, IReadOnlyList<ChatMessage> messages, IReadOnlyList<AttachmentInfo> attachments, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var uploaded = new List<object>();
        foreach (AttachmentInfo attachment in attachments)
            uploaded.Add(await UploadFileAsync(sessionId, attachment, cancellationToken));
        string streamId = ActiveStreamId = "mobile_" + Guid.NewGuid().ToString("N");
        string prompt = messages.LastOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["service"] = string.IsNullOrWhiteSpace(service) ? "chat" : service,
            ["sessionId"] = sessionId,
            ["streamId"] = streamId,
            ["prompt"] = prompt,
            ["messages"] = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
            ["files"] = uploaded
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat-stream") { Content = Json(payload) };
        using HttpResponseMessage response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            yield return ParseStreamEvent(line);
        }
        ActiveStreamId = "";
    }

    public async Task StopAsync(string streamId, CancellationToken cancellationToken = default) => await PostAsync("/api/chat-stream/stop", new { streamId }, cancellationToken);
    public async Task SteerAsync(string streamId, string text, CancellationToken cancellationToken = default) => await PostAsync("/api/chat-stream/steer", new { streamId, text }, cancellationToken);

    public async Task<IReadOnlyList<ChatSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/chat-sessions?take=40", cancellationToken);
        JsonElement root = json.RootElement;
        JsonElement list = root.ValueKind == JsonValueKind.Array ? root : TryProperty(root, "sessions", out var sessions) ? sessions : default;
        var result = new List<ChatSessionInfo>();
        if (list.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in list.EnumerateArray()) result.Add(new ChatSessionInfo { Id = ReadString(item, "id", "sessionId"), Title = ReadString(item, "title", "name") });
        return result;
    }

    public async Task<ChatSessionDetail> GetSessionAsync(string id, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/chat-session?id=" + Uri.EscapeDataString(id), cancellationToken);
        JsonElement root = json.RootElement;
        JsonElement session = TryProperty(root, "session", out var sessionElement) ? sessionElement : root;
        var detail = new ChatSessionDetail
        {
            Id = ReadString(session, "id", "sessionId"),
            Title = ReadString(session, "title", "name"),
            Model = ReadString(session, "model")
        };
        if (string.IsNullOrWhiteSpace(detail.Id)) detail.Id = id;
        if (string.IsNullOrWhiteSpace(detail.Title)) detail.Title = "New chat";
        if (TryProperty(session, "messages", out JsonElement messages) && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement message in messages.EnumerateArray())
            {
                string role = ReadString(message, "role", "author", "speaker");
                string content = ReadMessageContent(message);
                string reasoning = ReadString(message, "reasoning", "reasoningContent", "thought", "thinking");
                if (!string.IsNullOrWhiteSpace(role) || !string.IsNullOrWhiteSpace(content) || !string.IsNullOrWhiteSpace(reasoning))
                    detail.Messages.Add(new ChatMessage { Role = string.IsNullOrWhiteSpace(role) ? "assistant" : role, Content = content, Reasoning = reasoning });
            }
        }
        if (TryProperty(session, "files", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement file in files.EnumerateArray())
            {
                string name = ReadString(file, "name", "fileName", "id");
                string type = ReadString(file, "type", "contentType");
                if (!string.IsNullOrWhiteSpace(name))
                    detail.Files.Add(new AttachmentInfo { Name = name, ContentType = string.IsNullOrWhiteSpace(type) ? "application/octet-stream" : type });
            }
        }
        return detail;
    }

    public Task RenameSessionAsync(string id, string title, CancellationToken cancellationToken = default) => PostAsync("/api/chat-session-rename", new { id, sessionId = id, title, name = title }, cancellationToken);
    public Task DeleteSessionAsync(string id, CancellationToken cancellationToken = default) => PostAsync("/api/chat-sessions/delete", new { ids = new[] { id }, sessionIds = new[] { id } }, cancellationToken);
    public Task ShareSessionAsync(string id, CancellationToken cancellationToken = default) => PostAsync("/api/chat-session-share-link", new { id, sessionId = id }, cancellationToken);

    public async Task<string> CompletePairingAsync(string endpoint, string code, string deviceName, CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { BaseAddress = new Uri(NormalizeBaseUrl(endpoint)), Timeout = TimeSpan.FromSeconds(20) };
        using HttpResponseMessage response = await client.PostAsync("/api/mobile/pairing/complete", Json(new { code, deviceName, platform = "android" }), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(body);
        string token = ReadString(json.RootElement, "token", "accessToken");
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("The Workstation did not return a device token.");
        return token;
    }

    public async Task<string> TranscribeAsync(byte[] audio, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(audio);
        bytes.Headers.ContentType = new MediaTypeHeaderValue("audio/mp4");
        form.Add(bytes, "file", "voice.m4a");
        form.Add(new StringContent("whisper-1"), "model");
        using HttpResponseMessage response = await _http!.PostAsync("/v1/audio/transcriptions", form, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(body);
        return ReadString(json.RootElement, "text", "transcript");
    }

    public async Task<byte[]> SynthesizeSpeechAsync(string text, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using HttpResponseMessage response = await _http!.PostAsync("/v1/audio/speech", Json(new { model = "tts-1", voice = "alloy", input = text, response_format = "mp3" }), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public static ChatStreamEvent ParseStreamEvent(string line)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;
            string type = ReadString(root, "type");
            return new ChatStreamEvent
            {
                Type = string.IsNullOrWhiteSpace(type) ? "delta" : type,
                Content = ReadString(root, "content", "text", "delta", "answer", "answerContent", "message", "response", "body", "error"),
                Reasoning = ReadString(root, "reasoning", "reasoningContent", "thought", "thinking"),
                Status = ReadString(root, "status", "state", "message", "statusText"),
                Progress = ReadDouble(root, "progress"),
                ToolName = ReadString(root, "toolName", "name", "tool"),
                ToolStatus = ReadString(root, "toolStatus", "state"),
                RawJson = line
            };
        }
        catch { return new ChatStreamEvent { Type = "unknown", RawJson = line }; }
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) throw new ArgumentException("Enter a valid Workstation http/https address.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private async Task<object> UploadFileAsync(string sessionId, AttachmentInfo file, CancellationToken cancellationToken)
    {
        EnsureConnected();
        using HttpResponseMessage response = await _http!.PostAsync("/api/chat-file", Json(new { sessionId, name = file.Name, type = file.ContentType, dataUrl = file.DataUrl, asFile = true }), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(body);
        return TryProperty(json.RootElement, "file", out var uploaded) ? JsonSerializer.Deserialize<object>(uploaded.GetRawText())! : new { name = file.Name };
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        using HttpResponseMessage response = await _http!.GetAsync(path, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task PostAsync(string path, object payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        using HttpResponseMessage response = await _http!.PostAsync(path, Json(payload), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private void EnsureConnected() { if (_http is null || _server is null) throw new InvalidOperationException("Connect to a Workstation first."); }
    private static bool TryProperty(JsonElement element, string name, out JsonElement value) { value = default; if (element.ValueKind != JsonValueKind.Object) return false; foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } return false; }
    private static string ReadString(JsonElement element, params string[] names) { foreach (string name in names) if (TryProperty(element, name, out var value)) return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString(); return ""; }
    private static double? ReadDouble(JsonElement element, string name) => TryProperty(element, name, out var value) && value.TryGetDouble(out double number) ? number : null;
    private static bool ReadBool(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.True) return true;
            if (value.ValueKind == JsonValueKind.False) return false;
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed)) return parsed;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number)) return number != 0;
        }
        return false;
    }
    private static string ReadMessageContent(JsonElement message)
    {
        string direct = ReadString(message, "content", "text", "message");
        if (!string.IsNullOrWhiteSpace(direct) && !direct.TrimStart().StartsWith("[", StringComparison.Ordinal)) return direct;
        if (TryProperty(message, "content", out JsonElement content) && content.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (JsonElement part in content.EnumerateArray())
            {
                string text = ReadString(part, "text", "content", "value");
                if (!string.IsNullOrWhiteSpace(text)) parts.Add(text);
            }
            if (parts.Count > 0) return string.Join("\n", parts);
        }
        return direct;
    }
    private static string NormalizeFingerprint(string value) => new((value ?? "").Where(Uri.IsHexDigit).Select(char.ToUpperInvariant).ToArray());
    public void Dispose() => _http?.Dispose();
}
