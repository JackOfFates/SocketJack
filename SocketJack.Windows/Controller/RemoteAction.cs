using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;

namespace SocketJack.WPF {
    namespace Controller {

        public class RemoteAction {

            public enum ActionType {
                MouseDown,
                MouseUp,
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
                }
                else {
                    await PerformAction(Element, Action);
                    return true;
                }
            }

            private async Task PerformAction(FrameworkElement Element, ActionType Action) {
                switch (Action) {
                    case ActionType.MouseDown: {
                        var (btn, nx, ny, hasPos) = ParseClickArgs(Arguments);
                        await SimulateMouseDown(Element, btn, nx, ny, hasPos);
                        break;
                    }
                    case ActionType.MouseUp: {
                        var (btn, nx, ny, hasPos) = ParseClickArgs(Arguments);
                        await SimulateMouseUp(Element, btn, nx, ny, hasPos);
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

            // ButtonBase.IsPressed has a protected setter; use reflection to access it.
            // Synthetic mouse events bypass WPF's internal pressed-state tracking because
            // ButtonBase.OnMouseLeftButtonDown checks the physical MouseButtonState,
            // which is Released during a synthetic raise.
            private static readonly System.Reflection.MethodInfo _setIsPressedMethod =
                typeof(System.Windows.Controls.Primitives.ButtonBase)
                    .GetProperty("IsPressed")
                    ?.GetSetMethod(nonPublic: true);

            private static void SetButtonPressed(System.Windows.Controls.Primitives.ButtonBase button, bool value) {
                _setIsPressedMethod?.Invoke(button, new object[] { value });
            }

            // Tracks the ButtonBase that was last pressed via SimulateMouseDown so
            // SimulateMouseUp can reliably reset it even when the pressed visual
            // state shifts layout enough to change hit-test results.
            private static System.Windows.Controls.Primitives.ButtonBase _lastPressedButton;

            // Maps a MouseButton to its button-specific Direct RoutedEvent so
            // controls that override OnMouseLeftButtonDown etc. receive the event.
            private static RoutedEvent GetButtonSpecificDownEvent(MouseButton button) {
                switch (button) {
                    case MouseButton.Left: return UIElement.MouseLeftButtonDownEvent;
                    case MouseButton.Right: return UIElement.MouseRightButtonDownEvent;
                    default: return null;
                }
            }

            private static RoutedEvent GetButtonSpecificUpEvent(MouseButton button) {
                switch (button) {
                    case MouseButton.Left: return UIElement.MouseLeftButtonUpEvent;
                    case MouseButton.Right: return UIElement.MouseRightButtonUpEvent;
                    default: return null;
                }
            }

            private static IInputElement ResolveTarget(FrameworkElement root, double nx, double ny, bool hasPos) {
                if (hasPos) {
                    if (nx < 0) nx = 0;
                    if (ny < 0) ny = 0;
                    if (nx > 1) nx = 1;
                    if (ny > 1) ny = 1;
                    var pt = new Point(
                        nx * Math.Max(0, root.ActualWidth),
                        ny * Math.Max(0, root.ActualHeight));
                    var hit = root.InputHitTest(pt);
                    if (hit != null)
                        return hit;
                }
                return root;
            }

            private Task SimulateMouseDown(FrameworkElement Element, MouseButton MouseButton, double nx, double ny, bool hasPos) {
                return Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (hasPos) {
                        if (nx < 0) nx = 0;
                        if (ny < 0) ny = 0;
                        if (nx > 1) nx = 1;
                        if (ny > 1) ny = 1;
                    }

                    // Find the child at the normalized position
                    IInputElement target = Element;
                    if (hasPos) {
                        var pt = new Point(nx * Element.ActualWidth, ny * Element.ActualHeight);
                        var hit = Element.InputHitTest(pt);
                        if (hit != null)
                            target = hit;
                    }

                    // Raise on the child — WPF routes through the parent automatically
                    (target as UIElement)?.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton) {
                        RoutedEvent = UIElement.PreviewMouseDownEvent
                    });

                    if (target is UIElement targetUie) {
                        targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                        {
                            RoutedEvent = UIElement.PreviewMouseDownEvent
                        });

                        targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                        {
                            RoutedEvent = UIElement.MouseDownEvent
                        });

                        // WPF's input manager normally raises button-specific Direct
                        // events (e.g. MouseLeftButtonDown) after the generic
                        // PreviewMouseDown / MouseDown pair.  RaiseEvent does not do
                        // this automatically, so controls whose handlers listen for
                        // the button-specific event (TextBoxBase, etc.) never see the
                        // click.  Raise them explicitly.
                        var btnDownEvent = GetButtonSpecificDownEvent(MouseButton);
                        if (btnDownEvent != null)
                            targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                            {
                                RoutedEvent = btnDownEvent
                            });
                    }

