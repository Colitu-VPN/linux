using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Plan page, as on iOS, Android and Windows. Nothing is bought in the app:
/// the subscription is managed in the customer account on app.colitu.com. The
/// page shows the current plan, opens the account and refreshes the status
/// after the user changed something there.
/// </summary>
public partial class ColituMainWindow
{
    private const string AccountPortalUrl = "https://app.colitu.com";

    /// <summary>The Linux app is published under GPL-3.0; linked from About.</summary>
    private const string SourceCodeUrl = "https://github.com/cyberlexs/colitu-linux";

    private bool _refreshingPlan;

    private void WirePlan()
    {
        ManagePlanButton.Click += (_, _) => OpenUrl(AccountPortalUrl);
        PlanRefreshButton.Click += async (_, _) => await RefreshPlanAsync();
        SourceCodeButton.Click += (_, _) => OpenUrl(SourceCodeUrl);
    }

    private async Task RefreshPlanAsync()
    {
        if (_refreshingPlan)
        {
            return;
        }
        _refreshingPlan = true;
        PlanRefreshButton.IsEnabled = false;
        PlanRefreshSpinner.IsVisible = true;
        try
        {
            await RefreshDataAsync();
            ShowToast(Loc.I["plan.refreshed"]);
        }
        finally
        {
            PlanRefreshSpinner.IsVisible = false;
            PlanRefreshButton.IsEnabled = true;
            _refreshingPlan = false;
        }
    }
}
