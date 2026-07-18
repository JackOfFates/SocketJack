using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LmVs;
using SocketJack.Net;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    public Func<int, int, int, byte[]> PcAccessCaptureJpeg { get; set; }
    public Action<string> PcAccessInput { get; set; }
    public Func<PcAccessRtmpStartRequest, PcAccessRtmpStartResult> PcAccessStartRtmp { get; set; }
    public Action PcAccessStopRtmp { get; set; }
    public Func<PcAccessDesktopState> PcAccessDesktopState { get; set; }
    private readonly object _pcAccessLock = new object();
    private readonly Queue<PcAccessAuditEntry> _pcAccessAudit = new Queue<PcAccessAuditEntry>();
    private string _activePcAccessDeviceId = "";
    private string _activePcAccessSessionId = "";
    private DateTimeOffset _activePcAccessHeartbeatUtc;
    private long _pcAccessStreamGeneration;

    private void RegisterPcAccessRoutes(HttpServer server)
    {
        server.Map("GET", "/api/pc-access/status", (c, r, _) => HandlePcAccessStatus(c, r));
        server.Map("GET", "/api/pc-access/permissions", (c, r, _) => HandlePcAccessPermissions(c, r));
        server.Map("PUT", "/api/pc-access/permissions", (c, r, _) => HandlePcAccessPermissionsUpdate(c, r));
        server.Map("GET", "/api/pc-access/desktop", (c, r, _) => HandlePcAccessDesktop(c, r));
        server.Map("POST", "/api/pc-access/stream/start", (c, r, _) => HandlePcAccessStreamStart(c, r));
        server.Map("GET", "/api/pc-access/pointer", (c, r, _) => HandlePcAccessPointer(c, r));
        server.Map("POST", "/api/pc-access/desktop/input", (c, r, _) => HandlePcAccessInput(c, r));
        server.Map("GET", "/api/pc-access/ftp", (c, r, _) => HandlePcAccessFtp(c, r));
        server.Map("GET", "/api/pc-access/files", (c, r, _) => HandlePcAccessFiles(c, r));
        server.Map("POST", "/api/pc-access/files", (c, r, _) => HandlePcAccessFileMutation(c, r, "create"));
        server.Map("PUT", "/api/pc-access/files", (c, r, _) => HandlePcAccessFileMutation(c, r, "update"));
        server.Map("DELETE", "/api/pc-access/files", (c, r, _) => HandlePcAccessFileMutation(c, r, "delete"));
        server.Map("POST", "/api/pc-access/disconnect", (c, r, _) => HandlePcAccessDisconnect(c, r));
    }

    private string HandlePcAccessStatus(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out string ownerKey, out string source, out string error))
            return BuildJsonError(request, AuthenticateMobileDevice(request) == null ? 401 : 403, "Forbidden", error);
        bool supported = PcAccessCaptureJpeg != null && PcAccessInput != null;
        PcAccessDesktopState desktop = SafePcAccessDesktopState();
        PcAccessFtpSnapshot ftp = GetPcAccessFtpSnapshot(ownerKey);
        return JsonSerializer.Serialize(new
        {
            ok = true, allowed = true, supported,
            transport = PcAccessStartRtmp == null ? "jpeg" : "rtmp",
            rtmpAvailable = PcAccessStartRtmp != null && desktop.TailscaleAvailable && desktop.FfmpegAvailable,
            ffmpegAvailable = desktop.FfmpegAvailable,
            tailscaleAvailable = desktop.TailscaleAvailable,
            encoder = desktop.Encoder ?? "",
            ftpPermissionEnabled = ftp.PermissionEnabled,
            ftpRunning = ftp.Running,
            ftpReady = ftp.Ready,
            ftpWriteAllowed = ftp.AllowWrite,
            platform = Environment.OSVersion.Platform.ToString(), ownerKey, source, deviceId = device.Id,
            active = _activePcAccessDeviceId == device.Id,
            activeSessionId = _activePcAccessDeviceId == device.Id ? _activePcAccessSessionId : ""
        });
    }

    private string HandlePcAccessFtp(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out string ownerKey, out _, out string error))
            return BuildJsonError(request, AuthenticateMobileDevice(request) == null ? 401 : 403, "Forbidden", error);
        PcAccessDesktopState desktop = SafePcAccessDesktopState();
        PcAccessFtpSnapshot ftp = GetPcAccessFtpSnapshot(ownerKey);
        if (!ftp.PermissionEnabled)
            return BuildJsonError(request, 403, "Forbidden", "FTP permission is disabled for this device/account.");
        if (!ftp.Running)
            return BuildJsonError(request, 409, "FTP Stopped", "Start FTP for this user in JackLLM Workstation before opening PC Access files.");
        if (!ftp.OwnerMatches)
            return BuildJsonError(request, 409, "FTP In Use", "FTP is currently running for a different user.");
        if (string.IsNullOrWhiteSpace(desktop.TailscaleAddress))
            return BuildJsonError(request, 503, "Tailscale Unavailable", "Tailscale is required for mobile FTP file transfer.");
        if (string.IsNullOrWhiteSpace(ftp.UserName) || string.IsNullOrWhiteSpace(ftp.Password))
            return BuildJsonError(request, 409, "FTP Credentials Missing", "Configure an FTP username and password in JackLLM Workstation.");
        AuditPcAccess("ftp", device.Id, "credentials-issued");
        return JsonSerializer.Serialize(new
        {
            ok = true,
            host = desktop.TailscaleAddress,
            port = ftp.Port,
            userName = ftp.UserName,
            password = ftp.Password,
            root = "/",
            allowWrite = ftp.AllowWrite,
            passivePortStart = 50000,
            passivePortEnd = 50100
        });
    }

    private string HandlePcAccessPermissions(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request)) return BuildJsonError(request, 403, "Forbidden", "PC Access permissions can only be managed locally.");
        string target = request?.QueryParameters != null && request.QueryParameters.TryGetValue("target", out string value) ? value : "global";
        ChatPermissionState state = GetStoredChatPermissions(target, false) ?? new ChatPermissionState();
        return JsonSerializer.Serialize(new { ok = true, target, pcAccess = state.pcAccess, explicitRule = GetStoredChatPermissions(target, false) != null });
    }

    private string HandlePcAccessPermissionsUpdate(NetworkConnection connection, HttpRequest request)
    {
        if (!IsLocalAdmin(connection, request)) return BuildJsonError(request, 403, "Forbidden", "PC Access permissions can only be managed locally.");
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            string target = MobileJsonString(document.RootElement, "target").Trim();
            if (string.IsNullOrWhiteSpace(target)) target = "global";
            if (!(target == "global" || target.StartsWith("webauth:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("mobile:", StringComparison.OrdinalIgnoreCase) || target.StartsWith("ip:", StringComparison.OrdinalIgnoreCase)))
                return BuildJsonError(request, 400, "Bad Request", "Target must be global, webauth:<account>, mobile:<device>, or ip:<address/CIDR>.");
            bool enabled = document.RootElement.TryGetProperty("pcAccess", out JsonElement element) && element.ValueKind == JsonValueKind.True;
            ChatPermissionState state = GetStoredChatPermissions(target, false) ?? new ChatPermissionState();
            state.pcAccess = enabled;
            SaveChatPermissions(target, state);
            AuditPcAccess("permission", target, enabled ? "enabled" : "disabled");
            if (!enabled && _activePcAccessDeviceId.Length > 0) StopPcAccessStream("permission-revoked");
            return JsonSerializer.Serialize(new { ok = true, target, pcAccess = enabled });
        }
        catch (JsonException ex) { return BuildJsonError(request, 400, "Bad Request", "Invalid JSON: " + ex.Message); }
    }

    private string HandlePcAccessDesktop(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out _, out _, out string error))
            return BuildJsonError(request, AuthenticateMobileDevice(request) == null ? 401 : 403, "Forbidden", error);
        if (PcAccessCaptureJpeg == null) return BuildJsonError(request, 501, "Not Supported", "Desktop capture is available only on the Windows Workstation host.");
        lock (_pcAccessLock)
        {
            if (_activePcAccessDeviceId.Length > 0 && _activePcAccessDeviceId != device.Id) return BuildJsonError(request, 409, "Busy", "Another phone is controlling this Workstation.");
            _activePcAccessDeviceId = device.Id;
        }
        int width = PcQueryInt(request, "width", 1280, 320, 1920), height = PcQueryInt(request, "height", 720, 200, 1080), quality = PcQueryInt(request, "quality", 65, 25, 90);
        byte[] jpeg = PcAccessCaptureJpeg(width, height, quality);
        AuditPcAccess("desktop", device.Id, "frame");
        return JsonSerializer.Serialize(new { ok = true, contentType = "image/jpeg", data = Convert.ToBase64String(jpeg), width, height });
    }

    private string HandlePcAccessStreamStart(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out _, out _, out string error))
            return BuildJsonError(request, AuthenticateMobileDevice(request) == null ? 401 : 403, "Forbidden", error);
        if (PcAccessStartRtmp == null || PcAccessDesktopState == null)
            return BuildJsonError(request, 501, "Not Supported", "RTMP desktop streaming is available only on the Windows Workstation host.");

        int width = 1280, height = 720, fps = 20;
        try
        {
            using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            width = PcJsonInt(document.RootElement, "width", 1280, 640, 1280);
            height = PcJsonInt(document.RootElement, "height", 720, 360, 720);
            fps = PcJsonInt(document.RootElement, "fps", 20, 10, 24);
        }
        catch (JsonException ex) { return BuildJsonError(request, 400, "Bad Request", "Invalid JSON: " + ex.Message); }

        lock (_pcAccessLock)
        {
            if (_activePcAccessDeviceId.Length > 0 && _activePcAccessDeviceId != device.Id)
                return BuildJsonError(request, 409, "Busy", "Another phone is controlling this Workstation.");
        }

        StopPcAccessStream("restart");
        string sessionId = "pcs_" + PcRandomHex(18);
        string streamKey = PcRandomHex(32);
        PcAccessRtmpStartResult result;
        try
        {
            result = PcAccessStartRtmp(new PcAccessRtmpStartRequest
            {
                SessionId = sessionId,
                StreamKey = streamKey,
                Port = 19350,
                Width = width,
                Height = height,
                FramesPerSecond = fps
            });
        }
        catch (Exception ex) { return BuildJsonError(request, 503, "Unavailable", ex.Message); }
        if (result == null || !result.Ok || string.IsNullOrWhiteSpace(result.Url))
            return BuildJsonError(request, 503, "Unavailable", result?.Error ?? "The RTMP publisher could not be started.");

        long generation;
        lock (_pcAccessLock)
        {
            _activePcAccessDeviceId = device.Id;
            _activePcAccessSessionId = sessionId;
            _activePcAccessHeartbeatUtc = DateTimeOffset.UtcNow;
            generation = ++_pcAccessStreamGeneration;
        }
        _ = MonitorPcAccessHeartbeatAsync(generation);
        PcAccessDesktopState state = SafePcAccessDesktopState();
        AuditPcAccess("stream", device.Id, "started:" + result.Encoder);
        return JsonSerializer.Serialize(new
        {
            ok = true,
            sessionId,
            rtmpUrl = result.Url,
            codec = "h264",
            encoder = result.Encoder ?? "",
            bitrateKbps = result.BitrateKbps,
            expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(60),
            desktop = DesktopPayload(state),
            cursor = CursorPayload(state)
        });
    }

    private string HandlePcAccessPointer(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out _, out _, out string error))
            return BuildJsonError(request, AuthenticateMobileDevice(request) == null ? 401 : 403, "Forbidden", error);
        string sessionId = request?.QueryParameters != null && request.QueryParameters.TryGetValue("sessionId", out string value) ? value : "";
        lock (_pcAccessLock)
        {
            if (_activePcAccessDeviceId != device.Id || string.IsNullOrWhiteSpace(sessionId) || sessionId != _activePcAccessSessionId)
                return BuildJsonError(request, 409, "Inactive", "The PC Access stream session is no longer active.");
            _activePcAccessHeartbeatUtc = DateTimeOffset.UtcNow;
        }
        PcAccessDesktopState state = SafePcAccessDesktopState();
        return JsonSerializer.Serialize(new { ok = true, sessionId, desktop = DesktopPayload(state), cursor = CursorPayload(state) });
    }

    private string HandlePcAccessInput(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out _, out _, out string error)) return BuildJsonError(request, 403, "Forbidden", error);
        if (PcAccessInput == null) return BuildJsonError(request, 501, "Not Supported", "Desktop input is available only on the Windows Workstation host.");
        if (_activePcAccessDeviceId != device.Id) return BuildJsonError(request, 409, "Inactive", "Open the desktop stream before sending input.");
        string inputSessionId = "";
        try
        {
            using JsonDocument input = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            inputSessionId = MobileJsonString(input.RootElement, "sessionId");
        }
        catch (JsonException) { }
        lock (_pcAccessLock)
        {
            if (!string.IsNullOrWhiteSpace(inputSessionId) && inputSessionId != _activePcAccessSessionId)
                return BuildJsonError(request, 409, "Inactive", "The PC Access stream session is no longer active.");
            _activePcAccessHeartbeatUtc = DateTimeOffset.UtcNow;
        }
        PcAccessInput(request?.Body ?? "{}");
        AuditPcAccess("input", device.Id, "accepted");
        return JsonSerializer.Serialize(new { ok = true });
    }

    private string HandlePcAccessFiles(NetworkConnection connection, HttpRequest request)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out string ownerKey, out _, out string error)) return BuildJsonError(request, 403, "Forbidden", error);
        PcAccessFtpSnapshot ftp = GetPcAccessFtpSnapshot(ownerKey);
        if (!ftp.Ready) return BuildJsonError(request, 409, "FTP Required", "FTP must be enabled and running for this user before PC Access files can be used.");
        string requested = request?.QueryParameters != null && request.QueryParameters.TryGetValue("path", out string path) ? path : "";
        if (!TryResolveApprovedPcPath(ownerKey, requested, true, out string resolved, out error)) return BuildJsonError(request, 403, "Forbidden", error);
        if (File.Exists(resolved)) return JsonSerializer.Serialize(new { ok = true, path = resolved, file = true, data = Convert.ToBase64String(File.ReadAllBytes(resolved)) });
        if (!Directory.Exists(resolved)) return BuildJsonError(request, 404, "Not Found", "The requested path does not exist.");
        var entries = Directory.EnumerateFileSystemEntries(resolved).Take(1000).Select(p => new { name = Path.GetFileName(p), path = p, directory = Directory.Exists(p), size = File.Exists(p) ? new FileInfo(p).Length : 0L }).ToArray();
        return JsonSerializer.Serialize(new { ok = true, path = resolved, entries });
    }

    private string HandlePcAccessFileMutation(NetworkConnection connection, HttpRequest request, string verb)
    {
        if (!TryAuthorizePcAccess(connection, request, out MobileDeviceRecord device, out string ownerKey, out _, out string error)) return BuildJsonError(request, 403, "Forbidden", error);
        PcAccessFtpSnapshot ftp = GetPcAccessFtpSnapshot(ownerKey);
        if (!ftp.Ready) return BuildJsonError(request, 409, "FTP Required", "FTP must be enabled and running for this user before PC Access files can be used.");
        if (!ftp.AllowWrite) return BuildJsonError(request, 403, "Read Only", "This FTP account does not allow changes.");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(request?.Body) ? "{}" : request.Body);
            string path = MobileJsonString(doc.RootElement, "path"), destination = MobileJsonString(doc.RootElement, "destination"), kind = MobileJsonString(doc.RootElement, "kind");
            if (!TryResolveApprovedPcPath(ownerKey, path, false, out string resolved, out error)) return BuildJsonError(request, 403, "Forbidden", error);
            if (verb == "delete") { if (File.Exists(resolved)) File.Delete(resolved); else if (Directory.Exists(resolved)) Directory.Delete(resolved, false); else return BuildJsonError(request, 404, "Not Found", "Path not found."); }
            else if (verb == "create" && kind.Equals("directory", StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(resolved);
            else if (verb == "create") File.WriteAllBytes(resolved, Convert.FromBase64String(MobileJsonString(doc.RootElement, "data")));
            else { if (!TryResolveApprovedPcPath(ownerKey, destination, false, out string target, out error)) return BuildJsonError(request, 403, "Forbidden", error); if (File.Exists(resolved)) File.Move(resolved, target); else Directory.Move(resolved, target); }
            AuditPcAccess("file", device.Id, verb + ":" + resolved);
            return JsonSerializer.Serialize(new { ok = true, path = resolved });
        }
        catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException || ex is FormatException) { return BuildJsonError(request, 400, "Bad Request", ex.Message); }
    }

    private string HandlePcAccessDisconnect(NetworkConnection connection, HttpRequest request)
    {
        MobileDeviceRecord device = AuthenticateMobileDevice(request);
        if (device == null) return BuildJsonError(request, 401, "Unauthorized", "A paired mobile token is required.");
        lock (_pcAccessLock) if (_activePcAccessDeviceId != device.Id) return JsonSerializer.Serialize(new { ok = true });
        StopPcAccessStream("disconnected");
        AuditPcAccess("desktop", device.Id, "disconnected");
        return JsonSerializer.Serialize(new { ok = true });
    }

    private bool TryAuthorizePcAccess(NetworkConnection connection, HttpRequest request, out MobileDeviceRecord device, out string ownerKey, out string source, out string error)
    {
        device = AuthenticateMobileDevice(request); ownerKey = ""; source = ""; error = "A paired mobile token is required.";
        if (device == null || !MobileAccessEnabled) return false;
        ownerKey = string.IsNullOrWhiteSpace(device.OwnerKey) ? "mobile:" + device.Id : device.OwnerKey;
        ChatPermissionState owner = GetStoredChatPermissions(ownerKey, false);
        if (owner != null) { source = ownerKey; if (owner.pcAccess) return true; error = "PC Access is disabled for this device/account."; return false; }
        string ip = ExtractClientIp(connection, request);
        foreach (string target in PcIpTargets(ip)) { ChatPermissionState rule = GetStoredChatPermissions(target, false); if (rule != null) { source = target; if (rule.pcAccess) return true; error = "PC Access is denied by the matching IP rule."; return false; } }
        ChatPermissionState global = GetStoredChatPermissions("global", true); source = "global";
        if (global.pcAccess) return true;
        error = "PC Access has not been enabled for this device, account, IP, or globally."; AuditPcAccess("denied", device.Id, source); return false;
    }

    private PcAccessFtpSnapshot GetPcAccessFtpSnapshot(string ownerKey)
    {
        ChatPermissionState permissions = GetChatPermissions(ownerKey);
        lock (_chatFtpServerLock)
        {
            bool running = _chatFtpServer != null && _chatFtpServer.IsListening && _activeChatFtpConfig != null;
            bool ownerMatches = running && string.Equals(_activeChatFtpOwnerKey, ownerKey, StringComparison.OrdinalIgnoreCase);
            ChatFtpUserConfig user = ownerMatches && _activeChatFtpConfig.users != null
                ? _activeChatFtpConfig.users.FirstOrDefault(item => item != null)
                : null;
            return new PcAccessFtpSnapshot
            {
                PermissionEnabled = permissions != null && permissions.ftpServer,
                Running = running,
                OwnerMatches = ownerMatches,
                Port = ownerMatches ? _activeChatFtpConfig.port : 0,
                UserName = user?.userName ?? (ownerMatches ? _activeChatFtpConfig.userName : ""),
                Password = user?.password ?? (ownerMatches ? _activeChatFtpConfig.password : ""),
                AllowWrite = user?.allowWrite ?? (ownerMatches && _activeChatFtpConfig.allowWrite)
            };
        }
    }

    private IEnumerable<string> PcIpTargets(string ip)
    {
        ip = NormalizeClientIp(ip); yield return "ip:" + ip;
        if (!IPAddress.TryParse(ip, out IPAddress address)) yield break;
        for (int prefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128; prefix >= 0; prefix--)
            foreach (string candidate in new[] { "ip:" + ip + "/" + prefix }) if (PcCidrContains(candidate.Substring(3), address)) yield return candidate;
    }

    private static bool PcCidrContains(string cidr, IPAddress address)
    {
        string[] parts = cidr.Split('/'); if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress network) || !int.TryParse(parts[1], out int prefix)) return false;
        byte[] a = address.GetAddressBytes(), n = network.GetAddressBytes(); if (a.Length != n.Length || prefix < 0 || prefix > a.Length * 8) return false;
        for (int i = 0; i < a.Length; i++) { int bits = Math.Min(8, Math.Max(0, prefix - i * 8)); if (bits == 0) break; int mask = 0xff << (8 - bits); if ((a[i] & mask) != (n[i] & mask)) return false; } return true;
    }

    private bool TryResolveApprovedPcPath(string ownerKey, string requested, bool allowRootDefault, out string resolved, out string error)
    {
        resolved = ""; error = "No filesystem roots are approved for this owner.";
        List<string> roots = GetChatFilesystemAccess(ownerKey).Where(x => !string.IsNullOrWhiteSpace(x?.path)).Select(x => Path.GetFullPath(x.path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).ToList();
        if (roots.Count == 0) return false;
        resolved = string.IsNullOrWhiteSpace(requested) && allowRootDefault ? roots[0].TrimEnd(Path.DirectorySeparatorChar) : Path.GetFullPath(requested ?? "");
        string candidate = resolved.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!roots.Any(root => candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))) { error = "The path is outside the approved filesystem roots."; return false; }
        return true;
    }

    private int PcQueryInt(HttpRequest request, string key, int fallback, int min, int max) => request?.QueryParameters != null && request.QueryParameters.TryGetValue(key, out string value) && int.TryParse(value, out int parsed) ? Math.Max(min, Math.Min(max, parsed)) : fallback;
    private static int PcJsonInt(JsonElement root, string key, int fallback, int min, int max) => root.TryGetProperty(key, out JsonElement value) && value.TryGetInt32(out int parsed) ? Math.Max(min, Math.Min(max, parsed)) : fallback;
    private static string PcRandomHex(int byteCount)
    {
        byte[] bytes = new byte[byteCount];
        using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }

    private async Task MonitorPcAccessHeartbeatAsync(long generation)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            bool expired;
            lock (_pcAccessLock)
                expired = generation != _pcAccessStreamGeneration || _activePcAccessSessionId.Length == 0 || DateTimeOffset.UtcNow - _activePcAccessHeartbeatUtc > TimeSpan.FromSeconds(20);
            if (generation != _pcAccessStreamGeneration || _activePcAccessSessionId.Length == 0) return;
            if (expired) { StopPcAccessStream("heartbeat-expired"); return; }
        }
    }

    private void StopPcAccessStream(string reason)
    {
        string device;
        lock (_pcAccessLock)
        {
            device = _activePcAccessDeviceId;
            _activePcAccessDeviceId = "";
            _activePcAccessSessionId = "";
            _activePcAccessHeartbeatUtc = default;
            _pcAccessStreamGeneration++;
        }
        try { PcAccessStopRtmp?.Invoke(); } catch { }
        if (!string.IsNullOrWhiteSpace(device)) AuditPcAccess("stream", device, reason);
    }

    private PcAccessDesktopState SafePcAccessDesktopState()
    {
        try { return PcAccessDesktopState?.Invoke() ?? new PcAccessDesktopState(); }
        catch { return new PcAccessDesktopState(); }
    }

    private static object DesktopPayload(PcAccessDesktopState state) => new { left = state.Left, top = state.Top, width = state.Width, height = state.Height };
    private static object CursorPayload(PcAccessDesktopState state) => new
    {
        x = state.Width <= 0 ? 0d : Math.Max(0d, Math.Min(1d, (state.CursorX - state.Left) / (double)state.Width)),
        y = state.Height <= 0 ? 0d : Math.Max(0d, Math.Min(1d, (state.CursorY - state.Top) / (double)state.Height)),
        visible = state.CursorVisible
    };
    private void AuditPcAccess(string type, string subject, string detail) { lock (_pcAccessLock) { _pcAccessAudit.Enqueue(new PcAccessAuditEntry { AtUtc = DateTimeOffset.UtcNow, Type = type, Subject = subject, Detail = detail }); while (_pcAccessAudit.Count > 500) _pcAccessAudit.Dequeue(); } }
    private sealed class PcAccessAuditEntry { public DateTimeOffset AtUtc { get; set; } public string Type { get; set; } = ""; public string Subject { get; set; } = ""; public string Detail { get; set; } = ""; }
    private sealed class PcAccessFtpSnapshot
    {
        public bool PermissionEnabled { get; set; }
        public bool Running { get; set; }
        public bool OwnerMatches { get; set; }
        public bool Ready => PermissionEnabled && Running && OwnerMatches;
        public int Port { get; set; }
        public string UserName { get; set; } = "";
        public string Password { get; set; } = "";
        public bool AllowWrite { get; set; }
    }
}

public sealed class PcAccessRtmpStartRequest
{
    public string SessionId { get; set; } = "";
    public string StreamKey { get; set; } = "";
    public int Port { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int FramesPerSecond { get; set; }
}

public sealed class PcAccessRtmpStartResult
{
    public bool Ok { get; set; }
    public string Url { get; set; } = "";
    public string Encoder { get; set; } = "";
    public int BitrateKbps { get; set; } = 750;
    public string Error { get; set; } = "";
}

public sealed class PcAccessDesktopState
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int CursorX { get; set; }
    public int CursorY { get; set; }
    public bool CursorVisible { get; set; } = true;
    public bool FfmpegAvailable { get; set; }
    public bool TailscaleAvailable { get; set; }
    public string TailscaleAddress { get; set; } = "";
    public string Encoder { get; set; } = "";
}
