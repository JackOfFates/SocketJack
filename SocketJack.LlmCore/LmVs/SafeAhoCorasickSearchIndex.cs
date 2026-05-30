using System;
using System.Collections.Generic;

namespace LmVs
{
    internal sealed class SafeAhoCorasickSearchIndex
    {
        private readonly Node _root = new Node();
        private readonly string[] _patterns;

        private SafeAhoCorasickSearchIndex(string[] patterns)
        {
            _patterns = patterns;
            BuildTrie();
            BuildFailures();
        }

        public static bool TryCreate(IEnumerable<string> patterns, out SafeAhoCorasickSearchIndex index, out string error)
        {
            index = null;
            error = "";

            try
            {
                if (patterns == null)
                {
                    error = "No search patterns were supplied.";
                    return false;
                }

                var cleanPatterns = new List<string>();
                foreach (string pattern in patterns)
                {
                    if (string.IsNullOrWhiteSpace(pattern))
                        continue;

                    string clean = pattern.Trim();
                    bool exists = false;
                    foreach (string existing in cleanPatterns)
                    {
                        if (string.Equals(existing, clean, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }

                    if (!exists)
                        cleanPatterns.Add(clean);
                    if (cleanPatterns.Count >= 32)
                        break;
                }

                if (cleanPatterns.Count == 0)
                {
                    error = "Search query is empty.";
                    return false;
                }

                index = new SafeAhoCorasickSearchIndex(cleanPatterns.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                index = null;
                error = ex.Message;
                return false;
            }
        }

        public bool Contains(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            try
            {
                return ContainsCore(text);
            }
            catch
            {
                for (int i = 0; i < _patterns.Length; i++)
                {
                    if (text.IndexOf(_patterns[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }

                return false;
            }
        }

        private bool ContainsCore(string text)
        {
            Node node = _root;
            foreach (char ch in text)
            {
                char key = char.ToUpperInvariant(ch);
                while (!ReferenceEquals(node, _root) && !node.Next.ContainsKey(key))
                    node = node.Fail ?? _root;

                Node next;
                node = node.Next.TryGetValue(key, out next) ? next : _root;
                if (node.Terminal)
                    return true;
            }

            return false;
        }

        private void BuildTrie()
        {
            for (int i = 0; i < _patterns.Length; i++)
            {
                Node node = _root;
                foreach (char ch in _patterns[i])
                {
                    char key = char.ToUpperInvariant(ch);
                    Node next;
                    if (!node.Next.TryGetValue(key, out next))
                    {
                        next = new Node();
                        node.Next[key] = next;
                    }

                    node = next;
                }

                node.Terminal = true;
            }
        }

        private void BuildFailures()
        {
            var queue = new Queue<Node>();
            foreach (Node child in _root.Next.Values)
            {
                child.Fail = _root;
                queue.Enqueue(child);
            }

            while (queue.Count > 0)
            {
                Node current = queue.Dequeue();
                foreach (KeyValuePair<char, Node> transition in current.Next)
                {
                    char key = transition.Key;
                    Node child = transition.Value;
                    Node fallback = current.Fail ?? _root;

                    while (!ReferenceEquals(fallback, _root) && !fallback.Next.ContainsKey(key))
                        fallback = fallback.Fail ?? _root;

                    Node failTarget;
                    child.Fail = fallback.Next.TryGetValue(key, out failTarget) ? failTarget : _root;
                    child.Terminal = child.Terminal || (child.Fail != null && child.Fail.Terminal);
                    queue.Enqueue(child);
                }
            }
        }

        private sealed class Node
        {
            public readonly Dictionary<char, Node> Next = new Dictionary<char, Node>();
            public Node Fail;
            public bool Terminal;
        }
    }
}
