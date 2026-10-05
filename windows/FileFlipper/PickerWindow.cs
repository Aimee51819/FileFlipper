using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static FileFlipper.Loc;

namespace FileFlipper;

/// <summary>
/// The transparent floating window that hosts the bubble arc.
///
/// Drag mode: while a file is being dragged with Shift held, the window sits invisibly under the
/// pointer. When the drag enters it, Windows hands over the dragged files, the bubbles fade in, and
/// the user drops on one. Dragging out of the window hides it so ordinary Shift-drags still work.
///
/// Click mode (from the "Send to" menu): the bubbles appear at the pointer and are clicked instead.
/// </summary>
public sealed class PickerWindow : Window
{
    public const double Size = 420;
    /// <summary>How much room the arc needs above (or below) the pointer.</summary>
    private const double ArcReach = 200;

    public event Action<PickerItem, IReadOnlyList<string>>? Picked;

    private readonly BubbleArc arc = new();
    private IReadOnlyList<string> files = Array.Empty<string>();
    private bool tools;
    private bool armed;
    private bool clickMode;
    private int hideToken;

    public PickerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Almost transparent rather than fully: fully transparent pixels can't be drop targets.
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255));
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Size;
        Height = Size;
        AllowDrop = true;
        Content = arc;
        arc.Opacity = 0;

        SourceInitialized += (_, _) => Native.MakeToolWindow(this, clickThrough: false);
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += OnDragLeave;
        Drop += OnDrop;
        MouseMove += (_, e) => { if (clickMode) arc.Hover(e.GetPosition(arc)); };
        MouseLeftButtonUp += OnClick;
        MouseRightButtonUp += (_, _) => { if (clickMode) Dismiss(); };
        KeyDown += OnKey;
        KeyUp += OnKey;
        Deactivated += (_, _) => { if (clickMode) Dismiss(); };
    }

    public bool IsShowing => IsVisible && (armed || clickMode);

    // MARK: Drag mode

    /// <summary>Places the (still invisible) window under the pointer, ready to receive the drag.</summary>
    public void Arm(bool toolsMode)
    {
        hideToken++;
        clickMode = false;
        armed = true;
        tools = toolsMode;
        files = Array.Empty<string>();
        arc.Configure(new List<PickerItem>(), files, tools);
        arc.Opacity = 0;
        PlaceAtPointer();
        if (!IsVisible) Show();
    }

    public void SetToolsMode(bool toolsMode)
    {
        if (tools == toolsMode) return;
        tools = toolsMode;
        if (files.Count > 0) arc.Configure(Catalog.Items(files, tools), files, tools);
    }

    /// <summary>The mouse button went up. Hide after a moment so a drop landing at the same instant still arrives.</summary>
    public void EndDrag()
    {
        if (clickMode) return;
        armed = false;
        HideAfter(TimeSpan.FromMilliseconds(350));
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (clickMode) return;
        var dropped = (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>()).Where(File.Exists).ToList();
        if (dropped.Count == 0)
        {
            // Not a file drag (text, a folder…): get out of the way.
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            Dismiss();
            return;
        }
        if (files.Count == 0 || !files.SequenceEqual(dropped))
        {
            files = dropped;
            arc.Configure(Catalog.Items(files, tools), files, tools);
        }
        FadeIn();
        OnDragOver(sender, e);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        arc.Hover(e.GetPosition(arc));
        e.Effects = arc.Hovered != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        arc.ClearHover();
        // Left the window entirely: let the drag carry on to whatever is underneath.
        var pointer = Native.ToDips(Native.CursorPixels);
        if (!new Rect(Left, Top, Width, Height).Contains(pointer)) Dismiss();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        var item = arc.Hovered;
        var dropped = (e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>()).Where(File.Exists).ToList();
        var targets = dropped.Count > 0 ? dropped : files.ToList();
        e.Effects = item != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Dismiss();
        if (item != null)
        {
            // Let the drag session finish before doing any work.
            Dispatcher.BeginInvoke(() => Picked?.Invoke(item, targets), DispatcherPriority.Background);
        }
    }

    // MARK: Click mode

    /// <summary>Shows the bubbles at the pointer for files sent from Explorer's "Send to" menu.</summary>
    public void ShowForClick(IReadOnlyList<string> paths)
    {
        hideToken++;
        armed = false;
        clickMode = true;
        files = paths;
        tools = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        arc.Configure(Catalog.Items(files, tools), files, tools);
        arc.ShowsClickHint = true;
        PlaceAtPointer();
        Native.AllowActivation(this, true);
        Show();
        Activate();
        Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        Focus();
        FadeIn();
        arc.Hover(Mouse.GetPosition(arc));
    }

    private void OnClick(object sender, MouseButtonEventArgs e)
    {
        if (!clickMode) return;
        arc.Hover(e.GetPosition(arc));
        var item = arc.Hovered;
        var targets = files;
        Dismiss();
        if (item != null) Dispatcher.BeginInvoke(() => Picked?.Invoke(item, targets), DispatcherPriority.Background);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!clickMode) return;
        if (e.Key == Key.Escape) { Dismiss(); return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.Tab)
        {
            bool next = e.Key == Key.Tab ? (e.IsDown ? !tools : tools) : e.IsDown;
            if (next != tools)
            {
                tools = next;
                arc.Configure(Catalog.Items(files, tools), files, tools);
                arc.Hover(Mouse.GetPosition(arc));
            }
            e.Handled = true;
        }
    }

    // MARK: Showing and hiding

    private void PlaceAtPointer()
    {
        var pointer = Native.ToDips(Native.CursorPixels);
        var visible = Native.WorkAreaAtCursor;
        // Near the top of the screen, open the arc downward instead.
        bool upward = pointer.Y - ArcReach >= visible.Top;
        double left = pointer.X - Size / 2, top = pointer.Y - Size / 2;
        // Near the sides, slide back on screen. Only the half with the arc matters.
        left = Math.Min(Math.Max(left, visible.Left - 12), visible.Right + 12 - Size);
        top = upward ? Math.Max(top, visible.Top - 12) : Math.Min(top, visible.Bottom + 12 - Size);
        Left = left;
        Top = top;
        // The pointer may not be at the centre after sliding; the arc is drawn around it.
        arc.Center = new Point(pointer.X - left, pointer.Y - top);
        arc.OpensUpward = upward;
    }

    private void FadeIn()
    {
        arc.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    public void Dismiss()
    {
        armed = false;
        if (clickMode)
        {
            clickMode = false;
            arc.ShowsClickHint = false;
            Native.AllowActivation(this, false);
        }
        HideAfter(TimeSpan.Zero);
    }

    private void HideAfter(TimeSpan delay)
    {
        int token = ++hideToken;
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (token != hideToken) return;
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120));
            fade.Completed += (_, _) =>
            {
                if (token != hideToken) return;
                arc.ClearHover();
                Hide();
            };
            arc.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }
}

