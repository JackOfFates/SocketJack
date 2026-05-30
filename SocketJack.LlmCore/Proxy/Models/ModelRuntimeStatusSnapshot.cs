using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class ModelRuntimeStatusSnapshot
        {
            public string Provider { get; set; } = "LM Studio";
            public string ProviderKind { get; set; } = "lmstudio";
            public string BaseUrl { get; set; } = "";
            public string ModelsEndpoint { get; set; } = "";
            public bool Connected { get; set; }
            public string Error { get; set; } = "";
            public bool EmergencyFallbackActive { get; set; }
            public string CheckedUtc { get; set; } = "";
        }
    }
}