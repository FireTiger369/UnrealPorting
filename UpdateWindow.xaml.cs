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
                AppDomain.CurrentDomain.BaseDirectory,
                "UnrealPorting.Updater.exe"
            );

            if (!File.Exists(updaterExe))
            {
                MessageBox.Show("Updater executable missing.");
                return;
            }

            // PASS ALL NEEDED ARGUMENTS
            string downloadUrl = _manifest.download_url;
            string installDir = AppDomain.CurrentDomain.BaseDirectory;
            string version = _manifest.version;

            // Wrap them safely for command line
            string args =
                $"\"{downloadUrl}\" " +
                $"\"{installDir}\" " +
                $"\"{version}\"";

            Process.Start(updaterExe, args);

            Application.Current.Shutdown();
        }
    }
}