/// <summary>
/// Round icon bubbles on an arc above the pointer (below it near the top of the screen). Every bubble is
/// the same distance from the pointer and owns the whole direction it sits in, so a short flick toward a
/// bubble is enough to land on it.
/// </summary>
public sealed class BubbleArc : FrameworkElement
{
    private List<PickerItem> items = new();
    private IReadOnlyList<string> files = Array.Empty<string>();
    private bool isTools;
    private int? hovered;
    private bool opensUpward = true;
    private bool showsClickHint;

    public Point Center { get; set; } = new(PickerWindow.Size / 2, PickerWindow.Size / 2);

    public bool OpensUpward
    {
        get => opensUpward;
        set { opensUpward = value; InvalidateVisual(); }
    }

    public bool ShowsClickHint
    {
        get => showsClickHint;
        set { showsClickHint = value; InvalidateVisual(); }
    }

    public PickerItem? Hovered => hovered is int index && index < items.Count ? items[index] : null;

    // Geometry (DIPs).
    private const double BaseRadius = 150;
    private const double MaxSpan = 200;          // degrees the arc may cover
    private const double CancelRadius = 70;      // drops closer to the pointer than this do nothing
    private const double HoverScale = 1.14;
    private double Diameter => items.Count > 7 ? 56 : 60;

    public void Configure(List<PickerItem> newItems, IReadOnlyList<string> newFiles, bool tools)
    {
        items = newItems;
        files = newFiles;
        isTools = tools;
        hovered = null;
        InvalidateVisual();
    }

