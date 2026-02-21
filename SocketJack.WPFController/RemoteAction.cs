using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace SocketJack.WPFController {

    public class RemoteAction {

        public enum ActionType {
            Click,
            Focus,
            Keystrokes,
            MouseMove,
            MouseEnter,
            MouseLeave
        }

        public ActionType Action { get; set; }
        public string Arguments { get; set; }

        /// <summary>
        /// <para>Time in Milliseconds to perform the action.</para>
        /// <para>Default is 200.</para>
        /// </summary>
        /// <returns></returns>
        public int Duration { get; set; } = 200;
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
                        var (btn, nx, ny, hasPos) = ParseClickArgs(Arguments);
                        await SimulateClick(Element, btn, nx, ny, hasPos);
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
                case ActionType.MouseMove: {
                        var (nx, ny, hasPos) = ParsePosArgs(Arguments);
                        await SimulateMouseMove(Element, nx, ny, hasPos);
                        break;
                    }
                case ActionType.MouseEnter: {
                        var (nx, ny, hasPos) = ParsePosArgs(Arguments);
                        await SimulateMouseEnter(Element, nx, ny, hasPos);
                        break;
                    }
                case ActionType.MouseLeave: {
                        var (nx, ny, hasPos) = ParsePosArgs(Arguments);
                        await SimulateMouseLeave(Element, nx, ny, hasPos);
                        break;
                    }
            }
        }

        private static (double nx, double ny, bool hasPos) ParsePosArgs(string args) {
            // Supported formats:
            // 1) "" (no position)
            // 2) "0.5,0.5" (normalized)
            if (string.IsNullOrWhiteSpace(args))
                return (0, 0, false);

            var parts = args.Split(',');
            if (parts.Length >= 2 &&
                double.TryParse(parts[0].Trim(), out var nx) &&
                double.TryParse(parts[1].Trim(), out var ny)) {
                return (nx, ny, true);
            }

            return (0, 0, false);
        }

        private static (MouseButton button, double nx, double ny, bool hasPos) ParseClickArgs(string args) {
            // Supported formats:
            // 1) "Left" / "Right" / numeric enum value
            // 2) "Left,0.5,0.5" (normalized)
            if (string.IsNullOrWhiteSpace(args))
                return (MouseButton.Left, 0, 0, false);

            var parts = args.Split(',');
            if (parts.Length >= 1) {
                var btnText = parts[0].Trim();
                var btn = TryParseMouseButton(btnText, out var b) ? b : MouseButton.Left;

                if (parts.Length >= 3 &&
                    double.TryParse(parts[1].Trim(), out var nx) &&
                    double.TryParse(parts[2].Trim(), out var ny)) {
                    return (btn, nx, ny, true);
                }

                return (btn, 0, 0, false);
            }

            return (MouseButton.Left, 0, 0, false);
        }

        private static bool TryParseMouseButton(string text, out MouseButton button) {
            button = MouseButton.Left;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (Enum.TryParse(text, ignoreCase: true, out MouseButton parsed)) {
                button = parsed;
                return true;
            }

            if (int.TryParse(text, out var n) && Enum.IsDefined(typeof(MouseButton), n)) {
                button = (MouseButton)n;
                return true;
            }

            return false;
        }

        private async Task SimulateClick(FrameworkElement Element, MouseButton MouseButton, double nx, double ny, bool hasPos) {
            await Application.Current.Dispatcher.InvokeAsync(() => {
                var target = (IInputElement)Element;
                if (hasPos) {
                    if (nx < 0)
                        nx = 0;
                    if (ny < 0)
                        ny = 0;
                    if (nx > 1)
                        nx = 1;
                    if (ny > 1)
                        ny = 1;

                    var pt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                    var hit = Element.InputHitTest(pt);
                    if (hit != null)
                        target = hit;
                }

                Element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton) {
                    RoutedEvent = UIElement.PreviewMouseDownEvent,
                    Source = Element
                });

                if (target is UIElement uieDown)
                    uieDown.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton) {
                        RoutedEvent = UIElement.MouseDownEvent,
                        Source = target
                    });
            });
            await Task.Delay((int)Duration);
            await Application.Current.Dispatcher.InvokeAsync(() => {
                var target = (IInputElement)Element;
                if (hasPos) {
                    if (nx < 0)
                        nx = 0;
                    if (ny < 0)
                        ny = 0;
                    if (nx > 1)
                        nx = 1;
                    if (ny > 1)
                        ny = 1;

                    var pt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                    var hit = Element.InputHitTest(pt);
                    if (hit != null)
                        target = hit;
                }

                Element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton) {
                    RoutedEvent = UIElement.PreviewMouseUpEvent,
                    Source = Element
                });

                if (target is UIElement uieUp)
                    uieUp.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton) {
                        RoutedEvent = UIElement.MouseUpEvent,
                        Source = target
                    });

                // Walk up from the hit-test target to find the nearest ButtonBase
                // and raise Click on it. InputHitTest returns the deepest element
                // (e.g. a TextBlock), not the Button itself.
                if (MouseButton == System.Windows.Input.MouseButton.Left) {
                    var ancestor = target as DependencyObject;
                    while (ancestor != null) {
                        if (ancestor is System.Windows.Controls.Primitives.ButtonBase bb) {
                            bb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            break;
                        }
                        ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor);
                    }
                }
            });
        }

        private async Task SimulateKeystrokes(FrameworkElement Element, string text) {
            if (string.IsNullOrEmpty(text))
                return;

            int delayPerKey = (int)Math.Max(1, Duration / text.Length);
            foreach (char k in text) {
                var vk = (int)k;
                if (vk < 0)
                    continue;

                var key = KeyInterop.KeyFromVirtualKey(vk);

                await Application.Current.Dispatcher.InvokeAsync(() => {
                    var source = PresentationSource.FromVisual(Element);
                    if (source == null)
                        return;

                    var keyDownEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                        RoutedEvent = Keyboard.KeyDownEvent,
                        Source = Element
                    };
                    Element.RaiseEvent(keyDownEvent);

                    var keyUpEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                        RoutedEvent = Keyboard.KeyUpEvent,
                        Source = Element
                    };
                    Element.RaiseEvent(keyUpEvent);
                });

                await Task.Delay(delayPerKey);
            }
        }

        private Task SimulateMouseMove(FrameworkElement Element, double nx, double ny, bool hasPos) {
            return Application.Current.Dispatcher.InvokeAsync(() => {
                var target = (IInputElement)Element;
                if (hasPos) {
                    if (nx < 0) nx = 0;
                    if (ny < 0) ny = 0;
                    if (nx > 1) nx = 1;
                    if (ny > 1) ny = 1;

                    var pt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                    var hit = Element.InputHitTest(pt);
                    if (hit != null)
                        target = hit;
                }

                Element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                    RoutedEvent = UIElement.PreviewMouseMoveEvent,
                    Source = Element
                });

                if (target is UIElement uie)
                    uie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                        RoutedEvent = UIElement.MouseMoveEvent,
                        Source = target
                    });
            }).Task;
        }

        private Task SimulateMouseEnter(FrameworkElement Element, double nx, double ny, bool hasPos) {
            return Application.Current.Dispatcher.InvokeAsync(() => {
                var target = (IInputElement)Element;
                if (hasPos) {
                    if (nx < 0) nx = 0;
                    if (ny < 0) ny = 0;
                    if (nx > 1) nx = 1;
                    if (ny > 1) ny = 1;

                    var pt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                    var hit = Element.InputHitTest(pt);
                    if (hit != null)
                        target = hit;
                }

                if (target is UIElement uie)
                    uie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                        RoutedEvent = UIElement.MouseEnterEvent,
                        Source = target
                    });
            }).Task;
        }

        private Task SimulateMouseLeave(FrameworkElement Element, double nx, double ny, bool hasPos) {
            return Application.Current.Dispatcher.InvokeAsync(() => {
                var target = (IInputElement)Element;
                if (hasPos) {
                    if (nx < 0) nx = 0;
                    if (ny < 0) ny = 0;
                    if (nx > 1) nx = 1;
                    if (ny > 1) ny = 1;

                    var pt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                    var hit = Element.InputHitTest(pt);
                    if (hit != null)
                        target = hit;
                }

                if (target is UIElement uie)
                    uie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                        RoutedEvent = UIElement.MouseLeaveEvent,
                        Source = target
                    });
            }).Task;
        }
    }
}