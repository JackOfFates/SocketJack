using System.Windows;

namespace LmVsProxyGUI;

public partial class App : System.Windows.Application {
    private MainWindow? _mainWindow;

    private void OnStartup(object sender, StartupEventArgs e) {
        bool startHidden = e.Args.Any(arg => string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase));
        _mainWindow = new MainWindow(startHidden);
        _mainWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e) {
        _mainWindow?.ShutdownProxy();
        SocketJack.ThreadManager.Shutdown();
    }

}
