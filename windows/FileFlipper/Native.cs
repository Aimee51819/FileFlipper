using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileFlipper;

/// <summary>The few Win32 calls the picker and toast need.</summary>
internal static class Native
{
    public const int VK_LBUTTON = 0x01;
    public const int VK_RBUTTON = 0x02;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_ESCAPE = 0x1B;

    private const int SM_SWAPBUTTON = 23;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    public static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>The primary button, which is the right one when the buttons are swapped.</summary>
    public static bool PrimaryButtonDown => IsDown(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? VK_RBUTTON : VK_LBUTTON);

    public static (int X, int Y) DragThreshold => (Math.Max(4, GetSystemMetrics(SM_CXDRAG)), Math.Max(4, GetSystemMetrics(SM_CYDRAG)));

    public static System.Drawing.Point CursorPixels
    {
        get
        {
            GetCursorPos(out var point);
            return new System.Drawing.Point(point.X, point.Y);
        }
    }

    /// <summary>Screen pixels → WPF device-independent units. The app is system-DPI aware, so one factor fits all.</summary>
    public static double PixelsPerDip
    {
        get
        {
            try { return GetDpiForSystem() / 96.0; }
            catch (EntryPointNotFoundException) { return 1; }
        }
    }

    public static Point ToDips(System.Drawing.Point pixels) => new(pixels.X / PixelsPerDip, pixels.Y / PixelsPerDip);

    public static Rect ToDips(System.Drawing.Rectangle pixels)
    {
        var scale = PixelsPerDip;
        return new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale);
    }

    /// <summary>The area of the screen under the cursor that windows may use (without the taskbar), in DIPs.</summary>
    public static Rect WorkAreaAtCursor => ToDips(System.Windows.Forms.Screen.FromPoint(CursorPixels).WorkingArea);

    public static bool ForegroundIsThisProcess
    {
        get
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var processId);
            return processId == (uint)Environment.ProcessId;
        }
    }

    /// <summary>Keeps a window from taking focus or showing in Alt+Tab; optionally lets clicks pass through.</summary>
    public static void MakeToolWindow(Window window, bool clickThrough)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (clickThrough) style |= WS_EX_TRANSPARENT;
        SetWindowLong(handle, GWL_EXSTYLE, style);
    }

    /// <summary>Lets a tool window take focus again (needed when it should receive keys).</summary>
    public static void AllowActivation(Window window, bool allow)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        style = allow ? style & ~WS_EX_NOACTIVATE : style | WS_EX_NOACTIVATE;
        SetWindowLong(handle, GWL_EXSTYLE, style);
    }
}