    public void Hover(Point point)
    {
        var index = ItemIndex(point);
        if (index == hovered) return;
        hovered = index;
        InvalidateVisual();
    }

    public void ClearHover()
    {
        if (hovered == null) return;
        hovered = null;
        InvalidateVisual();
    }

    // MARK: Layout

    /// <summary>Radius of the arc and the angle between neighbouring bubbles (degrees).</summary>
    private (double Radius, double Step) Layout()
    {
        int count = items.Count;
        if (count <= 1) return (BaseRadius, 0);
        double spacing = Diameter + 8;
        double radius = BaseRadius;
        // Tightest step that keeps bubbles apart, but spread a few bubbles out a little.
        double tightest = 2 * Math.Asin(spacing / (2 * radius)) * 180 / Math.PI;
        double step = Math.Max(tightest, Math.Min(48, 150.0 / (count - 1)));
        if (step * (count - 1) > MaxSpan)
        {
            step = MaxSpan / (count - 1);
            radius = spacing / (2 * Math.Sin(step / 2 * Math.PI / 180));
        }
        return (radius, step);
    }

    /// <summary>Direction of bubble <paramref name="index"/> in degrees (maths convention, y up), left to right.</summary>
    private double Angle(int index)
    {
        var (_, step) = Layout();
        double span = step * (items.Count - 1);
        return opensUpward ? 90 + span / 2 - index * step : -90 - span / 2 + index * step;
    }

    private Point Position(int index)
    {
        double radians = Angle(index) * Math.PI / 180;
        double radius = Layout().Radius;
        return new Point(Center.X + Math.Cos(radians) * radius, Center.Y - Math.Sin(radians) * radius);
    }

    /// <summary>Whichever bubble lies in the pointer's direction, once it has moved far enough.</summary>
    private int? ItemIndex(Point point)
    {
        if (items.Count == 0) return null;
        double dx = point.X - Center.X, dy = Center.Y - point.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        var (radius, step) = Layout();
        if (distance < CancelRadius || distance > radius + Diameter * 1.5) return null;
        double direction = Math.Atan2(dy, dx) * 180 / Math.PI;
        double Difference(int index)
        {
            double delta = (direction - Angle(index)) % 360;
            if (delta > 180) delta -= 360;
            if (delta < -180) delta += 360;
            return Math.Abs(delta);
        }
        int nearest = Enumerable.Range(0, items.Count).MinBy(Difference);
        double tolerance = Math.Max(step / 2, 30);
        return Difference(nearest) <= tolerance ? nearest : null;
    }

    // MARK: Drawing

    protected override void OnRender(DrawingContext context)
    {
        for (int index = 0; index < items.Count; index++)
        {
            if (index != hovered) DrawBubble(context, index);
        }
        if (hovered is int h && h < items.Count) DrawBubble(context, h);
        DrawCaption(context);
    }

