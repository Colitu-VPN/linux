using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Multihop route and rotating exit IP. Home: a chip "Entry FI → Exit DE" on a double-VPN route,
/// or the current exit and the countdown to the next change while the exit rotates. Settings: the
/// "Rotating IP" card (off / 5 / 10 / 30 min and the exit-country checklist, panel: /me/rotation).
/// </summary>
public partial class ColituMainWindow
{
    // ── Home chip ──────────────────────────────────────────────────────────
    private ColituRotationStatus? _rotationStatus;
    private string? _rotationNode;
    private DateTimeOffset _rotationPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastRotationPoll = DateTimeOffset.MinValue;
    private bool _rotationPolling;
    private int _rotationEpoch;

    /// <summary>
    /// A chip under the status: "Entry FI → Exit DE" on a multihop route; with a rotating IP the
    /// current exit and the time to the next change. Runs every second with the session clock.
    /// </summary>
    private void ApplyRouteChip()
    {
        string? text = null;
        var connected = _vpn.Status == ColituVpnStatus.Connected;
        var server = connected ? _vpn.ConnectedServer : null;
        if (server is { IsMultihop: true })
        {
            ForgetRotationStatus();
            text = Loc.I.Format("multihop.home", ("entry", RouteEndText(server.Entry)), ("exit", RouteEndText(server.Exit)));
        }
        else if (connected && _vpn.RotationActive && server?.Id is { Length: > 0 } nodeId)
        {
            if (_rotationNode != nodeId)
            {
                // Another node: what the last one reported does not apply.
                _rotationNode = nodeId;
                ResetRotationStatus();
            }
            text = RotationText();
            PollRotationIfDue();
        }
        else
        {
            ForgetRotationStatus();
        }
        RouteChip.IsVisible = text != null;
        RouteChipText.Text = text?.ToUpper(Loc.I.Culture) ?? "";
    }

    private static string RouteEndText(ColituRouteEndpoint? end) => end?.Country ?? end?.Label ?? "?";

    private string? RotationText()
    {
        if (_rotationStatus is not { Active: true, CurrentExit: { } exit, NextChangeAt: { } next })
        {
            return null;
        }
        var label = exit.Country is { Length: 2 } country && !string.Equals(exit.Label, country, StringComparison.OrdinalIgnoreCase)
            ? $"{exit.Label} ({country})"
            : exit.Label;
        var left = next - DateTimeOffset.UtcNow;
        return left > TimeSpan.Zero
            ? Loc.I.Format("rotation.home", ("exit", label), ("time", ColituRotation.FormatCountdown(left)))
            : Loc.I.Format("rotation.homeDue", ("exit", label));
    }

    /// <summary>Asks for the status at the announced change time, never more often than every 60 s.</summary>
    private void PollRotationIfDue()
    {
        var now = DateTimeOffset.UtcNow;
        if (_rotationPolling || now < _rotationPollAt || now - _lastRotationPoll < TimeSpan.FromSeconds(ColituRotation.MinStatusPollSeconds))
        {
            return;
        }
        _ = PollRotationAsync();
    }

    private async Task PollRotationAsync()
    {
        _rotationPolling = true;
        _lastRotationPoll = DateTimeOffset.UtcNow;
        var epoch = _rotationEpoch;
        try
        {
            var status = await _vpn.GetRotationStatusAsync();
            if (epoch != _rotationEpoch)
            {
                return;
            }
            _rotationStatus = status;
            var now = DateTimeOffset.UtcNow;
            // Inactive (the node is not in the rotation mesh, too few exits): look again later.
            _rotationPollAt = now + (status is { Active: true } ? ColituRotation.NextPollDelay(status.NextChangeAt, now) : TimeSpan.FromMinutes(5));
        }
        catch (Exception ex)
        {
            if (epoch == _rotationEpoch)
            {
                _rotationPollAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(ColituRotation.MinStatusPollSeconds);
            }
            Logging.SaveLog("ColituMainWindow.PollRotationAsync", ex);
        }
        finally
        {
            _rotationPolling = false;
        }
    }

    /// <summary>The shown status is stale (new node, new preference): poll again as soon as allowed.</summary>
    private void ResetRotationStatus()
    {
        _rotationStatus = null;
        _rotationPollAt = DateTimeOffset.MinValue;
        _rotationEpoch++;
    }

    private void ForgetRotationStatus()
    {
        if (_rotationStatus != null || _rotationNode != null)
        {
            _rotationNode = null;
            ResetRotationStatus();
        }
    }

    // ── Settings card ──────────────────────────────────────────────────────
    private bool _rotationHooked;
    private bool _applyingRotation;
    private bool _savingRotation;
    /// <summary>The checklist while it holds a choice the panel would refuse (fewer than two countries).</summary>
    private List<string>? _rotationDraft;