                    if (hasPos)
                        ClickIndicatorAdorner.Show(Element, new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight)), Brushes.Red);

                    // Synthetic events don't trigger WPF's built-in focus-on-click.
                    // Walk up to the nearest focusable element and focus it so that
                    // TextBoxes, ComboBoxes, etc. respond to clicks correctly.
                    DependencyObject focusAncestor = target as DependencyObject;
                    while (focusAncestor != null) {
                        if (focusAncestor is UIElement fe && fe.Focusable) {
                            fe.Focus();
                            break;
                        }
                        focusAncestor = System.Windows.Media.VisualTreeHelper.GetParent(focusAncestor);
                    }

                    // Set IsPressed on the nearest ButtonBase so it reflects the
                    // pressed visual state.  See SetButtonPressed for why this is needed.
                    if (MouseButton == System.Windows.Input.MouseButton.Left) {
                        DependencyObject ancestor = target as DependencyObject;
                        while (ancestor != null) {
                            if (ancestor is System.Windows.Controls.Primitives.ButtonBase bb) {
                                SetButtonPressed(bb, true);
                                _lastPressedButton = bb;
                                break;
                            }
                            ancestor = System.Windows.Media.VisualTreeHelper.GetParent(ancestor);
                        }
                    }

                    // TextBoxBase.OnMouseLeftButtonDown positions the caret using
                    // the real mouse cursor, which is wrong for synthetic clicks.
                    // Override the caret position with the intended coordinates.
                    if (hasPos) {
                        DependencyObject tbWalk = target as DependencyObject;
                        while (tbWalk != null) {
                            if (tbWalk is TextBox tb) {
                                var rootPt = new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight));
                                var tbPt = Element.TranslatePoint(rootPt, tb);
                                int charIndex = tb.GetCharacterIndexFromPoint(tbPt, true);
                                if (charIndex >= 0)
                                    tb.Select(charIndex, 0);
                                break;
                            }
                            tbWalk = VisualTreeHelper.GetParent(tbWalk);
                        }
                    }

                    // Release any mouse capture that a control's built-in handler
                    // may have acquired (e.g. TextBoxBase.OnMouseLeftButtonDown
                    // calls CaptureMouse).  Leaving capture set would route the
                    // sharer's real mouse input to the control until the remote
                    // MouseUp arrives, causing unwanted selection artifacts.
                    if (Mouse.Captured != null)
                        Mouse.Capture(null);

                    }).Task;
            }

            private Task SimulateMouseUp(FrameworkElement Element, MouseButton MouseButton, double nx, double ny, bool hasPos) {
                return Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (hasPos) {
                        if (nx < 0) nx = 0;
                        if (ny < 0) ny = 0;
                        if (nx > 1) nx = 1;
                        if (ny > 1) ny = 1;
                    }

                    var target = ResolveTarget(Element, nx, ny, hasPos);

                    if (target is UIElement targetUie) {
                        targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                        {
                            RoutedEvent = UIElement.PreviewMouseUpEvent
                        });

                        targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                        {
                            RoutedEvent = UIElement.MouseUpEvent
                        });

                        // Raise button-specific Direct event (see SimulateMouseDown).
                        var btnUpEvent = GetButtonSpecificUpEvent(MouseButton);
                        if (btnUpEvent != null)
                            targetUie.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton)
                            {
                                RoutedEvent = btnUpEvent
                            });
                    }

                    if (hasPos)
                        ClickIndicatorAdorner.Show(Element, new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight)), Brushes.White);

                    // Reset the ButtonBase that was pressed in SimulateMouseDown.
                    // We use the tracked reference instead of re-hit-testing because
                    // the pressed visual state can shift the layout enough to cause
                    // the hit test to miss the original button.
                    if (_lastPressedButton != null) {
                        SetButtonPressed(_lastPressedButton, false);
                        _lastPressedButton = null;
                    }

                    // Release any residual mouse capture (safety net).
                    if (Mouse.Captured != null)
                        Mouse.Capture(null);
                }).Task;
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

                    await Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        var source = PresentationSource.FromVisual(Element);
                        if (source == null)
                            return;

                        var keyDownEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                        {
                            RoutedEvent = Keyboard.KeyDownEvent,
                            Source = Element
                        };
                        Element.RaiseEvent(keyDownEvent);

                        var keyUpEvent = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                        {
                            RoutedEvent = Keyboard.KeyUpEvent,
                            Source = Element
                        };
                        Element.RaiseEvent(keyUpEvent);
                    });

                    await Task.Delay(delayPerKey);
                }
            }

            private Task SimulateMouseMove(FrameworkElement Element, double nx, double ny, bool hasPos) {
                return Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (hasPos) {
                        if (nx < 0) nx = 0;
                        if (ny < 0) ny = 0;
                        if (nx > 1) nx = 1;
                        if (ny > 1) ny = 1;
                    }

                    var target = ResolveTarget(Element, nx, ny, hasPos);

                    if (target is UIElement targetUie) {
                        targetUie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        {
                            RoutedEvent = UIElement.PreviewMouseMoveEvent
                        });

                        targetUie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        {
                            RoutedEvent = UIElement.MouseMoveEvent
                        });
                    }

                    if (hasPos)
                        ClickIndicatorAdorner.Show(Element, new Point(nx * Math.Max(0, Element.ActualWidth), ny * Math.Max(0, Element.ActualHeight)), Brushes.White);
                }).Task;
            }

            private Task SimulateMouseEnter(FrameworkElement Element, double nx, double ny, bool hasPos) {
                return Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var target = ResolveTarget(Element, nx, ny, hasPos);

                    // MouseEnter is a Direct event — it only fires on the element
                    // it is raised on.  Raise on Element first (parent enters
                    // before child), then on the hit-tested target.
                    Element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    {
                        RoutedEvent = UIElement.MouseEnterEvent
                    });

                    if (target is UIElement targetUie && !ReferenceEquals(target, Element))
                        targetUie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        {
                            RoutedEvent = UIElement.MouseEnterEvent
                        });
                }).Task;
            }

            private Task SimulateMouseLeave(FrameworkElement Element, double nx, double ny, bool hasPos) {
                return Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var target = ResolveTarget(Element, nx, ny, hasPos);

                    // MouseLeave is a Direct event — raise on target first
                    // (child leaves before parent), then on Element.
                    if (target is UIElement targetUie && !ReferenceEquals(target, Element))
                        targetUie.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                        {
                            RoutedEvent = UIElement.MouseLeaveEvent
                        });

                    Element.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                    {
                        RoutedEvent = UIElement.MouseLeaveEvent
                    });
                }).Task;
            }
        }
    }
}