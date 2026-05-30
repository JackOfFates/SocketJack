using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LlmRuntime.Tests;

[TestClass]
public sealed class LlmBackendLifetimeGateTests
{
    [TestMethod]
    public async Task BeginDisposeAndWait_WaitsForActiveOperationBeforeRejectingNewWork()
    {
        var gate = new LlmBackendLifetimeGate("test-backend");
        using var operation = gate.Enter();
        using var disposeStarted = new ManualResetEventSlim();

        Task<bool> disposeTask = Task.Run(() =>
        {
            disposeStarted.Set();
            bool ownsDispose = gate.BeginDisposeAndWait();
            if (ownsDispose)
                gate.CompleteDispose();
            return ownsDispose;
        });

        Assert.IsTrue(disposeStarted.Wait(TimeSpan.FromSeconds(5)));
        await Task.Delay(100);
        Assert.IsFalse(disposeTask.IsCompleted);

        operation.Dispose();

        Task completed = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.AreSame(disposeTask, completed);
        Assert.IsTrue(await disposeTask);
        Assert.ThrowsException<ObjectDisposedException>(() => gate.Enter());
    }
}
