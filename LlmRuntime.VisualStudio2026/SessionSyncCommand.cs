namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

[VisualStudioContribution]
internal sealed class SessionSyncCommand : SocketJackAuthenticatedCommand
{
    public override CommandConfiguration CommandConfiguration => new("%SocketJack.SessionSync.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.Sync, IconSettings.IconAndText),
    };

    protected override async Task ExecuteAuthenticatedCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<SessionSyncToolWindow>(activate: true, cancellationToken);
    }
}
