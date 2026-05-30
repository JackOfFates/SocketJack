using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private sealed class GpuTdpSpec
        {
            public GpuTdpSpec(string name, double tdpWatts)
            {
                Name = name ?? "";
                TdpWatts = tdpWatts;
                NormalizedName = NormalizeGpuNameForLookup(Name);
            }

            public string Name { get; private set; }
            public string NormalizedName { get; private set; }
            public double TdpWatts { get; private set; }
        }

private sealed class GpuTdpCandidate
        {
            public string Name { get; set; }
            public double TdpWatts { get; set; }
            public string Source { get; set; }
            public string Detail { get; set; }
        }
    }
}