    private void HookRotationUi()
    {
        if (_rotationHooked)
        {
            return;
        }
        _rotationHooked = true;
        // The preference lives on the account: read it whenever the settings open.
        SettingsPage.PropertyChanged += async (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty && e.NewValue is true && _auth.HasSession)
            {
                try
                {
                    await _vpn.LoadRotationAsync();
                    _rotationDraft = null;
                }
                catch (Exception ex)
                {
                    Logging.SaveLog("ColituMainWindow.LoadRotation", ex);
                }
                ApplyRotationUi();
            }
        };
        Loc.I.Changed += () => Dispatcher.UIThread.Post(ApplyRotationUi);
    }

    private void ApplyRotationUi()
    {
        HookRotationUi();
        var preference = _vpn.Rotation;
        // An older panel has no rotation: the card stays hidden.
        RotationCard.IsVisible = preference != null;
        if (preference == null)
        {
            return;
        }

        _applyingRotation = true;
        try
        {
            RotationIntervals.Children.Clear();
            foreach (var seconds in ColituRotation.IntervalChoices.Where(value => value == 0 || preference.Intervals.Count == 0 || preference.Intervals.Contains(value)))
            {
                var option = new RadioButton
                {
                    Content = seconds == 0 ? Loc.I["rotation.off"] : Loc.I.Format("rotation.minutes", ("n", seconds / 60)),
                    GroupName = "RotationInterval",
                    Tag = seconds,
                    Theme = Resource<ControlTheme>("SegmentOption"),
                    IsChecked = seconds == preference.IntervalSeconds,
                    MinHeight = 40,
                    FontSize = 13.5
                };
                option.IsCheckedChanged += (sender, args) => _ = GuardAsync("RotationInterval", () => RotationIntervalCheckedAsync(sender));
                RotationIntervals.Children.Add(option);
            }

            // Russia (and any country outside the default set) is listed unchecked until the user ticks it.
            RotationCountriesPanel.IsVisible = preference.Active;
            var selected = _rotationDraft ?? ColituRotation.SelectedCountries(preference);
            RotationCountries.Children.Clear();
            foreach (var country in preference.AvailableCountries
                         .Where(item => item.Exits > 0)
                         .OrderBy(item => ColituServerRow.CountryName(item.Country) ?? item.Country, StringComparer.Create(Loc.I.Culture, true)))
            {
                var box = new CheckBox
                {
                    Content = ColituServerRow.CountryName(country.Country) ?? country.Country,
                    Tag = country.Country,
                    Theme = Resource<ControlTheme>("FieldCheck"),
                    MinWidth = 170,
                    Margin = new Thickness(2, 0, 12, 10),
                    IsChecked = selected.Contains(country.Country, StringComparer.OrdinalIgnoreCase)
                };
                box.IsCheckedChanged += (sender, args) => _ = GuardAsync("RotationCountry", RotationCountryChangedAsync);
                RotationCountries.Children.Add(box);
            }
        }
        finally
        {
            _applyingRotation = false;
        }
    }

    private List<string> CheckedRotationCountries() =>
        RotationCountries.Children.OfType<CheckBox>()
            .Where(box => box.IsChecked == true && box.Tag is string)
            .Select(box => (string)box.Tag!)
            .ToList();

    private async Task RotationIntervalCheckedAsync(object? sender)
    {
        if (_applyingRotation || !IsLoaded || sender is not RadioButton { Tag: int seconds, IsChecked: true } || _vpn.Rotation is not { } preference)
        {
            return;
        }
        if (seconds == preference.IntervalSeconds)
        {
            return;
        }
        await SaveRotationAsync(seconds, _rotationDraft ?? ColituRotation.SelectedCountries(preference));
    }

    private async Task RotationCountryChangedAsync()
    {
        if (_applyingRotation || !IsLoaded || _vpn.Rotation is not { } preference)
        {
            return;
        }
        await SaveRotationAsync(preference.IntervalSeconds, CheckedRotationCountries());
    }

    /// <summary>
    /// Saves the interval and countries. Fewer than two countries is refused here the way the panel
    /// would (INVALID_PREFERENCE) and stays in the checklist until it is fixed.
    /// </summary>
    private async Task SaveRotationAsync(int seconds, List<string> selected)
    {
        if (_savingRotation || _vpn.Rotation is not { } preference)
        {
            return;
        }
        if (ColituRotation.Validate(seconds, selected, preference.AvailableCountries) != ColituRotation.Validation.Ok)
        {
            _rotationDraft = selected;
            if (seconds != preference.IntervalSeconds)
            {
                // The interval radio moved but nothing was saved: show what is stored.
                ApplyRotationUi();
            }
            ShowRotationNote(Loc.I["rotation.err.few"]);
            return;
        }

        _savingRotation = true;
        RotationNote.IsVisible = false;
        try
        {
            // Off keeps the countries already stored; the default set is sent as an empty list.
            var countries = seconds == 0 ? preference.Countries : ColituRotation.PayloadCountries(selected, preference.AvailableCountries);
            var saved = await _vpn.SaveRotationAsync(seconds, countries);
            _rotationDraft = null;
            ResetRotationStatus();
            ShowToast(Loc.I["settings.saved"]);
            ApplyRotationUi();

            // A connection already running on Hysteria2 (or another non-VLESS transport) cannot rotate:
            // reconnect so the panel's VLESS settings are used.
            if (saved.Active && _vpn.Status == ColituVpnStatus.Connected && !_vpn.OnMultihopRoute
                && !ColituApiClient.IsVless(_vpn.ConnectedProtocol))
            {
                await _vpn.ReconnectAsync();
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "INVALID_PREFERENCE")
        {
            _rotationDraft = selected;
            ShowRotationNote(Loc.I["rotation.err.few"]);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SaveRotationAsync", ex);
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.generic"], true);
            ApplyRotationUi();
        }
        finally
        {
            _savingRotation = false;
            ApplyStatus();
        }
    }

    private void ShowRotationNote(string message)
    {
        RotationNoteText.Text = message;
        RotationNote.IsVisible = true;
    }
}
