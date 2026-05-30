using System.Text.Json;
using System.Net;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SocketJack.Net;

namespace JackLLMCompanion;

public sealed class CompanionHttpHost : IDisposable
{
    private readonly CompanionRepository _repository;
    private readonly DesktopAutomationService _desktop;
    private readonly CompanionLlmRunner _runner;
    private readonly CompanionTrainingService _training;
    private readonly CompanionProcessService _processes;
    private readonly string _authToken = CreateAuthToken();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
    private HttpServer? _server;

    public CompanionHttpHost(CompanionRepository repository, DesktopAutomationService desktop, CompanionLlmRunner runner, CompanionTrainingService training, CompanionProcessService processes)
    {
        _repository = repository;
        _desktop = desktop;
        _runner = runner;
        _training = training;
        _processes = processes;
    }

    public int Port { get; private set; }
    public string BaseUrl => Port > 0 ? "http://localhost:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) : "";

    public void Start()
    {
        Stop();
        if (TryStart(80))
            return;
        if (TryStart(8091))
            return;
        throw new InvalidOperationException("Companion could not bind port 80 or fallback port 8091.");
    }

    public void Stop()
    {
        if (_server == null)
            return;

        try
        {
            if (_server.IsListening)
                _server.StopListening();
            _server.Dispose();
        }
        catch
        {
        }
        finally
        {
            _server = null;
            Port = 0;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    private bool TryStart(int port)
    {
        var options = NetworkOptions.NewDefault();
        options.BindAddress = IPAddress.Loopback;
        var server = new HttpServer(options, port, "SocketJack Companion Workspace")
        {
            EnablePort80HttpsRedirect = false,
            CacheControl = "no-store"
        };
        server.RequestGate = RequestGate;
        MapRoutes(server);
        bool listening = server.Listen();
        if (!listening)
        {
            try { server.Dispose(); } catch { }
            return false;
        }

        _server = server;
        Port = port;
        return true;
    }

    private void MapRoutes(HttpServer server)
    {
        server.Map("GET", "/", (_, _, _) => WorkspaceHtml());
        server.Map("GET", "/Workspace", (_, _, _) => WorkspaceHtml());
        server.Map("GET", "/file", (_, _, _) => FileHtml());
        server.Map("GET", "/api/workspace", (_, _, _) => Json(GetWorkspaceState()));
        server.Map("GET", "/api/files", (_, _, _) => Json(_repository.GetFileState()));
        server.Map("POST", "/api/companion/permissions", (_, request, _) => SavePermissions(request));
        server.Map("POST", "/api/companion/approvals/decide", (_, request, _) => DecideApproval(request));
        server.Map("POST", "/api/companion/template", (_, request, _) => SaveTemplate(request));
        server.Map("POST", "/api/companion/wand/name", (_, _, _) => Json(new { ok = true, template = _repository.GenerateName() }));
        server.Map("POST", "/api/companion/wand/interests", (_, _, _) => Json(new { ok = true, template = _repository.InferInterests() }));
        server.Map("POST", "/api/companion/recording/start", (_, request, _) => StartRecording(request));
        server.Map("POST", "/api/companion/recording/stop", (_, request, _) => StopRecording(request));
        server.Map("POST", "/api/companion/action", (_, request, _) => RequestControlAction(request));
        server.Map("POST", "/api/companion/emergency-stop", (_, _, _) => EmergencyStop());
        server.Map("POST", "/api/companion/llm/task", (_, request, _) => SubmitLlmTask(request));
        server.Map("POST", "/api/companion/llm/stop", (_, request, _) => StopLlmTask(request));
        server.Map("GET", "/api/companion/llm/runner", (_, _, _) => Json(new { ok = true, runner = _runner.GetStatus() }));
        server.Map("GET", "/api/companion/llm/models", (_, request, cancellationToken) => DiscoverModels(request, cancellationToken));
        server.Map("POST", "/api/companion/llm/runner/start", (_, request, _) => StartRunner(request));
        server.Map("POST", "/api/companion/llm/runner/stop", (_, _, _) => StopRunner());
        server.Map("POST", "/api/companion/llm/config", (_, request, _) => ConfigureRunner(request));
        server.Map("GET", "/api/companion/training/state", (_, _, _) => Json(new { ok = true, training = _repository.GetTrainingState() }));
        server.Map("POST", "/api/companion/training/start", (_, request, _) => StartTraining(request));
        server.Map("POST", "/api/companion/training/cancel", (_, request, _) => CancelTraining(request));
        server.Map("POST", "/api/companion/training/settings", (_, request, _) => SaveTrainingSettings(request));
        server.Map("POST", "/api/companion/skills/review", (_, request, _) => ReviewSkillDraft(request));
        server.Map("GET", "/api/companion/replay", (_, request, _) => ReplayIndex(request));
        server.Map("GET", "/api/companion/replay/frame", (_, request, _) => ReplayFrame(request));
        server.Map("GET", "/api/companion/screen", (_, request, _) => CaptureScreenFile(request));
        server.Map("GET", "/api/companion/screen.json", (_, request, _) => CaptureScreenJson(request));
        server.Map("GET", "/api/companion/desktop/transport", (_, request, _) => DesktopTransportInfo(request));
        server.MapStream("GET", "/api/companion/desktop/stream", (_, request, stream, cancellationToken) => StreamDesktop(request, stream, cancellationToken));
        server.Map("GET", "/api/companion/desktop/ws", (connection, request, cancellationToken) => StreamDesktopWebSocket(connection, request, cancellationToken));
        server.Map("GET", "/api/companion/processes", (_, request, _) => Processes(request));
        server.Map("GET", "/api/companion/windows", (_, request, _) => Windows(request));
        server.Map("GET", "/api/companion/process-browser", (_, request, _) => ProcessBrowser(request));
        server.Map("POST", "/api/companion/processes/kill", (_, request, _) => KillProcess(request));
        server.Map("POST", "/api/companion/processes/start", (_, request, _) => StartProcess(request));
        server.Map("POST", "/api/companion/input", (_, request, _) => ExecuteInput(request));
        server.Map("POST", "/api/companion/file/register", (_, request, _) => RegisterFile(request));
        server.Map("GET", "/api/share", (_, _, _) => Json(_repository.GetFileState()));
        server.Map("POST", "/api/share/upload", (_, request, _) => UploadSharedFile(request));
        server.Map("POST", "/api/share/register-path", (_, request, _) => RegisterSharedPath(request));
        server.Map("GET", "/api/share/download", (_, request, _) => DownloadSharedFile(request));
    }

    private CompanionWorkspaceState GetWorkspaceState()
    {
        CompanionWorkspaceState state = _repository.GetWorkspaceState();
        state.Runner = _runner.GetStatus();
        return state;
    }

    private string SavePermissions(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        var permissions = new CompanionPermissions
        {
            HumanInteraction = ReadBool(root, "humanInteraction"),
            SpendMoney = ReadBool(root, "spendMoney"),
            AccountLogin = ReadBool(root, "accountLogin"),
            UseFiles = ReadBool(root, "useFiles"),
            PcSettings = ReadBool(root, "pcSettings"),
            InternetAccess = ReadBool(root, "internetAccess"),
            LiveInput = ReadBool(root, "liveInput")
        };
        return Json(new { ok = true, permissions = _repository.SavePermissions(permissions) });
    }

    private string DecideApproval(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string id = ReadString(root, "id", "");
        bool approved = ReadBool(root, "approved");
        string decidedBy = ReadString(root, "decidedBy", "web workspace");
        CompanionApprovalDecision decision = _repository.DecideApproval(id, approved, decidedBy);
        if (!decision.Ok && request?.Context != null)
            request.Context.StatusCodeNumber = 404;
        if (decision.Ok && approved)
            _runner.Start();
        return Json(new { ok = decision.Ok, decision, runner = _runner.GetStatus(), workspace = GetWorkspaceState() });
    }

    private string SaveTemplate(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        var template = new CompanionTemplate
        {
            Id = ReadString(root, "id", "JACK"),
            Name = ReadString(root, "name", "JACK"),
            CompanionName = ReadString(root, "companionName", "JACK"),
            Interests = ReadString(root, "interests", ""),
            TemplateText = ReadString(root, "templateText", "")
        };
        return Json(new { ok = true, template = _repository.SaveTemplate(template) });
    }

    private string StartRecording(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string title = ReadString(root, "title", "Companion work session");
        string note = ReadString(root, "note", "");
        CompanionSessionSummary session = _repository.StartRecording(title, note);
        _training.CaptureSessionKeyframe(session.Id, "recording_started");
        return Json(new { ok = true, session, workspace = GetWorkspaceState() });
    }

    private string StopRecording(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        string summary = ReadString(doc.RootElement, "summary", "Recording stopped from workspace.");
        string sessionId = _repository.StopRecording(summary);
        _training.CaptureSessionKeyframe(sessionId, "recording_stopped");
        CompanionLlmRunnerStatus runner = _runner.GetStatus();
        CompanionTrainingRun trainingRun = string.IsNullOrWhiteSpace(sessionId)
            ? new CompanionTrainingRun { Status = "idle", Summary = "No active recording was stopped." }
            : _training.StartTraining(sessionId, runner.ModelEndpoint, runner.Model);
        return Json(new { ok = true, trainingRun, workspace = GetWorkspaceState() });
    }

    private string RequestControlAction(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        string capability = ReadString(doc.RootElement, "capability", "");
        string detail = ReadString(doc.RootElement, "detail", "");
        CompanionControlDecision decision = _repository.RequestControlAction(capability, detail);
        return Json(new { ok = decision.Ok, decision });
    }

    private string SubmitLlmTask(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string goal = ReadString(root, "goal", "");
        string mode = ReadString(root, "mode", "assistive");
        string endpoint = ReadString(root, "modelEndpoint", "");
        string model = ReadString(root, "model", "");
        int maxSteps = ReadInt(root, "maxSteps", 0);
        if (!string.IsNullOrWhiteSpace(endpoint) || !string.IsNullOrWhiteSpace(model) || maxSteps > 0)
            _runner.Configure(endpoint, model, maxSteps);
        _runner.Start();
        CompanionLlmTask task = _repository.SubmitLlmTask(goal, mode);
        return Json(new { ok = true, task, runner = _runner.GetStatus(), workspace = GetWorkspaceState() });
    }

    private string StopLlmTask(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        string reason = ReadString(doc.RootElement, "reason", "Stopped from Companion workspace.");
        CompanionLlmTask task = _repository.StopLlmTask(reason);
        return Json(new { ok = true, task, runner = _runner.GetStatus(), workspace = GetWorkspaceState() });
    }

    private string DiscoverModels(HttpRequest request, CancellationToken cancellationToken)
    {
        CompanionLlmRunnerStatus runner = _runner.GetStatus();
        string endpoint = ReadQuery(request, "endpoint", runner.ModelEndpoint);
        string selected = ReadQuery(request, "selected", runner.Model);
        CompanionModelCatalogResult catalog = CompanionModelCatalog.DiscoverAsync(endpoint, selected, cancellationToken)
            .GetAwaiter()
            .GetResult();
        return Json(new { ok = catalog.Ok, catalog });
    }

    private string ConfigureRunner(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        _runner.Configure(
            ReadString(root, "modelEndpoint", ""),
            ReadString(root, "model", ""),
            ReadInt(root, "maxSteps", 0));
        return Json(new { ok = true, runner = _runner.GetStatus() });
    }

    private string StartRunner(HttpRequest request)
    {
        ConfigureRunner(request);
        _runner.Start();
        return Json(new { ok = true, runner = _runner.GetStatus() });
    }

    private string StopRunner()
    {
        _runner.Stop("Stopped from Companion workspace.");
        return Json(new { ok = true, runner = _runner.GetStatus() });
    }

    private string StartTraining(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string sessionId = ReadString(root, "sessionId", "");
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            CompanionWorkspaceState state = _repository.GetWorkspaceState();
            sessionId = state.IsRecording
                ? state.ActiveSessionId
                : state.Sessions.FirstOrDefault()?.Id ?? "";
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 400;
            return Json(new { ok = false, error = "No recording session is available for training." });
        }

        string endpoint = ReadString(root, "modelEndpoint", _runner.GetStatus().ModelEndpoint);
        string model = ReadString(root, "model", _runner.GetStatus().Model);
        CompanionTrainingRun run = _training.StartTraining(sessionId, endpoint, model);
        return Json(new { ok = true, trainingRun = run, training = _repository.GetTrainingState(), workspace = GetWorkspaceState() });
    }

    private string CancelTraining(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        string reason = ReadString(doc.RootElement, "reason", "Cancelled from Companion workspace.");
        CompanionTrainingRun run = _training.CancelActiveTraining(reason);
        return Json(new { ok = true, trainingRun = run, training = _repository.GetTrainingState() });
    }

    private string SaveTrainingSettings(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string approvalMode = ReadString(root, "approvalMode", "review_first");
        bool warningAccepted = ReadBool(root, "warningAccepted");
        if (approvalMode.Equals("enable_all", StringComparison.OrdinalIgnoreCase) && !warningAccepted)
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 400;
            return Json(new
            {
                ok = false,
                warningRequired = true,
                error = "Enable All learned skills is dangerous. Confirm the warning before saving this mode."
            });
        }

        var settings = new CompanionTrainingSettings
        {
            LearningEnabled = !HasProperty(root, "learningEnabled") || ReadBool(root, "learningEnabled"),
            ApprovalMode = approvalMode,
            ReplayMaxFrames = ReadInt(root, "replayMaxFrames", 300),
            ReplayMaxBytes = ReadLong(root, "replayMaxBytes", 262_144_000),
            CaptureProfile = ReadString(root, "captureProfile", "HybridMinimizedReplay")
        };
        return Json(new { ok = true, settings = _repository.SaveTrainingSettings(settings), training = _repository.GetTrainingState() });
    }

