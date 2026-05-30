using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Sockets;

namespace SocketJack.Net {
    internal static class TcpForwardingBufferProfile {
        public const int NormalSocketBufferSize = 128 * 1024;
        public const int StressedSocketBufferSize = 64 * 1024;
        public const int NormalCopyBufferSize = 64 * 1024;
        public const int StressedCopyBufferSize = 32 * 1024;

        private const int ActiveSessionStressThreshold = 8;
        private const double HighCpuPercentThreshold = 70.0;
        private static readonly object CpuSampleLock = new object();
        private static DateTime _lastCpuSampleUtc = DateTime.MinValue;
        private static TimeSpan _lastTotalProcessorTime = TimeSpan.Zero;
        private static bool _lastCpuHigh;

        public static bool IsStressed(int activeOrQueuedSessions) {
            return activeOrQueuedSessions >= ActiveSessionStressThreshold || IsProcessCpuHigh();
        }

        public static int GetSocketBufferSize(int activeOrQueuedSessions) {
            return IsStressed(activeOrQueuedSessions) ? StressedSocketBufferSize : NormalSocketBufferSize;
        }

        public static int GetCopyBufferSize(int activeOrQueuedSessions) {
            return IsStressed(activeOrQueuedSessions) ? StressedCopyBufferSize : NormalCopyBufferSize;
        }

        public static void ConfigureStreamingSocket(System.Net.Sockets.TcpClient client, int activeOrQueuedSessions) {
            if (client == null)
                return;

            client.NoDelay = true;
            try {
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            } catch {
            }

            int bufferSize = GetSocketBufferSize(activeOrQueuedSessions);
            try {
                client.ReceiveBufferSize = bufferSize;
                client.SendBufferSize = bufferSize;
            } catch (SocketException) {
                try {
                    client.ReceiveBufferSize = NormalSocketBufferSize;
                    client.SendBufferSize = NormalSocketBufferSize;
                } catch {
                }
            } catch (ObjectDisposedException) {
            }
        }

        public static byte[] RentCopyBuffer(int activeOrQueuedSessions) {
            return ArrayPool<byte>.Shared.Rent(GetCopyBufferSize(activeOrQueuedSessions));
        }

        public static void ReturnCopyBuffer(byte[] buffer) {
            if (buffer != null)
                ArrayPool<byte>.Shared.Return(buffer);
        }

        private static bool IsProcessCpuHigh() {
            DateTime now = DateTime.UtcNow;
            lock (CpuSampleLock) {
                if ((now - _lastCpuSampleUtc).TotalMilliseconds < 1000)
                    return _lastCpuHigh;

                TimeSpan totalProcessorTime;
                try {
                    totalProcessorTime = Process.GetCurrentProcess().TotalProcessorTime;
                } catch {
                    return _lastCpuHigh;
                }

                if (_lastCpuSampleUtc != DateTime.MinValue) {
                    double elapsedMs = Math.Max(1.0, (now - _lastCpuSampleUtc).TotalMilliseconds);
                    double cpuMs = Math.Max(0.0, (totalProcessorTime - _lastTotalProcessorTime).TotalMilliseconds);
                    double cpuPercent = cpuMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100.0;
                    _lastCpuHigh = cpuPercent >= HighCpuPercentThreshold;
                }

                _lastCpuSampleUtc = now;
                _lastTotalProcessorTime = totalProcessorTime;
                return _lastCpuHigh;
            }
        }
    }
}
