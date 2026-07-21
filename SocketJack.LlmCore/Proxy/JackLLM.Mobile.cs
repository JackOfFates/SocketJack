using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocketJack.Net;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    private readonly object _mobilePairingLock = new object();
    private MobileAccessState _mobileAccessState;

    public bool MobileAccessEnabled { get; set; }

    private void RegisterMobileRoutes(HttpServer server)
    {
        MobileAccessState persistedState = GetMobileAccessState();
        MobileAccessEnabled = MobileAccessEnabled || persistedState.Enabled;
        server.Map("GET", "/mobile", (connection, request, cancellationToken) => HandleMobileAdminPage(connection, request));
        server.Map("GET", "/mobile/", (connection, request, cancellationToken) => HandleMobileAdminPage(connection, request));
        server.Map("GET", "/api/mobile/status", (connection, request, cancellationToken) => HandleMobileStatus(connection, request));
        server.Map("POST", "/api/mobile/access", (connection, request, cancellationToken) => HandleMobileAccessUpdate(connection, request));
        server.Map("POST", "/api/mobile/pairing/start", (connection, request, cancellationToken) => HandleMobilePairingStart(connection, request));
        server.Map("POST", "/api/mobile/pairing/complete", (connection, request, cancellationToken) => HandleMobilePairingComplete(connection, request));
        server.Map("GET", "/api/mobile/devices", (connection, request, cancellationToken) => HandleMobileDevices(connection, request));
        server.Map("PUT", "/api/mobile/devices/*", (connection, request, cancellationToken) => HandleMobileDeviceUpdate(connection, request));
        server.Map("DELETE", "/api/mobile/devices/*", (connection, request, cancellationToken) => HandleMobileDeviceDelete(connection, request));
        RegisterPcAccessRoutes(server);
    }

    private string HandleMobileAdminPage(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Phone connection management is available only from the local Workstation.");
        if (request?.Context?.Response != null)
        {
            request.Context.Response.ContentType = "text/html; charset=utf-8";
            request.Context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        }
        string serverName = WebUtility.HtmlEncode(Environment.MachineName);
        string endpoint = WebUtility.HtmlEncode(ChatServerUrl.TrimEnd('/'));
        return @"<!doctype html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>JackLLM Mobile Connections</title>
<style>
:root{color-scheme:dark;--bg:#08111f;--panel:#111827;--muted:#94a3b8;--text:#e5eefb;--line:#26334d;--blue:#2563eb;--green:#10b981;--red:#ef4444}
body{margin:0;background:radial-gradient(circle at top,#172554 0,#08111f 44%,#020617 100%);color:var(--text);font:15px/1.45 system-ui,-apple-system,Segoe UI,sans-serif}
main{max-width:940px;margin:0 auto;padding:28px 18px 44px}
.hero,.card{background:rgba(17,24,39,.86);border:1px solid var(--line);border-radius:22px;box-shadow:0 18px 52px rgba(0,0,0,.34)}
.hero{padding:24px;margin-bottom:16px}.card{padding:18px;margin-top:14px}
h1{margin:0 0 6px;font-size:30px}h2{margin:0 0 12px;font-size:18px}.muted{color:var(--muted)}
.row{display:flex;gap:10px;flex-wrap:wrap;align-items:center}.space{justify-content:space-between}
button{border:0;border-radius:12px;padding:10px 14px;color:white;background:#334155;cursor:pointer;font-weight:700}
button.primary{background:var(--blue)}button.good{background:var(--green)}button.danger{background:var(--red)}
button:disabled{opacity:.55;cursor:not-allowed}.pill{border:1px solid var(--line);border-radius:999px;padding:6px 10px;color:var(--muted)}
code,.code{font-family:ui-monospace,SFMono-Regular,Consolas,monospace}.code{background:#020617;border:1px solid var(--line);border-radius:14px;padding:13px;overflow:auto;white-space:pre-wrap}
table{width:100%;border-collapse:collapse}td,th{padding:10px;border-bottom:1px solid var(--line);text-align:left}th{color:var(--muted);font-size:12px;text-transform:uppercase}
.empty{padding:18px;border:1px dashed var(--line);border-radius:14px;color:var(--muted)}.note{min-height:20px;color:var(--muted)}.error{color:#fca5a5}
</style>
</head>
<body>
<main>
<section class=""hero"">
<div class=""row space""><div><h1>JackLLM Mobile Connections</h1><div class=""muted"">Manage Android phones paired with " + serverName + @" at <code>" + endpoint + @"</code>.</div></div><span id=""state"" class=""pill"">Loading</span></div>
</section>
<section class=""card"">
<div class=""row space""><div><h2>Mobile Access</h2><div class=""muted"">Disabled by default. Enable only when pairing or using trusted phones.</div></div><div class=""row""><button id=""enable"" class=""good"">Enable</button><button id=""disable"" class=""danger"">Disable</button></div></div>
</section>
<section class=""card"">
<div class=""row space""><div><h2>Pair a phone</h2><div class=""muted"">Open JackLLM Mobile, choose manual/LAN pairing, then enter this code. Codes expire in five minutes and can be used once.</div></div><button id=""pair"" class=""primary"">Start pairing</button></div>
<div id=""pairing"" class=""code"" hidden></div>
</section>
<section class=""card"">
<div class=""row space""><h2>Paired phones</h2><button id=""reload"">Refresh</button></div>
<div id=""devices""></div>
</section>
<p id=""note"" class=""note""></p>
</main>
<script>
const state=document.getElementById('state'),enable=document.getElementById('enable'),disable=document.getElementById('disable'),pair=document.getElementById('pair'),pairing=document.getElementById('pairing'),devices=document.getElementById('devices'),reload=document.getElementById('reload'),note=document.getElementById('note');
function esc(v){return String(v||'').replace(/[&<>""']/g,c=>c==='&'?'&amp;':c==='<'?'&lt;':c==='>'?'&gt;':c==='""'?'&quot;':'&#39;');}
async function json(res){const text=await res.text();const data=text?JSON.parse(text):{};if(!res.ok||data.ok===false)throw new Error(data.error||data.message||('HTTP '+res.status));return data;}
function setNote(text,err){note.textContent=text||'';note.classList.toggle('error',!!err);}
async function load(){setNote('');const status=await json(await fetch('/api/mobile/status'));state.textContent=status.enabled?'Enabled':'Disabled';state.style.color=status.enabled?'#86efac':'#fca5a5';enable.disabled=!!status.enabled;disable.disabled=!status.enabled;pair.disabled=!status.enabled;const data=await json(await fetch('/api/mobile/devices'));renderDevices(data.devices||[]);}
function renderDevices(list){if(!list.length){devices.innerHTML='<div class=""empty"">No phones are paired yet.</div>';return;}devices.innerHTML='<table><thead><tr><th>Name</th><th>Platform</th><th>Created</th><th>Last seen</th><th>Scopes</th><th></th></tr></thead><tbody>'+list.map(d=>'<tr><td>'+esc(d.Name||d.name)+'</td><td>'+esc(d.Platform||d.platform)+'</td><td>'+esc(d.CreatedAtUtc||d.createdAtUtc)+'</td><td>'+esc(d.LastSeenAtUtc||d.lastSeenAtUtc)+'</td><td>'+esc((d.Scopes||d.scopes||[]).join(', '))+'</td><td><button class=""danger"" data-id=""'+esc(d.Id||d.id)+'"">Revoke</button></td></tr>').join('')+'</tbody></table>';devices.querySelectorAll('button[data-id]').forEach(b=>b.addEventListener('click',()=>revoke(b.dataset.id)));}
async function setAccess(enabled){await json(await fetch('/api/mobile/access',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled})}));await load();setNote(enabled?'Mobile Access enabled.':'Mobile Access disabled. Existing phones cannot call APIs until it is enabled again.');}
async function startPairing(){const data=await json(await fetch('/api/mobile/pairing/start',{method:'POST'}));pairing.hidden=false;pairing.textContent='Code: '+data.code+'\nEndpoint: '+data.endpoint+'\nExpires: '+data.expiresAt+'\nDeep link: '+data.qr;setNote('Pairing code is live for five minutes.');}
async function revoke(id){if(!id||!confirm('Revoke this phone?'))return;await json(await fetch('/api/mobile/devices/'+encodeURIComponent(id),{method:'DELETE'}));await load();setNote('Phone revoked.');}
enable.addEventListener('click',()=>setAccess(true).catch(e=>setNote(e.message,true)));disable.addEventListener('click',()=>setAccess(false).catch(e=>setNote(e.message,true)));pair.addEventListener('click',()=>startPairing().catch(e=>setNote(e.message,true)));reload.addEventListener('click',()=>load().catch(e=>setNote(e.message,true)));
load().catch(e=>setNote(e.message,true));
</script>
</body>
</html>";
    }

    private string HandleMobileAccessUpdate(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Mobile Access can only be changed from the local Workstation.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            JsonElement root = document.RootElement;
            bool enabled = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("enabled", out JsonElement enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
            MobileAccessEnabled = enabled;
            MobileAccessState state = GetMobileAccessState();
            state.Enabled = enabled;
            SaveMobileAccessState(state);
            return JsonSerializer.Serialize(new { ok = true, enabled = MobileAccessEnabled });
        }
        catch (JsonException ex) { return BuildJsonError(request, 400, "Bad Request", "Invalid JSON: " + ex.Message); }
    }

    private string HandleMobileStatus(NetworkConnection connection, HttpRequest request)
    {
        MobileDeviceRecord device = AuthenticateMobileDevice(request);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            enabled = MobileAccessEnabled,
            paired = device != null,
            deviceId = device?.Id ?? "",
            ownerKey = device?.OwnerKey ?? "",
            scopes = device?.Scopes ?? Array.Empty<string>(),
            dreamAdmin = device?.Scopes?.Contains("dream.admin", StringComparer.OrdinalIgnoreCase) == true,
            serverName = Environment.MachineName,
            pairingAvailable = MobileAccessEnabled && IsLocalAdmin(connection, request)
        });
    }

    private string HandleMobilePairingStart(NetworkConnection connection, HttpRequest request)
    {
        if (!MobileAccessEnabled)
            return BuildJsonError(request, 403, "Forbidden", "Mobile Access is disabled on this Workstation.");
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Pairing can only be started from the local Workstation.");

        lock (_mobilePairingLock)
        {
            MobileAccessState state = GetMobileAccessState();
            state.Pairings.RemoveAll(item => item.ExpiresAtUtc <= DateTimeOffset.UtcNow || item.Used);
            string code = RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6", CultureInfo.InvariantCulture);
            state.Pairings.Add(new MobilePairingRecord
            {
                CodeHash = HashSecret(code),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                OwnerKey = GetChatSessionOwnerKey(connection, request)
            });
            SaveMobileAccessState(state);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                code,
                expiresAt = state.Pairings[^1].ExpiresAtUtc,
                expiresInSeconds = 300,
                endpoint = ChatServerUrl.TrimEnd('/'),
                qr = "jackllm://pair?endpoint=" + Uri.EscapeDataString(ChatServerUrl.TrimEnd('/')) + "&code=" + code
            });
        }
    }

    private string HandleMobilePairingComplete(NetworkConnection connection, HttpRequest request)
    {
        if (!MobileAccessEnabled)
            return BuildJsonError(request, 403, "Forbidden", "Mobile Access is disabled on this Workstation.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            JsonElement root = document.RootElement;
            string code = MobileJsonString(root, "code").Trim();
            string deviceName = MobileJsonString(root, "deviceName").Trim();
            string platform = MobileJsonString(root, "platform").Trim();
            if (code.Length != 6 || !code.All(char.IsDigit))
                return BuildJsonError(request, 400, "Bad Request", "A six-digit pairing code is required.");

            lock (_mobilePairingLock)
            {
                MobileAccessState state = GetMobileAccessState();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                string hash = HashSecret(code);
                MobilePairingRecord pairing = state.Pairings.FirstOrDefault(item => !item.Used && item.ExpiresAtUtc > now && FixedEquals(item.CodeHash, hash));
                if (pairing == null)
                    return BuildJsonError(request, 401, "Unauthorized", "The pairing code is invalid, expired, or already used.");

                pairing.Used = true;
                byte[] tokenBytes = new byte[32];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(tokenBytes);
                string token = "jlm_" + Base64Url(tokenBytes);
                var device = new MobileDeviceRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Name = string.IsNullOrWhiteSpace(deviceName) ? "Android phone" : deviceName,
                    Platform = string.IsNullOrWhiteSpace(platform) ? "android" : platform,
                    TokenHash = HashSecret(token),
                    CreatedAtUtc = now,
                    LastSeenAtUtc = now,
                    Scopes = new[] { "chat", "models", "sessions", "files", "media", "voice", "share", "dream" }
                };
                device.OwnerKey = string.IsNullOrWhiteSpace(pairing.OwnerKey) ? "mobile:" + device.Id : pairing.OwnerKey;
                state.Devices.Add(device);
                SaveMobileAccessState(state);
                return JsonSerializer.Serialize(new { ok = true, token, deviceId = device.Id, device.Name, device.Scopes });
            }
        }
        catch (JsonException ex) { return BuildJsonError(request, 400, "Bad Request", "Invalid JSON: " + ex.Message); }
    }

    private string HandleMobileDevices(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Paired devices can only be managed locally.");
        lock (_mobilePairingLock)
        {
            MobileAccessState state = GetMobileAccessState();
            return JsonSerializer.Serialize(new { ok = true, enabled = MobileAccessEnabled, devices = state.Devices.Select(item => new { item.Id, item.Name, item.Platform, item.OwnerKey, item.CreatedAtUtc, item.LastSeenAtUtc, item.Scopes, dreamAdmin = item.Scopes?.Contains("dream.admin", StringComparer.OrdinalIgnoreCase) == true }) });
        }
    }

    private string HandleMobileDeviceUpdate(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Paired-device Dream administration can only be changed locally.");
        string id = request?.PathVariables?.LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(id) && request?.QueryParameters != null) request.QueryParameters.TryGetValue("id", out id);
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            bool dreamAdmin = document.RootElement.TryGetProperty("dreamAdmin", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            LmVs.MobileDreamDeviceSnapshot updated = SetMobileDreamAdminDiagnostics(id, dreamAdmin);
            return JsonSerializer.Serialize(new { ok = true, device = updated });
        }
        catch (Exception ex) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
    }

    public IReadOnlyList<LmVs.MobileDreamDeviceSnapshot> GetMobileDreamDevicesDiagnostics()
    {
        lock (_mobilePairingLock)
            return GetMobileAccessState().Devices.Select(item => new LmVs.MobileDreamDeviceSnapshot {
                Id = item.Id, Name = item.Name, Platform = item.Platform, OwnerKey = item.OwnerKey,
                DreamAdmin = item.Scopes?.Contains("dream.admin", StringComparer.OrdinalIgnoreCase) == true,
                LastSeenUtc = item.LastSeenAtUtc.ToString("O")
            }).ToList();
    }

    public LmVs.MobileDreamDeviceSnapshot SetMobileDreamAdminDiagnostics(string id, bool enabled)
    {
        lock (_mobilePairingLock)
        {
            MobileAccessState state = GetMobileAccessState();
            MobileDeviceRecord device = state.Devices.FirstOrDefault(item => item.Id.Equals(id ?? "", StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("Paired device was not found.");
            HashSet<string> scopes = new(device.Scopes ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase) { "dream" };
            if (enabled) scopes.Add("dream.admin"); else scopes.Remove("dream.admin");
            device.Scopes = scopes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            SaveMobileAccessState(state);
            return GetMobileDreamDevicesDiagnostics().First(item => item.Id == device.Id);
        }
    }

    private string HandleMobileDeviceDelete(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request))
            return BuildJsonError(request, 403, "Forbidden", "Paired devices can only be revoked locally.");
        string id = request?.PathVariables?.LastOrDefault() ?? "";
        if (string.IsNullOrWhiteSpace(id) && request?.QueryParameters != null) request.QueryParameters.TryGetValue("id", out id);
        lock (_mobilePairingLock)
        {
            MobileAccessState state = GetMobileAccessState();
            int removed = state.Devices.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            SaveMobileAccessState(state);
            return removed == 0 ? BuildJsonError(request, 404, "Not Found", "Paired device was not found.") : JsonSerializer.Serialize(new { ok = true, revoked = id });
        }
    }

    private MobileDeviceRecord AuthenticateMobileDevice(HttpRequest request)
    {
        string token = ExtractBearerToken(request);
        if (!token.StartsWith("jlm_", StringComparison.Ordinal) || !MobileAccessEnabled) return null;
        string hash = HashSecret(token);
        lock (_mobilePairingLock)
        {
            MobileAccessState state = GetMobileAccessState();
            MobileDeviceRecord device = state.Devices.FirstOrDefault(item => FixedEquals(item.TokenHash, hash));
            if (device != null && DateTimeOffset.UtcNow - device.LastSeenAtUtc > TimeSpan.FromMinutes(1))
            {
                device.LastSeenAtUtc = DateTimeOffset.UtcNow;
                SaveMobileAccessState(state);
            }
            return device;
        }
    }

    private MobileAccessState GetMobileAccessState()
    {
        if (_mobileAccessState != null) return _mobileAccessState;
        try
        {
            string path = MobileAccessStatePath();
            _mobileAccessState = File.Exists(path) ? JsonSerializer.Deserialize<MobileAccessState>(File.ReadAllText(path)) : null;
        }
        catch { _mobileAccessState = null; }
        _mobileAccessState ??= new MobileAccessState();
        bool migrated = false;
        foreach (MobileDeviceRecord device in _mobileAccessState.Devices)
        {
            if (!string.IsNullOrWhiteSpace(device.OwnerKey)) continue;
            // Devices paired before owner-aware pairing belonged to the local
            // Workstation user. Preserve their old mobile:<id> data through the
            // equivalent-owner alias while moving future data to that user.
            device.OwnerKey = GetDefaultLocalChatOwnerKey();
            migrated = true;
        }
        if (migrated) SaveMobileAccessState(_mobileAccessState);
        return _mobileAccessState;
    }

    private void SaveMobileAccessState(MobileAccessState state)
    {
        _mobileAccessState = state;
        string path = MobileAccessStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temporary, path);
    }

    private static string MobileAccessStatePath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "mobile-access.json");
    internal static string HashSecret(string value) { using (SHA256 sha = SHA256.Create()) return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")).Select(b => b.ToString("x2", CultureInfo.InvariantCulture))); }
    internal static bool FixedEquals(string left, string right)
    {
        if (left == null || right == null || left.Length != right.Length) return false;
        int difference = 0;
        for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
        return difference == 0;
    }
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string MobileJsonString(JsonElement root, string name) { if (root.ValueKind != JsonValueKind.Object) return ""; foreach (JsonProperty property in root.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString(); return ""; }

    private sealed class MobileAccessState
    {
        public bool Enabled { get; set; }
        public List<MobilePairingRecord> Pairings { get; set; } = new List<MobilePairingRecord>();
        public List<MobileDeviceRecord> Devices { get; set; } = new List<MobileDeviceRecord>();
    }

    private sealed class MobilePairingRecord
    {
        public string CodeHash { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public bool Used { get; set; }
        public string OwnerKey { get; set; } = "";
    }

    private sealed class MobileDeviceRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Platform { get; set; } = "android";
        public string TokenHash { get; set; } = "";
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset LastSeenAtUtc { get; set; }
        public string[] Scopes { get; set; } = Array.Empty<string>();
        public string OwnerKey { get; set; } = "";
    }
}
