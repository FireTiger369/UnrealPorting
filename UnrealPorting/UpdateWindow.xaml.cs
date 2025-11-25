using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UnrealPorting.Updater;

namespace UnrealPorting
{
    public partial class UpdateWindow : Window
    {
        private readonly UpdateManifest _manifest;

        public UpdateWindow(UpdateManifest manifest)
        {

            InitializeComponent();
            _manifest = manifest;

            ChangelogText.Text = manifest.changelog;
        }

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Update_Click(object sender, RoutedEventArgs e)
        {
            string updaterExe = Path.Combine(
                Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName),
                "UnrealPorting.Updater.exe"
            );

            if (!File.Exists(updaterExe))
            {
                MessageBox.Show("Updater executable missing:\n" + updaterExe);
                return;
            }

            // Correct install directory (where main EXE actually lives)
            string installDir = Path.GetDirectoryName(
                Process.GetCurrentProcess().MainModule.FileName
            );

            string downloadUrl = _manifest.download_url;
            string version = _manifest.version;

            string args = $"\"{downloadUrl}\" \"{installDir}\" \"{version}\"";

            try
            {
                Process.Start(updaterExe, args);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start updater:\n" + ex.Message);
                return;
            }

            // FULL shutdown required so updater can overwrite files
            Application.Current.Shutdown();
            Environment.Exit(0);
        }
    }
}
