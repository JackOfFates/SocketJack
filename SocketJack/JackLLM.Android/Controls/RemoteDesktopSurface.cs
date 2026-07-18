#if ANDROID
using Android.Views;
#endif

namespace JackLLM.Mobile.Controls;

public sealed class RemoteDesktopSurface : Grid
{
#if ANDROID
    private Android.Views.View? _platformView;
    private float _density = 1f;
    private double _downX;
    private double _downY;
    private long _downAt;
    private bool _dragging;
    private bool _multiGesture;
    private bool _multiMoved;
    private long _multiStartedAt;
    private double _previousCenterX;
    private double _previousCenterY;
    private double _previousDistance;
    private long _lastTapAt;
#endif
    private double _lastTapX;
    private double _lastTapY;

    public event EventHandler<RemotePointEventArgs>? RemoteTap;
    public event EventHandler<RemotePointEventArgs>? RemoteDoubleTap;
#pragma warning disable CS0067 // iOS has no portable two-finger/right-click recognizer; trackpad support can be added natively.
    public event EventHandler<RemotePointEventArgs>? RemoteRightTap;
#pragma warning restore CS0067
    public event EventHandler<RemoteDragEventArgs>? RemoteDrag;
    public event EventHandler<RemoteTransformEventArgs>? RemoteTransform;

#if IOS
    public RemoteDesktopSurface()
    {
        var tap = new TapGestureRecognizer { NumberOfTapsRequired = 1 };
        tap.Tapped += (_, args) =>
        {
            Point? point = args.GetPosition(this);
            if (point is null) return;
            _lastTapX = point.Value.X;
            _lastTapY = point.Value.Y;
            RemoteTap?.Invoke(this, new RemotePointEventArgs(point.Value.X, point.Value.Y));
        };
        var doubleTap = new TapGestureRecognizer { NumberOfTapsRequired = 2 };
        doubleTap.Tapped += (_, args) =>
        {
            Point? point = args.GetPosition(this);
            if (point is not null) RemoteDoubleTap?.Invoke(this, new RemotePointEventArgs(point.Value.X, point.Value.Y));
        };
        var pan = new PanGestureRecognizer();
        pan.PanUpdated += (_, args) =>
        {
            double x = Math.Clamp(_lastTapX + args.TotalX, 0, Math.Max(0, Width));
            double y = Math.Clamp(_lastTapY + args.TotalY, 0, Math.Max(0, Height));
            RemoteDragPhase phase = args.StatusType switch
            {
                GestureStatus.Started => RemoteDragPhase.Started,
                GestureStatus.Completed or GestureStatus.Canceled => RemoteDragPhase.Completed,
                _ => RemoteDragPhase.Moved
            };
            if (phase == RemoteDragPhase.Started && (_lastTapX <= 0 || _lastTapY <= 0))
            {
                _lastTapX = Width / 2d;
                _lastTapY = Height / 2d;
                x = _lastTapX;
                y = _lastTapY;
            }
            RemoteDrag?.Invoke(this, new RemoteDragEventArgs(x, y, phase));
            if (phase == RemoteDragPhase.Completed) { _lastTapX = x; _lastTapY = y; }
        };
        var pinch = new PinchGestureRecognizer();
        pinch.PinchUpdated += (_, args) =>
        {
            if (args.Status == GestureStatus.Running)
                RemoteTransform?.Invoke(this, new RemoteTransformEventArgs(
                    args.ScaleOrigin.X * Width, args.ScaleOrigin.Y * Height, 0, 0, args.Scale));
        };
        GestureRecognizers.Add(tap);
        GestureRecognizers.Add(doubleTap);
        GestureRecognizers.Add(pan);
        GestureRecognizers.Add(pinch);
    }
#endif

#if ANDROID
    protected override void OnHandlerChanged()
    {
        if (_platformView is not null) _platformView.Touch -= OnNativeTouch;
        base.OnHandlerChanged();
        _platformView = Handler?.PlatformView as Android.Views.View;
        if (_platformView is null) return;
        _density = _platformView.Context?.Resources?.DisplayMetrics?.Density ?? 1f;
        _platformView.Touch += OnNativeTouch;
    }

