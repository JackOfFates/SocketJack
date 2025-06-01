using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualBasic;
using SocketJack.Extensions;
using SocketJack.Networking;
using SocketJack.Networking.Shared;

namespace SocketJack.Management {

    /// <summary>
    /// Manages all Tcp Client and Server threads.
    /// </summary>
    public class ThreadManager : IDisposable {

        #region Properties

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

        private static void DisposeDelegate(TcpBase Instance) {
            if (Instance != null && !Instance.isDisposed)
                Instance.Dispose();
        }

        #endregion

        #region Multithreading
        protected internal static void StartCounters(bool Start) {
            //if (ConnectionCheckThread == null || ConnectionCheckThread.ThreadState == System.Threading.ThreadState.Stopped) {
            //    ConnectionCheckThread = new Thread(ConnectionCheckLoop) { Name = "ConnectionCheckThread", Priority = ThreadPriority.Highest };
            //}
            if (CounterThread == null || CounterThread.ThreadState == System.Threading.ThreadState.Stopped) {
                CounterThread = new Thread(CounterLoop) { Name = "ByteCounterThread" };
            }
            //if (SendThread == null || SendThread.ThreadState == System.Threading.ThreadState.Stopped) {
            //    SendThread = new Thread(SendStateLoop) { Name = "SendThread" };
            //}
            //if (ServerReceiveThread == null || ServerReceiveThread.ThreadState == System.Threading.ThreadState.Stopped) {
            //    ServerReceiveThread = new Thread(ServerReceiveStateLoop) { Name = "ServerReceiveThread" };
            //}
            //if (ClientReceiveThread == null || ClientReceiveThread.ThreadState == System.Threading.ThreadState.Stopped) {
            //    ClientReceiveThread = new Thread(ClientReceiveStateLoop) { Name = "ClientReceiveThread" };
            //}
            if (Start) {
                //if (!ConnectionCheckThread.IsAlive)
                //    ConnectionCheckThread.Start();
                if (!CounterThread.IsAlive)
                    CounterThread.Start();
                //if (!SendThread.IsAlive)
                //    SendThread.Start();
                //if (!ClientReceiveThread.IsAlive)
                //    ClientReceiveThread.Start();
                //if (!ServerReceiveThread.IsAlive)
                //    ServerReceiveThread.Start();
            }
        }

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
                        string @var = i == 0 && !hasVars ? AddVars[i] : AddVars[i] + ",";
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

