using Avalonia.Controls.Shapes;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

public partial class ColituMainWindow
{
    private List<ColituVpnServer> _servers = [];
    private ColituStatsResponse? _usage;
    private bool _planRequired;
    private ColituVpnStatus? _shownStatus;
    private Ellipse? _orbGlow;
    private CancellationTokenSource? _orbMotion;

    // ── Orb ────────────────────────────────────────────────────────────────
    private static readonly Color Violet = Color.FromRgb(0x9F, 0x8C, 0xFF);
    private static readonly Color Blue2 = Color.FromRgb(0x94, 0x83, 0xFF);
    private static readonly Color Lilac = Color.FromRgb(0xC4, 0xB5, 0xFD);
    private static readonly Color Pink = Color.FromRgb(0xE2, 0xD6, 0xFF);

    private void WireHome()
    {
        ConnectButton.TemplateApplied += (_, e) =>
        {
            _orbGlow = e.NameScope.Find<Ellipse>("Glow");
            if (_shownStatus is { } status)
            {
                AnimateOrb(status, popped: false);
            }
        };
        ConnectButton.Click += async (_, _) => await ToggleConnectionAsync();
        UnblockButton.Click += async (_, _) =>
        {
            await _vpn.DisconnectAsync();
            ApplyStatus();
            ShowToast(Loc.I["info.disconnected"]);
        };
        LocationCard.PointerReleased += (_, _) => Navigate("locations");
        ChangeServerButton.Click += (_, _) => Navigate("locations");
        PlanCtaButton.Click += (_, _) => Navigate("plan");

        foreach (var mode in new[] { HomeModeProxy, HomeModeTun, SettingsModeProxy, SettingsModeTun })
        {
            mode.IsCheckedChanged += async (sender, _) => await ModeCheckedAsync(sender);
        }
        foreach (var box in new[] { HomeKillSwitch, SettingsKillSwitch, HomeAutoConnect, SettingsAutoConnect, SettingsDns, SettingsAdBlock, SettingsTray })
        {
            box.IsCheckedChanged += async (sender, _) => await PreferenceChangedAsync(sender);
        }
    }

    /// <summary>
    /// Drives the particle ring and the button glow like the phone power button:
    /// the ring idles when off, churns while connecting and glows when on.
    /// </summary>
    private void AnimateOrb(ColituVpnStatus status, bool popped = true)
    {
        var busy = status is ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
        var on = status == ColituVpnStatus.Connected;

        OrbParticles.Energy = on ? 0.62 : busy ? 1.0 : 0.22;
        OrbParticles.Color = on ? Violet : Blue2;
        OrbParticles.Color2 = on ? Pink : Lilac;

        _orbMotion?.Cancel();
        _orbMotion = new CancellationTokenSource();
        if (_orbGlow != null)
        {
            if (on || busy)
            {
                ColituMotion.Breathe(_orbGlow, on ? 0.55 : 0.35, 0.8, on ? 1600 : 800, _orbMotion.Token);
            }
            else
            {
                _orbGlow.Opacity = 0.28;
            }
        }

        if (popped && _shownStatus != status && on)
        {
            // A small spring when the tunnel comes up.
            ColituMotion.Pop(ConnectButton);
        }

        var dotColor = on ? Color.FromRgb(0x5E, 0xE0, 0xA0) : busy ? Color.FromRgb(0xF5, 0xC3, 0x6B) : Color.FromRgb(0xFF, 0x6B, 0x7A);
        StatusDot.Fill = new SolidColorBrush(dotColor);
        StatusDotHalo.Fill = new SolidColorBrush(dotColor);
        if (on || busy)
        {
            ColituMotion.Ripple(StatusDotHalo, _orbMotion.Token);
        }
        else
        {
            StatusDotHalo.Opacity = 0;
        }
    }

