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
            string ConfigFile = AssemblyRuntimeConfig();
            if (System.IO.File.Exists(ConfigFile)) {
                string config = await System.IO.File.ReadAllTextAsync(ConfigFile);
                string newConfig = config;
                if (newConfig.Contains(ConfigPropertiesHeader)) {
                    var AddVars = new List<string>();
                    bool hasVars = ContainsVariables(newConfig);
                    for (int i = 0, loopTo = DisabledVariables.Count() - 1; i <= loopTo; i++) {
                        string @var = DisabledVariables[i];
                        if (newConfig.Contains(@var, StringComparison.CurrentCultureIgnoreCase)) {
                            newConfig.Replace(@var, Variables[i], StringComparison.CurrentCultureIgnoreCase);
                        }
                    }
                    for (int i = 0, loopTo = Variables.Count() - 1; i <= loopTo; i++) {
                        string @var = Variables[i];
                        if (!newConfig.Contains(@var, StringComparison.CurrentCultureIgnoreCase)) {
                            AddVars.Add(@var);
                        }
                    }
                    for (int i = AddVars.Count - 1; i >= 0; i -= 1) {
                        string @var = AddVars[i] + ","; //i == 0 && !hasVars ?  : AddVars[i] + ",";
                        newConfig = newConfig.Insert(newConfig.IndexOf(ConfigPropertiesHeader) + ConfigPropertiesHeader.Length + 1, @var + Environment.NewLine);
                    }
                } else if (newConfig.Contains(AfterIndex1, StringComparison.CurrentCultureIgnoreCase)) {
                    newConfig = newConfig.Insert(newConfig.IndexOf(AfterIndex1) + AfterIndex1.Length + 1, Environment.NewLine + FullConfigProperties);
                } else if (newConfig.Contains(AfterIndex2, StringComparison.CurrentCultureIgnoreCase)) {
                    newConfig = newConfig.Insert(newConfig.IndexOf(AfterIndex2) + AfterIndex2.Length + 1, Environment.NewLine + FullConfigProperties);
                }
                if ((config ?? "") != (newConfig ?? "")) {
                    System.IO.File.WriteAllText(ConfigFile, newConfig);
                    if (!Debugger.IsAttached) {
                        string ProcessName = System.Reflection.Assembly.GetEntryAssembly().Location.Replace(".dll", ".exe");
                        if (System.IO.File.Exists(ProcessName)) {
                            Process.Start(new ProcessStartInfo() { FileName = ProcessName });
                            Process.GetCurrentProcess().Kill();
                        }
                    }
                }
            }
        }

        private static bool ContainsVariables(string config) {
            if (config.Contains(ConfigPropertiesHeader)) {
                int startIndex = config.IndexOf(ConfigPropertiesHeader);
                int endIndex = config.IndexOf("}", startIndex);
                string substr = config.Substring(startIndex, endIndex - startIndex);
                return substr.Contains(",");
            }
            return false;
        }

        private static string[] Variables = new[] { "      \"DOTNET_GCCpuGroup\": \"1\"", 
                                                    "      \"DOTNET_Thread_UseAllCpuGroups\": \"1\"",
                                                    "      \"DOTNET_gcAllowVeryLargeObjects\": \"1\"",
                                                    "      \"DOTNET_Thread_AssignCpuGroups\": \"1\"",
                                                    "      \"COMPlus_Thread_AssignCpuGroups\": \"1\"",
                                                    "      \"COMPlus_GCCpuGroup\": \"1\"",
                                                    "      \"COMPlus_Thread_UseAllCpuGroups\": \"1\"",
                                                    "      \"COMPlus_gcAllowVeryLargeObjects\": \"1\"",
                                                    "      \"COMPlus_GCNoAffinitize\": \"1\"",
                                                    "      \"DOTNET_GCNoAffinitize\": \"1\"" };

        private static string[] DisabledVariables = new[] { 
                                                    "      \"DOTNET_GCCpuGroup\": \"0\"",
                                                    "      \"DOTNET_Thread_UseAllCpuGroups\": \"0\"",
                                                    "      \"DOTNET_gcAllowVeryLargeObjects\": \"0\"",
                                                    "      \"DOTNET_Thread_AssignCpuGroups\": \"0\"",
                                                    "      \"COMPlus_Thread_AssignCpuGroups\": \"0\"",
                                                    "      \"COMPlus_GCCpuGroup\": \"0\"",
                                                    "      \"COMPlus_Thread_UseAllCpuGroups\": \"0\"",
                                                    "      \"COMPlus_gcAllowVeryLargeObjects\": \"0\"",
                                                    "      \"COMPlus_GCNoAffinitize\": \"0\"",
                                                    "      \"DOTNET_GCNoAffinitize\": \"0\"" };

        private static string FullConfigProperties =  "    \"configProperties\": {" + Environment.NewLine + 
                                                    "      \"DOTNET_GCCpuGroup\": \"1\"," + Environment.NewLine + 
                                                    "      \"DOTNET_Thread_UseAllCpuGroups\": \"1\"," + Environment.NewLine +
                                                    "      \"DOTNET_Thread_AssignCpuGroups\": \"1\"," + Environment.NewLine +
                                                    "      \"DOTNET_gcAllowVeryLargeObjects\": \"1\"," + Environment.NewLine +
                                                    "      \"COMPlus_GCCpuGroup\": \"1\"," + Environment.NewLine +
                                                    "      \"COMPlus_Thread_UseAllCpuGroups\": \"1\"," + Environment.NewLine +
                                                    "      \"COMPlus_Thread_AssignCpuGroups\": \"1\"," + Environment.NewLine +
                                                    "      \"COMPlus_gcAllowVeryLargeObjects\": \"1\"," + Environment.NewLine +
                                                    "      \"DOTNET_GCNoAffinitize\": \"1\"," + Environment.NewLine +
                                                    "      \"COMPlus_GCNoAffinitize\": \"1\"" + Environment.NewLine + "      }";

        private static string ConfigPropertiesHeader = "\"configProperties\": {";
        //private static string RuntimeOptionsHeader = "\"runtimeOptions\": {";
        private static string AfterIndex1 = "],";
        private static string AfterIndex2 = "]";

        private string AssemblyRuntimeConfig() {
            string path = ExeLocation();
            string assemblyName = System.Reflection.Assembly.GetEntryAssembly().GetName().Name;
            string runtimeConfig = System.IO.Path.Combine(path, string.Join(".", new[] { assemblyName, "runtimeconfig.json" }));
            return runtimeConfig;
        }

        private string ExeLocation() {
            string fullpath = System.Reflection.Assembly.GetEntryAssembly().Location;
            return fullpath.Remove(fullpath.LastIndexOf(@"\"));
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
                        if (Server.Connected && Server.Connection != null) {
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