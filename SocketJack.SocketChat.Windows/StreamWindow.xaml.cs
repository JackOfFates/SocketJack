using SocketJack.Net;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using SocketJack.SocketChat;

namespace SocketJack.SocketChat.Windows;

public partial class StreamWindow : Window
{
    private MutableTcpServer? _server;
    private BroadcastServer? _broadcast;
    private readonly DispatcherTimer _previewTimer;
    private readonly SocketChatUserProfile _profile;
    private System.Windows.Forms.Screen CaptureScreen => System.Windows.Forms.Screen.AllScreens[Math.Clamp(_profile.StreamMonitorIndex, 0, System.Windows.Forms.Screen.AllScreens.Length - 1)];

    public StreamWindow(SocketChatUserProfile profile)
    {
        InitializeComponent(); _profile = profile;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000d / Math.Clamp(profile.StreamPreviewFps, 1, 60)) };
        _previewTimer.Tick += PreviewTimer_Tick;
        StreamKeyBox.Text = BroadcastServer.GenerateStreamKey();
        RtmpServerBox.Text = "rtmp://127.0.0.1:" + profile.StreamPort + "/stream";
        VlcUrlBox.Text = "http://127.0.0.1:" + profile.StreamPort + "/stream/data";
        PreviewPlaceholder.Text = profile.StreamSource == "Display" ? "Start preview to capture the configured display" : profile.StreamSource + " is configured through OBS";
        Closed += (_, _) => StopEverything();
    }

    private void HostButton_Click(object sender, RoutedEventArgs e)
    {
        if (_server != null) { StopHost(); return; }
        try
        {
            var options = NetworkOptions.NewDefault();
            options.UsePeerToPeer = true;
            _server = new MutableTcpServer(options, _profile.StreamPort, "SocketChat RTMP");
            _broadcast = new BroadcastServer(_server) { StreamKey = StreamKeyBox.Text };
            _broadcast.Register();
            if (!_server.Listen()) throw new InvalidOperationException("Port " + _profile.StreamPort + " is unavailable.");
            HostStatusText.Text = "Listening on port " + _profile.StreamPort + " · " + _profile.StreamBitrateKbps + " Kbps target";
            HostStatusText.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
            HostButton.Content = "Stop RTMP host";
        }
        catch (Exception ex) { StopHost(); HostStatusText.Text = "Could not start: " + ex.Message; }
    }

    private void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_previewTimer.IsEnabled) { _previewTimer.Stop(); ScreenPreview.Source = null; PreviewPlaceholder.Visibility = Visibility.Visible; PreviewButton.Content = "Start screen preview"; }
        else { _previewTimer.Start(); PreviewPlaceholder.Visibility = Visibility.Collapsed; PreviewButton.Content = "Stop preview"; }
    }

    private void PreviewTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            var bounds = CaptureScreen.Bounds; int width = bounds.Width; int height = bounds.Height;
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new System.Drawing.Size(width, height), CopyPixelOperation.SourceCopy);
            IntPtr handle = bitmap.GetHbitmap();
            try { var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(960, 540)); source.Freeze(); ScreenPreview.Source = source; }
            finally { DeleteObject(handle); }
        }
        catch (Exception ex) { _previewTimer.Stop(); PreviewPlaceholder.Text = "Screen capture unavailable: " + ex.Message; PreviewPlaceholder.Visibility = Visibility.Visible; }
    }

    private void OpenObs_Click(object sender, RoutedEventArgs e)
    {
        string path = _profile.ObsPath;
        if (!File.Exists(path)) { System.Windows.MessageBox.Show(this, "OBS Studio was not found.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Process.Start(new ProcessStartInfo(path) { WorkingDirectory = Path.GetDirectoryName(path)!, UseShellExecute = true });
    }

    private void OpenVlc_Click(object sender, RoutedEventArgs e)
    {
        string path = _profile.VlcPath;
        if (!File.Exists(path)) { System.Windows.MessageBox.Show(this, "VLC was not found.", "SocketChat", MessageBoxButton.OK, MessageBoxImage.Information); return; }
        Process.Start(new ProcessStartInfo(path, VlcUrlBox.Text) { UseShellExecute = true });
    }

    private void StopHost() { try { _server?.StopListening(); } catch { } _broadcast = null; _server = null; HostStatusText.Text = "Stopped"; HostStatusText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"); HostButton.Content = "Start RTMP host"; }
    private void StopEverything() { _previewTimer.Stop(); StopHost(); }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