    /// <summary>A soft shadow under a circle: a radial gradient that fades out over <paramref name="blur"/>.</summary>
    private static void DrawShadow(DrawingContext context, Rect rect, double radius, double opacity, double blur)
    {
        var dark = Color.FromArgb((byte)(255 * opacity), 0, 0, 0);
        var brush = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5) };
        brush.GradientStops.Add(new GradientStop(dark, 0));
        brush.GradientStops.Add(new GradientStop(dark, 0.85 * radius / (radius + blur)));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        brush.Freeze();
        var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2 + 3);
        context.DrawEllipse(brush, null, center, radius + blur, radius + blur);
    }

    private void DrawBubble(DrawingContext context, int index)
    {
        var item = items[index];
        bool isHovered = hovered == index;
        double size = Diameter * (isHovered ? HoverScale : 1);
        var middle = Position(index);
        var rect = new Rect(middle.X - size / 2, middle.Y - size / 2, size, size);

        DrawShadow(context, rect, size / 2, isHovered ? 0.28 : 0.16, isHovered ? 12 : 8);
        var fill = Palette.Brush(isHovered ? Palette.Orange : Palette.Bubble);
        var pen = isHovered ? null : new Pen(Palette.Brush(Palette.BubbleBorder), 1);
        context.DrawEllipse(fill, pen, middle, size / 2, size / 2);

        var iconColor = Palette.Brush(isHovered ? Colors.White : Palette.Icon);
        var textColor = Palette.Brush(isHovered ? Colors.White : Palette.Brown);
        double scale = isHovered ? HoverScale : 1;
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Label: shrink the font for longer words so it always fits inside the bubble.
        double fontSize = 11 * scale;
        FormattedText Title() => new(item.Title, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Palette.TextFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fontSize, textColor, pixelsPerDip);
        var title = Title();
        while (title.Width > size - 12 && fontSize > 7.5)
        {
            fontSize -= 0.5;
            title = Title();
        }
        var glyph = new FormattedText(item.Glyph ?? Catalog.FormatGlyph(item.Title), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Palette.IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 18 * scale, iconColor, pixelsPerDip);
        double total = glyph.Height + 1 + title.Height;
        double top = middle.Y - total / 2;
        context.DrawText(glyph, new Point(middle.X - glyph.Width / 2, top));
        context.DrawText(title, new Point(middle.X - title.Width / 2, top + glyph.Height + 1));
    }

    /// <summary>A small pill between the pointer and the arc: the file name, or what the hovered bubble does.</summary>
    private void DrawCaption(DrawingContext context)
    {
        string text;
        string accent = "";
        if (hovered is int h && h < items.Count)
        {
            text = items[h].Detail ?? L("Save as %@", items[h].Title);
        }
        else if (items.Count == 0)
        {
            if (files.Count == 0) return;
            text = isTools ? L("No tools for this file type") : L("No formats for this file type");
        }
        else if (files.Count > 0)
        {
            text = files.Count == 1 ? Path.GetFileName(files[0]) : L("%@ files", files.Count);
            accent = isTools ? L("TOOLS") : L("CONVERT");
        }
        else
        {
            return;
        }

        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var label = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Palette.TextFont, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal), 12, Palette.Brush(Palette.Brown), pixelsPerDip)
        {
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        FormattedText? accentText = accent.Length == 0 ? null : new FormattedText(accent, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Palette.TextFont, FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 9.5, Palette.Brush(Palette.Orange), pixelsPerDip);
        double accentWidth = accentText == null ? 0 : accentText.Width + 8;
        // Narrow enough to fit between the two outermost bubbles.
        double maxLabel = 200 - 24 - accentWidth;
        label.MaxTextWidth = Math.Max(20, maxLabel);
        double width = Math.Min(label.WidthIncludingTrailingWhitespace, maxLabel) + accentWidth + 24;
        const double height = 26;
        double offset = items.Count == 0 ? 0 : 50;
        double y = Center.Y + (opensUpward ? -offset : offset) - height / 2;
        var pill = new Rect(Center.X - width / 2, y, width, height);

        context.DrawRoundedRectangle(Palette.Brush(Colors.Black, 0.06), null, new Rect(pill.X, pill.Y + 2, pill.Width, pill.Height), height / 2, height / 2);
        context.DrawRoundedRectangle(Palette.Brush(Palette.Cream), new Pen(Palette.Brush(Palette.BubbleBorder, 0.6), 0.75), pill, height / 2, height / 2);
        context.DrawText(label, new Point(pill.X + 12, pill.Y + (height - label.Height) / 2));
        if (accentText != null)
            context.DrawText(accentText, new Point(pill.Right - 12 - accentText.Width, pill.Y + (height - accentText.Height) / 2 + 0.5));

        if (showsClickHint && items.Count > 0)
        {
            var hint = new FormattedText(L("Click a bubble · Ctrl for tools · Esc to close"), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(Palette.TextFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10.5, Palette.Brush(Palette.Brown, 0.75), pixelsPerDip);
            double hintY = opensUpward ? Center.Y + 14 : Center.Y - 14 - hint.Height;
            var hintRect = new Rect(Center.X - hint.Width / 2 - 8, hintY - 2, hint.Width + 16, hint.Height + 4);
            context.DrawRoundedRectangle(Palette.Brush(Palette.Cream, 0.92), null, hintRect, 8, 8);
            context.DrawText(hint, new Point(Center.X - hint.Width / 2, hintY));
        }
    }
}
