namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

internal abstract class SocketJackAuthenticatedCommand : Command
{
    private readonly SocketJackVisualStudioAuthService authService = new();

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        SocketJackLocalProxySupervisor.StartBestEffortFromStoredSelection();
        SocketJackVisualStudioAuthService.AuthStateChanged += this.OnAuthStateChanged;
        this.UpdateAuthState();
        return base.InitializeAsync(cancellationToken);
    }

    public sealed override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        if (!this.authService.HasStoredToken())
        {
            this.UpdateAuthState();
            await this.Extensibility.Shell().ShowToolWindowAsync<SocketJackSignInToolWindow>(activate: true, cancellationToken);
            return;
        }

        try
        {
            SocketJackAuthState state = this.authService.Load();
            await this.authService.ValidateAsync(state, cancellationToken);
        }
        catch (SocketJackAuthRequiredException)
        {
            this.UpdateAuthState();
            await this.Extensibility.Shell().ShowToolWindowAsync<SocketJackSignInToolWindow>(activate: true, cancellationToken);
            return;
        }

        await this.ExecuteAuthenticatedCommandAsync(context, cancellationToken);
    }

    protected abstract Task ExecuteAuthenticatedCommandAsync(IClientContext context, CancellationToken cancellationToken);

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        this.UpdateAuthState();
    }

    private void UpdateAuthState()
    {
        bool signedIn = this.authService.HasStoredToken();
        this.SetEnabledState(signedIn);
    }
}
