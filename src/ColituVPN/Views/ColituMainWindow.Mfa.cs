using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Second sign-in step for accounts with two-factor authentication. Turning 2FA on
/// and off happens on colitu.com only; the app just asks for the code: six digits
/// from the authenticator app, or one of the recovery codes.
/// </summary>
public partial class ColituMainWindow
{
    /// <summary>Leaves a few seconds for the request itself before the challenge runs out.</summary>
    private static readonly TimeSpan MfaExpiryMargin = TimeSpan.FromSeconds(5);
    private string? _mfaToken;
    private string _mfaEmail = "";
    private DateTime _mfaExpiresAt;
    private bool _mfaRecoveryMode;
    private bool _mfaByEmail;
    private bool _mfaBusy;
    private string? _mfaLastAutoCode;

    private void WireMfa()
    {
        MfaCodeBox.TextChanged += async (_, _) => await MfaCodeChangedAsync();
        MfaCodeBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitMfaAsync();
            }
            else if (e.Key == Key.Escape && !_mfaBusy)
            {
                e.Handled = true;
                LeaveMfa(null);
            }
        };
        MfaSubmitButton.Click += async (_, _) => await SubmitMfaAsync();
        MfaRecoveryButton.Click += (_, _) => SetMfaRecoveryMode(!_mfaRecoveryMode);
        MfaBackButton.Click += (_, _) => LeaveMfa(null);
    }

    private void ShowMfa(ColituAuthResult challenge)
    {
        _mfaToken = challenge.MfaToken;
        _mfaEmail = challenge.Message ?? EmailBox.Text?.Trim() ?? "";
        _mfaExpiresAt = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(30, challenge.MfaExpiresInSeconds)) - MfaExpiryMargin;
        _mfaByEmail = challenge.MfaByEmail;
        ShowView(MfaView);
        SetMfaBusy(false);
        SetMfaRecoveryMode(false);
    }

    private void ApplyMfaTexts()
    {
        if (_mfaByEmail)
        {
            // Unfamiliar-country sign-in: the code came by e-mail, so no recovery codes are offered.
            MfaTitle.Text = Loc.I["mfa.loginEmailTitle"];
            MfaSubtitle.Text = Loc.I["mfa.loginEmailBody"];
            MfaCodeLabel.Text = Loc.I["mfa.codeEmail"];
            MfaRecoveryButton.IsVisible = false;
            return;
        }
        MfaTitle.Text = Loc.I["mfa.title"];
        MfaRecoveryButton.IsVisible = true;
        MfaSubtitle.Text = Loc.I.Format("mfa.sub", ("email", _mfaEmail));
        MfaCodeLabel.Text = Loc.I[_mfaRecoveryMode ? "mfa.recoveryLabel" : "mfa.code"];
        MfaRecoveryText.Text = Loc.I[_mfaRecoveryMode ? "mfa.useApp" : "mfa.useRecovery"];
    }

    /// <summary>Recovery codes are free text (letters, dashes); app codes are six digits.</summary>
    private void SetMfaRecoveryMode(bool recovery)
    {
        _mfaRecoveryMode = recovery;
        _mfaLastAutoCode = null;
        MfaCodeBox.Text = "";
        MfaCodeBox.FontSize = recovery ? 20 : 30;
        MfaRecoveryHint.IsVisible = recovery;
        HideMfaError();
        ApplyMfaTexts();
        Dispatcher.UIThread.Post(() => MfaCodeBox.Focus(), DispatcherPriority.Input);
    }

    /// <summary>
    /// Digits only (a pasted "123 456" or "123-456" becomes 123456); the code is sent as
    /// soon as the sixth digit is in.
    /// </summary>
    private async Task MfaCodeChangedAsync()
    {
        if (_mfaRecoveryMode)
        {
            return;
        }
        KeepDigits(MfaCodeBox);
        if (!MfaView.IsVisible || _mfaBusy)
        {
            return;
        }
        var code = MfaCodeBox.Text ?? "";
        if (code.Length == 6 && code != _mfaLastAutoCode)
        {
            _mfaLastAutoCode = code;
            await SubmitMfaAsync();
        }
    }

    private async Task SubmitMfaAsync()
    {
        if (_mfaBusy || _mfaToken == null)
        {
            return;
        }

        var text = MfaCodeBox.Text ?? "";
        var code = _mfaRecoveryMode ? text.Trim() : new string(text.Where(char.IsAsciiDigit).ToArray());
        if (_mfaRecoveryMode && code.Length == 0)
        {
            ShowMfaError(Loc.I["mfa.err.recoveryEmpty"]);
            return;
        }
        if (!_mfaRecoveryMode && code.Length != 6)
        {
            ShowMfaError(Loc.I["mfa.err.length"]);
            return;
        }
        if (DateTime.UtcNow >= _mfaExpiresAt)
        {
            LeaveMfa(Loc.I["mfa.err.expired"]);
            return;
        }

        SetMfaBusy(true);
        HideMfaError();
        try
        {
            var result = await _auth.CompleteMfaLoginAsync(_mfaToken, code, _mfaEmail);
            if (!result.Success || result.RequiresMfa)
            {
                if (result.ErrorCode is "MFA_TOKEN_EXPIRED")
                {
                    // The challenge is gone: start again from the password.
                    LeaveMfa(result.Error ?? Loc.I["mfa.err.expired"]);
                    return;
                }
                // MFA_INVALID_CODE, RATE_LIMITED, network: stay on this step.
                ShowMfaError(result.Error ?? Loc.I["err.generic"]);
                MfaCodeBox.SelectAll();
                MfaCodeBox.Focus();
                return;
            }

            _mfaToken = null;
            await ContinueSignInAsync(result, registered: false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SubmitMfaAsync", ex);
            ShowMfaError(Loc.I["err.network"]);
        }
        finally
        {
            SetMfaBusy(false);
        }
    }

    /// <summary>Back to the password form, with <paramref name="error"/> shown there when given.</summary>
    private void LeaveMfa(string? error)
    {
        _mfaToken = null;
        MfaCodeBox.Text = "";
        AuthLoginTab.IsChecked = true;
        ShowAuth();
        if (error != null)
        {
            ShowAuthError(error);
        }
    }

    private void SetMfaBusy(bool busy)
    {
        _mfaBusy = busy;
        MfaSubmitButton.IsEnabled = !busy;
        MfaCodeBox.IsEnabled = !busy;
        MfaRecoveryButton.IsEnabled = !busy;
        MfaBackButton.IsEnabled = !busy;
        MfaSpinner.IsVisible = busy;
    }

    private void ShowMfaError(string message)
    {
        MfaErrorText.Text = message;
        MfaErrorBox.IsVisible = true;
        ColituMotion.FadeIn(MfaErrorBox, 6);
    }

    private void HideMfaError() => MfaErrorBox.IsVisible = false;
}
