using System.Reflection;

namespace SoftPhone.App;

/// <summary>
/// Surfaces the tag-driven version and the build stamp (injected by Directory.Build.props)
/// so the Settings window can answer "which build am I running".
/// </summary>
public static class BuildInfo
{
    public static string Version
    {
        get
        {
            var info = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            // Strip any "+<sha>" source-revision suffix.
            if (info is { Length: > 0 })
            {
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        }
    }

    public static string BuildStampUtc =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildStampUtc")?.Value ?? "unknown";

    public static string Summary => $"v{Version} · built {BuildStampUtc}";
}
