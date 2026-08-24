using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using heirowLLM.Mobile.Models;

namespace heirowLLM.Mobile.Services;

public sealed class HeirowLlmClient : IDisposable
{
    private static readonly JsonSerializerOptions WireJson = new() { PropertyNameCaseInsensitive = true };
    private readonly SecureCredentialStore _credentials;
    private HttpClient? _http;
    private ServerInfo? _server;
    public string ActiveStreamId { get; private set; } = "";
    public bool IsAdministrator { get; private set; }
    public bool IsOwner { get; private set; }
    public string AuthenticatedUserName { get; private set; } = "";
    public string AuthenticatedOwnerKey { get; private set; } = "";

    public HeirowLlmClient(SecureCredentialStore credentials) => _credentials = credentials;

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
        var tokens = new List<string>();
        foreach (string credentialKey in CredentialKeys(server))
        {
            string? candidate = await _credentials.GetServerTokenAsync(credentialKey);
            if (!string.IsNullOrWhiteSpace(candidate) && !tokens.Contains(candidate, StringComparer.Ordinal))
                tokens.Add(candidate);
        }
        foreach (string candidate in tokens)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", candidate);
            try
            {
                using HttpResponseMessage candidateResponse = await _http.GetAsync("/api/web-auth/session", cancellationToken);
                string candidateBody = await candidateResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!candidateResponse.IsSuccessStatusCode) continue;
                using JsonDocument candidateStatus = JsonDocument.Parse(string.IsNullOrWhiteSpace(candidateBody) ? "{}" : candidateBody);
                if (!ReadBool(candidateStatus.RootElement, "authenticated")) continue;
                using HttpResponseMessage candidateHealth = await _http.GetAsync("/api/health", cancellationToken);
                candidateHealth.EnsureSuccessStatusCode();
                ApplyAuthenticatedIdentity(candidateStatus.RootElement);
                return;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                // Try the next securely stored credential alias for this Workstation.
            }
        }
        if (tokens.Count > 0)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens[0]);
        else
            _http.DefaultRequestHeaders.Authorization = null;
        using HttpResponseMessage response = await _http.GetAsync("/api/health", cancellationToken);
        response.EnsureSuccessStatusCode();
        using HttpResponseMessage authResponse = await _http.GetAsync("/api/web-auth/session", cancellationToken);
        string authBody = await authResponse.Content.ReadAsStringAsync(cancellationToken);
        authResponse.EnsureSuccessStatusCode();
        using JsonDocument authStatus = JsonDocument.Parse(string.IsNullOrWhiteSpace(authBody) ? "{}" : authBody);
        if (ReadBool(authStatus.RootElement, "authenticated"))
        {
            ApplyAuthenticatedIdentity(authStatus.RootElement);
            return;
        }

        using HttpResponseMessage mobileResponse = await _http.GetAsync("/api/mobile/status", cancellationToken);
        string mobileBody = await mobileResponse.Content.ReadAsStringAsync(cancellationToken);
        mobileResponse.EnsureSuccessStatusCode();
        using JsonDocument mobileStatus = JsonDocument.Parse(string.IsNullOrWhiteSpace(mobileBody) ? "{}" : mobileBody);
        if (!ReadBool(mobileStatus.RootElement, "paired"))
            throw new UnauthorizedAccessException("Login with a Workstation account or pair this phone before using heirowLLM Mobile.");
        ApplyAuthenticatedIdentity(mobileStatus.RootElement);
    }

    public async Task<WorkstationAuthStatus> GetWorkstationAuthStatusAsync(
        string endpoint,
        string certificateFingerprint = "",
        CancellationToken cancellationToken = default)
    {
        using HttpClient client = CreateBootstrapClient(endpoint, certificateFingerprint);
        using HttpResponseMessage response = await client.GetAsync("/api/web-auth/session", cancellationToken);
        using JsonDocument json = await ReadAuthResponseAsync(response, cancellationToken);
        return new WorkstationAuthStatus
        {
            Authenticated = ReadBool(json.RootElement, "authenticated"),
            CanRegisterOpen = ReadBool(json.RootElement, "canRegisterOpen"),
            Username = ReadString(json.RootElement, "username"),
            OwnerKey = ReadString(json.RootElement, "ownerKey"),
            IsAdministrator = ReadBool(json.RootElement, "isAdministrator"),
            IsOwner = ReadBool(json.RootElement, "isOwner", "isServerOwner")
        };
    }

    public Task<WorkstationAuthResult> LoginToWorkstationAsync(
        string endpoint,
        string username,
        string password,
        string certificateFingerprint = "",
        CancellationToken cancellationToken = default) =>
        SubmitWorkstationAuthAsync(
            endpoint,
            "/api/web-auth/login",
            username,
            password,
            certificateFingerprint,
            cancellationToken);

    public Task<WorkstationAuthResult> RegisterWithWorkstationAsync(
        string endpoint,
        string username,
        string password,
        string certificateFingerprint = "",
        CancellationToken cancellationToken = default) =>
        SubmitWorkstationAuthAsync(
            endpoint,
            "/api/web-auth/registration-request",
            username,
            password,
            certificateFingerprint,
            cancellationToken);

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

    public async Task<MobileAlignmentSnapshot> GetAlignmentAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/alignment", cancellationToken);
        JsonElement alignment = TryProperty(json.RootElement, "alignment", out JsonElement value) ? value : json.RootElement;
        return JsonSerializer.Deserialize<MobileAlignmentSnapshot>(alignment.GetRawText(), WireJson) ?? new MobileAlignmentSnapshot();
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

    public async Task<string> AskChickenChaserAsync(
        string prompt,
        string sessionId,
        string sessionTitle,
        string projectName,
        int messageCount,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/chickenchaser/chat", new
        {
            prompt,
            mode = "chat",
            context = new
            {
                activeView = "mobile-chat",
                activeTitle = string.IsNullOrWhiteSpace(sessionTitle) ? "heirowLLM Mobile" : sessionTitle,
                page = new { title = "heirowLLM Mobile", route = "chat" },
                chat = new
                {
                    sessionId = sessionId ?? "",
                    sessionTitle = sessionTitle ?? "",
                    project = projectName ?? "Unsorted",
                    messageCount
                },
                controls = new[]
                {
                    new { targetId = "mobile:current-session", label = "Current chat session", kind = "session", disabled = false },
                    new { targetId = "mobile:prompt", label = "Message heirowLLM", kind = "editor", disabled = false }
                }
            }
        }, cancellationToken);

        JsonElement root = json.RootElement;
        if (!ReadBool(root, "ok"))
            throw new InvalidOperationException(ReadString(root, "error", "message"));
        if (!TryProperty(root, "result", out JsonElement result))
            return "How can I help?";
        if (result.ValueKind == JsonValueKind.String)
            return result.GetString() ?? "How can I help?";
        string reply = ReadString(result, "reply", "response", "message");
        return string.IsNullOrWhiteSpace(reply) ? "How can I help?" : reply;
    }

    public async Task<ChickenChaserMobileSettings> GetChickenChaserSettingsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/chickenchaser/settings", cancellationToken);
        if (!TryProperty(json.RootElement, "settings", out JsonElement settings))
            return new ChickenChaserMobileSettings();
        return JsonSerializer.Deserialize<ChickenChaserMobileSettings>(settings.GetRawText(), WireJson)
            ?? new ChickenChaserMobileSettings();
    }

    public async Task<ChickenChaserMobileSettings> SaveChickenChaserSettingsAsync(
        ChickenChaserMobileSettings settings,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/chickenchaser/settings", settings, cancellationToken);
        if (TryProperty(json.RootElement, "settings", out JsonElement saved))
            return JsonSerializer.Deserialize<ChickenChaserMobileSettings>(saved.GetRawText(), WireJson) ?? settings;
        return settings;
    }

    public async Task<IReadOnlyList<ContextApprovalRequest>> GetContextApprovalsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/context-approvals?sessionId=" + Uri.EscapeDataString(sessionId ?? ""), cancellationToken);
        ContextApprovalEnvelope envelope = JsonSerializer.Deserialize<ContextApprovalEnvelope>(json.RootElement.GetRawText(), WireJson) ?? new();
        return envelope.Approvals;
    }

    public async Task DecideContextApprovalAsync(string requestId, string sessionId, string action, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/context-approvals", new { requestId, sessionId, action }, cancellationToken);
        if (!ReadBool(json.RootElement, "ok"))
            throw new InvalidOperationException(ReadString(json.RootElement, "error", "message"));
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

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(string model, string service, string interactionMode, string sessionId, string projectId, string reasoningLevel, string sessionReasoningLevel, bool modelSupportsTools, bool jackhammerEnabled, int jackhammerTurnBudget, IReadOnlyList<ChatMessage> messages, IReadOnlyList<AttachmentInfo> attachments, string? requestedStreamId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var uploaded = new List<object>();
        foreach (AttachmentInfo attachment in attachments)
            uploaded.Add(attachment.IsUploaded
                ? new { name = attachment.Name, path = attachment.UploadedPath, type = attachment.MediaType, asFile = true }
                : await UploadProjectFileAsync(sessionId, attachment, "\\", false, null, cancellationToken));
        string streamId = ActiveStreamId = string.IsNullOrWhiteSpace(requestedStreamId) ? "mobile_" + Guid.NewGuid().ToString("N") : requestedStreamId;
        string prompt = messages.LastOrDefault(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))?.Content ?? "";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["service"] = string.IsNullOrWhiteSpace(service) ? "chat" : service,
            ["interactionMode"] = NormalizeInteractionMode(interactionMode),
            ["sessionId"] = sessionId,
            ["projectId"] = string.IsNullOrWhiteSpace(projectId) ? "unsorted" : projectId,
            ["streamId"] = streamId,
            ["reasoningLevel"] = string.IsNullOrWhiteSpace(reasoningLevel) ? "auto" : reasoningLevel,
            ["sessionReasoningLevel"] = string.IsNullOrWhiteSpace(sessionReasoningLevel) ? "inherit" : sessionReasoningLevel,
            ["contextConsentUi"] = modelSupportsTools,
            ["jackhammer"] = new
            {
                enabled = jackhammerEnabled,
                runId = "jackhammer_mobile_" + Guid.NewGuid().ToString("N"),
                turnBudget = Math.Clamp(jackhammerTurnBudget, 1, 200)
            },
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
    public async Task SteerAsync(string streamId, string sessionId, string text, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await PostJsonAsync("/api/chat-stream/steer", new
        {
            streamId,
            sessionId,
            steering = text,
            steeringId = "steer_mobile_" + Guid.NewGuid().ToString("N")
        }, cancellationToken);
        if (!ReadBool(response.RootElement, "ok") || !ReadBool(response.RootElement, "accepted"))
        {
            string error = ReadString(response.RootElement, "error");
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "The Workstation did not accept this heirowForge steering update." : error);
        }
    }

    public async Task<IReadOnlyList<ChatSessionInfo>> GetSessionsAsync(CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/chat-sessions?take=all", cancellationToken);
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
                ProjectId = ReadString(item, "projectId", "project_id"),
                ProjectName = ReadString(item, "projectName", "project_name"),
                Pinned = ReadBool(item, "pinned"),
                PinnedUtc = ReadString(item, "pinnedUtc"),
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

    public async Task<MobileMenuPermissionSnapshot> GetMobileMenuPermissionsAsync(CancellationToken cancellationToken = default)
    {
        string ownerKey = string.IsNullOrWhiteSpace(AuthenticatedOwnerKey) ? "global" : AuthenticatedOwnerKey;
        using JsonDocument json = await GetJsonAsync("/api/chat-permissions?ownerKey=" + Uri.EscapeDataString(ownerKey), cancellationToken);
        JsonElement root = json.RootElement;
        JsonElement permissions = TryProperty(root, "permissions", out JsonElement value) ? value : default;
        return new MobileMenuPermissionSnapshot
        {
            SqlAdmin = permissions.ValueKind == JsonValueKind.Object && ReadBool(permissions, "sqlAdmin"),
            PcAccess = permissions.ValueKind == JsonValueKind.Object && ReadBool(permissions, "pcAccess")
        };
    }

    public async Task<MobileDiagnosticsSnapshot> GetDiagnosticsAsync(string cursor = "", CancellationToken cancellationToken = default)
    {
        string path = "/api/diagnostics";
        if (!string.IsNullOrWhiteSpace(cursor)) path += "?since=" + Uri.EscapeDataString(cursor);
        using JsonDocument json = await GetJsonAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<MobileDiagnosticsSnapshot>(json.RootElement.GetRawText(), WireJson) ?? new MobileDiagnosticsSnapshot();
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
        detail.InteractionMode = ReadString(session, "interactionMode", "interaction_mode");
        if (string.IsNullOrWhiteSpace(detail.InteractionMode)) detail.InteractionMode = "chat";
        detail.ProjectId = ReadString(session, "projectId", "project_id");
        detail.ProjectName = ReadString(session, "projectName", "project_name");
        detail.Pinned = ReadBool(session, "pinned");
        if (string.IsNullOrWhiteSpace(detail.ProjectId)) detail.ProjectId = "unsorted";
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
                {
                    var chatMessage = new ChatMessage { Role = string.IsNullOrWhiteSpace(role) ? "assistant" : role, Content = content, Reasoning = reasoning };
                    if (TryProperty(message, "toolCalls", out JsonElement toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement toolCall in toolCalls.EnumerateArray())
                        {
                            string name = ReadString(toolCall, "toolName", "name", "tool");
                            string status = ReadString(toolCall, "status", "toolStatus", "state");
                            string detailText = ReadString(toolCall, "summary", "label", "resultPreview", "argumentsPreview", "detail");
                            if (!string.IsNullOrWhiteSpace(name))
                                chatMessage.Tools.Add(new ToolActivity { Name = name, Status = string.IsNullOrWhiteSpace(status) ? "completed" : status, Detail = detailText });
                        }
                        chatMessage.WorkSummary = BuildJackhammerWorkSummary(chatMessage.Tools, false);
                    }
                    detail.Messages.Add(chatMessage);
                }
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
    public Task PinSessionAsync(string id, bool pinned, CancellationToken cancellationToken = default) => PostAsync("/api/chat-session-action", new { id, action = pinned ? "pin" : "unpin" }, cancellationToken);
    public Task MoveSessionAsync(string id, string projectId, CancellationToken cancellationToken = default) => PostAsync("/api/chat-session-action", new { id, action = "assign-project", projectId }, cancellationToken);
    public async Task EnsureSessionAsync(string id, string projectId, string model = "", CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/chat-session", new
        {
            id,
            projectId = string.IsNullOrWhiteSpace(projectId) ? "unsorted" : projectId,
            title = "New chat",
            model,
            messages = Array.Empty<object>(),
            files = Array.Empty<object>()
        }, cancellationToken);
        if (!ReadBool(json.RootElement, "ok")) throw new InvalidOperationException(ReadString(json.RootElement, "error"));
    }

    public async Task<IReadOnlyList<ChatProjectInfo>> GetProjectsAsync(bool includeArchived = true, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/chat-projects?includeArchived=" + (includeArchived ? "true" : "false"), cancellationToken);
        JsonElement list = TryProperty(json.RootElement, "projects", out JsonElement projects) ? projects : default;
        var result = new List<ChatProjectInfo>();
        if (list.ValueKind == JsonValueKind.Array) foreach (JsonElement item in list.EnumerateArray()) result.Add(new ChatProjectInfo
        {
            Id = ReadString(item, "id", "projectId"), Name = ReadString(item, "name"), WorkspaceRoot = ReadString(item, "workspaceRoot"),
            UpdatedAt = ReadDate(item, "updatedUtc"), LastActivityAt = ReadDate(item, "lastActivityUtc"), SessionCount = (int)ReadLong(item, "sessionCount"),
            Pinned = ReadBool(item, "pinned"), Archived = ReadBool(item, "archived"), BuiltIn = ReadBool(item, "builtIn")
        });
        return result;
    }

    public Task CreateProjectAsync(string name, CancellationToken cancellationToken = default) => PostAsync("/api/chat-project", new { action = "create", name }, cancellationToken);
    public Task RenameProjectAsync(string id, string name, CancellationToken cancellationToken = default) => PostAsync("/api/chat-project", new { action = "rename", projectId = id, name }, cancellationToken);
    public Task PinProjectAsync(string id, bool pinned, CancellationToken cancellationToken = default) => PostAsync("/api/chat-project", new { action = pinned ? "pin" : "unpin", projectId = id }, cancellationToken);
    public Task ArchiveProjectAsync(string id, bool archived, CancellationToken cancellationToken = default) => PostAsync("/api/chat-project", new { action = archived ? "archive" : "restore", projectId = id }, cancellationToken);

    public async Task<ProjectFilesSnapshot> GetProjectFilesAsync(string sessionId, string path = "\\", string search = "", string sort = "name", CancellationToken cancellationToken = default)
    {
        string query = "?sessionId=" + Uri.EscapeDataString(sessionId) + "&kind=session&sort=" + Uri.EscapeDataString(sort ?? "name");
        if (!string.IsNullOrWhiteSpace(search)) query += "&search=" + Uri.EscapeDataString(search);
        else query += "&path=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(path) ? "\\" : path);
        using JsonDocument json = await GetJsonAsync("/api/chat-solution-explorer" + query, cancellationToken);
        if (!ReadBool(json.RootElement, "ok")) throw new InvalidOperationException(ReadString(json.RootElement, "error"));
        return JsonSerializer.Deserialize<ProjectFilesSnapshot>(json.RootElement.GetRawText(), WireJson) ?? new ProjectFilesSnapshot();
    }

    public async Task<ProjectFileVersionsSnapshot> GetProjectFileVersionsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await GetJsonAsync("/api/project-file-versions?sessionId=" + Uri.EscapeDataString(sessionId), cancellationToken);
        return JsonSerializer.Deserialize<ProjectFileVersionsSnapshot>(json.RootElement.GetRawText(), WireJson) ?? new ProjectFileVersionsSnapshot();
    }

    public async Task<ProjectFileVersionsSnapshot> MutateProjectFileVersionAsync(string sessionId, string action, string name = "", string versionId = "", CancellationToken cancellationToken = default)
    {
        using JsonDocument json = await PostJsonAsync("/api/project-file-versions", new { sessionId, action, name, versionId }, cancellationToken);
        return JsonSerializer.Deserialize<ProjectFileVersionsSnapshot>(json.RootElement.GetRawText(), WireJson) ?? new ProjectFileVersionsSnapshot();
    }

    public async Task DeleteProjectFileAsync(string sessionId, ProjectFileEntry entry, CancellationToken cancellationToken = default)
    {
        if (entry.IsDirectory && (string.IsNullOrWhiteSpace(entry.Path) || entry.Path == "\\"))
            throw new InvalidOperationException("The Project Files root cannot be deleted.");
        string path = "/api/chat-file?sessionId=" + Uri.EscapeDataString(sessionId) + "&kind=session&type=" +
            (entry.IsDirectory ? "directory" : "file") + "&path=" + Uri.EscapeDataString(entry.Path);
        using JsonDocument _ = await SendJsonAsync(HttpMethod.Delete, path, null, cancellationToken);
    }

    public async Task<byte[]> DownloadProjectFileAsync(string sessionId, ProjectFileEntry entry, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        string path = "/api/chat-file-download?sessionId=" + Uri.EscapeDataString(sessionId) + "&kind=session&path=" + Uri.EscapeDataString(entry.Path);
        if (entry.IsDirectory) path += "&zip=true&name=" + Uri.EscapeDataString(entry.Name);
        using HttpResponseMessage response = await _http!.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessWithServerMessageAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public Task<JsonDocument> PreviewProjectFileAsync(string sessionId, ProjectFileEntry entry, CancellationToken cancellationToken = default) =>
        GetJsonAsync("/api/chat-file-preview?sessionId=" + Uri.EscapeDataString(sessionId) + "&kind=session&path=" + Uri.EscapeDataString(entry.Path), cancellationToken);

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

    private static async Task<WorkstationAuthResult> SubmitWorkstationAuthAsync(
        string endpoint,
        string path,
        string username,
        string password,
        string certificateFingerprint,
        CancellationToken cancellationToken)
    {
        using HttpClient client = CreateBootstrapClient(endpoint, certificateFingerprint);
        using HttpResponseMessage response = await client.PostAsync(
            path,
            Json(new { username = (username ?? "").Trim(), password = password ?? "", remember = true }),
            cancellationToken);
        using JsonDocument json = await ReadAuthResponseAsync(response, cancellationToken);
        return new WorkstationAuthResult
        {
            Authenticated = ReadBool(json.RootElement, "authenticated") || !string.IsNullOrWhiteSpace(ReadString(json.RootElement, "accessToken")),
            Pending = ReadBool(json.RootElement, "pending"),
            Username = ReadString(json.RootElement, "username"),
            AccessToken = ReadString(json.RootElement, "accessToken", "access_token", "token"),
            Message = ReadString(json.RootElement, "message"),
            OwnerKey = ReadString(json.RootElement, "ownerKey"),
            IsAdministrator = ReadBool(json.RootElement, "isAdministrator"),
            IsOwner = ReadBool(json.RootElement, "isOwner", "isServerOwner")
        };
    }

    private void ApplyAuthenticatedIdentity(JsonElement root)
    {
        AuthenticatedUserName = ReadString(root, "username");
        AuthenticatedOwnerKey = ReadString(root, "ownerKey");
        IsOwner = ReadBool(root, "isOwner", "isServerOwner");
        IsAdministrator = IsOwner || ReadBool(root, "isAdministrator", "pcAccessEligible");
    }

    private static HttpClient CreateBootstrapClient(string endpoint, string certificateFingerprint)
    {
        var handler = new HttpClientHandler();
        if (!string.IsNullOrWhiteSpace(certificateFingerprint))
        {
            string expected = NormalizeFingerprint(certificateFingerprint);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                certificate is not null &&
                NormalizeFingerprint(certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256))
                    .Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        return new HttpClient(handler)
        {
            BaseAddress = new Uri(NormalizeBaseUrl(endpoint)),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    private static async Task<JsonDocument> ReadAuthResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        JsonDocument json;
        try { json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body); }
        catch (JsonException)
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidOperationException("The Workstation returned an invalid authentication response.");
        }
        if (!response.IsSuccessStatusCode || !ReadBool(json.RootElement, "ok"))
        {
            string message = ReadString(json.RootElement, "error", "message", "detail");
            json.Dispose();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "Workstation authentication failed with HTTP " + (int)response.StatusCode + "."
                : message);
        }
        return json;
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
            MobileAlignmentSnapshot? alignment = TryProperty(root, "alignment", out JsonElement alignmentElement)
                ? JsonSerializer.Deserialize<MobileAlignmentSnapshot>(alignmentElement.GetRawText(), WireJson)
                : null;
            string argumentsPreview = ReadString(root, "argumentsPreview", "arguments", "args");
            string toolName = ReadString(root, "toolName", "name", "tool");
            string checkpointJson = toolName.Equals("goal_checkpoint", StringComparison.OrdinalIgnoreCase)
                ? (string.IsNullOrWhiteSpace(argumentsPreview) ? ReadString(root, "resultPreview", "result") : argumentsPreview)
                : "";
            var checkpoint = ParseJackhammerCheckpoint(checkpointJson);
            return new ChatStreamEvent
            {
                Type = string.IsNullOrWhiteSpace(type) ? "delta" : type,
                Content = ReadString(root, "content", "contentDelta", "content_delta", "text", "delta", "answer", "answerContent", "answerDelta", "answer_delta", "message", "response", "error"),
                Reasoning = ReadString(root, "reasoning", "reasoningContent", "reasoning_content", "reasoningDelta", "reasoning_delta", "thought", "thoughts", "thoughtContent", "thought_content", "thinking", "thinkingContent", "thinking_content", "thinkingDelta", "thinking_delta", "analysis", "analysisContent", "analysis_content"),
                Status = ReadString(root, "status", "state", "message", "statusText"),
                Progress = ReadDouble(root, "progress"),
                ToolName = toolName,
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
                JackhammerSteps = checkpoint.Steps,
                JackhammerGoal = checkpoint.Goal,
                JackhammerStatus = checkpoint.Status,
                JackhammerProgressPercent = checkpoint.ProgressPercent,
                Alignment = alignment,
                RawJson = line
            };
        }
        catch { return new ChatStreamEvent { Type = "unknown", RawJson = line }; }
    }

    private static (IReadOnlyList<JackhammerPlanStep> Steps, string Goal, string Status, int ProgressPercent) ParseJackhammerCheckpoint(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (Array.Empty<JackhammerPlanStep>(), "", "", -1);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string goal = ReadString(root, "goal");
            string checkpointStatus = ReadString(root, "status");
            int progressPercent = -1;
            if (TryProperty(root, "progressPercent", out JsonElement progressElement) &&
                progressElement.ValueKind == JsonValueKind.Number && progressElement.TryGetDouble(out double numericProgress))
                progressPercent = Math.Clamp((int)Math.Round(numericProgress), 0, 100);
            if (!TryProperty(root, "steps", out JsonElement steps) || steps.ValueKind != JsonValueKind.Array)
                return (Array.Empty<JackhammerPlanStep>(), goal, checkpointStatus, progressPercent);
            var parsed = new List<(string Status, string Title, string Summary)>();
            foreach (JsonElement element in steps.EnumerateArray().Take(8))
            {
                string value = element.ValueKind == JsonValueKind.String
                    ? element.GetString() ?? ""
                    : element.ValueKind == JsonValueKind.Object
                        ? string.Join("|", new[] { ReadString(element, "status"), ReadString(element, "title", "action", "text"), ReadString(element, "summary", "description") }.Where(part => !string.IsNullOrWhiteSpace(part)))
                        : "";
                if (string.IsNullOrWhiteSpace(value)) continue;
                string[] parts = value.Split('|', 3, StringSplitOptions.TrimEntries);
                string status = parts.Length >= 2 ? parts[0].Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') : "pending";
                string title = parts.Length >= 2 ? parts[1].Trim() : value.Trim();
                string summary = parts.Length >= 3 ? parts[2].Trim() : "";
                if (status == "complete") status = "completed";
                if (status is not ("pending" or "in_progress" or "completed" or "blocked")) status = "pending";
                if (string.IsNullOrWhiteSpace(summary))
                {
                    int colon = title.IndexOf(": ", StringComparison.Ordinal);
                    if (colon is >= 4 and <= 72)
                    {
                        summary = title[(colon + 2)..].Trim();
                        title = title[..colon].Trim();
                    }
                }
                if (string.IsNullOrWhiteSpace(summary))
                {
                    summary = status switch
                    {
                        "completed" => "This part of the work is complete and its result is available to the remaining steps.",
                        "in_progress" => "heirowForge is actively working through this part of the goal now.",
                        "blocked" => "This step cannot continue until the blocker shown by the run is resolved.",
                        _ => "This step is queued and will begin after the preceding work is complete."
                    };
                }
                if (title.Length > 0) parsed.Add((status, title, summary));
            }
            JackhammerPlanStep[] result = parsed.Select((step, index) => new JackhammerPlanStep
            {
                Status = step.Status,
                Title = step.Title,
                Summary = step.Summary,
                Number = index + 1,
                Total = parsed.Count
            }).ToArray();
            return (result, goal, checkpointStatus, progressPercent);
        }
        catch { return (Array.Empty<JackhammerPlanStep>(), "", "", -1); }
    }

    private static string BuildJackhammerWorkSummary(IEnumerable<ToolActivity> tools, bool isGenerating)
    {
        ToolActivity[] items = tools.Take(12).ToArray();
        if (items.Length == 0) return "";
        var lines = new List<string> { "### heirowForge work tree" };
        foreach (ToolActivity item in items)
        {
            string state = item.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                           item.Status.Equals("complete", StringComparison.OrdinalIgnoreCase) ||
                           item.Status.Equals("succeeded", StringComparison.OrdinalIgnoreCase)
                ? "[x]"
                : item.Status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                  item.Status.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                    ? "[!]"
                    : "[ ]";
            string detail = string.IsNullOrWhiteSpace(item.Detail) ? "" : " - " + item.Detail.Trim();
            lines.Add($"- {state} **{item.Name}**{detail}");
        }
        lines.Add(isGenerating ? "\n_Work continues automatically._" : "\n_Work run complete._");
        return string.Join("\n", lines);
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) throw new ArgumentException("Enter a valid Workstation http/https address.");
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    internal static IReadOnlyList<string> CredentialKeys(ServerInfo server)
    {
        var keys = new List<string>();
        static void Add(List<string> target, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !target.Contains(value, StringComparer.OrdinalIgnoreCase))
                target.Add(value);
        }

        Add(keys, server.CredentialKey);
        Add(keys, server.LaunchKey);
        try
        {
            Uri endpoint = new(NormalizeBaseUrl(server.Endpoint));
            Add(keys, endpoint.Host);
            Add(keys, endpoint.GetLeftPart(UriPartial.Authority));
        }
        catch
        {
            // ConnectAsync reports an invalid endpoint through its normal validation path.
        }
        return keys;
    }

    private static string NormalizeInteractionMode(string value)
    {
        value = (value ?? "").Trim().ToLowerInvariant();
        return value is "plan" or "agent" or "companion" ? value : "chat";
    }

    public async Task<object> UploadProjectFileAsync(string sessionId, AttachmentInfo file, string targetPath = "\\", bool extractZip = false, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        file.UploadState = "uploading";
        file.UploadError = "";
        file.UploadProgress = 0;
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sessionId,
            name = file.Name,
            type = file.MediaType,
            dataUrl = file.DataUrl,
            asFile = true,
            targetPath = string.IsNullOrWhiteSpace(targetPath) ? "\\" : targetPath,
            directoryPath = string.IsNullOrWhiteSpace(targetPath) ? "\\" : targetPath,
            extractZip
        });
        using var content = new ProgressJsonContent(payload, value =>
        {
            file.UploadProgress = value;
            progress?.Report(value);
        });
        using HttpResponseMessage response = await _http!.PostAsync("/api/chat-file", content, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            file.UploadState = "failed";
            file.UploadError = ExtractServerError(body, response.ReasonPhrase);
            throw new InvalidOperationException(file.UploadError);
        }
        using JsonDocument json = JsonDocument.Parse(body);
        if (!ReadBool(json.RootElement, "ok") && TryProperty(json.RootElement, "error", out _))
        {
            file.UploadState = "failed";
            file.UploadError = ReadString(json.RootElement, "error");
            throw new InvalidOperationException(file.UploadError);
        }
        file.UploadProgress = 1;
        file.UploadState = "complete";
        if (TryProperty(json.RootElement, "file", out var uploaded))
        {
            file.UploadedPath = ReadString(uploaded, "path", "relativePath");
            return JsonSerializer.Deserialize<object>(uploaded.GetRawText())!;
        }
        return new { name = file.Name, path = file.UploadedPath, type = file.MediaType, asFile = true };
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

    private static async Task EnsureSuccessWithServerMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(ExtractServerError(body, response.ReasonPhrase));
    }

    private static string ExtractServerError(string body, string? fallback)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            string error = ReadString(json.RootElement, "error", "message", "detail");
            if (!string.IsNullOrWhiteSpace(error)) return error;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? fallback ?? "The Workstation rejected the request." : body;
    }

    private sealed class ProgressJsonContent : HttpContent
    {
        private readonly byte[] _payload;
        private readonly Action<double> _progress;

        public ProgressJsonContent(byte[] payload, Action<double> progress)
        {
            _payload = payload;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Headers.ContentLength = payload.LongLength;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            const int chunkSize = 64 * 1024;
            long sent = 0;
            while (sent < _payload.LongLength)
            {
                int count = (int)Math.Min(chunkSize, _payload.LongLength - sent);
                await stream.WriteAsync(_payload.AsMemory((int)sent, count));
                sent += count;
                _progress(_payload.Length == 0 ? 1 : (double)sent / _payload.Length);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.LongLength;
            return true;
        }
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
