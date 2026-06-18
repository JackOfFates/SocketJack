namespace LlmRuntime.VisualStudio2026;

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LlmRuntime.VisualStudio;
using Microsoft.Win32;
using Microsoft.VisualStudio.Extensibility.UI;

[DataContract]
internal abstract class SocketJackAuthenticatedViewModel : NotifyPropertyChangedObject
{
    private readonly SocketJackVisualStudioAuthService authService;
    private SocketJackAuthState authState;
    private string signInUserName = "";
    private string signInPassword = "";
    private string signInError = "";
    private string authStatus = "Sign in to SocketJack.com.";
    private bool rememberSignIn = true;
    private bool isSignedIn;
    private bool isSignInOverlayVisible = true;
    private bool isSigningIn;
    private bool isInlineSignInVisible;
    private bool isLocalWorkstationMode;

    protected SocketJackAuthenticatedViewModel(SocketJackVisualStudioAuthService authService)
    {
        this.authService = authService;
        this.authState = authService.Load();
        SocketJackVisualStudioAuthService.AuthStateChanged += this.OnSharedAuthStateChanged;
        this.BrowserSignInCommand = new AsyncCommand(this.BrowserSignInAsync);
        this.SignInCommand = new AsyncCommand(this.SignInAsync);
        this.SignOutCommand = new AsyncCommand(this.SignOutAsync);
        this.ShowInlineSignInCommand = new AsyncCommand(this.ShowInlineSignInAsync);
        this.ApplyAuthState(this.authState, showOverlayWhenMissing: true);
    }

    [DataMember]
    public IAsyncCommand BrowserSignInCommand { get; }

    [DataMember]
    public IAsyncCommand SignInCommand { get; }

    [DataMember]
    public IAsyncCommand SignOutCommand { get; }

