using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class RemoteSessionFileCloneRecord
        {
            public string Id;
            public string RemotePath;
            public string RemoteUrl;
            public string LocalPath;
            public string FileName;
            public string SessionId;
            public string OwnerKey;
            public string SandboxSessionId;
            public string SandboxFileId;
            public string SandboxPath;
            public string Status;
            public long Bytes;
            public string Sha256;
            public DateTimeOffset LastWriteUtc;
            public DateTimeOffset StartedUtc;
            public DateTimeOffset UpdatedUtc;
            public string Error;
            public bool Canceled;
            public bool ChangedLocally;
        }
    }
}
