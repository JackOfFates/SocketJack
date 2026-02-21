using System.Configuration;
using System.Data;
using System.Windows;

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
}

