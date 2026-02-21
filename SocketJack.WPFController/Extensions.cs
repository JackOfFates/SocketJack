using SocketJack.Net;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace SocketJack.WPFController {

    public static class Extensions {

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
            Server.Options.Whitelist.Add(typeof(ControlShareInput));
            Server.Options.Whitelist.Add(typeof(ControlShareRemoteAction));
        }

        public static void EnableRemoteControl(this TcpClient Client) {
            Client.RegisterCallback<RemoteAction>(Client_RemoteAction);
            Client.Options.Whitelist.Add(typeof(ControlShareFrame));
            Client.Options.Whitelist.Add(typeof(ControlShareInput));
            Client.Options.Whitelist.Add(typeof(ControlShareRemoteAction));
        }

        public static void EnableRemoteControl(this UdpServer Server) {
            Server.RegisterCallback<RemoteAction>(Server_RemoteAction);
            Server.Options.Whitelist.Add(typeof(ControlShareFrame));
            Server.Options.Whitelist.Add(typeof(ControlShareInput));
            Server.Options.Whitelist.Add(typeof(ControlShareRemoteAction));
        }

        public static void EnableRemoteControl(this UdpClient Client) {
            Client.RegisterCallback<RemoteAction>(Client_RemoteAction);
            Client.Options.Whitelist.Add(typeof(ControlShareFrame));
            Client.Options.Whitelist.Add(typeof(ControlShareInput));
            Client.Options.Whitelist.Add(typeof(ControlShareRemoteAction));
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