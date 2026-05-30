namespace JackLLMCompanion;

public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;

    private void OnStartup(object sender, System.Windows.StartupEventArgs e)
    {
        bool startHidden = e.Args.Any(arg =>
            string.Equals(arg, "--tray", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--hidden", StringComparison.OrdinalIgnoreCase));

        _mainWindow = new MainWindow(startHidden, TryReadParentProcessId(e.Args));
        MainWindow = _mainWindow;

        if (!startHidden)
            _mainWindow.Show();
    }

    private void OnExit(object sender, System.Windows.ExitEventArgs e)
    {
        SocketJack.ThreadManager.Shutdown();
    }

    private static int? TryReadParentProcessId(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid) && pid > 0)
                return pid;
        }

        return null;
    }
}
