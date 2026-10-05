using System;
using System.Windows.Threading;

namespace FileFlipper;

/// <summary>
/// Watches for drags happening anywhere on the system (typically from File Explorer) and reports
/// when the user holds Shift (formats) or Ctrl+Shift (tools) mid-drag.
///
/// It polls the mouse and keyboard state instead of installing hooks, so it needs no special
/// permissions and never slows down input:
///  - the primary mouse button being held, and the pointer having moved past the drag threshold,
///    means something is being dragged;
///  - which files are being dragged is learned when the drag enters the picker (see PickerWindow).
/// </summary>
public sealed class DragMonitor
{
    public bool IsEnabled { get; set; } = true;

    /// <summary>A drag with Shift held has started: the picker should get ready under the pointer.</summary>
    public event Action<bool>? Started;
    /// <summary>The user switched between Shift and Ctrl+Shift while the picker is up.</summary>
    public event Action<bool>? ModeChanged;
    /// <summary>The mouse button was released after the picker was armed.</summary>
    public event Action? Ended;

    private readonly DispatcherTimer timer;
    private bool mouseWasDown;
    private System.Drawing.Point downPoint;
    private bool armed;
    private bool toolsMode;
    private bool ignoreThisPress;

    public DragMonitor()
    {
        timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        timer.Tick += (_, _) => Tick();
    }

    public void Start() => timer.Start();
    public void Stop() => timer.Stop();

    private void Tick()
    {
        bool mouseDown = Native.PrimaryButtonDown;
        if (!mouseDown)
        {
            if (armed)
            {
                armed = false;
                Ended?.Invoke();
            }
            mouseWasDown = false;
            return;
        }
        if (!mouseWasDown)
        {
            mouseWasDown = true;
            downPoint = Native.CursorPixels;
            // Drags inside FileFlipper's own windows (the crop window) are not file drags.
            ignoreThisPress = Native.ForegroundIsThisProcess;
        }
        if (!IsEnabled || ignoreThisPress) return;

        bool shift = Native.IsDown(Native.VK_SHIFT);
        bool control = Native.IsDown(Native.VK_CONTROL);

        if (!armed)
        {
            if (!shift) return;
            var pointer = Native.CursorPixels;
            var (thresholdX, thresholdY) = Native.DragThreshold;
            bool moved = Math.Abs(pointer.X - downPoint.X) > thresholdX || Math.Abs(pointer.Y - downPoint.Y) > thresholdY;
            if (!moved) return;
            armed = true;
            toolsMode = control;
            Started?.Invoke(control);
        }
        else if (shift && control != toolsMode)
        {
            toolsMode = control;
            ModeChanged?.Invoke(control);
        }
    }
}
