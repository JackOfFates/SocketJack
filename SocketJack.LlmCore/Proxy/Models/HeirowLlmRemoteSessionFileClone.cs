using System;

namespace SocketJack.Net {
    public sealed class RemoteSessionFileCloneSnapshot {
        public string Id { get; set; } = "";
        public string RemotePath { get; set; } = "";
        public string RemoteUrl { get; set; } = "";
        public string LocalPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string OwnerKey { get; set; } = "";
        public string SandboxSessionId { get; set; } = "";
        public string SandboxFileId { get; set; } = "";
        public string SandboxPath { get; set; } = "";
        public string Status { get; set; } = "";
        public long Bytes { get; set; }
        public string Sha256 { get; set; } = "";
        public string LastWriteUtc { get; set; } = "";
        public string StartedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Canceled { get; set; }
        public bool ChangedLocally { get; set; }
    }

    public sealed class RemoteSessionFileCloneChangedEventArgs : EventArgs {
        public RemoteSessionFileCloneChangedEventArgs(RemoteSessionFileCloneSnapshot snapshot) {
            Snapshot = snapshot ?? new RemoteSessionFileCloneSnapshot();
        }

        public RemoteSessionFileCloneSnapshot Snapshot { get; }
    }
}
