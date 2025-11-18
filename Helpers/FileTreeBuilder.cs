using System;
using System.IO;
using System.Windows.Controls;

namespace UnrealPorting.Helpers
{
    public static class FileTreeBuilder
    {
        public static TreeViewItem CreateDirectoryNodeLazy(DirectoryInfo dirInfo)
        {
            var dirNode = new TreeViewItem
            {
                Header = dirInfo.Name,
                Tag = dirInfo.FullName
            };

            dirNode.Expanded += FolderNode_Expanded;

            // Add dummy child for lazy-loading
            if (HasAccessibleChildren(dirInfo))
                dirNode.Items.Add(null);

            return dirNode;
        }

        private static bool HasAccessibleChildren(DirectoryInfo dirInfo)
        {
            try
            {
                return dirInfo.GetDirectories().Length > 0 || dirInfo.GetFiles().Length > 0;
            }
            catch { return false; }
        }

        private static void FolderNode_Expanded(object sender, System.Windows.RoutedEventArgs e)
        {
            var node = sender as TreeViewItem;
            if (node == null || node.Items.Count != 1 || node.Items[0] != null)
                return;

            node.Items.Clear();

            string path = node.Tag as string;
            if (string.IsNullOrEmpty(path))
                return;

            DirectoryInfo dirInfo = new DirectoryInfo(path);

            // Add subdirectories
            DirectoryInfo[] subDirs = new DirectoryInfo[0];
            try { subDirs = dirInfo.GetDirectories(); } catch { }
            foreach (var dir in subDirs)
                node.Items.Add(CreateDirectoryNodeLazy(dir));

            // Add files
            FileInfo[] files = new FileInfo[0];
            try { files = dirInfo.GetFiles(); } catch { }
            foreach (var file in files)
                node.Items.Add(new TreeViewItem { Header = file.Name, Tag = file.FullName });
        }
    }
}
