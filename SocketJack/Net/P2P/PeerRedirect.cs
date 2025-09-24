namespace SocketJack.Net.P2P {

    public class PeerRedirect {

        public PeerRedirect(string Sender, string Recipient, object Obj) {
            this.Sender = Sender;
            this.Recipient = Recipient;
            this.value = Obj;
            this.Type = Obj.GetType().AssemblyQualifiedName;
        }

        public PeerRedirect(string Recipient, object Obj) {
            this.Recipient = Recipient;
            this.value = Obj;
            this.Type = Obj.GetType().AssemblyQualifiedName;
        }

        public PeerRedirect() {

        }

        public string CleanTypeName() {
            string name = Type.Contains(",") ? Type.Remove(Type.IndexOf(",")) : Type;
            if(name.Contains("+"))
                name = name.Remove(0, name.IndexOf("+")+1);
            return name;
        }

        public string Type { get; set; }
        public object value { get; set; }
        public string Sender { get; set; }
        public string Recipient { get; set; }

    }

}