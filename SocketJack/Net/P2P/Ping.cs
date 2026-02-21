namespace SocketJack.Net.P2P {

    /// <summary>
    /// Latency probe sent by clients. The server echoes it back as a <see cref="Pong"/>.
    /// </summary>
    public sealed class Ping {
        /// <summary>
        /// Unix-epoch timestamp in milliseconds set by the sender.
        /// </summary>
        public long TimestampMs { get; set; }
    }

    /// <summary>
    /// Latency probe response sent by the server in reply to a <see cref="Ping"/>.
    /// </summary>
    public sealed class Pong {
        /// <summary>
        /// The original timestamp from the <see cref="Ping"/> that triggered this response.
        /// </summary>
        public long TimestampMs { get; set; }
    }
}
