using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SocketJack.SocketChat.Windows;

internal static class AeroBackdrop
{
    public static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        window.Background = System.Windows.Media.Brushes.Transparent;
        if (HwndSource.FromHwnd(handle) is HwndSource source && source.CompositionTarget != null)
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        if (!TryEnableAccentBlur(handle)) TryEnableDwmBlur(handle);
        TryRoundCorners(handle);
    }

    private static bool TryEnableAccentBlur(IntPtr handle)
    {
        try
        {
            var accent = new AccentPolicy { AccentState = AccentState.EnableAcrylicBlurBehind, AccentFlags = 2, GradientColor = unchecked((int)0xB818100C) };
            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData { Attribute = WindowCompositionAttribute.AccentPolicy, Data = pointer, SizeOfData = size };
                return SetWindowCompositionAttribute(handle, ref data) != 0;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        catch { return false; }
    }

    private static bool TryEnableDwmBlur(IntPtr handle)
    {
        try
        {
            var blur = new DwmBlurBehind { Flags = 1, Enable = true, TransitionOnMaximized = true };
            bool enabled = DwmEnableBlurBehindWindow(handle, ref blur) == 0;
            var margins = new DwmMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            return DwmExtendFrameIntoClientArea(handle, ref margins) == 0 || enabled;
        }
        catch { return false; }
    }

    private static void TryRoundCorners(IntPtr handle) { try { int value = 2; _ = DwmSetWindowAttribute(handle, 33, ref value, sizeof(int)); } catch { } }

    private enum AccentState { Disabled, EnableGradient, EnableTransparentGradient, EnableBlurBehind, EnableAcrylicBlurBehind }
    private enum WindowCompositionAttribute { AccentPolicy = 19 }
    [StructLayout(LayoutKind.Sequential)] private struct AccentPolicy { public AccentState AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)] private struct WindowCompositionAttributeData { public WindowCompositionAttribute Attribute; public IntPtr Data; public int SizeOfData; }
    [StructLayout(LayoutKind.Sequential)] private struct DwmBlurBehind { public int Flags; [MarshalAs(UnmanagedType.Bool)] public bool Enable; public IntPtr BlurRegion; [MarshalAs(UnmanagedType.Bool)] public bool TransitionOnMaximized; }
    [StructLayout(LayoutKind.Sequential)] private struct DwmMargins { public int Left; public int Right; public int Top; public int Bottom; }
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("dwmapi.dll", PreserveSig = true)] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blurBehind);
    [DllImport("dwmapi.dll", PreserveSig = true)] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref DwmMargins margins);
    [DllImport("dwmapi.dll", PreserveSig = true)] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
