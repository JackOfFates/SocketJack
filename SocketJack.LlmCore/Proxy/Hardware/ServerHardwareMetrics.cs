using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace SocketJack.Net
{
    public partial class LmVsProxy
    {
private class ServerHardwarePercentMetric
        {
            public bool available { get; set; }
            public double? percent { get; set; }
            public string text { get; set; }

            public static ServerHardwarePercentMetric Unavailable(string text)
            {
                return new ServerHardwarePercentMetric
                {
                    available = false,
                    percent = null,
                    text = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text
                };
            }
        }

private sealed class ServerHardwareRamMetric : ServerHardwarePercentMetric
        {
            public ulong usedBytes { get; set; }
            public ulong totalBytes { get; set; }

            public new static ServerHardwareRamMetric Unavailable(string text)
            {
                return new ServerHardwareRamMetric
                {
                    available = false,
                    percent = null,
                    text = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text,
                    usedBytes = 0,
                    totalBytes = 0
                };
            }
        }

private sealed class ServerHardwareGpuMetric : ServerHardwarePercentMetric
        {
            public string name { get; set; }
            public string source { get; set; }
            public int deviceCount { get; set; }
            public double? vramPercent { get; set; }
            public string vramText { get; set; }
            public ulong vramUsedBytes { get; set; }
            public ulong vramTotalBytes { get; set; }

            public new static ServerHardwareGpuMetric Unavailable(string text)
            {
                return new ServerHardwareGpuMetric
                {
                    available = false,
                    name = "",
                    source = "",
                    deviceCount = 0,
                    percent = null,
                    text = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text,
                    vramPercent = null,
                    vramText = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text,
                    vramUsedBytes = 0,
                    vramTotalBytes = 0
                };
            }
        }

private sealed class ServerHardwareNetworkMetric
        {
            public bool available { get; set; }
            public double downKbps { get; set; }
            public double upKbps { get; set; }
            public string text { get; set; }

            public static ServerHardwareNetworkMetric Unavailable(string text)
            {
                return new ServerHardwareNetworkMetric
                {
                    available = false,
                    downKbps = 0,
                    upKbps = 0,
                    text = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text
                };
            }
        }

private sealed class ServerHardwareIoMetric
        {
            public bool available { get; set; }
            public double readBps { get; set; }
            public double writeBps { get; set; }
            public string text { get; set; }

            public static ServerHardwareIoMetric Unavailable(string text)
            {
                return new ServerHardwareIoMetric
                {
                    available = false,
                    readBps = 0,
                    writeBps = 0,
                    text = string.IsNullOrWhiteSpace(text) ? "Unavailable" : text
                };
            }
        }

        [StructLayout(LayoutKind.Sequential)]
private struct SystemFileTime
        {
            public uint dwLowDateTime;
            public uint dwHighDateTime;

            public ulong ToUInt64()
            {
                return ((ulong)dwHighDateTime << 32) | dwLowDateTime;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public static MemoryStatusEx Create()
            {
                MemoryStatusEx status = new MemoryStatusEx();
                status.dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                return status;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
private struct ProcessIoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }
    }
}
