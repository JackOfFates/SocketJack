namespace LlmRuntime.VisualStudio2026;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmRuntime.VisualStudio;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal abstract class HeirowLlmLocalViewModel : NotifyPropertyChangedObject
{
    private readonly HeirowLlmWorkstationAuthService authService = new();
    private HeirowLlmWorkstationAuthState authState;
    private string signInUserName = "";
    private string signInPassword = "";
    private string signInError = "";
    private string workstationStatus = "Sign in to the local heirowLLM Workstation.";
    private bool rememberSignIn = true;
    private bool isWorkstationUnavailable = true;
    private bool isSigningIn;

    protected HeirowLlmLocalViewModel()
    {
        this.authState = this.authService.Load();
        this.signInUserName = this.authState.UserName;
        this.IsWorkstationUnavailable = string.IsNullOrWhiteSpace(this.authState.AccessToken);
        this.SignInCommand = new AsyncCommand(this.SignInAsync);
    }

    [DataMember]
    public IAsyncCommand SignInCommand { get; }

    [DataMember]
    public string SignInUserName
    {
        get => this.signInUserName;
        set => this.SetProperty(ref this.signInUserName, value ?? "");
    }

    [DataMember]
    public string SignInPassword
    {
        get => this.signInPassword;
        set => this.SetProperty(ref this.signInPassword, value ?? "");
    }

    [DataMember]
    public string SignInError
    {
        get => this.signInError;
        protected set => this.SetProperty(ref this.signInError, value ?? "");
    }

    [DataMember]
    public string WorkstationStatus
    {
        get => this.workstationStatus;
        protected set => this.SetProperty(ref this.workstationStatus, value ?? "");
    }

    [DataMember]
    public bool RememberSignIn
    {
        get => this.rememberSignIn;
        set => this.SetProperty(ref this.rememberSignIn, value);
    }

    [DataMember]
    public bool IsWorkstationUnavailable
    {
        get => this.isWorkstationUnavailable;
        protected set => this.SetProperty(ref this.isWorkstationUnavailable, value);
    }

    [DataMember]
    public bool IsSigningIn
    {
        get => this.isSigningIn;
        protected set => this.SetProperty(ref this.isSigningIn, value);
    }

    protected string AuthToken => this.authState.AccessToken;

    protected string AuthUserName => this.authState.UserName;

    protected async Task EnsureWorkstationAvailableAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(this.authState.AccessToken))
        {
            this.ShowLocalSignIn("Sign in with an account created in heirowLLM Workstation.");
            throw new InvalidOperationException(this.SignInError);
        }

        try
        {
            this.authState = await this.authService.ValidateAsync(this.authState, cancellationToken).ConfigureAwait(false);
            this.ApplyAuthenticatedState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex is HeirowLlmWorkstationAuthenticationException)
            {
                this.authService.Clear();
                this.authState = new HeirowLlmWorkstationAuthState();
            }
            this.ShowLocalSignIn(ex.Message);
            throw;
        }
    }

    protected static void ApplyAuth(HttpRequestMessage request, string accessToken, string userName)
    {
        HeirowLlmWorkstationAuthService.ApplyAuth(request, accessToken, userName);
    }

    protected virtual Task OnSignedInAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task SignInAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        if (this.IsSigningIn)
        {
            return;
        }

        this.IsSigningIn = true;
        this.SignInError = "";
        this.WorkstationStatus = "Signing in to heirowLLM Workstation...";
        try
        {
            this.authState = await this.authService.LoginAsync(
                this.SignInUserName,
                this.SignInPassword,
                this.RememberSignIn,
                cancellationToken).ConfigureAwait(false);
            this.SignInPassword = "";
            this.ApplyAuthenticatedState();
            await this.OnSignedInAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.ShowLocalSignIn(ex.Message);
        }
        finally
        {
            this.IsSigningIn = false;
        }
    }

    private void ApplyAuthenticatedState()
    {
        this.SignInUserName = this.authState.UserName;
        this.SignInError = "";
        this.IsWorkstationUnavailable = false;
        this.WorkstationStatus =
            "Signed in to heirowLLM Workstation at " + SocketJackLocalWorkstationDiscovery.DefaultEndpoint +
            (string.IsNullOrWhiteSpace(this.authState.UserName) ? "." : " as " + this.authState.UserName + ".");
    }

    private void ShowLocalSignIn(string message)
    {
        this.IsWorkstationUnavailable = true;
        this.WorkstationStatus = "heirowLLM Workstation sign-in is required.";
        this.SignInError = message;
    }
}

internal sealed class HeirowLlmWorkstationAuthService
{
    private const string WorkstationBaseUrl = "http://127.0.0.1:11436";
    private readonly HttpClient httpClient;

