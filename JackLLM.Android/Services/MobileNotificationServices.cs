namespace JackLLM.Mobile.Services;

public interface IMobileNotificationService
{
    Task EnsurePermissionAsync();
    void StartGeneration(string serverKey);
    void NotifyThinkingCompleted(string serverKey, string sessionId);
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
    private NotificationNavigationTarget? _pending;

    public event EventHandler? PendingChanged;

    public void QueueSession(string serverKey, string sessionId)
    {
        lock (_gate) _pending = new NotificationNavigationTarget(serverKey ?? "", sessionId ?? "");
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public NotificationNavigationTarget? TakePending()
    {
        lock (_gate)
        {
            NotificationNavigationTarget? value = _pending;
            _pending = null;
            return value;
        }
    }
}

public sealed record NotificationNavigationTarget(string ServerKey, string SessionId);
