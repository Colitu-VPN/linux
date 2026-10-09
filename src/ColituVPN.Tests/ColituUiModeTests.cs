using System.Text.Json;
using AwesomeAssertions;
using v2rayN.Desktop.Services;
using Xunit;

namespace ColituVPN.Tests;

/// <summary>Simple / Advanced mode: who starts where, and what Simple mode still reports.</summary>
public class ColituUiModeTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void NewInstall_StartsInSimpleMode()
    {
        ColituVpnPreferences.ForNewInstall().AdvancedMode.Should().BeFalse();
    }

    [Fact]
    public void UserUpdatingFromAVersionWithoutTheSetting_KeepsAdvancedMode()
    {
        // A state file written before the setting existed.
        var session = JsonSerializer.Deserialize<ColituVpnSession>("""{"Status":0,"Preferences":{"KillSwitchEnabled":true,"ConnectionMode":"tun"},"PreferencesMigration":1}""", Options)!;
        session.Preferences.AdvancedMode.Should().BeTrue();
    }

    [Fact]
    public void SimpleMode_IsKeptAcrossRestarts_AndHiddenSettingsKeepTheirValues()
    {
        var simple = ColituVpnPreferences.ForNewInstall() with { KillSwitchEnabled = true, AdBlockEnabled = true };
        var json = JsonSerializer.Serialize(new ColituVpnSession { Preferences = simple }, Options);
        var restored = JsonSerializer.Deserialize<ColituVpnSession>(json, Options)!.Preferences.Normalize();

        restored.AdvancedMode.Should().BeFalse();
        restored.KillSwitchEnabled.Should().BeTrue();
        restored.AdBlockEnabled.Should().BeTrue();
        restored.WarmSpareEnabled.Should().BeTrue();
    }

    [Fact]
    public void AdvancedSettingsOn_OnlyForSettingsThatChangeWhatTheConnectionDoes()
    {
        var defaults = ColituVpnPreferences.ForNewInstall();
        // The kill switch is on by default: only holding the internet closed counts.
        ColituVpnService.AdvancedSettingsOn(defaults, rotation: false, multihopSelected: false, killSwitchHolding: false).Should().BeFalse();
        ColituVpnService.AdvancedSettingsOn(defaults, false, false, killSwitchHolding: true).Should().BeTrue();
        ColituVpnService.AdvancedSettingsOn(defaults, rotation: true, false, false).Should().BeTrue();
        ColituVpnService.AdvancedSettingsOn(defaults, false, multihopSelected: true, false).Should().BeTrue();

        var split = defaults with { SplitTunnelMode = ColituSplitTunnelModes.Exclude, SplitTunnelDomains = ["example.com"] };
        ColituVpnService.AdvancedSettingsOn(split, false, false, false).Should().BeTrue();
        // Split tunnelling chosen but empty does nothing.
        ColituVpnService.AdvancedSettingsOn(defaults with { SplitTunnelMode = ColituSplitTunnelModes.Exclude }, false, false, false).Should().BeFalse();
    }
}
