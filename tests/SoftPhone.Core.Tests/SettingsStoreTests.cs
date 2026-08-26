using SoftPhone.Core.Settings;
using Xunit;

namespace SoftPhone.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sp-tests-" + Guid.NewGuid().ToString("N"));

    private string PathFor() => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Load_returns_defaults_when_missing()
    {
        var store = new SettingsStore(PathFor());
        var s = store.Load();
        Assert.Null(s.Domain);
        Assert.True(s.StartWithWindows);
        Assert.True(s.RingtoneEnabled);
    }

    [Fact]
    public void Save_then_Load_roundtrips()
    {
        var store = new SettingsStore(PathFor());
        store.Save(new SoftPhoneSettings
        {
            Domain = "dialpad-dev.crestapps.online",
            StartWithWindows = false,
            RingtoneEnabled = false,
            Collapsed = true,
            Diagnostics = true,
            WindowBounds = new WindowBounds { Top = 10, Left = 20, Width = 400, Height = 700 },
        });

        var loaded = new SettingsStore(PathFor()).Load();
        Assert.Equal("dialpad-dev.crestapps.online", loaded.Domain);
        Assert.False(loaded.StartWithWindows);
        Assert.False(loaded.RingtoneEnabled);
        Assert.True(loaded.Collapsed);
        Assert.True(loaded.Diagnostics);
        Assert.NotNull(loaded.WindowBounds);
        Assert.Equal(400, loaded.WindowBounds!.Width);
    }

    [Fact]
    public void Load_survives_corrupt_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor(), "{ not valid json ");
        var s = new SettingsStore(PathFor()).Load();
        Assert.NotNull(s); // defaults, no throw
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { }
    }
}
