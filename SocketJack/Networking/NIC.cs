using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Mono.Nat;

namespace SocketJack.Networking {

    /// <summary>
    /// Active Network Interface Card
    /// </summary>
    public class NIC {

        public static event OnInterfaceDiscoveredEventHandler OnInterfaceDiscovered;
        public delegate void OnInterfaceDiscoveredEventHandler(int MTU, IPAddress LocalIP);

        public static event NatDiscoveredEventHandler NatDiscovered;

        public delegate void NatDiscoveredEventHandler();
        public static event OnErrorEventHandler OnError;

        public delegate void OnErrorEventHandler(Exception ex);

        /// <summary>
        /// Maximum Transmission Unit defined by the currently active NIC.
        /// </summary>
        public static int MTU {
            get {
                return _MTU;
            }
        }
        private static int _MTU = -1;

        /// <summary>
        /// <para>Overhead for Segment Type string for Reflection.</para>
        /// <para>Default is derrived from Typical JSON serialization.</para>
        /// <para></para>
        /// <para>If the Serializer is adding a lot of padding and object transfers are failing, increase this on Client and Server.</para>
        /// <para>Default is 200 (bytes)</para>
        /// </summary>
        public static int SegmentOverhead {
            get {
                return _SegmentOverhead;
            }
            set {
                int LastValue = _SegmentOverhead;
                _SegmentOverhead = value;
                _MTU = MTU + LastValue - SegmentOverhead;
            }
        }
        private static int _SegmentOverhead = 200;

        /// <summary>
        /// State of the active Network Interface Card
        /// </summary>
        /// <returns>True when the NIC has been found.</returns>
        public static bool InterfaceDiscovered {
            get {
                return _InterfaceDiscovered;
            }
        }
        private static bool _InterfaceDiscovered = true;

        /// <summary>
        /// Network Address Translation
        /// </summary>
        /// <returns>Discovered NAT device, or NULL.</returns>
        public static INatDevice NAT {
            get {
                return _NAT;
            }
        }
        private static INatDevice _NAT;
        private static bool DiscoveryStarted = false;

        /// <summary>
        /// Call if lost internet connection during construction of TcpClient/TcpServer and NAT = null.
        /// </summary>
        public static void DiscoverNAT() {
            if (NAT == null&& !DiscoveryStarted) {
                DiscoveryStarted = true;
                NatUtility.DeviceFound += OnNatDeviceFound;
                NatUtility.StartDiscovery();
            }
        }

        private static void OnNatDeviceFound(object sender, DeviceEventArgs args) {
            _NAT = args.Device;
            NatDiscovered?.Invoke();
        }

        /// <summary>
        /// Gets the Maximum Transmission Unit of the active NIC.
        /// </summary>
        /// <param name="LocalIP"></param>
        /// <returns></returns>
        public static async Task<int> GetMTU(IPAddress LocalIP) {
            return await Task.Run(() => {
                var computerProperties = IPGlobalProperties.GetIPGlobalProperties();
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                if (nics == null|| nics.Length < 1)
                    return -1;
                foreach (NetworkInterface adapter in nics) {
                    var properties = adapter.GetIPProperties();
                    int output = -1;
                    if (adapter.Supports(NetworkInterfaceComponent.IPv4)) {
                        var ipv4 = properties.GetIPv4Properties();
                        foreach (var IP in properties.UnicastAddresses) {
                            if ((IP.Address.ToString() ?? "") == (LocalIP.ToString() ?? "")) {
                                output = ipv4.Mtu;
                                break;
                            }
                        }
                    }
                    if (output != -1) {
                        return output;
                    }
                }
                return -1;
            });
        }

        /// <summary>
        /// <para>Check if the internet is available.</para>
        /// <para>Uses Google.com as a test.</para>
        /// </summary>
        /// <returns></returns>
        public static bool InternetAvailable() {
            using (var ping = new Ping()) {
                var reply = ping.Send("www.google.com");
                if (reply != null && reply.Status != IPStatus.Success) {
                    return false;
                } else {
                    return true;
                }
            }
        }

        /// <summary>
        /// <para>Get the Local IP Address of the active NIC.</para>
        /// <para>Uses a UDPClient to get the LocalEndPoint.</para>
        /// </summary>
        /// <returns></returns>
        public static IPAddress LocalIP() {
            var u = new UdpClient("209.159.154.138", 1);
            var localAddr = ((IPEndPoint)u.Client.LocalEndPoint).Address;
            return localAddr;
        }

        /// <summary>
        /// <para>Check if a port is available.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="port"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static bool PortAvailable(int port, bool ForwardPortIfAvailable = false) {
            bool isAvailable = true;

            var ipGlobalProperties = IPGlobalProperties.GetIPGlobalProperties();
            TcpConnectionInformation[] tcpConnInfoArray = ipGlobalProperties.GetActiveTcpConnections();

            foreach (TcpConnectionInformation tcpi in tcpConnInfoArray) {
                if (tcpi.LocalEndPoint.Port == port) {
                    isAvailable = false;
                    break;
                }
            }

            if (isAvailable && ForwardPortIfAvailable && InterfaceDiscovered && _NAT != null) {
                bool PortForwarded = false;
                try {
                    NAT.CreatePortMap(new Mapping(Protocol.Tcp, port, port));
                    PortForwarded = true;
                } catch (Exception ex) {
                    OnError?.Invoke(ex);
                }
                return isAvailable && PortForwarded;
            } else {
                return isAvailable;
            }
        }

        private static int RandomNumber(int LowerBound, int UpperBound) {
            return new Random().Next(LowerBound, UpperBound);
        }

        /// <summary>
        /// <para>Find an open port within a range.</para>
        /// <para>Optionally forward the port if available.</para>
        /// </summary>
        /// <param name="PortLowerBound"></param>
        /// <param name="PortUpperBound"></param>
        /// <param name="ForwardPortIfAvailable"></param>
        /// <returns></returns>
        public static long FindOpenPort(int PortLowerBound, int PortUpperBound, bool ForwardPortIfAvailable = false) {
            int Port = RandomNumber(PortLowerBound, PortUpperBound);
            while (!PortAvailable(Port, ForwardPortIfAvailable))
                Port = RandomNumber(PortLowerBound, PortUpperBound);
            return Port;
        }

        protected internal static async void Initialize() {
            if (_NAT is null)
                DiscoverNAT();
            if (!InterfaceDiscovered) {
                var lIP = LocalIP();
                _InterfaceDiscovered = true;
                _MTU = await GetMTU(lIP) - _SegmentOverhead;
                OnInterfaceDiscovered?.Invoke(MTU, lIP);
            }
        }

    }
}