    [DataMember]
    public IAsyncCommand ShowInlineSignInCommand { get; }

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
        set => this.SetProperty(ref this.signInError, value ?? "");
    }

    [DataMember]
    public string AuthStatus
    {
        get => this.authStatus;
        set => this.SetProperty(ref this.authStatus, value ?? "");
    }

    [DataMember]
    public bool RememberSignIn
    {
        get => this.rememberSignIn;
        set => this.SetProperty(ref this.rememberSignIn, value);
    }

    [DataMember]
    public bool IsSignedIn
    {
        get => this.isSignedIn;
        set => this.SetProperty(ref this.isSignedIn, value);
    }

    [DataMember]
    public bool IsSignInOverlayVisible
    {
        get => this.isSignInOverlayVisible;
        set => this.SetProperty(ref this.isSignInOverlayVisible, value);
    }

    [DataMember]
    public bool IsSigningIn
    {
        get => this.isSigningIn;
        set => this.SetProperty(ref this.isSigningIn, value);
    }

    [DataMember]
    public bool IsInlineSignInVisible
    {
        get => this.isInlineSignInVisible;
        set => this.SetProperty(ref this.isInlineSignInVisible, value);
    }

    [DataMember]
    public bool IsLocalWorkstationMode
    {
        get => this.isLocalWorkstationMode;
        set => this.SetProperty(ref this.isLocalWorkstationMode, value);
    }

    protected string AuthToken => this.IsLocalWorkstationMode ? "" : this.authState.AccessToken;

    protected string AuthUserName => this.IsLocalWorkstationMode ? "" : this.authState.UserName;

    protected async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (await SocketJackLocalWorkstationDiscovery.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            this.ApplyLocalWorkstationMode();
            return;
        }

        if (string.IsNullOrWhiteSpace(this.authState.AccessToken))
        {
            this.ShowSignIn("Sign in to SocketJack.com before using this extension.");
            throw new SocketJackAuthRequiredException("Sign in to SocketJack.com before using this extension.");
        }

        try
        {
            SocketJackAuthState validated = await this.authService.ValidateAsync(this.authState, cancellationToken);
            this.ApplyAuthState(validated, showOverlayWhenMissing: true);
        }
        catch (SocketJackAuthRequiredException)
        {
            this.authState = new SocketJackAuthState();
            this.ShowSignIn("Your SocketJack.com sign-in expired or was rejected.");
            throw;
        }
    }

    protected bool HandleAuthException(Exception ex)
    {
        if (ex is SocketJackAuthRequiredException)
        {
            this.authState = this.authService.Load();
            this.ShowSignIn("SocketJack.com sign-in is required. " + ex.Message);
            return true;
        }

        if (SocketJackVisualStudioAuthService.IsAuthFailure(ex))
        {
            SocketJackAuthState stored = this.authService.Load();
            if (!string.IsNullOrWhiteSpace(stored.AccessToken))
            {
                this.ApplyAuthState(stored, showOverlayWhenMissing: false);
                this.SignInError = "SocketJack.com rejected this request. Refresh will retry with the saved sign-in. " + ex.Message;
                return true;
            }

            this.ShowSignIn("SocketJack.com sign-in is required. " + ex.Message);
            return true;
        }

        return false;
    }

    private async Task BrowserSignInAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        if (this.IsSigningIn)
        {
            return;
        }

        this.IsSigningIn = true;
        this.SignInError = "";
        this.AuthStatus = "Opening SocketJack.com in your browser...";
        try
        {
            SocketJackAuthState state = await this.authService.LoginWithBrowserAsync(this.RememberSignIn, cancellationToken);
            this.ClearSignInPassword();
            this.ApplyAuthState(state, showOverlayWhenMissing: true);
            await this.OnSignedInAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.SignInError = ex.Message;
            this.IsInlineSignInVisible = true;
            this.ShowSignIn("Browser sign-in did not finish. You can try again or use the local form below.");
        }
        finally
        {
            this.IsSigningIn = false;
        }
    }

    private async Task SignInAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        if (this.IsSigningIn)
        {
            return;
        }

        this.IsSigningIn = true;
        this.SignInError = "";
        this.AuthStatus = "Signing in...";
        try
        {
            SocketJackAuthState state = await this.authService.LoginAsync(this.SignInUserName, this.SignInPassword, this.RememberSignIn, cancellationToken);
            this.ClearSignInPassword();
            this.ApplyAuthState(state, showOverlayWhenMissing: true);
            await this.OnSignedInAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.SignInError = ex.Message;
            this.ShowSignIn(ex.Message);
        }
        finally
        {
            this.IsSigningIn = false;
        }
    }

    private async Task SignOutAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        try
        {
            await this.authService.LogoutAsync(this.authState, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.SignInError = ex.Message;
        }

        this.authService.Clear();
        this.authState = new SocketJackAuthState();
        this.ApplyAuthState(new SocketJackAuthState(), showOverlayWhenMissing: true);
        if (!this.IsLocalWorkstationMode)
        {
            this.ShowSignIn("Signed out.");
        }
    }

    private Task ShowInlineSignInAsync(object? commandParameter, CancellationToken cancellationToken)
    {
        this.IsInlineSignInVisible = true;
        this.SignInError = "";
        return Task.CompletedTask;
    }

    protected virtual Task OnSignedInAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void OnSharedAuthStateChanged(object? sender, EventArgs e)
    {
        SocketJackAuthState state = this.authService.Load();
        this.ApplyAuthState(state, showOverlayWhenMissing: true);
        if (this.IsSignedIn)
        {
            _ = this.RefreshAfterSharedSignInAsync();
        }
    }

    private async Task RefreshAfterSharedSignInAsync()
    {
        try
        {
            await this.OnSignedInAsync(CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    private void ApplyAuthState(SocketJackAuthState state, bool showOverlayWhenMissing)
    {
        if (SocketJackLocalWorkstationDiscovery.IsLikelyAvailable())
        {
            this.ApplyLocalWorkstationMode();
            return;
        }

        this.authState = state ?? new SocketJackAuthState();
        this.SignInUserName = this.authState.UserName;
        this.IsLocalWorkstationMode = false;
        this.IsSignedIn = !string.IsNullOrWhiteSpace(this.authState.AccessToken);
        this.IsSignInOverlayVisible = showOverlayWhenMissing && !this.IsSignedIn;
        this.AuthStatus = this.IsSignedIn
            ? BuildSignedInStatus(this.authState)
            : "Sign in to SocketJack.com.";
        if (this.IsSignedIn)
        {
            this.SignInError = "";
            this.IsInlineSignInVisible = false;
        }
    }

    private void ShowSignIn(string message)
    {
        this.IsLocalWorkstationMode = false;
        this.IsSignedIn = false;
        this.IsSignInOverlayVisible = true;
        this.AuthStatus = "Sign in to SocketJack.com.";
        this.SignInError = message;
    }

    private void ApplyLocalWorkstationMode()
    {
        this.authState = new SocketJackAuthState();
        this.SignInUserName = "";
        this.IsLocalWorkstationMode = true;
        this.IsSignedIn = true;
        this.IsSignInOverlayVisible = false;
        this.IsInlineSignInVisible = false;
        this.SignInError = "";
        this.AuthStatus = "Local JackLLM Workstation detected at " + SocketJackLocalWorkstationDiscovery.DefaultEndpoint + ". SocketJack.com sign-in is not required for local configuration.";
    }

    private void ClearSignInPassword()
    {
        this.SignInPassword = "";
    }

    private static string BuildSignedInStatus(SocketJackAuthState state)
    {
        string userText = string.IsNullOrWhiteSpace(state.UserName) ? "." : " as " + state.UserName + ".";
        if (!string.IsNullOrWhiteSpace(state.LastValidationError))
        {
            return "Saved SocketJack.com sign-in loaded" + userText + " Refresh will retry automatically.";
        }

        if (state.ExpiresUtc > DateTimeOffset.MinValue)
        {
            return "Signed in to SocketJack.com" + userText + " Token refresh is automatic.";
        }

        return "Signed in to SocketJack.com" + userText;
    }
}

internal sealed class SocketJackVisualStudioAuthService
{
    public static event EventHandler? AuthStateChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient httpClient;

    public SocketJackVisualStudioAuthService(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
    }

    public string AuthFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SocketJack",
        "VisualStudio2026",
        "auth.json");

    public SocketJackAuthState Load()
    {
        try
        {
            if (!File.Exists(this.AuthFilePath))
            {
                return new SocketJackAuthState();
            }

            SocketJackStoredAuth? stored = JsonSerializer.Deserialize<SocketJackStoredAuth>(File.ReadAllText(this.AuthFilePath), JsonOptions);
            if (stored == null || string.IsNullOrWhiteSpace(stored.ProtectedAccessToken))
            {
                return new SocketJackAuthState();
            }

            return new SocketJackAuthState
            {
                AccessToken = Unprotect(stored.ProtectedAccessToken),
                UserName = stored.UserName ?? "",
                SavedUtc = stored.SavedUtc,
                Remember = stored.Remember,
                ExpiresUtc = stored.ExpiresUtc,
                LastValidatedUtc = stored.LastValidatedUtc,
                Authenticated = true
            };
        }
        catch
        {
            return new SocketJackAuthState();
        }
    }

    public bool HasStoredToken()
    {
        return !string.IsNullOrWhiteSpace(this.Load().AccessToken);
    }

    public void Save(SocketJackAuthState state)
    {
        if (state == null || string.IsNullOrWhiteSpace(state.AccessToken))
        {
            this.Clear();
            return;
        }

        string? directory = Path.GetDirectoryName(this.AuthFilePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        SocketJackStoredAuth stored = new()
        {
            UserName = state.UserName,
            ProtectedAccessToken = Protect(state.AccessToken),
            SavedUtc = state.SavedUtc <= DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : state.SavedUtc,
            Remember = state.Remember,
            ExpiresUtc = state.ExpiresUtc,
            LastValidatedUtc = state.LastValidatedUtc
        };
        File.WriteAllText(this.AuthFilePath, JsonSerializer.Serialize(stored, JsonOptions), new UTF8Encoding(false));
        AuthStateChanged?.Invoke(this, EventArgs.Empty);
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

        AuthStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<SocketJackAuthState> LoginAsync(string userName, string password, bool remember, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new InvalidOperationException("Enter your SocketJack.com user name.");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Enter your SocketJack.com password.");
        }

        JsonObject payload = new()
        {
            ["username"] = userName.Trim(),
            ["password"] = password,
            ["remember"] = remember
        };

        using HttpResponseMessage response = await this.httpClient.PostAsJsonAsync("https://socketjack.com/api/web-auth/login", payload, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SocketJackAuthRequiredException("SocketJack.com login failed: " + ExtractError(json, response.StatusCode));
        }

        SocketJackAuthState state = ParseAuthState(json, remember, fallback: null);
        if (string.IsNullOrWhiteSpace(state.AccessToken))
        {
            throw new SocketJackAuthRequiredException("SocketJack.com login did not return a bearer token.");
        }

        state.UserName = string.IsNullOrWhiteSpace(state.UserName) ? userName.Trim() : state.UserName;
        state.Remember = remember;
        state.SavedUtc = DateTimeOffset.UtcNow;
        this.Save(state);
        return state;
    }

    public async Task<SocketJackAuthState> LoginWithBrowserAsync(bool remember, CancellationToken cancellationToken)
    {
        string state = CreateBrowserLoginState();
        EnsureBrowserLoginProtocolHandler();
        DeleteBrowserLoginCallback(state);

        string callbackUrl = "socketjack://visualstudio-auth?socketjack_vs_state=" + Uri.EscapeDataString(state);
        string loginUrl = "https://socketjack.com/Login?returnUrl=" + Uri.EscapeDataString(callbackUrl);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));

        ProcessStartInfo startInfo = new(loginUrl)
        {
            UseShellExecute = true
        };
        Process.Start(startInfo);

        BrowserLoginCallback callback = await WaitForBrowserLoginCallbackAsync(state, timeout.Token).ConfigureAwait(false);
        string token = callback.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SocketJackAuthRequiredException(FirstNonEmpty(callback.Error, "SocketJack.com browser sign-in finished without a bearer token."));
        }

        SocketJackAuthState candidate = new()
        {
            AccessToken = token,
            UserName = callback.UserName,
            Remember = remember,
            SavedUtc = DateTimeOffset.UtcNow,
            Authenticated = true
        };
        SocketJackAuthState validated = await this.ValidateAsync(candidate, timeout.Token).ConfigureAwait(false);
        return validated;
    }

    public async Task<SocketJackAuthState> ValidateAsync(SocketJackAuthState current, CancellationToken cancellationToken)
    {
        if (current == null || string.IsNullOrWhiteSpace(current.AccessToken))
        {
            throw new SocketJackAuthRequiredException("No stored SocketJack.com bearer token was found.");
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "https://socketjack.com/api/web-auth/session");
        ApplyAuth(request, current.AccessToken, current.UserName);
        try
        {
            using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (IsDefinitiveAuthStatus(response.StatusCode))
                {
                    throw new SocketJackAuthRequiredException("SocketJack.com session check failed: " + ExtractError(json, response.StatusCode));
                }

                return UseCachedStateAfterRefreshFailure(current, "SocketJack.com session refresh returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ".");
            }

            SocketJackAuthState validated = ParseAuthState(json, current.Remember, current);
            if (!validated.Authenticated)
            {
                throw new SocketJackAuthRequiredException("SocketJack.com session is not authenticated.");
            }

            if (string.IsNullOrWhiteSpace(validated.AccessToken))
            {
                validated.AccessToken = current.AccessToken;
            }

            if (string.IsNullOrWhiteSpace(validated.UserName))
            {
                validated.UserName = current.UserName;
            }

            if (validated.ExpiresUtc <= DateTimeOffset.MinValue && current.ExpiresUtc > DateTimeOffset.MinValue)
            {
                validated.ExpiresUtc = current.ExpiresUtc;
            }

            validated.SavedUtc = current.SavedUtc <= DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : current.SavedUtc;
            validated.LastValidatedUtc = DateTimeOffset.UtcNow;
            validated.Remember = current.Remember;
            validated.LastValidationError = "";
            this.Save(validated);
            return validated;
        }
        catch (SocketJackAuthRequiredException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UseCachedStateAfterRefreshFailure(current, "SocketJack.com session refresh failed: " + ex.Message);
        }
    }

    public async Task LogoutAsync(SocketJackAuthState current, CancellationToken cancellationToken)
    {
        if (current == null || string.IsNullOrWhiteSpace(current.AccessToken))
        {
            return;
        }

        using HttpRequestMessage request = new(HttpMethod.Post, "https://socketjack.com/api/web-auth/logout");
        ApplyAuth(request, current.AccessToken, current.UserName);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        {
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException("SocketJack.com logout failed: " + ExtractError(json, response.StatusCode));
        }
    }

    public static void ApplyAuth(HttpRequestMessage request, string accessToken, string userName = "")
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("X-SocketJack-Auth", accessToken.Trim());
        }

        if (!string.IsNullOrWhiteSpace(userName))
        {
            request.Headers.TryAddWithoutValidation("X-SocketJack-User", userName.Trim());
            request.Headers.TryAddWithoutValidation("X-SocketJack-Username", userName.Trim());
        }
    }

    public static bool IsAuthFailure(Exception ex)
    {
        string text = ex.Message ?? "";
        return text.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("login", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("bearer", StringComparison.OrdinalIgnoreCase);
    }

    private static SocketJackAuthState ParseAuthState(string json, bool remember, SocketJackAuthState? fallback)
    {
        JsonObject? root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
        root ??= new JsonObject();
        string token = FirstString(root, "accessToken", "access_token", "token");
        bool hasAuthenticated = TryFirstBool(root, out bool authenticated, "authenticated", "active");
        if (!hasAuthenticated)
        {
            authenticated = !string.IsNullOrWhiteSpace(token) || fallback?.Authenticated == true || !string.IsNullOrWhiteSpace(fallback?.AccessToken);
        }

        if (authenticated && string.IsNullOrWhiteSpace(token))
        {
            token = fallback?.AccessToken ?? "";
        }

        string userName = FirstNonEmpty(
            FirstString(root, "username", "userName", "loginName", "user"),
            fallback?.UserName ?? "");
        DateTimeOffset expiresUtc = ReadExpiresUtc(root, fallback?.ExpiresUtc ?? DateTimeOffset.MinValue);
        return new SocketJackAuthState
        {
            AccessToken = token,
            UserName = userName,
            Authenticated = authenticated,
            Remember = remember,
            SavedUtc = fallback != null && fallback.SavedUtc > DateTimeOffset.MinValue ? fallback.SavedUtc : DateTimeOffset.UtcNow,
            ExpiresUtc = expiresUtc,
            LastValidatedUtc = DateTimeOffset.UtcNow
        };
    }

    private SocketJackAuthState UseCachedStateAfterRefreshFailure(SocketJackAuthState current, string reason)
    {
        SocketJackAuthState cached = new()
        {
            AccessToken = current.AccessToken,
            UserName = current.UserName,
            Authenticated = !string.IsNullOrWhiteSpace(current.AccessToken),
            Remember = current.Remember,
            SavedUtc = current.SavedUtc,
            ExpiresUtc = current.ExpiresUtc,
            LastValidatedUtc = current.LastValidatedUtc,
            LastValidationError = reason
        };
        return cached;
    }

    private static bool IsDefinitiveAuthStatus(System.Net.HttpStatusCode statusCode)
    {
        return statusCode == System.Net.HttpStatusCode.Unauthorized ||
            statusCode == System.Net.HttpStatusCode.Forbidden;
    }

    private static string Protect(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value ?? "");
        byte[] protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Unprotect(string value)
    {
        byte[] protectedBytes = Convert.FromBase64String(value ?? "");
        byte[] bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string ExtractError(string json, System.Net.HttpStatusCode statusCode)
    {
        try
        {
            JsonObject? root = JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) as JsonObject;
            string message = FirstString(root ?? new JsonObject(), "error", "message", "detail", "error_description");
            if (!string.IsNullOrWhiteSpace(message))
            {
                return message;
            }
        }
        catch (JsonException)
        {
        }

        return "HTTP " + ((int)statusCode).ToString(CultureInfo.InvariantCulture);
    }

    private static void EnsureBrowserLoginProtocolHandler()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("SocketJack browser app sign-in requires Windows URL protocol support.");
        }

        string handlerPath = GetBridgeExecutablePath();
        if (string.IsNullOrWhiteSpace(handlerPath) || !File.Exists(handlerPath))
        {
            throw new InvalidOperationException("The SocketJack browser sign-in helper was not found in the extension package.");
        }

        string command = "\"" + handlerPath + "\" --vs-auth-callback \"%1\"";
        using RegistryKey protocolKey = Registry.CurrentUser.CreateSubKey(@"Software\Classes\socketjack", writable: true)
            ?? throw new InvalidOperationException("Could not register the socketjack:// browser sign-in protocol.");
        protocolKey.SetValue("", "URL:SocketJack Visual Studio Sign-in");
        protocolKey.SetValue("URL Protocol", "");

        using RegistryKey iconKey = protocolKey.CreateSubKey("DefaultIcon", writable: true)
            ?? throw new InvalidOperationException("Could not register the socketjack:// browser sign-in icon.");
        iconKey.SetValue("", handlerPath);

        using RegistryKey commandKey = protocolKey.CreateSubKey(@"shell\open\command", writable: true)
            ?? throw new InvalidOperationException("Could not register the socketjack:// browser sign-in command.");
        commandKey.SetValue("", command);
    }

    private static string GetBridgeExecutablePath()
    {
        string assemblyPath = Assembly.GetExecutingAssembly().Location;
        string? extensionDirectory = Path.GetDirectoryName(assemblyPath);
        if (string.IsNullOrWhiteSpace(extensionDirectory))
        {
            return "";
        }

        return Path.Combine(extensionDirectory, "Bridge", "SocketJack.CopilotMcpBridge.exe");
    }

    private static async Task<BrowserLoginCallback> WaitForBrowserLoginCallbackAsync(string state, CancellationToken cancellationToken)
    {
        string path = BrowserLoginCallbackPath(state);
        while (!cancellationToken.IsCancellationRequested)
        {
            if (File.Exists(path))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    DeleteBrowserLoginCallback(state);
                    BrowserLoginCallback callback = JsonSerializer.Deserialize<BrowserLoginCallback>(json, JsonOptions) ?? new BrowserLoginCallback();
                    if (string.Equals(callback.State, state, StringComparison.Ordinal))
                    {
                        return callback;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DeleteBrowserLoginCallback(state);
                    throw new SocketJackAuthRequiredException("SocketJack.com browser sign-in callback could not be read: " + ex.Message);
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private static string BrowserLoginCallbackPath(string state)
    {
        return Path.Combine(BrowserLoginCallbackDirectory(), SanitizeBrowserLoginState(state) + ".json");
    }

    private static string BrowserLoginCallbackDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SocketJack",
            "VisualStudio2026",
            "BrowserLogin");
    }

    private static void DeleteBrowserLoginCallback(string state)
    {
        try
        {
            string path = BrowserLoginCallbackPath(state);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string SanitizeBrowserLoginState(string state)
    {
        var builder = new StringBuilder();
        foreach (char ch in state ?? "")
        {
            if ((ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F') || (ch >= '0' && ch <= '9'))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.Length == 0 ? "missing" : builder.ToString();
    }

    private static string CreateBrowserLoginState()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetQueryValue(string query, string name)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "";
        }

        string trimmed = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
        foreach (string pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            string key = equals < 0 ? pair : pair.Substring(0, equals);
            if (!string.Equals(Uri.UnescapeDataString(key.Replace("+", " ", StringComparison.Ordinal)), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = equals < 0 ? "" : pair.Substring(equals + 1);
            return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
        }

        return "";
    }

    private static string FirstString(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node == null)
            {
                continue;
            }

            string text = node.ToString();
            if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return text.Trim();
            }
        }

        return "";
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return "";
    }

    private static bool FirstBool(JsonObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node is JsonValue value && value.TryGetValue(out bool result))
            {
                return result;
            }

            if (node != null && bool.TryParse(node.ToString(), out result))
            {
                return result;
            }
        }

        return false;
    }

    private static bool TryFirstBool(JsonObject obj, out bool result, params string[] names)
    {
        foreach (string name in names)
        {
            JsonNode? node = obj[name];
            if (node is JsonValue value && value.TryGetValue(out result))
            {
                return true;
            }

            if (node != null && bool.TryParse(node.ToString(), out result))
            {
                return true;
            }
        }

        result = false;
        return false;
    }

    private static DateTimeOffset ReadExpiresUtc(JsonObject obj, DateTimeOffset fallback)
    {
        string expiresUtc = FirstString(obj, "expiresUtc", "expiresUTC", "expires_at", "expiresAt", "tokenExpiresUtc");
        if (!string.IsNullOrWhiteSpace(expiresUtc) &&
            DateTimeOffset.TryParse(expiresUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        string expiresIn = FirstString(obj, "expires_in", "expiresIn");
        if (!string.IsNullOrWhiteSpace(expiresIn) &&
            double.TryParse(expiresIn, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) &&
            seconds > 0)
        {
            return DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        return fallback;
    }
}

internal sealed class SocketJackAuthRequiredException : InvalidOperationException
{
    public SocketJackAuthRequiredException(string message)
        : base(message)
    {
    }
}

internal sealed class SocketJackAuthState
{
    public string AccessToken { get; set; } = "";
    public string UserName { get; set; } = "";
    public bool Authenticated { get; set; }
    public bool Remember { get; set; }
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastValidatedUtc { get; set; } = DateTimeOffset.MinValue;
    public string LastValidationError { get; set; } = "";
}

internal sealed class BrowserLoginCallback
{
    public string State { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Error { get; set; } = "";
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.MinValue;
}

internal sealed class SocketJackStoredAuth
{
    public string UserName { get; set; } = "";
    public string ProtectedAccessToken { get; set; } = "";
    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset ExpiresUtc { get; set; } = DateTimeOffset.MinValue;
    public DateTimeOffset LastValidatedUtc { get; set; } = DateTimeOffset.MinValue;
    public bool Remember { get; set; }
}
