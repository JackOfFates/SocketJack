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
            private const int BytesPerPixel = 4;
            private const int DirtyBlockSize = 16;
            private const int MaxDeltaFramesBeforeKeyFrame = 30;
            private const int ColorDifferenceThreshold = 2;
            private const double FullFrameDirtyRatio = 0.45;
            private static readonly TimeSpan KeyFrameInterval = TimeSpan.FromSeconds(3);

            public static Task<byte[]?> CaptureJpegAsync(FrameworkElement element, int quality = 77, double dpi = 96.0) {
                if (element == null)
                    return Task.FromResult<byte[]?>(null);

                return element.Dispatcher.InvokeAsync(() =>
                {
                    var rendered = RenderElementBitmap(element, dpi);
                    if (rendered == null)
                        return (byte[]?)null;

                    return EncodeJpeg(rendered.Bitmap, quality);
                }).Task;
            }

            public static Task<ControlBitmapCaptureResult?> CaptureAdaptiveJpegAsync(
                FrameworkElement element,
                ControlBitmapCaptureState state,
                int quality = 77,
                double dpi = 96.0,
                bool forceKeyFrame = false) {
                if (element == null)
                    return Task.FromResult<ControlBitmapCaptureResult?>(null);
                if (state == null)
                    throw new ArgumentNullException(nameof(state));

                return element.Dispatcher.InvokeAsync(() =>
                {
                    var rendered = RenderElementBitmap(element, dpi);
                    if (rendered == null)
                        return (ControlBitmapCaptureResult?)null;

                    int stride = GetStride(rendered.PixelWidth);
                    var currentPixels = new byte[stride * rendered.PixelHeight];
                    rendered.Bitmap.CopyPixels(currentPixels, stride, 0);

                    bool keyFrame = forceKeyFrame ||
                                    state.NeedsKeyFrame(rendered.PixelWidth, rendered.PixelHeight, stride, KeyFrameInterval, MaxDeltaFramesBeforeKeyFrame);
                    Int32Rect dirty = new Int32Rect(0, 0, rendered.PixelWidth, rendered.PixelHeight);
                    double changedRatio = 1.0;

                    if (!keyFrame) {
                        byte[]? previousPixels = state.PreviousPixels;
                        if (previousPixels == null ||
                            !TryFindDirtyBounds(
                                currentPixels,
                                previousPixels,
                                rendered.PixelWidth,
                                rendered.PixelHeight,
                                stride,
                                out dirty,
                                out changedRatio)) {
                            return (ControlBitmapCaptureResult?)null;
                        }

                        if (changedRatio >= FullFrameDirtyRatio)
                            keyFrame = true;
                    }

                    if (keyFrame)
                        dirty = new Int32Rect(0, 0, rendered.PixelWidth, rendered.PixelHeight);

                    int adaptiveQuality = SelectAdaptiveQuality(quality, keyFrame, changedRatio);
                    BitmapSource encodeSource = keyFrame
                        ? rendered.Bitmap
                        : new CroppedBitmap(rendered.Bitmap, dirty);
                    byte[] jpegBytes = EncodeJpeg(encodeSource, adaptiveQuality);
                    if (jpegBytes.Length == 0)
                        return (ControlBitmapCaptureResult?)null;

                    return new ControlBitmapCaptureResult
                    {
                        JpegBytes = jpegBytes,
                        IsDelta = !keyFrame,
                        DirtyX = dirty.X,
                        DirtyY = dirty.Y,
                        DirtyWidth = dirty.Width,
                        DirtyHeight = dirty.Height,
                        PixelWidth = rendered.PixelWidth,
                        PixelHeight = rendered.PixelHeight,
                        LogicalWidth = rendered.LogicalWidth,
                        LogicalHeight = rendered.LogicalHeight,
                        DpiX = rendered.DpiX,
                        DpiY = rendered.DpiY,
                        Quality = adaptiveQuality,
                        ChangedRatio = changedRatio,
                        Sequence = state.AllocateSequence(),
                        BaseSequence = keyFrame ? 0 : state.LastCommittedSequence,
                        CurrentPixels = currentPixels,
                        Stride = stride
                    };
                }).Task;
            }

            private static RenderedElementBitmap? RenderElementBitmap(FrameworkElement element, double dpi) {
                // Ensure layout is up to date.
                element.UpdateLayout();

                var dipWidth = element.ActualWidth;
                var dipHeight = element.ActualHeight;

                if (dipWidth <= 0 || dipHeight <= 0)
                    return null;

                // Detect the actual DPI so the captured bitmap has pixel dimensions
                // consistent with its DPI metadata. When the viewer decodes the JPEG,
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
                    return null;

                // Render via a VisualBrush so we capture exactly the element bounds.
                // Explicitly set Viewbox in absolute DIP coordinates so the brush
                // always maps the element's layout region 1-to-1 to the destination
                // rectangle. Without this, WPF's automatic bounding-box computation
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

                return new RenderedElementBitmap
                {
                    Bitmap = rtb,
                    PixelWidth = pixelWidth,
                    PixelHeight = pixelHeight,
                    LogicalWidth = dipWidth,
                    LogicalHeight = dipHeight,
                    DpiX = renderDpiX,
                    DpiY = renderDpiY
                };
            }

            private static byte[] EncodeJpeg(BitmapSource source, int quality) {
                quality = Math.Max(1, Math.Min(100, quality));

                var encoder = new JpegBitmapEncoder
                {
                    QualityLevel = quality
                };
                encoder.Frames.Add(BitmapFrame.Create(source));

                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            }

            private static bool TryFindDirtyBounds(
                byte[] currentPixels,
                byte[] previousPixels,
                int width,
                int height,
                int stride,
                out Int32Rect dirty,
                out double dirtyRatio) {
                int minX = width;
                int minY = height;
                int maxX = -1;
                int maxY = -1;

                for (int y = 0; y < height; y++) {
                    int row = y * stride;
                    for (int x = 0; x < width; x++) {
                        int i = row + (x * BytesPerPixel);
                        if (!PixelChanged(currentPixels, previousPixels, i))
                            continue;

                        if (x < minX) minX = x;
                        if (y < minY) minY = y;
                        if (x > maxX) maxX = x;
                        if (y > maxY) maxY = y;
                    }
                }

                if (maxX < minX || maxY < minY) {
                    dirty = new Int32Rect();
                    dirtyRatio = 0;
                    return false;
                }

                minX = AlignDown(minX, DirtyBlockSize);
                minY = AlignDown(minY, DirtyBlockSize);
                maxX = Math.Min(width - 1, AlignUp(maxX + 1, DirtyBlockSize) - 1);
                maxY = Math.Min(height - 1, AlignUp(maxY + 1, DirtyBlockSize) - 1);

                dirty = new Int32Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
                dirtyRatio = (double)dirty.Width * dirty.Height / Math.Max(1, width * height);
                return true;
            }

            private static bool PixelChanged(byte[] currentPixels, byte[] previousPixels, int offset) {
                return Math.Abs(currentPixels[offset] - previousPixels[offset]) > ColorDifferenceThreshold ||
                       Math.Abs(currentPixels[offset + 1] - previousPixels[offset + 1]) > ColorDifferenceThreshold ||
                       Math.Abs(currentPixels[offset + 2] - previousPixels[offset + 2]) > ColorDifferenceThreshold ||
                       Math.Abs(currentPixels[offset + 3] - previousPixels[offset + 3]) > ColorDifferenceThreshold;
            }

            private static int SelectAdaptiveQuality(int requestedQuality, bool keyFrame, double dirtyRatio) {
                int quality = Math.Max(1, Math.Min(100, requestedQuality));
                if (keyFrame)
                    return Math.Max(52, Math.Min(quality, 72));
                if (dirtyRatio <= 0.03)
                    return Math.Min(88, quality + 8);
                if (dirtyRatio >= 0.18)
                    return Math.Max(56, quality - 8);
                return quality;
            }

            private static int GetStride(int pixelWidth) {
                return checked(pixelWidth * BytesPerPixel);
            }

            private static int AlignDown(int value, int blockSize) {
                return value <= 0 ? 0 : (value / blockSize) * blockSize;
            }

            private static int AlignUp(int value, int blockSize) {
                if (value <= 0)
                    return 0;
                return ((value + blockSize - 1) / blockSize) * blockSize;
            }

            private sealed class RenderedElementBitmap {
                public RenderTargetBitmap Bitmap { get; set; } = null!;
                public int PixelWidth { get; set; }
                public int PixelHeight { get; set; }
                public double LogicalWidth { get; set; }
                public double LogicalHeight { get; set; }
                public double DpiX { get; set; }
                public double DpiY { get; set; }
            }
        }

        internal sealed class ControlBitmapCaptureState {
            private int _nextSequence = 1;
            private int _deltaFramesSinceKeyFrame;

            public byte[]? PreviousPixels { get; private set; }
            public int PreviousPixelWidth { get; private set; }
            public int PreviousPixelHeight { get; private set; }
            public int PreviousStride { get; private set; }
            public int LastCommittedSequence { get; private set; }
            public DateTime LastKeyFrameUtc { get; private set; } = DateTime.MinValue;

            public bool NeedsKeyFrame(int pixelWidth, int pixelHeight, int stride, TimeSpan keyFrameInterval, int maxDeltaFrames) {
                if (PreviousPixels == null || PreviousPixels.Length == 0)
                    return true;
                if (PreviousPixels.LongLength != (long)stride * pixelHeight)
                    return true;
                if (PreviousPixelWidth != pixelWidth || PreviousPixelHeight != pixelHeight || PreviousStride != stride)
                    return true;
                if (LastKeyFrameUtc == DateTime.MinValue)
                    return true;
                if (DateTime.UtcNow - LastKeyFrameUtc >= keyFrameInterval)
                    return true;
                return _deltaFramesSinceKeyFrame >= maxDeltaFrames;
            }

            public int AllocateSequence() {
                int sequence = _nextSequence++;
                if (_nextSequence == int.MaxValue)
                    _nextSequence = 1;
                if (sequence <= 0)
                    sequence = 1;
                return sequence;
            }

            public void Commit(ControlBitmapCaptureResult result) {
                if (result == null)
                    return;

                PreviousPixels = result.CurrentPixels;
                PreviousPixelWidth = result.PixelWidth;
                PreviousPixelHeight = result.PixelHeight;
                PreviousStride = result.Stride;
                LastCommittedSequence = result.Sequence;

                if (result.IsDelta) {
                    _deltaFramesSinceKeyFrame++;
                } else {
                    _deltaFramesSinceKeyFrame = 0;
                    LastKeyFrameUtc = DateTime.UtcNow;
                }
            }
        }

        internal sealed class ControlBitmapCaptureResult {
            public byte[] JpegBytes { get; set; } = Array.Empty<byte>();
            public bool IsDelta { get; set; }
            public int DirtyX { get; set; }
            public int DirtyY { get; set; }
            public int DirtyWidth { get; set; }
            public int DirtyHeight { get; set; }
            public int PixelWidth { get; set; }
            public int PixelHeight { get; set; }
            public double LogicalWidth { get; set; }
            public double LogicalHeight { get; set; }
            public double DpiX { get; set; }
            public double DpiY { get; set; }
            public int Quality { get; set; }
            public double ChangedRatio { get; set; }
            public int Sequence { get; set; }
            public int BaseSequence { get; set; }
            internal byte[] CurrentPixels { get; set; } = Array.Empty<byte>();
            internal int Stride { get; set; }
        }
#nullable restore
    }
}
