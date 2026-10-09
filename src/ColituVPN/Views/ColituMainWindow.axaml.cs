using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;
using v2rayN.Desktop.Services;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The Colitu shell: one window that hosts sign-in and the signed-in app, styled
/// like colitu.com and the Windows app. Page logic lives in the partial files
/// next to this one.
/// </summary>
public partial class ColituMainWindow : Window
{
    private readonly ColituVpnService _vpn = ColituVpnService.Instance;
    private readonly ColituAuthService _auth = ColituAuthService.Instance;
    private readonly ColituUpdateService _updater = ColituUpdateService.Instance;
    private readonly DispatcherTimer _clock;
    private readonly DispatcherTimer _refresh;
    private readonly DispatcherTimer _toastTimer;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _trayConnectItem;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _started;
    private string _page = "home";
    private bool _offline;

    public ColituMainWindow()
    {
        InitializeComponent();
        ApplyLanguage(_vpn.Preferences.Language);
        Loc.I.Changed += OnLanguageChanged;

        _vpn.StatusChanged += _ => Dispatcher.UIThread.Post(ApplyStatus);
        _vpn.Notice += key => Dispatcher.UIThread.Post(() =>
        {
            ShowToast(Loc.I[key], key.StartsWith("err", StringComparison.Ordinal));
            ApplyStatus();
        });
        _auth.SessionExpired += _ => Dispatcher.UIThread.Post(async () => await GuardAsync("SessionExpired", OnSessionExpiredAsync));
        _updater.UpdateAvailable += info => Dispatcher.UIThread.Post(() => ShowUpdate(info));

        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => UpdateSessionTimer();
        _clock.Start();

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        _refresh.Tick += async (_, _) => await RefreshDataAsync();

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4.5) };
        _toastTimer.Tick += (_, _) => HideToast();

        Opened += OnOpened;
        Closing += OnClosing;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                ApplyWindowShape();
            }
        };

        WireChrome();
        WireAuth();
        WireReset();
        WireVerify();
        WireMfa();
        WireRecovery();
        WirePaused();
        WireNotices();
        WireSplit();
        WireHome();
        WireLocations();
        WirePlan();
        WireAccount();
        WireSupport();
        WireSudo();
        BuildLanguageSelectors();
        BuildTrayIcon();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
        {
            return;
        }
        _started = true;
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        await StartAsync();
    }

    private async Task StartAsync()
    {
        ShowView(LoadingView);
        _ = CheckForUpdatesAsync(quiet: true);
        await _vpn.RecoverFromPreviousRunAsync();
        await RestoreSessionAsync();
        ApplyDevicePause();
        // The kill switch of a crashed run still blocks the internet: explain why, and
        // offer to reconnect or to turn protection off.
        if (_vpn.KillSwitchRecoveryPending)
        {
            ShowKillSwitchRecovery();
        }
    }

    private async Task RestoreSessionAsync()
    {
        var state = await _auth.InitializeAsync();
        if (state == ColituStartupState.VerificationRequired)
        {
            ShowVerify(codeJustSent: false);
            return;
        }
        if (state == ColituStartupState.SignedOut)
        {
            ShowAuth();
            if (_auth.SessionWasRevoked)
            {
                ShowAuthError(Loc.I["auth.expired"]);
            }
            return;
        }

        await EnterAppAsync(state == ColituStartupState.Offline);
    }

    private async Task EnterAppAsync(bool offline)
    {
        _offline = offline;
        ShowView(AppView);
        NavHome.IsChecked = true;
        Navigate("home", animate: false);
        ApplyPreferencesToUi();
        ApplyAccount();
        ApplyStatus();
        Dispatcher.UIThread.Post(() => MoveNavThumb(), DispatcherPriority.Loaded);

        if (offline)
        {
            ShowToast(Loc.I["home.offline"], true);
            _refresh.Interval = TimeSpan.FromSeconds(20);
        }
        else
        {
            await RefreshDataAsync(includeAccount: false);
        }
        _refresh.Start();
        StartSupportPolling();

        // Once: offer to protect all traffic (TUN). Not while a crashed run's kill switch
        // waits for an answer (that prompt comes first).
        if (_vpn.ShouldOfferTun && !_vpn.KillSwitchRecoveryPending)
        {
            await OfferTunModeAsync();
        }

        // TUN auto-connect waits for the sudo password: it is never asked for at startup
        // (unless the user just chose TUN in the offer above). After a crash with the kill
        // switch on, the user decides first.
        if ((!_planRequired || offline) && !_vpn.NeedsSudoPassword && !_vpn.KillSwitchRecoveryPending)
        {
            if (await _vpn.TryAutoConnectAsync())
            {
                ApplyStatus();
            }
        }
    }

    /// <summary>Reloads the account, plan, servers and usage. Also resumes an offline session.</summary>
    private async Task RefreshDataAsync(bool includeAccount = true)
    {
        if (!_auth.HasSession)
        {
            return;
        }

        try
        {
            if (_offline)
            {
                var state = await _auth.ResumeAsync();
                if (state == ColituStartupState.SignedOut)
                {
                    await OnSessionExpiredAsync();
                    return;
                }
                if (state == ColituStartupState.VerificationRequired)
                {
                    ShowVerify(codeJustSent: false);
                    return;
                }
                if (state == ColituStartupState.Offline)
                {
                    return;
                }
                _offline = false;
                _refresh.Interval = TimeSpan.FromMinutes(5);
            }
            else if (includeAccount)
            {
                await _auth.RefreshAccountAsync();
            }

            var servers = await _vpn.GetServersAsync();
            _servers = servers.Servers;
            BuildCategoryFilter();
            _planRequired = servers.PlanRequired || _auth.CurrentSubscription?.Active != true;
            _usage = _planRequired ? null : await _vpn.GetStatsAsync();
            ApplyAccount();
            RenderServers();
            ApplyStatus();
            _ = MeasurePingsAsync();
            _ = RefreshNoticesAsync();
            if (_page == "account")
            {
                await LoadDevicesAsync();
            }
        }
        catch (ColituApiException ex) when (ex.Terminal)
        {
            await OnSessionExpiredAsync();
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex))
        {
            if (!_offline)
            {
                _offline = true;
                _refresh.Interval = TimeSpan.FromSeconds(20);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshDataAsync", ex);
        }
    }

    private async Task OnSessionExpiredAsync()
    {
        if (AuthView.IsVisible)
        {
            return;
        }
        await _vpn.ForgetAccountAsync();
        StopSupportPolling();
        ClearNotices();
        ShowAuth();
        ShowAuthError(Loc.I["auth.expired"]);
        ShowFromTray();
        if (_vpn.KillSwitchEngaged)
        {
            ShowKillSwitchRecovery();
        }
    }

    // ── Views and navigation ───────────────────────────────────────────────
    private void ShowView(Control view)
    {
        foreach (var candidate in new Control[] { LoadingView, AuthView, ResetView, VerifyView, MfaView, AppView })
        {
            candidate.IsVisible = candidate == view;
        }
        ColituMotion.FadeIn(view, 10);
    }

    private void Nav_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string page, IsChecked: true } && IsLoaded && _page != page)
        {
            Navigate(page);
        }
    }

    private void Navigate(string page, bool animate = true)
    {
        _page = page;
        Control target = page switch
        {
            "locations" => LocationsPage,
            "plan" => PlanPage,
            "account" => AccountPage,
            "settings" => SettingsPage,
            "support" => SupportPage,
            _ => HomePage
        };
        foreach (var child in Pages.Children)
        {
            child.IsVisible = child == target;
        }

        // Support has no tab of its own: it opens from the floating launcher.
        var nav = page switch
        {
            "locations" => NavLocations,
            "plan" => NavPlan,
            "account" => NavAccount,
            "settings" => NavSettings,
            "support" => null,
            _ => NavHome
        };
        if (nav == null)
        {
            foreach (var item in NavList.Children.OfType<RadioButton>())
            {
                item.IsChecked = false;
            }
        }
        else if (nav.IsChecked != true)
        {
            nav.IsChecked = true;
        }
        NavThumb.Opacity = nav == null ? 0 : 1;
        MoveNavThumb();
        if (animate)
        {
            ColituMotion.FadeIn(target, 14);
        }
        if (target == HomePage)
        {
            ColituMotion.StaggerIn(HomeCards);
        }

        switch (page)
        {
            case "locations":
                RenderServers();
                _ = MeasurePingsAsync();
                break;
            case "account":
                _ = LoadDevicesAsync();
                break;
            case "support":
                _ = OpenSupportAsync();
                break;
        }
        UpdateSupportPolling();
    }

    private void MoveNavThumb()
    {
        var active = NavList.Children.OfType<RadioButton>().FirstOrDefault(item => item.IsChecked == true);
        if (active == null || active.Bounds.Width <= 0)
        {
            return;
        }
        var x = active.TranslatePoint(new Point(0, 0), NavList)?.X ?? 0;
        NavThumb.Height = active.Bounds.Height;
        NavThumb.Width = active.Bounds.Width;
        NavThumb.RenderTransform = TransformOperations.Parse($"translateX({x.ToString(CultureInfo.InvariantCulture)}px)");
    }

    // ── Language ───────────────────────────────────────────────────────────
    private static void ApplyLanguage(string? language)
    {
        Loc.I.SetLanguage(language);
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    private void OnLanguageChanged()
    {
        var culture = Loc.I.Culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        BuildLanguageSelectors();
        VersionText.Text = Loc.I.Format("settings.version", ("version", ColituAuthService.ClientVersion));
        ApplyAuthMode();
        ApplyResetState();
        ApplyAccount();
        ApplyStatus();
        ApplyModeHint();
        BuildCategoryFilter();
        RenderServers();
        ApplyVerifyTexts();
        ApplyMfaTexts();
        if (KillSwitchRecoveryPrompt.IsVisible)
        {
            KillSwitchRecoveryBody.Text = Loc.I[_vpn.KillSwitchApplies ? "ksr.body" : "ksr.bodyOff"];
        }
        RenderSupportList();
        UpdateTrayMenu();
        _ = LoadDevicesAsync();
        if (AppView.IsVisible)
        {
            // The panel writes the notices in the app's language.
            _ = RefreshNoticesAsync();
        }
        Dispatcher.UIThread.Post(MoveNavThumb, DispatcherPriority.Loaded);
    }

    private void BuildLanguageSelectors()
    {
        foreach (var (host, group) in new (Panel, string)[] { (AuthLanguageList, "AuthLang"), (SettingsLanguageList, "SettingsLang") })
        {
            host.Children.Clear();
            foreach (var language in Loc.Languages)
            {
                var option = new RadioButton
                {
                    Content = language switch { "ru" => "Русский", "tr" => "Türkçe", _ => "English" },
                    GroupName = group,
                    Tag = language,
                    Theme = Resource<ControlTheme>("SegmentOption"),
                    IsChecked = language == Loc.I.Language,
                    MinHeight = group == "AuthLang" ? 30 : 40,
                    FontSize = group == "AuthLang" ? 12.5 : 13.5
                };
                if (group == "AuthLang")
                {
                    option.Padding = new Thickness(10, 0);
                }
                option.IsCheckedChanged += LanguageOption_Checked;
                host.Children.Add(option);
            }
        }
    }

    private void LanguageOption_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string language, IsChecked: true } || language == Loc.I.Language)
        {
            return;
        }

        // Rebuilding the selectors inside their own change event would recurse; defer it.
        Dispatcher.UIThread.Post(async () => await GuardAsync("Language", async () =>
        {
            ApplyLanguage(language);
            await _vpn.UpdatePreferencesAsync(_vpn.Preferences with { Language = language });
        }), DispatcherPriority.Background);
    }

    /// <summary>
    /// Runs an event handler's async work. An exception escaping an async void handler
    /// would end the whole app (and leave a root core or the system proxy behind);
    /// here it is logged and shown as a short error instead.
    /// </summary>
    private async Task GuardAsync(string name, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Logging.SaveLog($"ColituMainWindow.{name}", ex);
            ShowToast(Loc.I["err.generic"], true);
        }
    }

    // ── Toast and notifications ────────────────────────────────────────────
    private void ShowToast(string text, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        ToastText.Text = text;
        ToastDot.Fill = Resource<IBrush>(error ? "DangerBrush" : "SuccessBrush");
        Toast.Opacity = 1;
        Toast.RenderTransform = TransformOperations.Parse("translateY(0px)");
        _toastTimer.Stop();
        _toastTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(text.Length / 14.0, 3.5, 8));
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Opacity = 0;
        Toast.RenderTransform = TransformOperations.Parse("translateY(16px)");
    }

    /// <summary>Desktop notification through the freedesktop service (notify-send), when available.</summary>
    private static void Notify(string text)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ColituShell.SystemBinary("notify-send")) { UseShellExecute = false };
            foreach (var arg in new[] { "--app-name=Colitu VPN", "--icon=colitu-vpn", "Colitu VPN", text })
            {
                startInfo.ArgumentList.Add(arg);
            }
            using var _ = Process.Start(startInfo);
        }
        catch
        {
            // No notification daemon: the tray tooltip still shows the state.
        }
    }

    private T Resource<T>(string key) where T : class
    {
        return this.TryFindResource(key, ActualThemeVariant, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException(key);
    }

    // ── Window chrome ──────────────────────────────────────────────────────
    private const double FrameRadius = 18;

    private void WireChrome()
    {
        DragArea.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            BeginMoveDrag(e);
        };
        foreach (var (grip, edge) in new (Border, WindowEdge)[]
                 {
                     (GripLeft, WindowEdge.West), (GripRight, WindowEdge.East), (GripTop, WindowEdge.North),
                     (GripBottom, WindowEdge.South), (GripBottomRight, WindowEdge.SouthEast), (GripBottomLeft, WindowEdge.SouthWest)
                 })
        {
            grip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
                {
                    BeginResizeDrag(edge, e);
                }
            };
        }
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseButton.Click += (_, _) => Close();
        SupportLauncher.Click += (_, _) => Navigate("support");
        foreach (var nav in NavList.Children.OfType<RadioButton>())
        {
            nav.IsCheckedChanged += Nav_Checked;
        }
        NavList.SizeChanged += (_, _) => MoveNavThumb();
    }

    private void ApplyWindowShape()
    {
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowFrame.CornerRadius = new CornerRadius(maximized ? 0 : FrameRadius);
        WindowFrame.BorderThickness = new Thickness(maximized ? 0 : 1);
        foreach (var grip in new[] { GripLeft, GripRight, GripTop, GripBottom, GripBottomRight, GripBottomLeft })
        {
            grip.IsVisible = !maximized;
        }
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exiting)
        {
            return;
        }

        var keepRunning = _vpn.Preferences.CloseToTray && AppView.IsVisible && _trayIcon != null;
        e.Cancel = true;
        if (keepRunning)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                Notify(Loc.I["tray.hidden"]);
            }
            return;
        }
        _ = ExitAsync();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
    }

    /// <summary>Called when a second copy of the app is started.</summary>
    public void BringToFront() => ShowFromTray();

    // ── Tray ───────────────────────────────────────────────────────────────
    private void BuildTrayIcon()
    {
        try
        {
            _trayConnectItem = new NativeMenuItem(Loc.I["tray.connect"]);
            _trayConnectItem.Click += async (_, _) => await GuardAsync("TrayConnect", TrayConnectAsync);
            var open = new NativeMenuItem(Loc.I["tray.open"]);
            open.Click += (_, _) => ShowFromTray();
            var exit = new NativeMenuItem(Loc.I["tray.exit"]);
            exit.Click += async (_, _) => await ExitAsync();
            var menu = new NativeMenu();
            menu.Items.Add(open);
            menu.Items.Add(_trayConnectItem);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(exit);

            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://ColituVPN/Assets/Colitu/colitu-tray.ico"))),
                ToolTipText = "Colitu VPN",
                Menu = menu,
                IsVisible = true
            };
            _trayIcon.Clicked += (_, _) => ShowFromTray();
            if (Application.Current is { } app)
            {
                TrayIcon.SetIcons(app, [_trayIcon]);
            }
        }
        catch (Exception ex)
        {
            // No StatusNotifier host (plain X11 without a tray): closing the window then quits.
            Logging.SaveLog("ColituMainWindow.BuildTrayIcon", ex);
            _trayIcon = null;
        }
    }

    private void UpdateTrayMenu()
    {
        if (_trayIcon?.Menu is not { } menu)
        {
            return;
        }
        var items = menu.Items.OfType<NativeMenuItem>().ToList();
        if (items.Count >= 3)
        {
            items[0].Header = Loc.I["tray.open"];
            items[2].Header = Loc.I["tray.exit"];
        }
    }

    private async Task TrayConnectAsync()
    {
        if (!AppView.IsVisible)
        {
            ShowFromTray();
            return;
        }
        if (_vpn.KillSwitchEngaged && _vpn.Status != ColituVpnStatus.Connected)
        {
            ShowFromTray();
            await TurnOffKillSwitchBlockAsync();
            return;
        }
        if (_vpn.Status is not (ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting) && _vpn.NeedsSudoPassword)
        {
            // The password is asked for in the window.
            ShowFromTray();
        }
        await ToggleConnectionAsync();
    }

    public async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        Hide();
        try
        {
            if (_vpn.Status is ColituVpnStatus.Connected or ColituVpnStatus.Connecting or ColituVpnStatus.Reconnecting || _vpn.KillSwitchEngaged)
            {
                await _vpn.DisconnectAsync();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.ExitAsync", ex);
        }
        if (_vpn.KillSwitchEngaged)
        {
            // Rules adopted from a crashed run and no password this run: they stay (fail
            // closed). The next start explains them again.
            Notify(Loc.I["ksr.exitNotice"]);
        }
        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Dispose();
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private static void OpenUrl(string url) => ColituShell.OpenUrl(url);

    private static string LocalizedPath(string path) => $"{ColituAuthService.WebBaseUrl}{path}";
}
