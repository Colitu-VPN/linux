using Avalonia.Controls;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Simple / Advanced mode. Simple shows connect, location, plan, notices, language, start-up,
/// account, support and updates; Advanced adds the expert settings. Hidden settings keep their
/// values and keep working (the kill switch too); only what is shown changes.
/// </summary>
public partial class ColituMainWindow
{
    private void HookUiMode()
    {
        SettingsAdvanced.IsCheckedChanged += (_, _) =>
        {
            if (!_applyingPreferences)
            {
                SetUiMode(SettingsAdvanced.IsChecked == true);
            }
        };
        // One tap from Home: Advanced mode on at once, the new items appear.
        HomeAdvancedButton.Click += (_, _) => SetUiMode(true);
        // "Advanced settings on": opens the settings in Advanced mode.
        AdvancedSettingsOnButton.Click += (_, _) =>
        {
            SetUiMode(true);
            Navigate("settings");
        };
    }

    /// <summary>Settings page: the expert settings show only in Advanced mode.</summary>
    private void ApplyUiMode()
    {
        var advanced = _vpn.AdvancedMode;
        SettingsAdvanced.IsChecked = advanced;

        // The connection card keeps its title and auto-connect in Simple mode.
        if (SettingsAutoConnect.Parent is Control autoConnectRow && autoConnectRow.Parent is Panel connection)
        {
            for (var i = 0; i < connection.Children.Count; i++)
            {
                var child = connection.Children[i];
                if (i == 0 || child == autoConnectRow)
                {
                    continue;
                }
                child.IsVisible = advanced
                    && (child != SettingsAdBlockRow || ColituVpnService.AdBlockAvailable)
                    && (child != SettingsProxyWarning || !_vpn.Preferences.IsTunMode);
            }
        }
        SplitCard.IsVisible = advanced;
        if (!advanced)
        {
            RotationCard.IsVisible = false;
        }
        if (SettingsTray.Parent is Control trayRow)
        {
            trayRow.IsVisible = advanced;
        }
        ApplyHomeMode();
    }

    /// <summary>Home: quick settings in Advanced mode; the "Advanced mode" button (and, when needed, "Advanced settings on") in Simple mode.</summary>
    private void ApplyHomeMode()
    {
        var advanced = _vpn.AdvancedMode;
        QuickSettingsCard.IsVisible = advanced;
        HomeAdvancedButton.IsVisible = !advanced;
        AdvancedSettingsOnButton.IsVisible = !advanced && _vpn.AdvancedSettingsActive;
    }

    private void SetUiMode(bool advanced)
    {
        if (advanced == _vpn.AdvancedMode)
        {
            return;
        }
        _vpn.SetAdvancedMode(advanced);
        if (!advanced)
        {
            _category = "all";
        }
        ApplyPreferencesToUi();
        ApplyStatus();
        BuildCategoryFilter();
        RenderServers();
        ShowToast(Loc.I[advanced ? "mode.advancedOn" : "mode.simpleOn"]);
    }
}
