using System.Windows;
using System.Windows.Controls;
using UnrealPorting.Properties;
using UnrealPorting2;

namespace UnrealPorting
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();

            // Load stored settings
            EngineVersionCombo.SelectedIndex = Properties.Settings.Default.EngineVersionIndex;
            UseCustomMappingsBox.IsChecked = Properties.Settings.Default.UseCustomMappings;

            var profile = App.SelectedProfile;
            if (profile == null)
                return;

            // Restore engine version
            foreach (ComboBoxItem item in EngineComboBox.Items)
            {
                if (item.Content.ToString() == profile.EngineVersion)
                {
                    EngineComboBox.SelectedItem = item;
                    break;
                }
            }
            MappingsPathBox.Text = profile.MappingPath;
            UseCustomMappingsBox.IsChecked = !string.IsNullOrEmpty(profile.MappingPath);

        }



        private void SelectMappings_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "USMAP Files (*.usmap)|*.usmap|All Files (*.*)|*.*";

            if (dlg.ShowDialog() == true)
            {
                MappingsPathBox.Text = dlg.FileName;  // ⭐ FIX: store in textbox only

                MessageBox.Show("Selected Mappings:\n" + dlg.FileName,
                                "Mappings Loaded",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            var profile = App.SelectedProfile;
            if (profile == null)
                return;

            // Save mapping only if checkbox checked
            if (UseCustomMappingsBox.IsChecked == true)
                profile.MappingPath = MappingsPathBox.Text.Trim();
            else
                profile.MappingPath = "";

            // Save engine version
            profile.EngineVersion = (EngineComboBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "";

            GameProfileStore.Save();

            MessageBox.Show("Settings saved.");
            this.DialogResult = true;
            Close();
        }


        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
