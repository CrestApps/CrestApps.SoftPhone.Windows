using System.Text.Json;

namespace SoftPhone.Core.Settings;

/// <summary>
/// Loads/saves the per-user <see cref="SoftPhoneSettings"/> as JSON. The path is
/// injectable so unit tests use a temp file; the default lives under the roaming
/// per-user app-data folder.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public string Path { get; }

    public SettingsStore(string path) => Path = path;

    public static SettingsStore Default() => new(DefaultPath());

    public static string DefaultPath()
    {
        // Test/CI isolation: SOFTPHONE_SETTINGS_DIR overrides the per-user location.
        var overrideDir = Environment.GetEnvironmentVariable("SOFTPHONE_SETTINGS_DIR");
        var baseDir = string.IsNullOrWhiteSpace(overrideDir)
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrestApps", "SoftPhone")
            : overrideDir;
        return System.IO.Path.Combine(baseDir, "settings.json");
    }

    public SoftPhoneSettings Load()
    {
        try
        {
            if (!File.Exists(Path)) return new SoftPhoneSettings();
            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json)) return new SoftPhoneSettings();
            return JsonSerializer.Deserialize<SoftPhoneSettings>(json, JsonOptions) ?? new SoftPhoneSettings();
        }
        catch
        {
            // Corrupt/unreadable settings should never crash the tray app.
            return new SoftPhoneSettings();
        }
    }

    public void Save(SoftPhoneSettings settings)
    {
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(Path, json);
    }
}
