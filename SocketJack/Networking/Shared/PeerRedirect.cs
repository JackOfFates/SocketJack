using System;

namespace SocketJack.Networking.Shared {

    [Serializable]
    public class PeerRedirect {

        public PeerRedirect(PeerIdentification Sender, PeerIdentification Recipient, object Obj) {
            this.Sender = Sender;
            this.Recipient = Recipient;
            this.Obj = Obj;
            this.Type = Obj.GetType().AssemblyQualifiedName;
        }

        public PeerRedirect StripIPs() {
            Sender.IP = null;
            Recipient.IP = null;
            return this;
        }

        public object Obj { get; set; }
        public string Type { get; set; }
        public PeerIdentification Sender { get; set; }
        public PeerIdentification Recipient { get; set; }

    }

}