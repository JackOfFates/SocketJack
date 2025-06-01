using SocketJack.Networking.Shared;
using System;

namespace SocketJack.Networking.P2P {

    public class PeerRedirect {

        public PeerRedirect(PeerIdentification Sender, PeerIdentification Recipient, TcpBase Parent, object Obj) {
            this.Sender = Sender;
            this.Recipient = Recipient;
            this.Redirect = Obj;
            this.Type = Obj.GetType().AssemblyQualifiedName;
        }

        public PeerRedirect StripIPs() {
            Sender.IP = null;
            Recipient.IP = null;
            return this;
        }

        public string CleanTypeName() {
            string name = Type.Contains(",") ? Type.Remove(Type.IndexOf(",")) : Type;
            if(name.Contains("+"))
                name = name.Remove(0, name.IndexOf("+")+1);
            return name;
        }

        public string Type { get; set; }
        public object Redirect { get; set; }
        public PeerIdentification Sender { get; set; }
        public PeerIdentification Recipient { get; set; }

    }

}