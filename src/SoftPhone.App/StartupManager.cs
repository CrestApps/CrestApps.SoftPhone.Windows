using System.Diagnostics;
using Microsoft.Win32;

namespace SoftPhone.App;

/// <summary>
/// Toggles "start at login". When the app is packaged (MSIX), Windows manages the
/// <c>windows.startupTask</c> declared in the manifest; unpackaged/dev builds fall back
/// to the per-user Run key so the behavior is testable outside the Store package.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CrestAppsSoftPhone";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                    key.SetValue(ValueName, $"\"{exe}\" --tray");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Non-fatal: a locked-down machine may deny the write; the setting simply won't stick.
        }
    }
}
