using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LmVs;

namespace SocketJack.Net;

public sealed class AlignmentSnapshot
{
    public int Score { get; set; }
    public string Tier { get; set; } = "Neutral";
    public string Edge { get; set; } = "top";
    public string Theme { get; set; } = "neutral";
    public string LastReason { get; set; } = "Every Hero chooses a path.";
    public string[] DisabledFeatures { get; set; } = Array.Empty<string>();
    public string[] HighlightedFeatures { get; set; } = Array.Empty<string>();
    public bool DreamsEnabled { get; set; } = true;
    public bool PendingReview { get; set; }
    public bool Locked { get; set; }
    public string RecoveryGuidance { get; set; } = "The good path is easy to walk, though the first step may feel hardest.";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class AlignmentAssessmentSnapshot
{
    public string Category { get; set; } = "neutral";
    public string Severity { get; set; } = "none";
    public string Capability { get; set; } = "chat";
    public double Confidence { get; set; }
    public int Delta { get; set; }
    public bool BenignContext { get; set; }
    public bool Evasion { get; set; }
    public string Reason { get; set; } = "Every Hero chooses a path.";
}

public partial class LmVsProxy
{
    private const string AlignmentLockQuote = "Your Will Energy is low. Watch that.";
    private const string AlignmentLockMessage = "The Guildmaster is disappointed in the path you chose. This account has been sealed.";
    private readonly object _alignmentLock = new();
    private readonly Dictionary<string, AlignmentProfile> _alignmentProfiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AlignmentEvent> _alignmentEvents = new();
    private bool _alignmentLoaded;

    private sealed class AlignmentStore
    {
        public List<AlignmentProfile> Profiles { get; set; } = new();
        public List<AlignmentEvent> Events { get; set; } = new();
    }

    private sealed class AlignmentProfile
    {
        public string OwnerKey { get; set; } = "";
        public int Score { get; set; }
        public string LastReason { get; set; } = "Every Hero chooses a path.";
        public string LastPromptHash { get; set; } = "";
        public string PositiveCreditDayUtc { get; set; } = "";
        public int PositiveCreditToday { get; set; }
        public int CriticalFindingsThirtyDays { get; set; }
        public bool PendingReview { get; set; }
        public bool Locked { get; set; }
        public HashSet<string> DisabledFeatures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> RecoveryScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FeatureUse { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string UpdatedUtc { get; set; } = "";
    }

    private sealed class AlignmentEvent
    {
        public string Id { get; set; } = "alignment_" + Guid.NewGuid().ToString("N");
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PromptHash { get; set; } = "";
        public string Category { get; set; } = "neutral";
        public string Severity { get; set; } = "none";
        public string Capability { get; set; } = "chat";
        public double Confidence { get; set; }
        public int Delta { get; set; }
        public string Reason { get; set; } = "";
        public bool Evasion { get; set; }
        public bool Dismissed { get; set; }
        public string CreatedUtc { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    }

    private string AlignmentStatePath => Path.Combine(_chatSessionRoot, "alignment-state.json");

    private void RegisterAlignmentRoutes(HttpServer server)
    {
        EnsureAlignmentLoaded();
        server.Map("GET", "/api/alignment", (connection, request, _) =>
            JsonSerializer.Serialize(new { ok = true, alignment = GetAlignmentSnapshot(GetChatSessionOwnerKey(connection, request)) }, AlignmentJsonOptions));
        server.Map("GET", "/api/alignment/admin", (connection, request, _) => HandleAlignmentAdminGet(connection, request));
        server.Map("POST", "/api/alignment/admin", (connection, request, _) => HandleAlignmentAdminPost(connection, request));
    }

