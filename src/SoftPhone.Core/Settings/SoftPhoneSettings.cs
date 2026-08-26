namespace SoftPhone.Core.Settings;

/// <summary>Last-known phone-window bounds for restore-on-open / expand.</summary>
public sealed class WindowBounds
{
    public double Top { get; set; }
    public double Left { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>
/// Per-user persisted settings. The equivalent of the extension's StoredSettings,
/// but only the <em>user</em> layer — the effective value can be overridden/locked by
/// enterprise policy (see <c>PolicyProvider</c>).
/// </summary>
public sealed class SoftPhoneSettings
{
    /// <summary>The configured tenant domain, e.g. "dialpad-dev.crestapps.online" (no scheme).</summary>
    public string? Domain { get; set; }

    /// <summary>Start the tray app at login (MSIX startupTask).</summary>
    public bool StartWithWindows { get; set; } = true;

    /// <summary>Play the bundled ringtone on an incoming call.</summary>
    public bool RingtoneEnabled { get; set; } = true;

    /// <summary>Last known phone-window bounds.</summary>
    public WindowBounds? WindowBounds { get; set; }

    /// <summary>Whether the phone window is currently collapsed to the compact affordance.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Whether the Settings window reveals the diagnostics tools.</summary>
    public bool Diagnostics { get; set; }

    public SoftPhoneSettings Clone() => new()
    {
        Domain = Domain,
        StartWithWindows = StartWithWindows,
        RingtoneEnabled = RingtoneEnabled,
        WindowBounds = WindowBounds is null ? null : new WindowBounds
        {
            Top = WindowBounds.Top,
            Left = WindowBounds.Left,
            Width = WindowBounds.Width,
            Height = WindowBounds.Height,
        },
        Collapsed = Collapsed,
        Diagnostics = Diagnostics,
    };
}
