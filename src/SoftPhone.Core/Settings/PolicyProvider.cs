namespace SoftPhone.Core.Settings;

/// <summary>Where an effective value came from (lowest → highest precedence).</summary>
public enum SettingSource
{
    Default,
    User,
    Provisioning,   // %ProgramData%\CrestApps\SoftPhone\config.json
    UserPolicy,     // HKCU\SOFTWARE\Policies\CrestApps\SoftPhone
    MachinePolicy,  // HKLM\SOFTWARE\Policies\CrestApps\SoftPhone
}

/// <summary>An effective setting value with its origin and whether it is locked by policy.</summary>
public readonly record struct EffectiveValue<T>(T Value, SettingSource Source, bool IsManaged);

/// <summary>
/// One layer of managed configuration. Implementations read the registry (ADMX/Intune),
/// a provisioning JSON file, etc. Kept as an interface so precedence/lock behavior is
/// unit-tested with in-memory fakes — no registry required.
/// </summary>
public interface IPolicyLayer
{
    SettingSource Source { get; }
    string? GetString(string name);
    bool? GetBool(string name);
}

/// <summary>Fully resolved settings the UI binds to (managed fields shown read-only).</summary>
public sealed class EffectiveSettings
{
    public required EffectiveValue<string> Domain { get; init; }
    public required EffectiveValue<bool> StartWithWindows { get; init; }
    public required EffectiveValue<bool> RingtoneEnabled { get; init; }

    /// <summary>True if any field is managed by policy (drives the "Managed by your organization" note).</summary>
    public bool AnyManaged => Domain.IsManaged || StartWithWindows.IsManaged || RingtoneEnabled.IsManaged;
}

/// <summary>
/// Merges managed policy layers over the user settings and the packaged defaults, with
/// precedence <b>Policy ▸ Provisioning ▸ User ▸ Default</b> (contract §6). Exposes each
/// setting's effective value plus its <c>IsManaged</c> lock state.
///
/// Policy value names (registry / provisioning JSON): <c>Domain</c> (string),
/// <c>AllowUserOverrideDomain</c> (bool), <c>StartWithWindows</c> (bool),
/// <c>RingtoneEnabled</c> (bool).
/// </summary>
public sealed class PolicyProvider
{
    private readonly IReadOnlyList<IPolicyLayer> _layers;

    /// <param name="layers">
    /// Managed layers in <b>descending</b> precedence (e.g. [MachinePolicy, UserPolicy, Provisioning]).
    /// The first layer that supplies a value wins for that setting.
    /// </param>
    public PolicyProvider(params IPolicyLayer[] layers) => _layers = layers;

    public PolicyProvider(IEnumerable<IPolicyLayer> layers) => _layers = layers.ToList();

    /// <summary>Resolve the effective settings, given the current user layer.</summary>
    public EffectiveSettings Resolve(SoftPhoneSettings user)
    {
        return new EffectiveSettings
        {
            Domain = ResolveDomain(user),
            StartWithWindows = ResolveBool("StartWithWindows", user.StartWithWindows, allowOverride: null),
            RingtoneEnabled = ResolveBool("RingtoneEnabled", user.RingtoneEnabled, allowOverride: null),
        };
    }

    private EffectiveValue<string> ResolveDomain(SoftPhoneSettings user)
    {
        // Find the highest-precedence layer that specifies a Domain.
        foreach (var layer in _layers)
        {
            var managed = layer.GetString("Domain");
            if (string.IsNullOrWhiteSpace(managed)) continue;

            // AllowUserOverrideDomain (default false = locked). Read it from the same layer.
            var allowOverride = layer.GetBool("AllowUserOverrideDomain") ?? false;

            if (allowOverride && !string.IsNullOrWhiteSpace(user.Domain))
            {
                // Managed value is only a seed; the user's explicit choice wins and stays editable.
                return new EffectiveValue<string>(user.Domain!.Trim(), SettingSource.User, IsManaged: false);
            }

            // Locked (or override allowed but user has not set anything yet).
            return new EffectiveValue<string>(managed.Trim(), layer.Source, IsManaged: !allowOverride);
        }

        if (!string.IsNullOrWhiteSpace(user.Domain))
            return new EffectiveValue<string>(user.Domain!.Trim(), SettingSource.User, IsManaged: false);

        return new EffectiveValue<string>("", SettingSource.Default, IsManaged: false);
    }

    private EffectiveValue<bool> ResolveBool(string name, bool userValue, bool? allowOverride)
    {
        foreach (var layer in _layers)
        {
            var managed = layer.GetBool(name);
            if (managed is null) continue;
            return new EffectiveValue<bool>(managed.Value, layer.Source, IsManaged: true);
        }
        return new EffectiveValue<bool>(userValue, SettingSource.User, IsManaged: false);
    }
}
