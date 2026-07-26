using System.Text.Json;
using Microsoft.Maui.Storage;

namespace JackLLM.Mobile.Services;

public sealed record RecentSessionReference(string ServerKey, string SessionId, DateTimeOffset OpenedAtUtc);

public sealed class RecentSessionStore
{
    private const string Key = "jackllm.mobile.recentSessions.v1";

    public void Remember(string serverKey, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(serverKey) || string.IsNullOrWhiteSpace(sessionId)) return;
        List<RecentSessionReference> entries = Load().
            Where(item => !item.ServerKey.Equals(serverKey, StringComparison.OrdinalIgnoreCase)).
            ToList();
        entries.Insert(0, new RecentSessionReference(serverKey, sessionId, DateTimeOffset.UtcNow));
        Preferences.Default.Set(Key, JsonSerializer.Serialize(entries.Take(20)));
    }

    public RecentSessionReference? FindRecent(string serverKey, TimeSpan maximumAge)
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow.Subtract(maximumAge);
        return Load().FirstOrDefault(item =>
            item.ServerKey.Equals(serverKey, StringComparison.OrdinalIgnoreCase) &&
            item.OpenedAtUtc >= cutoff &&
            !string.IsNullOrWhiteSpace(item.SessionId));
    }

    private static IReadOnlyList<RecentSessionReference> Load()
    {
        try { return JsonSerializer.Deserialize<List<RecentSessionReference>>(Preferences.Default.Get(Key, "[]")) ?? new(); }
        catch { return Array.Empty<RecentSessionReference>(); }
    }
}