    // ── Status ─────────────────────────────────────────────────────────────
    private void ApplyStatus()
    {
        var status = _vpn.Status;
        var on = status == ColituVpnStatus.Connected;
        var busy = status is ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting;
        var blocked = !on && _vpn.KillSwitchEngaged;
        var loc = Loc.I;

        HomeTitle.Text = on ? loc["home.title.on"]
            : blocked ? loc["home.title.blocked"]
            : busy ? loc["home.title.connecting"]
            : _planRequired ? loc["home.title.noplan"]
            : loc["home.title.off"];
        HomeSubtitle.Text = on ? loc.Format("home.sub.on", ("server", ServerLabel(_vpn.ConnectedServer) ?? loc["server.auto"]))
            : blocked ? loc["home.sub.blocked"]
            : busy ? loc["home.sub.connecting"]
            : _planRequired ? loc["home.sub.noplan"]
            : status == ColituVpnStatus.Error && _vpn.LastError is { Length: > 0 } error ? error
            : loc["home.sub.off"];
        ConnectLabel.Text = on ? loc["home.tapOff"]
            : busy ? loc["home.tapCancel"]
            : _planRequired ? loc["plan.choose"]
            : loc["home.tap"];
        ToolTip.SetTip(ConnectButton, on ? loc["home.disconnect"] : busy ? loc["home.cancel"] : loc["home.connect"]);
        var protocol = on ? _vpn.ConnectedProtocol : null;
        ProtocolChip.IsVisible = protocol is { Length: > 0 };
        ProtocolChipText.Text = protocol is { Length: > 0 } ? $"{loc["home.protocol"]} · {ColituTransportNames.Of(protocol, loc)}".ToUpper(loc.Culture) : "";

        StatusChipText.Text = on ? loc["status.protected"]
            : blocked ? loc["status.blocked"]
            : status == ColituVpnStatus.Reconnecting ? loc["status.reconnecting"]
            : busy ? loc["status.connecting"]
            : loc["status.unprotected"];
        if (_trayIcon != null)
        {
            _trayIcon.ToolTipText = $"Colitu VPN · {StatusChipText.Text}";
        }
        if (_trayConnectItem != null)
        {
            _trayConnectItem.Header = on || busy || blocked ? loc["tray.disconnect"] : loc["tray.connect"];
        }
        UnblockButton.IsVisible = blocked;

        if (_shownStatus != status)
        {
            AnimateOrb(status);
            _shownStatus = status;
        }
        ApplyLocationCard();
        UpdateSessionTimer();
    }

    private void UpdateSessionTimer()
    {
        var duration = _vpn.GetConnectionDuration();
        SessionTimer.Text = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        // The button shows the session clock while connected and the power glyph otherwise.
        var on = _vpn.Status == ColituVpnStatus.Connected;
        SessionTimer.IsVisible = on;
        PowerIcon.IsVisible = !on;
    }

