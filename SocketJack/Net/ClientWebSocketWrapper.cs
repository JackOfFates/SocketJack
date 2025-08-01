using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;

namespace SocketJack.Net {
    public class ClientWebSocketWrapper {
        public ClientWebSocket InnerWebSocket { get; } = new ClientWebSocket();

        public Socket GetUnderlyingSocket() {
            // Get the _innerWebSocket field of ClientWebSocket (holds a ManagedWebSocket instance)
            var innerWebSocketField = typeof(ClientWebSocket).GetField("_innerWebSocket", BindingFlags.Instance | BindingFlags.NonPublic);
            if (innerWebSocketField == null) return null;

            var managedWebSocket = innerWebSocketField.GetValue(InnerWebSocket);
            if (managedWebSocket == null) return null;

            // Get the WebSocket property from ManagedWebSocket (returns the actual WebSocket instance)
            var webSocketProperty = managedWebSocket.GetType().GetProperty("WebSocket", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (webSocketProperty == null) return null;

            WebSocket webSocketInstance = (WebSocket)webSocketProperty.GetValue(managedWebSocket);

            // Get the _stream field from the WebSocket instance (holds the connection stream)
            var streamField = webSocketInstance.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic);
            if (streamField == null) return null;

            var connectionStream = streamField.GetValue(webSocketInstance);

            // Get the _connection field from the connection stream (holds the actual stream object)
            var connectionField = connectionStream.GetType().GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (connectionField == null) return null;

            var actualStream = connectionField.GetValue(connectionStream);
            if (actualStream == null) return null;

            // Get the _stream field from the actual stream (should be a NetworkStream)
            var networkStreamField = actualStream.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (networkStreamField == null) return null;

            var networkStream = networkStreamField.GetValue(actualStream) as NetworkStream;

            // Get the underlying Socket from the NetworkStream
            var socketProperty = networkStream?.GetType().GetProperty("Socket", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (socketProperty == null) return null;

            return socketProperty.GetValue(networkStream) as Socket;
        }
    }
}
