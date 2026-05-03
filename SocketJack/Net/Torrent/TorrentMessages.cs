using System;
using System.Collections.Generic;

namespace SocketJack.Net.Torrent {

    // -----------------------------------------------------------------------
    //  Wire-protocol messages exchanged between peers and with the tracker.
    //  All types are [Serializable] so SocketJack's JSON serializer can
    //  serialize/deserialize them on the wire.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sent by a peer to the tracker to announce its presence and
    /// request a list of other peers sharing the same torrent.
    /// </summary>
    [Serializable]
    public class AnnounceRequest {
        /// <summary>InfoHash of the torrent.</summary>
        public string InfoHash { get; set; }

        /// <summary>Unique peer identifier.</summary>
        public string PeerId { get; set; }

        /// <summary>Port the peer is listening on for incoming connections.</summary>
        public int ListenPort { get; set; }

        /// <summary>Total bytes downloaded so far.</summary>
        public long Downloaded { get; set; }

        /// <summary>Total bytes remaining.</summary>
        public long Left { get; set; }

        /// <summary>Event type: started, stopped, completed, or empty for periodic.</summary>
        public string Event { get; set; } = "started";
    }

    /// <summary>
    /// Returned by the tracker in response to an <see cref="AnnounceRequest"/>.
    /// </summary>
    [Serializable]
    public class AnnounceResponse {
        /// <summary>Seconds between re-announce intervals.</summary>
        public int Interval { get; set; } = 120;

        /// <summary>List of peer endpoints sharing this torrent.</summary>
        public List<PeerEndpoint> Peers { get; set; } = new List<PeerEndpoint>();
    }

    /// <summary>
    /// Lightweight peer address returned by the tracker.
    /// </summary>
    [Serializable]
    public class PeerEndpoint {
        public string PeerId { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
    }

    // -----------------------------------------------------------------------
    //  Peer-to-peer messages (simplified BitTorrent wire protocol)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Handshake — first message exchanged between two peers.
    /// Both sides must agree on the InfoHash to continue.
    /// </summary>
    [Serializable]
    public class PeerHandshake {
        public string InfoHash { get; set; }
        public string PeerId { get; set; }
    }

    /// <summary>
    /// Bitfield — sent immediately after handshake.
    /// Each entry indicates whether the peer has that piece (true) or not.
    /// </summary>
    [Serializable]
    public class BitfieldMessage {
        public List<bool> Bitfield { get; set; } = new List<bool>();
    }

    /// <summary>
    /// Request a specific piece from a peer.
    /// </summary>
    [Serializable]
    public class PieceRequest {
        /// <summary>Zero-based piece index.</summary>
        public int PieceIndex { get; set; }
    }

    /// <summary>
    /// Delivers a requested piece's data.
    /// </summary>
    [Serializable]
    public class PieceData {
        /// <summary>Zero-based piece index.</summary>
        public int PieceIndex { get; set; }

        /// <summary>Raw piece bytes, Base64-encoded for JSON transport.</summary>
        public string Data { get; set; }
    }

    /// <summary>
    /// Notify peers that a new piece has been downloaded and verified.
    /// </summary>
    [Serializable]
    public class HaveMessage {
        /// <summary>Zero-based piece index that is now available.</summary>
        public int PieceIndex { get; set; }
    }

    /// <summary>
    /// Indicates the sender is choking (will not serve requests) or unchoking the receiver.
    /// </summary>
    [Serializable]
    public class ChokeMessage {
        public bool IsChoked { get; set; }
    }

    /// <summary>
    /// Expresses interest (or disinterest) in a peer's pieces.
    /// </summary>
    [Serializable]
    public class InterestedMessage {
        public bool IsInterested { get; set; }
    }

    // -----------------------------------------------------------------------
    //  Search / DHT-style discovery messages
    // -----------------------------------------------------------------------

    /// <summary>
    /// Search request sent to a tracker or DHT node.
    /// Supports keyword search and category filtering.
    /// </summary>
    [Serializable]
    public class TorrentSearchRequest {
        /// <summary>Keywords to match against torrent names.</summary>
        public string Query { get; set; }

        /// <summary>Optional category filter (e.g. "software", "video").</summary>
        public string Category { get; set; }

        /// <summary>Maximum results to return.</summary>
        public int MaxResults { get; set; } = 25;
    }

    /// <summary>
    /// Search results returned by a tracker or index node.
    /// </summary>
    [Serializable]
    public class TorrentSearchResponse {
        public List<TorrentSearchResult> Results { get; set; } = new List<TorrentSearchResult>();
    }

    /// <summary>
    /// A single search result pointing to a torrent.
    /// </summary>
    [Serializable]
    public class TorrentSearchResult {
        public string Name { get; set; }
        public string InfoHash { get; set; }
        public long TotalSize { get; set; }
        public int SeedCount { get; set; }
        public int PeerCount { get; set; }
        public string Category { get; set; }
        public DateTime CreatedUtc { get; set; }
    }
}
