using System;
using System.Windows;

namespace UnrealPorting.Updater
{
    public class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                MessageBox.Show("UNHANDLED EXCEPTION:\n" + e.ExceptionObject.ToString());
            };

            if (args.Length != 3)
            {
                MessageBox.Show("Updater launched incorrectly.\nArguments missing.");
                return;
            }

            string downloadUrl = args[0];
            string installDir = args[1];
            string version = args[2];

            var app = new Application();
            var win = new UpdaterWindow(downloadUrl, installDir, version);
            app.Run(win);
        }
    }
}
