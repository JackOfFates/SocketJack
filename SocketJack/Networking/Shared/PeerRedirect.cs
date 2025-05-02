using System;

namespace SocketJack.Networking.Shared {

    [Serializable]
    public class PeerRedirect {

        public PeerRedirect(PeerIdentification Sender, PeerIdentification Recipient, object Obj) {
            this.Sender = Sender;
            this.Recipient = Recipient;
            this.Obj = Obj;
        }

        public object Obj { get; set; }
        public PeerIdentification Sender { get; set; }
        public PeerIdentification Recipient { get; set; }

    }

}