    private void OnNativeTouch(object? sender, Android.Views.View.TouchEventArgs e)
    {
        MotionEvent motion = e.Event!;
        double X(int index) => motion.GetX(index) / Math.Max(0.1f, _density);
        double Y(int index) => motion.GetY(index) / Math.Max(0.1f, _density);
        long now = motion.EventTime;

        switch (motion.ActionMasked)
        {
            case MotionEventActions.Down:
                _downX = X(0); _downY = Y(0); _downAt = now; _dragging = false; _multiGesture = false;
                break;
            case MotionEventActions.PointerDown when motion.PointerCount >= 2:
                _multiGesture = true; _multiMoved = false; _multiStartedAt = now;
                _previousCenterX = (X(0) + X(1)) / 2d;
                _previousCenterY = (Y(0) + Y(1)) / 2d;
                _previousDistance = Distance(X(0), Y(0), X(1), Y(1));
                break;
            case MotionEventActions.Move when motion.PointerCount >= 2:
                double centerX = (X(0) + X(1)) / 2d;
                double centerY = (Y(0) + Y(1)) / 2d;
                double distance = Distance(X(0), Y(0), X(1), Y(1));
                double dx = centerX - _previousCenterX;
                double dy = centerY - _previousCenterY;
                double scale = _previousDistance <= 0 ? 1d : distance / _previousDistance;
                if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5 || Math.Abs(scale - 1d) > 0.005) _multiMoved = true;
                RemoteTransform?.Invoke(this, new RemoteTransformEventArgs(centerX, centerY, dx, dy, scale));
                _previousCenterX = centerX; _previousCenterY = centerY; _previousDistance = distance;
                break;
            case MotionEventActions.Move when !_multiGesture:
                double moveX = X(0), moveY = Y(0);
                if (!_dragging && now - _downAt >= 450 && Distance(_downX, _downY, moveX, moveY) >= 3)
                {
                    _dragging = true;
                    RemoteDrag?.Invoke(this, new RemoteDragEventArgs(_downX, _downY, RemoteDragPhase.Started));
                }
                if (_dragging) RemoteDrag?.Invoke(this, new RemoteDragEventArgs(moveX, moveY, RemoteDragPhase.Moved));
                break;
            case MotionEventActions.PointerUp:
                if (_multiGesture && !_multiMoved && now - _multiStartedAt < 400)
                    RemoteRightTap?.Invoke(this, new RemotePointEventArgs(_previousCenterX, _previousCenterY));
                break;
            case MotionEventActions.Up:
                double upX = X(0), upY = Y(0);
                if (_dragging)
                {
                    RemoteDrag?.Invoke(this, new RemoteDragEventArgs(upX, upY, RemoteDragPhase.Completed));
                }
                else if (!_multiGesture && now - _downAt < 500 && Distance(_downX, _downY, upX, upY) < 14)
                {
                    if (now - _lastTapAt < 350 && Distance(_lastTapX, _lastTapY, upX, upY) < 28)
                    {
                        _lastTapAt = 0;
                        RemoteDoubleTap?.Invoke(this, new RemotePointEventArgs(upX, upY));
                    }
                    else
                    {
                        _lastTapAt = now; _lastTapX = upX; _lastTapY = upY;
                        RemoteTap?.Invoke(this, new RemotePointEventArgs(upX, upY));
                    }
                }
                _dragging = false; _multiGesture = false;
                break;
            case MotionEventActions.Cancel:
                if (_dragging) RemoteDrag?.Invoke(this, new RemoteDragEventArgs(_downX, _downY, RemoteDragPhase.Completed));
                _dragging = false; _multiGesture = false;
                break;
        }
        e.Handled = true;
    }
#endif

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x2 - x1, dy = y2 - y1;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed class RemotePointEventArgs(double x, double y) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
}

public sealed class RemoteTransformEventArgs(double centerX, double centerY, double deltaX, double deltaY, double scaleFactor) : EventArgs
{
    public double CenterX { get; } = centerX;
    public double CenterY { get; } = centerY;
    public double DeltaX { get; } = deltaX;
    public double DeltaY { get; } = deltaY;
    public double ScaleFactor { get; } = scaleFactor;
}

public sealed class RemoteDragEventArgs(double x, double y, RemoteDragPhase phase) : EventArgs
{
    public double X { get; } = x;
    public double Y { get; } = y;
    public RemoteDragPhase Phase { get; } = phase;
}

public enum RemoteDragPhase { Started, Moved, Completed }

public sealed class RemoteCursorDrawable : IDrawable
{
    public double X { get; set; }
    public double Y { get; set; }
    public bool Visible { get; set; }
    public bool Pressed { get; set; }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (!Visible) return;
        float x = (float)X, y = (float)Y;
        canvas.SaveState();
        canvas.FillColor = Pressed ? Color.FromArgb("#FACC15") : Color.FromArgb("#22D3EE");
        canvas.StrokeColor = Colors.Black;
        canvas.StrokeSize = 2;
        var path = new PathF();
        path.MoveTo(x, y);
        path.LineTo(x + 4, y + 19);
        path.LineTo(x + 9, y + 13);
        path.LineTo(x + 15, y + 22);
        path.LineTo(x + 19, y + 19);
        path.LineTo(x + 13, y + 11);
        path.LineTo(x + 21, y + 9);
        path.Close();
        canvas.FillPath(path);
        canvas.DrawPath(path);
        canvas.RestoreState();
    }
}
