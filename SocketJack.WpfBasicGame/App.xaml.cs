using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace SocketJack.WpfBasicGame;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application {
    public void OnStartup(object? sender, StartupEventArgs e) {
        ThreadManager.UseGlobalTickLoop = true ;

        var mainWindow = new MainWindow();
        mainWindow.Show();
        var mainWindow2 = new MainWindow(2);
        mainWindow2.Show();
        mainWindow.ShareToggleButton_Click(null, null); 
    }
    public void OnExit(object sender, ExitEventArgs e) {
        ThreadManager.Shutdown();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e) {
        // JackDebug.WPF's SyntaxBox uses an Aho-Corasick engine limited to the first
        // 256 Unicode code points.  When the debug window renders text containing
        // characters >= U+0100 the engine throws IndexOutOfRangeException during the
        // WPF render pass.  Suppress it so the app stays alive.
        if (e.Exception is IndexOutOfRangeException &&
            e.Exception.StackTrace != null &&
            e.Exception.StackTrace.Contains("AhoCorasickSearch")) {
            e.Handled = true;
        }
    }
}

