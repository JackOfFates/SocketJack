namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal sealed class SocketJackCopilotServersToolWindow : ToolWindow
{
    private SocketJackCopilotServersViewModel? dataContext;

    public SocketJackCopilotServersToolWindow()
    {
        this.Title = "SocketJack Copilot Servers";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
        Toolbar = new ToolWindowToolbar(Toolbar),
    };

    [VisualStudioContribution]
    private static ToolbarConfiguration Toolbar => new("%SocketJack.Toolbar.DisplayName%")
    {
        Children =
        [
            ToolbarChild.Command<CreateSocketJackMcpConfigCommand>(),
        ],
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        this.dataContext = new SocketJackCopilotServersViewModel(this.Extensibility);
        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(new SocketJackCopilotServersControl(this.dataContext));
    }
}
