using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using static FileFlipper.Loc;

namespace FileFlipper;

/// <summary>The notification-area icon, its menu, and running the picked actions.</summary>
public sealed class TrayApp
{
    public const string Website = "https://aimee51819.github.io/FileFlipper/";

    private readonly Application application;
    private readonly DragMonitor monitor = new();
    private readonly PickerWindow picker = new();
    private readonly ToastWindow toast = new();
    private Forms.NotifyIcon? trayIcon;

    public TrayApp(Application application)
    {
        this.application = application;
    }

    public void Start(IReadOnlyList<string> files, bool background)
    {
        SetUpTrayIcon();

        monitor.IsEnabled = Settings.Enabled;
        monitor.Started += tools => picker.Arm(tools);
        monitor.ModeChanged += tools => picker.SetToolsMode(tools);
        monitor.Ended += () => picker.EndDrag();
        picker.Picked += Run;
        monitor.Start();

        if (!Settings.ShownWelcome)
        {
            Settings.ShownWelcome = true;
            try { if (!Settings.InSendToMenu) Settings.InSendToMenu = true; } catch { }
            application.Dispatcher.BeginInvoke(ShowHelp, DispatcherPriority.ApplicationIdle);
        }
        else if (!background && files.Count == 0)
        {
            toast.Show(L("FileFlipper is running — look for its icon in the notification area"), Glyph.Info, 4);
        }
        if (files.Count > 0) ShowPicker(files);
    }

    /// <summary>Files sent from Explorer's "Send to" menu (or a second launch with files).</summary>
    public void ShowPicker(IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0)
        {
            toast.Show(L("Send files, not folders, to FileFlipper"), Glyph.Info, 3);
            return;
        }
        picker.ShowForClick(files);
    }

    public void ShowAlreadyRunning()
    {
        toast.Show(L("FileFlipper is already running — look for its icon in the notification area"), Glyph.Info, 4);
    }

    // MARK: Running actions

    private void Run(PickerItem item, IReadOnlyList<string> files)
    {
        var what = files.Count == 1 ? Path.GetFileName(files[0]) : L("%@ files", files.Count);
        toast.Show(L("%@: %@…", item.Title, what), Glyph.Working, null);
        Task.Run(() => item.Action(files)).ContinueWith(task =>
        {
            application.Dispatcher.Invoke(() => Finish(task, item));
        });
    }

    private void Finish(Task<List<string>> task, PickerItem item)
    {
        if (task.Exception?.InnerException is { } error)
        {
            if (error is ConversionException { IsCancelled: true })
            {
                toast.Show(L("Cancelled"), Glyph.Cancel, 1.2);
                return;
            }
            var message = error is ConversionException ? error.Message : L("%@ failed: %@", item.Title, error.Message);
            toast.Show(message, Glyph.Error, 5);
            System.Media.SystemSounds.Exclamation.Play();
            return;
        }
        var outputs = task.Result;
        if (outputs.Count == 1) toast.Show(L("Saved %@", Path.GetFileName(outputs[0])), Glyph.Done);
        else if (outputs.Count > 1) toast.Show(L("Saved %@ files", outputs.Count), Glyph.Done);
        else toast.Show(L("%@: nothing to do", item.Title), Glyph.Info);
    }

    // MARK: Notification-area icon

    private void SetUpTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Opening += (_, _) => BuildMenu(menu);
        BuildMenu(menu);
        trayIcon = new Forms.NotifyIcon
        {
            Text = "FileFlipper",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        trayIcon.MouseUp += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            // Left click shows the same menu (NotifyIcon only does that for right clicks by itself).
            typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(trayIcon, null);
        };
        application.Exit += (_, _) =>
        {
            if (trayIcon != null) trayIcon.Visible = false;
            trayIcon?.Dispose();
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var size = Forms.SystemInformation.SmallIconSize;
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/FileFlipper.ico"))?.Stream;
            if (stream != null) return new System.Drawing.Icon(stream, size);
        }
        catch { }
        return System.Drawing.Icon.ExtractAssociatedIcon(Settings.ExecutablePath) ?? System.Drawing.SystemIcons.Application;
    }

    private void BuildMenu(Forms.ContextMenuStrip menu)
    {
        menu.Items.Clear();
        Forms.ToolStripMenuItem Item(string text, Action action, bool? check = null)
        {
            var entry = new Forms.ToolStripMenuItem(text);
            entry.Click += (_, _) => action();
            if (check is bool value) entry.Checked = value;
            menu.Items.Add(entry);
            return entry;
        }

        Item(L("Enabled"), ToggleEnabled, monitor.IsEnabled);
        Item(L("Start with Windows"), ToggleLaunchAtLogin, SafeGet(() => Settings.LaunchAtLogin));
        Item(L("Show in “Send to” Menu"), ToggleSendTo, SafeGet(() => Settings.InSendToMenu));
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item(L("How to Use…"), ShowHelp);
        Item(L("About FileFlipper"), ShowAbout);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Item(L("Quit FileFlipper"), () => application.Shutdown());
    }

    private static bool SafeGet(Func<bool> getter)
    {
        try { return getter(); } catch { return false; }
    }

    private void ToggleEnabled()
    {
        monitor.IsEnabled = !monitor.IsEnabled;
        Settings.Enabled = monitor.IsEnabled;
    }

    private void ToggleLaunchAtLogin()
    {
        try { Settings.LaunchAtLogin = !Settings.LaunchAtLogin; }
        catch (Exception error) { toast.Show(L("Start with Windows: %@", error.Message), Glyph.Error, 4); }
    }

    private void ToggleSendTo()
    {
        try { Settings.InSendToMenu = !Settings.InSendToMenu; }
        catch (Exception error) { toast.Show(error.Message, Glyph.Error, 4); }
    }

    private void ShowAbout()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        InfoWindow.Show(L("About FileFlipper"), new[]
        {
            L("Version %@", $"{version?.Major}.{version?.Minor}.{version?.Build}"),
            L("Convert files right in File Explorer.\nOpen source under the MIT License."),
        }, new[] { L("OK") }, Website);
    }

    public void ShowHelp()
    {
        InfoWindow.Show(L("How to use FileFlipper"), new[]
        {
            L("Convert: start dragging a file in File Explorer, then hold Shift. Round buttons appear in an arc above the pointer. Drop the file on a format and the converted copy is saved next to the original."),
            L("Tools: hold Ctrl + Shift while dragging to see tools for that file type (crop, compress, cut out, rotate, merge, split…)."),
            L("Markdown: pick MD to turn Word, PDF, PowerPoint or Excel into Markdown, ready for AI."),
            L("Changed your mind? Let go below the arc, or drag past it, and nothing happens."),
            L("Prefer clicking? Right-click files → Send to → FileFlipper. (On Windows 11, choose “Show more options” first.)"),
            L("Everything happens locally on your PC. FileFlipper lives in the notification area at the bottom-right of the screen."),
        }, new[] { L("Got It") });
    }
}
