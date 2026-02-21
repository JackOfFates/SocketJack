
namespace SocketJack.Net {
    public class SendQueueItem {
        public object Object { get; set; }
        public NetworkConnection Connection { get; set; }

        public bool Complete { get; set; }

        public SendQueueItem(object Object, NetworkConnection Connection) {
            this.Object = Object;
            this.Connection = Connection;
        }

    }
}