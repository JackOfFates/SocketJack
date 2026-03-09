using SocketJack.Serialization;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace SocketJack.Net {

    /// <summary>
    /// Shared WebSocket frame-building and JS constructor generation utilities
    /// used by both <see cref="WebSocketProtocolHandler"/> and
    /// <see cref="SocketJack.Net.WebSockets.WebSocketServer"/>.
    /// </summary>
    internal static class WebSocketFrameHelper {

        /// <summary>
        /// Returns the header length for a WebSocket frame with the given payload size.
        /// </summary>
        public static int GetHeaderLength(int payloadLength) {
            if (payloadLength <= 125) return 2;
            if (payloadLength <= 65535) return 4;
            return 10;
        }

        /// <summary>
        /// Writes a WebSocket frame header into <paramref name="dest"/> and returns
        /// the number of bytes written.
        /// </summary>
        public static int WriteHeader(Span<byte> dest, bool useBinaryFrame, int payloadLength) {
            dest[0] = useBinaryFrame ? (byte)0x82 : (byte)0x81;
            if (payloadLength <= 125) {
                dest[1] = (byte)payloadLength;
                return 2;
            }
            if (payloadLength <= 65535) {
                dest[1] = 126;
                dest[2] = (byte)((payloadLength >> 8) & 0xFF);
                dest[3] = (byte)(payloadLength & 0xFF);
                return 4;
            }
            dest[1] = 127;
            ulong len = (ulong)payloadLength;
            dest[2] = (byte)((len >> 56) & 0xFF);
            dest[3] = (byte)((len >> 48) & 0xFF);
            dest[4] = (byte)((len >> 40) & 0xFF);
            dest[5] = (byte)((len >> 32) & 0xFF);
            dest[6] = (byte)((len >> 24) & 0xFF);
            dest[7] = (byte)((len >> 16) & 0xFF);
            dest[8] = (byte)((len >> 8) & 0xFF);
            dest[9] = (byte)(len & 0xFF);
            return 10;
        }

        /// <summary>
        /// Builds a complete WebSocket frame (header + payload) as a new byte array.
        /// </summary>
        public static byte[] BuildFrame(byte[] payload, bool binary) {
            byte opcode = binary ? (byte)0x82 : (byte)0x81;
            int headerLen;
            byte[] frame;

            if (payload.Length <= 125) {
                headerLen = 2;
                frame = new byte[headerLen + payload.Length];
                frame[0] = opcode;
                frame[1] = (byte)payload.Length;
            } else if (payload.Length <= 65535) {
                headerLen = 4;
                frame = new byte[headerLen + payload.Length];
                frame[0] = opcode;
                frame[1] = 126;
                frame[2] = (byte)((payload.Length >> 8) & 0xFF);
                frame[3] = (byte)(payload.Length & 0xFF);
            } else {
                headerLen = 10;
                frame = new byte[headerLen + payload.Length];
                frame[0] = opcode;
                frame[1] = 127;
                ulong len = (ulong)payload.Length;
                frame[2] = (byte)((len >> 56) & 0xFF);
                frame[3] = (byte)((len >> 48) & 0xFF);
                frame[4] = (byte)((len >> 40) & 0xFF);
                frame[5] = (byte)((len >> 32) & 0xFF);
                frame[6] = (byte)((len >> 24) & 0xFF);
                frame[7] = (byte)((len >> 16) & 0xFF);
                frame[8] = (byte)((len >> 8) & 0xFF);
                frame[9] = (byte)(len & 0xFF);
            }

            Buffer.BlockCopy(payload, 0, frame, headerLen, payload.Length);
            return frame;
        }

        /// <summary>
        /// Generates JavaScript class constructors for all whitelisted types so that
        /// browser clients can create typed objects for the SocketJack protocol.
        /// </summary>
        public static string GenerateJSConstructors(IEnumerable<string> whitelist) {
            var javascript = new StringBuilder();
            foreach (var t in whitelist) {
                Type type = Wrapper.GetValueType(t);
                if (type == typeof(string) || type == typeof(Wrapper)) continue;
                if (type == null) continue;
                if (!type.IsClass || type.IsAbstract || type.IsGenericType) continue;
                var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                string className = type.Name;
                javascript.Append("class " + className + " {" + Environment.NewLine);
                javascript.Append("    constructor(");
                for (int i = 0; i < properties.Length; i++) {
                    javascript.Append(properties[i].Name);
                    if (i < properties.Length - 1) javascript.Append(", ");
                }
                javascript.Append(") {" + Environment.NewLine);
                for (int i = 0; i < properties.Length; i++) {
                    javascript.Append("        this." + properties[i].Name + " = " + properties[i].Name + ";" + Environment.NewLine);
                }
                javascript.Append("    }" + Environment.NewLine);
                javascript.Append("}" + Environment.NewLine);
                javascript.Append("if (typeof window !== 'undefined') { window['" + className + "'] = " + className + "; }" + Environment.NewLine + Environment.NewLine);
            }
            return javascript.ToString();
        }
    }
}
