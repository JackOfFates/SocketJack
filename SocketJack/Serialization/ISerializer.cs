
namespace SocketJack.Serialization {

    /// <summary>
    /// Interface used to add alternative serializers.
    /// </summary>
    public interface ISerializer {

        byte[] Serialize(object Obj);
        ObjectWrapper Deserialize(byte[] Data);
        object GetPropertyValue(PropertyValueArgs e);
    }

}