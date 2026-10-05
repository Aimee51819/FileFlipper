using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FileFlipper.Loc;

namespace FileFlipper;

/// <summary>
/// A small window for the image "Crop" tool: shows the picture, lets the user drag out an area
/// (optionally locked to an aspect ratio) and returns that area in image pixels.
/// </summary>
public static class CropWindow
{
    private static readonly (string Title, double? Ratio)[] Aspects =
    {
        (L("Free"), null), ("1:1", 1), ("4:3", 4.0 / 3), ("3:2", 3.0 / 2), ("16:9", 16.0 / 9), ("9:16", 9.0 / 16),
    };

    /// <summary>Runs modally. Returns the crop rectangle in pixels, or null if cancelled.</summary>
    public static Int32Rect? Run(BitmapSource image, string fileName)
    {
        const double canvasPadding = 18;
        var work = Native.WorkAreaAtCursor;
        double maxWidth = Math.Min(760, work.Width - 120), maxHeight = Math.Min(520, work.Height - 200);
        double scale = Math.Min(Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight), 1);
        var canvasSize = new Size(Math.Max(image.PixelWidth * scale + canvasPadding * 2, 700),
                                  Math.Max(image.PixelHeight * scale + canvasPadding * 2, 240));

        var cropView = new CropView(image, canvasPadding) { Width = canvasSize.Width, Height = canvasSize.Height };
        var window = new Window
        {
            Title = L("Crop “%@”", fileName),
            Background = Palette.Brush(Palette.Cream),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            UseLayoutRounding = true,
        };
        try { window.Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileFlipper.ico")); } catch { }

        var root = new DockPanel { Margin = new Thickness(20) };
        var controls = new DockPanel { Margin = new Thickness(0, 16, 0, 0), LastChildFill = false };
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(cropView);

        var choices = new List<Button>();
        var sizeLabel = new TextBlock
        {
            Foreground = Palette.Brush(Palette.Brown, 0.6),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            MinWidth = 120,
        };
        void UpdateSize()
        {
            var rect = cropView.CropRectInPixels;
            sizeLabel.Text = $"{rect.Width} × {rect.Height} px";
        }
        void Select(Button chosen)
        {
            foreach (var button in choices) PillButton.SetOn(button, button == chosen);
        }
        for (int index = 0; index < Aspects.Length; index++)
        {
            var aspect = Aspects[index];
            var button = PillButton.Create(aspect.Title, PillButton.Style.Choice, 28);
            button.Margin = new Thickness(0, 0, 6, 0);
            button.Click += (_, _) => { Select(button); cropView.Aspect = aspect.Ratio; };
            DockPanel.SetDock(button, Dock.Left);
            controls.Children.Add(button);
            choices.Add(button);
        }
        Select(choices[0]);
        DockPanel.SetDock(sizeLabel, Dock.Left);
        controls.Children.Add(sizeLabel);

        var crop = PillButton.Create(L("Crop"), PillButton.Style.Primary, 32, Glyph.Crop);
        crop.IsDefault = true;
        crop.Click += (_, _) => window.DialogResult = true;
        DockPanel.SetDock(crop, Dock.Right);
        controls.Children.Add(crop);
        var cancel = PillButton.Create(L("Cancel"), PillButton.Style.Secondary, 32);
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(cancel, Dock.Right);
        controls.Children.Add(cancel);

        cropView.Changed += UpdateSize;
        window.Content = root;
        window.Loaded += (_, _) => { UpdateSize(); window.Activate(); };
        bool ok = window.ShowDialog() == true;
        return ok ? cropView.CropRectInPixels : null;
    }

    /// <summary>Capsule buttons in FileFlipper's colours: orange for the main action, translucent peach otherwise.</summary>
    private static class PillButton
    {
        public enum Style { Primary, Secondary, Choice }

        private static readonly DependencyProperty OnProperty =
            DependencyProperty.RegisterAttached("On", typeof(bool), typeof(PillButton), new PropertyMetadata(false, (d, _) => Refresh((Button)d)));
        private static readonly DependencyProperty KindProperty =
            DependencyProperty.RegisterAttached("Kind", typeof(Style), typeof(PillButton), new PropertyMetadata(Style.Secondary));

        public static void SetOn(Button button, bool on) => button.SetValue(OnProperty, on);

