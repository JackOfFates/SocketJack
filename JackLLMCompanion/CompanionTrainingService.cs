using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JackLLMCompanion;

public sealed class CompanionTrainingService : IDisposable
{
    private readonly CompanionRepository _repository;
    private readonly DesktopAutomationService _desktop;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeRunCancellation;
    private string _activeRunId = "";

    public CompanionTrainingService(CompanionRepository repository, DesktopAutomationService desktop)
    {
        _repository = repository;
        _desktop = desktop;
    }

    public CompanionTrainingRun StartTraining(string sessionId, string modelEndpoint, string model)
    {
        CompanionTrainingSettings settings = _repository.GetTrainingState().Settings;
        CompanionTrainingRun run = _repository.CreateTrainingRun(sessionId, settings.CaptureProfile, modelEndpoint);

        if (!settings.LearningEnabled)
        {
            return _repository.UpdateTrainingRun(run.Id, "disabled", 100, "low", "Self-training is disabled in Companion training settings.", completed: true);
        }

        var cts = new CancellationTokenSource();
        lock (_sync)
        {
            _activeRunCancellation?.Cancel();
            _activeRunCancellation?.Dispose();
            _activeRunCancellation = cts;
            _activeRunId = run.Id;
        }

        _ = Task.Run(() => ProcessRunAsync(run, modelEndpoint, model, cts.Token), CancellationToken.None);
        return run;
    }

    public CompanionTrainingRun CancelActiveTraining(string reason)
    {
        string runId;
        lock (_sync)
        {
            runId = _activeRunId;
            _activeRunCancellation?.Cancel();
        }

        if (string.IsNullOrWhiteSpace(runId))
            return new CompanionTrainingRun { Status = "idle", Summary = "No active training run." };

        return _repository.UpdateTrainingRun(runId, "cancelled", 100, "medium", string.IsNullOrWhiteSpace(reason) ? "Training cancelled." : reason, completed: true);
    }