    private string ReviewSkillDraft(HttpRequest request)
    {
        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string draftId = ReadString(root, "draftId", ReadString(root, "id", ""));
        string action = ReadString(root, "action", "approve");
        bool warningAccepted = ReadBool(root, "warningAccepted");
        CompanionSkillReviewResult result = _repository.ReviewSkillDraft(draftId, action, warningAccepted);
        if (!result.Ok && request?.Context != null)
            request.Context.StatusCodeNumber = result.Message.Contains("Warning", StringComparison.OrdinalIgnoreCase) ? 400 : 404;
        return Json(new { ok = result.Ok, review = result, training = _repository.GetTrainingState(), workspace = GetWorkspaceState() });
    }

    private string ReplayIndex(HttpRequest request)
    {
        string runId = ReadQuery(request, "runId", "");
        string sessionId = ReadQuery(request, "sessionId", "");
        IEnumerable<CompanionTrainingEvidence> frames = _repository.GetTrainingState().Evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.KeyframePath));
        if (!string.IsNullOrWhiteSpace(runId))
            frames = frames.Where(item => string.Equals(item.RunId, runId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(sessionId))
            frames = frames.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        return Json(new { ok = true, frames = frames.ToList() });
    }

    private object ReplayFrame(HttpRequest request)
    {
        string id = ReadQuery(request, "id", "");
        if (string.IsNullOrWhiteSpace(id) || !_repository.TryGetTrainingKeyframe(id, out string path))
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 404;
            return Json(new { ok = false, error = "Replay keyframe was not found." });
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetFullPath(_repository.TrainingRoot);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            return Json(new { ok = false, error = "Replay keyframe is outside the Companion training store." });
        }

        return FileResponse.FromFile(full);
    }

    private string EmergencyStop()
    {
        CompanionEmergencyStopResult result = _repository.EmergencyStop("web workspace");
        _runner.Stop("Emergency stop from web workspace.");
        _runner.Start();
        return Json(new { ok = true, emergency = result, runner = _runner.GetStatus(), workspace = GetWorkspaceState() });
    }

    private object CaptureScreenFile(HttpRequest request)
    {
        if (!RequireLiveInput(request, "screen capture"))
            return Json(new { ok = false, approvalRequired = true, error = "Live input permission is required for screen capture." });

        DesktopScreenCapture capture = _desktop.CaptureScreen(ReadCaptureOptions(request, 1));
        _repository.RecordControlResult("screen", "Screen captured for Companion remote-control view.", true);
        return new FileResponse(capture.Bytes, capture.MimeType, capture.Encoding == "jpeg" ? "companion-screen.jpg" : "companion-screen.png");
    }

    private string CaptureScreenJson(HttpRequest request)
    {
        if (!RequireLiveInput(request, "screen capture"))
            return Json(new { ok = false, approvalRequired = true, error = "Live input permission is required for screen capture." });

        DesktopScreenCapture capture = _desktop.CaptureScreen(ReadCaptureOptions(request, 1));
        _repository.RecordControlResult("screen-json", "Screen captured for Companion JSON view.", true);
        return Json(BuildDesktopFrame(0, capture, "polling"));
    }

    private string ExecuteInput(HttpRequest request)
    {
        if (!RequireLiveInput(request, "desktop input"))
            return Json(new { ok = false, approvalRequired = true, error = "Live input permission is required for desktop input." });

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        var input = new DesktopInputRequest
        {
            Action = ReadString(root, "action", "move"),
            Button = ReadString(root, "button", "left"),
            X = ReadDouble(root, "x", 0),
            Y = ReadDouble(root, "y", 0),
            EndX = ReadDouble(root, "endX", ReadDouble(root, "x2", 0)),
            EndY = ReadDouble(root, "endY", ReadDouble(root, "y2", 0)),
            Normalized = ReadBool(root, "normalized"),
            Delta = ReadInt(root, "delta", 0),
            Text = ReadString(root, "text", ""),
            Key = ReadString(root, "key", "")
        };
        input.HasPoint = HasProperty(root, "x") || HasProperty(root, "y");
        input.HasEndPoint = HasProperty(root, "endX") || HasProperty(root, "endY") || HasProperty(root, "x2") || HasProperty(root, "y2");
        DesktopInputResult result = _desktop.ExecuteInput(input);
        _repository.RecordControlResult(input.Action, result.Message, result.Ok);
        return Json(new { ok = result.Ok, result });
    }

    private async Task StreamDesktop(HttpRequest request, ChunkedStream stream, CancellationToken cancellationToken)
    {
        stream.ContentType = "application/x-ndjson; charset=utf-8";
        if (!RequireLiveInput(request, "desktop stream"))
        {
            stream.WriteLine(Json(new { ok = false, approvalRequired = true, error = "Live input permission is required for desktop streaming." }));
            return;
        }

        int fps = Math.Max(1, Math.Min(4, ReadQueryInt(request, "fps", 1)));
        int maxFrames = Math.Max(1, Math.Min(600, ReadQueryInt(request, "frames", 240)));
        int delayMs = Math.Max(100, 1000 / fps);
        DesktopCaptureOptions captureOptions = ReadCaptureOptions(request, fps);
        _repository.RecordControlResult("desktop_stream", "Desktop stream started at " + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + " fps using " + captureOptions.Format + ".", true);

        for (int frame = 0; frame < maxFrames && !cancellationToken.IsCancellationRequested; frame++)
        {
            if (!_repository.CanUseLiveInput())
            {
                stream.WriteLine(Json(new { ok = false, stopped = true, error = "Live input permission was disabled." }));
                return;
            }

            DesktopScreenCapture capture = _desktop.CaptureScreen(captureOptions);
            stream.WriteLine(Json(BuildDesktopFrame(frame, capture, "chunked_ndjson")));
            AdaptCaptureOptions(captureOptions, capture.Bytes.Length);
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private string DesktopTransportInfo(HttpRequest request)
    {
        string wsUrl = "ws://localhost:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/api/companion/desktop/ws?fps=3&format=adaptive";
        return Json(new
        {
            ok = true,
            transports = new[]
            {
                new { id = "websocket", label = "WebSocket", url = wsUrl, requiresLiveInput = true },
                new { id = "chunked_ndjson", label = "Chunked NDJSON", url = "/api/companion/desktop/stream?fps=2&format=adaptive", requiresLiveInput = true },
                new { id = "polling", label = "Polling", url = "/api/companion/screen.json?format=adaptive", requiresLiveInput = true }
            },
            selected = ReadQuery(request, "prefer", "websocket")
        });
    }

    private object StreamDesktopWebSocket(NetworkConnection connection, HttpRequest request, CancellationToken cancellationToken)
    {
        if (!IsWebSocketRequest(request))
            return DesktopTransportInfo(request);

        request.Context.Response.RawResponseWritten = true;
        Stream? stream = connection.Stream;
        if (stream == null)
            return "";

        try { connection.SuppressConnectionTest = true; } catch { }
        WriteWebSocketHandshake(stream, request);
        if (!_repository.CanUseLiveInput())
        {
            _repository.RecordControlResult("desktop_websocket", "WebSocket desktop stream blocked because Live Input is disabled.", false);
            WriteWebSocketTextFrame(stream, Json(new { ok = false, approvalRequired = true, error = "Live input permission is required for desktop streaming." }));
            return "";
        }

        int fps = Math.Max(1, Math.Min(6, ReadQueryInt(request, "fps", 3)));
        int maxFrames = Math.Max(1, Math.Min(1800, ReadQueryInt(request, "frames", 900)));
        int delayMs = Math.Max(75, 1000 / fps);
        DesktopCaptureOptions captureOptions = ReadCaptureOptions(request, fps);
        _repository.RecordControlResult("desktop_websocket", "WebSocket desktop stream started at " + fps.ToString(System.Globalization.CultureInfo.InvariantCulture) + " fps using " + captureOptions.Format + ".", true);

        try
        {
            for (int frame = 0; frame < maxFrames && !cancellationToken.IsCancellationRequested; frame++)
            {
                if (!_repository.CanUseLiveInput())
                {
                    WriteWebSocketTextFrame(stream, Json(new { ok = false, stopped = true, error = "Live input permission was disabled." }));
                    break;
                }

                DesktopScreenCapture capture = _desktop.CaptureScreen(captureOptions);
                WriteWebSocketTextFrame(stream, Json(BuildDesktopFrame(frame, capture, "websocket")));
                AdaptCaptureOptions(captureOptions, capture.Bytes.Length);
                Task.Delay(delayMs, cancellationToken).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }

        return "";
    }

    private string Processes(HttpRequest request)
    {
        CompanionProcessQuery query = ReadProcessQuery(request, defaultSort: "cpu");
        CompanionProcessSnapshot snapshot = _processes.GetProcessSnapshot(query);
        return Json(new { ok = true, snapshot });
    }

    private string Windows(HttpRequest request)
    {
        CompanionProcessQuery query = ReadProcessQuery(request, defaultSort: "title");
        CompanionWindowSnapshot snapshot = _processes.GetWindowSnapshot(query);
        return Json(new { ok = true, snapshot });
    }

    private string ProcessBrowser(HttpRequest request)
    {
        string path = ReadQuery(request, "path", "");
        bool executableOnly = ReadQueryBool(request, "executableOnly", false);
        CompanionProcessBrowserSnapshot browser = _processes.BrowseFileSystem(path, executableOnly);
        return Json(new { ok = string.IsNullOrWhiteSpace(browser.Error), browser });
    }

    private string KillProcess(HttpRequest request)
    {
        if (!_repository.CanUsePcSettings())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("process_kill", "Process kill blocked because PC Settings is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "PC Settings permission is required before killing processes." });
        }

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        int pid = ReadInt(root, "pid", 0);
        bool entireTree = !HasProperty(root, "entireTree") || ReadBool(root, "entireTree");
        CompanionProcessMutationResult result = _processes.KillProcess(pid, entireTree);
        if (!result.Ok && request?.Context != null)
            request.Context.StatusCodeNumber = 400;
        _repository.RecordControlResult("process_kill", result.Message, result.Ok);
        return Json(new { ok = result.Ok, result, snapshot = _processes.GetProcessSnapshot(new CompanionProcessQuery { Take = 100, Sort = "cpu" }) });
    }

    private string StartProcess(HttpRequest request)
    {
        if (!_repository.CanUsePcSettings())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("process_start", "Process start blocked because PC Settings is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "PC Settings permission is required before starting processes." });
        }

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        var startRequest = new CompanionProcessStartRequest
        {
            Path = ReadString(root, "path", ""),
            Arguments = ReadString(root, "arguments", ""),
            WorkingDirectory = ReadString(root, "workingDirectory", "")
        };
        CompanionProcessMutationResult result = _processes.StartProcess(startRequest);
        if (!result.Ok && request?.Context != null)
            request.Context.StatusCodeNumber = 400;
        _repository.RecordControlResult("process_start", result.Message, result.Ok);
        return Json(new { ok = result.Ok, result, snapshot = _processes.GetProcessSnapshot(new CompanionProcessQuery { Take = 100, Sort = "cpu" }) });
    }

    private static object BuildDesktopFrame(int frame, DesktopScreenCapture capture, string transport)
    {
        string base64 = Convert.ToBase64String(capture.Bytes);
        return new
        {
            ok = true,
            frame,
            transport,
            capturedUtc = DateTimeOffset.UtcNow.ToString("O"),
            mimeType = capture.MimeType,
            encoding = capture.Encoding,
            quality = capture.Quality,
            adaptive = captureOptionsAdaptive(capture.Encoding),
            byteLength = capture.Bytes.Length,
            width = capture.Width,
            height = capture.Height,
            left = capture.Left,
            top = capture.Top,
            cursor = capture.Cursor,
            imageBase64 = base64,
            dataUrl = "data:" + capture.MimeType + ";base64," + base64
        };
    }

    private string RegisterFile(HttpRequest request)
    {
        if (!_repository.CanUseFiles())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("file_register", "File register blocked because Use Files is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "Use Files permission is required before registering files." });
        }

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string sessionId = ReadString(root, "sessionId", "");
        string path = ReadString(root, "path", "");
        string kind = ReadString(root, "kind", "file");
        string note = ReadString(root, "note", "Registered from Companion HTTP API.");
        _repository.RegisterFile(sessionId, path, kind, note);
        return Json(new { ok = true, files = _repository.GetFileState() });
    }

    private string UploadSharedFile(HttpRequest request)
    {
        if (!_repository.CanUseFiles())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("file_upload", "File upload blocked because Use Files is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "Use Files permission is required before sharing files." });
        }

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string fileName = ReadString(root, "fileName", "companion-upload.bin");
        string relativePath = ReadString(root, "relativePath", "");
        string base64Data = ReadString(root, "base64", "");
        string note = ReadString(root, "note", "Uploaded from /Workspace file sharing.");
        bool approved = ReadBool(root, "approved");
        try
        {
            CompanionSharedFile shared = _repository.SaveSharedUpload(fileName, base64Data, note, relativePath, approved);
            return Json(new
            {
                ok = !string.IsNullOrWhiteSpace(shared.Id),
                fileApprovalRequired = shared.RequiresApproval,
                shared,
                files = _repository.GetFileState()
            });
        }
        catch (Exception ex)
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 400;
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private string RegisterSharedPath(HttpRequest request)
    {
        if (!_repository.CanUseFiles())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("file_path_share", "Path share blocked because Use Files is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "Use Files permission is required before sharing files or folders." });
        }

        using JsonDocument doc = ParseBody(request);
        JsonElement root = doc.RootElement;
        string path = ReadString(root, "path", "");
        string note = ReadString(root, "note", "Shared from /Workspace path sharing.");
        bool approved = ReadBool(root, "approved");
        try
        {
            List<CompanionSharedFile> shared = _repository.ShareExistingPath(path, note, approved);
            bool needsApproval = shared.Any(item => item.RequiresApproval);
            return Json(new
            {
                ok = shared.Count > 0 && shared.All(item => !string.IsNullOrWhiteSpace(item.Id)),
                fileApprovalRequired = needsApproval,
                shared,
                files = _repository.GetFileState()
            });
        }
        catch (Exception ex)
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 400;
            return Json(new { ok = false, error = ex.Message });
        }
    }

    private object DownloadSharedFile(HttpRequest request)
    {
        if (!_repository.CanUseFiles())
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            _repository.RecordControlResult("file_download", "File download blocked because Use Files is disabled.", false);
            return Json(new { ok = false, approvalRequired = true, error = "Use Files permission is required before downloading shared files." });
        }

        string id = ReadQuery(request, "id", "");
        if (string.IsNullOrWhiteSpace(id) || !_repository.TryGetSharedFile(id, out CompanionSharedFile shared))
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 404;
            return Json(new { ok = false, error = "Shared file was not found." });
        }

        _repository.RegisterFile("", shared.Path, "shared-download", "Downloaded from Companion file sharing.");
        return FileResponse.FromFile(shared.Path);
    }

    private bool RequireLiveInput(HttpRequest request, string detail)
    {
        if (_repository.CanUseLiveInput())
            return true;
        if (request?.Context != null)
            request.Context.StatusCodeNumber = 403;
        _repository.RecordControlResult("approval_required", detail + " blocked because Live Input is disabled.", false);
        return false;
    }

    private static DesktopCaptureOptions ReadCaptureOptions(HttpRequest request, int fps)
    {
        string format = ReadQuery(request, "format", "adaptive").Trim().ToLowerInvariant();
        bool adaptive = format is "adaptive" or "auto" or "";
        if (format is "adaptive" or "auto" or "")
            format = fps > 1 ? "jpeg" : "png";
        if (format is not ("png" or "jpg" or "jpeg"))
            format = "png";

        int quality = Math.Max(35, Math.Min(95, ReadQueryInt(request, "quality", fps > 1 ? 68 : 82)));
        return new DesktopCaptureOptions
        {
            Format = format,
            Quality = quality,
            Adaptive = adaptive
        };
    }

    private static void AdaptCaptureOptions(DesktopCaptureOptions options, int byteLength)
    {
        if (options == null || !options.Adaptive || (options.Format ?? "").Equals("png", StringComparison.OrdinalIgnoreCase))
            return;

        const int targetBytes = 450 * 1024;
        if (byteLength > targetBytes && options.Quality > 42)
            options.Quality = Math.Max(42, options.Quality - 6);
        else if (byteLength < targetBytes / 3 && options.Quality < 82)
            options.Quality = Math.Min(82, options.Quality + 4);
    }

    private static bool captureOptionsAdaptive(string encoding)
    {
        return string.Equals(encoding, "jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebSocketRequest(HttpRequest request)
    {
        return request.Headers.TryGetValue("Upgrade", out string? upgrade) &&
               upgrade.Equals("websocket", StringComparison.OrdinalIgnoreCase) &&
               request.Headers.ContainsKey("Sec-WebSocket-Key");
    }

    private static void WriteWebSocketHandshake(Stream stream, HttpRequest request)
    {
        string key = request.Headers.TryGetValue("Sec-WebSocket-Key", out string? value) ? value : "";
        using SHA1 sha1 = SHA1.Create();
        string accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key.Trim() + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        byte[] bytes = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: " + accept + "\r\n" +
            "Cache-Control: no-store\r\n" +
            "\r\n");
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static void WriteWebSocketTextFrame(Stream stream, string text)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text ?? "");
        using var frame = new MemoryStream();
        frame.WriteByte(0x81);
        if (payload.Length <= 125)
        {
            frame.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            frame.WriteByte(126);
            frame.WriteByte((byte)((payload.Length >> 8) & 0xff));
            frame.WriteByte((byte)(payload.Length & 0xff));
        }
        else
        {
            frame.WriteByte(127);
            ulong length = (ulong)payload.Length;
            for (int i = 7; i >= 0; i--)
                frame.WriteByte((byte)((length >> (8 * i)) & 0xff));
        }

        frame.Write(payload, 0, payload.Length);
        byte[] bytes = frame.ToArray();
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private string RequestGate(NetworkConnection connection, HttpRequest request)
    {
        string localOnly = LocalOnlyGate(connection, request);
        if (!string.IsNullOrWhiteSpace(localOnly))
            return localOnly;

        if (request?.Path?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true && !IsAuthenticated(request))
        {
            if (request?.Context != null)
                request.Context.StatusCodeNumber = 403;
            return "SocketJack Companion API token is required.";
        }

        return "";
    }

    private bool IsAuthenticated(HttpRequest request)
    {
        string token = "";
        if (request?.Headers != null)
        {
            if (request.Headers.TryGetValue("X-JackLLM-Companion-Token", out string? headerToken))
                token = headerToken ?? "";
            else if (request.Headers.TryGetValue("Authorization", out string? auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                token = auth.Substring("Bearer ".Length).Trim();
        }

        if (string.IsNullOrWhiteSpace(token) && request?.QueryParameters != null)
            token = request.QueryParameters.TryGetValue("token", out string? queryToken) ? queryToken ?? "" : "";

        return FixedTimeEquals(token, _authToken);
    }

    private static bool FixedTimeEquals(string supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected))
            return false;

        byte[] left = Encoding.UTF8.GetBytes(supplied);
        byte[] right = Encoding.UTF8.GetBytes(expected);
        try
        {
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(left);
            CryptographicOperations.ZeroMemory(right);
        }
    }

    private static string CreateAuthToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string LocalOnlyGate(NetworkConnection connection, HttpRequest request)
    {
        try
        {
            if (connection?.Socket?.RemoteEndPoint is IPEndPoint endpoint && IPAddress.IsLoopback(endpoint.Address))
                return "";
        }
        catch
        {
        }

        if (request?.Context != null)
            request.Context.StatusCodeNumber = 403;
        return "SocketJack Companion only accepts local loopback requests in v1.";
    }

    private string Json(object value)
    {
        return JsonSerializer.Serialize(value, _jsonOptions);
    }

    private static JsonDocument ParseBody(HttpRequest request)
    {
        string body = request?.Body ?? "{}";
        if (string.IsNullOrWhiteSpace(body))
            body = "{}";
        return JsonDocument.Parse(body);
    }

    private static bool ReadBool(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return false;
        if (value.ValueKind == JsonValueKind.True)
            return true;
        if (value.ValueKind == JsonValueKind.False)
            return false;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool parsed))
            return parsed;
        return false;
    }

    private static string ReadString(JsonElement root, string name, string fallback)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value))
        {
            if (value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? fallback;
            return value.ToString();
        }
        return fallback;
    }

    private static string ReadQuery(HttpRequest request, string name, string fallback)
    {
        if (request?.QueryParameters != null && request.QueryParameters.TryGetValue(name, out string? value))
            return value ?? fallback;
        return fallback;
    }

    private static int ReadQueryInt(HttpRequest request, string name, int fallback)
    {
        string value = ReadQuery(request, name, "");
        return int.TryParse(value, out int result) ? result : fallback;
    }

    private static bool ReadQueryBool(HttpRequest request, string name, bool fallback)
    {
        string value = ReadQuery(request, name, "");
        return string.IsNullOrWhiteSpace(value) ? fallback : bool.TryParse(value, out bool result) ? result : fallback;
    }

    private static CompanionProcessQuery ReadProcessQuery(HttpRequest request, string defaultSort)
    {
        return new CompanionProcessQuery
        {
            Query = ReadQuery(request, "query", ReadQuery(request, "q", "")),
            WindowedOnly = ReadQueryBool(request, "windowedOnly", false),
            IncludeSystem = ReadQueryBool(request, "includeSystem", true),
            Take = Math.Max(1, Math.Min(1000, ReadQueryInt(request, "take", ReadQueryInt(request, "limit", 200)))),
            Sort = ReadQuery(request, "sort", defaultSort)
        };
    }

    private static double ReadDouble(JsonElement root, string name, double fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
            return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
            return number;
        return fallback;
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
            return number;
        return fallback;
    }

    private static long ReadLong(JsonElement root, string name, long fallback)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number))
            return number;
        if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
            return number;
        return fallback;
    }

    private static bool HasProperty(JsonElement root, string name)
    {
        return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out _);
    }

    private string WorkspaceHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SocketJack Companion Workspace</title>
