using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.VisualBasic.CompilerServices;

namespace SocketJack.WPFController {

    public class ElementRoute {

        #region Properties
        public string WindowName { get; set; }
        public string ElementName { get; set; }
        public string ID { get; set; }
        #endregion

        #region Cache
        protected internal static Dictionary<string, FrameworkElement> ControlCache = new Dictionary<string, FrameworkElement>();
        #endregion

        public ElementRoute() {

        }

        public ElementRoute(ref FrameworkElement Element) {
                ElementName = Element.Name;
                WindowName = Window.GetWindow(Element).Name;
                ID = Guid.NewGuid().ToString();
                Element.Tag = ID;
            }

        public async Task<FrameworkElement> GetElement() {
            string FormattedRoute = string.Format("{0}>{1}", new[] { WindowName, ElementName });
            return await Application.Current.Dispatcher.InvokeAsync(() => {
                if (ControlCache.ContainsKey(ID)) {
                    return ControlCache[ID];
                } else {
                    foreach (Window w in Application.Current.Windows) {
                        if ((w.Name ?? "") == (WindowName ?? "")) {
                            if (Conversions.ToBoolean(Operators.ConditionalCompareObjectEqual(w.Tag, ID, false))) {
                                ControlCache.Add(ID, w);
                                return w;
                            }
                            var Controls = w.GetAllControls();
                            foreach (var c in Controls) {
                                if (Conversions.ToBoolean(Operators.ConditionalCompareObjectEqual(c.Tag, ID, false))) {
                                    ControlCache.Add(ID, c);
                                    return c;
                                }
                            }
                        }
                    }
                }
                return null;
            });
        }

    }
}