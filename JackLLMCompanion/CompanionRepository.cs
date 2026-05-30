using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using SocketJack.Net.Database;

namespace JackLLMCompanion;

public sealed class CompanionRepository
{
    private const string DatabaseName = "companion";
    private const long MaxSharedFileBytes = 128L * 1024L * 1024L;
    private const long MaxSharedFolderBytes = 512L * 1024L * 1024L;
    private const int MaxSharedFolderFiles = 500;
    private readonly object _sync = new();
    private readonly DataServer _dataServer;
    private readonly Database _database;
    private readonly string _shareRoot;
    private readonly string _trainingRoot;
    private string _activeSessionId = "";
    private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
    private bool _initialized;

    public CompanionRepository(bool autoLoadFromDisk = true)
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SocketJack",
            "Companion");
        Directory.CreateDirectory(root);
        _shareRoot = Path.Combine(root, "SharedFiles");
        Directory.CreateDirectory(_shareRoot);
        _trainingRoot = Path.Combine(root, "Training");
        Directory.CreateDirectory(_trainingRoot);

        _dataServer = new DataServer(0, "SocketJack Companion Data")
        {
            DataPath = Path.Combine(root, "companion-data"),
            CacheMetadataPath = Path.Combine(root, "companion-cache-meta.json"),
            EnablePayloadEncryption = true
        };
        _dataServer.Load();
        _database = _dataServer.Databases.GetOrAdd(DatabaseName, _ => new Database(DatabaseName));
        EnsureTables();
        EnsureSeedData();
        _initialized = true;

        if (!autoLoadFromDisk) {
            _initialized = false;
        }
    }

    public static Task<CompanionRepository> CreateAsync()
    {
        return CreateAsync(CancellationToken.None);
    }

    public static async Task<CompanionRepository> CreateAsync(CancellationToken cancellationToken)
    {
        var repository = new CompanionRepository(autoLoadFromDisk: false);
        await repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return repository;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try {
            if (_initialized)
                return;

            await _dataServer.LoadAsync(cancellationToken).ConfigureAwait(false);
            EnsureTables();
            EnsureSeedData();
            _initialized = true;
        } finally {
            _initializationLock.Release();
        }
    }

    public async Task InitializeAsync()
    {
        await InitializeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    public string DataPath => _dataServer.DataPath;
    public string ShareRoot => _shareRoot;
    public string TrainingRoot => _trainingRoot;

    public CompanionWorkspaceState GetWorkspaceState()
    {
        lock (_sync)
        {
            var sessions = Rows("CompanionWorkSessions").Select(row => new CompanionSessionSummary
            {
                Id = Cell(row, 0),
                Title = Cell(row, 1),
                Status = Cell(row, 2),
                StartedUtc = Cell(row, 3),
                StoppedUtc = Cell(row, 4),
                EventCount = ParseInt(Cell(row, 5)),
                Summary = Cell(row, 6)
            }).OrderByDescending(item => item.StartedUtc, StringComparer.OrdinalIgnoreCase).ToList();

            var templates = Rows("CompanionTemplates").Select(row => new CompanionTemplate
            {
                Id = Cell(row, 0),
                Name = Cell(row, 1),
                CompanionName = Cell(row, 2),
                Interests = Cell(row, 3),
                TemplateText = Cell(row, 4),
                UpdatedUtc = Cell(row, 5)
            }).ToList();

            return new CompanionWorkspaceState
            {
                CapturedUtc = Now(),
                DataPath = DataPath,
                ActiveSessionId = _activeSessionId,
                IsRecording = !string.IsNullOrWhiteSpace(_activeSessionId),
                Projects = Rows("CompanionProjects").Select(row => new CompanionProject
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Status = Cell(row, 2),
                    Summary = Cell(row, 3),
                    UpdatedUtc = Cell(row, 4)
                }).ToList(),
                Sessions = sessions,
                RecentEvents = Rows("CompanionSessionEvents").Select(row => new CompanionEvent
                {
                    Id = Cell(row, 0),
                    SessionId = Cell(row, 1),
                    Sequence = ParseInt(Cell(row, 2)),
                    EventType = Cell(row, 3),
                    Detail = Cell(row, 4),
                    Application = Cell(row, 5),
                    Window = Cell(row, 6),
                    Url = Cell(row, 7),
                    FilePath = Cell(row, 8),
                    Person = Cell(row, 9),
                    CreatedUtc = Cell(row, 10)
                }).OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
                Templates = templates,
                Permissions = GetPermissionsNoLock(),
                People = Rows("CompanionPeople").Select(row => new CompanionPerson
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Risk = ParseInt(Cell(row, 2)),
                    Helpfulness = ParseInt(Cell(row, 3)),
                    Relevance = ParseInt(Cell(row, 4)),
                    Frequency = ParseInt(Cell(row, 5)),
                    TrustConsent = ParseInt(Cell(row, 6)),
                    FinancialExposure = ParseInt(Cell(row, 7)),
                    IntegrityIntentRisk = ParseInt(Cell(row, 8)),
                    Confidence = ParseInt(Cell(row, 9)),
                    Notes = Cell(row, 10),
                    UpdatedUtc = Cell(row, 11)
                }).ToList(),
                Applications = Rows("CompanionApplications").Select(row => new CompanionApplication
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Frequency = ParseInt(Cell(row, 2)),
                    LastSeenUtc = Cell(row, 3),
                    Notes = Cell(row, 4)
                }).ToList(),
                Skills = Rows("CompanionSkills").Select(row => new CompanionSkill
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Trigger = Cell(row, 2),
                    Steps = Cell(row, 3),
                    SourceSessionId = Cell(row, 4),
                    Confidence = ParseInt(Cell(row, 5)),
                    UpdatedUtc = Cell(row, 6)
                }).ToList(),
                Audit = Rows("CompanionAuditEvents").Select(row => new CompanionAuditEvent
                {
                    Id = Cell(row, 0),
                    Action = Cell(row, 1),
                    Detail = Cell(row, 2),
                    CreatedUtc = Cell(row, 3)
                }).OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase).Take(40).ToList(),
                LlmTasks = Rows("CompanionLlmTasks").Select(row => new CompanionLlmTask
                {
                    Id = Cell(row, 0),
                    Goal = Cell(row, 1),
                    Mode = Cell(row, 2),
                    Status = Cell(row, 3),
                    Plan = Cell(row, 4),
                    CreatedUtc = Cell(row, 5),
                    UpdatedUtc = Cell(row, 6),
                    Progress = ParseInt(Cell(row, 7)),
                    Step = ParseInt(Cell(row, 8)),
                    MaxSteps = ParseInt(Cell(row, 9)),
                    LastAction = Cell(row, 10),
                    LastError = Cell(row, 11),
                    RunnerId = Cell(row, 12),
                    ModelEndpoint = Cell(row, 13)
                }).OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase).Take(20).ToList(),
                PendingApprovals = BuildPendingApprovalsNoLock(),
                Training = GetTrainingStateNoLock()
            };
        }
    }

    public CompanionFileState GetFileState()
    {
        lock (_sync)
        {
            return new CompanionFileState
            {
                CapturedUtc = Now(),
                Files = Rows("CompanionFiles").Select(row => new CompanionFileRecord
                {
                    Id = Cell(row, 0),
                    SessionId = Cell(row, 1),
                    Path = Cell(row, 2),
                    Name = Cell(row, 3),
                    Kind = Cell(row, 4),
                    LastSeenUtc = Cell(row, 5),
                    Note = Cell(row, 6)
                }).OrderByDescending(item => item.LastSeenUtc, StringComparer.OrdinalIgnoreCase).ToList(),
                SharedFiles = Rows("CompanionSharedFiles").Select(row => new CompanionSharedFile
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Path = Cell(row, 2),
                    SizeBytes = ParseLong(Cell(row, 3)),
                    Note = Cell(row, 4),
                    CreatedUtc = Cell(row, 5),
                    RelativePath = Cell(row, 6),
                    Kind = string.IsNullOrWhiteSpace(Cell(row, 7)) ? "file" : Cell(row, 7),
                    RequiresApproval = ParseBool(Cell(row, 8)),
                    ApprovalReason = Cell(row, 9)
                }).OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
    }

    public CompanionTrainingState GetTrainingState()
    {
        lock (_sync)
            return GetTrainingStateNoLock();
    }

    public CompanionTrainingSettings SaveTrainingSettings(CompanionTrainingSettings settings)
    {
        lock (_sync)
        {
            SaveTrainingSettingsNoLock(settings);
            AddAuditNoLock("training_settings_saved", "Training approval mode: " + GetTrainingSettingsNoLock().ApprovalMode);
            SaveNoLock();
            return GetTrainingSettingsNoLock();
        }
    }

    public CompanionTrainingRun CreateTrainingRun(string sourceSessionId, string profile, string modelEndpoint)
    {
        lock (_sync)
        {
            string id = "training_" + Guid.NewGuid().ToString("N");
            var run = new object[]
            {
                id,
                sourceSessionId ?? "",
                "queued",
                string.IsNullOrWhiteSpace(profile) ? "HybridMinimizedReplay" : profile.Trim(),
                "5",
                "low",
                "Training run queued.",
                Now(),
                "",
                modelEndpoint ?? "",
                ""
            };
            Table("CompanionTrainingRuns").Rows.Add(run);
            AddAuditNoLock("training_run_created", id + " from " + sourceSessionId);
            SaveNoLock();
            return ToTrainingRun(run);
        }
    }

    public CompanionTrainingRun UpdateTrainingRun(string runId, string status, int progress, string riskLevel, string summary, string error = "", bool completed = false)
    {
        lock (_sync)
        {
            object[]? row = Table("CompanionTrainingRuns").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), runId, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return new CompanionTrainingRun { Id = runId ?? "", Status = "missing", Summary = "Training run was not found." };

            row = EnsureRowLength(row, 11);
            row[2] = string.IsNullOrWhiteSpace(status) ? Cell(row, 2) : status.Trim();
            row[4] = Math.Max(0, Math.Min(100, progress)).ToString(CultureInfo.InvariantCulture);
            row[5] = string.IsNullOrWhiteSpace(riskLevel) ? Cell(row, 5) : riskLevel.Trim();
            row[6] = summary ?? "";
            if (completed)
                row[8] = Now();
            row[10] = error ?? "";
            AddAuditNoLock("training_run_updated", runId + ": " + row[2]);
            SaveNoLock();
            return ToTrainingRun(row);
        }
    }

    public CompanionTrainingEvidence AddTrainingEvidence(CompanionTrainingEvidence evidence)
    {
        lock (_sync)
        {
            evidence.Id = string.IsNullOrWhiteSpace(evidence.Id) ? "evidence_" + Guid.NewGuid().ToString("N") : evidence.Id.Trim();
            evidence.CreatedUtc = string.IsNullOrWhiteSpace(evidence.CreatedUtc) ? Now() : evidence.CreatedUtc;
            object[] row =
            {
                evidence.Id,
                evidence.RunId ?? "",
                evidence.SessionId ?? "",
                evidence.Sequence.ToString(CultureInfo.InvariantCulture),
                evidence.EventType ?? "",
                evidence.Detail ?? "",
                evidence.Application ?? "",
                evidence.Window ?? "",
                evidence.Url ?? "",
                evidence.FilePath ?? "",
                evidence.Person ?? "",
                evidence.InputTrace ?? "",
                evidence.KeyframePath ?? "",
                evidence.Summary ?? "",
                evidence.SensitivityFlags ?? "",
                evidence.CreatedUtc
            };
            Table("CompanionTrainingEvidence").Rows.Add(row);
            SaveNoLock();
            return ToTrainingEvidence(row);
        }
    }

    public CompanionSkillDraft AddSkillDraft(CompanionSkillDraft draft)
    {
        lock (_sync)
        {
            draft.Id = string.IsNullOrWhiteSpace(draft.Id) ? "draft_" + Guid.NewGuid().ToString("N") : draft.Id.Trim();
            draft.Status = string.IsNullOrWhiteSpace(draft.Status) ? "draft" : draft.Status.Trim();
            draft.CreatedUtc = string.IsNullOrWhiteSpace(draft.CreatedUtc) ? Now() : draft.CreatedUtc;
            draft.UpdatedUtc = Now();
            object[] row =
            {
                draft.Id,
                draft.SkillId ?? "",
                draft.RunId ?? "",
                draft.SourceSessionId ?? "",
                string.IsNullOrWhiteSpace(draft.Name) ? "Recorded Companion skill" : draft.Name.Trim(),
                draft.Trigger ?? "",
                draft.Prerequisites ?? "",
                draft.Steps ?? "",
                draft.SafetyGates ?? "",
                draft.EvidenceRefs ?? "",
                Math.Max(0, Math.Min(100, draft.Confidence)).ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(draft.RiskLevel) ? "medium" : draft.RiskLevel.Trim(),
                draft.Status,
                draft.CreatedUtc,
                draft.UpdatedUtc
            };
            Table("CompanionSkillDrafts").Rows.Add(row);

            CompanionTrainingSettings settings = GetTrainingSettingsNoLock();
            if (settings.ApprovalMode.Equals("auto_enable_low_risk", StringComparison.OrdinalIgnoreCase) &&
                draft.RiskLevel.Equals("low", StringComparison.OrdinalIgnoreCase))
            {
                EnableSkillDraftNoLock(row, "Auto-enabled low-risk learned skill.");
            }
            else if (settings.ApprovalMode.Equals("enable_all", StringComparison.OrdinalIgnoreCase))
            {
                EnableSkillDraftNoLock(row, "Auto-enabled learned skill because Enable All mode is active.");
            }

            AddAuditNoLock("skill_draft_created", draft.Name);
            SaveNoLock();
            return ToSkillDraft(row);
        }
    }

    public CompanionSkillReviewResult ReviewSkillDraft(string draftId, string action, bool warningAccepted)
    {
        lock (_sync)
        {
            object[]? row = Table("CompanionSkillDrafts").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), draftId, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return new CompanionSkillReviewResult { Ok = false, Message = "Skill draft was not found." };

            row = EnsureRowLength(row, 15);
            string normalized = (action ?? "").Trim().ToLowerInvariant();
            CompanionSkill? enabled = null;
            if (normalized is "enable" or "enabled")
            {
                string risk = Cell(row, 11);
                if (!warningAccepted && !risk.Equals("low", StringComparison.OrdinalIgnoreCase))
                {
                    return new CompanionSkillReviewResult
                    {
                        Ok = false,
                        Draft = ToSkillDraft(row),
                        Message = "Warning confirmation is required before enabling a non-low-risk learned skill."
                    };
                }

                enabled = EnableSkillDraftNoLock(row, "Enabled from Companion skill review.");
            }
            else if (normalized is "approve" or "approved")
            {
                row[12] = "approved";
                row[14] = Now();
            }
            else if (normalized is "reject" or "rejected")
            {
                row[12] = "rejected";
                row[14] = Now();
            }
            else
            {
                return new CompanionSkillReviewResult { Ok = false, Draft = ToSkillDraft(row), Message = "Unknown review action." };
            }

            AddAuditNoLock("skill_draft_reviewed", draftId + ": " + row[12]);
            SaveNoLock();
            return new CompanionSkillReviewResult
            {
                Ok = true,
                Draft = ToSkillDraft(row),
                Skill = enabled,
                Message = "Skill draft " + row[12] + "."
            };
        }
    }

    public CompanionTrainingSessionSnapshot GetTrainingSnapshot(string sessionId)
    {
        lock (_sync)
        {
            CompanionSessionSummary session = Rows("CompanionWorkSessions")
                .Where(row => string.Equals(Cell(row, 0), sessionId, StringComparison.OrdinalIgnoreCase))
                .Select(row => new CompanionSessionSummary
                {
                    Id = Cell(row, 0),
                    Title = Cell(row, 1),
                    Status = Cell(row, 2),
                    StartedUtc = Cell(row, 3),
                    StoppedUtc = Cell(row, 4),
                    EventCount = ParseInt(Cell(row, 5)),
                    Summary = Cell(row, 6)
                })
                .FirstOrDefault() ?? new CompanionSessionSummary { Id = sessionId ?? "", Title = "Unknown session" };

            return new CompanionTrainingSessionSnapshot
            {
                Session = session,
                Events = Rows("CompanionSessionEvents")
                    .Where(row => string.Equals(Cell(row, 1), sessionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(row => ParseInt(Cell(row, 2)))
                    .Select(row => new CompanionEvent
                    {
                        Id = Cell(row, 0),
                        SessionId = Cell(row, 1),
                        Sequence = ParseInt(Cell(row, 2)),
                        EventType = Cell(row, 3),
                        Detail = Cell(row, 4),
                        Application = Cell(row, 5),
                        Window = Cell(row, 6),
                        Url = Cell(row, 7),
                        FilePath = Cell(row, 8),
                        Person = Cell(row, 9),
                        CreatedUtc = Cell(row, 10)
                    }).ToList(),
                Files = Rows("CompanionFiles")
                    .Where(row => string.Equals(Cell(row, 1), sessionId, StringComparison.OrdinalIgnoreCase))
                    .Select(row => new CompanionFileRecord
                    {
                        Id = Cell(row, 0),
                        SessionId = Cell(row, 1),
                        Path = Cell(row, 2),
                        Name = Cell(row, 3),
                        Kind = Cell(row, 4),
                        LastSeenUtc = Cell(row, 5),
                        Note = Cell(row, 6)
                    }).ToList()
            };
        }
    }

    public List<CompanionSkill> RankEnabledSkills(string goal, CompanionEnvironmentSnapshot snapshot, int take)
    {
        lock (_sync)
        {
            var enabledIds = Rows("CompanionSkillDrafts")
                .Where(row => Cell(row, 12).Equals("enabled", StringComparison.OrdinalIgnoreCase))
                .Select(row => Cell(row, 1))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return Rows("CompanionSkills")
                .Select(row => new CompanionSkill
                {
                    Id = Cell(row, 0),
                    Name = Cell(row, 1),
                    Trigger = Cell(row, 2),
                    Steps = Cell(row, 3),
                    SourceSessionId = Cell(row, 4),
                    Confidence = ParseInt(Cell(row, 5)),
                    UpdatedUtc = Cell(row, 6)
                })
                .Where(skill => enabledIds.Contains(skill.Id))
                .Select(skill => new { Skill = skill, Score = ScoreSkill(skill, goal, snapshot) })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Skill.Confidence)
                .Take(Math.Max(1, take))
                .Select(item => item.Skill)
                .ToList();
        }
    }

    public bool TryGetTrainingKeyframe(string evidenceId, out string path)
    {
        lock (_sync)
        {
            path = "";
            object[]? row = Table("CompanionTrainingEvidence").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), evidenceId, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return false;
            path = Cell(row, 12);
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        }
    }

    public CompanionSessionSummary StartRecording(string title, string note)
    {
        lock (_sync)
        {
            if (!string.IsNullOrWhiteSpace(_activeSessionId))
                StopRecording("Stopped before starting a new recording.");

            string id = "session_" + Guid.NewGuid().ToString("N");
            string now = Now();
            var row = new object[] { id, string.IsNullOrWhiteSpace(title) ? "Companion work session" : title.Trim(), "recording", now, "", "0", note ?? "" };
            Table("CompanionWorkSessions").Rows.Add(row);
            _activeSessionId = id;
            AddEventNoLock(id, "recording_started", "Recording started.", "", "", "", "", "", "");
            AddAuditNoLock("recording_started", id);
            SaveNoLock();
            return GetWorkspaceState().Sessions.First(item => item.Id == id);
        }
    }

    public string StopRecording(string summary)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_activeSessionId))
                return "";

            string id = _activeSessionId;
            foreach (object[] row in Table("CompanionWorkSessions").Rows)
            {
                if (!string.Equals(Cell(row, 0), id, StringComparison.OrdinalIgnoreCase))
                    continue;
                row[2] = "stopped";
                row[4] = Now();
                row[6] = string.IsNullOrWhiteSpace(summary) ? "Recording stopped." : summary.Trim();
                break;
            }

            AddEventNoLock(id, "recording_stopped", "Recording stopped.", "", "", "", "", "", "");
            AddAuditNoLock("recording_stopped", id);
            _activeSessionId = "";
            SaveNoLock();
            return id;
        }
    }

    public void AddManualEvent(string detail)
    {
        lock (_sync)
        {
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            AddEventNoLock(sessionId, "manual_note", string.IsNullOrWhiteSpace(detail) ? "Manual event." : detail.Trim(), "", "", "", "", "", "");
            AddAuditNoLock("manual_event", detail ?? "");
            SaveNoLock();
        }
    }

    public CompanionPermissions SavePermissions(CompanionPermissions permissions)
    {
        lock (_sync)
        {
            SavePermissionsNoLock(permissions);
            AddAuditNoLock("permissions_saved", "Companion permissions updated.");
            SaveNoLock();
            return GetPermissionsNoLock();
        }
    }

    public CompanionApprovalDecision DecideApproval(string approvalId, bool approved, string decidedBy)
    {
        lock (_sync)
        {
            approvalId = (approvalId ?? "").Trim();
            CompanionApprovalRequest? request = BuildPendingApprovalsNoLock()
                .FirstOrDefault(item => string.Equals(item.Id, approvalId, StringComparison.OrdinalIgnoreCase));
            if (request == null)
            {
                return new CompanionApprovalDecision
                {
                    Ok = false,
                    Approved = approved,
                    Message = "Approval request was not found or is no longer pending.",
                    PendingApprovals = BuildPendingApprovalsNoLock()
                };
            }

            string actor = string.IsNullOrWhiteSpace(decidedBy) ? "Companion admin" : decidedBy.Trim();
            if (approved)
            {
                CompanionPermissions permissions = GetPermissionsNoLock();
                ApplyCapabilityApprovalNoLock(permissions, request.Capability);
                SavePermissionsNoLock(permissions);
                AddAuditNoLock("approval_approved", actor + ": " + request.Title + " (" + request.Capability + ")");
                SaveNoLock();
                return new CompanionApprovalDecision
                {
                    Ok = true,
                    Approved = true,
                    Request = request,
                    Message = request.Capability.Equals("liveInput", StringComparison.OrdinalIgnoreCase)
                        ? "Live Input approved. Paused LLM desktop tasks are queued for the runner."
                        : "Approval granted for " + request.Capability + ".",
                    PendingApprovals = BuildPendingApprovalsNoLock()
                };
            }

            if (!string.IsNullOrWhiteSpace(request.RelatedTaskId))
                StopLlmTaskByIdNoLock(request.RelatedTaskId, "Approval denied from Companion admin UI.");

            AddAuditNoLock("approval_denied", actor + ": " + request.Title + " (" + request.Capability + ")");
            SaveNoLock();
            return new CompanionApprovalDecision
            {
                Ok = true,
                Approved = false,
                Request = request,
                Message = string.IsNullOrWhiteSpace(request.RelatedTaskId)
                    ? "Approval left disabled."
                    : "Approval denied and the paused LLM task was stopped.",
                PendingApprovals = BuildPendingApprovalsNoLock()
            };
        }
    }

    public CompanionTemplate SaveTemplate(CompanionTemplate template)
    {
        lock (_sync)
        {
            template.Id = string.IsNullOrWhiteSpace(template.Id) ? "JACK" : template.Id.Trim();
            template.Name = string.IsNullOrWhiteSpace(template.Name) ? "JACK" : template.Name.Trim();
            template.CompanionName = string.IsNullOrWhiteSpace(template.CompanionName) ? "JACK" : template.CompanionName.Trim();
            template.Interests = string.IsNullOrWhiteSpace(template.Interests) ? DefaultInterests : template.Interests.Trim();
            template.TemplateText = string.IsNullOrWhiteSpace(template.TemplateText) ? DefaultTemplateText : template.TemplateText.Trim();
            template.UpdatedUtc = Now();

            Table table = Table("CompanionTemplates");
            for (int i = 0; i < table.Rows.Count; i++)
            {
                if (string.Equals(Cell(table.Rows[i], 0), template.Id, StringComparison.OrdinalIgnoreCase))
                {
                    table.Rows[i] = TemplateRow(template);
                    AddAuditNoLock("template_saved", template.Id);
                    SaveNoLock();
                    return template;
                }
            }

            table.Rows.Add(TemplateRow(template));
            AddAuditNoLock("template_saved", template.Id);
            SaveNoLock();
            return template;
        }
    }

    public CompanionTemplate GenerateName()
    {
        lock (_sync)
        {
            string[] names = { "JACK", "Boost", "Relay", "Patch", "Vector", "Studio", "Turbo" };
            int index = Table("CompanionAuditEvents").Rows.Count % names.Length;
            CompanionTemplate template = GetJackTemplateNoLock();
            template.CompanionName = names[index];
            template.UpdatedUtc = Now();
            SaveTemplate(template);
            AddAuditNoLock("ai_name_generated", template.CompanionName);
            SaveNoLock();
            return template;
        }
    }

    public CompanionTemplate InferInterests()
    {
        lock (_sync)
        {
            var interests = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in DefaultInterests.Split(','))
                AddInterest(interests, item);

            foreach (object[] app in Table("CompanionApplications").Rows)
                AddInterest(interests, Cell(app, 1));

            foreach (object[] ev in Table("CompanionSessionEvents").Rows)
            {
                string detail = Cell(ev, 4);
                if (detail.IndexOf("music", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddInterest(interests, "music production");
                if (detail.IndexOf("program", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("code", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddInterest(interests, "programming");
                if (detail.IndexOf("youtube", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddInterest(interests, "YouTube learning");
                if (detail.IndexOf("car", StringComparison.OrdinalIgnoreCase) >= 0 || detail.IndexOf("turbo", StringComparison.OrdinalIgnoreCase) >= 0)
                    AddInterest(interests, "car stuff and turbos");
            }

            CompanionTemplate template = GetJackTemplateNoLock();
            template.Interests = string.Join(", ", interests.Take(16));
            template.UpdatedUtc = Now();
            SaveTemplate(template);
            AddAuditNoLock("ai_interests_inferred", template.Interests);
            SaveNoLock();
            return template;
        }
    }

    public CompanionControlDecision RequestControlAction(string capability, string detail)
    {
        lock (_sync)
        {
            CompanionPermissions permissions = GetPermissionsNoLock();
            bool allowed = IsCapabilityAllowed(permissions, capability);
            string message = allowed
                ? "Capability is enabled. V1 recorded the approved request; no raw OS input was executed by this scaffold."
                : "Capability requires explicit approval before Companion can act.";
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            AddEventNoLock(sessionId, allowed ? "control_request_approved" : "control_request_blocked", detail ?? capability ?? "control action", "", "", "", "", "", "");
            AddAuditNoLock(allowed ? "control_request_approved" : "control_request_blocked", capability + ": " + detail);
            SaveNoLock();
            return new CompanionControlDecision
            {
                Ok = allowed,
                ApprovalRequired = !allowed,
                Capability = capability ?? "",
                Message = message
            };
        }
    }

    public CompanionLlmTask SubmitLlmTask(string goal, string mode)
    {
        lock (_sync)
        {
            string trimmedGoal = string.IsNullOrWhiteSpace(goal) ? "Use the PC to help with the current task." : goal.Trim();
            string trimmedMode = string.IsNullOrWhiteSpace(mode) ? "assistive" : mode.Trim();
            bool canControl = GetPermissionsNoLock().LiveInput;
            string status = canControl ? "queued" : "approval_required";
            string plan = canControl
                ? "Task queued for the Companion LLM runner. The runner will observe the remote desktop, ask the model for one small next action, and execute only approved live-input actions."
                : "Enable Live Input before the LLM runner can control the PC. The task is saved, but no desktop input will execute.";
            string id = "llm_task_" + Guid.NewGuid().ToString("N");
            string now = Now();
            Table("CompanionLlmTasks").Rows.Add(new object[] { id, trimmedGoal, trimmedMode, status, plan, now, now, "0", "0", "8", "", "", "", "" });
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            AddEventNoLock(sessionId, "llm_task_" + status, trimmedGoal, "", "", "", "", "", "");
            AddAuditNoLock("llm_task_" + status, trimmedMode + ": " + trimmedGoal);
            SaveNoLock();
            return GetLlmTaskNoLock(id);
        }
    }

    public CompanionLlmTask StopLlmTask(string reason)
    {
        lock (_sync)
        {
            object[]? task = Table("CompanionLlmTasks").Rows
                .LastOrDefault(row => !string.Equals(Cell(row, 3), "stopped", StringComparison.OrdinalIgnoreCase) &&
                                      !string.Equals(Cell(row, 3), "completed", StringComparison.OrdinalIgnoreCase));
            if (task == null)
            {
                return new CompanionLlmTask
                {
                    Id = "",
                    Goal = "",
                    Mode = "",
                    Status = "idle",
                    Plan = "No active or queued LLM desktop task was found.",
                    CreatedUtc = Now(),
                    UpdatedUtc = Now()
                };
            }

            task[3] = "stopped";
            task[4] = string.IsNullOrWhiteSpace(reason) ? "Stopped by user." : reason.Trim();
            task[6] = Now();
            AddAuditNoLock("llm_task_stopped", Cell(task, 0));
            SaveNoLock();
            return GetLlmTaskNoLock(Cell(task, 0));
        }
    }

    public CompanionLlmTask? ClaimNextQueuedLlmTask(string runnerId, string modelEndpoint)
    {
        lock (_sync)
        {
            Table table = Table("CompanionLlmTasks");
            for (int i = 0; i < table.Rows.Count; i++)
            {
                object[] row = EnsureRowLength(table.Rows[i], 14);
                table.Rows[i] = row;
                string status = Cell(row, 3);
                if (!string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
                    continue;

                row[3] = "running";
                row[4] = "Runner started. Observing the desktop and asking the model for the next action.";
                row[6] = Now();
                row[7] = "5";
                row[8] = "0";
                row[10] = "runner_started";
                row[11] = "";
                row[12] = runnerId ?? "";
                row[13] = modelEndpoint ?? "";
                AddAuditNoLock("llm_task_running", Cell(row, 0));
                SaveNoLock();
                return GetLlmTaskNoLock(Cell(row, 0));
            }

            return null;
        }
    }

    public bool IsLlmTaskCancellationRequested(string taskId)
    {
        lock (_sync)
        {
            object[]? row = Table("CompanionLlmTasks").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), taskId, StringComparison.OrdinalIgnoreCase));
            if (row == null)
                return true;

            string status = Cell(row, 3);
            return string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "approval_required", StringComparison.OrdinalIgnoreCase);
        }
    }

    public void UpdateLlmTaskProgress(string taskId, string status, string plan, int progress, int step, int maxSteps, string lastAction, string lastError = "")
    {
        lock (_sync)
        {
            Table table = Table("CompanionLlmTasks");
            for (int i = 0; i < table.Rows.Count; i++)
            {
                object[] row = EnsureRowLength(table.Rows[i], 14);
                table.Rows[i] = row;
                if (!string.Equals(Cell(row, 0), taskId, StringComparison.OrdinalIgnoreCase))
                    continue;

                row[3] = string.IsNullOrWhiteSpace(status) ? Cell(row, 3) : status.Trim();
                row[4] = plan ?? "";
                row[6] = Now();
                row[7] = Math.Max(0, Math.Min(100, progress)).ToString(CultureInfo.InvariantCulture);
                row[8] = Math.Max(0, step).ToString(CultureInfo.InvariantCulture);
                row[9] = Math.Max(1, maxSteps).ToString(CultureInfo.InvariantCulture);
                row[10] = lastAction ?? "";
                row[11] = lastError ?? "";
                break;
            }

            SaveNoLock();
        }
    }

    public void RecordLlmRunnerEvent(string taskId, string eventType, string detail, CompanionEnvironmentSnapshot? snapshot = null)
    {
        lock (_sync)
        {
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            AddEventNoLock(
                sessionId,
                eventType,
                detail ?? "",
                snapshot?.Application ?? "",
                snapshot?.Window ?? "",
                snapshot?.Url ?? "",
                "",
                snapshot?.Person ?? "",
                "");
            AddAuditNoLock(eventType, taskId + ": " + detail);
            SaveNoLock();
        }
    }

    public void MarkApprovalRequiredLlmTasksQueuedIfApproved()
    {
        lock (_sync)
        {
            if (!GetPermissionsNoLock().LiveInput)
                return;

            bool changed = false;
            Table table = Table("CompanionLlmTasks");
            for (int i = 0; i < table.Rows.Count; i++)
            {
                object[] row = EnsureRowLength(table.Rows[i], 14);
                table.Rows[i] = row;
                if (!string.Equals(Cell(row, 3), "approval_required", StringComparison.OrdinalIgnoreCase))
                    continue;
                row[3] = "queued";
                row[4] = "Live Input is now enabled. Task is queued for the Companion LLM runner.";
                row[6] = Now();
                row[7] = "0";
                row[10] = "approval_granted";
                changed = true;
            }

            if (changed)
            {
                AddAuditNoLock("llm_tasks_queued_after_approval", "Live Input enabled.");
                SaveNoLock();
            }
        }
    }

    public CompanionEmergencyStopResult EmergencyStop(string source)
    {
        lock (_sync)
        {
            bool recordingWasActive = !string.IsNullOrWhiteSpace(_activeSessionId);
            if (recordingWasActive)
            {
                string id = _activeSessionId;
                foreach (object[] row in Table("CompanionWorkSessions").Rows)
                {
                    if (!string.Equals(Cell(row, 0), id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    row[2] = "stopped";
                    row[4] = Now();
                    row[6] = "Stopped by Companion emergency stop.";
                    break;
                }

                AddEventNoLock(id, "emergency_stop", "Emergency stop invoked from " + (source ?? "Companion") + ".", "", "", "", "", "", "");
                AddInferredSkillNoLock(id);
                _activeSessionId = "";
            }

            CompanionPermissions permissions = GetPermissionsNoLock();
            bool liveInputWasEnabled = permissions.LiveInput;
            permissions.LiveInput = false;
            Table table = Table("CompanionPermissions");
            table.Rows.Clear();
            table.Rows.Add(new object[]
            {
                permissions.HumanInteraction ? "true" : "false",
                permissions.SpendMoney ? "true" : "false",
                permissions.AccountLogin ? "true" : "false",
                permissions.UseFiles ? "true" : "false",
                permissions.PcSettings ? "true" : "false",
                permissions.InternetAccess ? "true" : "false",
                "false",
                Now()
            });

            StopLlmTaskNoLock("Emergency stop invoked from " + (source ?? "Companion") + ".");
            AddAuditNoLock("emergency_stop", "Source: " + (source ?? "Companion") + "; liveInputWasEnabled=" + liveInputWasEnabled.ToString(CultureInfo.InvariantCulture));
            SaveNoLock();
            return new CompanionEmergencyStopResult
            {
                Ok = true,
                LiveInputWasEnabled = liveInputWasEnabled,
                RecordingWasActive = recordingWasActive,
                Message = "Emergency stop complete. Live input is disabled, active recording is stopped, and queued LLM desktop tasks are stopped."
            };
        }
    }

    public bool CanUseLiveInput()
    {
        lock (_sync)
            return GetPermissionsNoLock().LiveInput;
    }

    public bool CanUseFiles()
    {
        lock (_sync)
            return GetPermissionsNoLock().UseFiles;
    }

    public bool CanUsePcSettings()
    {
        lock (_sync)
            return GetPermissionsNoLock().PcSettings;
    }

    public CompanionPermissions GetPermissions()
    {
        lock (_sync)
            return GetPermissionsNoLock();
    }

    public void RecordControlResult(string action, string detail, bool executed)
    {
        lock (_sync)
        {
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            AddEventNoLock(sessionId, executed ? "control_executed" : "control_blocked", detail ?? action ?? "", "", "", "", "", "", "");
            AddAuditNoLock(executed ? "control_executed" : "control_blocked", action + ": " + detail);
            SaveNoLock();
        }
    }

    public void RecordEnvironmentSnapshot(CompanionEnvironmentSnapshot snapshot)
    {
        if (snapshot == null)
            return;

        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(_activeSessionId))
                return;

            string app = (snapshot.Application ?? "").Trim();
            string window = (snapshot.Window ?? "").Trim();
            string url = (snapshot.Url ?? "").Trim();
            string person = (snapshot.Person ?? "").Trim();
            string detail = string.IsNullOrWhiteSpace(window)
                ? "Foreground application: " + app
                : "Foreground window: " + window;

            AddEventNoLock(_activeSessionId, "environment_snapshot", detail, app, window, url, "", person, "");
            if (!string.IsNullOrWhiteSpace(app))
                UpsertApplicationNoLock(app, "Observed while recording.");
            if (!string.IsNullOrWhiteSpace(person))
                UpsertPersonNoLock(person, "Observed from chat/communication window metadata.");

            foreach (string file in snapshot.Files ?? new List<string>())
                RegisterFileNoLock(_activeSessionId, file, "recent", "Recently modified while Companion recording was active.");

            SaveNoLock();
        }
    }

    public void RegisterFile(string sessionId, string filePath, string kind, string note)
    {
        lock (_sync)
        {
            sessionId = string.IsNullOrWhiteSpace(sessionId)
                ? (string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId)
                : sessionId.Trim();
            RegisterFileNoLock(sessionId, filePath, kind, note);
            AddAuditNoLock("file_registered", filePath ?? "");
            SaveNoLock();
        }
    }

    public CompanionSharedFile ShareExistingFile(string filePath, string note) => ShareExistingFile(filePath, note, approved: false);

    public CompanionSharedFile ShareExistingFile(string filePath, string note, bool approved)
    {
        lock (_sync)
        {
            if (!GetPermissionsNoLock().UseFiles)
            {
                AddAuditNoLock("file_share_blocked", "Use Files permission is disabled.");
                SaveNoLock();
                return new CompanionSharedFile
                {
                    Id = "",
                    Name = "",
                    Path = "",
                    Note = "Use Files permission is required before sharing files.",
                    CreatedUtc = Now()
                };
            }

            string sourcePath = Path.GetFullPath((filePath ?? "").Trim());
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("The selected file does not exist.", sourcePath);
            long sourceLength = new FileInfo(sourcePath).Length;
            if (sourceLength > MaxSharedFileBytes)
                throw new InvalidOperationException("File exceeds the Companion sharing limit of " + FormatBytes(MaxSharedFileBytes) + ".");

            string approvalReason = GetFileApprovalReason(sourcePath);
            if (!approved && !string.IsNullOrWhiteSpace(approvalReason))
            {
                AddAuditNoLock("file_share_needs_approval", sourcePath + ": " + approvalReason);
                SaveNoLock();
                return ApprovalRequiredFile(Path.GetFileName(sourcePath), sourcePath, approvalReason);
            }

            string safeName = BuildSafeFileName(Path.GetFileName(sourcePath));
            string id = "share_" + Guid.NewGuid().ToString("N");
            string targetPath = Path.Combine(_shareRoot, id + "_" + safeName);
            File.Copy(sourcePath, targetPath, overwrite: false);
            CompanionSharedFile shared = AddSharedFileNoLock(id, safeName, targetPath, note, "", "file", false, approvalReason);
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            RegisterFileNoLock(sessionId, sourcePath, "shared-source", "Shared through Companion file sharing.");
            RegisterFileNoLock(sessionId, targetPath, "shared-copy", note ?? "Shared through Companion file sharing.");
            AddAuditNoLock("file_shared", sourcePath);
            SaveNoLock();
            return shared;
        }
    }

    public List<CompanionSharedFile> ShareExistingPath(string path, string note, bool approved)
    {
        lock (_sync)
        {
            string sourcePath = Path.GetFullPath((path ?? "").Trim());
            if (File.Exists(sourcePath))
                return new List<CompanionSharedFile> { ShareExistingFile(sourcePath, note, approved) };
            if (!Directory.Exists(sourcePath))
                throw new FileNotFoundException("The selected file or folder does not exist.", sourcePath);
            if (!GetPermissionsNoLock().UseFiles)
            {
                AddAuditNoLock("folder_share_blocked", "Use Files permission is disabled.");
                SaveNoLock();
                return new List<CompanionSharedFile>
                {
                    new()
                    {
                        Id = "",
                        Note = "Use Files permission is required before sharing folders.",
                        CreatedUtc = Now()
                    }
                };
            }

            string approvalReason = GetFolderApprovalReason(sourcePath);
            if (!approved && !string.IsNullOrWhiteSpace(approvalReason))
            {
                AddAuditNoLock("folder_share_needs_approval", sourcePath + ": " + approvalReason);
                SaveNoLock();
                return new List<CompanionSharedFile> { ApprovalRequiredFile(Path.GetFileName(sourcePath), sourcePath, approvalReason) };
            }

            var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                .Take(MaxSharedFolderFiles + 1)
                .ToList();
            if (sourceFiles.Count > MaxSharedFolderFiles)
                throw new InvalidOperationException("Folder sharing is limited to " + MaxSharedFolderFiles.ToString(CultureInfo.InvariantCulture) + " files.");

            long totalBytes = 0;
            foreach (string file in sourceFiles)
            {
                long length = new FileInfo(file).Length;
                if (length > MaxSharedFileBytes)
                    throw new InvalidOperationException("Folder contains a file over the Companion sharing limit: " + Path.GetFileName(file) + ".");
                totalBytes += length;
                if (totalBytes > MaxSharedFolderBytes)
                    throw new InvalidOperationException("Folder exceeds the Companion sharing limit of " + FormatBytes(MaxSharedFolderBytes) + ".");
            }

            string folderId = "share_" + Guid.NewGuid().ToString("N");
            string safeFolder = BuildSafeFileName(Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            string targetRoot = Path.Combine(_shareRoot, folderId + "_" + safeFolder);
            Directory.CreateDirectory(targetRoot);
            var shared = new List<CompanionSharedFile>();
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            foreach (string file in sourceFiles)
            {
                string relativePath = BuildSafeRelativePath(Path.GetRelativePath(sourcePath, file));
                string targetPath = Path.Combine(targetRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetRoot);
                File.Copy(file, targetPath, overwrite: false);
                string id = "share_" + Guid.NewGuid().ToString("N");
                CompanionSharedFile item = AddSharedFileNoLock(id, Path.GetFileName(file), targetPath, note ?? "", relativePath, "folder-file", false, approvalReason);
                shared.Add(item);
                RegisterFileNoLock(sessionId, file, "shared-folder-source", "Shared through Companion folder sharing.");
                RegisterFileNoLock(sessionId, targetPath, "shared-folder-copy", note ?? "Shared through Companion folder sharing.");
            }

            AddAuditNoLock("folder_shared", sourcePath + " (" + shared.Count.ToString(CultureInfo.InvariantCulture) + " files)");
            SaveNoLock();
            return shared;
        }
    }

    public CompanionSharedFile SaveSharedUpload(string fileName, string base64Data, string note) => SaveSharedUpload(fileName, base64Data, note, "", approved: false);

    public CompanionSharedFile SaveSharedUpload(string fileName, string base64Data, string note, string relativePath, bool approved)
    {
        lock (_sync)
        {
            if (!GetPermissionsNoLock().UseFiles)
            {
                AddAuditNoLock("file_upload_blocked", "Use Files permission is disabled.");
                SaveNoLock();
                return new CompanionSharedFile
                {
                    Id = "",
                    Name = "",
                    Path = "",
                    Note = "Use Files permission is required before upload.",
                    CreatedUtc = Now()
                };
            }

            string safeName = BuildSafeFileName(string.IsNullOrWhiteSpace(fileName) ? "companion-upload.bin" : fileName);
            string safeRelativePath = BuildSafeRelativePath(string.IsNullOrWhiteSpace(relativePath) ? safeName : relativePath);
            string approvalReason = GetFileApprovalReason(safeRelativePath);
            if (!approved && !string.IsNullOrWhiteSpace(approvalReason))
            {
                AddAuditNoLock("file_upload_needs_approval", safeRelativePath + ": " + approvalReason);
                SaveNoLock();
                return ApprovalRequiredFile(safeName, safeRelativePath, approvalReason);
            }

            string encoded = (base64Data ?? "").Trim();
            if (encoded.Length > ((MaxSharedFileBytes + 2) / 3) * 4)
                throw new InvalidOperationException("Upload exceeds the Companion sharing limit of " + FormatBytes(MaxSharedFileBytes) + ".");

            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.LongLength > MaxSharedFileBytes)
                throw new InvalidOperationException("Upload exceeds the Companion sharing limit of " + FormatBytes(MaxSharedFileBytes) + ".");
            string id = "share_" + Guid.NewGuid().ToString("N");
            string targetPath = Path.Combine(_shareRoot, id + "_" + safeRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? _shareRoot);
            File.WriteAllBytes(targetPath, bytes);
            CompanionSharedFile shared = AddSharedFileNoLock(id, safeName, targetPath, note, safeRelativePath, safeRelativePath.Contains(Path.DirectorySeparatorChar) || safeRelativePath.Contains('/') ? "folder-upload" : "uploaded-file", false, approvalReason);
            string sessionId = string.IsNullOrWhiteSpace(_activeSessionId) ? EnsureScratchSessionNoLock() : _activeSessionId;
            RegisterFileNoLock(sessionId, targetPath, "uploaded-share", note ?? "Uploaded through Companion file sharing.");
            AddAuditNoLock("file_uploaded", safeName);
            SaveNoLock();
            return shared;
        }
    }

    public bool TryGetSharedFile(string id, out CompanionSharedFile shared)
    {
        lock (_sync)
        {
            object[]? row = Table("CompanionSharedFiles").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), id, StringComparison.OrdinalIgnoreCase));
            if (row == null)
            {
                shared = new CompanionSharedFile();
                return false;
            }

            shared = new CompanionSharedFile
            {
                Id = Cell(row, 0),
                Name = Cell(row, 1),
                Path = Cell(row, 2),
                SizeBytes = ParseLong(Cell(row, 3)),
                Note = Cell(row, 4),
                CreatedUtc = Cell(row, 5),
                RelativePath = Cell(row, 6),
                Kind = string.IsNullOrWhiteSpace(Cell(row, 7)) ? "file" : Cell(row, 7),
                RequiresApproval = ParseBool(Cell(row, 8)),
                ApprovalReason = Cell(row, 9)
            };
            return File.Exists(shared.Path);
        }
    }

    private void EnsureTables()
    {
        Table("CompanionProjects");
        Table("CompanionWorkSessions");
        Table("CompanionSessionEvents");
        Table("CompanionFiles");
        Table("CompanionPeople");
        Table("CompanionApplications");
        Table("CompanionPermissions");
        Table("CompanionTemplates");
        Table("CompanionSkills");
        Table("CompanionAuditEvents");
        Table("CompanionLlmTasks");
        Table("CompanionSharedFiles");
        Table("CompanionTrainingRuns");
        Table("CompanionTrainingEvidence");
        Table("CompanionSkillDrafts");
        Table("CompanionSkillExecutions");
        Table("CompanionTrainingSettings");
    }

    private void EnsureSeedData()
    {
        lock (_sync)
        {
            if (Table("CompanionProjects").Rows.Count == 0)
            {
                Table("CompanionProjects").Rows.Add(new object[]
                {
                    "project_companion",
                    "SocketJack Companion Tool",
                    "planning",
                    "Approval-gated PC companion service with live /Workspace and /file routes.",
                    Now()
                });
            }

            if (Table("CompanionPermissions").Rows.Count == 0)
                SavePermissions(new CompanionPermissions());

            if (!Table("CompanionTemplates").Rows.Any(row => string.Equals(Cell(row, 0), "JACK", StringComparison.OrdinalIgnoreCase)))
                Table("CompanionTemplates").Rows.Add(TemplateRow(new CompanionTemplate
                {
                    Id = "JACK",
                    Name = "JACK",
                    CompanionName = "JACK",
                    Interests = DefaultInterests,
                    TemplateText = DefaultTemplateText,
                    UpdatedUtc = Now()
                }));

            if (Table("CompanionApplications").Rows.Count == 0)
            {
                Table("CompanionApplications").Rows.Add(new object[] { "app_flstudio", "FL Studio", "1", Now(), "Music production workspace from the JACK template." });
                Table("CompanionApplications").Rows.Add(new object[] { "app_spotify", "Spotify", "1", Now(), "Focus music during non-listening tasks." });
                Table("CompanionApplications").Rows.Add(new object[] { "app_youtube", "YouTube", "1", Now(), "Learning and hobby research." });
            }

            if (Table("CompanionTrainingSettings").Rows.Count == 0)
                SaveTrainingSettingsNoLock(new CompanionTrainingSettings());

            SaveNoLock();
        }
    }

    private CompanionTrainingState GetTrainingStateNoLock()
    {
        return new CompanionTrainingState
        {
            CapturedUtc = Now(),
            Settings = GetTrainingSettingsNoLock(),
            Runs = Rows("CompanionTrainingRuns")
                .Select(ToTrainingRun)
                .OrderByDescending(item => item.StartedUtc, StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList(),
            Evidence = Rows("CompanionTrainingEvidence")
                .Select(ToTrainingEvidence)
                .OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase)
                .Take(80)
                .ToList(),
            SkillDrafts = Rows("CompanionSkillDrafts")
                .Select(ToSkillDraft)
                .OrderByDescending(item => item.UpdatedUtc, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList(),
            SkillExecutions = Rows("CompanionSkillExecutions")
                .Select(ToSkillExecution)
                .OrderByDescending(item => item.CreatedUtc, StringComparer.OrdinalIgnoreCase)
                .Take(40)
                .ToList()
        };
    }

    private CompanionTrainingSettings GetTrainingSettingsNoLock()
    {
        object[]? row = Table("CompanionTrainingSettings").Rows.FirstOrDefault();
        if (row == null)
            return new CompanionTrainingSettings { UpdatedUtc = Now() };

        row = EnsureRowLength(row, 6);
        return new CompanionTrainingSettings
        {
            LearningEnabled = string.IsNullOrWhiteSpace(Cell(row, 0)) || ParseBool(Cell(row, 0)),
            ApprovalMode = string.IsNullOrWhiteSpace(Cell(row, 1)) ? "review_first" : Cell(row, 1),
            ReplayMaxFrames = Math.Max(1, ParseInt(Cell(row, 2)) == 0 ? 300 : ParseInt(Cell(row, 2))),
            ReplayMaxBytes = Math.Max(1024 * 1024, ParseLong(Cell(row, 3)) == 0 ? 262_144_000 : ParseLong(Cell(row, 3))),
            CaptureProfile = string.IsNullOrWhiteSpace(Cell(row, 4)) ? "HybridMinimizedReplay" : Cell(row, 4),
            UpdatedUtc = Cell(row, 5)
        };
    }

    private void SaveTrainingSettingsNoLock(CompanionTrainingSettings settings)
    {
        string approvalMode = NormalizeApprovalMode(settings.ApprovalMode);
        object[] row =
        {
            settings.LearningEnabled.ToString(CultureInfo.InvariantCulture),
            approvalMode,
            Math.Max(1, settings.ReplayMaxFrames <= 0 ? 300 : settings.ReplayMaxFrames).ToString(CultureInfo.InvariantCulture),
            Math.Max(1024 * 1024, settings.ReplayMaxBytes <= 0 ? 262_144_000 : settings.ReplayMaxBytes).ToString(CultureInfo.InvariantCulture),
            string.IsNullOrWhiteSpace(settings.CaptureProfile) ? "HybridMinimizedReplay" : settings.CaptureProfile.Trim(),
            Now()
        };

        Table table = Table("CompanionTrainingSettings");
        if (table.Rows.Count == 0)
            table.Rows.Add(row);
        else
            table.Rows[0] = row;
    }

    private CompanionSkill EnableSkillDraftNoLock(object[] draftRow, string note)
    {
        draftRow = EnsureRowLength(draftRow, 15);
        string skillId = Cell(draftRow, 1);
        if (string.IsNullOrWhiteSpace(skillId))
        {
            skillId = "skill_" + Guid.NewGuid().ToString("N");
            draftRow[1] = skillId;
        }

        CompanionSkill skill = new()
        {
            Id = skillId,
            Name = Cell(draftRow, 4),
            Trigger = Cell(draftRow, 5),
            Steps = Cell(draftRow, 7),
            SourceSessionId = Cell(draftRow, 3),
            Confidence = ParseInt(Cell(draftRow, 10)),
            UpdatedUtc = Now()
        };

        Table skills = Table("CompanionSkills");
        bool updated = false;
        for (int i = 0; i < skills.Rows.Count; i++)
        {
            if (!string.Equals(Cell(skills.Rows[i], 0), skillId, StringComparison.OrdinalIgnoreCase))
                continue;
            skills.Rows[i] = new object[] { skill.Id, skill.Name, skill.Trigger, skill.Steps, skill.SourceSessionId, skill.Confidence.ToString(CultureInfo.InvariantCulture), skill.UpdatedUtc };
            updated = true;
            break;
        }
        if (!updated)
            skills.Rows.Add(new object[] { skill.Id, skill.Name, skill.Trigger, skill.Steps, skill.SourceSessionId, skill.Confidence.ToString(CultureInfo.InvariantCulture), skill.UpdatedUtc });

        draftRow[12] = "enabled";
        draftRow[14] = Now();
        AddAuditNoLock("skill_enabled", note + " " + skill.Name);
        return skill;
    }

    private static int ScoreSkill(CompanionSkill skill, string goal, CompanionEnvironmentSnapshot snapshot)
    {
        string haystack = (skill.Name + " " + skill.Trigger + " " + skill.Steps).ToLowerInvariant();
        var needles = new List<string>();
        AddTokens(needles, goal);
        AddTokens(needles, snapshot.Application);
        AddTokens(needles, snapshot.Window);
        AddTokens(needles, snapshot.Url);
        AddTokens(needles, snapshot.Person);
        foreach (string file in snapshot.Files.Take(6))
            AddTokens(needles, Path.GetFileNameWithoutExtension(file));

        int score = skill.Confidence;
        foreach (string token in needles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (token.Length >= 3 && haystack.Contains(token.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                score += 12;
        }
        return score;
    }

    private static void AddTokens(List<string> tokens, string value)
    {
        foreach (string token in (value ?? "").Split([' ', '\t', '\r', '\n', '-', '_', '.', '/', '\\', ':', '|', ',', ';', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length >= 3)
                tokens.Add(token);
        }
    }

    private static string NormalizeApprovalMode(string approvalMode)
    {
        string mode = (approvalMode ?? "").Trim().ToLowerInvariant();
        return mode switch
        {
            "auto_enable_low_risk" or "low_risk" or "auto-low-risk" => "auto_enable_low_risk",
            "enable_all" or "all" or "danger_enable_all" => "enable_all",
            _ => "review_first"
        };
    }

    private string EnsureScratchSessionNoLock()
    {
        string id = "session_" + Guid.NewGuid().ToString("N");
        Table("CompanionWorkSessions").Rows.Add(new object[] { id, "Companion scratch log", "open", Now(), "", "0", "Auto-created for events outside an active recording." });
        return id;
    }

    private void AddEventNoLock(string sessionId, string eventType, string detail, string application, string window, string url, string filePath, string person, string extra)
    {
        int sequence = Table("CompanionSessionEvents").Rows.Count(row => string.Equals(Cell(row, 1), sessionId, StringComparison.OrdinalIgnoreCase)) + 1;
        Table("CompanionSessionEvents").Rows.Add(new object[]
        {
            "event_" + Guid.NewGuid().ToString("N"),
            sessionId,
            sequence.ToString(CultureInfo.InvariantCulture),
            eventType,
            detail ?? "",
            application ?? "",
            window ?? "",
            url ?? "",
            filePath ?? "",
            person ?? "",
            Now()
        });

        foreach (object[] row in Table("CompanionWorkSessions").Rows)
        {
            if (!string.Equals(Cell(row, 0), sessionId, StringComparison.OrdinalIgnoreCase))
                continue;
            row[5] = sequence.ToString(CultureInfo.InvariantCulture);
            break;
        }
    }

    private void AddInferredSkillNoLock(string sessionId)
    {
        string steps = BuildSkillStepsNoLock(sessionId);
        string id = "skill_" + Guid.NewGuid().ToString("N");
        Table("CompanionSkills").Rows.Add(new object[]
        {
            id,
            "Review recorded session",
            "When a similar session context appears",
            steps,
            sessionId,
            "65",
            Now()
        });
    }

    private CompanionLlmTask GetLlmTaskNoLock(string id)
    {
        object[]? row = Table("CompanionLlmTasks").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), id, StringComparison.OrdinalIgnoreCase));
        if (row == null)
            return new CompanionLlmTask();

        row = EnsureRowLength(row, 14);
        return new CompanionLlmTask
        {
            Id = Cell(row, 0),
            Goal = Cell(row, 1),
            Mode = Cell(row, 2),
            Status = Cell(row, 3),
            Plan = Cell(row, 4),
            CreatedUtc = Cell(row, 5),
            UpdatedUtc = Cell(row, 6),
            Progress = ParseInt(Cell(row, 7)),
            Step = ParseInt(Cell(row, 8)),
            MaxSteps = ParseInt(Cell(row, 9)),
            LastAction = Cell(row, 10),
            LastError = Cell(row, 11),
            RunnerId = Cell(row, 12),
            ModelEndpoint = Cell(row, 13)
        };
    }

    private void StopLlmTaskNoLock(string reason)
    {
        foreach (object[] task in Table("CompanionLlmTasks").Rows)
        {
            if (string.Equals(Cell(task, 3), "stopped", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Cell(task, 3), "completed", StringComparison.OrdinalIgnoreCase))
                continue;
            task[3] = "stopped";
            task[4] = reason ?? "Stopped.";
            task[6] = Now();
        }
    }

    private void StopLlmTaskByIdNoLock(string taskId, string reason)
    {
        foreach (object[] task in Table("CompanionLlmTasks").Rows)
        {
            if (!string.Equals(Cell(task, 0), taskId, StringComparison.OrdinalIgnoreCase))
                continue;
            task[3] = "stopped";
            task[4] = string.IsNullOrWhiteSpace(reason) ? "Stopped by user." : reason.Trim();
            task[6] = Now();
            task[10] = "approval_denied";
            break;
        }
    }

    private List<CompanionApprovalRequest> BuildPendingApprovalsNoLock()
    {
        var approvals = new List<CompanionApprovalRequest>();
        CompanionPermissions permissions = GetPermissionsNoLock();
        foreach (object[] rawTask in Table("CompanionLlmTasks").Rows)
        {
            object[] task = EnsureRowLength(rawTask, 14);
            if (!string.Equals(Cell(task, 3), "approval_required", StringComparison.OrdinalIgnoreCase))
                continue;

            approvals.Add(new CompanionApprovalRequest
            {
                Id = "llm_live_input_" + Cell(task, 0),
                Kind = "llm_task",
                Capability = "liveInput",
                Title = "Resume LLM desktop task",
                Detail = FirstNonEmpty(Cell(task, 4), Cell(task, 1), "The LLM runner is paused until Live Input is approved."),
                Source = "LLM Control",
                RelatedTaskId = Cell(task, 0),
                CreatedUtc = Cell(task, 5),
                UpdatedUtc = Cell(task, 6),
                RecommendedAction = permissions.LiveInput ? "Queue paused task" : "Enable Live Input and queue paused task"
            });
        }

        CompanionApprovalRequest? liveInputRequest = BuildCapabilityApprovalFromAuditNoLock(
            "cap_live_input",
            "liveInput",
            "Live Input requested",
            "Remote Desktop",
            "Live Input is disabled",
            permissions.LiveInput);
        if (liveInputRequest != null && approvals.All(item => !item.Capability.Equals("liveInput", StringComparison.OrdinalIgnoreCase)))
            approvals.Add(liveInputRequest);

        CompanionApprovalRequest? filesRequest = BuildCapabilityApprovalFromAuditNoLock(
            "cap_use_files",
            "useFiles",
            "File access requested",
            "File Sharing",
            "Use Files permission is disabled",
            permissions.UseFiles);
        if (filesRequest != null)
            approvals.Add(filesRequest);

        foreach (object[] row in Table("CompanionAuditEvents").Rows
                     .Where(item => string.Equals(Cell(item, 1), "control_request_blocked", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(item => Cell(item, 3), StringComparer.OrdinalIgnoreCase)
                     .Take(12))
        {
            string capability = NormalizeApprovalCapability(ReadCapabilityPrefix(Cell(row, 2)));
            if (string.IsNullOrWhiteSpace(capability) ||
                IsCapabilityAllowed(permissions, capability) ||
                approvals.Any(item => item.Capability.Equals(capability, StringComparison.OrdinalIgnoreCase)) ||
                HasCapabilityDenialAfterNoLock(capability, Cell(row, 3)))
                continue;

            approvals.Add(new CompanionApprovalRequest
            {
                Id = "cap_" + capability,
                Kind = "capability",
                Capability = capability,
                Title = CapabilityLabel(capability) + " requested",
                Detail = Cell(row, 2),
                Source = "Companion Control",
                CreatedUtc = Cell(row, 3),
                UpdatedUtc = Cell(row, 3),
                RecommendedAction = "Enable " + CapabilityLabel(capability)
            });
        }

        return approvals
            .OrderByDescending(item => item.UpdatedUtc, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private CompanionApprovalRequest? BuildCapabilityApprovalFromAuditNoLock(string id, string capability, string title, string source, string marker, bool alreadyEnabled)
    {
        if (alreadyEnabled)
            return null;

        object[]? row = Table("CompanionAuditEvents").Rows
            .Where(item => string.Equals(Cell(item, 1), "control_blocked", StringComparison.OrdinalIgnoreCase) &&
                           Cell(item, 2).IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(item => Cell(item, 3), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (row == null)
            return null;

        if (HasCapabilityDenialAfterNoLock(capability, Cell(row, 3)))
            return null;

        return new CompanionApprovalRequest
        {
            Id = id,
            Kind = "capability",
            Capability = capability,
            Title = title,
            Detail = Cell(row, 2),
            Source = source,
            CreatedUtc = Cell(row, 3),
            UpdatedUtc = Cell(row, 3),
            RecommendedAction = "Enable " + CapabilityLabel(capability)
        };
    }

    private void SavePermissionsNoLock(CompanionPermissions permissions)
    {
        Table table = Table("CompanionPermissions");
        table.Rows.Clear();
        table.Rows.Add(new object[]
        {
            permissions.HumanInteraction ? "true" : "false",
            permissions.SpendMoney ? "true" : "false",
            permissions.AccountLogin ? "true" : "false",
            permissions.UseFiles ? "true" : "false",
            permissions.PcSettings ? "true" : "false",
            permissions.InternetAccess ? "true" : "false",
            permissions.LiveInput ? "true" : "false",
            Now()
        });
        if (permissions.LiveInput)
            QueueApprovalRequiredLlmTasksNoLock();
    }

    private void QueueApprovalRequiredLlmTasksNoLock()
    {
        Table taskTable = Table("CompanionLlmTasks");
        for (int i = 0; i < taskTable.Rows.Count; i++)
        {
            object[] task = EnsureRowLength(taskTable.Rows[i], 14);
            taskTable.Rows[i] = task;
            if (!string.Equals(Cell(task, 3), "approval_required", StringComparison.OrdinalIgnoreCase))
                continue;
            task[3] = "queued";
            task[4] = "Live Input is enabled. Task queued for the Companion LLM runner.";
            task[6] = Now();
            task[10] = "approval_granted";
        }
    }

    private static void ApplyCapabilityApprovalNoLock(CompanionPermissions permissions, string capability)
    {
        switch (NormalizeApprovalCapability(capability).ToLowerInvariant())
        {
            case "humaninteraction":
                permissions.HumanInteraction = true;
                break;
            case "spendmoney":
                permissions.SpendMoney = true;
                break;
            case "accountlogin":
                permissions.AccountLogin = true;
                break;
            case "usefiles":
                permissions.UseFiles = true;
                break;
            case "pcsettings":
                permissions.PcSettings = true;
                break;
            case "internetaccess":
                permissions.InternetAccess = true;
                break;
            case "liveinput":
                permissions.LiveInput = true;
                break;
        }
    }

    private static string CapabilityLabel(string capability)
    {
        return (capability ?? "").Trim().ToLowerInvariant() switch
        {
            "humaninteraction" => "Human Interaction",
            "spendmoney" => "Spend Money",
            "accountlogin" => "Account Login",
            "usefiles" => "Use Files",
            "pcsettings" => "PC Settings",
            "internetaccess" => "Internet Access",
            "liveinput" => "Live Input",
            _ => capability ?? ""
        };
    }

    private bool HasCapabilityDenialAfterNoLock(string capability, string blockedUtc)
    {
        object[]? denial = Table("CompanionAuditEvents").Rows
            .Where(item => string.Equals(Cell(item, 1), "approval_denied", StringComparison.OrdinalIgnoreCase) &&
                           Cell(item, 2).IndexOf("(" + capability + ")", StringComparison.OrdinalIgnoreCase) >= 0)
            .OrderByDescending(item => Cell(item, 3), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return denial != null && string.Compare(Cell(denial, 3), blockedUtc ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string ReadCapabilityPrefix(string value)
    {
        value = (value ?? "").Trim();
        int index = value.IndexOf(':');
        return index < 0 ? value : value[..index];
    }

    private static string NormalizeApprovalCapability(string capability)
    {
        return (capability ?? "").Trim().ToLowerInvariant() switch
        {
            "human" or "chat" or "humaninteraction" => "humanInteraction",
            "money" or "spend" or "spendmoney" => "spendMoney",
            "login" or "account" or "accountlogin" => "accountLogin",
            "files" or "file" or "usefiles" => "useFiles",
            "settings" or "pcsettings" => "pcSettings",
            "internet" or "web" or "internetaccess" => "internetAccess",
            "input" or "liveinput" or "remotecontrol" => "liveInput",
            _ => ""
        };
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        return "";
    }

    private CompanionSharedFile AddSharedFileNoLock(string id, string name, string path, string note, string relativePath = "", string kind = "file", bool requiresApproval = false, string approvalReason = "")
    {
        var info = new FileInfo(path);
        string now = Now();
        Table("CompanionSharedFiles").Rows.Add(new object[]
        {
            id,
            name,
            path,
            info.Exists ? info.Length.ToString(CultureInfo.InvariantCulture) : "0",
            note ?? "",
            now,
            relativePath ?? "",
            string.IsNullOrWhiteSpace(kind) ? "file" : kind,
            requiresApproval ? "true" : "false",
            approvalReason ?? ""
        });
        return new CompanionSharedFile
        {
            Id = id,
            Name = name,
            Path = path,
            SizeBytes = info.Exists ? info.Length : 0,
            Note = note ?? "",
            CreatedUtc = now,
            RelativePath = relativePath ?? "",
            Kind = string.IsNullOrWhiteSpace(kind) ? "file" : kind,
            RequiresApproval = requiresApproval,
            ApprovalReason = approvalReason ?? ""
        };
    }

    private CompanionSharedFile ApprovalRequiredFile(string name, string path, string approvalReason)
    {
        return new CompanionSharedFile
        {
            Id = "",
            Name = name ?? "",
            Path = path ?? "",
            Note = "Per-file approval is required before Companion can share this item.",
            CreatedUtc = Now(),
            RequiresApproval = true,
            ApprovalReason = approvalReason ?? "Sensitive file approval is required."
        };
    }

    private string BuildSkillStepsNoLock(string sessionId)
    {
        var events = Table("CompanionSessionEvents").Rows
            .Where(row => string.Equals(Cell(row, 1), sessionId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(row => ParseInt(Cell(row, 2)))
            .Take(20)
            .ToList();
        var apps = events.Select(row => Cell(row, 5)).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5);
        var people = events.Select(row => Cell(row, 9)).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5);
        return "1. Restore context from the recorded session. 2. Check foreground apps/windows in order. 3. Reuse relevant apps: " +
               string.Join(", ", apps) +
               ". 4. Consider people context: " +
               string.Join(", ", people) +
               ". 5. Ask before any gated action involving files, accounts, money, humans, internet, settings, or live input.";
    }

    private void AddAuditNoLock(string action, string detail)
    {
        Table("CompanionAuditEvents").Rows.Add(new object[]
        {
            "audit_" + Guid.NewGuid().ToString("N"),
            action ?? "",
            detail ?? "",
            Now()
        });
    }

    private void UpsertApplicationNoLock(string name, string note)
    {
        Table table = Table("CompanionApplications");
        foreach (object[] row in table.Rows)
        {
            if (!string.Equals(Cell(row, 1), name, StringComparison.OrdinalIgnoreCase))
                continue;
            row[2] = (ParseInt(Cell(row, 2)) + 1).ToString(CultureInfo.InvariantCulture);
            row[3] = Now();
            if (!string.IsNullOrWhiteSpace(note))
                row[4] = note;
            return;
        }

        table.Rows.Add(new object[] { "app_" + Guid.NewGuid().ToString("N"), name, "1", Now(), note ?? "" });
    }

    private void UpsertPersonNoLock(string name, string note)
    {
        Table table = Table("CompanionPeople");
        foreach (object[] row in table.Rows)
        {
            if (!string.Equals(Cell(row, 1), name, StringComparison.OrdinalIgnoreCase))
                continue;
            row[5] = (ParseInt(Cell(row, 5)) + 1).ToString(CultureInfo.InvariantCulture);
            row[11] = Now();
            if (!string.IsNullOrWhiteSpace(note))
                row[10] = note;
            return;
        }

        table.Rows.Add(new object[]
        {
            "person_" + Guid.NewGuid().ToString("N"),
            name,
            "35",
            "50",
            "50",
            "1",
            "50",
            "20",
            "35",
            "40",
            note ?? "",
            Now()
        });
    }

    private void RegisterFileNoLock(string sessionId, string filePath, string kind, string note)
    {
        filePath = (filePath ?? "").Trim();
        if (filePath.Length == 0)
            return;

        Table table = Table("CompanionFiles");
        foreach (object[] row in table.Rows)
        {
            if (!string.Equals(Cell(row, 1), sessionId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Cell(row, 2), filePath, StringComparison.OrdinalIgnoreCase))
                continue;
            row[4] = string.IsNullOrWhiteSpace(kind) ? Cell(row, 4) : kind;
            row[5] = Now();
            row[6] = note ?? "";
            return;
        }

        table.Rows.Add(new object[]
        {
            "file_" + Guid.NewGuid().ToString("N"),
            sessionId,
            filePath,
            Path.GetFileName(filePath),
            string.IsNullOrWhiteSpace(kind) ? "file" : kind.Trim(),
            Now(),
            note ?? ""
        });
    }

    private static CompanionTrainingRun ToTrainingRun(object[] row)
    {
        row = EnsureRowLength(row, 11);
        return new CompanionTrainingRun
        {
            Id = Cell(row, 0),
            SourceSessionId = Cell(row, 1),
            Status = Cell(row, 2),
            Profile = Cell(row, 3),
            Progress = ParseInt(Cell(row, 4)),
            RiskLevel = Cell(row, 5),
            Summary = Cell(row, 6),
            StartedUtc = Cell(row, 7),
            CompletedUtc = Cell(row, 8),
            ModelEndpoint = Cell(row, 9),
            Error = Cell(row, 10)
        };
    }

    private static CompanionTrainingEvidence ToTrainingEvidence(object[] row)
    {
        row = EnsureRowLength(row, 16);
        return new CompanionTrainingEvidence
        {
            Id = Cell(row, 0),
            RunId = Cell(row, 1),
            SessionId = Cell(row, 2),
            Sequence = ParseInt(Cell(row, 3)),
            EventType = Cell(row, 4),
            Detail = Cell(row, 5),
            Application = Cell(row, 6),
            Window = Cell(row, 7),
            Url = Cell(row, 8),
            FilePath = Cell(row, 9),
            Person = Cell(row, 10),
            InputTrace = Cell(row, 11),
            KeyframePath = Cell(row, 12),
            Summary = Cell(row, 13),
            SensitivityFlags = Cell(row, 14),
            CreatedUtc = Cell(row, 15)
        };
    }

    private static CompanionSkillDraft ToSkillDraft(object[] row)
    {
        row = EnsureRowLength(row, 15);
        return new CompanionSkillDraft
        {
            Id = Cell(row, 0),
            SkillId = Cell(row, 1),
            RunId = Cell(row, 2),
            SourceSessionId = Cell(row, 3),
            Name = Cell(row, 4),
            Trigger = Cell(row, 5),
            Prerequisites = Cell(row, 6),
            Steps = Cell(row, 7),
            SafetyGates = Cell(row, 8),
            EvidenceRefs = Cell(row, 9),
            Confidence = ParseInt(Cell(row, 10)),
            RiskLevel = Cell(row, 11),
            Status = Cell(row, 12),
            CreatedUtc = Cell(row, 13),
            UpdatedUtc = Cell(row, 14)
        };
    }

    private static CompanionSkillExecution ToSkillExecution(object[] row)
    {
        row = EnsureRowLength(row, 8);
        return new CompanionSkillExecution
        {
            Id = Cell(row, 0),
            SkillId = Cell(row, 1),
            TaskId = Cell(row, 2),
            MatchedContext = Cell(row, 3),
            Result = Cell(row, 4),
            UserRating = ParseInt(Cell(row, 5)),
            Notes = Cell(row, 6),
            CreatedUtc = Cell(row, 7)
        };
    }

    private CompanionPermissions GetPermissionsNoLock()
    {
        object[]? row = Table("CompanionPermissions").Rows.FirstOrDefault();
        if (row == null)
            return new CompanionPermissions();

        return new CompanionPermissions
        {
            HumanInteraction = ParseBool(Cell(row, 0)),
            SpendMoney = ParseBool(Cell(row, 1)),
            AccountLogin = ParseBool(Cell(row, 2)),
            UseFiles = ParseBool(Cell(row, 3)),
            PcSettings = ParseBool(Cell(row, 4)),
            InternetAccess = ParseBool(Cell(row, 5)),
            LiveInput = ParseBool(Cell(row, 6)),
            UpdatedUtc = Cell(row, 7)
        };
    }

    private CompanionTemplate GetJackTemplateNoLock()
    {
        object[]? row = Table("CompanionTemplates").Rows.FirstOrDefault(item => string.Equals(Cell(item, 0), "JACK", StringComparison.OrdinalIgnoreCase));
        if (row == null)
        {
            return new CompanionTemplate
            {
                Id = "JACK",
                Name = "JACK",
                CompanionName = "JACK",
                Interests = DefaultInterests,
                TemplateText = DefaultTemplateText,
                UpdatedUtc = Now()
            };
        }

        return new CompanionTemplate
        {
            Id = Cell(row, 0),
            Name = Cell(row, 1),
            CompanionName = Cell(row, 2),
            Interests = Cell(row, 3),
            TemplateText = Cell(row, 4),
            UpdatedUtc = Cell(row, 5)
        };
    }

    private static object[] TemplateRow(CompanionTemplate template)
    {
        return new object[]
        {
            template.Id,
            template.Name,
            template.CompanionName,
            template.Interests,
            template.TemplateText,
            template.UpdatedUtc
        };
    }

    private static object[] EnsureRowLength(object[] row, int length)
    {
        if (row.Length >= length)
            return row;

        object[] expanded = new object[length];
        Array.Copy(row, expanded, row.Length);
        for (int i = row.Length; i < expanded.Length; i++)
            expanded[i] = "";
        return expanded;
    }

    private bool IsCapabilityAllowed(CompanionPermissions permissions, string capability)
    {
        string key = (capability ?? "").Trim().ToLowerInvariant();
        return key switch
        {
            "human" or "chat" or "humaninteraction" => permissions.HumanInteraction,
            "money" or "spend" or "spendmoney" => permissions.SpendMoney,
            "login" or "account" or "accountlogin" => permissions.AccountLogin,
            "files" or "file" or "usefiles" => permissions.UseFiles,
            "settings" or "pcsettings" => permissions.PcSettings,
            "internet" or "web" => permissions.InternetAccess,
            "input" or "liveinput" or "remotecontrol" => permissions.LiveInput,
            _ => false
        };
    }

    private Table Table(string name)
    {
        return _database.Tables.GetOrAdd(name, _ => new Table(name));
    }

    private List<object[]> Rows(string name)
    {
        return Table(name).Rows.ToList();
    }

    private void SaveNoLock()
    {
        _dataServer.Save();
    }

    private static string Cell(object[] row, int index)
    {
        return row != null && index >= 0 && index < row.Length ? row[index]?.ToString() ?? "" : "";
    }

    private static bool ParseBool(string value)
    {
        return bool.TryParse(value, out bool result) && result;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
    }

    private static long ParseLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result) ? result : 0;
    }

    private static string Now()
    {
        return DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    private static void AddInterest(SortedSet<string> interests, string value)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0)
            return;
        interests.Add(value);
    }

    private static string BuildSafeFileName(string fileName)
    {
        fileName = string.IsNullOrWhiteSpace(fileName) ? "companion-file.bin" : fileName.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return fileName.Length > 140 ? fileName[..140] : fileName;
    }

    private static string BuildSafeRelativePath(string relativePath)
    {
        relativePath = string.IsNullOrWhiteSpace(relativePath) ? "companion-file.bin" : relativePath.Trim().Replace('\\', '/');
        var segments = new List<string>();
        foreach (string rawSegment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (rawSegment is "." or "..")
                continue;
            segments.Add(BuildSafeFileName(rawSegment));
        }

        return segments.Count == 0 ? "companion-file.bin" : Path.Combine(segments.ToArray());
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return Math.Round(bytes / 1024d / 1024d / 1024d, 1).ToString(CultureInfo.InvariantCulture) + " GB";
        if (bytes >= 1024L * 1024L)
            return Math.Round(bytes / 1024d / 1024d, 1).ToString(CultureInfo.InvariantCulture) + " MB";
        if (bytes >= 1024L)
            return Math.Round(bytes / 1024d, 1).ToString(CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
    }

    private static string GetFolderApprovalReason(string folderPath)
    {
        string folderReason = GetFileApprovalReason(folderPath);
        if (!string.IsNullOrWhiteSpace(folderReason))
            return folderReason;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories).Take(500))
            {
                string reason = GetFileApprovalReason(file);
                if (!string.IsNullOrWhiteSpace(reason))
                    return "Folder contains a sensitive file: " + Path.GetFileName(file) + ". " + reason;
            }
        }
        catch (Exception ex)
        {
            return "Folder scan failed and needs explicit approval: " + ex.Message;
        }

        return "";
    }

    private static string GetFileApprovalReason(string pathOrName)
    {
        string value = (pathOrName ?? "").Trim();
        if (value.Length == 0)
            return "";

        string extension = Path.GetExtension(value).ToLowerInvariant();
        string[] riskyExtensions =
        [
            ".bat", ".cmd", ".com", ".cpl", ".dll", ".exe", ".js", ".jse", ".msi", ".msp",
            ".ps1", ".psm1", ".reg", ".scr", ".vbe", ".vbs", ".wsf", ".jar"
        ];
        if (riskyExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return "Executable or script-like files require explicit per-file approval.";

        string normalized = "/" + value.Replace('\\', '/').Trim('/').ToLowerInvariant() + "/";
        string[] sensitiveSegments =
        [
            "/windows/",
            "/program files/",
            "/program files",
            "/program files (x86)/",
            "/program files (x86)",
            "/users/",
            "/appdata/",
            "/.ssh/",
            "/.gnupg/",
            "/microsoft/credentials/",
            "/system32/"
        ];
        if (sensitiveSegments.Any(segment => normalized.Contains(segment, StringComparison.OrdinalIgnoreCase)))
            return "Sensitive system, app data, credential, or executable directory requires explicit per-file approval.";

        return "";
    }

    private const string DefaultInterests = "Programming, YouTube learning, car stuff, turbos, cheap small-engine boost, FL Studio, Neurofunk, Jump Up, Drum and Bass, Riddim Dubstep, Spotify focus music";

    private const string DefaultTemplateText = "I like Programming. I like watching Youtube, and learning new things. Like car stuff, turbos are cool. Big Boost in a small engine because it's cheap. Gets the job done. I like to make music in FL Studio. Neurofunk, Jump up, and other drum and bass. Also, Riddim Dubstep. I like to use Spotify to listen to music while I'm doing stuff/tasks that don't require listening to something to interpret. you know, like people do when working out or something. just to keep me focused.\n\nyou're my friend so whatever you find you should share with me and ill share some stuff with you!";
}

public sealed class CompanionWorkspaceState
{
    public string CapturedUtc { get; set; } = "";
    public string DataPath { get; set; } = "";
    public string ActiveSessionId { get; set; } = "";
    public bool IsRecording { get; set; }
    public List<CompanionProject> Projects { get; set; } = new();
    public List<CompanionSessionSummary> Sessions { get; set; } = new();
    public List<CompanionEvent> RecentEvents { get; set; } = new();
    public List<CompanionTemplate> Templates { get; set; } = new();
    public CompanionPermissions Permissions { get; set; } = new();
    public List<CompanionPerson> People { get; set; } = new();
    public List<CompanionApplication> Applications { get; set; } = new();
    public List<CompanionSkill> Skills { get; set; } = new();
    public List<CompanionAuditEvent> Audit { get; set; } = new();
    public List<CompanionLlmTask> LlmTasks { get; set; } = new();
    public List<CompanionApprovalRequest> PendingApprovals { get; set; } = new();
    public CompanionLlmRunnerStatus Runner { get; set; } = new();
    public CompanionTrainingState Training { get; set; } = new();
}

public sealed class CompanionTrainingState
{
    public string CapturedUtc { get; set; } = "";
    public CompanionTrainingSettings Settings { get; set; } = new();
    public List<CompanionTrainingRun> Runs { get; set; } = new();
    public List<CompanionTrainingEvidence> Evidence { get; set; } = new();
    public List<CompanionSkillDraft> SkillDrafts { get; set; } = new();
    public List<CompanionSkillExecution> SkillExecutions { get; set; } = new();
}

public sealed class CompanionTrainingSettings
{
    public bool LearningEnabled { get; set; } = true;
    public string ApprovalMode { get; set; } = "review_first";
    public int ReplayMaxFrames { get; set; } = 300;
    public long ReplayMaxBytes { get; set; } = 262_144_000;
    public string CaptureProfile { get; set; } = "HybridMinimizedReplay";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionTrainingRun
{
    public string Id { get; set; } = "";
    public string SourceSessionId { get; set; } = "";
    public string Status { get; set; } = "";
    public string Profile { get; set; } = "";
    public int Progress { get; set; }
    public string RiskLevel { get; set; } = "";
    public string Summary { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string CompletedUtc { get; set; } = "";
    public string ModelEndpoint { get; set; } = "";
    public string Error { get; set; } = "";
}

public sealed class CompanionTrainingEvidence
{
    public string Id { get; set; } = "";
    public string RunId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Sequence { get; set; }
    public string EventType { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Application { get; set; } = "";
    public string Window { get; set; } = "";
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Person { get; set; } = "";
    public string InputTrace { get; set; } = "";
    public string KeyframePath { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SensitivityFlags { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class CompanionSkillDraft
{
    public string Id { get; set; } = "";
    public string SkillId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string SourceSessionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Prerequisites { get; set; } = "";
    public string Steps { get; set; } = "";
    public string SafetyGates { get; set; } = "";
    public string EvidenceRefs { get; set; } = "";
    public int Confidence { get; set; }
    public string RiskLevel { get; set; } = "medium";
    public string Status { get; set; } = "draft";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionSkillExecution
{
    public string Id { get; set; } = "";
    public string SkillId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string MatchedContext { get; set; } = "";
    public string Result { get; set; } = "";
    public int UserRating { get; set; }
    public string Notes { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class CompanionSkillReviewResult
{
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public CompanionSkillDraft? Draft { get; set; }
    public CompanionSkill? Skill { get; set; }
}

public sealed class CompanionTrainingSessionSnapshot
{
    public CompanionSessionSummary Session { get; set; } = new();
    public List<CompanionEvent> Events { get; set; } = new();
    public List<CompanionFileRecord> Files { get; set; } = new();
}

public sealed class CompanionFileState
{
    public string CapturedUtc { get; set; } = "";
    public List<CompanionFileRecord> Files { get; set; } = new();
    public List<CompanionSharedFile> SharedFiles { get; set; } = new();
}

public sealed class CompanionProject
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionSessionSummary
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string StartedUtc { get; set; } = "";
    public string StoppedUtc { get; set; } = "";
    public int EventCount { get; set; }
    public string Summary { get; set; } = "";
}

public sealed class CompanionEvent
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public int Sequence { get; set; }
    public string EventType { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Application { get; set; } = "";
    public string Window { get; set; } = "";
    public string Url { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Person { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class CompanionTemplate
{
    public string Id { get; set; } = "JACK";
    public string Name { get; set; } = "JACK";
    public string CompanionName { get; set; } = "JACK";
    public string Interests { get; set; } = "";
    public string TemplateText { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionPermissions
{
    public bool HumanInteraction { get; set; }
    public bool SpendMoney { get; set; }
    public bool AccountLogin { get; set; }
    public bool UseFiles { get; set; }
    public bool PcSettings { get; set; }
    public bool InternetAccess { get; set; }
    public bool LiveInput { get; set; }
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionPerson
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Risk { get; set; }
    public int Helpfulness { get; set; }
    public int Relevance { get; set; }
    public int Frequency { get; set; }
    public int TrustConsent { get; set; }
    public int FinancialExposure { get; set; }
    public int IntegrityIntentRisk { get; set; }
    public int Confidence { get; set; }
    public string Notes { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionApplication
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Frequency { get; set; }
    public string LastSeenUtc { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed class CompanionSkill
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Trigger { get; set; } = "";
    public string Steps { get; set; } = "";
    public string SourceSessionId { get; set; } = "";
    public int Confidence { get; set; }
    public string UpdatedUtc { get; set; } = "";
}

public sealed class CompanionAuditEvent
{
    public string Id { get; set; } = "";
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
}

public sealed class CompanionFileRecord
{
    public string Id { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string LastSeenUtc { get; set; } = "";
    public string Note { get; set; } = "";
}

public sealed class CompanionControlDecision
{
    public bool Ok { get; set; }
    public bool ApprovalRequired { get; set; }
    public string Capability { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class CompanionLlmTask
{
    public string Id { get; set; } = "";
    public string Goal { get; set; } = "";
    public string Mode { get; set; } = "";
    public string Status { get; set; } = "";
    public string Plan { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public int Progress { get; set; }
    public int Step { get; set; }
    public int MaxSteps { get; set; } = 8;
    public string LastAction { get; set; } = "";
    public string LastError { get; set; } = "";
    public string RunnerId { get; set; } = "";
    public string ModelEndpoint { get; set; } = "";
}

public sealed class CompanionSharedFile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Note { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string Kind { get; set; } = "file";
    public bool RequiresApproval { get; set; }
    public string ApprovalReason { get; set; } = "";
}

public sealed class CompanionApprovalRequest
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Capability { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Source { get; set; } = "";
    public string RelatedTaskId { get; set; } = "";
    public string CreatedUtc { get; set; } = "";
    public string UpdatedUtc { get; set; } = "";
    public string RecommendedAction { get; set; } = "";
}

public sealed class CompanionApprovalDecision
{
    public bool Ok { get; set; }
    public bool Approved { get; set; }
    public string Message { get; set; } = "";
    public CompanionApprovalRequest? Request { get; set; }
    public List<CompanionApprovalRequest> PendingApprovals { get; set; } = new();
}

public sealed class CompanionEmergencyStopResult
{
    public bool Ok { get; set; }
    public bool LiveInputWasEnabled { get; set; }
    public bool RecordingWasActive { get; set; }
    public string Message { get; set; } = "";
}
