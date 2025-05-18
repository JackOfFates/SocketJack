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
/* TODO ERROR: Skipped EndIfDirectiveTrivia
#End If
*/
namespace SocketJack.Management {

    /// <summary>
    /// Manages all Tcp Client and Server threads.
    /// </summary>
    public class ThreadManager : IDisposable {

        /// <summary>
        /// Time in Milliseconds to wait for the client to connect before timing out.
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
                if (Alive)
                    CreateThreads(Start);
            }
        }
        private static bool _Alive = false;

        /// <summary>
        /// Must be called on application shutdown to ensure all threads are closed.
        /// </summary>
        public static void Shutdown() {
            Alive = false;
            TcpClients.ValuesForAll(DisposeDelegate);
            TcpServers.ValuesForAll(DisposeDelegate);
        }

        private static void DisposeDelegate(TcpBase Instance) {
            if (Instance != null && !Instance.isDisposed)
                Instance.Dispose();
        }

        protected internal static ConcurrentDictionary<Guid, TcpClient> TcpClients = new ConcurrentDictionary<Guid, TcpClient>();
        protected internal static ConcurrentDictionary<Guid, TcpServer> TcpServers = new ConcurrentDictionary<Guid, TcpServer>();
        protected internal static Thread ConnectionCheckThread;
        protected internal static Thread CounterThread;
        protected internal static Thread SendThread;
        protected internal static Thread ClientReceiveThread;
        protected internal static Thread ServerReceiveThread;

        protected internal static void ConnectionCheckLoop() {
            while (Alive) {
                TcpClients.ValuesForAll(ClientConnectionCheckDelegate);
                TcpServers.ValuesForAll(ServerConnectionCheckDelegate);
                Thread.Sleep(Timeout);
            }
        }

        protected internal static void CounterLoop() {
            while (Alive) {
                TcpClients.ValuesForAll(CounterDelegate);
                TcpServers.ValuesForAll(CounterDelegate);
                Thread.Sleep(1000);
            }
        }

        private static void CounterDelegate(TcpBase Instance) {
            Instance._SentBytesPerSecond = (int)Instance.SentBytesCounter;
            Instance._ReceivedBytesPerSecond = (int)Instance.ReceivedBytesCounter;
            Instance.SentBytesCounter = 0L;
            Instance.ReceivedBytesCounter = 0L;
            Instance.InvokeBytesPerSecondUpdate();
        }

        private static void ServerConnectionCheckDelegate(TcpServer Server) {
            Server.ConnectedClients.ValuesForAll((Client) => {
                if (Client.Socket is null)
                    return;
                Client.Send(new PingObj());
                if (!Server.IsConnected(Client))
                    Server.CloseConnection(Client);
            });
        }

        private static void ClientConnectionCheckDelegate(TcpClient Client) {
            if (Client.BaseSocket != null && Client.BaseConnection != null)
                Client.Send(new PingObj());

            if(Client.BaseConnection != null) {
                lock (Client.BaseConnection) {
                    bool isConnected = Client.IsConnected(Client.BaseConnection);
                    if (!isConnected && !Client.BaseConnection.Closed)
                        Client.Disconnect();
                }
            }
        }

        private static void SendStateLoop() {
            while (Alive) {
                TcpClients.ValuesForAll(ClientSendDelegate);
                TcpServers.ValuesForAll(ServerSendDelegate);
                Thread.Sleep(1);
            }
        }

        private static void ClientSendDelegate(TcpClient Client) {
            if (Client.BaseConnection != null && !Client.BaseConnection.IsSending) {
                lock (Client.BaseConnection.SendQueue) {
                    if ((Client.Connected) && !Client.BaseConnection.Closed && !Client.isSending) {
                        var ProcessStates = new List<SendState>();
                        while (Client.BaseConnection.SendQueue.Count > 0) {
                            SendState state;
                            Client.BaseConnection.SendQueue.TryDequeue(out state);
                            if (state != null)
                                ProcessStates.Add(state);
                        }

                        if (ProcessStates.Count == 0)
                            return;

                        for (int i = 0; i < ProcessStates.Count; i++) {
                            var state = ProcessStates[i];
                            if (!Client.Connected || Client.BaseConnection.Closed) 
                                break;
                            if (state != null)
                                Client.ProcessSendState(ref state);
                        }
                    }
                }
            }
        }
        
        private static void ServerSendDelegate(TcpServer Server) {
            if (Server.isListening) {
                foreach (var item in Server.ConnectedClients) {
                    if (!item.Value.IsSending) {
                        lock (item.Value?.SendQueue) {
                            while (item.Value?.SendQueue.Count > 0) {
                                SendState state = null;
                                var success = item.Value?.SendQueue.TryDequeue(out state);
                                if (state != null) 
                                    Server.ProcessSendState(ref state);
                            }
                        }
                    }
                }
            }
        }

        private static void ClientReceiveStateLoop() {
            while (Alive) {
                TcpClients.ValuesForAll(async (Client) => await ClientReceiveDelegate(Client));
                Thread.Sleep(1);
            }
        }

        private async static void ServerReceiveStateLoop() {
            while (Alive) {
                for (int i = 0, loopTo = TcpServers.Count - 1; i <= loopTo; i++) {
                    var server = TcpServers.ElementAt(i).Value;
                    if (server != null && server.isListening) {
                        for (int ClientIndex = 0, loopTo1 = server.ConnectedClients.Count; ClientIndex < loopTo1; ClientIndex++) {
                            if (server.ConnectedClients.Count - 1 < i) return;

                            var Client = server.ConnectedClients.ElementAt(ClientIndex).Value;
                            if (Client == null || Client.Socket is null) {
                                return;
                            } else if (!Client.IsReceiving) {
                                Interlocked.Increment(ref server.ActiveClients);
                                Client.IsReceiving = true;
                                ReceiveResult result = await server.Receive(Client);
                                Client._DownloadBuffer = result.RemainingBytes;
                                Client.IsReceiving = false;
                                Interlocked.Decrement(ref server.ActiveClients);
                            }
                        }
                    }
                }
                Thread.Sleep(1);
            }
        }

        private async static Task ClientReceiveDelegate(TcpClient Client) {
            if (Client == null || Client.BaseSocket == null || !Client.Connected) {
                return;
            }
            lock (Client.BaseConnection) {
                if (Client.BaseConnection.IsReceiving) 
                    return;
                Client.BaseConnection.IsReceiving = true;
            }

            try {
                ReceiveResult result = await Client.ReceiveClient(Client);
                lock (Client.BaseConnection) {
                    Client.BaseConnection._DownloadBuffer = result.RemainingBytes;
                }
            } catch (Exception ex) {
                Client.InvokeOnError(Client.BaseConnection, ex);
            } finally {
                lock (Client.BaseConnection) {
                    Client.BaseConnection.IsReceiving = false;
                }
            }
        }

        //private async static Task ClientReceiveDelegate(TcpClient Client) {
        //    if (Client == null || Client.BaseSocket == null || !Client.Connected) {
        //        return;
        //    } else if (!Client.BaseConnection.IsReceiving) {
        //        Client.BaseConnection.IsReceiving = true;
        //        ReceiveResult result = await Client.ReceiveClient(Client);
        //        Client.BaseConnection._DownloadBuffer = result.RemainingBytes;
        //        Client.BaseConnection.IsReceiving = false;
        //    }
        //}

        protected internal static void CreateThreads(bool Start) {
            if (ConnectionCheckThread == null || ConnectionCheckThread.ThreadState == System.Threading.ThreadState.Stopped) {
                ConnectionCheckThread = new Thread(ConnectionCheckLoop) { Name = "ConnectionCheckThread", Priority = ThreadPriority.Highest };
            }
            if (CounterThread == null|| CounterThread.ThreadState == System.Threading.ThreadState.Stopped) {
                CounterThread = new Thread(CounterLoop) { Name = "ByteCounterThread" };
            }
            if (SendThread == null|| SendThread.ThreadState == System.Threading.ThreadState.Stopped) {
                SendThread = new Thread(SendStateLoop) { Name = "SendThread" };
            }
            if (ServerReceiveThread == null|| ServerReceiveThread.ThreadState == System.Threading.ThreadState.Stopped) {
                ServerReceiveThread = new Thread(ServerReceiveStateLoop) { Name = "ServerReceiveThread" };
            }
            if (ClientReceiveThread == null|| ClientReceiveThread.ThreadState == System.Threading.ThreadState.Stopped) {
                ClientReceiveThread = new Thread(ClientReceiveStateLoop) { Name = "ClientReceiveThread" };
            }
            if (Start) {
                if (!ConnectionCheckThread.IsAlive)
                    ConnectionCheckThread.Start();
                if (!CounterThread.IsAlive)
                    CounterThread.Start();
                if (!SendThread.IsAlive)
                    SendThread.Start();
                if (!ClientReceiveThread.IsAlive)
                    ClientReceiveThread.Start();
                if (!ServerReceiveThread.IsAlive)
                    ServerReceiveThread.Start();
            }
        }

        private bool disposedValue;

        private void EnableMultithreading() {
            Environment.SetEnvironmentVariable("DOTNET_GCCpuGroup", "1");
            Environment.SetEnvironmentVariable("DOTNET_Thread_UseAllCpuGroups", "1");
            Environment.SetEnvironmentVariable("DOTNET_gcAllowVeryLargeObjects", "1");
            Environment.SetEnvironmentVariable("DOTNET_Thread_AssignCpuGroups", "0");
            Environment.SetEnvironmentVariable("DOTNET_GCNoAffinitize", "1");
            Environment.SetEnvironmentVariable("COMPlus_GCCpuGroup", "1");
            Environment.SetEnvironmentVariable("COMPlus_Thread_UseAllCpuGroups", "1");
            Environment.SetEnvironmentVariable("COMPlus_gcAllowVeryLargeObjects", "1");
            Environment.SetEnvironmentVariable("COMPlus_Thread_AssignCpuGroups", "0");
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
                    for (int i = 0, loopTo = Variables.Count() - 1; i <= loopTo; i++) {
                        string @var = Variables[i];
                        if (!newConfig.Contains(@var)) {
                            AddVars.Add(@var);
                        }
                    }
                    for (int i = AddVars.Count - 1; i >= 0; i -= 1) {
                        string @var = i == 0 && !hasVars ? AddVars[i] : AddVars[i] + ",";
                        newConfig = newConfig.Insert(newConfig.IndexOf(ConfigPropertiesHeader) + ConfigPropertiesHeader.Length + 1, @var + Environment.NewLine);
                    }
                } else if (newConfig.Contains(AfterIndex1)) {
                    newConfig = newConfig.Insert(newConfig.IndexOf(AfterIndex1) + AfterIndex1.Length + 1, Environment.NewLine + FullConfigProperties);
                } else if (newConfig.Contains(AfterIndex2)) {
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

        private static string[] Variables = new[] { "      \"DOTNET_GCCpuGroup\": \"1\"", "      \"DOTNET_Thread_UseAllCpuGroups\": \"1\"", "      \"DOTNET_gcAllowVeryLargeObjects\": \"1\"", "      \"DOTNET_Thread_AssignCpuGroups\": \"0\"", "      \"COMPlus_Thread_AssignCpuGroups\": \"0\"", "      \"COMPlus_GCCpuGroup\": \"1\"", "      \"COMPlus_Thread_UseAllCpuGroups\": \"1\"", "      \"COMPlus_gcAllowVeryLargeObjects\": \"1\"", "      \"COMPlus_GCNoAffinitize\": \"1\"", "      \"DOTNET_GCNoAffinitize\": \"1\"" };
        private static string FullConfigProperties = "    \"configProperties\": {" + Environment.NewLine + "      \"DOTNET_GCCpuGroup\": \"1\"," + Environment.NewLine + "      \"DOTNET_Thread_UseAllCpuGroups\": \"1\"," + Environment.NewLine + "      \"DOTNET_Thread_AssignCpuGroups\": \"0\"," + Environment.NewLine + "      \"DOTNET_gcAllowVeryLargeObjects\": \"1\"," + Environment.NewLine + "      \"COMPlus_GCCpuGroup\": \"1\"," + Environment.NewLine + "      \"COMPlus_Thread_UseAllCpuGroups\": \"1\"," + Environment.NewLine + "      \"COMPlus_Thread_AssignCpuGroups\": \"0\"," + Environment.NewLine + "      \"COMPlus_gcAllowVeryLargeObjects\": \"1\"," + Environment.NewLine + "      \"DOTNET_GCNoAffinitize\": \"1\"," + Environment.NewLine + "      \"COMPlus_GCNoAffinitize\": \"1\"" + Environment.NewLine + "      }";
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
    }

}