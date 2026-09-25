using System.Diagnostics;
using System.IO;
using System.Linq;
using FlaUI.Core;
using FlaUI.UIA3;

namespace SoftPhone.UiTests;

/// <summary>
/// Launches the built app exe with an isolated per-user settings directory and provides the
/// FlaUI automation handle. Disposing kills the process and cleans up the temp settings.
/// </summary>
public sealed class AppHost : IDisposable
{
    public Application App { get; }
    public UIA3Automation Automation { get; } = new();

    private readonly string _settingsDir;

    private AppHost(Application app, string settingsDir)
    {
        App = app;
        _settingsDir = settingsDir;
    }

    public static AppHost Launch(string arguments, string? seedSettingsJson = null)
    {
        var exe = LocateExe();
        var settingsDir = Path.Combine(Path.GetTempPath(), "sp-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsDir);
        if (seedSettingsJson is not null)
            File.WriteAllText(Path.Combine(settingsDir, "settings.json"), seedSettingsJson);

        WaitForPreviousInstancesToExit(exe);

        var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = false };
        psi.Environment["SOFTPHONE_SETTINGS_DIR"] = settingsDir;
        // Keep each test's WebView2 profile isolated too.
        psi.Environment["WEBVIEW2_USER_DATA_FOLDER"] = Path.Combine(settingsDir, "WebView2");

        var app = Application.Launch(psi);
        return new AppHost(app, settingsDir);
    }

    /// <summary>
    /// A still-running copy (e.g. from a previous test) owns the single-instance mutex, and a new launch
    /// would only signal it and exit. Wait for any such copy of this exe to be gone first.
    /// </summary>
    private static void WaitForPreviousInstancesToExit(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        foreach (var process in Process.GetProcessesByName(name))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase))
                        process.WaitForExit(15_000);
                }
                catch { /* exited meanwhile, or not ours to inspect */ }
            }
        }
    }

    /// <summary>Walk up from the test output to the repo root and find the app exe.</summary>
    private static string LocateExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CrestApps.SoftPhone.Windows.sln")))
            dir = dir.Parent;
        if (dir is null)
            throw new FileNotFoundException("Could not locate the repo root (.sln) from the test directory.");

        foreach (var config in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(dir.FullName, "src", "SoftPhone.App", "bin", config, "net10.0-windows", "CrestApps.SoftPhone.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("Built app exe not found. Build SoftPhone.App first.");
    }

    public void Dispose()
    {
        try { App.Close(); } catch { }
        try { if (!App.HasExited) App.Kill(); } catch { }

        // Kill only asks the process to end. Until it has, it still holds the app's single-instance
        // mutex, so the next test's launch would find a "primary" instance, signal it, and exit at
        // once — leaving that test waiting for a window that never opens.
        try
        {
            using var process = Process.GetProcessById(App.ProcessId);
            process.WaitForExit(15_000);
        }
        catch (ArgumentException) { /* already gone */ }
        catch (InvalidOperationException) { /* already gone */ }
        Automation.Dispose();
        try { if (Directory.Exists(_settingsDir)) Directory.Delete(_settingsDir, true); } catch { }
    }
}
