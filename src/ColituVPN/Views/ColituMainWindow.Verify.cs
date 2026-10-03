using System.Net;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

public partial class ColituMainWindow
{
    private const int ResendCooldownSeconds = 60;
    private static readonly TimeSpan VerifyPollInterval = TimeSpan.FromSeconds(20);
    private DispatcherTimer? _resendTimer;
    private DispatcherTimer? _verifyPoll;
    private DateTime _lastVerifyCheck;
    private string? _lastAutoCode;
    private int _resendSeconds;
    private bool _verifyBusy;

    private void WireVerify()
    {
        VerifyCodeBox.TextChanged += async (_, _) => await VerifyCodeChangedAsync();
        VerifyCodeBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitVerificationAsync();
            }
        };
        VerifySubmitButton.Click += async (_, _) => await SubmitVerificationAsync();
        VerifyResendButton.Click += async (_, _) => await ResendVerificationAsync();
        VerifyOtherButton.Click += async (_, _) =>
        {
            _resendTimer?.Stop();
            StopVerifyWatch();
            await _auth.LogoutAsync();
            AuthLoginTab.IsChecked = true;
            ShowAuth();
        };
        Activated += async (_, _) =>
        {
            if (VerifyVisible)
            {
                await CheckVerifiedElsewhereAsync();
            }
        };
    }

    /// <summary>Shows the six-digit code screen for an account that still has to confirm its e-mail.</summary>
    private void ShowVerify(bool codeJustSent)
    {
        _refresh.Stop();
        StopSupportPolling();
        ShowView(VerifyView);
        _lastAutoCode = null;
        VerifyCodeBox.Text = "";
        HideVerifyError();
        ApplyVerifyTexts();
        StartResendCooldown(codeJustSent ? ResendCooldownSeconds : 0);
        StartVerifyWatch();
        Dispatcher.UIThread.Post(() => VerifyCodeBox.Focus(), DispatcherPriority.Input);
    }

    private bool VerifyVisible => VerifyView.IsVisible;

    /// <summary>
    /// The code can also be entered on colitu.com. While this screen is open the
    /// app checks every 20 seconds, and whenever the window comes back to the
    /// front, whether the address is confirmed, and then carries on by itself.
    /// </summary>
    private void StartVerifyWatch()
    {
        if (_verifyPoll == null)
        {
            _verifyPoll = new DispatcherTimer { Interval = VerifyPollInterval };
            _verifyPoll.Tick += async (_, _) => await CheckVerifiedElsewhereAsync();
        }
        _lastVerifyCheck = DateTime.UtcNow;
        _verifyPoll.Start();
    }

    private void StopVerifyWatch() => _verifyPoll?.Stop();

    private async Task CheckVerifiedElsewhereAsync()
    {
        if (!VerifyVisible || !_auth.HasSession)
        {
            StopVerifyWatch();
            return;
        }
        if (_verifyBusy || DateTime.UtcNow - _lastVerifyCheck < TimeSpan.FromSeconds(5))
        {
            return;
        }
        _lastVerifyCheck = DateTime.UtcNow;

        _verifyBusy = true;
        try
        {
            if (await _auth.TryCompleteVerificationAsync() && VerifyVisible)
            {
                await FinishVerificationAsync();
            }
        }
        catch (ColituApiException ex) when (ex.Terminal || ex.ErrorCode is "SESSION_EXPIRED")
        {
            StopVerifyWatch();
            await _auth.LogoutAsync();
            ShowAuth();
            ShowAuthError(Loc.I["auth.expired"]);
        }
        catch (ColituApiException ex) when ((int)ex.StatusCode is >= 400 and < 500 && ex.StatusCode != HttpStatusCode.TooManyRequests)
        {
            // Confirmed, but this computer could not be added (device limit,
            // trial already used here, no plan): same outcome as typing the code.
            StopVerifyWatch();
            await _auth.LogoutAsync();
            ShowAuth();
            ShowAuthError(ex.Message);
        }
        catch (Exception ex)
        {
            // Offline or a server hiccup: the next tick tries again quietly.
            Logging.SaveLog("ColituMainWindow.CheckVerifiedElsewhereAsync", ex);
        }
        finally
        {
            _verifyBusy = false;
        }
    }

    private async Task FinishVerificationAsync()
    {
        StopVerifyWatch();
        _resendTimer?.Stop();
        await EnterAppAsync(offline: false);
        var subscription = _auth.CurrentSubscription;
        ShowToast(subscription?.Active == true
            ? Loc.I["verify.done"] + " " + PlanTitle(subscription) + " · " + PlanDetailText(subscription)
            : Loc.I["verify.done"]);
    }

    /// <summary>Keeps digits only and sends the code as soon as the sixth digit is in.</summary>
    private async Task VerifyCodeChangedAsync()
    {
        KeepDigits(VerifyCodeBox);
        if (!VerifyVisible || _verifyBusy)
        {
            return;
        }
        var code = VerifyCodeBox.Text ?? "";
        if (code.Length == 6 && code != _lastAutoCode)
        {
            _lastAutoCode = code;
            await SubmitVerificationAsync();
        }
    }

    private void ApplyVerifyTexts()
    {
        VerifySubtitle.Text = Loc.I.Format("verify.sub", ("email", _auth.PendingEmail ?? _auth.CurrentUser?.Email ?? ""));
        UpdateResendText();
    }

    private async Task SubmitVerificationAsync()
    {
        if (_verifyBusy)
        {
            return;
        }

        var code = new string((VerifyCodeBox.Text ?? "").Where(char.IsAsciiDigit).ToArray());
        if (code.Length != 6)
        {
            ShowVerifyError(Loc.I["verify.err.length"]);
            return;
        }

        SetVerifyBusy(true);
        HideVerifyError();
        try
        {
            await _auth.VerifyEmailAsync(code);
            await FinishVerificationAsync();
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "VERIFICATION_CODE_INVALID" or "VERIFICATION_CODE_EXPIRED" or "RATE_LIMITED" or "EMAIL_NOT_VERIFIED")
        {
            ShowVerifyError(ex.Message);
            VerifyCodeBox.SelectAll();
            VerifyCodeBox.Focus();
        }
        catch (ColituApiException ex) when (ex.Terminal || ex.ErrorCode is "SESSION_EXPIRED")
        {
            StopVerifyWatch();
            await _auth.LogoutAsync();
            ShowAuth();
            ShowAuthError(Loc.I["auth.expired"]);
        }
        catch (ColituApiException ex)
        {
            // The address is confirmed, but this computer could not be added
            // (device limit, trial already used here, no plan): explain it on the
            // sign-in screen where the user can pick another account.
            StopVerifyWatch();
            await _auth.LogoutAsync();
            ShowAuth();
            ShowAuthError(ex.Message);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SubmitVerificationAsync", ex);
            ShowVerifyError(Loc.I["err.network"]);
        }
        finally
        {
            SetVerifyBusy(false);
        }
    }

    private async Task ResendVerificationAsync()
    {
        if (_resendSeconds > 0 || _verifyBusy)
        {
            return;
        }

        VerifyResendButton.IsEnabled = false;
        HideVerifyError();
        try
        {
            await _auth.SendVerificationCodeAsync();
            ShowToast(Loc.I["verify.sent"]);
            StartResendCooldown(ResendCooldownSeconds);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "VERIFICATION_RATE_LIMITED")
        {
            ShowVerifyError(ex.Message);
            StartResendCooldown(ResendCooldownSeconds);
        }
        catch (ColituApiException ex) when (ex.Terminal || ex.ErrorCode is "SESSION_EXPIRED")
        {
            await _auth.LogoutAsync();
            ShowAuth();
            ShowAuthError(Loc.I["auth.expired"]);
        }
        catch (Exception ex)
        {
            ShowVerifyError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
            StartResendCooldown(0);
        }
    }

    private void StartResendCooldown(int seconds)
    {
        _resendSeconds = seconds;
        if (_resendTimer == null)
        {
            _resendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resendTimer.Tick += (_, _) =>
            {
                if (--_resendSeconds <= 0)
                {
                    _resendSeconds = 0;
                    _resendTimer.Stop();
                }
                UpdateResendText();
            };
        }
        if (seconds > 0)
        {
            _resendTimer.Start();
        }
        else
        {
            _resendTimer.Stop();
        }
        UpdateResendText();
    }

    private void UpdateResendText()
    {
        VerifyResendText.Text = _resendSeconds > 0
            ? Loc.I.Format("verify.resendIn", ("n", _resendSeconds))
            : Loc.I["verify.resend"];
        VerifyResendButton.IsEnabled = _resendSeconds == 0 && !_verifyBusy;
        VerifyResendButton.Opacity = VerifyResendButton.IsEnabled ? 1 : 0.55;
    }

    private void SetVerifyBusy(bool busy)
    {
        _verifyBusy = busy;
        VerifySubmitButton.IsEnabled = !busy;
        VerifyCodeBox.IsEnabled = !busy;
        VerifySpinner.IsVisible = busy;
        UpdateResendText();
    }

    private void ShowVerifyError(string message)
    {
        VerifyErrorText.Text = message;
        VerifyErrorBox.IsVisible = true;
        ColituMotion.FadeIn(VerifyErrorBox, 6);
    }

    private void HideVerifyError() => VerifyErrorBox.IsVisible = false;
}
