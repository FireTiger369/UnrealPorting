using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace UnrealPorting
{
    public partial class ToastWindow : Window
    {
        private readonly Window _parent;
        private readonly double _offsetY;

        public event Action? ToastClosed;
        private double _displayDuration = 3;

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            ToastClosed?.Invoke();
        }

        public void StartAnimation()
        {
            RunAnimation();
        }
        private void StartTimerBar()
        {
            // Duration scaled to message length
            double baseDuration = 2.7;
            double duration = baseDuration + (MessageText.Text.Length / 40.0);
            if (duration > 7) duration = 7;

            AnimateTimerBar(duration);

            // Store for RunAnimation’s fade-out
            _displayDuration = duration;
        }
        private readonly string? _filePath;
        public ToastWindow(Window parent, string message, ToastType type, double offsetY, string? filePath = null)
        {
            InitializeComponent();

            _parent = parent;
            _offsetY = offsetY;
            _filePath = filePath;

            MessageText.Text = message;
            if (!string.IsNullOrEmpty(_filePath))
            {
                var link = new TextBlock
                {
                    Text = "Open Folder",
                    Foreground = new SolidColorBrush(Color.FromRgb(90, 166, 255)),
                    FontSize = 13,
                    Margin = new Thickness(14, 0, 14, 6),
                    Cursor = Cursors.Hand,
                    TextDecorations = TextDecorations.Underline
                };

                link.MouseLeftButtonDown += (s, e) =>
                {
                    try
                    {
                        if (File.Exists(_filePath))
                            Process.Start("explorer.exe", "/select," + _filePath);
                        else if (Directory.Exists(_filePath))
                            Process.Start("explorer.exe", _filePath);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to open folder:\n" + ex.Message);
                    }
                };

                Grid.SetColumn(link, 1); // same column as MessageText
                Grid.SetRow(link, 1);    // row under the message

                ((Grid)OuterBorder.Child).Children.Add(link);
            }

            string accent = "#5AA6FF";
            string dropShadow = "#5AA6FF";
            string timerbar = "#5AA6FF";
            switch (type)
            {
                case ToastType.Success: accent = "#4ADE80"; break;
                case ToastType.Warning: accent = "#F59E0B"; break;
                case ToastType.Error: accent = "#EF4444"; break;
            }
            switch (type)
            {
                case ToastType.Success: dropShadow = "#4ADE80"; break;
                case ToastType.Warning: dropShadow = "#F59E0B"; break;
                case ToastType.Error: dropShadow = "#EF4444"; break;
            }
            switch (type)
            {
                case ToastType.Success: timerbar = "#4ADE80"; break;
                case ToastType.Warning: timerbar = "#F59E0B"; break;
                case ToastType.Error: timerbar = "#EF4444"; break;
            }
            Accent.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accent));
            TimerBar.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(timerbar));
            var dropShadowEffect = OuterBorder.Effect as DropShadowEffect;
            if (Effect != null)
            {
                dropShadowEffect.Color = (Color)ColorConverter.ConvertFromString(dropShadow);
            }

            // Position AFTER layout is ready
            ContentRendered += (_, __) =>
            {
                // Now layout is FINAL
                UpdateLayout();

                // Position in top-right
                Point parentTopRight = _parent.PointToScreen(new Point(_parent.ActualWidth, 0));

                Left = parentTopRight.X - ActualWidth - 20;
                Top = parentTopRight.Y + _offsetY;

                // 🔥 NOW start TOAST + TIMER animation
                RunAnimation();
                StartTimerBar();
            };
        }

        private void AnimateTimerBar(double duration)
        {
            // Get the full toast width AFTER layout is measured
            double fullWidth = OuterBorder.ActualWidth - 10; // small padding

            if (fullWidth < 20)
                fullWidth = 20;

            TimerBar.Width = fullWidth;

            var shrinkAnim = new DoubleAnimation
            {
                From = fullWidth,
                To = 0,
                Duration = TimeSpan.FromSeconds(duration),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            TimerBar.BeginAnimation(WidthProperty, shrinkAnim);
        }

        private void RunAnimation()
        {
            OuterBorder.Opacity = 0;
            Slide.X = 60;

            // 📌 Duration can scale based on text length:
            double baseDuration = 3.0;
            double duration = baseDuration + (MessageText.Text.Length / 40.0);
            if (duration > 7) duration = 7; // cap max if you want

            // Fade + Slide IN
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var slideIn = new DoubleAnimation(60, 0, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            OuterBorder.BeginAnimation(OpacityProperty, fadeIn);
            Slide.BeginAnimation(TranslateTransform.XProperty, slideIn);

            // Auto-Close timer
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_displayDuration) };

            timer.Tick += (s, e) =>
            {
                timer.Stop();

                var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                var slideOut = new DoubleAnimation(0, 60, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOut.Completed += (_, __) => Close();

                OuterBorder.BeginAnimation(OpacityProperty, fadeOut);
                Slide.BeginAnimation(TranslateTransform.XProperty, slideOut);
            };

            timer.Start();
        }
    }

    public enum ToastType
    {
        Info,
        Success,
        Warning,
        Error
    }
}
