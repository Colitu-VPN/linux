using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Settings: split tunneling. Off by default; "selected apps and sites bypass the VPN" or
/// "only selected apps and sites use the VPN". Apps (process name or path) work in TUN
/// mode only; domains and IP ranges in both modes. Saving while connected reconnects.
/// </summary>
public partial class ColituMainWindow
{
    private bool _splitLoaded;

    private void WireSplit()
    {
        foreach (var mode in new[] { SplitModeOff, SplitModeExclude, SplitModeInclude })
        {
            mode.IsCheckedChanged += (_, _) =>
            {
                if (!_applyingPreferences)
                {
                    ApplySplitVisibility(SelectedSplitMode());
                }
            };
        }
        SplitSaveButton.Click += async (_, _) => await GuardAsync("SaveSplit", SaveSplitTunnelAsync);
        SplitChip.PointerReleased += (_, _) => Navigate("settings");
    }

    private string SelectedSplitMode() =>
        SplitModeExclude.IsChecked == true ? ColituSplitTunnelModes.Exclude
        : SplitModeInclude.IsChecked == true ? ColituSplitTunnelModes.Include
        : ColituSplitTunnelModes.Off;

    /// <summary>Fills the settings from the saved preferences (the lists only once, so typing is never overwritten).</summary>
    private void ApplySplitToUi(bool reloadLists = false)
    {
        var preferences = _vpn.Preferences;
        if (!_splitLoaded || reloadLists)
        {
            _splitLoaded = true;
            SplitModeOff.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Off;
            SplitModeExclude.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Exclude;
            SplitModeInclude.IsChecked = preferences.SplitTunnelMode == ColituSplitTunnelModes.Include;
            SplitAppsBox.Text = string.Join('\n', preferences.SplitTunnelApps ?? []);
            SplitDomainsBox.Text = string.Join('\n', preferences.SplitTunnelDomains ?? []);
            SplitIpsBox.Text = string.Join('\n', preferences.SplitTunnelIps ?? []);
            SplitErrorBox.IsVisible = false;
        }
        ApplySplitVisibility(SelectedSplitMode());
    }

    private void ApplySplitVisibility(string mode)
    {
        SplitLists.IsVisible = mode != ColituSplitTunnelModes.Off;
        SplitProxyNote.IsVisible = !_vpn.Preferences.IsTunMode;
        SplitIncludeNote.IsVisible = mode == ColituSplitTunnelModes.Include;
    }

    /// <summary>Home: "Split tunneling on: 3 apps/sites outside the VPN".</summary>
    private void ApplySplitChip()
    {
        var preferences = _vpn.Preferences;
        var count = ColituSplitTunnel.EffectiveCount(preferences);
        SplitChip.IsVisible = count > 0 && _vpn.AdvancedMode;
        SplitChipText.Text = count > 0
            ? Loc.I.Format(preferences.SplitTunnelMode == ColituSplitTunnelModes.Include ? "home.split.include" : "home.split.exclude", ("n", count))
            : "";
    }

    private async Task SaveSplitTunnelAsync()
    {
        var mode = SelectedSplitMode();
        var apps = ColituSplitTunnel.Validate(ColituSplitTunnel.SplitEntries(SplitAppsBox.Text, allowSpaces: true), ColituSplitTunnel.TryNormalizeApp, StringComparer.Ordinal);
        var domainEntries = ColituSplitTunnel.SplitEntries(SplitDomainsBox.Text);
        var ipEntries = ColituSplitTunnel.SplitEntries(SplitIpsBox.Text);
        // An address typed among the sites (or a site among the addresses) goes to its own list.
        ipEntries.AddRange(domainEntries.Where(entry => ColituSplitTunnel.TryNormalizeNetwork(entry, out _)));
        domainEntries.RemoveAll(entry => ColituSplitTunnel.TryNormalizeNetwork(entry, out _));
        domainEntries.AddRange(ipEntries.Where(entry => !ColituSplitTunnel.TryNormalizeNetwork(entry, out _) && ColituSplitTunnel.TryNormalizeDomain(entry, out _)));
        ipEntries.RemoveAll(entry => !ColituSplitTunnel.TryNormalizeNetwork(entry, out _) && ColituSplitTunnel.TryNormalizeDomain(entry, out _));
        var domains = ColituSplitTunnel.Validate(domainEntries, ColituSplitTunnel.TryNormalizeDomain, StringComparer.Ordinal);
        var ips = ColituSplitTunnel.Validate(ipEntries, ColituSplitTunnel.TryNormalizeNetwork, StringComparer.OrdinalIgnoreCase);

        var invalid = apps.Invalid.Concat(domains.Invalid).Concat(ips.Invalid).ToList();
        if (invalid.Count > 0)
        {
            var shown = string.Join(", ", invalid.Take(8)) + (invalid.Count > 8 ? ", …" : "");
            SplitErrorText.Text = Loc.I.Format("settings.split.invalid", ("items", shown));
            SplitErrorBox.IsVisible = true;
            ColituMotion.FadeIn(SplitErrorBox, 6);
            return;
        }

        var preferences = _vpn.Preferences with
        {
            SplitTunnelMode = mode,
            SplitTunnelApps = apps.Valid,
            SplitTunnelDomains = domains.Valid,
            SplitTunnelIps = ips.Valid
        };
        await SavePreferencesAsync(preferences);
        ApplySplitToUi(reloadLists: true);

        if (mode != ColituSplitTunnelModes.Off && !ColituSplitTunnel.IsActive(preferences))
        {
            SplitErrorText.Text = Loc.I[_vpn.Preferences.IsTunMode || apps.Valid.Count == 0 ? "settings.split.empty" : "settings.split.proxyNote"];
            SplitErrorBox.IsVisible = true;
        }
    }
}
