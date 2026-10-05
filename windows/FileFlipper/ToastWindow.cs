using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FileFlipper;

/// <summary>A small pill near the bottom of the screen that reports progress and results. Clicks pass through it.</summary>
public sealed class ToastWindow : Window
{
    private readonly TextBlock icon = new();
    private readonly TextBlock label = new();
    private readonly Border pill;
    private int token;

    public ToastWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Focusable = false;
        IsHitTestVisible = false;

        icon.FontFamily = Palette.IconFont;
        icon.FontSize = 16;
        icon.Foreground = Palette.Brush(Palette.Orange);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin = new Thickness(0, 0, 10, 0);

        label.FontFamily = Palette.TextFont;
        label.FontSize = 13.5;
        label.FontWeight = FontWeights.Medium;
        label.Foreground = Palette.Brush(Palette.Brown);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        label.MaxWidth = 460;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(label);
        pill = new Border
        {
            Child = row,
            Background = Palette.Brush(Palette.Cream, 0.98),
            BorderBrush = Palette.Brush(Palette.BubbleBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 11, 20, 11),
            MinWidth = 180,
            Margin = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.22, Direction = 270 },
        };
        Content = pill;
        SourceInitialized += (_, _) => Native.MakeToolWindow(this, clickThrough: true);
    }

    /// <summary>Shows <paramref name="text"/>; a null duration keeps it up until the next message.</summary>
    public void Show(string text, string glyph, double? seconds = 2.5)
    {
        int current = ++token;
        icon.Text = glyph;
        label.Text = text;
        if (!IsVisible)
        {
            pill.Opacity = 0;
            Show();
        }
        UpdateLayout();
        var area = Native.WorkAreaAtCursor;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 64;
        pill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));

        if (seconds is not double duration) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(duration) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (current != token) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
            fade.Completed += (_, _) => { if (current == token) Hide(); };
            pill.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }
}
