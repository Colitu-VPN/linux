using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Plan changes that touch this device:
/// - the banner in the last days of a trial or plan that moves to the free plan (which
///   may allow fewer devices), closable for the day;
/// - the full-screen "This device is paused" state when the panel answers
///   DEVICE_OVER_LIMIT. While paused the app never connects by itself, and the kill
///   switch is released so it cannot keep the user offline here.
/// </summary>
public partial class ColituMainWindow
{
    private bool _pausedDuringConnect;
    private bool _pausedBusy;

    private void WirePaused()
    {
        _auth.DevicePauseChanged += () => Dispatcher.UIThread.Post(ApplyDevicePause);
        DevicePausedActivate.Click += async (_, _) => await GuardAsync("PausedActivate", ActivateThisDeviceAsync);
        DevicePausedPremium.Click += (_, _) => OpenUrl(LocalizedPath("/pricing"));
        DevicePausedUnblock.Click += async (_, _) => await GuardAsync("PausedUnblock", async () =>
        {
            await TurnOffKillSwitchBlockAsync();
            ApplyDevicePause();
        });
        DevicePausedRetry.Click += async (_, _) => await GuardAsync("PausedRetry", RetryAfterPauseAsync);
        DevicePausedSignOut.Click += async (_, _) => await GuardAsync("PausedSignOut", async () =>
        {
            DevicePausedView.IsVisible = false;
            await SignOutAsync();
        });
        PlanNoticeClose.Click += (_, _) =>
        {
            _vpn.DismissPlanNoticeForToday();
            PlanNoticeBanner.IsVisible = false;
        };
    }

    // ── Paused device ──────────────────────────────────────────────────────
    private void ApplyDevicePause()
    {
        var pause = _auth.DevicePause;
        if (pause == null || !_auth.HasSession || AuthView.IsVisible || MfaView.IsVisible)
        {
            DevicePausedView.IsVisible = false;
            return;
        }
        DevicePausedBody.Text = pause.Describe(Loc.I);
        DevicePausedUnblock.IsVisible = _vpn.KillSwitchEngaged && _vpn.Status != ColituVpnStatus.Connected;
        if (!DevicePausedView.IsVisible)
        {
            DevicePausedErrorBox.IsVisible = false;
            SetPausedBusy(false);
            DevicePausedView.IsVisible = true;
            ColituMotion.FadeIn(DevicePausedView, 10);
            if (!IsVisible)
            {
                Notify(Loc.I["err.devicePaused"]);
            }
        }
        ApplyStatus();
    }

    private async Task ActivateThisDeviceAsync()
    {
        if (_pausedBusy)
        {
            return;
        }
        SetPausedBusy(true);
        DevicePausedErrorBox.IsVisible = false;
        try
        {
            await _auth.ActivateThisDeviceAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ActivateThisDevice", ex);
            DevicePausedErrorText.Text = ex is ColituApiException api && !ColituVpnService.IsNetworkFailure(api) ? api.Message : Loc.I["err.network"];
            DevicePausedErrorBox.IsVisible = true;
            return;
        }
        finally
        {
            SetPausedBusy(false);
        }

        DevicePausedView.IsVisible = false;
        ShowToast(Loc.I["paused.activated"]);
        await ContinueAfterPauseAsync();
    }

    /// <summary>After a plan change elsewhere (Premium bought, another device removed): try again.</summary>
    private async Task RetryAfterPauseAsync()
    {
        if (_pausedBusy)
        {
            return;
        }
        _auth.SetDevicePause(null);
        await ContinueAfterPauseAsync();
    }

    /// <summary>Reloads the account and, when the pause stopped a connection, connects again (a new pause shows the screen again).</summary>
    private async Task ContinueAfterPauseAsync()
    {
        await RefreshDataAsync();
        if (_auth.DevicePause != null)
        {
            return;
        }
        var reconnect = _pausedDuringConnect || _vpn.Preferences.AutoConnectEnabled;
        _pausedDuringConnect = false;
        if (reconnect && !_planRequired && _vpn.Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting))
        {
            await ToggleConnectionAsync();
        }
        ApplyStatus();
    }

    private void SetPausedBusy(bool busy)
    {
        _pausedBusy = busy;
        DevicePausedSpinner.IsVisible = busy;
        DevicePausedActivate.IsEnabled = DevicePausedPremium.IsEnabled = DevicePausedRetry.IsEnabled = DevicePausedSignOut.IsEnabled = !busy;
    }

    // ── End of the trial or plan ───────────────────────────────────────────
    private void ApplyPlanNotice()
    {
        var text = _vpn.PlanNoticeDismissedToday ? null : ColituPlanNotice.Build(_auth.CurrentSubscription, DateTimeOffset.UtcNow, Loc.I);
        PlanNoticeText.Text = text ?? "";
        PlanNoticeBanner.IsVisible = text != null;
    }
}
