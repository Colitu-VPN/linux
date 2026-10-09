using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// The notice banner at the top of the home cards: the first notice from the panel
/// that this user has not closed. Fetched with the periodic data refresh, at most
/// every 15 minutes.
/// </summary>
public partial class ColituMainWindow
{
    private List<ColituNotice> _notices = [];
    private ColituNotice? _shownNotice;
    private DateTimeOffset _noticesFetchedAt = DateTimeOffset.MinValue;
    private string? _noticesLanguage;
    private bool _noticesFetching;
    private int _noticesEpoch;

    private static readonly string[] NoticeBoxClasses = ["neutralBox", "infoBox", "warnBox", "errorBox"];

    private void WireNotices()
    {
        NoticeAction.Click += (_, _) => OnNoticeAction();
        NoticeClose.Click += (_, _) => OnNoticeClose();
    }

    /// <summary>Asks the panel for notices when the last answer is old or in another language.</summary>
    private async Task RefreshNoticesAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var language = Loc.I.Language;
        if (_noticesFetching
            || (language == _noticesLanguage && now - _noticesFetchedAt < ColituNotices.MinFetchInterval))
        {
            return;
        }
        _noticesFetching = true;
        _noticesFetchedAt = now;
        _noticesLanguage = language;
        var epoch = _noticesEpoch;
        try
        {
            var fetched = await ColituNoticeService.Instance.FetchAsync(language);
            if (fetched == null)
            {
                // Failed: try again with the next data refresh.
                _noticesFetchedAt = DateTimeOffset.MinValue;
                return;
            }
            if (epoch != _noticesEpoch)
            {
                return;
            }
            _notices = fetched;
            ShowNextNotice();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.RefreshNoticesAsync", ex);
        }
        finally
        {
            _noticesFetching = false;
        }
    }

    /// <summary>Shows the first notice not closed yet, or hides the banner.</summary>
    private void ShowNextNotice()
    {
        var now = DateTimeOffset.UtcNow;
        var store = ColituNoticeStore.Instance;
        var next = ColituNotices.PickNext(_notices, store.IsDismissed, now);
        var changed = next?.Id != _shownNotice?.Id;
        _shownNotice = next;
        if (next == null)
        {
            NoticeBanner.IsVisible = false;
            return;
        }

        NoticeTitle.Text = next.Title;
        NoticeTitle.IsVisible = next.Title.Length > 0;
        NoticeBody.Text = next.Body;
        NoticeBody.IsVisible = next.Body.Length > 0;
        NoticeAction.Content = next.Button ?? "";
        NoticeAction.IsVisible = next.HasButton;
        ApplyNoticeLevel(next.Level);
        NoticeBanner.IsVisible = true;
        if (changed)
        {
            ColituMotion.FadeIn(NoticeBanner, 8);
        }
        if (store.MarkSeen(next.Id, now))
        {
            _ = ColituNoticeService.Instance.SendEventAsync(next.Id, ColituNotices.EventSeen);
        }
    }

    // Critical red, warning amber, promo the accent violet, info neutral (the theme's existing boxes).
    private void ApplyNoticeLevel(string level)
    {
        var box = level switch
        {
            ColituNotices.LevelCritical => "errorBox",
            ColituNotices.LevelWarning => "warnBox",
            ColituNotices.LevelPromo => "infoBox",
            _ => "neutralBox"
        };
        foreach (var name in NoticeBoxClasses)
        {
            NoticeBanner.Classes.Set(name, name == box);
        }
    }

    private void OnNoticeAction()
    {
        if (_shownNotice is not { HasButton: true } notice)
        {
            return;
        }
        OpenUrl(notice.Url!);
        _ = ColituNoticeService.Instance.SendEventAsync(notice.Id, ColituNotices.EventClicked);
    }

    private void OnNoticeClose()
    {
        if (_shownNotice is not { } notice)
        {
            return;
        }
        ColituNoticeStore.Instance.MarkDismissed(notice.Id, DateTimeOffset.UtcNow);
        _ = ColituNoticeService.Instance.SendEventAsync(notice.Id, ColituNotices.EventDismissed);
        ShowNextNotice();
    }

    /// <summary>Sign-out: the next account must not see this one's notices.</summary>
    private void ClearNotices()
    {
        _noticesEpoch++;
        _notices = [];
        _shownNotice = null;
        _noticesFetchedAt = DateTimeOffset.MinValue;
        NoticeBanner.IsVisible = false;
    }
}
