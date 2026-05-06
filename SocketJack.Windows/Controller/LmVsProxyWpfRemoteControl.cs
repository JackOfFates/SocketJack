#if WINDOWS
#nullable enable
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using LmVs;
using SocketJack.Net;

namespace SocketJack.WPF {
    namespace Controller {

        public static class LmVsProxyWpfRemoteControl {
            private static readonly object SyncRoot = new object();
            private static WeakReference<FrameworkElement>? _captureElement;
            private static ButtonBase? _lastPressedButton;

            public static void EnsureRegistered() {
                lock (SyncRoot) {
                    LmVsProxyRemoteControl.ProviderName = "SocketJack.WPF";
                    LmVsProxyRemoteControl.CaptureScreen = CaptureScreen;
                    LmVsProxyRemoteControl.ExecuteInput = ExecuteInput;
                }
            }

            public static void RegisterAdminPanel(FrameworkElement element) {
                RegisterCaptureElement(element);
            }

            public static void RegisterCaptureElement(FrameworkElement element) {
                if (element == null)
                    throw new ArgumentNullException(nameof(element));

                lock (SyncRoot)
                    _captureElement = new WeakReference<FrameworkElement>(element);

                EnsureRegistered();
            }

            private static LmVsProxyScreenCaptureResult CaptureScreen(LmVsProxyScreenCaptureOptions options) {
                FrameworkElement element = ResolveCaptureElement()
                    ?? throw new InvalidOperationException("No SocketJack.WPF capture element is registered, and Application.Current.MainWindow is not available.");

                int quality = Math.Max(1, Math.Min(100, options?.Quality ?? 82));
                byte[]? jpeg = ControlBitmapCapture.CaptureJpegAsync(element, quality).GetAwaiter().GetResult();
                if (jpeg == null || jpeg.Length == 0)
                    throw new InvalidOperationException("SocketJack.WPF could not capture the registered element.");

                var bounds = element.Dispatcher.Invoke(() =>
                {
                    element.UpdateLayout();
                    Point topLeft = element.PointToScreen(new Point(0, 0));
                    return new {
                        Width = (int)Math.Ceiling(Math.Max(0, element.ActualWidth)),
                        Height = (int)Math.Ceiling(Math.Max(0, element.ActualHeight)),
                        Left = (int)Math.Round(topLeft.X),
                        Top = (int)Math.Round(topLeft.Y)
                    };
                });

                return new LmVsProxyScreenCaptureResult {
                    Bytes = jpeg,
                    MimeType = "image/jpeg",
                    Width = bounds.Width,
                    Height = bounds.Height,
                    Left = bounds.Left,
                    Top = bounds.Top,
                    Backend = "SocketJack.WPF.RenderTargetBitmap"
                };
            }

            private static LmVsProxyRemoteInputResult ExecuteInput(LmVsProxyRemoteInputRequest input) {
                if (input == null)
                    input = new LmVsProxyRemoteInputRequest();

                FrameworkElement element = ResolveCaptureElement()
                    ?? throw new InvalidOperationException("Remote input requires a registered SocketJack.WPF capture element.");

                return element.Dispatcher.Invoke(() => ExecuteInputOnDispatcher(element, input));
            }

