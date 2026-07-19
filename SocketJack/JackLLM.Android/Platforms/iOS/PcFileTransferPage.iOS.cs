using JackLLM.Mobile.Models;
using JackLLM.Mobile.Services;
using SocketJack.Net;

namespace JackLLM.Mobile.Pages;

/// <summary>
/// iOS file transfer surface. Apple document providers don't expose Android's
/// persistent folder-tree API, so uploads use the system document picker and
/// downloads use the system share/save sheet.
/// </summary>
public sealed class PcFileTransferPage : ContentPage
{
    private readonly JackLlmClient _client;
    private readonly Label _status = new() { Text = "FTP is required", TextColor = Color.FromArgb("#CBD5E1") };
    private readonly Label _selectedLocalLabel = PanePath("Choose a document");
    private readonly Label _remotePathLabel = PanePath("/");
    private readonly VerticalStackLayout _remoteFiles = new() { Spacing = 5 };
    private readonly ProgressBar _progress = new() { ProgressColor = Color.FromArgb("#38BDF8"), IsVisible = false };
    private readonly Button _upload;
    private readonly Button _download;
    private readonly Button _cancel;
    private readonly RefreshView _refreshView;
    private SocketJack.Net.FtpClient? _ftp;
    private PcAccessFtpConnection? _connection;
    private FileResult? _selectedLocal;
    private FtpListItem? _selectedRemote;
    private string _remotePath = "/";
    private CancellationTokenSource? _transfer;

