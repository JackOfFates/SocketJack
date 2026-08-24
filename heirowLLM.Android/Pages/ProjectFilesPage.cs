using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using heirowLLM.Mobile.Models;
using heirowLLM.Mobile.Services;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Storage;

namespace heirowLLM.Mobile.Pages;

public sealed class ProjectFilesPage : ContentPage
{
    private readonly HeirowLlmClient _client;
    private readonly string _sessionId;
    private readonly ObservableCollection<ProjectFileEntry> _entries = new();
    private readonly CollectionView _list;
    private readonly RefreshView _refresh;
    private readonly Entry _search;
    private readonly Picker _sort;
    private readonly Label _pathLabel;
    private readonly Label _storageLabel;
    private readonly Label _status;
    private readonly ProgressBar _storage;
    private string _path = "\\";

    public ProjectFilesPage(HeirowLlmClient client, string sessionId)
    {
        _client = client;
        _sessionId = sessionId;
        Title = "Project Files";
        BackgroundColor = Color.FromArgb("#0B1020");

        _search = new Entry { Placeholder = "Search project files", TextColor = Colors.White, PlaceholderColor = Color.FromArgb("#64748B"), ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
        _search.Completed += async (_, _) => await RefreshAsync();
        _sort = new Picker { Title = "Sort", ItemsSource = new[] { "Name", "Modified", "Size", "Type" }, SelectedIndex = 0, TextColor = Colors.White, WidthRequest = 120 };
        _sort.SelectedIndexChanged += async (_, _) => await RefreshAsync();
        _pathLabel = new Label { Text = "Project Files", TextColor = Color.FromArgb("#BFDBFE"), FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
        _storageLabel = new Label { TextColor = Color.FromArgb("#94A3B8"), FontSize = 11 };
        _status = new Label { TextColor = Color.FromArgb("#94A3B8"), FontSize = 11 };
        _storage = new ProgressBar { ProgressColor = Color.FromArgb("#60A5FA"), BackgroundColor = Color.FromArgb("#26334D"), HeightRequest = 3 };

        _list = new CollectionView { ItemsSource = _entries, SelectionMode = SelectionMode.None, ItemTemplate = new DataTemplate(BuildEntryView) };
        _refresh = new RefreshView { Content = _list, RefreshColor = Color.FromArgb("#60A5FA") };
        _refresh.Refreshing += async (_, _) => await RefreshAsync();

        var back = new Button { Text = "‹", WidthRequest = 42, IsVisible = false, BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White };
        back.Clicked += async (_, _) =>
        {
            _path = ParentPath(_path);
            back.IsVisible = _path != "\\";
            await RefreshAsync();
        };
        var upload = new Button { Text = "Upload", BackgroundColor = Color.FromArgb("#1D4ED8"), TextColor = Colors.White };
        upload.Clicked += async (_, _) => await UploadAsync();
        var versions = new Button { Text = "Versions", BackgroundColor = Color.FromArgb("#1F2937"), TextColor = Colors.White };
        versions.Clicked += async (_, _) => await Navigation.PushAsync(new ProjectVersionsPage(_client, _sessionId));
        var pathRow = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) }, ColumnSpacing = 7 };
        pathRow.Add(back, 0); pathRow.Add(_pathLabel, 1); pathRow.Add(upload, 2); pathRow.Add(versions, 3);
        var searchRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        searchRow.Add(_search, 0); searchRow.Add(_sort, 1);
        Content = new Grid
        {
            Padding = new Thickness(12, 8),
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            Children =
            {
                pathRow.Row(0), searchRow.Row(1), _storageLabel.Row(2), _storage.Row(3), _refresh.Row(4), _status.Row(5)
            }
        };
        Loaded += async (_, _) => await RefreshAsync();
    }

    private View BuildEntryView()
    {
        var icon = new Label { FontSize = 24, VerticalTextAlignment = TextAlignment.Center };
        icon.SetBinding(Label.TextProperty, nameof(ProjectFileEntry.Icon));
        var name = new Label { TextColor = Colors.White, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
        name.SetBinding(Label.TextProperty, nameof(ProjectFileEntry.Name));
        var detail = new Label { TextColor = Color.FromArgb("#94A3B8"), FontSize = 10 };
        detail.SetBinding(Label.TextProperty, nameof(ProjectFileEntry.Detail));
        var more = new Button { Text = "•••", WidthRequest = 48, BackgroundColor = Colors.Transparent, TextColor = Colors.White };
        more.Clicked += async (_, _) =>
        {
            if (more.BindingContext is ProjectFileEntry entry) await ShowActionsAsync(entry);
        };
        var text = new VerticalStackLayout { Spacing = 2, Children = { name, detail } };
        var grid = new Grid { Padding = new Thickness(10, 8), ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 10 };
        grid.Add(icon, 0); grid.Add(text, 1); grid.Add(more, 2);
        var border = new Border { Margin = new Thickness(0, 3), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = grid };
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            if (border.BindingContext is not ProjectFileEntry entry) return;
            if (entry.IsDirectory)
            {
                _path = entry.Path;
                _search.Text = "";
                await RefreshAsync();
            }
            else await PreviewAsync(entry);
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

    private async Task RefreshAsync()
    {
        try
        {
            _refresh.IsRefreshing = true;
            string search = _search.Text?.Trim() ?? "";
            ProjectFilesSnapshot snapshot = await _client.GetProjectFilesAsync(_sessionId, _path, search, (_sort.SelectedItem as string ?? "Name").ToLowerInvariant());
            IEnumerable<ProjectFileEntry> source = search.Length > 0 ? snapshot.Results : snapshot.Children.Count > 0 || snapshot.Current is not null ? snapshot.Children : snapshot.Roots;
            _entries.Clear();
            foreach (ProjectFileEntry entry in source) _entries.Add(entry);
            _pathLabel.Text = _path == "\\" ? "Project Files" : "Project Files  ›  " + _path.Trim('\\').Replace("\\", "  ›  ");
            _storage.Progress = snapshot.Storage.UsageRatio;
            _storageLabel.Text = snapshot.Storage.Unlimited
                ? $"{Bytes(snapshot.Storage.TotalUsedBytes)} used · unlimited administrator storage"
                : $"{Bytes(snapshot.Storage.TotalUsedBytes)} of {Bytes(snapshot.Storage.LimitBytes)}";
            _status.Text = $"{_entries.Count} item{(_entries.Count == 1 ? "" : "s")} · shared by this project";
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally { _refresh.IsRefreshing = false; }
    }

    private async Task UploadAsync()
    {
        try
        {
            IEnumerable<FileResult> selected = await FilePicker.Default.PickMultipleAsync(new PickOptions { PickerTitle = "Upload to " + (_path == "\\" ? "Project Files" : _path) }) ?? Array.Empty<FileResult>();
            FileResult[] files = selected?.ToArray() ?? Array.Empty<FileResult>();
            if (files.Length == 0) return;
            long aggregateTotal = 0;
            var pending = new List<AttachmentInfo>();
            foreach (FileResult file in files)
            {
                await using Stream input = await file.OpenReadAsync();
                using var memory = new MemoryStream();
                await input.CopyToAsync(memory);
                var attachment = new AttachmentInfo { Name = file.FileName, ContentType = file.ContentType ?? "application/octet-stream", Data = memory.ToArray() };
                pending.Add(attachment);
                aggregateTotal += attachment.Data.LongLength;
            }
            long completed = 0;
            foreach (AttachmentInfo attachment in pending)
            {
                bool extractZip = attachment.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    await DisplayAlertAsync("ZIP upload", $"Extract {attachment.Name} into the selected folder?", "Extract", "Keep ZIP");
                long before = completed;
                var progress = new Progress<double>(value => _status.Text = $"Uploading {attachment.Name} · {value:P0} · {Bytes(before + (long)(attachment.Data.LongLength * value))} / {Bytes(aggregateTotal)}");
                await _client.UploadProjectFileAsync(_sessionId, attachment, _path, extractZip, progress);
                completed += attachment.Data.LongLength;
            }
            _status.Text = $"{pending.Count} upload{(pending.Count == 1 ? "" : "s")} complete";
            await RefreshAsync();
        }
        catch (Exception ex) { await DisplayAlertAsync("Upload failed", ex.Message, "OK"); }
    }

    private async Task ShowActionsAsync(ProjectFileEntry entry)
    {
        string action = await DisplayActionSheetAsync(entry.Name, "Cancel", "Delete", entry.IsDirectory ? "Open" : "Preview", entry.IsDirectory ? "Download ZIP" : "Download");
        if (action == "Open") { _path = entry.Path; await RefreshAsync(); }
        else if (action == "Preview") await PreviewAsync(entry);
        else if (action is "Download" or "Download ZIP") await DownloadAsync(entry);
        else if (action == "Delete") await DeleteAsync(entry);
    }

    private async Task PreviewAsync(ProjectFileEntry entry)
    {
        try
        {
            using JsonDocument json = await _client.PreviewProjectFileAsync(_sessionId, entry);
            JsonElement root = json.RootElement;
            if (!GetBool(root, "ok")) throw new InvalidOperationException(GetString(root, "error"));
            string kind = GetString(root, "previewKind");
            string dataUrl = GetString(root, "dataUrl");
            string text = GetString(root, "text");
            View content;
            if (kind == "image" && dataUrl.Contains(','))
            {
                byte[] bytes = Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]);
                content = new ScrollView { Content = new Image { Source = ImageSource.FromStream(() => new MemoryStream(bytes)), Aspect = Aspect.AspectFit } };
            }
            else if ((kind == "audio" || kind == "video") && dataUrl.Length > 0)
            {
                string tag = kind == "video" ? "video" : "audio";
                content = new WebView { Source = new HtmlWebViewSource { Html = $"<html><body style='margin:0;background:#0b1020;display:flex;align-items:center;justify-content:center'><{tag} controls autoplay style='max-width:100%;max-height:100%' src='{dataUrl}'></{tag}></body></html>" } };
            }
            else content = new Editor { Text = text, IsReadOnly = true, TextColor = Colors.White, BackgroundColor = Color.FromArgb("#0B1020"), FontFamily = "monospace" };
            await Navigation.PushAsync(new ContentPage { Title = entry.Name, BackgroundColor = Color.FromArgb("#0B1020"), Content = content });
        }
        catch
        {
            await DownloadAsync(entry, open: true);
        }
    }

    private async Task DownloadAsync(ProjectFileEntry entry, bool open = false)
    {
        try
        {
            byte[] bytes = await _client.DownloadProjectFileAsync(_sessionId, entry);
            string name = entry.IsDirectory ? entry.Name + ".zip" : entry.Name;
            string path = System.IO.Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllBytesAsync(path, bytes);
            if (open) await Launcher.Default.OpenAsync(new OpenFileRequest(name, new ReadOnlyFile(path)));
            else await Share.Default.RequestAsync(new ShareFileRequest { Title = "Save or share " + name, File = new ShareFile(path) });
        }
        catch (Exception ex) { await DisplayAlertAsync("Download failed", ex.Message, "OK"); }
    }

    private async Task DeleteAsync(ProjectFileEntry entry)
    {
        if (entry.IsDirectory && entry.Path == "\\") return;
        string detail = entry.IsDirectory ? "This recursively deletes the folder and every item inside it." : "This deletes the file from every session in this project.";
        if (!await DisplayAlertAsync("Delete " + entry.Name + "?", detail, "Delete", "Cancel")) return;
        try { await _client.DeleteProjectFileAsync(_sessionId, entry); await RefreshAsync(); }
        catch (Exception ex) { await DisplayAlertAsync("Delete failed", ex.Message, "OK"); }
    }

    private static string ParentPath(string path)
    {
        string trimmed = (path ?? "\\").TrimEnd('\\', '/');
        int index = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        return index <= 0 ? "\\" : trimmed[..index];
    }
    private static string Bytes(long value) => value >= 1073741824 ? $"{value / 1073741824d:0.#} GB" : value >= 1048576 ? $"{value / 1048576d:0.#} MB" : value >= 1024 ? $"{value / 1024d:0.#} KB" : $"{value} B";
    private static string GetString(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? value.ToString() : "";
    private static bool GetBool(JsonElement root, string name) => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}

public sealed class ProjectVersionsPage : ContentPage
{
    private readonly HeirowLlmClient _client;
    private readonly string _sessionId;
    private readonly ObservableCollection<ProjectFileVersion> _versions = new();

    public ProjectVersionsPage(HeirowLlmClient client, string sessionId)
    {
        _client = client;
        _sessionId = sessionId;
        Title = "Versions";
        BackgroundColor = Color.FromArgb("#0B1020");
        var list = new CollectionView { ItemsSource = _versions, ItemTemplate = new DataTemplate(BuildVersionView) };
        var create = new Button { Text = "Create named version", BackgroundColor = Color.FromArgb("#1D4ED8"), TextColor = Colors.White, Margin = new Thickness(12) };
        create.Clicked += async (_, _) =>
        {
            string? name = await DisplayPromptAsync("New Version", "Name this Project Files snapshot:", initialValue: "Version " + DateTime.Now.ToString("g"));
            if (string.IsNullOrWhiteSpace(name)) return;
            await _client.MutateProjectFileVersionAsync(_sessionId, "create", name);
            await LoadAsync();
        };
        Content = new Grid { RowDefinitions = { new(GridLength.Auto), new(GridLength.Star) }, Children = { create.Row(0), list.Row(1) } };
        Loaded += async (_, _) => await LoadAsync();
    }

    private View BuildVersionView()
    {
        var name = new Label { TextColor = Colors.White, FontAttributes = FontAttributes.Bold };
        name.SetBinding(Label.TextProperty, nameof(ProjectFileVersion.Name));
        var detail = new Label { TextColor = Color.FromArgb("#94A3B8"), FontSize = 11 };
        detail.SetBinding(Label.TextProperty, nameof(ProjectFileVersion.Detail));
        var actions = new Button { Text = "•••", BackgroundColor = Colors.Transparent, TextColor = Colors.White };
        actions.Clicked += async (_, _) =>
        {
            if (actions.BindingContext is not ProjectFileVersion version) return;
            string action = await DisplayActionSheetAsync(version.Name, "Cancel", "Delete", "Revert");
            if (action == "Revert")
            {
                if (!await DisplayAlertAsync("Revert Project Files?", "A safety snapshot of the current files will be created automatically before restore.", "Revert", "Cancel")) return;
                await _client.MutateProjectFileVersionAsync(_sessionId, "restore", versionId: version.Id);
                await DisplayAlertAsync("Restored", "Project Files were restored. Your pre-restore safety snapshot is available in Versions.", "OK");
            }
            else if (action == "Delete" && await DisplayAlertAsync("Delete Version?", "This removes the saved snapshot. Current Project Files are unchanged.", "Delete", "Cancel"))
                await _client.MutateProjectFileVersionAsync(_sessionId, "delete", versionId: version.Id);
            await LoadAsync();
        };
        var grid = new Grid { Padding = 12, ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) } };
        grid.Add(new VerticalStackLayout { Children = { name, detail } }, 0); grid.Add(actions, 1);
        return new Border { Margin = new Thickness(12, 4), BackgroundColor = Color.FromArgb("#151C2F"), Stroke = Color.FromArgb("#26334D"), StrokeShape = new RoundRectangle { CornerRadius = 12 }, Content = grid };
    }

    private async Task LoadAsync()
    {
        try
        {
            ProjectFileVersionsSnapshot result = await _client.GetProjectFileVersionsAsync(_sessionId);
            _versions.Clear();
            foreach (ProjectFileVersion version in result.Versions) _versions.Add(version);
        }
        catch (Exception ex) { await DisplayAlertAsync("Versions", ex.Message, "OK"); }
    }
}