    private static readonly JsonSerializerOptions AlignmentJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private string HandleAlignmentAdminGet(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizeDatabaseAdministrator(connection, request, out _, out string error))
            return BuildJsonError(request, 403, "Forbidden", error);
        string ownerKey = NormalizeChatFilesystemOwnerKey(GetQueryParameter(request, "ownerKey"));
        lock (_alignmentLock)
        {
            EnsureAlignmentLoadedNoLock();
            object events = _alignmentEvents
                .Where(item => string.IsNullOrWhiteSpace(ownerKey) || string.Equals(item.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.CreatedUtc)
                .Take(100)
                .ToArray();
            object profiles = string.IsNullOrWhiteSpace(ownerKey)
                ? _alignmentProfiles.Keys.OrderBy(key => key).Select(key => new { ownerKey = key, alignment = GetAlignmentSnapshotNoLock(key) }).ToArray()
                : new[] { new { ownerKey, alignment = GetAlignmentSnapshotNoLock(ownerKey) } };
            return JsonSerializer.Serialize(new { ok = true, profiles, events }, AlignmentJsonOptions);
        }
    }

    private string HandleAlignmentAdminPost(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizeDatabaseAdministrator(connection, request, out _, out string error))
            return BuildJsonError(request, 403, "Forbidden", error);
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
            JsonElement root = document.RootElement;
            string action = root.TryGetProperty("action", out JsonElement actionElement) ? actionElement.GetString() ?? "" : "";
            string ownerKey = root.TryGetProperty("ownerKey", out JsonElement ownerElement) ? ownerElement.GetString() ?? "" : "";
            string eventId = root.TryGetProperty("eventId", out JsonElement eventElement) ? eventElement.GetString() ?? "" : "";
            string capability = root.TryGetProperty("capability", out JsonElement capabilityElement) ? capabilityElement.GetString() ?? "" : "";
            ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
            if (string.IsNullOrWhiteSpace(ownerKey)) throw new InvalidOperationException("ownerKey is required.");
            lock (_alignmentLock)
            {
                EnsureAlignmentLoadedNoLock();
                AlignmentProfile profile = GetAlignmentProfileNoLock(ownerKey);
                switch ((action ?? "").Trim().ToLowerInvariant())
                {
                    case "confirm-lock":
                        if (!profile.PendingReview) throw new InvalidOperationException("This owner does not have a pending alignment review.");
                        profile.Locked = true;
                        profile.LastReason = AlignmentLockQuote + " " + AlignmentLockMessage;
                        break;
                    case "unlock":
                        profile.Locked = false;
                        profile.PendingReview = false;
                        break;
                    case "restore-capability":
                        capability = NormalizeAlignmentCapability(capability);
                        profile.DisabledFeatures.Remove(capability);
                        profile.RecoveryScores.Remove(capability);
                        break;
                    case "dismiss-event":
                        AlignmentEvent alignmentEvent = _alignmentEvents.FirstOrDefault(item => string.Equals(item.Id, eventId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase));
                        if (alignmentEvent == null) throw new InvalidOperationException("Alignment event was not found.");
                        if (!alignmentEvent.Dismissed)
                        {
                            alignmentEvent.Dismissed = true;
                            profile.Score = Math.Clamp(profile.Score - alignmentEvent.Delta, -100, 100);
                            profile.LastReason = "An alignment finding was dismissed after review.";
                            RecalculateAlignmentReviewNoLock(profile);
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Use confirm-lock, unlock, restore-capability, or dismiss-event.");
                }
                profile.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
                SaveAlignmentStateNoLock();
                return JsonSerializer.Serialize(new { ok = true, alignment = GetAlignmentSnapshotNoLock(ownerKey) }, AlignmentJsonOptions);
            }
        }
        catch (Exception ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
    }

    public AlignmentSnapshot GetAlignmentSnapshot(string ownerKey)
    {
        ownerKey = NormalizeChatFilesystemOwnerKey(ownerKey);
        lock (_alignmentLock)
        {
            EnsureAlignmentLoadedNoLock();
            return GetAlignmentSnapshotNoLock(ownerKey);
        }
    }

    public AlignmentAssessmentSnapshot AssessAlignmentTextForDiagnostics(string text)
    {
        return AssessAlignmentText(text, Array.Empty<string>());
    }

    internal void ApplyAlignmentAssessmentForDiagnostics(string ownerKey, string prompt, AlignmentAssessmentSnapshot assessment)
    {
        ApplyAlignmentAssessment(NormalizeChatFilesystemOwnerKey(ownerKey), "diagnostics", AlignmentHash(prompt ?? ""), assessment);
    }

