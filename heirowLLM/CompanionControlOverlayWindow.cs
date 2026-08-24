using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using FormsScreen = System.Windows.Forms.Screen;

namespace heirowLLM;

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
    private readonly Canvas _cursorLayer = new();
    private readonly Viewbox _swordCursor;

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
                    Text = "heirow IS USING YOUR PC (CTRL+ESC TO CANCEL)",
                    Foreground = Brushes.White,
                    FontSize = 15,
                    FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center
                }
            });
        }

        _swordCursor = CreateSwordCursor();
        _cursorLayer.IsHitTestVisible = false;
        _cursorLayer.Children.Add(_swordCursor);
        surface.Children.Add(_cursorLayer);

        Content = surface;
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            PositionWindow();
            if (GetCursorPos(out NativePoint point))
                UpdateCursorPosition(new System.Drawing.Point(point.X, point.Y));
        };
    }

    internal void UpdateCursorPosition(System.Drawing.Point screenPosition)
    {
        double left = screenPosition.X - _screenBounds.Left - 27;
        double top = screenPosition.Y - _screenBounds.Top - 4;
        Canvas.SetLeft(_swordCursor, Math.Clamp(left, 0, Math.Max(0, _screenBounds.Width - 32)));
        Canvas.SetTop(_swordCursor, Math.Clamp(top, 0, Math.Max(0, _screenBounds.Height - 32)));
        _swordCursor.Visibility = _screenBounds.Contains(screenPosition) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Viewbox CreateSwordCursor()
    {
        Canvas icon = new() { Width = 32, Height = 32 };
        icon.Children.Add(new Polygon
        {
            Points = new PointCollection
            {
                new(5, 27), new(20, 12), new(28, 4), new(24, 13), new(9, 28)
            },
            Fill = new LinearGradientBrush(Color.FromRgb(245, 248, 255), Color.FromRgb(114, 164, 222), 45),
            Stroke = new SolidColorBrush(Color.FromRgb(13, 30, 58)),
            StrokeThickness = 1.4
        });
        icon.Children.Add(new Line
        {
            X1 = 5, Y1 = 21, X2 = 13, Y2 = 29,
            Stroke = new SolidColorBrush(Color.FromRgb(255, 201, 74)),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        icon.Children.Add(new Line
        {
            X1 = 7, Y1 = 26, X2 = 3, Y2 = 30,
            Stroke = new SolidColorBrush(Color.FromRgb(121, 70, 35)),
            StrokeThickness = 3,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        });
        icon.Children.Add(new Ellipse
        {
            Width = 4, Height = 4,
            Fill = new SolidColorBrush(Color.FromRgb(255, 201, 74)),
            Margin = new Thickness(1, 28, 0, 0)
        });
        return new Viewbox
        {
            Width = 32,
            Height = 32,
            IsHitTestVisible = false,
            Child = icon
        };
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

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
