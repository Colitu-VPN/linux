using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// "Forgot password": the address gets a six-digit code, and the code plus a
/// new password sign this computer in. The panel ends every other session.
/// </summary>
public partial class ColituMainWindow
{
    private DispatcherTimer? _resetTimer;
    private int _resetSeconds;
    private bool _resetCodeSent;
    private bool _resetBusy;

    private void WireReset()
    {
        foreach (var box in new[] { ResetEmailBox, ResetCodeBox, ResetPasswordBox, ResetRepeatBox })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await SubmitResetAsync();
                }
            };
        }
        ResetCodeBox.TextChanged += (_, _) => KeepDigits(ResetCodeBox);
        ResetSubmitButton.Click += async (_, _) => await SubmitResetAsync();
        ResetResendButton.Click += async (_, _) =>
        {
            if (_resetSeconds <= 0)
            {
                await SendResetCodeAsync();
            }
        };
        ResetChangeEmailButton.Click += (_, _) =>
        {
            _resetCodeSent = false;
            ResetCodeBox.Text = "";
            HideResetMessages();
            ApplyResetState();
            ResetEmailBox.Focus();
        };
        ResetBackButton.Click += (_, _) =>
        {
            _resetTimer?.Stop();
            var email = ResetEmailBox.Text?.Trim() ?? "";
            if (IsEmail(email))
            {
                EmailBox.Text = email;
            }
            AuthLoginTab.IsChecked = true;
            ShowAuth();
        };
    }

    /// <summary>Codes pasted from the e-mail may carry spaces or a dash ("123 456"): keep six digits.</summary>
    private static void KeepDigits(TextBox box)
    {
        var text = box.Text ?? "";
        var digits = new string(text.Where(char.IsAsciiDigit).Take(6).ToArray());
        if (digits != text)
        {
            box.Text = digits;
            box.CaretIndex = digits.Length;
        }
    }

    private void ShowReset(string email)
    {
        _refresh.Stop();
        ShowView(ResetView);
        ResetEmailBox.Text = email;
        ResetCodeBox.Text = "";
        ResetPasswordBox.Text = "";
        ResetRepeatBox.Text = "";
        _resetCodeSent = false;
        HideResetMessages();
        ApplyResetState();
        Dispatcher.UIThread.Post(() => ResetEmailBox.Focus(), DispatcherPriority.Input);
    }

    private void ApplyResetState()
    {
        ResetSubtitle.Text = Loc.I[_resetCodeSent ? "reset.codeSub" : "reset.sub"];
        ResetSubmitText.Text = Loc.I[_resetCodeSent ? "reset.submit" : "reset.send"];
        ResetCodeFields.IsVisible = ResetResendButton.IsVisible = ResetChangeEmailButton.IsVisible = ResetNote.IsVisible = _resetCodeSent;
        ResetEmailBox.IsEnabled = !_resetCodeSent && !_resetBusy;
        UpdateResetResendText();
    }

    private async Task SubmitResetAsync()
    {
        if (_resetCodeSent)
        {
            await ChangePasswordWithCodeAsync();
        }
        else
        {
            await SendResetCodeAsync();
        }
    }

    private async Task SendResetCodeAsync()
    {
        if (_resetBusy)
        {
            return;
        }
        var email = ResetEmailBox.Text?.Trim() ?? "";
        if (!IsEmail(email))
        {
            ShowResetError(Loc.I["auth.err.email"]);
            return;
        }
        SetResetBusy(true);
        HideResetMessages();
        try
        {
            await _auth.RequestPasswordResetAsync(email);
            _resetCodeSent = true;
            StartResetCooldown();
            ApplyResetState();
            ShowResetInfo(Loc.I.Format("reset.sent", ("email", email)));
            Dispatcher.UIThread.Post(() => ResetCodeBox.Focus(), DispatcherPriority.Input);
        }
        catch (ColituApiException ex)
        {
            ShowResetError(ex.Message);
            if (ex.ErrorCode is "VERIFICATION_RATE_LIMITED")
            {
                _resetCodeSent = true;
                StartResetCooldown();
                ApplyResetState();
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SendResetCode", ex);
            ShowResetError(Loc.I["err.network"]);
        }
        finally
        {
            SetResetBusy(false);
        }
    }

    private async Task ChangePasswordWithCodeAsync()
    {
        if (_resetBusy)
        {
            return;
        }
        var code = new string((ResetCodeBox.Text ?? "").Where(char.IsAsciiDigit).ToArray());
        var password = ResetPasswordBox.Text ?? "";
        var error = code.Length != 6 ? Loc.I["verify.err.length"]
            : password.Length < MinPasswordLength ? Loc.I["auth.err.password"]
            : ResetRepeatBox.Text != password ? Loc.I["auth.err.mismatch"]
            : null;
        if (error != null)
        {
            ShowResetError(error);
            return;
        }
        SetResetBusy(true);
        HideResetMessages();
        try
        {
            var email = ResetEmailBox.Text?.Trim() ?? "";
            var result = await _auth.ResetPasswordAsync(email, code, password);
            if (!result.Success)
            {
                ShowResetError(result.Error ?? Loc.I["err.generic"]);
                return;
            }
            ResetPasswordBox.Text = "";
            ResetRepeatBox.Text = "";
            ResetCodeBox.Text = "";
            _resetTimer?.Stop();
            EmailBox.Text = email;
            if (result.RequiresMfa)
            {
                ShowMfa(result);
                return;
            }
            if (result.RequiresEmailVerification)
            {
                ShowVerify(codeJustSent: true);
                return;
            }
            await EnterAppAsync(offline: false);
            ShowToast(Loc.I["reset.done"]);
        }
        catch (Exception ex)
        {
            ShowResetError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetResetBusy(false);
        }
    }

    private void StartResetCooldown()
    {
        _resetSeconds = ResendCooldownSeconds;
        if (_resetTimer == null)
        {
            _resetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _resetTimer.Tick += (_, _) =>
            {
                if (--_resetSeconds <= 0)
                {
                    _resetTimer.Stop();
                }
                UpdateResetResendText();
            };
        }
        _resetTimer.Start();
        UpdateResetResendText();
    }

    private void UpdateResetResendText()
    {
        ResetResendText.Text = _resetSeconds > 0 ? Loc.I.Format("verify.resendIn", ("n", _resetSeconds)) : Loc.I["verify.resend"];
        ResetResendButton.IsEnabled = _resetSeconds <= 0 && !_resetBusy;
    }

    private void SetResetBusy(bool busy)
    {
        _resetBusy = busy;
        ResetSubmitButton.IsEnabled = !busy;
        ResetSpinner.IsVisible = busy;
        ResetCodeBox.IsEnabled = ResetPasswordBox.IsEnabled = ResetRepeatBox.IsEnabled = !busy;
        ResetEmailBox.IsEnabled = !busy && !_resetCodeSent;
        UpdateResetResendText();
    }

    private void ShowResetError(string message)
    {
        ResetInfoBox.IsVisible = false;
        ResetErrorText.Text = message;
        ResetErrorBox.IsVisible = true;
        ColituMotion.FadeIn(ResetErrorBox, 6);
    }

    private void ShowResetInfo(string message)
    {
        ResetInfoText.Text = message;
        ResetInfoBox.IsVisible = true;
        ColituMotion.FadeIn(ResetInfoBox, 6);
    }

    private void HideResetMessages()
    {
        ResetErrorBox.IsVisible = false;
        ResetInfoBox.IsVisible = false;
    }
}
