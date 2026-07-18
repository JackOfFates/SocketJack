namespace JackLLM.Mobile.Services;

public interface IMobileNotificationService
{
    Task EnsurePermissionAsync();
    void StartGeneration(string serverKey);
    void NotifyThinkingCompleted(string serverKey);
    void StopGeneration();
    void ResetUnread();
}

public static class AppVisibilityService
{
    private static int _active;
    public static bool IsActive => Volatile.Read(ref _active) == 1;
    public static event Action<bool>? VisibilityChanged;
    public static void SetActive(bool active)
    {
        int value = active ? 1 : 0;
        if (Interlocked.Exchange(ref _active, value) != value) VisibilityChanged?.Invoke(active);
    }
}

public sealed class NotificationNavigationService
{
    private readonly object _gate = new();
    private string _pendingServerKey = "";

    public event EventHandler? PendingChanged;

    public void QueueSessions(string serverKey)
    {
        lock (_gate) _pendingServerKey = serverKey ?? "";
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public string TakePendingServerKey()
    {
        lock (_gate)
        {
            string value = _pendingServerKey;
            _pendingServerKey = "";
            return value;
        }
    }
}
