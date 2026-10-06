using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

public partial class ColituMainWindow
{
    private ColituUpdateInfo? _pendingUpdate;
    private bool _updateBusy;
    private TaskCompletionSource<bool>? _confirmResult;

    private void WireAccount()
    {
        ManageAccountButton.Click += (_, _) => OpenUrl(LocalizedPath("/account"));
        // Two-factor authentication is set up on the website only.
        SecurityButton.Click += (_, _) => OpenUrl(LocalizedPath("/account/security"));
        // Config export (VLESS/Hysteria2 links for other clients) lives on the website for now.
        ManualConfigButton.Click += (_, _) => OpenUrl(ManualConfigUrl);
        SupportButton.Click += (_, _) => Navigate("support");
        MailSupportButton.Click += (_, _) => OpenUrl($"mailto:{ColituAuthService.SupportEmail}");
        SettingsStartup.IsCheckedChanged += async (_, _) => await StartupChangedAsync();
        WebsiteButton.Click += (_, _) => OpenUrl(ColituAuthService.WebBaseUrl);
        PrivacyButton.Click += (_, _) => OpenUrl(LocalizedPath("/legal/privacy"));
        TermsButton.Click += (_, _) => OpenUrl(LocalizedPath("/legal/terms"));
        LogsButton.Click += (_, _) => ColituShell.OpenFolder(Utils.GetLogPath());
        CheckUpdatesButton.Click += async (_, _) => await CheckForUpdatesAsync(quiet: false);
        UpdateButton.Click += async (_, _) =>
        {
            if (_pendingUpdate is { } info && !_updateBusy)
            {
                ShowUpdatePrompt(info);
                await InstallUpdateAsync();
            }
        };
        UpdatePromptNow.Click += async (_, _) => await InstallUpdateAsync();
        UpdatePromptLater.Click += (_, _) => UpdatePrompt.IsVisible = false;
        ConfirmOkButton.Click += (_, _) => CloseConfirm(true);
        ConfirmCancelButton.Click += (_, _) => CloseConfirm(false);
    }

    private const string ManualConfigUrl = "https://colitu.com/account/manual-config";

    // ── Devices ────────────────────────────────────────────────────────────
    private async Task LoadDevicesAsync()
    {
        if (!_auth.HasSession || !AppView.IsVisible)
        {
            return;
        }
        try
        {
            var account = await _auth.LoadAccountAsync();
            var limit = Math.Max(1, account.Subscription?.DeviceLimit ?? 1);
            DevicesTitle.Text = Loc.I.Format("account.devicesTitle", ("used", account.Devices.Count), ("limit", limit));
            DeviceList.ItemsSource = account.Devices
                .OrderByDescending(device => device.Current)
                .Select(device => new ColituDeviceRow
                {
                    Id = device.Id ?? "",
                    Name = device.Name ?? "",
                    IsCurrent = device.Current,
                    IsPaused = device.Paused,
                    Detail = string.Join(" · ", new[]
                    {
                        PlatformName(device.Platform),
                        DateTimeOffset.TryParse(device.LastActiveAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var seen)
                            ? Loc.I.Format("account.lastSeen", ("date", FormatDate(seen)))
                            : null
                    }.Where(part => !string.IsNullOrWhiteSpace(part)))
                })
                .ToList();
            ApplyAccount();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadDevicesAsync", ex);
        }
    }

