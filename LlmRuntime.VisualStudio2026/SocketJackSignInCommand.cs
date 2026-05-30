namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

[VisualStudioContribution]
internal sealed class SocketJackSignInCommand : Command
{
    private readonly SocketJackVisualStudioAuthService authService = new();

    public override CommandConfiguration CommandConfiguration => new("%SocketJack.SignIn.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.User, IconSettings.IconAndText),
    };

    public override Task InitializeAsync(CancellationToken cancellationToken)
    {
        SocketJackVisualStudioAuthService.AuthStateChanged += this.OnAuthStateChanged;
        this.UpdateAuthState();
        return base.InitializeAsync(cancellationToken);
    }

    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        await this.Extensibility.Shell().ShowToolWindowAsync<SocketJackSignInToolWindow>(activate: true, cancellationToken);
    }

    private void OnAuthStateChanged(object? sender, EventArgs e)
    {
        this.UpdateAuthState();
    }

    private void UpdateAuthState()
    {
        bool signedIn = this.authService.HasStoredToken();
        this.SetEnabledState(true);
        this.SetVisibilityState(!signedIn);
    }
}
