# SocketJack Sandbox Planning

Last updated: 2026-05-11

This folder is the planning home for the SocketJack sandbox feature set. The sandbox is intended to become a reusable SocketJack library component for filesystem and registry virtualization, with memory-first storage that can be partial or complete depending on the caller's options and limits.

## Files

Implementation files:

| File | Purpose |
|---|---|
| `SandboxOptions.cs` | Option model for filesystem, registry, memory, persistence, limits, mounts, and bounded logging. |
| `SandboxContracts.cs` | Public session, filesystem, registry, audit, snapshot, manifest, and exception contracts. |
| `InMemorySandboxAuditLog.cs` | Ring-buffer audit log with category filters for filesystem, registry, and lightweight memory telemetry. |
| `InMemorySandboxFileSystem.cs` | Memory-backed virtual filesystem with path normalization, quotas, file IDs, import policy, manifests, and operation logging. |
| `InMemorySandboxRegistry.cs` | Memory-backed virtual registry with quotas, snapshots, mutation logging, and lightweight memory telemetry. |
| `SandboxSession.cs` | Session lifecycle and factory for the managed sandbox implementation. |

Planning files:

| File | Purpose |
|---|---|
| `SandboxFeatureSetPlan.md` | Architecture and phased implementation plan for the reusable sandbox library surface. |
| `SandboxOptionsMatrix.md` | Full option range for filesystem, registry, memory loading, persistence, quota, audit, and compatibility behavior. |
| `JackLLMMigrationPlan.md` | Migration plan for JackLLM, SocketJack.com master list sessions, and Companion file storage/serving. |

## Direction

Build the sandbox as a library-level contract first, then wire applications into it through adapters:

1. `SocketJack.Sandbox` owns virtual filesystem, virtual registry, storage, limits, session identity, and audit events.
2. SocketJack-owned services use `ISandboxFileSystem`, `ISandboxRegistry`, and `ISandboxFileStore` instead of direct `System.IO` or registry calls.
3. WPF and web projects opt into the same session and storage model so JackLLM, SocketJack.com, and Companion stop inventing separate file/session stores.
4. Full process-level sandboxing is a later layer over the same model, using a broker/native boundary when managed adapters are not enough.

## Current Status

Phase 1 foundation is in place. The managed sandbox can create a memory-backed session, write/read/delete virtual files, import allowed host files, write/read/delete virtual registry values, produce manifests/snapshots, and emit bounded filesystem, registry, and memory audit events. The implementation and planning files are linked into the SocketJack WPF project while remaining physically owned by the core `SocketJack` project.

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>1,613 lines / 10 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack/Sandbox</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 6 | 1,274 |
| Markdown | 4 | 339 |
| **Total** | **10** | **1,613** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
