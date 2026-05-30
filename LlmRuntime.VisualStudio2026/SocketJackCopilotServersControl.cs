namespace LlmRuntime.VisualStudio2026;

using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

internal sealed class SocketJackCopilotServersControl : RemoteUserControl
{
    public SocketJackCopilotServersControl(object? dataContext, SynchronizationContext? synchronizationContext = null)
        : base(dataContext, synchronizationContext)
    {
    }
}
