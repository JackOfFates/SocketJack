namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

[VisualStudioContribution]
internal sealed class SocketJackCopilotServersCommand : SocketJackAuthenticatedCommand
{
    public override CommandConfiguration CommandConfiguration => new("%SocketJack.CopilotServers.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.ToolWindow, IconSettings.IconAndText),
    };

    protected override async Task ExecuteAuthenticatedCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<SocketJackCopilotServersToolWindow>(activate: true, cancellationToken);
    }
}
