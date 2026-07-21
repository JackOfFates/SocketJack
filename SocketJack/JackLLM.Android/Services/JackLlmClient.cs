using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using JackLLM.Mobile.Models;

namespace JackLLM.Mobile.Services;

public sealed class JackLlmClient : IDisposable
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };
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
        string credentialKey = string.IsNullOrWhiteSpace(server.CredentialKey) ? server.LaunchKey : server.CredentialKey;
        string? token = await _credentials.GetServerTokenAsync(credentialKey);
        if (string.IsNullOrWhiteSpace(token)) token = await _credentials.GetSocketJackTokenAsync();
        if (!string.IsNullOrWhiteSpace(token)) _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await _http.GetAsync("/api/health", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<TimeSpan?> MeasureHealthAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using HttpResponseMessage response = await _http!.GetAsync("/api/health", timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    public async Task<bool> SupportsVoiceAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        foreach (string path in new[] { "/v1/audio/transcriptions", "/v1/audio/speech" })
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                using HttpResponseMessage response = await _http!.SendAsync(request, cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
            }
            catch (HttpRequestException) { return false; }
        }
        return true;
    }

    public async Task<JsonDocument> GetPcAccessStatusAsync(CancellationToken cancellationToken = default) => await GetJsonAsync("/api/pc-access/status", cancellationToken);

    public async Task<PcAccessStreamSession> StartPcAccessStreamAsync(int width = 1280, int height = 720, int fps = 20, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/pc-access/stream/start", new { width, height, fps }, cancellationToken);
        return ReadPcAccessSession(json.RootElement);
    }

    public async Task<PcAccessPointerSnapshot> GetPcAccessPointerAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/pc-access/pointer?sessionId=" + Uri.EscapeDataString(sessionId), cancellationToken);
        return ReadPcAccessPointer(json.RootElement);
    }

    public async Task<byte[]> GetPcDesktopFrameAsync(int width, int height, int quality, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync($"/api/pc-access/desktop?width={width}&height={height}&quality={quality}", cancellationToken);
        string data = ReadString(json.RootElement, "data");
        return Convert.FromBase64String(data);
    }

    public Task SendPcInputAsync(object input, CancellationToken cancellationToken = default) => PostAsync("/api/pc-access/desktop/input", input, cancellationToken);
    public Task DisconnectPcAccessAsync(CancellationToken cancellationToken = default) => PostAsync("/api/pc-access/disconnect", new { }, cancellationToken);
    public async Task<PcAccessFtpConnection> GetPcAccessFtpAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/pc-access/ftp", cancellationToken);
        JsonElement root = json.RootElement;
        return new PcAccessFtpConnection
        {
            Host = ReadString(root, "host"),
            Port = (int)ReadLong(root, "port"),
            UserName = ReadString(root, "userName"),
            Password = ReadString(root, "password"),
            Root = ReadString(root, "root"),
            AllowWrite = ReadBool(root, "allowWrite")
        };
    }
    public async Task<JsonDocument> BrowsePcFilesAsync(string path, CancellationToken cancellationToken = default) => await GetJsonAsync("/api/pc-access/files?path=" + Uri.EscapeDataString(path ?? ""), cancellationToken);
    public Task CreatePcDirectoryAsync(string path, CancellationToken cancellationToken = default) => PostAsync("/api/pc-access/files", new { path, kind = "directory" }, cancellationToken);

    private static PcAccessStreamSession ReadPcAccessSession(JsonElement root)
    {
        PcAccessPointerSnapshot pointer = ReadPcAccessPointer(root);
        return new PcAccessStreamSession
        {
            SessionId = ReadString(root, "sessionId"),
            RtmpUrl = ReadString(root, "rtmpUrl"),
            Encoder = ReadString(root, "encoder"),
            Codec = ReadString(root, "codec"),
            BitrateKbps = (int)ReadLong(root, "bitrateKbps"),
            Desktop = pointer.Desktop,
            Cursor = pointer.Cursor
        };
    }

    private static PcAccessPointerSnapshot ReadPcAccessPointer(JsonElement root)
    {
        TryProperty(root, "desktop", out JsonElement desktop);
        TryProperty(root, "cursor", out JsonElement cursor);
        return new PcAccessPointerSnapshot
        {
            Desktop = new PcDesktopBounds
            {
                Left = (int)ReadLong(desktop, "left"), Top = (int)ReadLong(desktop, "top"),
                Width = (int)ReadLong(desktop, "width"), Height = (int)ReadLong(desktop, "height")
            },
            Cursor = new PcCursorState
            {
                X = ReadDouble(cursor, "x") ?? 0, Y = ReadDouble(cursor, "y") ?? 0,
                Visible = ReadBool(cursor, "visible")
            }
        };
    }

    public async Task<IReadOnlyList<ModelInfo>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/models", cancellationToken);
        JsonElement root = json.RootElement;
        JsonElement list = root.ValueKind == JsonValueKind.Array ? root : TryProperty(root, "models", out var models) ? models : default;
        var result = new List<ModelInfo>();
        result.Add(new ModelInfo { Id = "auto", Name = "Auto · Instant Router", Service = "chat", SupportsChat = true, SupportsTools = true, SupportsImages = true });
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
                        SupportsImages = ReadBool(item, "supportsImages", "supportsVision", "vision", "images"),
                        SupportsAudioGeneration = supportsAudio,
                        SupportsImageGeneration = supportsImage,
                        SupportsVideoGeneration = supportsVideo,
                        IsLoaded = ReadBool(item, "isLoaded", "loaded"),
                        IsAvailable = !TryProperty(item, "isAvailable", out _) || ReadBool(item, "isAvailable", "available"),
                        SupportsChat = !supportsAudio && !supportsImage && !supportsVideo && !service.Equals("audio", StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
        }
        return result;
    }

    public async Task<MobileDreamSettingsEnvelope> GetDreamSettingsAsync(string ownerKey = "", CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/dream-settings" + OwnerQuery(ownerKey), cancellationToken);
        return JsonSerializer.Deserialize<MobileDreamSettingsEnvelope>(json.RootElement.GetRawText(), WireJson) ?? new MobileDreamSettingsEnvelope();
    }

    public async Task<MobileDreamSettingsEnvelope> SaveDreamSettingsAsync(string ownerKey, MobileDreamSettings settings, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await SendJsonAsync(HttpMethod.Put, "/api/dream-settings", MergeOwner(settings, ownerKey), cancellationToken);
        return JsonSerializer.Deserialize<MobileDreamSettingsEnvelope>(json.RootElement.GetRawText(), WireJson) ?? new MobileDreamSettingsEnvelope { Settings = settings };
    }

    public async Task ResetDreamSettingsAsync(string ownerKey, CancellationToken cancellationToken = default) =>
        _ = await SendJsonAsync(HttpMethod.Delete, "/api/dream-settings" + OwnerQuery(ownerKey), null, cancellationToken);

    public async Task<MobileDreamStatus> GetDreamStatusAsync(string ownerKey = "", CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/dream-status" + OwnerQuery(ownerKey), cancellationToken);
        return JsonSerializer.Deserialize<MobileDreamStatus>(json.RootElement.GetRawText(), WireJson) ?? new MobileDreamStatus();
    }

    public async Task<IReadOnlyList<MobileDreamOwner>> GetDreamOwnersAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/dream-owners", cancellationToken);
        return json.RootElement.TryGetProperty("owners", out JsonElement owners)
            ? JsonSerializer.Deserialize<List<MobileDreamOwner>>(owners.GetRawText(), WireJson) ?? new List<MobileDreamOwner>()
            : Array.Empty<MobileDreamOwner>();
    }

    public async Task<IReadOnlyList<MobileDreamJournalEntry>> GetDreamJournalAsync(string ownerKey = "", string status = "", CancellationToken cancellationToken = default)
    {
        string query = OwnerQuery(ownerKey);
        query += (query.Length == 0 ? "?" : "&") + "status=" + Uri.EscapeDataString(status ?? "");
        using JsonDocument json = await GetJsonAsync("/api/dream-journal" + query, cancellationToken);
        return json.RootElement.TryGetProperty("journal", out JsonElement journal)
            ? JsonSerializer.Deserialize<List<MobileDreamJournalEntry>>(journal.GetRawText(), WireJson) ?? new List<MobileDreamJournalEntry>()
            : Array.Empty<MobileDreamJournalEntry>();
    }

    public async Task<(MobileDreamPermissionSnapshot Permissions, bool CanManageOwners)> GetDreamPermissionsAsync(string ownerKey = "", CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/dream-permissions" + OwnerQuery(ownerKey), cancellationToken);
        MobileDreamPermissionSnapshot permissions = json.RootElement.TryGetProperty("permissions", out JsonElement p) ? JsonSerializer.Deserialize<MobileDreamPermissionSnapshot>(p.GetRawText(), WireJson) ?? new() : new();
        bool canManage = json.RootElement.TryGetProperty("canManageOwners", out JsonElement manage) && manage.ValueKind == JsonValueKind.True;
        return (permissions, canManage);
    }

    public async Task SaveDreamPermissionsAsync(string ownerKey, object permissions, CancellationToken cancellationToken = default) =>
        _ = await SendJsonAsync(HttpMethod.Put, "/api/dream-permissions", MergeOwner(permissions, ownerKey), cancellationToken);

    public async Task ControlDreamAsync(string ownerKey, string action, CancellationToken cancellationToken = default) =>
        _ = await PostJsonAsync("/api/dream-runs", new { ownerKey, action }, cancellationToken);

    public async Task DecideDreamCandidateAsync(string ownerKey, string id, string action, CancellationToken cancellationToken = default) =>
        _ = await PostJsonAsync("/api/dream-candidates", new { ownerKey, id, action }, cancellationToken);

    public async Task DeleteDreamJournalAsync(string ownerKey, string id, CancellationToken cancellationToken = default) =>
        _ = await SendJsonAsync(HttpMethod.Delete, "/api/dream-journal" + OwnerQuery(ownerKey) + (string.IsNullOrWhiteSpace(ownerKey) ? "?" : "&") + "id=" + Uri.EscapeDataString(id), null, cancellationToken);

    public async Task ClearResolvedDreamJournalAsync(string ownerKey, CancellationToken cancellationToken = default) =>
        _ = await PostJsonAsync("/api/dream-journal/clear", new { ownerKey }, cancellationToken);

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(string model, string service, string sessionId, string reasoningLevel, string sessionReasoningLevel, IReadOnlyList<ChatMessage> messages, IReadOnlyList<AttachmentInfo> attachments, string? requestedStreamId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var uploaded = new List<object>();
        foreach (AttachmentInfo attachment in attachments)
            uploaded.Add(await UploadFileAsync(sessionId, attachment, cancellationToken));
        string streamId = ActiveStreamId = string.IsNullOrWhiteSpace(requestedStreamId) ? "mobile_" + Guid.NewGuid().ToString("N") : requestedStreamId;
        string prompt = messages.LastOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["service"] = string.IsNullOrWhiteSpace(service) ? "chat" : service,
            ["sessionId"] = sessionId,
            ["streamId"] = streamId,
            ["reasoningLevel"] = string.IsNullOrWhiteSpace(reasoningLevel) ? "auto" : reasoningLevel,
            ["sessionReasoningLevel"] = string.IsNullOrWhiteSpace(sessionReasoningLevel) ? "inherit" : sessionReasoningLevel,
            ["max_tokens"] = service.Equals("agent", StringComparison.OrdinalIgnoreCase) ? 16384 : 4096,
            ["filesystemContext"] = new { mode = "none", roots = Array.Empty<string>() },
            ["prompt"] = prompt,
            ["messages"] = BuildWireMessages(messages, attachments),
            ["files"] = uploaded
        };
        try
        {
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
        }
        finally
        {
            if (ActiveStreamId.Equals(streamId, StringComparison.Ordinal)) ActiveStreamId = "";
        }
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
            foreach (JsonElement item in list.EnumerateArray()) result.Add(new ChatSessionInfo
            {
                Id = ReadString(item, "id", "sessionId"),
                Title = ReadString(item, "title", "name"),
                CreatedAt = ReadDate(item, "createdUtc", "createdAt"),
                UpdatedAt = ReadDate(item, "updatedUtc", "updatedAt", "savedUtc"),
                Model = ReadString(item, "model"),
                Runtime = ReadString(item, "runtime"),
                MessageCount = (int)ReadLong(item, "messageCount"),
                FileCount = (int)ReadLong(item, "fileCount"),
                CommentCount = (int)ReadLong(item, "commentCount"),
                PromptTokenCount = ReadLong(item, "promptTokenCount"),
                PromptTokenBudget = ReadLong(item, "promptTokenBudget"),
                TokensUsed = ReadLong(item, "tokensUsed"),
                GpuSeconds = ReadDouble(item, "gpuSeconds") ?? 0,
                CpuComputeSeconds = ReadDouble(item, "cpuComputeSeconds") ?? 0,
                RamGbSeconds = ReadDouble(item, "ramGbSeconds") ?? 0,
                IoBytes = ReadLong(item, "ioBytes")
            });
        return result;
    }

    public async Task<HardwareSnapshot> GetHardwareAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/server-hardware", cancellationToken);
        JsonElement root = json.RootElement;
        TryProperty(root, "cpu", out JsonElement cpu);
        TryProperty(root, "ram", out JsonElement ram);
        TryProperty(root, "gpu", out JsonElement gpu);
        return new HardwareSnapshot
        {
            CpuPercent = ReadDouble(cpu, "percent"),
            RamPercent = ReadDouble(ram, "percent"),
            RamUsedBytes = ReadULong(ram, "usedBytes"),
            RamTotalBytes = ReadULong(ram, "totalBytes"),
            GpuPercent = ReadDouble(gpu, "percent"),
            VramPercent = ReadDouble(gpu, "vramPercent"),
            VramUsedBytes = ReadULong(gpu, "vramUsedBytes"),
            VramTotalBytes = ReadULong(gpu, "vramTotalBytes"),
            GpuName = ReadString(gpu, "name")
        };
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
        detail.ReasoningLevel = ReadString(session, "reasoningLevel", "reasoning_level");
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
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) line = line[5..].TrimStart();
            using JsonDocument json = JsonDocument.Parse(line);
            JsonElement root = json.RootElement;
            string type = ReadString(root, "type");
            JsonElement routing = TryProperty(root, "routing", out JsonElement routeElement) ? routeElement : root;
            return new ChatStreamEvent
            {
                Type = string.IsNullOrWhiteSpace(type) ? "delta" : type,
                Content = ReadString(root, "content", "text", "delta", "answer", "answerContent", "message", "response", "error"),
                Reasoning = ReadString(root, "reasoning", "reasoningContent", "thought", "thinking"),
                Status = ReadString(root, "status", "state", "message", "statusText"),
                Progress = ReadDouble(root, "progress"),
                ToolName = ReadString(root, "toolName", "name", "tool"),
                ToolStatus = ReadString(root, "toolStatus", "state"),
                ToolDetail = ReadString(root, "label", "summary", "resultPreview", "argumentsPreview"),
                TokenDelta = ReadLong(root, "tokenDelta"),
                TokensUsed = ReadLong(root, "tokensUsed"),
                GpuSecondsUsed = ReadDouble(root, "gpuSecondsUsed") ?? 0,
                CpuComputeSecondsUsed = ReadDouble(root, "cpuComputeSecondsUsed") ?? 0,
                RamGbSecondsUsed = ReadDouble(root, "ramGbSecondsUsed") ?? 0,
                PromptTokensLoaded = ReadLong(root, "promptTokensLoaded"),
                PromptTokensTotal = ReadLong(root, "promptTokensTotal"),
                RoutedModel = ReadString(routing, "selectedModel", "selected_model", "model"),
                ReasoningLevel = ReadString(routing, "effectiveReasoning", "effective_reasoning", "reasoningLevel"),
                RouteReason = ReadString(routing, "reasonCode", "reason_code"),
                PromptFingerprint = ReadString(routing, "promptFingerprint", "prompt_fingerprint"),
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
        using HttpResponseMessage response = await _http!.PostAsync("/api/chat-file", Json(new { sessionId, name = file.Name, type = file.MediaType, dataUrl = file.DataUrl, asFile = !file.IsImage }), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(body);
        return TryProperty(json.RootElement, "file", out var uploaded) ? JsonSerializer.Deserialize<object>(uploaded.GetRawText())! : new { name = file.Name };
    }

    private static object[] BuildWireMessages(IReadOnlyList<ChatMessage> messages, IReadOnlyList<AttachmentInfo> attachments)
    {
        AttachmentInfo[] images = attachments.Where(attachment => attachment.IsImage).ToArray();
        int imageMessageIndex = -1;
        if (images.Length > 0)
        {
            for (int index = messages.Count - 1; index >= 0; index--)
            {
                if (messages[index].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                {
                    imageMessageIndex = index;
                    break;
                }
            }
        }

        var wireMessages = new List<object>(messages.Count);
        for (int index = 0; index < messages.Count; index++)
        {
            ChatMessage message = messages[index];
            if (index != imageMessageIndex)
            {
                wireMessages.Add(new { role = message.Role, content = message.Content });
                continue;
            }

            var content = new List<object>();
            if (!string.IsNullOrWhiteSpace(message.Content))
                content.Add(new { type = "text", text = message.Content });
            foreach (AttachmentInfo image in images)
                content.Add(new { type = "image_url", image_url = new { url = image.DataUrl } });
            wireMessages.Add(new { role = message.Role, content = content.ToArray() });
        }
        return wireMessages.ToArray();
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

    private async Task<JsonDocument> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        using HttpResponseMessage response = await _http!.PostAsync(path, Json(payload), cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object? payload, CancellationToken cancellationToken)
    {
        EnsureConnected();
        using var request = new HttpRequestMessage(method, path);
        if (payload is not null) request.Content = Json(payload);
        using HttpResponseMessage response = await _http!.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static string OwnerQuery(string ownerKey) => string.IsNullOrWhiteSpace(ownerKey) ? "" : "?ownerKey=" + Uri.EscapeDataString(ownerKey);

    private static Dictionary<string, object?> MergeOwner(object value, string ownerKey)
    {
        var result = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value), WireJson) ?? new Dictionary<string, object?>();
        result["ownerKey"] = string.IsNullOrWhiteSpace(ownerKey) ? null : ownerKey;
        return result;
    }

    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    private void EnsureConnected() { if (_http is null || _server is null) throw new InvalidOperationException("Connect to a Workstation first."); }
    private static bool TryProperty(JsonElement element, string name, out JsonElement value) { value = default; if (element.ValueKind != JsonValueKind.Object) return false; foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } return false; }
    private static string ReadString(JsonElement element, params string[] names) { foreach (string name in names) if (TryProperty(element, name, out var value)) return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.ToString(); return ""; }
    private static double? ReadDouble(JsonElement element, string name) => TryProperty(element, name, out var value) && value.TryGetDouble(out double number) ? number : null;
    private static long ReadLong(JsonElement element, string name) => TryProperty(element, name, out var value) && value.TryGetInt64(out long number) ? number : 0;
    private static ulong ReadULong(JsonElement element, string name) => TryProperty(element, name, out var value) && value.TryGetUInt64(out ulong number) ? number : 0;
    private static DateTimeOffset ReadDate(JsonElement element, params string[] names)
    {
        string value = ReadString(element, names);
        return DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : default;
    }
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
