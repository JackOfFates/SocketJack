namespace heirowLLM.Mobile.Services;

public sealed record ChickenChaserOverlayStatus(
    bool Supported,
    bool PermissionGranted,
    bool Enabled);

public interface IChickenChaserOverlayService
{
    Task<ChickenChaserOverlayStatus> GetStatusAsync();
    Task<bool> EnableAsync();
    Task DisableAsync();
    Task<bool> OpenPermissionSettingsAsync();
    void SetNotificationDot(bool visible);
}

public sealed class UnsupportedChickenChaserOverlayService : IChickenChaserOverlayService
{
    public Task<ChickenChaserOverlayStatus> GetStatusAsync() =>
        Task.FromResult(new ChickenChaserOverlayStatus(false, false, false));

    public Task<bool> EnableAsync() => Task.FromResult(false);
    public Task DisableAsync() => Task.CompletedTask;
    public Task<bool> OpenPermissionSettingsAsync() => Task.FromResult(false);
    public void SetNotificationDot(bool visible) { }
}

public sealed class ChickenChaserOverlayNavigationService
{
    private readonly object _gate = new();
    private bool _pending;

    public event EventHandler? PendingChanged;

    public void QueueOpen()
    {
        lock (_gate) _pending = true;
        PendingChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool TakePending()
    {
        lock (_gate)
        {
            bool pending = _pending;
            _pending = false;
            return pending;
        }
    }
}
