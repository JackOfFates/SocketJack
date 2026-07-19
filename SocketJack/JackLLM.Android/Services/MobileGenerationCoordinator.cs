using System.Diagnostics;
using JackLLM.Mobile.Controls;
using JackLLM.Mobile.Models;

namespace JackLLM.Mobile.Services;

public sealed record MobileGenerationRequest(
    ServerInfo Server,
    JackLlmClient Client,
    string SessionId,
    string Model,
    string Service,
    string ReasoningLevel,
    string SessionReasoningLevel,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<AttachmentInfo> Attachments,
    int PriorServerMessageCount,
    string UserContent);

public sealed record MobileGenerationSnapshot(
    string GenerationId,
    string ServerKey,
    string SessionId,
    string UserContent,
    int PriorServerMessageCount,
    string Content,
    string Reasoning,
    string Status,
    string Telemetry,
    string RouteSummary,
    IReadOnlyList<ToolActivity> Tools,
    double Progress,
    bool IsGenerating,
    bool IsRecovering,
    bool IsStopped,
    bool HasError)
{
    public static MobileGenerationSnapshot Empty { get; } = new("", "", "", "", 0, "", "", "", "", "", Array.Empty<ToolActivity>(), 0, false, false, false, false);
}

public sealed class MobileGenerationCoordinator
{
    private readonly object _gate = new();
    private readonly IMobileNotificationService _notifications;
    private CancellationTokenSource? _cancellation;
    private JackLlmClient? _client;
    private string _streamId = "";
    private MobileGenerationSnapshot _snapshot = MobileGenerationSnapshot.Empty;
    private readonly MobileResponseMilestonePolicy _milestonePolicy = new();
    private bool _capturingEmbeddedReasoning;
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;
    private bool _publishScheduled;

    public MobileGenerationCoordinator(IMobileNotificationService notifications) => _notifications = notifications;

    public event EventHandler<MobileGenerationSnapshot>? SnapshotChanged;

    public MobileGenerationSnapshot Current
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool IsGenerating => Current.IsGenerating;

    public async Task<bool> StartAsync(MobileGenerationRequest request)
    {
        lock (_gate)
        {
            if (_snapshot.IsGenerating) return false;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _client = request.Client;
            _streamId = "mobile_" + Guid.NewGuid().ToString("N");
            _milestonePolicy.Reset();
            _capturingEmbeddedReasoning = false;
            _snapshot = new MobileGenerationSnapshot(
                _streamId, request.Server.LaunchKey, request.SessionId, request.UserContent,
                request.PriorServerMessageCount, "", "", StartingStatus(request.Service), "", "", Array.Empty<ToolActivity>(),
                0, true, false, false, false);
        }

        Publish(immediate: true);
        await _notifications.EnsurePermissionAsync();
        _notifications.StartGeneration(request.Server.LaunchKey);
        _ = Task.Run(() => RunAsync(request, _streamId, _cancellation.Token));
        return true;
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        JackLlmClient? client;
        string streamId;
        lock (_gate)
        {
            cancellation = _cancellation;
            client = _client;
            streamId = _streamId;
        }
        cancellation?.Cancel();
        if (client is not null && !string.IsNullOrWhiteSpace(streamId))
        {
            try { await client.StopAsync(streamId); }
            catch { }
        }
    }