    private AlignmentSnapshot GetAlignmentSnapshotNoLock(string ownerKey)
    {
        AlignmentProfile profile = GetAlignmentProfileNoLock(ownerKey);
        string tier = AlignmentTier(profile.Score);
        bool negative = profile.Score < 0;
        string[] disabled = profile.DisabledFeatures.OrderBy(value => value).ToArray();
        return new AlignmentSnapshot
        {
            Score = profile.Score,
            Tier = tier,
            Edge = negative || profile.Locked ? "bottom" : "top",
            Theme = profile.Locked ? "locked" : negative ? "evil" : profile.Score > 0 ? "good" : "neutral",
            LastReason = profile.LastReason,
            DisabledFeatures = disabled,
            HighlightedFeatures = profile.Score > 0
                ? profile.FeatureUse.Where(pair => !profile.DisabledFeatures.Contains(pair.Key)).OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(3).Select(pair => pair.Key).ToArray()
                : Array.Empty<string>(),
            DreamsEnabled = profile.Score >= 0 && !profile.Locked,
            PendingReview = profile.PendingReview,
            Locked = profile.Locked,
            RecoveryGuidance = profile.Locked
                ? AlignmentLockQuote + " " + AlignmentLockMessage
                : negative
                    ? "Hard Mode: The path darkens by your choices. Only the Hero can choose another road."
                    : profile.Score > 0
                        ? "Care for the Hero first; then help Albion."
                        : "You can only help others after you help yourself.",
            UpdatedUtc = profile.UpdatedUtc
        };
    }

    private AlignmentProfile GetAlignmentProfileNoLock(string ownerKey)
    {
        ownerKey = string.IsNullOrWhiteSpace(ownerKey) ? "global" : ownerKey.Trim();
        if (!_alignmentProfiles.TryGetValue(ownerKey, out AlignmentProfile profile))
        {
            profile = new AlignmentProfile { OwnerKey = ownerKey, UpdatedUtc = DateTimeOffset.UtcNow.ToString("O") };
            _alignmentProfiles[ownerKey] = profile;
        }
        profile.DisabledFeatures ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        profile.RecoveryScores ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        profile.FeatureUse ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return profile;
    }

    private async Task<AlignmentSnapshot> EvaluateAlignmentForRequestAsync(string ownerKey, string requestBody, string sessionId, CancellationToken cancellationToken)
    {
        EnsureAlignmentLoaded();
        string prompt = FirstNonEmpty(ExtractChatUiLastUserPromptText(requestBody), ExtractLastUserMessage(requestBody) ?? "");
        if (string.IsNullOrWhiteSpace(prompt)) return GetAlignmentSnapshot(ownerKey);
        string promptHash = AlignmentHash(prompt);
        string[] recentCategories;
        lock (_alignmentLock)
        {
            recentCategories = _alignmentEvents.Where(item => string.Equals(item.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase) && !item.Dismissed)
                .OrderByDescending(item => item.CreatedUtc).Take(6).Select(item => item.Category).ToArray();
        }
        AlignmentAssessmentSnapshot signals = AssessAlignmentText(prompt, recentCategories);
        AlignmentAssessmentSnapshot local = await TryAssessAlignmentWithLocalModelAsync(prompt, signals, recentCategories, cancellationToken).ConfigureAwait(false);
        AlignmentAssessmentSnapshot assessment = signals.BenignContext
            ? signals
            : local ?? new AlignmentAssessmentSnapshot { Category = "neutral", Severity = "none", Capability = signals.Capability, Reason = "Every Hero chooses a path." };
        if (signals.Category == "constructive" && local?.Category == "constructive")
            assessment.Delta = signals.Delta;
        ApplyAlignmentAssessment(ownerKey, sessionId, promptHash, assessment);
        return GetAlignmentSnapshot(ownerKey);
    }

