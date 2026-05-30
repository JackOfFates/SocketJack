# SocketJack-MagicMasterList

.NET 8 WPF host/admin app for the JackLLM server browser. It serves the public website on port `80`, runs the master API/SQL/admin services on port `8494`, and registers a hosted SocketJack database through `TdsProtocolHandler`.

## Current Role

| Component | Stack | Purpose |
|---|---|---|
| Website host | .NET 8 WPF + SocketJack HTTP | Public landing and server browser surface. |
| Master API | SocketJack `MutableTcpServer` | Health, registration, listing, and admin delete endpoints. |
| Data layer | SocketJack `DataServer` + TDS | JSON-backed server registry with SQL/admin access. |
| JackLLM integration | SocketJack `2026` platform line | Receives host profiles with model, hardware, pricing, and tool metadata. |

## What's New

- Server registrations now describe more than reachability: model inventory, tool availability, RAM/VRAM, GPU/CPU, token limits, cost factor, Stripe fields, and arbitrary model-provided metadata can be indexed.
- The WPF admin search indexes the normalized submitted JSON plus key profile fields, which makes server discovery useful for operators and clients.
- The hosted SocketJack database can be inspected through the SQL admin panel while the public API stays small and predictable.

Defaults:

```powershell
dotnet run --project .\SocketJack-MagicMasterList\SocketJack-MagicMasterList.csproj
```

- Website URL: `https://socketjack.com/`
- API URL: `https://socketjack.com/api`
- Database directory: `data/socketjack-master-list-db`
- Database/table: `SocketJack.JackLLMServers`
- SQL admin panel: `https://socketjack.com/sql`

API:

- `GET /healthz`
- `GET /api/jackllm/servers`
- `POST /api/jackllm/servers`
- `DELETE /api/jackllm/servers/{id}` with `X-JackLLM-Master-Key` when `SOCKETJACK_MAGIC_MASTER_LIST_ADMIN_KEY` is set

Server registrations can include title, description, IP/host, available models, tools allowed, available RAM/VRAM, GPU/CPU model, max tokens, cost, cost factor, Stripe fields, uptime/status, and any additional model-provided string/number values. The WPF search box indexes those fields plus the full submitted JSON.

Environment overrides:

- `SOCKETJACK_MAGIC_MASTER_LIST_PORT`
- `SOCKETJACK_MAGIC_MASTER_LIST_BIND_HOST`
- `SOCKETJACK_MAGIC_MASTER_LIST_BIND_URLS`
- `SOCKETJACK_MAGIC_MASTER_LIST_PUBLIC_URL`
- `SOCKETJACK_MAGIC_MASTER_LIST_WEBSITE_PORT`
- `SOCKETJACK_MAGIC_MASTER_LIST_WEBSITE_BIND_URLS`
- `SOCKETJACK_MAGIC_MASTER_LIST_WEBSITE_PUBLIC_URL`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_ENABLED`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_HOST`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_BIND_ADDRESS`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_TARGET_URL`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_SSL_ENABLED`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_SSL_CERTIFICATE`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_SSL_KEY`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_SSL_PASSWORD`
- `SOCKETJACK_MAGIC_MASTER_LIST_JACKCAST_SSL_SUBJECT`
- `SOCKETJACK_MAGIC_MASTER_LIST_DATA_FILE`
- `SOCKETJACK_MAGIC_MASTER_LIST_DATABASE`
- `SOCKETJACK_MAGIC_MASTER_LIST_TABLE`
- `SOCKETJACK_MAGIC_MASTER_LIST_TTL_MINUTES`
- `SOCKETJACK_MAGIC_MASTER_LIST_MAX_SERVERS`
- `SOCKETJACK_MAGIC_MASTER_LIST_ADMIN_KEY`

<!-- LINECOUNTER-OUTPUT:START -->
<details>
<summary><strong>LineCounter - Output</strong> <code>27,973 lines / 13 files</code></summary>

<br>

<strong>Scope:</strong> <code>SocketJack-MagicMasterList</code><br>
<strong>Source:</strong> <code>GetLineCount.bat</code> rules, non-empty/non-whitespace lines only; build/vendor folders skipped.

| Language | Files | Lines |
|---|---:|---:|
| C# | 4 | 24,577 |
| XAML | 2 | 1,739 |
| HTML | 2 | 1,316 |
| Markdown | 3 | 203 |
| JSON | 1 | 101 |
| MSBuild/XML | 1 | 37 |
| **Total** | **13** | **27,973** |

</details>
<!-- LINECOUNTER-OUTPUT:END -->
