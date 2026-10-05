using System;
using System.IO;
using Microsoft.Win32;

namespace FileFlipper;

/// <summary>Preferences in HKCU\Software\FileFlipper, plus the Windows integration points (startup and "Send to").</summary>
public static class Settings
{
    private const string KeyPath = @"Software\FileFlipper";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "FileFlipper";

    public static string ExecutablePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FileFlipper.exe");

    public static bool Enabled
    {
        get => Read("Enabled", 1) != 0;
        set => Write("Enabled", value ? 1 : 0);
    }

    public static bool ShownWelcome
    {
        get => Read("ShownWelcome", 0) != 0;
        set => Write("ShownWelcome", value ? 1 : 0);
    }

    /// <summary>Starts FileFlipper (in the background) when the user signs in.</summary>
    public static bool LaunchAtLogin
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunPath);
            return key?.GetValue(RunValue) is string command && command.Contains(ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath);
            if (value) key.SetValue(RunValue, $"\"{ExecutablePath}\" --background");
            else key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private static string SendToShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "FileFlipper.lnk");

    /// <summary>A "FileFlipper" entry in Explorer's right-click "Send to" menu.</summary>
    public static bool InSendToMenu
    {
        get => File.Exists(SendToShortcut);
        set
        {
            if (!value)
            {
                try { File.Delete(SendToShortcut); } catch { }
                return;
            }
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var shortcut = shell.CreateShortcut(SendToShortcut);
            shortcut.TargetPath = ExecutablePath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(ExecutablePath);
            shortcut.IconLocation = ExecutablePath + ",0";
            shortcut.Description = "FileFlipper";
            shortcut.Save();
        }
    }

    private static int Read(string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) is int value ? value : fallback;
    }

    private static void Write(string name, int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }
}
