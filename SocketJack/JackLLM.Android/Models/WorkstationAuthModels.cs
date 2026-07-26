namespace JackLLM.Mobile.Models;

public sealed class WorkstationAuthStatus
{
    public bool Authenticated { get; set; }
    public bool CanRegisterOpen { get; set; }
    public string Username { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public bool IsOwner { get; set; }
}

public sealed class WorkstationAuthResult
{
    public bool Authenticated { get; set; }
    public bool Pending { get; set; }
    public string Username { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string Message { get; set; } = "";
    public string OwnerKey { get; set; } = "";
    public bool IsAdministrator { get; set; }
    public bool IsOwner { get; set; }
}
