using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class ServerLocationLookupCacheEntry
        {
            public DateTimeOffset ExpiresUtc { get; set; }
            public ServerLocationLookupResult Result { get; set; }
        }

private sealed class ServerLocationLookupResult
        {
            public bool found { get; set; }
            public bool cached { get; set; }
            public bool privateAddress { get; set; }
            public string input { get; set; } = "";
            public string target { get; set; } = "";
            public string ip { get; set; } = "";
            public string city { get; set; } = "";
            public string region { get; set; } = "";
            public string country { get; set; } = "";
            public string countryCode { get; set; } = "";
            public string timezone { get; set; } = "";
            public string isp { get; set; } = "";
            public string label { get; set; } = "";
            public string source { get; set; } = "";
            public string error { get; set; } = "";
            public string updatedUtc { get; set; } = "";
            public double? latitude { get; set; }
            public double? longitude { get; set; }
        }
    }
}