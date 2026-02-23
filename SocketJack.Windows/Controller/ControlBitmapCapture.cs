// This project mixes nullable contexts; keep this file consistent.
#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SocketJack.WPF {
    namespace Controller {

        internal static class ControlBitmapCapture {
            public static Task<byte[]?> CaptureJpegAsync(FrameworkElement element, int quality = 77, double dpi = 96.0) {
                if (element == null)
                    return Task.FromResult<byte[]?>(null);

                return element.Dispatcher.InvokeAsync(() =>
                {
                    // Ensure layout is up to date
                    element.UpdateLayout();

                    var dipWidth = element.ActualWidth;
                    var dipHeight = element.ActualHeight;

                    if (dipWidth <= 0 || dipHeight <= 0)
                        return (byte[]?)null;

                    // Detect the actual DPI so the captured bitmap has pixel dimensions
                    // consistent with its DPI metadata.  When the viewer decodes the JPEG,
                    // WPF may fall back to the system DPI; using the true DPI here keeps
                    // the natural size correct regardless of the fallback path.
                    double renderDpiX = dpi;
                    double renderDpiY = dpi;
                    var ps = PresentationSource.FromVisual(element);
                    if (ps != null && ps.CompositionTarget != null) {
                        var m = ps.CompositionTarget.TransformToDevice;
                        renderDpiX = 96.0 * m.M11;
                        renderDpiY = 96.0 * m.M22;
                    }

                    int pixelWidth = (int)Math.Ceiling(dipWidth * renderDpiX / 96.0);
                    int pixelHeight = (int)Math.Ceiling(dipHeight * renderDpiY / 96.0);

                    if (pixelWidth <= 0 || pixelHeight <= 0)
                        return (byte[]?)null;

                    // Render via a VisualBrush so we capture exactly the element bounds.
                    // Explicitly set Viewbox in absolute DIP coordinates so the brush
                    // always maps the element's layout region 1-to-1 to the destination
                    // rectangle.  Without this, WPF's automatic bounding-box computation
                    // can return device-scaled bounds after visual-tree invalidations on
                    // high-DPI displays, causing the content to fill only half the bitmap.
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen()) {
                        var vb = new VisualBrush(element)
                        {
                            Viewbox = new Rect(0, 0, dipWidth, dipHeight),
                            ViewboxUnits = BrushMappingMode.Absolute
                        };
                        dc.DrawRectangle(vb, null, new Rect(0, 0, dipWidth, dipHeight));
                    }

                    var rtb = new RenderTargetBitmap(pixelWidth, pixelHeight, renderDpiX, renderDpiY, PixelFormats.Pbgra32);
                    rtb.Render(dv);

                    if (quality < 1)
                        quality = 1;
                    if (quality > 100)
                        quality = 100;

                    var encoder = new JpegBitmapEncoder
                    {
                        QualityLevel = quality
                    };
                    encoder.Frames.Add(BitmapFrame.Create(rtb));

                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    return ms.ToArray();
                }).Task;
            }
#nullable restore
        }
    }
}