using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;

namespace WhisperTray;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WhisperTray";
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "WhisperTray.lnk");
    private static string ExecutablePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Application executable path is unavailable.");

    public static bool IsEnabled
    {
        get
        {
            if (!File.Exists(ShortcutPath))
            {
                return false;
            }

            var (shell, shortcut) = OpenShortcut();

            try
            {
                return string.Equals((string)shortcut.TargetPath, ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var (shell, shortcut) = OpenShortcut();

            try
            {
                shortcut.TargetPath = ExecutablePath;
                shortcut.WorkingDirectory = AppContext.BaseDirectory;
                shortcut.Description = "Whisper Tray";
                shortcut.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }
        }
        else
        {
            File.Delete(ShortcutPath);
        }

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.DeleteValue(ValueName, false);
    }

    public static bool TryMigrateLegacyRegistration()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);

            if (key?.GetValue(ValueName) is string)
            {
                SetEnabled(true);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (dynamic Shell, dynamic Shortcut) OpenShortcut()
    {
        var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
        return (shell, ((dynamic)shell).CreateShortcut(ShortcutPath));
    }
}
