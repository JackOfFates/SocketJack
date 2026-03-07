using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace SocketJack.Net {

    /// <summary>
    /// Result of evaluating <c>.htaccess</c> rules against an incoming request.
    /// </summary>
    public enum HtAccessResult {
        /// <summary>Access is allowed.</summary>
        Allowed,
        /// <summary>Access is denied (403 Forbidden).</summary>
        Denied,
        /// <summary>Authentication is required (401 Unauthorized).</summary>
        AuthRequired
    }

    /// <summary>
    /// Fluent builder for generating <c>.htaccess</c> files that control directory
    /// security and access when used with <see cref="HttpServer.MapDirectory"/> or
    /// <see cref="HttpProtocolHandler.MapDirectory"/>.
    /// <para>
    /// Example usage:
    /// <code>
    /// server.Http.MapDirectory("/Update", updateDir, htaccess =&gt; {
    ///     htaccess
    ///         .DenyDirectoryListing()
    ///         .AllowFrom("192.168.1.0/24")
    ///         .DenyFiles("*.log", "*.bak")
    ///         .RequireBasicAuth("Admin Area", "admin", "secret");
    /// });
    /// </code>
    /// </para>
    /// </summary>
    public class HtAccessBuilder {

        private readonly List<string> _lines = new List<string>();

        #region Directory Listing

        /// <summary>Disables auto-generated directory listings (<c>Options -Indexes</c>).</summary>
        public HtAccessBuilder DenyDirectoryListing() {
            _lines.Add("Options -Indexes");
            return this;
        }

        /// <summary>Enables auto-generated directory listings (<c>Options +Indexes</c>).</summary>
        public HtAccessBuilder AllowDirectoryListing() {
            _lines.Add("Options +Indexes");
            return this;
        }

        #endregion

        #region Access Control

        /// <summary>Denies all access to the directory (<c>Require all denied</c>).</summary>
        public HtAccessBuilder DenyAll() {
            _lines.Add("Require all denied");
            return this;
        }

        /// <summary>Grants access to all clients (<c>Require all granted</c>).</summary>
        public HtAccessBuilder AllowAll() {
            _lines.Add("Require all granted");
            return this;
        }

        /// <summary>
        /// Allows access from the specified IP addresses or CIDR ranges.
        /// <para>Example: <c>.AllowFrom("192.168.1.0/24", "10.0.0.1")</c></para>
        /// </summary>
        public HtAccessBuilder AllowFrom(params string[] ipOrCidr) {
            foreach (var ip in ipOrCidr)
                _lines.Add("Allow from " + ip);
            return this;
        }

        /// <summary>
        /// Denies access from the specified IP addresses or CIDR ranges.
        /// <para>Example: <c>.DenyFrom("10.0.0.5")</c></para>
        /// </summary>
        public HtAccessBuilder DenyFrom(params string[] ipOrCidr) {
            foreach (var ip in ipOrCidr)
                _lines.Add("Deny from " + ip);
            return this;
        }

        /// <summary>
        /// Sets the evaluation order for Allow/Deny rules.
        /// <c>"deny,allow"</c> = deny first, then allow overrides (default).
        /// <c>"allow,deny"</c> = allow first, then deny overrides.
        /// </summary>
        public HtAccessBuilder Order(string order) {
            _lines.Add("Order " + order);
            return this;
        }

        #endregion

        #region Authentication

        /// <summary>
        /// Requires HTTP Basic authentication with the given realm and credentials.
        /// Credentials are stored as <c># AuthCredential user:password</c> comment
        /// directives inside the <c>.htaccess</c> file.
        /// </summary>
        /// <param name="realm">The authentication realm displayed to the user.</param>
        /// <param name="credentials">
        /// One or more username/password pairs as <c>"user:password"</c> strings.
        /// </param>
        public HtAccessBuilder RequireBasicAuth(string realm, params string[] credentials) {
            _lines.Add("AuthType Basic");
            _lines.Add("AuthName \"" + (realm ?? "Restricted") + "\"");
            _lines.Add("Require valid-user");
            foreach (var cred in credentials) {
                // Store credentials as a comment directive the server can parse.
                _lines.Add("# AuthCredential " + cred);
            }
            return this;
        }

        #endregion

        #region File Restrictions

        /// <summary>
        /// Denies access to files matching the given glob patterns.
        /// <para>Example: <c>.DenyFiles("*.log", "*.bak", ".env")</c></para>
        /// </summary>
        public HtAccessBuilder DenyFiles(params string[] patterns) {
            var regex = GlobsToRegex(patterns);
            _lines.Add("<FilesMatch \"" + regex + "\">");
            _lines.Add("  Require all denied");
            _lines.Add("</FilesMatch>");
            return this;
        }

        /// <summary>
        /// Restricts access to only the specified file patterns; all others are denied.
        /// <para>Example: <c>.AllowOnlyFiles("*.html", "*.css", "*.js")</c></para>
        /// </summary>
        public HtAccessBuilder AllowOnlyFiles(params string[] patterns) {
            var regex = GlobsToRegex(patterns);
            _lines.Add("# AllowOnlyFiles " + regex);
            return this;
        }

        #endregion

        #region Custom Headers

        /// <summary>
        /// Adds a response header for all files served from this directory.
        /// <para>Example: <c>.AddHeader("X-Frame-Options", "DENY")</c></para>
        /// </summary>
        public HtAccessBuilder AddHeader(string name, string value) {
            _lines.Add("Header set " + name + " \"" + value + "\"");
            return this;
        }

        #endregion

        #region Custom Directives

        /// <summary>
        /// Appends a raw directive line to the <c>.htaccess</c> file.
        /// </summary>
        public HtAccessBuilder AddDirective(string line) {
            _lines.Add(line);
            return this;
        }

        /// <summary>
        /// Appends a comment line to the <c>.htaccess</c> file.
        /// </summary>
        public HtAccessBuilder AddComment(string comment) {
            _lines.Add("# " + comment);
            return this;
        }

        #endregion

        #region Build / Write

        /// <summary>
        /// Builds the <c>.htaccess</c> content as a string.
        /// </summary>
        public string Build() {
            return string.Join("\n", _lines);
        }

        /// <summary>
        /// Writes the generated <c>.htaccess</c> file to the specified directory.
        /// </summary>
        internal void WriteTo(string directory) {
            var path = Path.Combine(directory, ".htaccess");
            File.WriteAllText(path, Build());
        }

        #endregion

        #region Helpers

        private static string GlobsToRegex(string[] patterns) {
            // Convert globs like "*.log" to regex like "\.(log)$"
            var extensions = new List<string>();
            var exactNames = new List<string>();
            foreach (var p in patterns) {
                if (p.StartsWith("*.")) {
                    extensions.Add(EscapeRegex(p.Substring(2)));
                } else {
                    exactNames.Add(EscapeRegex(p));
                }
            }
            var parts = new List<string>();
            if (extensions.Count > 0)
                parts.Add("\\.(" + string.Join("|", extensions) + ")$");
            if (exactNames.Count > 0)
                parts.Add("^(" + string.Join("|", exactNames) + ")$");
            return string.Join("|", parts);
        }

        private static string EscapeRegex(string s) {
            return s.Replace(".", "\\.").Replace("*", ".*").Replace("?", ".");
        }

        #endregion
    }

    /// <summary>
    /// Evaluates <c>.htaccess</c> rules against incoming HTTP requests.
    /// Used internally by <see cref="HttpServer"/> and <see cref="HttpProtocolHandler"/>
    /// to enforce directory-level access control.
    /// </summary>
    public static class HtAccessEvaluator {

        /// <summary>
        /// Parsed representation of an <c>.htaccess</c> file.
        /// </summary>
        internal class HtAccessRules {
            public bool? RequireAllDenied { get; set; }
            public string OrderMode { get; set; } // "deny,allow" or "allow,deny"
            public List<string> AllowFrom { get; set; } = new List<string>();
            public List<string> DenyFrom { get; set; } = new List<string>();
            public bool RequireAuth { get; set; }
            public string AuthType { get; set; }
            public string AuthName { get; set; }
            public List<string> AuthCredentials { get; set; } = new List<string>();
            public List<string> DenyFilePatterns { get; set; } = new List<string>();
            public string AllowOnlyFilesPattern { get; set; }
            public Dictionary<string, string> ResponseHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Checks whether the request should be allowed based on <c>.htaccess</c> rules
        /// in the given directory. Also populates <paramref name="authRealm"/> when
        /// authentication is required but not provided.
        /// </summary>
        /// <param name="localDirectory">The local directory being accessed.</param>
        /// <param name="fileName">The file name being requested (for FilesMatch rules), or null for directory access.</param>
        /// <param name="clientIp">The connecting client's IP address.</param>
        /// <param name="authorizationHeader">The value of the <c>Authorization</c> request header, or null.</param>
        /// <param name="authRealm">Set to the realm name when <see cref="HtAccessResult.AuthRequired"/> is returned.</param>
        /// <param name="responseHeaders">Populated with any <c>Header set</c> directives.</param>
        public static HtAccessResult Evaluate(string localDirectory, string fileName,
            string clientIp, string authorizationHeader,
            out string authRealm, out Dictionary<string, string> responseHeaders) {

            authRealm = null;
            responseHeaders = null;

            if (string.IsNullOrEmpty(localDirectory))
                return HtAccessResult.Allowed;

            var htaccessPath = Path.Combine(localDirectory, ".htaccess");
            if (!File.Exists(htaccessPath))
                return HtAccessResult.Allowed;

            HtAccessRules rules;
            try {
                rules = ParseRules(File.ReadAllLines(htaccessPath));
            } catch {
                return HtAccessResult.Allowed;
            }

            if (rules.ResponseHeaders.Count > 0)
                responseHeaders = new Dictionary<string, string>(rules.ResponseHeaders);

            // Check file-level deny patterns.
            if (!string.IsNullOrEmpty(fileName) && rules.DenyFilePatterns.Count > 0) {
                foreach (var pattern in rules.DenyFilePatterns) {
                    try {
                        if (System.Text.RegularExpressions.Regex.IsMatch(fileName, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            return HtAccessResult.Denied;
                    } catch { }
                }
            }

            // Check allow-only-files pattern.
            if (!string.IsNullOrEmpty(fileName) && !string.IsNullOrEmpty(rules.AllowOnlyFilesPattern)) {
                try {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(fileName, rules.AllowOnlyFilesPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        return HtAccessResult.Denied;
                } catch { }
            }

            // Require all denied/granted.
            if (rules.RequireAllDenied.HasValue) {
                if (rules.RequireAllDenied.Value)
                    return HtAccessResult.Denied;
                // Require all granted — skip IP checks.
                if (!rules.RequireAllDenied.Value && rules.AllowFrom.Count == 0 && rules.DenyFrom.Count == 0) {
                    return CheckAuth(rules, authorizationHeader, out authRealm);
                }
            }

            // IP-based access control.
            if (rules.AllowFrom.Count > 0 || rules.DenyFrom.Count > 0) {
                var ipResult = EvaluateIpAccess(rules, clientIp);
                if (ipResult == HtAccessResult.Denied)
                    return HtAccessResult.Denied;
            }

            // Authentication check.
            return CheckAuth(rules, authorizationHeader, out authRealm);
        }

        private static HtAccessResult CheckAuth(HtAccessRules rules, string authorizationHeader, out string authRealm) {
            authRealm = null;

            if (!rules.RequireAuth)
                return HtAccessResult.Allowed;

            if (rules.AuthCredentials.Count == 0)
                return HtAccessResult.Allowed;

            if (string.IsNullOrEmpty(authorizationHeader)) {
                authRealm = rules.AuthName ?? "Restricted";
                return HtAccessResult.AuthRequired;
            }

            // Parse Basic auth: "Basic base64(user:pass)"
            if (authorizationHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) {
                try {
                    var encoded = authorizationHeader.Substring(6).Trim();
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    foreach (var cred in rules.AuthCredentials) {
                        if (decoded == cred)
                            return HtAccessResult.Allowed;
                    }
                } catch { }
            }

            authRealm = rules.AuthName ?? "Restricted";
            return HtAccessResult.AuthRequired;
        }

        private static HtAccessResult EvaluateIpAccess(HtAccessRules rules, string clientIp) {
            if (string.IsNullOrEmpty(clientIp))
                return HtAccessResult.Denied;

            string orderMode = (rules.OrderMode ?? "deny,allow").ToLowerInvariant().Replace(" ", "");

            if (orderMode == "allow,deny") {
                // Default: denied. Allow rules checked first, then deny can override.
                bool allowed = false;
                if (rules.AllowFrom.Count == 0 && rules.DenyFrom.Count > 0) {
                    // Implicit allow-all except denied.
                    allowed = true;
                }
                foreach (var entry in rules.AllowFrom) {
                    if (IpMatches(clientIp, entry)) { allowed = true; break; }
                }
                foreach (var entry in rules.DenyFrom) {
                    if (IpMatches(clientIp, entry)) return HtAccessResult.Denied;
                }
                return allowed ? HtAccessResult.Allowed : HtAccessResult.Denied;
            } else {
                // "deny,allow" (default): deny rules checked first, allow can override.
                bool denied = false;
                foreach (var entry in rules.DenyFrom) {
                    if (IpMatches(clientIp, entry)) { denied = true; break; }
                }
                foreach (var entry in rules.AllowFrom) {
                    if (IpMatches(clientIp, entry)) return HtAccessResult.Allowed;
                }
                return denied ? HtAccessResult.Denied : HtAccessResult.Allowed;
            }
        }

        private static bool IpMatches(string clientIp, string rule) {
            if (string.IsNullOrEmpty(rule)) return false;
            if (rule.Equals("all", StringComparison.OrdinalIgnoreCase)) return true;

            // Normalize IPv6-mapped IPv4 (::ffff:192.168.1.1 → 192.168.1.1)
            if (clientIp.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
                clientIp = clientIp.Substring(7);

            // CIDR match (e.g., "192.168.1.0/24")
            if (rule.Contains("/")) {
                try {
                    var parts = rule.Split('/');
                    var network = IPAddress.Parse(parts[0]);
                    int prefixLen = int.Parse(parts[1]);
                    var client = IPAddress.Parse(clientIp);

                    var networkBytes = network.GetAddressBytes();
                    var clientBytes = client.GetAddressBytes();
                    if (networkBytes.Length != clientBytes.Length) return false;

                    int fullBytes = prefixLen / 8;
                    int remainingBits = prefixLen % 8;

                    for (int i = 0; i < fullBytes && i < networkBytes.Length; i++) {
                        if (networkBytes[i] != clientBytes[i]) return false;
                    }
                    if (remainingBits > 0 && fullBytes < networkBytes.Length) {
                        byte mask = (byte)(0xFF << (8 - remainingBits));
                        if ((networkBytes[fullBytes] & mask) != (clientBytes[fullBytes] & mask)) return false;
                    }
                    return true;
                } catch {
                    return false;
                }
            }

            // Prefix match (e.g., "192.168.1." or "10.")
            if (rule.EndsWith("."))
                return clientIp.StartsWith(rule, StringComparison.OrdinalIgnoreCase);

            // Exact match
            return clientIp.Equals(rule, StringComparison.OrdinalIgnoreCase);
        }

        internal static HtAccessRules ParseRules(string[] lines) {
            var rules = new HtAccessRules();
            bool inFilesMatch = false;
            string currentFilesMatchPattern = null;

            for (int i = 0; i < lines.Length; i++) {
                var line = lines[i].Trim();
                if (line.Length == 0) continue;

                // Parse AuthCredential comment directives.
                if (line.StartsWith("# AuthCredential ", StringComparison.OrdinalIgnoreCase)) {
                    rules.AuthCredentials.Add(line.Substring("# AuthCredential ".Length));
                    continue;
                }

                // Parse AllowOnlyFiles comment directive.
                if (line.StartsWith("# AllowOnlyFiles ", StringComparison.OrdinalIgnoreCase)) {
                    rules.AllowOnlyFilesPattern = line.Substring("# AllowOnlyFiles ".Length).Trim();
                    continue;
                }

                if (line.StartsWith("#")) continue;

                // FilesMatch blocks.
                if (line.StartsWith("<FilesMatch", StringComparison.OrdinalIgnoreCase)) {
                    var q1 = line.IndexOf('"');
                    var q2 = line.LastIndexOf('"');
                    if (q1 >= 0 && q2 > q1) {
                        currentFilesMatchPattern = line.Substring(q1 + 1, q2 - q1 - 1);
                        inFilesMatch = true;
                    }
                    continue;
                }
                if (line.StartsWith("</FilesMatch", StringComparison.OrdinalIgnoreCase)) {
                    inFilesMatch = false;
                    currentFilesMatchPattern = null;
                    continue;
                }
                if (inFilesMatch && currentFilesMatchPattern != null) {
                    if (line.IndexOf("Require all denied", StringComparison.OrdinalIgnoreCase) >= 0) {
                        rules.DenyFilePatterns.Add(currentFilesMatchPattern);
                    }
                    continue;
                }

                // Global directives.
                if (line.StartsWith("Require all denied", StringComparison.OrdinalIgnoreCase)) {
                    rules.RequireAllDenied = true;
                } else if (line.StartsWith("Require all granted", StringComparison.OrdinalIgnoreCase)) {
                    rules.RequireAllDenied = false;
                } else if (line.StartsWith("Require valid-user", StringComparison.OrdinalIgnoreCase)) {
                    rules.RequireAuth = true;
                } else if (line.StartsWith("Order ", StringComparison.OrdinalIgnoreCase)) {
                    rules.OrderMode = line.Substring("Order ".Length).Trim();
                } else if (line.StartsWith("Allow from ", StringComparison.OrdinalIgnoreCase)) {
                    rules.AllowFrom.Add(line.Substring("Allow from ".Length).Trim());
                } else if (line.StartsWith("Deny from ", StringComparison.OrdinalIgnoreCase)) {
                    rules.DenyFrom.Add(line.Substring("Deny from ".Length).Trim());
                } else if (line.StartsWith("AuthType ", StringComparison.OrdinalIgnoreCase)) {
                    rules.AuthType = line.Substring("AuthType ".Length).Trim();
                } else if (line.StartsWith("AuthName ", StringComparison.OrdinalIgnoreCase)) {
                    var val = line.Substring("AuthName ".Length).Trim();
                    if (val.StartsWith("\"") && val.EndsWith("\"") && val.Length >= 2)
                        val = val.Substring(1, val.Length - 2);
                    rules.AuthName = val;
                } else if (line.StartsWith("Header set ", StringComparison.OrdinalIgnoreCase)) {
                    var rest = line.Substring("Header set ".Length).Trim();
                    var spaceIdx = rest.IndexOf(' ');
                    if (spaceIdx > 0) {
                        var name = rest.Substring(0, spaceIdx);
                        var value = rest.Substring(spaceIdx + 1).Trim().Trim('"');
                        rules.ResponseHeaders[name] = value;
                    }
                }
            }

            return rules;
        }
    }
}
