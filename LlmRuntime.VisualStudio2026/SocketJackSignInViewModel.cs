namespace LlmRuntime.VisualStudio2026;

using System.Runtime.Serialization;

[DataContract]
internal sealed class SocketJackSignInViewModel : SocketJackAuthenticatedViewModel
{
    private string status = "Sign in to SocketJack.com to enable the SocketJack Visual Studio tools.";

    public SocketJackSignInViewModel()
        : base(new SocketJackVisualStudioAuthService())
    {
    }

    [DataMember]
    public string Status
    {
        get => this.status;
        set => this.SetProperty(ref this.status, value ?? "");
    }

    protected override Task OnSignedInAsync(CancellationToken cancellationToken)
    {
        this.Status = "Signed in. The SocketJack menu commands are enabled.";
        return Task.CompletedTask;
    }
}