        public static Button Create(string title, Style style, double height, string? glyph = null)
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            if (glyph != null)
            {
                content.Children.Add(new TextBlock
                {
                    Text = glyph, FontFamily = Palette.IconFont, FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 1, 7, 0),
                });
            }
            content.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = Palette.TextFont,
                FontSize = style == Style.Choice ? 12.5 : 13.5,
                FontWeight = style == Style.Primary ? FontWeights.Bold : FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(height / 2));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetValue(Border.PaddingProperty, new Thickness(style == Style.Choice ? 12 : 18, 0, style == Style.Choice ? 12 : 18, 0));
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            border.AppendChild(presenter);

            var button = new Button
            {
                Content = content,
                Height = height,
                Cursor = Cursors.Hand,
                Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
                FocusVisualStyle = null,
            };
            button.SetValue(KindProperty, style);
            button.MouseEnter += (_, _) => Refresh(button);
            button.MouseLeave += (_, _) => Refresh(button);
            button.PreviewMouseLeftButtonDown += (_, _) => Refresh(button, pressed: true);
            button.PreviewMouseLeftButtonUp += (_, _) => Refresh(button);
            Refresh(button);
            return button;
        }

        private static void Refresh(Button button) => Refresh(button, pressed: false);

        private static void Refresh(Button button, bool pressed)
        {
            var style = (Style)button.GetValue(KindProperty);
            bool on = (bool)button.GetValue(OnProperty);
            bool hovering = button.IsMouseOver;
            (Color fill, double opacity, Color text) = style switch
            {
                Style.Primary => (Palette.Orange, pressed ? 0.75 : hovering ? 0.9 : 1, Colors.White),
                Style.Secondary => (Palette.Peach, pressed ? 0.95 : hovering ? 0.8 : 0.6, Palette.Brown),
                _ when on => (Palette.Orange, 0.88, Colors.White),
                _ => (Palette.Peach, pressed ? 0.85 : hovering ? 0.7 : 0.45, Palette.Brown),
            };
            button.Background = Palette.Brush(fill, opacity);
            button.Foreground = Palette.Brush(text);
        }
    }

    /// <summary>
    /// Draws the image with a draggable selection: drag inside to move it, drag a corner to resize it,
    /// drag anywhere else on the picture to start a new selection.
    /// </summary>
    private sealed class CropView : FrameworkElement
    {
        private readonly BitmapSource image;
        private readonly double padding;
        private Rect selection;
        private double? aspect;
        private enum DragKind { None, Move, Resize }
        private DragKind drag;
        private Point dragStart, anchor;
        private Rect original;
        private const double HandleSize = 10;

        public event Action? Changed;

        public CropView(BitmapSource image, double padding)
        {
            this.image = image;
            this.padding = padding;
            Cursor = Cursors.Cross;
            Loaded += (_, _) => { selection = ImageRect; InvalidateVisual(); Changed?.Invoke(); };
        }

        public double? Aspect
        {
            get => aspect;
            set { aspect = value; FitSelectionToAspect(); }
        }

        /// <summary>Where the picture is drawn inside the view (aspect-fit).</summary>
        private Rect ImageRect
        {
            get
            {
                double width = ActualWidth > 0 ? ActualWidth : Width, height = ActualHeight > 0 ? ActualHeight : Height;
                var area = new Rect(padding, padding, Math.Max(1, width - padding * 2), Math.Max(1, height - padding * 2));
                double scale = Math.Min(area.Width / image.PixelWidth, area.Height / image.PixelHeight);
                double w = image.PixelWidth * scale, h = image.PixelHeight * scale;
                return new Rect(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
            }
        }

        public Int32Rect CropRectInPixels
        {
            get
            {
                var frame = ImageRect;
                if (selection.IsEmpty || selection.Width <= 0) return new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight);
                double scale = image.PixelWidth / frame.Width;
                int x = (int)Math.Round((selection.X - frame.X) * scale);
                int y = (int)Math.Round((selection.Y - frame.Y) * scale);
                int w = (int)Math.Round(selection.Width * scale);
                int h = (int)Math.Round(selection.Height * scale);
                x = Math.Clamp(x, 0, image.PixelWidth - 1);
                y = Math.Clamp(y, 0, image.PixelHeight - 1);
                w = Math.Clamp(w, 1, image.PixelWidth - x);
                h = Math.Clamp(h, 1, image.PixelHeight - y);
                return new Int32Rect(x, y, w, h);
            }
        }

        protected override void OnRender(DrawingContext context)
        {
            var canvas = Palette.Brush(Palette.Canvas);
            context.DrawRoundedRectangle(canvas, null, new Rect(0, 0, ActualWidth, ActualHeight), 14, 14);
            var frame = ImageRect;
            context.DrawImage(image, frame);

            // Dim everything outside the selection.
            var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(frame), new RectangleGeometry(selection));
            context.DrawGeometry(Palette.Brush(Palette.Canvas, 0.7), null, shade);

            // Rule-of-thirds guides.
            var guide = new Pen(Palette.Brush(Colors.White, 0.35), 1);
            for (int step = 1; step <= 2; step++)
            {
                double x = selection.X + selection.Width * step / 3, y = selection.Y + selection.Height * step / 3;
                context.DrawLine(guide, new Point(x, selection.Top), new Point(x, selection.Bottom));
                context.DrawLine(guide, new Point(selection.Left, y), new Point(selection.Right, y));
            }
            context.DrawRectangle(null, new Pen(Palette.Brush(Colors.White, 0.85), 1), selection);

            // L-shaped corner grips, drawn just inside the selection.
            double arm = Math.Min(20, Math.Min(selection.Width / 3, selection.Height / 3));
            var grip = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            foreach (var corner in Corners)
            {
                double dx = corner.X == selection.Left ? 1 : -1, dy = corner.Y == selection.Top ? 1 : -1;
                var tip = new Point(corner.X + dx * 1.5, corner.Y + dy * 1.5);
                var figure = new PathFigure(new Point(tip.X + dx * arm, tip.Y), new[]
                {
                    new LineSegment(tip, true), new LineSegment(new Point(tip.X, tip.Y + dy * arm), true),
                }, false);
                context.DrawGeometry(null, grip, new PathGeometry(new[] { figure }));
            }
        }

        private Point[] Corners => new[] { selection.TopLeft, selection.TopRight, selection.BottomLeft, selection.BottomRight };

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var point = e.GetPosition(this);
            var frame = ImageRect;
            foreach (var corner in Corners)
            {
                if ((corner - point).Length <= HandleSize * 1.5)
                {
                    // Resize from the opposite corner.
                    anchor = new Point(corner.X == selection.Left ? selection.Right : selection.Left,
                                       corner.Y == selection.Top ? selection.Bottom : selection.Top);
                    drag = DragKind.Resize;
                    CaptureMouse();
                    return;
                }
            }
            if (selection.Contains(point)) { drag = DragKind.Move; dragStart = point; original = selection; }
            else if (frame.Contains(point)) { drag = DragKind.Resize; anchor = point; }
            else drag = DragKind.None;
            if (drag != DragKind.None) CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (drag == DragKind.None) return;
            var point = e.GetPosition(this);
            var frame = ImageRect;
            if (drag == DragKind.Move)
            {
                var moved = original;
                moved.Offset(point.X - dragStart.X, point.Y - dragStart.Y);
                moved.X = Math.Min(Math.Max(moved.X, frame.Left), frame.Right - moved.Width);
                moved.Y = Math.Min(Math.Max(moved.Y, frame.Top), frame.Bottom - moved.Height);
                selection = moved;
            }
            else
            {
                var clamped = new Point(Math.Min(Math.Max(point.X, frame.Left), frame.Right), Math.Min(Math.Max(point.Y, frame.Top), frame.Bottom));
                selection = RectFrom(anchor, clamped);
            }
            InvalidateVisual();
            Changed?.Invoke();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            // A click without a real drag would leave a tiny selection; fall back to the whole picture.
            if (selection.Width < 8 || selection.Height < 8)
            {
                selection = ImageRect;
                FitSelectionToAspect();
            }
            drag = DragKind.None;
            InvalidateVisual();
            Changed?.Invoke();
        }

        private Rect RectFrom(Point start, Point point)
        {
            double dx = point.X - start.X, dy = point.Y - start.Y;
            double width = Math.Abs(dx), height = Math.Abs(dy);
            if (aspect is double ratio)
            {
                // Shrink whichever side is too long; the point is already inside the picture, so this still fits.
                if (height > 0 && width / height > ratio) width = height * ratio;
                else height = width / ratio;
            }
            return new Rect(dx >= 0 ? start.X : start.X - width, dy >= 0 ? start.Y : start.Y - height, width, height);
        }

        /// <summary>The largest selection with the chosen aspect ratio, centred on the picture.</summary>
        private void FitSelectionToAspect()
        {
            var frame = ImageRect;
            if (aspect is not double ratio)
            {
                selection = frame;
            }
            else
            {
                double width = frame.Width, height = width / ratio;
                if (height > frame.Height) { height = frame.Height; width = height * ratio; }
                selection = new Rect(frame.X + (frame.Width - width) / 2, frame.Y + (frame.Height - height) / 2, width, height);
            }
            InvalidateVisual();
            Changed?.Invoke();
        }
    }
}
