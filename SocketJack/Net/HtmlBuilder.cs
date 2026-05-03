using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SocketJack.Net {
    /// <summary>
    /// HtmlBuilder is a specialized StringBuilder for constructing HTML, JavaScript, and CSS content.
    /// Supports appending fragments, loading templates from files or strings, and variable replacement.
    /// </summary>
    public class HtmlBuilder {
        private readonly StringBuilder _sb = new StringBuilder();
        private readonly Dictionary<string, string> _vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public HtmlBuilder() { }
        public HtmlBuilder(string template) {
            AppendTemplate(template);
        }
        public HtmlBuilder(string filePath, bool isFile) {
            if (isFile) LoadFile(filePath);
            else AppendTemplate(filePath);
        }

        public HtmlBuilder Append(string html) {
            _sb.Append(html);
            return this;
        }
        public HtmlBuilder AppendLine(string html) {
            _sb.AppendLine(html);
            return this;
        }
        public HtmlBuilder AppendJs(string js) {
            _sb.Append("<script>").Append(js).Append("</script>");
            return this;
        }
        public HtmlBuilder AppendCss(string css) {
            _sb.Append("<style>").Append(css).Append("</style>");
            return this;
        }
        public HtmlBuilder AppendTemplate(string template) {
            if (!string.IsNullOrEmpty(template))
                _sb.Append(template);
            return this;
        }
        public HtmlBuilder LoadFile(string filePath) {
            if (File.Exists(filePath)) {
                var content = File.ReadAllText(filePath);
                _sb.Append(content);
            }
            return this;
        }
        public HtmlBuilder SetVar(string key, string value) {
            _vars[key] = value;
            return this;
        }
        public HtmlBuilder SetVars(Dictionary<string, string> vars) {
            if (vars == null) return this;
            foreach (var kv in vars) _vars[kv.Key] = kv.Value;
            return this;
        }
        public HtmlBuilder ReplaceVars() {
            foreach (var kv in _vars) {
                _sb.Replace("$" + kv.Key, kv.Value ?? string.Empty);
            }
            return this;
        }
        public override string ToString() {
            return _sb.ToString();
        }
        public string ToHtml() {
            ReplaceVars();
            return _sb.ToString();
        }
        public void Clear() {
            _sb.Clear();
            _vars.Clear();
        }
    }
}
