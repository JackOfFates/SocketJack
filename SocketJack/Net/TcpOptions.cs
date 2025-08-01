using System;
using SocketJack.Compression;
using SocketJack.Net.P2P;
using SocketJack.Serialization;
using SocketJack.Serialization.Json;

namespace SocketJack.Net {

    /// <summary>
    /// Default options for <see langword="TcpClient"/> and <see langword="TcpServer"/>.
    /// <para>These options are used by default unless overridden here.</para>
    /// <para>Set before creating any instances of TcpClient or TcpServer.</para>
    /// <para>Example:</para>
    /// <code>
    /// DefaultOptions.Logging = <see langword="True"/>
    /// </code>
    /// </summary>
    public class TcpOptions {

        public static TcpOptions DefaultOptions = new TcpOptions();

        /// <summary>
        /// Serializer for both <see langword="TcpClient"/> and <see langword="TcpServer"/>.
        /// <para>Default is System.Text.Json.</para>
        /// </summary>
        public ISerializer Serializer { get; set; } = new JsonSerializer();

        /// <summary>
        /// Compression algorithm for both <see langword="TcpClient"/> and <see langword="TcpServer"/>.
        /// <para>Default is GZip2.</para>
        /// </summary>
        public ICompression CompressionAlgorithm { get; set; } = new GZip2Compression();

        /// <summary>
        /// Output OnConnected, OnDisconnected, and OnConnectionFailed events to Console.
        /// Send and Receive events only logged when LogSendEvents or LogReceiveEvents are set to True.
        /// </summary>
        /// <returns></returns>
        public bool Logging { get; set; } = false;

        /// <summary>
        /// Log sent events to console.
        /// </summary>
        /// <returns></returns>
        public bool LogSendEvents { get; set; } = false;

        /// <summary>
        /// <para>Log received events to console.</para>
        /// </summary>
        /// <returns></returns>
        public bool LogReceiveEvents { get; set; } = false;

        /// <summary>
        /// Log to Debug Output Window.
        /// </summary>
        /// <returns></returns>
        public bool LogToOutput { get; set; } = false;

        /// <summary>
        /// <para>Turns on or off Peer to Peer functionality.</para>
        /// <para>Required to be set before TcpClient.Connect or TcpServer.StartListening.</para>
        /// </summary>
        /// <returns></returns>
        public bool UsePeerToPeer { get; set; } = true;

        /// <summary>
        /// Update the title of the console window with traffic statistics.
        /// </summary>
        /// <returns></returns>
        public bool UpdateConsoleTitle { get; set; } = false;

        /// <summary>
        /// Timespan to attempt to connect to a server.
        /// <para>Default is 3 seconds.</para>
        /// </summary>
        /// <returns></returns>
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(3L);

        /// <summary>
        /// When True the client will automatically retry connection to the last used Host / Port.
        /// </summary>
        /// <returns>False by default.</returns>
        public bool AutoReconnect { get; set; } = false;

        /// <summary>
        /// Maximum concurrent pending connections.
        /// </summary>
        /// <returns>100 is default. Lower to reduce processing time.</returns>
        public int Backlog { get; set; } = 100;

        /// <summary>
        /// Maximum buffer size per connection.
        /// </summary>
        /// <remarks>Default is 100MB.</remarks>
        /// <value>Long</value>
        /// <remarks></remarks>
        public int MaximumBufferSize {
            get {
                return _MaximumBufferSize;
            }
            set {
                _MaximumBufferSize = value;
            }
        }
        protected internal int _MaximumBufferSize = 104857600;

        /// <summary>
        /// Maximum receiving bandwidth.
        /// </summary>
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 to disable buffering.</para>
        /// <para>Disabling buffer will not work with SSL.</para>
        /// </remarks>
        /// <value>Integer</value>
        public int MaximumDownloadMbps {
            get {
                return _MaximumDownloadMbps;
            }
            set {
                _MaximumDownloadMbps = value;
                isDownloadBuffered = MaximumDownloadMbps > 0;
                MaximumDownloadBytesPerSecond = MaximumDownloadMbps * 1024 * 1024 / 8;
            }
        }
        internal bool isDownloadBuffered = true;
        protected internal int _MaximumDownloadMbps = 100;
        protected internal int MaximumDownloadBytesPerSecond = 13107200;

        /// <summary>
        /// Download buffer size.
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public int DownloadBufferSize { get; set; } = 65536;
       

        /// <summary>
        /// Maximum Upload bandwidth.
        /// <remarks>
        /// <para>Default is 100Mbps. Set to 0 for unlimited.</para>
        /// </remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public int MaximumUploadMbps {
            get {
                return _MaximumUploadMbps;
            }
            set {
                _MaximumUploadMbps = value;
                isUploadBuffered = MaximumUploadMbps > 0;
                MaximumUploadBytesPerSecond = MaximumUploadMbps * 1024 * 1024 / 8;
            }
        }
        internal bool isUploadBuffered = true;
        protected internal int _MaximumUploadMbps = 100;
        protected internal int MaximumUploadBytesPerSecond = 13107200;

        /// <summary>
        /// Upload buffer size.
        /// <remarks>Default is 65536 bytes.</remarks>
        /// <value>Integer</value>
        /// <remarks></remarks>
        /// </summary>
        public int UploadBufferSize { get; set; } = 65536;

        /// <summary>
        /// <para>Use compression for network transfer.</para>
        /// <para>Must be set before connection started.</para>
        /// </summary>
        public bool UseCompression { get; set; } = false;

        /// <summary>
        /// Send and receive objects with terminator (default is <see langword="true"/>).
        /// <para><see langword="Required"/> to parse objects.
        /// An exception would be used in the case of HttpServer where the terminator is not used.
        /// HttpServer simply reads to the end of the stream.</para>
        /// </summary>
        public bool UseTerminatedStreams { get; set; } = true;

        /// <summary>
        /// Use SSL for network transfer.
        /// </summary>
        public bool UseSsl { get; set; } = false;

        /// <summary>
        /// Types that are allowed to be deserialized.
        /// </summary>
        public TypeList Whitelist { get; internal set; } = new TypeList(new[] {
            // SocketJack types,
            typeof(MetadataKeyValue),
            typeof(PeerAction),
            //typeof(PingObject), Deprecated
            typeof(Identifier),
            typeof(Identifier[]),
            typeof(PeerRedirect),
            typeof(PeerServer),
            typeof(Wrapper),
            typeof(Segment),
            // System serializable types
            typeof(string),
            typeof(int),
            typeof(long),
            typeof(short),
            typeof(byte),
            typeof(bool),
            typeof(double),
            typeof(float),
            typeof(decimal),
            typeof(DateTime),
            typeof(Guid),
            typeof(TimeSpan),
            typeof(byte[]),
            typeof(char),
            typeof(char[]),
            typeof(uint),
            typeof(ulong),
            typeof(ushort),
            typeof(sbyte)
        });

        /// <summary>
        /// Types that are not allowed to be deserialized.
        /// </summary>
        public TypeList Blacklist { get; internal set; } = new TypeList(new[] { typeof(object) } );
    }
}