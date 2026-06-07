using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LlmRuntime;
using Microsoft.Web.WebView2.Core;

namespace LlmRuntime.Wpf;

public partial class HuggingFaceModelDownloaderControl : UserControl, IDisposable
{
    private const string HuggingFaceModelsUrl = "https://huggingface.co/models?library=gguf&pipeline_tag=text-generation&sort=trending";
    private static readonly HttpClient ApiClient = new();
    private static readonly JsonSerializerOptions BrowserJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ModelRepositoryScanner RepositoryScanner = new(ApiClient);
    private static readonly TimeSpan IdealModelCacheDuration = TimeSpan.FromMinutes(20);

    private readonly HuggingFaceIdealModelScanner _idealModelScanner = new(ApiClient);
    private readonly Dictionary<string, IdealModelCategoryCacheEntry> _idealModelCache = new(StringComparer.OrdinalIgnoreCase);
    private ModelDownloadService? _downloadService;
    private ModelDownloadService? _completeModelDownloadService;
    private ModelConversionService? _conversionService;
    private bool _browserReady;
    private bool _externalBrowserMode;
    private string? _lastInjectedUrl;
    private readonly List<DownloadQueueItem> _downloadQueue = [];
    private readonly ObservableCollection<DownloadQueueViewItem> _downloadQueueItems = [];
    private DownloadQueueItem? _activeDownload;
    private DownloadQueueItem? _lastDownloadItem;
    private string? _lastDownloadUrl;
    private string? _lastDownloadedPath;
    private bool _downloadPaused;
    private double _activeProgressPercent = -1;
    private string _activeProgressDetail = "";
    private string _lastNavigatedUrl = HuggingFaceModelsUrl;
    private bool _disposed;

    public HuggingFaceModelDownloaderControl()
    {
        InitializeComponent();
        ModelsDirectory = Path.Combine(Environment.CurrentDirectory, "Models");
        CompleteModelsDirectory = Path.Combine(Environment.CurrentDirectory, "CompleteModels");
        DownloadQueueListBox.ItemsSource = _downloadQueueItems;
        RefreshDownloadQueue();
        Loaded += async (_, _) => await InitializeBrowserAsync();
    }

