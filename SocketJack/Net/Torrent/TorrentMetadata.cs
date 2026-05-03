using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.Net.Torrent {

    /// <summary>
    /// Represents the metadata for a torrent — the "info" dictionary equivalent.
    /// Contains file info, piece hashes, and the unique InfoHash identifying this torrent.
    /// </summary>
    [Serializable]
    public class TorrentMetadata {

        /// <summary>
        /// Human-readable name for this torrent (e.g. "MyApp-v2.0.zip").
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Total size of the file(s) in bytes.
        /// </summary>
        public long TotalSize { get; set; }

        /// <summary>
        /// Size in bytes of each piece (last piece may be smaller).
        /// Default is 256 KB.
        /// </summary>
        public int PieceSize { get; set; } = 256 * 1024;

        /// <summary>
        /// Total number of pieces.
        /// </summary>
        public int PieceCount { get; set; }

        /// <summary>
        /// SHA-256 hash of each piece, indexed by piece number.
        /// Used to verify downloaded data integrity.
        /// </summary>
        public List<string> PieceHashes { get; set; } = new List<string>();

        /// <summary>
        /// Unique identifier for this torrent, derived from hashing the metadata.
        /// Peers use this to find each other on the tracker.
        /// </summary>
        public string InfoHash { get; set; }

        /// <summary>
        /// Tracker endpoint(s) where peers can announce and discover other peers.
        /// Format: "host:port"
        /// </summary>
        public List<string> Trackers { get; set; } = new List<string>();

        /// <summary>
        /// List of individual files in a multi-file torrent.
        /// For single-file torrents this contains one entry.
        /// </summary>
        public List<TorrentFileEntry> Files { get; set; } = new List<TorrentFileEntry>();

        /// <summary>
        /// UTC timestamp when this torrent was created.
        /// </summary>
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Build metadata from an existing file on disk.
        /// Splits the file into pieces, hashes each one, and computes the InfoHash.
        /// </summary>
        /// <param name="filePath">Path to the source file.</param>
        /// <param name="trackerEndpoints">Tracker addresses in "host:port" format.</param>
        /// <param name="pieceSize">Piece size in bytes (default 256 KB).</param>
        /// <returns>Populated <see cref="TorrentMetadata"/>.</returns>
        public static TorrentMetadata FromFile(string filePath, List<string> trackerEndpoints, int pieceSize = 256 * 1024) {
            var info = new FileInfo(filePath);
            var meta = new TorrentMetadata {
                Name = info.Name,
                TotalSize = info.Length,
                PieceSize = pieceSize,
                Trackers = trackerEndpoints,
                Files = new List<TorrentFileEntry> {
                    new TorrentFileEntry { Path = info.Name, Length = info.Length }
                }
            };

            // Hash each piece
            using (var stream = File.OpenRead(filePath)) {
                byte[] buffer = new byte[pieceSize];
                int bytesRead;
                while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0) {
                    byte[] piece = new byte[bytesRead];
                    Array.Copy(buffer, piece, bytesRead);
                    string hash = ComputeSha256(piece);
                    meta.PieceHashes.Add(hash);
                }
            }

            meta.PieceCount = meta.PieceHashes.Count;

            // InfoHash = SHA-256 of all piece hashes concatenated with the name
            string combined = meta.Name + string.Join("", meta.PieceHashes);
            meta.InfoHash = ComputeSha256(Encoding.UTF8.GetBytes(combined));

            return meta;
        }

        private static string ComputeSha256(byte[] data) {
            using (var sha = SHA256.Create()) {
                byte[] hash = sha.ComputeHash(data);
                var sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++) {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Represents a single file entry within a (possibly multi-file) torrent.
    /// </summary>
    [Serializable]
    public class TorrentFileEntry {

        /// <summary>
        /// Relative file path within the torrent.
        /// </summary>
        public string Path { get; set; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long Length { get; set; }
    }
}
