using System.Net.Mail;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

public partial class ColituMainWindow
{
    private const int MinPasswordLength = 10;
    private bool _registerMode;
    private bool _passwordVisible;
    private bool _authBusy;

    private void WireAuth()
    {
        AuthLoginTab.IsCheckedChanged += AuthTab_Checked;
        AuthRegisterTab.IsCheckedChanged += AuthTab_Checked;
        foreach (var box in new[] { EmailBox, PasswordBox, PasswordRepeatBox })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    await SubmitAuthAsync();
                }
            };
        }
        AuthSubmitButton.Click += async (_, _) => await SubmitAuthAsync();
        ShowPasswordButton.Click += (_, _) => TogglePasswordVisibility();
        ForgotButton.Click += (_, _) => ShowReset(EmailBox.Text?.Trim() ?? "");
        TermsLink.Click += (_, _) => OpenUrl(LocalizedPath("/legal/terms"));
        SignOutButton.Click += async (_, _) => await GuardAsync("SignOut", SignOutAsync);
        ApplyAuthMode();
    }

    private void ShowAuth()
    {
        _refresh.Stop();
        ShowView(AuthView);
        PasswordBox.Text = "";
        PasswordRepeatBox.Text = "";
        HideAuthError();
        ApplyAuthMode();
        Dispatcher.UIThread.Post(() => (string.IsNullOrWhiteSpace(EmailBox.Text) ? EmailBox : PasswordBox).Focus(), DispatcherPriority.Input);
    }

    private void AuthTab_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { IsChecked: true })
        {
            return;
        }
        _registerMode = sender == AuthRegisterTab;
        HideAuthError();
        ApplyAuthMode();
        if (IsLoaded)
        {
            ColituMotion.FadeIn(AuthCard, 8);
        }
    }

    private void ApplyAuthMode()
    {
        AuthTitle.Text = Loc.I[_registerMode ? "auth.registerTitle" : "auth.loginTitle"];
        AuthSubtitle.Text = Loc.I[_registerMode ? "auth.registerSub" : "auth.loginSub"];
        AuthSubmitText.Text = Loc.I[_registerMode ? "auth.submitRegister" : "auth.submitLogin"];
        RegisterFields.IsVisible = _registerMode;
        ForgotButton.IsVisible = !_registerMode;
    }

    private void TogglePasswordVisibility()
    {
        _passwordVisible = !_passwordVisible;
        PasswordBox.RevealPassword = _passwordVisible;
        ShowPasswordButton.Opacity = _passwordVisible ? 1 : 0.8;
        PasswordBox.Focus();
    }

    private async Task SubmitAuthAsync()
    {
        if (_authBusy)
        {
            return;
        }

        var email = EmailBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        var error = ValidateAuth(email, password);
        if (error != null)
        {
            ShowAuthError(error);
            return;
        }

        SetAuthBusy(true);
        HideAuthError();
        try
        {
            var result = _registerMode
                ? await _auth.RegisterAsync("", email, password)
                : await _auth.LoginAsync(email, password);
            if (!result.Success)
            {
                ShowAuthError(result.Error ?? Loc.I["err.generic"]);
                return;
            }

            await ContinueSignInAsync(result, _registerMode);
        }
        catch (Exception ex)
        {
            ShowAuthError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetAuthBusy(false);
        }
    }

    /// <summary>
    /// What follows an accepted sign-in (password, 2FA code or registration): the 2FA
    /// step, the e-mail confirmation, or the app.
    /// </summary>
    private async Task ContinueSignInAsync(ColituAuthResult result, bool registered)
    {
        PasswordBox.Text = "";
        PasswordRepeatBox.Text = "";
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
        if (registered && _auth.CurrentSubscription is { Active: true } subscription)
        {
            ShowToast(PlanTitle(subscription) + " · " + PlanDetailText(subscription));
        }
    }

    private string? ValidateAuth(string email, string password)
    {
        if (!IsEmail(email))
        {
            return Loc.I["auth.err.email"];
        }
        if (password.Length < MinPasswordLength && _registerMode)
        {
            return Loc.I["auth.err.password"];
        }
        if (password.Length == 0)
        {
            return Loc.I["auth.err.password"];
        }
        if (_registerMode && PasswordRepeatBox.Text != password)
        {
            return Loc.I["auth.err.mismatch"];
        }
        if (_registerMode && TermsCheck.IsChecked != true)
        {
            return Loc.I["auth.err.terms"];
        }
        return null;
    }

    private static bool IsEmail(string value)
    {
        if (value.Length is < 3 or > 254 || !value.Contains('@'))
        {
            return false;
        }
        try
        {
            return new MailAddress(value).Address == value;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void SetAuthBusy(bool busy)
    {
        _authBusy = busy;
        AuthSubmitButton.IsEnabled = !busy;
        AuthSpinner.IsVisible = busy;
        EmailBox.IsEnabled = PasswordBox.IsEnabled = PasswordRepeatBox.IsEnabled = !busy;
        AuthLoginTab.IsEnabled = AuthRegisterTab.IsEnabled = !busy;
    }

    private void ShowAuthError(string message)
    {
        AuthErrorText.Text = message;
        AuthErrorBox.IsVisible = true;
        ColituMotion.FadeIn(AuthErrorBox, 6);
    }

    private void HideAuthError() => AuthErrorBox.IsVisible = false;

    private async Task SignOutAsync()
    {
        await _vpn.ForgetAccountAsync();
        StopSupportPolling();
        await _auth.LogoutAsync();
        _servers = [];
        _usage = null;
        _planRequired = false;
        AuthLoginTab.IsChecked = true;
        ShowAuth();
        // Rules kept from a crashed run need the password to go; say why there is no internet.
        if (_vpn.KillSwitchEngaged)
        {
            ShowKillSwitchRecovery();
        }
    }
}
