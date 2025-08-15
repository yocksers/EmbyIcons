using System;
using System.Collections.Generic;

namespace EmbyIcons.Helpers
{
    /// <summary>
    /// A Trie (Prefix Tree) implementation for efficient longest prefix matching of strings.
    /// </summary>
    /// <typeparam name="T">The type of value to store at the end of a key path.</typeparam>
    public class Trie<T>
    {
        private readonly TrieNode _root = new TrieNode();

        private class TrieNode
        {
            public T? Value { get; set; }
            public bool IsTerminal { get; set; }
            public Dictionary<char, TrieNode> Children { get; } = new Dictionary<char, TrieNode>();
        }

        /// <summary>
        /// Inserts a key and its associated value into the Trie.
        /// </summary>
        /// <param name="key">The string key to insert.</param>
        /// <param name="value">The value to associate with the key.</param>
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

        /// <summary>
        /// Finds the value associated with the longest prefix of the given query string that exists in the Trie.
        /// </summary>
        /// <param name="query">The string to search for a prefix of.</param>
        /// <returns>The value of the longest found prefix, or the default value of T if no prefix is found.</returns>
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
