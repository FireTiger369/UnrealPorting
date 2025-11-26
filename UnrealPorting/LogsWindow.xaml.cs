using System.Windows;

namespace UnrealPorting
{
    public partial class LogsWindow : Window
    {
        public LogsWindow()
        {
            InitializeComponent();
        }
        public void AddLog(string msg)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddLog(msg));
                return;
            }

            if (LogsList.Items.Count > 5000)
                LogsList.Items.RemoveAt(0);

            string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            LogsList.Items.Add(line);

            LogsList.ScrollIntoView(line);
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            LogsList.Items.Clear();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
        }
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            e.Cancel = true; // stop destroy
            this.Hide();     // hide instead
        }
    }
}