    private async Task<AlignmentAssessmentSnapshot> TryAssessAlignmentWithLocalModelAsync(string prompt, AlignmentAssessmentSnapshot seed, IReadOnlyList<string> recentCategories, CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            string instruction = "Classify observable intent conservatively. Never diagnose personality or mental health. Help-seeking about distress, self-harm, trauma, fiction, quotations, role-play, security research, prevention, and remediation are benign unless the user clearly intends harm. Repeated malicious categories may be context for disguised or split intent, but are not proof by themselves. Return JSON only with category (constructive|neutral|self-sabotage|feature-abuse|malicious|critical), severity (none|low|medium|high|critical), capability (chat|dreams|terminal|filesystem|uploads|downloads|internet|media|pc-access|agent), confidence 0..1, benignContext boolean, evasion boolean, and a short reason without chain-of-thought.";
            string body = JsonSerializer.Serialize(new
            {
                model = ChatModel,
                messages = new[] { new { role = "system", content = instruction }, new { role = "user", content = "Recent classification categories: " + string.Join(", ", recentCategories ?? Array.Empty<string>()) + "\nCurrent prompt:\n" + prompt } },
                temperature = 0,
                max_tokens = 220,
                stream = false
            });
            ChatUiCompletion completion = await ExecuteChatUiCompletionWithProxyToolsAsync(body, "alignment-classifier", "alignment-" + Guid.NewGuid().ToString("N"), timeout.Token).ConfigureAwait(false);
            string json = ExtractAlignmentJson(completion?.Content);
            if (string.IsNullOrWhiteSpace(json)) return null;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            string category = root.TryGetProperty("category", out JsonElement categoryElement) ? categoryElement.GetString() ?? "neutral" : "neutral";
            double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement) && confidenceElement.TryGetDouble(out double parsedConfidence) ? Math.Clamp(parsedConfidence, 0, 1) : 0;
            bool benign = root.TryGetProperty("benignContext", out JsonElement benignElement) && benignElement.ValueKind == JsonValueKind.True;
            bool evasion = root.TryGetProperty("evasion", out JsonElement evasionElement) && evasionElement.ValueKind == JsonValueKind.True;
            string severity = root.TryGetProperty("severity", out JsonElement severityElement) ? severityElement.GetString() ?? "none" : "none";
            string capability = root.TryGetProperty("capability", out JsonElement capabilityElement) ? capabilityElement.GetString() ?? seed.Capability : seed.Capability;
            string reason = root.TryGetProperty("reason", out JsonElement reasonElement) ? reasonElement.GetString() ?? "Every Hero chooses a path." : "Every Hero chooses a path.";
            if (benign || confidence < 0.90) category = "neutral";
            int delta = AlignmentDelta(category, confidence);
            return new AlignmentAssessmentSnapshot { Category = category, Severity = severity, Capability = NormalizeAlignmentCapability(capability), Confidence = confidence, Delta = delta, BenignContext = benign, Evasion = evasion, Reason = reason };
        }
        catch { return null; }
    }

