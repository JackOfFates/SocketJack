using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace SocketJack.Update;

public partial class App : Application {
    private MainWindow? _mainWindow;
    private UpdateServerHost? _headlessHost;

    public App() {
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
    }

    private void OnStartup(object sender, StartupEventArgs e) {
        bool headless = IsHeadless(e.Args);
        if (headless) {
            WriteHeadlessLog("startup args=" + string.Join(" ", e.Args));
            WriteHeadlessLog("headless mode selected");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            UpdateServerOptions options = UpdateServerOptions.Load();
            WriteHeadlessLog("base=" + AppContext.BaseDirectory + " port=" + options.Port + " publicUrl=" + options.PublicUrl + " dataDir=" + options.DataDirectory);
            _headlessHost = new UpdateServerHost(options);
            bool started = _headlessHost.Start();
            WriteHeadlessLog("headless listen result=" + started + " endpoint=" + _headlessHost.EndpointText + " status=" + _headlessHost.Status);
            if (!started)
                Shutdown(1);
            return;
        }

        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e) {
        _mainWindow?.ShutdownServer();
        _headlessHost?.Dispose();
    }

    private static bool IsHeadless(string[] args) {
        if (args.Any(arg => arg.Equals("--headless", StringComparison.OrdinalIgnoreCase) ||
                            arg.Equals("/headless", StringComparison.OrdinalIgnoreCase)))
            return true;

        string value = Environment.GetEnvironmentVariable("SOCKETJACK_UPDATE_HEADLESS") ?? "";
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteHeadlessLog(string message) {
        WriteStartupLog(message);
    }

    private static void WriteStartupLog(string message) {
        try {
            File.AppendAllText(
                Path.Combine(AppContext.BaseDirectory, "SocketJack.Update.startup.log"),
                DateTimeOffset.Now.ToString("O") + " " + message + Environment.NewLine);
        } catch {
        }
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e) {
        WriteStartupLog("unhandled exception " + e.ExceptionObject);
    }

    private static void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        WriteStartupLog("dispatcher exception " + e.Exception);
    }
}
