using SocketJack.Net;
using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace SocketJack.Update;

public sealed class UpdateServerHost : IDisposable {
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromDays(14);
    private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);
    private const string MasterListChannelId = "socketjack-magic-master-list";
    private const string MasterListProcessName = "SocketJack-MagicMasterList";
    private const string MetadataFileName = ".socketjack-update.meta";
    private const string WindowsRunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static readonly object ProcessStartSync = new();
    private readonly object _sync = new();
    private readonly object _logSync = new();
    private readonly UpdateServerOptions _options;
    private readonly MutableTcpServer _server;
    private MutableTcpServer? _publicHttpServer;
    private MutableTcpServer? _publicHttpsServer;
    private string _publicForwardingError = "";
    private readonly Dictionary<string, UploadSession> _uploads = new(StringComparer.OrdinalIgnoreCase);
    private AuthState _auth = new();
    private bool _disposed;

    public UpdateServerHost(UpdateServerOptions options) {
        _options = options;
        Directory.CreateDirectory(_options.DataDirectory);
        Directory.CreateDirectory(_options.UploadStagingDirectory);
        LoadAuth();
        ConfigureAdminFromEnvironment();
        var networkOptions = NetworkOptions.NewDefault();
        networkOptions.BindAddress = _options.BindAddress;
        networkOptions.UseSsl = false;
        _server = new MutableTcpServer(networkOptions, _options.Port, "SocketJack Update");
        _server.EnablePort80HttpsRedirect = false;
        _server.SslTargetHost = "";
        _server.OnError += Server_OnError;
        MapRoutes();
        ConfigurePublicForwarding();
        AutoStartConfiguredChannels();
    }

    public event EventHandler? StatusChanged;
    public bool HasAdminLogin => !string.IsNullOrWhiteSpace(_auth.UserName) && !string.IsNullOrWhiteSpace(_auth.PasswordHash);
    public string EndpointText => CombineUrl(_options.PublicUrl, "");
    public string Status { get; private set; } = "Idle";
    private string LogFilePath => Path.Combine(_options.DataDirectory, "SocketJack.Update.log");
    private string PublicBasePath => GetPublicBasePath(_options.PublicUrl);

    private void Server_OnError(SocketJack.Net.ErrorEventArgs e) {
        if (e?.Exception == null)
            return;
        AppendLog("http-error", e.Exception.GetType().Name + " detail=\"" + e.Exception.Message + "\" stack=\"" + e.Exception.StackTrace + "\"");
    }

    public bool Start() {
        bool privateOk = _server.IsListening || _server.Listen();
        bool publicHttpOk = _publicHttpServer == null || _publicHttpServer.IsListening || _publicHttpServer.Listen();
        bool publicHttpsOk = _publicHttpsServer == null || _publicHttpsServer.IsListening || _publicHttpsServer.Listen();
        bool publicForwardingEnabled = false;
        bool publicForwardingOk = !publicForwardingEnabled ||
                                   (_publicHttpServer != null && publicHttpOk &&
                                    _publicHttpsServer != null && publicHttpsOk);
        bool ok = privateOk;

        if (ok) {
            Status = "Listening on " + EndpointText;
            if (publicForwardingEnabled && publicForwardingOk && _options.PublicForwarding != null)
                Status += " | public forwarding :" + _options.PublicForwarding.HttpPort.ToString(CultureInfo.InvariantCulture) + "/:" + _options.PublicForwarding.HttpsPort.ToString(CultureInfo.InvariantCulture);
            else if (publicForwardingEnabled)
                Status += " | public forwarding unavailable: " + FirstNonEmpty(_publicForwardingError, "public listeners did not start");
        } else if (!privateOk) {
            Status = "Unable to listen on private update port " + _options.Port.ToString(CultureInfo.InvariantCulture);
        } else {
            Status = FirstNonEmpty(_publicForwardingError, "Public forwarding listeners failed to start.");
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public void Stop() {
        if (_server.IsListening)
            _server.StopListening();
        if (_publicHttpServer?.IsListening == true)
            _publicHttpServer.StopListening();
        if (_publicHttpsServer?.IsListening == true)
            _publicHttpsServer.StopListening();
        Status = "Stopped";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<UpdateChannel> GetChannelSnapshots() {
        var channels = new List<UpdateChannel>();
        Dictionary<string, LastUpdateRecord> lastUpdates = LoadLastUpdateMetadata()
            .ToDictionary(record => UpdateServerOptions.NormalizeChannelId(record.ChannelId), StringComparer.OrdinalIgnoreCase);
        foreach (UpdateChannel channel in _options.Channels) {
            long bytes = 0;
            int count = 0;
            if (Directory.Exists(channel.UpdateDirectory)) {
                foreach (string file in Directory.EnumerateFiles(channel.UpdateDirectory, "*", SearchOption.AllDirectories)) {
                    try {
                        string relativePath = Path.GetRelativePath(channel.UpdateDirectory, file)
                            .Replace(Path.DirectorySeparatorChar, '/')
                            .Replace(Path.AltDirectorySeparatorChar, '/');
                        if (!ShouldIncludeManifestFile(relativePath, channel.Id))
                            continue;
                        var info = new FileInfo(file);
                        bytes += info.Length;
                        count++;
                    } catch {
                    }
                }
            }
            lastUpdates.TryGetValue(channel.Id, out LastUpdateRecord? lastUpdate);
            channels.Add(new UpdateChannel {
                Id = channel.Id,
                DisplayName = channel.DisplayName,
                UpdateDirectory = channel.UpdateDirectory,
                PublicPath = channel.PublicPath,
                FileCount = count,
                ByteCount = bytes,
                ManagedProcessName = GetManagedProcessName(channel),
                ManagedExecutablePath = GetManagedExecutablePath(channel),
                IsProcessManaged = IsProcessManaged(channel),
                IsProcessRunning = IsProcessRunning(channel),
                ManagedProcessIds = GetManagedProcessIds(channel),
                AutoStartAfterUpdate = channel.AutoStartAfterUpdate,
                LastUpdateUtc = FirstNonEmpty(lastUpdate?.LastUpdateUtc, channel.LastUpdateUtc),
                IsDynamic = channel.IsDynamic,
                IsStartupEnabled = IsWindowsStartupEnabled(channel)
            });
        }
        return channels;
    }

    public void ConfigureAdmin(string username, string password) {
        if (string.IsNullOrWhiteSpace(username))
            throw new InvalidOperationException("Username is required.");
        if (string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Password is required.");

        byte[] salt = RandomBytes(16);
        _auth = new AuthState {
            UserName = username.Trim(),
            PasswordSalt = Convert.ToBase64String(salt),
            PasswordHash = HashPassword(password, salt),
            TokenHash = "",
            TokenExpiresUtc = ""
        };
        SaveAuth();
        Status = "Admin login configured for " + _auth.UserName;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public ProcessActionResult StartChannelProcess(string channelId) {
        UpdateChannel channel = RequireManagedChannel(channelId);
        ProcessActionResult result = StartManagedProcess(channel);
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public ProcessActionResult StopChannelProcess(string channelId) {
        UpdateChannel channel = RequireManagedChannel(channelId);
        ProcessActionResult result = StopManagedProcess(channel);
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public ProcessActionResult RestartChannelProcess(string channelId) {
        UpdateChannel channel = RequireManagedChannel(channelId);
        ProcessActionResult stop = StopManagedProcess(channel);
        ProcessActionResult start = StartManagedProcess(channel);
        var result = new ProcessActionResult {
            Ok = stop.Ok && start.Ok,
            Message = stop.Message + " " + start.Message,
            ProcessId = start.ProcessId
        };
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public ProcessActionResult AddChannelToWindowsStartup(string channelId) {
        UpdateChannel channel = RequireManagedChannel(channelId);
        string exePath = GetManagedExecutablePath(channel);
        if (!File.Exists(exePath))
            return FailProcessAction(channel.DisplayName + " executable was not found: " + exePath);

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(WindowsRunKeyPath, true) ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");
        key.SetValue(GetStartupValueName(channel), QuoteCommandPath(exePath), RegistryValueKind.String);

        var result = new ProcessActionResult { Ok = true, Message = channel.DisplayName + " added to Windows startup." };
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public ProcessActionResult RemoveDynamicChannel(string channelId) {
        string normalizedId = UpdateServerOptions.NormalizeChannelId(channelId);
        int index = _options.Channels.FindIndex(channel => string.Equals(UpdateServerOptions.NormalizeChannelId(channel.Id), normalizedId, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return FailProcessAction("Update channel was not found: " + channelId);

        UpdateChannel channel = _options.Channels[index];
        if (UpdateServerOptions.IsDefaultChannelId(channel.Id))
            return FailProcessAction(channel.DisplayName + " is a default update item and cannot be removed.");
        if (!channel.IsDynamic)
            return FailProcessAction(channel.DisplayName + " is configured by the server and cannot be removed from dynamicUpdates.json.");

        _options.Channels.RemoveAt(index);
        SaveDynamicChannelSettings();

        var result = new ProcessActionResult {
            Ok = true,
            Message = channel.DisplayName + " removed from update items. Files and running processes were left untouched."
        };
        Status = result.Message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    public void SetChannelAutoStart(string channelId, bool autoStart) {
        UpdateChannel? channel = FindChannel(channelId);
        if (channel == null)
            throw new InvalidOperationException("Update channel was not found: " + channelId);

        channel.AutoStartAfterUpdate = autoStart;
        SaveDynamicChannelSettings();
        Status = channel.DisplayName + " auto-run " + (autoStart ? "enabled" : "disabled") + ".";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetAllManagedChannelAutoStart(bool autoStart) {
        foreach (UpdateChannel channel in _options.Channels) {
            if (IsProcessManaged(channel))
                channel.AutoStartAfterUpdate = autoStart;
        }
        SaveDynamicChannelSettings();
        Status = "Auto-run " + (autoStart ? "enabled" : "disabled") + " for managed channels.";
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ConfigurePublicForwarding() {
        if (_options.PublicForwarding?.Enabled == true) {
            _options.PublicForwarding.Enabled = false;
            _publicForwardingError = "Public forwarding is disabled in SocketJack.Update. SocketJack-MagicMasterList owns public ports 80 and 443.";
            AppendLog("public-forward", _publicForwardingError);
        }

        _publicHttpServer = null;
        _publicHttpsServer = null;
    }

    private static Dictionary<string, List<ForwardRoute>> BuildForwardRoutes(PublicForwardingOptions forwarding) {
        var routes = new Dictionary<string, List<ForwardRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (PublicForwardRouteOptions route in forwarding.Routes) {
            if (!Uri.TryCreate(route.TargetUrl, UriKind.Absolute, out Uri? target) ||
                (!target.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                 !target.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) {
                continue;
            }

            var forwardRoute = new ForwardRoute(
                target,
                route.PreserveHostHeader,
                route.ServiceName,
                route.OfflineSummary,
                UpdateServerOptions.NormalizeForwardPathPrefix(route.PathPrefix));
            foreach (string hostName in route.HostNames.Select(UpdateServerOptions.NormalizeHostName)) {
                if (string.IsNullOrWhiteSpace(hostName))
                    continue;
                if (!routes.TryGetValue(hostName, out List<ForwardRoute>? hostRoutes)) {
                    hostRoutes = new List<ForwardRoute>();
                    routes[hostName] = hostRoutes;
                }
                hostRoutes.Add(forwardRoute);
            }
        }

        foreach (List<ForwardRoute> hostRoutes in routes.Values)
            hostRoutes.Sort((left, right) => right.PathPrefix.Length.CompareTo(left.PathPrefix.Length));
        return routes;
    }

    private static Dictionary<string, X509Certificate2> LoadPublicForwardingCertificates(PublicForwardingOptions forwarding) {
        var certificates = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
        foreach (PublicCertificateOptions certificateOptions in forwarding.Certificates) {
            if (!certificateOptions.Enabled)
                continue;

            string subject = FirstNonEmpty(certificateOptions.CertificateSubject, certificateOptions.HostNames.FirstOrDefault());
            X509Certificate2 certificate = LoadSslCertificate(
                certificateOptions.CertificatePath,
                certificateOptions.CertificateKeyPath,
                ResolveCertificatePassword(certificateOptions.CertificatePath, certificateOptions.CertificateKeyPath, certificateOptions.CertificatePassword, certificateOptions.CertificatePasswordPath),
                subject,
                "public forwarding certificate for " + FirstNonEmpty(subject, "configured host"));

            foreach (string hostName in certificateOptions.HostNames.Select(UpdateServerOptions.NormalizeHostName)) {
                if (!string.IsNullOrWhiteSpace(hostName))
                    certificates[hostName] = certificate;
            }
        }

        return certificates;
    }

    private static X509Certificate2 ResolveDefaultForwardingCertificate(PublicForwardingOptions forwarding, Dictionary<string, X509Certificate2> certificates) {
        string defaultHost = UpdateServerOptions.NormalizeHostName(forwarding.DefaultCertificateHost);
        if (!string.IsNullOrWhiteSpace(defaultHost) && certificates.TryGetValue(defaultHost, out X509Certificate2? certificate))
            return certificate;
        if (certificates.Count > 0)
            return certificates.Values.First();
        throw new InvalidOperationException("Public forwarding requires at least one configured certificate.");
    }

    private void MapRoutes() {
        MapPublicRoute("GET", "/", (connection, request, cancellationToken) => Json(request, new {
            service = "SocketJack Update",
            ok = true,
            channels = GetChannelSnapshots().Select(c => new {
                c.Id,
                c.DisplayName,
                manifest = CombineUrl(_options.PublicUrl, GetChannelPublicPath(c).TrimStart('/') + "/meta")
            }).ToArray()
        }));
        MapPublicRoute("GET", "/healthz", (connection, request, cancellationToken) => Text(request, "OK"));
        MapPublicRoute("POST", "/api/socketjack/oauth/token", (connection, request, cancellationToken) => HandleToken(request));
        MapPublicRoute("GET", "/api/socketjack/auth/session", (connection, request, cancellationToken) => HandleSession(request));
        MapPublicRoute("POST", "/api/socketjack/oauth/introspect", (connection, request, cancellationToken) => HandleIntrospect(request));
        MapPublicRoute("GET", "/api/update/channels", (connection, request, cancellationToken) => Json(request, new { ok = true, channels = GetChannelSnapshots() }));
        MapPublicRoute("POST", "/api/update/channels/remove", (connection, request, cancellationToken) => HandleRemoveChannel(request));
        MapPublicRoute("DELETE", "/api/update/channels/*", (connection, request, cancellationToken) => HandleRemoveChannel(request));
        MapPublicRoute("POST", "/api/update/publish/begin", (connection, request, cancellationToken) => HandlePublishBegin(request));
        MapPublicRoute("POST", "/api/update/publish/file", (connection, request, cancellationToken) => HandlePublishFile(request));
        MapPublicRoute("POST", "/api/update/publish/archive", (connection, request, cancellationToken) => HandlePublishArchive(request));
        MapPublicRoute("POST", "/api/update/publish/archive-binary", (connection, request, cancellationToken) => HandlePublishArchiveBinary(request));
        MapPublicRoute("POST", "/api/update/publish/complete", (connection, request, cancellationToken) => HandlePublishComplete(request));
        MapPublicRoute("POST", "/api/update/publish/abort", (connection, request, cancellationToken) => HandlePublishAbort(request));
        MapPublicRoute("GET", "/update", (connection, request, cancellationToken) => HandleUpdateRoot(request));
        MapPublicRoute("GET", "/update/", (connection, request, cancellationToken) => HandleUpdateRoot(request));
        MapPublicRoute("GET", "/update/*", (connection, request, cancellationToken) => HandleUpdateDownload(request));

        foreach (string path in new[] {
            "/api/socketjack/oauth/token",
            "/api/socketjack/auth/session",
            "/api/socketjack/oauth/introspect",
            "/api/update/channels",
            "/api/update/channels/remove",
            "/api/update/channels/*",
            "/api/update/publish/begin",
            "/api/update/publish/file",
            "/api/update/publish/archive",
            "/api/update/publish/archive-binary",
            "/api/update/publish/complete",
            "/api/update/publish/abort",
            "/update",
            "/update/",
            "/update/*"
        }) {
            MapPublicRoute("OPTIONS", path, (connection, request, cancellationToken) => NoContent(request));
        }
    }

    private void MapPublicRoute(string method, string path, MutableTcpServer.RouteHandler handler) {
        foreach (string routePath in GetPublicRoutePaths(path))
            _server.Map(method, routePath, handler);
    }

    private IEnumerable<string> GetPublicRoutePaths(string path) {
        string normalizedPath = NormalizeRoutePath(path);
        var paths = new List<string> { normalizedPath };
        string basePath = PublicBasePath;
        if (!string.IsNullOrWhiteSpace(basePath)) {
            string prefixedPath = normalizedPath == "/"
                ? basePath
                : basePath + normalizedPath;
            paths.Add(NormalizeRoutePath(prefixedPath));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private string HandleToken(HttpRequest request) {
        if (!HasAdminLogin)
            return Json(request, new { error = "server_not_configured", error_description = "Configure the SocketJack Update admin login in the WPF app first." }, 503, "Service Unavailable");

        if (!TryParseBody(request, out JsonDocument? document, out string parseError) || document == null)
            return Json(request, new { error = "invalid_request", error_description = parseError }, 400, "Bad Request");
        using (document) {
            JsonElement root = document.RootElement;
            string grantType = FirstNonEmpty(GetJsonString(root, "grant_type"), GetJsonString(root, "grantType"), "password");
            if (!grantType.Equals("password", StringComparison.OrdinalIgnoreCase))
                return Json(request, new { error = "unsupported_grant_type" }, 400, "Bad Request");

            string username = FirstNonEmpty(GetJsonString(root, "username"), GetJsonString(root, "user"), GetJsonString(root, "login"));
            string password = FirstNonEmpty(GetJsonString(root, "password"), GetJsonString(root, "pass"));
            if (!ValidatePassword(username, password))
                return Json(request, new { error = "invalid_grant", error_description = "Invalid SocketJack Update login." }, 401, "Unauthorized");

            string token = GenerateToken();
            _auth.TokenHash = HashToken(token);
            _auth.TokenExpiresUtc = DateTimeOffset.UtcNow.Add(TokenLifetime).ToString("O", CultureInfo.InvariantCulture);
            SaveAuth();
            return Json(request, new {
                access_token = token,
                token_type = "Bearer",
                expires_in = (int)TokenLifetime.TotalSeconds,
                username = _auth.UserName,
                isAdministrator = true
            });
        }
    }

    private string HandleSession(HttpRequest request) {
        AuthState? account = Authenticate(request);
        return Json(request, new {
            ok = true,
            authenticated = account != null,
            username = account?.UserName ?? "",
            isAdministrator = account != null,
            configured = HasAdminLogin
        });
    }

    private string HandleIntrospect(HttpRequest request) {
        AuthState? account = Authenticate(request);
        return Json(request, new {
            active = account != null,
            username = account?.UserName ?? "",
            isAdministrator = account != null,
            token_type = account == null ? "" : "Bearer",
            exp = account == null ? 0 : ParseUnixTime(account.TokenExpiresUtc)
        });
    }

    private string HandlePublishBegin(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        using JsonDocument document = ParseBody(request);
        JsonElement root = document.RootElement;
        string channelId = UpdateServerOptions.NormalizeChannelId(FirstNonEmpty(GetJsonString(root, "channel"), GetJsonString(root, "channelId")));
        if (!TryGetOrCreatePublishChannel(channelId, root, out UpdateChannel? channel, out string channelError, out bool channelChanged) || channel == null)
            return Json(request, new { ok = false, error = channelError, channel = channelId }, 404, "Not Found");
        if (!TryApplyRequestedUpdateDirectory(channel, root, out string updateDirectoryError, out bool updateDirectoryChanged))
            return Json(request, new { ok = false, error = updateDirectoryError, channel = channel.Id }, 400, "Bad Request");
        if (ApplyRequestedChannelDetails(channel, root))
            channelChanged = true;
        if (channelChanged || updateDirectoryChanged)
            SaveDynamicChannelSettings();

        string uploadId = "sjupd_" + Guid.NewGuid().ToString("N");
        string stagingRoot = Path.Combine(_options.UploadStagingDirectory, uploadId);
        Directory.CreateDirectory(stagingRoot);
        bool hasDesiredPaths = TryGetJsonStringSet(root, "files", out HashSet<string> desiredPaths) ||
                               TryGetJsonStringSet(root, "paths", out desiredPaths);
        CopyExistingUpdateFiles(channel.UpdateDirectory, stagingRoot, hasDesiredPaths ? desiredPaths : null, channel.Id);
        var session = new UploadSession {
            Id = uploadId,
            ChannelId = channel.Id,
            StagingRoot = stagingRoot,
            ArchivePath = Path.Combine(_options.UploadStagingDirectory, uploadId + ".zip"),
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        lock (_sync)
            _uploads[uploadId] = session;

        LogPublish(uploadId, channel.Id, "begin updateDirectory=\"" + channel.UpdateDirectory + "\" stagingRoot=\"" + stagingRoot + "\" desiredPaths=" + (hasDesiredPaths ? desiredPaths.Count.ToString(CultureInfo.InvariantCulture) : "all") + (updateDirectoryChanged ? " requestedUpdateDirectoryApplied=true" : ""));
        Status = "Receiving " + channel.DisplayName + " publish " + uploadId;
        StatusChanged?.Invoke(this, EventArgs.Empty);
        return Json(request, new {
            ok = true,
            uploadId,
            channel = channel.Id,
            updateDirectory = channel.UpdateDirectory,
            stagingRoot,
            capabilities = new {
                archiveJsonChunks = true,
                archiveBinaryChunks = true
            }
        });
    }

    private string HandlePublishFile(HttpRequest request) {
        try {
            return HandlePublishFileCore(request);
        } catch (Exception ex) {
            (string uploadId, string path) = ReadUploadDebugFields(request);
            return Json(request, new {
                ok = false,
                error = "Update file upload failed.",
                detail = ex.Message,
                exception = ex.GetType().Name,
                uploadId,
                path,
                bodyLength = request.Body?.Length ?? 0,
                contentLength = request.Headers.TryGetValue("Content-Length", out string? contentLength) ? contentLength : ""
            }, 500, "Internal Server Error");
        }
    }

    private string HandlePublishFileCore(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        using JsonDocument document = ParseBody(request);
        JsonElement root = document.RootElement;
        string uploadId = FirstNonEmpty(GetJsonString(root, "uploadId"), GetJsonString(root, "id"));
        if (!TryGetUpload(uploadId, out UploadSession? session, out string error) || session == null)
            return Json(request, new { ok = false, error }, 404, "Not Found");

        string relativePath = NormalizeRelativePath(FirstNonEmpty(GetJsonString(root, "path"), GetJsonString(root, "relativePath")));
        if (!IsAllowedUpdateFile(relativePath, session.ChannelId, out error))
            return Json(request, new { ok = false, error }, 400, "Bad Request");

        long offset = Math.Max(0, GetJsonLong(root, "offset", 0));
        long totalLength = Math.Max(0, GetJsonLong(root, "totalLength", GetJsonLong(root, "length", 0)));
        bool finalChunk = GetJsonBool(root, "finalChunk", GetJsonBool(root, "final", false));
        bool allowOlderReplacement = GetJsonBool(root, "allowOlderReplacement", GetJsonBool(root, "forceOlder", false));
        string expectedSha256 = GetJsonString(root, "sha256").Trim().ToLowerInvariant();
        string publisherDateText = FirstNonEmpty(GetJsonString(root, "lastWriteUtc"), GetJsonString(root, "modifiedUtc"), GetJsonString(root, "modified"));
        bool hasPublisherLastWriteUtc = TryParseUtc(publisherDateText, out DateTimeOffset publisherLastWriteUtc);
        string contentBase64 = FirstNonEmpty(GetJsonString(root, "contentBase64"), GetJsonString(root, "content"));
        byte[] content;
        try {
            content = Convert.FromBase64String(contentBase64);
        } catch {
            return Json(request, new { ok = false, error = "Chunk payload is not valid base64." }, 400, "Bad Request");
        }

        lock (session.Sync) {
            string fullPath = ResolveChildPath(session.StagingRoot, relativePath);
            UpdateChannel? channel = FindChannel(session.ChannelId);
            string serverPath = channel == null ? "" : ResolveChildPath(channel.UpdateDirectory, relativePath);
            if (offset == 0 && !string.IsNullOrWhiteSpace(serverPath) && File.Exists(serverPath)) {
                if (string.IsNullOrWhiteSpace(expectedSha256))
                    return Json(request, new { ok = false, error = "Publisher file hash is required for update comparison.", path = relativePath }, 400, "Bad Request");
                if (!hasPublisherLastWriteUtc)
                    return Json(request, new { ok = false, error = "Publisher file last-write date is required for update comparison.", path = relativePath }, 400, "Bad Request");

                string serverSha256 = ComputeSha256Hex(serverPath);
                DateTimeOffset serverLastWriteUtc = GetFileLastWriteUtc(serverPath);
                if (ConstantTimeEquals(serverSha256, expectedSha256))
                    return SkipPublishFile(request, session, uploadId, relativePath, totalLength, "same-hash", serverPath, serverSha256, serverLastWriteUtc, publisherLastWriteUtc);
                if (ServerFileIsNewerThanPublisher(serverLastWriteUtc, publisherLastWriteUtc) && !allowOlderReplacement)
                    return SkipPublishFile(request, session, uploadId, relativePath, totalLength, "publisher-not-newer", serverPath, serverSha256, serverLastWriteUtc, publisherLastWriteUtc);
            }

            RemoveSkippedFile(session, relativePath, totalLength);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? session.StagingRoot);
            using (var stream = new FileStream(fullPath, offset == 0 ? FileMode.Create : FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (stream.Length != offset)
                    return Json(request, new { ok = false, error = "Chunk offset mismatch.", expectedOffset = stream.Length, receivedOffset = offset }, 409, "Conflict");
                stream.Position = offset;
                stream.Write(content, 0, content.Length);
                if (finalChunk)
                    stream.SetLength(offset + content.Length);
            }

            long writtenLength = new FileInfo(fullPath).Length;
            if (finalChunk) {
                if (writtenLength != totalLength)
                    return Json(request, new { ok = false, error = "Uploaded file length mismatch.", writtenLength, totalLength }, 409, "Conflict");
                if (!string.IsNullOrWhiteSpace(expectedSha256) && !ConstantTimeEquals(ComputeSha256Hex(fullPath), expectedSha256))
                    return Json(request, new { ok = false, error = "Uploaded file hash mismatch.", path = relativePath }, 409, "Conflict");
                if (hasPublisherLastWriteUtc)
                    File.SetLastWriteTimeUtc(fullPath, publisherLastWriteUtc.UtcDateTime);
                session.FileCount++;
                session.ByteCount += writtenLength;
            }
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            return Json(request, new { ok = true, uploadId, path = relativePath, writtenLength, complete = finalChunk, skipped = false, olderReplacement = allowOlderReplacement, session.FileCount, session.ByteCount, session.SkippedFileCount, session.SkippedByteCount });
        }
    }

    private string SkipPublishFile(HttpRequest request, UploadSession session, string uploadId, string relativePath, long totalLength, string reason, string serverPath, string serverSha256, DateTimeOffset serverLastWriteUtc, DateTimeOffset publisherLastWriteUtc) {
        CopyServerFileToStaging(session, relativePath, serverPath, serverLastWriteUtc);
        if (session.SkippedPaths.Add(relativePath)) {
            session.SkippedFileCount++;
            session.SkippedByteCount += totalLength;
        }
        session.UpdatedUtc = DateTimeOffset.UtcNow;
        return Json(request, new {
            ok = true,
            uploadId,
            path = relativePath,
            skipped = true,
            complete = true,
            reason,
            needsPublisherDecision = reason.Equals("publisher-not-newer", StringComparison.OrdinalIgnoreCase),
            serverSha256,
            serverLastWriteUtc = serverLastWriteUtc.ToString("O", CultureInfo.InvariantCulture),
            publisherLastWriteUtc = publisherLastWriteUtc.ToString("O", CultureInfo.InvariantCulture),
            session.FileCount,
            session.ByteCount,
            session.SkippedFileCount,
            session.SkippedByteCount
        });
    }

    private static void CopyServerFileToStaging(UploadSession session, string relativePath, string serverPath, DateTimeOffset serverLastWriteUtc) {
        if (string.IsNullOrWhiteSpace(serverPath) || !File.Exists(serverPath))
            return;
        string stagedPath = ResolveChildPath(session.StagingRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(stagedPath) ?? session.StagingRoot);
        File.Copy(serverPath, stagedPath, true);
        File.SetLastWriteTimeUtc(stagedPath, serverLastWriteUtc.UtcDateTime);
    }

    private static void RemoveSkippedFile(UploadSession session, string relativePath, long totalLength) {
        if (!session.SkippedPaths.Remove(relativePath))
            return;
        session.SkippedFileCount = Math.Max(0, session.SkippedFileCount - 1);
        session.SkippedByteCount = Math.Max(0, session.SkippedByteCount - totalLength);
    }

    private string HandlePublishFileBinary(HttpRequest request) {
        try {
            return HandlePublishFileBinaryCore(request);
        } catch (Exception ex) {
            return Json(request, new {
                ok = false,
                error = "Binary update file upload failed.",
                detail = ex.Message,
                exception = ex.GetType().Name,
                uploadId = Query(request, "uploadId"),
                path = Query(request, "path"),
                bodyLength = request.BodyBytes?.LongLength ?? 0,
                contentLength = request.Headers.TryGetValue("Content-Length", out string? contentLength) ? contentLength : ""
            }, 500, "Internal Server Error");
        }
    }

    private string HandlePublishFileBinaryCore(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        string uploadId = FirstNonEmpty(Query(request, "uploadId"), Query(request, "id"));
        if (!TryGetUpload(uploadId, out UploadSession? session, out string error) || session == null)
            return Json(request, new { ok = false, error }, 404, "Not Found");

        string relativePath = NormalizeRelativePath(FirstNonEmpty(Query(request, "path"), Query(request, "relativePath")));
        if (!IsAllowedUpdateFile(relativePath, session.ChannelId, out error))
            return Json(request, new { ok = false, error }, 400, "Bad Request");

        byte[] content = request.BodyBytes ?? Array.Empty<byte>();
        long totalLength = Math.Max(0, ParseLong(Query(request, "totalLength"), content.LongLength));
        bool allowOlderReplacement = ParseBool(Query(request, "allowOlderReplacement"), false) || ParseBool(Query(request, "forceOlder"), false);
        string expectedSha256 = Query(request, "sha256").Trim().ToLowerInvariant();
        string publisherDateText = FirstNonEmpty(Query(request, "lastWriteUtc"), Query(request, "modifiedUtc"), Query(request, "modified"));
        bool hasPublisherLastWriteUtc = TryParseUtc(publisherDateText, out DateTimeOffset publisherLastWriteUtc);

        if (content.LongLength != totalLength)
            return Json(request, new { ok = false, error = "Uploaded file length mismatch.", writtenLength = content.LongLength, totalLength }, 409, "Conflict");

        lock (session.Sync) {
            string fullPath = ResolveChildPath(session.StagingRoot, relativePath);
            UpdateChannel? channel = FindChannel(session.ChannelId);
            string serverPath = channel == null ? "" : ResolveChildPath(channel.UpdateDirectory, relativePath);
            if (!string.IsNullOrWhiteSpace(serverPath) && File.Exists(serverPath)) {
                if (string.IsNullOrWhiteSpace(expectedSha256))
                    return Json(request, new { ok = false, error = "Publisher file hash is required for update comparison.", path = relativePath }, 400, "Bad Request");
                if (!hasPublisherLastWriteUtc)
                    return Json(request, new { ok = false, error = "Publisher file last-write date is required for update comparison.", path = relativePath }, 400, "Bad Request");

                string serverSha256 = ComputeSha256Hex(serverPath);
                DateTimeOffset serverLastWriteUtc = GetFileLastWriteUtc(serverPath);
                if (ConstantTimeEquals(serverSha256, expectedSha256))
                    return SkipPublishFile(request, session, uploadId, relativePath, totalLength, "same-hash", serverPath, serverSha256, serverLastWriteUtc, publisherLastWriteUtc);
                if (ServerFileIsNewerThanPublisher(serverLastWriteUtc, publisherLastWriteUtc) && !allowOlderReplacement)
                    return SkipPublishFile(request, session, uploadId, relativePath, totalLength, "publisher-not-newer", serverPath, serverSha256, serverLastWriteUtc, publisherLastWriteUtc);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256)) {
                string contentSha256 = ToHex(SHA256.HashData(content));
                if (!ConstantTimeEquals(contentSha256, expectedSha256))
                    return Json(request, new { ok = false, error = "Uploaded file hash mismatch.", path = relativePath }, 409, "Conflict");
            }

            RemoveSkippedFile(session, relativePath, totalLength);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? session.StagingRoot);
            File.WriteAllBytes(fullPath, content);
            if (hasPublisherLastWriteUtc)
                File.SetLastWriteTimeUtc(fullPath, publisherLastWriteUtc.UtcDateTime);

            session.FileCount++;
            session.ByteCount += totalLength;
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            return Json(request, new { ok = true, uploadId, path = relativePath, writtenLength = totalLength, complete = true, skipped = false, olderReplacement = allowOlderReplacement, session.FileCount, session.ByteCount, session.SkippedFileCount, session.SkippedByteCount });
        }
    }

    private string HandlePublishArchive(HttpRequest request) {
        try {
            return HandlePublishArchiveCore(request);
        } catch (Exception ex) {
            (string uploadId, string path) = ReadUploadDebugFields(request);
            return Json(request, new {
                ok = false,
                error = "Update archive upload failed.",
                detail = ex.Message,
                exception = ex.GetType().Name,
                uploadId,
                path,
                bodyLength = request.Body?.Length ?? 0,
                contentLength = request.Headers.TryGetValue("Content-Length", out string? contentLength) ? contentLength : ""
            }, 500, "Internal Server Error");
        }
    }

    private string HandlePublishArchiveBinary(HttpRequest request) {
        try {
            return HandlePublishArchiveBinaryCore(request);
        } catch (Exception ex) {
            return Json(request, new {
                ok = false,
                error = "Binary update archive upload failed.",
                detail = ex.Message,
                exception = ex.GetType().Name,
                uploadId = Query(request, "uploadId"),
                bodyLength = request.BodyBytes?.LongLength ?? 0,
                contentLength = request.Headers.TryGetValue("Content-Length", out string? contentLength) ? contentLength : ""
            }, 500, "Internal Server Error");
        }
    }

    private string HandlePublishArchiveBinaryCore(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        string uploadId = FirstNonEmpty(Query(request, "uploadId"), Query(request, "id"));
        if (!TryGetUpload(uploadId, out UploadSession? session, out string error) || session == null)
            return Json(request, new { ok = false, error }, 404, "Not Found");

        byte[] content = request.BodyBytes ?? Array.Empty<byte>();
        bool hasChunkFields = HasQueryValue(request, "offset") || HasQueryValue(request, "finalChunk") || HasQueryValue(request, "final");
        long offset = Math.Max(0, ParseLong(Query(request, "offset"), 0));
        long totalLength = Math.Max(0, ParseLong(Query(request, "totalLength"), ParseLong(Query(request, "length"), content.LongLength)));
        bool finalChunk = hasChunkFields
            ? ParseBool(Query(request, "finalChunk"), ParseBool(Query(request, "final"), false))
            : true;
        string expectedSha256 = Query(request, "sha256").Trim().ToLowerInvariant();
        int expectedFileCount = (int)Math.Max(0, ParseLong(Query(request, "fileCount"), 0));
        long expectedByteCount = Math.Max(0, ParseLong(Query(request, "byteCount"), 0));

        return AcceptPublishArchiveChunk(request, session, uploadId, content, offset, totalLength, finalChunk, expectedSha256, expectedFileCount, expectedByteCount, "archive-binary");
    }

    private string HandlePublishArchiveCore(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        using JsonDocument document = ParseBody(request);
        JsonElement root = document.RootElement;
        string uploadId = FirstNonEmpty(GetJsonString(root, "uploadId"), GetJsonString(root, "id"));
        if (!TryGetUpload(uploadId, out UploadSession? session, out string error) || session == null)
            return Json(request, new { ok = false, error }, 404, "Not Found");

        long offset = Math.Max(0, GetJsonLong(root, "offset", 0));
        long totalLength = Math.Max(0, GetJsonLong(root, "totalLength", GetJsonLong(root, "length", 0)));
        bool finalChunk = GetJsonBool(root, "finalChunk", GetJsonBool(root, "final", false));
        string expectedSha256 = GetJsonString(root, "sha256").Trim().ToLowerInvariant();
        int expectedFileCount = (int)Math.Max(0, GetJsonLong(root, "fileCount", 0));
        long expectedByteCount = Math.Max(0, GetJsonLong(root, "byteCount", 0));
        string contentBase64 = FirstNonEmpty(GetJsonString(root, "contentBase64"), GetJsonString(root, "content"));
        byte[] content;
        try {
            content = Convert.FromBase64String(contentBase64);
        } catch {
            return Json(request, new { ok = false, error = "Archive chunk payload is not valid base64." }, 400, "Bad Request");
        }

        return AcceptPublishArchiveChunk(request, session, uploadId, content, offset, totalLength, finalChunk, expectedSha256, expectedFileCount, expectedByteCount, "archive");
    }

    private string AcceptPublishArchiveChunk(HttpRequest request, UploadSession session, string uploadId, byte[] content, long offset, long totalLength, bool finalChunk, string expectedSha256, int expectedFileCount, long expectedByteCount, string logKind) {
        if (content == null)
            content = Array.Empty<byte>();

        long chunkEnd = offset + content.LongLength;
        if (offset < 0 || chunkEnd < offset)
            return Json(request, new { ok = false, error = "Archive chunk offset is invalid.", receivedOffset = offset }, 400, "Bad Request");
        if (totalLength > 0 && chunkEnd > totalLength)
            return Json(request, new { ok = false, error = "Archive chunk exceeds declared length.", receivedEnd = chunkEnd, totalLength }, 409, "Conflict");
        if (finalChunk && totalLength > 0 && chunkEnd != totalLength)
            return Json(request, new { ok = false, error = "Final archive chunk does not end at declared length.", receivedEnd = chunkEnd, totalLength }, 409, "Conflict");

        lock (session.Sync) {
            if (session.ArchiveExtracted) {
                if (totalLength > 0 && session.ArchiveLength > 0 && totalLength != session.ArchiveLength)
                    return Json(request, new { ok = false, error = "Archive was already extracted with a different length.", archiveLength = session.ArchiveLength, totalLength }, 409, "Conflict");
                if (expectedFileCount > 0 && expectedFileCount != session.ArchiveExtractedFiles)
                    return Json(request, new { ok = false, error = "Archive file count mismatch.", expectedFileCount, extractedFiles = session.ArchiveExtractedFiles }, 409, "Conflict");
                if (expectedByteCount > 0 && expectedByteCount != session.ArchiveExtractedBytes)
                    return Json(request, new { ok = false, error = "Archive byte count mismatch.", expectedByteCount, extractedBytes = session.ArchiveExtractedBytes }, 409, "Conflict");
                return Json(request, new { ok = true, uploadId, archiveLength = session.ArchiveLength, complete = true, duplicate = true, extractedFiles = session.ArchiveExtractedFiles, extractedBytes = session.ArchiveExtractedBytes, session.FileCount, session.ByteCount });
            }

            Directory.CreateDirectory(Path.GetDirectoryName(session.ArchivePath) ?? _options.UploadStagingDirectory);
            long writtenLength;
            bool duplicateChunk = false;
            using (var stream = new FileStream(session.ArchivePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (stream.Length < offset)
                    return Json(request, new { ok = false, error = "Archive chunk offset mismatch.", expectedOffset = stream.Length, receivedOffset = offset }, 409, "Conflict");

                if (stream.Length > offset) {
                    if (stream.Length >= chunkEnd && FileRangeMatches(stream, offset, content)) {
                        duplicateChunk = true;
                        writtenLength = stream.Length;
                    } else {
                        return Json(request, new { ok = false, error = "Archive chunk offset mismatch.", expectedOffset = stream.Length, receivedOffset = offset }, 409, "Conflict");
                    }
                } else {
                    stream.Position = offset;
                    stream.Write(content, 0, content.Length);
                    writtenLength = stream.Length;
                }

                if (finalChunk) {
                    stream.SetLength(chunkEnd);
                    writtenLength = chunkEnd;
                }
            }

            if (!finalChunk) {
                session.UpdatedUtc = DateTimeOffset.UtcNow;
                return Json(request, new { ok = true, uploadId, archiveLength = writtenLength, complete = false, duplicate = duplicateChunk });
            }

            if (totalLength <= 0)
                totalLength = writtenLength;
            if (writtenLength != totalLength)
                return Json(request, new { ok = false, error = "Uploaded archive length mismatch.", writtenLength, totalLength }, 409, "Conflict");
            if (!string.IsNullOrWhiteSpace(expectedSha256) && !ConstantTimeEquals(ComputeSha256Hex(session.ArchivePath), expectedSha256))
                return Json(request, new { ok = false, error = "Uploaded archive hash mismatch." }, 409, "Conflict");

            (int extractedFiles, long extractedBytes) = ExtractArchiveToStaging(session);
            TryDeleteFile(session.ArchivePath);
            if (expectedFileCount > 0 && expectedFileCount != extractedFiles)
                return Json(request, new { ok = false, error = "Archive file count mismatch.", expectedFileCount, extractedFiles }, 409, "Conflict");
            if (expectedByteCount > 0 && expectedByteCount != extractedBytes)
                return Json(request, new { ok = false, error = "Archive byte count mismatch.", expectedByteCount, extractedBytes }, 409, "Conflict");

            session.FileCount += extractedFiles;
            session.ByteCount += extractedBytes;
            session.ArchiveExtracted = true;
            session.ArchiveLength = totalLength;
            session.ArchiveSha256 = expectedSha256;
            session.ArchiveExtractedFiles = extractedFiles;
            session.ArchiveExtractedBytes = extractedBytes;
            session.UpdatedUtc = DateTimeOffset.UtcNow;
            LogPublish(uploadId, session.ChannelId, logKind + " extracted files=" + extractedFiles.ToString(CultureInfo.InvariantCulture) + " bytes=" + extractedBytes.ToString(CultureInfo.InvariantCulture) + " archiveBytes=" + totalLength.ToString(CultureInfo.InvariantCulture) + " stagingRoot=\"" + session.StagingRoot + "\"");
            return Json(request, new { ok = true, uploadId, archiveLength = writtenLength, complete = true, duplicate = duplicateChunk, extractedFiles, extractedBytes, session.FileCount, session.ByteCount });
        }
    }

    private static (int FileCount, long ByteCount) ExtractArchiveToStaging(UploadSession session) {
        int fileCount = 0;
        long byteCount = 0;
        using ZipArchive archive = ZipFile.OpenRead(session.ArchivePath);
        foreach (ZipArchiveEntry entry in archive.Entries) {
            if (string.IsNullOrWhiteSpace(entry.Name))
                continue;

            string relativePath = NormalizeRelativePath(entry.FullName);
            if (!IsAllowedUpdateFile(relativePath, session.ChannelId, out string error))
                throw new InvalidOperationException("Archive entry is not allowed: " + relativePath + ". " + error);

            string destinationPath = ResolveChildPath(session.StagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? session.StagingRoot);
            entry.ExtractToFile(destinationPath, true);
            File.SetLastWriteTimeUtc(destinationPath, entry.LastWriteTime.UtcDateTime);
            var info = new FileInfo(destinationPath);
            fileCount++;
            byteCount += info.Length;
        }
        return (fileCount, byteCount);
    }

    private static (string UploadId, string Path) ReadUploadDebugFields(HttpRequest request) {
        try {
            using JsonDocument document = ParseBody(request);
            JsonElement root = document.RootElement;
            return (
                FirstNonEmpty(GetJsonString(root, "uploadId"), GetJsonString(root, "id")),
                FirstNonEmpty(GetJsonString(root, "path"), GetJsonString(root, "relativePath"))
            );
        } catch {
            return ("", "");
        }
    }

    private string HandlePublishComplete(HttpRequest request) {
        try {
            return HandlePublishCompleteCore(request);
        } catch (Exception ex) {
            string replaceStep = ex is PublishCompletionException publishException ? publishException.Step : "";
            Exception detailException = ex is PublishCompletionException { InnerException: not null } wrapped ? wrapped.InnerException! : ex;
            (string uploadId, _) = ReadUploadDebugFields(request);
            string channelId = "";
            string updateDirectory = "";
            string stagingRoot = "";
            UploadSession? failureSession = null;
            try {
                if (TryGetUpload(uploadId, out UploadSession? session, out _) && session != null) {
                    failureSession = session;
                    stagingRoot = session.StagingRoot;
                    UpdateChannel? channel = FindChannel(session.ChannelId);
                    channelId = channel?.Id ?? session.ChannelId;
                    updateDirectory = channel?.UpdateDirectory ?? "";
                }
            } catch {
            }

            DirectoryLockDiagnostics diagnostics = InspectUpdateDirectory(updateDirectory);
            if (failureSession != null) {
                UpdateChannel? failureChannel = FindChannel(failureSession.ChannelId);
                if (failureChannel != null)
                    MarkLockingProcessRestartIntent(failureChannel, failureSession, diagnostics);
            }
            LogPublish(uploadId, channelId, "complete failed step=\"" + replaceStep + "\" exception=" + detailException.GetType().Name + " detail=\"" + detailException.Message + "\" updateDirectory=\"" + updateDirectory + "\" stagingRoot=\"" + stagingRoot + "\" processes=" + diagnostics.Processes.Count.ToString(CultureInfo.InvariantCulture) + " lockedFiles=" + diagnostics.LockedFiles.Count.ToString(CultureInfo.InvariantCulture));
            return Json(request, new {
                ok = false,
                error = "Update publish completion failed.",
                detail = detailException.Message,
                exception = detailException.GetType().Name,
                replaceStep,
                uploadId,
                channel = channelId,
                updateDirectory,
                stagingRoot,
                serverLog = LogFilePath,
                probableLockingProcesses = diagnostics.Processes,
                probableLockingProcessDetails = diagnostics.ProcessDetails,
                lockedFiles = diagnostics.LockedFiles
            }, 500, "Internal Server Error");
        }
    }

    private string HandlePublishCompleteCore(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        using JsonDocument document = ParseBody(request);
        JsonElement root = document.RootElement;
        string uploadId = FirstNonEmpty(GetJsonString(root, "uploadId"), GetJsonString(root, "id"));
        if (!TryGetUpload(uploadId, out UploadSession? session, out string error) || session == null)
            return Json(request, new { ok = false, error }, 404, "Not Found");

        UpdateChannel? channel = FindChannel(session.ChannelId);
        if (channel == null)
            return Json(request, new { ok = false, error = "Upload channel no longer exists." }, 404, "Not Found");
        if (!TryApplyRequestedUpdateDirectory(channel, root, out string updateDirectoryError, out bool updateDirectoryChanged))
            return Json(request, new { ok = false, error = updateDirectoryError, channel = channel.Id }, 400, "Bad Request");
        bool channelDetailsChanged = ApplyRequestedChannelDetails(channel, root);
        if (updateDirectoryChanged || channelDetailsChanged)
            SaveDynamicChannelSettings();

        int expectedFileCount = (int)Math.Max(0, GetJsonLong(root, "fileCount", 0));
        long expectedByteCount = Math.Max(0, GetJsonLong(root, "byteCount", 0));
        long expectedSkippedFileCount = GetJsonLong(root, "skippedFileCount", -1);
        long expectedSkippedByteCount = GetJsonLong(root, "skippedByteCount", -1);
        HashSet<int> approvedLockingProcessIds = GetJsonIntSet(root, "killLockingProcessIds", "killProcessIds");
        List<string> stoppedProcesses = new();
        List<string> startedProcesses = new();
        lock (session.Sync) {
            if (expectedFileCount > 0 && expectedFileCount != session.FileCount)
                return Json(request, new { ok = false, error = "File count mismatch.", expectedFileCount, uploadedFileCount = session.FileCount }, 409, "Conflict");
            if (expectedByteCount > 0 && expectedByteCount != session.ByteCount)
                return Json(request, new { ok = false, error = "Byte count mismatch.", expectedByteCount, uploadedByteCount = session.ByteCount }, 409, "Conflict");
            if (expectedSkippedFileCount >= 0 && expectedSkippedFileCount != session.SkippedFileCount)
                return Json(request, new { ok = false, error = "Skipped file count mismatch.", expectedSkippedFileCount, skippedFileCount = session.SkippedFileCount }, 409, "Conflict");
            if (expectedSkippedByteCount >= 0 && expectedSkippedByteCount != session.SkippedByteCount)
                return Json(request, new { ok = false, error = "Skipped byte count mismatch.", expectedSkippedByteCount, skippedByteCount = session.SkippedByteCount }, 409, "Conflict");
            if (!IsSafeReplaceRoot(channel.UpdateDirectory))
                return Json(request, new { ok = false, error = "Configured update directory is not safe to replace.", channel = channel.Id }, 500, "Internal Server Error");

            string parent = Path.GetDirectoryName(channel.UpdateDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? _options.DataDirectory;
            Directory.CreateDirectory(parent);
            string backup = Path.Combine(parent, ".backup-" + channel.Id + "-" + Guid.NewGuid().ToString("N"));
            bool hasBackup = false;
            bool stagingMovedToTarget = false;
            bool inPlaceSynced = false;
            string replaceStep = "initializing";
            try {
                LogPublish(uploadId, channel.Id, "complete start updateDirectory=\"" + channel.UpdateDirectory + "\" stagingRoot=\"" + session.StagingRoot + "\" expectedFiles=" + expectedFileCount.ToString(CultureInfo.InvariantCulture) + " uploadedFiles=" + session.FileCount.ToString(CultureInfo.InvariantCulture) + (updateDirectoryChanged ? " requestedUpdateDirectoryApplied=true" : ""));
                replaceStep = "stop managed processes";
                stoppedProcesses = StopProcessesBeforePublish(channel, restartAfterUpdateSession: session);
                if (stoppedProcesses.Count > 0)
                    LogPublish(uploadId, channel.Id, "stopped processes: " + string.Join("; ", stoppedProcesses));

                replaceStep = "verify managed process stopped";
                if (IsProcessManaged(channel) && IsProcessRunning(channel)) {
                    LogPublish(uploadId, channel.Id, "managed process still running ids=" + string.Join(",", GetManagedProcessIds(channel)));
                    return Json(request, new {
                        ok = false,
                        error = "Managed process is still running; stop it before replacing the update directory.",
                        channel = channel.Id,
                        updateDirectory = channel.UpdateDirectory,
                        managedProcessName = GetManagedProcessName(channel),
                        managedProcessIds = GetManagedProcessIds(channel),
                        serverLog = LogFilePath,
                        stoppedProcesses
                    }, 500, "Internal Server Error");
                }

                replaceStep = "inspect update directory locks";
                DirectoryLockDiagnostics diagnostics = InspectUpdateDirectory(channel.UpdateDirectory);
                if (diagnostics.Processes.Count > 0 || diagnostics.LockedFiles.Count > 0) {
                    if (approvedLockingProcessIds.Count > 0 && diagnostics.ProcessDetails.Any(process => approvedLockingProcessIds.Contains(process.Id))) {
                        replaceStep = "stop approved locking processes";
                        List<string> stoppedLockingProcesses = StopLockingProcessesBeforePublish(channel, session, diagnostics, approvedLockingProcessIds);
                        stoppedProcesses.AddRange(stoppedLockingProcesses);
                        if (stoppedLockingProcesses.Count > 0)
                            LogPublish(uploadId, channel.Id, "stopped locking processes: " + string.Join("; ", stoppedLockingProcesses));
                        System.Threading.Thread.Sleep(500);

                        replaceStep = "inspect update directory locks";
                        diagnostics = InspectUpdateDirectory(channel.UpdateDirectory);
                    }

                    if (diagnostics.Processes.Count > 0 || diagnostics.LockedFiles.Count > 0) {
                        MarkLockingProcessRestartIntent(channel, session, diagnostics);
                        LogPublish(uploadId, channel.Id, "update directory locked processes=" + diagnostics.Processes.Count.ToString(CultureInfo.InvariantCulture) + " lockedFiles=" + diagnostics.LockedFiles.Count.ToString(CultureInfo.InvariantCulture));
                        return Json(request, new {
                            ok = false,
                            error = "Update directory is still in use by running processes or locked files.",
                            channel = channel.Id,
                            updateDirectory = channel.UpdateDirectory,
                            requiresProcessKill = diagnostics.ProcessDetails.Any(process => process.CanStop),
                            lockingProcesses = diagnostics.Processes,
                            lockingProcessDetails = diagnostics.ProcessDetails,
                            lockedFiles = diagnostics.LockedFiles,
                            serverLog = LogFilePath,
                            stoppedProcesses
                        }, 500, "Internal Server Error");
                    }
                }

                replaceStep = "copy protected files";
                RunPublishFileSystemOperation(uploadId, channel.Id, replaceStep, channel.UpdateDirectory, () => CopyProtectedUpdateFiles(channel.UpdateDirectory, session.StagingRoot, channel.Id));
                if (Directory.Exists(channel.UpdateDirectory)) {
                    replaceStep = "move current update directory to backup";
                    try {
                        RunPublishFileSystemOperation(uploadId, channel.Id, replaceStep, channel.UpdateDirectory, () => Directory.Move(channel.UpdateDirectory, backup));
                        hasBackup = true;
                    } catch (Exception moveEx) when (IsRetryablePublishFileSystemException(moveEx)) {
                        DirectoryLockDiagnostics fallbackDiagnostics = InspectUpdateDirectory(channel.UpdateDirectory);
                        if (fallbackDiagnostics.Processes.Count > 0 || fallbackDiagnostics.LockedFiles.Count > 0)
                            throw;

                        LogPublish(uploadId, channel.Id, "root directory move failed without file locks; falling back to in-place sync exception=" + moveEx.GetType().Name + " detail=\"" + moveEx.Message + "\"");
                        replaceStep = "sync staging into update directory";
                        RunPublishFileSystemOperation(uploadId, channel.Id, replaceStep, channel.UpdateDirectory, () => SyncStagingIntoUpdateDirectory(session.StagingRoot, channel.UpdateDirectory, uploadId, channel.Id));
                        inPlaceSynced = true;
                    }
                }
                if (!inPlaceSynced) {
                    replaceStep = "move staging directory into place";
                    RunPublishFileSystemOperation(uploadId, channel.Id, replaceStep, channel.UpdateDirectory, () => Directory.Move(session.StagingRoot, channel.UpdateDirectory));
                    stagingMovedToTarget = true;
                } else {
                    replaceStep = "delete staging directory after in-place sync";
                    RunPublishFileSystemOperation(uploadId, channel.Id, replaceStep, session.StagingRoot, () => {
                        if (Directory.Exists(session.StagingRoot))
                            Directory.Delete(session.StagingRoot, true);
                    });
                }
                if (hasBackup) {
                    try {
                        RunPublishFileSystemOperation(uploadId, channel.Id, "delete backup directory", backup, () => {
                            if (Directory.Exists(backup))
                                Directory.Delete(backup, true);
                        });
                    } catch (Exception cleanupEx) {
                        LogPublish(uploadId, channel.Id, "backup cleanup failed path=\"" + backup + "\" exception=" + cleanupEx.GetType().Name + " detail=\"" + cleanupEx.Message + "\"");
                    }
                }
                LogPublish(uploadId, channel.Id, "complete replace succeeded");
            } catch (Exception ex) {
                LogPublish(uploadId, channel.Id, "complete replace failed step=\"" + replaceStep + "\" exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\" hasBackup=" + hasBackup.ToString() + " stagingMovedToTarget=" + stagingMovedToTarget.ToString() + " inPlaceSynced=" + inPlaceSynced.ToString());
                try {
                    if (hasBackup && !stagingMovedToTarget) {
                        if (Directory.Exists(channel.UpdateDirectory)) {
                            RunPublishFileSystemOperation(uploadId, channel.Id, "rollback delete partial target", channel.UpdateDirectory, () => Directory.Delete(channel.UpdateDirectory, true));
                        }
                        if (Directory.Exists(backup)) {
                            RunPublishFileSystemOperation(uploadId, channel.Id, "rollback restore backup", channel.UpdateDirectory, () => Directory.Move(backup, channel.UpdateDirectory));
                            LogPublish(uploadId, channel.Id, "rollback restored backup from \"" + backup + "\"");
                        }
                    } else if (!hasBackup) {
                        LogPublish(uploadId, channel.Id, "rollback skipped; live directory was not moved");
                    } else {
                        LogPublish(uploadId, channel.Id, "rollback skipped; staging directory is already in target location");
                    }
                } catch (Exception rollbackEx) {
                    LogPublish(uploadId, channel.Id, "rollback failed exception=" + rollbackEx.GetType().Name + " detail=\"" + rollbackEx.Message + "\"");
                }
                throw new PublishCompletionException(replaceStep, ex);
            }

            lock (_sync)
                _uploads.Remove(uploadId);
        }

        startedProcesses = StartProcessesAfterPublish(channel, session);
        Status = "Published " + channel.DisplayName + " (" + session.FileCount.ToString(CultureInfo.InvariantCulture) + " files)" +
                 (startedProcesses.Count == 0 ? "" : " " + string.Join(" ", startedProcesses));
        StatusChanged?.Invoke(this, EventArgs.Empty);
        DirectoryHashManifest manifest = SaveManifest(channel);
        LastUpdateRecord lastUpdate = RecordLastUpdate(channel, session, uploadId);
        return Json(request, new { ok = true, uploadId, channel = channel.Id, updateDirectory = channel.UpdateDirectory, fileCount = session.FileCount, byteCount = session.ByteCount, skippedFileCount = session.SkippedFileCount, skippedByteCount = session.SkippedByteCount, stoppedProcesses, startedProcesses, lastUpdate = lastUpdate.LastUpdateUtc, manifest });
    }

    private string HandlePublishAbort(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        using JsonDocument document = ParseBody(request);
        string uploadId = FirstNonEmpty(GetJsonString(document.RootElement, "uploadId"), GetJsonString(document.RootElement, "id"));
        UploadSession? session = null;
        lock (_sync) {
            if (_uploads.TryGetValue(uploadId, out session))
                _uploads.Remove(uploadId);
        }
        if (session != null)
            TryDeleteDirectory(session.StagingRoot);
        if (session != null)
            TryDeleteFile(session.ArchivePath);
        if (session != null)
            LogPublish(uploadId, session.ChannelId, "abort deleted stagingRoot=\"" + session.StagingRoot + "\"");
        return Json(request, new { ok = true, uploadId, aborted = session != null });
    }

    private string HandleRemoveChannel(HttpRequest request) {
        if (Authenticate(request) == null)
            return Json(request, new { ok = false, error = "SocketJack Update login required." }, 401, "Unauthorized");

        string channelId = FirstNonEmpty(
            request.PathVariables.Count > 0 ? request.PathVariables[0] : "",
            Query(request, "channel"),
            Query(request, "channelId"),
            Query(request, "id"));

        if (string.IsNullOrWhiteSpace(channelId)) {
            if (!TryParseBody(request, out JsonDocument? document, out string error) || document == null)
                return Json(request, new { ok = false, error }, 400, "Bad Request");

            using (document) {
                channelId = FirstNonEmpty(
                    GetJsonString(document.RootElement, "channel"),
                    GetJsonString(document.RootElement, "channelId"),
                    GetJsonString(document.RootElement, "id"));
            }
        }

        if (string.IsNullOrWhiteSpace(channelId))
            return Json(request, new { ok = false, error = "Channel id is required." }, 400, "Bad Request");

        ProcessActionResult result = RemoveDynamicChannel(channelId);
        return Json(request, new {
            ok = result.Ok,
            channel = UpdateServerOptions.NormalizeChannelId(channelId),
            message = result.Message
        }, result.Ok ? 200 : 400, result.Ok ? "OK" : "Bad Request");
    }

    private object HandleUpdateRoot(HttpRequest request) {
        if (IsLegacyGuiUpdateRequest(request)) {
            UpdateChannel? guiChannel = FindChannel("jackllm");
            if (guiChannel != null)
                return Json(request, LoadOrCreateManifest(guiChannel));
        }

        return Json(request, new { ok = true, channels = GetChannelSnapshots() });
    }

    private object HandleUpdateDownload(HttpRequest request) {
        string wildcard = request.PathVariables.Count > 0 ? request.PathVariables[0] : "";
        string[] parts = NormalizeRelativePath(wildcard).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return HandleUpdateRoot(request);

        UpdateChannel? channel = FindChannel(parts[0]);
        string relativePath;
        if (channel == null) {
            channel = FindChannel("jackllm");
            relativePath = string.Join("/", parts);
        } else {
            relativePath = string.Join("/", parts.Skip(1));
        }

        if (channel == null)
            return Text(request, "Update channel not found.", "text/plain; charset=utf-8", 404, "Not Found");
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Equals("meta", StringComparison.OrdinalIgnoreCase) || relativePath.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) || IsMetadataFile(relativePath))
            return Json(request, ShouldRefreshManifest(request) ? SaveManifest(channel) : LoadOrCreateManifest(channel));

        string fullPath = ResolveChildPath(channel.UpdateDirectory, relativePath);
        if (!File.Exists(fullPath))
            return Text(request, "Update file not found.", "text/plain; charset=utf-8", 404, "Not Found");
        AddCors(request);
        request.Context.Response.Headers["Cache-Control"] = "no-store";
        return FileResponse.FromFile(fullPath);
    }

    private DirectoryHashManifest BuildManifest(UpdateChannel channel) {
        return DirectoryHashMetadata.BuildManifest(channel.UpdateDirectory, CombineUrl(_options.PublicUrl, GetChannelPublicPath(channel).TrimStart('/') + "/"), relativePath => ShouldIncludeManifestFile(relativePath, channel.Id));
    }

    private DirectoryHashManifest LoadOrCreateManifest(UpdateChannel channel) {
        string metadataPath = GetMetadataFilePath(channel);
        if (File.Exists(metadataPath)) {
            try {
                DirectoryHashManifest? manifest = JsonSerializer.Deserialize<DirectoryHashManifest>(File.ReadAllText(metadataPath), UpdateServerOptions.JsonOptions);
                if (manifest != null) {
                    RefreshManifestUrls(channel, manifest);
                    return manifest;
                }
            } catch {
            }
        }

        return SaveManifest(channel);
    }

    private static bool ShouldRefreshManifest(HttpRequest request) {
        return IsQueryFlagEnabled(request, "fresh") ||
               IsQueryFlagEnabled(request, "refresh") ||
               IsQueryFlagEnabled(request, "noCache");
    }

    private static bool IsQueryFlagEnabled(HttpRequest request, string name) {
        if (!HasQueryValue(request, name))
            return false;
        string value = Query(request, name).Trim();
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

    private DirectoryHashManifest SaveManifest(UpdateChannel channel) {
        DirectoryHashManifest manifest = BuildManifest(channel);
        RefreshManifestUrls(channel, manifest);
        try {
            Directory.CreateDirectory(channel.UpdateDirectory);
            File.WriteAllText(GetMetadataFilePath(channel), JsonSerializer.Serialize(manifest, UpdateServerOptions.JsonOptions));
        } catch {
        }
        return manifest;
    }

    private LastUpdateRecord RecordLastUpdate(UpdateChannel channel, UploadSession session, string uploadId) {
        LastUpdateRecord record = new() {
            ChannelId = channel.Id,
            DisplayName = channel.DisplayName,
            UpdateDirectory = channel.UpdateDirectory,
            UploadId = uploadId,
            LastUpdateUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            FileCount = session.FileCount,
            ByteCount = session.ByteCount
        };
        channel.LastUpdateUtc = record.LastUpdateUtc;

        LastUpdateSettings settings = new() { Updates = LoadLastUpdateMetadata() };
        int index = settings.Updates.FindIndex(item => string.Equals(UpdateServerOptions.NormalizeChannelId(item.ChannelId), channel.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
            settings.Updates[index] = record;
        else
            settings.Updates.Add(record);

        try {
            Directory.CreateDirectory(Path.GetDirectoryName(_options.LastUpdateMetadataPath) ?? _options.DataDirectory);
            File.WriteAllText(_options.LastUpdateMetadataPath, JsonSerializer.Serialize(settings, UpdateServerOptions.JsonOptions));
        } catch (Exception ex) {
            AppendLog("metadata", "last update save failed exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
        }

        SaveDynamicChannelSettings();
        return record;
    }

    private List<LastUpdateRecord> LoadLastUpdateMetadata() {
        try {
            if (!File.Exists(_options.LastUpdateMetadataPath))
                return new List<LastUpdateRecord>();
            string json = File.ReadAllText(_options.LastUpdateMetadataPath);
            if (string.IsNullOrWhiteSpace(json))
                return new List<LastUpdateRecord>();
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return JsonSerializer.Deserialize<List<LastUpdateRecord>>(json, UpdateServerOptions.JsonOptions) ?? new List<LastUpdateRecord>();
            return JsonSerializer.Deserialize<LastUpdateSettings>(json, UpdateServerOptions.JsonOptions)?.Updates ?? new List<LastUpdateRecord>();
        } catch {
            return new List<LastUpdateRecord>();
        }
    }

    private void RefreshManifestUrls(UpdateChannel channel, DirectoryHashManifest manifest) {
        string baseUrl = CombineUrl(_options.PublicUrl, GetChannelPublicPath(channel).TrimStart('/') + "/");
        manifest.BaseUrl = baseUrl;
        foreach (DirectoryHashFile file in manifest.Files)
            file.Url = CombineUrl(baseUrl, file.Path);
    }

    private static string GetChannelPublicPath(UpdateChannel channel) {
        string publicPath = UpdateServerOptions.NormalizePublicPath(channel.PublicPath);
        return string.IsNullOrWhiteSpace(publicPath) ? "/update/" + channel.Id : publicPath;
    }

    private bool IsLegacyGuiUpdateRequest(HttpRequest request) {
        string path = RemovePublicBasePath(request.Path ?? "");
        return path.Equals("/Update", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/Update/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMetadataFilePath(UpdateChannel channel) {
        return Path.Combine(channel.UpdateDirectory, MetadataFileName);
    }

    private static bool ShouldIncludeManifestFile(string relativePath, string channelId) {
        if (IsMetadataFile(relativePath))
            return false;
        return IsAllowedUpdateFile(relativePath, channelId, out _);
    }

    private static bool IsMetadataFile(string relativePath) {
        return string.Equals(Path.GetFileName(NormalizeRelativePath(relativePath)), MetadataFileName, StringComparison.OrdinalIgnoreCase);
    }

    private bool ValidatePassword(string username, string password) {
        if (!string.Equals(username, _auth.UserName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(password))
            return false;
        byte[] salt = Convert.FromBase64String(_auth.PasswordSalt);
        return ConstantTimeEquals(HashPassword(password, salt), _auth.PasswordHash);
    }

    private AuthState? Authenticate(HttpRequest request) {
        string token = ExtractToken(request);
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(_auth.TokenHash))
            return null;
        if (!DateTimeOffset.TryParse(_auth.TokenExpiresUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset expires) ||
            expires <= DateTimeOffset.UtcNow)
            return null;
        return ConstantTimeEquals(HashToken(token), _auth.TokenHash) ? _auth : null;
    }

    private void LoadAuth() {
        if (File.Exists(_options.AuthFilePath)) {
            _auth = JsonSerializer.Deserialize<AuthState>(File.ReadAllText(_options.AuthFilePath), UpdateServerOptions.JsonOptions) ?? new AuthState();
        }
    }

    private void SaveAuth() {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.AuthFilePath) ?? _options.DataDirectory);
        File.WriteAllText(_options.AuthFilePath, JsonSerializer.Serialize(_auth, UpdateServerOptions.JsonOptions));
    }

    private void SaveDynamicChannelSettings() {
        try {
            UpdateServerOptions.SaveDynamicChannels(_options.Channels);
        } catch (Exception ex) {
            AppendLog("dynamic", "save failed exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
        }
    }

    private void ConfigureAdminFromEnvironment() {
        if (HasAdminLogin)
            return;
        string username = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_ADMIN_USER") ?? "";
        string password = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_ADMIN_PASSWORD") ?? "";
        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            ConfigureAdmin(username, password);
    }

    private void LogPublish(string uploadId, string channelId, string message) {
        AppendLog("publish", "uploadId=" + SafeLogText(uploadId) + " channel=" + SafeLogText(channelId) + " " + SafeLogText(message));
    }

    private void AppendLog(string area, string message) {
        try {
            lock (_logSync) {
                Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath) ?? _options.DataDirectory);
                File.AppendAllText(
                    LogFilePath,
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " [" + SafeLogText(area) + "] " + SafeLogText(message) + Environment.NewLine,
                    Encoding.UTF8);
            }
        } catch {
        }
    }

    private static string SafeLogText(string value) {
        return (value ?? "")
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }

    private UpdateChannel? FindChannel(string channelId) {
        channelId = UpdateServerOptions.NormalizeChannelId(channelId);
        return _options.Channels.FirstOrDefault(channel => string.Equals(channel.Id, channelId, StringComparison.OrdinalIgnoreCase));
    }

    private void AutoStartConfiguredChannels() {
        foreach (UpdateChannel channel in _options.Channels) {
            if (!ShouldAutoStartOnUpdaterStartup(channel) || !IsProcessManaged(channel) || IsProcessRunning(channel))
                continue;

            try {
                ProcessActionResult result = StartManagedProcess(channel);
                AppendLog("startup", result.Message);
            } catch (Exception ex) {
                AppendLog("startup", "failed to start " + SafeLogText(channel.Id) + " exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
            }
        }
    }

    private static List<string> StopProcessesBeforePublish(UpdateChannel channel, bool forceKill = false, UploadSession? restartAfterUpdateSession = null) {
        var stopped = new List<string>();
        if (channel == null)
            return stopped;

        if (!IsProcessManaged(channel))
            return stopped;

        foreach (Process process in GetManagedProcesses(channel)) {
            try {
                if (process.HasExited)
                    continue;

                string label = process.ProcessName + "#" + process.Id.ToString(CultureInfo.InvariantCulture);
                string executablePath = FirstNonEmpty(TryGetProcessExecutablePath(process), GetManagedExecutablePath(channel));
                if (restartAfterUpdateSession != null)
                    QueueExtraRestartProcess(restartAfterUpdateSession, process.ProcessName, executablePath);
                if (!forceKill) {
                    try {
                        if (process.CloseMainWindow() && process.WaitForExit(5000)) {
                            stopped.Add(label + ": closed" + (restartAfterUpdateSession == null ? "" : ", restart queued"));
                            continue;
                        }
                    } catch {
                    }
                }

                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                    if (process.WaitForExit(10000) || process.HasExited)
                        stopped.Add(label + ": killed" + (restartAfterUpdateSession == null ? "" : ", restart queued"));
                    else
                        stopped.Add(label + ": kill timed out");
                }
            } catch (Exception ex) {
                stopped.Add(process.ProcessName + "#" + process.Id.ToString(CultureInfo.InvariantCulture) + ": " + ex.Message);
            } finally {
                process.Dispose();
            }
        }

        List<int> survivors = GetManagedProcessIds(channel);
        if (survivors.Count > 0)
            stopped.Add("survivors: still running #" + string.Join(",", survivors));

        return stopped;
    }

    private static List<string> StopLockingProcessesBeforePublish(UpdateChannel channel, UploadSession session, DirectoryLockDiagnostics diagnostics, HashSet<int> approvedProcessIds) {
        var stopped = new List<string>();
        if (diagnostics.ProcessDetails.Count == 0 || approvedProcessIds.Count == 0)
            return stopped;

        foreach (LockingProcessInfo info in diagnostics.ProcessDetails.OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ThenBy(process => process.Id)) {
            if (!approvedProcessIds.Contains(info.Id))
                continue;

            string label = FormatProcessLabel(info.Name, info.Id);
            if (!info.CanStop) {
                stopped.Add(label + ": not stopped");
                continue;
            }

            try {
                using Process process = Process.GetProcessById(info.Id);
                if (process.HasExited) {
                    stopped.Add(label + ": already exited");
                    continue;
                }

                string executablePath = FirstNonEmpty(info.ExecutablePath, TryGetProcessExecutablePath(process));
                bool shouldRestart = ShouldQueueRestartForLockingProcess(channel, session, info.Name, executablePath);

                try {
                    if (process.CloseMainWindow() && process.WaitForExit(5000)) {
                        if (shouldRestart)
                            QueueExtraRestartProcess(session, info.Name, executablePath);
                        stopped.Add(label + ": closed" + (shouldRestart ? ", restart queued" : ""));
                        continue;
                    }
                } catch {
                }

                if (!process.HasExited) {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10000);
                    if (shouldRestart)
                        QueueExtraRestartProcess(session, info.Name, executablePath);
                    stopped.Add(label + ": killed" + (shouldRestart ? ", restart queued" : ""));
                }
            } catch (Exception ex) {
                stopped.Add(label + ": " + ex.Message);
            }
        }

        return stopped;
    }

    private static void QueueExtraRestartProcess(UploadSession session, string processName, string executablePath) {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        string fullExecutablePath;
        try {
            fullExecutablePath = Path.GetFullPath(executablePath);
        } catch {
            return;
        }

        if (!File.Exists(fullExecutablePath))
            return;

        if (session.ExtraRestartProcesses.Values.Any(spec => SamePath(spec.ExecutablePath, fullExecutablePath)))
            return;

        session.ExtraRestartProcesses[fullExecutablePath] = new RestartProcessSpec {
            ProcessName = string.IsNullOrWhiteSpace(processName) ? Path.GetFileNameWithoutExtension(fullExecutablePath) : processName,
            ExecutablePath = fullExecutablePath,
            WorkingDirectory = Path.GetDirectoryName(fullExecutablePath) ?? ""
        };
    }

    private static void MarkLockingProcessRestartIntent(UpdateChannel channel, UploadSession session, DirectoryLockDiagnostics diagnostics) {
        foreach (LockingProcessInfo info in diagnostics.ProcessDetails)
            info.RestartAfterUpdate = ShouldQueueRestartForLockingProcess(channel, session, info.Name, info.ExecutablePath);
    }

    private static bool ShouldQueueRestartForLockingProcess(UpdateChannel channel, UploadSession session, string processName, string executablePath) {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return false;
        if (!string.IsNullOrWhiteSpace(GetManagedProcessName(channel)) &&
            string.Equals(processName, GetManagedProcessName(channel), StringComparison.OrdinalIgnoreCase))
            return false;
        if (!string.IsNullOrWhiteSpace(GetManagedExecutablePath(channel)) && SamePath(executablePath, GetManagedExecutablePath(channel)))
            return false;
        return !session.ExtraRestartProcesses.Values.Any(spec => SamePath(spec.ExecutablePath, executablePath));
    }

    private static string TryGetProcessExecutablePath(Process process) {
        try {
            string path = process.MainModule?.FileName ?? "";
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        } catch {
        }

        return TryGetProcessExecutablePathByQueryFullProcessImageName(process.Id);
    }

    private static string TryGetProcessExecutablePathByQueryFullProcessImageName(int processId) {
        if (!OperatingSystem.IsWindows())
            return "";

        IntPtr handle = IntPtr.Zero;
        try {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return "";

            var buffer = new StringBuilder(32768);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? buffer.ToString(0, size)
                : "";
        } catch {
            return "";
        } finally {
            if (handle != IntPtr.Zero)
                CloseHandle(handle);
        }
    }

    private UpdateChannel RequireManagedChannel(string channelId) {
        UpdateChannel? channel = FindChannel(channelId);
        if (channel == null)
            throw new InvalidOperationException("Unknown update channel: " + channelId);
        if (!IsProcessManaged(channel))
            throw new InvalidOperationException(channel.DisplayName + " does not have a managed process.");
        return channel;
    }

    private static bool IsProcessManaged(UpdateChannel channel) => !string.IsNullOrWhiteSpace(GetManagedProcessName(channel)) || !string.IsNullOrWhiteSpace(channel?.ManagedExecutablePath);

    private static string GetManagedProcessName(UpdateChannel channel) {
        string configured = channel?.ManagedProcessName ?? "";
        if (string.IsNullOrWhiteSpace(configured) && string.Equals(channel?.Id, MasterListChannelId, StringComparison.OrdinalIgnoreCase))
            configured = MasterListProcessName;
        if (string.IsNullOrWhiteSpace(configured) && !string.IsNullOrWhiteSpace(channel?.ManagedExecutablePath))
            configured = Path.GetFileNameWithoutExtension(channel.ManagedExecutablePath.Trim().Trim('"'));
        configured = configured.Trim();
        return configured.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(configured)
            : configured;
    }

    private static string GetManagedExecutablePath(UpdateChannel channel) {
        if (!string.IsNullOrWhiteSpace(channel.ManagedExecutablePath)) {
            string configuredPath = channel.ManagedExecutablePath.Trim().Trim('"');
            return Path.IsPathRooted(configuredPath)
                ? Path.GetFullPath(configuredPath)
                : Path.GetFullPath(Path.Combine(channel.UpdateDirectory, configuredPath));
        }
        string processName = GetManagedProcessName(channel);
        return string.IsNullOrWhiteSpace(processName) ? "" : Path.Combine(channel.UpdateDirectory, processName + ".exe");
    }

    private static List<int> GetManagedProcessIds(UpdateChannel channel) {
        var ids = new List<int>();
        foreach (Process process in GetManagedProcesses(channel)) {
            try {
                if (!process.HasExited)
                    ids.Add(process.Id);
            } catch {
            } finally {
                process.Dispose();
            }
        }
        return ids;
    }

    private static List<Process> GetManagedProcesses(UpdateChannel channel) {
        string processName = GetManagedProcessName(channel);
        string executablePath = GetManagedExecutablePath(channel);
        bool matchExecutablePath = !string.IsNullOrWhiteSpace(executablePath);
        var processes = new List<Process>();
        var seenProcessIds = new HashSet<int>();
        var candidateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(processName))
            candidateNames.Add(processName);
        if (matchExecutablePath) {
            string executableProcessName = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrWhiteSpace(executableProcessName))
                candidateNames.Add(executableProcessName);
        }

        foreach (string candidateName in candidateNames) {
            foreach (Process process in Process.GetProcessesByName(candidateName)) {
                AddManagedProcessIfMatched(process, executablePath, matchExecutablePath, processes, seenProcessIds, allowNameOnlyFallback: true);
            }
        }

        if (matchExecutablePath) {
            foreach (Process process in Process.GetProcesses()) {
                AddManagedProcessIfMatched(process, executablePath, matchExecutablePath: true, processes, seenProcessIds, allowNameOnlyFallback: false);
            }
        }

        return processes;
    }

    private static void AddManagedProcessIfMatched(Process process, string executablePath, bool matchExecutablePath, List<Process> processes, HashSet<int> seenProcessIds, bool allowNameOnlyFallback) {
        bool keepProcess = false;
        try {
            if (process.HasExited || seenProcessIds.Contains(process.Id))
                return;
            if (matchExecutablePath) {
                string actualExecutablePath = TryGetProcessExecutablePath(process);
                if (!string.IsNullOrWhiteSpace(actualExecutablePath)) {
                    if (!SamePath(actualExecutablePath, executablePath))
                        return;
                } else if (!allowNameOnlyFallback || !IsMatchingExecutableProcess(process, executablePath)) {
                    return;
                }
            }

            processes.Add(process);
            seenProcessIds.Add(process.Id);
            keepProcess = true;
        } catch {
        } finally {
            if (!keepProcess)
                process.Dispose();
        }
    }

    private static bool IsProcessRunning(UpdateChannel channel) => GetManagedProcessIds(channel).Count > 0;

    private static List<LockingProcessInfo> GetProcessesLoadedFromDirectory(string directory) {
        var matches = new List<LockingProcessInfo>();
        if (string.IsNullOrWhiteSpace(directory))
            return matches;

        string fullRoot;
        try {
            fullRoot = Path.GetFullPath(directory);
        } catch {
            return matches;
        }

        if (IsPathInsideRoot(AppContext.BaseDirectory, fullRoot)) {
            matches.Add(new LockingProcessInfo {
                Name = "current",
                Id = Environment.ProcessId,
                ExecutablePath = AppContext.BaseDirectory,
                Reason = "baseDir=" + AppContext.BaseDirectory,
                CanStop = false
            });
        }

        foreach (Process process in Process.GetProcesses()) {
            try {
                if (process.HasExited)
                    continue;

                string executablePath = "";
                try {
                    executablePath = process.MainModule?.FileName ?? "";
                } catch {
                }

                if (IsPathInsideRoot(executablePath, fullRoot)) {
                    matches.Add(new LockingProcessInfo {
                        Name = process.ProcessName,
                        Id = process.Id,
                        ExecutablePath = executablePath,
                        Reason = "",
                        CanStop = process.Id != Environment.ProcessId
                    });
                }
            } catch {
            } finally {
                process.Dispose();
            }
        }

        return matches
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Id)
            .ToList();
    }

    private void SyncStagingIntoUpdateDirectory(string stagingRoot, string updateRoot, string uploadId, string channelId) {
        Directory.CreateDirectory(updateRoot);
        var stagedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0;
        int deleted = 0;
        int prunedDirectories = 0;
        int pruneFailures = 0;

        foreach (string sourcePath in Directory.EnumerateFiles(stagingRoot, "*", SearchOption.AllDirectories)) {
            string relativePath = NormalizeRelativePath(Path.GetRelativePath(stagingRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/'));
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            stagedPaths.Add(relativePath);
            string destinationPath = ResolveChildPath(updateRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? updateRoot);
            ClearReadOnly(destinationPath);
            File.Copy(sourcePath, destinationPath, true);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            copied++;
        }

        foreach (string targetPath in Directory.EnumerateFiles(updateRoot, "*", SearchOption.AllDirectories).ToList()) {
            string relativePath;
            try {
                relativePath = NormalizeRelativePath(Path.GetRelativePath(updateRoot, targetPath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/'));
            } catch {
                continue;
            }

            if (stagedPaths.Contains(relativePath) ||
                IsMetadataFile(relativePath) ||
                IsProtectedServerFile(channelId, relativePath) ||
                IsProtectedSiblingChannelFile(channelId, relativePath))
                continue;

            ClearReadOnly(targetPath);
            File.Delete(targetPath);
            deleted++;
        }

        foreach (string directoryPath in Directory.EnumerateDirectories(updateRoot, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length).ToList()) {
            try {
                if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                    continue;
                Directory.Delete(directoryPath);
                prunedDirectories++;
            } catch (Exception ex) {
                pruneFailures++;
                if (pruneFailures <= 5)
                    LogPublish(uploadId, channelId, "in-place sync could not prune directory=\"" + directoryPath + "\" exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
            }
        }

        LogPublish(uploadId, channelId, "in-place sync complete copied=" + copied.ToString(CultureInfo.InvariantCulture) + " deletedStale=" + deleted.ToString(CultureInfo.InvariantCulture) + " prunedDirs=" + prunedDirectories.ToString(CultureInfo.InvariantCulture) + " pruneFailures=" + pruneFailures.ToString(CultureInfo.InvariantCulture));
    }

    private static void ClearReadOnly(string path) {
        if (!File.Exists(path))
            return;

        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private void RunPublishFileSystemOperation(string uploadId, string channelId, string operation, string diagnosticDirectory, Action action) {
        const int maxAttempts = 8;
        for (int attempt = 1; attempt <= maxAttempts; attempt++) {
            try {
                action();
                if (attempt > 1)
                    LogPublish(uploadId, channelId, operation + " succeeded after attempt " + attempt.ToString(CultureInfo.InvariantCulture));
                return;
            } catch (Exception ex) when (IsRetryablePublishFileSystemException(ex)) {
                DirectoryLockDiagnostics diagnostics = InspectUpdateDirectory(diagnosticDirectory);
                LogPublish(uploadId, channelId, operation + " failed attempt " + attempt.ToString(CultureInfo.InvariantCulture) + "/" + maxAttempts.ToString(CultureInfo.InvariantCulture) + " exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\" processes=" + diagnostics.Processes.Count.ToString(CultureInfo.InvariantCulture) + " lockedFiles=" + diagnostics.LockedFiles.Count.ToString(CultureInfo.InvariantCulture));
                if (attempt >= maxAttempts)
                    throw;
                System.Threading.Thread.Sleep(Math.Min(1500, 150 * attempt));
            }
        }
    }

    private static bool IsRetryablePublishFileSystemException(Exception ex) {
        return ex is IOException || ex is UnauthorizedAccessException;
    }

    private static DirectoryLockDiagnostics InspectUpdateDirectory(string directory) {
        var diagnostics = new DirectoryLockDiagnostics {
            Directory = directory ?? ""
        };

        if (string.IsNullOrWhiteSpace(directory))
            return diagnostics;

        try {
            diagnostics.Exists = Directory.Exists(directory);
        } catch {
            return diagnostics;
        }

        diagnostics.ProcessDetails = GetProcessesLoadedFromDirectory(directory);
        List<LockedFileInfo> lockedFileDetails = GetLockedFilesInDirectory(directory, 100);
        diagnostics.LockedFiles = lockedFileDetails.Select(file => file.Display).ToList();
        diagnostics.ProcessDetails.AddRange(GetProcessesLockingFiles(lockedFileDetails.Select(file => file.FullPath).Where(path => !string.IsNullOrWhiteSpace(path)).ToList()));
        diagnostics.ProcessDetails = diagnostics.ProcessDetails
            .GroupBy(process => process.Id)
            .Select(group => group.First())
            .OrderBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(process => process.Id)
            .ToList();
        diagnostics.Processes = diagnostics.ProcessDetails.Select(process => process.Display).ToList();
        return diagnostics;
    }

    private static List<LockedFileInfo> GetLockedFilesInDirectory(string directory, int maxResults) {
        var lockedFiles = new List<LockedFileInfo>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return lockedFiles;

        foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) {
            try {
                using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.None);
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                string relativePath;
                try {
                    relativePath = Path.GetRelativePath(directory, filePath)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                } catch {
                    relativePath = filePath;
                }

                lockedFiles.Add(new LockedFileInfo {
                    FullPath = filePath,
                    RelativePath = relativePath,
                    Error = ex.GetType().Name + ": " + ex.Message
                });
                if (lockedFiles.Count >= maxResults) {
                    lockedFiles.Add(new LockedFileInfo {
                        RelativePath = "... additional locked files omitted",
                        Error = ""
                    });
                    break;
                }
            }
        }

        return lockedFiles;
    }

    private static List<LockingProcessInfo> GetProcessesLockingFiles(IReadOnlyList<string> filePaths) {
        var processes = new List<LockingProcessInfo>();
        string[] resources = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(512)
            .ToArray();
        if (resources.Length == 0)
            return processes;

        uint sessionHandle = 0;
        var sessionKey = new StringBuilder(64);
        if (RmStartSession(out sessionHandle, 0, sessionKey) != 0)
            return processes;

        try {
            if (RmRegisterResources(sessionHandle, (uint)resources.Length, resources, 0, null, 0, null) != 0)
                return processes;

            uint processInfoNeeded = 0;
            uint processInfoCount = 0;
            uint rebootReasons;
            int result = RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, null, out rebootReasons);
            if (result == ErrorMoreData && processInfoNeeded > 0) {
                var processInfo = new RM_PROCESS_INFO[processInfoNeeded];
                processInfoCount = processInfoNeeded;
                result = RmGetList(sessionHandle, out processInfoNeeded, ref processInfoCount, processInfo, out rebootReasons);
                if (result == 0) {
                    for (int i = 0; i < processInfoCount; i++) {
                        int processId = processInfo[i].Process.dwProcessId;
                        if (processId <= 0)
                            continue;

                        string processName = processInfo[i].strAppName ?? "";
                        string executablePath = "";
                        try {
                            using Process process = Process.GetProcessById(processId);
                            if (!process.HasExited) {
                                processName = string.IsNullOrWhiteSpace(processName) ? process.ProcessName : processName;
                                executablePath = TryGetProcessExecutablePath(process);
                            }
                        } catch {
                        }

                        processes.Add(new LockingProcessInfo {
                            Name = string.IsNullOrWhiteSpace(processName) ? "process" : processName,
                            Id = processId,
                            ExecutablePath = executablePath,
                            Reason = "locked file",
                            CanStop = processId != Environment.ProcessId
                        });
                    }
                }
            }
        } catch {
        } finally {
            if (sessionHandle != 0)
                RmEndSession(sessionHandle);
        }

        return processes;
    }

    private static bool IsPathInsideRoot(string path, string root) {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            return false;

        try {
            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        } catch {
            return false;
        }
    }

    private static ProcessActionResult StartManagedProcess(UpdateChannel channel) {
        string processName = GetManagedProcessName(channel);
        if (string.IsNullOrWhiteSpace(processName))
            return FailProcessAction(channel.DisplayName + " does not have a managed process.");

        string exePath = GetManagedExecutablePath(channel);
        if (!File.Exists(exePath))
            return FailProcessAction(channel.DisplayName + " executable was not found: " + exePath);

        lock (ProcessStartSync) {
            List<int> runningIds = GetRunningExecutableProcessIds(exePath);
            if (runningIds.Count > 0) {
                return new ProcessActionResult {
                    Ok = true,
                    Message = channel.DisplayName + " is already running as process #" + string.Join(",", runningIds) + ".",
                    ProcessId = runningIds[0]
                };
            }

            var startInfo = new ProcessStartInfo {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? channel.UpdateDirectory,
                UseShellExecute = true
            };
            Process? process = Process.Start(startInfo);
            return new ProcessActionResult {
                Ok = process != null,
                Message = process == null
                    ? channel.DisplayName + " did not start."
                    : channel.DisplayName + " started as process #" + process.Id.ToString(CultureInfo.InvariantCulture) + ".",
                ProcessId = process?.Id ?? 0
            };
        }
    }

    private static ProcessActionResult StopManagedProcess(UpdateChannel channel) {
        if (!IsProcessManaged(channel))
            return FailProcessAction(channel.DisplayName + " does not have a managed process.");

        List<string> stopped = StopProcessesBeforePublish(channel, forceKill: true);
        if (stopped.Count == 0)
            return new ProcessActionResult { Ok = true, Message = channel.DisplayName + " is not running." };

        return new ProcessActionResult {
            Ok = stopped.All(item => !item.Contains(":", StringComparison.Ordinal) || item.Contains(": closed", StringComparison.OrdinalIgnoreCase) || item.Contains(": killed", StringComparison.OrdinalIgnoreCase)),
            Message = channel.DisplayName + " stopped: " + string.Join(", ", stopped)
        };
    }

    private static List<string> StartProcessesAfterPublish(UpdateChannel channel, UploadSession session) {
        var started = new List<string>();
        var startedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (RestartProcessSpec spec in session.ExtraRestartProcesses.Values.OrderBy(spec => spec.ProcessName, StringComparer.OrdinalIgnoreCase)) {
            if (string.IsNullOrWhiteSpace(spec.ExecutablePath))
                continue;
            if (startedPaths.Any(path => SamePath(path, spec.ExecutablePath)))
                continue;

            try {
                lock (ProcessStartSync) {
                    List<int> runningIds = GetRunningExecutableProcessIds(spec.ExecutablePath);
                    if (runningIds.Count > 0) {
                        started.Add(spec.ProcessName + " is already running as process #" + string.Join(",", runningIds) + ".");
                        continue;
                    }

                    var startInfo = new ProcessStartInfo {
                        FileName = spec.ExecutablePath,
                        WorkingDirectory = string.IsNullOrWhiteSpace(spec.WorkingDirectory)
                            ? Path.GetDirectoryName(spec.ExecutablePath) ?? ""
                            : spec.WorkingDirectory,
                        UseShellExecute = true
                    };
                    Process? process = Process.Start(startInfo);
                    started.Add(process == null
                        ? spec.ProcessName + " did not start."
                        : spec.ProcessName + " started as process #" + process.Id.ToString(CultureInfo.InvariantCulture) + ".");
                    startedPaths.Add(Path.GetFullPath(spec.ExecutablePath));
                }
            } catch (Exception ex) {
                started.Add("Failed to restart " + spec.ProcessName + ": " + ex.Message);
            }
        }
        return started;
    }

    private static bool ShouldAutoStartAfterUpdate(UpdateChannel channel) {
        return IsProcessManaged(channel) && channel.AutoStartAfterUpdate;
    }

    private static bool ShouldAutoStartOnUpdaterStartup(UpdateChannel channel) {
        return ShouldAutoStartAfterUpdate(channel) && !IsUserWorkstationChannel(channel);
    }

    private static bool IsUserWorkstationChannel(UpdateChannel channel) {
        string id = UpdateServerOptions.NormalizeChannelId(channel?.Id ?? "");
        return id.Equals("jackllm", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("jackllm-companion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExecutableRunning(string executablePath) {
        return GetRunningExecutableProcessIds(executablePath).Count > 0;
    }

    private static List<int> GetRunningExecutableProcessIds(string executablePath) {
        var ids = new List<int>();
        if (string.IsNullOrWhiteSpace(executablePath))
            return ids;

        string fullPath;
        try {
            fullPath = Path.GetFullPath(executablePath);
        } catch {
            return ids;
        }

        string processName = Path.GetFileNameWithoutExtension(fullPath);
        if (string.IsNullOrWhiteSpace(processName))
            return ids;

        foreach (Process process in Process.GetProcessesByName(processName)) {
            try {
                if (!process.HasExited && IsMatchingExecutableProcess(process, fullPath))
                    ids.Add(process.Id);
            } catch {
            } finally {
                process.Dispose();
            }
        }

        return ids;
    }

    private static bool IsWindowsStartupEnabled(UpdateChannel channel) {
        if (!IsProcessManaged(channel))
            return false;

        try {
            string exePath = GetManagedExecutablePath(channel);
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(WindowsRunKeyPath, false);
            string configured = key?.GetValue(GetStartupValueName(channel)) as string ?? "";
            return !string.IsNullOrWhiteSpace(configured) && configured.IndexOf(exePath, StringComparison.OrdinalIgnoreCase) >= 0;
        } catch {
            return false;
        }
    }

    private static string GetStartupValueName(UpdateChannel channel) => "SocketJack Update - " + channel.DisplayName;
    private static string QuoteCommandPath(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";
    private static ProcessActionResult FailProcessAction(string message) => new() { Ok = false, Message = message };
    private static string FormatProcessLabel(string processName, int processId) {
        return (string.IsNullOrWhiteSpace(processName) ? "process" : processName) + "#" + processId.ToString(CultureInfo.InvariantCulture);
    }

    private static bool SamePath(string left, string right) {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try {
            return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        } catch {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsMatchingExecutableProcess(Process process, string expectedExecutablePath) {
        string actualExecutablePath = TryGetProcessExecutablePath(process);
        if (!string.IsNullOrWhiteSpace(actualExecutablePath))
            return SamePath(actualExecutablePath, expectedExecutablePath);

        string expectedProcessName = Path.GetFileNameWithoutExtension(expectedExecutablePath);
        return !string.IsNullOrWhiteSpace(expectedProcessName) &&
               string.Equals(process.ProcessName, expectedProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGetOrCreatePublishChannel(string channelId, JsonElement root, out UpdateChannel? channel, out string error, out bool changed) {
        channel = FindChannel(channelId);
        error = "";
        changed = false;
        if (channel != null)
            return true;

        string updateDirectory = FirstNonEmpty(
            GetJsonString(root, "updateDirectory"),
            GetJsonString(root, "serverPath"),
            GetJsonString(root, "targetDirectory"),
            GetJsonString(root, "publishDirectory"));
        if (string.IsNullOrWhiteSpace(updateDirectory)) {
            error = "Unknown update channel. Supply updateDirectory/serverPath to create it dynamically.";
            return false;
        }

        string fullPath;
        try {
            fullPath = Path.GetFullPath(updateDirectory);
        } catch (Exception ex) {
            error = "Requested update directory is invalid: " + ex.Message;
            return false;
        }

        if (!IsSafeReplaceRoot(fullPath)) {
            error = "Requested update directory is not safe to replace.";
            return false;
        }

        channel = new UpdateChannel {
            Id = UpdateServerOptions.NormalizeChannelId(channelId),
            DisplayName = FirstNonEmpty(GetJsonString(root, "displayName"), GetJsonString(root, "name"), channelId),
            UpdateDirectory = fullPath,
            PublicPath = FirstNonEmpty(GetJsonString(root, "publicPath"), "/update/" + UpdateServerOptions.NormalizeChannelId(channelId)),
            IsDynamic = true
        };
        ApplyRequestedChannelDetails(channel, root);
        UpdateServerOptions.NormalizeChannel(channel, _options.DataDirectory);
        _options.Channels.Add(channel);
        changed = true;
        LogPublish("", channel.Id, "dynamic channel created updateDirectory=\"" + channel.UpdateDirectory + "\" publicPath=\"" + channel.PublicPath + "\"");
        return true;
    }

    private static bool ApplyRequestedChannelDetails(UpdateChannel channel, JsonElement root) {
        bool changed = false;
        changed |= SetIfPresent(value => channel.DisplayName = value, channel.DisplayName, FirstNonEmpty(GetJsonString(root, "displayName"), GetJsonString(root, "name")));
        changed |= SetIfPresent(value => channel.PublicPath = UpdateServerOptions.NormalizePublicPath(value), channel.PublicPath, GetJsonString(root, "publicPath"));
        changed |= SetIfPresent(value => channel.ManagedProcessName = value, channel.ManagedProcessName, FirstNonEmpty(GetJsonString(root, "managedProcessName"), GetJsonString(root, "processName")));
        changed |= SetIfPresent(value => channel.ManagedExecutablePath = value, channel.ManagedExecutablePath, FirstNonEmpty(GetJsonString(root, "managedExecutablePath"), GetJsonString(root, "managedExe"), GetJsonString(root, "executablePath")));
        if (TryGetJsonBool(root, out bool autoStart, "autoStartAfterUpdate", "autoRun", "autostart", "autoStart")) {
            if (channel.AutoStartAfterUpdate != autoStart) {
                channel.AutoStartAfterUpdate = autoStart;
                changed = true;
            }
        }
        return changed;
    }

    private static bool SetIfPresent(Action<string> setter, string currentValue, string requestedValue) {
        if (string.IsNullOrWhiteSpace(requestedValue))
            return false;
        requestedValue = requestedValue.Trim();
        if (string.Equals(currentValue ?? "", requestedValue, StringComparison.OrdinalIgnoreCase))
            return false;
        setter(requestedValue);
        return true;
    }

    private static bool TryApplyRequestedUpdateDirectory(UpdateChannel channel, JsonElement root, out string error, out bool changed) {
        error = "";
        changed = false;
        string requested = FirstNonEmpty(
            GetJsonString(root, "updateDirectory"),
            GetJsonString(root, "serverPath"),
            GetJsonString(root, "targetDirectory"),
            GetJsonString(root, "publishDirectory"));
        if (string.IsNullOrWhiteSpace(requested))
            return true;

        string fullPath;
        try {
            fullPath = Path.GetFullPath(requested);
        } catch (Exception ex) {
            error = "Requested update directory is invalid: " + ex.Message;
            return false;
        }

        if (!IsSafeReplaceRoot(fullPath)) {
            error = "Requested update directory is not safe to replace.";
            return false;
        }

        if (SamePath(channel.UpdateDirectory, fullPath))
            return true;

        channel.UpdateDirectory = fullPath;
        changed = true;
        return true;
    }

    private bool TryGetUpload(string uploadId, out UploadSession? session, out string error) {
        session = null;
        error = "";
        lock (_sync) {
            if (!_uploads.TryGetValue(uploadId, out session) || session == null) {
                error = "Upload session was not found.";
                return false;
            }
        }
        return true;
    }

    private static void CopyExistingUpdateFiles(string sourceRoot, string stagingRoot, HashSet<string>? desiredPaths, string channelId) {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return;

        foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)) {
            string relativePath;
            try {
                relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
            } catch {
                continue;
            }

            if (!ShouldIncludeManifestFile(relativePath, channelId))
                continue;
            if (desiredPaths != null && !desiredPaths.Contains(NormalizeRelativePath(relativePath)))
                continue;

            try {
                string destinationPath = ResolveChildPath(stagingRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? stagingRoot);
                File.Copy(sourcePath, destinationPath, true);
                File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
            } catch {
            }
        }
    }

    private static void CopyProtectedUpdateFiles(string sourceRoot, string stagingRoot, string channelId) {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            return;

        foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)) {
            string relativePath = Path.GetRelativePath(sourceRoot, sourcePath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            if (!IsProtectedServerFile(channelId, relativePath) && !IsProtectedSiblingChannelFile(channelId, relativePath))
                continue;

            string destinationPath = ResolveChildPath(stagingRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? stagingRoot);
            File.Copy(sourcePath, destinationPath, true);
            File.SetLastWriteTimeUtc(destinationPath, File.GetLastWriteTimeUtc(sourcePath));
        }
    }

    private static bool TryGetJsonStringSet(JsonElement element, string name, out HashSet<string> values) {
        values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;

            if (property.Value.ValueKind != JsonValueKind.Array)
                return true;

            foreach (JsonElement item in property.Value.EnumerateArray()) {
                string value = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString();
                value = NormalizeRelativePath(value);
                if (!string.IsNullOrWhiteSpace(value))
                    values.Add(value);
            }
            return true;
        }

        return false;
    }

    private static HashSet<int> GetJsonIntSet(JsonElement element, params string[] names) {
        var values = new HashSet<int>();
        if (element.ValueKind != JsonValueKind.Object)
            return values;

        foreach (string name in names) {
            foreach (JsonProperty property in element.EnumerateObject()) {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind == JsonValueKind.Array) {
                    foreach (JsonElement item in property.Value.EnumerateArray()) {
                        if (TryReadJsonInt(item, out int value) && value > 0)
                            values.Add(value);
                    }
                } else if (TryReadJsonInt(property.Value, out int value) && value > 0) {
                    values.Add(value);
                }
            }
        }

        return values;
    }

    private static bool TryReadJsonInt(JsonElement element, out int value) {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
            return true;
        string text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsAllowedUpdateFile(string relativePath, string channelId, out string error) {
        error = "";
        if (string.IsNullOrWhiteSpace(relativePath)) {
            error = "File path is required.";
            return false;
        }
        if (Path.IsPathRooted(relativePath) || relativePath.Contains("../", StringComparison.Ordinal) || relativePath.Equals("..", StringComparison.Ordinal)) {
            error = "File path must stay inside the update channel.";
            return false;
        }
        if (IsJackLlmWorkstationChannel(channelId) && IsBlockedJackLlmPayloadPath(relativePath, out error))
            return false;

        string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsProtectedDataSegment)) {
            error = "Data folders are not accepted by the update API.";
            return false;
        }
        string fileName = Path.GetFileName(relativePath);
        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !IsAllowedJsonUpdateFile(fileName)) {
            error = "JSON files are not accepted except .runtimeconfig.json and .deps.json.";
            return false;
        }
        return true;
    }

    private static bool IsProtectedServerFile(string channelId, string relativePath) {
        string normalized = NormalizeRelativePath(relativePath);
        if (IsMetadataFile(normalized))
            return false;
        if (IsJackLlmWorkstationChannel(channelId))
            return false;

        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsProtectedDataSegment))
            return true;

        string fileName = Path.GetFileName(normalized);
        return fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
               !IsAllowedJsonUpdateFile(fileName);
    }

    private static bool IsProtectedSiblingChannelFile(string channelId, string relativePath) {
        if (!string.Equals(UpdateServerOptions.NormalizeChannelId(channelId), "jackllm", StringComparison.OrdinalIgnoreCase))
            return false;

        string normalized = NormalizeRelativePath(relativePath);
        const string prefix = "Companion/";
        return normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
               IsAllowedUpdateFile(normalized.Substring(prefix.Length), "jackllm-companion", out _);
    }

    private static bool IsAllowedJsonUpdateFile(string fileName) {
        return fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dataserver.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJackLlmWorkstationChannel(string channelId) {
        string id = UpdateServerOptions.NormalizeChannelId(channelId);
        return id.Equals("jackllm", StringComparison.OrdinalIgnoreCase) ||
               id.Equals("jackllm-companion", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBlockedJackLlmPayloadPath(string relativePath, out string error) {
        string normalized = NormalizeRelativePath(relativePath);
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string blockedSegment = segments.FirstOrDefault(IsJackLlmRuntimeDataSegment) ?? "";
        if (!string.IsNullOrWhiteSpace(blockedSegment)) {
            error = "JackLLM update payloads cannot include runtime/user data folders: " + blockedSegment + ".";
            return true;
        }

        string fileName = Path.GetFileName(normalized);
        if (IsMetadataFile(normalized) || IsJackLlmRuntimeDataFile(fileName)) {
            error = "JackLLM update payloads cannot include runtime/user data files: " + fileName + ".";
            return true;
        }

        if (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)) {
            error = "JackLLM update payloads cannot include app/user JSON config files.";
            return true;
        }

        error = "";
        return false;
    }

    private static bool IsJackLlmRuntimeDataSegment(string segment) {
        return segment.Equals("agents", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("caches", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("config", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("configs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("database", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("databases", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("downloads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("jackllmchat", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("log", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("logs", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("models", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("completemodels", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("profiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessionfiles", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sessions", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("sockjackdml", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("temp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tmp", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("tools", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("uploads", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("userdata", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("user-data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspace", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("workspaces", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJackLlmRuntimeDataFile(string fileName) {
        if (string.IsNullOrWhiteSpace(fileName))
            return true;

        string extension = Path.GetExtension(fileName);
        return fileName.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("auth.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dynamicUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("JackLLM.settings.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("lastUpdates.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-config.json", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("updater-status.json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".bak", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cache", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".config", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".db", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".iobj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ipdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lib", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".log", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".map", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".old", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".orig", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".suo", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".user", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".wixpdb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedDataSegment(string segment) {
        return segment.Equals("data", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("database", StringComparison.OrdinalIgnoreCase) ||
               segment.Equals("databases", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeReplaceRoot(string path) {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? "";
        return !string.IsNullOrWhiteSpace(fullPath) &&
               !fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) &&
               fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length >= 8;
    }

    private static string ResolveChildPath(string root, string relativePath) {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, NormalizeRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Path escapes update root.");
        return fullPath;
    }

    private static JsonDocument ParseBody(HttpRequest request) {
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
    }

    private static bool TryParseBody(HttpRequest request, out JsonDocument? document, out string error) {
        try {
            document = ParseBody(request);
            error = "";
            return true;
        } catch (JsonException ex) {
            document = null;
            error = "Request body is not valid JSON: " + ex.Message;
            return false;
        }
    }

    private static string Json(HttpRequest request, object value, int statusCode = 200, string reasonPhrase = "OK") {
        request.Context.StatusCodeNumber = statusCode;
        request.Context.ReasonPhrase = reasonPhrase;
        AddCors(request);
        request.Context.Response.ContentType = "application/json";
        string json = JsonSerializer.Serialize(value, UpdateServerOptions.JsonOptions);
        request.Context.Response.Body = json;
        return json;
    }

    private static string Text(HttpRequest request, string text, string contentType = "text/plain; charset=utf-8", int statusCode = 200, string reasonPhrase = "OK") {
        request.Context.StatusCodeNumber = statusCode;
        request.Context.ReasonPhrase = reasonPhrase;
        AddCors(request);
        request.Context.Response.ContentType = contentType;
        request.Context.Response.Body = text;
        return text;
    }

    private static string NoContent(HttpRequest request) {
        request.Context.StatusCodeNumber = 204;
        request.Context.ReasonPhrase = "No Content";
        AddCors(request);
        request.Context.Response.BodyBytes = Array.Empty<byte>();
        return "";
    }

    private static void AddCors(HttpRequest request) {
        Dictionary<string, string> headers = request.Context.Response.Headers;
        headers["Access-Control-Allow-Origin"] = "*";
        headers["Access-Control-Allow-Methods"] = "GET, POST, DELETE, OPTIONS";
        headers["Access-Control-Allow-Headers"] = "Content-Type, Authorization, X-SocketJack-Auth";
        headers["Cache-Control"] = "no-store";
    }

    private static string ExtractToken(HttpRequest request) {
        if (request.Headers.TryGetValue("Authorization", out string? authorization) &&
            !string.IsNullOrWhiteSpace(authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return authorization.Substring("Bearer ".Length).Trim();
        if (request.Headers.TryGetValue("X-SocketJack-Auth", out string? token))
            return token.Trim();
        return "";
    }

    private static string GetJsonString(JsonElement element, string name) {
        if (element.ValueKind != JsonValueKind.Object)
            return "";
        foreach (JsonProperty property in element.EnumerateObject()) {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
        }
        return "";
    }

    private static long GetJsonLong(JsonElement element, string name, long fallback) {
        return long.TryParse(GetJsonString(element, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : fallback;
    }

    private static bool GetJsonBool(JsonElement element, string name, bool fallback) {
        return bool.TryParse(GetJsonString(element, name), out bool value) ? value : fallback;
    }

    private static bool TryGetJsonBool(JsonElement element, out bool value, params string[] names) {
        foreach (string name in names) {
            string text = GetJsonString(element, name);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            if (bool.TryParse(text, out value))
                return true;
            if (text.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("on", StringComparison.OrdinalIgnoreCase)) {
                value = true;
                return true;
            }
            if (text.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("no", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("n", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("off", StringComparison.OrdinalIgnoreCase)) {
                value = false;
                return true;
            }
        }
        value = false;
        return false;
    }

    private static string Query(HttpRequest request, string name) {
        if (request.QueryParameters != null && request.QueryParameters.TryGetValue(name, out string? value))
            return value ?? "";
        return "";
    }

    private static bool HasQueryValue(HttpRequest request, string name) {
        return request.QueryParameters != null &&
               request.QueryParameters.TryGetValue(name, out string? value) &&
               value != null;
    }

    private static long ParseLong(string value, long fallback) {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) ? parsed : fallback;
    }

    private static bool ParseBool(string value, bool fallback) {
        return bool.TryParse(value, out bool parsed) ? parsed : fallback;
    }

    private static string FirstNonEmpty(params string?[] values) => UpdateServerOptions.FirstNonEmpty(values);

    private static string NormalizeRelativePath(string path) {
        path = Uri.UnescapeDataString((path ?? "").Replace("+", "%20")).Replace('\\', '/').Trim('/');
        return path == "." ? "" : path;
    }

    private static string NormalizeRoutePath(string path) {
        path = (path ?? "").Trim().Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;
        while (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
            path = path.Substring(0, path.Length - 1);
        return path;
    }

    private string RemovePublicBasePath(string path) {
        path = NormalizeRoutePath(path);
        string basePath = PublicBasePath;
        if (string.IsNullOrWhiteSpace(basePath))
            return path;
        if (path.Equals(basePath, StringComparison.OrdinalIgnoreCase))
            return "/";
        return path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)
            ? path.Substring(basePath.Length)
            : path;
    }

    private static string GetPublicBasePath(string publicUrl) {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out Uri? uri))
            return "";
        return NormalizeRoutePath(uri.AbsolutePath) == "/" ? "" : NormalizeRoutePath(uri.AbsolutePath);
    }

    private static X509Certificate2 LoadSslCertificate(string certificatePath, string certificatePassword) {
        return LoadSslCertificate(certificatePath, "", certificatePassword, "", "SocketJack Update HTTPS");
    }

    private static X509Certificate2 LoadSslCertificate(string certificatePath, string keyPath, string certificatePassword, string subject, string description) {
        if (string.IsNullOrWhiteSpace(certificatePath))
            certificatePath = "";
        if (string.IsNullOrWhiteSpace(keyPath))
            keyPath = "";
        string password = ResolveCertificatePassword(certificatePath, keyPath, certificatePassword, "");
        var flags = X509KeyStorageFlags.MachineKeySet |
                    X509KeyStorageFlags.PersistKeySet |
                    X509KeyStorageFlags.Exportable;

        foreach (string pfxPath in GetPfxCandidates(certificatePath, subject)) {
            if (!File.Exists(pfxPath))
                continue;

            X509Certificate2 candidate = string.IsNullOrEmpty(password)
                ? new X509Certificate2(pfxPath, (string?)null, flags)
                : new X509Certificate2(pfxPath, password, flags);
            if (IsCertificateUsable(candidate, subject, description))
                return candidate;
            candidate.Dispose();
        }

        if (File.Exists(certificatePath)) {
            if (File.Exists(keyPath)) {
                X509Certificate2 certificate = string.IsNullOrEmpty(password)
                    ? X509Certificate2.CreateFromPemFile(certificatePath, keyPath)
                    : X509Certificate2.CreateFromEncryptedPemFile(certificatePath, password, keyPath);
                return ReimportCertificate(certificate, password, flags, subject, description);
            }

            var publicCertificate = new X509Certificate2(certificatePath);
            if (publicCertificate.HasPrivateKey)
                return ReimportCertificate(publicCertificate, password, flags, subject, description);

            X509Certificate2? storeMatch = FindStoreCertificateWithPrivateKey(publicCertificate, subject, description);
            if (storeMatch != null)
                return storeMatch;
        }

        X509Certificate2? subjectMatch = FindStoreCertificateWithPrivateKey(null, subject, description);
        if (subjectMatch != null)
            return subjectMatch;

        throw new InvalidOperationException(
            "Unable to load a " + description + " with an accessible private key. " +
            "Configure a PFX, configure a PEM certificate/key pair, " +
            "or install a matching certificate with its private key in the Windows LocalMachine or CurrentUser Personal certificate store.");
    }

    private static IEnumerable<string> GetPfxCandidates(string certificatePath, string subject) {
        if (!string.IsNullOrWhiteSpace(certificatePath)) {
            certificatePath = Path.GetFullPath(certificatePath);
            string extension = Path.GetExtension(certificatePath);
            if (string.Equals(extension, ".pfx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".p12", StringComparison.OrdinalIgnoreCase))
                yield return certificatePath;

            string? directory = Path.GetDirectoryName(certificatePath);
            string name = Path.GetFileNameWithoutExtension(certificatePath);
            if (!string.IsNullOrWhiteSpace(directory) && !string.IsNullOrWhiteSpace(name)) {
                yield return Path.Combine(directory, name + ".pfx");
                yield return Path.Combine(directory, name + ".p12");
            }
        }

        string normalizedSubject = SanitizeCertificateFileName(subject);
        if (!string.IsNullOrWhiteSpace(normalizedSubject)) {
            yield return @"C:\" + normalizedSubject + ".pfx";
            yield return @"C:\" + normalizedSubject + ".p12";
        }
    }

    private static X509Certificate2 ReimportCertificate(X509Certificate2 certificate, string password, X509KeyStorageFlags flags, string subject, string description) {
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The configured " + description + " does not include a private key.");

        byte[] pfx = certificate.Export(X509ContentType.Pfx, password);
        certificate.Dispose();
        var imported = new X509Certificate2(pfx, password, flags);
        if (!IsCertificateUsable(imported, subject, description)) {
            imported.Dispose();
            throw new InvalidOperationException("The configured " + description + " is not usable for " + subject + ".");
        }
        return imported;
    }

    private static bool IsCertificateUsable(X509Certificate2 certificate, string subject, string description) {
        if (certificate == null || !certificate.HasPrivateKey)
            return false;
        if (certificate.NotAfter <= DateTime.Now)
            return false;
        if (!string.IsNullOrWhiteSpace(subject) && !IsCertificateMatch(certificate, null, subject))
            return false;

        try {
            using RSA? rsa = certificate.GetRSAPrivateKey();
            using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
            if (rsa == null && ecdsa == null)
                throw new InvalidOperationException("The certificate private key is neither RSA nor ECDSA.");
        } catch (Exception ex) {
            throw new InvalidOperationException(
                "The " + description + " private key could not be opened by this process. " +
                "Check the PFX password and private-key permissions.", ex);
        }

        return true;
    }

    private static X509Certificate2? FindStoreCertificateWithPrivateKey(X509Certificate2? publicCertificate, string subject, string description) {
        foreach (StoreLocation location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser }) {
            using var store = new X509Store(StoreName.My, location);
            try {
                store.Open(OpenFlags.ReadOnly);
            } catch {
                continue;
            }

            X509Certificate2? certificate = store.Certificates
                .OfType<X509Certificate2>()
                .Where(candidate => candidate.HasPrivateKey && IsCertificateMatch(candidate, publicCertificate, subject))
                .Where(candidate => candidate.NotAfter > DateTime.Now)
                .OrderByDescending(candidate => candidate.NotAfter)
                .FirstOrDefault();
            if (certificate != null && IsCertificateUsable(certificate, subject, description))
                return new X509Certificate2(certificate);
        }

        return null;
    }

    private static bool IsCertificateMatch(X509Certificate2 candidate, X509Certificate2? publicCertificate, string subject) {
        if (publicCertificate != null &&
            string.Equals(candidate.Thumbprint, publicCertificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
            return true;

        subject = UpdateServerOptions.NormalizeHostName(subject);
        if (string.IsNullOrWhiteSpace(subject))
            return true;

        string dnsName = candidate.GetNameInfo(X509NameType.DnsName, false);
        return string.Equals(dnsName, subject, StringComparison.OrdinalIgnoreCase) ||
               candidate.Subject.IndexOf(subject, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string SanitizeCertificateFileName(string subject) {
        subject = (subject ?? "").Trim().ToLowerInvariant();
        if (subject.Length == 0)
            return "";

        var builder = new StringBuilder(subject.Length);
        foreach (char ch in subject) {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }
        return builder.ToString().Trim('_');
    }

    private static string ResolveCertificatePassword(string certificatePath, string keyPath, string configuredPassword, string passwordPath) {
        if (!string.IsNullOrEmpty(configuredPassword))
            return configuredPassword;
        if (!string.IsNullOrWhiteSpace(passwordPath) && File.Exists(passwordPath))
            return File.ReadAllText(passwordPath).Trim();

        string certificateExtension = Path.GetExtension(certificatePath ?? "");
        bool certificateIsPfx =
            string.Equals(certificateExtension, ".pfx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(certificateExtension, ".p12", StringComparison.OrdinalIgnoreCase);
        if (certificateIsPfx && !string.IsNullOrWhiteSpace(keyPath) && File.Exists(keyPath))
            return File.ReadAllText(keyPath).Trim();

        return "";
    }

    private static string CombineUrl(string baseUrl, string relativePath) {
        baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://socketjack.com/SecureAuthority/" : baseUrl.Trim();
        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
            baseUrl += "/";
        return baseUrl + (relativePath ?? "").TrimStart('/').Replace("\\", "/");
    }

    private static byte[] RandomBytes(int count) {
        byte[] bytes = new byte[count];
        RandomNumberGenerator.Fill(bytes);
        return bytes;
    }

    private static string GenerateToken() => Convert.ToBase64String(RandomBytes(48)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashToken(string token) => ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));
    private static string HashPassword(string password, byte[] salt) => ToHex(Rfc2898DeriveBytes.Pbkdf2(password, salt, 120000, HashAlgorithmName.SHA256, 32));
    private static string ComputeSha256Hex(string filePath) {
        using FileStream stream = File.OpenRead(filePath);
        return ToHex(SHA256.HashData(stream));
    }
    private static DateTimeOffset GetFileLastWriteUtc(string filePath) {
        DateTime utc = DateTime.SpecifyKind(File.GetLastWriteTimeUtc(filePath), DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToUniversalTime();
    }
    private static bool ServerFileIsNewerThanPublisher(DateTimeOffset serverLastWriteUtc, DateTimeOffset publisherLastWriteUtc) {
        return serverLastWriteUtc.ToUniversalTime() - publisherLastWriteUtc.ToUniversalTime() > TimestampTolerance;
    }
    private static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    private static bool ConstantTimeEquals(string left, string right) {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left ?? "");
        byte[] rightBytes = Encoding.UTF8.GetBytes(right ?? "");
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
    private static bool FileRangeMatches(FileStream stream, long offset, byte[] expected) {
        if (expected == null)
            expected = Array.Empty<byte>();
        if (offset < 0 || stream.Length < offset + expected.LongLength)
            return false;

        byte[] buffer = new byte[Math.Min(64 * 1024, Math.Max(1, expected.Length))];
        int compared = 0;
        stream.Position = offset;
        while (compared < expected.Length) {
            int requested = Math.Min(buffer.Length, expected.Length - compared);
            int read = stream.Read(buffer, 0, requested);
            if (read != requested)
                return false;
            if (!buffer.AsSpan(0, read).SequenceEqual(expected.AsSpan(compared, read)))
                return false;
            compared += read;
        }
        return true;
    }
    private static long ParseUnixTime(string value) {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToUnixTimeSeconds()
            : 0;
    }
    private static bool TryParseUtc(string value, out DateTimeOffset parsed) {
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
            return true;
        parsed = default;
        return false;
    }
    private static void TryDeleteDirectory(string path) {
        try {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        } catch {
        }
    }
    private static void TryDeleteFile(string path) {
        try {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        } catch {
        }
    }

    public void Dispose() {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _server.Dispose();
        _publicHttpServer?.Dispose();
        _publicHttpsServer?.Dispose();
    }

    private sealed record ForwardRoute(Uri TargetUri, bool PreserveHostHeader, string ServiceName, string OfflineSummary, string PathPrefix);

    private sealed class PublicDomainForwardingHandler : IProtocolHandler {
        private const int MaxHeaderBytes = 64 * 1024;
        private static readonly string[] HttpMethods = {
            "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE", "CONNECT"
        };

        private readonly Dictionary<string, List<ForwardRoute>> _routes;
        private readonly bool _redirectToHttps;
        private readonly int _httpsPort;
        private readonly Action<string, string> _log;
        private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();

        public PublicDomainForwardingHandler(Dictionary<string, List<ForwardRoute>> routes, bool redirectToHttps, int httpsPort, Action<string, string> log) {
            _routes = routes;
            _redirectToHttps = redirectToHttps;
            _httpsPort = httpsPort;
            _log = log;
        }

        public string Name => _redirectToHttps ? "PublicDomainHttpRedirect" : "PublicDomainHttpsForward";

        public bool CanHandle(byte[] data) {
            if (data == null || data.Length == 0)
                return false;
            int length = Math.Min(data.Length, 12);
            string prefix = Encoding.ASCII.GetString(data, 0, length);
            return HttpMethods.Any(method =>
                prefix.StartsWith(method + " ", StringComparison.OrdinalIgnoreCase) ||
                (method + " ").StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[]? incoming = TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            PendingRequest pending = _pending.GetOrAdd(connection.ID, _ => new PendingRequest());
            byte[]? requestBytes = null;
            lock (pending.Sync) {
                if (pending.Started)
                    return;
                pending.Buffer.AddRange(incoming);
                int headerEnd = FindHeaderEnd(pending.Buffer);
                if (headerEnd < 0) {
                    if (pending.Buffer.Count > MaxHeaderBytes) {
                        pending.Started = true;
                        requestBytes = Array.Empty<byte>();
                    }
                    return;
                }

                pending.Started = true;
                requestBytes = pending.Buffer.ToArray();
            }

            connection.RawTcpMode = true;
            connection.SuppressConnectionTest = true;
            _ = Task.Run(() => HandleRequestAsync(server, connection, requestBytes));
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            if (connection != null)
                _pending.TryRemove(connection.ID, out _);
        }

        private async Task HandleRequestAsync(MutableTcpServer server, NetworkConnection connection, byte[] requestBytes) {
            try {
                if (requestBytes.Length == 0) {
                    await WriteSimpleResponseAsync(connection, 431, "Request Header Fields Too Large", "Request header is too large.").ConfigureAwait(false);
                    return;
                }

                if (!TryParseRequestHead(requestBytes, out RequestHead request, out string parseError)) {
                    await WriteSimpleResponseAsync(connection, 400, "Bad Request", parseError).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(request.HostName) ||
                    !_routes.TryGetValue(request.HostName, out List<ForwardRoute>? hostRoutes) ||
                    !TrySelectRoute(hostRoutes, request, out ForwardRoute? route) ||
                    route == null) {
                    await WriteSimpleResponseAsync(connection, 421, "Misdirected Request", "421 Misdirected Request").ConfigureAwait(false);
                    return;
                }

                if (_redirectToHttps) {
                    string location = BuildHttpsRedirectLocation(request, _httpsPort);
                    await WriteRedirectAsync(connection, location).ConfigureAwait(false);
                    return;
                }

                try {
                    await ProxyAsync(connection, requestBytes, request, route).ConfigureAwait(false);
                } catch (Exception ex) {
                    _log("public-forward", "upstream offline service=\"" + SanitizeLogValue(route.ServiceName) + "\" host=\"" + SanitizeLogValue(request.HostName) + "\" exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
                    await WriteOfflineResponseAsync(connection, request, route).ConfigureAwait(false);
                }
            } catch (Exception ex) {
                _log("public-forward", "request failed exception=" + ex.GetType().Name + " detail=\"" + ex.Message + "\"");
                try {
                    await WriteSimpleResponseAsync(connection, 502, "Bad Gateway", "The public forwarding layer could not complete this request.").ConfigureAwait(false);
                } catch {
                }
            } finally {
                _pending.TryRemove(connection.ID, out _);
                try { connection.CloseConnection(); } catch { }
            }
        }

        private static bool TrySelectRoute(IReadOnlyList<ForwardRoute> routes, RequestHead request, out ForwardRoute? route) {
            string path = GetRequestPath(request.Target);
            foreach (ForwardRoute candidate in routes) {
                if (PathMatches(candidate.PathPrefix, path)) {
                    route = candidate;
                    return true;
                }
            }

            route = null;
            return false;
        }

        private static bool PathMatches(string prefix, string path) {
            prefix = UpdateServerOptions.NormalizeForwardPathPrefix(prefix);
            path = GetRequestPath(path);
            if (prefix == "/")
                return true;
            return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetRequestPath(string requestTarget) {
            requestTarget = requestTarget ?? "/";
            if (Uri.TryCreate(requestTarget, UriKind.Absolute, out Uri? absolute))
                requestTarget = absolute.AbsolutePath;
            int query = requestTarget.IndexOf('?');
            if (query >= 0)
                requestTarget = requestTarget.Substring(0, query);
            int hash = requestTarget.IndexOf('#');
            if (hash >= 0)
                requestTarget = requestTarget.Substring(0, hash);
            if (string.IsNullOrWhiteSpace(requestTarget) || requestTarget == "*")
                return "/";
            return requestTarget.StartsWith("/", StringComparison.Ordinal) ? requestTarget : "/" + requestTarget;
        }

        private static async Task WriteOfflineResponseAsync(NetworkConnection connection, RequestHead request, ForwardRoute route) {
            string checkedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
            string service = WebUtility.HtmlEncode(FirstNonEmpty(route.ServiceName, request.HostName, "SocketJack service"));
            string host = WebUtility.HtmlEncode(FirstNonEmpty(request.HostName, "requested host"));
            string summary = WebUtility.HtmlEncode(FirstNonEmpty(route.OfflineSummary, "The backing service is not responding right now."));
            string html = "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">" +
                "<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
                "<title>" + service + " Offline</title>" +
                "<style>" +
                ":root{color-scheme:dark}body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0d1117;color:#e6edf3;font-family:Segoe UI,Arial,sans-serif}" +
                "main{width:min(720px,calc(100vw - 32px));border:1px solid #30363d;background:#161b22;padding:28px;box-sizing:border-box}" +
                "h1{margin:0 0 10px;font-size:28px;font-weight:650;letter-spacing:0}p{margin:0 0 18px;line-height:1.55;color:#c9d1d9}" +
                "dl{display:grid;grid-template-columns:max-content 1fr;gap:10px 18px;margin:22px 0 0}dt{color:#8b949e}dd{margin:0;color:#f0f6fc;word-break:break-word}" +
                ".status{display:inline-block;margin-bottom:18px;padding:5px 9px;border:1px solid #f85149;color:#ffb3ad;background:#2d1110;font-size:13px}" +
                "</style></head><body><main>" +
                "<div class=\"status\">503 Service Unavailable</div>" +
                "<h1>" + service + " is offline</h1>" +
                "<p>" + summary + " This usually means the process is stopped, restarting, or temporarily unreachable.</p>" +
                "<dl><dt>Public host</dt><dd>" + host + "</dd><dt>Status</dt><dd>Offline</dd><dt>Checked</dt><dd>" + checkedUtc + "</dd></dl>" +
                "</main></body></html>";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(html);
            string headers =
                "HTTP/1.1 503 Service Unavailable\r\n" +
                "Content-Type: text/html; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Retry-After: 30\r\n" +
                "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            await WriteRawResponseAsync(connection, headers, bodyBytes).ConfigureAwait(false);
        }

        private static async Task ProxyAsync(NetworkConnection connection, byte[] requestBytes, RequestHead request, ForwardRoute route) {
            using var upstream = new System.Net.Sockets.TcpClient();
            upstream.NoDelay = true;
            await upstream.ConnectAsync(route.TargetUri.Host, GetPort(route.TargetUri)).ConfigureAwait(false);

            Stream upstreamStream = upstream.GetStream();
            SslStream? upstreamSsl = null;
            if (route.TargetUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) {
                upstreamSsl = new SslStream(upstreamStream, leaveInnerStreamOpen: false);
                await upstreamSsl.AuthenticateAsClientAsync(route.TargetUri.Host).ConfigureAwait(false);
                upstreamStream = upstreamSsl;
            }

            try {
                byte[] forwardedRequest = BuildForwardedRequestBytes(requestBytes, request, route, connection);
                await upstreamStream.WriteAsync(forwardedRequest, 0, forwardedRequest.Length).ConfigureAwait(false);
                await upstreamStream.FlushAsync().ConfigureAwait(false);

                Stream clientStream = connection.Stream;
                Task uploadTask = CopyClientToUpstreamAsync(clientStream, upstreamStream, connection, upstream);
                Task downloadTask = CopyUpstreamToClientAsync(upstreamStream, clientStream, connection);

                if (request.IsWebSocketUpgrade)
                    await Task.WhenAny(uploadTask, downloadTask).ConfigureAwait(false);
                else
                    await downloadTask.ConfigureAwait(false);
            } finally {
                upstreamSsl?.Dispose();
            }
        }

        private static async Task CopyClientToUpstreamAsync(Stream clientStream, Stream upstreamStream, NetworkConnection connection, System.Net.Sockets.TcpClient upstream) {
            byte[] buffer = new byte[81920];
            try {
                while (true) {
                    int read = await clientStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    await upstreamStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                    await upstreamStream.FlushAsync().ConfigureAwait(false);
                    connection.TrackBytesReceived(read);
                }
            } catch {
            } finally {
                try { upstream.Client.Shutdown(SocketShutdown.Send); } catch { }
            }
        }

        private static async Task CopyUpstreamToClientAsync(Stream upstreamStream, Stream clientStream, NetworkConnection connection) {
            byte[] buffer = new byte[81920];
            while (true) {
                int read = await upstreamStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read <= 0)
                    break;
                await clientStream.WriteAsync(buffer, 0, read).ConfigureAwait(false);
                await clientStream.FlushAsync().ConfigureAwait(false);
                connection.TrackBytesSent(read);
            }
        }

        private static byte[] BuildForwardedRequestBytes(byte[] requestBytes, RequestHead request, ForwardRoute route, NetworkConnection connection) {
            var sb = new StringBuilder();
            sb.Append(request.Method);
            sb.Append(' ');
            sb.Append(BuildUpstreamRequestTarget(route.TargetUri, request.Target));
            sb.Append(' ');
            sb.Append(string.IsNullOrWhiteSpace(request.Version) ? "HTTP/1.1" : request.Version);
            sb.Append("\r\n");

            string hostHeader = route.PreserveHostHeader && !string.IsNullOrWhiteSpace(request.RawHost)
                ? SanitizeHeaderValue(request.RawHost)
                : BuildOriginHostHeader(route.TargetUri);
            sb.Append("Host: ").Append(hostHeader).Append("\r\n");

            foreach ((string name, string value) in request.Headers) {
                if (ShouldSkipForwardedRequestHeader(name, request.IsWebSocketUpgrade))
                    continue;
                sb.Append(name).Append(": ").Append(SanitizeHeaderValue(value)).Append("\r\n");
            }

            string clientIp = "";
            try { clientIp = connection.EndPoint?.Address?.ToString() ?? ""; } catch { }
            if (!string.IsNullOrWhiteSpace(request.RawHost))
                sb.Append("X-Forwarded-Host: ").Append(SanitizeHeaderValue(request.RawHost)).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(clientIp))
                sb.Append("X-Forwarded-For: ").Append(SanitizeHeaderValue(clientIp)).Append("\r\n");
            sb.Append("X-Forwarded-Proto: https\r\n");
            sb.Append(request.IsWebSocketUpgrade ? "Connection: Upgrade\r\n" : "Connection: close\r\n");
            sb.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
            int bodyLength = Math.Max(0, requestBytes.Length - request.HeaderLength);
            byte[] output = new byte[headerBytes.Length + bodyLength];
            Buffer.BlockCopy(headerBytes, 0, output, 0, headerBytes.Length);
            if (bodyLength > 0)
                Buffer.BlockCopy(requestBytes, request.HeaderLength, output, headerBytes.Length, bodyLength);
            return output;
        }

        private static bool ShouldSkipForwardedRequestHeader(string headerName, bool isWebSocketUpgrade) {
            if (string.IsNullOrWhiteSpace(headerName))
                return true;

            return headerName.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("X-Forwarded-Host", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("X-Forwarded-For", StringComparison.OrdinalIgnoreCase) ||
                   headerName.Equals("X-Forwarded-Proto", StringComparison.OrdinalIgnoreCase) ||
                   (!isWebSocketUpgrade && headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParseRequestHead(byte[] requestBytes, out RequestHead request, out string error) {
            request = new RequestHead();
            error = "";
            int headerLength = FindHeaderEnd(requestBytes);
            if (headerLength < 0) {
                error = "HTTP headers are incomplete.";
                return false;
            }

            string headerText = Encoding.ASCII.GetString(requestBytes, 0, headerLength);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || string.IsNullOrWhiteSpace(lines[0])) {
                error = "HTTP request line is missing.";
                return false;
            }

            string[] requestLine = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length < 2) {
                error = "HTTP request line is invalid.";
                return false;
            }

            request.Method = requestLine[0];
            request.Target = requestLine[1];
            request.Version = requestLine.Length > 2 ? requestLine[2] : "HTTP/1.1";
            request.HeaderLength = headerLength;

            foreach (string line in lines.Skip(1)) {
                if (string.IsNullOrEmpty(line))
                    break;
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;

                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                request.Headers.Add((name, value));
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) {
                    request.RawHost = value;
                    request.HostName = UpdateServerOptions.NormalizeHostName(value);
                } else if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase) &&
                           value.Equals("websocket", StringComparison.OrdinalIgnoreCase)) {
                    request.IsWebSocketUpgrade = true;
                }
            }

            return true;
        }

        private static string BuildUpstreamRequestTarget(Uri targetUri, string requestTarget) {
            string pathAndQuery = requestTarget ?? "/";
            if (Uri.TryCreate(pathAndQuery, UriKind.Absolute, out Uri? absolute))
                pathAndQuery = absolute.PathAndQuery;
            if (string.IsNullOrWhiteSpace(pathAndQuery))
                pathAndQuery = "/";
            if (pathAndQuery != "*" && !pathAndQuery.StartsWith("/", StringComparison.Ordinal))
                pathAndQuery = "/" + pathAndQuery;

            string basePath = string.IsNullOrWhiteSpace(targetUri.AbsolutePath) || targetUri.AbsolutePath == "/"
                ? ""
                : targetUri.AbsolutePath.TrimEnd('/');
            return pathAndQuery == "*" || string.IsNullOrWhiteSpace(basePath)
                ? pathAndQuery
                : basePath + pathAndQuery;
        }

        private static string BuildHttpsRedirectLocation(RequestHead request, int httpsPort) {
            string host = UpdateServerOptions.NormalizeHostName(request.RawHost);
            if (string.IsNullOrWhiteSpace(host))
                host = request.HostName;
            string port = httpsPort == 443 ? "" : ":" + httpsPort.ToString(CultureInfo.InvariantCulture);
            return "https://" + host + port + BuildRedirectPath(request.Target);
        }

        private static string BuildRedirectPath(string requestTarget) {
            if (Uri.TryCreate(requestTarget, UriKind.Absolute, out Uri? absolute))
                return string.IsNullOrWhiteSpace(absolute.PathAndQuery) ? "/" : absolute.PathAndQuery;
            if (string.IsNullOrWhiteSpace(requestTarget) || requestTarget == "*")
                return "/";
            return requestTarget.StartsWith("/", StringComparison.Ordinal) ? requestTarget : "/" + requestTarget;
        }

        private static string BuildOriginHostHeader(Uri targetUri) {
            int port = GetPort(targetUri);
            bool defaultPort = targetUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                ? port == 80
                : port == 443;
            return defaultPort ? targetUri.Host : targetUri.Host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static int GetPort(Uri uri) {
            return uri.Port > 0 ? uri.Port : (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80);
        }

        private static async Task WriteRedirectAsync(NetworkConnection connection, string location) {
            string body = "Redirecting to " + location;
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers =
                "HTTP/1.1 308 Permanent Redirect\r\n" +
                "Location: " + SanitizeHeaderValue(location) + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            await WriteRawResponseAsync(connection, headers, bodyBytes).ConfigureAwait(false);
        }

        private static async Task WriteSimpleResponseAsync(NetworkConnection connection, int statusCode, string reason, string body) {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body ?? "");
            string headers =
                "HTTP/1.1 " + statusCode.ToString(CultureInfo.InvariantCulture) + " " + reason + "\r\n" +
                "Content-Type: text/plain; charset=utf-8\r\n" +
                "Cache-Control: no-store\r\n" +
                "Content-Length: " + bodyBytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Connection: close\r\n\r\n";
            await WriteRawResponseAsync(connection, headers, bodyBytes).ConfigureAwait(false);
        }

        private static async Task WriteRawResponseAsync(NetworkConnection connection, string headers, byte[] bodyBytes) {
            Stream stream = connection.Stream;
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            connection.TrackBytesSent(headerBytes.Length + bodyBytes.Length);
        }

        private static string SanitizeHeaderValue(string value) {
            return (value ?? "").Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();
        }

        private static string SanitizeLogValue(string value) {
            return (value ?? "").Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Trim();
        }

        private static int FindHeaderEnd(List<byte> buffer) {
            for (int i = 0; i < buffer.Count - 3; i++) {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i + 4;
            }
            return -1;
        }

        private static int FindHeaderEnd(byte[] buffer) {
            for (int i = 0; i < buffer.Length - 3; i++) {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                    return i + 4;
            }
            return -1;
        }

        private static byte[]? TryGetRawBytes(object value) {
            return value switch {
                byte[] bytes => bytes,
                List<byte> list => list.ToArray(),
                string text => Encoding.UTF8.GetBytes(text),
                _ => null
            };
        }

        private sealed class PendingRequest {
            public object Sync { get; } = new();
            public List<byte> Buffer { get; } = new();
            public bool Started { get; set; }
        }

        private sealed class RequestHead {
            public string Method { get; set; } = "";
            public string Target { get; set; } = "/";
            public string Version { get; set; } = "HTTP/1.1";
            public string RawHost { get; set; } = "";
            public string HostName { get; set; } = "";
            public int HeaderLength { get; set; }
            public bool IsWebSocketUpgrade { get; set; }
            public List<(string Name, string Value)> Headers { get; } = new();
        }
    }

    private sealed class PublishCompletionException : IOException {
        public PublishCompletionException(string step, Exception innerException)
            : base("Publish completion failed during " + step + ": " + innerException.Message, innerException) {
            Step = step;
        }

        public string Step { get; }
    }

    private sealed class DirectoryLockDiagnostics {
        public string Directory { get; set; } = "";
        public bool Exists { get; set; }
        public List<string> Processes { get; set; } = new();
        public List<LockingProcessInfo> ProcessDetails { get; set; } = new();
        public List<string> LockedFiles { get; set; } = new();
    }

    private sealed class LockedFileInfo {
        public string FullPath { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string Error { get; set; } = "";
        public string Display => string.IsNullOrWhiteSpace(Error) ? RelativePath : RelativePath + " (" + Error + ")";
    }

    private sealed class LockingProcessInfo {
        public string Name { get; set; } = "";
        public int Id { get; set; }
        public string ExecutablePath { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool CanStop { get; set; } = true;
        public bool RestartAfterUpdate { get; set; }
        public string Display => FormatProcessLabel(Name, Id) +
                                 (string.IsNullOrWhiteSpace(ExecutablePath) ? "" : " exe=" + ExecutablePath) +
                                 (string.IsNullOrWhiteSpace(Reason) ? "" : " " + Reason);
    }

    private sealed class RestartProcessSpec {
        public string ProcessName { get; set; } = "";
        public string ExecutablePath { get; set; } = "";
        public string WorkingDirectory { get; set; } = "";
    }

    private sealed class LastUpdateSettings {
        public List<LastUpdateRecord> Updates { get; set; } = new();
    }

    private sealed class LastUpdateRecord {
        public string ChannelId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string UpdateDirectory { get; set; } = "";
        public string UploadId { get; set; } = "";
        public string LastUpdateUtc { get; set; } = "";
        public int FileCount { get; set; }
        public long ByteCount { get; set; }
    }

    private sealed class AuthState {
        public string UserName { get; set; } = "";
        public string PasswordSalt { get; set; } = "";
        public string PasswordHash { get; set; } = "";
        public string TokenHash { get; set; } = "";
        public string TokenExpiresUtc { get; set; } = "";
    }

    private sealed class UploadSession {
        public string Id { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string StagingRoot { get; set; } = "";
        public string ArchivePath { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public int FileCount { get; set; }
        public long ByteCount { get; set; }
        public int SkippedFileCount { get; set; }
        public long SkippedByteCount { get; set; }
        public bool ArchiveExtracted { get; set; }
        public long ArchiveLength { get; set; }
        public string ArchiveSha256 { get; set; } = "";
        public int ArchiveExtractedFiles { get; set; }
        public long ArchiveExtractedBytes { get; set; }
        public HashSet<string> SkippedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, RestartProcessSpec> ExtraRestartProcesses { get; } = new(StringComparer.OrdinalIgnoreCase);
        public object Sync { get; } = new();
    }

    private const int ErrorMoreData = 234;
    private const int RmRebootReasonNone = 0;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, StringBuilder strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[]? rgsFilenames,
        uint nApplications,
        [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        out uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }
}
