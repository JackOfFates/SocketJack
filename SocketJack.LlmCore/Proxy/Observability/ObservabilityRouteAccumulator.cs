using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using LmVs;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class ObservabilityRouteAccumulator
        {
            public string Method { get; set; } = "";
            public string Route { get; set; } = "";
            public string Category { get; set; } = "";
            public long Count { get; set; }
            public long Failures { get; set; }
            public double TotalLatencyMs { get; set; }
            public double MaxLatencyMs { get; set; }
            public long TotalTokens { get; set; }
            public DateTimeOffset FirstSeenUtc { get; set; }
            public DateTimeOffset LastSeenUtc { get; set; }

            public ObservabilityRouteSnapshot ToSnapshot()
            {
                return new ObservabilityRouteSnapshot
                {
                    Method = Method ?? "",
                    Route = Route ?? "",
                    Category = Category ?? "",
                    Count = Count,
                    Failures = Failures,
                    FailureRate = Count <= 0 ? 0 : Math.Round((double)Failures / Count, 4),
                    AverageLatencyMs = Count <= 0 ? 0 : Math.Round(TotalLatencyMs / Count, 2),
                    MaxLatencyMs = Math.Round(MaxLatencyMs, 2),
                    TotalTokens = TotalTokens,
                    FirstSeenUtc = FirstSeenUtc == default(DateTimeOffset) ? "" : FirstSeenUtc.ToString("O"),
                    LastSeenUtc = LastSeenUtc == default(DateTimeOffset) ? "" : LastSeenUtc.ToString("O")
                };
            }
        }
    }
}
