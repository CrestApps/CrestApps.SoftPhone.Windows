using Microsoft.Win32;
using SoftPhone.Core.Settings;
using Xunit;

namespace SoftPhone.Core.Tests;

/// <summary>
/// End-to-end policy test that seeds a real HKCU key and asserts the effective value +
/// IsManaged, then cleans up. It uses a writable test sub-key (the production Policies path
/// is admin-only, which is exactly what makes it a managed key) so the registry read path is
/// still exercised for real, unelevated.
/// </summary>
public class RegistryPolicyLayerTests : IDisposable
{
    // A normal, user-writable key (NOT under \Policies, which requires elevation).
    private const string TestSubKey = @"SOFTWARE\CrestApps\SoftPhoneTest";

    private RegistryPolicyLayer Layer() => new(SettingSource.UserPolicy, RegistryHive.CurrentUser, TestSubKey);

    public RegistryPolicyLayerTests() => Cleanup();

    [Fact]
    public void Seeded_registry_policy_locks_domain()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(TestSubKey, writable: true))
        {
            key!.SetValue("Domain", "corp.example.com", RegistryValueKind.String);
            key.SetValue("RingtoneEnabled", 0, RegistryValueKind.DWord);
        }

        var provider = new PolicyProvider(Layer());
        var eff = provider.Resolve(new SoftPhoneSettings { Domain = "user.example.com", RingtoneEnabled = true });

        Assert.Equal("corp.example.com", eff.Domain.Value);
        Assert.True(eff.Domain.IsManaged);
        Assert.Equal(SettingSource.UserPolicy, eff.Domain.Source);

        Assert.False(eff.RingtoneEnabled.Value);
        Assert.True(eff.RingtoneEnabled.IsManaged);
    }

    [Fact]
    public void Seeded_registry_policy_with_override_allows_user_value()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(TestSubKey, writable: true))
        {
            key!.SetValue("Domain", "corp.example.com", RegistryValueKind.String);
            key.SetValue("AllowUserOverrideDomain", 1, RegistryValueKind.DWord);
        }

        var eff = new PolicyProvider(Layer()).Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("user.example.com", eff.Domain.Value);
        Assert.False(eff.Domain.IsManaged);
    }

    [Fact]
    public void No_policy_key_falls_back_to_user()
    {
        var eff = new PolicyProvider(Layer()).Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("user.example.com", eff.Domain.Value);
        Assert.False(eff.Domain.IsManaged);
    }

    private static void Cleanup()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(TestSubKey, throwOnMissingSubKey: false); } catch { }
    }

    public void Dispose() => Cleanup();
}
