namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

[VisualStudioContribution]
internal sealed class SocketJackSignInToolWindow : ToolWindow
{
    private SocketJackSignInViewModel? dataContext;

    public SocketJackSignInToolWindow()
    {
        this.Title = "SocketJack Sign-in";
    }

    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        this.dataContext = new SocketJackSignInViewModel();
        return Task.CompletedTask;
    }

    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult<IRemoteUserControl>(new SocketJackSignInControl(this.dataContext));
    }
}
