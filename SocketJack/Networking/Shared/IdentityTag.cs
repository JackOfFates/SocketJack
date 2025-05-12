using System;
using System.Collections.Generic;
using System.Text;

namespace SocketJack.Networking.Shared
{
    public class IdentityTag {
        public string Tag { get; set; }
        public string ID { get; set; }

        public IdentityTag(string ID, string Tag) {
            this.ID = ID;
            this.Tag = Tag;
        }
    }
}
