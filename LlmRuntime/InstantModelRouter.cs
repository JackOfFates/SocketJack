using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LlmRuntime;

public enum RouterReasoningLevel { Minimal, Low, Medium, High, Auto }

public sealed record ModelRouteRequest(
    string Prompt,
    string Service = "chat",
    RouterReasoningLevel Reasoning = RouterReasoningLevel.Auto,
    bool RequiresVision = false,
    bool RequiresTools = false,
    int RequiredContextTokens = 0,
    long? AvailableVramBytes = null);

public sealed record ModelRouteCandidate(
    string Model,
    double Score,
    bool Compatible,
    bool Loaded,
    bool Healthy,
    bool FitsVram,
    bool RequiresLoading,
    long EstimatedWorkingSetBytes,
    int QueueDepth,
    double TokensPerSecond,
    IReadOnlyList<string> Factors,
    string RejectionReason = "");

public sealed record ModelRouteDecision(
    string SelectedModel,
    RouterReasoningLevel EffectiveReasoning,
    string Classification,
    string ReasonCode,
    bool RequiresLoading,
    long EstimatedWorkingSetBytes,
    long? AvailableVramBytes,
    double ElapsedMilliseconds,
    string PromptFingerprint,
    IReadOnlyList<ModelRouteCandidate> Candidates)
{
    public bool Success => !string.IsNullOrWhiteSpace(SelectedModel);
}