            private static LmVsProxyRemoteInputResult ExecuteInputOnDispatcher(FrameworkElement element, LmVsProxyRemoteInputRequest input) {
                element.UpdateLayout();
                string action = (input.Action ?? "move").Trim().ToLowerInvariant();
                RemoteElementPoint start = input.HasPoint
                    ? ResolveElementPoint(element, input.X, input.Y, input.Normalized)
                    : ResolveElementPoint(element, 0.5, 0.5, normalized: true);
                RemoteElementPoint target = input.HasTargetPoint
                    ? ResolveElementPoint(element, input.ToX, input.ToY, input.Normalized)
                    : start;

                switch (action) {
                    case "move":
                    case "mousemove":
                        RaiseMouseMove(start);
                        break;
                    case "click":
                    case "mouseclick":
                        RaiseMouseClick(start, input.Button);
                        break;
                    case "doubleclick":
                    case "double-click":
                        RaiseMouseClick(start, input.Button);
                        Thread.Sleep(70);
                        RaiseMouseClick(start, input.Button);
                        break;
                    case "down":
                    case "mousedown":
                        RaiseMouseDown(start, ParseMouseButton(input.Button));
                        break;
                    case "up":
                    case "mouseup":
                        RaiseMouseUp(start, ParseMouseButton(input.Button));
                        break;
                    case "drag":
                    case "mousedrag":
                        RaiseMouseDrag(start, target, input.Button, input.DurationMs, input.Steps);
                        break;
                    case "wheel":
                    case "scroll":
                        RaiseMouseWheel(start, input.Delta == 0 ? -120 : input.Delta);
                        break;
                    case "text":
                    case "type":
                        RaiseTextInput(element, start, input.Text);
                        break;
                    case "key":
                    case "keypress":
                        RaiseKeyInput(element, start, input.Key);
                        break;
                    default:
                        throw new NotSupportedException("Unsupported remote input action: " + input.Action);
                }

                return new LmVsProxyRemoteInputResult {
                    Action = action,
                    X = start.RootPoint.X,
                    Y = start.RootPoint.Y,
                    Message = "SocketJack.WPF routed event input executed."
                };
            }

            private static FrameworkElement? ResolveCaptureElement() {
                lock (SyncRoot) {
                    if (_captureElement != null &&
                        _captureElement.TryGetTarget(out FrameworkElement? element) &&
                        element != null)
                        return element;
                }

                Application? app = Application.Current;
                if (app == null)
                    return null;

                if (app.Dispatcher.CheckAccess())
                    return app.MainWindow;

                return app.Dispatcher.Invoke(() => app.MainWindow);
            }

            private static RemoteElementPoint ResolveElementPoint(FrameworkElement element, double x, double y, bool normalized) {
                double rootX;
                double rootY;
                if (normalized) {
                    rootX = Clamp(x, 0, 1) * Math.Max(0, element.ActualWidth);
                    rootY = Clamp(y, 0, 1) * Math.Max(0, element.ActualHeight);
                } else {
                    rootX = Clamp(x, 0, Math.Max(0, element.ActualWidth));
                    rootY = Clamp(y, 0, Math.Max(0, element.ActualHeight));
                }

                var rootPoint = new Point(rootX, rootY);
                IInputElement target = element.InputHitTest(rootPoint) ?? element;
                return new RemoteElementPoint {
                    Root = element,
                    RootPoint = rootPoint,
                    Target = target
                };
            }

            private static void RaiseMouseClick(RemoteElementPoint point, string? button) {
                MouseButton mouseButton = ParseMouseButton(button);
                RaiseMouseDown(point, mouseButton);
                Thread.Sleep(35);
                RaiseMouseUp(point, mouseButton);
            }

            private static void RaiseMouseDrag(RemoteElementPoint start, RemoteElementPoint target, string? button, int durationMs, int steps) {
                int safeSteps = Math.Max(1, Math.Min(240, steps));
                int delay = safeSteps <= 1 ? 0 : Math.Max(0, durationMs) / safeSteps;
                MouseButton mouseButton = ParseMouseButton(button);
                RaiseMouseDown(start, mouseButton);
                for (int i = 1; i <= safeSteps; i++) {
                    double t = (double)i / safeSteps;
                    var nextPoint = new Point(
                        start.RootPoint.X + ((target.RootPoint.X - start.RootPoint.X) * t),
                        start.RootPoint.Y + ((target.RootPoint.Y - start.RootPoint.Y) * t));
                    RaiseMouseMove(ResolveElementPoint(start.Root, nextPoint.X, nextPoint.Y, normalized: false));
                    if (delay > 0)
                        Thread.Sleep(delay);
                }
                RaiseMouseUp(target, mouseButton);
            }

