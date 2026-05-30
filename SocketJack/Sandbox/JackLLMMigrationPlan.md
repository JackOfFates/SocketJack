# JackLLM Sandbox Migration Plan

Last updated: 2026-05-11

## Goal

Move JackLLM, SocketJack.com master list, and JackLLM Companion file storage/serving onto a shared SocketJack sandbox model. The migration should keep current behavior working while giving each browser login, server listing, shell relay, chat, and Companion work recording a real sandbox session with explicit filesystem, registry, memory, and retention policy.

## Shared Concepts

| Concept | Meaning |
|---|---|
| `SandboxTenant` | Account, host, app, or local user that owns one or more sessions. |
| `SandboxSession` | Isolated unit of files, virtual registry values, limits, audit events, and TTL. |
| `SandboxFileId` | Public-safe identifier for a file; APIs should not expose real local paths by default. |
| `SandboxManifest` | Snapshot metadata: files, hashes, sizes, registry diff, created/updated times, retention. |
| `SandboxPolicy` | Resolved `SandboxOptions` for a session based on account, host, app, and approval gates. |

## JackLLM

Current role:

- Hosts local model proxy endpoints, browser chat, permissions, service health, payments, server browser metadata, and remote session files.
- Tracks remote session files and managed local roots through the existing JackLLM file/session features.

Migration plan:

1. Introduce `JackLLMSandboxService` in the GUI project that creates one `SandboxSession` per chat/client/session.
2. Route uploads, generated artifacts, downloads, remote session file clone/watch/cleanup, and temporary tool files through `ISandboxFileStore`.
3. Map current permissions to sandbox policy:
   - Uploads/downloads disabled means filesystem `DenyAll` except existing session reads.
   - User-approved file access means declared read-only or copy-on-write mounts.
   - Remote session file clone uses `MemoryOverlay` or `CopyOnWrite` with a per-session TTL.
4. Keep old file URLs and local roots as compatibility aliases while new responses include `sandboxSessionId` and `fileId`.
5. Add a GUI panel for session storage, memory usage, dirty files, snapshots, and cleanup.

Recommended first profile:

`CopyOnWrite` filesystem, `MemoryOverlay` registry, `WriteLayerOnly` memory, `SnapshotOnClose` persistence for saved chats, and `Discard` for anonymous scratch sessions.

## SocketJack.com Master List

Current role:

- Hosts public website and server browser.
- Uses web auth endpoints and cookies/access tokens.
- Tracks published JackLLM server rows, owner data, health, shell relay metadata, and active relay session counts.

The master list should add explicit sessions in two layers: web/account sessions and JackLLM usage sessions.

### Web Account Sessions

Add a durable table so `/api/web-auth/session` is backed by revocable session rows instead of only self-contained token state.

| Table | Key Fields |
|---|---|
| `JackLLMWebSessions` | `Id`, `AccountUserName`, `AccessTokenHash`, `RememberTokenHash`, `UserAgentHash`, `IpHash`, `CreatedUtc`, `LastSeenUtc`, `ExpiresUtc`, `RevokedUtc`, `ConsentVersion`, `SandboxSessionId`, `CapabilitiesJson` |
| `JackLLMWebSessionEvents` | `Id`, `SessionId`, `EventType`, `Detail`, `IpHash`, `CreatedUtc` |

Endpoint changes:

| Endpoint | Change |
|---|---|
| `POST /api/web-auth/login` | Create `JackLLMWebSessions` row, create sandbox session, issue cookie/token pointing to row. |
| `GET /api/web-auth/session` | Renew `LastSeenUtc`, return session id/capabilities, rotate near-expiry tokens. |
| `POST /api/web-auth/logout` | Revoke current session row and expire cookies. |
| `POST /api/web-auth/register` | Same as login after account creation. |

Sandbox use:

- Keep master website cookies and account state outside public file storage.
- Use the session sandbox for small user-owned website state such as preferences, pending launches, temporary payment return data, and safe downloaded manifests.
- Registry mode is `DenyAll` for the public website.

### JackLLM Usage Sessions

When a signed-in user launches or reserves a listed host, create a usage session that can later connect payment, shell relay, file serving, and audit records.

| Table | Key Fields |
|---|---|
| `JackLLMUsageSessions` | `Id`, `WebSessionId`, `AccountUserName`, `ServerId`, `OwnerUserName`, `RelayId`, `Status`, `StartedUtc`, `LastSeenUtc`, `ClosedUtc`, `TokenBudget`, `TokensUsed`, `PaymentState`, `SandboxSessionId`, `LaunchUrl`, `MetadataJson` |
| `JackLLMUsageSessionEvents` | `Id`, `UsageSessionId`, `EventType`, `Detail`, `CreatedUtc` |
| `JackLLMUsageSessionFiles` | `Id`, `UsageSessionId`, `SandboxFileId`, `Name`, `Kind`, `SizeBytes`, `Hash`, `CreatedUtc`, `ExpiresUtc` |

