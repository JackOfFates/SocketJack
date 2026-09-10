using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SocketJack.Net.Database;

namespace SocketJack.MarketWatcher;

public sealed class MarketStore : IDisposable
{
    private readonly DataServer server;
    public string FilePath { get; }
    public MarketStore(string directory)
    {
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "market.sjdb");
        server = new DataServer(0, "MarketWatcherData", loadFromDisk: false) {
            AutoSave = false, EnablePayloadEncryption = true, EnableCacheOptimizing = false, DataPath = FilePath
        };
        if (File.Exists(FilePath)) server.Load();
    }
    public SavedState Read()
    {
        if (!server.Databases.TryGetValue("MarketWatcher", out var db) || !db.Tables.TryGetValue("State", out var table) || table.Rows.Count == 0)
        {
            if (File.Exists(FilePath)) throw new IOException("The encrypted database could not be loaded. Preserve it and restore access under the original Windows user.");
            return new();
        }
        return JsonSerializer.Deserialize<SavedState>(table.Rows[0][0].ToString()!, Json.Options) ?? throw new IOException("Invalid saved state.");
    }
    public async Task Write(SavedState state)
    {
        var db = server.Databases.GetOrAdd("MarketWatcher", _ => new Database("MarketWatcher"));
        var table = new Table("State") { Columns = [new Column("Json", typeof(string))], Rows = [new object[] { JsonSerializer.Serialize(state, Json.Options) }] };
        db.Tables.TryGetValue("State", out var previous);
        db.Tables["State"] = table;
        try
        {
            // Unlike SaveAsync, this public method propagates persistence failures.
            // Use the final path: SocketJack encryption is bound to that path.
            await server.SaveReencryptedCopyAsync(FilePath);
        }
        catch { if (previous != null) db.Tables["State"] = previous; else db.Tables.TryRemove("State", out _); throw; }
    }
    public static string Protect(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string Unprotect(string value)
    {
        byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Dispose() => server.Dispose();
}
