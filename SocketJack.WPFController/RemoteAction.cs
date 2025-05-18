using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace SocketJack.WPFController {

    [Serializable]
    public class RemoteAction {

        public enum ActionType {
            Click,
            Focus,
            Keystrokes
        }

        public ActionType Action { get; set; }
        public string Arguments { get; set; }

        /// <summary>
    /// <para>Time in Milliseconds to perform the action.</para>
    /// <para>Default is 200.</para>
    /// </summary>
    /// <returns></returns>
        public long Duration { get; set; } = 200L;
        public ElementRoute Route { get; set; }

        public RemoteAction() {

        }

        public RemoteAction(ElementRoute Route) {
            this.Route = Route;
        }

        public async Task<bool> PerformAction() {
            var Element = await Route.GetElement();
            if (Element is null) {
                return false;
            } else {
                await PerformAction(Element, Action);
                return true;
            }
        }

        private async Task PerformAction(FrameworkElement Element, ActionType Action) {
            switch (Action) {
                case ActionType.Click: {
                        var Args = Enum.IsDefined(typeof(MouseButton), Arguments) ? (MouseButton)Conversions.ToInteger(Arguments) : MouseButton.Left;
                        await SimulateClick(Element, Args);
                        break;
                    }
                case ActionType.Focus: {
                        Element.Focus();
                        break;
                    }
                case ActionType.Keystrokes: {
                        await SimulateKeystrokes(Element, Arguments);
                        break;
                    }
            }
        }

        private async Task SimulateClick(FrameworkElement Element, MouseButton MouseButton) {
            if (Element is Button) {
                Element.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            } else {
                var mouseDownEvent = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton) { RoutedEvent = UIElement.MouseDownEvent, Source = Element };
                Element.RaiseEvent(mouseDownEvent);
                await Task.Delay((int)Duration);
                var mouseUpEvent = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton) { RoutedEvent = UIElement.MouseUpEvent, Source = Element };
                Element.RaiseEvent(mouseUpEvent);
            }
        }

        private async Task SimulateKeystrokes(FrameworkElement Element, string text) {
            int delayPerKey = (int)(Duration / text.Length);
            foreach (char k in text) {
                Key key = (Key)KeyInterop.VirtualKeyFromKey((Key)Strings.AscW(k));

                var keyDownEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Element), 0, key) { RoutedEvent = Keyboard.KeyDownEvent };
                Element.RaiseEvent(keyDownEvent);
                var keyUpEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(Element), 0, key) { RoutedEvent = Keyboard.KeyUpEvent };
                Element.RaiseEvent(keyUpEvent);

                await Task.Delay(delayPerKey);
            }
        }
    }
}