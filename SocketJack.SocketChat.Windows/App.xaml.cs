using System.Windows;

namespace SocketJack.SocketChat.Windows;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => {
            if (sender is Window window) AeroBackdrop.Apply(window);
        }));
        base.OnStartup(e);
    }
}
