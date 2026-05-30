using System.Windows;

namespace SocketJack.MagicMasterList;

public partial class App : System.Windows.Application {
    private MainWindow? _mainWindow;

    private void OnStartup(object sender, StartupEventArgs e) {
        _mainWindow = new MainWindow();
        _mainWindow.Show();
    }

    private void OnExit(object sender, ExitEventArgs e) {
        _mainWindow?.ShutdownServer();
    }
}
