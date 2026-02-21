using SocketJack.Net.P2P;
using SocketJack.Net;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace SocketJack.WPFController;

public static class FrameworkElementShareExtensions {

    private static bool ShareDiagEnabled() {
        return true;
    }

    private static void ShareDiagLog(string text) {
        try {
            if (!ShareDiagEnabled())
                return;

            var app = Application.Current;
            if (app == null)
                return;

            app.Dispatcher.BeginInvoke(() => {
                try {
                    var mw = app.MainWindow;
                    if (mw == null)
                        return;

                    // Try to call a MainWindow.Log(string) method if present (WpfBasicGame).
                    var mi = mw.GetType().GetMethod("Log", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (mi == null)
                        return;
                    mi.Invoke(mw, new object[] { "[share] " + text });
                } catch {
                }
            });
        } catch {
        }
    }

    private const int DefaultJpegQuality = 77;

    public static IDisposable Share(this FrameworkElement element, TcpClient client, Identifier peer, int fps = 10) {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(peer);

        // Ensure message types are whitelisted.
        client.Options.Whitelist.Add(typeof(ControlShareFrame));
        client.Options.Whitelist.Add(typeof(ControlShareInput));
        client.Options.Whitelist.Add(typeof(ControlShareRemoteAction));

        var route = new ElementRoute(ref element);
        var cts = new CancellationTokenSource();

        var inputCallback = RegisterInputReplay(client, peer, route, cts);
        var actionCallback = RegisterRemoteActionReplay(client, peer, route, cts);

        _ = Task.Run(async () => {
            var delayMs = fps <= 0 ? 100 : (int)Math.Max(10, 1000.0 / fps);
            while (!cts.IsCancellationRequested) {
                try {
                    var jpg = await ControlBitmapCapture.CaptureJpegAsync(element, quality: DefaultJpegQuality).ConfigureAwait(false);
                    if (jpg != null && jpg.Length > 0) {
                        var width = (int)Math.Ceiling(element.ActualWidth);
                        var height = (int)Math.Ceiling(element.ActualHeight);

                        client.Send(peer, new ControlShareFrame {
                            ControlId = route.ID,
                            Route = route,
                            JpegBytes = jpg,
                            Quality = DefaultJpegQuality,
                            Width = width,
                            Height = height,
                            UnixMs = unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                        });

                        //ShareDiagLog($"send frame to={peer.ID} size={width}x{height}");
                    }
                } catch {
                    // best-effort streaming
                }

                try {
                    await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                } catch {
                    break;
                }
            }
        }, cts.Token);

        return new StopHandle(cts, () => {
            try {
                client.RemoveCallback(inputCallback);
            } catch {
            }
            try {
                client.RemoveCallback(actionCallback);
            } catch {
            }
        });
    }

    private static Action<ReceivedEventArgs<ControlShareRemoteAction>> RegisterRemoteActionReplay(TcpClient client, Identifier peer, ElementRoute route, CancellationTokenSource cts) {
        Action<ReceivedEventArgs<ControlShareRemoteAction>> cb = e => {
            try {
                if (cts.IsCancellationRequested)
                    return;
                if (e.Object == null)
                    return;
                if (e.Connection == null || e.Connection.Identity == null)
                    return;
                if (e.Connection.Identity.ID != peer.ID)
                    return;

                var cid = e.Object.ControlId;
                if (string.IsNullOrWhiteSpace(cid))
                    cid = route.ID;
                if (cid != route.ID)
                    return;

                var ra = new RemoteAction(route) {
                    Action = e.Object.Action,
                    Arguments = e.Object.Arguments,
                    Duration = e.Object.Duration
                };

                ShareDiagLog($"recv action from={peer.ID} action={e.Object.Action} args=\"{e.Object.Arguments}\"");

                _ = ra.PerformAction();
            } catch {
            }
        };

        client.RegisterCallback(cb);
        return cb;
    }

    private static void RegisterCallback(this TcpClient client, Action<ReceivedEventArgs<ControlShareRemoteAction>> cb) {
        client.RegisterCallback<ControlShareRemoteAction>(cb);
    }

    private static void RemoveCallback(this TcpClient client, Action<ReceivedEventArgs<ControlShareRemoteAction>> cb) {
        client.RemoveCallback<ControlShareRemoteAction>(cb);
    }

    private static Action<ReceivedEventArgs<ControlShareInput>> RegisterInputReplay(TcpClient client, Identifier peer, ElementRoute route, CancellationTokenSource cts) {
        Action<ReceivedEventArgs<ControlShareInput>> cb = e => {
            try {
                if (cts.IsCancellationRequested)
                    return;
                if (e.Object == null)
                    return;
                if (e.Connection == null || e.Connection.Identity == null)
                    return;
                if (e.Connection.Identity.ID != peer.ID)
                    return;
                var cid = e.Object.ControlId;
                if (string.IsNullOrWhiteSpace(cid))
                    cid = route.ID;
                if (cid != route.ID)
                    return;

                if (e.Object.IsMove) {
                    ShareDiagLog($"recv move from={peer.ID} nx={e.Object.X:0.###} ny={e.Object.Y:0.###}");
                    _ = DispatchMouseMoveAsync(route, e.Object.X, e.Object.Y);
                }

                if (e.Object.IsClick) {
                    var btn = e.Object.Button == 1 ? MouseButton.Right : MouseButton.Left;
                    ShareDiagLog($"recv click from={peer.ID} btn={btn} nx={e.Object.X:0.###} ny={e.Object.Y:0.###}");
                    _ = DispatchMouseClickAsync(route, e.Object.X, e.Object.Y, btn);
                }
            } catch {
            }
        };

        client.RegisterCallback(cb);
        return cb;
    }

    private static void RegisterCallback(this TcpClient client, Action<ReceivedEventArgs<ControlShareInput>> cb) {
        client.RegisterCallback<ControlShareInput>(cb);
    }

    private static void RemoveCallback(this TcpClient client, Action<ReceivedEventArgs<ControlShareInput>> cb) {
        client.RemoveCallback<ControlShareInput>(cb);
    }

    private static async Task DispatchMouseMoveAsync(ElementRoute route, double nx, double ny) {
        await await Application.Current.Dispatcher.InvokeAsync(async () => {
            try {
                var element = await route.GetElement().ConfigureAwait(true);
                if (element == null)
                    return;

                if (nx < 0)
                    nx = 0;
                if (ny < 0)
                    ny = 0;
                if (nx > 1)
                    nx = 1;
                if (ny > 1)
                    ny = 1;

                var x = nx * Math.Max(0, element.ActualWidth);
                var y = ny * Math.Max(0, element.ActualHeight);

                var target = (IInputElement)element;
                var hit = element.InputHitTest(new Point(x, y));
                if (hit != null)
                    target = hit;

                ShareDiagLog($"hit move x={x:0.#} y={y:0.#} target={target.GetType().Name}");

                element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                    RoutedEvent = UIElement.PreviewMouseMoveEvent,
                    Source = element
                });

                if (target is UIElement uie)
                    uie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                        RoutedEvent = UIElement.MouseMoveEvent,
                        Source = target
                    });
            } catch {
            }
        });
    }

    private static async Task DispatchMouseClickAsync(ElementRoute route, double nx, double ny, MouseButton button) {
        await await Application.Current.Dispatcher.InvokeAsync(async () => {
            try {
                var element = await route.GetElement().ConfigureAwait(true);
                if (element == null)
                    return;

                if (nx < 0)
                    nx = 0;
                if (ny < 0)
                    ny = 0;
                if (nx > 1)
                    nx = 1;
                if (ny > 1)
                    ny = 1;

                var x = nx * Math.Max(0, element.ActualWidth);
                var y = ny * Math.Max(0, element.ActualHeight);

                var target = (IInputElement)element;
                var hit = element.InputHitTest(new Point(x, y));
                if (hit != null)
                    target = hit;

                ShareDiagLog($"hit click x={x:0.#} y={y:0.#} btn={button} target={target.GetType().Name}");

                element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = element
                });

                if (target is UIElement uieDown)
                    uieDown.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
                        RoutedEvent = UIElement.MouseDownEvent,
                        Source = target
                    });

                element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
                    RoutedEvent = UIElement.PreviewMouseUpEvent,
                    Source = element
                });

                if (target is UIElement uieUp)
                    uieUp.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
                        RoutedEvent = UIElement.MouseUpEvent,
                        Source = target
                    });

                // For common controls, also trigger click semantic.
                // InputHitTest returns the deepest element (e.g. a TextBlock inside a
                // Button), so walk up the visual tree to find the nearest ButtonBase.
                if (button == MouseButton.Left) {
                    var ancestor = target as DependencyObject;
                    while (ancestor != null) {
                        if (ancestor is System.Windows.Controls.Primitives.ButtonBase bb) {
                            bb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            break;
                        }
                        ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor);
                    }
                }
            } catch {
            }
        });
    }

    private sealed class StopHandle : IDisposable {
        private readonly CancellationTokenSource _cts;
        private readonly Action _onDispose;
        public StopHandle(CancellationTokenSource cts, Action onDispose) {
            _cts = cts;
            _onDispose = onDispose;
        }
        public void Dispose() {
            try {
                _cts.Cancel();
            } catch {
            }
            try {
                _cts.Dispose();
            } catch {
            }

            try {
                _onDispose();
            } catch {
            }
        }
    }
}