    private void ApplyAlignmentAssessment(string ownerKey, string sessionId, string promptHash, AlignmentAssessmentSnapshot assessment)
    {
        if (assessment == null || string.IsNullOrWhiteSpace(promptHash)) return;
        lock (_alignmentLock)
        {
            EnsureAlignmentLoadedNoLock();
            AlignmentProfile profile = GetAlignmentProfileNoLock(ownerKey);
            if (string.Equals(profile.LastPromptHash, promptHash, StringComparison.OrdinalIgnoreCase)) return;
            int delta = assessment.Delta;
            string today = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd");
            if (!string.Equals(profile.PositiveCreditDayUtc, today, StringComparison.Ordinal))
            {
                profile.PositiveCreditDayUtc = today;
                profile.PositiveCreditToday = 0;
            }
            if (delta > 0)
            {
                delta = Math.Min(delta, Math.Max(0, 6 - profile.PositiveCreditToday));
                profile.PositiveCreditToday += delta;
            }
            profile.LastPromptHash = promptHash;
            if (delta == 0 && assessment.Category == "neutral") return;
            profile.Score = Math.Clamp(profile.Score + delta, -100, 100);
            profile.LastReason = string.IsNullOrWhiteSpace(assessment.Reason) ? AlignmentReasonForCategory(assessment.Category) : assessment.Reason;
            profile.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            string capability = NormalizeAlignmentCapability(assessment.Capability);
            if (delta < 0 && profile.Score < 0) _ = Task.Run(() => CancelDreamForAlignment(ownerKey));
            if ((assessment.Category == "feature-abuse" || assessment.Category == "malicious" || assessment.Category == "critical") && assessment.Confidence >= 0.90 && capability != "chat" && capability != "dreams")
            {
                profile.DisabledFeatures.Add(capability);
                profile.RecoveryScores[capability] = Math.Min(100, Math.Max(0, profile.Score + 20));
            }
            if (assessment.Category == "critical" && assessment.Confidence >= 0.95)
            {
                profile.CriticalFindingsThirtyDays = CountRecentCriticalNoLock(ownerKey) + 1;
            }
            if (profile.CriticalFindingsThirtyDays >= 2 || profile.Score <= -90)
            {
                profile.PendingReview = true;
                foreach (string feature in AlignmentRestrictableFeatures) profile.DisabledFeatures.Add(feature);
            }
            RestoreRecoveredFeaturesNoLock(profile);
            _alignmentEvents.Add(new AlignmentEvent
            {
                OwnerKey = ownerKey,
                SessionId = sessionId ?? "",
                PromptHash = promptHash,
                Category = assessment.Category,
                Severity = assessment.Severity,
                Capability = capability,
                Confidence = assessment.Confidence,
                Delta = delta,
                Reason = profile.LastReason,
                Evasion = assessment.Evasion
            });
            if (_alignmentEvents.Count > 5000) _alignmentEvents.RemoveRange(0, _alignmentEvents.Count - 5000);
            SaveAlignmentStateNoLock();
        }
    }

    private static readonly string[] AlignmentRestrictableFeatures = { "terminal", "filesystem", "uploads", "downloads", "internet", "media", "pc-access", "agent" };

    private void RestoreRecoveredFeaturesNoLock(AlignmentProfile profile)
    {
        if (profile.Score < 0 || profile.Locked) return;
        foreach (string capability in profile.RecoveryScores.Where(pair => profile.Score >= pair.Value).Select(pair => pair.Key).ToArray())
        {
            profile.DisabledFeatures.Remove(capability);
            profile.RecoveryScores.Remove(capability);
        }
    }

    private int CountRecentCriticalNoLock(string ownerKey)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return _alignmentEvents.Count(item => !item.Dismissed && string.Equals(item.OwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase) && item.Category == "critical" && DateTimeOffset.TryParse(item.CreatedUtc, out DateTimeOffset created) && created >= cutoff);
    }

    private void RecalculateAlignmentReviewNoLock(AlignmentProfile profile)
    {
        profile.CriticalFindingsThirtyDays = CountRecentCriticalNoLock(profile.OwnerKey);
        profile.PendingReview = profile.CriticalFindingsThirtyDays >= 2 || profile.Score <= -90;
        if (!profile.PendingReview) profile.Locked = false;
    }

    private void RecordAlignmentFeatureUse(string ownerKey, string capability)
    {
        capability = NormalizeAlignmentCapability(capability);
        if (capability == "chat") return;
        lock (_alignmentLock)
        {
            AlignmentProfile profile = GetAlignmentProfileNoLock(ownerKey);
            profile.FeatureUse[capability] = profile.FeatureUse.TryGetValue(capability, out int uses) ? uses + 1 : 1;
            profile.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            SaveAlignmentStateNoLock();
        }
    }

    private ChatPermissionState ApplyAlignmentRestrictions(string ownerKey, ChatPermissionState source)
    {
        AlignmentSnapshot alignment = GetAlignmentSnapshot(ownerKey);
        if (alignment.Score >= 0 && !alignment.Locked && alignment.DisabledFeatures.Length == 0) return source;
        ChatPermissionState permissions = CopyChatPermissions(source);
        HashSet<string> disabled = new(alignment.DisabledFeatures, StringComparer.OrdinalIgnoreCase);
        if (alignment.Locked)
        {
            permissions.banUntilEnabled = true;
            permissions.bannedUntilUtc = "";
        }
        if (disabled.Contains("internet")) permissions.internetSearch = false;
        if (disabled.Contains("filesystem")) permissions.vsCopilotTools = false;
        if (disabled.Contains("downloads")) { permissions.fileDownloads = false; permissions.ftpServer = false; }
        if (disabled.Contains("uploads")) { permissions.fileUploads = false; permissions.imageUploads = false; }
        if (disabled.Contains("terminal")) { permissions.terminalCommands = false; permissions.terminalForeverApproved = false; }
        if (disabled.Contains("pc-access")) permissions.pcAccess = false;
        if (disabled.Contains("agent")) permissions.agentAccess = false;
        return permissions;
    }

