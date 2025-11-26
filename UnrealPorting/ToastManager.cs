using System.Collections.Generic;
using System.Windows;

namespace UnrealPorting
{
    public static class ToastManager
    {
        private static readonly List<ToastWindow> ActiveToasts = new();

        public static void ShowToast(Window parent, string message, ToastType type = ToastType.Info)
        {
            // Calculate stacked vertical offset
            double offsetY = 20;
            foreach (var t in ActiveToasts)
                offsetY += t.ActualHeight + 12;

            // Create toast (positioning happens inside constructor)
            var toast = new ToastWindow(parent, message, type, offsetY);

            ActiveToasts.Add(toast);

            toast.ToastClosed += () =>
            {
                ActiveToasts.Remove(toast);
                RepositionToasts(parent);
            };

            toast.Show();
        }

        private static void RepositionToasts(Window parent)
        {
            double offsetY = 20;

            foreach (var t in ActiveToasts)
            {
                Point parentTopRight =
                    parent.PointToScreen(new Point(parent.ActualWidth, 0));

                t.Left = parentTopRight.X - t.ActualWidth - 20;
                t.Top = parentTopRight.Y + offsetY;

                offsetY += t.ActualHeight + 12;
            }
        }
    }
}
