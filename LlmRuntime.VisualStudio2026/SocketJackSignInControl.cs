namespace LlmRuntime.VisualStudio2026;

using System.Threading;
using Microsoft.VisualStudio.Extensibility.UI;

internal sealed class SocketJackSignInControl : RemoteUserControl
{
    public SocketJackSignInControl(object? dataContext, SynchronizationContext? synchronizationContext = null)
        : base(dataContext, synchronizationContext)
    {
    }
}