    public HeirowLlmWorkstationAuthService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }

    private string AuthFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "heirowLLM",
        "VisualStudio",
        "workstation-auth.json");

    public async Task<HeirowLlmWorkstationAuthState> LoginAsync(
        string userName,
        string password,
        bool remember,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Enter your heirowLLM Workstation user name and password.");
        }

        using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync(
            WorkstationBaseUrl + "/api/web-auth/login",
            new { username = userName.Trim(), password, remember },
            cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("heirowLLM Workstation sign-in failed: " + ExtractError(json, response.StatusCode));
        }

        JsonObject root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        string token = FirstString(root, "accessToken", "token", "bearerToken");
        string normalizedUserName = FirstString(root, "username", "userName", "user");
        string expiresUtc = FirstString(root, "expiresUtc", "expirationUtc");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("heirowLLM Workstation did not return an access token.");
        }

        var state = new HeirowLlmWorkstationAuthState
        {
            AccessToken = token,
            UserName = string.IsNullOrWhiteSpace(normalizedUserName) ? userName.Trim() : normalizedUserName,
            ExpiresUtc = expiresUtc
        };
        if (remember)
        {
            this.Save(state);
        }
        else
        {
            this.Clear();
        }

        return state;
    }

    public async Task<HeirowLlmWorkstationAuthState> ValidateAsync(
        HeirowLlmWorkstationAuthState current,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.AccessToken))
        {
            throw new HeirowLlmWorkstationAuthenticationException("No saved heirowLLM Workstation sign-in was found.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, WorkstationBaseUrl + "/api/web-auth/session");
        ApplyAuth(request, current.AccessToken, current.UserName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = "heirowLLM Workstation session check failed: " + ExtractError(json, response.StatusCode);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new HeirowLlmWorkstationAuthenticationException(message);
            }

            throw new InvalidOperationException(message);
        }

        JsonObject root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject ?? new JsonObject();
        bool authenticated = root["authenticated"]?.GetValue<bool?>() == true ||
            root["active"]?.GetValue<bool?>() == true;
        if (!authenticated)
        {
            throw new HeirowLlmWorkstationAuthenticationException("The saved heirowLLM Workstation sign-in expired or was rejected. Sign in again; remembered Visual Studio sessions now last 30 days.");
        }

        current.UserName = FirstNonEmpty(FirstString(root, "username", "userName", "user"), current.UserName);
        current.ExpiresUtc = FirstNonEmpty(FirstString(root, "expiresUtc", "expirationUtc"), current.ExpiresUtc);
        return current;
    }

    public HeirowLlmWorkstationAuthState Load()
    {
        try
        {
            if (!File.Exists(this.AuthFilePath))
            {
                return new HeirowLlmWorkstationAuthState();
            }

            HeirowLlmStoredAuth? stored = JsonSerializer.Deserialize<HeirowLlmStoredAuth>(File.ReadAllText(this.AuthFilePath));
            if (stored == null || string.IsNullOrWhiteSpace(stored.ProtectedToken))
            {
                return new HeirowLlmWorkstationAuthState();
            }

            byte[] protectedBytes = Convert.FromBase64String(stored.ProtectedToken);
            byte[] tokenBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            try
            {
                return new HeirowLlmWorkstationAuthState
                {
                    AccessToken = Encoding.UTF8.GetString(tokenBytes),
                    UserName = stored.UserName ?? "",
                    ExpiresUtc = stored.ExpiresUtc ?? ""
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tokenBytes);
            }
        }
        catch
        {
            return new HeirowLlmWorkstationAuthState();
        }
    }

    public void Save(HeirowLlmWorkstationAuthState state)
    {
        string? directory = Path.GetDirectoryName(this.AuthFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        byte[] tokenBytes = Encoding.UTF8.GetBytes(state.AccessToken);
        try
        {
            byte[] protectedBytes = ProtectedData.Protect(tokenBytes, null, DataProtectionScope.CurrentUser);
            var stored = new HeirowLlmStoredAuth
            {
                UserName = state.UserName,
                ProtectedToken = Convert.ToBase64String(protectedBytes),
                ExpiresUtc = state.ExpiresUtc
            };
            File.WriteAllText(this.AuthFilePath, JsonSerializer.Serialize(stored), new UTF8Encoding(false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(this.AuthFilePath))
            {
                File.Delete(this.AuthFilePath);
            }
        }
        catch
        {
        }
    }

    public static void ApplyAuth(HttpRequestMessage request, string accessToken, string userName)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Trim());
            request.Headers.TryAddWithoutValidation("X-SocketJack-Auth", accessToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            request.Headers.TryAddWithoutValidation("X-SocketJack-User", userName.Trim());
            request.Headers.TryAddWithoutValidation("X-SocketJack-Username", userName.Trim());
        }
    }

    private static string ExtractError(string json, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            return FirstNonEmpty(
                root?["message"]?.ToString(),
                root?["error"]?["message"]?.ToString(),
                root?["error"]?.ToString(),
                "HTTP " + ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch
        {
            return "HTTP " + ((int)statusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string FirstString(JsonObject root, params string[] names)
    {
        foreach (string name in names)
        {
            string value = root[name]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

internal sealed class HeirowLlmWorkstationAuthState
{
    public string AccessToken { get; set; } = "";
    public string UserName { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
}

internal sealed class HeirowLlmStoredAuth
{
    public string UserName { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public string ExpiresUtc { get; set; } = "";
}

internal sealed class HeirowLlmWorkstationAuthenticationException : InvalidOperationException
{
    public HeirowLlmWorkstationAuthenticationException(string message)
        : base(message)
    {
    }
}
