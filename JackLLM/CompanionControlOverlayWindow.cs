using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FormsScreen = System.Windows.Forms.Screen;

namespace JackLLM;

internal sealed class CompanionControlOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020L;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint WdaExcludeFromCapture = 0x00000011;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly System.Drawing.Rectangle _screenBounds;

    internal CompanionControlOverlayWindow(FormsScreen screen)
    {
        _screenBounds = screen.Bounds;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Focusable = false;
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;

        Grid surface = new();
        surface.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(31, 126, 255)),
            BorderThickness = new Thickness(6),
            IsHitTestVisible = false
        });

        if (screen.Primary)
        {
            surface.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(18, 93, 201)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(96, 173, 255)),
                BorderThickness = new Thickness(1, 0, 1, 1),
                CornerRadius = new CornerRadius(0, 0, 7, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Padding = new Thickness(18, 7, 18, 8),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = "JACK IS USING YOUR PC (CTRL+ESC TO CANCEL)",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center
                }
            });
        }

        Content = surface;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => PositionWindow();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        long style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style | WsExTransparent | WsExToolWindow | WsExNoActivate));
        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
        PositionWindow();
    }

    private void PositionWindow()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;
        SetWindowPos(
            handle,
            HwndTopmost,
            _screenBounds.Left,
            _screenBounds.Top,
            _screenBounds.Width,
            _screenBounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern IntPtr GetWindowLong32(IntPtr window, int index);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern IntPtr SetWindowLong32(IntPtr window, int index, IntPtr value);

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(window, index, value) : SetWindowLong32(window, index, value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
