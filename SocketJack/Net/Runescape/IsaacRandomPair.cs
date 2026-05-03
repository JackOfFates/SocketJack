namespace SocketJack.Net.Runescape {

    /// <summary>
    /// Holds a pair of <see cref="IsaacRandom"/> ciphers used for encoding
    /// and decoding game packets.
    /// </summary>
    public class IsaacRandomPair {

        public IsaacRandom EncodingRandom { get; }
        public IsaacRandom DecodingRandom { get; }

        public IsaacRandomPair(IsaacRandom encodingRandom, IsaacRandom decodingRandom) {
            EncodingRandom = encodingRandom;
            DecodingRandom = decodingRandom;
        }
    }
}
