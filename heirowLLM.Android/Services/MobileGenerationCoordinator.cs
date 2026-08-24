using System.Diagnostics;
using heirowLLM.Mobile.Controls;
using heirowLLM.Mobile.Models;

namespace heirowLLM.Mobile.Services;

public sealed record MobileGenerationRequest(
    ServerInfo Server,
    HeirowLlmClient Client,
    string SessionId,
    string ProjectId,
    string Model,
    string Service,
    string InteractionMode,
    string ReasoningLevel,
    string SessionReasoningLevel,
    bool ModelSupportsTools,
    bool JackhammerEnabled,
    int JackhammerTurnBudget,
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
    IReadOnlyList<JackhammerPlanStep> JackhammerSteps,
    string JackhammerGoal,
    string JackhammerStatus,
    int JackhammerProgressPercent,
    double Progress,
    bool JackhammerEnabled,
    bool IsGenerating,
    bool IsRecovering,
    bool IsStopped,
    bool HasError)
{
    public static MobileGenerationSnapshot Empty { get; } = new("", "", "", "", 0, "", "", "", "", "", Array.Empty<ToolActivity>(), Array.Empty<JackhammerPlanStep>(), "", "", -1, 0, false, false, false, false, false);
}

public sealed class MobileGenerationCoordinator
{
    private readonly object _gate = new();
    private readonly IMobileNotificationService _notifications;
    private CancellationTokenSource? _cancellation;
    private HeirowLlmClient? _client;
    private string _streamId = "";
    private MobileGenerationSnapshot _snapshot = MobileGenerationSnapshot.Empty;
    private readonly MobileResponseMilestonePolicy _milestonePolicy = new();
    private readonly MobileStreamTextAccumulator _streamText = new();
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;
    private bool _publishScheduled;
    private MobileGenerationRequest? _pendingRequest;
    private bool _awaitingAuthentication;
    private bool _streamConnected;
    private bool _steeringEnabled;

    public MobileGenerationCoordinator(IMobileNotificationService notifications) => _notifications = notifications;

    public event EventHandler<MobileGenerationSnapshot>? SnapshotChanged;
    public event EventHandler<MobileAlignmentSnapshot>? AlignmentChanged;
    public event EventHandler? AuthenticationRequired;

    public MobileGenerationSnapshot Current
    {
        get { lock (_gate) return _snapshot; }
    }

    public bool IsGenerating => Current.IsGenerating;

    public bool CanSteer
    {
        get
        {
            lock (_gate)
                return _streamConnected && _steeringEnabled && _snapshot.IsGenerating && _client is not null && !string.IsNullOrWhiteSpace(_streamId);
        }
    }

    public async Task<bool> StartAsync(MobileGenerationRequest request)
    {
        lock (_gate)
        {
            if (_snapshot.IsGenerating) return false;
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            _client = request.Client;
            _pendingRequest = request;
            _awaitingAuthentication = false;
            _streamConnected = false;
            _steeringEnabled = !request.Service.EndsWith("_generation", StringComparison.OrdinalIgnoreCase);
            _streamId = "mobile_" + Guid.NewGuid().ToString("N");
            _milestonePolicy.Reset();
            _streamText.Reset();
            _snapshot = new MobileGenerationSnapshot(
                _streamId, request.Server.LaunchKey, request.SessionId, request.UserContent,
                request.PriorServerMessageCount, "", "", StartingStatus(request.Service), "", "", Array.Empty<ToolActivity>(),
                Array.Empty<JackhammerPlanStep>(), "", "", -1, 0, request.JackhammerEnabled, true, false, false, false);
        }

        Publish(immediate: true);
        await _notifications.EnsurePermissionAsync();
        _notifications.StartGeneration(request.Server.LaunchKey);
        _ = Task.Run(() => RunAsync(request, _streamId, _cancellation.Token));
        return true;
    }