    private bool IsAlignmentDreamAllowed(string ownerKey)
    {
        AlignmentSnapshot snapshot = GetAlignmentSnapshot(ownerKey);
        return snapshot.DreamsEnabled && !snapshot.Locked;
    }

    private bool IsAlignmentCapabilityAllowed(string ownerKey, string capability)
    {
        AlignmentSnapshot snapshot = GetAlignmentSnapshot(ownerKey);
        return !snapshot.Locked && !snapshot.DisabledFeatures.Contains(NormalizeAlignmentCapability(capability), StringComparer.OrdinalIgnoreCase);
    }

    private string BuildAlignmentRestrictionMessage(string ownerKey)
    {
        AlignmentSnapshot snapshot = GetAlignmentSnapshot(ownerKey);
        return snapshot.Locked ? AlignmentLockQuote + " " + AlignmentLockMessage : "";
    }

    private object HandleAlignmentProtectedRequest(NetworkConnection connection, HttpRequest request, Func<object> action)
    {
        AlignmentSnapshot snapshot = GetAlignmentSnapshot(GetChatSessionOwnerKey(connection, request));
        if (!snapshot.Locked) return action();
        return BuildJsonError(request, 423, "Locked", "alignment_restricted: " + AlignmentLockQuote + " " + AlignmentLockMessage);
    }

    private void CancelDreamForAlignment(string ownerKey)
    {
        try
        {
            DreamState state = GetDreamStateByOwner(ownerKey);
            lock (_dreamLock)
            {
                state.manualRequested = false;
                state.userPaused = true;
                state.status = "paused-alignment";
                state.phase = "alignment";
                state.limitingResource = "alignment";
                state.updatedUtc = DateTimeOffset.UtcNow.ToString("O");
                state.cancellation?.Cancel();
            }
            SaveDreamState();
        }
        catch { }
    }

