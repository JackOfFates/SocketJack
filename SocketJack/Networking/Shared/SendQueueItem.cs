
namespace SocketJack.Networking.Shared {
    public class SendQueueItem {
        public object Object { get; set; }
        public TcpConnection Connection { get; set; }

        public bool Complete { get; set; }

        public SendQueueItem(object Object, TcpConnection Connection) {
            this.Object = Object;
            this.Connection = Connection;
        }

    }
}