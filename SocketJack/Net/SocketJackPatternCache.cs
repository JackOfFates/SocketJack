using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SocketJack.Net {
    internal sealed class SocketJackPatternCache {
        private const byte StoreAndUseCommand = 1;
        private const byte UseCommand = 2;
        private const int KeyLength = 16;
        private const int StoreHeaderLength = 5 + 1 + KeyLength + 4;
        private const int UseFrameLength = 5 + 1 + KeyLength;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SJPC1");

        private readonly object _gate = new object();
        private readonly Dictionary<string, SendPatternEntry> _sendPatterns = new Dictionary<string, SendPatternEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, CacheEntry> _sendCachedKeys = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly Dictionary<string, CacheEntry> _receiveCachedKeys = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private long _sendCachedBytes;
        private long _receiveCachedBytes;
        private long _sequence;

        public byte[] PrepareSend(byte[] payload, NetworkOptions options, Func<byte[], bool> canSendFrame = null) {
            if (payload == null || payload.Length == 0 || options == null || !options.EnablePatternCache)
                return payload;

            int maxBytes = Math.Max(1, options.PatternCacheMaximumBytes);
            int minBytes = Math.Max(1, options.PatternCacheMinimumBytes);
            int threshold = Math.Max(1, options.PatternCachePromotionThreshold);

            if (payload.Length < minBytes || payload.Length > maxBytes)
                return payload;

            byte[] hash = ComputeSha256(payload);
            byte[] keyBytes = new byte[KeyLength];
            Buffer.BlockCopy(hash, 0, keyBytes, 0, KeyLength);
            string patternHash = ToHex(hash, 0, hash.Length);
            string key = ToHex(keyBytes, 0, keyBytes.Length);

            lock (_gate) {
                if (!_sendPatterns.TryGetValue(patternHash, out SendPatternEntry pattern)) {
                    pattern = new SendPatternEntry {
                        PatternHash = patternHash,
                        Key = key,
                        KeyBytes = keyBytes
                    };
                    _sendPatterns[patternHash] = pattern;
                }

                pattern.Occurrences++;

                if (pattern.RemoteCached && _sendCachedKeys.TryGetValue(pattern.Key, out CacheEntry cached)) {
                    cached.LastUsed = ++_sequence;
                    byte[] useFrame = BuildUseFrame(pattern.KeyBytes);
                    return canSendFrame == null || canSendFrame(useFrame) ? useFrame : payload;
                }

                if (pattern.Occurrences < threshold)
                    return payload;

                byte[] storeFrame = BuildStoreFrame(pattern.KeyBytes, payload);
                if (canSendFrame != null && !canSendFrame(storeFrame))
                    return payload;

                AddSendCachedLocked(pattern, payload.Length, maxBytes);
                return pattern.RemoteCached ? storeFrame : payload;
            }
        }

        public bool TryResolveReceived(byte[] payload, NetworkOptions options, out byte[] resolved, out string error) {
            resolved = payload;
            error = null;

            if (payload == null || payload.Length < UseFrameLength || !HasMagic(payload))
                return true;

            if (options == null || !options.EnablePatternCache) {
                error = "Received a SocketJack pattern-cache frame while NetworkOptions.EnablePatternCache is disabled.";
                return false;
            }

            byte command = payload[Magic.Length];
            byte[] keyBytes = new byte[KeyLength];
            Buffer.BlockCopy(payload, Magic.Length + 1, keyBytes, 0, KeyLength);
            string key = ToHex(keyBytes, 0, keyBytes.Length);

            lock (_gate) {
                if (command == StoreAndUseCommand) {
                    if (payload.Length < StoreHeaderLength) {
                        error = "Received a malformed SocketJack pattern-cache store frame.";
                        return false;
                    }

                    int valueLength = ReadInt32(payload, Magic.Length + 1 + KeyLength);
                    if (valueLength < 0 || payload.Length != StoreHeaderLength + valueLength) {
                        error = "Received a SocketJack pattern-cache store frame with an invalid length.";
                        return false;
                    }

                    byte[] value = new byte[valueLength];
                    if (valueLength > 0)
                        Buffer.BlockCopy(payload, StoreHeaderLength, value, 0, valueLength);

                    int maxBytes = Math.Max(1, options.PatternCacheMaximumBytes);
                    AddReceiveCachedLocked(key, value, maxBytes);
                    resolved = value;
                    return true;
                }

                if (command == UseCommand) {
                    if (_receiveCachedKeys.TryGetValue(key, out CacheEntry cached)) {
                        cached.LastUsed = ++_sequence;
                        resolved = cached.Value;
                        return true;
                    }

                    error = "SocketJack pattern-cache miss for key " + key + ".";
                    return false;
                }
            }

            error = "Received a SocketJack pattern-cache frame with an unknown command.";
            return false;
        }

        public static string ComputeSha256Hex(byte[] value) {
            if (value == null || value.Length == 0)
                return string.Empty;

            byte[] hash = ComputeSha256(value);
            return ToHex(hash, 0, hash.Length);
        }

        private void AddSendCachedLocked(SendPatternEntry pattern, int size, int maxBytes) {
            if (pattern == null || size <= 0 || size > maxBytes)
                return;

            EvictSendUntilFitsLocked(size, maxBytes);
            if (_sendCachedBytes + size > maxBytes)
                return;

            if (_sendCachedKeys.TryGetValue(pattern.Key, out CacheEntry existing))
                _sendCachedBytes -= existing.Size;

            _sendCachedKeys[pattern.Key] = new CacheEntry {
                Key = pattern.Key,
                PatternHash = pattern.PatternHash,
                Size = size,
                LastUsed = ++_sequence
            };
            _sendCachedBytes += size;
            pattern.RemoteCached = true;
        }

        private void AddReceiveCachedLocked(string key, byte[] value, int maxBytes) {
            int size = value?.Length ?? 0;
            if (string.IsNullOrEmpty(key) || size <= 0 || size > maxBytes)
                return;

            EvictReceiveUntilFitsLocked(size, maxBytes);
            if (_receiveCachedBytes + size > maxBytes)
                return;

            if (_receiveCachedKeys.TryGetValue(key, out CacheEntry existing))
                _receiveCachedBytes -= existing.Size;

            _receiveCachedKeys[key] = new CacheEntry {
                Key = key,
                Value = value,
                Size = size,
                LastUsed = ++_sequence
            };
            _receiveCachedBytes += size;
        }

        private void EvictSendUntilFitsLocked(int incomingSize, int maxBytes) {
            while (_sendCachedBytes + incomingSize > maxBytes && _sendCachedKeys.Count > 0) {
                CacheEntry victim = _sendCachedKeys.Values.OrderBy(entry => entry.LastUsed).First();
                _sendCachedKeys.Remove(victim.Key);
                _sendCachedBytes -= victim.Size;
                if (!string.IsNullOrEmpty(victim.PatternHash) && _sendPatterns.TryGetValue(victim.PatternHash, out SendPatternEntry pattern))
                    pattern.RemoteCached = false;
            }
        }

        private void EvictReceiveUntilFitsLocked(int incomingSize, int maxBytes) {
            while (_receiveCachedBytes + incomingSize > maxBytes && _receiveCachedKeys.Count > 0) {
                CacheEntry victim = _receiveCachedKeys.Values.OrderBy(entry => entry.LastUsed).First();
                _receiveCachedKeys.Remove(victim.Key);
                _receiveCachedBytes -= victim.Size;
            }
        }

        private static byte[] BuildUseFrame(byte[] keyBytes) {
            byte[] frame = new byte[UseFrameLength];
            Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
            frame[Magic.Length] = UseCommand;
            Buffer.BlockCopy(keyBytes, 0, frame, Magic.Length + 1, KeyLength);
            return frame;
        }

        private static byte[] BuildStoreFrame(byte[] keyBytes, byte[] payload) {
            byte[] frame = new byte[StoreHeaderLength + payload.Length];
            Buffer.BlockCopy(Magic, 0, frame, 0, Magic.Length);
            frame[Magic.Length] = StoreAndUseCommand;
            Buffer.BlockCopy(keyBytes, 0, frame, Magic.Length + 1, KeyLength);
            WriteInt32(frame, Magic.Length + 1 + KeyLength, payload.Length);
            Buffer.BlockCopy(payload, 0, frame, StoreHeaderLength, payload.Length);
            return frame;
        }

        private static bool HasMagic(byte[] payload) {
            if (payload == null || payload.Length < Magic.Length + 1)
                return false;
            for (int i = 0; i < Magic.Length; i++) {
                if (payload[i] != Magic[i])
                    return false;
            }
            return true;
        }

        private static byte[] ComputeSha256(byte[] value) {
            using (var sha = SHA256.Create())
                return sha.ComputeHash(value);
        }

        private static string ToHex(byte[] bytes, int offset, int count) {
            if (bytes == null || count <= 0)
                return string.Empty;

            var sb = new StringBuilder(count * 2);
            for (int i = 0; i < count; i++)
                sb.Append(bytes[offset + i].ToString("x2"));
            return sb.ToString();
        }

        private static int ReadInt32(byte[] bytes, int offset) {
            return bytes[offset]
                | (bytes[offset + 1] << 8)
                | (bytes[offset + 2] << 16)
                | (bytes[offset + 3] << 24);
        }

        private static void WriteInt32(byte[] bytes, int offset, int value) {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
            bytes[offset + 2] = (byte)((value >> 16) & 0xff);
            bytes[offset + 3] = (byte)((value >> 24) & 0xff);
        }

        private sealed class SendPatternEntry {
            public string PatternHash;
            public string Key;
            public byte[] KeyBytes;
            public int Occurrences;
            public bool RemoteCached;
        }

        private sealed class CacheEntry {
            public string Key;
            public string PatternHash;
            public byte[] Value;
            public int Size;
            public long LastUsed;
        }
    }
}
