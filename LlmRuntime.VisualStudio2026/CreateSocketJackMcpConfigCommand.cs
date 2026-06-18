namespace LlmRuntime.VisualStudio2026;

using LlmRuntime.VisualStudio;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;

[VisualStudioContribution]
internal sealed class CreateSocketJackMcpConfigCommand : SocketJackAuthenticatedCommand
{
    private readonly HttpClient httpClient;

    public CreateSocketJackMcpConfigCommand(HttpClient httpClient)
    {
        this.httpClient = httpClient;
    }

    public override CommandConfiguration CommandConfiguration => new("%SocketJack.CreateMcpConfig.DisplayName%")
    {
        Icon = new(ImageMoniker.KnownValues.SettingsFile, IconSettings.IconAndText),
    };

    protected override async Task ExecuteAuthenticatedCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        try
        {
            var configurator = new SocketJackCopilotConfigurator(this.Extensibility, this.httpClient);
            SocketJackAuthState authState = new();
            if (!await SocketJackLocalWorkstationDiscovery.IsAvailableAsync(this.httpClient, cancellationToken).ConfigureAwait(false))
            {
                SocketJackVisualStudioAuthService authService = new();
                authState = await authService.ValidateAsync(authService.Load(), cancellationToken);
            }

            SocketJackConfigureResult result = await configurator.ConfigureFirstEligibleAsync(authState.AccessToken, authState.UserName, cancellationToken);
            await this.Extensibility.Shell().ShowPromptAsync(result.ToUserMessage(), PromptOptions.OK, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await this.Extensibility.Shell().ShowPromptAsync("SocketJack MCP config was not created: " + ex.Message, PromptOptions.OK, cancellationToken);
        }
    }
}
