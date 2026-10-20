using System.Collections.Generic;

namespace EmbyIcons.Helpers
{
    /// <summary>
    /// Case-insensitive path trie. Matches only on directory boundaries, so a key of
    /// "/media/movies" matches "/media/movies/x.mkv" but not "/media/movies-old/x.mkv".
    /// Both '/' and '\' are treated as separators.
    /// </summary>
    public class Trie<T>
    {
        private readonly TrieNode _root = new TrieNode();

        private class TrieNode
        {
            public T? Value { get; set; }
            public bool IsTerminal { get; set; }
            public Dictionary<char, TrieNode> Children { get; } = new Dictionary<char, TrieNode>();
        }

        private static string NormalizePath(string path)
        {
            var normalized = path.ToLowerInvariant().Replace('\\', '/');
            var trimmed = normalized.TrimEnd('/');
            return trimmed.Length == 0 ? normalized : trimmed;
        }

        public void Insert(string key, T value)
        {
            if (string.IsNullOrEmpty(key)) return;

            var node = _root;
            var normalizedKey = NormalizePath(key);

            foreach (char c in normalizedKey)
            {
                if (!node.Children.TryGetValue(c, out var child))
                {
                    child = new TrieNode();
                    node.Children[c] = child;
                }
                node = child;
            }
            node.IsTerminal = true;
            node.Value = value;
        }

        public T? FindLongestPrefix(string query)
        {
            if (string.IsNullOrEmpty(query)) return default;

            var node = _root;
            T? longestPrefixValue = default;

            var normalizedQuery = query.ToLowerInvariant().Replace('\\', '/');
            for (int i = 0; i < normalizedQuery.Length; i++)
            {
                var c = normalizedQuery[i];
                if (!node.Children.TryGetValue(c, out var child))
                {
                    break;
                }

                node = child;
                if (node.IsTerminal && IsBoundary(normalizedQuery, i))
                {
                    longestPrefixValue = node.Value;
                }
            }

            return longestPrefixValue;
        }

        private static bool IsBoundary(string path, int index) =>
            path[index] == '/' || index == path.Length - 1 || path[index + 1] == '/';

        public void Clear()
        {
            _root.Children.Clear();
            _root.Value = default;
            _root.IsTerminal = false;
        }
    }
}
