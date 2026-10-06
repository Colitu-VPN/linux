using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// TUN mode on Linux runs the VPN core as root through sudo, like v2rayN. The
/// password is asked for once per run, checked with "sudo -v" and kept in
/// memory only (AppManager.LinuxSudoPwd); it is never written to disk.
/// </summary>
public partial class ColituMainWindow
{
    private TaskCompletionSource<bool>? _sudoResult;

    private void WireSudo()
    {
        SudoOkButton.Click += async (_, _) => await SubmitSudoAsync();
        SudoCancelButton.Click += (_, _) => CloseSudoPrompt(false);
        SudoPasswordBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await SubmitSudoAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseSudoPrompt(false);
            }
        };
    }

    /// <summary>
    /// Shows the prompt; true once a working password is stored. <paramref name="bodyKey"/>
    /// says what the password is for (TUN mode, or changing the kill switch rules).
    /// </summary>
    private Task<bool> AskSudoPasswordAsync(string bodyKey = "sudo.body")
    {
        if (AppManager.Instance.LinuxSudoPwd.IsNotEmpty())
        {
            return Task.FromResult(true);
        }
        _sudoResult?.TrySetResult(false);
        _sudoResult = new TaskCompletionSource<bool>();
        SudoBodyText.Text = Loc.I[bodyKey];
        SudoPasswordBox.Text = "";
        SudoErrorBox.IsVisible = false;
        SudoSpinner.IsVisible = false;
        SudoOkButton.IsEnabled = true;
        SudoPrompt.IsVisible = true;
        ColituMotion.FadeIn(SudoPrompt, 8);
        ShowFromTray();
        Dispatcher.UIThread.Post(() => SudoPasswordBox.Focus(), DispatcherPriority.Input);
        return _sudoResult.Task;
    }

    private async Task SubmitSudoAsync()
    {
        var password = SudoPasswordBox.Text ?? "";
        if (password.Length == 0 || !SudoOkButton.IsEnabled)
        {
            return;
        }
        SudoOkButton.IsEnabled = false;
        SudoSpinner.IsVisible = true;
        SudoErrorBox.IsVisible = false;
        var ok = await ColituKillSwitch.VerifySudoPasswordAsync(password);
        SudoSpinner.IsVisible = false;
        SudoOkButton.IsEnabled = true;
        if (!ok)
        {
            SudoErrorBox.IsVisible = true;
            SudoPasswordBox.SelectAll();
            SudoPasswordBox.Focus();
            return;
        }
        AppManager.Instance.LinuxSudoPwd = password;
        CloseSudoPrompt(true);
    }

    private void CloseSudoPrompt(bool result)
    {
        SudoPasswordBox.Text = "";
        SudoPrompt.IsVisible = false;
        _sudoResult?.TrySetResult(result);
        _sudoResult = null;
    }
}
