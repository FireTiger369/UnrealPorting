using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using UnrealPorting.Properties;

namespace UnrealPorting2
{
    public partial class App : Application
    {
        public static event Action<GameProfile>? ProfileChanged;

        public static GameProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                _selectedProfile = value;
                ProfileChanged?.Invoke(value);
            }
        }
        private static GameProfile? _selectedProfile;


        public App()
        {

            // Global domain-level exceptions (non-UI threads)
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                Console.WriteLine("[FATAL] Unhandled: " + (ex?.Message ?? e.ExceptionObject.ToString()));
                Console.WriteLine(ex?.StackTrace);
                Debug.WriteLine("[FATAL] " + ex);
            };

            // UI (Dispatcher) exceptions
            DispatcherUnhandledException += (s, e) =>
            {
                Console.WriteLine("[UI ERROR] " + e.Exception.Message);
                Console.WriteLine(e.Exception.StackTrace);
                Debug.WriteLine("[UI ERROR] " + e.Exception);
                e.Handled = true;
            };

            // Task-based exceptions (async void / async Task fire-and-forget)
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                Console.WriteLine("[TASK ERROR] " + e.Exception.Message);
                Console.WriteLine(e.Exception.StackTrace);
                Debug.WriteLine("[TASK ERROR] " + e.Exception);
                e.SetObserved();
            };
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "UnrealPorting");
                if (Directory.Exists(tempDir))
                {
                    foreach (var file in Directory.GetFiles(tempDir, "aes_temp_*.txt"))
                    {
                        File.Delete(file);
                    }
                    Console.WriteLine("[AES] Cleared temporary AES files on exit.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Failed to clear temporary AES files: {ex.Message}");
            }

            base.OnExit(e);
        }

    }
}

