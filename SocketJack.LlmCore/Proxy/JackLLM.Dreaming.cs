using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using LmVs;
using SocketJack.Net.Services;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    private readonly object _dreamLock = new();
    private readonly CancellationTokenSource _dreamLifetime = new();
    private readonly Dictionary<string, DreamState> _dreamStates = new(StringComparer.OrdinalIgnoreCase);
    private Task _dreamScheduler;

    private sealed class DreamSettings
    {
        public bool enabled { get; set; }
        public string preset { get; set; } = "conservative";
        public int pollSeconds { get; set; } = 5;
        public int startGraceSeconds { get; set; } = 30;
        public int pauseGraceSeconds { get; set; } = 3;
        public int recurrenceMinutes { get; set; } = 240;
        public int maxRunMinutes { get; set; } = 10;
        public int tokenBudget { get; set; } = 2048;
        public int sessionsPerPass { get; set; } = 6;
        public int startCpuPercent { get; set; } = 35;
        public int pauseCpuPercent { get; set; } = 65;
        public int startRamPercent { get; set; } = 65;
        public int pauseRamPercent { get; set; } = 82;
        public int startGpuPercent { get; set; } = 30;
        public int pauseGpuPercent { get; set; } = 70;
        public int startVramPercent { get; set; } = 55;
        public int pauseVramPercent { get; set; } = 82;
        public int startDiskPercent { get; set; } = 35;
        public int pauseDiskPercent { get; set; } = 75;
        public string model { get; set; } = "lm-studio";
        public string service { get; set; } = "";
        public bool autoSaveStrictFacts { get; set; } = true;
    }

    private sealed class DreamResources
    {
        public double cpuPercent { get; set; }
        public double ramPercent { get; set; }
        public double gpuPercent { get; set; }
        public double vramPercent { get; set; }
        public double diskPercent { get; set; }
        public bool foregroundModelWork { get; set; }
        public string sampledUtc { get; set; } = "";
    }

    private sealed class DreamState
    {
        public string ownerKey { get; set; } = "global";
        public DreamSettings settings { get; set; } = new();
        public string status { get; set; } = "waiting-for-resources";
        public string phase { get; set; } = "waiting";
        public int progress { get; set; }
        public string limitingResource { get; set; } = "";
        public string startedUtc { get; set; } = "";
        public string updatedUtc { get; set; } = "";
        public string completedUtc { get; set; } = "";
        public string lastRunUtc { get; set; } = "";
        public string eligibleSinceUtc { get; set; } = "";
        public string pressureSinceUtc { get; set; } = "";
        public bool manualRequested { get; set; }
        public bool userPaused { get; set; }
        public DreamResources resources { get; set; } = new();
        public List<DreamJournal> journal { get; set; } = new();
        public CancellationTokenSource cancellation { get; set; }
    }

    private sealed class DreamJournal
    {
        public string id { get; set; } = Guid.NewGuid().ToString("N");
        public string status { get; set; } = "running";
        public string summary { get; set; } = "";
        public string rawReflection { get; set; } = "";
        public string createdUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
        public string completedUtc { get; set; } = "";
        public List<DreamCandidate> candidates { get; set; } = new();
        public List<DreamToolAudit> tools { get; set; } = new();
    }

    private sealed class DreamCandidate
    {
        public string id { get; set; } = Guid.NewGuid().ToString("N");
        public string text { get; set; } = "";
        public string disposition { get; set; } = "review";
        public double confidence { get; set; }
        public bool explicitFact { get; set; }
        public bool sensitive { get; set; }
        public bool conflicting { get; set; }
        public string sourceSessionId { get; set; } = "";
        public string staleMemoryId { get; set; } = "";
        public string staleMemoryText { get; set; } = "";
        public string candidateType { get; set; } = "memory";
    }

    private sealed class DreamToolAudit
    {
        public string tool { get; set; } = "";
        public string permission { get; set; } = "";
        public string status { get; set; } = "blocked";
        public string reason { get; set; } = "";
        public string createdUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }

    private void RegisterDreamRoutes(HttpServer server)
    {
        LoadDreamState();
        _dreamScheduler ??= Task.Run(() => DreamSchedulerAsync(_dreamLifetime.Token));
        server.Map("GET", "/api/dream-settings", (c, r, ct) => DreamSettingsGet(c, r));
        server.Map("PUT", "/api/dream-settings", (c, r, ct) => DreamSettingsPut(c, r));
        server.Map("GET", "/api/dream-status", (c, r, ct) => DreamStatus(c, r));
        server.Map("POST", "/api/dream-runs", (c, r, ct) => DreamControl(c, r));
        server.Map("GET", "/api/dream-journal", (c, r, ct) => DreamJournalGet(c, r));
        server.Map("POST", "/api/dream-candidates", (c, r, ct) => DreamCandidatePost(c, r));
    }

    private DreamState DreamOwner(NetworkConnection connection, HttpRequest request)
    {
        string owner = NormalizeChatFilesystemOwnerKey(GetChatSessionOwnerKey(connection, request));
        if (string.IsNullOrWhiteSpace(owner)) owner = "global";
        lock (_dreamLock)
        {
            if (!_dreamStates.TryGetValue(owner, out DreamState state)) _dreamStates[owner] = state = new DreamState { ownerKey = owner };
            return state;
        }
    }

    private string DreamSettingsGet(NetworkConnection c, HttpRequest r)
    {
        DreamState state = DreamOwner(c, r);
        return JsonSerializer.Serialize(new { ok = true, settings = state.settings, presets = DreamPresets() });
    }

    private string DreamSettingsPut(NetworkConnection c, HttpRequest r)
    {
        try
        {
            DreamState state = DreamOwner(c, r);
            DreamSettings settings = JsonSerializer.Deserialize<DreamSettings>(r.Body ?? "{}", new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Settings required.");
            NormalizeDreamSettings(settings);
            lock (_dreamLock) state.settings = settings;
            SaveDreamState();
            return JsonSerializer.Serialize(new { ok = true, settings });
        }
        catch (Exception ex) { return BuildJsonError(r, 400, "Bad Request", ex.Message); }
    }

    private string DreamStatus(NetworkConnection c, HttpRequest r)
    {
        DreamState s = DreamOwner(c, r);
        lock (_dreamLock) return JsonSerializer.Serialize(new { ok = true, s.status, s.phase, s.progress, s.limitingResource, resources = s.resources, s.startedUtc, s.updatedUtc, s.completedUtc, s.lastRunUtc, enabled = s.settings.enabled, s.manualRequested });
    }

    private string DreamControl(NetworkConnection c, HttpRequest r)
    {
        DreamState s = DreamOwner(c, r);
        using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(r.Body) ? "{}" : r.Body);
        string action = doc.RootElement.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "start" : "start";
        lock (_dreamLock)
        {
            if (action is "start" or "resume") { s.manualRequested = true; s.userPaused = false; s.status = "waiting-for-resources"; }
            else if (action == "pause") { s.userPaused = true; s.status = "paused-foreground-work"; s.limitingResource = "user"; s.cancellation?.Cancel(); }
            else if (action == "stop") { s.manualRequested = false; s.userPaused = true; s.status = "canceled"; s.cancellation?.Cancel(); }
            else return BuildJsonError(r, 400, "Bad Request", "Unknown dream action.");
            s.updatedUtc = DateTimeOffset.UtcNow.ToString("O");
        }
        SaveDreamState();
        return DreamStatus(c, r);
    }

    private string DreamJournalGet(NetworkConnection c, HttpRequest r)
    {
        DreamState s = DreamOwner(c, r);
        lock (_dreamLock) return JsonSerializer.Serialize(new { ok = true, journal = s.journal.OrderByDescending(x => x.createdUtc).Take(100) });
    }

    private string DreamCandidatePost(NetworkConnection c, HttpRequest r)
    {
        DreamState s = DreamOwner(c, r);
        using JsonDocument doc = JsonDocument.Parse(r.Body ?? "{}");
        string id = doc.RootElement.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "" : "";
        string action = doc.RootElement.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
        lock (_dreamLock)
        {
            DreamCandidate candidate = s.journal.SelectMany(x => x.candidates).FirstOrDefault(x => x.id == id);
            if (candidate == null) return BuildJsonError(r, 404, "Not Found", "Dream candidate not found.");
            if (action == "approve")
            {
                if (!TrySaveChatMemory(s.ownerKey, candidate.text, candidate.sourceSessionId, "conflict-approved", out _, out string error)) return BuildJsonError(r, 400, "Bad Request", error);
                candidate.disposition = "approved";
            }
            else if (action == "overwrite")
            {
                SoftDeleteChatMemories(s.ownerKey, candidate.staleMemoryId, "", false);
                if (!TrySaveChatMemory(s.ownerKey, candidate.text, candidate.sourceSessionId, "conflict-overwrite", out _, out string error)) return BuildJsonError(r, 400, "Bad Request", error);
                candidate.disposition = "overwritten";
            }
            else if (action == "delete-stale")
            {
                SoftDeleteChatMemories(s.ownerKey, candidate.staleMemoryId, "", false);
                candidate.disposition = "stale-deleted";
            }
            else if (action is "reject" or "delete") candidate.disposition = action == "reject" ? "rejected" : "deleted";
            else return BuildJsonError(r, 400, "Bad Request", "Unknown candidate action.");
        }
        SaveDreamState();
        return JsonSerializer.Serialize(new { ok = true });
    }

    private async Task DreamSchedulerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                DreamState[] states;
                lock (_dreamLock) states = _dreamStates.Values.ToArray();
                foreach (DreamState state in states) EvaluateDream(state, token);
            }
            catch (Exception ex) { LogMessage("[Dream] Scheduler: " + ex.Message); }
            int seconds;
            lock (_dreamLock) seconds = Math.Max(2, _dreamStates.Values.Select(x => x.settings.pollSeconds).DefaultIfEmpty(5).Min());
            await Task.Delay(TimeSpan.FromSeconds(seconds), token).ConfigureAwait(false);
        }
    }

    private void EvaluateDream(DreamState s, CancellationToken lifetime)
    {
        if ((!s.settings.enabled && !s.manualRequested) || s.userPaused) return;
        s.resources = SampleDreamResources();
        string pressure = DreamPressure(s.settings, s.resources, s.status == "running");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(pressure))
        {
            s.eligibleSinceUtc = "";
            if (!DateTimeOffset.TryParse(s.pressureSinceUtc, out DateTimeOffset since)) s.pressureSinceUtc = (since = now).ToString("O");
            if ((now - since).TotalSeconds >= s.settings.pauseGraceSeconds)
            {
                s.limitingResource = pressure;
                s.status = s.resources.foregroundModelWork ? "paused-foreground-work" : "paused-resource-pressure";
                s.cancellation?.Cancel();
            }
            return;
        }
        s.pressureSinceUtc = "";
        if (!DateTimeOffset.TryParse(s.eligibleSinceUtc, out DateTimeOffset eligible)) s.eligibleSinceUtc = (eligible = now).ToString("O");
        if ((now - eligible).TotalSeconds < s.settings.startGraceSeconds || s.cancellation != null) return;
        if (!s.manualRequested && DateTimeOffset.TryParse(s.lastRunUtc, out DateTimeOffset last) && now - last < TimeSpan.FromMinutes(s.settings.recurrenceMinutes)) return;
        s.cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _ = RunDreamAsync(s, s.cancellation.Token);
    }

    private async Task RunDreamAsync(DreamState s, CancellationToken token)
    {
        DreamJournal entry = new();
        lock (_dreamLock) { s.status = "running"; s.phase = "session-selection"; s.progress = 10; s.startedUtc = s.updatedUtc = DateTimeOffset.UtcNow.ToString("O"); s.journal.Add(entry); }
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(s.settings.maxRunMinutes));
            var sessions = GetChatSessionDiagnostics().Where(x => NormalizeChatFilesystemOwnerKey(x.OwnerKey) == s.ownerKey).Take(s.settings.sessionsPerPass).ToList();
            s.phase = "reflection"; s.progress = 35;
            string index = string.Join("\n", sessions.Select(x => $"- {x.Id}: {x.Title}; {x.MessageCount} messages; {x.UpdatedUtc}"));
            string instruction = "You are JackLLM Dreaming. Return JSON only: {summary:string,candidates:[{text,confidence,explicitFact,sensitive,conflicting,sourceSessionId}]}. Only direct durable user facts/preferences may set explicitFact true. Never invent facts. Session index:\n" + index;
            string body = JsonSerializer.Serialize(new { model = s.settings.model, messages = new[] { new { role = "system", content = instruction } }, temperature = .2, max_tokens = s.settings.tokenBudget, stream = false });
            ChatPermissionState dreamPermissions = BuildDreamPermissions(s.ownerKey);
            bool vsTools = dreamPermissions.agentAccess && dreamPermissions.vsCopilotTools;
            body = AddProxyResearchTools(body, dreamPermissions, vsTools, dreamPermissions.terminalCommands, dreamPermissions.internetSearch, s.ownerKey);
            ChatUiCompletion completion = await ExecuteChatUiCompletionWithProxyToolsAsync(body, s.ownerKey, "dream-" + entry.id, timeout.Token, emitToolCall: tool => {
                lock (_dreamLock) entry.tools.Add(new DreamToolAudit { tool = tool.Name, permission = DreamPermissionForTool(tool.Name), status = tool.Status, reason = tool.Summary });
                RecordObservabilityEvent("dream", "dream tool", tool.Status, tool.Name, s.ownerKey, "dream:" + tool.Name, tool.DurationMs);
            }).ConfigureAwait(false);
            string response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = completion.Content } } } });
            s.phase = "consolidation"; s.progress = 75;
            ParseDream(response, entry);
            foreach (DreamCandidate candidate in entry.candidates)
                if (s.settings.autoSaveStrictFacts && candidate.explicitFact && candidate.confidence >= .9 && !candidate.sensitive && !candidate.conflicting && TrySaveChatMemory(s.ownerKey, candidate.text, candidate.sourceSessionId, "dream-auto", out _, out _)) candidate.disposition = "auto-saved";
            entry.status = "completed"; entry.completedUtc = DateTimeOffset.UtcNow.ToString("O");
            lock (_dreamLock) { s.status = "completed"; s.phase = "complete"; s.progress = 100; s.completedUtc = s.lastRunUtc = entry.completedUtc; s.manualRequested = false; }
            RecordObservabilityEvent("dream", "dream completed", "completed", entry.summary, s.ownerKey, "/api/dream-runs", 0);
        }
        catch (OperationCanceledException) { entry.status = "paused"; if (s.status == "running") s.status = "paused-resource-pressure"; }
        catch (Exception ex) { entry.status = "failed"; entry.summary = ex.Message; s.status = "failed"; }
        finally { entry.completedUtc = DateTimeOffset.UtcNow.ToString("O"); lock (_dreamLock) { s.cancellation?.Dispose(); s.cancellation = null; s.updatedUtc = DateTimeOffset.UtcNow.ToString("O"); } SaveDreamState(); }
    }

    private static void ParseDream(string response, DreamJournal entry)
    {
        entry.rawReflection = response ?? "";
        try
        {
            using JsonDocument envelope = JsonDocument.Parse(response);
            string content = envelope.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "{}";
            int first = content.IndexOf('{'), last = content.LastIndexOf('}'); if (first >= 0 && last > first) content = content.Substring(first, last - first + 1);
            using JsonDocument json = JsonDocument.Parse(content);
            entry.summary = json.RootElement.TryGetProperty("summary", out JsonElement summary) ? summary.GetString() ?? "" : "Dream completed.";
            if (json.RootElement.TryGetProperty("candidates", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in list.EnumerateArray()) entry.candidates.Add(new DreamCandidate {
                    text = item.TryGetProperty("text", out JsonElement text) ? text.GetString() ?? "" : "",
                    confidence = item.TryGetProperty("confidence", out JsonElement confidence) && confidence.TryGetDouble(out double value) ? value : 0,
                    explicitFact = item.TryGetProperty("explicitFact", out JsonElement fact) && fact.ValueKind == JsonValueKind.True,
                    sensitive = item.TryGetProperty("sensitive", out JsonElement sensitive) && sensitive.ValueKind == JsonValueKind.True,
                    conflicting = item.TryGetProperty("conflicting", out JsonElement conflicting) && conflicting.ValueKind == JsonValueKind.True,
                    sourceSessionId = item.TryGetProperty("sourceSessionId", out JsonElement source) ? source.GetString() ?? "" : ""
                });
        }
        catch { entry.summary = "Dream output retained for manual review because structured parsing failed."; }
    }

    private DreamResources SampleDreamResources()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ServerHardwarePercentMetric cpu = BuildServerHardwareCpuMetric(now);
        ServerHardwareRamMetric ram = BuildServerHardwareRamMetric();
        ServerHardwareGpuMetric gpu = BuildServerHardwareGpuMetric(now);
        ServerHardwareIoMetric io = BuildServerHardwareIoMetric(now);
        double diskPercent = io.available ? Math.Clamp((io.readBps + io.writeBps) * 100d / (100d * 1024d * 1024d), 0, 100) : 0;
        return new DreamResources {
            cpuPercent = cpu.percent ?? 0, ramPercent = ram.percent ?? 0, gpuPercent = gpu.percent ?? 0, vramPercent = gpu.vramPercent ?? 0, diskPercent = diskPercent,
            foregroundModelWork = GetActivePromptSessionDiagnostics().Any(x => x.Status == "running"), sampledUtc = now.ToString("O")
        };
    }

    private static string DreamPressure(DreamSettings s, DreamResources r, bool running)
    {
        if (r.foregroundModelWork) return "foreground-model-work";
        if (r.cpuPercent > (running ? s.pauseCpuPercent : s.startCpuPercent)) return "cpu";
        if (r.ramPercent > (running ? s.pauseRamPercent : s.startRamPercent)) return "ram";
        if (r.gpuPercent > (running ? s.pauseGpuPercent : s.startGpuPercent)) return "gpu";
        if (r.vramPercent > (running ? s.pauseVramPercent : s.startVramPercent)) return "vram";
        if (r.diskPercent > (running ? s.pauseDiskPercent : s.startDiskPercent)) return "disk";
        return "";
    }

    private static object DreamPresets() => new {
        conservative = new { startCpuPercent = 35, pauseCpuPercent = 65, startRamPercent = 65, pauseRamPercent = 82, startGpuPercent = 30, pauseGpuPercent = 70, startVramPercent = 55, pauseVramPercent = 82, startDiskPercent = 35, pauseDiskPercent = 75, startGraceSeconds = 30, pauseGraceSeconds = 3 },
        balanced = new { startCpuPercent = 50, pauseCpuPercent = 75, startRamPercent = 72, pauseRamPercent = 88, startGpuPercent = 45, pauseGpuPercent = 80, startVramPercent = 65, pauseVramPercent = 88, startDiskPercent = 50, pauseDiskPercent = 82, startGraceSeconds = 15, pauseGraceSeconds = 5 },
        aggressive = new { startCpuPercent = 70, pauseCpuPercent = 90, startRamPercent = 82, pauseRamPercent = 94, startGpuPercent = 70, pauseGpuPercent = 92, startVramPercent = 80, pauseVramPercent = 94, startDiskPercent = 70, pauseDiskPercent = 92, startGraceSeconds = 5, pauseGraceSeconds = 8 }
    };

    private static void NormalizeDreamSettings(DreamSettings s)
    {
        s.pollSeconds = Math.Clamp(s.pollSeconds, 2, 300); s.startGraceSeconds = Math.Clamp(s.startGraceSeconds, 0, 3600); s.pauseGraceSeconds = Math.Clamp(s.pauseGraceSeconds, 0, 300);
        s.recurrenceMinutes = Math.Clamp(s.recurrenceMinutes, 1, 10080); s.maxRunMinutes = Math.Clamp(s.maxRunMinutes, 1, 240); s.tokenBudget = Math.Clamp(s.tokenBudget, 128, 32768); s.sessionsPerPass = Math.Clamp(s.sessionsPerPass, 1, 50);
        (s.startCpuPercent, s.pauseCpuPercent) = ClampPair(s.startCpuPercent, s.pauseCpuPercent);
        (s.startRamPercent, s.pauseRamPercent) = ClampPair(s.startRamPercent, s.pauseRamPercent);
        (s.startGpuPercent, s.pauseGpuPercent) = ClampPair(s.startGpuPercent, s.pauseGpuPercent);
        (s.startVramPercent, s.pauseVramPercent) = ClampPair(s.startVramPercent, s.pauseVramPercent);
        (s.startDiskPercent, s.pauseDiskPercent) = ClampPair(s.startDiskPercent, s.pauseDiskPercent);
    }
    private static (int Start, int Pause) ClampPair(int start, int pause) { start = Math.Clamp(start, 1, 99); return (start, Math.Clamp(pause, start + 1, 100)); }

    private void ApplyDreamPermissionJson(JsonElement root, ChatPermissionState p)
    {
        p.dreamInternetSearch = ReadDreamBool(root, "dreamInternetSearch", p.dreamInternetSearch) && p.internetSearch;
        p.dreamVsCopilotTools = ReadDreamBool(root, "dreamVsCopilotTools", p.dreamVsCopilotTools) && p.vsCopilotTools;
        p.dreamFileDownloads = ReadDreamBool(root, "dreamFileDownloads", p.dreamFileDownloads) && p.fileDownloads;
        p.dreamFtpServer = ReadDreamBool(root, "dreamFtpServer", p.dreamFtpServer) && p.ftpServer;
        p.dreamSqlAdmin = ReadDreamBool(root, "dreamSqlAdmin", p.dreamSqlAdmin) && p.sqlAdmin;
        p.dreamTerminalCommands = ReadDreamBool(root, "dreamTerminalCommands", p.dreamTerminalCommands) && p.terminalCommands && p.terminalForeverApproved;
        p.dreamAgentAccess = ReadDreamBool(root, "dreamAgentAccess", p.dreamAgentAccess) && p.agentAccess;
        p.dreamFileUploads = ReadDreamBool(root, "dreamFileUploads", p.dreamFileUploads) && p.fileUploads;
        p.dreamImageUploads = ReadDreamBool(root, "dreamImageUploads", p.dreamImageUploads) && p.imageUploads;
        p.dreamPcAccess = ReadDreamBool(root, "dreamPcAccess", p.dreamPcAccess) && p.pcAccess;
    }
    private static bool ReadDreamBool(JsonElement root, string name, bool fallback) => root.TryGetProperty(name, out JsonElement value) ? value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed) && parsed) : fallback;

    private ChatPermissionState BuildDreamPermissions(string ownerKey)
    {
        ChatPermissionState p = GetChatPermissions(ownerKey);
        return new ChatPermissionState {
            internetSearch = p.internetSearch && p.dreamInternetSearch,
            vsCopilotTools = p.vsCopilotTools && p.dreamVsCopilotTools,
            fileDownloads = p.fileDownloads && p.dreamFileDownloads,
            ftpServer = p.ftpServer && p.dreamFtpServer,
            sqlAdmin = p.sqlAdmin && p.dreamSqlAdmin,
            terminalCommands = p.terminalCommands && p.terminalForeverApproved && p.dreamTerminalCommands,
            terminalForeverApproved = p.terminalForeverApproved && p.dreamTerminalCommands,
            agentAccess = p.agentAccess && p.dreamAgentAccess,
            fileUploads = p.fileUploads && p.dreamFileUploads,
            imageUploads = p.imageUploads && p.dreamImageUploads,
            pcAccess = p.pcAccess && p.dreamPcAccess
        };
    }
    private static string DreamPermissionForTool(string tool) => tool != null && tool.IndexOf("terminal", StringComparison.OrdinalIgnoreCase) >= 0 ? "terminalCommands+Dream+standing-approval" : tool != null && (tool.IndexOf("search", StringComparison.OrdinalIgnoreCase) >= 0 || tool.IndexOf("browser", StringComparison.OrdinalIgnoreCase) >= 0) ? "internetSearch+Dream" : "agentAccess+Dream";

    private void EnrichRuntimeRequestWithMemories(NetworkConnection connection, HttpRequest request, bool responsesApi)
    {
        try
        {
            string ownerKey = GetChatSessionOwnerKey(connection, request);
            ProcessExplicitChatMemoryCommands(ownerKey, "runtime-shared", request.Body);
            string hint = BuildChatMemorySystemHint(ownerKey);
            if (string.IsNullOrWhiteSpace(hint)) return;
            JsonNode root = JsonNode.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
            if (root is not JsonObject obj) return;
            if (responsesApi)
            {
                string existing = obj["instructions"]?.GetValue<string>() ?? "";
                obj["instructions"] = string.IsNullOrWhiteSpace(existing) ? hint : existing + "\n\n" + hint;
            }
            else
            {
                JsonArray messages = obj["messages"] as JsonArray ?? new JsonArray();
                messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = hint });
                obj["messages"] = messages;
            }
            request.Body = obj.ToJsonString();
        }
        catch (Exception ex) { LogMessage("[Memory] Runtime memory injection failed: " + ex.Message); }
    }

    private bool TryQueueMemoryConflict(string ownerKey, string proposedText, string sourceSessionId, out ChatMemoryRecord conflicting)
    {
        conflicting = null;
        foreach (ChatMemoryRecord existing in GetChatMemories(ownerKey))
        {
            if (!LikelyMemoryConflict(existing.text, proposedText)) continue;
            conflicting = existing;
            DreamState state = GetDreamStateByOwner(ownerKey);
            lock (_dreamLock)
            {
                bool duplicate = state.journal.SelectMany(x => x.candidates).Any(x => x.disposition == "review" && x.staleMemoryId == existing.id && NormalizeChatMemoryComparable(x.text) == NormalizeChatMemoryComparable(proposedText));
                if (!duplicate)
                {
                    DreamCandidate candidate = new() { text = proposedText, sourceSessionId = sourceSessionId, staleMemoryId = existing.id, staleMemoryText = existing.text, candidateType = "memory-conflict", confidence = 1, explicitFact = true, conflicting = true };
                    state.journal.Add(new DreamJournal { status = "review", summary = "Memory conflict needs review: keep both, overwrite the stale memory, or delete the stale memory.", completedUtc = DateTimeOffset.UtcNow.ToString("O"), candidates = new List<DreamCandidate> { candidate } });
                }
            }
            SaveDreamState();
            RecordObservabilityEvent("memory", "memory conflict", "review", proposedText, ownerKey, "/api/dream-journal", 0);
            return true;
        }
        return false;
    }

    private DreamState GetDreamStateByOwner(string ownerKey)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        lock (_dreamLock) { if (!_dreamStates.TryGetValue(ownerKey, out DreamState state)) _dreamStates[ownerKey] = state = new DreamState { ownerKey = ownerKey }; return state; }
    }

    private static bool LikelyMemoryConflict(string oldText, string newText)
    {
        string oldNorm = NormalizeChatMemoryComparable(oldText), newNorm = NormalizeChatMemoryComparable(newText);
        if (string.IsNullOrWhiteSpace(oldNorm) || string.IsNullOrWhiteSpace(newNorm) || oldNorm == newNorm) return false;
        HashSet<string> stop = new(StringComparer.OrdinalIgnoreCase) { "a","an","the","is","are","was","were","be","to","of","and","or","that","this","i","we","you","my","our","your" };
        HashSet<string> left = new(oldNorm.Split(' ').Where(x => x.Length > 1 && !stop.Contains(x)), StringComparer.OrdinalIgnoreCase);
        HashSet<string> right = new(newNorm.Split(' ').Where(x => x.Length > 1 && !stop.Contains(x)), StringComparer.OrdinalIgnoreCase);
        if (left.Count == 0 || right.Count == 0) return false;
        int overlap = left.Count(right.Contains); double similarity = overlap / (double)Math.Min(left.Count, right.Count);
        bool negationChanged = oldNorm.Contains(" not ") != newNorm.Contains(" not ") || oldNorm.Contains(" no ") != newNorm.Contains(" no ");
        bool valueChanged = left.Any(x => char.IsDigit(x[0])) || right.Any(x => char.IsDigit(x[0]));
        return similarity >= .6 && (negationChanged || valueChanged || similarity >= .8);
    }

    private string DreamStatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "JackLLM", "dream-state.json");
    private void LoadDreamState() { try { if (!File.Exists(DreamStatePath)) return; DreamState[] states = JsonSerializer.Deserialize<DreamState[]>(File.ReadAllText(DreamStatePath)); if (states != null) lock (_dreamLock) foreach (DreamState s in states) { s.cancellation = null; if (s.status == "running") s.status = "paused-foreground-work"; _dreamStates[s.ownerKey] = s; } } catch (Exception ex) { LogMessage("[Dream] Load: " + ex.Message); } }
    private void SaveDreamState() { try { DreamState[] states; lock (_dreamLock) states = _dreamStates.Values.ToArray(); Directory.CreateDirectory(Path.GetDirectoryName(DreamStatePath)!); File.WriteAllText(DreamStatePath, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { LogMessage("[Dream] Save: " + ex.Message); } }
    private void DisposeDreaming() { try { _dreamLifetime.Cancel(); lock (_dreamLock) foreach (DreamState s in _dreamStates.Values) s.cancellation?.Cancel(); SaveDreamState(); _dreamLifetime.Dispose(); } catch { } }
}