    private async Task ToggleConnectionAsync()
    {
        var status = _vpn.Status;
        if (status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting)
        {
            try
            {
                await _vpn.DisconnectAsync();
                ShowToast(Loc.I["info.disconnected"]);
            }
            catch (Exception ex)
            {
                Logging.SaveLog("ColituMainWindow.Disconnect", ex);
            }
            ApplyStatus();
            return;
        }

        if (_planRequired && !_offline)
        {
            Navigate("plan");
            return;
        }

        if (_vpn.NeedsSudoPassword && !await AskSudoPasswordAsync())
        {
            return;
        }

        try
        {
            await _vpn.ConnectSavedAsync();
            _ = RefreshUsageSoonAsync();
        }
        catch (ColituPlanRequiredException)
        {
            _planRequired = true;
            ApplyAccount();
            ShowToast(Loc.I["err.noPlan"], true);
            Navigate("plan");
        }
        catch (ColituSudoRequiredException)
        {
            ShowToast(Loc.I["err.admin"], true);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, true);
        }
        finally
        {
            ApplyStatus();
        }
    }

    private async Task RefreshUsageSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        try
        {
            _usage = await _vpn.GetStatsAsync() ?? _usage;
        }
        catch
        {
            // Keep the last numbers.
        }
        ApplyAccount();
    }

    // ── Location card ──────────────────────────────────────────────────────
    private void ApplyLocationCard()
    {
        var server = _vpn.Status is ColituVpnStatus.Connected && _vpn.ConnectedServer != null
            ? _vpn.ConnectedServer
            : _vpn.IsAutoSelection ? null : _vpn.SelectedServer ?? _servers.FirstOrDefault(s => s.Id == _vpn.SavedServerId);
        var row = server == null ? ColituServerRow.Auto() : ColituServerRow.From(server);
        if (_vpn.IsAutoSelection && server != null)
        {
            row.Subtitle = Loc.I["server.auto"] + " · " + row.Subtitle;
        }
        HomeFlag.Content = row;
        HomeServerTitle.Text = row.Title;
        HomeServerSubtitle.Text = row.Subtitle;
        var connected = _vpn.Status == ColituVpnStatus.Connected;
        HomeServerCheck.Background = connected ? Resource<IBrush>("SuccessBrush") : Brushes.Transparent;
        HomeServerCheck.BorderBrush = Resource<IBrush>(connected ? "SuccessBrush" : "DimBrush");
        HomeServerCheckMark.IsVisible = connected;
    }

    private static string? ServerLabel(ColituVpnServer? server) => server == null ? null : ColituServerRow.From(server).Title;

    // ── Plan card and account summary ──────────────────────────────────────
    private void ApplyAccount()
    {
        var subscription = _auth.CurrentSubscription;
        var loc = Loc.I;
        var active = subscription?.Active == true;
        var status = subscription?.Status ?? "inactive";

        PlanName.Text = active ? PlanTitle(subscription!) : loc["plan.none"];
        PlanDetail.Text = active ? PlanDetailText(subscription!) : loc["plan.noneHint"];
        SetBadge(PlanBadge, PlanBadgeText, status);

        var limit = subscription?.TrafficLimitBytes;
        var used = _usage?.Stats.TotalUsedBytes ?? subscription?.TrafficUsedBytes ?? 0;
        TrafficBlock.IsVisible = active;
        if (limit is > 0)
        {
            TrafficText.Text = loc.Format("home.trafficOf", ("used", FormatBytes(used)), ("limit", FormatBytes(limit.Value)));
            TrafficTrack.IsVisible = true;
            var ratio = Math.Clamp((double)used / limit.Value, 0, 1);
            Dispatcher.UIThread.Post(() => TrafficFill.Width = Math.Max(6, TrafficTrack.Bounds.Width * ratio), DispatcherPriority.Loaded);
        }
        else
        {
            TrafficText.Text = used > 0 ? FormatBytes(used) : loc["home.unlimited"];
            TrafficTrack.IsVisible = false;
        }

        DevicesText.Text = subscription == null ? "—" : $"{Math.Max(1, subscription.DevicesUsed)} / {Math.Max(1, subscription.DeviceLimit)}";

        var free = IsFreePlan(subscription);
        var expiresSoon = active && !free && ExpiresAt(subscription!) is { } end && end - DateTimeOffset.UtcNow < TimeSpan.FromDays(3);
        PlanCtaButton.IsVisible = !active || expiresSoon || free;
        PlanCtaText.Text = free ? loc["plan.upgrade"] : active ? loc["plan.extend"] : loc["plan.choose"];

        // Account page
        var email = _auth.CurrentUser?.Email ?? "";
        AccountEmail.Text = email;
        AvatarText.Text = email.Length >= 2 ? email[..2].ToUpperInvariant() : "C";
        AccountId.Text = _auth.CurrentUser?.Id is { Length: > 0 } id ? $"ID · {id[..Math.Min(8, id.Length)]}" : "";
        AccountPlan.Text = active ? PlanTitle(subscription!) : loc["plan.none"];
        SetBadge(AccountPlanBadge, AccountPlanBadgeText, status);
        AccountUntil.Text = free ? loc["plan.freeHint"] : ExpiresAt(subscription) is { } until ? FormatDate(until) : "—";

        // Plan page strip
        CurrentPlanText.Text = active ? $"{PlanTitle(subscription!)} · {PlanDetailText(subscription!)}" : loc["plan.none"];
        SetBadge(CurrentPlanBadge, CurrentPlanBadgeText, status);
        CreditText.Text = loc.Format("brand.credit", ("brand", "Avenlith"));
    }

    /// <summary>The free plan: 10 GB a month, renewed on the 1st.</summary>
    private static bool IsFreePlan(ColituSubscription? subscription) =>
        string.Equals(subscription?.PlanName, "Free", StringComparison.OrdinalIgnoreCase);

    private static string PlanTitle(ColituSubscription subscription)
    {
        if (IsFreePlan(subscription))
        {
            return Loc.I["plan.freeName"];
        }
        if (string.Equals(subscription.Status, "trialing", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.I["plan.trialName"];
        }
        return string.IsNullOrWhiteSpace(subscription.PlanName) ? "Colitu VPN" : subscription.PlanName!;
    }

    private static string PlanDetailText(ColituSubscription subscription)
    {
        if (IsFreePlan(subscription))
        {
            return Loc.I["plan.freeHint"];
        }
        if (ExpiresAt(subscription) is not { } end)
        {
            return "";
        }
        var left = end - DateTimeOffset.UtcNow;
        var leftText = left.TotalDays >= 1
            ? Loc.I.Count("day", (long)Math.Floor(left.TotalDays))
            : Loc.I.Count("hour", Math.Max(1, (long)Math.Ceiling(left.TotalHours)));
        return $"{Loc.I.Format("plan.until", ("date", FormatDate(end)))} · {Loc.I.Format("plan.left", ("left", leftText))}";
    }

    private static DateTimeOffset? ExpiresAt(ColituSubscription? subscription)
    {
        return DateTimeOffset.TryParse(subscription?.ExpiresAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }

    private void SetBadge(Border badge, TextBlock text, string status)
    {
        var key = status switch
        {
            "active" => "plan.status.active",
            "trialing" => "plan.status.trialing",
            "expired" => "plan.status.expired",
            "quota_exceeded" => "plan.status.quota",
            _ => "plan.status.inactive"
        };
        text.Text = Loc.I[key];
        var active = status is "active" or "trialing";
        // Lavender badge with dark text when active, rose glass otherwise (ColituBadge).
        badge.Background = active ? Resource<IBrush>("BadgeBrush") : new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x6B, 0x7A));
        text.Foreground = Resource<IBrush>(active ? "OnAccentBrush" : "DangerBrush");
    }

    private static string FormatDate(DateTimeOffset value) => value.ToLocalTime().ToString("d MMMM yyyy", Loc.I.Culture);

    private static string FormatBytes(long bytes)
    {
        string[] units = Loc.I.Language == "ru" ? ["Б", "КБ", "МБ", "ГБ", "ТБ"] : ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.#", Loc.I.Culture)} {units[unit]}";
    }

    // ── Preferences (home quick settings and the settings page) ───────────
    private bool _applyingPreferences;

    private void ApplyPreferencesToUi()
    {
        _applyingPreferences = true;
        try
        {
            var preferences = _vpn.Preferences;
            var tun = preferences.IsTunMode;
            HomeModeTun.IsChecked = SettingsModeTun.IsChecked = tun;
            HomeModeProxy.IsChecked = SettingsModeProxy.IsChecked = !tun;
            HomeKillSwitch.IsChecked = SettingsKillSwitch.IsChecked = preferences.KillSwitchEnabled;
            HomeAutoConnect.IsChecked = SettingsAutoConnect.IsChecked = preferences.AutoConnectEnabled;
            SettingsDns.IsChecked = preferences.DnsLeakProtectionEnabled;
            SettingsAdBlock.IsChecked = preferences.AdBlockEnabled;
            SettingsAdBlockRow.IsVisible = ColituVpnService.AdBlockAvailable;
            SettingsTray.IsChecked = preferences.CloseToTray;
            SettingsStartup.IsChecked = _vpn.LaunchAtStartup;
            ApplyModeHint();
        }
        finally
        {
            _applyingPreferences = false;
        }
    }

    private void ApplyModeHint()
    {
        ModeHint.Text = Loc.I[_vpn.Preferences.IsTunMode ? "settings.mode.tunHint" : "settings.mode.proxyHint"];
    }

    private async Task ModeCheckedAsync(object? sender)
    {
        if (_applyingPreferences || !IsLoaded || sender is not RadioButton { Tag: string mode, IsChecked: true })
        {
            return;
        }
        var normalized = mode == ColituConnectionModes.Tun ? ColituConnectionModes.Tun : ColituConnectionModes.Proxy;
        if (_vpn.Preferences.ConnectionMode == normalized)
        {
            return;
        }
        await SavePreferencesAsync(_vpn.Preferences with { ConnectionMode = normalized });
    }

    private async Task PreferenceChangedAsync(object? sender)
    {
        if (_applyingPreferences || !IsLoaded || sender is not CheckBox box)
        {
            return;
        }
        var on = box.IsChecked == true;
        var preferences = _vpn.Preferences;
        preferences = box == HomeKillSwitch || box == SettingsKillSwitch ? preferences with { KillSwitchEnabled = on }
            : box == HomeAutoConnect || box == SettingsAutoConnect ? preferences with { AutoConnectEnabled = on }
            : box == SettingsDns ? preferences with { DnsLeakProtectionEnabled = on }
            : box == SettingsAdBlock ? preferences with { AdBlockEnabled = on }
            : box == SettingsTray ? preferences with { CloseToTray = on }
            : preferences;
        await SavePreferencesAsync(preferences);
    }

    private async Task SavePreferencesAsync(ColituVpnPreferences preferences)
    {
        var tunnelSettingsChanged = preferences.ConnectionMode != _vpn.Preferences.ConnectionMode
            || preferences.DnsLeakProtectionEnabled != _vpn.Preferences.DnsLeakProtectionEnabled
            || preferences.AdBlockEnabled != _vpn.Preferences.AdBlockEnabled;
        var connected = _vpn.Status == ColituVpnStatus.Connected;

        // Switching a live tunnel to TUN needs the password before the reconnect.
        if (connected && preferences.IsTunMode && !_vpn.Preferences.IsTunMode
            && AppManager.Instance.LinuxSudoPwd.IsNullOrEmpty() && !await AskSudoPasswordAsync())
        {
            ApplyPreferencesToUi();
            return;
        }

        try
        {
            await _vpn.UpdatePreferencesAsync(preferences);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SavePreferencesAsync", ex);
        }
        ApplyPreferencesToUi();

        if (tunnelSettingsChanged && connected)
        {
            // Mode, DNS protection and ad blocking are part of the core config: reconnect to apply.
            try
            {
                await _vpn.ReconnectAsync();
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, true);
            }
            ApplyStatus();
        }
        else if (!tunnelSettingsChanged)
        {
            ShowToast(Loc.I["settings.saved"]);
        }
    }
}
