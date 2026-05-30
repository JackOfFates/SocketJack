namespace LlmRuntime.VisualStudio2026;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

[VisualStudioContribution]
public sealed class SocketJackVisualStudioExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "SocketJack.LlmRuntime.VisualStudio2026",
            version: this.ExtensionAssemblyVersion,
            publisherName: "JackOfFates",
            displayName: "SocketJack for Visual Studio 2026",
            description: "SocketJack Copilot server picker and MCP configuration for Visual Studio 2026."),
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSingleton<HttpClient>();
    }
}