        protected internal static ConcurrentDictionary<Guid, TcpClient> TcpClients = new ConcurrentDictionary<Guid, TcpClient>();
        protected internal static ConcurrentDictionary<Guid, TcpServer> TcpServers = new ConcurrentDictionary<Guid, TcpServer>();
        protected internal static Thread CounterThread;
        private static DateTime LastCount = DateTime.UtcNow;
        private static TimeSpan OneSecond = TimeSpan.FromSeconds(1);
        //protected internal static Thread SendThread;
        //protected internal static Thread ClientReceiveThread;
        //protected internal static Thread ServerReceiveThread;
        protected internal static void CounterLoop() {
            while (Alive) {
                for (int i = 0; i < TcpClients.Count; i++) {
                    MethodExtensions.TryInvoke(() => {
                        if(TcpClients.Count - 1 < i) return;
                        var Client = TcpClients.Values.ElementAt(i);
                        if (Client.Connected) {
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
                        if (Server.Connected) {
                            Server.Connection._SentBytesPerSecond = Server.Connection.SentBytesCounter;
                            Server.Connection._ReceivedBytesPerSecond = Server.Connection.ReceivedBytesCounter;
                            Server.Connection.SentBytesCounter = 0;
                            Server.Connection.ReceivedBytesCounter = 0;
                            Server.InvokeBytesPerSecondUpdate(Server.Connection);
                        }
                    });
                }
                //var Clients = TcpClients.ToList();
                //Clients.ForEach(c => {
                //    var Client = c.Value;
                //    if (Client.Connected) {
                //        Client.Connection._SentBytesPerSecond = Client.Connection.SentBytesCounter;
                //        Client.Connection._ReceivedBytesPerSecond = Client.Connection.ReceivedBytesCounter;
                //        Client.Connection.SentBytesCounter = 0;
                //        Client.Connection.ReceivedBytesCounter = 0;
                //        Client.InvokeBytesPerSecondUpdate(Client.Connection);
                //    }
                //});
                //var Servers = TcpServers.ToList();
                //Servers.ForEach(c => {
                //    var Server = c.Value;
                //    if (Server.isListening) {
                //        Server.Connection._SentBytesPerSecond = Server.Connection.SentBytesCounter;
                //        Server.Connection._ReceivedBytesPerSecond = Server.Connection.ReceivedBytesCounter;
                //        Server.Connection.SentBytesCounter = 0;
                //        Server.Connection.ReceivedBytesCounter = 0;
                //        Server.InvokeBytesPerSecondUpdate(Server.Connection);
                //    }
                //});
                do {
                    Thread.Sleep(1);
                } while(DateTime.UtcNow - LastCount < OneSecond);
                LastCount = DateTime.UtcNow;
            }
        }
        //private static void CounterDelegate(TcpBase Instance) {
        //    Instance._SentBytesPerSecond = (int)Instance.SentBytesCounter;
        //    Instance._ReceivedBytesPerSecond = (int)Instance.ReceivedBytesCounter;
        //    Instance.SentBytesCounter = 0L;
        //    Instance.ReceivedBytesCounter = 0L;
        //    Instance.InvokeBytesPerSecondUpdate();
        //}
        //private static void ServerConnectionCheckDelegate(TcpServer Server) {
        //    Server.Clients.ValuesForAll((Client) => {
        //        if (Client.Socket is null)
        //            return;
        //        Client.Send(new PingObj());
        //        if (!Client.Poll())
        //            Server.CloseConnection(Client);
        //    });
        //}
        //private static void ClientConnectionCheckDelegate(TcpClient Client) {
        //    if (Client.Socket != null && Client.Connection != null)
        //        Client.Send(new PingObj());
        //    if(Client.Connection != null) {
        //        lock (Client.Connection) {
        //            if (!Client.Connection.Poll())
        //                Client.Disconnect();
        //        }
        //    }
        //}
        //private static void SendStateLoop() {
        //    while (Alive) {
        //        var clientsTasks = Task.Run(() => {
        //            var clients = TcpClients.Values.ToArray();
        //            for (int i = 0; i < clients.Length - 1; i++) {
        //                var client = clients[i];
        //                if (!client.isSending)
        //                    Task.Run(() => { ClientSendDelegate(client); });
        //            }
        //        });
        //        var serversTasks = Task.Run(() => {
        //            var servers = TcpServers.Values.ToArray();
        //            for (int i = 0; i < servers.Length - 1; i++) {
        //                var server = servers[i];
        //                if (!server.isSending)
        //                    Task.Run(() => { ServerSendDelegate(server); });
        //            }
        //        });
        //        //var t1 = TcpClients.ValuesForAll(ClientSendDelegate);
        //        //var t2 = TcpServers.ValuesForAll(ServerSendDelegate);
        //        //t1 = null;
        //        //t2 = null;
        //        Thread.Sleep(1);
        //    }
        //}
        //private static void ClientSendDelegate(TcpClient Client) {
        //    if (Client.Connection != null && !Client.Connection.IsSending) {
        //        lock (Client.Connection.SendQueue) {
        //            if ((Client.Connected) && !Client.Connection.Closed && !Client.isSending) {
        //                var Items = new List<SendQueueItem>();
        //                while (Client.Connection.SendQueue.Count > 0) {
        //                    SendQueueItem Item;
        //                    Client.Connection.SendQueue.TryDequeue(out Item);
        //                    if (Item != null)
        //                        Items.Add(Item);
        //                }
        //                if (Items.Count == 0)
        //                    return;
        //                for (int i = 0; i < Items.Count; i++) {
        //                    var state = Items[i];
        //                    if (!Client.Connected || Client.Connection.Closed) 
        //                        break;
        //                    if (state != null)
        //                        Client.Connection.TrySendQueueItem(ref state);
        //                }
        //            }
        //        }
        //    }
        //}
        //private static void ServerSendDelegate(TcpServer Server) {
        //    if (Server.isListening) {
        //        foreach (var item in Server.Clients) {
        //            if (!item.Value.IsSending) {
        //                lock (item.Value?.SendQueue) {
        //                    while (item.Value?.SendQueue.Count > 0) {
        //                        SendQueueItem state = null;
        //                        var success = item.Value?.SendQueue.TryDequeue(out state);
        //                        if (state != null) 
        //                            Server.Connection.TrySendQueueItem(ref state);
        //                    }
        //                }
        //            }
        //        }
        //    }
        //}
        //private static void ClientReceiveStateLoop() {
        //    while (Alive) {
        //        if (TcpClients.Count > 0) {
        //            for (int i = 0, loopTo = TcpClients.Count - 1; i <= loopTo; i++) {
        //                var client = TcpClients.ElementAt(i).Value;
        //                if (client != null && client.Connected) {
        //                    TcpConnection Connection = client.Connection;
        //                    if (Connection is null || Connection.Socket is null || Connection.IsReceiving || Connection.Socket.Available == 0) {
        //                        continue;
        //                    } else {
        //                        Connection.IsReceiving = true;
        //                        Task.Run(async () => {
        //                            await Connection.Receive();
        //                            Connection.IsReceiving = false;
        //                        });
        //                    }
        //                }
        //            }
        //        }
        //        Thread.Sleep(1);
        //    }
        //}
        //private static void ServerReceiveStateLoop() {
        //    while (Alive) {
        //        if (TcpServers.Count > 0) {
        //            for (int i = 0, loopTo = TcpServers.Count - 1; i <= loopTo; i++) {
        //                var server = TcpServers.ElementAt(i).Value;
        //                if (server != null && server.isListening) {
        //                    if (server.Clients.Count > 0) {
        //                        for (int ClientIndex = 0, loopTo1 = server.Clients.Count; ClientIndex < loopTo1; ClientIndex++) {
        //                            if (server.Clients.Count - 1 < i) break;
        //                            var getClient = MethodExtensions.TryInvoke(server.Clients.ElementAt, ref ClientIndex);
        //                            if (getClient != null) {
        //                                TcpConnection Connection = getClient.Result.Value;
        //                                if (Connection == null || Connection.Socket is null || Connection.IsReceiving || Connection.Socket.Available == 0) {
        //                                    continue;
        //                                } else {
        //                                    Connection.IsReceiving = true;
        //                                    Interlocked.Increment(ref server.ActiveClients);
        //                                    Task.Run(async () => {
        //                                        await Connection.Receive();
        //                                        Connection.IsReceiving = false;
        //                                        Interlocked.Decrement(ref server.ActiveClients);
        //                                    });
        //                                }
        //                            }
        //                        }
        //                    }
        //                }
        //            }
        //        }
        //        Thread.Sleep(1);
        //    }
        //}

    }
}