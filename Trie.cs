using System;
using System.Collections.Generic;

namespace EmbyIcons.Helpers
{
    public class Trie<T>
    {
        private readonly TrieNode _root = new TrieNode();

        private class TrieNode
        {
            public T? Value { get; set; }
            public bool IsTerminal { get; set; }
            public Dictionary<char, TrieNode> Children { get; } = new Dictionary<char, TrieNode>();
        }

        public void Insert(string key, T value)
        {
            var node = _root;
            foreach (char c in key)
            {
                var lowerChar = char.ToLowerInvariant(c);
                if (!node.Children.TryGetValue(lowerChar, out var child))
                {
                    child = new TrieNode();
                    node.Children[lowerChar] = child;
                }
                node = child;
            }
            node.IsTerminal = true;
            node.Value = value;
        }

        public T? FindLongestPrefix(string query)
        {
            var node = _root;
            T? longestPrefixValue = default;

            if (node.IsTerminal)
            {
                longestPrefixValue = node.Value;
            }

            foreach (char c in query)
            {
                var lowerChar = char.ToLowerInvariant(c);
                if (node.Children.TryGetValue(lowerChar, out var child))
                {
                    node = child;
                    if (node.IsTerminal)
                    {
                        longestPrefixValue = node.Value;
                    }
                }
                else
                {
                    break;
                }
            }

            return longestPrefixValue;
        }
    }
}