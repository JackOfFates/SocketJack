using SocketJack.Net.P2P;
using SocketJack.Net;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.WPF {
    namespace Controller {

        public static class FrameworkElementShareExtensions {

            private static bool ShareDiagEnabled() {
                return true;
            }

            private const int DefaultJpegQuality = 77;

            public static IDisposable Share(this FrameworkElement element, TcpClient client, Identifier peer, int fps = 10) {
                ArgumentNullException.ThrowIfNull(element);
                ArgumentNullException.ThrowIfNull(client);
                ArgumentNullException.ThrowIfNull(peer);

                // Ensure message types are whitelisted.
                client.Options.Whitelist.Add(typeof(ControlShareFrame));
                client.Options.Whitelist.Add(typeof(ControlShareRemoteAction));

                var route = new ElementRoute(ref element);
                var cts = new CancellationTokenSource();

                var actionCallback = RegisterRemoteActionReplay(client, peer, route, cts);

                _ = Task.Run(async () =>
                {
                    var delayMs = fps <= 0 ? 100 : (int)Math.Max(10, 1000.0 / fps);
                    while (!cts.IsCancellationRequested) {
                        try {
                            var jpg = await ControlBitmapCapture.CaptureJpegAsync(element, quality: DefaultJpegQuality).ConfigureAwait(false);
                            if (jpg != null && jpg.Length > 0) {
                                var width = (int)Math.Ceiling(element.ActualWidth);
                                var height = (int)Math.Ceiling(element.ActualHeight);

                                client.Send(peer, new ControlShareFrame
                                {
                                    ControlId = route.ID,
                                    Route = route,
                                    JpegBytes = jpg,
                                    Quality = DefaultJpegQuality,
                                    Width = width,
                                    Height = height,
                                    UnixMs = unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                });
                            }
                        }
                        catch {
                            // best-effort streaming
                        }

                        try {
                            await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                        }
                        catch {
                            break;
                        }
                    }
                }, cts.Token);

                return new StopHandle(cts, () =>
                {
                    try {
                        client.RemoveCallback(actionCallback);
                    }
                    catch {
                    }
                });
            }

            private static Action<ReceivedEventArgs<ControlShareRemoteAction>> RegisterRemoteActionReplay(TcpClient client, Identifier peer, ElementRoute route, CancellationTokenSource cts) {
                Action<ReceivedEventArgs<ControlShareRemoteAction>> cb = e =>
                {
                    try {
                        if (cts.IsCancellationRequested)
                            return;
                        if (e.Object == null)
                            return;
                        if (e.Connection == null)
                            return;
                        if (e.From?.ID != peer.ID)
                            return;

                        var cid = e.Object.ControlId;
                        if (string.IsNullOrWhiteSpace(cid))
                            cid = route.ID;
                        if (cid != route.ID)
                            return;

                        var ra = new RemoteAction(route)
                        {
                            Action = e.Object.Action,
                            Arguments = e.Object.Arguments,
                            Duration = e.Object.Duration
                        };

                        _ = ra.PerformAction();
                    }
                    catch {
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
                    }
                    catch {
                    }
                    try {
                        _cts.Dispose();
                    }
                    catch {
                    }

                    try {
                        _onDispose();
                    }
                    catch {
                    }
                }
            }
        }
    }
}