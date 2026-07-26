using System.Text.Json;
using JackLLM.Mobile.Models;
using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Services;

public sealed class ServerStore
{
    private const string Key = "jackllm.mobile.servers.v1";

    public IReadOnlyList<ServerInfo> Load()
    {
        try { return JsonSerializer.Deserialize<List<ServerInfo>>(Preferences.Default.Get(Key, "[]")) ?? new(); }
        catch { return Array.Empty<ServerInfo>(); }
    }

    public void Save(ServerInfo server)
    {
        var servers = Load().ToList();
        string key = server.LaunchKey;
        servers.RemoveAll(item => item.LaunchKey.Equals(key, StringComparison.OrdinalIgnoreCase));
        server.IsSaved = true;
        servers.Insert(0, server);
        Preferences.Default.Set(Key, JsonSerializer.Serialize(servers.Take(20)));
    }

    public void Remove(ServerInfo server)
    {
        var servers = Load().Where(item => !item.LaunchKey.Equals(server.LaunchKey, StringComparison.OrdinalIgnoreCase));
        Preferences.Default.Set(Key, JsonSerializer.Serialize(servers));
    }
}
