namespace LlmRuntime;

internal sealed class LlmBackendLifetimeGate
{
    private readonly object _sync = new();
    private readonly string _objectName;
    private int _activeOperations;
    private bool _disposeStarted;
    private bool _disposed;
    private TaskCompletionSource? _idle;
    private TaskCompletionSource? _disposeCompleted;

    public LlmBackendLifetimeGate(string objectName)
    {
        _objectName = objectName;
    }

    public Lease Enter()
    {
        lock (_sync)
        {
            ThrowIfDisposedCore();
            _activeOperations++;
        }

        return new Lease(this);
    }

    public bool BeginDisposeAndWait()
    {
        Task? idleTask = null;
        Task? disposeCompletedTask = null;

        lock (_sync)
        {
            if (_disposed)
                return false;

            if (_disposeStarted)
            {
                disposeCompletedTask = _disposeCompleted?.Task;
            }
            else
            {
                _disposeStarted = true;
                _disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_activeOperations > 0)
                {
                    _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    idleTask = _idle.Task;
                }
            }
        }

        disposeCompletedTask?.GetAwaiter().GetResult();
        if (disposeCompletedTask != null)
            return false;

        idleTask?.GetAwaiter().GetResult();
        return true;
    }

    public void CompleteDispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _disposeStarted = true;
            _disposeCompleted?.TrySetResult();
        }
    }

    public void ThrowIfDisposed()
    {
        lock (_sync)
            ThrowIfDisposedCore();
    }

    private void ThrowIfDisposedCore()
    {
        if (_disposed || _disposeStarted)
            throw new ObjectDisposedException(_objectName);
    }

    private void Exit()
    {
        lock (_sync)
        {
            if (_activeOperations <= 0)
                return;

            _activeOperations--;
            if (_activeOperations == 0)
                _idle?.TrySetResult();
        }
    }

    public sealed class Lease : IDisposable
    {
        private LlmBackendLifetimeGate? _owner;

        internal Lease(LlmBackendLifetimeGate owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Exit();
        }
    }
}
