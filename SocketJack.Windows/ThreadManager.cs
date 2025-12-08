using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using SocketJack.Extensions;
using SocketJack.Net;

namespace SocketJack {

    /// <summary>
    /// Manages all Tcp Client and Server threads.
    /// </summary>
    public class ThreadManager : IDisposable {

        #region Properties

        /// <summary>
        /// All TcpClient instances.
        /// </summary>
        public static ConcurrentDictionary<Guid, ISocket> TcpClients { get; } = new ConcurrentDictionary<Guid, ISocket>();

        /// <summary>
        /// All TcpServer instances.
        /// </summary>
        public static ConcurrentDictionary<Guid, ISocket> TcpServers { get; } = new ConcurrentDictionary<Guid, ISocket>();

        /// <summary>
        /// Time in Milliseconds to wait before timing out.
        /// </summary>
        public static int Timeout { get; set; } = 3000;

        protected internal static bool RuntimesSet = false;

        protected internal static bool Alive {
            get {
                return _Alive;
            }
            set {
                bool Start = !_Alive && value;
                _Alive = value;
                StartCounters(Alive);
            }
        }
        private static bool _Alive = false;

        protected internal static void StartCounters(bool Start) {
            if (CounterThread == null || CounterThread.ThreadState == System.Threading.ThreadState.Stopped) {
                CounterThread = new Thread(CounterLoop) { Name = "ByteCounterThread" };
            }
            if (Start) {
                if (!CounterThread.IsAlive)
                    CounterThread.Start();
            }
        }

        #endregion

        #region IDisposable
        /// <summary>
        /// Must be called on application shutdown to ensure all threads are closed.
        /// </summary>
        public static void Shutdown() {
            Alive = false;
            TcpClients.Values.ToList().ForEach(DisposeDelegate);
            TcpServers.Values.ToList().ForEach(DisposeDelegate);
        }

        protected virtual void Dispose(bool disposing) {
            if (!disposedValue) {
                if (disposing) {
                    Shutdown();
                }

                disposedValue = true;
            }
        }
        public void Dispose() {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        private static void DisposeDelegate(ISocket Instance) {
            if (Instance != null && !Instance.isDisposed)
                Instance.Dispose();
        }

        #endregion

        #region Multithreading

        private bool disposedValue;

        private void EnableMultithreading() {
            Environment.SetEnvironmentVariable("DOTNET_GCCpuGroup", "1");
            Environment.SetEnvironmentVariable("DOTNET_Thread_UseAllCpuGroups", "1");
            Environment.SetEnvironmentVariable("DOTNET_gcAllowVeryLargeObjects", "1");
            Environment.SetEnvironmentVariable("DOTNET_Thread_AssignCpuGroups", "1");
            Environment.SetEnvironmentVariable("DOTNET_GCNoAffinitize", "1");
            Environment.SetEnvironmentVariable("COMPlus_GCCpuGroup", "1");
            Environment.SetEnvironmentVariable("COMPlus_Thread_UseAllCpuGroups", "1");
            Environment.SetEnvironmentVariable("COMPlus_gcAllowVeryLargeObjects", "1");
            Environment.SetEnvironmentVariable("COMPlus_Thread_AssignCpuGroups", "1");
            Environment.SetEnvironmentVariable("COMPlus_GCNoAffinitize", "1");
        }

        protected internal async void ModifyRuntimeConfig() {
            EnableMultithreading();
            if (RuntimesSet)
                return;
            RuntimesSet = true;
        }

        #endregion

        protected internal static Thread CounterThread;
        private static DateTime LastCount = DateTime.UtcNow;
        private static TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        protected internal static void CounterLoop() {
            while (Alive) {
                for (int i = 0; i < TcpClients.Count; i++) {
                    MethodExtensions.TryInvoke(() => {
                        if(TcpClients.Count - 1 < i) return;
                        var Client = TcpClients.Values.ElementAt(i);
                        if (Client.Connected && Client.Connection != null) {
                            Client.Connection._SentBytesPerSecond = Client.Connection.SentBytesCounter;
                            Client.Connection._ReceivedBytesPerSecond = Client.Connection.ReceivedBytesCounter;
                            Client.Connection.SentBytesCounter = 0;
                            Client.Connection.ReceivedBytesCounter = 0;
                            Client.InvokeBytesPerSecondUpdate(Client.Connection);
                        }
                    });
                }
                for (int i = 0; i < TcpServers.Count; i++) {
                    MethodExtensions.TryInvoke(() => {
                        if (TcpServers.Count - 1 < i) return;
                        var Server = TcpServers.Values.ElementAt(i);
                        if (Server.Connection != null) {
                            if (Server.GetType() == typeof(TcpServer)) {
                                foreach (var c in Server.AsTcpServer().Clients.Values) {
                                    Server.Connection.SentBytesCounter += c.SentBytesCounter;
                                    Server.Connection.ReceivedBytesCounter += c.ReceivedBytesCounter;
                                    c.SentBytesCounter = 0;
                                    c.ReceivedBytesCounter = 0;
                                }
                            }
                            Server.Connection._SentBytesPerSecond = Server.Connection.SentBytesCounter;
                            Server.Connection._ReceivedBytesPerSecond = Server.Connection.ReceivedBytesCounter;
                            Server.Connection.SentBytesCounter = 0;
                            Server.Connection.ReceivedBytesCounter = 0;
                            Server.InvokeBytesPerSecondUpdate(Server.Connection);
                        }
                    });
                }
                do {
                    Thread.Sleep(1);
                } while(DateTime.UtcNow - LastCount < OneSecond);
                LastCount = DateTime.UtcNow;
            }
        }
    }
}