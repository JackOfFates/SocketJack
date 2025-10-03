using SocketJack.Net;
using SocketJack.Net.WebSockets;
using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Extensions {
    public static class ISocketExtensions {
        public static T As<T>(this ISocket socket) {
            if (socket is T)
                return (T)socket;
            else
                return default;
        }

        public static WebSocketClient AsWsClient(this ISocket socket) {
            return socket.As<WebSocketClient>();
        }
        public static TcpClient AsTcpClient(this ISocket socket) {
            return socket.As<TcpClient>();
        }

        public static WebSocketServer AsWsServer(this ISocket socket) {
            return socket.As<WebSocketServer>();
        }
        public static TcpServer AsTcpServer(this ISocket socket) {
            return socket.As<TcpServer>();
        }

    }
}
