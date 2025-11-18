using Microsoft.WindowsAPICodePack.Dialogs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace UnrealPorting
{
    public partial class MappingsWindow : Window
    {
        public List<string> SelectedMappings { get; private set; } = new();

        public MappingsWindow()
        {
            InitializeComponent();
        }
        public string SelectedMapping { get; private set; } = "";


        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CommonOpenFileDialog()
            {
                IsFolderPicker = false,
                Multiselect = false,
                Title = "Select a .usmap mapping file"
            };

            dlg.Filters.Add(new CommonFileDialogFilter("Mappings (*.usmap)", "*.usmap"));

            if (dlg.ShowDialog() == CommonFileDialogResult.Ok)
            {
                string file = dlg.FileName;

                MappingsPathBox.Text = file;

                MappingsList.Items.Clear();
                MappingsList.Items.Add(file);

                SelectedMapping = file;  // store it
            }
        }



        private void LoadMappingsFromFolder(string path)
        {
            MappingsList.Items.Clear();

            if (!Directory.Exists(path))
                return;
            
            var files = Directory.GetFiles(path, "*.usmap", SearchOption.TopDirectoryOnly);

            foreach (var file in files)
                MappingsList.Items.Add(file);
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(SelectedMapping))
            {
                DialogResult = true;
                Close();
            }
        }


        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
