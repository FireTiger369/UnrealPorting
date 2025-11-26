using System;
using System.Windows;
using System.Windows.Controls;

namespace UnrealPorting
{
    public partial class MipSelectWindow : Window
    {
        public int SelectedMip { get; private set; } = -1;

        public MipSelectWindow((int x, int y)[] mipSizes)
        {
            InitializeComponent();

            for (int i = 0; i < mipSizes.Length; i++)
            {
                int index = i;
                var (w, h) = mipSizes[i];

                var btn = new Button
                {
                    Style = (Style)FindResource("MipButton"),
                    Content = $"Mip {index}\n{w} x {h}",
                    Height = 60
                };

                btn.Click += (_, __) =>
                {
                    SelectedMip = index;
                    DialogResult = true;
                };

                MipGrid.Children.Add(btn);
            }
        }
    }
}
