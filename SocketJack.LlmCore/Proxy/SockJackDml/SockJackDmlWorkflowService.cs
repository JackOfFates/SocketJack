using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SocketJack.Net
{
    internal sealed class SockJackDmlWorkflowService
    {
        private readonly object _sync = new object();
        private readonly string _root;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public SockJackDmlWorkflowService(string chatSessionRoot)
        {
            _root = Path.Combine(string.IsNullOrWhiteSpace(chatSessionRoot) ? AppContext.BaseDirectory : chatSessionRoot, "SockJackDml", "owners");
            Directory.CreateDirectory(_root);
        }

        public string Serialize(object value)
        {
            return JsonSerializer.Serialize(value, _jsonOptions);
        }

        public MagicPlanCreateRequest ReadPlanCreateRequest(string json)
        {
            return ReadRequest<MagicPlanCreateRequest>(json) ?? new MagicPlanCreateRequest();
        }

        public MagicProgressDocumentRequest ReadProgressDocumentRequest(string json)
        {
            return ReadRequest<MagicProgressDocumentRequest>(json) ?? new MagicProgressDocumentRequest();
        }

        public MagicPlanExecutionRequest ReadPlanExecutionRequest(string json)
        {
            return ReadRequest<MagicPlanExecutionRequest>(json) ?? new MagicPlanExecutionRequest();
        }

        public MagicWorkflowStatusRequest ReadWorkflowStatusRequest(string json)
        {
            return ReadRequest<MagicWorkflowStatusRequest>(json) ?? new MagicWorkflowStatusRequest();
        }

        public MagicProgressFindRequest ReadProgressFindRequest(string json)
        {
            return ReadRequest<MagicProgressFindRequest>(json) ?? new MagicProgressFindRequest();
        }

        public MagicExecutionControlRequest ReadExecutionControlRequest(string json)
        {
            return ReadRequest<MagicExecutionControlRequest>(json) ?? new MagicExecutionControlRequest();
        }

        public MagicEvidenceLinkRequest ReadEvidenceLinkRequest(string json)
        {
            return ReadRequest<MagicEvidenceLinkRequest>(json) ?? new MagicEvidenceLinkRequest();
        }

        public MagicPlanCreateResult CreateOrUpdatePlan(string ownerKey, string sessionId, MagicPlanCreateRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicPlanCreateRequest();

            string now = DateTimeOffset.UtcNow.ToString("O");
            lock (_sync)
            {
                var plans = ReadList<MagicPlanRecord>(PlansPath(ownerKey));
                MagicPlanRecord record = FindPlan(plans, sessionId, request);
                bool isNew = record == null;
                if (record == null)
                {
                    string seed = ownerKey + "|" + sessionId + "|" + FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName) + "|" + request.FeatureName + "|" + request.Goal + "|" + now;
                    record = new MagicPlanRecord
                    {
                        Id = string.IsNullOrWhiteSpace(request.PlanId) ? "plan_" + ShortHash(seed) : NormalizeId(request.PlanId),
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        CreatedUtc = now
                    };
                }

                record.ProjectOrSessionName = NormalizeText(FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName, record.ProjectOrSessionName), 120, "Session");
                record.FeatureName = NormalizeText(FirstNonEmpty(request.FeatureName, request.Feature, record.FeatureName), 120, "Feature");
                record.Goal = NormalizeText(FirstNonEmpty(request.Goal, request.Objective, record.Goal), 4000, "");
                record.Context = NormalizeText(FirstNonEmpty(request.Context, request.Detail, record.Context), 8000, "");
                record.Constraints = MergeStrings(record.Constraints, request.Constraints);
                record.Requirements = MergeStrings(record.Requirements, request.Requirements);
                record.KnownFiles = MergeStrings(record.KnownFiles, request.KnownFiles);
                record.Answers = MergeStrings(record.Answers, request.QuestionsAnswered);
                if (!string.IsNullOrWhiteSpace(request.UserResponse))
                    record.Answers = MergeStrings(record.Answers, new[] { request.UserResponse });
                if (request.Steps != null && request.Steps.Count > 0)
                    record.Steps = NormalizePlanSteps(request.Steps);
                if (record.Steps.Count == 0)
                    record.Steps = BuildDefaultPlanSteps(record);

                List<string> questions = BuildPlanQuestions(record);
                bool finalize = request.Finalize || request.ReadyToFinalize || string.Equals(request.Status, "final", StringComparison.OrdinalIgnoreCase);
                bool needsInput = !finalize && questions.Count > 0;
                record.Questions = questions;
                record.Status = needsInput ? "draft" : "final";
                record.PlanMarkdown = needsInput ? "" : RenderPlanMarkdown(record);
                record.UpdatedUtc = now;

                plans.RemoveAll(plan => string.Equals(plan.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                plans.Add(record);
                WriteList(PlansPath(ownerKey), plans);

                return new MagicPlanCreateResult
                {
                    Ok = true,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    PlanId = record.Id,
                    ProjectOrSessionName = record.ProjectOrSessionName,
                    FeatureName = record.FeatureName,
                    NeedsInput = needsInput,
                    Questions = questions,
                    PlanMarkdown = record.PlanMarkdown,
                    Status = record.Status,
                    Created = isNew,
                    UpdatedUtc = now,
                    StoragePath = PlansPath(ownerKey)
                };
            }
        }

        public MagicProgressDocumentResult CreateOrUpdateProgress(string ownerKey, string sessionId, MagicProgressDocumentRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicProgressDocumentRequest();
            AppendJsonStages(request);
            AppendJsonLogEntries(request);

            string now = DateTimeOffset.UtcNow.ToString("O");
            lock (_sync)
            {
                var plans = ReadList<MagicPlanRecord>(PlansPath(ownerKey));
                MagicPlanRecord plan = FindPlanById(plans, request.PlanId);
                var records = ReadList<MagicProgressDocumentRecord>(ProgressPath(ownerKey));
                MagicProgressDocumentRecord record = FindProgress(records, sessionId, request, plan);
                bool isNew = record == null;
                if (record == null)
                {
                    string project = FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName, plan == null ? "" : plan.ProjectOrSessionName, sessionId, "Session");
                    string feature = FirstNonEmpty(request.FeatureName, request.Feature, plan == null ? "" : plan.FeatureName, "Feature");
                    record = new MagicProgressDocumentRecord
                    {
                        Id = string.IsNullOrWhiteSpace(request.ProgressId) ? "progress_" + ShortHash(ownerKey + "|" + sessionId + "|" + project + "|" + feature + "|" + now) : NormalizeId(request.ProgressId),
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        ProjectOrSessionName = NormalizeText(project, 120, "Session"),
                        FeatureName = NormalizeText(feature, 120, "Feature"),
                        CreatedUtc = now
                    };
                }

                record.PlanId = NormalizeId(FirstNonEmpty(request.PlanId, record.PlanId, plan == null ? "" : plan.Id));
                record.ProjectOrSessionName = NormalizeText(FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName, record.ProjectOrSessionName, plan == null ? "" : plan.ProjectOrSessionName), 120, "Session");
                record.FeatureName = NormalizeText(FirstNonEmpty(request.FeatureName, request.Feature, record.FeatureName, plan == null ? "" : plan.FeatureName), 120, "Feature");
                record.ProgressFileName = SanitizeProgressFileName(record.ProjectOrSessionName, record.FeatureName);
                record.ProgressPath = NormalizeText(FirstNonEmpty(request.ProgressPath, request.Path, record.ProgressPath), 1000, "");
                record.CurrentStatus = NormalizeText(FirstNonEmpty(request.CurrentStatus, request.Status, record.CurrentStatus, isNew ? "started" : ""), 4000, "");
                record.NextStep = NormalizeText(FirstNonEmpty(request.NextStep, record.NextStep), 2000, "");
                record.VerificationNotes = NormalizeText(FirstNonEmpty(request.VerificationNotes, request.Verification, record.VerificationNotes), 4000, "");
                record.Summary = NormalizeText(FirstNonEmpty(request.Summary, record.Summary), 4000, "");
                if (request.OverallPercent.HasValue)
                    record.OverallPercent = ClampPercent(request.OverallPercent.Value);

                if (request.Stages != null && request.Stages.Count > 0)
                    record.Stages = MergeStages(record.Stages, request.Stages);
                if (record.Stages.Count == 0 && plan != null)
                    record.Stages = plan.Steps.Select(step => new MagicProgressStage
                    {
                        Name = step.Title,
                        Status = "pending",
                        Percent = 0,
                        Notes = step.Detail
                    }).ToList();
                if (!request.OverallPercent.HasValue)
                    record.OverallPercent = InferOverallPercent(record);

                if (request.ReplaceLog)
                    record.LogEntries.Clear();
                if (request.LogEntries != null)
                {
                    foreach (MagicProgressLogEntry entry in request.LogEntries)
                        AddProgressLog(record, entry, now);
                }
                if (!string.IsNullOrWhiteSpace(request.LogEntry))
                    AddProgressLog(record, new MagicProgressLogEntry { Status = FirstNonEmpty(request.LogStatus, "note"), Detail = request.LogEntry }, now);
                if (record.LogEntries.Count == 0)
                    AddProgressLog(record, new MagicProgressLogEntry { Status = "created", Detail = "Progress tracker initialized." }, now);

                record.UpdatedUtc = now;
                record.Markdown = RenderProgressMarkdown(record);
                records.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                records.Add(record);
                WriteList(ProgressPath(ownerKey), records);

                return new MagicProgressDocumentResult
                {
                    Ok = true,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    ProgressId = record.Id,
                    PlanId = record.PlanId,
                    ProjectOrSessionName = record.ProjectOrSessionName,
                    FeatureName = record.FeatureName,
                    ProgressFileName = record.ProgressFileName,
                    ProgressPath = record.ProgressPath,
                    OverallPercent = record.OverallPercent,
                    CurrentStatus = record.CurrentStatus,
                    Markdown = record.Markdown,
                    Created = isNew,
                    UpdatedUtc = now,
                    StoragePath = ProgressPath(ownerKey)
                };
            }
        }

        public void UpdateProgressDocumentPath(string ownerKey, string progressId, string progressPath)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            progressId = NormalizeId(progressId);
            progressPath = NormalizeText(progressPath, 1000, "");
            if (string.IsNullOrWhiteSpace(progressId) || string.IsNullOrWhiteSpace(progressPath))
                return;

            string now = DateTimeOffset.UtcNow.ToString("O");
            lock (_sync)
            {
                var records = ReadList<MagicProgressDocumentRecord>(ProgressPath(ownerKey));
                MagicProgressDocumentRecord record = records.FirstOrDefault(item => string.Equals(item.Id, progressId, StringComparison.OrdinalIgnoreCase));
                if (record == null)
                    return;

                record.ProgressPath = progressPath;
                record.UpdatedUtc = now;
                record.Markdown = RenderProgressMarkdown(record);
                WriteList(ProgressPath(ownerKey), records);
            }
        }

        public MagicWorkflowStatusResult GetWorkflowStatus(string ownerKey, string sessionId, MagicWorkflowStatusRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicWorkflowStatusRequest();

            lock (_sync)
            {
                var plans = FilterPlans(ReadList<MagicPlanRecord>(PlansPath(ownerKey)), sessionId, request).ToList();
                var progress = FilterProgress(ReadList<MagicProgressDocumentRecord>(ProgressPath(ownerKey)), sessionId, request).ToList();
                var executions = FilterExecutions(ReadList<MagicPlanExecutionRecord>(ExecutionsPath(ownerKey)), sessionId, request).ToList();
                var evidenceLinks = FilterEvidenceLinks(ReadList<MagicEvidenceLinkRecord>(EvidenceLinksPath(ownerKey)), sessionId, request).ToList();
                MagicPlanExecutionRecord latestExecution = executions
                    .OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc))
                    .FirstOrDefault();
                MagicProgressDocumentRecord latestProgress = progress
                    .OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc))
                    .FirstOrDefault();

                return new MagicWorkflowStatusResult
                {
                    Ok = true,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    PlanCount = plans.Count,
                    ProgressCount = progress.Count,
                    ExecutionCount = executions.Count,
                    EvidenceLinkCount = evidenceLinks.Count,
                    LatestBlocker = latestExecution == null ? "" : latestExecution.Blocker,
                    LatestStatus = FirstNonEmpty(latestExecution == null ? "" : latestExecution.Status, latestProgress == null ? "" : latestProgress.CurrentStatus, "none"),
                    NextAction = InferNextAction(plans, progress, latestExecution),
                    ApprovalState = BuildApprovalState(latestExecution),
                    Plans = plans.Select(ToPlanSnapshot).Take(NormalizeTake(request.Take, 50)).ToList(),
                    ProgressDocuments = progress.Select(ToProgressSnapshot).Take(NormalizeTake(request.Take, 50)).ToList(),
                    Executions = executions.Select(ToExecutionSnapshot).Take(NormalizeTake(request.Take, 50)).ToList(),
                    EvidenceLinks = evidenceLinks.Select(ToEvidenceLinkSnapshot).Take(NormalizeTake(request.Take, 50)).ToList(),
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O"),
                    StoragePath = OwnerWorkflowDirectory(ownerKey)
                };
            }
        }

        public MagicProgressFindResult FindProgressDocument(string ownerKey, string sessionId, MagicProgressFindRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicProgressFindRequest();

            lock (_sync)
            {
                var plans = ReadList<MagicPlanRecord>(PlansPath(ownerKey));
                MagicPlanRecord plan = FindPlanById(plans, request.PlanId);
                var lookup = new MagicProgressDocumentRequest
                {
                    ProgressId = request.ProgressId,
                    PlanId = request.PlanId,
                    ProjectOrSessionName = request.ProjectOrSessionName,
                    ProjectName = request.ProjectName,
                    SessionName = request.SessionName,
                    FeatureName = request.FeatureName,
                    Feature = request.Feature,
                    ProgressPath = request.ProgressPath,
                    Path = request.Path
                };
                var records = ReadList<MagicProgressDocumentRecord>(ProgressPath(ownerKey));
                MagicProgressDocumentRecord record = FindProgress(records, sessionId, lookup, plan);
                if (record == null && !string.IsNullOrWhiteSpace(request.ProgressPath))
                {
                    record = records
                        .Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault(item => string.Equals(item.ProgressPath, request.ProgressPath, StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(item.ProgressFileName, Path.GetFileName(request.ProgressPath), StringComparison.OrdinalIgnoreCase));
                }

                return new MagicProgressFindResult
                {
                    Ok = record != null,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    Progress = record == null ? null : ToProgressSnapshot(record),
                    ProgressPath = record == null ? "" : record.ProgressPath,
                    ProgressFileName = record == null
                        ? SanitizeProgressFileName(FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName, plan == null ? "" : plan.ProjectOrSessionName, "Session"), FirstNonEmpty(request.FeatureName, request.Feature, plan == null ? "" : plan.FeatureName, "Feature"))
                        : record.ProgressFileName,
                    Error = record == null ? "No matching SockJackDml progress document was found." : "",
                    UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
                };
            }
        }

        public MagicExecutionControlResult ControlExecution(string ownerKey, string sessionId, MagicExecutionControlRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicExecutionControlRequest();
            string action = NormalizeToken(request.Action, "status");
            string executionId = NormalizeId(request.ExecutionId);
            string now = DateTimeOffset.UtcNow.ToString("O");

            lock (_sync)
            {
                var executions = ReadList<MagicPlanExecutionRecord>(ExecutionsPath(ownerKey));
                MagicPlanExecutionRecord record = string.IsNullOrWhiteSpace(executionId)
                    ? executions.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc))
                        .FirstOrDefault()
                    : executions.FirstOrDefault(item => string.Equals(item.Id, executionId, StringComparison.OrdinalIgnoreCase));

                if (record == null)
                {
                    return new MagicExecutionControlResult
                    {
                        Ok = false,
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        Action = action,
                        Error = "Execution record was not found.",
                        UpdatedUtc = now
                    };
                }

                string previousStatus = record.Status;
                if (action == "pause")
                {
                    record.Paused = true;
                    record.Status = "paused";
                    record.Blocker = FirstNonEmpty(request.Reason, "Execution paused.");
                }
                else if (action == "resume")
                {
                    record.Paused = false;
                    if (string.Equals(record.Status, "paused", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(record.Status, "blocked", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(record.Status, "canceled", StringComparison.OrdinalIgnoreCase))
                    {
                        record.Status = "ready";
                    }
                    record.Blocker = "";
                }
                else if (action == "cancel")
                {
                    record.Paused = false;
                    record.Status = "canceled";
                    record.Blocker = FirstNonEmpty(request.Reason, "Execution canceled.");
                }
                else if (action == "retry" || action == "retry_blocked_step")
                {
                    record.Paused = false;
                    record.Status = "ready";
                    record.Blocker = "";
                    string actionId = NormalizeId(request.ActionId);
                    if (!string.IsNullOrWhiteSpace(actionId))
                    {
                        record.Results.RemoveAll(result => string.Equals(result.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
                        foreach (MagicPlanExecutionAction item in record.Actions)
                        {
                            if (string.Equals(item.Id, actionId, StringComparison.OrdinalIgnoreCase))
                                item.Status = "pending";
                        }
                    }
                    record.CursorIndex = CountCompletedActions(record);
                }
                else if (action == "skip" || action == "mark_skipped")
                {
                    string actionId = NormalizeId(request.ActionId);
                    if (string.IsNullOrWhiteSpace(actionId))
                        actionId = NextPendingAction(record)?.Id ?? "";
                    if (string.IsNullOrWhiteSpace(actionId))
                    {
                        return new MagicExecutionControlResult
                        {
                            Ok = false,
                            OwnerKey = ownerKey,
                            SessionId = sessionId,
                            ExecutionId = record.Id,
                            Action = action,
                            Error = "No pending action was available to skip.",
                            Execution = ToExecutionSnapshot(record),
                            UpdatedUtc = now
                        };
                    }

                    foreach (MagicPlanExecutionAction item in record.Actions)
                    {
                        if (string.Equals(item.Id, actionId, StringComparison.OrdinalIgnoreCase))
                            item.Status = "skipped";
                    }
                    record.Results.Add(new MagicPlanActionResult
                    {
                        ActionId = actionId,
                        Type = "control",
                        Summary = "Skipped by execution control.",
                        Status = "skipped",
                        Output = FirstNonEmpty(request.Reason, "Action skipped."),
                        CompletedUtc = now
                    });
                    record.CursorIndex = CountCompletedActions(record);
                    record.Status = record.CursorIndex >= record.Actions.Count ? "completed" : "ready";
                    record.Blocker = "";
                }
                else if (action == "status")
                {
                    // Read-only control action.
                }
                else
                {
                    return new MagicExecutionControlResult
                    {
                        Ok = false,
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        ExecutionId = record.Id,
                        Action = action,
                        Error = "Unknown execution control action: " + action,
                        Execution = ToExecutionSnapshot(record),
                        UpdatedUtc = now
                    };
                }

                record.UpdatedUtc = now;
                executions.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                executions.Add(record);
                WriteList(ExecutionsPath(ownerKey), executions);

                return new MagicExecutionControlResult
                {
                    Ok = true,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    ExecutionId = record.Id,
                    Action = action,
                    PreviousStatus = previousStatus,
                    Status = record.Status,
                    Execution = ToExecutionSnapshot(record),
                    UpdatedUtc = now
                };
            }
        }

        public MagicEvidenceLinkResult LinkEvidence(string ownerKey, string sessionId, MagicEvidenceLinkRequest request)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicEvidenceLinkRequest();
            string evidencePacketId = NormalizeId(FirstNonEmpty(request.EvidencePacketId, request.PacketId));
            string executionId = NormalizeId(request.ExecutionId);
            string actionId = NormalizeId(request.ActionId);
            string now = DateTimeOffset.UtcNow.ToString("O");
            if (string.IsNullOrWhiteSpace(evidencePacketId))
            {
                return new MagicEvidenceLinkResult
                {
                    Ok = false,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    Error = "evidencePacketId is required after the evidence packet is created.",
                    UpdatedUtc = now
                };
            }

            lock (_sync)
            {
                var links = ReadList<MagicEvidenceLinkRecord>(EvidenceLinksPath(ownerKey));
                MagicEvidenceLinkRecord link = links.FirstOrDefault(item =>
                    string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.EvidencePacketId, evidencePacketId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.ActionId, actionId, StringComparison.OrdinalIgnoreCase));
                bool created = link == null;
                if (link == null)
                {
                    link = new MagicEvidenceLinkRecord
                    {
                        Id = "evidence_link_" + ShortHash(ownerKey + "|" + sessionId + "|" + evidencePacketId + "|" + executionId + "|" + actionId),
                        OwnerKey = ownerKey,
                        SessionId = sessionId,
                        EvidencePacketId = evidencePacketId,
                        ExecutionId = executionId,
                        ActionId = actionId,
                        CreatedUtc = now
                    };
                }

                link.PlanId = NormalizeId(FirstNonEmpty(request.PlanId, link.PlanId));
                link.ProgressId = NormalizeId(FirstNonEmpty(request.ProgressId, link.ProgressId));
                link.SourceKind = NormalizeToken(FirstNonEmpty(request.SourceKind, link.SourceKind), "execution");
                link.SourceRef = NormalizeText(FirstNonEmpty(request.SourceRef, link.SourceRef), 1000, "");
                link.Summary = NormalizeText(FirstNonEmpty(request.Summary, link.Summary), 4000, "");
                link.Classification = NormalizeToken(FirstNonEmpty(request.Classification, link.Classification), "private");
                link.UpdatedUtc = now;

                links.RemoveAll(item => string.Equals(item.Id, link.Id, StringComparison.OrdinalIgnoreCase));
                links.Add(link);
                WriteList(EvidenceLinksPath(ownerKey), links);

                return new MagicEvidenceLinkResult
                {
                    Ok = true,
                    OwnerKey = ownerKey,
                    SessionId = sessionId,
                    Created = created,
                    Link = ToEvidenceLinkSnapshot(link),
                    UpdatedUtc = now,
                    StoragePath = EvidenceLinksPath(ownerKey)
                };
            }
        }

        public async Task<MagicPlanExecutionResult> ExecutePlanAsync(
            string ownerKey,
            string sessionId,
            MagicPlanExecutionRequest request,
            Func<MagicPlanExecutionAction, Task<MagicPlanActionResult>> executor)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            request = request ?? new MagicPlanExecutionRequest();
            AppendJsonActions(request);

            string now = DateTimeOffset.UtcNow.ToString("O");
            string executionMode = NormalizeExecutionMode(request.ExecutionMode);
            int maxSteps = NormalizeMaxSteps(executionMode, request.MaxSteps);
            MagicPlanExecutionRecord existingRecord = null;
            string executionId = NormalizeId(request.ExecutionId);
            if (!string.IsNullOrWhiteSpace(executionId))
            {
                lock (_sync)
                {
                    existingRecord = ReadList<MagicPlanExecutionRecord>(ExecutionsPath(ownerKey))
                        .FirstOrDefault(item => string.Equals(item.Id, executionId, StringComparison.OrdinalIgnoreCase));
                }
            }

            var actions = NormalizeExecutionActions(request.Actions);
            if (actions.Count == 0 && existingRecord != null && existingRecord.Actions != null && existingRecord.Actions.Count > 0)
                actions = NormalizeExecutionActions(existingRecord.Actions);

            MagicActionValidationResult validation = ValidateExecutionActions(actions);
            MagicExecutionPreviewPacket preview = BuildExecutionPreviewPacket(actions, validation);
            var selectedActions = validation.Ok
                ? SelectExecutionActions(actions, request.NextStepId, executionMode, maxSteps)
                : new List<MagicPlanExecutionAction>();
            var results = new List<MagicPlanActionResult>();
            string status = "preview";
            string blocker = "";

            if (!validation.Ok)
            {
                status = "invalid";
                blocker = validation.Errors.Count == 0 ? "Execution action validation failed." : validation.Errors[0].Message;
                foreach (MagicActionValidationIssue issue in validation.Errors)
                {
                    results.Add(new MagicPlanActionResult
                    {
                        ActionId = issue.ActionId,
                        Type = "validation",
                        Summary = issue.Code,
                        Status = "blocked",
                        Output = issue.Message,
                        CompletedUtc = now
                    });
                }
            }
            else if (existingRecord != null && existingRecord.Paused && executionMode != "preview")
            {
                status = "paused";
                blocker = "Execution is paused. Resume it with sockjackdml_execution_control before applying more steps.";
            }
            else if (executionMode == "preview")
            {
                foreach (MagicPlanExecutionAction action in selectedActions)
                {
                    results.Add(new MagicPlanActionResult
                    {
                        ActionId = action.Id,
                        Type = action.Type,
                        Summary = FirstNonEmpty(action.Summary, action.Description, action.Type),
                        Status = "preview",
                        Output = "Preview only; no project mutation was attempted."
                    });
                }
                status = selectedActions.Count == 0 ? "needs_action_details" : "preview";
                if (selectedActions.Count == 0)
                    blocker = "No executable actions were supplied. Provide actionsJson or actions with bounded file, Git, terminal, or progress steps.";
            }
            else if (selectedActions.Count == 0)
            {
                status = "blocked";
                blocker = "No executable actions were supplied. Provide actionsJson or actions with bounded file, Git, terminal, or progress steps.";
            }
            else
            {
                status = "completed";
                foreach (MagicPlanExecutionAction action in selectedActions)
                {
                    MagicPlanActionResult result;
                    if (executor == null)
                    {
                        result = new MagicPlanActionResult
                        {
                            ActionId = action.Id,
                            Type = action.Type,
                            Summary = FirstNonEmpty(action.Summary, action.Description, action.Type),
                            Status = "blocked",
                            Output = "No execution callback was provided."
                        };
                    }
                    else
                    {
                        result = await executor(action).ConfigureAwait(false);
                        if (result == null)
                        {
                            result = new MagicPlanActionResult
                            {
                                ActionId = action.Id,
                                Type = action.Type,
                                Summary = FirstNonEmpty(action.Summary, action.Description, action.Type),
                                Status = "failed",
                                Output = "Execution callback returned no result."
                            };
                        }
                    }

                    if (string.IsNullOrWhiteSpace(result.ActionId))
                        result.ActionId = action.Id;
                    if (string.IsNullOrWhiteSpace(result.Type))
                        result.Type = action.Type;
                    if (string.IsNullOrWhiteSpace(result.Summary))
                        result.Summary = FirstNonEmpty(action.Summary, action.Description, action.Type);
                    if (string.IsNullOrWhiteSpace(result.CompletedUtc))
                        result.CompletedUtc = DateTimeOffset.UtcNow.ToString("O");
                    results.Add(result);

                    action.Status = string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase)
                        ? "completed"
                        : string.Equals(result.Status, "skipped", StringComparison.OrdinalIgnoreCase)
                            ? "skipped"
                            : string.Equals(result.Status, "blocked", StringComparison.OrdinalIgnoreCase)
                                ? "blocked"
                                : "failed";
                    if (!string.IsNullOrWhiteSpace(result.EvidencePacketId))
                        action.EvidencePacketId = result.EvidencePacketId;

                    if (!string.Equals(result.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(result.Status, "skipped", StringComparison.OrdinalIgnoreCase))
                    {
                        status = string.Equals(result.Status, "blocked", StringComparison.OrdinalIgnoreCase) ? "blocked" : "failed";
                        blocker = result.Output;
                        break;
                    }
                }

                if (status == "completed" && CountCompletedActions(new MagicPlanExecutionRecord { Actions = actions }) < actions.Count)
                    status = "partial";
            }

            MagicProgressDocumentResult progress = CreateOrUpdateProgress(ownerKey, sessionId, BuildExecutionProgressRequest(request, status, blocker, results, now));
            var storedResults = existingRecord == null || existingRecord.Results == null
                ? new List<MagicPlanActionResult>()
                : new List<MagicPlanActionResult>(existingRecord.Results);
            storedResults.AddRange(results);
            MagicPlanExecutionRecord record = new MagicPlanExecutionRecord
            {
                Id = string.IsNullOrWhiteSpace(request.ExecutionId) ? "exec_" + ShortHash(ownerKey + "|" + sessionId + "|" + request.PlanId + "|" + now) : NormalizeId(request.ExecutionId),
                OwnerKey = ownerKey,
                SessionId = sessionId,
                PlanId = NormalizeId(FirstNonEmpty(request.PlanId, existingRecord == null ? "" : existingRecord.PlanId)),
                ExecutionMode = executionMode,
                Status = status,
                Blocker = blocker,
                Actions = actions,
                Results = storedResults,
                ProgressId = progress.ProgressId,
                Paused = status == "paused",
                Validation = validation,
                Preview = preview,
                CreatedUtc = existingRecord == null || string.IsNullOrWhiteSpace(existingRecord.CreatedUtc) ? now : existingRecord.CreatedUtc,
                UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
            record.CursorIndex = CountCompletedActions(record);
            MagicPlanExecutionAction nextAction = NextPendingAction(record);

            lock (_sync)
            {
                var executions = ReadList<MagicPlanExecutionRecord>(ExecutionsPath(ownerKey));
                executions.RemoveAll(item => string.Equals(item.Id, record.Id, StringComparison.OrdinalIgnoreCase));
                executions.Add(record);
                WriteList(ExecutionsPath(ownerKey), executions);
            }

            return new MagicPlanExecutionResult
            {
                Ok = status != "failed" && status != "invalid",
                OwnerKey = ownerKey,
                SessionId = sessionId,
                ExecutionId = record.Id,
                PlanId = record.PlanId,
                ExecutionMode = executionMode,
                Status = status,
                Blocker = blocker,
                ActionsAttempted = results.Count,
                Results = results,
                Progress = progress,
                Validation = validation,
                Preview = preview,
                CursorIndex = record.CursorIndex,
                Paused = record.Paused,
                NextActionId = nextAction == null ? "" : nextAction.Id,
                NextActionSummary = nextAction == null ? "" : FirstNonEmpty(nextAction.Summary, nextAction.Description, nextAction.Type),
                UpdatedUtc = record.UpdatedUtc,
                StoragePath = ExecutionsPath(ownerKey)
            };
        }

        public static string SanitizeProgressFileName(string projectOrSessionName, string featureName)
        {
            string project = SanitizeFileSegment(projectOrSessionName, "Session", 72);
            string feature = SanitizeFileSegment(featureName, "Feature", 72);
            return project + "_" + feature + "_Progress.md";
        }

        private MagicProgressDocumentRequest BuildExecutionProgressRequest(MagicPlanExecutionRequest request, string status, string blocker, List<MagicPlanActionResult> results, string now)
        {
            var logs = new List<MagicProgressLogEntry>();
            foreach (MagicPlanActionResult result in results)
            {
                logs.Add(new MagicProgressLogEntry
                {
                    TimestampUtc = string.IsNullOrWhiteSpace(result.CompletedUtc) ? now : result.CompletedUtc,
                    Status = result.Status,
                    Detail = FirstNonEmpty(result.Summary, result.Type, result.ActionId) + ": " + NormalizeText(result.Output, 1200, "")
                });
            }
            if (logs.Count == 0)
            {
                logs.Add(new MagicProgressLogEntry
                {
                    TimestampUtc = now,
                    Status = status,
                    Detail = string.IsNullOrWhiteSpace(blocker) ? "Execution packet prepared." : blocker
                });
            }

            string currentStatus = status == "completed"
                ? "Execution completed for the supplied bounded step(s)."
                : status == "partial"
                    ? "Auto approval-gated execution stopped at the configured max-step cap."
                    : status == "preview"
                        ? "Execution preview prepared; no project mutation was attempted."
                        : "Execution " + status + (string.IsNullOrWhiteSpace(blocker) ? "." : ": " + blocker);

            return new MagicProgressDocumentRequest
            {
                PlanId = request.PlanId,
                ProjectOrSessionName = request.ProjectOrSessionName,
                FeatureName = request.FeatureName,
                ProgressPath = request.ProgressPath,
                CurrentStatus = currentStatus,
                NextStep = status == "completed" ? "Verify the change and record test results." : FirstNonEmpty(blocker, "Provide the next approved bounded action."),
                VerificationNotes = request.VerificationNotes,
                LogEntries = logs,
                OverallPercent = status == "completed" ? 85 : (int?)null
            };
        }

        private List<MagicPlanExecutionAction> SelectExecutionActions(List<MagicPlanExecutionAction> actions, string nextStepId, string executionMode, int maxSteps)
        {
            if (actions == null || actions.Count == 0)
                return new List<MagicPlanExecutionAction>();

            IEnumerable<MagicPlanExecutionAction> pending = actions.Where(IsActionPending);
            if (!string.IsNullOrWhiteSpace(nextStepId))
            {
                MagicPlanExecutionAction selected = pending.FirstOrDefault(action => string.Equals(action.Id, nextStepId, StringComparison.OrdinalIgnoreCase));
                if (selected != null)
                    return new List<MagicPlanExecutionAction> { selected };
            }

            if (executionMode == "approved_apply")
                return pending.Take(1).ToList();
            return pending.Take(maxSteps).ToList();
        }

        public MagicActionValidationResult ValidateExecutionActions(List<MagicPlanExecutionAction> actions)
        {
            var result = new MagicActionValidationResult();
            foreach (MagicPlanExecutionAction action in actions ?? new List<MagicPlanExecutionAction>())
            {
                if (action == null)
                    continue;
                string type = NormalizeToken(FirstNonEmpty(action.Type, action.Kind, action.ToolName, action.Operation), "progress");
                bool hasArgumentsJson = !string.IsNullOrWhiteSpace(action.ArgumentsJson);
                switch (type)
                {
                    case "progress":
                    case "note":
                    case "documentation":
                    case "progress_document":
                        break;
                    case "write_file":
                    case "create_file":
                    case "vs_write_file":
                        Require(action, result, "path", FirstNonEmpty(action.Path), "File write actions require path.");
                        Require(action, result, "content", FirstNonEmpty(action.Content, hasArgumentsJson ? action.ArgumentsJson : ""), "File write actions require content or argumentsJson.");
                        break;
                    case "replace_in_file":
                    case "edit_file":
                    case "vs_replace_in_file":
                        Require(action, result, "path", FirstNonEmpty(action.Path), "File edit actions require path.");
                        Require(action, result, "oldString", FirstNonEmpty(action.OldString, hasArgumentsJson ? action.ArgumentsJson : ""), "File edit actions require oldString or argumentsJson.");
                        Require(action, result, "newString", FirstNonEmpty(action.NewString, hasArgumentsJson ? action.ArgumentsJson : ""), "File edit actions require newString or argumentsJson.");
                        break;
                    case "copy_file":
                    case "vs_copy_file":
                        Require(action, result, "path", FirstNonEmpty(action.Path), "File copy actions require path.");
                        Require(action, result, "newPath", FirstNonEmpty(action.NewPath, action.NewName, hasArgumentsJson ? action.ArgumentsJson : ""), "File copy actions require newPath, newName, or argumentsJson.");
                        break;
                    case "rename_file":
                    case "move_file":
                    case "vs_rename_file":
                        Require(action, result, "path", FirstNonEmpty(action.Path), "File rename actions require path.");
                        Require(action, result, "newPath", FirstNonEmpty(action.NewPath, action.NewName, hasArgumentsJson ? action.ArgumentsJson : ""), "File rename actions require newPath, newName, or argumentsJson.");
                        break;
                    case "delete_file":
                    case "vs_delete_file":
                        Require(action, result, "path", FirstNonEmpty(action.Path, hasArgumentsJson ? action.ArgumentsJson : ""), "File delete actions require path or argumentsJson.");
                        break;
                    case "terminal_command":
                    case "command":
                    case "shell":
                    case "run_command_in_terminal":
                        Require(action, result, "command", FirstNonEmpty(action.Command, hasArgumentsJson ? action.ArgumentsJson : ""), "Terminal actions require command or argumentsJson.");
                        break;
                    case "git":
                    case "git_status":
                    case "git_add":
                    case "git_commit":
                    case "git_branch":
                    case "git_checkout":
                    case "git_push":
                    case "git_pull":
                        Require(action, result, "operation", FirstNonEmpty(action.Operation, action.Command, hasArgumentsJson ? action.ArgumentsJson : ""), "Git actions require operation, command, or argumentsJson.");
                        break;
                    default:
                        AddValidationIssue(result.Errors, action, "unsupported_action_type", "Unsupported action type: " + type, "type");
                        AddRepairField(result, "type");
                        break;
                }

                if (ActionRequiresApproval(action))
                {
                    AddValidationIssue(result.Warnings, action, "approval_required", "This action must pass the existing file, terminal, or Git approval gates.", "");
                }
            }

            if (actions == null || actions.Count == 0)
            {
                AddValidationIssue(result.Errors, null, "missing_actions", "No execution actions were supplied.", "actionsJson");
                AddRepairField(result, "actionsJson");
            }

            result.Ok = result.Errors.Count == 0;
            return result;
        }

        private static void Require(MagicPlanExecutionAction action, MagicActionValidationResult result, string field, string value, string message)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return;
            AddValidationIssue(result.Errors, action, "missing_" + field, message, field);
            AddRepairField(result, field);
        }

        private static void AddValidationIssue(List<MagicActionValidationIssue> list, MagicPlanExecutionAction action, string code, string message, string field)
        {
            list.Add(new MagicActionValidationIssue
            {
                ActionId = action == null ? "" : action.Id,
                Code = code,
                Message = message,
                Field = field
            });
        }

        private static void AddRepairField(MagicActionValidationResult result, string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return;
            if (!result.SuggestedRepairFields.Any(item => string.Equals(item, field, StringComparison.OrdinalIgnoreCase)))
                result.SuggestedRepairFields.Add(field);
        }

        private MagicExecutionPreviewPacket BuildExecutionPreviewPacket(List<MagicPlanExecutionAction> actions, MagicActionValidationResult validation)
        {
            var preview = new MagicExecutionPreviewPacket();
            foreach (MagicPlanExecutionAction action in actions ?? new List<MagicPlanExecutionAction>())
            {
                if (action == null)
                    continue;
                string type = NormalizeToken(FirstNonEmpty(action.Type, action.Kind, action.ToolName, action.Operation), "progress");
                string path = FirstNonEmpty(action.Path, action.NewPath, action.NewName);
                if (type.Contains("write") || type.Contains("create"))
                    AddDistinct(preview.FilesToWrite, new[] { FirstNonEmpty(path, action.Summary, action.Id) });
                else if (type.Contains("replace") || type.Contains("edit") || type.Contains("rename") || type.Contains("move") || type.Contains("delete"))
                    AddDistinct(preview.FilesToEdit, new[] { FirstNonEmpty(path, action.Summary, action.Id) });
                if (type.Contains("terminal") || type == "command" || type == "shell" || type.Contains("run_command"))
                    AddDistinct(preview.CommandsToRun, new[] { FirstNonEmpty(action.Command, action.Summary, action.Id) });
                if (type == "git" || type.StartsWith("git_", StringComparison.OrdinalIgnoreCase))
                    AddDistinct(preview.GitMutations, new[] { FirstNonEmpty(action.Operation, action.Command, action.Summary, action.Id) });
                if (!string.IsNullOrWhiteSpace(action.RiskNotes))
                    AddDistinct(preview.RiskNotes, new[] { action.RiskNotes });
                if (ActionRequiresApproval(action))
                    preview.ApprovalRequired = true;
            }

            foreach (MagicActionValidationIssue issue in validation == null ? new List<MagicActionValidationIssue>() : validation.Warnings)
                AddDistinct(preview.RiskNotes, new[] { issue.Message });
            return preview;
        }

        private static bool IsActionPending(MagicPlanExecutionAction action)
        {
            if (action == null)
                return false;
            string status = (action.Status ?? "").Trim();
            return status.Length == 0 ||
                   status.Equals("pending", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("ready", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("todo", StringComparison.OrdinalIgnoreCase);
        }

        private List<MagicPlanExecutionAction> NormalizeExecutionActions(List<MagicPlanExecutionAction> actions)
        {
            var normalized = new List<MagicPlanExecutionAction>();
            int index = 1;
            foreach (MagicPlanExecutionAction action in actions ?? new List<MagicPlanExecutionAction>())
            {
                if (action == null)
                    continue;
                action.Id = NormalizeText(FirstNonEmpty(action.Id, action.StepId, "step_" + index.ToString(CultureInfo.InvariantCulture)), 80, "step_" + index.ToString(CultureInfo.InvariantCulture));
                action.Type = NormalizeToken(FirstNonEmpty(action.Type, action.Kind, action.ToolName), "progress");
                action.Summary = NormalizeText(action.Summary, 500, "");
                action.Description = NormalizeText(action.Description, 1000, "");
                action.Status = NormalizeToken(action.Status, "pending");
                normalized.Add(action);
                index++;
            }
            return normalized;
        }

        private static int NormalizeMaxSteps(string executionMode, int maxSteps)
        {
            if (executionMode == "approved_apply")
                return 1;
            if (maxSteps <= 0)
                maxSteps = executionMode == "auto_approval_gated" ? 5 : 20;
            return Math.Max(1, Math.Min(20, maxSteps));
        }

        private static string NormalizeExecutionMode(string executionMode)
        {
            executionMode = NormalizeToken(executionMode, "preview");
            switch (executionMode)
            {
                case "preview":
                case "approved_apply":
                case "auto_approval_gated":
                    return executionMode;
                default:
                    return "preview";
            }
        }

        private void AppendJsonStages(MagicProgressDocumentRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.StagesJson))
                return;
            List<MagicProgressStage> parsed = ReadJsonList<MagicProgressStage>(request.StagesJson);
            if (parsed.Count == 0)
                return;
            if (request.Stages == null)
                request.Stages = new List<MagicProgressStage>();
            request.Stages.AddRange(parsed);
        }

        private void AppendJsonLogEntries(MagicProgressDocumentRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.LogEntriesJson))
                return;
            List<MagicProgressLogEntry> parsed = ReadJsonList<MagicProgressLogEntry>(request.LogEntriesJson);
            if (parsed.Count == 0)
                return;
            if (request.LogEntries == null)
                request.LogEntries = new List<MagicProgressLogEntry>();
            request.LogEntries.AddRange(parsed);
        }

        private void AppendJsonActions(MagicPlanExecutionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ActionsJson))
                return;
            List<MagicPlanExecutionAction> parsed = ReadJsonList<MagicPlanExecutionAction>(request.ActionsJson);
            if (parsed.Count == 0)
                return;
            if (request.Actions == null)
                request.Actions = new List<MagicPlanExecutionAction>();
            request.Actions.AddRange(parsed);
        }

        private List<T> ReadJsonList<T>(string json)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(json))
                {
                    if (document.RootElement.ValueKind == JsonValueKind.Array)
                        return JsonSerializer.Deserialize<List<T>>(document.RootElement.GetRawText(), _jsonOptions) ?? new List<T>();
                    if (document.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        T item = JsonSerializer.Deserialize<T>(document.RootElement.GetRawText(), _jsonOptions);
                        var list = new List<T>();
                        if (item != null)
                            list.Add(item);
                        return list;
                    }
                }
            }
            catch
            {
            }
            return new List<T>();
        }

        private T ReadRequest<T>(string json) where T : class
        {
            if (string.IsNullOrWhiteSpace(json))
                json = "{}";
            return JsonSerializer.Deserialize<T>(json, _jsonOptions);
        }

        private MagicPlanRecord FindPlan(List<MagicPlanRecord> plans, string sessionId, MagicPlanCreateRequest request)
        {
            string planId = NormalizeId(request.PlanId);
            if (!string.IsNullOrWhiteSpace(planId))
                return plans.FirstOrDefault(plan => string.Equals(plan.Id, planId, StringComparison.OrdinalIgnoreCase));

            string project = FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName);
            string feature = FirstNonEmpty(request.FeatureName, request.Feature);
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(feature))
                return null;

            return plans
                .Where(plan => string.Equals(plan.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .Where(plan => string.Equals(plan.ProjectOrSessionName, project, StringComparison.OrdinalIgnoreCase))
                .Where(plan => string.Equals(plan.FeatureName, feature, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(plan => FirstNonEmpty(plan.UpdatedUtc, plan.CreatedUtc))
                .FirstOrDefault();
        }

        private static MagicPlanRecord FindPlanById(List<MagicPlanRecord> plans, string planId)
        {
            planId = NormalizeId(planId);
            return string.IsNullOrWhiteSpace(planId)
                ? null
                : plans.FirstOrDefault(plan => string.Equals(plan.Id, planId, StringComparison.OrdinalIgnoreCase));
        }

        private MagicProgressDocumentRecord FindProgress(List<MagicProgressDocumentRecord> records, string sessionId, MagicProgressDocumentRequest request, MagicPlanRecord plan)
        {
            string progressId = NormalizeId(request.ProgressId);
            if (!string.IsNullOrWhiteSpace(progressId))
                return records.FirstOrDefault(record => string.Equals(record.Id, progressId, StringComparison.OrdinalIgnoreCase));

            string planId = NormalizeId(FirstNonEmpty(request.PlanId, plan == null ? "" : plan.Id));
            if (!string.IsNullOrWhiteSpace(planId))
            {
                MagicProgressDocumentRecord byPlan = records
                    .Where(record => string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                    .Where(record => string.Equals(record.PlanId, planId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(record => FirstNonEmpty(record.UpdatedUtc, record.CreatedUtc))
                    .FirstOrDefault();
                if (byPlan != null)
                    return byPlan;
            }

            string project = FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName, plan == null ? "" : plan.ProjectOrSessionName);
            string feature = FirstNonEmpty(request.FeatureName, request.Feature, plan == null ? "" : plan.FeatureName);
            if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(feature))
                return null;

            return records
                .Where(record => string.Equals(record.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .Where(record => string.Equals(record.ProjectOrSessionName, project, StringComparison.OrdinalIgnoreCase))
                .Where(record => string.Equals(record.FeatureName, feature, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => FirstNonEmpty(record.UpdatedUtc, record.CreatedUtc))
                .FirstOrDefault();
        }

        private static List<string> BuildPlanQuestions(MagicPlanRecord record)
        {
            var questions = new List<string>();
            if (string.IsNullOrWhiteSpace(record.ProjectOrSessionName) || record.ProjectOrSessionName == "Session")
                questions.Add("What project or session name should anchor this plan?");
            if (string.IsNullOrWhiteSpace(record.FeatureName) || record.FeatureName == "Feature")
                questions.Add("What feature name should be used for the plan and progress tracker?");
            if (string.IsNullOrWhiteSpace(record.Goal))
                questions.Add("What is the concrete goal or behavior this plan should implement?");
            if (record.Requirements == null || record.Requirements.Count == 0)
                questions.Add("What acceptance criteria or required changes should the plan satisfy?");
            return questions;
        }

        private static List<MagicPlanStep> BuildDefaultPlanSteps(MagicPlanRecord record)
        {
            string feature = string.IsNullOrWhiteSpace(record.FeatureName) ? "the feature" : record.FeatureName;
            return new List<MagicPlanStep>
            {
                new MagicPlanStep { Id = "step_1", Title = "Orient in the existing system", Detail = "Inspect the relevant services, routes, schemas, and tests before editing.", Status = "pending" },
                new MagicPlanStep { Id = "step_2", Title = "Implement " + feature, Detail = "Make bounded code changes that follow existing local patterns.", Status = "pending" },
                new MagicPlanStep { Id = "step_3", Title = "Create or update the progress tracker", Detail = "Keep the human-readable progress markdown and durable SockJackDml state in sync.", Status = "pending" },
                new MagicPlanStep { Id = "step_4", Title = "Verify and record results", Detail = "Run focused builds or tests and capture verification notes.", Status = "pending" }
            };
        }

        private static List<MagicPlanStep> NormalizePlanSteps(List<MagicPlanStep> steps)
        {
            var normalized = new List<MagicPlanStep>();
            int index = 1;
            foreach (MagicPlanStep step in steps ?? new List<MagicPlanStep>())
            {
                if (step == null)
                    continue;
                normalized.Add(new MagicPlanStep
                {
                    Id = NormalizeText(FirstNonEmpty(step.Id, "step_" + index.ToString(CultureInfo.InvariantCulture)), 80, "step_" + index.ToString(CultureInfo.InvariantCulture)),
                    Title = NormalizeText(FirstNonEmpty(step.Title, step.Name), 200, "Step " + index.ToString(CultureInfo.InvariantCulture)),
                    Detail = NormalizeText(FirstNonEmpty(step.Detail, step.Description), 2000, ""),
                    Status = NormalizeToken(step.Status, "pending")
                });
                index++;
            }
            return normalized;
        }

        private static List<MagicProgressStage> MergeStages(List<MagicProgressStage> existing, List<MagicProgressStage> incoming)
        {
            var stages = new List<MagicProgressStage>();
            foreach (MagicProgressStage stage in existing ?? new List<MagicProgressStage>())
                stages.Add(CopyStage(stage));

            foreach (MagicProgressStage stage in incoming ?? new List<MagicProgressStage>())
            {
                if (stage == null)
                    continue;
                string name = NormalizeText(FirstNonEmpty(stage.Name, stage.Title), 200, "Stage");
                MagicProgressStage target = stages.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    target = new MagicProgressStage { Name = name };
                    stages.Add(target);
                }
                target.Status = NormalizeToken(stage.Status, string.IsNullOrWhiteSpace(target.Status) ? "pending" : target.Status);
                target.Percent = ClampPercent(stage.Percent);
                target.Notes = NormalizeText(FirstNonEmpty(stage.Notes, stage.Detail, target.Notes), 2000, "");
            }

            return stages;
        }

        private static MagicProgressStage CopyStage(MagicProgressStage stage)
        {
            return new MagicProgressStage
            {
                Name = NormalizeText(FirstNonEmpty(stage == null ? "" : stage.Name, stage == null ? "" : stage.Title), 200, "Stage"),
                Status = NormalizeToken(stage == null ? "" : stage.Status, "pending"),
                Percent = ClampPercent(stage == null ? 0 : stage.Percent),
                Notes = NormalizeText(FirstNonEmpty(stage == null ? "" : stage.Notes, stage == null ? "" : stage.Detail), 2000, "")
            };
        }

        private static int InferOverallPercent(MagicProgressDocumentRecord record)
        {
            if (record == null)
                return 0;
            if (record.Stages != null && record.Stages.Count > 0)
                return ClampPercent((int)Math.Round(record.Stages.Average(stage => ClampPercent(stage.Percent))));
            return ClampPercent(record.OverallPercent);
        }

        private static void AddProgressLog(MagicProgressDocumentRecord record, MagicProgressLogEntry entry, string defaultTimestamp)
        {
            if (record == null || entry == null)
                return;
            string detail = NormalizeText(entry.Detail, 3000, "");
            if (string.IsNullOrWhiteSpace(detail))
                return;
            record.LogEntries.Add(new MagicProgressLogEntry
            {
                TimestampUtc = NormalizeText(FirstNonEmpty(entry.TimestampUtc, defaultTimestamp), 80, defaultTimestamp),
                Status = NormalizeToken(entry.Status, "note"),
                Detail = detail
            });
        }

        private static string RenderPlanMarkdown(MagicPlanRecord record)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(record.ProjectOrSessionName).Append(" - ").Append(record.FeatureName).Append(" Plan\n\n");
            sb.Append("## Summary\n");
            sb.Append(string.IsNullOrWhiteSpace(record.Goal) ? "Define and implement the requested feature." : record.Goal).Append("\n\n");

            if (!string.IsNullOrWhiteSpace(record.Context))
            {
                sb.Append("## Context\n").Append(record.Context).Append("\n\n");
            }

            AppendMarkdownList(sb, "Requirements", record.Requirements);
            AppendMarkdownList(sb, "Constraints", record.Constraints);
            AppendMarkdownList(sb, "Known Files", record.KnownFiles);

            sb.Append("## Implementation Steps\n");
            foreach (MagicPlanStep step in record.Steps)
            {
                sb.Append("- [ ] ").Append(step.Title);
                if (!string.IsNullOrWhiteSpace(step.Detail))
                    sb.Append(": ").Append(step.Detail);
                sb.Append("\n");
            }
            sb.Append("\n");

            sb.Append("## Approval Gates\n");
            sb.Append("- File writes must use the existing VS/file authorization path.\n");
            sb.Append("- Terminal commands must use the terminal approval queue.\n");
            sb.Append("- Git mutations must use the Git service approval gate.\n\n");

            sb.Append("## Verification\n");
            sb.Append("- Run focused builds, endpoint checks, or tests that cover changed surfaces.\n");
            sb.Append("- Record verification notes in the paired progress tracker.\n\n");
            sb.Append("_Plan id: `").Append(record.Id).Append("`. Updated: ").Append(record.UpdatedUtc).Append("_\n");
            return sb.ToString();
        }

        private static string RenderProgressMarkdown(MagicProgressDocumentRecord record)
        {
            var sb = new StringBuilder();
            sb.Append("# ").Append(record.ProjectOrSessionName).Append(" - ").Append(record.FeatureName).Append(" Progress\n\n");
            sb.Append("- Plan: `").Append(string.IsNullOrWhiteSpace(record.PlanId) ? "none" : record.PlanId).Append("`\n");
            sb.Append("- Status: ").Append(string.IsNullOrWhiteSpace(record.CurrentStatus) ? "not started" : record.CurrentStatus).Append("\n");
            sb.Append("- Overall: ").Append(ClampPercent(record.OverallPercent).ToString(CultureInfo.InvariantCulture)).Append("%\n");
            sb.Append("- Updated: ").Append(record.UpdatedUtc).Append("\n\n");

            if (!string.IsNullOrWhiteSpace(record.Summary))
                sb.Append("## Summary\n").Append(record.Summary).Append("\n\n");

            sb.Append("## Stages\n");
            sb.Append("| Stage | Status | Percent | Notes |\n");
            sb.Append("| --- | --- | ---: | --- |\n");
            foreach (MagicProgressStage stage in record.Stages)
            {
                sb.Append("| ")
                  .Append(EscapeTableCell(stage.Name))
                  .Append(" | ")
                  .Append(EscapeTableCell(stage.Status))
                  .Append(" | ")
                  .Append(ClampPercent(stage.Percent).ToString(CultureInfo.InvariantCulture))
                  .Append("% | ")
                  .Append(EscapeTableCell(stage.Notes))
                  .Append(" |\n");
            }
            if (record.Stages.Count == 0)
                sb.Append("| Implementation | pending | 0% | Awaiting first tracked step. |\n");
            sb.Append("\n");

            sb.Append("## Progress Log\n");
            foreach (MagicProgressLogEntry entry in record.LogEntries.OrderBy(item => item.TimestampUtc))
            {
                sb.Append("- ").Append(FirstNonEmpty(entry.TimestampUtc, record.UpdatedUtc))
                  .Append(" - ").Append(FirstNonEmpty(entry.Status, "note"))
                  .Append(" - ").Append(entry.Detail).Append("\n");
            }
            sb.Append("\n");

            sb.Append("## Next Step\n");
            sb.Append(string.IsNullOrWhiteSpace(record.NextStep) ? "Define the next bounded implementation step." : record.NextStep).Append("\n\n");

            sb.Append("## Verification Notes\n");
            sb.Append(string.IsNullOrWhiteSpace(record.VerificationNotes) ? "No verification recorded yet." : record.VerificationNotes).Append("\n");
            return sb.ToString();
        }

        private static void AppendMarkdownList(StringBuilder sb, string title, List<string> values)
        {
            if (values == null || values.Count == 0)
                return;
            sb.Append("## ").Append(title).Append("\n");
            foreach (string value in values)
                sb.Append("- ").Append(value).Append("\n");
            sb.Append("\n");
        }

        private List<T> ReadList<T>(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new List<T>();
                string json = File.ReadAllText(path, Encoding.UTF8);
                return JsonSerializer.Deserialize<List<T>>(string.IsNullOrWhiteSpace(json) ? "[]" : json, _jsonOptions) ?? new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }

        private void WriteList<T>(string path, List<T> items)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(items ?? new List<T>(), _jsonOptions), Encoding.UTF8);
        }

        private string PlansPath(string ownerKey)
        {
            return Path.Combine(OwnerWorkflowDirectory(ownerKey), "plans.json");
        }

        private string ProgressPath(string ownerKey)
        {
            return Path.Combine(OwnerWorkflowDirectory(ownerKey), "progress.json");
        }

        private string ExecutionsPath(string ownerKey)
        {
            return Path.Combine(OwnerWorkflowDirectory(ownerKey), "executions.json");
        }

        private string EvidenceLinksPath(string ownerKey)
        {
            return Path.Combine(OwnerWorkflowDirectory(ownerKey), "evidence-links.json");
        }

        private string OwnerWorkflowDirectory(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string directory = Path.Combine(_root, SanitizePathSegment(ownerKey) + "_" + ShortHash(ownerKey), "workflows");
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static IEnumerable<MagicPlanRecord> FilterPlans(List<MagicPlanRecord> plans, string sessionId, MagicWorkflowStatusRequest request)
        {
            IEnumerable<MagicPlanRecord> query = plans ?? new List<MagicPlanRecord>();
            request = request ?? new MagicWorkflowStatusRequest();
            if (!request.IncludeAllSessions)
                query = query.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            string planId = NormalizeId(request.PlanId);
            if (!string.IsNullOrWhiteSpace(planId))
                query = query.Where(item => string.Equals(item.Id, planId, StringComparison.OrdinalIgnoreCase));
            string project = FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName);
            if (!string.IsNullOrWhiteSpace(project))
                query = query.Where(item => string.Equals(item.ProjectOrSessionName, project, StringComparison.OrdinalIgnoreCase));
            string feature = FirstNonEmpty(request.FeatureName, request.Feature);
            if (!string.IsNullOrWhiteSpace(feature))
                query = query.Where(item => string.Equals(item.FeatureName, feature, StringComparison.OrdinalIgnoreCase));
            return query.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc));
        }

        private static IEnumerable<MagicProgressDocumentRecord> FilterProgress(List<MagicProgressDocumentRecord> records, string sessionId, MagicWorkflowStatusRequest request)
        {
            IEnumerable<MagicProgressDocumentRecord> query = records ?? new List<MagicProgressDocumentRecord>();
            request = request ?? new MagicWorkflowStatusRequest();
            if (!request.IncludeAllSessions)
                query = query.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            string progressId = NormalizeId(request.ProgressId);
            if (!string.IsNullOrWhiteSpace(progressId))
                query = query.Where(item => string.Equals(item.Id, progressId, StringComparison.OrdinalIgnoreCase));
            string planId = NormalizeId(request.PlanId);
            if (!string.IsNullOrWhiteSpace(planId))
                query = query.Where(item => string.Equals(item.PlanId, planId, StringComparison.OrdinalIgnoreCase));
            string project = FirstNonEmpty(request.ProjectOrSessionName, request.ProjectName, request.SessionName);
            if (!string.IsNullOrWhiteSpace(project))
                query = query.Where(item => string.Equals(item.ProjectOrSessionName, project, StringComparison.OrdinalIgnoreCase));
            string feature = FirstNonEmpty(request.FeatureName, request.Feature);
            if (!string.IsNullOrWhiteSpace(feature))
                query = query.Where(item => string.Equals(item.FeatureName, feature, StringComparison.OrdinalIgnoreCase));
            string path = FirstNonEmpty(request.ProgressPath, request.Path);
            if (!string.IsNullOrWhiteSpace(path))
            {
                string fileName = Path.GetFileName(path);
                query = query.Where(item => string.Equals(item.ProgressPath, path, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(item.ProgressFileName, fileName, StringComparison.OrdinalIgnoreCase));
            }
            return query.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc));
        }

        private static IEnumerable<MagicPlanExecutionRecord> FilterExecutions(List<MagicPlanExecutionRecord> records, string sessionId, MagicWorkflowStatusRequest request)
        {
            IEnumerable<MagicPlanExecutionRecord> query = records ?? new List<MagicPlanExecutionRecord>();
            request = request ?? new MagicWorkflowStatusRequest();
            if (!request.IncludeAllSessions)
                query = query.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            string executionId = NormalizeId(request.ExecutionId);
            if (!string.IsNullOrWhiteSpace(executionId))
                query = query.Where(item => string.Equals(item.Id, executionId, StringComparison.OrdinalIgnoreCase));
            string planId = NormalizeId(request.PlanId);
            if (!string.IsNullOrWhiteSpace(planId))
                query = query.Where(item => string.Equals(item.PlanId, planId, StringComparison.OrdinalIgnoreCase));
            string progressId = NormalizeId(request.ProgressId);
            if (!string.IsNullOrWhiteSpace(progressId))
                query = query.Where(item => string.Equals(item.ProgressId, progressId, StringComparison.OrdinalIgnoreCase));
            return query.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc));
        }

        private static IEnumerable<MagicEvidenceLinkRecord> FilterEvidenceLinks(List<MagicEvidenceLinkRecord> records, string sessionId, MagicWorkflowStatusRequest request)
        {
            IEnumerable<MagicEvidenceLinkRecord> query = records ?? new List<MagicEvidenceLinkRecord>();
            request = request ?? new MagicWorkflowStatusRequest();
            if (!request.IncludeAllSessions)
                query = query.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            string planId = NormalizeId(request.PlanId);
            if (!string.IsNullOrWhiteSpace(planId))
                query = query.Where(item => string.Equals(item.PlanId, planId, StringComparison.OrdinalIgnoreCase));
            string progressId = NormalizeId(request.ProgressId);
            if (!string.IsNullOrWhiteSpace(progressId))
                query = query.Where(item => string.Equals(item.ProgressId, progressId, StringComparison.OrdinalIgnoreCase));
            string executionId = NormalizeId(request.ExecutionId);
            if (!string.IsNullOrWhiteSpace(executionId))
                query = query.Where(item => string.Equals(item.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase));
            return query.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc));
        }

        private static MagicWorkflowPlanSummary ToPlanSnapshot(MagicPlanRecord record)
        {
            return new MagicWorkflowPlanSummary
            {
                PlanId = record.Id,
                SessionId = record.SessionId,
                ProjectOrSessionName = record.ProjectOrSessionName,
                FeatureName = record.FeatureName,
                Goal = record.Goal,
                Status = record.Status,
                StepCount = record.Steps == null ? 0 : record.Steps.Count,
                UpdatedUtc = record.UpdatedUtc
            };
        }

        private static MagicWorkflowProgressSummary ToProgressSnapshot(MagicProgressDocumentRecord record)
        {
            return new MagicWorkflowProgressSummary
            {
                ProgressId = record.Id,
                PlanId = record.PlanId,
                SessionId = record.SessionId,
                ProjectOrSessionName = record.ProjectOrSessionName,
                FeatureName = record.FeatureName,
                ProgressFileName = record.ProgressFileName,
                ProgressPath = record.ProgressPath,
                OverallPercent = record.OverallPercent,
                CurrentStatus = record.CurrentStatus,
                NextStep = record.NextStep,
                UpdatedUtc = record.UpdatedUtc
            };
        }

        private static MagicWorkflowExecutionSummary ToExecutionSnapshot(MagicPlanExecutionRecord record)
        {
            MagicPlanExecutionAction next = NextPendingAction(record);
            return new MagicWorkflowExecutionSummary
            {
                ExecutionId = record.Id,
                PlanId = record.PlanId,
                ProgressId = record.ProgressId,
                SessionId = record.SessionId,
                ExecutionMode = record.ExecutionMode,
                Status = record.Status,
                Blocker = record.Blocker,
                CursorIndex = record.CursorIndex,
                Paused = record.Paused,
                ActionCount = record.Actions == null ? 0 : record.Actions.Count,
                ResultCount = record.Results == null ? 0 : record.Results.Count,
                NextActionId = next == null ? "" : next.Id,
                NextActionSummary = next == null ? "" : FirstNonEmpty(next.Summary, next.Description, next.Type),
                Preview = record.Preview,
                Validation = record.Validation,
                UpdatedUtc = record.UpdatedUtc
            };
        }

        private static MagicEvidenceLinkSnapshot ToEvidenceLinkSnapshot(MagicEvidenceLinkRecord record)
        {
            return new MagicEvidenceLinkSnapshot
            {
                LinkId = record.Id,
                EvidencePacketId = record.EvidencePacketId,
                PlanId = record.PlanId,
                ProgressId = record.ProgressId,
                ExecutionId = record.ExecutionId,
                ActionId = record.ActionId,
                SourceKind = record.SourceKind,
                SourceRef = record.SourceRef,
                Summary = record.Summary,
                Classification = record.Classification,
                UpdatedUtc = record.UpdatedUtc
            };
        }

        private static string InferNextAction(List<MagicPlanRecord> plans, List<MagicProgressDocumentRecord> progress, MagicPlanExecutionRecord latestExecution)
        {
            MagicPlanExecutionAction action = NextPendingAction(latestExecution);
            if (action != null)
                return FirstNonEmpty(action.Summary, action.Description, action.Type, action.Id);
            MagicProgressDocumentRecord latestProgress = progress == null
                ? null
                : progress.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc)).FirstOrDefault();
            if (latestProgress != null && !string.IsNullOrWhiteSpace(latestProgress.NextStep))
                return latestProgress.NextStep;
            MagicPlanRecord plan = plans == null
                ? null
                : plans.OrderByDescending(item => FirstNonEmpty(item.UpdatedUtc, item.CreatedUtc)).FirstOrDefault();
            MagicPlanStep step = plan == null || plan.Steps == null
                ? null
                : plan.Steps.FirstOrDefault(item => !string.Equals(item.Status, "completed", StringComparison.OrdinalIgnoreCase));
            return step == null ? "" : FirstNonEmpty(step.Title, step.Detail, step.Id);
        }

        private static MagicWorkflowApprovalState BuildApprovalState(MagicPlanExecutionRecord latestExecution)
        {
            var state = new MagicWorkflowApprovalState();
            if (latestExecution == null)
            {
                state.Summary = "No execution has been prepared.";
                return state;
            }

            List<MagicPlanExecutionAction> actions = latestExecution.Actions ?? new List<MagicPlanExecutionAction>();
            state.PendingActionCount = actions.Count(IsActionPending);
            state.ApprovalRequired = actions.Any(ActionRequiresApproval);
            state.HasBlockedAction = string.Equals(latestExecution.Status, "blocked", StringComparison.OrdinalIgnoreCase);
            state.Summary = state.ApprovalRequired
                ? "One or more pending actions require file, Git, or terminal approval gates."
                : "No pending gated action was detected in the latest execution.";
            return state;
        }

        private static bool ActionRequiresApproval(MagicPlanExecutionAction action)
        {
            if (action == null)
                return false;
            string type = NormalizeToken(FirstNonEmpty(action.Type, action.Kind, action.ToolName, action.Operation), "progress");
            return type.Contains("file") ||
                   type.Contains("write") ||
                   type.Contains("replace") ||
                   type.Contains("delete") ||
                   type.Contains("copy") ||
                   type.Contains("rename") ||
                   type.Contains("move") ||
                   type.Contains("terminal") ||
                   type.Contains("command") ||
                   type.Contains("shell") ||
                   type.Contains("git");
        }

        private static MagicPlanExecutionAction NextPendingAction(MagicPlanExecutionRecord record)
        {
            return record == null || record.Actions == null
                ? null
                : record.Actions.FirstOrDefault(IsActionPending);
        }

        private static int CountCompletedActions(MagicPlanExecutionRecord record)
        {
            if (record == null || record.Actions == null)
                return 0;
            int count = 0;
            foreach (MagicPlanExecutionAction action in record.Actions)
            {
                string status = NormalizeToken(action == null ? "" : action.Status, "pending");
                if (status == "completed" || status == "skipped")
                    count++;
            }
            return count;
        }

        private static int NormalizeTake(int requested, int fallback)
        {
            if (requested <= 0)
                requested = fallback;
            return Math.Max(1, Math.Min(200, requested));
        }

        private static List<string> MergeStrings(List<string> existing, IEnumerable<string> incoming)
        {
            var values = new List<string>();
            AddDistinct(values, existing);
            AddDistinct(values, incoming);
            return values;
        }

        private static void AddDistinct(List<string> values, IEnumerable<string> incoming)
        {
            if (values == null || incoming == null)
                return;
            foreach (string item in incoming)
            {
                string value = NormalizeText(item, 2000, "");
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (!values.Any(existing => string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)))
                    values.Add(value);
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "";
        }

        private static string NormalizeOwnerKey(string ownerKey)
        {
            ownerKey = (ownerKey ?? "").Trim();
            return string.IsNullOrWhiteSpace(ownerKey) ? "local" : ownerKey;
        }

        private static string NormalizeId(string id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0)
                return "";
            var sb = new StringBuilder();
            foreach (char ch in id)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                    sb.Append(ch);
                if (sb.Length >= 120)
                    break;
            }
            return sb.ToString();
        }

        private static string NormalizeToken(string value, string fallback)
        {
            value = NormalizeId(value).ToLowerInvariant();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string NormalizeText(string text, int maxLength, string fallback)
        {
            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (text.Length == 0)
                text = fallback ?? "";
            if (maxLength > 0 && text.Length > maxLength)
                text = text.Substring(0, maxLength);
            return text;
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static string EscapeTableCell(string value)
        {
            return (value ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", "<br>");
        }

        private static string SanitizeFileSegment(string value, string fallback, int maxLength)
        {
            value = NormalizeText(value, maxLength, fallback);
            var sb = new StringBuilder();
            bool lastWasSeparator = false;
            foreach (char ch in value)
            {
                bool ok = ch >= 0x20 && ch <= 0x7e && (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.');
                if (ok)
                {
                    sb.Append(ch);
                    lastWasSeparator = false;
                }
                else if (!lastWasSeparator)
                {
                    sb.Append('_');
                    lastWasSeparator = true;
                }
                if (sb.Length >= maxLength)
                    break;
            }
            string text = sb.ToString().Trim('_', '.', '-', ' ');
            return string.IsNullOrWhiteSpace(text) ? fallback : text;
        }

        private static string SanitizePathSegment(string value)
        {
            return SanitizeFileSegment(value, "owner", 80);
        }

        private static string ShortHash(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                return BitConverter.ToString(hash, 0, Math.Min(8, hash.Length)).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    internal sealed class MagicPlanCreateRequest
    {
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Feature { get; set; } = "";
        public string Goal { get; set; } = "";
        public string Objective { get; set; } = "";
        public string Context { get; set; } = "";
        public string Detail { get; set; } = "";
        public List<string> Constraints { get; set; } = new List<string>();
        public List<string> Requirements { get; set; } = new List<string>();
        public List<string> KnownFiles { get; set; } = new List<string>();
        public List<string> QuestionsAnswered { get; set; } = new List<string>();
        public string UserResponse { get; set; } = "";
        public bool Finalize { get; set; }
        public bool ReadyToFinalize { get; set; }
        public string Status { get; set; } = "";
        public List<MagicPlanStep> Steps { get; set; } = new List<MagicPlanStep>();
    }

    internal sealed class MagicPlanCreateResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public bool NeedsInput { get; set; }
        public List<string> Questions { get; set; } = new List<string>();
        public string PlanMarkdown { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Created { get; set; }
        public string UpdatedUtc { get; set; } = "";
        public string StoragePath { get; set; } = "";
        public object SessionCompatibility { get; set; }
    }

    internal sealed class MagicProgressDocumentRequest
    {
        public string ProgressId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Feature { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public string Path { get; set; } = "";
        public int? OverallPercent { get; set; }
        public string CurrentStatus { get; set; } = "";
        public string Status { get; set; } = "";
        public string Summary { get; set; } = "";
        public string NextStep { get; set; } = "";
        public string VerificationNotes { get; set; } = "";
        public string Verification { get; set; } = "";
        public List<MagicProgressStage> Stages { get; set; } = new List<MagicProgressStage>();
        public string StagesJson { get; set; } = "";
        public List<MagicProgressLogEntry> LogEntries { get; set; } = new List<MagicProgressLogEntry>();
        public string LogEntriesJson { get; set; } = "";
        public string LogEntry { get; set; } = "";
        public string LogStatus { get; set; } = "";
        public bool ReplaceLog { get; set; }
    }

    internal sealed class MagicProgressDocumentResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string ProgressFileName { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public int OverallPercent { get; set; }
        public string CurrentStatus { get; set; } = "";
        public string Markdown { get; set; } = "";
        public bool Created { get; set; }
        public bool DocumentWritten { get; set; }
        public string DocumentWriteResult { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string StoragePath { get; set; } = "";
        public object SessionCompatibility { get; set; }
    }

    internal sealed class MagicPlanExecutionRequest
    {
        public string ExecutionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string PlanMarkdown { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public string ExecutionMode { get; set; } = "preview";
        public int MaxSteps { get; set; }
        public string NextStepId { get; set; } = "";
        public List<MagicPlanExecutionAction> Actions { get; set; } = new List<MagicPlanExecutionAction>();
        public string ActionsJson { get; set; } = "";
        public string VerificationNotes { get; set; } = "";
    }

    internal sealed class MagicPlanExecutionResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ExecutionMode { get; set; } = "";
        public string Status { get; set; } = "";
        public string Blocker { get; set; } = "";
        public int ActionsAttempted { get; set; }
        public List<MagicPlanActionResult> Results { get; set; } = new List<MagicPlanActionResult>();
        public MagicProgressDocumentResult Progress { get; set; }
        public MagicActionValidationResult Validation { get; set; }
        public MagicExecutionPreviewPacket Preview { get; set; }
        public int CursorIndex { get; set; }
        public bool Paused { get; set; }
        public string NextActionId { get; set; } = "";
        public string NextActionSummary { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string StoragePath { get; set; } = "";
        public object SessionCompatibility { get; set; }
    }

    internal sealed class MagicPlanExecutionAction
    {
        public string Id { get; set; } = "";
        public string StepId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Kind { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string Operation { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
        public string Path { get; set; } = "";
        public string NewPath { get; set; } = "";
        public string NewName { get; set; } = "";
        public string Content { get; set; } = "";
        public string OldString { get; set; } = "";
        public string NewString { get; set; } = "";
        public bool Overwrite { get; set; }
        public bool ReplaceAll { get; set; }
        public string Command { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
        public string Shell { get; set; } = "";
        public int TimeoutMs { get; set; }
        public string ArgumentsJson { get; set; } = "";
        public string EvidencePacketId { get; set; } = "";
        public string RiskNotes { get; set; } = "";
    }

    internal sealed class MagicPlanActionResult
    {
        public string ActionId { get; set; } = "";
        public string Type { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Status { get; set; } = "";
        public string Output { get; set; } = "";
        public string EvidencePacketId { get; set; } = "";
        public string CompletedUtc { get; set; } = "";
    }

    internal sealed class MagicWorkflowStatusRequest
    {
        public string PlanId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Feature { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public string Path { get; set; } = "";
        public bool IncludeAllSessions { get; set; }
        public int Take { get; set; }
    }

    internal sealed class MagicWorkflowStatusResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public int PlanCount { get; set; }
        public int ProgressCount { get; set; }
        public int ExecutionCount { get; set; }
        public int EvidenceLinkCount { get; set; }
        public string LatestStatus { get; set; } = "";
        public string LatestBlocker { get; set; } = "";
        public string NextAction { get; set; } = "";
        public MagicWorkflowApprovalState ApprovalState { get; set; } = new MagicWorkflowApprovalState();
        public List<MagicWorkflowPlanSummary> Plans { get; set; } = new List<MagicWorkflowPlanSummary>();
        public List<MagicWorkflowProgressSummary> ProgressDocuments { get; set; } = new List<MagicWorkflowProgressSummary>();
        public List<MagicWorkflowExecutionSummary> Executions { get; set; } = new List<MagicWorkflowExecutionSummary>();
        public List<MagicEvidenceLinkSnapshot> EvidenceLinks { get; set; } = new List<MagicEvidenceLinkSnapshot>();
        public string UpdatedUtc { get; set; } = "";
        public string StoragePath { get; set; } = "";
    }

    internal sealed class MagicWorkflowApprovalState
    {
        public bool ApprovalRequired { get; set; }
        public int PendingActionCount { get; set; }
        public bool HasBlockedAction { get; set; }
        public string Summary { get; set; } = "";
    }

    internal sealed class MagicWorkflowPlanSummary
    {
        public string PlanId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Goal { get; set; } = "";
        public string Status { get; set; } = "";
        public int StepCount { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicWorkflowProgressSummary
    {
        public string ProgressId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string ProgressFileName { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public int OverallPercent { get; set; }
        public string CurrentStatus { get; set; } = "";
        public string NextStep { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicWorkflowExecutionSummary
    {
        public string ExecutionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ExecutionMode { get; set; } = "";
        public string Status { get; set; } = "";
        public string Blocker { get; set; } = "";
        public int CursorIndex { get; set; }
        public bool Paused { get; set; }
        public int ActionCount { get; set; }
        public int ResultCount { get; set; }
        public string NextActionId { get; set; } = "";
        public string NextActionSummary { get; set; } = "";
        public MagicExecutionPreviewPacket Preview { get; set; }
        public MagicActionValidationResult Validation { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicProgressFindRequest
    {
        public string ProgressId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string ProjectName { get; set; } = "";
        public string SessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Feature { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public string Path { get; set; } = "";
    }

    internal sealed class MagicProgressFindResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public MagicWorkflowProgressSummary Progress { get; set; }
        public string ProgressPath { get; set; } = "";
        public string ProgressFileName { get; set; } = "";
        public string Error { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicExecutionControlRequest
    {
        public string ExecutionId { get; set; } = "";
        public string Action { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    internal sealed class MagicExecutionControlResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string Action { get; set; } = "";
        public string PreviousStatus { get; set; } = "";
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";
        public MagicWorkflowExecutionSummary Execution { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicEvidenceLinkRequest
    {
        public string EvidencePacketId { get; set; } = "";
        public string PacketId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Classification { get; set; } = "";
        public string Title { get; set; } = "";
        public string ContentType { get; set; } = "";
    }

    internal sealed class MagicEvidenceLinkResult
    {
        public bool Ok { get; set; }
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public bool Created { get; set; }
        public string Error { get; set; } = "";
        public MagicEvidenceLinkSnapshot Link { get; set; }
        public string UpdatedUtc { get; set; } = "";
        public string StoragePath { get; set; } = "";
    }

    internal sealed class MagicEvidenceLinkSnapshot
    {
        public string LinkId { get; set; } = "";
        public string EvidencePacketId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Classification { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicActionValidationResult
    {
        public bool Ok { get; set; }
        public List<MagicActionValidationIssue> Errors { get; set; } = new List<MagicActionValidationIssue>();
        public List<MagicActionValidationIssue> Warnings { get; set; } = new List<MagicActionValidationIssue>();
        public List<string> SuggestedRepairFields { get; set; } = new List<string>();
    }

    internal sealed class MagicActionValidationIssue
    {
        public string ActionId { get; set; } = "";
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        public string Field { get; set; } = "";
    }

    internal sealed class MagicExecutionPreviewPacket
    {
        public List<string> FilesToWrite { get; set; } = new List<string>();
        public List<string> FilesToEdit { get; set; } = new List<string>();
        public List<string> CommandsToRun { get; set; } = new List<string>();
        public List<string> GitMutations { get; set; } = new List<string>();
        public bool ApprovalRequired { get; set; }
        public List<string> RiskNotes { get; set; } = new List<string>();
    }

    internal sealed class MagicPlanStep
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Name { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Description { get; set; } = "";
        public string Status { get; set; } = "";
    }

    internal sealed class MagicProgressStage
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Status { get; set; } = "";
        public int Percent { get; set; }
        public string Notes { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    internal sealed class MagicProgressLogEntry
    {
        public string TimestampUtc { get; set; } = "";
        public string Status { get; set; } = "";
        public string Detail { get; set; } = "";
    }

    internal sealed class MagicPlanRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string Goal { get; set; } = "";
        public string Context { get; set; } = "";
        public List<string> Constraints { get; set; } = new List<string>();
        public List<string> Requirements { get; set; } = new List<string>();
        public List<string> KnownFiles { get; set; } = new List<string>();
        public List<string> Questions { get; set; } = new List<string>();
        public List<string> Answers { get; set; } = new List<string>();
        public List<MagicPlanStep> Steps { get; set; } = new List<MagicPlanStep>();
        public string PlanMarkdown { get; set; } = "";
        public string Status { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicProgressDocumentRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProjectOrSessionName { get; set; } = "";
        public string FeatureName { get; set; } = "";
        public string ProgressFileName { get; set; } = "";
        public string ProgressPath { get; set; } = "";
        public int OverallPercent { get; set; }
        public string CurrentStatus { get; set; } = "";
        public string Summary { get; set; } = "";
        public string NextStep { get; set; } = "";
        public string VerificationNotes { get; set; } = "";
        public List<MagicProgressStage> Stages { get; set; } = new List<MagicProgressStage>();
        public List<MagicProgressLogEntry> LogEntries { get; set; } = new List<MagicProgressLogEntry>();
        public string Markdown { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicPlanExecutionRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ExecutionMode { get; set; } = "";
        public string Status { get; set; } = "";
        public string Blocker { get; set; } = "";
        public List<MagicPlanExecutionAction> Actions { get; set; } = new List<MagicPlanExecutionAction>();
        public List<MagicPlanActionResult> Results { get; set; } = new List<MagicPlanActionResult>();
        public string ProgressId { get; set; } = "";
        public int CursorIndex { get; set; }
        public bool Paused { get; set; }
        public MagicActionValidationResult Validation { get; set; }
        public MagicExecutionPreviewPacket Preview { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicEvidenceLinkRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string EvidencePacketId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string ProgressId { get; set; } = "";
        public string ExecutionId { get; set; } = "";
        public string ActionId { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Classification { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }
}


