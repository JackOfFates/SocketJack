using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SocketJack.WPF {
    namespace Controller {

        internal sealed class ClickIndicatorAdorner : Adorner {

            private readonly Point _position;
            private readonly Brush _brush;

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

            private sealed class ActiveEntry {
                internal ClickIndicatorAdorner Adorner;
                internal AdornerLayer Layer;
            }

            private ClickIndicatorAdorner(UIElement adornedElement, Point position, Brush brush) : base(adornedElement) {
                _position = position;
                _brush = brush;
                IsHitTestVisible = false;
            }

            protected override void OnRender(DrawingContext drawingContext) {
                var geo = new StreamGeometry();
                using (var ctx = geo.Open()) {
                    ctx.BeginFigure(new Point(_position.X + _cursorPoints[0].X, _position.Y + _cursorPoints[0].Y), true, true);
                    for (int i = 1; i < _cursorPoints.Length; i++)
                        ctx.LineTo(new Point(_position.X + _cursorPoints[i].X, _position.Y + _cursorPoints[i].Y), true, false);
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
        }
    }
}