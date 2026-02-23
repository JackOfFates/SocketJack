using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SocketJack.WPF {
    namespace Controller {

        public sealed class ControlShareViewer : IDisposable {
            private readonly ISocket _client;
            private readonly Image _image;
            private readonly Identifier _filterPeer;

            private Identifier _sharePeer;
            private string _controlId;

            private int _frameWidth;
            private int _frameHeight;

            private int _bitmapPixelWidth;
            private int _bitmapPixelHeight;

            private double _bitmapLogicalWidth;
            private double _bitmapLogicalHeight;

            private DateTime _nextMoveSendAt = DateTime.MinValue;
            private DateTime _lastFrameReceivedUtc = DateTime.MinValue;
            private bool _mouseInside;
            private bool _canSendP2p => _client != null && _client.RemoteIdentity != null;
            private DateTime _leftDownAtUtc = DateTime.MinValue;
            private DateTime _rightDownAtUtc = DateTime.MinValue;
            private Point _leftDownPosition;
            private Point _rightDownPosition;
            private bool _leftDownPending;
            private bool _rightDownPending;

            private const double FrameTimeoutSeconds = 3.0;

            private bool IsReceivingFrames {
                get {
                    return (DateTime.UtcNow - _lastFrameReceivedUtc).TotalSeconds < FrameTimeoutSeconds;
                }
            }

            public ControlShareViewer(TcpClient client, Image image, Identifier peer = null)
                : this((ISocket)client, image, peer) { }

            public ControlShareViewer(UdpClient client, Image image, Identifier peer = null)
                : this((ISocket)client, image, peer) { }

            private ControlShareViewer(ISocket client, Image image, Identifier peer) {
                ArgumentNullException.ThrowIfNull(client);
                ArgumentNullException.ThrowIfNull(image);
                _client = client;
                _image = image;
                _filterPeer = peer;
                if (peer != null)
                    _sharePeer = peer;

                _client.Options.Whitelist.Add(typeof(ControlShareFrame));
                _client.Options.Whitelist.Add(typeof(RemoteAction));

                _client.RegisterCallback<ControlShareFrame>(OnFrame);
                _client.RegisterCallback<RemoteAction>(OnRemoteAction);

                _image.IsHitTestVisible = true;
                _image.Focusable = true;
                _image.PreviewMouseMove += Image_MouseMove;
                _image.MouseEnter += Image_MouseEnter;
                _image.MouseLeave += Image_MouseLeave;
                _image.PreviewMouseLeftButtonDown += Image_MouseLeftButtonDown;
                _image.PreviewMouseRightButtonDown += Image_MouseRightButtonDown;
                _image.PreviewMouseLeftButtonUp += Image_MouseLeftButtonUp;
                _image.PreviewMouseRightButtonUp += Image_MouseRightButtonUp;
                _image.PreviewKeyDown += Image_PreviewKeyDown;
                _image.PreviewKeyUp += Image_PreviewKeyUp;
            }

            private void OnRemoteAction(ReceivedEventArgs<RemoteAction> e) {
                // If this client is currently the sharer, it can receive remote actions
                // from the viewer and execute them.
                if (e == null || e.Object == null)
                    return;
                if (_filterPeer != null) {
                    if (e.Connection == null)
                        return;
                    if (e.From?.ID != _filterPeer.ID)
                        return;
                }

                _ = e.Object.PerformAction();
            }

            private void Image_MouseEnter(object sender, MouseEventArgs e) {
                _mouseInside = true;
                if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY)) {
                    SendRemoteAction(RemoteAction.ActionType.MouseEnter, "");
                    return;
                }
                var p = e.GetPosition(_image);
                var x = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                var y = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);
                SendRemoteAction(RemoteAction.ActionType.MouseEnter, $"{x:0.####},{y:0.####}");
            }

            private void Image_MouseLeave(object sender, MouseEventArgs e) {
                // While dragging (mouse captured), WPF still fires MouseLeave when
                // the cursor physically exits the image bounds.  Suppress it so
                // that MouseMove continues to relay position updates during drag.
                if (_leftDownPending || _rightDownPending)
                    return;
                _mouseInside = false;
                if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY)) {
                    SendRemoteAction(RemoteAction.ActionType.MouseLeave, "");
                    return;
                }
                var p = e.GetPosition(_image);
                var x = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                var y = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);
                SendRemoteAction(RemoteAction.ActionType.MouseLeave, $"{x:0.####},{y:0.####}");
            }

            private void SendRemoteAction(RemoteAction.ActionType type, string args, int duration = 50) {
                if (_client == null || !_client.Connected)
                    return;
                if (!_canSendP2p)
                    return;
                if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
                    return;
                if (string.IsNullOrWhiteSpace(_controlId))
                    return;
                if (!IsReceivingFrames)
                    return;

                try {
                    _client.Send(_sharePeer, new RemoteAction
                    {
                        Route = new ElementRoute { ID = _controlId },
                        Action = type,
                        Arguments = args,
                        Duration = duration
                    });
                }
                catch {
                }
            }

            private void OnFrame(ReceivedEventArgs<ControlShareFrame> e) {
                if (e?.Object == null)
                    return;
                if (e.Connection == null)
                    return;
                if (_filterPeer != null && e.From?.ID != _filterPeer.ID)
                    return;

                _lastFrameReceivedUtc = DateTime.UtcNow;

                var msg = e.Object;
                if (msg.JpegBytes == null || msg.JpegBytes.Length == 0)
                    return;

                // Remember sender + control id so we can send input back.
                _sharePeer = e.From;
                _controlId = msg.ControlId;
                if (string.IsNullOrWhiteSpace(_controlId))
                    _controlId = msg.Route == null ? _controlId : msg.Route.ID;

                if (msg.Width > 0)
                    _frameWidth = msg.Width;
                if (msg.Height > 0)
                    _frameHeight = msg.Height;

                // Decode the JPEG on the UI thread so the BitmapImage is created in the
                // correct DPI-awareness context.  Background-thread decoding can cause
                // WPF to fall back to the system DPI, which halves the image's natural
                // size on high-DPI displays.
                var jpegBytes = msg.JpegBytes;
                _image.Dispatcher.BeginInvoke(() =>
                {
                    try {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(jpegBytes);
                        bmp.EndInit();
                        bmp.Freeze();

                        _bitmapPixelWidth = bmp.PixelWidth;
                        _bitmapPixelHeight = bmp.PixelHeight;
                        _bitmapLogicalWidth = bmp.Width;
                        _bitmapLogicalHeight = bmp.Height;

                        _image.Source = bmp;
                    }
                    catch {
                    }
                });
            }

            private bool TryGetRenderedBitmapMetrics(out double drawW, out double drawH, out double offsetX, out double offsetY) {
                drawW = 0;
                drawH = 0;
                offsetX = 0;
                offsetY = 0;

                var iw = _image.ActualWidth;
                var ih = _image.ActualHeight;
                if (iw <= 1 || ih <= 1)
                    return false;

                // BitmapImage.Width/Height are the logical dimensions WPF uses for Stretch.Uniform
                // layout.  They can differ from _frameWidth/_frameHeight when the JPEG was captured
                // at a DPI other than 96 (e.g. on a high-DPI screen), causing the rendered image to
                // occupy a different area than the frame-size math would predict.
                var bw = _bitmapLogicalWidth;
                var bh = _bitmapLogicalHeight;

                if (bw <= 0 || bh <= 0) {
                    // No decoded frame yet – fall back to sender-reported logical size.
                    bw = _frameWidth;
                    bh = _frameHeight;
                }

                if (bw <= 0 || bh <= 0) {
                    // Final fallback: treat the Image element as filling its content exactly.
                    bw = iw;
                    bh = ih;
                }

                if (bw <= 0 || bh <= 0)
                    return false;

                var scale = Math.Min(iw / bw, ih / bh);
                if (scale <= 0)
                    return false;

                drawW = bw * scale;
                drawH = bh * scale;
                offsetX = (iw - drawW) / 2.0;
                offsetY = (ih - drawH) / 2.0;
                return drawW > 1 && drawH > 1;
            }

            private void Image_MouseMove(object sender, MouseEventArgs e) {
                if (_client == null || !_client.Connected)
                    return;
                if (!_canSendP2p)
                    return;
                if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
                    return;
                if (string.IsNullOrWhiteSpace(_controlId))
                    return;
                if (!IsReceivingFrames)
                    return;

                var now = DateTime.UtcNow;
                if (now < _nextMoveSendAt)
                    return;
                _nextMoveSendAt = now.AddMilliseconds(16.6);

                if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
                    return;

                var p = e.GetPosition(_image);
                var x = (p.X - offsetX) / drawW;
                var y = (p.Y - offsetY) / drawH;

                if (x < 0)
                    x = 0;
                if (y < 0)
                    y = 0;
                if (x > 1)
                    x = 1;
                if (y > 1)
                    y = 1;

                if (_mouseInside || _leftDownPending || _rightDownPending)
                    SendRemoteAction(RemoteAction.ActionType.MouseMove, $"{x:0.####},{y:0.####}");
            }

            private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
                try {
                    _image.CaptureMouse();
                    _image.Focus();
                }
                catch {
                }

                if (_client == null || !_client.Connected)
                    return;
                if (!_canSendP2p)
                    return;
                if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
                    return;
                if (string.IsNullOrWhiteSpace(_controlId))
                    return;
                if (!IsReceivingFrames)
                    return;

                if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
                    return;

                var p = e.GetPosition(_image);
                var x = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                var y = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);

                if (_leftDownPending) {
                    _leftDownPending = false;
                    SendRemoteAction(RemoteAction.ActionType.MouseUp, $"Left,{_leftDownPosition.X:0.####},{_leftDownPosition.Y:0.####}");
                }

                _leftDownAtUtc = DateTime.UtcNow;
                _leftDownPosition = new Point(x, y);
                _leftDownPending = true;
                SendRemoteAction(RemoteAction.ActionType.MouseDown, $"Left,{x:0.####},{y:0.####}");
                e.Handled = false;
            }

            private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
                try {
                    if (Mouse.Captured == _image)
                        Mouse.Capture(null);
                }
                catch {
                }
                if (!_leftDownPending)
                    return;
                _leftDownPending = false;
                var holdMs = Math.Max(1, (int)(DateTime.UtcNow - _leftDownAtUtc).TotalMilliseconds);

                var upX = _leftDownPosition.X;
                var upY = _leftDownPosition.Y;
                if (TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY)) {
                    var p = e.GetPosition(_image);
                    upX = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                    upY = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);
                }

                SendRemoteAction(RemoteAction.ActionType.MouseUp, $"Left,{upX:0.####},{upY:0.####}",
                    holdMs);
            }

            private void Image_MouseRightButtonDown(object sender, MouseButtonEventArgs e) {
                try {
                    _image.CaptureMouse();
                    _image.Focus();
                }
                catch {
                }
                if (_rightDownPending) {
                    _rightDownPending = false;
                    SendRemoteAction(RemoteAction.ActionType.MouseUp, $"Right,{_rightDownPosition.X:0.####},{_rightDownPosition.Y:0.####}", 50);
                }
                if (_client == null || !_client.Connected)
                    return;
                if (!_canSendP2p)
                    return;
                if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
                    return;
                if (string.IsNullOrWhiteSpace(_controlId))
                    return;
                if (!IsReceivingFrames)
                    return;

                if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
                    return;

                var p = e.GetPosition(_image);
                var x = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                var y = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);

                _rightDownAtUtc = DateTime.UtcNow;
                _rightDownPosition = new Point(x, y);
                _rightDownPending = true;
                SendRemoteAction(RemoteAction.ActionType.MouseDown, $"Right,{x:0.####},{y:0.####}");
                e.Handled = false;
            }

            private void Image_MouseRightButtonUp(object sender, MouseButtonEventArgs e) {
                try {
                    if (Mouse.Captured == _image)
                        Mouse.Capture(null);
                }
                catch {
                }
                if (!_rightDownPending)
                    return;
                _rightDownPending = false;
                var holdMs = Math.Max(1, (int)(DateTime.UtcNow - _rightDownAtUtc).TotalMilliseconds);

                var upX = _rightDownPosition.X;
                var upY = _rightDownPosition.Y;
                if (TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY)) {
                    var p = e.GetPosition(_image);
                    upX = Math.Clamp((p.X - offsetX) / drawW, 0, 1);
                    upY = Math.Clamp((p.Y - offsetY) / drawH, 0, 1);
                }

                SendRemoteAction(RemoteAction.ActionType.MouseUp, $"Right,{upX:0.####},{upY:0.####}",
                    holdMs);
            }

            private void Image_PreviewKeyDown(object sender, KeyEventArgs e) {
                // When Alt is held, e.Key becomes Key.System; the real key is in e.SystemKey.
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk <= 0)
                    return;

                SendRemoteAction(RemoteAction.ActionType.Keystrokes, ((char)vk).ToString(), 50);
            }

            private void Image_PreviewKeyUp(object sender, KeyEventArgs e) {
                // SimulateKeystrokes already raises both KeyDown and KeyUp on the
                // receiving side, so no additional action is needed here.  The
                // handler is registered so the event can be extended in the future.
            }

            public void Dispose() {
                _image.PreviewMouseMove -= Image_MouseMove;
                _image.MouseEnter -= Image_MouseEnter;
                _image.MouseLeave -= Image_MouseLeave;
                _image.PreviewMouseLeftButtonDown -= Image_MouseLeftButtonDown;
                _image.PreviewMouseLeftButtonUp -= Image_MouseLeftButtonUp;
                _image.PreviewMouseRightButtonDown -= Image_MouseRightButtonDown;
                _image.PreviewMouseRightButtonUp -= Image_MouseRightButtonUp;
                _image.PreviewKeyDown -= Image_PreviewKeyDown;
                _image.PreviewKeyUp -= Image_PreviewKeyUp;

                try {
                    _client.RemoveCallback<ControlShareFrame>(OnFrame);
                }
                catch {
                }

                try {
                    _client.RemoveCallback<RemoteAction>(OnRemoteAction);
                }
                catch {
                }
            }
        }
    }
}