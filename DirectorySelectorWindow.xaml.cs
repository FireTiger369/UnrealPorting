using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using UnrealPorting.Properties;
using UnrealPorting2;

namespace UnrealPorting
{
    public partial class DirectorySelectorWindow : Window
    {
        public DirectorySelectorWindow()
        {
            InitializeComponent();

            // Populate the existing profile list
            foreach (var profile in GameProfileStore.Profiles)
                ExistingProfilesCombo.Items.Add(profile.Name);

            ExistingProfilesCombo.SelectedIndex =
                GameProfileStore.Profiles.Count > 0 ? 0 : -1;
        }

        private void BrowseDirectory_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                NewDirBox.Text = dlg.SelectedPath;
            }
        }

        private void AddGame_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(NewNameBox.Text) ||
                string.IsNullOrWhiteSpace(NewDirBox.Text))
            {
                MessageBox.Show("Please fill out all fields.");
                return;
            }

            var profile = new GameProfile
            {
                Name = NewNameBox.Text,
                Directory = NewDirBox.Text,
                EngineVersion =
                (NewEngineCombo.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "",
                AesFileKeys = new Dictionary<string, string>(),
                AesGuidKeys = new Dictionary<string, string>(),
                MappingPath = ""
            };

            GameProfileStore.Profiles.Add(profile);
            GameProfileStore.Save();

            ExistingProfilesCombo.Items.Add(profile.Name);
            ExistingProfilesCombo.SelectedItem = profile.Name;

            MessageBox.Show("Game profile added!");
        }

        private void OK_Click(object sender, RoutedEventArgs e)
        {
            if (ExistingProfilesCombo.SelectedItem is string selectedName)
            {
                var profile = GameProfileStore.Profiles
                    .FirstOrDefault(p => p.Name == selectedName);

                if (profile != null)
                {
                    App.SelectedProfile = profile;
                    Console.WriteLine($"[PROFILE] Selected profile: {profile.Name}");
                }
                else
                {
                    Console.WriteLine("[PROFILE] ERROR: selected profile name not found.");
                }
            }
            else
            {
                Console.WriteLine("[PROFILE] No profile selected.");
            }

            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
