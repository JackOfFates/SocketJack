using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Layouts;

namespace JackLLM.Mobile.Pages;

public sealed class DreamManagementPage : ContentPage
{
    private readonly JackLlmClient _client;
    private readonly Picker _owner = new() { Title = "Owner" };
    private readonly Switch _enabled = new();
    private readonly Picker _preset = new() { Title = "Preset", ItemsSource = new[] { "conservative", "balanced", "aggressive", "custom" } };
    private readonly Label _status = Muted("Loading Dream status...");
    private readonly Label _resources = Muted("Resources have not been sampled.");
    private readonly Label _notice = Muted("");
    private readonly VerticalStackLayout _permissions = new() { Spacing = 6 };
    private readonly VerticalStackLayout _journal = new() { Spacing = 10 };
    private readonly Dictionary<string, Entry> _fields = new();
    private MobileDreamSettingsEnvelope _settings = new();
    private CancellationTokenSource? _polling;
    private bool _loading;

    public DreamManagementPage(ServerInfo server, JackLlmClient client)
    {
        _client = client;
        Title = "Dreaming";
        BackgroundColor = Color.FromArgb("#0B1020");
        _owner.SelectedIndexChanged += async (_, _) => { if (!_loading) await LoadAllAsync(true); };

        var save = ActionButton("Save settings", "#2563EB", async () => await SaveAsync());
        var reset = ActionButton("Reset to global", "#334155", async () => await ResetAsync());
        var now = ActionButton("Dream now", "#0F766E", async () => await ControlAsync("start"));
        var pause = ActionButton("Pause", "#334155", async () => await ControlAsync("pause"));
        var resume = ActionButton("Resume", "#334155", async () => await ControlAsync("resume"));
        var cancel = ActionButton("Cancel run", "#991B1B", async () => await ControlAsync("cancel"));
        var clear = ActionButton("Clear resolved", "#7C2D12", async () => await ClearResolvedAsync());

        var settingsGrid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 10, RowSpacing = 8 };
        AddField(settingsGrid, "recurrenceMinutes", "Every minutes", 0, 0);
        AddField(settingsGrid, "maxRunMinutes", "Max run minutes", 0, 1);
        AddField(settingsGrid, "tokenBudget", "Output tokens", 1, 0);
        AddField(settingsGrid, "sourceTokenBudget", "Source tokens", 1, 1);
        AddField(settingsGrid, "sessionsPerPass", "Sessions/pass", 2, 0);
        AddField(settingsGrid, "startCpuPercent", "Start CPU %", 2, 1);
        AddField(settingsGrid, "pauseCpuPercent", "Pause CPU %", 3, 0);
        AddField(settingsGrid, "startRamPercent", "Start RAM %", 3, 1);
        AddField(settingsGrid, "pauseRamPercent", "Pause RAM %", 4, 0);
        AddField(settingsGrid, "startGpuPercent", "Start GPU %", 4, 1);
        AddField(settingsGrid, "pauseGpuPercent", "Pause GPU %", 5, 0);
        AddField(settingsGrid, "startVramPercent", "Start VRAM %", 5, 1);
        AddField(settingsGrid, "pauseVramPercent", "Pause VRAM %", 6, 0);
        AddField(settingsGrid, "startDiskPercent", "Start disk %", 6, 1);
        AddField(settingsGrid, "pauseDiskPercent", "Pause disk %", 7, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 32), Spacing = 14,
                Children =
                {
                    new Label { Text = server.DisplayName + " Dream Mode", FontSize = 25, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                    _owner,
                    new HorizontalStackLayout { Spacing = 10, Children = { new Label { Text = "Automatic Dreaming", TextColor = Colors.White, VerticalTextAlignment = TextAlignment.Center }, _enabled } },
                    _preset,
                    settingsGrid,
                    new FlexLayout { Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap, Children = { save, reset, now, pause, resume, cancel } },
                    Card(new VerticalStackLayout { Spacing = 5, Children = { Section("Live status"), _status, _resources, _notice } }),
                    Card(new VerticalStackLayout { Spacing = 8, Children = { Section("Dream tool permissions"), Muted("Each Dream grant also requires its base Workstation permission. Terminal requires permanent trust."), _permissions } }),
                    new HorizontalStackLayout { Children = { Section("Dream journal"), clear } },
                    _journal
                }
            }
        };
        ToolbarItems.Add(new ToolbarItem("Refresh", null, async () => await LoadAllAsync(true)));
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _polling?.Cancel();
        _polling = new CancellationTokenSource();
        _ = PollAsync(_polling.Token);
    }

    protected override void OnDisappearing()
    {
        _polling?.Cancel();
        base.OnDisappearing();
    }

    private async Task PollAsync(CancellationToken token)
    {
        await LoadOwnersAsync(token);
        await LoadAllAsync(true, token);
        while (!token.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), token); await LoadAllAsync(false, token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _notice.Text = ex.Message; }
        }
    }

    private string OwnerKey => (_owner.SelectedItem as MobileDreamOwner)?.OwnerKey ?? "";

    private async Task LoadOwnersAsync(CancellationToken token)
    {
        try
        {
            IReadOnlyList<MobileDreamOwner> owners = await _client.GetDreamOwnersAsync(token);
            _loading = true;
            _owner.ItemsSource = owners.ToList();
            _owner.IsVisible = owners.Count > 0;
            if (owners.Count > 0) _owner.SelectedIndex = 0;
        }
        catch { _owner.IsVisible = false; }
        finally { _loading = false; }
    }

    private async Task LoadAllAsync(bool includeSettings, CancellationToken token = default)
    {
        if (_loading) return;
        try
        {
            _loading = true;
            string owner = OwnerKey;
            if (includeSettings)
            {
                _settings = await _client.GetDreamSettingsAsync(owner, token);
                RenderSettings(_settings.Settings);
            }
            MobileDreamStatus status = await _client.GetDreamStatusAsync(owner, token);
            IReadOnlyList<MobileDreamJournalEntry> journal = await _client.GetDreamJournalAsync(owner, cancellationToken: token);
            (MobileDreamPermissionSnapshot permissions, bool canManage) = await _client.GetDreamPermissionsAsync(owner, token);
            _status.Text = string.Join(" · ", new[] { status.Status, status.Phase, status.LimitingResource, string.IsNullOrWhiteSpace(status.NextRunUtc) ? "" : "next " + status.NextRunUtc }.Where(x => !string.IsNullOrWhiteSpace(x)));
            _resources.Text = $"CPU {status.Resources.CpuPercent:0}% · RAM {status.Resources.RamPercent:0}% · GPU {status.Resources.GpuPercent:0}% · VRAM {status.Resources.VramPercent:0}% · disk {status.Resources.DiskPercent:0}% · {status.ProcessedSessions} sessions / {status.ProcessedMessages} messages";
            _notice.Text = status.LastError;
            RenderPermissions(permissions, canManage);
            RenderJournal(journal);
        }
        catch (Exception ex) { _notice.Text = "Dream management failed: " + ex.Message; }
        finally { _loading = false; }
    }

    private void RenderSettings(MobileDreamSettings settings)
    {
        _enabled.IsToggled = settings.Enabled;
        _preset.SelectedItem = settings.Preset;
        Set("recurrenceMinutes", settings.RecurrenceMinutes); Set("maxRunMinutes", settings.MaxRunMinutes); Set("tokenBudget", settings.TokenBudget); Set("sourceTokenBudget", settings.SourceTokenBudget); Set("sessionsPerPass", settings.SessionsPerPass);
        Set("startCpuPercent", settings.StartCpuPercent); Set("pauseCpuPercent", settings.PauseCpuPercent); Set("startRamPercent", settings.StartRamPercent); Set("pauseRamPercent", settings.PauseRamPercent); Set("startGpuPercent", settings.StartGpuPercent); Set("pauseGpuPercent", settings.PauseGpuPercent); Set("startVramPercent", settings.StartVramPercent); Set("pauseVramPercent", settings.PauseVramPercent); Set("startDiskPercent", settings.StartDiskPercent); Set("pauseDiskPercent", settings.PauseDiskPercent);
    }

    private async Task SaveAsync()
    {
        MobileDreamSettings s = _settings.Settings;
        s.Enabled = _enabled.IsToggled; s.Preset = _preset.SelectedItem?.ToString() ?? "custom";
        s.RecurrenceMinutes = Read("recurrenceMinutes", s.RecurrenceMinutes); s.MaxRunMinutes = Read("maxRunMinutes", s.MaxRunMinutes); s.TokenBudget = Read("tokenBudget", s.TokenBudget); s.SourceTokenBudget = Read("sourceTokenBudget", s.SourceTokenBudget); s.SessionsPerPass = Read("sessionsPerPass", s.SessionsPerPass);
        s.StartCpuPercent = Read("startCpuPercent", s.StartCpuPercent); s.PauseCpuPercent = Read("pauseCpuPercent", s.PauseCpuPercent); s.StartRamPercent = Read("startRamPercent", s.StartRamPercent); s.PauseRamPercent = Read("pauseRamPercent", s.PauseRamPercent); s.StartGpuPercent = Read("startGpuPercent", s.StartGpuPercent); s.PauseGpuPercent = Read("pauseGpuPercent", s.PauseGpuPercent); s.StartVramPercent = Read("startVramPercent", s.StartVramPercent); s.PauseVramPercent = Read("pauseVramPercent", s.PauseVramPercent); s.StartDiskPercent = Read("startDiskPercent", s.StartDiskPercent); s.PauseDiskPercent = Read("pauseDiskPercent", s.PauseDiskPercent);
        _settings = await _client.SaveDreamSettingsAsync(OwnerKey, s); _notice.Text = "Dream settings saved."; await LoadAllAsync(true);
    }

    private async Task ResetAsync()
    {
        if (string.IsNullOrWhiteSpace(OwnerKey) || OwnerKey.Equals("global", StringComparison.OrdinalIgnoreCase)) { _notice.Text = "Select an owner override to reset."; return; }
        if (!await DisplayAlertAsync("Reset Dream settings", "Reset this owner to global Dream defaults?", "Reset", "Cancel")) return;
        await _client.ResetDreamSettingsAsync(OwnerKey); await LoadAllAsync(true);
    }

    private async Task ControlAsync(string action) { await _client.ControlDreamAsync(OwnerKey, action); await LoadAllAsync(false); }

    private async Task ClearResolvedAsync()
    {
        if (!await DisplayAlertAsync("Clear resolved", "Delete resolved Dream journal entries while preserving pending reviews?", "Clear", "Cancel")) return;
        await _client.ClearResolvedDreamJournalAsync(OwnerKey); await LoadAllAsync(false);
    }

    private void RenderPermissions(MobileDreamPermissionSnapshot p, bool canManage)
    {
        _permissions.Clear();
        AddPermission("Internet search", p.InternetSearch, p.DreamInternetSearch, canManage, value => new { dreamInternetSearch = value });
        AddPermission("VS tools", p.VsCopilotTools, p.DreamVsCopilotTools, canManage, value => new { dreamVsCopilotTools = value });
        AddPermission("Agent", p.AgentAccess, p.DreamAgentAccess, canManage, value => new { dreamAgentAccess = value });
        AddPermission("Terminal", p.TerminalCommands && p.TerminalForeverApproved, p.DreamTerminalCommands, canManage, value => new { dreamTerminalCommands = value });
        AddPermission("Uploads", p.FileUploads, p.DreamFileUploads, canManage, value => new { dreamFileUploads = value });
        AddPermission("Images", p.ImageUploads, p.DreamImageUploads, canManage, value => new { dreamImageUploads = value });
        AddPermission("Downloads", p.FileDownloads, p.DreamFileDownloads, canManage, value => new { dreamFileDownloads = value });
        AddPermission("FTP", p.FtpServer, p.DreamFtpServer, canManage, value => new { dreamFtpServer = value });
        AddPermission("PC Access", p.PcAccess, p.DreamPcAccess, canManage, value => new { dreamPcAccess = value });
    }

    private void AddPermission(string name, bool baseEnabled, bool enabled, bool canManage, Func<bool, object> payload)
    {
        var toggle = new Switch { IsToggled = enabled, IsEnabled = canManage && baseEnabled };
        toggle.Toggled += async (_, args) => { if (_loading) return; try { await _client.SaveDreamPermissionsAsync(OwnerKey, payload(args.Value)); _notice.Text = name + " Dream permission saved."; } catch (Exception ex) { _notice.Text = ex.Message; await LoadAllAsync(false); } };
        _permissions.Add(new HorizontalStackLayout { Spacing = 9, Children = { toggle, new Label { Text = name + (baseEnabled ? "" : " · base permission off"), TextColor = baseEnabled ? Colors.White : Color.FromArgb("#64748B"), VerticalTextAlignment = TextAlignment.Center } } });
    }

    private void RenderJournal(IReadOnlyList<MobileDreamJournalEntry> entries)
    {
        _journal.Clear();
        if (entries.Count == 0) { _journal.Add(Muted("No dreams recorded yet.")); return; }
        foreach (MobileDreamJournalEntry entry in entries)
        {
            var body = new VerticalStackLayout { Spacing = 6, Children = { new Label { Text = entry.Summary + " (" + entry.Status + ")", TextColor = Colors.White, FontAttributes = FontAttributes.Bold }, Muted($"{entry.ProcessedSessions} sessions · {entry.ProcessedMessages} messages · {entry.CreatedUtc}") } };
            if (!string.IsNullOrWhiteSpace(entry.RawReflection)) body.Add(new Label { Text = entry.RawReflection, TextColor = Color.FromArgb("#94A3B8"), FontSize = 11, LineBreakMode = LineBreakMode.WordWrap });
            foreach (MobileDreamCandidate candidate in entry.Candidates)
            {
                body.Add(new Label { Text = candidate.Text + " · " + candidate.Disposition + " · " + Math.Round(candidate.Confidence * 100) + "%", TextColor = Colors.White, FontSize = 12 });
                if (candidate.Disposition != "review") continue;
                var actions = new FlexLayout { Direction = FlexDirection.Row, Wrap = FlexWrap.Wrap };
                foreach (string action in candidate.CandidateType == "memory-conflict" ? new[] { "approve", "overwrite", "delete-stale", "reject" } : new[] { "approve", "reject" })
                    actions.Add(ActionButton(action, "#334155", async () => { await _client.DecideDreamCandidateAsync(OwnerKey, candidate.Id, action); await LoadAllAsync(false); }));
                body.Add(actions);
            }
            if (!entry.Candidates.Any(item => item.Disposition == "review") && entry.Status != "running") body.Add(ActionButton("Delete entry", "#7F1D1D", async () => { await _client.DeleteDreamJournalAsync(OwnerKey, entry.Id); await LoadAllAsync(false); }));
            _journal.Add(Card(body));
        }
    }

    private void AddField(Grid grid, string key, string label, int row, int column)
    {
        var entry = new Entry { Keyboard = Keyboard.Numeric, TextColor = Colors.White, BackgroundColor = Color.FromArgb("#111827") };
        _fields[key] = entry;
        grid.Add(new VerticalStackLayout { Spacing = 3, Children = { Muted(label), entry } }, column, row);
    }
    private void Set(string key, int value) => _fields[key].Text = value.ToString();
    private int Read(string key, int fallback) => int.TryParse(_fields[key].Text, out int value) ? value : fallback;
    private static Label Section(string text) => new() { Text = text, FontSize = 17, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, VerticalTextAlignment = TextAlignment.Center };
    private static Label Muted(string text) => new() { Text = text, TextColor = Color.FromArgb("#94A3B8"), FontSize = 12, LineBreakMode = LineBreakMode.WordWrap };
    private static Border Card(View content) => new() { Stroke = Color.FromArgb("#334155"), StrokeThickness = 1, BackgroundColor = Color.FromArgb("#111827"), Padding = 12, Content = content };
    private static Button ActionButton(string text, string color, Func<Task> action)
    {
        var button = new Button { Text = text, BackgroundColor = Color.FromArgb(color), TextColor = Colors.White, CornerRadius = 10, Margin = new Thickness(0, 0, 7, 7) };
        button.Clicked += async (_, _) => { try { await action(); } catch (Exception ex) { await Application.Current!.Windows[0].Page!.DisplayAlertAsync("Dream Mode", ex.Message, "OK"); } };
        return button;
    }
}