    private AlignmentAssessmentSnapshot AssessAlignmentText(string text, IReadOnlyList<string> recentCategories)
    {
        string normalized = NormalizeAlignmentText(text);
        string capability = AlignmentCapabilityForText(normalized);
        if (string.IsNullOrWhiteSpace(normalized)) return new AlignmentAssessmentSnapshot();
        bool helpSeeking = ContainsAny(normalized, "help me", "i need help", "keep me safe", "stop me", "prevent", "avoid", "recover", "healing", "therapy", "support", "i do not want", "i don't want");
        bool contextual = ContainsAny(normalized, "fiction", "story", "novel", "roleplay", "role play", "quote", "research", "security test", "authorized", "defensive", "remediation", "awareness", "educational");
        bool constructiveSelf = ContainsAny(normalized, "self care", "self-care", "help myself", "love myself", "healthy boundary", "get better", "improve my life", "take care of myself", "stay safe", "build confidence", "ask for help");
        bool constructiveOther = ContainsAny(normalized, "help someone", "help others", "protect someone", "support my", "care for", "keep them safe");
        bool selfSabotage = ContainsAny(normalized, "ruin my life", "destroy myself", "sabotage myself", "give up on myself", "make my life worse", "hurt myself", "starve myself", "punish myself");
        bool evasion = ContainsAny(normalized, "evade detection", "bypass safety", "avoid being flagged", "word it differently", "hide my intent", "don't detect", "do not detect", "jailbreak");
        bool critical = ContainsAny(normalized, "mass casualty", "crimes against humanity", "human sacrifice", "kill everyone", "murder people", "ransomware attack", "terrorist attack") && !helpSeeking && !contextual;
        bool malicious = ContainsAny(normalized, "how to kill", "help me kill", "hurt someone", "blackmail", "steal passwords", "deploy ransomware", "destroy their files", "track them without", "coerce", "poison someone") && !helpSeeking && !contextual;
        bool featureAbuse = (evasion || ContainsAny(normalized, "delete all files", "wipe the drive", "disable security", "exfiltrate", "steal credentials", "take over the pc")) && !helpSeeking && !contextual;
        if (helpSeeking || contextual)
            return new AlignmentAssessmentSnapshot { Category = constructiveSelf ? "constructive" : "neutral", Severity = "none", Capability = capability, Confidence = 0.98, Delta = constructiveSelf ? 2 : 0, BenignContext = true, Reason = constructiveSelf ? "The first good choice may feel the hardest. Care for the Hero first." : "Seeking understanding is not an evil deed." };
        if (critical)
            return new AlignmentAssessmentSnapshot { Category = "critical", Severity = "critical", Capability = capability, Confidence = 0.97, Delta = -40, Evasion = evasion, Reason = "Hard Mode: deliberate catastrophic harm narrows the path." };
        if (featureAbuse)
            return new AlignmentAssessmentSnapshot { Category = "feature-abuse", Severity = "high", Capability = capability, Confidence = 0.94, Delta = -10, Evasion = evasion, Reason = "A Guild privilege was turned against its purpose." };
        if (malicious)
            return new AlignmentAssessmentSnapshot { Category = "malicious", Severity = "high", Capability = capability, Confidence = 0.94, Delta = -20, Evasion = evasion, Reason = "The path darkens when harm becomes the quest." };
        if (selfSabotage)
            return new AlignmentAssessmentSnapshot { Category = "self-sabotage", Severity = "low", Capability = "chat", Confidence = 0.92, Delta = -2, Reason = "The Hero has chosen against their own well-being." };
        if (constructiveSelf)
            return new AlignmentAssessmentSnapshot { Category = "constructive", Severity = "none", Capability = capability, Confidence = 0.96, Delta = 2, Reason = "Care for the Hero first; then help Albion." };
        if (constructiveOther)
            return new AlignmentAssessmentSnapshot { Category = "constructive", Severity = "none", Capability = capability, Confidence = 0.92, Delta = 1, Reason = "A good deed begins with a Hero who can stand." };
        bool suspiciousFraming = ContainsAny(normalized, "hypothetically", "asking for a friend", "purely theoretical", "no moral lecture") && recentCategories.Any(value => value is "feature-abuse" or "malicious" or "critical");
        return suspiciousFraming
            ? new AlignmentAssessmentSnapshot { Category = "review", Severity = "medium", Capability = capability, Confidence = 0.5, Evasion = true, Reason = "Intent requires a conservative second look." }
            : new AlignmentAssessmentSnapshot { Category = "neutral", Severity = "none", Capability = capability, Confidence = 0.8, Delta = 0, Reason = "Every Hero chooses a path." };
    }

    private static int AlignmentDelta(string category, double confidence)
    {
        if (confidence < 0.90) return 0;
        return (category ?? "").Trim().ToLowerInvariant() switch
        {
            "constructive" => 1,
            "self-sabotage" => -2,
            "feature-abuse" => -10,
            "malicious" => -20,
            "critical" when confidence >= 0.95 => -40,
            _ => 0
        };
    }

    private static string AlignmentReasonForCategory(string category) => (category ?? "").Trim().ToLowerInvariant() switch
    {
        "constructive" => "Care for the Hero first; then help Albion.",
        "self-sabotage" => "The Hero has chosen against their own well-being.",
        "feature-abuse" => "A Guild privilege was turned against its purpose.",
        "malicious" => "The path darkens when harm becomes the quest.",
        "critical" => "Hard Mode: deliberate catastrophic harm narrows the path.",
        _ => "Every Hero chooses a path."
    };

    private static string AlignmentTier(int score) => score switch
    {
        >= 50 => "Radiant",
        > 0 => "Good",
        0 => "Neutral",
        >= -19 => "Fallen",
        >= -49 => "Corrupt",
        >= -79 => "Hard",
        _ => "Critical"
    };

