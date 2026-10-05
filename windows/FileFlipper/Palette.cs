using System.Windows.Media;

namespace FileFlipper;

/// <summary>FileFlipper's colours, shared by the picker, the toast and the crop window (same as the Mac app).</summary>
public static class Palette
{
    public static readonly Color Peach = Color.FromRgb(255, 224, 194);
    public static readonly Color Orange = Color.FromRgb(250, 102, 31);
    public static readonly Color Brown = Color.FromRgb(64, 38, 26);
    public static readonly Color Cream = Color.FromRgb(255, 247, 240);
    public static readonly Color Canvas = Color.FromRgb(43, 33, 28);
    /// <summary>Picker bubbles.</summary>
    public static readonly Color Bubble = Color.FromArgb(250, 255, 245, 235);
    public static readonly Color BubbleBorder = Color.FromRgb(240, 204, 173);
    public static readonly Color Icon = Color.FromRgb(201, 84, 28);

    public static SolidColorBrush Brush(Color color, double opacity = 1)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>Icons come from the Windows icon font (Segoe Fluent Icons on Windows 11, MDL2 Assets on 10).</summary>
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public static readonly FontFamily TextFont = new("Segoe UI, Microsoft YaHei UI");
}

/// <summary>Glyphs from the Windows icon font, standing in for the Mac app's SF Symbols.</summary>
public static class Glyph
{
    public const string Photo = "";
    public const string Document = "";
    public const string Pdf = "";
    public const string Markdown = "";
    public const string Code = "";
    public const string Gif = "";
    public const string Video = "";
    public const string Audio = "";
    public const string Crop = "";
    public const string Compress = "";
    public const string Clean = "";
    public const string Rotate = "";
    public const string Flip = "";
    public const string Grayscale = "";
    public const string Cutout = "";
    public const string Merge = "";
    public const string Split = "";
    public const string Text = "";
    public const string Mute = "";
    public const string Camera = "";
    public const string Mono = "";
    public const string Plain = "";

    public const string Working = "";
    public const string Done = "";
    public const string Error = "";
    public const string Cancel = "";
    public const string Info = "";
}