    private bool TryBeginOnUi(Action action)
    {
        if (action == null || _disposed)
            return false;

        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return false;

            if (Dispatcher.CheckAccess())
            {
                action();
                return true;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                try { action(); } catch { }
            }), DispatcherPriority.Background);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBeginOnUi(Func<Task> action)
    {
        if (action == null || _disposed)
            return false;

        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return false;

            if (Dispatcher.CheckAccess())
            {
                _ = RunUiAsyncAction(action);
                return true;
            }

            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                    return;

                await RunUiAsyncAction(action).ConfigureAwait(true);
            }), DispatcherPriority.Background);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task RunUiAsyncAction(Func<Task> action)
    {
        try { await action().ConfigureAwait(true); } catch { }
    }

    public string ModelsDirectory { get; set; }

    public string CompleteModelsDirectory { get; set; }

    public event Action<string>? ModelDownloaded;

    public event Action<string>? ModelLoadRequested;

    public event Action<DownloadProgress>? DownloadProgressChanged;

    public event Action<string>? StatusChanged;

    public void QueueModelCandidate(
        ModelFileCandidate candidate,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? taskOverride = null)
    {
        if (candidate == null)
            return;

        string action = candidate.Action ?? "";
        string task = string.IsNullOrWhiteSpace(candidate.Task) ? taskOverride ?? "" : candidate.Task;
        if (string.Equals(action, "convert_onnx", StringComparison.OrdinalIgnoreCase))
        {
            StartConversion(candidate.Repository, candidate.Revision, candidate.SourcePaths);
            return;
        }

        if (!candidate.CanDownload)
        {
            SetStatus("Model candidate is not downloadable: " + (string.IsNullOrWhiteSpace(candidate.Reason) ? candidate.Repository : candidate.Reason));
            return;
        }

        if (string.Equals(action, "download_pytorch_bundle", StringComparison.OrdinalIgnoreCase) ||
            candidate.Format == ModelFileFormat.Pytorch)
        {
            StartBundleDownload(
                candidate.Repository,
                candidate.Revision,
                candidate.SourcePaths,
                candidate.TargetDirectoryName,
                task,
                candidate.SizeBytes,
                candidate.BaseModel,
                candidate.BaseModels,
                candidate.AdapterType,
                metadata,
                bearerToken);
            return;
        }

        string sourcePath = candidate.Path;
        if (sourcePath.StartsWith("Base model:", StringComparison.OrdinalIgnoreCase))
            sourcePath = "";
        if (string.IsNullOrWhiteSpace(sourcePath))
            sourcePath = candidate.SourcePaths.FirstOrDefault() ?? candidate.FileName;
        if (string.IsNullOrWhiteSpace(candidate.Repository) || string.IsNullOrWhiteSpace(sourcePath))
        {
            SetStatus("Model candidate did not include a repository and file path.");
            return;
        }

        string revision = string.IsNullOrWhiteSpace(candidate.Revision) ? "main" : candidate.Revision;
        string url = $"https://huggingface.co/{candidate.Repository}/resolve/{Uri.EscapeDataString(revision)}/{sourcePath}?download=true";
        StartDownload(url, candidate.Repository, sourcePath, candidate.Format, revision, task, candidate.TargetDirectoryName, bearerToken, metadata);
    }

    public async Task InitializeBrowserAsync()
    {
        if (_browserReady)
            return;

        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(CompleteModelsDirectory);
        _downloadService = new ModelDownloadService(ModelsDirectory);
        _downloadService.ProgressChanged += OnDownloadProgress;
        _downloadService.DownloadCompleted += OnDownloadCompleted;
        _downloadService.DownloadFailed += OnDownloadFailed;
        _completeModelDownloadService = new ModelDownloadService(CompleteModelsDirectory);
        _completeModelDownloadService.ProgressChanged += OnDownloadProgress;
        _completeModelDownloadService.DownloadCompleted += OnDownloadCompleted;
        _completeModelDownloadService.DownloadFailed += OnDownloadFailed;
        _conversionService = new ModelConversionService(CompleteModelsDirectory);
        _conversionService.JobChanged += OnConversionJobChanged;

        if (ShouldUseExternalBrowserMode())
        {
            DisableEmbeddedBrowser("Embedded Hugging Face browser is disabled for this Linux/Wine build.");
            return;
        }

        try
        {
            await Browser.EnsureCoreWebView2Async();
        }
        catch (Exception ex) when (IsWebViewRuntimeUnavailable(ex))
        {
            DisableEmbeddedBrowser("Embedded Hugging Face browser is unavailable: " + ex.Message);
            return;
        }

        _browserReady = true;
        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
        Browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
        Browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
        Browser.CoreWebView2.DownloadStarting += OnDownloadStarting;
        Navigate(ReadSavedBrowserUrl());
    }

    private static bool ShouldUseExternalBrowserMode()
    {
        string forced = Environment.GetEnvironmentVariable("JACKLLM_EXTERNAL_BROWSER") ?? "";
        if (forced.Equals("1", StringComparison.OrdinalIgnoreCase) ||
            forced.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            forced.Equals("on", StringComparison.OrdinalIgnoreCase))
            return true;

        return RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || IsWineRuntime();
    }

    private void DisableEmbeddedBrowser(string status)
    {
        _browserReady = false;
        _externalBrowserMode = true;
        BrowserHost.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;
        ExternalBrowserFallbackPanel.Visibility = Visibility.Visible;
        AddressBox.IsEnabled = true;
        BackButton.IsEnabled = false;
        ForwardButton.IsEnabled = false;
        RefreshButton.IsEnabled = true;
        HomeButton.IsEnabled = true;
        RefreshButton.Content = "Open";
        HomeButton.Content = "HF";
        string savedUrl = ReadSavedBrowserUrl();
        _lastNavigatedUrl = savedUrl;
        AddressBox.Text = savedUrl;
        SetStatus(status + " External Linux browser fallback is enabled.");
    }

    private static bool IsWebViewRuntimeUnavailable(Exception ex)
    {
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            if (current is WebView2RuntimeNotFoundException ||
                current is FileNotFoundException ||
                current is COMException)
            {
                return true;
            }
        }

        return false;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        AddressBox.Text = e.Uri;
        _lastInjectedUrl = null;

        if (ModelDownloadService.IsModelUrl(e.Uri))
        {
            e.Cancel = true;
            StartDownload(e.Uri);
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess || Browser.CoreWebView2 == null)
            return;

        AddressBox.Text = Browser.CoreWebView2.Source;
        _lastNavigatedUrl = Browser.CoreWebView2.Source;
        SaveCurrentPage();
        await InjectIdealModelsForCurrentPageAsync();
        await InjectDownloadPanelForCurrentPageAsync();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (ModelDownloadService.IsModelUrl(e.Uri))
            StartDownload(e.Uri);
        else
            Navigate(e.Uri);
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        if (!ModelDownloadService.IsModelUrl(e.DownloadOperation.Uri))
            return;

        e.Cancel = true;
        StartDownload(e.DownloadOperation.Uri);
    }

    private async Task InjectIdealModelsForCurrentPageAsync()
    {
        if (!_browserReady || Browser.CoreWebView2 == null)
            return;

        string url = Browser.CoreWebView2.Source;
        IReadOnlyList<string> categoryIds = GetIdealModelCategoryIdsForUrl(url);
        if (categoryIds.Count == 0)
            return;

        string title = BuildIdealModelsTitle(categoryIds);
        string loadingPayload = JsonSerializer.Serialize(new
        {
            title,
            loading = true,
            categories = categoryIds.Select(id => BuildIdealModelCategoryPlaceholder(id)).ToArray()
        }, BrowserJsonOptions);
        await Browser.CoreWebView2.ExecuteScriptAsync(BuildIdealModelsInjectionScript(loadingPayload)).ConfigureAwait(true);

        try
        {
            IReadOnlyList<HuggingFaceIdealModelCategoryResult> categories = await GetIdealModelCategoryResultsAsync(categoryIds).ConfigureAwait(true);
            string payload = JsonSerializer.Serialize(new
            {
                title,
                loading = false,
                categories
            }, BrowserJsonOptions);
            await Browser.CoreWebView2.ExecuteScriptAsync(BuildIdealModelsInjectionScript(payload)).ConfigureAwait(true);

            int count = categories.Sum(category => category.Models.Count);
            SetStatus("Showing " + count.ToString(CultureInfo.InvariantCulture) + " ideal model suggestion(s) for this Hugging Face search.");
        }
        catch (Exception ex)
        {
            string payload = JsonSerializer.Serialize(new
            {
                title,
                loading = false,
                error = ex.Message,
                categories = Array.Empty<HuggingFaceIdealModelCategoryResult>()
            }, BrowserJsonOptions);
            await Browser.CoreWebView2.ExecuteScriptAsync(BuildIdealModelsInjectionScript(payload)).ConfigureAwait(true);
            SetStatus("Ideal model scan failed: " + ex.Message);
        }
    }

    private async Task<IReadOnlyList<HuggingFaceIdealModelCategoryResult>> GetIdealModelCategoryResultsAsync(IReadOnlyList<string> categoryIds)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string[] missing = categoryIds
            .Where(id => !_idealModelCache.TryGetValue(id, out IdealModelCategoryCacheEntry? entry) ||
                         now - entry.LoadedAt > IdealModelCacheDuration)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missing.Length > 0)
        {
            HuggingFaceAuthContext auth = await GetHuggingFaceAuthAsync().ConfigureAwait(true);
            IReadOnlyList<HuggingFaceIdealModelCategoryResult> fresh =
                await _idealModelScanner.ScanAsync(missing, 5, auth.Cookies, auth.BearerToken).ConfigureAwait(true);
            DateTimeOffset loadedAt = DateTimeOffset.UtcNow;
            foreach (HuggingFaceIdealModelCategoryResult result in fresh)
                _idealModelCache[result.Id] = new IdealModelCategoryCacheEntry(result, loadedAt);
        }

        return categoryIds
            .Select(id => _idealModelCache.TryGetValue(id, out IdealModelCategoryCacheEntry? entry)
                ? entry.Result
                : BuildIdealModelCategoryPlaceholder(id, "No suggestions loaded."))
            .ToArray();
    }

    private static HuggingFaceIdealModelCategoryResult BuildIdealModelCategoryPlaceholder(string id, string error = "")
    {
        HuggingFaceIdealModelCategory? category = HuggingFaceIdealModelScanner.DefaultCategories
            .FirstOrDefault(candidate => candidate.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        return new HuggingFaceIdealModelCategoryResult
        {
            Id = id,
            Label = category?.Label ?? id,
            Description = category?.Description ?? "",
            Models = [],
            Error = error
        };
    }

    private static string BuildIdealModelsTitle(IReadOnlyList<string> categoryIds)
    {
        if (categoryIds.Count == 1)
        {
            HuggingFaceIdealModelCategoryResult category = BuildIdealModelCategoryPlaceholder(categoryIds[0]);
            return "Ideal " + category.Label + " Models";
        }

        return "Ideal Models";
    }

    private static IReadOnlyList<string> GetIdealModelCategoryIdsForUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            !uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Trim('/').Equals("models", StringComparison.OrdinalIgnoreCase))
            return [];

        string text = BuildHuggingFaceModelSearchText(uri);
        var ids = new List<string>();
        AddCategoryIf(ids, "video", ContainsAny(text, "text-to-video", "image-to-video", "video-to-video", "video-generation", "video"));
        AddCategoryIf(ids, "vision", ContainsAny(text, "image-text-to-text", "visual-question-answering", "image-to-text", "document-question-answering", "visual"));
        AddCategoryIf(ids, "image", ContainsAny(text, "text-to-image", "image-to-image", "unconditional-image-generation", "image-generation", "image-classification", "stable-diffusion", "diffusers", "controlnet"));
        AddCategoryIf(ids, "audio", ContainsAny(text, "text-to-audio", "audio-to-audio", "text-to-speech", "automatic-speech-recognition", "audio-classification", "audio", "speech", "voice", "musicgen", "tts", "whisper"));
        AddCategoryIf(ids, "embedding", ContainsAny(text, "feature-extraction", "sentence-similarity", "embedding", "embed"));
        AddCategoryIf(ids, "text", ContainsAny(text, "text-generation", "text2text-generation", "conversational", "gguf", "llm", "language-model"));

        if (ids.Count == 0)
            ids.AddRange(HuggingFaceIdealModelScanner.DefaultCategories.Select(category => category.Id));

        return ids.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BuildHuggingFaceModelSearchText(Uri uri)
    {
        var parts = new List<string>();
        string query = uri.Query.TrimStart('?');
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] tokens = pair.Split('=', 2);
            parts.Add(Uri.UnescapeDataString(tokens[0]).Replace('_', '-'));
            if (tokens.Length > 1)
                parts.Add(Uri.UnescapeDataString(tokens[1]).Replace('_', '-'));
        }

        return string.Join(" ", parts);
    }

    private static void AddCategoryIf(List<string> ids, string id, bool condition)
    {
        if (condition && !ids.Contains(id, StringComparer.OrdinalIgnoreCase))
            ids.Add(id);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string BuildIdealModelsInjectionScript(string payloadJson)
    {
        string escapedPayload = JsonSerializer.Serialize(payloadJson);
        return """
(() => {
  const payload = JSON.parse(__PAYLOAD__);
  const old = document.getElementById('llm-runtime-ideal-models-panel');
  if (old) old.remove();

  const categories = Array.isArray(payload.categories) ? payload.categories : [];
  const esc = (value) => String(value ?? '').replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;').replaceAll('>', '&gt;');
  const fmt = (value) => {
    const number = Number(value || 0);
    if (number >= 1000000000) return (number / 1000000000).toFixed(1).replace(/\.0$/, '') + 'B';
    if (number >= 1000000) return (number / 1000000).toFixed(1).replace(/\.0$/, '') + 'M';
    if (number >= 1000) return (number / 1000).toFixed(1).replace(/\.0$/, '') + 'K';
    return String(Math.max(0, number));
  };
  const hasModels = categories.some(category => Array.isArray(category.models) && category.models.length);
  if (!payload.loading && !payload.error && !hasModels && !categories.length) return;

  const card = (model) => {
    const tags = Array.isArray(model.tags) ? model.tags.slice(0, 4).filter(Boolean) : [];
    const tagHtml = tags.map(tag => `<span style="border:1px solid #30363d;border-radius:999px;padding:2px 7px;color:#8b949e;font-size:11px">${esc(tag)}</span>`).join('');
    const meta = [model.pipelineTag || '', model.libraryName || '', fmt(model.downloads) + ' downloads', model.likes ? fmt(model.likes) + ' likes' : ''].filter(Boolean).join(' | ');
    return `<a href="${esc(model.url)}" target="_self" data-llm-runtime-ideal-model="true" style="display:block;min-width:220px;max-width:320px;flex:1 1 240px;text-decoration:none;border:1px solid #30363d;border-radius:10px;background:#0d1117;color:#c9d1d9;padding:12px;box-shadow:0 8px 20px rgba(0,0,0,.16)">
      <div style="font-weight:700;color:#f0f6fc;margin-bottom:5px;overflow-wrap:anywhere">${esc(model.modelId)}</div>
      <div style="font-size:12px;color:#8b949e;margin-bottom:8px">${esc(meta)}</div>
      <div style="font-size:12px;color:#c9d1d9;margin-bottom:10px;line-height:1.35">${esc(model.reason || 'Open the model card and scan downloadable files.')}</div>
      <div style="display:flex;flex-wrap:wrap;gap:5px">${tagHtml}</div>
    </a>`;
  };
  const categoryBlocks = categories.map(category => {
    const models = Array.isArray(category.models) ? category.models : [];
    const body = payload.loading
      ? '<div style="color:#8b949e;font-size:13px">Scanning Hugging Face...</div>'
      : models.length
        ? `<div style="display:flex;gap:10px;overflow-x:auto;padding-bottom:2px">${models.map(card).join('')}</div>`
        : `<div style="color:#d29922;font-size:13px">${esc(category.error || 'No ideal models found for this category.')}</div>`;
    return `<section style="margin-top:12px">
      <div style="display:flex;align-items:baseline;justify-content:space-between;gap:12px;margin-bottom:8px">
        <div style="font-weight:700;color:#f0f6fc">${esc(category.label || 'Models')}</div>
        <div style="font-size:12px;color:#8b949e">${esc(category.description || '')}</div>
      </div>
      ${body}
    </section>`;
  }).join('');

  const panel = document.createElement('section');
  panel.id = 'llm-runtime-ideal-models-panel';
  panel.style.cssText = 'margin:16px auto 18px;max-width:1120px;border:1px solid #30363d;border-radius:12px;background:#161b22;color:#c9d1d9;font-family:Inter,Arial,sans-serif;padding:14px 16px;box-shadow:0 12px 28px rgba(0,0,0,.18);';
  panel.innerHTML = `<div style="display:flex;justify-content:space-between;gap:12px;align-items:center">
      <div>
        <div style="font-size:16px;font-weight:800;color:#f0f6fc">${esc(payload.title || 'Ideal Models')}</div>
        <div style="font-size:12px;color:#8b949e;margin-top:3px">SocketJack suggestions for the current Hugging Face model search. Open a card to scan and download from that repository.</div>
      </div>
      ${payload.error ? `<div style="color:#f85149;font-size:12px;text-align:right">${esc(payload.error)}</div>` : ''}
    </div>
    ${categoryBlocks}`;

  panel.querySelectorAll('a[data-llm-runtime-ideal-model]').forEach(link => {
    link.addEventListener('click', event => {
      event.preventDefault();
      window.location.href = link.href;
    });
  });

  const main = document.querySelector('main') || document.body;
  const firstModelLink = Array.from(main.querySelectorAll('a[href]')).find(anchor => {
    try {
      const target = new URL(anchor.href, window.location.origin);
      const parts = target.pathname.split('/').filter(Boolean);
      return target.hostname === 'huggingface.co' && parts.length === 2 && !['models', 'datasets', 'spaces', 'docs', 'settings'].includes(parts[0]);
    } catch {
      return false;
    }
  });
  const firstModelBlock = firstModelLink ? (firstModelLink.closest('article,li') || firstModelLink.parentElement) : null;
  const resultList = firstModelBlock ? firstModelBlock.parentElement : null;
  if (resultList && resultList.parentElement && resultList !== main)
    resultList.parentElement.insertBefore(panel, resultList);
  else
    main.prepend(panel);
})();
""".Replace("__PAYLOAD__", escapedPayload);
    }

    private async Task InjectDownloadPanelForCurrentPageAsync()
    {
        if (!_browserReady || Browser.CoreWebView2 == null)
            return;

        string url = Browser.CoreWebView2.Source;
        if (string.Equals(_lastInjectedUrl, url, StringComparison.OrdinalIgnoreCase))
            return;

        if (!TryParseHuggingFaceRepo(url, out string owner, out string repo))
            return;

        _lastInjectedUrl = url;
        SetStatus($"Scanning {owner}/{repo}...");

        try
        {
            HuggingFaceAuthContext auth = await GetHuggingFaceAuthAsync().ConfigureAwait(true);
            var candidates = await GetCandidatesAsync(owner, repo, auth);
            var modelFit = CreateFitSnapshot(ModelsDirectory);
            var completeModelFit = CreateFitSnapshot(CompleteModelsDirectory);
            foreach (var candidate in candidates)
            {
                bool useCompleteModels = UsesCompleteModelsDirectory(candidate.Format, candidate.Task);
                string targetDirectory = useCompleteModels ? CompleteModelsDirectory : ModelsDirectory;
                candidate.ApplyFit(useCompleteModels ? completeModelFit : modelFit, targetDirectory);
            }

            string payload = JsonSerializer.Serialize(new
            {
                repo = $"{owner}/{repo}",
                revision = "main",
                sharedVideoMemoryBytes = modelFit.SharedVideoMemoryBytes,
                driveFreeBytes = modelFit.DriveFreeBytes,
                modelsDirectory = ModelsDirectory,
                completeModelsDirectory = CompleteModelsDirectory,
                files = candidates
            }, BrowserJsonOptions);

            string script = BuildInjectionScript(payload);
            await Browser.CoreWebView2.ExecuteScriptAsync(script);
            SetStatus($"Found {candidates.Count} model file(s).");
        }
        catch (Exception ex)
        {
            SetStatus($"Model scan failed: {ex.Message}");
        }
    }

    private static async Task<List<ModelFileCandidate>> GetCandidatesAsync(string owner, string repo, HuggingFaceAuthContext auth)
    {
        var scan = await RepositoryScanner.ScanHuggingFaceAsync(owner, repo, cookies: auth.Cookies, bearerToken: auth.BearerToken);
        var candidates = scan.Candidates.ToList();
        candidates.AddRange(await GetBaseModelCandidatesAsync(scan, candidates, auth));
        return candidates;
    }

    private static async Task<IReadOnlyList<ModelFileCandidate>> GetBaseModelCandidatesAsync(
        ModelRepositoryScanResult adapterScan,
        IReadOnlyList<ModelFileCandidate> candidates,
        HuggingFaceAuthContext auth)
    {
        var baseCandidates = new List<ModelFileCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelFileCandidate adapterCandidate in candidates)
        {
            bool hasBaseModelReference = adapterCandidate.BaseModels.Count > 0 ||
                                         !string.IsNullOrWhiteSpace(adapterCandidate.BaseModel);
            if (!hasBaseModelReference)
                continue;

            foreach (string baseModel in adapterCandidate.BaseModels.Prepend(adapterCandidate.BaseModel))
            {
                if (!TryParseHuggingFaceRepoReference(baseModel, out string baseOwner, out string baseRepo, out string baseRevision))
                    continue;

                string baseRepository = baseOwner + "/" + baseRepo;
                if (string.Equals(baseRepository, adapterScan.Repository, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!seen.Add(baseRepository + "@" + baseRevision))
                    continue;

                try
                {
                    var baseScan = await RepositoryScanner.ScanHuggingFaceAsync(baseOwner, baseRepo, baseRevision, auth.Cookies, auth.BearerToken);
                    ModelFileCandidate? baseDownload = SelectBaseModelDownloadCandidate(baseScan, adapterCandidate.Task);
                    if (baseDownload != null)
                        baseCandidates.Add(CreateBaseModelDownloadCandidate(baseDownload, adapterScan.Repository, adapterCandidate.Task));
                }
                catch
                {
                    baseCandidates.Add(CreateUnavailableBaseModelCandidate(baseRepository, baseRevision, adapterScan.Repository, adapterCandidate.Task));
                }
            }
        }

        return baseCandidates;
    }

    private static ModelFileCandidate? SelectBaseModelDownloadCandidate(ModelRepositoryScanResult baseScan, string adapterTask)
    {
        return baseScan.Candidates
            .Where(candidate => candidate.CanDownload)
            .OrderByDescending(candidate => string.Equals(candidate.Action, "download_pytorch_bundle", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(candidate => IsSameTask(candidate.Task, adapterTask))
            .ThenByDescending(candidate => candidate.UsesCompleteModelLayout())
            .ThenBy(candidate => candidate.SizeBytes)
            .FirstOrDefault();
    }

    private static ModelFileCandidate CreateBaseModelDownloadCandidate(ModelFileCandidate candidate, string adapterRepository, string adapterTask)
    {
        string effectiveTask = !string.IsNullOrWhiteSpace(adapterTask) && !IsSameTask(candidate.Task, adapterTask)
            ? adapterTask
            : string.IsNullOrWhiteSpace(candidate.Task) ? adapterTask : candidate.Task;
        string label = BuildGeneratorLabel(effectiveTask);
        var tags = candidate.Tags.Concat(["base-model"]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new ModelFileCandidate
        {
            Path = "Base model: " + candidate.Repository,
            FileName = candidate.FileName,
            Repository = candidate.Repository,
            Revision = candidate.Revision,
            SizeBytes = candidate.SizeBytes,
            EstimatedRequiredDriveBytes = candidate.EstimatedRequiredDriveBytes,
            Format = candidate.Format,
            Task = effectiveTask,
            TargetDirectoryName = candidate.TargetDirectoryName,
            ModelKindLabel = label,
            Quantization = candidate.Quantization,
            Tags = tags,
            SourcePaths = candidate.SourcePaths,
            Action = candidate.Action,
            ActionLabel = "Download Base " + label + " Model",
            CanDownload = candidate.CanDownload,
            CanConvert = candidate.CanConvert,
            Exists = candidate.Exists,
            Reason = "Base model required by " + adapterRepository + ".",
            BaseModel = candidate.BaseModel,
            BaseModels = candidate.BaseModels,
            AdapterType = candidate.AdapterType
        };
    }

    private static ModelFileCandidate CreateUnavailableBaseModelCandidate(string repository, string revision, string adapterRepository, string task)
    {
        string label = BuildGeneratorLabel(task);
        return new ModelFileCandidate
        {
            Path = "Base model: " + repository,
            FileName = repository.Replace('/', '_') + "-base-model",
            Repository = repository,
            Revision = string.IsNullOrWhiteSpace(revision) ? "main" : revision,
            Format = ModelFileFormat.Unknown,
            Task = task,
            TargetDirectoryName = repository + "/" + (string.IsNullOrWhiteSpace(revision) ? "main" : revision),
            ModelKindLabel = label,
            Tags = ["base-model", task],
            Action = "unsupported",
            ActionLabel = "Base unavailable",
            Reason = "Could not scan base model " + repository + " required by " + adapterRepository + "."
        };
    }

    private static bool TryParseHuggingFaceRepoReference(string value, out string owner, out string repo, out string revision)
    {
        owner = "";
        repo = "";
        revision = "main";
        value = (value ?? "").Trim().Trim('"', '\'').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(value))
            return false;

        const string huggingFacePrefix = "https://huggingface.co/";
        if (value.StartsWith(huggingFacePrefix, StringComparison.OrdinalIgnoreCase))
            value = value[huggingFacePrefix.Length..];
        else if (value.StartsWith("http://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            value = value["http://huggingface.co/".Length..];

        int queryIndex = value.IndexOfAny(['?', '#']);
        if (queryIndex >= 0)
            value = value[..queryIndex];

        int atIndex = value.LastIndexOf('@');
        if (atIndex > 0 && atIndex < value.Length - 1)
        {
            revision = value[(atIndex + 1)..].Trim('/');
            value = value[..atIndex];
        }

        string[] parts = value.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return false;

        owner = parts[0];
        repo = parts[1];
        if (parts.Length >= 4 && parts[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
            revision = parts[3];
        if (string.IsNullOrWhiteSpace(revision))
            revision = "main";

        return !owner.Equals("models", StringComparison.OrdinalIgnoreCase) &&
               !owner.Equals("datasets", StringComparison.OrdinalIgnoreCase) &&
               !owner.Equals("spaces", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameTask(string left, string right) =>
        string.Equals((left ?? "").Trim(), (right ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static string BuildGeneratorLabel(string task)
    {
        if (string.Equals(task, "video", StringComparison.OrdinalIgnoreCase))
            return "Video Generation";
        if (string.Equals(task, "audio", StringComparison.OrdinalIgnoreCase))
            return "Audio Generation";
        if (string.Equals(task, "image", StringComparison.OrdinalIgnoreCase))
            return "Image Generation";
        if (string.Equals(task, "embedding", StringComparison.OrdinalIgnoreCase))
            return "Embedding";
        return "Text Generation";
    }

    private static string BuildInjectionScript(string payloadJson)
    {
        string escapedPayload = JsonSerializer.Serialize(payloadJson);
        return """
(() => {
  const payload = JSON.parse(__PAYLOAD__);
  const old = document.getElementById('llm-runtime-download-panel');
  if (old) old.remove();

  const fmt = (bytes) => {
    if (!bytes || bytes < 0) return 'unknown';
    if (bytes < 1024) return bytes + ' B';
    if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
    if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1) + ' MB';
    return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB';
  };
  const esc = (value) => String(value ?? '').replaceAll('&', '&amp;').replaceAll('"', '&quot;').replaceAll('<', '&lt;');

  const panel = document.createElement('section');
  panel.id = 'llm-runtime-download-panel';
  panel.style.cssText = 'margin:16px auto;max-width:980px;border:1px solid #30363d;border-radius:10px;background:#0d1117;color:#c9d1d9;font-family:Inter,Arial,sans-serif;overflow:hidden;box-shadow:0 12px 28px rgba(0,0,0,.24);';
  const rows = payload.files.map(file => {
    const canAct = !!(file.canDownload || file.canConvert);
    const isWarning = !!file.isWarning;
    const disabled = canAct ? '' : 'disabled';
    const buttonText = file.actionLabel || (file.exists ? 'Already downloaded' : canAct ? 'Download' : 'Unsupported');
    const reason = file.reason || (canAct ? 'Fits current memory and drive limits.' : 'Unsupported.');
    const action = file.action || (file.canDownload ? 'download_gguf' : 'unsupported');
    const reasonColor = isWarning ? '#f0883e' : (canAct ? '#3fb950' : '#d29922');
    const buttonBorder = isWarning ? '#f0883e' : (canAct ? '#238636' : '#30363d');
    const buttonBackground = isWarning ? '#3d2b16' : (canAct ? '#238636' : '#21262d');
    const buttonColor = isWarning ? '#ffab70' : (canAct ? '#fff' : '#8b949e');
    const repo = file.repository || payload.repo;
    const revision = file.revision || payload.revision || 'main';
    const sourcePaths = JSON.stringify(file.sourcePaths || []);
    const baseModels = JSON.stringify(file.baseModels || []);
    const targetDirectoryName = file.targetDirectoryName || '';
    const task = file.task || '';
    const sizeBytes = file.sizeBytes || 0;
    const baseModel = file.baseModel || '';
    const adapterType = file.adapterType || '';
    return `<tr style="border-top:1px solid #21262d">
      <td style="padding:10px 14px;font-family:Consolas,monospace;font-size:12px">${esc(file.path)}</td>
      <td style="padding:10px 14px;white-space:nowrap">${esc(file.format || '')}</td>
      <td style="padding:10px 14px;white-space:nowrap">${esc(file.quantization || '')}</td>
      <td style="padding:10px 14px;white-space:nowrap">${esc((file.tags || []).join(', '))}</td>
      <td style="padding:10px 14px;white-space:nowrap;text-align:right">${fmt(file.sizeBytes)}</td>
      <td style="padding:10px 14px;color:${reasonColor}">${esc(reason)}</td>
      <td style="padding:10px 14px;text-align:right">
        <button ${disabled} data-action="${esc(action)}" data-repo="${esc(repo)}" data-revision="${esc(revision)}" data-path="${esc(file.path)}" data-format="${esc(file.format || '')}" data-source-paths="${esc(sourcePaths)}" data-target-directory-name="${esc(targetDirectoryName)}" data-task="${esc(task)}" data-size-bytes="${esc(sizeBytes)}" data-base-model="${esc(baseModel)}" data-base-models="${esc(baseModels)}" data-adapter-type="${esc(adapterType)}" style="border:1px solid ${buttonBorder};border-radius:6px;background:${buttonBackground};color:${buttonColor};padding:6px 12px;font-weight:600;cursor:${canAct ? 'pointer' : 'not-allowed'}">${esc(buttonText)}</button>
      </td>
    </tr>`;
  }).join('');

  panel.innerHTML = `<div style="display:flex;justify-content:space-between;gap:12px;align-items:center;padding:14px 16px;background:#161b22;border-bottom:1px solid #30363d">
      <div>
        <div style="font-weight:700;color:#f0f6fc">LlmRuntime downloads</div>
        <div style="font-size:12px;color:#8b949e">GGUF/ONNX files save to ${payload.modelsDirectory}. Complete PyTorch bundles save to ${payload.completeModelsDirectory}. Suggested models must fit shared video memory and free drive space.</div>
      </div>
      <div style="font-size:12px;color:#8b949e;text-align:right">Shared video memory: ${fmt(payload.sharedVideoMemoryBytes)}<br/>Drive free: ${fmt(payload.driveFreeBytes)}</div>
    </div>
    <table style="width:100%;border-collapse:collapse;font-size:13px"><thead><tr style="color:#8b949e"><th style="padding:8px 14px;text-align:left">File</th><th style="padding:8px 14px;text-align:left">Format</th><th style="padding:8px 14px;text-align:left">Quant</th><th style="padding:8px 14px;text-align:left">Tags</th><th style="padding:8px 14px;text-align:right">Size</th><th style="padding:8px 14px;text-align:left">Fit</th><th style="padding:8px 14px;text-align:right">Action</th></tr></thead><tbody>${rows || '<tr><td style="padding:14px">No GGUF, ONNX, or source tensor files found in this repository.</td></tr>'}</tbody></table>`;

  panel.querySelectorAll('button[data-path]').forEach(button => {
    button.addEventListener('click', () => {
      let sourcePaths = [];
      try { sourcePaths = JSON.parse(button.dataset.sourcePaths || '[]'); } catch {}
      let baseModels = [];
      try { baseModels = JSON.parse(button.dataset.baseModels || '[]'); } catch {}
      window.chrome.webview.postMessage(JSON.stringify({ action: button.dataset.action || 'download_gguf', repo: button.dataset.repo || payload.repo, revision: button.dataset.revision || payload.revision || 'main', path: button.dataset.path, format: button.dataset.format, sourcePaths, targetDirectoryName: button.dataset.targetDirectoryName || '', task: button.dataset.task || '', sizeBytes: Number(button.dataset.sizeBytes || 0), baseModel: button.dataset.baseModel || '', baseModels, adapterType: button.dataset.adapterType || '' }));
    });
  });

  const target = document.querySelector('main') || document.body;
  target.prepend(panel);
})();
""".Replace("__PAYLOAD__", escapedPayload);
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string raw = e.TryGetWebMessageAsString();
            using var document = JsonDocument.Parse(raw);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("action", out var actionElement))
                return;

            string action = actionElement.GetString() ?? "";
            string repo = root.GetProperty("repo").GetString() ?? "";
            string path = root.GetProperty("path").GetString() ?? "";
            string revision = root.TryGetProperty("revision", out var revisionElement) ? revisionElement.GetString() ?? "main" : "main";
            string rawFormat = root.TryGetProperty("format", out var formatElement) ? formatElement.GetString() ?? "" : "";
            string targetDirectoryName = root.TryGetProperty("targetDirectoryName", out var targetElement) ? targetElement.GetString() ?? "" : "";
            string task = root.TryGetProperty("task", out var taskElement) ? taskElement.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(path))
                return;

            if (string.Equals(action, "convert_onnx", StringComparison.OrdinalIgnoreCase))
            {
                var sourcePaths = root.TryGetProperty("sourcePaths", out var sourcePathsElement) && sourcePathsElement.ValueKind == JsonValueKind.Array
                    ? sourcePathsElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                    : Array.Empty<string>();
                StartConversion(repo, revision, sourcePaths);
                return;
            }

            if (string.Equals(action, "download_pytorch_bundle", StringComparison.OrdinalIgnoreCase))
            {
                var sourcePaths = root.TryGetProperty("sourcePaths", out var sourcePathsElement) && sourcePathsElement.ValueKind == JsonValueKind.Array
                    ? sourcePathsElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                    : Array.Empty<string>();
                long sizeBytes = root.TryGetProperty("sizeBytes", out var sizeElement) && sizeElement.TryGetInt64(out long parsedSize) ? parsedSize : 0;
                string baseModel = root.TryGetProperty("baseModel", out var baseModelElement) ? baseModelElement.GetString() ?? "" : "";
                var baseModels = root.TryGetProperty("baseModels", out var baseModelsElement) && baseModelsElement.ValueKind == JsonValueKind.Array
                    ? baseModelsElement.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray()
                    : Array.Empty<string>();
                string adapterType = root.TryGetProperty("adapterType", out var adapterTypeElement) ? adapterTypeElement.GetString() ?? "" : "";
                StartBundleDownload(repo, revision, sourcePaths, targetDirectoryName, task, sizeBytes, baseModel, baseModels, adapterType);
                return;
            }

            if (!action.StartsWith("download", StringComparison.OrdinalIgnoreCase))
                return;

            string url = $"https://huggingface.co/{repo}/resolve/{Uri.EscapeDataString(string.IsNullOrWhiteSpace(revision) ? "main" : revision)}/{path}?download=true";
            StartDownload(url, repo, path, TryParseModelFileFormat(rawFormat), revision, task, targetDirectoryName);
        }
        catch (Exception ex)
        {
            SetStatus($"Download request failed: {ex.Message}");
        }
    }

    private void StartDownload(
        string url,
        string? repo = null,
        string? sourcePath = null,
        ModelFileFormat? format = null,
        string? revision = null,
        string? task = null,
        string? targetDirectoryName = null,
        string? bearerToken = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (_downloadService == null || _completeModelDownloadService == null)
            return;

        ModelFileFormat modelFormat = format ?? DetectFormatFromUrl(url);
        string normalizedTask = task ?? "";
        bool useCompleteModels = UsesCompleteModelsDirectory(modelFormat, normalizedTask);
        string targetRoot = useCompleteModels ? CompleteModelsDirectory : ModelsDirectory;
        string fileName = BuildDownloadFileName(url, repo, sourcePath, modelFormat, revision, normalizedTask, targetDirectoryName, useCompleteModels);
        string finalPath = Path.Combine(targetRoot, fileName);
        if (File.Exists(finalPath))
        {
            SetStatus($"Already downloaded: {fileName}");
            UpdateHealthCard(finalPath);
            return;
        }

        var item = new DownloadQueueItem(
            CreateDownloadId(),
            url,
            fileName,
            repo ?? "",
            sourcePath ?? "",
            revision ?? "main",
            modelFormat,
            [],
            targetDirectoryName ?? "",
            normalizedTask,
            0,
            "",
            [],
            "",
            useCompleteModels,
            metadata ?? new Dictionary<string, string>(),
            bearerToken ?? "");
        _downloadQueue.Add(item);
        _lastDownloadItem = item;
        _lastDownloadUrl = url;
        _downloadPaused = false;
        RefreshDownloadQueue();
        TryStartNextDownload();
    }

    private void StartBundleDownload(
        string repo,
        string revision,
        IReadOnlyList<string> sourcePaths,
        string targetDirectoryName,
        string task,
        long sizeBytes,
        string baseModel,
        IReadOnlyList<string> baseModels,
        string adapterType,
        IReadOnlyDictionary<string, string>? metadata = null,
        string? bearerToken = null)
    {
        if (_completeModelDownloadService == null)
            return;

        if (sourcePaths.Count == 0)
        {
            SetStatus("No PyTorch model files were found to download.");
            return;
        }

        string relativeDirectory = string.IsNullOrWhiteSpace(targetDirectoryName)
            ? BuildBundleRelativeDirectory(repo, revision)
            : targetDirectoryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string manifestPath = Path.Combine(CompleteModelsDirectory, relativeDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            SetStatus("Already downloaded complete model: " + repo);
            UpdateHealthCard(manifestPath);
            return;
        }

        var item = new DownloadQueueItem(
            CreateDownloadId(),
            "",
            repo + " complete model",
            repo,
            "",
            string.IsNullOrWhiteSpace(revision) ? "main" : revision,
            ModelFileFormat.Pytorch,
            sourcePaths,
            targetDirectoryName,
            task,
            sizeBytes,
            baseModel,
            baseModels,
            adapterType,
            true,
            metadata ?? new Dictionary<string, string>(),
            bearerToken ?? "");
        _downloadQueue.Add(item);
        _lastDownloadItem = item;
        _lastDownloadUrl = "";
        _downloadPaused = false;
        RefreshDownloadQueue();
        TryStartNextDownload();
    }

    private void TryStartNextDownload()
    {
        if (_downloadService == null || _downloadPaused || _downloadService.IsDownloading || _completeModelDownloadService?.IsDownloading == true || _activeDownload != null)
            return;

        if (_downloadQueue.Count == 0)
        {
            UpdateDownloadButtons();
            RefreshDownloadQueue();
            return;
        }

        _activeDownload = _downloadQueue[0];
        _downloadQueue.RemoveAt(0);
        _activeProgressPercent = -1;
        _activeProgressDetail = "";
        RefreshDownloadQueue();
        CancelButton.IsEnabled = true;
        PauseButton.IsEnabled = true;
        ResumeButton.IsEnabled = false;
        RetryButton.IsEnabled = false;
        DownloadProgressBar.Value = 0;
        SetStatus($"Downloading {_activeDownload.FileName}...");
        if (_activeDownload.Format == ModelFileFormat.Pytorch)
        {
            if (_completeModelDownloadService == null)
            {
                SetStatus("Complete model downloader is not initialized.");
                _activeDownload = null;
                return;
            }

            _ = StartQueuedBundleDownloadAsync(_activeDownload);
        }
        else
        {
            ModelDownloadService service = _activeDownload.UseCompleteModels ? _completeModelDownloadService! : _downloadService;
            _ = StartQueuedFileDownloadAsync(service, _activeDownload);
        }
    }

    private async Task StartQueuedBundleDownloadAsync(DownloadQueueItem item)
    {
        if (_completeModelDownloadService == null)
            return;

        HuggingFaceAuthContext auth = await GetHuggingFaceAuthAsync().ConfigureAwait(true);
        string bearerToken = string.IsNullOrWhiteSpace(item.BearerToken) ? auth.BearerToken : item.BearerToken;
        await _completeModelDownloadService.DownloadBundleAsync(new ModelBundleDownloadRequest
        {
            Repository = item.Repo,
            Revision = item.Revision,
            TargetRelativeDirectory = item.TargetDirectoryName,
            Task = item.Task,
            TotalSizeBytes = item.SizeBytes,
            SourcePaths = item.SourcePaths,
            Metadata = BuildBundleMetadata(item)
        }, auth.Cookies, bearerToken).ConfigureAwait(true);
    }

    private async Task StartQueuedFileDownloadAsync(ModelDownloadService service, DownloadQueueItem item)
    {
        HuggingFaceAuthContext auth = await GetHuggingFaceAuthAsync().ConfigureAwait(true);
        string bearerToken = string.IsNullOrWhiteSpace(item.BearerToken) ? auth.BearerToken : item.BearerToken;
        await service.DownloadAsync(item.Url, item.FileName, auth.Cookies, bearerToken).ConfigureAwait(true);
    }

    private void OnDownloadProgress(DownloadProgress progress)
    {
        TryBeginOnUi(() =>
        {
            _activeProgressPercent = progress.Percent >= 0 ? Math.Min(100, progress.Percent) : -1;
            _activeProgressDetail = DownloadFormat.FormatBytes(progress.DownloadedBytes) + " / " + DownloadFormat.FormatBytes(progress.TotalBytes);
            DownloadProgressBar.Value = _activeProgressPercent >= 0 ? _activeProgressPercent : 0;
            SetStatus($"Downloading {progress.FileName}: {DownloadFormat.FormatBytes(progress.DownloadedBytes)} / {DownloadFormat.FormatBytes(progress.TotalBytes)}");
            DownloadProgressChanged?.Invoke(progress);
            RefreshDownloadQueue();
        });
    }

    private void OnDownloadCompleted(string path)
    {
        TryBeginOnUi((Func<Task>)(async () =>
        {
            DownloadQueueItem? completedDownload = _activeDownload;
            string registeredPath = path;
            bool isOnnx = completedDownload?.Format == ModelFileFormat.Onnx || path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
            bool isPytorch = completedDownload?.Format == ModelFileFormat.Pytorch || IsManifestFormat(path, "pytorch");
            bool isSingleFileCompleteModel = completedDownload?.UseCompleteModels == true && !isOnnx && !isPytorch;
            _lastDownloadedPath = path;
            _activeDownload = null;
            _activeProgressPercent = -1;
            _activeProgressDetail = "";
            UpdateDownloadButtons();
            DownloadProgressBar.Value = 100;
            if (isPytorch)
            {
                SetStatus($"Downloaded and registered PyTorch model bundle {completedDownload?.Repo ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? path)}");
            }
            else if (isOnnx)
            {
                try
                {
                    registeredPath = OnnxModelManifestWriter.WriteSingleFileManifest(path, completedDownload?.Repo, completedDownload?.SourcePath, completedDownload?.Revision);
                    SetStatus($"Downloaded and registered ONNX model {System.IO.Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    SetStatus($"Downloaded {System.IO.Path.GetFileName(path)}, but ONNX manifest registration failed: {ex.Message}");
                }
            }
            else if (isSingleFileCompleteModel)
            {
                try
                {
                    string manifestFormat = completedDownload?.Format == ModelFileFormat.Gguf ? "gguf" : "pytorch";
                    registeredPath = CompleteModelManifestWriter.WriteManifest(
                        System.IO.Path.GetDirectoryName(path) ?? CompleteModelsDirectory,
                        completedDownload?.Repo,
                        completedDownload?.Revision,
                        completedDownload?.Task,
                        manifestFormat,
                        string.IsNullOrWhiteSpace(completedDownload?.SourcePath) ? [System.IO.Path.GetFileName(path)] : [completedDownload.SourcePath],
                        completedDownload == null ? null : BuildBundleMetadata(completedDownload));
                    string label = string.Equals(manifestFormat, "gguf", StringComparison.OrdinalIgnoreCase) ? "GGUF Diffusers model" : "complete model";
                    SetStatus($"Downloaded and registered {label} {System.IO.Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    SetStatus($"Downloaded {System.IO.Path.GetFileName(path)}, but complete model registration failed: {ex.Message}");
                }
            }
            else
            {
                SetStatus($"Downloaded {System.IO.Path.GetFileName(path)}");
            }

            UpdateHealthCard(registeredPath);
            ModelDownloaded?.Invoke(registeredPath);
            bool canAutoLoad = completedDownload != null && completedDownload.Format == ModelFileFormat.Gguf && !completedDownload.UseCompleteModels;
            if (canAutoLoad && LoadAfterDownloadCheckBox.IsChecked.GetValueOrDefault(false))
                ModelLoadRequested?.Invoke(path);
            _lastInjectedUrl = null;
            await InjectDownloadPanelForCurrentPageAsync();
            TryStartNextDownload();
        }));
    }

    private void OnDownloadFailed(string message)
    {
        TryBeginOnUi(() =>
        {
            _activeDownload = null;
            _activeProgressPercent = -1;
            _activeProgressDetail = "";
            UpdateDownloadButtons();
            SetStatus(message);
            if (!message.StartsWith("Download paused", StringComparison.OrdinalIgnoreCase))
                TryStartNextDownload();
        });
    }

    private async Task<HuggingFaceAuthContext> GetHuggingFaceAuthAsync()
    {
        string cookies = "";
        try
        {
            if (Browser.CoreWebView2 != null)
            {
                IReadOnlyList<CoreWebView2Cookie> webViewCookies =
                    await Browser.CoreWebView2.CookieManager.GetCookiesAsync("https://huggingface.co/").ConfigureAwait(true);
                cookies = string.Join("; ", webViewCookies
                    .Where(cookie => !string.IsNullOrWhiteSpace(cookie.Name))
                    .Select(cookie => cookie.Name + "=" + cookie.Value));
            }
        }
        catch
        {
            cookies = "";
        }

        return new HuggingFaceAuthContext(cookies, ReadHuggingFaceBearerToken());
    }

    private static string ReadHuggingFaceBearerToken()
    {
        foreach (string key in new[] { "HF_TOKEN", "HUGGINGFACE_HUB_TOKEN", "HUGGINGFACE_TOKEN" })
        {
            string? value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "";
    }

    private static bool TryParseHuggingFaceRepo(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
            return false;

        string[] parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] is "models" or "datasets" or "spaces" or "docs" or "settings")
            return false;

        owner = parts[0];
        repo = parts[1];
        return true;
    }

    private void Navigate(string url)
    {
        url = NormalizeBrowserUrl(url);
        if (ModelDownloadService.IsModelUrl(url))
        {
            StartDownload(url);
            return;
        }

        _lastNavigatedUrl = url;
        AddressBox.Text = url;

        if (_browserReady && Browser.CoreWebView2 != null)
        {
            Browser.CoreWebView2.Navigate(url);
            return;
        }

        SaveCurrentPage();
        if (_externalBrowserMode)
            OpenExternalBrowser(url);
    }

    private static string NormalizeBrowserUrl(string url)
    {
        url = (url ?? "").Trim();
        if (string.IsNullOrWhiteSpace(url))
            return HuggingFaceModelsUrl;
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        return url;
    }

    private void OpenExternalBrowser(string url)
    {
        url = NormalizeBrowserUrl(url);
        _lastNavigatedUrl = url;
        AddressBox.Text = url;
        SaveCurrentPage();

        if (TryOpenExternalBrowser(url, out string detail))
            SetStatus("Opened " + url + " in the native Linux browser.");
        else
            SetStatus("Could not open external browser for " + url + ". " + detail);
    }

    private static bool TryOpenExternalBrowser(string url, out string detail)
    {
        var errors = new List<string>();
        foreach (ExternalBrowserCommand command in BuildExternalBrowserCommands(url))
        {
            try
            {
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = command.FileName,
                    UseShellExecute = command.UseShellExecute,
                    CreateNoWindow = true
                };
                if (!command.UseShellExecute)
                {
                    foreach (string argument in command.Arguments)
                        process.StartInfo.ArgumentList.Add(argument);
                }

                if (process.Start())
                {
                    if (command.VerifyExitCode &&
                        process.WaitForExit(1500) &&
                        process.ExitCode != 0)
                    {
                        errors.Add(command.FileName + ": exited with code " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
                        continue;
                    }

                    detail = "";
                    return true;
                }
            }
            catch (Exception ex)
            {
                errors.Add(command.FileName + ": " + ex.Message);
            }
        }

        detail = string.Join(" ", errors.Take(3));
        return false;
    }

    private static IEnumerable<ExternalBrowserCommand> BuildExternalBrowserCommands(string url)
    {
        bool linux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        bool wine = IsWineRuntime();

        if (linux || wine)
        {
            foreach (ExternalBrowserCommand command in BuildLinuxDesktopBrowserCommands(url, wine))
                yield return command;
        }

        if (wine)
        {
            yield return new ExternalBrowserCommand("winebrowser", [url], false);
            yield return new ExternalBrowserCommand("winebrowser.exe", [url], false);
            yield return new ExternalBrowserCommand("cmd", ["/c", "start", "", url], false);
            yield return new ExternalBrowserCommand("explorer.exe", [url], false);
        }

        if (linux || wine)
        {
            foreach (string linuxPath in new[]
                     {
                         "/usr/bin/xdg-open",
                         "/usr/local/bin/xdg-open",
                         "/usr/bin/gio",
                         "/usr/local/bin/gio",
                         "/usr/bin/sensible-browser",
                         "/usr/local/bin/sensible-browser",
                         "/usr/bin/gnome-open",
                         "/usr/local/bin/gnome-open",
                         "/usr/bin/kde-open5",
                         "/usr/local/bin/kde-open5",
                         "/usr/bin/kde-open",
                         "/usr/local/bin/kde-open",
                         "/usr/bin/firefox",
                         "/usr/local/bin/firefox",
                         "/usr/bin/chromium",
                         "/usr/local/bin/chromium",
                         "/usr/bin/chromium-browser",
                         "/usr/local/bin/chromium-browser",
                         "/usr/bin/google-chrome",
                         "/usr/local/bin/google-chrome",
                         "/usr/bin/brave-browser",
                         "/usr/local/bin/brave-browser",
                         "/usr/bin/microsoft-edge",
                         "/usr/local/bin/microsoft-edge",
                         "/usr/bin/waterfox",
                         "/usr/local/bin/waterfox"
                     })
            {
                IReadOnlyList<string> arguments = linuxPath.EndsWith("/gio", StringComparison.OrdinalIgnoreCase)
                    ? ["open", url]
                    : [url];
                yield return new ExternalBrowserCommand(linuxPath, arguments, false, true);
                if (wine)
                    yield return new ExternalBrowserCommand(ToWineUnixPath(linuxPath), arguments, false, true);
            }
        }

        yield return new ExternalBrowserCommand(url, [], true);
    }

    private static IEnumerable<ExternalBrowserCommand> BuildLinuxDesktopBrowserCommands(string url, bool wine)
    {
        const string openerScript = """
url="$1"
if [ -z "$url" ]; then
    exit 2
fi

for opener in xdg-open sensible-browser gnome-open kde-open5 kde-open; do
    if command -v "$opener" >/dev/null 2>&1; then
        nohup "$opener" "$url" >/dev/null 2>&1 &
        exit 0
    fi
done

if command -v gio >/dev/null 2>&1; then
    nohup gio open "$url" >/dev/null 2>&1 &
    exit 0
fi

for browser in firefox google-chrome chromium chromium-browser brave-browser microsoft-edge waterfox; do
    if command -v "$browser" >/dev/null 2>&1; then
        nohup "$browser" "$url" >/dev/null 2>&1 &
        exit 0
    fi
done

exit 1
""";

        foreach (string shellPath in new[] { "/bin/sh", "/usr/bin/sh", "/bin/bash", "/usr/bin/bash" })
        {
            yield return new ExternalBrowserCommand(shellPath, ["-lc", openerScript, "jackllm-open-url", url], false, true);
            if (wine)
                yield return new ExternalBrowserCommand(ToWineUnixPath(shellPath), ["-lc", openerScript, "jackllm-open-url", url], false, true);
        }
    }

    private static bool IsWineRuntime()
    {
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WINEPREFIX")) ||
               !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JACKLLM_WINE_SAFE_WPF")) ||
               Directory.Exists("Z:\\usr\\bin");
    }

    private static string ToWineUnixPath(string path)
    {
        path = (path ?? "").Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return path;
        return "Z:" + path.Replace('/', '\\');
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusChanged?.Invoke(text);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoBack == true)
            Browser.CoreWebView2.GoBack();
    }

    private void ForwardButton_Click(object sender, RoutedEventArgs e)
    {
        if (Browser.CoreWebView2?.CanGoForward == true)
            Browser.CoreWebView2.GoForward();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserReady && Browser.CoreWebView2 != null)
            Browser.CoreWebView2.Reload();
        else
            OpenExternalBrowser(AddressBox.Text);
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => Navigate(HuggingFaceModelsUrl);

    private void OpenExternalBrowserButton_Click(object sender, RoutedEventArgs e) =>
        OpenExternalBrowser(string.IsNullOrWhiteSpace(AddressBox.Text) ? HuggingFaceModelsUrl : AddressBox.Text);

    private async void QueueBestFromAddressButton_Click(object sender, RoutedEventArgs e)
    {
        await QueueBestDownloadFromAddressAsync().ConfigureAwait(true);
    }

    private async Task QueueBestDownloadFromAddressAsync()
    {
        string url = NormalizeBrowserUrl(AddressBox.Text);
        _lastNavigatedUrl = url;
        AddressBox.Text = url;
        SaveCurrentPage();

        if (!TryParseHuggingFaceRepo(url, out string owner, out string repo))
        {
            SetStatus("Paste a Hugging Face model URL first, for example https://huggingface.co/owner/model.");
            return;
        }

        try
        {
            SetStatus($"Scanning {owner}/{repo} from Linux browser mode...");
            HuggingFaceAuthContext auth = await GetHuggingFaceAuthAsync().ConfigureAwait(true);
            List<ModelFileCandidate> candidates = await GetCandidatesAsync(owner, repo, auth).ConfigureAwait(true);
            ApplyFitToCandidates(candidates);
            ModelFileCandidate? candidate = SelectBestExternalBrowserCandidate(candidates);
            if (candidate == null)
            {
                SetStatus($"No downloadable model file was found for {owner}/{repo}.");
                return;
            }

            QueueModelCandidate(candidate, auth.BearerToken);
            SetStatus($"Queued {candidate.FileName} from {owner}/{repo}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Model scan failed: {ex.Message}");
        }
    }

    private void ApplyFitToCandidates(IEnumerable<ModelFileCandidate> candidates)
    {
        var modelFit = CreateFitSnapshot(ModelsDirectory);
        var completeModelFit = CreateFitSnapshot(CompleteModelsDirectory);
        foreach (ModelFileCandidate candidate in candidates)
        {
            bool useCompleteModels = UsesCompleteModelsDirectory(candidate.Format, candidate.Task);
            string targetDirectory = useCompleteModels ? CompleteModelsDirectory : ModelsDirectory;
            candidate.ApplyFit(useCompleteModels ? completeModelFit : modelFit, targetDirectory);
        }
    }

    private static ModelFileCandidate? SelectBestExternalBrowserCandidate(IEnumerable<ModelFileCandidate> candidates)
    {
        return candidates
            .Where(candidate => candidate.CanDownload)
            .OrderByDescending(candidate => candidate.Format == ModelFileFormat.Gguf)
            .ThenByDescending(candidate => GetGgufQuantizationRank(candidate))
            .ThenByDescending(candidate => string.Equals(candidate.Action, "download_pytorch_bundle", StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.SizeBytes <= 0 ? long.MaxValue : candidate.SizeBytes)
            .FirstOrDefault();
    }

    private static int GetGgufQuantizationRank(ModelFileCandidate candidate)
    {
        if (candidate == null || candidate.Format != ModelFileFormat.Gguf)
            return 0;

        string text = string.Join(" ", candidate.Quantization, candidate.FileName, candidate.Path);
        if (text.Contains("Q4_K_M", StringComparison.OrdinalIgnoreCase))
            return 100;
        if (text.Contains("Q4_K_S", StringComparison.OrdinalIgnoreCase))
            return 90;
        if (text.Contains("IQ4_XS", StringComparison.OrdinalIgnoreCase))
            return 80;
        if (text.Contains("Q5_K_M", StringComparison.OrdinalIgnoreCase))
            return 70;
        if (text.Contains("Q5_K_S", StringComparison.OrdinalIgnoreCase))
            return 60;
        if (text.Contains("Q6_K", StringComparison.OrdinalIgnoreCase))
            return 50;
        if (text.Contains("Q8_0", StringComparison.OrdinalIgnoreCase))
            return 40;
        if (text.Contains("Q3_K", StringComparison.OrdinalIgnoreCase))
            return 30;
        if (text.Contains("Q2_K", StringComparison.OrdinalIgnoreCase))
            return 20;
        return 10;
    }

    public void SaveCurrentPage()
    {
        try
        {
            string? url = _externalBrowserMode ? _lastNavigatedUrl : Browser.CoreWebView2?.Source;
            if (string.IsNullOrWhiteSpace(url))
                url = _lastNavigatedUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(BrowserStatePath) ?? Environment.CurrentDirectory);
            File.WriteAllText(BrowserStatePath, url);
        }
        catch
        {
        }
    }

    private static string ReadSavedBrowserUrl()
    {
        try
        {
            if (!File.Exists(BrowserStatePath))
                return HuggingFaceModelsUrl;

            string url = File.ReadAllText(BrowserStatePath).Trim();
            return IsSafeHuggingFaceUrl(url) ? url : HuggingFaceModelsUrl;
        }
        catch
        {
            return HuggingFaceModelsUrl;
        }
    }

    private static bool IsSafeHuggingFaceUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)) &&
               uri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase);
    }

    private static string BrowserStatePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SocketJack", "huggingface-browser-url.txt");

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        string url = AddressBox.Text.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;
        Navigate(url);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _activeDownload = null;
        _activeProgressPercent = -1;
        _activeProgressDetail = "";
        _downloadPaused = false;
        _downloadQueue.Clear();
        RefreshDownloadQueue();
        _downloadService?.Cancel();
        _completeModelDownloadService?.Cancel();
        UpdateDownloadButtons();
    }

    private void CancelDownloadItemButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string id || string.IsNullOrWhiteSpace(id))
            return;

        if (_activeDownload?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
        {
            SetStatus("Canceling " + _activeDownload.FileName + "...");
            _downloadService?.Cancel();
            _completeModelDownloadService?.Cancel();
            return;
        }

        int removed = _downloadQueue.RemoveAll(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            if (_lastDownloadItem?.Id.Equals(id, StringComparison.OrdinalIgnoreCase) == true)
                _lastDownloadItem = null;
            SetStatus("Removed queued download.");
            RefreshDownloadQueue();
            UpdateDownloadButtons();
        }
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _downloadService?.Pause();
        _completeModelDownloadService?.Pause();
        _downloadPaused = true;
        PauseButton.IsEnabled = false;
        ResumeButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastDownloadUrl) || _lastDownloadItem != null;
    }

    private void ResumeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDownloadItem != null)
        {
            _downloadPaused = false;
            _downloadQueue.Add(_lastDownloadItem);
            RefreshDownloadQueue();
            TryStartNextDownload();
        }
        else if (!string.IsNullOrWhiteSpace(_lastDownloadUrl))
        {
            _downloadPaused = false;
            StartDownload(_lastDownloadUrl);
        }
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastDownloadItem != null)
        {
            _downloadPaused = false;
            _downloadQueue.Add(_lastDownloadItem);
            RefreshDownloadQueue();
            TryStartNextDownload();
        }
        else if (!string.IsNullOrWhiteSpace(_lastDownloadUrl))
        {
            _downloadPaused = false;
            StartDownload(_lastDownloadUrl);
        }
    }

    private void CleanupButton_Click(object sender, RoutedEventArgs e)
    {
        int deleted = (_downloadService?.CleanupPartialFiles() ?? 0) + (_completeModelDownloadService?.CleanupPartialFiles() ?? 0);
        SetStatus($"Cleaned up {deleted} partial download file(s).");
        if (!string.IsNullOrWhiteSpace(_lastDownloadedPath) && File.Exists(_lastDownloadedPath))
            UpdateHealthCard(_lastDownloadedPath);
    }

    public void Dispose()
    {
        _disposed = true;
        try { Browser.Dispose(); } catch { }
        try { _conversionService?.Dispose(); } catch { }
    }

    private void RefreshDownloadQueue()
    {
        _downloadQueueItems.Clear();
        if (_activeDownload != null)
            _downloadQueueItems.Add(CreateDownloadQueueView(
                _activeDownload,
                "Downloading",
                string.IsNullOrWhiteSpace(_activeProgressDetail) ? BuildQueueItemDetail(_activeDownload) : _activeProgressDetail,
                _activeProgressPercent,
                true));

        foreach (DownloadQueueItem item in _downloadQueue)
            _downloadQueueItems.Add(CreateDownloadQueueView(item, "Queued", BuildQueueItemDetail(item), -1, true));

        foreach (ModelConversionJob job in _conversionService?.ListJobs().Take(6) ?? [])
        {
            string prefix = string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase) ? "Converted" :
                string.Equals(job.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "Conversion failed" :
                string.Equals(job.Status, "cancelled", StringComparison.OrdinalIgnoreCase) ? "Conversion cancelled" :
                "Converting";
            _downloadQueueItems.Add(new DownloadQueueViewItem
            {
                Id = "conversion-" + job.JobId,
                FileName = string.IsNullOrWhiteSpace(job.Repository) ? "ONNX conversion" : job.Repository,
                Detail = job.Message,
                Status = prefix,
                Percent = Math.Max(0, Math.Min(100, job.Percent)),
                PercentText = job.Percent.ToString("F0", CultureInfo.InvariantCulture) + "%",
                IsCancelable = false,
                StatusForeground = BuildBrush("#FCD34D"),
                StatusBackground = BuildBrush("#1F1A0E"),
                StatusBorder = BuildBrush("#4A3411")
            });
        }

        if (_downloadQueueItems.Count == 0)
        {
            _downloadQueueItems.Add(new DownloadQueueViewItem
            {
                Id = "empty",
                FileName = "Queue empty",
                Detail = "Downloads you start from the browser or model list will appear here.",
                Status = "Idle",
                Percent = -1,
                PercentText = "",
                IsCancelable = false,
                StatusForeground = BuildBrush("#AAB4C3"),
                StatusBackground = BuildBrush("#111821"),
                StatusBorder = BuildBrush("#2D3440")
            });
        }

        int activeCount = _activeDownload == null ? 0 : 1;
        int queuedCount = _downloadQueue.Count;
        QueueSummaryText.Text = activeCount + queuedCount == 0
            ? "Idle"
            : activeCount.ToString(CultureInfo.InvariantCulture) + " active / " + queuedCount.ToString(CultureInfo.InvariantCulture) + " queued";
    }

    private void UpdateDownloadButtons()
    {
        bool downloading = _downloadService?.IsDownloading == true || _completeModelDownloadService?.IsDownloading == true || _activeDownload != null;
        CancelButton.IsEnabled = downloading || _downloadQueue.Count > 0;
        PauseButton.IsEnabled = downloading && !_downloadPaused;
        ResumeButton.IsEnabled = (!downloading || _downloadPaused) && (!string.IsNullOrWhiteSpace(_lastDownloadUrl) || _lastDownloadItem != null);
        RetryButton.IsEnabled = !downloading && (!string.IsNullOrWhiteSpace(_lastDownloadUrl) || _lastDownloadItem != null);
    }

    private void UpdateHealthCard(string modelPath)
    {
        try
        {
            if (modelPath.EndsWith(".jackonnx.json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(System.IO.Path.GetFileName(modelPath), "manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(modelPath));
                JsonElement root = document.RootElement;
                string name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "unknown" : "unknown";
                string type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() ?? "unknown" : "unknown";
                string format = root.TryGetProperty("format", out var formatElement) ? formatElement.GetString() ?? "onnx" : "onnx";
                long requiredMemory = root.TryGetProperty("requiredMemoryBytes", out var requiredElement) && requiredElement.TryGetInt64(out long required) ? required : 0;
                HealthText.Text = "Health: " +
                                  "name " + name +
                                  " | type " + type +
                                  " | format " + format +
                                  " | memory estimate " + DownloadFormat.FormatBytes(requiredMemory);
                return;
            }

            var info = new FileInfo(modelPath);
            GgufMetadataReader? metadata = null;
            if (modelPath.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                metadata = GgufMetadataReader.Read(modelPath);

            string architecture = metadata?.GetString("general.architecture") ?? "unknown";
            string tokenizer = metadata?.GetString("tokenizer.ggml.model") ?? metadata?.GetString("tokenizer.ggml.pre") ?? "unknown";
            uint? context = !string.IsNullOrWhiteSpace(architecture) ? metadata?.GetUInt32($"{architecture}.context_length") : null;
            string quant = ModelHeuristics.DetectQuantType(modelPath);
            IReadOnlyList<string> tags = ModelHeuristics.DetectModelTags(modelPath, metadata);
            long estimatedBytes = (long)Math.Ceiling(info.Length * 1.2);

            HealthText.Text = "Health: " +
                              "context " + (context?.ToString() ?? "unknown") +
                              " | memory estimate " + DownloadFormat.FormatBytes(estimatedBytes) +
                              " | tokenizer " + tokenizer +
                              " | architecture " + architecture +
                              " | quantization " + (string.IsNullOrWhiteSpace(quant) ? "unknown" : quant) +
                              " | tags " + (tags.Count == 0 ? "none" : string.Join(", ", tags));
        }
        catch (Exception ex)
        {
            HealthText.Text = "Health: unable to inspect model (" + ex.Message + ")";
        }
    }

    private static bool IsManifestFormat(string path, string format)
    {
        try
        {
            if (!File.Exists(path) ||
                (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                 !path.EndsWith(".jackonnx.json", StringComparison.OrdinalIgnoreCase)))
                return false;

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("format", out JsonElement formatElement) &&
                   string.Equals(formatElement.GetString(), format, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void StartConversion(string repo, string revision, IReadOnlyList<string> sourcePaths)
    {
        if (_conversionService == null)
            return;

        var job = _conversionService.StartConversion(new ModelConversionRequest
        {
            Repository = repo,
            Revision = string.IsNullOrWhiteSpace(revision) ? "main" : revision,
            SourcePaths = sourcePaths,
            Task = "text-generation",
            Precision = "fp32"
        });
        string sourceSummary = sourcePaths.Count == 0 ? "source tensor bundle" : sourcePaths.Count + " source tensor file(s)";
        SetStatus($"ONNX conversion job {job.JobId} queued for {repo} ({sourceSummary}).");
        HealthText.Text = "Conversion: queued. Configure LLMRUNTIME_ONNX_CONVERTER for a custom exporter, or install Optimum/Transformers for the built-in Python worker.";
        RefreshDownloadQueue();
    }

    private void OnConversionJobChanged(ModelConversionJob job)
    {
        TryBeginOnUi(() =>
        {
            SetStatus($"Conversion {job.JobId}: {job.Status} ({job.Percent:F0}%) - {job.Message}");
            HealthText.Text = "Conversion: " + job.Status +
                              " | repo " + (string.IsNullOrWhiteSpace(job.Repository) ? "local" : job.Repository) +
                              " | task " + job.Task +
                              " | output " + job.OutputDirectory +
                              (string.IsNullOrWhiteSpace(job.Error) ? "" : " | error " + job.Error);

            if (string.Equals(job.Status, "completed", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(job.ManifestPath) &&
                File.Exists(job.ManifestPath))
            {
                _lastDownloadedPath = job.ManifestPath;
                UpdateHealthCard(job.ManifestPath);
                ModelDownloaded?.Invoke(job.ManifestPath);
                _lastInjectedUrl = null;
                _ = InjectDownloadPanelForCurrentPageAsync();
            }

            RefreshDownloadQueue();
        });
    }

    private static DownloadQueueViewItem CreateDownloadQueueView(
        DownloadQueueItem item,
        string status,
        string detail,
        double percent,
        bool isCancelable)
    {
        bool isActive = status.Equals("Downloading", StringComparison.OrdinalIgnoreCase);
        return new DownloadQueueViewItem
        {
            Id = item.Id,
            FileName = item.FileName,
            Detail = detail,
            Status = status,
            Percent = percent,
            PercentText = percent >= 0 ? percent.ToString("F0", CultureInfo.InvariantCulture) + "%" : "",
            IsCancelable = isCancelable,
            StatusForeground = BuildBrush(isActive ? "#FFD7D7" : "#AAB4C3"),
            StatusBackground = BuildBrush(isActive ? "#25151A" : "#111821"),
            StatusBorder = BuildBrush(isActive ? "#80334A" : "#2D3440")
        };
    }

    private static string BuildQueueItemDetail(DownloadQueueItem item)
    {
        int fileCount = item.SourcePaths.Count > 0 ? item.SourcePaths.Count : 1;
        string kind = item.Format == ModelFileFormat.Pytorch
            ? "Complete model bundle"
            : item.UseCompleteModels
                ? "Complete model file"
                : item.Format.ToString().ToUpperInvariant() + " file";
        string size = item.SizeBytes > 0 ? " - " + DownloadFormat.FormatBytes(item.SizeBytes) : "";
        string files = fileCount > 1 ? " - " + fileCount.ToString(CultureInfo.InvariantCulture) + " files" : "";
        string repo = string.IsNullOrWhiteSpace(item.Repo) ? "" : " - " + item.Repo;
        return kind + files + size + repo;
    }

    private static SolidColorBrush BuildBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static string CreateDownloadId() => "download-" + Guid.NewGuid().ToString("N");

    private sealed record DownloadQueueItem(
        string Id,
        string Url,
        string FileName,
        string Repo,
        string SourcePath,
        string Revision,
        ModelFileFormat Format,
        IReadOnlyList<string> SourcePaths,
        string TargetDirectoryName,
        string Task,
        long SizeBytes,
        string BaseModel,
        IReadOnlyList<string> BaseModels,
        string AdapterType,
        bool UseCompleteModels,
        IReadOnlyDictionary<string, string> Metadata,
        string BearerToken);

    private sealed class DownloadQueueViewItem
    {
        public string Id { get; init; } = "";

        public string FileName { get; init; } = "";

        public string Detail { get; init; } = "";

        public string Status { get; init; } = "";

        public double Percent { get; init; }

        public string PercentText { get; init; } = "";

        public bool IsCancelable { get; init; }

        public Brush StatusForeground { get; init; } = BuildBrush("#AAB4C3");

        public Brush StatusBackground { get; init; } = BuildBrush("#111821");

        public Brush StatusBorder { get; init; } = BuildBrush("#2D3440");

        public Visibility CancelVisibility => IsCancelable ? Visibility.Visible : Visibility.Collapsed;

        public Visibility ProgressVisibility => Percent >= 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record IdealModelCategoryCacheEntry(HuggingFaceIdealModelCategoryResult Result, DateTimeOffset LoadedAt);

    private sealed record HuggingFaceAuthContext(string Cookies, string BearerToken);

    private sealed record ExternalBrowserCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        bool UseShellExecute,
        bool VerifyExitCode = false);

    private static IReadOnlyDictionary<string, string> BuildBundleMetadata(DownloadQueueItem item)
    {
        var metadata = new Dictionary<string, string>(item.Metadata, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(item.AdapterType))
        {
            metadata["adapterType"] = item.AdapterType;
            if (string.Equals(item.AdapterType, "lora", StringComparison.OrdinalIgnoreCase))
                metadata["adapterRequiresBaseModel"] = "true";
        }

        IReadOnlyList<string> baseModels = item.BaseModels
            .Concat([item.BaseModel])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (baseModels.Count > 0)
        {
            metadata["baseModel"] = baseModels[0];
            metadata["base_model"] = baseModels[0];
            metadata["baseModels"] = string.Join("|", baseModels);
        }

        return metadata;
    }

    private static ModelFileFormat? TryParseModelFileFormat(string rawFormat)
    {
        return Enum.TryParse(rawFormat, ignoreCase: true, out ModelFileFormat parsed) ? parsed : null;
    }

    private static ModelFileFormat DetectFormatFromUrl(string url)
    {
        if (ModelDownloadService.IsOnnxUrl(url))
            return ModelFileFormat.Onnx;
        if (ModelDownloadService.IsSafetensorUrl(url))
            return ModelFileFormat.Safetensors;
        if (ModelDownloadService.IsGgufUrl(url))
            return ModelFileFormat.Gguf;
        return ModelFileFormat.Unknown;
    }

    private static bool UsesCompleteModelsDirectory(ModelFileFormat format, string? task)
    {
        if (format != ModelFileFormat.Gguf)
            return true;

        string normalized = (task ?? "").Trim();
        return normalized.Equals("image", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("audio", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildDownloadFileName(
        string url,
        string? repo,
        string? sourcePath,
        ModelFileFormat format,
        string? revision,
        string? task,
        string? targetDirectoryName,
        bool useCompleteModels)
    {
        string fileName = ModelDownloadService.ExtractFileName(url);
        if (useCompleteModels)
        {
            string directory = string.IsNullOrWhiteSpace(targetDirectoryName)
                ? string.IsNullOrWhiteSpace(repo) ? "" : BuildBundleRelativeDirectory(repo, revision ?? "main")
                : targetDirectoryName!.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            string relativeFile = SanitizeRelativePath(string.IsNullOrWhiteSpace(sourcePath) ? fileName : sourcePath!);
            return string.IsNullOrWhiteSpace(directory) ? relativeFile : Path.Combine(directory, relativeFile);
        }

        if (format == ModelFileFormat.Onnx && !string.IsNullOrWhiteSpace(repo))
        {
            string prefix = SanitizeFileName(repo.Replace('/', '-').Replace('\\', '-'));
            string sourceName = string.IsNullOrWhiteSpace(sourcePath) ? fileName : System.IO.Path.GetFileName(sourcePath);
            fileName = SanitizeFileName(prefix + "-" + sourceName);
        }

        return SanitizeFileName(fileName);
    }

    private static string BuildBundleRelativeDirectory(string repo, string revision)
    {
        string rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim().Trim('/');
        string[] parts = repo.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(rev)
            .Select(SanitizeFileName)
            .ToArray();
        return parts.Length == 0 ? Path.Combine("model", "main") : Path.Combine(parts);
    }

    private static string SanitizeFileName(string fileName)
    {
        foreach (char invalid in System.IO.Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');
        return string.IsNullOrWhiteSpace(fileName) ? "model.bin" : fileName;
    }

    private static string SanitizeRelativePath(string path)
    {
        string[] parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(SanitizeFileName)
            .ToArray();
        return parts.Length == 0 ? SanitizeFileName(path) : Path.Combine(parts);
    }

    private static ModelFitSnapshot CreateFitSnapshot(string modelsDirectory)
    {
        Directory.CreateDirectory(modelsDirectory);
        string root = Path.GetPathRoot(Path.GetFullPath(modelsDirectory)) ?? modelsDirectory;
        long free = 0;
        try { free = new DriveInfo(root).AvailableFreeSpace; } catch { }

        return new ModelFitSnapshot
        {
            SharedVideoMemoryBytes = EstimateSharedVideoMemoryBytes(),
            DriveFreeBytes = free
        };
    }

    private static long EstimateSharedVideoMemoryBytes()
    {
        var memoryStatus = new MemoryStatusEx();
        if (GlobalMemoryStatusEx(memoryStatus) && memoryStatus.ullTotalPhys > 0)
            return (long)Math.Min(memoryStatus.ullTotalPhys / 2, long.MaxValue);

        return 0;
    }

    private static class DownloadFormat
    {
        public static string FormatBytes(long bytes)
        {
            if (bytes <= 0)
                return "unknown";
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
