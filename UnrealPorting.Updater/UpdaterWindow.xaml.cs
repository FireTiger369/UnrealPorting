using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;

namespace UnrealPorting.Updater
{
    public partial class UpdaterWindow : Window
    {
        private readonly string _downloadUrl;
        private readonly string _installDir;
        private readonly string _version;

        public UpdaterWindow(string downloadUrl, string installDir, string version)
        {
            InitializeComponent();

            _downloadUrl = downloadUrl;
            _installDir = installDir;
            _version = version;

            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                // Start spinner
                var sb = (Storyboard)FindResource("SpinnerPremium");
                sb.Begin(this, true);

                VersionText.Text = $"Updating to v{_version}";

                string tempZip = Path.Combine(Path.GetTempPath(), "UnrealPorting_Update.zip");

                await DownloadUpdateAsync(tempZip);
                await ExtractUpdateAsync(tempZip);
                RestartMainApp();
                try
                {
                    string updaterPath = Process.GetCurrentProcess().MainModule.FileName;
                    string oldPath = updaterPath + ".old";

                    if (File.Exists(oldPath))
                        File.Delete(oldPath);

                    // Rename current running updater to .old
                    File.Move(updaterPath, oldPath, true);
                }
                catch
                {
                    // ignored — optional log later
                }
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("LOADED EXCEPTION:\n" + ex.ToString());
            }
        }

        // ---------------------------
        // Download with progress
        // ---------------------------
        private async Task DownloadUpdateAsync(string dest)
        {
            using var client = new HttpClient();
            using var response = await client.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;

            using var input = await response.Content.ReadAsStreamAsync();
            using var output = new FileStream(dest, FileMode.Create);

            byte[] buffer = new byte[8192];
            long downloaded = 0;
            int read;

            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await output.WriteAsync(buffer, 0, read);
                downloaded += read;

                if (totalBytes.HasValue)
                {
                    int percent = (int)((downloaded * 100) / totalBytes.Value);
                    ProgressBar.Value = percent;
                    PercentText.Text = percent + "%";
                }
            }
        }

        // ---------------------------
        // Extract ZIP
        // ---------------------------
        private async Task ExtractUpdateAsync(string zipPath)
        {
            await Task.Run(() =>
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "UnrealPorting_Update_Extract");
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);

                Directory.CreateDirectory(tempDir);

                // Extract ZIP into temp folder (preserve internal structure)
                ZipFile.ExtractToDirectory(zipPath, tempDir);

                // Detect if zip has a parent folder (common PublishOutput problem)
                string[] entries = Directory.GetDirectories(tempDir);
                if (entries.Length == 1 && Directory.GetFiles(tempDir).Length == 0)
                {
                    // ZIP contains a single top-level folder -> flatten it
                    string inner = entries[0];
                    CopyFilesRecursively(inner, _installDir);
                }
                else
                {
                    // ZIP contains files at root -> normal behavior
                    CopyFilesRecursively(tempDir, _installDir);
                }

                Directory.Delete(tempDir, true);
            });
        }
        private void CopyFilesRecursively(string source, string target)
        {
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, target));
            }

            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = file.Replace(source, target);

                // Do NOT overwrite the updater while it is running.
                // If file is the updater EXE, save it as .new so the main app can swap it later
                if (Path.GetFileName(file).Equals("UnrealPorting.Updater.exe", StringComparison.OrdinalIgnoreCase))
                {
                    string newUpdaterDest = Path.Combine(target, "UnrealPorting.Updater.exe.new");
                    File.Copy(file, newUpdaterDest, true);
                    continue;
                }

                File.Copy(file, dest, true);
            }
        }


        // ---------------------------
        // Restart main app
        // ---------------------------
        private void RestartMainApp()
        {
            string exePath = Path.Combine(_installDir, "UnrealPorting.exe");

            if (File.Exists(exePath))
                Process.Start(exePath);
        }
    }
}
