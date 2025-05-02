
namespace SocketJack.Serialization {
    public class DeserializedObject {
        public object Obj { get; set; }

        public DeserializedObject(object Obj, long Length) {
            this.Obj = Obj;
            this.Length = Length;
        }

        public long Length { get; set; }
    }
}