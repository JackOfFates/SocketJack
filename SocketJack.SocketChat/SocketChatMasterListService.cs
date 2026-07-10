using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace SocketJack.SocketChat {
    public sealed class SocketChatMasterListService {
        private readonly string _path;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };

        public string Path => _path;
        public SocketChatMasterListService(string path) { _path = System.IO.Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path))); }

        public SocketChatMasterList Read() {
            try { return File.Exists(_path) ? JsonSerializer.Deserialize<SocketChatMasterList>(File.ReadAllText(_path), JsonOptions) ?? new SocketChatMasterList() : new SocketChatMasterList(); }
            catch { return new SocketChatMasterList(); }
        }

        public SocketChatMasterList Update(Action<SocketChatMasterList> mutation) {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path) ?? ".");
            string lockPath = _path + ".lock";
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++) {
                try {
                    using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                        SocketChatMasterList list = Read();
                        mutation(list);
                        list.Users = list.Users.GroupBy(u => u.Fingerprint, StringComparer.Ordinal).Select(g => g.OrderByDescending(u => u.LastSeenUtc).First()).ToList();
                        list.Servers = list.Servers.GroupBy(s => s.Id, StringComparer.Ordinal).Select(g => g.OrderByDescending(s => s.LastSeenUtc).First()).ToList();
                        list.FriendRequests = list.FriendRequests.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.First()).ToList();
                        list.Revision++;
                        list.UpdatedUtc = DateTimeOffset.UtcNow;
                        string temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
                        if (File.Exists(_path)) File.Replace(temp, _path, null); else File.Move(temp, _path);
                        return list;
                    }
                } catch (IOException ex) { last = ex; Thread.Sleep(100 + attempt * 25); }
            }
            throw new IOException("Dropbox masterlist is busy or unavailable.", last);
        }
    }
}
