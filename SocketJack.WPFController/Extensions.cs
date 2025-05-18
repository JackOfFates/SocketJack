using SocketJack.Networking;
using SocketJack.Networking.Shared;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

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
        }

        public static void EnableRemoteControl(this TcpClient Client) {
            Client.RegisterCallback<RemoteAction>(Client_RemoteAction);
        }

        private static void Server_RemoteAction(ReceivedEventArgs<RemoteAction> a) {

        }

        private static void Client_RemoteAction(ReceivedEventArgs<RemoteAction> a) {

        }
    }
}