    private async void ActivateDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColituDeviceRow device })
        {
            return;
        }
        try
        {
            await _auth.ActivateDeviceAsync(device.Id);
            ShowToast(Loc.I["account.activated"]);
            await LoadDevicesAsync();
            if (device.IsCurrent)
            {
                await ContinueAfterPauseAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
        }
    }

    private async void RemoveDevice_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ColituDeviceRow device })
        {
            return;
        }

        if (!await ConfirmAsync(Loc.I.Format("account.removeConfirm", ("name", device.Name))))
        {
            return;
        }

        try
        {
            if (device.IsCurrent)
            {
                await _vpn.ForgetAccountAsync();
            }
            await _auth.RemoveDeviceAsync(device.Id);
            if (!device.IsCurrent)
            {
                await LoadDevicesAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
        }
    }

    private Task<bool> ConfirmAsync(string text)
    {
        _confirmResult?.TrySetResult(false);
        _confirmResult = new TaskCompletionSource<bool>();
        ConfirmText.Text = text;
        ConfirmPrompt.IsVisible = true;
        ColituMotion.FadeIn(ConfirmPrompt, 8);
        return _confirmResult.Task;
    }

    private void CloseConfirm(bool result)
    {
        ConfirmPrompt.IsVisible = false;
        _confirmResult?.TrySetResult(result);
        _confirmResult = null;
    }

    private static string? PlatformName(string? platform) => platform?.Trim().ToLowerInvariant() switch
    {
        "windows" => "Windows",
        "ios" => "iOS",
        "android" => "Android",
        "macos" or "mac" => "macOS",
        "linux" => "Linux",
        null or "" => null,
        var other => other
    };

    // ── Settings ───────────────────────────────────────────────────────────
    private async Task StartupChangedAsync()
    {
        if (_applyingPreferences || !IsLoaded || (SettingsStartup.IsChecked == true) == _vpn.LaunchAtStartup)
        {
            return;
        }
        try
        {
            await _vpn.SetLaunchAtStartupAsync(SettingsStartup.IsChecked == true);
            ShowToast(Loc.I["settings.saved"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.StartupChanged", ex);
            _applyingPreferences = true;
            SettingsStartup.IsChecked = _vpn.LaunchAtStartup;
            _applyingPreferences = false;
            ShowToast(Loc.I["err.generic"], true);
        }
    }

    // ── Updates ────────────────────────────────────────────────────────────
    private async Task CheckForUpdatesAsync(bool quiet)
    {
        if (_updateBusy)
        {
            return;
        }
        if (!quiet)
        {
            CheckUpdatesButton.IsEnabled = false;
            CheckUpdatesText.Text = Loc.I["settings.checking"];
        }
        try
        {
            var update = await _updater.CheckForUpdateAsync(ignoreAttemptCache: !quiet);
            if (update == null && !quiet)
            {
                ShowToast(Loc.I["settings.upToDate"]);
            }
        }
        finally
        {
            CheckUpdatesButton.IsEnabled = true;
            CheckUpdatesText.Text = Loc.I["settings.checkUpdates"];
        }
    }

    private void ShowUpdate(ColituUpdateInfo info)
    {
        var first = _pendingUpdate?.VersionCode != info.VersionCode;
        _pendingUpdate = info;
        UpdateButtonText.Text = Loc.I.Format("update.available", ("version", info.NewVersion));
        UpdateButton.IsVisible = true;
        ColituMotion.FadeIn(UpdateButton, 6);
        // Ask once per start and version; "Later" keeps the title-bar button.
        if (first && !_updateBusy)
        {
            ShowUpdatePrompt(info);
        }
    }

    private void ShowUpdatePrompt(ColituUpdateInfo info)
    {
        UpdatePromptTitle.Text = Loc.I.Format("update.title", ("version", info.NewVersion));
        UpdatePromptBody.Text = Loc.I[info.Force ? "update.force" : "update.ask"];
        UpdatePromptStatus.IsVisible = !info.CanInstall;
        UpdatePromptStatus.Text = info.CanInstall ? "" : Loc.I["update.manual"];
        UpdatePromptLater.IsVisible = !info.Force;
        SetUpdatePromptBusy(false);
        UpdatePrompt.IsVisible = true;
        ColituMotion.FadeIn(UpdatePrompt, 8);
    }

    private void SetUpdatePromptBusy(bool busy)
    {
        UpdatePromptNow.IsEnabled = !busy;
        UpdatePromptLater.IsEnabled = !busy;
        UpdatePromptSpinner.IsVisible = busy;
        UpdatePromptNowText.Text = Loc.I["update.now"];
    }

    private async Task InstallUpdateAsync()
    {
        if (_pendingUpdate is not { } info || _updateBusy)
        {
            return;
        }
        if (!info.CanInstall)
        {
            // A copied folder (no package manager behind it): send the user to the download page.
            OpenUrl(ColituUpdateService.DownloadPageUrl);
            UpdatePrompt.IsVisible = false;
            return;
        }
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        SetUpdatePromptBusy(true);
        UpdatePromptStatus.Text = Loc.I["update.restart"];
        UpdatePromptStatus.IsVisible = true;
        try
        {
            var progress = new Progress<ColituDownloadProgress>(value =>
            {
                var text = Loc.I.Format("update.downloading", ("percent", value.Percent));
                UpdateButtonText.Text = text;
                UpdatePromptNowText.Text = text;
            });
            await _updater.DownloadUpdateAsync(info, progress);
            UpdateButtonText.Text = Loc.I["update.installing"];
            UpdatePromptNowText.Text = Loc.I["update.installing"];
            UpdatePromptStatus.Text = Loc.I["update.auth"];
            if (_vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting || _vpn.KillSwitchEngaged)
            {
                await _vpn.DisconnectAsync();
            }
            await _updater.InstallAsync(info);
            ColituUpdateService.RelaunchAfterExit();
            await ExitAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.InstallUpdateAsync", ex);
            ShowToast(Loc.I["update.failed"], true);
            UpdateButtonText.Text = Loc.I.Format("update.available", ("version", info.NewVersion));
            UpdatePromptStatus.Text = Loc.I["update.failed"];
        }
        finally
        {
            _updateBusy = false;
            UpdateButton.IsEnabled = true;
            SetUpdatePromptBusy(false);
        }
    }
}

public sealed class ColituDeviceRow
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool IsPaused { get; init; }
}
