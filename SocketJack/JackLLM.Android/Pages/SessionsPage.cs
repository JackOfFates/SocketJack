using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace JackLLM.Mobile.Pages;

public sealed class SessionsPage : ContentPage
{
    private readonly ServerInfo _server;
    private readonly JackLlmClient _client;
    private readonly Func<string, Task> _openSession;
    private readonly Func<string, Task> _newSession;
    private readonly VerticalStackLayout _projectsHost;
    private readonly ActivityIndicator _loading;
    private readonly Label _syncState;
    private readonly Label _summary;
    private readonly RefreshView _refreshView;
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase) { "unsorted" };
    private IReadOnlyList<ChatProjectInfo> _projects = Array.Empty<ChatProjectInfo>();
    private IReadOnlyList<ChatSessionInfo> _sessions = Array.Empty<ChatSessionInfo>();
    private bool _showArchived;
    private bool _loaded;

    public SessionsPage(ServerInfo server, JackLlmClient client, Func<string, Task> openSession, Func<string, Task> newSession)
    {
        _server = server;
        _client = client;
        _openSession = openSession;
        _newSession = newSession;
        Title = "Projects";
        BackgroundColor = Color.FromArgb("#080D1A");

        _syncState = new Label { Text = "●  Syncing", FontSize = 11, TextColor = Color.FromArgb("#93C5FD"), VerticalTextAlignment = TextAlignment.Center };
        _summary = new Label { Text = "Projects", FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#CBD5E1"), Margin = new Thickness(4, 8, 0, 3) };
        _loading = new ActivityIndicator { IsRunning = true, Color = Color.FromArgb("#60A5FA"), HorizontalOptions = LayoutOptions.Center, HeightRequest = 24 };
        _projectsHost = new VerticalStackLayout { Spacing = 9, Padding = new Thickness(0, 4, 0, 20) };

        ToolbarItems.Add(new ToolbarItem("＋", null, async () => await CreateProjectAsync()) { AutomationId = "CreateProject" });
        ToolbarItems.Add(new ToolbarItem("Archive", null, async () => { _showArchived = !_showArchived; Render(); await Task.CompletedTask; }) { AutomationId = "ToggleArchivedProjects" });
        ToolbarItems.Add(new ToolbarItem("↻", null, async () => await LoadAsync()) { AutomationId = "RefreshProjects" });

        var eyebrow = new Label { Text = "PROJECTS · SYNCHRONIZED", CharacterSpacing = 1.4, FontSize = 10, FontAttributes = FontAttributes.Bold, TextColor = Color.FromArgb("#64748B") };
        var rootTitle = new Label { Text = "▾  ▣  " + server.DisplayName, FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
        var rootHeader = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }, ColumnSpacing = 8 };
        rootHeader.Add(rootTitle, 0); rootHeader.Add(_syncState, 1);
        var root = new Border { Padding = new Thickness(14, 12), Margin = new Thickness(0, 8, 0, 2), BackgroundColor = Color.FromArgb("#121A2C"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 16 }, Content = new VerticalStackLayout { Spacing = 4, Children = { rootHeader, new Label { Text = server.Endpoint, FontSize = 10, TextColor = Color.FromArgb("#94A3B8"), LineBreakMode = LineBreakMode.TailTruncation } } } };
        var scroll = new ScrollView { Content = _projectsHost };
        var pageContent = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star) },
            Padding = new Thickness(12, 10, 12, 0),
            Children = { eyebrow, root.Row(1), _summary.Row(2), _loading.Row(3), scroll.Row(4) }
        };
        _refreshView = new RefreshView { Content = pageContent, RefreshColor = Color.FromArgb("#60A5FA") };
        _refreshView.Refreshing += async (_, _) => { try { await LoadAsync(); } finally { _refreshView.IsRefreshing = false; } };
        Content = _refreshView;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading.IsVisible = _loading.IsRunning = true;
        _syncState.Text = "●  Syncing";
        try
        {
            Task<IReadOnlyList<ChatProjectInfo>> projects = _client.GetProjectsAsync(true);
            Task<IReadOnlyList<ChatSessionInfo>> sessions = _client.GetSessionsAsync();
            await Task.WhenAll(projects, sessions);
            _projects = projects.Result;
            _sessions = sessions.Result;
            _summary.Text = $"Projects  {_projects.Count:N0}   ·   {_sessions.Count:N0} chats   ·   {_sessions.Sum(item => item.MessageCount):N0} messages";
            _syncState.Text = "●  Synced now";
            _syncState.TextColor = Color.FromArgb("#6EE7B7");
            _loaded = true;
            Render();
        }
        catch (Exception ex)
        {
            _syncState.Text = "●  Offline";
            _syncState.TextColor = Color.FromArgb("#FCA5A5");
            _summary.Text = "Sync failed · " + ex.Message;
        }
        finally { _loading.IsRunning = _loading.IsVisible = false; }
    }

    private void Render()
    {
        _projectsHost.Children.Clear();
        foreach (ChatProjectInfo project in _projects.Where(item => _showArchived || !item.Archived))
            _projectsHost.Children.Add(CreateProjectNode(project));
        if (_projectsHost.Children.Count == 0)
            _projectsHost.Children.Add(new Label { Text = "No projects yet. Tap ＋ to create one.", Margin = new Thickness(20), TextColor = Color.FromArgb("#94A3B8"), HorizontalTextAlignment = TextAlignment.Center });
    }

    private View CreateProjectNode(ChatProjectInfo project)
    {
        List<ChatSessionInfo> sessions = _sessions.Where(item => string.Equals(string.IsNullOrWhiteSpace(item.ProjectId) ? "unsorted" : item.ProjectId, project.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Pinned).ThenByDescending(item => item.PinnedUtc).ThenByDescending(item => item.UpdatedAt).ToList();
        bool expanded = _expanded.Contains(project.Id);
        var toggle = SmallButton(expanded ? "▾" : "▸", expanded ? "Collapse project" : "Expand project");
        var title = new Label { Text = (project.Pinned ? "★ " : "") + project.Name + $"  ·  {sessions.Count:N0}", FontSize = 15, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
        var subtitle = new Label { Text = project.Subtitle, FontSize = 10, TextColor = Color.FromArgb("#94A3B8"), LineBreakMode = LineBreakMode.TailTruncation };
        var titleStack = new VerticalStackLayout { Spacing = 1, Children = { title, subtitle } };
        var add = SmallButton("＋", "New chat in project"); add.IsEnabled = !project.Archived;
        var pin = SmallButton(project.Pinned ? "★" : "☆", project.Pinned ? "Unpin project" : "Pin project"); pin.IsVisible = !project.BuiltIn;
        var more = SmallButton("⋯", "Project actions"); more.IsVisible = !project.BuiltIn;
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(38), new ColumnDefinition(GridLength.Star), new ColumnDefinition(42), new ColumnDefinition(42), new ColumnDefinition(42) }, ColumnSpacing = 4 };
        header.Add(toggle, 0); header.Add(titleStack, 1); header.Add(add, 2); header.Add(pin, 3); header.Add(more, 4);
        var children = new VerticalStackLayout { Spacing = 5, IsVisible = expanded, Margin = new Thickness(7, 7, 0, 0) };
        foreach (ChatSessionInfo session in sessions) children.Add(CreateSessionNode(session));
        if (sessions.Count == 0) children.Add(new Label { Text = "No chats yet. Tap ＋ to start one.", FontSize = 11, TextColor = Color.FromArgb("#64748B"), Margin = new Thickness(12, 8) });
        toggle.Clicked += (_, _) => { if (_expanded.Contains(project.Id)) _expanded.Remove(project.Id); else _expanded.Add(project.Id); Render(); };
        add.Clicked += async (_, _) => { await _newSession(project.Id); await Navigation.PopAsync(); };
        pin.Clicked += async (_, _) => { await _client.PinProjectAsync(project.Id, !project.Pinned); await LoadAsync(); };
        more.Clicked += async (_, _) => await ShowProjectActionsAsync(project);
        var border = new Border { Padding = 10, BackgroundColor = project.Archived ? Color.FromArgb("#171827") : Color.FromArgb("#10192A"), Stroke = project.Pinned ? Color.FromArgb("#EAB308") : Color.FromArgb("#26334D"), StrokeThickness = 1, StrokeShape = new RoundRectangle { CornerRadius = 15 }, Content = new VerticalStackLayout { Spacing = 2, Children = { header, children } } };
        return border;
    }

    private View CreateSessionNode(ChatSessionInfo session)
    {
        var title = new Label { Text = (session.Pinned ? "★ " : "") + session.Title, FontSize = 14, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation };
        var meta = new Label { Text = session.UpdatedDisplay + "\n" + session.ActivityDisplay + "\n" + session.TokenDisplay, FontSize = 10, TextColor = Color.FromArgb("#94A3B8"), LineBreakMode = LineBreakMode.TailTruncation };
        var open = SmallButton("↗", "Open chat");
        var pin = SmallButton(session.Pinned ? "★" : "☆", session.Pinned ? "Unpin chat" : "Pin chat");
        var more = SmallButton("⋯", "Chat actions");
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(42), new ColumnDefinition(42), new ColumnDefinition(42) }, ColumnSpacing = 4 };
        grid.Add(new VerticalStackLayout { Spacing = 3, Children = { title, meta } }, 0); grid.Add(open, 1); grid.Add(pin, 2); grid.Add(more, 3);
        open.Clicked += async (_, _) => { await _openSession(session.Id); await Navigation.PopAsync(); };
        pin.Clicked += async (_, _) => { await _client.PinSessionAsync(session.Id, !session.Pinned); await LoadAsync(); };
        more.Clicked += async (_, _) => await ShowSessionActionsAsync(session);
        return new Border { Margin = new Thickness(14, 2, 0, 2), Padding = new Thickness(10, 8), BackgroundColor = Color.FromArgb("#111827"), Stroke = session.Pinned ? Color.FromArgb("#CA8A04") : Color.FromArgb("#202C43"), StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = grid };
    }

    private async Task CreateProjectAsync()
    {
        string? name = await DisplayPromptAsync("New project", "Project name", "Create", "Cancel", "New Project", 120);
        if (string.IsNullOrWhiteSpace(name)) return;
        await _client.CreateProjectAsync(name.Trim());
        await LoadAsync();
    }

    private async Task ShowProjectActionsAsync(ChatProjectInfo project)
    {
        string archiveLabel = project.Archived ? "Restore" : "Archive";
        string? action = await DisplayActionSheetAsync(project.Name, "Cancel", null, "Rename", archiveLabel);
        if (action == "Rename")
        {
            string? name = await DisplayPromptAsync("Rename project", "Project name", "Save", "Cancel", project.Name, 120);
            if (!string.IsNullOrWhiteSpace(name)) await _client.RenameProjectAsync(project.Id, name.Trim());
        }
        else if (action == archiveLabel) await _client.ArchiveProjectAsync(project.Id, !project.Archived);
        await LoadAsync();
    }

    private async Task ShowSessionActionsAsync(ChatSessionInfo session)
    {
        string? action = await DisplayActionSheetAsync(session.Title, "Cancel", "Delete", "Rename", "Move to project");
        if (action == "Rename")
        {
            string? name = await DisplayPromptAsync("Rename chat", "Chat name", "Save", "Cancel", session.Title, 160);
            if (!string.IsNullOrWhiteSpace(name)) await _client.RenameSessionAsync(session.Id, name.Trim());
        }
        else if (action == "Move to project")
        {
            ChatProjectInfo[] targets = _projects.Where(item => !item.Archived).ToArray();
            string? targetName = await DisplayActionSheetAsync("Move chat", "Cancel", null, targets.Select(item => item.Name).ToArray());
            ChatProjectInfo? target = targets.FirstOrDefault(item => item.Name == targetName);
            if (target is not null) await _client.MoveSessionAsync(session.Id, target.Id);
        }
        else if (action == "Delete" && await DisplayAlertAsync("Delete chat", "Delete this chat permanently?", "Delete", "Cancel")) await _client.DeleteSessionAsync(session.Id);
        await LoadAsync();
    }

    private static Button SmallButton(string text, string help)
    {
        var button = new Button { Text = text, FontSize = 17, TextColor = Color.FromArgb("#BFDBFE"), BackgroundColor = Colors.Transparent, WidthRequest = 40, HeightRequest = 40, Padding = 0, CornerRadius = 10 };
        AutomationProperties.SetHelpText(button, help);
        return button;
    }
}
