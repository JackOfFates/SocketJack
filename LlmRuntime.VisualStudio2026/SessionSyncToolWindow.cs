namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal sealed class SessionSyncToolWindow : ToolWindow
{
    private SessionSyncViewModel? dataContext;

    public SessionSyncToolWindow()
    {
        this.Title = "Session Sync";
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
            ToolbarChild.Command<SessionSyncCommand>(),
        ],
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        this.dataContext = new SessionSyncViewModel(this.Extensibility);
        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(new SessionSyncControl(this.dataContext));
    }
}
