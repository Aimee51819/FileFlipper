using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileFlipper;

/// <summary>A small message window in FileFlipper's colours, used for the guide and About.</summary>
public static class InfoWindow
{
    private static Window? current;

    /// <summary>Shows the window; returns the index of the button that was clicked (or -1 if closed).</summary>
    public static int Show(string title, IEnumerable<string> paragraphs, IReadOnlyList<string> buttons, string? link = null)
    {
        current?.Close();
        int clicked = -1;
        var window = new Window
        {
            Title = title,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = Palette.Brush(Palette.Cream),
            Topmost = true,
        };
        try { window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileFlipper.ico")); } catch { }

        var stack = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        try
        {
            header.Children.Add(new Image
            {
                Source = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileFlipper.ico")),
                Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0),
            });
        }
        catch { }
        header.Children.Add(new TextBlock
        {
            Text = title, FontFamily = Palette.TextFont, FontSize = 20, FontWeight = FontWeights.SemiBold,
            Foreground = Palette.Brush(Palette.Brown), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
        });
        stack.Children.Add(header);

        foreach (var paragraph in paragraphs)
        {
            stack.Children.Add(new TextBlock
            {
                Text = paragraph, TextWrapping = TextWrapping.Wrap, FontFamily = Palette.TextFont, FontSize = 13.5,
                Foreground = Palette.Brush(Palette.Brown, 0.9), Margin = new Thickness(0, 0, 0, 10), LineHeight = 20,
            });
        }
        if (link != null)
        {
            var hyperlink = new Hyperlink(new Run(link)) { NavigateUri = new Uri(link), Foreground = Palette.Brush(Palette.Icon) };
            hyperlink.RequestNavigate += (_, e) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); } catch { }
            };
            stack.Children.Add(new TextBlock(hyperlink) { FontFamily = Palette.TextFont, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });
        }

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        for (int index = buttons.Count - 1; index >= 0; index--)
        {
            int captured = index;
            bool primary = index == 0;
            var button = new Button
            {
                Content = buttons[index],
                MinWidth = 96,
                Height = 32,
                Padding = new Thickness(16, 0, 16, 0),
                Margin = new Thickness(10, 0, 0, 0),
                FontFamily = Palette.TextFont,
                FontSize = 13.5,
                FontWeight = primary ? FontWeights.Bold : FontWeights.SemiBold,
                Foreground = primary ? Brushes.White : Palette.Brush(Palette.Brown),
                Background = primary ? Palette.Brush(Palette.Orange) : Palette.Brush(Palette.Peach, 0.7),
                BorderThickness = new Thickness(0),
                IsDefault = primary,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(16));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            button.Click += (_, _) => { clicked = captured; window.Close(); };
            row.Children.Add(button);
        }
        stack.Children.Add(row);
        window.Content = stack;
        window.Closed += (_, _) => { if (current == window) current = null; };
        window.Loaded += (_, _) => window.Activate();
        current = window;
        window.ShowDialog();
        return clicked;
    }
}
