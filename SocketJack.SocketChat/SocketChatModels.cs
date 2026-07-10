using System;
using System.Collections.Generic;

namespace SocketJack.SocketChat {
    public enum SocketChatChannelKind { Text, Voice, DirectMessage }
    public enum SocketChatTransportKind { Direct, HostRelay, ExternalTunnel, DropboxOnly }
    public enum SocketChatRecordKind { Message, Edit, Delete, Reaction, Presence, Attachment, Signal, HostLease }

    public sealed class SocketChatLobby {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "New lobby";
        public string OwnerFingerprint { get; set; } = "";
        public long Epoch { get; set; } = 1;
        public bool IsPublic { get; set; }
        public List<SocketChatChannel> Channels { get; set; } = new List<SocketChatChannel>();
    }

    public sealed class SocketChatChannel {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string LobbyId { get; set; } = "";
        public string Name { get; set; } = "general";
        public SocketChatChannelKind Kind { get; set; }
    }

    public sealed class SocketChatMessage {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string LobbyId { get; set; } = "";
        public string ChannelId { get; set; } = "";
        public string SenderFingerprint { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTimeOffset SentUtc { get; set; } = DateTimeOffset.UtcNow;
        public long Sequence { get; set; }
        public List<SocketChatAttachment> Attachments { get; set; } = new List<SocketChatAttachment>();
    }

    public sealed class SocketChatAttachment {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FileName { get; set; } = "";
        public string ContentType { get; set; } = "application/octet-stream";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
        public string DropboxPath { get; set; } = "";
    }

    public sealed class SocketChatMessageMutation {
        public string MessageId { get; set; } = "";
        public SocketChatMessage Message { get; set; }
        public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class SocketChatDeleteMutation {
        public string MessageId { get; set; } = "";
        public List<string> AttachmentIds { get; set; } = new List<string>();
        public bool DeleteMessage { get; set; }
        public DateTimeOffset DeletedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class SocketChatEnvelope {
        public int Version { get; set; } = 1;
        public SocketChatRecordKind Kind { get; set; }
        public string LobbyId { get; set; } = "";
        public long Epoch { get; set; }
        public string SenderFingerprint { get; set; } = "";
        public long Sequence { get; set; }
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset ExpiresUtc { get; set; }
        public string CipherText { get; set; } = "";
        public string Signature { get; set; } = "";
    }

    public sealed class SocketChatHostCandidate {
        public string Fingerprint { get; set; } = "";
        public bool PubliclyReachable { get; set; }
        public bool TunnelReachable { get; set; }
        public double UpstreamMbps { get; set; }
        public double LatencyMs { get; set; }
        public double PacketLossPercent { get; set; }
        public TimeSpan Uptime { get; set; }
        public DateTimeOffset ObservedUtc { get; set; } = DateTimeOffset.UtcNow;
    }

    public sealed class SocketChatHostLease {
        public string LobbyId { get; set; } = "";
        public string HostFingerprint { get; set; } = "";
        public long Epoch { get; set; }
        public DateTimeOffset ExpiresUtc { get; set; }
        public string Signature { get; set; } = "";
    }

    public sealed class SocketChatUserProfile {
        public string DeviceFingerprint { get; set; } = "";
        public string Username { get; set; } = "SocketChat User";
        public string AvatarPath { get; set; } = "";
        public string NetworkIdentityHash { get; set; } = "";
        public List<string> ObservedIpAddresses { get; set; } = new List<string>();
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>Stable ban key anchored to the cryptographic device identity, not the editable username.</summary>
        public string BanIdentityKey { get; set; } = "";
        public string MasterListPath { get; set; } = "";
        public string CoordinatorUrl { get; set; } = "http://127.0.0.1:4280";
        public bool HostCoordinator { get; set; } = true;
        public List<string> FriendFingerprints { get; set; } = new List<string>();
        public int MicrophoneDeviceNumber { get; set; } = -1;
        public double MicrophoneLevel { get; set; } = 1.0;
        public bool VoiceActivationEnabled { get; set; }
        public double VoiceActivationThresholdDb { get; set; } = -42;
        public bool NoiseSuppressionEnabled { get; set; } = true;
        public bool EchoCancellationEnabled { get; set; } = true;
        public bool AutomaticGainEnabled { get; set; } = true;
        public bool HighPassFilterEnabled { get; set; } = true;
        public string PushToTalkKey { get; set; } = "LeftCtrl";
        public string ToggleMuteKey { get; set; } = "M";
        public bool PushToTalkEnabled { get; set; }
        public int StreamPort { get; set; } = 1935;
        public int StreamPreviewFps { get; set; } = 10;
        public int StreamBitrateKbps { get; set; } = 4500;
        public int StreamMonitorIndex { get; set; }
        public string StreamSource { get; set; } = "Display";
        public string ObsPath { get; set; } = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
        public string VlcPath { get; set; } = @"C:\Program Files\VideoLAN\VLC\vlc.exe";
    }

    public sealed class SocketChatMasterList {
        public int Version { get; set; } = 1;
        public long Revision { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public List<SocketChatDirectoryUser> Users { get; set; } = new List<SocketChatDirectoryUser>();
        public List<SocketChatDirectoryServer> Servers { get; set; } = new List<SocketChatDirectoryServer>();
        public List<SocketChatFriendRequest> FriendRequests { get; set; } = new List<SocketChatFriendRequest>();
    }

    public sealed class SocketChatDirectoryUser {
        public string Fingerprint { get; set; } = "";
        public string PublicKey { get; set; } = "";
        public string Username { get; set; } = "";
        public string BanIdentityKey { get; set; } = "";
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Signature { get; set; } = "";
    }

    public sealed class SocketChatDirectoryServer {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string HostFingerprint { get; set; } = "";
        public string Endpoint { get; set; } = "";
        public string Transport { get; set; } = "dropbox-only";
        public int MemberCount { get; set; }
        public DateTimeOffset LastSeenUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Signature { get; set; } = "";
    }

    public sealed class SocketChatFriendRequest {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string FromFingerprint { get; set; } = "";
        public string ToFingerprint { get; set; } = "";
        public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
        public string Signature { get; set; } = "";
        public DateTimeOffset? AcceptedUtc { get; set; }
        public string AcceptedSignature { get; set; } = "";
    }
}
