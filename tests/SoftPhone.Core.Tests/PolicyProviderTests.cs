using SoftPhone.Core.Settings;
using Xunit;

namespace SoftPhone.Core.Tests;

public class PolicyProviderTests
{
    /// <summary>In-memory policy layer for precedence/lock tests — no registry needed.</summary>
    private sealed class FakeLayer : IPolicyLayer
    {
        private readonly Dictionary<string, object> _v = new(StringComparer.OrdinalIgnoreCase);
        public FakeLayer(SettingSource source) => Source = source;
        public SettingSource Source { get; }
        public FakeLayer Set(string name, string value) { _v[name] = value; return this; }
        public FakeLayer Set(string name, bool value) { _v[name] = value; return this; }
        public string? GetString(string name) => _v.TryGetValue(name, out var o) && o is string s ? s : null;
        public bool? GetBool(string name) => _v.TryGetValue(name, out var o) && o is bool b ? b : null;
    }

    [Fact]
    public void Default_when_no_policy_and_no_user()
    {
        var eff = new PolicyProvider().Resolve(new SoftPhoneSettings());
        Assert.Equal("", eff.Domain.Value);
        Assert.Equal(SettingSource.Default, eff.Domain.Source);
        Assert.False(eff.Domain.IsManaged);
        Assert.False(eff.AnyManaged);
    }

    [Fact]
    public void User_value_wins_over_default()
    {
        var eff = new PolicyProvider().Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("user.example.com", eff.Domain.Value);
        Assert.Equal(SettingSource.User, eff.Domain.Source);
        Assert.False(eff.Domain.IsManaged);
    }

    [Fact]
    public void Machine_policy_locks_domain_and_beats_user()
    {
        var machine = new FakeLayer(SettingSource.MachinePolicy).Set("Domain", "corp.example.com");
        var eff = new PolicyProvider(machine).Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("corp.example.com", eff.Domain.Value);
        Assert.Equal(SettingSource.MachinePolicy, eff.Domain.Source);
        Assert.True(eff.Domain.IsManaged);
        Assert.True(eff.AnyManaged);
    }

    [Fact]
    public void AllowUserOverride_lets_user_value_win_and_stay_editable()
    {
        var machine = new FakeLayer(SettingSource.MachinePolicy)
            .Set("Domain", "corp.example.com")
            .Set("AllowUserOverrideDomain", true);
        var eff = new PolicyProvider(machine).Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("user.example.com", eff.Domain.Value);
        Assert.False(eff.Domain.IsManaged);
    }

    [Fact]
    public void AllowUserOverride_seeds_managed_value_when_user_has_none()
    {
        var machine = new FakeLayer(SettingSource.MachinePolicy)
            .Set("Domain", "corp.example.com")
            .Set("AllowUserOverrideDomain", true);
        var eff = new PolicyProvider(machine).Resolve(new SoftPhoneSettings());
        Assert.Equal("corp.example.com", eff.Domain.Value);
        Assert.False(eff.Domain.IsManaged); // override allowed → not locked
    }

    [Fact]
    public void Precedence_machine_over_user_policy_over_provisioning()
    {
        var machine = new FakeLayer(SettingSource.MachinePolicy).Set("Domain", "machine.example.com");
        var userPol = new FakeLayer(SettingSource.UserPolicy).Set("Domain", "userpol.example.com");
        var prov = new FakeLayer(SettingSource.Provisioning).Set("Domain", "prov.example.com");

        var eff = new PolicyProvider(machine, userPol, prov).Resolve(new SoftPhoneSettings());
        Assert.Equal("machine.example.com", eff.Domain.Value);
        Assert.Equal(SettingSource.MachinePolicy, eff.Domain.Source);
    }

    [Fact]
    public void Provisioning_used_when_no_registry_policy()
    {
        var prov = new FakeLayer(SettingSource.Provisioning).Set("Domain", "prov.example.com");
        var eff = new PolicyProvider(prov).Resolve(new SoftPhoneSettings { Domain = "user.example.com" });
        Assert.Equal("prov.example.com", eff.Domain.Value);
        Assert.True(eff.Domain.IsManaged);
        Assert.Equal(SettingSource.Provisioning, eff.Domain.Source);
    }

    [Fact]
    public void Managed_booleans_lock_start_and_ringtone()
    {
        var machine = new FakeLayer(SettingSource.MachinePolicy)
            .Set("StartWithWindows", false)
            .Set("RingtoneEnabled", false);
        var eff = new PolicyProvider(machine).Resolve(new SoftPhoneSettings { StartWithWindows = true, RingtoneEnabled = true });
        Assert.False(eff.StartWithWindows.Value);
        Assert.True(eff.StartWithWindows.IsManaged);
        Assert.False(eff.RingtoneEnabled.Value);
        Assert.True(eff.RingtoneEnabled.IsManaged);
    }
}
