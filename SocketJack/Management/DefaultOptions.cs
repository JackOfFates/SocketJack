using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SocketJack.Networking.P2P;
using SocketJack.Networking.Shared;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;

namespace SocketJack.Management {

    /// <summary>
    /// Default options for <see langword="TcpClient"/> and <see langword="TcpServer"/>.
    /// <para>These options are used by default unless overridden here.</para>
    /// <para>Set before creating any instances of TcpClient or TcpServer.</para>
    /// <para>Example:</para>
    /// <code>
    /// DefaultOptions.Logging = <see langword="True"/>
    /// </code>
    /// </summary>
    public class DefaultOptions {

        /// <summary>
        /// Default serialization protocol for <see langword="TcpClient"/> and <see langword="TcpServer"/>.
        /// </summary>
        public static ISerializer DefaultSerializer { get; set; } = new JsonProtocol();

        /// <summary>
        /// Output events like OnConnected, OnDisconnected, OnConnectionFailed, OnClientTimedOut, and more to Console and Debug Output Window.
        /// Send and Receive events only logged when LogSendEvents or LogReceiveEvents are set to True.
        /// </summary>
        /// <returns></returns>
        public static bool Logging { get; set; } = false;

        /// <summary>
        /// Log sent events to console.
        /// </summary>
        /// <returns></returns>
        public static bool LogSendEvents { get; set; } = false;

        /// <summary>
        /// <para>Log received events to console.</para>
        /// </summary>
        /// <returns></returns>
        public static bool LogReceiveEvents { get; set; } = false;

        /// <summary>
        /// <para>Turns on or off Peer to Peer functionality.</para>
        /// <para>Required to be set before TcpClient.Connect or TcpServer.StartListening.</para>
        /// </summary>
        /// <returns></returns>
        public static bool PeerToPeerEnabled { get; set; } = true;

        /// <summary>
        /// Timespan to attempt to connect to a server.
        /// <para>Default is 3 seconds.</para>
        /// </summary>
        /// <returns></returns>
        public static TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(3L);

        /// <summary>
        /// When True the client will automatically retry connection to the last used Host / Port.
        /// </summary>
        /// <returns>False by default.</returns>
        public static bool AutoReconnect { get; set; } = false;

        /// <summary>
        /// Maximum concurrent pending connections.
        /// </summary>
        /// <returns>9999 is default. Lower to reduce processing time.</returns>
        public static int Backlog { get; set; } = 9999;

        /// <summary>
        /// Maximum buffer size per connection.
        /// </summary>
        /// <remarks>Default is 100MB.</remarks>
        /// <value>Long</value>
        /// <remarks></remarks>
        public static int MaximumBufferSize {
            get {
                return _MaximumBufferSize;
            }
            set {
                _MaximumBufferSize = value;
            }
        }
        protected internal static int _MaximumBufferSize = 104857600;

        /// <summary>
        /// Maximum receiving bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 for unlimited.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        public static int MaximumDownloadMbps {
            get {
                return _MaximumDownloadMbps;
            }
            set {
                _MaximumDownloadMbps = value;
                MaximumDownloadBytesPerSecond = MaximumDownloadMbps * 1024 * 1024 / 8;
            }
        }
        protected internal static int _MaximumDownloadMbps = 100;
        protected internal static int MaximumDownloadBytesPerSecond = 13107200;

        /// <summary>
        /// Download buffer size.
        /// </summary>
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// <summary>
        public static int DownloadBufferSize { get; set; } = 65536;

        /// <summary>
        /// Maximum Upload bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 for unlimited.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        public static int MaximumUploadMbps {
            get {
                return _MaximumUploadMbps;
            }
            set {
                _MaximumUploadMbps = value;
                MaximumUploadBytesPerSecond = MaximumUploadMbps * 1024 * 1024 / 8;
            }
        }
        protected internal static int _MaximumUploadMbps = 100;
        protected internal static int MaximumUploadBytesPerSecond = 13107200;

        /// <summary>
        /// Upload buffer size.
        /// </summary>
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// <summary>
        public static int UploadBufferSize { get; set; } = 65536;

        /// <summary>
        /// Types that are allowed to be deserialized.
        /// </summary>
        public static WhitelistedTypes Whitelist { get; internal set; } = new WhitelistedTypes(new[]{typeof(PingObj), typeof(PeerIdentification), typeof(PeerRedirect), typeof(PeerServer) });
    }
}