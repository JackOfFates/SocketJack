using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SocketJack.Update;

public sealed class UpdateServerOptions {
    private const string LoopbackBindHost = "127.0.0.1";
    public string BindHost { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8500;
    public string DataDirectory { get; set; } = @"C:\SocketJackUpdate";
    public PublicForwardingOptions PublicForwarding { get; set; } = PublicForwardingOptions.CreateDefault();
    public List<UpdateChannel> Channels { get; set; } = new();

    [JsonIgnore]
    public string PublicUrl => BuildPublicUrl();

    public IPAddress BindAddress {
        get {
            string bindHost = NormalizeLoopbackBindHost(BindHost);
            if (IPAddress.TryParse(bindHost, out IPAddress? address))
                return address;
            try {
                IPAddress[] addresses = Dns.GetHostAddresses(bindHost);
                return addresses.FirstOrDefault(IPAddress.IsLoopback) ?? IPAddress.Loopback;
            } catch {
                return IPAddress.Loopback;
            }
        }
    }

    private string BuildPublicUrl() {
        return "http://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/SecureAuthority/";
    }

    public static UpdateServerOptions Load() {
        string path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        UpdateServerOptions options = File.Exists(path)
            ? JsonSerializer.Deserialize<UpdateServerOptions>(File.ReadAllText(path), JsonOptions) ?? new UpdateServerOptions()
            : new UpdateServerOptions();

        options.BindHost = NormalizeLoopbackBindHost(FirstNonEmpty(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_BIND_HOST"), options.BindHost, LoopbackBindHost));
        options.DataDirectory = FirstNonEmpty(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_DATA_DIR"), options.DataDirectory, @"C:\SocketJackUpdate");
        if (int.TryParse(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PORT"), out int port) && port > 0 && port <= 65535)
            options.Port = port;

        options.PublicForwarding ??= PublicForwardingOptions.CreateDefault();
        NormalizePublicForwarding(options.PublicForwarding);
        DisablePublicForwarding(options.PublicForwarding);
        AddMissingDefaultChannels(options.Channels);
        ApplyDynamicChannels(options.Channels, options.DataDirectory);

        foreach (UpdateChannel channel in options.Channels) {
            NormalizeChannel(channel, options.DataDirectory);
        }

        return options;
    }

    private static void DisablePublicForwarding(PublicForwardingOptions? forwarding) {
        if (forwarding == null)
            return;

        forwarding.Enabled = false;
        forwarding.HttpPort = 0;
        forwarding.HttpsPort = 0;
        forwarding.Certificates ??= new List<PublicCertificateOptions>();
        forwarding.Routes ??= new List<PublicForwardRouteOptions>();
        forwarding.Certificates.Clear();
        forwarding.Routes.Clear();
    }

    private static void NormalizePublicForwarding(PublicForwardingOptions? forwarding) {
        if (forwarding == null)
            return;

        forwarding.Enabled = GetBool(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_FORWARDING_ENABLED"), forwarding.Enabled);
        forwarding.HttpBindHost = NormalizePublicBindHost(FirstNonEmpty(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_HTTP_BIND_HOST"), forwarding.HttpBindHost, "0.0.0.0"));
        forwarding.HttpsBindHost = NormalizePublicBindHost(FirstNonEmpty(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_HTTPS_BIND_HOST"), forwarding.HttpsBindHost, forwarding.HttpBindHost, "0.0.0.0"));
        forwarding.DefaultCertificateHost = NormalizeHostName(FirstNonEmpty(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_DEFAULT_CERT_HOST"), forwarding.DefaultCertificateHost, "socketjack.com"));

        if (int.TryParse(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_HTTP_PORT"), out int httpPort) && httpPort > 0 && httpPort <= 65535)
            forwarding.HttpPort = httpPort;
        if (int.TryParse(Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_PUBLIC_HTTPS_PORT"), out int httpsPort) && httpsPort > 0 && httpsPort <= 65535)
            forwarding.HttpsPort = httpsPort;

        forwarding.Certificates ??= new List<PublicCertificateOptions>();
        forwarding.Routes ??= new List<PublicForwardRouteOptions>();

        if (forwarding.Certificates.Count == 0)
            forwarding.Certificates.AddRange(PublicForwardingOptions.DefaultCertificates());
        if (forwarding.Routes.Count == 0)
            forwarding.Routes.AddRange(PublicForwardingOptions.DefaultRoutes());

        foreach (PublicCertificateOptions certificate in forwarding.Certificates) {
            certificate.HostNames ??= new List<string>();
            certificate.HostNames = certificate.HostNames
                .Select(NormalizeHostName)
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            certificate.CertificateSubject = NormalizeHostName(FirstNonEmpty(certificate.CertificateSubject, certificate.HostNames.FirstOrDefault()));
        }

        foreach (PublicForwardRouteOptions route in forwarding.Routes) {
            route.HostNames ??= new List<string>();
            route.HostNames = route.HostNames
                .Select(NormalizeHostName)
                .Where(host => !string.IsNullOrWhiteSpace(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            route.TargetUrl = FirstNonEmpty(route.TargetUrl).Trim();
            route.PathPrefix = NormalizeForwardPathPrefix(route.PathPrefix);
            route.ServiceName = FirstNonEmpty(route.ServiceName, route.HostNames.FirstOrDefault(), "SocketJack service");
            route.OfflineSummary = FirstNonEmpty(route.OfflineSummary, "The backing service is not responding right now.");
            route.PreserveHostHeader = true;
        }
    }

    private static string NormalizeLoopbackBindHost(string value) {
        value = FirstNonEmpty(value, LoopbackBindHost);
        if (value.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return LoopbackBindHost;
        if (IPAddress.TryParse(value, out IPAddress? address) && IPAddress.IsLoopback(address))
            return LoopbackBindHost;
        return LoopbackBindHost;
    }

    internal static string NormalizePublicBindHost(string value) {
        value = FirstNonEmpty(value, "0.0.0.0");
        if (value.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("+", StringComparison.OrdinalIgnoreCase))
            return "0.0.0.0";
        return value;
    }

    internal static IPAddress ResolvePublicBindAddress(string value) {
        value = NormalizePublicBindHost(value);
        if (IPAddress.TryParse(value, out IPAddress? address))
            return address;
        try {
            IPAddress[] addresses = Dns.GetHostAddresses(value);
            return addresses.FirstOrDefault(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ??
                   addresses.FirstOrDefault() ??
                   IPAddress.Any;
        } catch {
            return IPAddress.Any;
        }
    }

    internal static string NormalizeHostName(string value) {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        value = value.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            value = uri.Host;
        else {
            int slash = value.IndexOf('/');
            if (slash >= 0)
                value = value.Substring(0, slash);
            if (value.StartsWith("[", StringComparison.Ordinal)) {
                int close = value.IndexOf(']');
                if (close > 0)
                    value = value.Substring(1, close - 1);
            } else {
                int colon = value.LastIndexOf(':');
                if (colon > 0 && value.IndexOf(':') == colon)
                    value = value.Substring(0, colon);
            }
        }

        return value.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static bool GetBool(string? value, bool fallback) {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("on", StringComparison.OrdinalIgnoreCase);
    }

    public string AuthFilePath => Path.Combine(DataDirectory, "auth.json");
    public string UploadStagingDirectory => Path.Combine(DataDirectory, "Incoming");
    public string LastUpdateMetadataPath => Path.Combine(DataDirectory, "lastUpdates.json");
    public static string DynamicUpdatesPath => Path.Combine(Environment.CurrentDirectory, "dynamicUpdates.json");

    internal static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static string NormalizeChannelId(string value) {
        value = (value ?? "").Trim().ToLowerInvariant();
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '-').ToArray();
        value = new string(chars).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "default" : value;
    }

    internal static string NormalizePublicPath(string value) {
        value = (value ?? "").Trim().Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(value))
            return "";
        return "/" + value;
    }

    internal static string NormalizeForwardPathPrefix(string value) {
        value = (value ?? "").Trim().Replace('\\', '/');
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            value = uri.AbsolutePath;
        int query = value.IndexOf('?');
        if (query >= 0)
            value = value.Substring(0, query);
        int hash = value.IndexOf('#');
        if (hash >= 0)
            value = value.Substring(0, hash);
        value = value.Trim('/');
        return string.IsNullOrWhiteSpace(value) || value == "*" ? "/" : "/" + value;
    }

    internal static string FirstNonEmpty(params string?[] values) {
        foreach (string? value in values) {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return "";
    }

    internal static void NormalizeChannel(UpdateChannel channel, string dataDirectory) {
        channel.Id = NormalizeChannelId(channel.Id);
        if (string.IsNullOrWhiteSpace(channel.DisplayName))
            channel.DisplayName = channel.Id;
        channel.UpdateDirectory = Path.GetFullPath(FirstNonEmpty(channel.UpdateDirectory, Path.Combine(dataDirectory, "Packages", channel.Id)));
        channel.PublicPath = NormalizePublicPath(channel.PublicPath);
    }

    internal static void SaveDynamicChannels(IEnumerable<UpdateChannel> channels) {
        DynamicUpdateSettings settings = new() {
            Channels = channels
                .Where(IsPersistedChannelSetting)
                .Select(ClonePersistedChannel)
                .OrderBy(channel => channel.Id, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        File.WriteAllText(DynamicUpdatesPath, JsonSerializer.Serialize(settings, JsonOptions));
    }

    private static void ApplyDynamicChannels(List<UpdateChannel> channels, string dataDirectory) {
        DynamicUpdateSettings settings = LoadDynamicSettings();
        foreach (UpdateChannel dynamicChannel in settings.Channels) {
            NormalizeChannel(dynamicChannel, dataDirectory);
            int existingIndex = channels.FindIndex(channel => string.Equals(NormalizeChannelId(channel.Id), dynamicChannel.Id, StringComparison.OrdinalIgnoreCase));
            bool isDefaultChannel = IsDefaultChannelId(dynamicChannel.Id);
            bool hasExistingChannel = existingIndex >= 0;
            bool shouldBeDynamic = !isDefaultChannel &&
                                   (dynamicChannel.IsDynamic ||
                                    !hasExistingChannel ||
                                    (hasExistingChannel && channels[existingIndex].IsDynamic));
            dynamicChannel.IsDynamic = shouldBeDynamic;
            if (existingIndex >= 0) {
                channels[existingIndex] = dynamicChannel;
            } else {
                channels.Add(dynamicChannel);
            }
        }
    }

    private static DynamicUpdateSettings LoadDynamicSettings() {
        if (!File.Exists(DynamicUpdatesPath))
            return new DynamicUpdateSettings();

        try {
            string json = File.ReadAllText(DynamicUpdatesPath);
            if (string.IsNullOrWhiteSpace(json))
                return new DynamicUpdateSettings();
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
                return new DynamicUpdateSettings {
                    Channels = JsonSerializer.Deserialize<List<UpdateChannel>>(json, JsonOptions) ?? new List<UpdateChannel>()
                };
            return JsonSerializer.Deserialize<DynamicUpdateSettings>(json, JsonOptions) ?? new DynamicUpdateSettings();
        } catch {
            return new DynamicUpdateSettings();
        }
    }

    private static UpdateChannel ClonePersistedChannel(UpdateChannel channel) {
        return new UpdateChannel {
            Id = channel.Id,
            DisplayName = channel.DisplayName,
            UpdateDirectory = channel.UpdateDirectory,
            PublicPath = channel.PublicPath,
            ManagedProcessName = channel.ManagedProcessName,
            ManagedExecutablePath = channel.ManagedExecutablePath,
            AutoStartAfterUpdate = channel.AutoStartAfterUpdate,
            LastUpdateUtc = channel.LastUpdateUtc,
            IsDynamic = channel.IsDynamic
        };
    }

    private static bool IsPersistedChannelSetting(UpdateChannel channel) {
        return channel.IsDynamic || IsDefaultChannelId(channel.Id);
    }

    internal static bool IsDefaultChannelId(string channelId) {
        string id = NormalizeChannelId(channelId);
        return DefaultChannels().Any(channel => string.Equals(NormalizeChannelId(channel.Id), id, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddMissingDefaultChannels(List<UpdateChannel> channels) {
        foreach (UpdateChannel defaultChannel in DefaultChannels()) {
            string id = NormalizeChannelId(defaultChannel.Id);
            if (channels.Any(channel => string.Equals(NormalizeChannelId(channel.Id), id, StringComparison.OrdinalIgnoreCase)))
                continue;

            channels.Add(defaultChannel);
        }
    }

    internal static IEnumerable<UpdateChannel> DefaultChannels() {
        yield return new UpdateChannel {
            Id = "jackllm",
            DisplayName = "JackLLM",
            UpdateDirectory = @"C:\JackLLM\Update",
            PublicPath = "/Update",
            ManagedProcessName = "JackLLM",
            ManagedExecutablePath = "JackLLM.exe",
            AutoStartAfterUpdate = false
        };
        yield return new UpdateChannel {
            Id = "socketjack-magic-master-list",
            DisplayName = "SocketJack-MagicMasterList",
            UpdateDirectory = @"C:\Users\jackoffates\Desktop\Server2",
            ManagedProcessName = "SocketJack-MagicMasterList",
            ManagedExecutablePath = "SocketJack-MagicMasterList.exe",
            AutoStartAfterUpdate = true
        };
        yield return new UpdateChannel {
            Id = "jackllm-companion",
            DisplayName = "JackLLM Companion",
            UpdateDirectory = @"C:\JackLLM\Update\Companion",
            ManagedProcessName = "JackLLMCompanion",
            ManagedExecutablePath = "JackLLMCompanion.exe",
            AutoStartAfterUpdate = false
        };
        yield return new UpdateChannel {
            Id = "onlineusers-server",
            DisplayName = "OnlineUsers Server",
            UpdateDirectory = @"C:\Users\jackoffates\Desktop\wShare Server",
            ManagedProcessName = "OnlineUsers",
            ManagedExecutablePath = "OnlineUsers.exe",
            AutoStartAfterUpdate = true
        };
    }
}

public sealed class UpdateChannel {
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string UpdateDirectory { get; set; } = "";
    public string PublicPath { get; set; } = "";
    public int FileCount { get; set; }
    public long ByteCount { get; set; }
    public bool IsProcessManaged { get; set; }
    public string ManagedProcessName { get; set; } = "";
    public string ManagedExecutablePath { get; set; } = "";
    public bool AutoStartAfterUpdate { get; set; }
    public string LastUpdateUtc { get; set; } = "";
    public bool IsDynamic { get; set; }
    public bool IsProcessRunning { get; set; }
    public List<int> ManagedProcessIds { get; set; } = new();
    public bool IsStartupEnabled { get; set; }
}

public sealed class DynamicUpdateSettings {
    public List<UpdateChannel> Channels { get; set; } = new();
}

public sealed class PublicForwardingOptions {
    public bool Enabled { get; set; }
    public string HttpBindHost { get; set; } = "0.0.0.0";
    public int HttpPort { get; set; }
    public string HttpsBindHost { get; set; } = "0.0.0.0";
    public int HttpsPort { get; set; }
    public string DefaultCertificateHost { get; set; } = "";
    public List<PublicCertificateOptions> Certificates { get; set; } = new();
    public List<PublicForwardRouteOptions> Routes { get; set; } = new();

    internal static PublicForwardingOptions CreateDefault() {
        return new PublicForwardingOptions {
            Enabled = false,
            Certificates = new List<PublicCertificateOptions>(),
            Routes = new List<PublicForwardRouteOptions>()
        };
    }

    internal static IEnumerable<PublicCertificateOptions> DefaultCertificates() {
        yield break;
    }

    internal static IEnumerable<PublicForwardRouteOptions> DefaultRoutes() {
        yield break;
    }
}

public sealed class PublicCertificateOptions {
    public bool Enabled { get; set; } = true;
    public List<string> HostNames { get; set; } = new();
    public string CertificateSubject { get; set; } = "";
    public string CertificatePath { get; set; } = "";
    public string CertificateKeyPath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";
    public string CertificatePasswordPath { get; set; } = "";
}

public sealed class PublicForwardRouteOptions {
    public List<string> HostNames { get; set; } = new();
    public string PathPrefix { get; set; } = "/";
    public string ServiceName { get; set; } = "";
    public string OfflineSummary { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public bool PreserveHostHeader { get; set; } = true;
}

public sealed class ProcessActionResult {
    public bool Ok { get; set; }
    public string Message { get; set; } = "";
    public int ProcessId { get; set; }
}