New endpoint examples:

| Endpoint | Purpose |
|---|---|
| `POST /api/jackllm/sessions` | Create a usage session for a selected server and return launch/proxy info. |
| `GET /api/jackllm/sessions/current` | List active sessions for the current signed-in user. |
| `POST /api/jackllm/sessions/{id}/heartbeat` | Renew the usage session and update relay activity. |
| `POST /api/jackllm/sessions/{id}/close` | Close the session, snapshot or discard sandbox files, and release relay capacity. |
| `GET /api/jackllm/sessions/{id}/files` | Return public-safe session file metadata. |

Initial policy:

- Anonymous users cannot create usage sessions.
- Master-list usage sessions get `LockedDown` filesystem by default with a small `MemoryOverlay` for temporary launch/payment artifacts.
- Host-served files stay on the host until explicitly uploaded or proxied into a session file store.
- Shell relay active session counts should be derived from `JackLLMUsageSessions` where possible, then reconciled with live relay runtime.

## JackLLM Companion

Current role:

- Stores work sessions, session events, files, shared files, approvals, skills, and training evidence under `%LOCALAPPDATA%\SocketJack\Companion`.
- Serves `/Workspace`, `/file`, `/api/files`, `/api/share`, uploads, folder shares, and downloads.

Migration plan:

1. Create one `SandboxSession` per Companion work recording. Keep a scratch sandbox for events outside a recording.
2. Replace direct `SharedFiles` path construction with `ISandboxFileStore.SaveUpload`, `ImportHostPath`, `Download`, and `GetManifest`.
3. Store `SandboxSessionId` and `SandboxFileId` in `CompanionFiles` and `CompanionSharedFiles` while preserving existing `Path` columns for compatibility during dual-read.
4. Use real local paths only as private source metadata; web APIs should return file IDs, names, sizes, kinds, approval status, and relative paths.
5. Bind `UseFiles` permission to sandbox policy:
   - Disabled: `DenyAll` write/import/download.
   - Enabled: `MemoryOverlay` writes and copy-on-write imports.
   - Per-file approval: promote a denied import to an allowed mount/import after user confirmation.
6. Training snapshots should include the sandbox manifest and hashes, not broad real path lists.

Recommended first profile:

`MemoryOverlay` filesystem with disk spill allowed under `%LOCALAPPDATA%\SocketJack\Companion\SandboxSpill`, `DenyAll` registry, `SnapshotOnClose` for recordings, and `DeleteAfterTtl` for scratch sessions.

## Data Migration

| Source | Migration |
|---|---|
| Companion `CompanionSharedFiles` | Create sandbox files for copied share files, preserve old path as `LegacyPath`, add hash/size. |
| Companion `CompanionFiles` | Add `SandboxSessionId` and `SandboxFileId` when the file is inside the shared root; keep external paths as source references requiring approval. |
| Master web auth | Create web session rows on next login; existing access tokens can be accepted until their normal expiry. |
| Master shell relays | Start recording usage session IDs on new launches; reconcile old active counts as runtime-only until closed. |
| JackLLM remote files | Register current managed roots as sandbox bind mounts, then move new clones into sandbox storage. |

## Rollout Phases

| Phase | Work |
|---|---|
| 1 | Add sandbox contracts and in-memory implementations to SocketJack. |
| 2 | Add adapters in JackLLM and Companion while preserving existing tables/URLs. |
| 3 | Add master-list web session table and usage session endpoints. |
| 4 | Dual-write file/session metadata and add admin views for sandbox usage. |
| 5 | Switch reads to sandbox manifests; retain legacy fallback for one release. |
| 6 | Remove legacy direct-path serving from public APIs after migration verification. |

## Acceptance Criteria

- SocketJack.com login creates and renews a revocable `JackLLMWebSessions` row.
- Launching a listed host creates a `JackLLMUsageSessions` row with heartbeat and close behavior.
- Companion uploads and downloads can complete through sandbox file IDs without exposing local paths.
- JackLLM remote session files can be listed, cloned, cleaned up, and snapshotted through `ISandboxFileStore`.
- Turning off file permission blocks import/download across GUI and Companion through the same sandbox policy.
- Existing users can still open old Companion shared files and existing JackLLM file links during the compatibility period.
