using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Net.Runescape {

    /// <summary>
    /// Login state machine states for an Old School RuneScape connection.
    /// </summary>
    public enum OsrsLoginState {
        Handshake,
        LoginHeader,
        LoginPayload,
        Authenticated
    }

    /// <summary>
    /// Per-connection session state for the OSRS protocol.
    /// </summary>
    public class OsrsSession {
        public OsrsLoginState State { get; set; } = OsrsLoginState.Handshake;
        public long ServerSeed { get; set; }
        public int UsernameHash { get; set; }
        public int LoginLength { get; set; }
        public bool Reconnecting { get; set; }
        public IsaacRandomPair RandomPair { get; set; }
        public PlayerCredentials Credentials { get; set; }
        public List<byte> Buffer { get; } = new List<byte>();
    }

    /// <summary>
    /// An <see cref="IProtocolHandler"/> that detects and processes Old School
    /// RuneScape (revision 317) client connections on a <see cref="MutableTcpServer"/>.
    /// <para>
    /// Register this handler so a single port can serve OSRS game clients alongside
    /// HTTP, WebSocket, and other SocketJack protocols.
    /// </para>
    /// <example>
    /// <code>
    /// var server = new MutableTcpServer(43594, "GameServer");
    /// server.RegisterProtocol(new OldschoolRunescape());
    /// server.Listen();
    /// </code>
    /// </example>
    /// </summary>
    public class OldschoolRunescapeProtocol : IProtocolHandler {

        private static readonly Random Random = new Random();

        private readonly ConcurrentDictionary<Guid, OsrsSession> _sessions =
            new ConcurrentDictionary<Guid, OsrsSession>();

        /// <summary>
        /// Raised when a player has been fully authenticated and ISAAC ciphers are ready.
        /// </summary>
        public event Action<NetworkConnection, OsrsSession> PlayerAuthenticated;

        public string Name => "OldSchoolRuneScape";

        /// <summary>
        /// Returns <see langword="true"/> when the first byte is the OSRS game
        /// login handshake opcode (14).
        /// </summary>
        public bool CanHandle(byte[] data) {
            if (data == null || data.Length < 1)
                return false;

            // HandshakeType.ServiceGame = 14
            return data[0] == 14;
        }

        public void ProcessReceive(MutableTcpServer server, NetworkConnection connection, ref IReceivedEventArgs e) {
            byte[] incoming = MutableTcpServer.TryGetRawBytes(e.Obj);
            if (incoming == null || incoming.Length == 0)
                return;

            var session = _sessions.GetOrAdd(connection.ID, _ => {
                connection.SuppressConnectionTest = true;
                return new OsrsSession();
            });

            lock (session.Buffer) {
                session.Buffer.AddRange(incoming);
                ProcessBuffer(connection, session);
            }
        }

        public void OnDisconnected(MutableTcpServer server, NetworkConnection connection) {
            _sessions.TryRemove(connection.ID, out _);
        }

        private void ProcessBuffer(NetworkConnection connection, OsrsSession session) {
            bool progress = true;
            while (progress && session.Buffer.Count > 0) {
                progress = false;
                switch (session.State) {
                    case OsrsLoginState.Handshake:
                        progress = DecodeHandshake(connection, session);
                        break;
                    case OsrsLoginState.LoginHeader:
                        progress = DecodeLoginHeader(connection, session);
                        break;
                    case OsrsLoginState.LoginPayload:
                        progress = DecodeLoginPayload(connection, session);
                        break;
                    case OsrsLoginState.Authenticated:
                        // Post-login game packets can be handled by subscribers
                        // of PlayerAuthenticated or by further protocol extensions.
                        progress = false;
                        break;
                }
            }
        }

        /// <summary>
        /// Reads the handshake opcode and username hash, then sends back
        /// the server session key (exchange data response).
        /// </summary>
        private bool DecodeHandshake(NetworkConnection connection, OsrsSession session) {
            // Need at least 2 bytes: handshake opcode + username hash
            if (session.Buffer.Count < 2)
                return false;

            // First byte is the handshake opcode (already matched in CanHandle).
            byte opcode = session.Buffer[0];
            session.UsernameHash = session.Buffer[1];
            session.Buffer.RemoveRange(0, 2);

            if (opcode != 14) {
                CloseConnection(connection);
                return false;
            }

            // Generate a server seed for ISAAC cipher initialization.
            session.ServerSeed = NextLong();

            // Send exchange data response: status(1) + padding(8) + serverSeed(8) = 17 bytes
            var response = new byte[17];
            response[0] = 0; // StatusExchangeData
            // Bytes 1-8: padding (zeros)
            WriteLong(response, 9, session.ServerSeed);

            WriteToConnection(connection, response);
            session.State = OsrsLoginState.LoginHeader;
            return true;
        }

        /// <summary>
        /// Reads the login type byte and payload length.
        /// </summary>
        private bool DecodeLoginHeader(NetworkConnection connection, OsrsSession session) {
            if (session.Buffer.Count < 2)
                return false;

            int type = session.Buffer[0];
            // Type 16 = standard login, Type 18 = reconnection
            if (type != 16 && type != 18) {
                WriteResponseCode(connection, 11); // StatusLoginServerRejectedSession
                CloseConnection(connection);
                return false;
            }

            session.Reconnecting = type == 18;
            session.LoginLength = session.Buffer[1] & 0xFF;
            session.Buffer.RemoveRange(0, 2);
            session.State = OsrsLoginState.LoginPayload;
            return true;
        }

        /// <summary>
        /// Reads the full login payload: client version, archive CRCs, ISAAC seeds,
        /// username, and password.
        /// </summary>
        private bool DecodeLoginPayload(NetworkConnection connection, OsrsSession session) {
            if (session.Buffer.Count < session.LoginLength)
                return false;

            byte[] payload = session.Buffer.GetRange(0, session.LoginLength).ToArray();
            session.Buffer.RemoveRange(0, session.LoginLength);

            int pos = 0;

            // Client version: 255 - magicByte
            int version = 255 - payload[pos++];

            // Release number (short)
            int release = (payload[pos] << 8) | payload[pos + 1];
            pos += 2;

            // Memory status
            int memoryStatus = payload[pos++];
            if (memoryStatus != 0 && memoryStatus != 1) {
                WriteResponseCode(connection, 11);
                CloseConnection(connection);
                return false;
            }

            bool lowMemory = memoryStatus == 1;

            // Archive CRCs (9 ints = 36 bytes)
            var crcs = new int[9];
            for (int i = 0; i < 9; i++) {
                crcs[i] = ReadInt(payload, pos);
                pos += 4;
            }

            // RSA block length byte
            int rsaBlockLength = payload[pos++];
            if (rsaBlockLength != session.LoginLength - 41) {
                WriteResponseCode(connection, 11);
                CloseConnection(connection);
                return false;
            }

            // RSA decryption marker (should be 10)
            int rsaId = payload[pos++];
            if (rsaId != 10) {
                WriteResponseCode(connection, 11);
                CloseConnection(connection);
                return false;
            }

            // ISAAC seeds
            long clientSeed = ReadLong(payload, pos);
            pos += 8;

            long reportedSeed = ReadLong(payload, pos);
            pos += 8;

            if (reportedSeed != session.ServerSeed) {
                WriteResponseCode(connection, 11);
                CloseConnection(connection);
                return false;
            }

            // UID
            int uid = ReadInt(payload, pos);
            pos += 4;

            // Username and password (null-terminated strings)
            string username = ReadString(payload, ref pos);
            string password = ReadString(payload, ref pos);

            // Build ISAAC cipher pair
            var seed = new int[4];
            seed[0] = (int)(clientSeed >> 32);
            seed[1] = (int)clientSeed;
            seed[2] = (int)(session.ServerSeed >> 32);
            seed[3] = (int)session.ServerSeed;

            var decodingRandom = new IsaacRandom(seed);
            for (int i = 0; i < seed.Length; i++) {
                seed[i] += 50;
            }
            var encodingRandom = new IsaacRandom(seed);

            var socketAddress = connection.EndPoint;
            var hostAddress = socketAddress?.Address?.ToString() ?? "unknown";

            session.Credentials = new PlayerCredentials {
                Username = username,
                Password = password,
                EncodedUsername = session.UsernameHash,
                Uid = uid,
                HostAddress = hostAddress
            };

            session.RandomPair = new IsaacRandomPair(encodingRandom, decodingRandom);

            // Validate credentials
            if (string.IsNullOrEmpty(username) || username.Length > 12
                || password.Length < 4 || password.Length > 20) {
                WriteResponseCode(connection, 3); // StatusInvalidCredentials
                CloseConnection(connection);
                return false;
            }

            // Send login OK: status(1) + rights(1) + flagged(1)
            var okResponse = new byte[3];
            okResponse[0] = 2; // StatusOk
            okResponse[1] = 0; // Rights (normal player)
            okResponse[2] = 0; // Not flagged
            WriteToConnection(connection, okResponse);

            session.State = OsrsLoginState.Authenticated;

            try {
                PlayerAuthenticated?.Invoke(connection, session);
            } catch {
                // Swallow subscriber exceptions to keep the server stable.
            }

            return true;
        }

        #region Helpers

        private static void WriteToConnection(NetworkConnection connection, byte[] data) {
            var stream = connection.Stream;
            if (stream != null) {
                try {
                    stream.Write(data, 0, data.Length);
                    stream.Flush();
                    connection.TrackBytesSent(data.Length);
                } catch {
                    // Connection may have been closed.
                }
            }
        }

        private static void WriteResponseCode(NetworkConnection connection, int code) {
            WriteToConnection(connection, new byte[] { (byte)code });
        }

        private static void CloseConnection(NetworkConnection connection) {
            try {
                connection.CloseConnection();
            } catch {
                // Already closed.
            }
        }

        private static void WriteLong(byte[] buffer, int offset, long value) {
            buffer[offset] = (byte)(value >> 56);
            buffer[offset + 1] = (byte)(value >> 48);
            buffer[offset + 2] = (byte)(value >> 40);
            buffer[offset + 3] = (byte)(value >> 32);
            buffer[offset + 4] = (byte)(value >> 24);
            buffer[offset + 5] = (byte)(value >> 16);
            buffer[offset + 6] = (byte)(value >> 8);
            buffer[offset + 7] = (byte)value;
        }

        private static int ReadInt(byte[] buffer, int offset) {
            return (buffer[offset] << 24)
                 | (buffer[offset + 1] << 16)
                 | (buffer[offset + 2] << 8)
                 | buffer[offset + 3];
        }

        private static long ReadLong(byte[] buffer, int offset) {
            return ((long)ReadInt(buffer, offset) << 32)
                 | ((long)ReadInt(buffer, offset + 4) & 0xFFFFFFFFL);
        }

        private static string ReadString(byte[] buffer, ref int offset) {
            var sb = new StringBuilder();
            while (offset < buffer.Length) {
                int b = buffer[offset++];
                if (b == 10 || b == 0) // newline or null terminator
                    break;
                sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static long NextLong() {
            byte[] buf = new byte[8];
            lock (Random) {
                Random.NextBytes(buf);
            }
            return BitConverter.ToInt64(buf, 0);
        }

        #endregion
    }
}
