namespace LlmRuntime.VisualStudio2026;

using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;

internal static class SocketJackCommandConfiguration
{
    [VisualStudioContribution]
    public static MenuConfiguration SocketJackMenu => new("%SocketJack.Menu.DisplayName%")
    {
        Placements =
        [
            CommandPlacement.KnownPlacements.ExtensionsMenu,
        ],
        Children =
        [
            MenuChild.Group(SocketJackGroup),
        ],
    };

    private static CommandGroupConfiguration SocketJackGroup => new()
    {
        Children =
        [
            GroupChild.Command<SocketJackSignInCommand>(),
            GroupChild.Command<SocketJackCopilotServersCommand>(),
            GroupChild.Command<SessionSyncCommand>(),
            GroupChild.Command<CreateSocketJackMcpConfigCommand>(),
        ],
    };
}