    public PcFileTransferPage(JackLlmClient client)
    {
        _client = client;
        Title = "File Transfer";
        BackgroundColor = Color.FromArgb("#0B1020");

        var chooseFile = IconButton("＋", "Choose an iPhone or iPad document");
        chooseFile.Clicked += async (_, _) => await RunUiAsync(ChooseLocalFileAsync);
        var refresh = IconButton("↻", "Refresh Workstation folder");
        refresh.Clicked += async (_, _) => await RunUiAsync(LoadRemoteAsync);
        _cancel = IconButton("■", "Cancel transfer", "#7F1D1D");
        _cancel.IsVisible = false;
        _cancel.Clicked += (_, _) => _transfer?.Cancel();

        _upload = IconButton("→", "Upload selected Apple document", "#075985");
        _download = IconButton("←", "Download selected FTP file", "#166534");
        _upload.Clicked += async (_, _) => await RunUiAsync(UploadSelectedAsync);
        _download.Clicked += async (_, _) => await RunUiAsync(DownloadSelectedAsync);

        var remoteBack = IconButton("‹", "FTP parent folder");
        remoteBack.Clicked += async (_, _) => await RunUiAsync(async () =>
        {
            _remotePath = ParentRemotePath(_remotePath);
            await LoadRemoteAsync();
        });
        var remoteNew = IconButton("+", "New FTP folder");
        remoteNew.Clicked += async (_, _) => await RunUiAsync(CreateRemoteFolderAsync);
        var remoteDelete = IconButton("⌫", "Delete selected FTP item", "#7F1D1D");
        remoteDelete.Clicked += async (_, _) => await RunUiAsync(DeleteRemoteAsync);

        var header = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 6,
            Children = { _status, chooseFile.Column(1), refresh.Column(2), _cancel.Column(3) }
        };
        var localPane = new Border
        {
            Padding = 10,
            Stroke = Color.FromArgb("#334155"),
            BackgroundColor = Color.FromArgb("#111827"),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label { Text = "APPLE FILES", TextColor = Color.FromArgb("#67E8F9"), FontSize = 11, FontAttributes = FontAttributes.Bold },
                    _selectedLocalLabel,
                    new Label { Text = "Choose a file for upload. Downloads open the iOS share sheet so you can save them to Files, iCloud Drive, or another provider.", TextColor = Color.FromArgb("#94A3B8"), FontSize = 11 }
                }
            }
        };
        var remotePane = BuildRemotePane(remoteBack, remoteNew, remoteDelete);
        var arrows = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 10, Children = { _upload, _download } };
        var panes = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star) },
            ColumnSpacing = 6,
            Children = { localPane.Column(0), arrows.Column(1), remotePane.Column(2) }
        };
        var pageContent = new Grid
        {
            Padding = 8,
            RowSpacing = 7,
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
            Children = { header.Row(0), _progress.Row(1), panes.Row(2) }
        };
        _refreshView = new RefreshView { Content = pageContent, RefreshColor = Color.FromArgb("#38BDF8") };
        _refreshView.Refreshing += async (_, _) =>
        {
            try { await RunUiAsync(LoadRemoteAsync); }
            finally { _refreshView.IsRefreshing = false; }
        };
        Content = _refreshView;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await ConnectFtpAsync(); await LoadRemoteAsync(); }
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

    private async Task ChooseLocalFileAsync()
    {
        _selectedLocal = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose a file to upload" });
        _selectedLocalLabel.Text = _selectedLocal?.FileName ?? "Choose a document";
    }

    private async Task LoadRemoteAsync()
    {
        if (_ftp?.IsConnected != true) await ConnectFtpAsync();
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
                else { _selectedRemote = item; MarkSelected(button); }
            };
            _remoteFiles.Children.Add(button);
        }
        if (entries.Count == 0) _remoteFiles.Children.Add(new Label { Text = "Empty folder", TextColor = Color.FromArgb("#64748B"), FontSize = 11 });
    }

    private async Task UploadSelectedAsync()
    {
        if (_selectedLocal is null) { _status.Text = "Choose an Apple Files document to upload."; return; }
        if (_connection?.AllowWrite != true) { _status.Text = "The FTP account is read-only."; return; }
        if (_ftp is null) return;
        using Stream input = await _selectedLocal.OpenReadAsync();
        string target = CombineRemote(_remotePath, _selectedLocal.FileName);
        await RunTransferAsync("Uploading " + _selectedLocal.FileName, async (progress, token) =>
            await _ftp.UploadFileAsync(input, target, false, 0, progress, token));
        await LoadRemoteAsync();
    }

    private async Task DownloadSelectedAsync()
    {
        if (_selectedRemote is null || _selectedRemote.IsDirectory) { _status.Text = "Select an FTP file to download."; return; }
        if (_ftp is null) return;
        string folder = Path.Combine(FileSystem.AppDataDirectory, "Transfers");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, SanitizeFileName(_selectedRemote.Name));
        await using (FileStream output = File.Create(path))
        {
            await RunTransferAsync("Downloading " + _selectedRemote.Name, async (progress, token) =>
                await _ftp.DownloadFileAsync(_selectedRemote.FullPath, output, 0, progress, token));
        }
        await Share.Default.RequestAsync(new ShareFileRequest("Save or share " + _selectedRemote.Name, new ShareFile(path)));
    }

    private async Task RunTransferAsync(string title, Func<IProgress<FtpProgress>, CancellationToken, Task> action)
    {
        _transfer?.Cancel();
        _transfer?.Dispose();
        _transfer = new CancellationTokenSource();
        _progress.Progress = 0;
        _progress.IsVisible = _cancel.IsVisible = true;
        _upload.IsEnabled = _download.IsEnabled = false;
        var progress = new Progress<FtpProgress>(value =>
        {
            _status.Text = value.TotalBytes is > 0
                ? $"{title} · {FormatSize(value.BytesTransferred)} / {FormatSize(value.TotalBytes.Value)}"
                : $"{title} · {FormatSize(value.BytesTransferred)}";
            if (value.TotalBytes is > 0) _progress.Progress = Math.Clamp(value.BytesTransferred / (double)value.TotalBytes.Value, 0, 1);
        });
        try { await action(progress, _transfer.Token); _status.Text = title + " · complete"; }
        finally
        {
            _progress.IsVisible = _cancel.IsVisible = false;
            _upload.IsEnabled = _download.IsEnabled = true;
        }
    }

    private async Task CreateRemoteFolderAsync()
    {
        if (_ftp is null || _connection?.AllowWrite != true) { _status.Text = "The FTP account is read-only."; return; }
        string name = await DisplayPromptAsync("New FTP folder", "Folder name");
        if (!string.IsNullOrWhiteSpace(name)) { await _ftp.CreateDirectoryAsync(CombineRemote(_remotePath, name.Trim())); await LoadRemoteAsync(); }
    }

    private async Task DeleteRemoteAsync()
    {
        if (_selectedRemote is null || _ftp is null || _connection?.AllowWrite != true) return;
        if (!await DisplayAlertAsync("Delete from Workstation?", _selectedRemote.Name, "Delete", "Cancel")) return;
        if (_selectedRemote.IsDirectory) await _ftp.DeleteDirectoryAsync(_selectedRemote.FullPath); else await _ftp.DeleteFileAsync(_selectedRemote.FullPath);
        await LoadRemoteAsync();
    }

    private async Task RunUiAsync(Func<Task> operation)
    {
        try { await operation(); }
        catch (OperationCanceledException) { _status.Text = "Transfer canceled"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private Border BuildRemotePane(Button back, Button add, Button delete)
    {
        var tools = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 3,
            Children = { back, _remotePathLabel.Column(1), add.Column(2), delete.Column(3) }
        };
        return new Border
        {
            Padding = 6,
            Stroke = Color.FromArgb("#334155"),
            BackgroundColor = Color.FromArgb("#111827"),
            Content = new Grid
            {
                RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Star) },
                RowSpacing = 5,
                Children =
                {
                    new Label { Text = "WORKSTATION FTP", TextColor = Color.FromArgb("#67E8F9"), FontSize = 11, FontAttributes = FontAttributes.Bold }.Row(0),
                    tools.Row(1),
                    new ScrollView { Content = _remoteFiles }.Row(2)
                }
            }
        };
    }

    private void ShowError(Exception ex) => MainThread.BeginInvokeOnMainThread(() => _status.Text = ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } => "Enable FTP permission for this user in Workstation.",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Conflict } => "Start FTP for this user in Workstation.",
        HttpRequestException => "The Workstation FTP connection is unavailable.",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "FTP transfer is unavailable." : ex.Message
    });

    private static Label PanePath(string value) => new() { Text = value, TextColor = Colors.White, FontSize = 11, LineBreakMode = LineBreakMode.MiddleTruncation, VerticalTextAlignment = TextAlignment.Center };
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
    private void MarkSelected(Button selected)
    {
        foreach (View child in _remoteFiles.Children) if (child is Button button) button.BackgroundColor = Color.FromArgb("#172033");
        selected.BackgroundColor = Color.FromArgb("#1D4ED8");
    }
    private static string SanitizeFileName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(name) ? "download.bin" : name;
    }
    private static string CombineRemote(string parent, string name) => (parent.TrimEnd('/') + "/" + name.Trim('/')).Replace("//", "/");
    private static string ParentRemotePath(string path) { string value = path.TrimEnd('/'); int split = value.LastIndexOf('/'); return split <= 0 ? "/" : value[..split]; }
    private static string FormatSize(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.#} GB" : bytes >= 1048576 ? $"{bytes / 1048576d:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024d:0.#} KB" : $"{bytes} B";
}