    private static string AlignmentCapabilityForRequest(string requestBody)
    {
        string text = requestBody ?? "";
        if (text.IndexOf("terminal", StringComparison.OrdinalIgnoreCase) >= 0) return "terminal";
        if (text.IndexOf("browser", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("internet", StringComparison.OrdinalIgnoreCase) >= 0) return "internet";
        if (text.IndexOf("image", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("media", StringComparison.OrdinalIgnoreCase) >= 0) return "media";
        if (text.IndexOf("agent", StringComparison.OrdinalIgnoreCase) >= 0) return "agent";
        return "chat";
    }

    private static string AlignmentCapabilityForText(string text)
    {
        if (ContainsAny(text, "terminal", "command", "powershell", "shell", "cmd.exe")) return "terminal";
        if (ContainsAny(text, "file", "folder", "drive", "directory")) return "filesystem";
        if (ContainsAny(text, "upload", "attach")) return "uploads";
        if (ContainsAny(text, "download", "ftp")) return "downloads";
        if (ContainsAny(text, "internet", "browser", "website", "web search")) return "internet";
        if (ContainsAny(text, "image", "video", "media", "picture")) return "media";
        if (ContainsAny(text, "pc access", "remote desktop", "take over the pc")) return "pc-access";
        if (ContainsAny(text, "agent", "tool")) return "agent";
        return "chat";
    }

    private static string NormalizeAlignmentCapability(string capability)
    {
        string value = (capability ?? "chat").Trim().ToLowerInvariant().Replace('_', '-').Replace(' ', '-');
        return AlignmentRestrictableFeatures.Contains(value, StringComparer.OrdinalIgnoreCase) || value is "chat" or "dreams" ? value : "chat";
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string NormalizeAlignmentText(string value)
    {
        string normalized = Regex.Replace((value ?? "").ToLowerInvariant(), @"\s+", " ").Trim();
        return normalized.Length > 12000 ? normalized.Substring(0, 12000) : normalized;
    }

    private static string AlignmentHash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(NormalizeAlignmentText(value)));
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private static string ExtractAlignmentJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        int start = value.IndexOf('{');
        int end = value.LastIndexOf('}');
        return start >= 0 && end > start ? value.Substring(start, end - start + 1) : "";
    }

    private void EnsureAlignmentLoaded()
    {
        lock (_alignmentLock) EnsureAlignmentLoadedNoLock();
    }

    private void EnsureAlignmentLoadedNoLock()
    {
        if (_alignmentLoaded) return;
        _alignmentLoaded = true;
        try
        {
            if (!File.Exists(AlignmentStatePath)) return;
            AlignmentStore store = JsonSerializer.Deserialize<AlignmentStore>(File.ReadAllText(AlignmentStatePath), AlignmentJsonOptions) ?? new AlignmentStore();
            foreach (AlignmentProfile profile in store.Profiles ?? new List<AlignmentProfile>())
            {
                if (string.IsNullOrWhiteSpace(profile.OwnerKey)) continue;
                profile.DisabledFeatures = new HashSet<string>(profile.DisabledFeatures ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);
                profile.RecoveryScores = new Dictionary<string, int>(profile.RecoveryScores ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
                profile.FeatureUse = new Dictionary<string, int>(profile.FeatureUse ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);
                _alignmentProfiles[profile.OwnerKey] = profile;
            }
            _alignmentEvents.AddRange(store.Events ?? new List<AlignmentEvent>());
        }
        catch (Exception ex) { LogMessage("[Alignment] State load failed: " + ex.Message); }
    }

    private void SaveAlignmentStateNoLock()
    {
        try
        {
            Directory.CreateDirectory(_chatSessionRoot);
            AlignmentStore store = new() { Profiles = _alignmentProfiles.Values.OrderBy(item => item.OwnerKey).ToList(), Events = _alignmentEvents.ToList() };
            File.WriteAllText(AlignmentStatePath, JsonSerializer.Serialize(store, AlignmentJsonOptions));
        }
        catch (Exception ex) { LogMessage("[Alignment] State save failed: " + ex.Message); }
    }
}
