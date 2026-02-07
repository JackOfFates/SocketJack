using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace SocketJack.WPFController;

public sealed class ControlShareViewer : IDisposable {
    private readonly TcpClient _client;
    private readonly Image _image;

    private Identifier _sharePeer;
    private string _controlId;

    private int _frameWidth;
    private int _frameHeight;

    private int _bitmapPixelWidth;
    private int _bitmapPixelHeight;

    private DateTime _nextMoveSendAt = DateTime.MinValue;
    private bool _mouseInside;
    private bool _canSendP2p;

    public ControlShareViewer(TcpClient client, Image image) {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _image = image ?? throw new ArgumentNullException(nameof(image));

        _client.Options.Whitelist.Add(typeof(ControlShareFrame));
        _client.Options.Whitelist.Add(typeof(ControlShareInput));
        _client.Options.Whitelist.Add(typeof(ControlShareRemoteAction));

        _client.RegisterCallback<ControlShareFrame>(OnFrame);
        _client.RegisterCallback<ControlShareRemoteAction>(OnRemoteAction);

        // P2P sends require RemoteIdentity to be set; wait until identification.
        _canSendP2p = _client.RemoteIdentity != null;
        _client.OnIdentified += (_, _) => { _canSendP2p = true; };

        _image.IsHitTestVisible = true;
        _image.Focusable = false;
        _image.PreviewMouseMove += Image_MouseMove;
        _image.MouseEnter += Image_MouseEnter;
        _image.MouseLeave += Image_MouseLeave;
        _image.PreviewMouseLeftButtonDown += Image_MouseLeftButtonDown;
        _image.PreviewMouseRightButtonDown += Image_MouseRightButtonDown;
        _image.PreviewMouseLeftButtonUp += Image_MouseLeftButtonUp;
        _image.PreviewMouseRightButtonUp += Image_MouseRightButtonUp;
    }

    private void OnRemoteAction(ReceivedEventArgs<ControlShareRemoteAction> e) {
        // If this client is currently the sharer, it can receive remote actions
        // from the viewer. Convert to RemoteAction and execute.
        if (e == null || e.Object == null)
            return;

        // RemoteAction needs a route; use ControlId to locate the element.
        var route = new ElementRoute { ID = e.Object.ControlId };

        var ra = new RemoteAction(route) {
            Action = e.Object.Action,
            Arguments = e.Object.Arguments,
            Duration = e.Object.Duration
        };
 
        _ = ra.PerformAction();
    }

    private void Image_MouseEnter(object sender, MouseEventArgs e) {
        _mouseInside = true;
        var iw = _image.ActualWidth;
        var ih = _image.ActualHeight;
        if (iw <= 1 || ih <= 1) {
            SendRemoteAction(RemoteAction.ActionType.MouseEnter, "");
            return;
        }

        var fw = _frameWidth > 0 ? _frameWidth : (int)Math.Round(iw);
        var fh = _frameHeight > 0 ? _frameHeight : (int)Math.Round(ih);

        var scale = Math.Min(iw / fw, ih / fh);
        if (scale <= 0) {
            SendRemoteAction(RemoteAction.ActionType.MouseEnter, "");
            return;
        }
        var drawW = fw * scale;
        var drawH = fh * scale;
        var offsetX = (iw - drawW) / 2.0;
        var offsetY = (ih - drawH) / 2.0;

        // Convert to root-window coordinates then subtract Image's top-left to ensure
        // we are always working in Image-local space (prevents window-relative drift).
        var root = Application.Current?.MainWindow as IInputElement;
        var pRoot = root == null ? e.GetPosition(_image) : e.GetPosition(root);
        var imageOrigin = _image.TranslatePoint(new Point(0, 0), root == null ? _image : (UIElement)root);
        var p = new Point(pRoot.X - imageOrigin.X, pRoot.Y - imageOrigin.Y);
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
        SendRemoteAction(RemoteAction.ActionType.MouseEnter, $"{x:0.####},{y:0.####}");
    }

    private void Image_MouseLeave(object sender, MouseEventArgs e) {
        _mouseInside = false;
        var iw = _image.ActualWidth;
        var ih = _image.ActualHeight;
        if (iw <= 1 || ih <= 1) {
            SendRemoteAction(RemoteAction.ActionType.MouseLeave, "");
            return;
        }

        var fw = _frameWidth > 0 ? _frameWidth : (int)Math.Round(iw);
        var fh = _frameHeight > 0 ? _frameHeight : (int)Math.Round(ih);

        var scale = Math.Min(iw / fw, ih / fh);
        if (scale <= 0) {
            SendRemoteAction(RemoteAction.ActionType.MouseLeave, "");
            return;
        }
        var drawW = fw * scale;
        var drawH = fh * scale;
        var offsetX = (iw - drawW) / 2.0;
        var offsetY = (ih - drawH) / 2.0;

        var root = Application.Current?.MainWindow as IInputElement;
        var pRoot = root == null ? e.GetPosition(_image) : e.GetPosition(root);
        var imageOrigin = _image.TranslatePoint(new Point(0, 0), root == null ? _image : (UIElement)root);
        var p = new Point(pRoot.X - imageOrigin.X, pRoot.Y - imageOrigin.Y);
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
        SendRemoteAction(RemoteAction.ActionType.MouseLeave, $"{x:0.####},{y:0.####}");
    }

    private void SendRemoteAction(RemoteAction.ActionType type, string args) {
        if (_client == null || !_client.Connected)
            return;
        if (!_canSendP2p)
            return;
        if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
            return;
        if (string.IsNullOrWhiteSpace(_controlId))
            return;

        try {
            _client.Send(_sharePeer, new ControlShareRemoteAction {
                ControlId = _controlId,
                Action = type,
                Arguments = args,
                Duration = 50
            });
        } catch {
        }
    }

