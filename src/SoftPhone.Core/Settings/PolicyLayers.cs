using Microsoft.Win32;

namespace SoftPhone.Core.Settings;

/// <summary>
/// Reads a managed layer from a registry policy key
/// (ADMX-backed Group Policy / Intune). Values: <c>Domain</c> (REG_SZ),
/// <c>AllowUserOverrideDomain</c> / <c>StartWithWindows</c> / <c>RingtoneEnabled</c>
/// (REG_DWORD 0/1). Contract §6.
/// </summary>
public sealed class RegistryPolicyLayer : IPolicyLayer
{
    public const string SubKey = @"SOFTWARE\Policies\CrestApps\SoftPhone";

    private readonly RegistryHive _hive;
    private readonly string _subKey;

    /// <param name="subKey">
    /// Registry sub-key under the hive. Defaults to the GP-backed Policies path; overridable
    /// so tests can exercise the read path against a writable (non-Policies) key.
    /// </param>
    public RegistryPolicyLayer(SettingSource source, RegistryHive hive, string? subKey = null)
    {
        Source = source;
        _hive = hive;
        _subKey = subKey ?? SubKey;
    }

    public SettingSource Source { get; }

    public static RegistryPolicyLayer Machine() => new(SettingSource.MachinePolicy, RegistryHive.LocalMachine);
    public static RegistryPolicyLayer CurrentUser() => new(SettingSource.UserPolicy, RegistryHive.CurrentUser);

    public string? GetString(string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(_subKey);
            return key?.GetValue(name) as string;
        }
        catch
        {
            return null;
        }
    }

    public bool? GetBool(string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(_hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(_subKey);
            var value = key?.GetValue(name);
            if (value is null) return null;
            return Convert.ToInt64(value) != 0;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads a managed layer from a machine-wide provisioning JSON file
/// (<c>%ProgramData%\CrestApps\SoftPhone\config.json</c>) for non-GPO shops. Same keys as
/// the registry layer; lower precedence than registry policy, higher than the user. §6.
/// </summary>
public sealed class ProvisioningFilePolicyLayer : IPolicyLayer
{
    private readonly Dictionary<string, System.Text.Json.JsonElement> _values;

    private ProvisioningFilePolicyLayer(Dictionary<string, System.Text.Json.JsonElement> values) => _values = values;

    public SettingSource Source => SettingSource.Provisioning;

    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "CrestApps", "SoftPhone", "config.json");

    public static ProvisioningFilePolicyLayer Load(string? path = null)
    {
        path ??= DefaultPath();
        var empty = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path)) return new ProvisioningFilePolicyLayer(empty);
            var json = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var map = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in doc.RootElement.EnumerateObject())
                map[prop.Name] = prop.Value.Clone();
            return new ProvisioningFilePolicyLayer(map);
        }
        catch
        {
            return new ProvisioningFilePolicyLayer(empty);
        }
    }

    public string? GetString(string name)
    {
        if (!_values.TryGetValue(name, out var el)) return null;
        return el.ValueKind == System.Text.Json.JsonValueKind.String ? el.GetString() : null;
    }

    public bool? GetBool(string name)
    {
        if (!_values.TryGetValue(name, out var el)) return null;
        return el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            System.Text.Json.JsonValueKind.Number => el.GetInt64() != 0,
            _ => null,
        };
    }
}

public static class PolicyProviderFactory
{
    /// <summary>
    /// The real, machine-configured provider: machine policy ▸ user policy ▸ provisioning file.
    /// </summary>
    public static PolicyProvider CreateDefault() => new(
        RegistryPolicyLayer.Machine(),
        RegistryPolicyLayer.CurrentUser(),
        ProvisioningFilePolicyLayer.Load());
}