<style>
:root{color-scheme:dark;--bg:#080b10;--panel:#1a2230d9;--panel2:#111924cc;--line:#334e5e74;--text:#f1f7ff;--muted:#9fb1c8;--accent:#59d6c9;--warn:#ffb35a;--bad:#ff5c7a;--ok:#6ff2a6}
*{box-sizing:border-box}body{margin:0;min-height:100vh;background:linear-gradient(135deg,#080b10,#111827 48%,#072021);color:var(--text);font:14px/1.45 "Segoe UI",system-ui,sans-serif}
header{display:flex;align-items:flex-start;justify-content:space-between;gap:16px;padding:18px 22px;border-bottom:1px solid var(--line);background:#111924aa;backdrop-filter:blur(12px)}
h1{margin:0;font-size:24px;font-weight:650}h2{margin:0 0 10px;font-size:16px}h3{margin:14px 0 8px;font-size:13px;color:var(--muted)}a{color:#8bd7ff}.muted{color:var(--muted)}
.actions{display:flex;gap:8px;flex-wrap:wrap;justify-content:flex-end}.tabs{display:flex;gap:8px;flex-wrap:wrap;padding:12px 14px 0}.tab{border-color:#334e5e74}.tab.active{background:linear-gradient(#264f63,#1a8a82);border-color:#59d6c9;color:white}
main{padding:14px}.view{display:none}.view.active{display:block}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}.two{display:grid;grid-template-columns:minmax(0,1.45fr) minmax(320px,.55fr);gap:12px}
section,.card{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px;min-width:0}.card{background:var(--panel2)}
.row{display:flex;align-items:center;justify-content:space-between;gap:10px;border-top:1px solid #293b4d;padding:8px 0}.row:first-child{border-top:0}
button{background:#aa263448;color:var(--text);border:1px solid #554e6a8b;border-radius:7px;padding:8px 12px;cursor:pointer;font-weight:650}button:hover{background:#cc324761;border-color:#7dd3fc}button.danger{background:#893442;border-color:#ff5c7a}button.good{background:#1f7a68;border-color:#59d6c9}
input,textarea,select{width:100%;background:#99111924;color:var(--text);border:1px solid #554e6a8b;border-radius:7px;padding:8px;margin:5px 0 10px}textarea{min-height:120px;resize:vertical}
.pill{display:inline-flex;border:1px solid #446075;border-radius:999px;padding:2px 8px;color:var(--muted);font-size:12px}.on{color:var(--ok);border-color:#1f8a4e}.off{color:var(--warn);border-color:#8a6b1f}.bad{color:var(--bad);border-color:#893442}
.approval-banner{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:12px;background:#221b11;border:1px solid #8a6b1f;border-radius:8px;padding:10px 12px}.approval-card{background:#221b11;border:1px solid #8a6b1f;border-radius:8px;padding:12px;margin-bottom:10px}.approval-actions{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}.approval-detail{margin-top:4px;color:var(--muted);overflow-wrap:anywhere}
.remote-shell{background:#05080c;border:1px solid #334e5e74;border-radius:8px;overflow:hidden;min-height:320px;display:grid;place-items:center}.screen-frame{position:relative;width:100%}.remote-shell img{width:100%;height:auto;display:block;cursor:crosshair;image-rendering:auto}.cursor-dot{position:absolute;width:16px;height:16px;margin:-8px 0 0 -8px;border:2px solid #fff;border-radius:999px;box-shadow:0 0 0 2px #111,0 0 16px #59d6c9;pointer-events:none}.remote-empty{padding:40px;color:var(--muted);text-align:center}.screen-frame.loading img{opacity:.86}
.plan-list{counter-reset:step}.plan-list div{position:relative;padding:9px 10px 9px 34px;border-top:1px solid #293b4d}.plan-list div:before{counter-increment:step;content:counter(step);position:absolute;left:0;top:9px;width:22px;height:22px;border-radius:999px;background:#263448;display:grid;place-items:center;color:var(--accent);font-size:12px;font-weight:700}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:8px;border-bottom:1px solid #293b4d;vertical-align:top}th{color:var(--muted);font-size:12px}
.process-row .process-actions{visibility:hidden;opacity:0;pointer-events:none;transition:opacity .12s}.process-row:hover .process-actions{visibility:visible;opacity:1;pointer-events:auto}.process-browser{max-height:260px;overflow:auto;border:1px solid #293b4d;border-radius:7px;margin-top:8px}.process-browser button{width:100%;text-align:left;background:transparent;border:0;border-radius:0;border-bottom:1px solid #293b4d;font-weight:500}.process-browser button:hover{background:#263448}
@media(max-width:900px){header{display:block}.actions{justify-content:flex-start;margin-top:12px}.two{grid-template-columns:1fr}}
</style>
</head>
<body>
<header>
  <div>
    <h1>SocketJack Companion Workspace</h1>
    <div class="muted" id="status">Loading workspace...</div>
  </div>
  <div class="actions">
    <button id="recordBtn">Start Recording</button>
    <button class="danger" onclick="emergencyStop()">Emergency Stop</button>
    <button onclick="location.href='/file'">Files</button>
    <button onclick="refresh()">Refresh</button>
  </div>
</header>
<nav class="tabs">
  <button class="tab active" data-tab="plan">Plan</button>
  <button class="tab" data-tab="approvals">Approvals</button>
  <button class="tab" data-tab="llm">LLM Control</button>
  <button class="tab" data-tab="remote">Remote Desktop</button>
  <button class="tab" data-tab="files">File Sharing</button>
  <button class="tab" data-tab="training">Training</button>
  <button class="tab" data-tab="processes">Processes</button>
  <button class="tab" data-tab="memory">Memory</button>
  <button class="tab" data-tab="audit">Audit</button>
</nav>
<main>
  <div id="approvalBanner" class="approval-banner" hidden></div>
  <div id="plan" class="view active">
    <div class="two">
      <section>
        <h2>Web UI Implementation Plan</h2>
        <div class="plan-list">
          <div><b>Command center:</b> keep recording, current projects, active LLM task, approval gates, and emergency stop visible at all times.</div>
          <div><b>LLM control loop:</b> accept a user goal, build a proposed plan, stream model/tool decisions, and execute only actions allowed by the permission gates.</div>
          <div><b>Remote desktop:</b> replace one-shot screenshots with a live desktop frame loop, click-to-control coordinates, keyboard entry, wheel/drag support, and visible blocked/approved state.</div>
          <div><b>File sharing:</b> upload files into the Companion share, download shared files, associate files with work sessions, and later add drag/drop plus per-file approval prompts.</div>
          <div><b>Memory and skills:</b> show learned skills, people/app context, confidence, evidence, and session links so the LLM can reuse relevant workflows.</div>
          <div><b>Safety layer:</b> make Ctrl+Esc and the web emergency button stop live input, queued LLM tasks, and active recording immediately.</div>
        </div>
      </section>
      <section>
        <h2>Approval Gates</h2>
        <div id="permissions"></div>
        <h3>Pending Approvals</h3>
        <div id="pendingApprovalsPlan"></div>
      </section>
    </div>
    <section style="margin-top:12px">
      <h2>Current Projects</h2>
      <div id="projects" class="grid"></div>
    </section>
  </div>

  <div id="approvals" class="view">
    <div class="two">
      <section>
        <h2>Pending Approvals</h2>
        <div id="pendingApprovals"></div>
      </section>
      <section>
        <h2>Approval Gates</h2>
        <div id="approvalGateSummary"></div>
      </section>
    </div>
  </div>

  <div id="llm" class="view">
    <div class="two">
      <section>
        <h2>LLM Desktop Task</h2>
        <div class="muted">This queues a Companion LLM task. The runner observes the desktop, asks the configured model for one small JSON action at a time, and executes only approved actions.</div>
        <label>Goal for JACK</label>
        <textarea id="llmGoal" placeholder="Example: open Spotify, play focus music, then help me organize this project folder."></textarea>
        <label>Control mode</label>
        <select id="llmMode"><option value="assistive">Assistive, ask before action</option><option value="guided">Guided remote desktop</option><option value="record-only">Observe and learn only</option></select>
        <label>Model endpoint</label>
        <input id="runnerEndpoint" value="http://localhost:11435/v1/chat/completions">
        <div class="grid">
          <label>Model<select id="runnerModel"></select></label>
          <label>Max steps<input id="runnerMaxSteps" value="8"></label>
        </div>
        <div class="actions" style="justify-content:flex-start">
          <button onclick="refreshModels()">Refresh Models</button>
          <span id="modelListStatus" class="muted"></span>
        </div>
        <div class="actions" style="justify-content:flex-start">
          <button onclick="startRunner()">Start Runner</button>
          <button onclick="stopRunner()">Stop Runner</button>
          <button class="good" onclick="submitLlmTask()">Queue LLM Task</button>
          <button onclick="stopLlmTask()">Stop LLM Task</button>
        </div>
        <div id="llmStatus" class="muted"></div>
      </section>
      <section>
        <h2>Task Queue</h2>
        <div id="llmTasks"></div>
      </section>
    </div>
  </div>

  <div id="remote" class="view">
    <div class="two">
      <section>
        <h2>Remote Desktop</h2>
        <div class="remote-shell" id="remoteShell"><div class="remote-empty">Live Input approval is required, then capture or start live view.</div></div>
        <div class="actions" style="justify-content:flex-start;margin-top:10px">
          <button onclick="captureScreen()">Capture Frame</button>
          <button id="liveBtn" onclick="toggleLiveDesktop()">Start Live View</button>
          <button onclick="sendInput({action:'click',button:'left',x:.5,y:.5,normalized:true})">Click Center</button>
          <button onclick="sendInput({action:'key',key:'escape'})">Esc</button>
          <button class="danger" onclick="emergencyStop()">Emergency Stop</button>
        </div>
        <div id="controlStatus" class="muted" style="margin-top:8px"></div>
      </section>
      <section>
        <h2>Input</h2>
        <label>Live transport</label>
        <select id="desktopTransport"><option value="websocket">WebSocket</option><option value="chunked">Chunked stream</option><option value="polling">Polling</option></select>
        <label>Frame encoding</label>
        <select id="desktopFormat"><option value="adaptive">Adaptive</option><option value="jpeg">JPEG</option><option value="png">PNG</option></select>
        <label>Text to type</label>
        <textarea id="typeText"></textarea>
        <button onclick="sendInput({action:'type',text:document.getElementById('typeText').value})">Type Text</button>
        <h3>Desktop gestures</h3>
        <div class="grid">
          <button onclick="sendInput({action:'key',key:'tab'})">Tab</button>
          <button onclick="sendInput({action:'key',key:'enter'})">Enter</button>
          <button onclick="sendInput({action:'key',key:'backspace'})">Backspace</button>
          <button onclick="sendInput({action:'wheel',delta:120})">Wheel Up</button>
          <button onclick="sendInput({action:'wheel',delta:-120})">Wheel Down</button>
        </div>
        <h3>Remote desktop v2 notes</h3>
        <div class="muted">Live view supports WebSocket, chunked NDJSON, and polling fallback. Frames include cursor echo and adaptive JPEG/PNG encoding.</div>
      </section>
    </div>
  </div>

  <div id="files" class="view">
    <div class="two">
      <section>
        <h2>File Sharing</h2>
        <input id="shareUpload" type="file" multiple>
        <input id="shareFolder" type="file" webkitdirectory multiple>
        <input id="sharePath" placeholder="Optional local file or folder path on this PC">
        <input id="shareNote" placeholder="Note for this shared file">
        <button onclick="uploadShare()">Upload To Companion Share</button>
        <button onclick="shareLocalPath()">Share Local Path</button>
        <div id="dropZone" class="card" style="border-style:dashed;margin-top:8px">Drop files or folders here</div>
        <div id="shareStatus" class="muted"></div>
      </section>
      <section>
        <h2>Shared Files</h2>
        <div id="sharedFiles"></div>
      </section>
    </div>
    <section style="margin-top:12px">
      <h2>Session Files</h2>
      <div id="sessionFiles"></div>
    </section>
  </div>

  <div id="training" class="view">
    <div class="two">
      <section>
        <h2>Self-Training</h2>
        <div class="muted">Recordings become evidence packs, evidence packs become draft skills, and only enabled skills are reused by JACK.</div>
        <div class="grid">
          <label>Learning enabled<select id="trainingLearning"><option value="true">Enabled</option><option value="false">Disabled</option></select></label>
          <label>Approval mode<select id="trainingApprovalMode"><option value="review_first">Review first</option><option value="auto_enable_low_risk">Auto-enable low risk</option><option value="enable_all">Enable all learned skills</option></select></label>
          <label>Max replay frames<input id="trainingMaxFrames" value="300"></label>
          <label>Max replay MB<input id="trainingMaxMb" value="250"></label>
        </div>
        <div class="actions" style="justify-content:flex-start">
          <button onclick="saveTrainingSettings()">Save Training Settings</button>
          <button class="good" onclick="startTraining()">Train From Latest Session</button>
          <button onclick="cancelTraining()">Cancel Training</button>
        </div>
        <div id="trainingStatus" class="muted"></div>
      </section>
      <section>
        <h2>Training Runs</h2>
        <div id="trainingRuns"></div>
      </section>
    </div>
    <div class="two" style="margin-top:12px">
      <section>
        <h2>Draft Skills</h2>
        <div id="skillDrafts"></div>
      </section>
      <section>
        <h2>Replay Evidence</h2>
        <div id="trainingEvidence"></div>
      </section>
    </div>
  </div>

  <div id="processes" class="view">
    <div class="two">
      <section>
        <h2>Running Processes</h2>
        <div class="grid">
          <label>Filter<input id="processQuery" placeholder="name, PID, path, or window title"></label>
          <label>Rows<select id="processTake"><option>50</option><option selected>100</option><option>200</option></select></label>
          <label>View<select id="processWindowed"><option value="false">All processes</option><option value="true">Windowed only</option></select></label>
        </div>
        <div class="actions" style="justify-content:flex-start">
          <button onclick="refreshProcesses()">Refresh Processes</button>
        </div>
        <div id="processStatus" class="muted"></div>
        <div id="processRows"></div>
      </section>
      <section>
        <h2>Start Process</h2>
        <label>Selected file or folder<input id="processStartPath" placeholder="Choose a file from the browser below"></label>
        <label>Arguments<input id="processStartArgs" placeholder="Optional command-line arguments"></label>
        <div class="actions" style="justify-content:flex-start">
          <button class="good" onclick="startProcessFromWeb()">Start</button>
          <button onclick="browseProcessFiles(document.getElementById('processStartPath').value)">Browse</button>
          <button onclick="browseProcessFiles(processBrowserParent)">Parent</button>
          <button onclick="browseProcessFiles('')">Drives</button>
        </div>
        <div id="processStartStatus" class="muted"></div>
        <div id="processBrowser" class="process-browser"></div>
      </section>
    </div>
  </div>

  <div id="memory" class="view">
    <div class="grid">
      <section><h2>Work Sessions</h2><div id="sessions"></div></section>
      <section><h2>Recent Events</h2><div id="events"></div></section>
      <section><h2>JACK Template</h2>
        <label>Name</label><input id="companionName"><button onclick="wandName()">AI Name</button>
        <label>Hobbies and interests</label><textarea id="interests"></textarea><button onclick="wandInterests()">AI Interests</button>
        <label>Template text</label><textarea id="templateText"></textarea><button onclick="saveTemplate()">Save Template</button>
      </section>
      <section><div id="skills"></div><div id="people"></div></section>
    </div>
  </div>

  <div id="audit" class="view">
    <section><h2>Audit Events</h2><div id="auditRows"></div></section>
  </div>
</main>
<script>
const companionToken = 'AUTH_TOKEN_PLACEHOLDER';
const nativeFetch = window.fetch.bind(window);
window.fetch = (input, init = {}) => {
  const rawUrl = typeof input === 'string' ? input : (input && input.url) || '';
  const url = String(rawUrl || '');
  if (url.startsWith('/api/') || url.startsWith(location.origin + '/api/')) {
    init = Object.assign({}, init);
    init.headers = Object.assign({'X-JackLLM-Companion-Token': companionToken}, init.headers || {});
  }
  return nativeFetch(input, init);
};
let state=null, fileState=null, modelCatalog=null, runnerModelDirty=false, liveDesktop=false, liveTimer=null, desktopAbort=null, dragStart=null, processState=null, processBrowserParent='';
const $=id=>document.getElementById(id);
function esc(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
async function post(url,payload={}){const r=await fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(payload)});return await r.json();}
function approvalCards(items,compact=false){
  if(!items.length)return '<div class="muted">No pending approvals.</div>';
  return items.map(a=>`<div class="approval-card"><div class="row"><span><b>${esc(a.title||'Approval required')}</b><br><span class="muted">${esc(a.source||a.kind||'Companion')} | ${esc(a.capability||'capability')}</span></span><span class="pill off">${esc(a.recommendedAction||'Approve')}</span></div><div class="approval-detail">${esc(a.detail||'Review this request before continuing.')}</div>${compact?'':`<div class="approval-actions"><button class="good" data-approval-id="${esc(a.id)}" data-approval-action="approve">Approve</button><button data-approval-id="${esc(a.id)}" data-approval-action="deny">Deny</button></div>`}</div>`).join('');
}
function bindApprovalButtons(){
  document.querySelectorAll('[data-approval-action]').forEach(btn=>btn.onclick=()=>decideApproval(btn.dataset.approvalId||'',btn.dataset.approvalAction==='approve'));
}
async function decideApproval(id,approved){
  const data=await post('/api/companion/approvals/decide',{id,approved,decidedBy:'web workspace'});
  $('status').textContent=data.decision?.message||data.error||'Approval updated.';
  if(data.workspace){state=data.workspace; render();} else await refresh();
}
document.querySelectorAll('.tab').forEach(btn=>btn.onclick=()=>{document.querySelectorAll('.tab,.view').forEach(x=>x.classList.remove('active'));btn.classList.add('active');$(btn.dataset.tab).classList.add('active');if(btn.dataset.tab==='processes'){refreshProcesses();if(!$('processBrowser').innerHTML)browseProcessFiles($('processStartPath').value);}});
async function refresh(){
  const [w,f]=await Promise.all([fetch('/api/workspace',{cache:'no-store'}),fetch('/api/share',{cache:'no-store'})]);
  state=await w.json(); fileState=await f.json(); render();
}
async function refreshProcesses(){
  const params=new URLSearchParams({query:$('processQuery')?.value||'',windowedOnly:$('processWindowed')?.value||'false',take:$('processTake')?.value||'100',sort:'cpu'});
  const data=await fetch('/api/companion/processes?'+params.toString(),{cache:'no-store'}).then(r=>r.json());
  processState=data.snapshot||{};
  renderProcesses();
}
function renderProcesses(){
  const s=processState||{}; const rows=s.processes||[];
  $('processStatus').textContent=`Showing ${rows.length} of ${s.filteredCount||0} filtered processes | total ${s.totalCount||0} | RAM ${s.totalRamGb||0} GB | GPU ${s.gpuAvailable?'available':('unavailable: '+(s.gpuUnavailableReason||''))}`;
  $('processRows').innerHTML='<table><thead><tr><th>Name</th><th>PID</th><th>CPU</th><th>GPU</th><th>RAM</th><th>Admin</th><th>Window</th><th>Path</th><th></th></tr></thead><tbody>'+rows.map(p=>`<tr class="process-row"><td>${esc(p.name)}</td><td>${esc(p.pid)}</td><td>${esc(p.cpuPercentDisplay)}</td><td>${esc(p.gpuPercentDisplay)}</td><td>${esc(p.ramGbDisplay)}<br><span class="muted">${esc(p.ramPercentDisplay)}</span></td><td>${esc(p.adminState)}</td><td>${esc(p.windowSummary||'')}</td><td>${esc(p.executablePath||p.unavailableReason||'')}</td><td><span class="process-actions"><button class="danger" onclick="killProcessFromWeb(${Number(p.pid)||0},'')">Kill</button></span></td></tr>`).join('')+'</tbody></table>';
}
async function killProcessFromWeb(pid,name){
  if(!pid)return;
  if(!confirm(`Kill ${name||'process'} (PID ${pid})? Unsaved work may be lost.`))return;
  const data=await post('/api/companion/processes/kill',{pid,entireTree:true});
  $('processStatus').textContent=data.result?.message||data.error||'Kill request completed.';
  await refreshProcesses();
}
async function startProcessFromWeb(){
  const payload={path:$('processStartPath').value,arguments:$('processStartArgs').value};
  const data=await post('/api/companion/processes/start',payload);
  $('processStartStatus').textContent=data.result?.message||data.error||'Start request completed.';
  await refreshProcesses();
}
async function browseProcessFiles(path){
  const data=await fetch('/api/companion/process-browser?path='+encodeURIComponent(path||'')+'&executableOnly=false',{cache:'no-store'}).then(r=>r.json());
  const b=data.browser||{}; processBrowserParent=b.parentPath||'';
  $('processStartStatus').textContent=b.error?('Browse failed: '+b.error):`Browsing ${b.path||'drives'} | ${b.count||0} entries`;
  $('processBrowser').innerHTML=(b.entries||[]).map(e=>`<button data-path="${esc(e.path)}" data-dir="${e.isDirectory?'true':'false'}" onclick="selectProcessBrowserButton(this)"><b>${e.isDirectory?'[+] ':''}${esc(e.name)}</b><br><span class="muted">${esc(e.kind)} | ${esc(e.path)}</span></button>`).join('')||'<div class="muted" style="padding:10px">No entries.</div>';
}
function selectProcessBrowserButton(button){selectProcessBrowserEntry(button.dataset.path||'',button.dataset.dir==='true');}
function selectProcessBrowserEntry(path,isDirectory){
  if(isDirectory){browseProcessFiles(path);return;}
  $('processStartPath').value=path;
  $('processStartStatus').textContent='Selected '+path;
}
function render(){
  $('status').textContent=(state.isRecording?'Recording ':'Idle ')+'| Data: '+state.dataPath+' | Updated '+new Date(state.capturedUtc).toLocaleString();
  $('recordBtn').textContent=state.isRecording?'Stop Recording':'Start Recording';
  $('projects').innerHTML=(state.projects||[]).map(p=>`<div class="card"><b>${esc(p.name)}</b><div class="muted">${esc(p.status)}</div><div>${esc(p.summary)}</div></div>`).join('');
  const perms=state.permissions||{};
  const pnames=[['humanInteraction','Human interaction'],['spendMoney','Spend money'],['accountLogin','Account login'],['useFiles','Real files'],['pcSettings','PC settings'],['internetAccess','Internet'],['liveInput','Live input']];
  $('permissions').innerHTML=pnames.map(([k,n])=>`<div class="row"><span>${n}</span><span class="pill ${perms[k]?'on':'off'}">${perms[k]?'enabled':'approval required'}</span></div>`).join('');
  const approvals=state.pendingApprovals||[];
  $('approvalBanner').hidden=!approvals.length;
  $('approvalBanner').innerHTML=approvals.length?`<span><b>${approvals.length} approval${approvals.length===1?'':'s'} pending</b><br><span class="muted">${esc(approvals[0].title||'Approval required')}: ${esc(approvals[0].detail||'')}</span></span><button class="tab" onclick="document.querySelector('[data-tab=approvals]').click()">Review</button>`:'';
  $('pendingApprovalsPlan').innerHTML=approvalCards(approvals,true);
  $('pendingApprovals').innerHTML=approvalCards(approvals,false);
  $('approvalGateSummary').innerHTML=pnames.map(([k,n])=>`<div class="row"><span>${n}</span><span class="pill ${perms[k]?'on':'off'}">${perms[k]?'enabled':'blocked'}</span></div>`).join('');
  $('sessions').innerHTML=(state.sessions||[]).map(s=>`<div class="row"><span><b>${esc(s.title)}</b><br><span class="muted">${esc(s.id)} | ${esc(s.eventCount)} events</span></span><span class="pill">${esc(s.status)}</span></div>`).join('')||'<div class="muted">No sessions yet.</div>';
  $('events').innerHTML=(state.recentEvents||[]).map(e=>`<div class="row"><span><b>${esc(e.eventType)}</b><br><span class="muted">${esc(e.detail)}</span></span><span>${esc(e.sequence)}</span></div>`).join('')||'<div class="muted">No events yet.</div>';
  const t=(state.templates||[]).find(x=>x.id==='JACK')||(state.templates||[])[0]||{};
  $('companionName').value=t.companionName||'JACK'; $('interests').value=t.interests||''; $('templateText').value=t.templateText||'';
  $('skills').innerHTML='<h2>Learned Skills</h2>'+((state.skills||[]).map(s=>`<div class="row"><span><b>${esc(s.name)}</b><br><span class="muted">${esc(s.trigger)}</span></span><span class="pill">${esc(s.confidence)}%</span></div>`).join('')||'<div class="muted">No learned skills yet.</div>');
  $('people').innerHTML='<h2>People Memory</h2>'+((state.people||[]).map(p=>`<div class="row"><span><b>${esc(p.name)}</b><br><span class="muted">risk ${p.risk}, helpful ${p.helpfulness}, relevant ${p.relevance}, confidence ${p.confidence}</span></span></div>`).join('')||'<div class="muted">No people records yet.</div>');
  const runner=state.runner||{};
  $('runnerEndpoint').value=runner.modelEndpoint||$('runnerEndpoint').value;
  renderModelOptions(runner.model||$('runnerModel').value||'local-model');
  $('runnerMaxSteps').value=runner.maxSteps||$('runnerMaxSteps').value;
  $('llmStatus').textContent=(runner.isRunning?'Runner on':'Runner off')+(runner.isBusy?' | busy':' | idle')+' | '+(runner.message||'');
  $('llmTasks').innerHTML=(state.llmTasks||[]).map(t=>`<div class="row"><span><b>${esc(t.goal)}</b><br><span class="muted">${esc(t.mode)} | step ${esc(t.step||0)}/${esc(t.maxSteps||0)} | ${esc(t.lastAction||'')}<br>${esc(t.plan)}</span></span><span><span class="pill ${t.status==='approval_required'?'off':(t.status==='failed'?'bad':'')}">${esc(t.status)}</span><br><span class="muted">${esc(t.progress||0)}%</span></span></div>`).join('')||'<div class="muted">No LLM desktop tasks queued.</div>';
  $('auditRows').innerHTML=(state.audit||[]).map(a=>`<div class="row"><span><b>${esc(a.action)}</b><br><span class="muted">${esc(a.detail)}</span></span><span>${esc(new Date(a.createdUtc).toLocaleTimeString())}</span></div>`).join('')||'<div class="muted">No audit events yet.</div>';
  $('sharedFiles').innerHTML=(fileState.sharedFiles||[]).map(f=>`<div class="row"><span><b>${esc(f.relativePath||f.name)}</b><br><span class="muted">${esc(f.kind||'file')} | ${Math.round((f.sizeBytes||0)/1024)} KB | ${esc(f.note)}</span></span><a href="/api/share/download?id=${encodeURIComponent(f.id)}&token=${encodeURIComponent(companionToken)}">download</a></div>`).join('')||'<div class="muted">No shared files yet.</div>';
  $('sessionFiles').innerHTML='<table><thead><tr><th>Name</th><th>Kind</th><th>Session</th><th>Path</th></tr></thead><tbody>'+((fileState.files||[]).map(f=>`<tr><td>${esc(f.name)}</td><td>${esc(f.kind)}</td><td>${esc(f.sessionId)}</td><td>${esc(f.path)}</td></tr>`).join('')||'<tr><td colspan="4" class="muted">No session files yet.</td></tr>')+'</tbody></table>';
  renderTraining();
  bindApprovalButtons();
}
function renderTraining(){
  const training=state.training||{}; const settings=training.settings||{};
  $('trainingLearning').value=String(settings.learningEnabled!==false);
  $('trainingApprovalMode').value=settings.approvalMode||'review_first';
  $('trainingMaxFrames').value=settings.replayMaxFrames||300;
  $('trainingMaxMb').value=Math.round((settings.replayMaxBytes||262144000)/1024/1024);
  const active=(training.runs||[]).find(r=>!['completed','cancelled','failed','disabled','needs_model'].includes(String(r.status||'').toLowerCase()));
  $('trainingStatus').textContent=active?`Training ${active.status} ${active.progress||0}% | ${active.summary||''}`:`Training idle | ${settings.approvalMode||'review_first'} | ${settings.learningEnabled===false?'disabled':'enabled'}`;
  $('trainingRuns').innerHTML=(training.runs||[]).map(r=>`<div class="row"><span><b>${esc(r.status)}</b><br><span class="muted">${esc(r.sourceSessionId)} | ${esc(r.summary)}${r.error?'<br>'+esc(r.error):''}</span></span><span><span class="pill ${r.status==='failed'?'bad':(r.status==='needs_model'?'off':'')}">${esc(r.progress||0)}%</span><br><span class="muted">${esc(r.riskLevel||'')}</span></span></div>`).join('')||'<div class="muted">No training runs yet.</div>';
  $('skillDrafts').innerHTML=(training.skillDrafts||[]).map(d=>`<div class="card"><div class="row"><span><b>${esc(d.name||'Draft skill')}</b><br><span class="muted">${esc(d.trigger||'')}</span></span><span><span class="pill ${d.status==='enabled'?'on':(d.status==='rejected'?'bad':'off')}">${esc(d.status||'draft')}</span><br><span class="muted">${esc(d.riskLevel||'medium')} ${esc(d.confidence||0)}%</span></span></div><div class="muted">${esc(d.steps||'').slice(0,360)}</div><div class="approval-actions"><button onclick="reviewDraft('${esc(d.id)}','approve')">Approve</button><button class="good" onclick="reviewDraft('${esc(d.id)}','enable')">Enable</button><button onclick="reviewDraft('${esc(d.id)}','reject')">Reject</button></div></div>`).join('')||'<div class="muted">No draft skills yet.</div>';
  const frames=(training.evidence||[]).filter(e=>e.keyframePath);
  $('trainingEvidence').innerHTML=frames.map(e=>`<div class="row"><span><b>${esc(e.eventType)}</b><br><span class="muted">${esc(e.summary||e.keyframePath)} | ${esc(e.sensitivityFlags||'')}</span></span><a target="_blank" href="/api/companion/replay/frame?id=${encodeURIComponent(e.id)}">open</a></div>`).join('')||'<div class="muted">No replay frames indexed yet.</div>';
}
function renderModelOptions(selected){
  const select=$('runnerModel'); if(!select)return;
  const current=runnerModelDirty?(select.value||selected||'local-model'):(selected||select.value||'local-model');
  const ids=[current,...((modelCatalog?.models||[]).map(m=>m.id||m.name||''))].filter(Boolean);
  const unique=[...new Set(ids)];
  select.innerHTML=unique.map(id=>`<option value="${esc(id)}">${esc(id)}</option>`).join('')||'<option value="local-model">local-model</option>';
  select.value=unique.find(id=>id.toLowerCase()===current.toLowerCase())||unique[0]||'local-model';
  const status=$('modelListStatus');
  if(status&&modelCatalog)status.textContent=modelCatalog.ok?`Loaded ${modelCatalog.models?.length||0} model(s) from ${modelCatalog.source||'local model API'}`:(modelCatalog.warning||'Model list unavailable.');
}
async function refreshModels(){
  const endpoint=$('runnerEndpoint')?.value||'http://localhost:11435/v1/chat/completions';
  const selected=$('runnerModel')?.value||'local-model';
  const data=await fetch(`/api/companion/llm/models?endpoint=${encodeURIComponent(endpoint)}&selected=${encodeURIComponent(selected)}`,{cache:'no-store'}).then(r=>r.json());
  modelCatalog=data.catalog||{ok:false,models:[{id:selected}],selected,warning:data.error||'Model list unavailable.'};
  renderModelOptions(modelCatalog.selected||selected);
}
$('recordBtn').onclick=async()=>{state?.isRecording?await post('/api/companion/recording/stop',{summary:'Stopped from /Workspace'}):await post('/api/companion/recording/start',{title:'Workspace recording'});await refresh();};
async function saveTemplate(){await post('/api/companion/template',{id:'JACK',name:'JACK',companionName:$('companionName').value,interests:$('interests').value,templateText:$('templateText').value});await refresh();}
async function wandName(){await post('/api/companion/wand/name');await refresh();}
async function wandInterests(){await post('/api/companion/wand/interests');await refresh();}
function trainingConfig(){
  return {learningEnabled:$('trainingLearning').value==='true',approvalMode:$('trainingApprovalMode').value,replayMaxFrames:parseInt($('trainingMaxFrames').value||'300',10)||300,replayMaxBytes:(parseInt($('trainingMaxMb').value||'250',10)||250)*1024*1024,captureProfile:'HybridMinimizedReplay'};
}
async function saveTrainingSettings(){
  const payload=trainingConfig();
  if(payload.approvalMode==='enable_all'&&!confirm('Enable All learned skills is dangerous. JACK may reuse every inferred skill without review. Continue?'))return;
  const data=await post('/api/companion/training/settings',Object.assign(payload,{warningAccepted:payload.approvalMode==='enable_all'}));
  $('trainingStatus').textContent=data.ok?'Training settings saved.':(data.error||'Training settings blocked.');
  await refresh();
}
async function startTraining(){
  const latest=(state.sessions||[])[0]||{};
  const data=await post('/api/companion/training/start',{sessionId:latest.id,modelEndpoint:$('runnerEndpoint').value,model:$('runnerModel').value});
  $('trainingStatus').textContent=data.trainingRun?.summary||data.error||'Training started.';
  await refresh();
}
async function cancelTraining(){const data=await post('/api/companion/training/cancel',{reason:'Cancelled from /Workspace'});$('trainingStatus').textContent=data.trainingRun?.summary||'Training cancelled.';await refresh();}
async function reviewDraft(id,action){
  let warningAccepted=false;
  if(action==='enable'){
    const draft=((state.training||{}).skillDrafts||[]).find(d=>d.id===id)||{};
    warningAccepted=String(draft.riskLevel||'medium').toLowerCase()!=='low'?confirm('This learned skill is not low risk. Enable it anyway?'):true;
    if(!warningAccepted)return;
  }
  const data=await post('/api/companion/skills/review',{draftId:id,action,warningAccepted});
  $('trainingStatus').textContent=data.review?.message||data.error||'Skill review updated.';
  await refresh();
}
function runnerConfig(){runnerModelDirty=false;return {modelEndpoint:$('runnerEndpoint').value,model:$('runnerModel').value,maxSteps:parseInt($('runnerMaxSteps').value||'8',10)||8};}
async function startRunner(){const data=await post('/api/companion/llm/runner/start',runnerConfig());$('llmStatus').textContent=data.runner?.message||'Runner started.';await refresh();}
async function stopRunner(){const data=await post('/api/companion/llm/runner/stop',{});$('llmStatus').textContent=data.runner?.message||'Runner stopped.';await refresh();}
async function submitLlmTask(){const data=await post('/api/companion/llm/task',Object.assign({goal:$('llmGoal').value,mode:$('llmMode').value},runnerConfig()));$('llmStatus').textContent=data.task?.plan||'Task queued.';await refresh();}
async function stopLlmTask(){const data=await post('/api/companion/llm/stop',{reason:'Stopped from /Workspace'});$('llmStatus').textContent=data.task?.plan||'Task stopped.';await refresh();}
async function emergencyStop(){const data=await post('/api/companion/emergency-stop',{});$('controlStatus').textContent=data.emergency?.message||'Emergency stop complete.';liveDesktop=false;setLiveButton();await refresh();}
function setLiveButton(){$('liveBtn').textContent=liveDesktop?'Stop Live View':'Start Live View';if(!liveDesktop&&liveTimer){clearInterval(liveTimer);liveTimer=null;}if(!liveDesktop&&desktopAbort){desktopAbort.abort();desktopAbort=null;}}
function toggleLiveDesktop(){liveDesktop=!liveDesktop;setLiveButton();if(liveDesktop)startDesktopStream();}
async function startDesktopStream(){
  const transport=$('desktopTransport')?.value||'websocket';
  if(transport==='websocket'&&window.WebSocket){startDesktopWebSocket();return;}
  if(transport==='polling'){liveTimer=setInterval(captureScreen,900);captureScreen();return;}
  desktopAbort=new AbortController();
  try{
    const r=await fetch(`/api/companion/desktop/stream?fps=2&frames=600&format=${encodeURIComponent($('desktopFormat')?.value||'adaptive')}`,{cache:'no-store',signal:desktopAbort.signal});
    if(!r.body){liveTimer=setInterval(captureScreen,1000);return;}
    const reader=r.body.getReader(); const decoder=new TextDecoder(); let buffer='';
    while(liveDesktop){
      const {done,value}=await reader.read(); if(done)break;
      buffer+=decoder.decode(value,{stream:true});
      const lines=buffer.split('\\n'); buffer=lines.pop()||'';
      for(const line of lines){ if(line.trim()) renderDesktopFrame(JSON.parse(line)); }
    }
  }catch(e){ if(liveDesktop){$('controlStatus').textContent='Stream stopped: '+e.message; liveTimer=setInterval(captureScreen,1200);} }
}
function startDesktopWebSocket(){
  const proto=location.protocol==='https:'?'wss':'ws';
  const ws=new WebSocket(`${proto}://${location.host}/api/companion/desktop/ws?fps=3&frames=1800&format=${encodeURIComponent($('desktopFormat')?.value||'adaptive')}&token=${encodeURIComponent(companionToken)}`);
  desktopAbort={abort:()=>{try{ws.close();}catch{}}};
  ws.onmessage=ev=>{try{renderDesktopFrame(JSON.parse(ev.data));}catch(e){$('controlStatus').textContent='Frame parse failed: '+e.message;}};
  ws.onerror=()=>{if(liveDesktop){$('controlStatus').textContent='WebSocket unavailable; using chunked stream.';$('desktopTransport').value='chunked';startDesktopStream();}};
  ws.onclose=()=>{if(liveDesktop&&($('desktopTransport')?.value||'')==='websocket'){$('controlStatus').textContent='WebSocket closed.';}};
}
async function captureScreen(){
  const r=await fetch(`/api/companion/screen.json?format=${encodeURIComponent($('desktopFormat')?.value||'adaptive')}`,{cache:'no-store'});
  const data=await r.json();
  renderDesktopFrame(data);
}
function renderDesktopFrame(data){
  $('controlStatus').textContent=data.ok?`Frame ${data.width}x${data.height} | ${data.transport||'polling'} | ${data.encoding||''} | ${Math.round((data.byteLength||0)/1024)} KB`:(data.error||'Screen capture blocked.');
  if(data.ok){
    const shell=$('remoteShell');
    let frame=$('screenFrame');
    if(!frame){
      shell.innerHTML='<div class="screen-frame" id="screenFrame"><img id="screenPreview" alt="Remote desktop frame"><span class="cursor-dot" id="remoteCursor" hidden></span></div>';
      frame=$('screenFrame');
      $('screenPreview').onclick=ev=>{
        const rect=ev.currentTarget.getBoundingClientRect();
        sendInput({action:'click',button:'left',x:(ev.clientX-rect.left)/rect.width,y:(ev.clientY-rect.top)/rect.height,normalized:true});
      };
      $('screenPreview').onpointerdown=ev=>{
        const rect=ev.currentTarget.getBoundingClientRect();
        dragStart={x:(ev.clientX-rect.left)/rect.width,y:(ev.clientY-rect.top)/rect.height};
      };
      $('screenPreview').onpointerup=ev=>{
        if(!dragStart)return;
        const rect=ev.currentTarget.getBoundingClientRect();
        const end={x:(ev.clientX-rect.left)/rect.width,y:(ev.clientY-rect.top)/rect.height};
        if(Math.abs(end.x-dragStart.x)>0.015||Math.abs(end.y-dragStart.y)>0.015)
          sendInput({action:'drag',button:'left',x:dragStart.x,y:dragStart.y,endX:end.x,endY:end.y,normalized:true});
        dragStart=null;
      };
    }
    const preview=$('screenPreview');
    if(preview&&preview.src!==data.dataUrl){
      frame.classList.add('loading');
      const next=new Image();
      next.onload=()=>{preview.src=data.dataUrl;frame.classList.remove('loading');};
      next.onerror=()=>frame.classList.remove('loading');
      next.src=data.dataUrl;
    }
    const cursor=$('remoteCursor');
    if(cursor){
      const visible=!!(data.cursor&&data.cursor.visible);
      cursor.hidden=!visible;
      if(visible){
        cursor.style.left=((data.cursor.normalizedX||0)*100)+'%';
        cursor.style.top=((data.cursor.normalizedY||0)*100)+'%';
      }
    }
  }
}
async function sendInput(payload){
  const data=await post('/api/companion/input',payload);
  $('controlStatus').textContent=data.ok?(data.result?.message||'Input sent.'):(data.error||data.decision?.message||'Input blocked.');
  if(data.ok&&liveDesktop) setTimeout(captureScreen,180);
}
async function uploadShare(){
  const files=[...($('shareUpload').files||[]),...($('shareFolder').files||[])]; if(!files.length){$('shareStatus').textContent='Choose or drop files first.';return;}
  await uploadShareFiles(files);
}
async function uploadShareFiles(files){
  let shared=0;
  for(const file of files){
  const reader=new FileReader();
  const result=await new Promise((resolve,reject)=>{reader.onload=()=>resolve(reader.result);reader.onerror=()=>reject(reader.error);reader.readAsDataURL(file);});
    const base64=String(result).split(',')[1]||'';
    const payload={fileName:file.name,relativePath:file.webkitRelativePath||file.name,base64,note:$('shareNote').value};
    let data=await post('/api/share/upload',payload);
    if(data.fileApprovalRequired&&confirm((data.shared?.approvalReason||'This file requires approval.')+'\\n\\nShare it anyway?'))
      data=await post('/api/share/upload',Object.assign(payload,{approved:true}));
    if(data.ok) shared++;
    $('shareStatus').textContent=data.ok?'Shared '+(file.webkitRelativePath||file.name):(data.error||data.shared?.approvalReason||'Upload blocked.');
  }
  $('shareStatus').textContent='Shared '+shared+' of '+files.length+' file(s).';
  await refresh();
}
async function shareLocalPath(){
  const payload={path:$('sharePath').value,note:$('shareNote').value};
  let data=await post('/api/share/register-path',payload);
  if(data.fileApprovalRequired&&confirm((data.shared?.[0]?.approvalReason||'This path requires approval.')+'\\n\\nShare it anyway?'))
    data=await post('/api/share/register-path',Object.assign(payload,{approved:true}));
  $('shareStatus').textContent=data.ok?'Shared '+(data.shared?.length||0)+' item(s).':(data.error||data.shared?.[0]?.approvalReason||'Path share blocked.');
  await refresh();
}
const dz=$('dropZone');
$('runnerModel').onchange=()=>{runnerModelDirty=true;};
dz.ondragover=e=>{e.preventDefault();dz.style.borderColor='#59d6c9';};
dz.ondragleave=()=>{dz.style.borderColor='';};
dz.ondrop=e=>{e.preventDefault();dz.style.borderColor='';uploadShareFiles([...e.dataTransfer.files]);};
refresh(); refreshModels(); setInterval(refresh,2500);
</script>
</body>
</html>
""".Replace("AUTH_TOKEN_PLACEHOLDER", _authToken);
    }

    private string FileHtml()
    {
        return """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SocketJack Companion Files</title>
<style>
body{margin:0;background:#101820;color:#e7edf3;font:14px/1.45 "Segoe UI",system-ui,sans-serif}header{padding:18px 22px;border-bottom:1px solid #385066;background:#111b25}main{padding:14px}a{color:#8bd7ff}.muted{color:#a8b5c2}
table{width:100%;border-collapse:collapse;background:#182330;border:1px solid #385066}th,td{text-align:left;padding:9px;border-bottom:1px solid #2b4358;vertical-align:top}th{background:#202d3b}
button{background:#25445c;color:#e7edf3;border:1px solid #385066;border-radius:6px;padding:8px 11px;cursor:pointer}
</style>
</head>
<body>
<header><h1>Companion Files</h1><div class="muted" id="status">Loading files...</div><p><a href="/Workspace">Workspace</a></p></header>
<main>
<h2>Shared Files</h2>
<table><thead><tr><th>Name</th><th>Size</th><th>Note</th><th>Created</th><th>Download</th></tr></thead><tbody id="shares"></tbody></table>
<h2>Session Files</h2>
<table><thead><tr><th>Name</th><th>Kind</th><th>Session</th><th>Path</th><th>Note</th><th>Last Seen</th></tr></thead><tbody id="rows"></tbody></table>
</main>
<script>
const companionToken = 'AUTH_TOKEN_PLACEHOLDER';
const nativeFetch = window.fetch.bind(window);
window.fetch = (input, init = {}) => {
  const rawUrl = typeof input === 'string' ? input : (input && input.url) || '';
  const url = String(rawUrl || '');
  if (url.startsWith('/api/') || url.startsWith(location.origin + '/api/')) {
    init = Object.assign({}, init);
    init.headers = Object.assign({'X-JackLLM-Companion-Token': companionToken}, init.headers || {});
  }
  return nativeFetch(input, init);
};
const $=id=>document.getElementById(id);
function esc(v){return String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));}
async function refresh(){
 const r=await fetch('/api/files',{cache:'no-store'}); const data=await r.json();
 $('status').textContent='Updated '+new Date(data.capturedUtc).toLocaleString()+' | '+(data.files||[]).length+' session files | '+(data.sharedFiles||[]).length+' shared files';
 $('shares').innerHTML=(data.sharedFiles||[]).map(f=>`<tr><td>${esc(f.relativePath||f.name)}</td><td>${Math.round((f.sizeBytes||0)/1024)} KB</td><td>${esc(f.kind||'file')} | ${esc(f.note)}</td><td>${esc(f.createdUtc)}</td><td><a href="/api/share/download?id=${encodeURIComponent(f.id)}&token=${encodeURIComponent(companionToken)}">download</a></td></tr>`).join('')||'<tr><td colspan="5" class="muted">No shared files yet.</td></tr>';
 $('rows').innerHTML=(data.files||[]).map(f=>`<tr><td>${esc(f.name)}</td><td>${esc(f.kind)}</td><td>${esc(f.sessionId)}</td><td>${esc(f.path)}</td><td>${esc(f.note)}</td><td>${esc(f.lastSeenUtc)}</td></tr>`).join('')||'<tr><td colspan="6" class="muted">No files have been associated with Companion work sessions yet.</td></tr>';
}
refresh(); setInterval(refresh,2500);
</script>
</body>
</html>
""".Replace("AUTH_TOKEN_PLACEHOLDER", _authToken);
    }
}
