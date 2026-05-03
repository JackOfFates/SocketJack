using SocketJack.Net;
using SocketJack.Net.P2P;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SocketJack.WPF {
    namespace Controller {
        public static class Extensions {

            /// <summary>
            /// Creates a <see cref="ControlShareViewer"/> that receives shared control frames
            /// from the specified peer and displays them on the specified <see cref="Image"/> control.
            /// </summary>
            public static ControlShareViewer ViewShare(this TcpClient client, Image image, Identifier peer) {
                return new ControlShareViewer(client, image, peer);
            }

            public static ControlShareViewer ViewShare(this UdpClient client, Image image, Identifier peer) {
                return new ControlShareViewer(client, image, peer);
            }

            public static RemoteAction CreateAction(this FrameworkElement Element, RemoteAction.ActionType Action, string Arguments = "") {
                return new RemoteAction(new ElementRoute(ref Element)) { Action = Action, Arguments = Arguments };
            }

            public static List<FrameworkElement> GetAllControls(this Window window) {
                var controls = new List<FrameworkElement>();
                GetChildControls(window, controls);
                return controls;
            }

            public static List<Delegate> GetPublicVoids(this Window window) {
                return default;

            }

            /// <summary>
            /// Returns all currently open <see cref="Window"/> instances in the application.
            /// Useful for populating a selector (e.g. <see cref="ComboBox"/>) so the user can
            /// choose which window to pass to <see cref="GetAllControls(Window)"/>.
            /// </summary>
            public static List<Window> GetOpenWindows() {
                return Application.Current.Dispatcher.Invoke(() =>
                    Application.Current.Windows.OfType<Window>().ToList());
            }

            /// <summary>
            /// Resolves a <see cref="FrameworkElement"/> to the <see cref="Window"/> that contains it.
            /// Returns <c>null</c> if the element is not part of a window's visual tree.
            /// </summary>
            public static Window ToWindow(this FrameworkElement element) {
                if (element == null) return null;
                if (element is Window w) return w;
                return Window.GetWindow(element);
            }

            /// <summary>
            /// Finds an open <see cref="Window"/> whose <see cref="Window.Name"/> matches <paramref name="windowName"/>.
            /// Returns <c>null</c> if no matching window is found.
            /// </summary>
            public static Window ToWindow(this string windowName) {
                if (string.IsNullOrEmpty(windowName)) return null;
                return Application.Current.Dispatcher.Invoke(() =>
                    Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => string.Equals(w.Name, windowName, StringComparison.OrdinalIgnoreCase)));
            }

            /// <summary>
            /// Finds an open <see cref="Window"/> whose <see cref="Window.Title"/> matches <paramref name="windowTitle"/>.
            /// Returns <c>null</c> if no matching window is found.
            /// </summary>
            public static Window FindWindowByTitle(this string windowTitle) {
                if (string.IsNullOrEmpty(windowTitle)) return null;
                return Application.Current.Dispatcher.Invoke(() =>
                    Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => string.Equals(w.Title, windowTitle, StringComparison.OrdinalIgnoreCase)));
            }

            /// <summary>
            /// Populates a <see cref="ComboBox"/> with all currently open windows,
            /// displaying each window's title (or type name as fallback).
            /// Subscribe to <see cref="ComboBox.SelectionChanged"/> to retrieve the selected <see cref="Window"/>.
            /// </summary>
            public static void PopulateWindowSelector(this ComboBox comboBox) {
                if (comboBox == null) return;
                Application.Current.Dispatcher.Invoke(() => {
                    comboBox.Items.Clear();
                    foreach (var w in Application.Current.Windows.OfType<Window>()) {
                        var display = !string.IsNullOrWhiteSpace(w.Title) ? w.Title : w.GetType().Name;
                        comboBox.Items.Add(new ComboBoxItem { Content = display, Tag = w });
                    }
                });
            }

            /// <summary>
            /// Gets the <see cref="Window"/> selected in a <see cref="ComboBox"/> that was
            /// populated by <see cref="PopulateWindowSelector(ComboBox)"/>.
            /// Returns <c>null</c> if nothing is selected.
            /// </summary>
            public static Window GetSelectedWindow(this ComboBox comboBox) {
                if (comboBox?.SelectedItem is ComboBoxItem item && item.Tag is Window w)
                    return w;
                return null;
            }

            private static void GetChildControls(DependencyObject parent, List<FrameworkElement> controls) {
                for (int i = 0, loopTo = VisualTreeHelper.GetChildrenCount(parent) - 1; i <= loopTo; i++) {
                    var child = VisualTreeHelper.GetChild(parent, i);
                    if (child is FrameworkElement) {
                        controls.Add((FrameworkElement)child);
                    }
                    GetChildControls(child, controls);
                }
            }

            public static void EnableRemoteControl(this TcpServer Server) {
                Server.RegisterCallback<RemoteAction>(Server_RemoteAction);
                Server.Options.Whitelist.Add(typeof(ControlShareFrame));
                Server.Options.Whitelist.Add(typeof(RemoteAction));
            }

            public static void EnableRemoteControl(this TcpClient Client) {
                Client.RegisterCallback<RemoteAction>(Client_RemoteAction);
                Client.Options.Whitelist.Add(typeof(ControlShareFrame));
                Client.Options.Whitelist.Add(typeof(RemoteAction));
            }

            public static void EnableRemoteControl(this UdpServer Server) {
                Server.RegisterCallback<RemoteAction>(Server_RemoteAction);
                Server.Options.Whitelist.Add(typeof(ControlShareFrame));
                Server.Options.Whitelist.Add(typeof(RemoteAction));
            }

            public static void EnableRemoteControl(this UdpClient Client) {
                Client.RegisterCallback<RemoteAction>(Client_RemoteAction);
                Client.Options.Whitelist.Add(typeof(ControlShareFrame));
                Client.Options.Whitelist.Add(typeof(RemoteAction));
            }

            private static void Server_RemoteAction(ReceivedEventArgs<RemoteAction> a) {

                // Server-side: perform the action on the server UI (if applicable).
                // This library is WPF-only; execute on the current Application dispatcher.
                if (a == null || a.Object == null)
                    return;
                Dispatcher.CurrentDispatcher.InvokeAsync(async () => { await a.Object.PerformAction(); });

            }

            private static void Client_RemoteAction(ReceivedEventArgs<RemoteAction> a) {

                // Client-side: perform the action on the client UI.
                if (a == null || a.Object == null)
                    return;
                Dispatcher.CurrentDispatcher.InvokeAsync(async () => { await a.Object.PerformAction(); });

            }
        }
    }
}