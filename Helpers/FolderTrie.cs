using System;
using System.Collections.Generic;

namespace UnrealPorting.Helpers
{
    public class FolderNode
    {
        public string Name { get; }
        public Dictionary<string, FolderNode> Children { get; } = new();
        public int FileCount { get; set; }

        public FolderNode(string name = "")
        {
            Name = name;
        }
    }

    public class FolderTrie
    {
        public FolderNode Root { get; }

        private readonly StringInterner _interner;

        public FolderTrie(StringInterner interner)
        {
            _interner = interner ?? new StringInterner();
            Root = new FolderNode("Root");
        }

        /// <summary>
        /// Adds a normalized path like "FortniteGame/Content/Textures/Grass.uasset"
        /// into the folder trie.
        /// </summary>
        public void AddPath(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
                return;

            string[] parts = fullPath.Replace('\\', '/')
                                     .Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
                return;

            FolderNode node = Root;

            // all but the last segment are folders
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string part = _interner.Intern(parts[i]);
                if (!node.Children.TryGetValue(part, out var next))
                {
                    next = new FolderNode(part);
                    node.Children[part] = next;
                }
                node = next;
            }

            // mark that this folder contains at least one file
            node.FileCount++;
        }

        public FolderNode? GetNode(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return Root;

            string[] parts = path.Replace('\\', '/')
                                 .Split('/', StringSplitOptions.RemoveEmptyEntries);

            var node = Root;
            foreach (var part in parts)
            {
                if (!node.Children.TryGetValue(part, out var next))
                    return null;
                node = next;
            }

            return node;
        }

    public void Compact()
        {
            void Recurse(FolderNode node)
            {
                foreach (var kv in node.Children)
                    Recurse(kv.Value);

                // Trim dictionary and list capacity
                if (node.Children is Dictionary<string, FolderNode> dict)
                {
                    foreach (var k in dict.Keys.ToList()) { /* trigger JIT compaction */ }
                }
            }
            Recurse(Root);
            GC.Collect();
        }

    }
}
