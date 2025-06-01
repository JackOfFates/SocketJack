using System;

namespace SocketJack.Serialization {

    /// <summary>
    /// Interface used to add alternative serializers.
    /// </summary>
    public interface ISerializer {

        byte[] Serialize(object Obj);
        Wrapper Deserialize(byte[] Data);
        object GetPropertyValue(PropertyValueArgs e);

        object GetValue(object source, Type asType, bool Parse);
    }

}