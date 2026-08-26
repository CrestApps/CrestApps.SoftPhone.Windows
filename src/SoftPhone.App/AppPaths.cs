using System.IO;

namespace SoftPhone.App;

/// <summary>Per-user, roaming-safe locations for app data (settings, WebView2 profile).</summary>
public static class AppPaths
{
    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CrestApps", "SoftPhone");

    /// <summary>WebView2 user-data folder — holds the tenant session cookie between runs.</summary>
    public static string WebViewUserData
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER");
            var p = string.IsNullOrWhiteSpace(overrideDir) ? Path.Combine(Root, "WebView2") : overrideDir;
            Directory.CreateDirectory(p);
            return p;
        }
    }

    /// <summary>Absolute path to a bundled asset shipped next to the executable.</summary>
    public static string Asset(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", relative);
}