/// <summary>Fast, deterministic, local-only model routing. Hard compatibility and memory gates always win.</summary>
public sealed class InstantModelRouter
{
    public const double MaximumWorkingSetToAvailableVramRatio = 1.25d;
    private static readonly TimeSpan ClassificationCacheDuration = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Expires, PromptFeatures Features)> _classificationCache = new();

    public ModelRouteDecision Route(IEnumerable<LlmModelInfo> models, ModelRouteRequest request)
    {
        var stopwatch = Stopwatch.StartNew();
        string prompt = request.Prompt ?? "";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt))).ToLowerInvariant();
        PromptFeatures features = Classify(prompt, fingerprint);
        RouterReasoningLevel reasoning = request.Reasoning == RouterReasoningLevel.Auto ? features.SuggestedReasoning : request.Reasoning;
        var candidates = models
            .Where(model => LlmModelRegistry.IsChatLoadableModel(model))
            .Select(model => Score(model, request, features, reasoning))
            .OrderByDescending(candidate => candidate.Compatible)
            .ThenByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Loaded)
            .ThenByDescending(candidate => candidate.Healthy)
            .ThenBy(candidate => candidate.QueueDepth)
            .ThenByDescending(candidate => candidate.TokensPerSecond)
            .ThenBy(candidate => candidate.EstimatedWorkingSetBytes)
            .ThenBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ModelRouteCandidate? selected = candidates.FirstOrDefault(candidate => candidate.Compatible);
        stopwatch.Stop();
        return new ModelRouteDecision(
            selected?.Model ?? "", reasoning, features.Classification,
            selected == null ? "no_compatible_model" : selected.Loaded ? "best_loaded_match" : "best_loadable_match",
            selected?.RequiresLoading ?? false, selected?.EstimatedWorkingSetBytes ?? 0,
            request.AvailableVramBytes, stopwatch.Elapsed.TotalMilliseconds, fingerprint, candidates);
    }

    private ModelRouteCandidate Score(LlmModelInfo model, ModelRouteRequest request, PromptFeatures features, RouterReasoningLevel reasoning)
    {
        LoadedModelInstance? instance = model.LoadedInstances
            .OrderBy(instance => instance.QueuedJobs + instance.ActiveJobs)
            .FirstOrDefault();
        bool loaded = instance != null;
        bool healthy = instance == null || string.IsNullOrWhiteSpace(instance.Health) || instance.Health.Equals("healthy", StringComparison.OrdinalIgnoreCase);
        int queue = instance?.QueuedJobs + instance?.ActiveJobs ?? 0;
        double speed = instance?.TokensPerSecond ?? 0;
        bool vision = HasAny(model, "vision", "image-text-to-text", "vlm", "multimodal");
        bool tools = HasAny(model, "tool", "function", "agent", "instruct");
        bool contextFits = request.RequiredContextTokens <= 0 || !model.MaxContextLength.HasValue || model.MaxContextLength.Value >= request.RequiredContextTokens;
        long workingSet = EstimateWorkingSet(model, request.RequiredContextTokens);
        bool telemetryKnown = request.AvailableVramBytes is > 0;
        bool fits = loaded || telemetryKnown && workingSet <= request.AvailableVramBytes!.Value * MaximumWorkingSetToAvailableVramRatio;
        string rejection = !healthy ? "unhealthy" : request.RequiresVision && !vision ? "vision_required" : request.RequiresTools && !tools ? "tools_required" : !contextFits ? "context_too_small" : !fits ? telemetryKnown ? "vram_limit" : "vram_unknown" : "";
        bool compatible = rejection.Length == 0;

        double strength = EstimateStrength(model);
        double desiredStrength = reasoning switch { RouterReasoningLevel.Minimal => 1, RouterReasoningLevel.Low => 2, RouterReasoningLevel.Medium => 3, RouterReasoningLevel.High => 4, _ => 3 };
        double score = 100 - Math.Abs(strength - desiredStrength) * 14;
        if (loaded) score += 22;
        if (features.IsCoding && HasAny(model, "code", "coder")) score += 18;
        if (features.IsMath && HasAny(model, "math", "reason", "r1")) score += 16;
        if (features.IsCreative && HasAny(model, "creative", "instruct")) score += 8;
        if (request.RequiresVision && vision) score += 30;
        if (request.RequiresTools && tools) score += 20;
        score += Math.Min(12, speed / 10d) - queue * 8;
        if (!compatible) score -= 1000;

        var factors = new List<string> { loaded ? "loaded" : "load required", $"strength {strength:0.0}/{desiredStrength:0.0}", features.Classification };
        if (features.IsCoding) factors.Add("coding prompt");
        if (features.IsMath) factors.Add("reasoning prompt");
        if (speed > 0) factors.Add($"{speed:0.#} tok/s");
        if (queue > 0) factors.Add($"queue {queue}");
        return new ModelRouteCandidate(model.Key, Math.Round(score, 3), compatible, loaded, healthy, fits, !loaded, workingSet, queue, speed, factors, rejection);
    }

    internal static long EstimateWorkingSet(LlmModelInfo model, int requiredContextTokens)
    {
        long weights = Math.Max(1, model.SizeBytes);
        int context = Math.Clamp(requiredContextTokens <= 0 ? 8192 : requiredContextTokens, 1024, 131072);
        long contextOverhead = Math.Max(256L * 1024 * 1024, (long)context * 64 * 1024);
        return SaturatingAdd((long)Math.Ceiling(weights * 1.08d), contextOverhead);
    }

    private PromptFeatures Classify(string prompt, string fingerprint)
    {
        string cacheKey = fingerprint + ":" + prompt.Length;
        if (_classificationCache.TryGetValue(cacheKey, out var cached) && cached.Expires > DateTimeOffset.UtcNow) return cached.Features;
        string text = prompt.ToLowerInvariant();
        bool coding = text.Contains("c#", StringComparison.Ordinal) || text.Contains(".net", StringComparison.Ordinal) ||
            Regex.IsMatch(text, @"\b(code|class|function|compile|debug|bug|repository|refactor|api|sql|javascript|typescript|python|java|parser|program|script)\b");
        bool math = Regex.IsMatch(text, @"\b(prove|calculate|equation|algorithm|reason|analy[sz]e|compare|trade-?off|architecture)\b");
        bool creative = !coding && Regex.IsMatch(text, @"\b(write|story|poem|brainstorm|creative|marketing|slogan|design)\b");
        int complexity = (prompt.Length > 1200 ? 2 : prompt.Length > 350 ? 1 : 0) + (coding ? 1 : 0) + (math ? 1 : 0);
        RouterReasoningLevel suggested = complexity >= 3 ? RouterReasoningLevel.High : complexity == 2 ? RouterReasoningLevel.Medium : complexity == 1 ? RouterReasoningLevel.Low : RouterReasoningLevel.Minimal;
        string classification = coding ? "coding" : math ? "reasoning" : creative ? "creative" : "general";
        var features = new PromptFeatures(classification, coding, math, creative, suggested);
        _classificationCache[cacheKey] = (DateTimeOffset.UtcNow + ClassificationCacheDuration, features);
        return features;
    }

    private static double EstimateStrength(LlmModelInfo model)
    {
        string value = (model.ParamsString ?? "") + " " + model.DisplayName + " " + model.Key;
        Match match = Regex.Match(value, @"(?<n>\d+(?:\.\d+)?)\s*[bB]\b");
        if (match.Success && double.TryParse(match.Groups["n"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double billions))
            return billions >= 30 ? 5 : billions >= 13 ? 4 : billions >= 7 ? 3 : billions >= 3 ? 2 : 1;
        long size = model.SizeBytes;
        return size >= 18L << 30 ? 5 : size >= 8L << 30 ? 4 : size >= 4L << 30 ? 3 : size >= 2L << 30 ? 2 : 1;
    }

    private static bool HasAny(LlmModelInfo model, params string[] values)
    {
        string haystack = string.Join(" ", model.Key, model.DisplayName, model.Type, model.Architecture, string.Join(" ", model.Tags)).ToLowerInvariant();
        return values.Any(value => haystack.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static long SaturatingAdd(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;
    private sealed record PromptFeatures(string Classification, bool IsCoding, bool IsMath, bool IsCreative, RouterReasoningLevel SuggestedReasoning);
}

public sealed class ModelRoutingFeedbackStore
{
    private readonly string _path;
    private readonly object _gate = new();
    public ModelRoutingFeedbackStore(string dataRoot) => _path = Path.Combine(dataRoot, "model-routing-feedback.jsonl");

    public void Append(object record)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
        }
    }

    public int CountValidated()
    {
        lock (_gate)
        {
            if (!File.Exists(_path)) return 0;
            try { return File.ReadLines(_path).Count(line => !string.IsNullOrWhiteSpace(line)); }
            catch { return 0; }
        }
    }
}