    private async Task RunAsync(MobileGenerationRequest request, string streamId, CancellationToken cancellationToken)
    {
        using var telemetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task telemetry = MonitorHardwareAsync(request.Client, telemetryCancellation.Token);
        try
        {
            SetState(snapshot => snapshot with { Status = RunningStatus(request.Service), Progress = 0 });
            await foreach (ChatStreamEvent item in request.Client.StreamChatAsync(
                request.Model, request.Service, request.SessionId, request.ReasoningLevel,
                request.SessionReasoningLevel, request.Messages, request.Attachments, streamId, cancellationToken))
            {
                ApplyStreamEvent(item, request.Server.LaunchKey);
            }

            await ReconcileAsync(request, cancellationToken, 10, TimeSpan.FromMilliseconds(300));
            SetState(snapshot => snapshot with
            {
                Content = MobileOutputReliability.CollapseExactAdjacentBlocks(snapshot.Content),
                Reasoning = MobileOutputReliability.CollapseExactAdjacentBlocks(snapshot.Reasoning)
            });
            bool hasVisibleOutput = !string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(Current.Content));
            SetState(snapshot => snapshot with
            {
                IsGenerating = false,
                IsRecovering = false,
                Content = hasVisibleOutput ? snapshot.Content : MissingVisibleAnswerMessage,
                HasError = !hasVisibleOutput,
                Status = hasVisibleOutput ? "" : "No visible answer received",
                Progress = 1
            });
        }
        catch (OperationCanceledException)
        {
            SetState(snapshot => snapshot with { IsGenerating = false, IsRecovering = false, IsStopped = true, Status = "Stopped" });
        }
        catch (MobileStreamErrorException ex)
        {
            Debug.WriteLine("JackLLM Mobile stream error: " + ex.Message);
            SetState(snapshot => snapshot with
            {
                IsGenerating = false,
                IsRecovering = false,
                HasError = true,
                Content = string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(snapshot.Content)) ? ex.Message : snapshot.Content,
                Status = ex.Message
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("JackLLM Mobile stream interrupted: " + ex);
            SetState(snapshot => snapshot with { IsRecovering = true, Status = "Connection interrupted — recovering…" });
            bool recovered = await ReconcileAsync(request, CancellationToken.None, 24, TimeSpan.FromMilliseconds(500));
            SetState(snapshot => snapshot with
            {
                IsGenerating = false,
                IsRecovering = false,
                HasError = !recovered,
                Content = recovered || !string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(snapshot.Content))
                    ? snapshot.Content
                    : "The connection was interrupted before a visible answer could be recovered. Reopen the session and try again.",
                Status = recovered ? "" : "Could not recover the completed response. Reopen the session to retry."
            });
        }
        finally
        {
            telemetryCancellation.Cancel();
            try { await telemetry; } catch (OperationCanceledException) { }
            _notifications.StopGeneration();
            lock (_gate)
            {
                if (_streamId == streamId)
                {
                    _cancellation?.Dispose();
                    _cancellation = null;
                    _client = null;
                    _streamId = "";
                }
            }
        }
    }

    private void ApplyStreamEvent(ChatStreamEvent item, string serverKey)
    {
        string eventType = item.Type.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        switch (eventType)
        {
            case "content": case "delta": case "contentdelta": case "answer": case "message":
                Append(item.Content, reasoning: false);
                Append(item.Reasoning, reasoning: true);
                break;
            case "reasoning": case "reasoningdelta": case "thinking": case "thinkingdelta":
                Append(item.Reasoning + item.Content, reasoning: true);
                break;
            case "progress":
                SetState(snapshot => snapshot with { Status = string.IsNullOrWhiteSpace(item.Status) ? "Working…" : item.Status, Progress = NormalizeProgress(item.Progress) });
                break;
            case "route":
                SetState(snapshot => snapshot with
                {
                    RouteSummary = "Auto · " + item.RoutedModel + " · " + item.ReasoningLevel + (string.IsNullOrWhiteSpace(item.RouteReason) ? "" : " · " + item.RouteReason),
                    Status = "Routing complete"
                });
                break;
            case "usage":
                string prompt = item.PromptTokensTotal > 0 ? $" · Prompt {item.PromptTokensLoaded:N0}/{item.PromptTokensTotal:N0}" : "";
                SetState(snapshot => snapshot with { Status = $"Tokens {item.TokensUsed:N0} · GPU compute {item.GpuSecondsUsed:0.##}s · CPU {item.CpuComputeSecondsUsed:0.##}s · RAM {item.RamGbSecondsUsed:0.##} GB·s{prompt}" });
                break;
            case "toolcall": case "serviceaccess": case "filechanges": case "filechange":
                SetState(snapshot => snapshot with
                {
                    Tools = snapshot.Tools.Concat(new[]
                    {
                        new ToolActivity
                        {
                            Name = string.IsNullOrWhiteSpace(item.ToolName) ? (eventType.StartsWith("file", StringComparison.Ordinal) ? "File changes" : "Tool") : item.ToolName,
                            Status = string.IsNullOrWhiteSpace(item.ToolStatus) ? "running" : item.ToolStatus,
                            Detail = string.IsNullOrWhiteSpace(item.ToolDetail) ? item.Status : item.ToolDetail
                        }
                    }).ToArray(),
                    Status = string.IsNullOrWhiteSpace(item.Status) ? snapshot.Status : item.Status
                });
                break;
            case "error":
                throw new MobileStreamErrorException(FirstNonEmpty(item.Content, item.Status, "The Workstation returned an error."));
            case "done": case "complete": case "completed": case "end":
                SetState(snapshot => snapshot with { Status = "" });
                break;
            default:
                Append(item.Reasoning, reasoning: true);
                Append(item.Content, reasoning: false);
                break;
        }

        MobileGenerationSnapshot current = Current;
        if (_milestonePolicy.TryReachVisibleAnswer(!string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(current.Content))))
        {
            _notifications.NotifyThinkingCompleted(serverKey, current.SessionId);
        }
    }

    private void Append(string text, bool reasoning)
    {
        if (string.IsNullOrEmpty(text)) return;
        SetState(snapshot =>
        {
            string content = snapshot.Content;
            string thought = snapshot.Reasoning;
            if (reasoning)
            {
                thought = MobileOutputReliability.MergeStreamDelta(thought, text);
            }
            else if (_capturingEmbeddedReasoning)
            {
                int end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (end >= 0)
                {
                    thought = MobileOutputReliability.MergeStreamDelta(thought, text[..end]);
                    content = MobileOutputReliability.MergeStreamDelta(content, text[(end + 8)..]);
                    _capturingEmbeddedReasoning = false;
                }
                else thought = MobileOutputReliability.MergeStreamDelta(thought, text);
            }
            else
            {
                int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    content = MobileOutputReliability.MergeStreamDelta(content, text[..start]);
                    string remainder = text[(start + 7)..];
                    int end = remainder.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                    if (end >= 0)
                    {
                        thought = MobileOutputReliability.MergeStreamDelta(thought, remainder[..end]);
                        content = MobileOutputReliability.MergeStreamDelta(content, remainder[(end + 8)..]);
                    }
                    else
                    {
                        thought = MobileOutputReliability.MergeStreamDelta(thought, remainder);
                        _capturingEmbeddedReasoning = true;
                    }
                }
                else content = MobileOutputReliability.MergeStreamDelta(content, text);
            }
            return snapshot with { Content = content, Reasoning = thought };
        });
    }

    private async Task<bool> ReconcileAsync(MobileGenerationRequest request, CancellationToken cancellationToken, int attempts, TimeSpan delay)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                ChatSessionDetail detail = await request.Client.GetSessionAsync(request.SessionId, cancellationToken);
                ChatMessage? saved = detail.Messages
                    .Skip(Math.Min(request.PriorServerMessageCount, detail.Messages.Count))
                    .LastOrDefault(message => message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
                        && (!string.IsNullOrWhiteSpace(message.Content) || !string.IsNullOrWhiteSpace(message.Reasoning)));
                if (saved is not null)
                {
                    string content = saved.Content;
                    string reasoning = saved.Reasoning;
                    ExtractEmbeddedReasoning(ref content, ref reasoning);
                    content = MobileOutputReliability.CollapseExactAdjacentBlocks(content);
                    reasoning = MobileOutputReliability.CollapseExactAdjacentBlocks(reasoning);
                    SetState(snapshot => snapshot with
                    {
                        Content = ChooseMostComplete(snapshot.Content, content),
                        Reasoning = ChooseMostComplete(snapshot.Reasoning, reasoning)
                    });
                    bool hasVisibleAnswer = !string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(content));
                    if (_milestonePolicy.TryReachVisibleAnswer(hasVisibleAnswer))
                    {
                        _notifications.NotifyThinkingCompleted(request.Server.LaunchKey, request.SessionId);
                    }
                    if (hasVisibleAnswer) return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { }
            if (attempt + 1 < attempts) await Task.Delay(delay, cancellationToken);
        }
        return false;
    }

    private const string MissingVisibleAnswerMessage =
        "The agent finished without producing a visible answer. Try a lower reasoning level or another tool-capable model.";

    private async Task MonitorHardwareAsync(JackLlmClient client, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                HardwareSnapshot hardware = await client.GetHardwareAsync(cancellationToken);
                SetState(snapshot => snapshot with { Telemetry = hardware.Display });
            }
            catch (OperationCanceledException) { break; }
            catch { }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private void SetState(Func<MobileGenerationSnapshot, MobileGenerationSnapshot> update)
    {
        lock (_gate) _snapshot = update(_snapshot);
        Publish();
    }

    private void Publish(bool immediate = false)
    {
        EventHandler<MobileGenerationSnapshot>? handler = SnapshotChanged;
        if (handler is null) return;
        MobileGenerationSnapshot? snapshot = null;
        TimeSpan delay = TimeSpan.Zero;
        lock (_gate)
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _lastPublished;
            if (immediate || elapsed >= TimeSpan.FromMilliseconds(80))
            {
                _lastPublished = DateTimeOffset.UtcNow;
                snapshot = _snapshot;
            }
            else if (!_publishScheduled)
            {
                _publishScheduled = true;
                delay = TimeSpan.FromMilliseconds(80) - elapsed;
            }
        }
        if (snapshot is not null)
        {
            handler.Invoke(this, snapshot);
            return;
        }
        if (delay > TimeSpan.Zero) _ = PublishAfterDelayAsync(delay);
    }

    private async Task PublishAfterDelayAsync(TimeSpan delay)
    {
        await Task.Delay(delay);
        EventHandler<MobileGenerationSnapshot>? handler = SnapshotChanged;
        MobileGenerationSnapshot snapshot;
        lock (_gate)
        {
            _publishScheduled = false;
            _lastPublished = DateTimeOffset.UtcNow;
            snapshot = _snapshot;
        }
        handler?.Invoke(this, snapshot);
    }

    private static double NormalizeProgress(double? value) => !value.HasValue ? 0 : Math.Clamp(value.Value > 1 ? value.Value / 100d : value.Value, 0, 1);

    private static string StartingStatus(string service) => service.Equals("agent", StringComparison.OrdinalIgnoreCase)
        ? "Starting Advanced Agent…"
        : "Starting " + service.Replace('_', ' ') + "…";

    private static string RunningStatus(string service) => service.Equals("agent", StringComparison.OrdinalIgnoreCase)
        ? "Advanced Agent is analyzing the request…"
        : "Running " + service.Replace('_', ' ') + "…";

    private static string FirstNonEmpty(params string[] values) => values.First(value => !string.IsNullOrWhiteSpace(value));

    private static string ChooseMostComplete(string streamed, string saved)
    {
        streamed = MobileOutputReliability.CollapseExactAdjacentBlocks(streamed);
        saved = MobileOutputReliability.CollapseExactAdjacentBlocks(saved);
        return saved.Length >= streamed.Length ? saved : streamed;
    }

    private sealed class MobileStreamErrorException(string message) : Exception(message);

    private static void ExtractEmbeddedReasoning(ref string content, ref string reasoning)
    {
        int start = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (start >= 0)
        {
            int end = content.IndexOf("</think>", start + 7, StringComparison.OrdinalIgnoreCase);
            if (end >= 0)
            {
                reasoning += content[(start + 7)..end];
                content = content[..start] + content[(end + 8)..];
            }
            else
            {
                reasoning += content[(start + 7)..];
                content = content[..start];
            }
        }
    }
}
