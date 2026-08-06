namespace LlmRuntime.VisualStudio2026;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

[VisualStudioContribution]
public sealed class SocketJackVisualStudioExtension : Extension
{
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        LoadedWhen = ActivationConstraint.SolutionState(SolutionState.Exists),
        Metadata = new(
            id: "SocketJack.LlmRuntime.VisualStudio2026",
            version: this.ExtensionAssemblyVersion,
            publisherName: "SocketJack",
            displayName: "SocketJack for Visual Studio 2026",
            description: "Connect Visual Studio Copilot to a local JackLLM Workstation, configure MCP/Ollama BYOM, and sync session files locally.")
        {
            Icon = "Assets\\SocketJackIcon128.png",
            PreviewImage = "Assets\\SocketJackPreview200.png",
            License = "Assets\\LICENSE.txt",
            ReleaseNotes = "Marketplace\\release-notes.txt",
            MoreInfo = "https://github.com/JackOfFates/SocketJack",
            Tags = new[]
            {
                "SocketJack",
                "Copilot",
                "MCP",
                "AI",
                "LLM",
                "Visual Studio 2026",
                "BYOM",
            },
            InstallationTargetVersion = "[17.14,19.0)",
            Preview = true,
        },
    };

    protected override void InitializeServices(IServiceCollection serviceCollection)
    {
        base.InitializeServices(serviceCollection);
        serviceCollection.AddSingleton<HttpClient>();
        SocketJackLocalProxySupervisor.StartBestEffortFromStoredSelection();
    }

    protected override Task OnInitializedAsync(
        VisualStudioExtensibility extensibility,
        CancellationToken cancellationToken)
    {
        SocketJackLocalProxySupervisor.StartBestEffortFromStoredSelection();
        return base.OnInitializedAsync(extensibility, cancellationToken);
    }
}