            private static void RaiseMouseDown(RemoteElementPoint point, MouseButton button) {
                if (point.Target is not UIElement target)
                    return;

                RaiseMouseButtonEvent(target, UIElement.PreviewMouseDownEvent, button);
                RaiseMouseButtonEvent(target, UIElement.MouseDownEvent, button);
                RoutedEvent? directEvent = GetButtonSpecificDownEvent(button);
                if (directEvent != null)
                    RaiseMouseButtonEvent(target, directEvent, button);

                FocusHitTarget(point);
                SetTextBoxCaretFromPoint(point);

                if (button == MouseButton.Left) {
                    ButtonBase? pressed = FindAncestor<ButtonBase>(point.Target as DependencyObject);
                    if (_lastPressedButton != null && !ReferenceEquals(_lastPressedButton, pressed))
                        SetButtonPressed(_lastPressedButton, false);
                    _lastPressedButton = pressed;
                    if (pressed != null)
                        SetButtonPressed(pressed, true);
                }
            }

            private static void RaiseMouseUp(RemoteElementPoint point, MouseButton button) {
                if (point.Target is UIElement target) {
                    RaiseMouseButtonEvent(target, UIElement.PreviewMouseUpEvent, button);
                    RaiseMouseButtonEvent(target, UIElement.MouseUpEvent, button);
                    RoutedEvent? directEvent = GetButtonSpecificUpEvent(button);
                    if (directEvent != null)
                        RaiseMouseButtonEvent(target, directEvent, button);
                }

                if (button == MouseButton.Left) {
                    ButtonBase? clickButton = FindAncestor<ButtonBase>(point.Target as DependencyObject) ?? _lastPressedButton;
                    _lastPressedButton = null;
                    if (clickButton != null) {
                        ApplyButtonDefaultState(clickButton);
                        clickButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, clickButton));
                        if (clickButton.Command != null && clickButton.Command.CanExecute(clickButton.CommandParameter))
                            clickButton.Command.Execute(clickButton.CommandParameter);
                        SetButtonPressed(clickButton, false);
                    }
                }

                if (Mouse.Captured != null)
                    Mouse.Capture(null);
            }

