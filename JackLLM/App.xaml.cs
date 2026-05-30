using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace JackLLM;

public partial class App : System.Windows.Application {
    private MainWindow? _mainWindow;
    private StartupLoadingWindow? _startupWindow;
    private CancellationTokenSource? _startupCancellation;
    private static readonly object CrashLogLock = new();
    private static bool _crashLoggingInstalled;

    private async void OnStartup(object sender, StartupEventArgs e) {
        InstallCrashLogging();
        bool wineSafeWpf = IsWineSafeWpfMode();
        if (wineSafeWpf)
            ConfigureWineSafeWpf();

        bool startHidden = e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
        _startupCancellation = new CancellationTokenSource();
        if (wineSafeWpf) {
            try {
                IProgress<StartupLoadingProgress> wineProgress = new Progress<StartupLoadingProgress>();
                _mainWindow = await JackLLM.MainWindow.CreateAsync(startHidden, wineProgress, _startupCancellation.Token);
                _startupCancellation.Token.ThrowIfCancellationRequested();
                MainWindow = _mainWindow;
                _mainWindow.Show();
            } catch (Exception ex) {
                MessageBox.Show(
                    "JackLLM Workstation failed to start: " + ex.Message,
                    "Startup failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
            return;
        }

        var loadingWindow = new StartupLoadingWindow();
        _startupWindow = loadingWindow;
        loadingWindow.CancelRequested += OnStartupCancelRequested;
        MainWindow = loadingWindow;
        loadingWindow.Show();
        IProgress<StartupLoadingProgress> progress = new Progress<StartupLoadingProgress>(loadingWindow.UpdateProgress);

        try {
            progress.Report(new StartupLoadingProgress(2, "Preparing JackLLM Workstation...", "Starting the loading surface."));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            _mainWindow = await JackLLM.MainWindow.CreateAsync(startHidden, progress, _startupCancellation.Token);
            _startupCancellation.Token.ThrowIfCancellationRequested();

            progress.Report(new StartupLoadingProgress(100, "JackLLM Workstation ready", "Opening the workstation."));
            MainWindow = _mainWindow;
            _mainWindow.Show();
            loadingWindow.CompleteAndClose();
            _startupWindow = null;
        } catch (OperationCanceledException) when (_startupCancellation?.IsCancellationRequested == true) {
            loadingWindow.CompleteAndClose();
            Shutdown();
        } catch (Exception ex) when (IsRepairableStartupFailure(ex)) {
            var repairWindow = new RepairInstallationWindow(ex, startHidden);
            MainWindow = repairWindow;
            repairWindow.Show();
            loadingWindow.CompleteAndClose();
        } catch (Exception ex) {
            MessageBox.Show(
                "JackLLM Workstation failed to start: " + ex.Message,
                "Startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            loadingWindow.CompleteAndClose();
            Shutdown(-1);
        } finally {
            loadingWindow.CancelRequested -= OnStartupCancelRequested;
            _startupCancellation?.Dispose();
            _startupCancellation = null;
            if (_startupWindow == loadingWindow)
                _startupWindow = null;
        }
    }

    private void OnExit(object sender, ExitEventArgs e) {
        try { _startupCancellation?.Cancel(); } catch { }
        if (_mainWindow != null) {
            _mainWindow.ShutdownProxy();
            SocketJack.ThreadManager.Shutdown();
        }
    }

    private void OnStartupCancelRequested(object? sender, EventArgs e) {
        try { _startupCancellation?.Cancel(); } catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        WriteCrashLog("DispatcherUnhandledException", e.Exception);
        if (IsWineSafeWpfMode() && IsWineDispatcherInvalidHandle(e.Exception))
            e.Handled = true;
    }

    private static void InstallCrashLogging() {
        if (_crashLoggingInstalled)
            return;

        _crashLoggingInstalled = true;
        Current.DispatcherUnhandledException += CurrentAppDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrashLog("AppDomain.UnhandledException", args.ExceptionObject as Exception, "IsTerminating=" + args.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, args) => {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", args.Exception);
            if (IsWineSafeWpfMode())
                args.SetObserved();
        };
    }

    private static void CurrentAppDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        if (Current is App app)
            app.OnDispatcherUnhandledException(sender, e);
    }

    internal static void WriteCrashLog(string source, Exception? exception = null, string detail = "") {
        try {
            string path = GetCrashLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var builder = new StringBuilder();
            builder.Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] ").AppendLine(source);
            if (!string.IsNullOrWhiteSpace(detail))
                builder.AppendLine(detail.Trim());
            builder.Append("WineSafe=").Append(IsWineSafeWpfMode()).Append(" DISPLAY=").AppendLine(Environment.GetEnvironmentVariable("DISPLAY") ?? "");
            if (exception != null)
                builder.AppendLine(exception.ToString());
            builder.AppendLine();
            lock (CrashLogLock)
                File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
        } catch {
        }
    }

    private static string GetCrashLogPath() {
        string configured = Environment.GetEnvironmentVariable("JACKLLM_CRASH_LOG") ?? "";
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        string basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(basePath))
            basePath = Path.GetTempPath();
        return Path.Combine(basePath, "JackLLM", "Logs", "wpf-crash.log");
    }

    private static bool IsRepairableStartupFailure(Exception ex) {
        for (Exception? current = ex; current != null; current = current.InnerException) {
            if (current is FileNotFoundException or FileLoadException or BadImageFormatException)
                return true;
        }
        return false;
    }

    private static bool IsWineSafeWpfMode() {
        string? value = Environment.GetEnvironmentVariable("JACKLLM_WINE_SAFE_WPF");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWineDispatcherInvalidHandle(Exception? exception) {
        for (Exception? current = exception; current != null; current = current.InnerException) {
            if (current is Win32Exception win32 && win32.NativeErrorCode == 6)
                return true;
            string message = current.Message ?? "";
            if (message.IndexOf("Invalid handle", StringComparison.OrdinalIgnoreCase) >= 0 &&
                current.StackTrace?.IndexOf("System.Windows.Threading.Dispatcher", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static void ConfigureWineSafeWpf() {
        try {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        } catch {
        }

        var fontFamily = new FontFamily("Tahoma");
        OverrideStyleSetter(typeof(Window), Control.FontFamilyProperty, fontFamily);
        OverrideStyleSetter(typeof(Window), TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        OverrideStyleSetter(typeof(Window), TextOptions.TextRenderingModeProperty, TextRenderingMode.Grayscale);
        OverrideStyleSetter(typeof(TextBlock), TextBlock.FontFamilyProperty, fontFamily);
        OverrideStyleSetter(typeof(TextBlock), TextOptions.TextFormattingModeProperty, TextFormattingMode.Display);
        OverrideStyleSetter(typeof(TextBlock), TextOptions.TextRenderingModeProperty, TextRenderingMode.Grayscale);
        OverrideStyleSetter(typeof(Control), Control.FontFamilyProperty, fontFamily);
    }

    private static void OverrideStyleSetter(object key, DependencyProperty property, object value) {
        try {
            if (Current.Resources[key] is Style style && !style.IsSealed)
                style.Setters.Add(new Setter(property, value));
        } catch {
        }
    }

}
