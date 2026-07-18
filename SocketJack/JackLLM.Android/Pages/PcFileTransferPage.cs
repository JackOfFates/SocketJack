using AndroidX.DocumentFile.Provider;
using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using SocketJack.Net;

namespace JackLLM.Mobile.Pages;

public sealed class PcFileTransferPage : ContentPage
{
    private const string LocalTreePreference = "pc_access_local_tree";
    private readonly JackLlmClient _client;
    private readonly Label _status = new() { Text = "FTP is required", TextColor = Color.FromArgb("#CBD5E1"), LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _localPath = PanePath("Phone");
    private readonly Label _remotePathLabel = PanePath("/");
    private readonly VerticalStackLayout _localFiles = new() { Spacing = 5 };
    private readonly VerticalStackLayout _remoteFiles = new() { Spacing = 5 };
    private readonly ProgressBar _progress = new() { ProgressColor = Color.FromArgb("#38BDF8"), IsVisible = false };
    private readonly Button _upload;
    private readonly Button _download;
    private readonly Button _cancel;
    private SocketJack.Net.FtpClient? _ftp;
    private PcAccessFtpConnection? _connection;
    private DocumentFile? _localRoot;
    private DocumentFile? _localDirectory;
    private DocumentFile? _selectedLocal;
    private FtpListItem? _selectedRemote;
    private string _remotePath = "/";
    private CancellationTokenSource? _transfer;

    public PcFileTransferPage(JackLlmClient client)
    {
        _client = client;
        Title = "File Transfer";
        BackgroundColor = Color.FromArgb("#0B1020");

        var chooseFolder = IconButton("⌂", "Choose phone folder");
        chooseFolder.Clicked += async (_, _) => await RunUiAsync(ChooseLocalFolderAsync);
        var refresh = IconButton("↻", "Refresh both folders");
        refresh.Clicked += async (_, _) => await RunUiAsync(RefreshBothAsync);
        _cancel = IconButton("■", "Cancel transfer", "#7F1D1D");
        _cancel.IsVisible = false;
        _cancel.Clicked += (_, _) => _transfer?.Cancel();

        _upload = IconButton("→", "Upload selected phone file", "#075985");
        _download = IconButton("←", "Download selected FTP file", "#166534");
        _upload.Clicked += async (_, _) => await RunUiAsync(UploadSelectedAsync);
        _download.Clicked += async (_, _) => await RunUiAsync(DownloadSelectedAsync);

        var localBack = IconButton("‹", "Phone parent folder");
        localBack.Clicked += async (_, _) => await RunUiAsync(async () => { if (_localDirectory?.ParentFile is { } parent && _localRoot is not null) { _localDirectory = parent; await LoadLocalAsync(); } });
        var remoteBack = IconButton("‹", "FTP parent folder");
        remoteBack.Clicked += async (_, _) => await RunUiAsync(async () => { _remotePath = ParentRemotePath(_remotePath); await LoadRemoteAsync(); });

        var localNew = IconButton("+", "New phone folder");
        localNew.Clicked += async (_, _) => await RunUiAsync(CreateLocalFolderAsync);
        var remoteNew = IconButton("+", "New FTP folder");
        remoteNew.Clicked += async (_, _) => await RunUiAsync(CreateRemoteFolderAsync);
        var localDelete = IconButton("⌫", "Delete selected phone item", "#7F1D1D");
        localDelete.Clicked += async (_, _) => await RunUiAsync(DeleteLocalAsync);
        var remoteDelete = IconButton("⌫", "Delete selected FTP item", "#7F1D1D");
        remoteDelete.Clicked += async (_, _) => await RunUiAsync(DeleteRemoteAsync);

        Grid header = new()
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 6,
            Children = { _status, chooseFolder.Column(1), refresh.Column(2), _cancel.Column(3) }
        };

        View localPane = BuildPane("PHONE", _localPath, localBack, localNew, localDelete, _localFiles, "Select a folder with ⌂");
        View remotePane = BuildPane("WORKSTATION FTP", _remotePathLabel, remoteBack, remoteNew, remoteDelete, _remoteFiles, "Connecting…");
        var arrows = new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Spacing = 10,
            Children = { _upload, _download }
        };
        var panes = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star) },
            ColumnSpacing = 6,
            Children = { localPane.Column(0), arrows.Column(1), remotePane.Column(2) }
        };

        Content = new Grid
        {
            Padding = 8,
            RowSpacing = 7,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children = { header.Row(0), _progress.Row(1), panes.Row(2) }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await RestoreLocalFolderAsync();
            await ConnectFtpAsync();
            await RefreshBothAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    protected override async void OnDisappearing()
    {
        _transfer?.Cancel();
        if (_ftp is not null)
        {
            try { await _ftp.DisconnectAsync(); } catch { _ftp.Dispose(); }
            _ftp = null;
        }
        base.OnDisappearing();
    }

    private async Task ConnectFtpAsync()
    {
        if (_ftp?.IsConnected == true) return;
        _status.Text = "Checking FTP permission…";
        _connection = await _client.GetPcAccessFtpAsync();
        _ftp = new SocketJack.Net.FtpClient { DataConnectionMode = FtpDataConnectionMode.ExtendedPassive };
        await _ftp.ConnectAsync(_connection.Host, _connection.Port);
        await _ftp.LoginAsync(_connection.UserName, _connection.Password);
        _remotePath = string.IsNullOrWhiteSpace(_connection.Root) ? "/" : _connection.Root;
        _status.Text = _connection.AllowWrite ? "Connected · FTP read/write" : "Connected · FTP read-only";
    }

    private async Task ChooseLocalFolderAsync()
    {
#if ANDROID
        Android.Net.Uri? uri = await MainActivity.PickFolderAsync();
        if (uri is null) return;
        Preferences.Default.Set(LocalTreePreference, uri.ToString());
        _localRoot = DocumentFile.FromTreeUri(Android.App.Application.Context, uri);
        _localDirectory = _localRoot;
        await LoadLocalAsync();
#endif
    }

    private async Task RestoreLocalFolderAsync()
    {
#if ANDROID
        string saved = Preferences.Default.Get(LocalTreePreference, "");
        if (string.IsNullOrWhiteSpace(saved)) return;
        Android.Net.Uri? savedUri = Android.Net.Uri.Parse(saved);
        if (savedUri is null) return;
        _localRoot = DocumentFile.FromTreeUri(Android.App.Application.Context, savedUri);
        _localDirectory = _localRoot;
        await LoadLocalAsync();
#endif
    }

    private async Task RefreshBothAsync()
    {
        await LoadLocalAsync();
        if (_ftp?.IsConnected != true) await ConnectFtpAsync();
        await LoadRemoteAsync();
    }

    private async Task RunUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { _status.Text = "Transfer canceled"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task LoadLocalAsync()
    {
        _selectedLocal = null;
        _localFiles.Children.Clear();
        _localPath.Text = _localDirectory?.Name ?? "Choose folder";
        DocumentFile[] entries = _localDirectory?.ListFiles() ?? Array.Empty<DocumentFile>();
        foreach (DocumentFile item in entries.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            Button button = FileButton(item.Name ?? "Unnamed", item.IsDirectory, item.IsDirectory ? "" : FormatSize(item.Length()));
            button.Clicked += async (_, _) =>
            {
                if (item.IsDirectory) { _localDirectory = item; await LoadLocalAsync(); }
                else { _selectedLocal = item; MarkSelected(_localFiles, button); }
            };
            _localFiles.Children.Add(button);
        }
        if (entries.Length == 0) _localFiles.Children.Add(EmptyLabel("Empty folder"));
        return Task.CompletedTask;
    }

    private async Task LoadRemoteAsync()
    {
        if (_ftp is null) return;
        _selectedRemote = null;
        _remoteFiles.Children.Clear();
        _remotePathLabel.Text = _remotePath;
        IReadOnlyList<FtpListItem> entries = await _ftp.ListDirectoryAsync(_remotePath);
        foreach (FtpListItem item in entries.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            Button button = FileButton(item.Name, item.IsDirectory, item.IsDirectory ? "" : FormatSize(item.Size));
            button.Clicked += async (_, _) =>
            {
                if (item.IsDirectory) { _remotePath = CombineRemote(_remotePath, item.Name); await LoadRemoteAsync(); }
                else { _selectedRemote = item; MarkSelected(_remoteFiles, button); }
            };
            _remoteFiles.Children.Add(button);
        }
        if (entries.Count == 0) _remoteFiles.Children.Add(EmptyLabel("Empty folder"));
    }

    private async Task UploadSelectedAsync()
    {
        if (_selectedLocal is null || _selectedLocal.IsDirectory) { _status.Text = "Select a phone file to upload."; return; }
        if (_connection?.AllowWrite != true) { _status.Text = "The FTP account is read-only."; return; }
        if (_ftp is null) return;
        string target = CombineRemote(_remotePath, _selectedLocal.Name ?? "upload.bin");
        Android.Net.Uri? localUri = _selectedLocal.Uri;
        using Stream? input = localUri is null ? null : Android.App.Application.Context.ContentResolver?.OpenInputStream(localUri);
        if (input is null) throw new IOException("The selected phone file could not be opened.");
        await RunTransferAsync("Uploading " + _selectedLocal.Name, async (progress, token) =>
            await _ftp.UploadFileAsync(input, target, false, 0, progress, token));
        await LoadRemoteAsync();
    }

    private async Task DownloadSelectedAsync()
    {
        if (_selectedRemote is null || _selectedRemote.IsDirectory) { _status.Text = "Select an FTP file to download."; return; }
        if (_localDirectory is null) { await ChooseLocalFolderAsync(); if (_localDirectory is null) return; }
        if (_ftp is null) return;
        DocumentFile? existing = _localDirectory.FindFile(_selectedRemote.Name);
        if (existing is not null)
        {
            string? action = await DisplayActionSheetAsync("File already exists", "Cancel", null, "Replace");
            if (action != "Replace") return;
            existing.Delete();
        }
        DocumentFile? outputFile = _localDirectory.CreateFile("application/octet-stream", _selectedRemote.Name);
        if (outputFile is null) throw new IOException("The phone folder did not allow creating this file.");
        Android.Net.Uri? outputUri = outputFile.Uri;
        using Stream? output = outputUri is null ? null : Android.App.Application.Context.ContentResolver?.OpenOutputStream(outputUri, "wt");
        if (output is null) throw new IOException("The phone destination could not be opened.");
        try
        {
            await RunTransferAsync("Downloading " + _selectedRemote.Name, async (progress, token) =>
                await _ftp.DownloadFileAsync(_selectedRemote.FullPath, output, 0, progress, token));
        }
        catch { outputFile.Delete(); throw; }
        await LoadLocalAsync();
    }

    private async Task RunTransferAsync(string title, Func<IProgress<FtpProgress>, CancellationToken, Task> action)
    {
        _transfer?.Cancel();
        _transfer?.Dispose();
        _transfer = new CancellationTokenSource();
        _progress.Progress = 0;
        _progress.IsVisible = true;
        _cancel.IsVisible = true;
        _upload.IsEnabled = _download.IsEnabled = false;
        var progress = new Progress<FtpProgress>(value =>
        {
            _status.Text = value.TotalBytes is > 0 ? $"{title} · {FormatSize(value.BytesTransferred)} / {FormatSize(value.TotalBytes.Value)}" : $"{title} · {FormatSize(value.BytesTransferred)}";
            if (value.TotalBytes is > 0) _progress.Progress = Math.Clamp(value.BytesTransferred / (double)value.TotalBytes.Value, 0, 1);
        });
        try { await action(progress, _transfer.Token); _status.Text = title + " · complete"; }
        catch (OperationCanceledException) { _status.Text = "Transfer canceled"; }
        finally
        {
            _progress.IsVisible = false;
            _cancel.IsVisible = false;
            _upload.IsEnabled = _download.IsEnabled = true;
        }
    }

    private async Task CreateLocalFolderAsync()
    {
        if (_localDirectory is null) { await ChooseLocalFolderAsync(); return; }
        string name = await DisplayPromptAsync("New phone folder", "Folder name");
        if (!string.IsNullOrWhiteSpace(name)) { _localDirectory.CreateDirectory(name.Trim()); await LoadLocalAsync(); }
    }

    private async Task CreateRemoteFolderAsync()
    {
        if (_ftp is null || _connection?.AllowWrite != true) { _status.Text = "The FTP account is read-only."; return; }
        string name = await DisplayPromptAsync("New FTP folder", "Folder name");
        if (!string.IsNullOrWhiteSpace(name)) { await _ftp.CreateDirectoryAsync(CombineRemote(_remotePath, name.Trim())); await LoadRemoteAsync(); }
    }

    private async Task DeleteLocalAsync()
    {
        if (_selectedLocal is null) return;
        if (await DisplayAlertAsync("Delete from phone?", _selectedLocal.Name ?? "Selected item", "Delete", "Cancel")) { _selectedLocal.Delete(); await LoadLocalAsync(); }
    }

    private async Task DeleteRemoteAsync()
    {
        if (_selectedRemote is null || _ftp is null || _connection?.AllowWrite != true) return;
        if (!await DisplayAlertAsync("Delete from Workstation?", _selectedRemote.Name, "Delete", "Cancel")) return;
        if (_selectedRemote.IsDirectory) await _ftp.DeleteDirectoryAsync(_selectedRemote.FullPath); else await _ftp.DeleteFileAsync(_selectedRemote.FullPath);
        await LoadRemoteAsync();
    }

    private void ShowError(Exception ex) => MainThread.BeginInvokeOnMainThread(() => _status.Text = ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "Enable FTP permission for this user in Workstation.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } => "Start FTP for this user in Workstation.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } => "Restart the updated Workstation to enable mobile FTP transfer.",
        HttpRequestException => "The Workstation FTP connection is unavailable.",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "FTP transfer is unavailable." : ex.Message
    });

    private static View BuildPane(string title, Label path, Button back, Button add, Button delete, Layout files, string empty)
    {
        var heading = new Label { Text = title, TextColor = Color.FromArgb("#67E8F9"), FontSize = 11, FontAttributes = FontAttributes.Bold };
        var tools = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 3,
            Children = { back, path.Column(1), add.Column(2), delete.Column(3) }
        };
        files.Children.Add(EmptyLabel(empty));
        return new Border
        {
            Padding = 6,
            Stroke = Color.FromArgb("#334155"),
            BackgroundColor = Color.FromArgb("#111827"),
            Content = new Grid
            {
                RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
                RowSpacing = 5,
                Children = { heading.Row(0), tools.Row(1), new ScrollView { Content = files }.Row(2) }
            }
        };
    }

    private static Label PanePath(string value) => new() { Text = value, TextColor = Colors.White, FontSize = 11, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalTextAlignment = TextAlignment.Center };
    private static Label EmptyLabel(string value) => new() { Text = value, TextColor = Color.FromArgb("#64748B"), FontSize = 11, Margin = new Thickness(4, 8) };
    private static Button IconButton(string text, string help, string color = "#1E293B")
    {
        var button = new Button { Text = text, WidthRequest = 38, HeightRequest = 38, Padding = 0, FontSize = 18, TextColor = Colors.White, BackgroundColor = Color.FromArgb(color) };
        AutomationProperties.SetHelpText(button, help);
        return button;
    }
    private static Button FileButton(string name, bool directory, string detail) => new()
    {
        Text = (directory ? "▣ " : "□ ") + name + (string.IsNullOrWhiteSpace(detail) ? "" : "\n" + detail),
        FontSize = 11,
        Padding = new Thickness(5, 3),
        HorizontalOptions = LayoutOptions.Fill,
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#172033"),
        LineBreakMode = LineBreakMode.TailTruncation
    };
    private static void MarkSelected(Layout layout, Button selected)
    {
        foreach (View child in layout.Children) if (child is Button button) button.BackgroundColor = Color.FromArgb("#172033");
        selected.BackgroundColor = Color.FromArgb("#1D4ED8");
    }
    private static string CombineRemote(string parent, string name) => (parent.TrimEnd('/') + "/" + name.Trim('/')).Replace("//", "/");
    private static string ParentRemotePath(string path) { string value = path.TrimEnd('/'); int split = value.LastIndexOf('/'); return split <= 0 ? "/" : value[..split]; }
    private static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.#} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
