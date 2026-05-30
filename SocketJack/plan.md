# SockJackDml SocketJack.com Feature Plan

Last updated: 2026-05-11

## Vision

SockJackDml is the SocketJack.com mission-control layer over chat, model routing, files, screen/voice assist, evidence, approvals, and accessibility outputs. It turns a live SocketJack session into an auditable operation: every mission has a timeline, every risky action is proposed before execution, every remote-assist session has consent and revoke state, and every important artifact can be sealed into an evidence packet.

## SocketJack.com Integration

| Surface | Implementation |
| --- | --- |
| Website route | `/sockjackdml`, `/sockjackdml/missions`, `/sockjackdml/packs`, `/sockjackdml/operator`, `/sockjackdml/assist`, `/sockjackdml/capabilities`, `/sockjackdml/evidence`, and `/sockjackdml/accessibility` all route to `html/SockJackDml.html`. |
| Main chat entry | `html/JackLLMWebChat.html` exposes an `Magic` header action that opens `/sockjackdml`. |
| Backend host | `SocketJack.LlmCore/Proxy/JackLLM.cs` registers SockJackDml pages and JSON APIs on the existing SocketJack chat HTTP server. |
| Durable storage | `SocketJack.LlmCore/Proxy/SockJackDml/SockJackDmlService.cs` stores mission records, timeline events, consent sessions, evidence packets, and accessibility outputs under the chat session root. |
| Service catalog | `/api/chat-services` advertises `sockjack_dml` as a selectable mission-control service when Agent access is enabled. |
| Existing SocketJack context | Capability routing reads SocketJack permissions, model runtime health, server profile, peer selection, and Copilot duplicator status. |

## Feature Pillars

| Feature | Purpose | User Value | Core Capabilities | Dependencies | Success Criteria |
| --- | --- | --- | --- | --- | --- |
| SockJackDml Mission Control | Keep high-stakes work in mission timelines. | Replaces scattered chat context with a durable command record. | Mission create/list, progress phase, timeline events, decisions, approval state. | Chat session identity and storage. | A user can create a mission, add events, select a mission, and approve/reject pending events. |
| SockJackDml Mission Packs | Convert repeated operations into reusable playbooks. | Starts incident, launch, remote assist, evidence, and accessibility workflows quickly. | Built-in packs, phase steps, approval flags, timeline injection. | Mission Control. | Running a pack creates or updates a mission and queues timeline steps. |
| Live AI Operator | Propose actions without silently executing risky work. | Keeps the AI useful in urgent work while preserving human control. | Observation capture, proposed action contract, risk, confidence, pending decision event. | Agent permission and Mission Control. | Operator proposals appear as pending timeline decisions. |
| Zero-Trust Remote Assist | Make remote help consent-scoped and revocable. | Lets helpers assist without surrendering control or leaking sensitive data. | Consent scope, redaction policy, expiry, approve, revoke, audit hash, mission linkage. | Existing remote client/screen/input APIs plus Mission Control. | Assist sessions can be opened, approved, revoked, and audited. |
| Capability Router | Score which SocketJack capability should handle work. | Avoids brittle routing across local model, peer compute, tools, and privacy constraints. | Runtime health, peer selection, permission state, route map, scoring factors. | Model runtime, server browser profile, Copilot duplicator, permissions. | `/api/sockjackdml/capabilities` returns current readiness and scores for all SockJackDml pillars. |
| Evidence Vault | Seal mission artifacts for audit and export. | Creates a mission record suitable for support, disputes, retrospectives, and compliance. | Evidence packet create/list/export, hash, classification, related events, manifest. | Mission Control and chat storage. | Evidence packets export as `sockjackdml-evidence-json-v1` with manifest and linked timeline events. |
| Realtime Accessibility Layer | Convert live context into captions, summaries, OCR, and translation artifacts. | Keeps operations usable across language, ability, device, and attention limits. | Input/output kind, language metadata, generated text, mission event linkage. | Mission Control and evidence storage. | Accessibility outputs are saved and can be linked into mission timelines/evidence. |

## Implementation Phases

| Phase | Percent | Deliverable |
| --- | ---: | --- |
| Phase 0 | 10% | Roadmap and SocketJack.com implementation map. |
| Phase 1 | 25% | Data contracts and service skeleton. |
| Phase 2 | 45% | Backend APIs and durable storage. |
| Phase 3 | 65% | SocketJack.com UI/client integration. |
| Phase 4 | 85% | Approval, privacy, revoke, export, and failure handling. |
| Phase 5 | 100% | Docs, polish, build verification, and release-ready progress tracking. |

## API Map

| Method | Route | Feature |
| --- | --- | --- |
| GET/POST | `/api/sockjackdml/missions` | Mission list/create |
| GET/POST | `/api/sockjackdml/missions/events` | Timeline list/create |
| POST | `/api/sockjackdml/missions/events/decision` | Approve/reject events |
| GET | `/api/sockjackdml/packs` | Mission pack catalog |
| POST | `/api/sockjackdml/packs/run` | Mission pack execution |
| POST | `/api/sockjackdml/operator/propose` | Operator proposal |
| GET | `/api/sockjackdml/capabilities` | Capability router |
| GET/POST | `/api/sockjackdml/assist/sessions` | Assist list/create |
| POST | `/api/sockjackdml/assist/approve` | Assist approval |
| POST | `/api/sockjackdml/assist/revoke` | Assist revoke |
| GET/POST | `/api/sockjackdml/evidence/packets` | Evidence list/create |
| GET | `/api/sockjackdml/evidence/export` | Evidence export |
| GET/POST | `/api/sockjackdml/accessibility/events` | Accessibility list/create |

## Release Checklist

- [x] Add SocketJack.com route surface.
- [x] Add backend service and JSON APIs.
- [x] Add mission-control UI.
- [x] Add chat header entry point.
- [x] Add service catalog entry.
- [x] Add durable evidence/assist/accessibility storage.
- [x] Update progress tracking.
- [x] Build verification.



