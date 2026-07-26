using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class WorkstationAuthPage : ContentPage
{
    private readonly SecureCredentialStore _credentials;
    private readonly ServerStore _store;
    private readonly Func<ServerInfo, Task> _authenticated;
    private readonly Entry _endpoint;
    private readonly Entry _username;
    private readonly Entry _password;
    private readonly Button _login;
    private readonly Button _register;
    private readonly Label _status;
    private bool _busy;

    public WorkstationAuthPage(
        SecureCredentialStore credentials,
        ServerStore store,
        Func<ServerInfo, Task> authenticated,
        string endpoint = "")
    {
        _credentials = credentials;
        _store = store;
        _authenticated = authenticated;
        Title = "Workstation account";
        BackgroundColor = Color.FromArgb("#0B1020");

        _endpoint = Field(
            string.IsNullOrWhiteSpace(endpoint) ? "http://10.0.2.2:11436" : endpoint,
            "Workstation address",
            Keyboard.Url);
        _endpoint.AutomationId = "WorkstationEndpoint";
        _username = Field("", "Username", Keyboard.Text);
        _username.AutomationId = "WorkstationUsername";
        _password = Field("", "Password", Keyboard.Text);
        _password.IsPassword = true;
        _password.AutomationId = "WorkstationPassword";
        _password.Completed += async (_, _) => await LoginAsync();

        _login = ActionButton("Login", "#2563EB", "WorkstationLogin");
        _register = ActionButton("Register", "#334155", "WorkstationRegister");
        _login.Clicked += async (_, _) => await LoginAsync();
        _register.Clicked += async (_, _) => await RegisterAsync();
        _status = new Label
        {
            Text = "Use an account stored by this JackLLM Workstation. Your password is sent only to that Workstation.",
            TextColor = Color.FromArgb("#94A3B8"),
            FontSize = 13,
            LineBreakMode = LineBreakMode.WordWrap,
            AutomationId = "WorkstationAuthStatus"
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(20, 28),
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = "Connect to your Workstation",
                        FontSize = 28,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = "Login or request a new account before JackLLM Mobile can use chat, models, sessions, or PC Access.",
                        FontSize = 14,
                        TextColor = Color.FromArgb("#CBD5E1")
                    },
                    Card("Workstation", _endpoint),
                    Card("Account", _username, _password),
                    new HorizontalStackLayout { Spacing = 10, Children = { _login, _register } },
                    _status,
                    new Label
                    {
                        Text = "Registration may require approval from the Workstation administrator. One-time pairing remains available from the previous screen.",
                        FontSize = 11,
                        TextColor = Color.FromArgb("#64748B")
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrWhiteSpace(_endpoint.Text)) return;
        try
        {
            using var client = new JackLlmClient(_credentials);
            WorkstationAuthStatus status = await client.GetWorkstationAuthStatusAsync(_endpoint.Text);
            _register.Text = status.CanRegisterOpen ? "Register" : "Request account";
        }
        catch
        {
            // The user can still correct the address and submit explicitly.
        }
    }

    private async Task LoginAsync()
    {
        if (!TryReadFields(out string endpoint, out string username, out string password)) return;
        await RunAsync("Signing in through the Workstation...", async client =>
        {
            WorkstationAuthResult result = await client.LoginToWorkstationAsync(endpoint, username, password);
            await FinishAuthenticationAsync(endpoint, result);
        });
    }

    private async Task RegisterAsync()
    {
        if (!TryReadFields(out string endpoint, out string username, out string password)) return;
        await RunAsync("Sending the account request to the Workstation...", async client =>
        {
            WorkstationAuthResult result = await client.RegisterWithWorkstationAsync(endpoint, username, password);
            if (result.Pending)
            {
                _status.TextColor = Color.FromArgb("#FDE68A");
                _status.Text = string.IsNullOrWhiteSpace(result.Message)
                    ? "Account request sent. Ask the Workstation administrator to approve it, then return here and log in."
                    : result.Message;
                _login.IsEnabled = true;
                return;
            }
            await FinishAuthenticationAsync(endpoint, result);
        });
    }

    private async Task FinishAuthenticationAsync(string endpoint, WorkstationAuthResult result)
    {
        if (!result.Authenticated || string.IsNullOrWhiteSpace(result.AccessToken))
            throw new InvalidOperationException("The Workstation did not return an authenticated account token.");

        var server = new ServerInfo
        {
            Name = new Uri(endpoint).Host,
            OwnerUserName = result.Username,
            Endpoint = endpoint,
            IsSaved = true
        };
        await _credentials.SetServerTokenAsync(server.LaunchKey, result.AccessToken);
        _store.Save(server);

        using var verification = new JackLlmClient(_credentials);
        await verification.ConnectAsync(server);
        _status.TextColor = Color.FromArgb("#6EE7B7");
        _status.Text = "Signed in as " + result.Username + ". Opening JackLLM Mobile...";
        await _authenticated(server);
    }

    private async Task RunAsync(string progress, Func<JackLlmClient, Task> action)
    {
        if (_busy) return;
        _busy = true;
        SetButtons(false);
        _status.TextColor = Color.FromArgb("#94A3B8");
        _status.Text = progress;
        try
        {
            using var client = new JackLlmClient(_credentials);
            await action(client);
        }
        catch (Exception ex)
        {
            _status.TextColor = Color.FromArgb("#FCA5A5");
            _status.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private bool TryReadFields(out string endpoint, out string username, out string password)
    {
        endpoint = "";
        username = (_username.Text ?? "").Trim();
        password = _password.Text ?? "";
        try { endpoint = JackLlmClient.NormalizeBaseUrl(_endpoint.Text ?? ""); }
        catch (Exception ex) { ShowError(ex.Message); return false; }
        if (string.IsNullOrWhiteSpace(username))
        {
            ShowError("Enter your Workstation username.");
            return false;
        }
        if (string.IsNullOrWhiteSpace(password))
        {
            ShowError("Enter your Workstation password.");
            return false;
        }
        _endpoint.Text = endpoint;
        return true;
    }

    private void ShowError(string message)
    {
        _status.TextColor = Color.FromArgb("#FCA5A5");
        _status.Text = message;
    }

    private void SetButtons(bool enabled)
    {
        _login.IsEnabled = enabled;
        _register.IsEnabled = enabled;
    }

    private static Entry Field(string text, string placeholder, Keyboard keyboard) => new()
    {
        Text = text,
        Placeholder = placeholder,
        Keyboard = keyboard,
        TextColor = Colors.White,
        PlaceholderColor = Color.FromArgb("#64748B"),
        BackgroundColor = Colors.Transparent
    };

    private static Button ActionButton(string text, string color, string automationId) => new()
    {
        Text = text,
        BackgroundColor = Color.FromArgb(color),
        TextColor = Colors.White,
        CornerRadius = 12,
        AutomationId = automationId
    };

    private static Border Card(string title, params View[] fields)
    {
        var content = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                new Label
                {
                    Text = title,
                    FontSize = 12,
                    FontAttributes = FontAttributes.Bold,
                    TextColor = Color.FromArgb("#94A3B8")
                }
            }
        };
        foreach (View field in fields) content.Children.Add(field);
        return new Border
        {
            Padding = 14,
            BackgroundColor = Color.FromArgb("#151C2F"),
            Stroke = Color.FromArgb("#26334D"),
            StrokeShape = new RoundRectangle { CornerRadius = 16 },
            Content = content
        };
    }
}