    public string CaptureSessionKeyframe(string sessionId, string reason, CompanionEnvironmentSnapshot? snapshot = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return "";

        CompanionTrainingSettings settings = _repository.GetTrainingState().Settings;
        if (!settings.LearningEnabled)
            return "";

        string sessionRoot = Path.Combine(_repository.TrainingRoot, "ReplayFrames", BuildSafeSegment(sessionId));
        Directory.CreateDirectory(sessionRoot);

        var existing = Directory.EnumerateFiles(sessionRoot, "*.jpg", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .ToList();
        if (existing.Count >= settings.ReplayMaxFrames)
            return "";
        if (existing.Sum(file => file.Length) >= settings.ReplayMaxBytes)
            return "";

        try
        {
            DesktopScreenCapture capture = _desktop.CaptureScreen(new DesktopCaptureOptions { Format = "jpg", Quality = 56 });
            string safeReason = BuildSafeSegment(reason);
            string path = Path.Combine(sessionRoot, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "_" + safeReason + ".jpg");
            SaveDownscaledJpeg(capture.Bytes, path, 1280, 56);
            string metadataPath = Path.ChangeExtension(path, ".json");
            File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
            {
                sessionId,
                reason,
                capturedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                application = snapshot?.Application ?? "",
                window = snapshot?.Window ?? "",
                url = snapshot?.Url ?? "",
                person = snapshot?.Person ?? "",
                width = capture.Width,
                height = capture.Height
            }));
            return path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "";
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _activeRunCancellation?.Cancel();
            _activeRunCancellation?.Dispose();
            _activeRunCancellation = null;
            _activeRunId = "";
        }
    }

    private async Task ProcessRunAsync(CompanionTrainingRun run, string modelEndpoint, string model, CancellationToken cancellationToken)
    {
        try
        {
            _repository.UpdateTrainingRun(run.Id, "capturing_evidence", 15, "low", "Building evidence pack from recorded session and minimized replay.");
            CompanionTrainingSessionSnapshot snapshot = _repository.GetTrainingSnapshot(run.SourceSessionId);
            var evidenceRefs = new List<string>();
            var flags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (CompanionEvent ev in snapshot.Events.Take(500))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string eventFlags = AnalyzeSensitivity(ev);
                AddFlags(flags, eventFlags);
                CompanionTrainingEvidence evidence = _repository.AddTrainingEvidence(new CompanionTrainingEvidence
                {
                    RunId = run.Id,
                    SessionId = run.SourceSessionId,
                    Sequence = ev.Sequence,
                    EventType = ev.EventType,
                    Detail = RedactSensitive(ev.Detail),
                    Application = RedactSensitive(ev.Application),
                    Window = RedactSensitive(ev.Window),
                    Url = RedactUrl(ev.Url),
                    FilePath = RedactPath(ev.FilePath),
                    Person = RedactSensitive(ev.Person),
                    InputTrace = BuildInputTrace(ev),
                    Summary = BuildEvidenceSummary(ev),
                    SensitivityFlags = eventFlags
                });
                evidenceRefs.Add(evidence.Id);
            }

            int frameSequence = snapshot.Events.Count + 1;
            foreach (string frame in EnumerateSessionKeyframes(run.SourceSessionId).Take(300))
            {
                cancellationToken.ThrowIfCancellationRequested();
                CompanionTrainingEvidence evidence = _repository.AddTrainingEvidence(new CompanionTrainingEvidence
                {
                    RunId = run.Id,
                    SessionId = run.SourceSessionId,
                    Sequence = frameSequence++,
                    EventType = "replay_keyframe",
                    Detail = "Minimized replay keyframe captured during the recording.",
                    KeyframePath = frame,
                    Summary = Path.GetFileName(frame),
                    SensitivityFlags = "screen_keyframe"
                });
                evidenceRefs.Add(evidence.Id);
            }

            string risk = CalculateRisk(flags);
            _repository.UpdateTrainingRun(run.Id, "evidence_ready", 45, risk, "Evidence pack ready: " + evidenceRefs.Count.ToString(CultureInfo.InvariantCulture) + " evidence item(s).");

            if (string.IsNullOrWhiteSpace(modelEndpoint))
            {
                _repository.UpdateTrainingRun(run.Id, "needs_model", 55, risk, "Evidence is ready, but no model endpoint was configured for draft skill generation.", completed: true);
                return;
            }

            _repository.UpdateTrainingRun(run.Id, "drafting_skill", 70, risk, "Asking model to summarize the recording as a reviewed skill draft.");
            CompanionSkillDraft? draft = await GenerateDraftAsync(run, snapshot, evidenceRefs, flags, risk, modelEndpoint, model, cancellationToken).ConfigureAwait(false);
            if (draft == null)
                return;
            _repository.AddSkillDraft(draft);
            _repository.UpdateTrainingRun(run.Id, "completed", 100, draft.RiskLevel, "Draft skill created: " + draft.Name, completed: true);
        }
        catch (OperationCanceledException)
        {
            _repository.UpdateTrainingRun(run.Id, "cancelled", 100, "medium", "Training run cancelled.", completed: true);
        }
        catch (Exception ex)
        {
            _repository.UpdateTrainingRun(run.Id, "failed", 100, "high", "Training run failed.", ex.Message, completed: true);
        }
        finally
        {
            lock (_sync)
            {
                if (string.Equals(_activeRunId, run.Id, StringComparison.OrdinalIgnoreCase))
                    _activeRunId = "";
            }
        }
    }

    private async Task<CompanionSkillDraft?> GenerateDraftAsync(
        CompanionTrainingRun run,
        CompanionTrainingSessionSnapshot snapshot,
        IReadOnlyList<string> evidenceRefs,
        IReadOnlyCollection<string> flags,
        string risk,
        string modelEndpoint,
        string model,
        CancellationToken cancellationToken)
    {
        string prompt = BuildDraftPrompt(snapshot, flags);
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var payload = new
            {
                model = string.IsNullOrWhiteSpace(model) ? "local-model" : model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = "You convert recorded PC work sessions into safe, reusable Companion skill drafts. Return one JSON object only." },
                    new { role = "user", content = prompt }
                }
            };
            string json = JsonSerializer.Serialize(payload);
            using var response = await client.PostAsync(modelEndpoint, new StringContent(json, Encoding.UTF8, "application/json"), cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            string content = ExtractOpenAiContent(doc.RootElement);
            if (TryParseDraft(content, run, snapshot, evidenceRefs, risk, out CompanionSkillDraft parsed))
                return parsed;
        }
        catch
        {
            _repository.UpdateTrainingRun(run.Id, "needs_model", 55, risk, "Model endpoint did not return a usable draft. Evidence remains ready for retry.", completed: true);
            return null;
        }

        return BuildHeuristicDraft(run, snapshot, evidenceRefs, flags, risk);
    }

    private static CompanionSkillDraft BuildHeuristicDraft(
        CompanionTrainingRun run,
        CompanionTrainingSessionSnapshot snapshot,
        IReadOnlyList<string> evidenceRefs,
        IReadOnlyCollection<string> flags,
        string risk)
    {
        string apps = string.Join(", ", snapshot.Events.Select(item => item.Application).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5));
        string windows = string.Join("; ", snapshot.Events.Select(item => item.Window).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(4));
        string steps = "1. Restore the recorded context: " + (string.IsNullOrWhiteSpace(apps) ? "the same desktop/app setup" : apps) + ".\n" +
                       "2. Check the current foreground window before acting: " + (string.IsNullOrWhiteSpace(windows) ? "match the recorded workflow window" : windows) + ".\n" +
                       "3. Follow the recorded action order using small reversible steps.\n" +
                       "4. Stop and ask the user before any gated capability or mismatched screen state.\n" +
                       "5. Record the result and whether this skill should be refined.";
        return new CompanionSkillDraft
        {
            RunId = run.Id,
            SourceSessionId = run.SourceSessionId,
            Name = "Skill from " + (string.IsNullOrWhiteSpace(snapshot.Session.Title) ? "recorded session" : snapshot.Session.Title),
            Trigger = "When the goal, app, window, or files resemble session " + run.SourceSessionId,
            Prerequisites = "User has reviewed this draft and the current screen resembles the source recording.",
            Steps = steps,
            SafetyGates = BuildSafetyGates(flags),
            EvidenceRefs = string.Join(",", evidenceRefs.Take(80)),
            Confidence = 65,
            RiskLevel = risk,
            Status = "draft"
        };
    }

    private static bool TryParseDraft(string content, CompanionTrainingRun run, CompanionTrainingSessionSnapshot snapshot, IReadOnlyList<string> evidenceRefs, string fallbackRisk, out CompanionSkillDraft draft)
    {
        draft = new CompanionSkillDraft();
        string json = ExtractJsonObject(content);
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            draft = new CompanionSkillDraft
            {
                RunId = run.Id,
                SourceSessionId = run.SourceSessionId,
                Name = ReadJsonString(root, "name", "Skill from " + snapshot.Session.Title),
                Trigger = ReadJsonString(root, "trigger", "When the current context resembles the recording."),
                Prerequisites = ReadJsonString(root, "prerequisites", "Review current context before acting."),
                Steps = ReadJsonString(root, "steps", ""),
                SafetyGates = ReadJsonString(root, "safetyGates", ""),
                EvidenceRefs = string.Join(",", evidenceRefs.Take(80)),
                Confidence = ReadJsonInt(root, "confidence", 70),
                RiskLevel = ReadJsonString(root, "riskLevel", fallbackRisk),
                Status = "draft"
            };
            if (string.IsNullOrWhiteSpace(draft.Steps))
                draft.Steps = ReadJsonString(root, "orderedSteps", "");
            return !string.IsNullOrWhiteSpace(draft.Name) && !string.IsNullOrWhiteSpace(draft.Steps);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDraftPrompt(CompanionTrainingSessionSnapshot snapshot, IReadOnlyCollection<string> flags)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Create a reusable PC companion skill draft from this recorded session.");
        sb.AppendLine("Return JSON with: name, trigger, prerequisites, steps, safetyGates, confidence, riskLevel.");
        sb.AppendLine("Do not include passwords, raw secrets, private messages, or raw file contents.");
        sb.AppendLine("Sensitive flags: " + (flags.Count == 0 ? "none" : string.Join(", ", flags)));
        sb.AppendLine("Session: " + snapshot.Session.Title + " (" + snapshot.Session.Id + ")");
        foreach (CompanionEvent ev in snapshot.Events.Take(80))
            sb.AppendLine(ev.Sequence.ToString(CultureInfo.InvariantCulture) + ". " + ev.EventType + " | app=" + ev.Application + " | window=" + RedactSensitive(ev.Window) + " | detail=" + RedactSensitive(ev.Detail));
        if (snapshot.Files.Count > 0)
            sb.AppendLine("Session files: " + string.Join("; ", snapshot.Files.Take(20).Select(file => file.Kind + ":" + RedactPath(file.Path))));
        return sb.ToString();
    }

    private IEnumerable<string> EnumerateSessionKeyframes(string sessionId)
    {
        string sessionRoot = Path.Combine(_repository.TrainingRoot, "ReplayFrames", BuildSafeSegment(sessionId));
        return Directory.Exists(sessionRoot)
            ? Directory.EnumerateFiles(sessionRoot, "*.jpg", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            : Enumerable.Empty<string>();
    }

    private static string AnalyzeSensitivity(CompanionEvent ev)
    {
        var flags = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        string text = (ev.EventType + " " + ev.Detail + " " + ev.Application + " " + ev.Window + " " + ev.Url + " " + ev.FilePath + " " + ev.Person).ToLowerInvariant();
        if (text.Contains("login") || text.Contains("sign in") || text.Contains("password") || text.Contains("account"))
            flags.Add("account_login");
        if (text.Contains("money") || text.Contains("checkout") || text.Contains("buy") || text.Contains("bank") || text.Contains("invoice") || text.Contains("credit"))
            flags.Add("spend_money");
        if (!string.IsNullOrWhiteSpace(ev.Person) || text.Contains("discord") || text.Contains("slack") || text.Contains("teams") || text.Contains("gmail") || text.Contains("chat"))
            flags.Add("human_chat");
        if (!string.IsNullOrWhiteSpace(ev.FilePath))
            flags.Add("real_file");
        if (text.Contains("settings") || text.Contains("control panel") || text.Contains("regedit") || text.Contains("system32"))
            flags.Add("pc_settings");
        if (ev.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase) || text.Contains("browser") || text.Contains("youtube"))
            flags.Add("internet");
        if (LooksSecretLike(text))
            flags.Add("secret_redacted");
        return string.Join(",", flags);
    }

    private static string BuildSafetyGates(IReadOnlyCollection<string> flags)
    {
        if (flags.Count == 0)
            return "Ask before live input, files, accounts, money, human interaction, internet, or PC settings if they appear.";
        return "This skill references " + string.Join(", ", flags) + ". Require the matching Companion permission gate and stop for user review on mismatch.";
    }

    private static string CalculateRisk(IReadOnlyCollection<string> flags)
    {
        if (flags.Contains("spend_money") || flags.Contains("account_login") || flags.Contains("secret_redacted"))
            return "high";
        if (flags.Contains("human_chat") || flags.Contains("real_file") || flags.Contains("pc_settings") || flags.Contains("internet"))
            return "medium";
        return "low";
    }

    private static void AddFlags(SortedSet<string> flags, string csv)
    {
        foreach (string flag in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            flags.Add(flag);
    }

    private static string BuildInputTrace(CompanionEvent ev)
    {
        if (ev.EventType.Contains("control", StringComparison.OrdinalIgnoreCase))
            return RedactSensitive(ev.Detail);
        return "";
    }

    private static string BuildEvidenceSummary(CompanionEvent ev)
    {
        string app = string.IsNullOrWhiteSpace(ev.Application) ? "desktop" : ev.Application;
        return ev.Sequence.ToString(CultureInfo.InvariantCulture) + ". " + ev.EventType + " in " + app + ": " + RedactSensitive(ev.Detail);
    }

    private static string RedactSensitive(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        string redacted = Regex.Replace(value, "(password|passwd|token|secret|api[_-]?key)\\s*[:=]\\s*\\S+", "$1=[redacted]", RegexOptions.IgnoreCase);
        redacted = Regex.Replace(redacted, "[A-Za-z0-9_\\-]{24,}\\.[A-Za-z0-9_\\-]{12,}\\.[A-Za-z0-9_\\-]{12,}", "[redacted-token]");
        return redacted.Length > 600 ? redacted[..600] + "..." : redacted;
    }

    private static string RedactUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "";
        try
        {
            var uri = new Uri(url);
            return uri.GetLeftPart(UriPartial.Path);
        }
        catch
        {
            return RedactSensitive(url);
        }
    }

    private static string RedactPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        string name = Path.GetFileName(path);
        string directory = Path.GetDirectoryName(path) ?? "";
        if (directory.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            directory = directory.Replace(Environment.UserName, "[user]", StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(name) ? directory : Path.Combine(directory, name);
    }

    private static bool LooksSecretLike(string text)
    {
        return Regex.IsMatch(text ?? "", "(password|passwd|api[_-]?key|token|secret)", RegexOptions.IgnoreCase);
    }

    private static string ExtractOpenAiContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            JsonElement first = choices[0];
            if (first.TryGetProperty("message", out JsonElement message) && message.TryGetProperty("content", out JsonElement content))
                return content.GetString() ?? content.ToString();
            if (first.TryGetProperty("text", out JsonElement text))
                return text.GetString() ?? text.ToString();
        }
        return root.ToString();
    }

    private static string ExtractJsonObject(string text)
    {
        string safeText = text ?? "";
        int start = safeText.IndexOf('{');
        int end = safeText.LastIndexOf('}');
        return start >= 0 && end > start ? safeText.Substring(start, end - start + 1) : "";
    }

    private static string ReadJsonString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : value.ToString();
        return fallback;
    }

    private static int ReadJsonInt(JsonElement root, string name, int fallback)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
                return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                return number;
        }
        return fallback;
    }

    private static void SaveDownscaledJpeg(byte[] sourceBytes, string targetPath, int maxWidth, long quality)
    {
        using var sourceStream = new MemoryStream(sourceBytes);
        using var source = new Bitmap(sourceStream);
        double scale = source.Width > maxWidth ? (double)maxWidth / source.Width : 1d;
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var target = new Bitmap(width, height);
        using (Graphics graphics = Graphics.FromImage(target))
        {
            graphics.CompositingQuality = CompositingQuality.HighSpeed;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighSpeed;
            graphics.DrawImage(source, 0, 0, width, height);
        }

        ImageCodecInfo? encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(item => item.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder == null)
        {
            target.Save(targetPath, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, Math.Clamp(quality, 35, 90));
        target.Save(targetPath, encoder, parameters);
    }

    private static string BuildSafeSegment(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "session" : value.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value.Length > 120 ? value[..120] : value;
    }
}
