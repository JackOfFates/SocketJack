using SocketJack.SocketChat;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace SocketJack.Net;

public partial class LmVsProxy
{
    private readonly object _socketChatLock = new object();
    private SocketChatManagedDatabase _socketChatDatabase;
    private SocketChatDeviceIdentity _socketChatIdentity;
    private byte[] _socketChatLocalKey;

    private void RegisterSocketChatRoutes(MutableTcpServer server)
    {
        string html = HtmlPageResources.GetHtml("SocketChat.html");
        server.Map("GET", "/SocketChat", (connection, request, cancellationToken) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/SocketChat/", (connection, request, cancellationToken) => RenderChatServerHtml(html, null, request));
        server.Map("GET", "/api/socketchat/status", (connection, request, cancellationToken) => HandleSocketChatStatus());
        server.Map("GET", "/api/socketchat/messages", (connection, request, cancellationToken) => HandleSocketChatMessages(request));
        server.Map("POST", "/api/socketchat/messages", (connection, request, cancellationToken) => HandleSocketChatSend(request));
        server.Map("POST", "/api/socketchat/pairing-code", (connection, request, cancellationToken) => JsonSerializer.Serialize(new { code = SocketChatDeviceIdentity.CreatePairingCode(), expiresInSeconds = 300 }));
        server.Map("OPTIONS", "/api/socketchat/status", (connection, request, cancellationToken) => HandleWebAuthCorsPreflight(request));
        server.Map("OPTIONS", "/api/socketchat/messages", (connection, request, cancellationToken) => HandleWebAuthCorsPreflight(request));
        server.Map("OPTIONS", "/api/socketchat/pairing-code", (connection, request, cancellationToken) => HandleWebAuthCorsPreflight(request));
    }

    private void EnsureSocketChat()
    {
        if (_socketChatDatabase != null) return;
        lock (_socketChatLock)
        {
            if (_socketChatDatabase != null) return;
            string root = Path.Combine(_chatSessionRoot, "SocketChat");
            Directory.CreateDirectory(root);
            _socketChatIdentity = SocketChatDeviceIdentity.LoadOrCreate(Path.Combine(root, ".device-key"));
            string keyPath = Path.Combine(root, ".local-store-key");
            if (File.Exists(keyPath)) _socketChatLocalKey = Convert.FromBase64String(File.ReadAllText(keyPath));
            else
            {
                _socketChatLocalKey = new byte[32];
                RandomNumberGenerator.Fill(_socketChatLocalKey);
                File.WriteAllText(keyPath, Convert.ToBase64String(_socketChatLocalKey));
                try { File.SetAttributes(keyPath, File.GetAttributes(keyPath) | FileAttributes.Hidden); } catch { }
            }
            _socketChatDatabase = new SocketChatManagedDatabase(Path.Combine(root, "Database"));
        }
    }

    private object HandleSocketChatStatus()
    {
        EnsureSocketChat();
        return JsonSerializer.Serialize(new
        {
            name = "SocketChat",
            version = 1,
            fingerprint = FormatSocketChatFingerprint(_socketChatIdentity.Fingerprint),
            database = "managed-encrypted-cache",
            dropbox = new { connected = false, mode = "not-configured" },
            transport = new
            {
                current = "local",
                ladder = new[] { "direct-ipv6", "stun", "upnp-nat-pmp", "manual-forward", "external-tunnel", "host-reverse-relay", "dropbox-only" },
                maxVoicePeers = 8,
                relayPriority = new[] { "control-text", "voice", "files" }
            },
            capabilities = new { text = true, images = true, files = true, voice = true, video = "one-to-one-planned", maxAttachmentBytes = 10 * 1024 * 1024 }
        });
    }

    private object HandleSocketChatMessages(HttpRequest request)
    {
        EnsureSocketChat();
        string lobbyId = ReadSocketChatQuery(request, "lobbyId", "local-lobby");
        var messages = _socketChatDatabase.Read(lobbyId).Where(e => e.Kind == SocketChatRecordKind.Message).Select(e =>
        {
            try
            {
                byte[] plain = SocketChatCrypto.Decrypt(_socketChatLocalKey, e.CipherText);
                return JsonSerializer.Deserialize<SocketChatMessage>(plain);
            }
            catch { return null; }
        }).Where(message => message != null).ToArray();
        return JsonSerializer.Serialize(new { lobbyId, messages });
    }

    private object HandleSocketChatSend(HttpRequest request)
    {
        EnsureSocketChat();
        using JsonDocument document = JsonDocument.Parse(string.IsNullOrWhiteSpace(request.Body) ? "{}" : request.Body);
        JsonElement root = document.RootElement;
        string text = GetSocketChatString(root, "text").Trim();
        string lobbyId = FirstNonEmpty(GetSocketChatString(root, "lobbyId"), "local-lobby");
        string channelId = FirstNonEmpty(GetSocketChatString(root, "channelId"), "general");
        if (string.IsNullOrWhiteSpace(text))
        {
            request.Context.StatusCodeNumber = 400;
            request.Context.ReasonPhrase = "Bad Request";
            return JsonSerializer.Serialize(new { error = "Message text is required." });
        }
        if (text.Length > 8000)
        {
            request.Context.StatusCodeNumber = 413;
            request.Context.ReasonPhrase = "Payload Too Large";
            return JsonSerializer.Serialize(new { error = "Message text exceeds 8000 characters." });
        }
        long sequence = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var message = new SocketChatMessage { LobbyId = lobbyId, ChannelId = channelId, SenderFingerprint = _socketChatIdentity.Fingerprint, Text = text, Sequence = sequence };
        string cipherText = SocketChatCrypto.Encrypt(_socketChatLocalKey, JsonSerializer.SerializeToUtf8Bytes(message));
        var envelope = new SocketChatEnvelope { Kind = SocketChatRecordKind.Message, LobbyId = lobbyId, Epoch = 1, SenderFingerprint = _socketChatIdentity.Fingerprint, Sequence = sequence, CipherText = cipherText };
        envelope.Signature = _socketChatIdentity.Sign(SocketChatEnvelopeSigningValue(envelope));
        _socketChatDatabase.Append(envelope);
        return JsonSerializer.Serialize(new { ok = true, message });
    }

    private static string SocketChatEnvelopeSigningValue(SocketChatEnvelope envelope) => string.Join("|", envelope.Version, envelope.Kind, envelope.LobbyId, envelope.Epoch, envelope.SenderFingerprint, envelope.Sequence, envelope.CreatedUtc.ToUnixTimeMilliseconds(), envelope.CipherText);
    private static string FormatSocketChatFingerprint(string value) => string.Join("-", Enumerable.Range(0, value.Length / 4).Select(i => value.Substring(i * 4, 4)));
    private static string GetSocketChatString(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string ReadSocketChatQuery(HttpRequest request, string key, string fallback)
    {
        string query = request?.QueryString ?? "";
        foreach (string part in query.TrimStart('?').Split('&'))
        {
            string[] pair = part.Split(new[] { '=' }, 2);
            if (pair.Length == 2 && string.Equals(Uri.UnescapeDataString(pair[0]), key, StringComparison.OrdinalIgnoreCase)) return Uri.UnescapeDataString(pair[1].Replace('+', ' '));
        }
        return fallback;
    }
}