    private void OnFrame(ReceivedEventArgs<ControlShareFrame> e) {
        if (e?.Object == null)
            return;
        if (e.Connection == null || e.Connection.Identity == null)
            return;

        var msg = e.Object;
        if (msg.JpegBytes == null || msg.JpegBytes.Length == 0)
            return;

        // Remember sender + control id so we can send input back.
        _sharePeer = e.Connection.Identity;
        _controlId = msg.ControlId;
        if (string.IsNullOrWhiteSpace(_controlId))
            _controlId = msg.Route == null ? _controlId : msg.Route.ID;

        if (msg.Width > 0)
            _frameWidth = msg.Width;
        if (msg.Height > 0)
            _frameHeight = msg.Height;

        try {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(msg.JpegBytes);
            bmp.EndInit();
            bmp.Freeze();

            _bitmapPixelWidth = bmp.PixelWidth;
            _bitmapPixelHeight = bmp.PixelHeight;

            _image.Dispatcher.BeginInvoke(() => _image.Source = bmp);
        } catch {
        }
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

        // Use the sender-reported frame size as the source coordinate space.
        // The decoded JPEG pixel size can differ from the sender's logical size (layout scaling).
        var bw = _frameWidth;
        var bh = _frameHeight;

        if (bw <= 0 || bh <= 0) {
            // Fallback to decoded bitmap pixel size.
            bw = _bitmapPixelWidth;
            bh = _bitmapPixelHeight;
        }

        if (bw <= 0 || bh <= 0) {
            // Final fallback to Image size.
            bw = (int)Math.Round(iw);
            bh = (int)Math.Round(ih);
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

        var now = DateTime.UtcNow;
        if (now < _nextMoveSendAt)
            return;
        _nextMoveSendAt = now.AddMilliseconds(33);

        if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
            return;

        var root = Application.Current?.MainWindow as IInputElement;
        var pRoot = root == null ? e.GetPosition(_image) : e.GetPosition(root);
        var imageOrigin = _image.TranslatePoint(new Point(0, 0), root == null ? _image : (UIElement)root);
        var p = new Point(pRoot.X - imageOrigin.X, pRoot.Y - imageOrigin.Y);
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

        if (_mouseInside)
            SendRemoteAction(RemoteAction.ActionType.MouseMove, $"{x:0.####},{y:0.####}");

        _client.Send(_sharePeer, new ControlShareInput {
            ControlId = _controlId,
            X = x,
            Y = y,
            IsMove = true,
            IsClick = false,
            Button = 0
        });
    }

    private void Image_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        try {
            _image.CaptureMouse();
        } catch {
        }
        if (_client == null || !_client.Connected)
            return;
        if (!_canSendP2p)
            return;
        if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
            return;
        if (string.IsNullOrWhiteSpace(_controlId))
            return;

        if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
            return;

        var p = e.GetPosition(_image);
        var x = (p.X - offsetX) / drawW;
        var y = (p.Y - offsetY) / drawH;

        _client.Send(_sharePeer, new ControlShareInput {
            ControlId = _controlId,
            X = x,
            Y = y,
            IsMove = false,
            IsClick = true,
            Button = 0
        });

        SendRemoteAction(RemoteAction.ActionType.Click, $"{MouseButton.Left},{x:0.####},{y:0.####}");
        e.Handled = false;
    }

    private void Image_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) {
        try {
            if (Mouse.Captured == _image)
                Mouse.Capture(null);
        } catch {
        }
    }

    private void Image_MouseRightButtonDown(object sender, MouseButtonEventArgs e) {
        try {
            _image.CaptureMouse();
        } catch {
        }
        if (_client == null || !_client.Connected)
            return;
        if (!_canSendP2p)
            return;
        if (_sharePeer == null || string.IsNullOrWhiteSpace(_sharePeer.ID))
            return;
        if (string.IsNullOrWhiteSpace(_controlId))
            return;

        if (!TryGetRenderedBitmapMetrics(out var drawW, out var drawH, out var offsetX, out var offsetY))
            return;

        var p = e.GetPosition(_image);
        var x = (p.X - offsetX) / drawW;
        var y = (p.Y - offsetY) / drawH;

        _client.Send(_sharePeer, new ControlShareInput {
            ControlId = _controlId,
            X = x,
            Y = y,
            IsMove = false,
            IsClick = true,
            Button = 1
        });

        SendRemoteAction(RemoteAction.ActionType.Click, $"{MouseButton.Right},{x:0.####},{y:0.####}");
        e.Handled = false;
    }

    private void Image_MouseRightButtonUp(object sender, MouseButtonEventArgs e) {
        try {
            if (Mouse.Captured == _image)
                Mouse.Capture(null);
        } catch {
        }
    }

    public void Dispose() {
        _image.PreviewMouseMove -= Image_MouseMove;
        _image.MouseEnter -= Image_MouseEnter;
        _image.MouseLeave -= Image_MouseLeave;
        _image.PreviewMouseLeftButtonDown -= Image_MouseLeftButtonDown;
        _image.PreviewMouseLeftButtonUp -= Image_MouseLeftButtonUp;
        _image.PreviewMouseRightButtonDown -= Image_MouseRightButtonDown;
        _image.PreviewMouseRightButtonUp -= Image_MouseRightButtonUp;

        try {
            _client.RemoveCallback<ControlShareFrame>(OnFrame);
        } catch {
        }

        try {
            _client.RemoveCallback<ControlShareRemoteAction>(OnRemoteAction);
        } catch {
        }
    }
}
