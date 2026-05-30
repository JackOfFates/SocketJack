using System.Runtime.CompilerServices;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace SocketJack.WPF {
    namespace Controller {

        internal sealed class ClickIndicatorAdorner : Adorner {

            private readonly Brush _brush;
            private static readonly TimeSpan RemoteFadeDuration = TimeSpan.FromMilliseconds(180);
            private const double RemoteMovePixelsPerMillisecond = 0.82;
            private const double RemoteNearDistance = 56;
            private const double RemoteClickHitSize = 34;

            // Classic arrow cursor geometry (tip at origin).
            private static readonly Point[] _cursorPoints = {
                new(0, 0),
                new(0, 15),
                new(4, 11.5),
                new(6.5, 16.5),
                new(8.5, 15.5),
                new(6, 10.5),
                new(10, 10.5)
            };

            private static readonly Pen _outlinePen = CreateOutlinePen();

            private static Pen CreateOutlinePen() {
                var pen = new Pen(Brushes.Black, 1.0);
                pen.Freeze();
                return pen;
            }

            private static readonly ConditionalWeakTable<FrameworkElement, ActiveEntry> _active = new();
            private static readonly ConditionalWeakTable<FrameworkElement, RemoteEntry> _remoteActive = new();
            private static readonly ConditionalWeakTable<FrameworkElement, RemoteOverlayHost> _remoteOverlayHosts = new();
            private static readonly ConditionalWeakTable<FrameworkElement, RemoteOverlayEntry> _remoteOverlayActive = new();
            private static WeakReference<FrameworkElement>? _lastRemoteElement;
            private static Point? _lastRemotePosition;
            private static bool _remoteCursorHiddenByUser;
            internal static event EventHandler? RemoteCursorHiddenChanged;

            internal static readonly DependencyProperty CursorXProperty =
                DependencyProperty.Register(
                    nameof(CursorX),
                    typeof(double),
                    typeof(ClickIndicatorAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            internal static readonly DependencyProperty CursorYProperty =
                DependencyProperty.Register(
                    nameof(CursorY),
                    typeof(double),
                    typeof(ClickIndicatorAdorner),
                    new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

            internal double CursorX {
                get => (double)GetValue(CursorXProperty);
                set => SetValue(CursorXProperty, value);
            }

            internal double CursorY {
                get => (double)GetValue(CursorYProperty);
                set => SetValue(CursorYProperty, value);
            }

            internal static bool RemoteCursorHiddenByUser => _remoteCursorHiddenByUser;

            private sealed class ActiveEntry {
                internal ClickIndicatorAdorner Adorner;
                internal AdornerLayer Layer;
            }

            private sealed class RemoteEntry {
                internal FrameworkElement Element;
                internal ClickIndicatorAdorner Adorner;
                internal AdornerLayer Layer;
                internal MouseEventHandler MouseMoveHandler;
                internal MouseEventHandler MouseLeaveHandler;
                internal MouseButtonEventHandler PreviewMouseDownHandler;
                internal bool IsDimmed;
            }

            private sealed class RemoteOverlayHost {
                internal Canvas Host;
            }

            private sealed class RemoteOverlayEntry {
                internal FrameworkElement Element;
                internal Canvas Host;
                internal RemoteCursorVisual Visual;
                internal MouseEventHandler MouseMoveHandler;
                internal MouseEventHandler MouseLeaveHandler;
                internal MouseButtonEventHandler PreviewMouseDownHandler;
                internal bool IsDimmed;
            }

            private sealed class RemoteCursorVisual : FrameworkElement {
                private readonly Brush _brush;

                internal RemoteCursorVisual(Brush brush) {
                    _brush = brush;
                    Width = RemoteClickHitSize;
                    Height = RemoteClickHitSize;
                    IsHitTestVisible = false;
                }

                internal Point CurrentPosition {
                    get {
                        double left = Canvas.GetLeft(this);
                        double top = Canvas.GetTop(this);
                        return new Point(double.IsNaN(left) ? 0 : left, double.IsNaN(top) ? 0 : top);
                    }
                }

                protected override void OnRender(DrawingContext drawingContext) {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open()) {
                        ctx.BeginFigure(_cursorPoints[0], true, true);
                        for (int i = 1; i < _cursorPoints.Length; i++)
                            ctx.LineTo(_cursorPoints[i], true, false);
                    }
                    geo.Freeze();
                    drawingContext.DrawGeometry(_brush, _outlinePen, geo);
                }

                internal void AnimateTo(Point target) {
                    double distance = Distance(CurrentPosition, target);
                    if (distance < 0.5) {
                        Canvas.SetLeft(this, target.X);
                        Canvas.SetTop(this, target.Y);
                        return;
                    }

                    int durationMs = Math.Max(120, Math.Min(680, (int)Math.Round(distance / RemoteMovePixelsPerMillisecond)));
                    var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
                    BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(target.X, TimeSpan.FromMilliseconds(durationMs)) {
                        EasingFunction = easing
                    }, HandoffBehavior.SnapshotAndReplace);
                    BeginAnimation(Canvas.TopProperty, new DoubleAnimation(target.Y, TimeSpan.FromMilliseconds(durationMs)) {
                        EasingFunction = easing
                    }, HandoffBehavior.SnapshotAndReplace);
                }

                internal void AnimateOpacityTo(double opacity, TimeSpan duration) {
                    BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) {
                        EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                    }, HandoffBehavior.SnapshotAndReplace);
                }
            }

            private ClickIndicatorAdorner(UIElement adornedElement, Point position, Brush brush) : base(adornedElement) {
                _brush = brush;
                CursorX = position.X;
                CursorY = position.Y;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext) {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open()) {
                    ctx.BeginFigure(new Point(CursorX + _cursorPoints[0].X, CursorY + _cursorPoints[0].Y), true, true);
                    for (int i = 1; i < _cursorPoints.Length; i++)
                        ctx.LineTo(new Point(CursorX + _cursorPoints[i].X, CursorY + _cursorPoints[i].Y), true, false);
                }
                geo.Freeze();
                drawingContext.DrawGeometry(_brush, _outlinePen, geo);
            }

            internal static void Show(FrameworkElement element, Point position, Brush brush = null) {
                var layer = AdornerLayer.GetAdornerLayer(element);
                if (layer is null)
                    return;

                if (_active.TryGetValue(element, out var existing)) {
                    existing.Layer.Remove(existing.Adorner);
                    _active.Remove(element);
                }

                var adorner = new ClickIndicatorAdorner(element, position, brush == null ? Brushes.Red : brush);
                layer.Add(adorner);
                _active.Add(element, new ActiveEntry { Adorner = adorner, Layer = layer });
            }

            internal static void Hide(FrameworkElement element) {
                if (!_active.TryGetValue(element, out var entry))
                    return;
                entry.Layer.Remove(entry.Adorner);
                _active.Remove(element);
            }

            internal static void RegisterRemoteCursorHost(FrameworkElement element, Canvas host) {
                if (element == null || host == null)
                    return;

                _remoteOverlayHosts.Remove(element);
                _remoteOverlayHosts.Add(element, new RemoteOverlayHost { Host = host });
            }

            internal static void ShowRemoteCursor(FrameworkElement element, Point position) {
                if (element == null)
                    return;

                _lastRemoteElement = new WeakReference<FrameworkElement>(element);
                _lastRemotePosition = position;

                if (_remoteCursorHiddenByUser)
                    return;

                if (TryShowRemoteOverlayCursor(element, position))
                    return;

                var layer = AdornerLayer.GetAdornerLayer(element);
                if (layer is null)
                    return;

                if (_remoteActive.TryGetValue(element, out var existing)) {
                    existing.Adorner.AnimateRemoteCursorTo(position);
                    UpdateRemoteCursorDim(existing, Mouse.GetPosition(element));
                    return;
                }

                var adorner = new ClickIndicatorAdorner(element, position, Brushes.White) {
                    Opacity = 0
                };
                var entry = new RemoteEntry {
                    Element = element,
                    Adorner = adorner,
                    Layer = layer
                };
                entry.MouseMoveHandler = (_, e) => UpdateRemoteCursorDim(entry, e.GetPosition(element));
                entry.MouseLeaveHandler = (_, _) => UpdateRemoteCursorDim(entry, farAway: true);
                entry.PreviewMouseDownHandler = (_, e) => {
                    if (!IsPointOnRemoteCursor(entry.Adorner, e.GetPosition(element)))
                        return;

                    e.Handled = true;
                    HideRemoteCursor(element, userInitiated: true);
                };

                element.PreviewMouseMove += entry.MouseMoveHandler;
                element.MouseLeave += entry.MouseLeaveHandler;
                element.PreviewMouseDown += entry.PreviewMouseDownHandler;
                layer.Add(adorner);
                _remoteActive.Add(element, entry);
                adorner.AnimateOpacityTo(1, RemoteFadeDuration);
                UpdateRemoteCursorDim(entry, Mouse.GetPosition(element));
            }

            private static bool TryShowRemoteOverlayCursor(FrameworkElement element, Point position) {
                if (!_remoteOverlayHosts.TryGetValue(element, out var hostEntry) || hostEntry.Host == null)
                    return false;

                Canvas host = hostEntry.Host;
                Point hostPosition = TranslatePointSafe(element, position, host);
                if (_remoteOverlayActive.TryGetValue(element, out var existing)) {
                    existing.Visual.AnimateTo(hostPosition);
                    UpdateRemoteCursorDim(existing, Mouse.GetPosition(host));
                    return true;
                }

                var visual = new RemoteCursorVisual(Brushes.White) {
                    Opacity = 0
                };
                Canvas.SetLeft(visual, hostPosition.X);
                Canvas.SetTop(visual, hostPosition.Y);

                var entry = new RemoteOverlayEntry {
                    Element = element,
                    Host = host,
                    Visual = visual
                };
                entry.MouseMoveHandler = (_, e) => UpdateRemoteCursorDim(entry, e.GetPosition(host));
                entry.MouseLeaveHandler = (_, _) => UpdateRemoteCursorDim(entry, farAway: true);
                entry.PreviewMouseDownHandler = (_, e) => {
                    if (!IsPointOnRemoteCursor(entry.Visual, e.GetPosition(host)))
                        return;

                    e.Handled = true;
                    HideRemoteCursor(element, userInitiated: true);
                };

                element.PreviewMouseMove += entry.MouseMoveHandler;
                element.MouseLeave += entry.MouseLeaveHandler;
                element.PreviewMouseDown += entry.PreviewMouseDownHandler;
                host.Children.Add(visual);
                _remoteOverlayActive.Add(element, entry);
                visual.AnimateOpacityTo(1, RemoteFadeDuration);
                UpdateRemoteCursorDim(entry, Mouse.GetPosition(host));
                return true;
            }

            internal static void HideRemoteCursor(FrameworkElement element, bool userInitiated = false) {
                if (element != null && _remoteOverlayActive.TryGetValue(element, out var overlayEntry)) {
                    DetachRemoteEntry(overlayEntry);
                    _remoteOverlayActive.Remove(element);
                }

                if (element != null && _remoteActive.TryGetValue(element, out var entry)) {
                    DetachRemoteEntry(entry);
                    _remoteActive.Remove(element);
                }

                if (userInitiated)
                    SetRemoteCursorHiddenByUser(true);
            }

            internal static void RestoreRemoteCursorVisibility() {
                SetRemoteCursorHiddenByUser(false);
                if (_lastRemoteElement == null ||
                    !_lastRemoteElement.TryGetTarget(out FrameworkElement element) ||
                    _lastRemotePosition == null)
                    return;

                if (element.Dispatcher.CheckAccess()) {
                    ShowRemoteCursor(element, _lastRemotePosition.Value);
                    return;
                }

                element.Dispatcher.BeginInvoke(new Action(() => ShowRemoteCursor(element, _lastRemotePosition.Value)));
            }

            private static void DetachRemoteEntry(RemoteEntry entry) {
                entry.Element.PreviewMouseMove -= entry.MouseMoveHandler;
                entry.Element.MouseLeave -= entry.MouseLeaveHandler;
                entry.Element.PreviewMouseDown -= entry.PreviewMouseDownHandler;
                entry.Layer.Remove(entry.Adorner);
            }

            private static void DetachRemoteEntry(RemoteOverlayEntry entry) {
                entry.Element.PreviewMouseMove -= entry.MouseMoveHandler;
                entry.Element.MouseLeave -= entry.MouseLeaveHandler;
                entry.Element.PreviewMouseDown -= entry.PreviewMouseDownHandler;
                entry.Host.Children.Remove(entry.Visual);
            }

            private static void SetRemoteCursorHiddenByUser(bool hidden) {
                if (_remoteCursorHiddenByUser == hidden)
                    return;

                _remoteCursorHiddenByUser = hidden;
                RemoteCursorHiddenChanged?.Invoke(null, EventArgs.Empty);
            }

            private static void UpdateRemoteCursorDim(RemoteEntry entry, Point pointer) {
                double distance = Distance(new Point(entry.Adorner.CursorX, entry.Adorner.CursorY), pointer);
                bool shouldDim = distance <= RemoteNearDistance;
                if (entry.IsDimmed == shouldDim)
                    return;

                entry.IsDimmed = shouldDim;
                entry.Adorner.AnimateOpacityTo(shouldDim ? 0.5 : 1.0, TimeSpan.FromMilliseconds(120));
            }

            private static void UpdateRemoteCursorDim(RemoteOverlayEntry entry, Point pointer) {
                double distance = Distance(entry.Visual.CurrentPosition, pointer);
                bool shouldDim = distance <= RemoteNearDistance;
                if (entry.IsDimmed == shouldDim)
                    return;

                entry.IsDimmed = shouldDim;
                entry.Visual.AnimateOpacityTo(shouldDim ? 0.5 : 1.0, TimeSpan.FromMilliseconds(120));
            }

            private static void UpdateRemoteCursorDim(RemoteEntry entry, bool farAway) {
                if (!farAway) {
                    UpdateRemoteCursorDim(entry, Mouse.GetPosition(entry.Element));
                    return;
                }

                if (!entry.IsDimmed)
                    return;

                entry.IsDimmed = false;
                entry.Adorner.AnimateOpacityTo(1.0, TimeSpan.FromMilliseconds(120));
            }

            private static void UpdateRemoteCursorDim(RemoteOverlayEntry entry, bool farAway) {
                if (!farAway) {
                    UpdateRemoteCursorDim(entry, Mouse.GetPosition(entry.Host));
                    return;
                }

                if (!entry.IsDimmed)
                    return;

                entry.IsDimmed = false;
                entry.Visual.AnimateOpacityTo(1.0, TimeSpan.FromMilliseconds(120));
            }

            private void AnimateRemoteCursorTo(Point target) {
                double distance = Distance(new Point(CursorX, CursorY), target);
                if (distance < 0.5) {
                    CursorX = target.X;
                    CursorY = target.Y;
                    return;
                }

                int durationMs = Math.Max(120, Math.Min(680, (int)Math.Round(distance / RemoteMovePixelsPerMillisecond)));
                var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };
                BeginAnimation(CursorXProperty, new DoubleAnimation(target.X, TimeSpan.FromMilliseconds(durationMs)) {
                    EasingFunction = easing
                }, HandoffBehavior.SnapshotAndReplace);
                BeginAnimation(CursorYProperty, new DoubleAnimation(target.Y, TimeSpan.FromMilliseconds(durationMs)) {
                    EasingFunction = easing
                }, HandoffBehavior.SnapshotAndReplace);
            }

            private void AnimateOpacityTo(double opacity, TimeSpan duration) {
                BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, duration) {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                }, HandoffBehavior.SnapshotAndReplace);
            }

            private static bool IsPointOnRemoteCursor(ClickIndicatorAdorner adorner, Point point) {
                var bounds = new Rect(
                    adorner.CursorX - 4,
                    adorner.CursorY - 4,
                    RemoteClickHitSize,
                    RemoteClickHitSize);
                return bounds.Contains(point);
            }

            private static bool IsPointOnRemoteCursor(RemoteCursorVisual visual, Point point) {
                Point position = visual.CurrentPosition;
                var bounds = new Rect(
                    position.X - 4,
                    position.Y - 4,
                    RemoteClickHitSize,
                    RemoteClickHitSize);
                return bounds.Contains(point);
            }

            private static Point TranslatePointSafe(FrameworkElement source, Point point, FrameworkElement target) {
                try {
                    return source.TranslatePoint(point, target);
                } catch {
                    return point;
                }
            }

            private static double Distance(Point a, Point b) {
                double dx = a.X - b.X;
                double dy = a.Y - b.Y;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
        }
    }
}
