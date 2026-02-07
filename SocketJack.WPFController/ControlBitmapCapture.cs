// This project mixes nullable contexts; keep this file consistent.
#nullable enable
using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SocketJack.WPFController;

internal static class ControlBitmapCapture {
    public static Task<byte[]?> CaptureJpegAsync(FrameworkElement element, int quality = 77, double dpi = 96.0) {
        if (element == null)
            return Task.FromResult<byte[]?>(null);

        return element.Dispatcher.InvokeAsync(() => {
            // Ensure layout is up to date
            element.UpdateLayout();

            var width = (int)Math.Ceiling(element.ActualWidth);
            var height = (int)Math.Ceiling(element.ActualHeight);

            if (width <= 0 || height <= 0)
                return (byte[]?)null;

            // Render via a VisualBrush so we capture exactly the element bounds.
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen()) {
                var vb = new VisualBrush(element);
                dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
            }

            var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(dv);

            if (quality < 1)
                quality = 1;
            if (quality > 100)
                quality = 100;

            var encoder = new JpegBitmapEncoder {
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
