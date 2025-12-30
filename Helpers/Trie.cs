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
            if (string.IsNullOrEmpty(key)) return;
            
            var node = _root;
            var lowerKey = key.ToLowerInvariant();
            
            foreach (char c in lowerKey)
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

            if (node.IsTerminal)
            {
                longestPrefixValue = node.Value;
            }

            var lowerQuery = query.ToLowerInvariant();
            foreach (char c in lowerQuery)
            {
                if (node.Children.TryGetValue(c, out var child))
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

        public void Clear()
        {
            // MEMORY LEAK FIX: Clear all nodes to release memory
            _root.Children.Clear();
            _root.Value = default;
            _root.IsTerminal = false;
        }
    }
}