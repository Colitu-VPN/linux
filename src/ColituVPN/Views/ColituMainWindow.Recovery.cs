using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Two prompts about how much of the computer Colitu protects:
/// - after a crash with the kill switch on, the rules keep blocking the internet (fail
///   closed); the prompt explains why and offers "Reconnect" or "Turn off protection";
/// - once per installation, the offer to protect all traffic with TUN mode instead of
///   the proxy mode Colitu starts in.
/// </summary>
public partial class ColituMainWindow
{
    private TaskCompletionSource<bool>? _tunOfferResult;
    private bool _recoveryBusy;

    private void WireRecovery()
    {
        KillSwitchRecoveryReconnect.Click += async (_, _) => await GuardAsync("RecoveryReconnect", RecoveryReconnectAsync);
        KillSwitchRecoveryTurnOff.Click += async (_, _) => await GuardAsync("RecoveryTurnOff", RecoveryTurnOffAsync);
        KillSwitchRecoveryLater.Click += (_, _) =>
        {
            if (!_recoveryBusy)
            {
                HideKillSwitchRecovery();
            }
        };
        TunOfferAccept.Click += (_, _) => CloseTunOffer(true);
        TunOfferKeep.Click += (_, _) => CloseTunOffer(false);
    }

    // ── Kill switch after a crash ──────────────────────────────────────────
    private void ShowKillSwitchRecovery()
    {
        var wantsProtection = _vpn.KillSwitchApplies;
        KillSwitchRecoveryBody.Text = Loc.I[wantsProtection ? "ksr.body" : "ksr.bodyOff"];
        // Reconnecting needs a signed-in account; otherwise only turning protection off helps.
        KillSwitchRecoveryReconnect.IsVisible = wantsProtection && AppView.IsVisible && !_planRequired;
        KillSwitchRecoveryErrorBox.IsVisible = false;
        SetRecoveryBusy(false);
        KillSwitchRecoveryPrompt.IsVisible = true;
        ColituMotion.FadeIn(KillSwitchRecoveryPrompt, 8);
        ShowFromTray();
        ApplyStatus();
    }

    private void HideKillSwitchRecovery()
    {
        KillSwitchRecoveryPrompt.IsVisible = false;
        ApplyStatus();
    }

    private async Task RecoveryReconnectAsync()
    {
        if (_recoveryBusy || !await AskSudoPasswordAsync())
        {
            return;
        }
        SetRecoveryBusy(true);
        try
        {
            // The crashed run's root cores would hold the TUN device and its routes.
            await _vpn.StopOrphanRootCoresAsync();
        }
        finally
        {
            SetRecoveryBusy(false);
        }
        HideKillSwitchRecovery();
        // Connecting replaces the old rules with the new session's in one step: the
        // internet never opens outside the VPN. If it fails, the rules keep blocking.
        await ToggleConnectionAsync();
        ApplyStatus();
    }

    private async Task RecoveryTurnOffAsync()
    {
        if (_recoveryBusy)
        {
            return;
        }
        SetRecoveryBusy(true);
        try
        {
            if (await TryTurnOffKillSwitchBlockAsync())
            {
                HideKillSwitchRecovery();
                ShowToast(Loc.I["ksr.removed"]);
            }
            else if (_vpn.KillSwitchEngaged && AppManager.Instance.LinuxSudoPwd.IsNotEmpty())
            {
                KillSwitchRecoveryErrorText.Text = Loc.I["ksr.failed"];
                KillSwitchRecoveryErrorBox.IsVisible = true;
            }
        }
        finally
        {
            SetRecoveryBusy(false);
        }
    }

    /// <summary>
    /// "Turn protection off and restore internet" (home, tray, crash prompt): removes the
    /// kill switch rules, asking for the sudo password first when this run does not know
    /// it yet (rules kept from a crashed run).
    /// </summary>
    private async Task TurnOffKillSwitchBlockAsync()
    {
        if (await TryTurnOffKillSwitchBlockAsync())
        {
            KillSwitchRecoveryPrompt.IsVisible = false;
            ShowToast(Loc.I["ksr.removed"]);
        }
        else if (_vpn.KillSwitchEngaged && AppManager.Instance.LinuxSudoPwd.IsNotEmpty())
        {
            ShowToast(Loc.I["ksr.failed"], true);
        }
        ApplyStatus();
    }

    /// <summary>True once the rules are gone; false when the password was refused or nft failed.</summary>
    private async Task<bool> TryTurnOffKillSwitchBlockAsync()
    {
        if (_vpn.KillSwitchNeedsPasswordToRelease && !await AskSudoPasswordAsync("sudo.bodyKillSwitch"))
        {
            return false;
        }
        if (_vpn.KillSwitchRecoveryPending)
        {
            return await _vpn.RemoveLeftoverKillSwitchAsync();
        }
        await _vpn.DisconnectAsync();
        return !_vpn.KillSwitchEngaged;
    }

    private void SetRecoveryBusy(bool busy)
    {
        _recoveryBusy = busy;
        KillSwitchRecoverySpinner.IsVisible = busy;
        KillSwitchRecoveryReconnect.IsEnabled = KillSwitchRecoveryTurnOff.IsEnabled = KillSwitchRecoveryLater.IsEnabled = !busy;
    }

    // ── One-time TUN offer ─────────────────────────────────────────────────
    /// <summary>
    /// Shows the offer and waits for the answer. The mode only changes when the user picks
    /// TUN; either answer is remembered so the offer never comes back.
    /// </summary>
    private async Task OfferTunModeAsync()
    {
        _tunOfferResult?.TrySetResult(false);
        _tunOfferResult = new TaskCompletionSource<bool>();
        TunOfferPrompt.IsVisible = true;
        ColituMotion.FadeIn(TunOfferPrompt, 8);
        var useTun = await _tunOfferResult.Task;
        _vpn.MarkTunOfferAnswered();
        if (!useTun || _vpn.Preferences.IsTunMode)
        {
            return;
        }

        await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { ConnectionMode = ColituConnectionModes.Tun });
        ApplyPreferencesToUi();
        ApplyStatus();
        // The user just read that TUN needs the password: ask now so auto-connect can go on.
        if (_vpn.Preferences.AutoConnectEnabled && (!_planRequired || _offline))
        {
            await AskSudoPasswordAsync();
        }
        else
        {
            ShowToast(Loc.I["tunOffer.done"]);
        }
    }

    private void CloseTunOffer(bool useTun)
    {
        TunOfferPrompt.IsVisible = false;
        _tunOfferResult?.TrySetResult(useTun);
        _tunOfferResult = null;
    }
}
