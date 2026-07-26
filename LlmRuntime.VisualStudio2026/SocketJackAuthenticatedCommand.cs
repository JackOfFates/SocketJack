namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

internal abstract class SocketJackAuthenticatedCommand : Command
{
    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        SocketJackLocalProxySupervisor.StartBestEffortFromStoredSelection();
        this.SetEnabledState(true);
        return base.InitializeAsync(cancellationToken);
    }

    public sealed override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.ExecuteAuthenticatedCommandAsync(context, cancellationToken);
    }

    protected abstract Task ExecuteAuthenticatedCommandAsync(IClientContext context, CancellationToken cancellationToken);
}
