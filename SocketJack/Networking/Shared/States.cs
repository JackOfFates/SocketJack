
namespace SocketJack.Networking.Shared {
    public class SendState {
        public object Object { get; set; }

        public ConnectedClient Client { get; set; }

        public SendState(object Object, ConnectedClient Client) {
            this.Object = Object;
            this.Client = Client;
        }

    }

    internal class ReceiveState {
        public ConnectedClient Client = null;
        public static readonly byte[] Terminator = new[] { (byte)226, (byte)128, (byte)161 };

        public int BytesRead {
            get {
                return _BytesRead;
            }
            set {
                _BytesRead = value;
            }
        }
        private int _BytesRead = 0;

        public byte[] Buffer {
            get {
                return _Buffer;
            }
            protected internal set {
                _Buffer = value;
            }
        }
        private byte[] _Buffer;

        public string GetString() {
            if (Buffer is null) {
                return string.Empty;
            } else {
                return System.Text.Encoding.UTF8.GetString(Buffer);
            }
        }

    }
}