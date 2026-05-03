using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace SocketJack.Net.Runescape {

    /// <summary>
    /// A convenience wrapper that creates a <see cref="MutableTcpServer"/> pre-configured
    /// with the <see cref="OldschoolRunescapeProtocol"/> protocol handler.
    /// <example>
    /// <code>
    /// var server = new OldschoolServer(43594);
    /// server.Protocol.PlayerAuthenticated += (conn, session) => {
    ///     Console.WriteLine($"{session.Credentials.Username} logged in.");
    /// };
    /// server.Listen();
    /// </code>
    /// </example>
    /// </summary>
    public class OldschoolServer {

        private readonly MutableTcpServer _server;

        /// <summary>
        /// The underlying OSRS protocol handler.
        /// Subscribe to <see cref="OldschoolRunescapeProtocol.PlayerAuthenticated"/>
        /// to receive login events.
        /// </summary>
        public OldschoolRunescapeProtocol Protocol { get; }

        /// <summary>
        /// The underlying <see cref="MutableTcpServer"/> instance.
        /// </summary>
        public MutableTcpServer Server => _server;

        /// <summary>
        /// Creates a new OSRS server on the specified port.
        /// </summary>
        /// <param name="port">The TCP port to listen on (default 43594).</param>
        /// <param name="name">Display name for logging.</param>
        public OldschoolServer(int port = 43594, string name = "OldSchoolRuneScape") {
            Protocol = new OldschoolRunescapeProtocol();
            _server = new MutableTcpServer(port, name);
            _server.RegisterProtocol(Protocol);
        }

        /// <summary>
        /// Starts listening for incoming connections.
        /// </summary>
        public void Listen() {
            _server.Listen();
        }
    }
}