    public Task<bool> RetryPendingAsync()
    {
        MobileGenerationRequest? request;
        lock (_gate)
        {
            request = _pendingRequest;
            _awaitingAuthentication = false;
        }
        return request is null ? Task.FromResult(false) : StartAsync(request);
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellation;
        HeirowLlmClient? client;
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

    public async Task SteerAsync(string text, CancellationToken cancellationToken = default)
    {
        HeirowLlmClient client;
        string streamId;
        string sessionId;
        lock (_gate)
        {
            if (!_streamConnected || !_steeringEnabled || !_snapshot.IsGenerating || _client is null || string.IsNullOrWhiteSpace(_streamId))
                throw new InvalidOperationException("Steering is available only after an active text response connects.");
            client = _client;
            streamId = _streamId;
            sessionId = _snapshot.SessionId;
        }
        text = (text ?? "").Trim();
        if (text.Length == 0) throw new ArgumentException("Enter steering direction first.", nameof(text));
        await client.SteerAsync(streamId, sessionId, text, cancellationToken);
        SetState(snapshot => snapshot with { Status = "Steering accepted — applying at the next safe response break" });
    }

    private async Task RunAsync(MobileGenerationRequest request, string streamId, CancellationToken cancellationToken)
    {
        using var telemetryCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task telemetry = MonitorHardwareAsync(request.Client, telemetryCancellation.Token);
        try
        {
            SetState(snapshot => snapshot with { Status = RunningStatus(request.Service), Progress = 0 });
            await StreamOnceAsync(request, streamId, cancellationToken);

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
        catch (Exception ex) when (RequiresAuthentication(ex))
        {
            Debug.WriteLine("heirowLLM Mobile authentication required: " + ex.Message);
            PauseForAuthentication();
        }
        catch (MobileStreamErrorException ex)
        {
            Debug.WriteLine("heirowLLM Mobile stream error: " + ex.Message);
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
            Debug.WriteLine("heirowLLM Mobile stream interrupted: " + ex);
            SetState(snapshot => snapshot with { IsRecovering = true, Status = "Connection interrupted — recovering…" });
            RecoveryResult result;
            try
            {
                result = await RecoverOrRetryAsync(request, streamId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                SetState(snapshot => snapshot with { IsGenerating = false, IsRecovering = false, IsStopped = true, Status = "Stopped" });
                return;
            }
            if (result == RecoveryResult.AuthenticationRequired)
            {
                PauseForAuthentication();
                return;
            }
            bool recovered = result == RecoveryResult.Recovered;
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
                    _streamConnected = false;
                    _cancellation?.Dispose();
                    _cancellation = null;
                    _client = null;
                    _streamId = "";
                    if (!_awaitingAuthentication)
                        _pendingRequest = null;
                }
            }
        }
    }

    private async Task StreamOnceAsync(MobileGenerationRequest request, string streamId, CancellationToken cancellationToken)
    {
        await foreach (ChatStreamEvent item in request.Client.StreamChatAsync(
            request.Model, request.Service, request.InteractionMode, request.SessionId, request.ProjectId, request.ReasoningLevel,
            request.SessionReasoningLevel, request.ModelSupportsTools, request.JackhammerEnabled, request.JackhammerTurnBudget,
            request.Messages, request.Attachments, streamId, cancellationToken))
        {
            lock (_gate) _streamConnected = true;
            ApplyStreamEvent(item, request.Server.LaunchKey);
        }
    }

    private async Task<RecoveryResult> RecoverOrRetryAsync(MobileGenerationRequest request, string streamId, CancellationToken cancellationToken)
    {
        int[] reconnectDelaysMs = [0, 500, 1500, 3000, 5000];
        foreach (int delayMs in reconnectDelaysMs)
        {
            if (delayMs > 0) await Task.Delay(delayMs, cancellationToken);
            try
            {
                await request.Client.ConnectAsync(request.Server, cancellationToken);
                if (await ReconcileAsync(request, cancellationToken, 8, TimeSpan.FromMilliseconds(500)))
                    return RecoveryResult.Recovered;
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (RequiresAuthentication(ex))
            {
                return RecoveryResult.AuthenticationRequired;
            }
            catch
            {
                // Try the next bounded reconnect delay.
            }
        }

        try
        {
            SetState(snapshot => snapshot with { Status = "Reconnected — retrying your request…", IsRecovering = true });
            await StreamOnceAsync(request, streamId, cancellationToken);
            bool reconciled = await ReconcileAsync(request, cancellationToken, 12, TimeSpan.FromMilliseconds(500));
            bool hasVisible = !string.IsNullOrWhiteSpace(ModelOutputSanitizer.Sanitize(Current.Content));
            return reconciled || hasVisible ? RecoveryResult.Recovered : RecoveryResult.Failed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (RequiresAuthentication(ex))
        {
            return RecoveryResult.AuthenticationRequired;
        }
        catch (Exception retryError)
        {
            Debug.WriteLine("heirowLLM Mobile automatic retry failed: " + retryError);
            return RecoveryResult.Failed;
        }
    }

    private void PauseForAuthentication()
    {
        lock (_gate) _awaitingAuthentication = true;
        SetState(snapshot => snapshot with
        {
            IsGenerating = false,
            IsRecovering = false,
            HasError = false,
            Status = "Sign in to continue this request"
        });
        MainThread.BeginInvokeOnMainThread(() => AuthenticationRequired?.Invoke(this, EventArgs.Empty));
    }

    private static bool RequiresAuthentication(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return true;
        if (exception is HttpRequestException requestException &&
            requestException.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return true;
        string message = exception.Message ?? "";
        return message.Contains("authentication required", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sign in", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyStreamEvent(ChatStreamEvent item, string serverKey)
    {
        if (item.Type.Equals("alignment", StringComparison.OrdinalIgnoreCase) && item.Alignment is not null)
        {
            AlignmentChanged?.Invoke(this, item.Alignment);
            return;
        }
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
                    JackhammerSteps = item.JackhammerSteps.Count > 0 ? item.JackhammerSteps : snapshot.JackhammerSteps,
                    JackhammerGoal = string.IsNullOrWhiteSpace(item.JackhammerGoal) ? snapshot.JackhammerGoal : item.JackhammerGoal,
                    JackhammerStatus = string.IsNullOrWhiteSpace(item.JackhammerStatus) ? snapshot.JackhammerStatus : item.JackhammerStatus,
                    JackhammerProgressPercent = item.JackhammerProgressPercent >= 0 ? item.JackhammerProgressPercent : snapshot.JackhammerProgressPercent,
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
        _streamText.Append(text, reasoning);
        SetState(snapshot =>
        {
            return snapshot with { Content = _streamText.Content, Reasoning = _streamText.Reasoning };
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

    private async Task MonitorHardwareAsync(HeirowLlmClient client, CancellationToken cancellationToken)
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

    private enum RecoveryResult
    {
        Failed,
        Recovered,
        AuthenticationRequired
    }

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
