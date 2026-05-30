using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SocketJack.Net
{
    internal sealed class SockJackDmlService
    {
        private readonly object _sync = new object();
        private readonly string _root;
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };

        public SockJackDmlService(string chatSessionRoot)
        {
            _root = Path.Combine(string.IsNullOrWhiteSpace(chatSessionRoot) ? AppContext.BaseDirectory : chatSessionRoot, "SockJackDml");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(Path.Combine(_root, "owners"));
        }

        public SockJackDmlSummary GetSummary(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            var missions = ListMissions(ownerKey);
            var events = new List<MagicMissionEvent>();
            foreach (MagicMissionRecord mission in missions)
                events.AddRange(ListMissionEvents(ownerKey, mission.Id));

            var assists = ListAssistSessions(ownerKey);
            var evidence = ListEvidencePackets(ownerKey);
            var accessibility = ListAccessibilityEvents(ownerKey, "");

            return new SockJackDmlSummary
            {
                OwnerKey = ownerKey,
                MissionCount = missions.Count,
                ActiveMissionCount = missions.Count(m => string.Equals(m.Status, "active", StringComparison.OrdinalIgnoreCase)),
                PendingApprovalCount = events.Count(e => e.RequiresApproval && !e.Approved),
                AssistSessionCount = assists.Count,
                ActiveAssistSessionCount = assists.Count(s => string.Equals(s.Status, "approved", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Status, "pending", StringComparison.OrdinalIgnoreCase)),
                EvidencePacketCount = evidence.Count,
                AccessibilityEventCount = accessibility.Count,
                UpdatedUtc = DateTimeOffset.UtcNow.ToString("O")
            };
        }

        public List<MagicMissionRecord> ListMissions(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            lock (_sync)
            {
                return ReadList<MagicMissionRecord>(MissionsPath(ownerKey))
                    .OrderByDescending(m => FirstNonEmpty(m.UpdatedUtc, m.CreatedUtc))
                    .ToList();
            }
        }

        public MagicMissionRecord FindMission(string ownerKey, string missionId)
        {
            missionId = NormalizeId(missionId);
            if (string.IsNullOrWhiteSpace(missionId))
                return null;

            return ListMissions(ownerKey).FirstOrDefault(m => string.Equals(m.Id, missionId, StringComparison.OrdinalIgnoreCase));
        }

        public MagicMissionRecord CreateMission(string ownerKey, string title, string objective, string priority, IEnumerable<string> tags)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string now = DateTimeOffset.UtcNow.ToString("O");
            var mission = new MagicMissionRecord
            {
                Id = "mission_" + ShortHash(ownerKey + "|" + title + "|" + now),
                OwnerKey = ownerKey,
                Title = NormalizeText(title, 160, "Untitled mission"),
                Objective = NormalizeText(objective, 1200, ""),
                Status = "active",
                Priority = NormalizeToken(priority, "normal"),
                CurrentPhase = "Phase 1 - Mission timeline",
                ProgressPercent = 25,
                Tags = NormalizeTags(tags),
                CreatedUtc = now,
                UpdatedUtc = now
            };

            lock (_sync)
            {
                var missions = ReadList<MagicMissionRecord>(MissionsPath(ownerKey));
                missions.RemoveAll(m => string.Equals(m.Id, mission.Id, StringComparison.OrdinalIgnoreCase));
                missions.Add(mission);
                WriteList(MissionsPath(ownerKey), missions);
                WriteList(EventsPath(ownerKey, mission.Id), new List<MagicMissionEvent>
                {
                    new MagicMissionEvent
                    {
                        Id = "evt_" + ShortHash(mission.Id + "|created|" + now),
                        MissionId = mission.Id,
                        OwnerKey = ownerKey,
                        Type = "mission-created",
                        Title = "Mission created",
                        Detail = mission.Objective,
                        Actor = "sockjackdml",
                        Status = "complete",
                        CreatedUtc = now,
                        RequiresApproval = false,
                        Approved = true
                    }
                });
            }

            return mission;
        }

        public List<MagicMissionEvent> ListMissionEvents(string ownerKey, string missionId)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            missionId = NormalizeId(missionId);
            if (string.IsNullOrWhiteSpace(missionId))
                return new List<MagicMissionEvent>();

            lock (_sync)
            {
                return ReadList<MagicMissionEvent>(EventsPath(ownerKey, missionId))
                    .OrderBy(e => e.CreatedUtc)
                    .ToList();
            }
        }

        public MagicMissionEvent AddMissionEvent(
            string ownerKey,
            string missionId,
            string type,
            string title,
            string detail,
            string actor,
            string status,
            bool requiresApproval,
            string evidencePacketId = "")
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            missionId = NormalizeId(missionId);
            if (string.IsNullOrWhiteSpace(missionId))
                throw new InvalidOperationException("A missionId is required.");

            MagicMissionRecord mission = FindMission(ownerKey, missionId);
            if (mission == null)
                throw new InvalidOperationException("Mission not found: " + missionId);

            string now = DateTimeOffset.UtcNow.ToString("O");
            var missionEvent = new MagicMissionEvent
            {
                Id = "evt_" + ShortHash(missionId + "|" + title + "|" + now),
                MissionId = missionId,
                OwnerKey = ownerKey,
                Type = NormalizeToken(type, "note"),
                Title = NormalizeText(title, 180, "Mission update"),
                Detail = NormalizeText(detail, 6000, ""),
                Actor = NormalizeText(actor, 80, "operator"),
                Status = NormalizeToken(status, requiresApproval ? "pending" : "complete"),
                CreatedUtc = now,
                EvidencePacketId = NormalizeId(evidencePacketId),
                RequiresApproval = requiresApproval,
                Approved = !requiresApproval
            };

            lock (_sync)
            {
                var events = ReadList<MagicMissionEvent>(EventsPath(ownerKey, missionId));
                events.Add(missionEvent);
                WriteList(EventsPath(ownerKey, missionId), events);
                TouchMissionLocked(ownerKey, missionId, "Phase 2 - Backend/API implementation", Math.Max(mission.ProgressPercent, requiresApproval ? 45 : 65));
            }

            return missionEvent;
        }

        public MagicMissionEvent DecideMissionEvent(string ownerKey, string missionId, string eventId, bool approved, string decision, string note)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            missionId = NormalizeId(missionId);
            eventId = NormalizeId(eventId);
            string now = DateTimeOffset.UtcNow.ToString("O");

            lock (_sync)
            {
                var events = ReadList<MagicMissionEvent>(EventsPath(ownerKey, missionId));
                MagicMissionEvent missionEvent = events.FirstOrDefault(e => string.Equals(e.Id, eventId, StringComparison.OrdinalIgnoreCase));
                if (missionEvent == null)
                    throw new InvalidOperationException("Mission event not found: " + eventId);

                missionEvent.Approved = approved;
                missionEvent.Status = approved ? "approved" : "rejected";
                missionEvent.Decision = NormalizeText(decision, 120, approved ? "approved" : "rejected");
                missionEvent.DecisionNote = NormalizeText(note, 2000, "");
                missionEvent.DecidedUtc = now;
                WriteList(EventsPath(ownerKey, missionId), events);
                TouchMissionLocked(ownerKey, missionId, "Phase 4 - Approval and failure handling", 85);
                return missionEvent;
            }
        }

        public List<MagicMissionPack> ListMissionPacks()
        {
            return new List<MagicMissionPack>
            {
                BuildIncidentPack(),
                BuildLaunchPack(),
                BuildRemoteAssistPack(),
                BuildEvidencePack(),
                BuildAccessibilityPack()
            };
        }

        public MagicMissionPackRun RunMissionPack(string ownerKey, string missionId, string packId)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            packId = NormalizeId(packId);
            MagicMissionPack pack = ListMissionPacks().FirstOrDefault(p => string.Equals(p.Id, packId, StringComparison.OrdinalIgnoreCase));
            if (pack == null)
                throw new InvalidOperationException("Mission pack not found: " + packId);

            MagicMissionRecord mission = FindMission(ownerKey, missionId);
            if (mission == null)
                mission = CreateMission(ownerKey, pack.Name, pack.Purpose, "high", new[] { "mission-pack", pack.Id });

            var added = new List<MagicMissionEvent>();
            foreach (MagicMissionPackStep step in pack.Steps)
            {
                added.Add(AddMissionEvent(
                    ownerKey,
                    mission.Id,
                    "mission-pack-step",
                    step.Name,
                    step.Capability + " | " + step.Phase,
                    "mission-pack:" + pack.Id,
                    step.RequiresApproval ? "pending" : "queued",
                    step.RequiresApproval));
            }

            lock (_sync)
                TouchMissionLocked(ownerKey, mission.Id, "Phase 3 - UI/client integration", 65);

            return new MagicMissionPackRun
            {
                Mission = FindMission(ownerKey, mission.Id),
                Pack = pack,
                Events = added,
                RunUtc = DateTimeOffset.UtcNow.ToString("O")
            };
        }

        public MagicMissionEvent CreateOperatorProposal(string ownerKey, string missionId, string observation, string proposedAction, string risk, int confidencePercent)
        {
            string detail = "Observation: " + NormalizeText(observation, 3000, "") +
                            "\nProposed action: " + NormalizeText(proposedAction, 3000, "") +
                            "\nRisk: " + NormalizeToken(risk, "medium") +
                            "\nConfidence: " + Math.Max(0, Math.Min(100, confidencePercent)).ToString(CultureInfo.InvariantCulture) + "%";
            return AddMissionEvent(ownerKey, missionId, "operator-proposal", "Live AI Operator proposal", detail, "live-ai-operator", "pending", true);
        }

        public List<MagicAssistSession> ListAssistSessions(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            lock (_sync)
            {
                return ReadList<MagicAssistSession>(AssistPath(ownerKey))
                    .OrderByDescending(s => FirstNonEmpty(s.UpdatedUtc, s.CreatedUtc))
                    .ToList();
            }
        }

        public MagicAssistSession CreateAssistSession(string ownerKey, string missionId, string subject, string consentScope, string redactionPolicy, int expiresMinutes)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string now = DateTimeOffset.UtcNow.ToString("O");
            int safeMinutes = Math.Max(5, Math.Min(1440, expiresMinutes <= 0 ? 60 : expiresMinutes));
            var session = new MagicAssistSession
            {
                Id = "assist_" + ShortHash(ownerKey + "|" + subject + "|" + now),
                OwnerKey = ownerKey,
                MissionId = NormalizeId(missionId),
                Subject = NormalizeText(subject, 180, "Remote assist session"),
                ConsentScope = NormalizeText(consentScope, 1200, "screen, voice, files, and proposed actions require approval"),
                RedactionPolicy = NormalizeText(redactionPolicy, 800, "redact secrets, tokens, payment data, and private chat"),
                Status = "pending",
                CreatedUtc = now,
                UpdatedUtc = now,
                ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(safeMinutes).ToString("O")
            };
            session.AuditHash = ComputeAuditHash(session, "created");

            lock (_sync)
            {
                var sessions = ReadList<MagicAssistSession>(AssistPath(ownerKey));
                sessions.Add(session);
                WriteList(AssistPath(ownerKey), sessions);
            }

            if (!string.IsNullOrWhiteSpace(session.MissionId))
            {
                AddMissionEvent(ownerKey, session.MissionId, "assist-request", "Remote assist consent requested", session.ConsentScope, "zero-trust-assist", "pending", true);
            }

            return session;
        }

        public MagicAssistSession ApproveAssistSession(string ownerKey, string sessionId, string note)
        {
            return UpdateAssistSession(ownerKey, sessionId, "approved", note, "approved");
        }

        public MagicAssistSession RevokeAssistSession(string ownerKey, string sessionId, string reason)
        {
            return UpdateAssistSession(ownerKey, sessionId, "revoked", reason, "revoked");
        }

        public List<MagicEvidencePacket> ListEvidencePackets(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            lock (_sync)
            {
                return ReadList<MagicEvidencePacket>(EvidencePath(ownerKey))
                    .OrderByDescending(p => FirstNonEmpty(p.UpdatedUtc, p.CreatedUtc))
                    .ToList();
            }
        }

        public MagicEvidencePacket FindEvidencePacket(string ownerKey, string id)
        {
            id = NormalizeId(id);
            return ListEvidencePackets(ownerKey).FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public MagicEvidencePacket CreateEvidencePacket(
            string ownerKey,
            string missionId,
            string title,
            string summary,
            string sourceKind,
            string sourceRef,
            string contentType,
            string classification,
            IEnumerable<string> relatedEventIds)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string now = DateTimeOffset.UtcNow.ToString("O");
            var packet = new MagicEvidencePacket
            {
                Id = "evidence_" + ShortHash(ownerKey + "|" + title + "|" + now),
                OwnerKey = ownerKey,
                MissionId = NormalizeId(missionId),
                Title = NormalizeText(title, 180, "Evidence packet"),
                Summary = NormalizeText(summary, 6000, ""),
                SourceKind = NormalizeToken(sourceKind, "manual"),
                SourceRef = NormalizeText(sourceRef, 1000, ""),
                ContentType = NormalizeText(contentType, 120, "application/json"),
                Classification = NormalizeToken(classification, "private"),
                ExportFormat = "sockjackdml-evidence-json-v1",
                RelatedEventIds = NormalizeIds(relatedEventIds),
                CreatedUtc = now,
                UpdatedUtc = now
            };
            packet.Hash = ComputeSha256(string.Join("\n", new[]
            {
                packet.OwnerKey,
                packet.MissionId,
                packet.Title,
                packet.Summary,
                packet.SourceKind,
                packet.SourceRef,
                packet.Classification,
                packet.CreatedUtc
            }));

            lock (_sync)
            {
                var packets = ReadList<MagicEvidencePacket>(EvidencePath(ownerKey));
                packets.Add(packet);
                WriteList(EvidencePath(ownerKey), packets);
            }

            if (!string.IsNullOrWhiteSpace(packet.MissionId))
                AddMissionEvent(ownerKey, packet.MissionId, "evidence-captured", packet.Title, packet.Summary, "evidence-vault", "sealed", false, packet.Id);

            return packet;
        }

        public MagicEvidenceExport ExportEvidencePacket(string ownerKey, string id)
        {
            MagicEvidencePacket packet = FindEvidencePacket(ownerKey, id);
            if (packet == null)
                throw new InvalidOperationException("Evidence packet not found: " + id);

            List<MagicMissionEvent> events = string.IsNullOrWhiteSpace(packet.MissionId)
                ? new List<MagicMissionEvent>()
                : ListMissionEvents(ownerKey, packet.MissionId)
                    .Where(e => packet.RelatedEventIds.Count == 0 || packet.RelatedEventIds.Contains(e.Id, StringComparer.OrdinalIgnoreCase) || string.Equals(e.EvidencePacketId, packet.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            var export = new MagicEvidenceExport
            {
                Format = "sockjackdml-evidence-json-v1",
                ExportedUtc = DateTimeOffset.UtcNow.ToString("O"),
                Packet = packet,
                Events = events,
                Manifest = new Dictionary<string, string>
                {
                    ["packetId"] = packet.Id,
                    ["hash"] = packet.Hash,
                    ["classification"] = packet.Classification,
                    ["sourceKind"] = packet.SourceKind,
                    ["missionId"] = packet.MissionId
                }
            };
            export.Json = JsonSerializer.Serialize(new
            {
                export.Format,
                export.ExportedUtc,
                export.Manifest,
                export.Packet,
                export.Events
            }, _jsonOptions);
            return export;
        }

        public List<MagicAccessibilityEvent> ListAccessibilityEvents(string ownerKey, string missionId)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            missionId = NormalizeId(missionId);
            lock (_sync)
            {
                IEnumerable<MagicAccessibilityEvent> events = ReadList<MagicAccessibilityEvent>(AccessibilityPath(ownerKey));
                if (!string.IsNullOrWhiteSpace(missionId))
                    events = events.Where(e => string.Equals(e.MissionId, missionId, StringComparison.OrdinalIgnoreCase));
                return events.OrderByDescending(e => e.CreatedUtc).ToList();
            }
        }

        public MagicAccessibilityEvent CreateAccessibilityEvent(string ownerKey, string missionId, string inputKind, string outputKind, string originalText, string language, string targetLanguage)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string now = DateTimeOffset.UtcNow.ToString("O");
            outputKind = NormalizeToken(outputKind, "summary");
            string normalizedOriginal = NormalizeText(originalText, 12000, "");
            var item = new MagicAccessibilityEvent
            {
                Id = "access_" + ShortHash(ownerKey + "|" + outputKind + "|" + now),
                OwnerKey = ownerKey,
                MissionId = NormalizeId(missionId),
                InputKind = NormalizeToken(inputKind, "text"),
                OutputKind = outputKind,
                Language = NormalizeText(language, 32, "auto"),
                TargetLanguage = NormalizeText(targetLanguage, 32, ""),
                OriginalText = normalizedOriginal,
                ProcessedText = BuildAccessibilityOutput(outputKind, normalizedOriginal, targetLanguage),
                Status = "processed",
                CreatedUtc = now
            };

            lock (_sync)
            {
                var events = ReadList<MagicAccessibilityEvent>(AccessibilityPath(ownerKey));
                events.Add(item);
                WriteList(AccessibilityPath(ownerKey), events);
            }

            if (!string.IsNullOrWhiteSpace(item.MissionId))
                AddMissionEvent(ownerKey, item.MissionId, "accessibility-output", item.OutputKind + " generated", item.ProcessedText, "accessibility-layer", "complete", false);

            return item;
        }

        private MagicAssistSession UpdateAssistSession(string ownerKey, string sessionId, string status, string note, string auditAction)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            sessionId = NormalizeId(sessionId);
            string now = DateTimeOffset.UtcNow.ToString("O");

            lock (_sync)
            {
                var sessions = ReadList<MagicAssistSession>(AssistPath(ownerKey));
                MagicAssistSession session = sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase));
                if (session == null)
                    throw new InvalidOperationException("Assist session not found: " + sessionId);

                session.Status = NormalizeToken(status, "pending");
                session.Note = NormalizeText(note, 2000, "");
                session.UpdatedUtc = now;
                session.AuditHash = ComputeAuditHash(session, auditAction);
                WriteList(AssistPath(ownerKey), sessions);

                if (!string.IsNullOrWhiteSpace(session.MissionId))
                {
                    var events = ReadList<MagicMissionEvent>(EventsPath(ownerKey, session.MissionId));
                    events.Add(new MagicMissionEvent
                    {
                        Id = "evt_" + ShortHash(session.Id + "|" + status + "|" + now),
                        MissionId = session.MissionId,
                        OwnerKey = ownerKey,
                        Type = "assist-" + status,
                        Title = "Remote assist " + status,
                        Detail = session.Note,
                        Actor = "zero-trust-assist",
                        Status = status,
                        CreatedUtc = now,
                        RequiresApproval = false,
                        Approved = true
                    });
                    WriteList(EventsPath(ownerKey, session.MissionId), events);
                    TouchMissionLocked(ownerKey, session.MissionId, status == "revoked" ? "Phase 4 - Revoke/audit complete" : "Phase 3 - Approved assist active", status == "revoked" ? 85 : 65);
                }

                return session;
            }
        }

        private static MagicMissionPack BuildIncidentPack()
        {
            return new MagicMissionPack
            {
                Id = "incident-commander",
                Name = "Incident Commander",
                Purpose = "Coordinate a production incident from first signal through evidence, operator proposals, remote assist, and retrospective export.",
                UserValue = "Turns a chaotic outage into one timeline with approvals, evidence, and reversible actions.",
                ApprovalPolicy = "Operator proposals and remote input require explicit approval.",
                Dependencies = new List<string> { "Observability", "Evidence Vault", "Live AI Operator", "Zero-Trust Remote Assist" },
                SuccessCriteria = new List<string> { "Impact known", "Owner assigned", "Evidence sealed", "Action proposals approved or rejected", "Retrospective export generated" },
                Steps = new List<MagicMissionPackStep>
                {
                    new MagicMissionPackStep { Id = "triage", Name = "Capture impact and scope", Phase = "Phase 1", Capability = "SockJackDml Mission Control", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "evidence", Name = "Seal telemetry and chat evidence", Phase = "Phase 2", Capability = "Evidence Vault", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "proposal", Name = "Draft operator fix proposal", Phase = "Phase 3", Capability = "Live AI Operator", RequiresApproval = true },
                    new MagicMissionPackStep { Id = "assist", Name = "Open time-boxed remote assist", Phase = "Phase 4", Capability = "Zero-Trust Remote Assist", RequiresApproval = true },
                    new MagicMissionPackStep { Id = "export", Name = "Export incident record", Phase = "Phase 5", Capability = "Evidence Vault", RequiresApproval = false }
                }
            };
        }

        private static MagicMissionPack BuildLaunchPack()
        {
            return new MagicMissionPack
            {
                Id = "launch-room",
                Name = "Launch Room",
                Purpose = "Run a release or model-server launch with gates, rollback evidence, capability checks, and accessibility outputs.",
                UserValue = "Makes launches auditable, reversible, and easier for distributed teams to follow.",
                ApprovalPolicy = "Rollback and remote actions require approval.",
                Dependencies = new List<string> { "Capability Router", "Evidence Vault", "Accessibility Layer" },
                SuccessCriteria = new List<string> { "Capabilities healthy", "Launch gates accepted", "Rollback packet ready", "Status captions available" },
                Steps = new List<MagicMissionPackStep>
                {
                    new MagicMissionPackStep { Id = "capability", Name = "Score active runtime and peer fallback", Phase = "Phase 1", Capability = "Capability Router", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "gates", Name = "Record launch gates", Phase = "Phase 2", Capability = "SockJackDml Mission Control", RequiresApproval = true },
                    new MagicMissionPackStep { Id = "rollback", Name = "Prepare rollback packet", Phase = "Phase 3", Capability = "Evidence Vault", RequiresApproval = true },
                    new MagicMissionPackStep { Id = "captions", Name = "Generate team status captions", Phase = "Phase 4", Capability = "Realtime Accessibility Layer", RequiresApproval = false }
                }
            };
        }

        private static MagicMissionPack BuildRemoteAssistPack()
        {
            return new MagicMissionPack
            {
                Id = "remote-rescue",
                Name = "Remote Rescue",
                Purpose = "Open a consent-scoped assist session with redaction, revoke, audit hash, and evidence capture.",
                UserValue = "Lets a trusted helper work with the user without surrendering control.",
                ApprovalPolicy = "Every session starts pending; input proposals are not execution permissions.",
                Dependencies = new List<string> { "Zero-Trust Remote Assist", "Live AI Operator", "Evidence Vault" },
                SuccessCriteria = new List<string> { "Consent scope written", "Redaction policy active", "Revoke path verified", "Audit hash sealed" },
                Steps = new List<MagicMissionPackStep>
                {
                    new MagicMissionPackStep { Id = "scope", Name = "Write consent and redaction scope", Phase = "Phase 1", Capability = "Zero-Trust Remote Assist", RequiresApproval = true },
                    new MagicMissionPackStep { Id = "observe", Name = "Capture observation context", Phase = "Phase 2", Capability = "Live AI Operator", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "revoke", Name = "Verify revoke/audit controls", Phase = "Phase 4", Capability = "Zero-Trust Remote Assist", RequiresApproval = false }
                }
            };
        }

        private static MagicMissionPack BuildEvidencePack()
        {
            return new MagicMissionPack
            {
                Id = "audit-lockbox",
                Name = "Audit Lockbox",
                Purpose = "Seal decisions, files, operator proposals, and accessibility outputs into exportable evidence packets.",
                UserValue = "Creates a durable record for compliance, support, retrospectives, and dispute resolution.",
                ApprovalPolicy = "Private evidence is exportable only from the owner's current session context.",
                Dependencies = new List<string> { "Evidence Vault", "Trust Abuse", "Observability" },
                SuccessCriteria = new List<string> { "Packet hash generated", "Related events linked", "Export manifest available" },
                Steps = new List<MagicMissionPackStep>
                {
                    new MagicMissionPackStep { Id = "collect", Name = "Collect evidence candidates", Phase = "Phase 2", Capability = "Evidence Vault", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "seal", Name = "Seal packet hash", Phase = "Phase 4", Capability = "Evidence Vault", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "export", Name = "Generate export manifest", Phase = "Phase 5", Capability = "Evidence Vault", RequiresApproval = false }
                }
            };
        }

        private static MagicMissionPack BuildAccessibilityPack()
        {
            return new MagicMissionPack
            {
                Id = "accessibility-copilot",
                Name = "Accessibility Copilot",
                Purpose = "Convert screen, voice, OCR, captions, summaries, and translations into mission timeline artifacts.",
                UserValue = "Keeps teams in sync across ability, device, language, and attention limits.",
                ApprovalPolicy = "Generated text is marked as processed context and can be reviewed before evidence export.",
                Dependencies = new List<string> { "Realtime Accessibility Layer", "Evidence Vault", "SockJackDml Mission Control" },
                SuccessCriteria = new List<string> { "Caption/summary generated", "Mission event linked", "Evidence export possible" },
                Steps = new List<MagicMissionPackStep>
                {
                    new MagicMissionPackStep { Id = "input", Name = "Capture accessibility input", Phase = "Phase 1", Capability = "Realtime Accessibility Layer", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "process", Name = "Generate caption/summary/translation", Phase = "Phase 3", Capability = "Realtime Accessibility Layer", RequiresApproval = false },
                    new MagicMissionPackStep { Id = "link", Name = "Link output to mission timeline", Phase = "Phase 4", Capability = "SockJackDml Mission Control", RequiresApproval = false }
                }
            };
        }

        private void TouchMissionLocked(string ownerKey, string missionId, string phase, int progressPercent)
        {
            var missions = ReadList<MagicMissionRecord>(MissionsPath(ownerKey));
            MagicMissionRecord mission = missions.FirstOrDefault(m => string.Equals(m.Id, missionId, StringComparison.OrdinalIgnoreCase));
            if (mission == null)
                return;

            mission.CurrentPhase = NormalizeText(phase, 120, mission.CurrentPhase);
            mission.ProgressPercent = Math.Max(mission.ProgressPercent, Math.Min(100, Math.Max(0, progressPercent)));
            mission.UpdatedUtc = DateTimeOffset.UtcNow.ToString("O");
            WriteList(MissionsPath(ownerKey), missions);
        }

        private List<T> ReadList<T>(string path)
        {
            if (!File.Exists(path))
                return new List<T>();

            try
            {
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                    return new List<T>();
                return JsonSerializer.Deserialize<List<T>>(json, _jsonOptions) ?? new List<T>();
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

        private string MissionsPath(string ownerKey)
        {
            return Path.Combine(OwnerDirectory(ownerKey), "missions.json");
        }

        private string EventsPath(string ownerKey, string missionId)
        {
            return Path.Combine(OwnerDirectory(ownerKey), "events_" + NormalizeId(missionId) + ".json");
        }

        private string AssistPath(string ownerKey)
        {
            return Path.Combine(OwnerDirectory(ownerKey), "assist-sessions.json");
        }

        private string EvidencePath(string ownerKey)
        {
            return Path.Combine(OwnerDirectory(ownerKey), "evidence-packets.json");
        }

        private string AccessibilityPath(string ownerKey)
        {
            return Path.Combine(OwnerDirectory(ownerKey), "accessibility-events.json");
        }

        private string OwnerDirectory(string ownerKey)
        {
            ownerKey = NormalizeOwnerKey(ownerKey);
            string key = SanitizePathSegment(ownerKey);
            string path = Path.Combine(_root, "owners", key + "_" + ShortHash(ownerKey));
            Directory.CreateDirectory(path);
            return path;
        }

        private static string BuildAccessibilityOutput(string outputKind, string text, string targetLanguage)
        {
            text = NormalizeText(text, 12000, "");
            if (string.IsNullOrWhiteSpace(text))
                return "";

            if (string.Equals(outputKind, "caption", StringComparison.OrdinalIgnoreCase))
                return text.Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.Equals(outputKind, "translation", StringComparison.OrdinalIgnoreCase))
                return "Translation target [" + NormalizeText(targetLanguage, 32, "requested") + "]: " + text;
            if (string.Equals(outputKind, "ocr", StringComparison.OrdinalIgnoreCase))
                return text;

            string[] sentences = text.Split(new[] { '.', '!', '?', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string summary = string.Join(". ", sentences.Select(s => s.Trim()).Where(s => s.Length > 0).Take(3));
            if (string.IsNullOrWhiteSpace(summary))
                summary = text.Length > 360 ? text.Substring(0, 360).Trim() + "..." : text.Trim();
            return summary;
        }

        private static List<string> NormalizeTags(IEnumerable<string> tags)
        {
            return (tags ?? Array.Empty<string>())
                .Select(t => NormalizeToken(t, ""))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }

        private static List<string> NormalizeIds(IEnumerable<string> ids)
        {
            return (ids ?? Array.Empty<string>())
                .Select(NormalizeId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(64)
                .ToList();
        }

        private static string NormalizeOwnerKey(string ownerKey)
        {
            ownerKey = NormalizeText(ownerKey, 180, "");
            return string.IsNullOrWhiteSpace(ownerKey) ? "socketjack-local" : ownerKey;
        }

        private static string NormalizeId(string value)
        {
            value = NormalizeText(value, 120, "");
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var sb = new StringBuilder();
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '.')
                    sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string NormalizeToken(string value, string fallback)
        {
            value = NormalizeText(value, 80, "");
            if (string.IsNullOrWhiteSpace(value))
                value = fallback ?? "";
            value = value.Trim().ToLowerInvariant().Replace(' ', '-').Replace('_', '-');

            var sb = new StringBuilder();
            foreach (char ch in value)
            {
                if (char.IsLetterOrDigit(ch) || ch == '-')
                    sb.Append(ch);
            }
            return sb.Length == 0 ? (fallback ?? "") : sb.ToString();
        }

        private static string NormalizeText(string value, int maxLength, string fallback)
        {
            value = value ?? "";
            var sb = new StringBuilder();
            foreach (char ch in value)
            {
                if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')
                    continue;
                sb.Append(ch);
                if (sb.Length >= maxLength)
                    break;
            }
            string result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? (fallback ?? "") : result;
        }

        private static string SanitizePathSegment(string value)
        {
            value = NormalizeText(value, 80, "owner");
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char ch in value)
            {
                if (invalid.Contains(ch) || char.IsControl(ch) || char.IsWhiteSpace(ch))
                    sb.Append('_');
                else if (char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch == '.')
                    sb.Append(ch);
                else
                    sb.Append('_');
            }
            string result = sb.ToString().Trim('_', '.');
            return string.IsNullOrWhiteSpace(result) ? "owner" : result;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return "";
        }

        private static string ShortHash(string value)
        {
            string hash = ComputeSha256(value ?? "");
            return hash.Length > 16 ? hash.Substring(0, 16) : hash;
        }

        private static string ComputeSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ComputeAuditHash(MagicAssistSession session, string action)
        {
            if (session == null)
                return "";
            return ComputeSha256(string.Join("|", new[]
            {
                session.Id,
                session.OwnerKey,
                session.MissionId,
                session.Subject,
                session.ConsentScope,
                session.RedactionPolicy,
                session.Status,
                session.CreatedUtc,
                session.UpdatedUtc,
                session.ExpiresUtc,
                action ?? ""
            }));
        }
    }

    internal sealed class SockJackDmlSummary
    {
        public string OwnerKey { get; set; } = "";
        public int MissionCount { get; set; }
        public int ActiveMissionCount { get; set; }
        public int PendingApprovalCount { get; set; }
        public int AssistSessionCount { get; set; }
        public int ActiveAssistSessionCount { get; set; }
        public int EvidencePacketCount { get; set; }
        public int AccessibilityEventCount { get; set; }
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicMissionRecord
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Title { get; set; } = "";
        public string Objective { get; set; } = "";
        public string Status { get; set; } = "";
        public string Priority { get; set; } = "";
        public string CurrentPhase { get; set; } = "";
        public int ProgressPercent { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicMissionEvent
    {
        public string Id { get; set; } = "";
        public string MissionId { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string Type { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Status { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string EvidencePacketId { get; set; } = "";
        public bool RequiresApproval { get; set; }
        public bool Approved { get; set; }
        public string Decision { get; set; } = "";
        public string DecisionNote { get; set; } = "";
        public string DecidedUtc { get; set; } = "";
    }

    internal sealed class MagicMissionPack
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Purpose { get; set; } = "";
        public string UserValue { get; set; } = "";
        public string ApprovalPolicy { get; set; } = "";
        public List<string> Dependencies { get; set; } = new List<string>();
        public List<string> SuccessCriteria { get; set; } = new List<string>();
        public List<MagicMissionPackStep> Steps { get; set; } = new List<MagicMissionPackStep>();
    }

    internal sealed class MagicMissionPackStep
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Capability { get; set; } = "";
        public bool RequiresApproval { get; set; }
    }

    internal sealed class MagicMissionPackRun
    {
        public MagicMissionRecord Mission { get; set; }
        public MagicMissionPack Pack { get; set; }
        public List<MagicMissionEvent> Events { get; set; } = new List<MagicMissionEvent>();
        public string RunUtc { get; set; } = "";
    }

    internal sealed class MagicAssistSession
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string MissionId { get; set; } = "";
        public string Subject { get; set; } = "";
        public string ConsentScope { get; set; } = "";
        public string RedactionPolicy { get; set; } = "";
        public string Status { get; set; } = "";
        public string Note { get; set; } = "";
        public string AuditHash { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string ExpiresUtc { get; set; } = "";
    }

    internal sealed class MagicEvidencePacket
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string MissionId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string SourceKind { get; set; } = "";
        public string SourceRef { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string Classification { get; set; } = "";
        public string ExportFormat { get; set; } = "";
        public string Hash { get; set; } = "";
        public List<string> RelatedEventIds { get; set; } = new List<string>();
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    internal sealed class MagicEvidenceExport
    {
        public string Format { get; set; } = "";
        public string ExportedUtc { get; set; } = "";
        public MagicEvidencePacket Packet { get; set; }
        public List<MagicMissionEvent> Events { get; set; } = new List<MagicMissionEvent>();
        public Dictionary<string, string> Manifest { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Json { get; set; } = "";
    }

    internal sealed class MagicAccessibilityEvent
    {
        public string Id { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string MissionId { get; set; } = "";
        public string InputKind { get; set; } = "";
        public string OutputKind { get; set; } = "";
        public string Language { get; set; } = "";
        public string TargetLanguage { get; set; } = "";
        public string OriginalText { get; set; } = "";
        public string ProcessedText { get; set; } = "";
        public string Status { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
    }
}