            private static void RaiseMouseMove(RemoteElementPoint point) {
                if (point.Target is not UIElement target)
                    return;

                target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                    RoutedEvent = UIElement.PreviewMouseMoveEvent,
                    Source = target
                });
                target.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) {
                    RoutedEvent = UIElement.MouseMoveEvent,
                    Source = target
                });
            }

            private static void RaiseMouseWheel(RemoteElementPoint point, int delta) {
                if (point.Target is not UIElement target)
                    return;

                target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) {
                    RoutedEvent = UIElement.PreviewMouseWheelEvent,
                    Source = target
                });
                target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = target
                });
            }

            private static void RaiseTextInput(FrameworkElement root, RemoteElementPoint fallbackPoint, string? text) {
                if (string.IsNullOrEmpty(text))
                    return;

                IInputElement target = Keyboard.FocusedElement ?? fallbackPoint.Target ?? root;
                UIElement? targetElement = target as UIElement ?? FindAncestor<UIElement>(target as DependencyObject) ?? root;
                var source = PresentationSource.FromVisual(root);
                if (source == null || targetElement == null)
                    return;

                foreach (string chunk in EnumerateTextElements(text)) {
                    var composition = new TextComposition(InputManager.Current, target, chunk);
                    var preview = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                        RoutedEvent = TextCompositionManager.PreviewTextInputEvent,
                        Source = targetElement
                    };
                    targetElement.RaiseEvent(preview);
                    if (preview.Handled)
                        continue;

                    var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice, composition) {
                        RoutedEvent = TextCompositionManager.TextInputEvent,
                        Source = targetElement
                    };
                    targetElement.RaiseEvent(input);
                    if (!input.Handled)
                        InsertTextFallback(target, chunk);
                }
            }

            private static void RaiseKeyInput(FrameworkElement root, RemoteElementPoint fallbackPoint, string? keyText) {
                Key key = ResolveWpfKey(keyText);
                IInputElement target = Keyboard.FocusedElement ?? fallbackPoint.Target ?? root;
                UIElement? targetElement = target as UIElement ?? FindAncestor<UIElement>(target as DependencyObject) ?? root;
                var source = PresentationSource.FromVisual(root);
                if (source == null || targetElement == null)
                    return;

                var previewDown = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = targetElement
                };
                targetElement.RaiseEvent(previewDown);

                var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                    RoutedEvent = Keyboard.KeyDownEvent,
                    Source = targetElement
                };
                if (!previewDown.Handled)
                    targetElement.RaiseEvent(down);

                if (!previewDown.Handled && !down.Handled)
                    ApplyKeyFallback(target, key);

                var previewUp = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                    RoutedEvent = Keyboard.PreviewKeyUpEvent,
                    Source = targetElement
                };
                targetElement.RaiseEvent(previewUp);

                var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) {
                    RoutedEvent = Keyboard.KeyUpEvent,
                    Source = targetElement
                };
                if (!previewUp.Handled)
                    targetElement.RaiseEvent(up);
            }

            private static void RaiseMouseButtonEvent(UIElement target, RoutedEvent routedEvent, MouseButton button) {
                target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, button) {
                    RoutedEvent = routedEvent,
                    Source = target
                });
            }

            private static void FocusHitTarget(RemoteElementPoint point) {
                DependencyObject? current = point.Target as DependencyObject;
                while (current != null) {
                    if (current is UIElement element && element.Focusable) {
                        element.Focus();
                        Keyboard.Focus(element);
                        return;
                    }
                    current = VisualTreeHelper.GetParent(current);
                }
            }

            private static void SetTextBoxCaretFromPoint(RemoteElementPoint point) {
                TextBox? textBox = FindAncestor<TextBox>(point.Target as DependencyObject);
                if (textBox == null)
                    return;

                Point textBoxPoint = point.Root.TranslatePoint(point.RootPoint, textBox);
                int charIndex = textBox.GetCharacterIndexFromPoint(textBoxPoint, true);
                if (charIndex >= 0)
                    textBox.Select(charIndex, 0);
            }

            private static void InsertTextFallback(IInputElement target, string text) {
                TextBox? textBox = FindAncestor<TextBox>(target as DependencyObject);
                if (textBox != null) {
                    int start = textBox.SelectionStart;
                    textBox.SelectedText = text;
                    textBox.SelectionStart = start + text.Length;
                    textBox.SelectionLength = 0;
                    return;
                }

                PasswordBox? passwordBox = FindAncestor<PasswordBox>(target as DependencyObject);
                if (passwordBox != null) {
                    passwordBox.Password += text;
                    return;
                }

                RichTextBox? richTextBox = FindAncestor<RichTextBox>(target as DependencyObject);
                richTextBox?.CaretPosition.InsertTextInRun(text);
            }

            private static void ApplyKeyFallback(IInputElement target, Key key) {
                TextBox? textBox = FindAncestor<TextBox>(target as DependencyObject);
                if (textBox != null) {
                    ApplyTextBoxKeyFallback(textBox, key);
                    return;
                }

                if (key == Key.Enter) {
                    ButtonBase? button = FindAncestor<ButtonBase>(target as DependencyObject);
                    if (button != null) {
                        ApplyButtonDefaultState(button);
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                        if (button.Command != null && button.Command.CanExecute(button.CommandParameter))
                            button.Command.Execute(button.CommandParameter);
                    }
                } else if (key == Key.Tab && target is UIElement element) {
                    element.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                }
            }

            private static void ApplyTextBoxKeyFallback(TextBox textBox, Key key) {
                switch (key) {
                    case Key.Enter:
                        if (textBox.AcceptsReturn)
                            InsertTextFallback(textBox, Environment.NewLine);
                        break;
                    case Key.Back:
                        if (textBox.SelectionLength > 0) {
                            textBox.SelectedText = "";
                        } else if (textBox.SelectionStart > 0) {
                            int start = textBox.SelectionStart;
                            textBox.Text = textBox.Text.Remove(start - 1, 1);
                            textBox.SelectionStart = start - 1;
                        }
                        break;
                    case Key.Delete:
                        if (textBox.SelectionLength > 0) {
                            textBox.SelectedText = "";
                        } else if (textBox.SelectionStart < textBox.Text.Length) {
                            int start = textBox.SelectionStart;
                            textBox.Text = textBox.Text.Remove(start, 1);
                            textBox.SelectionStart = start;
                        }
                        break;
                    case Key.Tab:
                        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                        break;
                }
            }

            private static void ApplyButtonDefaultState(ButtonBase button) {
                if (button is RadioButton radio) {
                    radio.IsChecked = true;
                } else if (button is ToggleButton toggle) {
                    if (toggle.IsChecked == true)
                        toggle.IsChecked = toggle.IsThreeState ? null : (bool?)false;
                    else
                        toggle.IsChecked = true;
                }
            }

            private static readonly System.Reflection.MethodInfo? SetIsPressedMethod =
                typeof(ButtonBase).GetProperty("IsPressed")?.GetSetMethod(nonPublic: true);

            private static void SetButtonPressed(ButtonBase button, bool value) {
                try {
                    SetIsPressedMethod?.Invoke(button, new object[] { value });
                } catch {
                }
            }

            private static RoutedEvent? GetButtonSpecificDownEvent(MouseButton button) {
                switch (button) {
                    case MouseButton.Left:
                        return UIElement.MouseLeftButtonDownEvent;
                    case MouseButton.Right:
                        return UIElement.MouseRightButtonDownEvent;
                    default:
                        return null;
                }
            }

            private static RoutedEvent? GetButtonSpecificUpEvent(MouseButton button) {
                switch (button) {
                    case MouseButton.Left:
                        return UIElement.MouseLeftButtonUpEvent;
                    case MouseButton.Right:
                        return UIElement.MouseRightButtonUpEvent;
                    default:
                        return null;
                }
            }

            private static MouseButton ParseMouseButton(string? button) {
                switch ((button ?? "left").Trim().ToLowerInvariant()) {
                    case "right":
                    case "secondary":
                        return MouseButton.Right;
                    case "middle":
                    case "wheel":
                        return MouseButton.Middle;
                    default:
                        return MouseButton.Left;
                }
            }

            private static Key ResolveWpfKey(string? key) {
                string normalized = (key ?? "").Trim();
                if (normalized.Length == 1)
                    return KeyInterop.KeyFromVirtualKey(char.ToUpperInvariant(normalized[0]));

                switch (normalized.ToLowerInvariant()) {
                    case "enter":
                    case "return":
                        return Key.Enter;
                    case "tab":
                        return Key.Tab;
                    case "backspace":
                    case "back":
                        return Key.Back;
                    case "escape":
                    case "esc":
                        return Key.Escape;
                    case "delete":
                    case "del":
                        return Key.Delete;
                    case "left":
                    case "arrowleft":
                        return Key.Left;
                    case "up":
                    case "arrowup":
                        return Key.Up;
                    case "right":
                    case "arrowright":
                        return Key.Right;
                    case "down":
                    case "arrowdown":
                        return Key.Down;
                    case "home":
                        return Key.Home;
                    case "end":
                        return Key.End;
                    case "pageup":
                    case "prior":
                        return Key.PageUp;
                    case "pagedown":
                    case "next":
                        return Key.PageDown;
                    case "space":
                        return Key.Space;
                }

                throw new NotSupportedException("Unsupported remote key: " + key);
            }

            private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject {
                while (current != null) {
                    if (current is T match)
                        return match;
                    current = VisualTreeHelper.GetParent(current);
                }

                return null;
            }

            private static double Clamp(double value, double min, double max) {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    return min;
                return Math.Max(min, Math.Min(max, value));
            }

            private static System.Collections.Generic.IEnumerable<string> EnumerateTextElements(string text) {
                TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
                while (enumerator.MoveNext())
                    yield return enumerator.GetTextElement();
            }

            private static RemoteWindowPoint ResolveWindowPoint(double x, double y, bool normalized) {
                FrameworkElement element = ResolveCaptureElement()
                    ?? throw new InvalidOperationException("Remote input requires a registered SocketJack.WPF capture element.");

                return element.Dispatcher.Invoke(() =>
                {
                    element.UpdateLayout();
                    Window window = Window.GetWindow(element);
                    PresentationSource presentationSource = window == null
                        ? HwndSource.FromVisual(element)
                        : HwndSource.FromVisual(window);
                    HwndSource? source = presentationSource as HwndSource;
                    if (source == null || source.Handle == IntPtr.Zero)
                        throw new InvalidOperationException("SocketJack.WPF could not resolve the target window handle.");

                    NativePoint screenPoint;
                    NativePoint clientPoint;
                    if (normalized) {
                        double clampedX = Math.Max(0, Math.Min(1, x));
                        double clampedY = Math.Max(0, Math.Min(1, y));
                        double px = clampedX * Math.Max(0, element.ActualWidth);
                        double py = clampedY * Math.Max(0, element.ActualHeight);
                        Point screen = element.PointToScreen(new Point(px, py));
                        screenPoint = new NativePoint {
                            X = (int)Math.Round(screen.X),
                            Y = (int)Math.Round(screen.Y)
                        };
                        clientPoint = screenPoint;
                        ScreenToClient(source.Handle, ref clientPoint);
                    } else {
                        screenPoint = new NativePoint {
                            X = (int)Math.Round(x),
                            Y = (int)Math.Round(y)
                        };
                        clientPoint = screenPoint;
                        ScreenToClient(source.Handle, ref clientPoint);
                    }

                    return new RemoteWindowPoint {
                        Hwnd = source.Handle,
                        ClientX = clientPoint.X,
                        ClientY = clientPoint.Y,
                        ScreenX = screenPoint.X,
                        ScreenY = screenPoint.Y
                    };
                });
            }

            private static void DragWindowMouse(RemoteWindowPoint start, RemoteWindowPoint target, string? button, int durationMs, int steps) {
                int safeSteps = Math.Max(1, Math.Min(240, steps));
                int delay = safeSteps <= 1 ? 0 : Math.Max(0, durationMs) / safeSteps;
                MouseMessages(button, out int down, out int up);
                IntPtr buttonState = MouseButtonStateFlags(button);
                SendMouseMessage(start, down, buttonState);
                for (int i = 1; i <= safeSteps; i++) {
                    double t = (double)i / safeSteps;
                    var next = new RemoteWindowPoint {
                        Hwnd = start.Hwnd,
                        ClientX = (int)Math.Round(start.ClientX + ((target.ClientX - start.ClientX) * t)),
                        ClientY = (int)Math.Round(start.ClientY + ((target.ClientY - start.ClientY) * t)),
                        ScreenX = (int)Math.Round(start.ScreenX + ((target.ScreenX - start.ScreenX) * t)),
                        ScreenY = (int)Math.Round(start.ScreenY + ((target.ScreenY - start.ScreenY) * t))
                    };
                    SendMouseMessage(next, WindowMessageMouseMove, buttonState);
                    if (delay > 0)
                        Thread.Sleep(delay);
                }
                SendMouseMessage(target, up, IntPtr.Zero);
            }

            private static void SendMouseClick(RemoteWindowPoint point, string? button) {
                MouseMessages(button, out int down, out int up);
                SendMouseMessage(point, down, MouseButtonStateFlags(button));
                Thread.Sleep(35);
                SendMouseMessage(point, up, IntPtr.Zero);
            }

            private static void SendMouseWheel(RemoteWindowPoint point, int delta) {
                int wParam = (delta << 16);
                SendMessage(point.Hwnd, WindowMessageMouseWheel, new IntPtr(wParam), PackLParam(point.ScreenX, point.ScreenY));
            }

            private static void SendMouseMessage(RemoteWindowPoint point, int message, IntPtr wParam) {
                SendMessage(point.Hwnd, message, wParam, PackLParam(point.ClientX, point.ClientY));
            }

            private static void MouseMessages(string? button, out int down, out int up) {
                switch ((button ?? "left").Trim().ToLowerInvariant()) {
                    case "right":
                    case "secondary":
                        down = WindowMessageRightButtonDown;
                        up = WindowMessageRightButtonUp;
                        break;
                    case "middle":
                    case "wheel":
                        down = WindowMessageMiddleButtonDown;
                        up = WindowMessageMiddleButtonUp;
                        break;
                    default:
                        down = WindowMessageLeftButtonDown;
                        up = WindowMessageLeftButtonUp;
                        break;
                }
            }

            private static IntPtr MouseButtonStateFlags(string? button) {
                switch ((button ?? "left").Trim().ToLowerInvariant()) {
                    case "right":
                    case "secondary":
                        return new IntPtr(MouseKeyRightButton);
                    case "middle":
                    case "wheel":
                        return new IntPtr(MouseKeyMiddleButton);
                    default:
                        return new IntPtr(MouseKeyLeftButton);
                }
            }

            private static void SendWindowText(IntPtr hwnd, string? text) {
                if (string.IsNullOrEmpty(text))
                    return;

                foreach (char ch in text)
                    SendMessage(hwnd, WindowMessageChar, new IntPtr(ch), new IntPtr(1));
            }

            private static void SendWindowKey(IntPtr hwnd, string? key) {
                string normalized = (key ?? "").Trim().ToLowerInvariant();
                if (normalized.Length == 1) {
                    SendMessage(hwnd, WindowMessageChar, new IntPtr(normalized[0]), new IntPtr(1));
                    return;
                }

                ushort virtualKey = ResolveVirtualKey(key);
                int scanCode = (int)MapVirtualKey(virtualKey, 0);
                IntPtr down = new IntPtr(1 | (scanCode << 16));
                IntPtr up = new IntPtr(1 | (scanCode << 16) | (1 << 30) | unchecked((int)0x80000000));
                SendMessage(hwnd, WindowMessageKeyDown, new IntPtr(virtualKey), down);
                SendMessage(hwnd, WindowMessageKeyUp, new IntPtr(virtualKey), up);
            }

            private static ushort ResolveVirtualKey(string? key) {
                string normalized = (key ?? "").Trim().ToLowerInvariant();
                switch (normalized) {
                    case "enter":
                    case "return":
                        return 0x0D;
                    case "tab":
                        return 0x09;
                    case "backspace":
                    case "back":
                        return 0x08;
                    case "escape":
                    case "esc":
                        return 0x1B;
                    case "delete":
                    case "del":
                        return 0x2E;
                    case "left":
                    case "arrowleft":
                        return 0x25;
                    case "up":
                    case "arrowup":
                        return 0x26;
                    case "right":
                    case "arrowright":
                        return 0x27;
                    case "down":
                    case "arrowdown":
                        return 0x28;
                    case "home":
                        return 0x24;
                    case "end":
                        return 0x23;
                    case "pageup":
                    case "prior":
                        return 0x21;
                    case "pagedown":
                    case "next":
                        return 0x22;
                    case "space":
                        return 0x20;
                }

                throw new NotSupportedException("Unsupported remote key: " + key);
            }

            private static IntPtr PackLParam(int x, int y) {
                int value = (x & 0xffff) | ((y & 0xffff) << 16);
                return new IntPtr(value);
            }

            private struct RemoteWindowPoint {
                public IntPtr Hwnd;
                public int ClientX;
                public int ClientY;
                public int ScreenX;
                public int ScreenY;
            }

            private struct RemoteElementPoint {
                public FrameworkElement Root;
                public Point RootPoint;
                public IInputElement Target;
            }

            private const int WindowMessageMouseMove = 0x0200;
            private const int WindowMessageLeftButtonDown = 0x0201;
            private const int WindowMessageLeftButtonUp = 0x0202;
            private const int WindowMessageRightButtonDown = 0x0204;
            private const int WindowMessageRightButtonUp = 0x0205;
            private const int WindowMessageMiddleButtonDown = 0x0207;
            private const int WindowMessageMiddleButtonUp = 0x0208;
            private const int WindowMessageMouseWheel = 0x020A;
            private const int WindowMessageKeyDown = 0x0100;
            private const int WindowMessageKeyUp = 0x0101;
            private const int WindowMessageChar = 0x0102;
            private const int MouseKeyLeftButton = 0x0001;
            private const int MouseKeyRightButton = 0x0002;
            private const int MouseKeyMiddleButton = 0x0010;

            [StructLayout(LayoutKind.Sequential)]
            private struct NativePoint {
                public int X;
                public int Y;
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        }
#nullable restore
    }
}
#endif
