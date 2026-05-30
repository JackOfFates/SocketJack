using SocketJack.Net.P2P;
using SocketJack.Net;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace SocketJack.WPF {
    namespace Controller {

        public static class FrameworkElementShareExtensions {

            private static bool ShareDiagEnabled() {
                return true;
            }

            private const int DefaultJpegQuality = 72;
            private const int IdleFrameDelayMs = 250;
            private const int IdleBackoffStepMs = 25;

            public static IDisposable Share(this FrameworkElement element, TcpClient client, Identifier peer, int fps = 10) {
                return ShareInternal(element, client, peer, fps);
            }

            public static IDisposable Share(this FrameworkElement element, UdpClient client, Identifier peer, int fps = 10) {
                return ShareInternal(element, client, peer, fps);
            }

            private static IDisposable ShareInternal(FrameworkElement element, ISocket client, Identifier peer, int fps) {
                ArgumentNullException.ThrowIfNull(element);
                ArgumentNullException.ThrowIfNull(client);
                ArgumentNullException.ThrowIfNull(peer);

                // Ensure message types are whitelisted.
                client.Options.Whitelist.Add(typeof(ControlShareFrame));
                client.Options.Whitelist.Add(typeof(RemoteAction));

                var route = new ElementRoute(ref element);
                var cts = new CancellationTokenSource();

                var actionCallback = RegisterRemoteActionReplay(client, peer, route, cts);
                var captureState = new ControlBitmapCaptureState();

                _ = Task.Run(async () =>
                {
                    var delayMs = fps <= 0 ? 100 : (int)Math.Max(10, 1000.0 / fps);
                    int unchangedTicks = 0;
                    while (!cts.IsCancellationRequested) {
                        try {
                            var frame = await ControlBitmapCapture.CaptureAdaptiveJpegAsync(
                                element,
                                captureState,
                                quality: DefaultJpegQuality).ConfigureAwait(false);

                            if (frame != null && frame.JpegBytes.Length > 0) {
                                client.Send(peer, new ControlShareFrame
                                {
                                    ControlId = route.ID,
                                    Route = route,
                                    JpegBytes = frame.IsDelta ? null : frame.JpegBytes,
                                    DeltaJpegBytes = frame.IsDelta ? frame.JpegBytes : null,
                                    IsDelta = frame.IsDelta,
                                    DirtyX = frame.DirtyX,
                                    DirtyY = frame.DirtyY,
                                    DirtyWidth = frame.DirtyWidth,
                                    DirtyHeight = frame.DirtyHeight,
                                    PixelWidth = frame.PixelWidth,
                                    PixelHeight = frame.PixelHeight,
                                    DpiX = frame.DpiX,
                                    DpiY = frame.DpiY,
                                    Sequence = frame.Sequence,
                                    BaseSequence = frame.BaseSequence,
                                    ChangedRatio = frame.ChangedRatio,
                                    Quality = frame.Quality,
                                    Width = (int)Math.Ceiling(frame.LogicalWidth),
                                    Height = (int)Math.Ceiling(frame.LogicalHeight),
                                    UnixMs = unchecked((int)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                                });
                                captureState.Commit(frame);
                                unchangedTicks = 0;
                            } else {
                                unchangedTicks = Math.Min(16, unchangedTicks + 1);
                            }
                        }
                        catch {
                            // best-effort streaming
                        }

                        try {
                            int nextDelayMs = unchangedTicks == 0
                                ? delayMs
                                : Math.Min(IdleFrameDelayMs, delayMs + (unchangedTicks * IdleBackoffStepMs));
                            await Task.Delay(nextDelayMs, cts.Token).ConfigureAwait(false);
                        }
                        catch {
                            break;
                        }
                    }
                }, cts.Token);

                return new StopHandle(cts, () =>
                {
                    try {
                        client.RemoveCallback<RemoteAction>(actionCallback);
                    }
                    catch {
                    }
                });
            }

            private static Action<ReceivedEventArgs<RemoteAction>> RegisterRemoteActionReplay(ISocket client, Identifier peer, ElementRoute route, CancellationTokenSource cts) {
                var actionQueue = new ConcurrentQueue<RemoteAction>();
                var draining = new int[] { 0 };

                Action<ReceivedEventArgs<RemoteAction>> cb = e =>
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

                        var cid = e.Object.Route?.ID;
                        if (string.IsNullOrWhiteSpace(cid))
                            cid = route.ID;
                        if (cid != route.ID)
                            return;

                        e.Object.Route = route;
                        actionQueue.Enqueue(e.Object);
                        DrainReplayQueue(actionQueue, draining);
                    }
                    catch {
                    }
                };

                client.RegisterCallback<RemoteAction>(cb);
                return cb;
            }

            private static async void DrainReplayQueue(ConcurrentQueue<RemoteAction> queue, int[] draining) {
                if (Interlocked.CompareExchange(ref draining[0], 1, 0) != 0)
                    return;
                try {
                    while (queue.TryDequeue(out var action)) {
                        try {
                            await action.PerformAction();
                        }
                        catch {
                        }
                    }
                }
                finally {
                    Volatile.Write(ref draining[0], 0);
                }
                if (!queue.IsEmpty)
                    DrainReplayQueue(queue, draining);
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
