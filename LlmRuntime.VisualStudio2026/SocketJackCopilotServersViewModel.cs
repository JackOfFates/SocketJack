namespace LlmRuntime.VisualStudio2026;

using System.Runtime.Serialization;
using LlmRuntime.VisualStudio;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal sealed class SocketJackCopilotServersViewModel : SocketJackAuthenticatedViewModel
{
    private readonly SocketJackCopilotConfigurator configurator;
    private readonly List<SocketJackServerDisplayItem> allServers = new();
    private string searchText = "";
    private string selectedServerId = "";
    private string selectedModelId = "";
    private List<SocketJackServerDisplayItem> servers = new();
    private List<SocketJackModelDisplayItem> models = new();
    private string serverDetails = "Refresh to load SocketJack MasterList servers.";
    private string modelDetails = "";
    private string status = "Ready.";
    private bool isBusy;

    public SocketJackCopilotServersViewModel(VisualStudioExtensibility extensibility)
        : base(new SocketJackVisualStudioAuthService())
    {
        this.configurator = new SocketJackCopilotConfigurator(extensibility, new HttpClient());
        this.RefreshCommand = new AsyncCommand(this.RefreshAsync);
        this.LoadModelsCommand = new AsyncCommand(this.LoadModelsAsync);
        this.TestConnectionCommand = new AsyncCommand(this.TestConnectionAsync);
        this.ConfigureCommand = new AsyncCommand(this.ConfigureAsync);
    }

    [DataMember]
    public IAsyncCommand RefreshCommand { get; }

    [DataMember]
    public IAsyncCommand LoadModelsCommand { get; }

    [DataMember]
    public IAsyncCommand TestConnectionCommand { get; }

    [DataMember]
    public IAsyncCommand ConfigureCommand { get; }

    [DataMember]
    public string SearchText
    {
        get => this.searchText;
        set
        {
            value ??= "";
            if (string.Equals(this.searchText, value, StringComparison.Ordinal))
            {
                return;
            }

            this.SetProperty(ref this.searchText, value);
            this.ApplyServerFilter();
        }
    }

    [DataMember]
    public List<SocketJackServerDisplayItem> Servers
    {
        get => this.servers;
        set => this.SetProperty(ref this.servers, value);
    }

    [DataMember]
    public List<SocketJackModelDisplayItem> Models
    {
        get => this.models;
        set => this.SetProperty(ref this.models, value);
    }

    [DataMember]
    public string SelectedServerId
    {
        get => this.selectedServerId;
        set
        {
            value ??= "";
            if (string.Equals(this.selectedServerId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this.SetProperty(ref this.selectedServerId, value);
            this.Models = new List<SocketJackModelDisplayItem>();
            this.SelectedModelId = "";
            this.UpdateServerDetails();
            this.UpdateModelDetails();
        }
    }

    [DataMember]
    public string SelectedModelId
    {
        get => this.selectedModelId;
        set
        {
            value ??= "";
            if (string.Equals(this.selectedModelId, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            this.SetProperty(ref this.selectedModelId, value);
            this.UpdateModelDetails();
        }
    }

    [DataMember]
    public string ServerDetails
    {
        get => this.serverDetails;
        set => this.SetProperty(ref this.serverDetails, value);
    }

    [DataMember]
    public string ModelDetails
    {
        get => this.modelDetails;
        set => this.SetProperty(ref this.modelDetails, value);
    }

    [DataMember]
    public string Status
    {
        get => this.status;
        set => this.SetProperty(ref this.status, value);
    }

    [DataMember]
    public bool IsBusy
    {
        get => this.isBusy;
        set => this.SetProperty(ref this.isBusy, value);
    }

    private async Task RefreshAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Loading SocketJack MasterList servers...", async token =>
        {
            await this.EnsureSignedInAsync(token);
            IReadOnlyList<SocketJackServerCandidate> candidates = await this.configurator.GetServersAsync(token);
            this.allServers.Clear();
            this.allServers.AddRange(candidates.Select(SocketJackServerDisplayItem.FromCandidate));
            this.ApplyServerFilter();

            SocketJackServerDisplayItem? selected = this.Servers.FirstOrDefault(server => server.CanUseForCopilot) ?? this.Servers.FirstOrDefault();
            if (selected != null)
            {
                this.SelectedServerId = selected.Id;
            }

            this.Status = "Loaded " + this.Servers.Count + " SocketJack servers. Select a server, then load models.";
            if (selected?.CanUseForCopilot == true)
            {
                await this.LoadModelsCoreAsync(token);
            }
        }, cancellationToken);
    }

    private async Task LoadModelsAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Loading models for selected SocketJack server...", this.LoadModelsCoreAsync, cancellationToken);
    }

    private async Task TestConnectionAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Testing selected SocketJack model route...", async token =>
        {
            await this.EnsureSignedInAsync(token);
            SocketJackServerCandidate server = this.GetSelectedServerOrThrow();
            SocketJackModelCandidate? model = this.TryGetSelectedModel();
            SocketJackEndpointAccessResult result = model == null
                ? await this.configurator.TestModelRouteAsync(server, token)
                : await this.configurator.TestChatRouteAsync(server, model, token);
            if (result.CanUseDirectEndpoint)
            {
                this.Status = result.Message + " Direct SocketJack address can be used.";
                return;
            }

            SocketJackEndpointAccessResult fallback = await this.configurator.TestSocketJackFallbackRouteAsync(server, token);
            this.Status = result.Message + " " + (fallback.CanUseDirectEndpoint ? fallback.Message + " Local WebSocket proxy fallback can be used." : fallback.Message);
        }, cancellationToken);
    }

    private async Task ConfigureAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        await this.RunBusyAsync("Configuring Visual Studio Copilot for SocketJack...", async token =>
        {
            await this.EnsureSignedInAsync(token);
            SocketJackServerCandidate server = this.GetSelectedServerOrThrow();
            SocketJackModelCandidate model = this.GetSelectedModelOrThrow();
            if (!server.CanUseForCopilot)
            {
                throw new InvalidOperationException(server.DisabledReason);
            }

            if (!model.IsSelectable)
            {
                throw new InvalidOperationException(model.EligibilityReason);
            }

            SocketJackConfigureResult result = await this.configurator.ConfigureAsync(server, model, this.AuthToken, this.AuthUserName, token);
            this.Status = result.ToUserMessage();
        }, cancellationToken);
    }

    private async Task LoadModelsCoreAsync(CancellationToken cancellationToken)
    {
        await this.EnsureSignedInAsync(cancellationToken);
        SocketJackServerCandidate server = this.GetSelectedServerOrThrow();
        SocketJackModelDiscoveryResult result = await this.configurator.GetModelsAsync(server, cancellationToken);
        this.Models = result.Models.Select(SocketJackModelDisplayItem.FromCandidate).ToList();
        SocketJackModelDisplayItem? selected = this.Models.FirstOrDefault(model => model.IsSelectable) ?? this.Models.FirstOrDefault();
        this.SelectedModelId = selected?.Id ?? "";
        this.UpdateModelDetails();

        string warnings = result.Warnings.Count == 0 ? "" : " Warnings: " + string.Join(" ", result.Warnings);
        this.Status = "Loaded " + this.Models.Count + " models for " + server.DisplayName + "." + warnings;
    }

    private void ApplyServerFilter()
    {
        string filter = this.searchText.Trim();
        IEnumerable<SocketJackServerDisplayItem> filtered = this.allServers;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filtered = filtered.Where(server =>
                server.DisplayText.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                server.Endpoint.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        string previousSelection = this.selectedServerId;
        this.Servers = filtered.ToList();
        if (!this.Servers.Any(server => string.Equals(server.Id, previousSelection, StringComparison.OrdinalIgnoreCase)))
        {
            this.SelectedServerId = this.Servers.FirstOrDefault()?.Id ?? "";
        }

        this.UpdateServerDetails();
    }

    private SocketJackServerCandidate GetSelectedServerOrThrow()
    {
        SocketJackServerDisplayItem? item = this.allServers.FirstOrDefault(server => string.Equals(server.Id, this.SelectedServerId, StringComparison.OrdinalIgnoreCase));
        return item?.Candidate ?? throw new InvalidOperationException("Select a SocketJack server first.");
    }

    private SocketJackModelCandidate GetSelectedModelOrThrow()
    {
        SocketJackModelCandidate? model = this.TryGetSelectedModel();
        return model ?? throw new InvalidOperationException("Select a SocketJack model first.");
    }

    private SocketJackModelCandidate? TryGetSelectedModel()
    {
        SocketJackModelDisplayItem? item = this.Models.FirstOrDefault(model => string.Equals(model.Id, this.SelectedModelId, StringComparison.OrdinalIgnoreCase));
        return item?.Candidate;
    }

    private void UpdateServerDetails()
    {
        SocketJackServerDisplayItem? item = this.allServers.FirstOrDefault(server => string.Equals(server.Id, this.SelectedServerId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            this.ServerDetails = "No SocketJack server selected.";
            return;
        }

        SocketJackServerCandidate server = item.Candidate;
        this.ServerDetails =
            "Endpoint: " + server.EffectiveEndpoint + Environment.NewLine +
            "Tools: " + (string.IsNullOrWhiteSpace(server.ToolsAllowed) ? (server.ToolsAdvertised ? "advertised" : "not advertised") : server.ToolsAllowed) + Environment.NewLine +
            "Status: " + (server.CanUseForCopilot ? "eligible" : server.DisabledReason) + Environment.NewLine +
            "Hardware: " + (string.IsNullOrWhiteSpace(server.Hardware) ? "not reported" : server.Hardware);
    }

    private void UpdateModelDetails()
    {
        SocketJackModelDisplayItem? item = this.Models.FirstOrDefault(model => string.Equals(model.Id, this.SelectedModelId, StringComparison.OrdinalIgnoreCase));
        if (item == null)
        {
            this.ModelDetails = "No model loaded for the selected server.";
            return;
        }

        SocketJackModelCandidate model = item.Candidate;
        this.ModelDetails =
            "Id: " + model.Id + Environment.NewLine +
            "Status: " + model.EligibilityReason + Environment.NewLine +
            "Tools: " + (model.SupportsTools ? "yes" : "no") + ", Vision: " + (model.SupportsVision ? "yes" : "no") + Environment.NewLine +
            "Load: " + (model.IsLoaded ? "loaded" : model.RuntimeLoadable || model.WebChatDynamicLoadable ? "loadable" : "not loadable");
    }

    private async Task RunBusyAsync(string message, Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        if (this.IsBusy)
        {
            return;
        }

        this.IsBusy = true;
        this.Status = message;
        try
        {
            await action(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!this.HandleAuthException(ex))
            {
                this.Status = "SocketJack operation failed: " + ex.Message;
            }
        }
        finally
        {
            this.IsBusy = false;
        }
    }
}

[DataContract]
internal sealed class SocketJackServerDisplayItem
{
    [IgnoreDataMember]
    public SocketJackServerCandidate Candidate { get; init; } = new();

    [DataMember]
    public string Id { get; init; } = "";

    [DataMember]
    public string DisplayText { get; init; } = "";

    [DataMember]
    public string Endpoint { get; init; } = "";

    [DataMember]
    public bool CanUseForCopilot { get; init; }

    public static SocketJackServerDisplayItem FromCandidate(SocketJackServerCandidate candidate)
    {
        string displayName = string.IsNullOrWhiteSpace(candidate.DisplayName) ? candidate.Id : candidate.DisplayName;
        string status = candidate.CanUseForCopilot ? "eligible" : candidate.DisabledReason;
        return new SocketJackServerDisplayItem
        {
            Candidate = candidate,
            Id = candidate.Id,
            Endpoint = candidate.EffectiveEndpoint,
            CanUseForCopilot = candidate.CanUseForCopilot,
            DisplayText = displayName + " - " + status,
        };
    }
}

[DataContract]
internal sealed class SocketJackModelDisplayItem
{
    [IgnoreDataMember]
    public SocketJackModelCandidate Candidate { get; init; } = new();

    [DataMember]
    public string Id { get; init; } = "";

    [DataMember]
    public string DisplayText { get; init; } = "";

    [DataMember]
    public bool IsSelectable { get; init; }

    public static SocketJackModelDisplayItem FromCandidate(SocketJackModelCandidate candidate)
    {
        string displayName = candidate.Id;
        return new SocketJackModelDisplayItem
        {
            Candidate = candidate,
            Id = candidate.Id,
            IsSelectable = candidate.IsSelectable,
            DisplayText = candidate.IsSelectable ? displayName : displayName + " - " + candidate.EligibilityReason,
        };
    }
}
