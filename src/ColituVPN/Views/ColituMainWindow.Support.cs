using ShapePath = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using v2rayN.Desktop.Services;

namespace v2rayN.Desktop.Views;

/// <summary>
/// Live support: the conversation list, the chat thread with attachments and
/// the unread badge on the floating launcher. The thread refreshes every few
/// seconds while it is on screen; the badge checks in the background.
/// </summary>
public partial class ColituMainWindow
{
    private readonly ColituSupportService _support = ColituSupportService.Instance;
    private readonly Dictionary<string, Bitmap> _supportImages = [];
    private readonly List<string> _supportNewFiles = [];
    private readonly List<string> _supportReplyFiles = [];
    private List<ColituSupportConversation> _supportConversations = [];
    private DispatcherTimer? _supportUnreadTimer;
    private DispatcherTimer? _supportThreadTimer;
    private ColituSupportConversation? _supportCurrent;
    private string? _supportSignature;
    private int _supportUnread = -1;
    private bool _supportBusy;
    private bool _supportSelecting;
    private bool _supportPolling;

    private void WireSupport()
    {
        SupportHelpButton.Click += (_, _) => OpenUrl($"https://docs.colitu.com/{Loc.I.Language}");
        SupportNewButton.Click += (_, _) => ShowNewSupportForm();
        SupportCancelNewButton.Click += async (_, _) =>
        {
            if (_supportCurrent != null)
            {
                await OpenSupportThreadAsync(_supportCurrent.Id);
            }
            else
            {
                ShowSupportPane(SupportPlaceholder);
            }
        };
        SupportAttachNewButton.Click += async (_, _) => await PickSupportFilesAsync(_supportNewFiles, SupportNewFiles);
        SupportAttachReplyButton.Click += async (_, _) => await PickSupportFilesAsync(_supportReplyFiles, SupportReplyFiles);
        SupportCreateButton.Click += async (_, _) => await CreateSupportRequestAsync();
        SupportSendButton.Click += async (_, _) => await SendSupportReplyAsync();
        SupportList.SelectionChanged += async (_, _) =>
        {
            if (!_supportSelecting && SupportList.SelectedItem is ColituSupportRow row)
            {
                await OpenSupportThreadAsync(row.Id);
            }
        };
        // Enter sends, Shift+Enter starts a new line. Tunnel: the text box would take Enter first.
        SupportReplyBox.AddHandler(KeyDownEvent, async (_, e) =>
        {
            if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
            {
                e.Handled = true;
                await SendSupportReplyAsync();
            }
        }, RoutingStrategies.Tunnel);
    }

    // ── Polling and the badge ──────────────────────────────────────────────
    private void StartSupportPolling()
    {
        _supportUnreadTimer ??= NewTimer(TimeSpan.FromSeconds(45), CheckSupportUnreadAsync);
        _supportUnreadTimer.Start();
        SupportLauncher.IsVisible = true;
        _ = CheckSupportUnreadAsync();
    }

    private void StopSupportPolling()
    {
        _supportUnreadTimer?.Stop();
        _supportThreadTimer?.Stop();
        _supportUnread = -1;
        _supportConversations = [];
        _supportCurrent = null;
        _supportSignature = null;
        _supportNewFiles.Clear();
        _supportReplyFiles.Clear();
        _supportImages.Clear();
        SupportBadge.IsVisible = false;
        SupportLauncher.IsVisible = false;
        SupportList.ItemsSource = null;
        SupportMessages.Children.Clear();
        ShowSupportPane(SupportPlaceholder);
    }

    /// <summary>The thread refreshes quickly only while the support page shows it.</summary>
    private void UpdateSupportPolling()
    {
        _supportThreadTimer ??= NewTimer(TimeSpan.FromSeconds(5), () => PollSupportThreadAsync());
        if (_page == "support" && _supportCurrent != null && SupportThread.IsVisible)
        {
            _supportThreadTimer.Start();
        }
        else
        {
            _supportThreadTimer.Stop();
        }
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, Func<Task> tick)
    {
        var timer = new DispatcherTimer { Interval = interval };
        timer.Tick += async (_, _) => await tick();
        return timer;
    }

    private async Task CheckSupportUnreadAsync()
    {
        if (!_auth.HasSession || !AppView.IsVisible)
        {
            return;
        }

        try
        {
            var unread = await _support.UnreadAsync();
            if (_supportUnread >= 0 && unread > _supportUnread)
            {
                NotifySupportReply();
            }
            _supportUnread = unread;
            ApplySupportBadge();
            if (_page == "support" && unread > 0)
            {
                await LoadSupportListAsync(showSpinner: false);
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_UNAVAILABLE")
        {
            // Support switched off in the panel: hide the launcher until it returns.
            SupportLauncher.IsVisible = false;
        }
        catch (Exception ex) when (ColituVpnService.IsNetworkFailure(ex))
        {
            // A background badge poll while the tunnel restarts or the network drops; the next
            // poll (45 s) tries again. Not worth a stack trace in the log.
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.CheckSupportUnreadAsync", ex);
        }
    }

    private void NotifySupportReply()
    {
        var hidden = !IsVisible || WindowState == WindowState.Minimized;
        if (hidden)
        {
            Notify(Loc.I["support.newReply"]);
        }
        else if (_page != "support")
        {
            ShowToast(Loc.I["support.newReply"]);
            ColituMotion.Pop(SupportLauncher, 1.14, 2);
        }
    }

    private void ApplySupportBadge()
    {
        var count = Math.Max(0, _supportUnread);
        SupportBadge.IsVisible = count > 0;
        SupportBadgeText.Text = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
    }

    // ── Page and list ──────────────────────────────────────────────────────
    private async Task OpenSupportAsync()
    {
        await LoadSupportListAsync(showSpinner: _supportConversations.Count == 0);
        if (_supportCurrent != null && _supportConversations.Any(item => item.Id == _supportCurrent.Id))
        {
            await OpenSupportThreadAsync(_supportCurrent.Id);
        }
        else if (_supportConversations.Count > 0)
        {
            await OpenSupportThreadAsync(_supportConversations[0].Id);
        }
        else if (!SupportNewForm.IsVisible)
        {
            ShowNewSupportForm();
        }
    }

    private async Task LoadSupportListAsync(bool showSpinner)
    {
        if (showSpinner)
        {
            SupportListSpinner.IsVisible = true;
            SupportListEmpty.IsVisible = false;
        }
        try
        {
            _supportConversations = (await _support.ListAsync())
                .OrderByDescending(item => item.LastMessageAt)
                .ToList();
            _supportUnread = _supportConversations.Sum(item => item.Unread);
            ApplySupportBadge();
            SupportListSpinner.IsVisible = false;
            RenderSupportList();
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadSupportListAsync", ex);
            if (showSpinner)
            {
                ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
            }
        }
        finally
        {
            SupportListSpinner.IsVisible = false;
        }
    }

    private void RenderSupportList()
    {
        var rows = _supportConversations.Select(ColituSupportRow.From).ToList();
        _supportSelecting = true;
        try
        {
            SupportList.ItemsSource = rows;
            SupportList.SelectedItem = rows.FirstOrDefault(row => row.Id == _supportCurrent?.Id && SupportThread.IsVisible);
        }
        finally
        {
            _supportSelecting = false;
        }
        SupportListEmpty.IsVisible = rows.Count == 0 && !SupportListSpinner.IsVisible;
        if (_supportCurrent != null && SupportThread.IsVisible)
        {
            ApplySupportThreadHeader(_supportConversations.FirstOrDefault(item => item.Id == _supportCurrent.Id) ?? _supportCurrent);
        }
    }

    private void ShowSupportPane(Control pane)
    {
        foreach (var candidate in new Control[] { SupportPlaceholder, SupportNewForm, SupportThread })
        {
            candidate.IsVisible = candidate == pane;
        }
        ColituMotion.FadeIn(pane, 8);
        UpdateSupportPolling();
    }

    // ── New request ────────────────────────────────────────────────────────
    private void ShowNewSupportForm()
    {
        _supportSelecting = true;
        SupportList.SelectedItem = null;
        _supportSelecting = false;
        SupportSubjectBox.Text = "";
        SupportMessageBox.Text = "";
        _supportNewFiles.Clear();
        RenderFileChips(SupportNewFiles, _supportNewFiles);
        SupportDiagnostics.IsChecked = true;
        SupportErrorBox.IsVisible = false;
        ShowSupportPane(SupportNewForm);
        Dispatcher.UIThread.Post(() => SupportSubjectBox.Focus(), DispatcherPriority.Input);
    }

    private async Task CreateSupportRequestAsync()
    {
        if (_supportBusy)
        {
            return;
        }

        var subject = SupportSubjectBox.Text?.Trim() ?? "";
        var message = SupportMessageBox.Text?.Trim() ?? "";
        if (subject.Length == 0 || message.Length == 0)
        {
            ShowSupportError(Loc.I["support.err.subject"]);
            return;
        }

        SetSupportBusy(true, SupportCreateButton, SupportCreateSpinner);
        SupportErrorBox.IsVisible = false;
        try
        {
            var diagnostics = SupportDiagnostics.IsChecked == true
                ? await Task.Run(ColituSupportService.CollectDiagnostics)
                : null;
            var files = _supportNewFiles.ToList();
            var created = await _support.CreateAsync(subject, message, files, diagnostics);
            _supportNewFiles.Clear();
            ShowToast(Loc.I["support.sent"]);
            await LoadSupportListAsync(showSpinner: false);
            if (created != null)
            {
                await OpenSupportThreadAsync(created.Id);
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.SupportCreate", ex);
            ShowSupportError(ex is ColituApiException api ? api.Message : Loc.I["err.network"]);
        }
        finally
        {
            SetSupportBusy(false, SupportCreateButton, SupportCreateSpinner);
        }
    }

    private void ShowSupportError(string message)
    {
        SupportErrorText.Text = message;
        SupportErrorBox.IsVisible = true;
        ColituMotion.FadeIn(SupportErrorBox, 6);
    }

    // ── Thread ─────────────────────────────────────────────────────────────
    private async Task OpenSupportThreadAsync(string id)
    {
        var known = _supportConversations.FirstOrDefault(item => item.Id == id);
        if (_supportCurrent?.Id != id)
        {
            _supportSignature = null;
            SupportMessages.Children.Clear();
            _supportReplyFiles.Clear();
            RenderFileChips(SupportReplyFiles, _supportReplyFiles);
            SupportReplyBox.Text = "";
        }
        _supportCurrent = known ?? _supportCurrent ?? new ColituSupportConversation { Id = id };
        if (known != null)
        {
            ApplySupportThreadHeader(known);
        }
        if (!SupportThread.IsVisible)
        {
            ShowSupportPane(SupportThread);
        }
        await PollSupportThreadAsync(forceScroll: true);

        // Opening the thread marks the replies read on the panel.
        if (known is { Unread: > 0 })
        {
            known.Unread = 0;
            _supportUnread = _supportConversations.Sum(item => item.Unread);
            ApplySupportBadge();
            RenderSupportList();
        }
        UpdateSupportPolling();
    }

    private async Task PollSupportThreadAsync(bool forceScroll = false)
    {
        var current = _supportCurrent;
        if (current == null || _supportPolling)
        {
            return;
        }

        _supportPolling = true;
        try
        {
            var thread = await _support.ThreadAsync(current.Id);
            if (_supportCurrent?.Id != current.Id)
            {
                return;
            }
            if (thread.Conversation != null)
            {
                _supportCurrent = thread.Conversation;
                ApplySupportThreadHeader(thread.Conversation);
            }
            var messages = thread.Messages ?? [];
            var signature = $"{messages.Count}:{messages.LastOrDefault()?.Id}";
            if (signature == _supportSignature && !forceScroll)
            {
                return;
            }
            var nearBottom = SupportScroll.Offset.Y >= SupportScroll.Extent.Height - SupportScroll.Viewport.Height - 60;
            var grew = _supportSignature != null && signature != _supportSignature;
            _supportSignature = signature;
            RenderSupportMessages(messages);
            if (forceScroll || nearBottom || grew)
            {
                Dispatcher.UIThread.Post(() => SupportScroll.ScrollToEnd(), DispatcherPriority.Loaded);
            }
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_CONVERSATION_NOT_FOUND")
        {
            _supportCurrent = null;
            ShowSupportPane(SupportPlaceholder);
            await LoadSupportListAsync(showSpinner: false);
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.PollSupportThreadAsync", ex);
        }
        finally
        {
            _supportPolling = false;
        }
    }

    private void ApplySupportThreadHeader(ColituSupportConversation conversation)
    {
        SupportThreadTitle.Text = string.IsNullOrWhiteSpace(conversation.Subject) ? Loc.I["support.title"] : conversation.Subject;
        var (text, background, foreground) = ColituSupportRow.StatusLook(conversation.Status);
        SupportThreadStatusText.Text = text;
        SupportThreadStatusText.Foreground = foreground;
        SupportThreadStatus.Background = background;
        var closed = conversation.Status == "closed";
        SupportComposer.IsVisible = !closed;
        SupportReplyFiles.IsVisible = !closed;
        SupportClosedNote.IsVisible = closed;
    }

    private void RenderSupportMessages(IReadOnlyList<ColituSupportMessage> messages)
    {
        SupportMessages.Children.Clear();
        DateTime? lastDay = null;
        foreach (var message in messages)
        {
            var local = message.CreatedAt.ToLocalTime();
            if (lastDay != local.Date)
            {
                lastDay = local.Date;
                SupportMessages.Children.Add(new TextBlock
                {
                    Text = local.ToString("D", Loc.I.Culture),
                    Margin = new Thickness(0, 10, 0, 12),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    FontSize = 11.5,
                    Foreground = Resource<IBrush>("DimBrush")
                });
            }
            SupportMessages.Children.Add(BuildBubble(message, local));
        }
    }

    private Control BuildBubble(ColituSupportMessage message, DateTimeOffset local)
    {
        var mine = message.Sender == "user";
        var body = new StackPanel();
        if (!mine)
        {
            var name = message.Sender == "bot" ? "Colitu Bot" : Loc.I["support.team"];
            if (!string.IsNullOrWhiteSpace(message.AdminName) && message.Sender == "admin")
            {
                name += " · " + message.AdminName;
            }
            body.Children.Add(new TextBlock { Text = name, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Resource<IBrush>("LilacBrush"), Margin = new Thickness(0, 0, 0, 5) });
        }
        if (!string.IsNullOrWhiteSpace(message.Body))
        {
            // Selectable, so users can copy what support wrote.
            var text = new SelectableTextBlock
            {
                Text = message.Body,
                Foreground = Resource<IBrush>(mine ? "OnAccentBrush" : "TextBrush")
            };
            text.Classes.Add("message");
            body.Children.Add(text);
        }
        foreach (var attachment in message.Attachments ?? [])
        {
            body.Children.Add(attachment.IsImage ? BuildImageAttachment(attachment) : BuildFileAttachment(attachment, mine));
        }
        body.Children.Add(new TextBlock
        {
            Text = local.ToString("t", Loc.I.Culture),
            FontSize = 10.5,
            Margin = new Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = mine ? new SolidColorBrush(Color.FromArgb(0x99, 0x0B, 0x0A, 0x14)) : Resource<IBrush>("DimBrush")
        });

        return new Border
        {
            Child = body,
            MaxWidth = 460,
            Padding = new Thickness(15, 11, 15, 9),
            Margin = new Thickness(mine ? 80 : 0, 0, mine ? 0 : 80, 10),
            HorizontalAlignment = mine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            CornerRadius = mine ? new CornerRadius(18, 18, 6, 18) : new CornerRadius(18, 18, 18, 6),
            Background = Resource<IBrush>(mine ? "ActionBrush" : "Surface2Brush"),
            BorderBrush = mine ? null : Resource<IBrush>("LineBrush"),
            BorderThickness = new Thickness(mine ? 0 : 1)
        };
    }

    private Control BuildImageAttachment(ColituSupportAttachment attachment)
    {
        var image = new Image { MaxWidth = 300, MaxHeight = 220, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
        var frame = new Border
        {
            Margin = new Thickness(0, 8, 0, 0),
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            Cursor = new Cursor(StandardCursorType.Hand),
            MinWidth = 120,
            MinHeight = 80,
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            Child = image
        };
        ToolTip.SetTip(frame, attachment.FileName);
        frame.PointerReleased += async (_, _) => await OpenSupportAttachmentAsync(attachment);
        _ = LoadSupportImageAsync(attachment.Id, image, frame);
        return frame;
    }

    private async Task LoadSupportImageAsync(string id, Image target, Border frame)
    {
        try
        {
            if (!_supportImages.TryGetValue(id, out var bitmap))
            {
                var bytes = await _support.DownloadBytesAsync(id);
                using var stream = new MemoryStream(bytes);
                bitmap = Bitmap.DecodeToWidth(stream, 600);
                _supportImages[id] = bitmap;
            }
            target.Source = bitmap;
            frame.MinWidth = frame.MinHeight = 0;
            frame.Background = null;
        }
        catch (Exception ex)
        {
            Logging.SaveLog("ColituMainWindow.LoadSupportImageAsync", ex);
        }
    }

    private Control BuildFileAttachment(ColituSupportAttachment attachment, bool mine)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new ShapePath
        {
            Data = Resource<Geometry>("IconFile"),
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            Stroke = Resource<IBrush>(mine ? "OnAccentBrush" : "LilacBrush"),
            StrokeThickness = 1.6,
            StrokeJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{attachment.FileName}  ·  {FormatSize(attachment.Size)}",
            FontSize = 12.5,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Resource<IBrush>(mine ? "OnAccentBrush" : "TextBrush")
        });
        var chip = new Border
        {
            Child = row,
            Margin = new Thickness(0, 8, 0, 0),
            Padding = new Thickness(10, 7, 12, 7),
            CornerRadius = new CornerRadius(10),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(mine ? Color.FromArgb(0x26, 0x0B, 0x0A, 0x14) : Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF))
        };
        chip.PointerReleased += async (_, _) => await OpenSupportAttachmentAsync(attachment);
        return chip;
    }

    private async Task OpenSupportAttachmentAsync(ColituSupportAttachment attachment)
    {
        try
        {
            var path = await _support.DownloadAsync(attachment);
            // Shows the folder in the file manager: nothing downloaded is executed directly.
            ColituShell.OpenFolder(System.IO.Path.GetDirectoryName(path)!);
            ShowToast(System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
        }
    }

    // ── Reply ──────────────────────────────────────────────────────────────
    private async Task SendSupportReplyAsync()
    {
        var current = _supportCurrent;
        var body = SupportReplyBox.Text?.Trim() ?? "";
        if (_supportBusy || current == null || (body.Length == 0 && _supportReplyFiles.Count == 0))
        {
            return;
        }

        SetSupportBusy(true, SupportSendButton, SupportSendSpinner);
        SupportSendIcon.IsVisible = false;
        try
        {
            await _support.ReplyAsync(current.Id, body, _supportReplyFiles.ToList());
            SupportReplyBox.Text = "";
            _supportReplyFiles.Clear();
            RenderFileChips(SupportReplyFiles, _supportReplyFiles);
            await PollSupportThreadAsync(forceScroll: true);
            await LoadSupportListAsync(showSpinner: false);
        }
        catch (ColituApiException ex) when (ex.ErrorCode is "SUPPORT_CONVERSATION_CLOSED")
        {
            ShowToast(ex.Message, true);
            await PollSupportThreadAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex is ColituApiException api ? api.Message : Loc.I["err.network"], true);
        }
        finally
        {
            SetSupportBusy(false, SupportSendButton, SupportSendSpinner);
            SupportSendIcon.IsVisible = true;
            SupportReplyBox.Focus();
        }
    }

    // ── Files ──────────────────────────────────────────────────────────────
    private async Task PickSupportFilesAsync(List<string> files, Panel chips)
    {
        var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = Loc.I["support.attach"],
            FileTypeFilter =
            [
                new FilePickerFileType("Images, PDF, text, archives")
                {
                    Patterns = ColituSupportService.AllowedExtensions.Select(extension => "*" + extension).ToList()
                }
            ]
        });
        foreach (var file in picked.Select(item => item.TryGetLocalPath()).Where(path => path != null).Cast<string>())
        {
            if (files.Count >= ColituSupportService.MaxFilesPerMessage)
            {
                ShowToast(Loc.I["support.err.files"], true);
                break;
            }
            if (ColituSupportService.CheckFile(file) is { } problem)
            {
                ShowToast($"{System.IO.Path.GetFileName(file)}: {problem}", true);
                continue;
            }
            if (!files.Contains(file, StringComparer.Ordinal))
            {
                files.Add(file);
            }
        }
        RenderFileChips(chips, files);
    }

    private void RenderFileChips(Panel host, List<string> files)
    {
        host.Children.Clear();
        foreach (var file in files)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = $"{System.IO.Path.GetFileName(file)}  ·  {FormatSize(new FileInfo(file).Length)}",
                FontSize = 12.5,
                MaxWidth = 260,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            var icon = new ShapePath { Data = Resource<Geometry>("IconX"), Width = 9, Height = 9 };
            icon.Classes.Add("stroke");
            var remove = new Button
            {
                Theme = Resource<ControlTheme>("IconButton"),
                Width = 22,
                Height = 22,
                Margin = new Thickness(6, 0, 0, 0),
                Content = icon
            };
            var captured = file;
            remove.Click += (_, _) =>
            {
                files.Remove(captured);
                RenderFileChips(host, files);
            };
            row.Children.Add(remove);
            host.Children.Add(new Border
            {
                Child = row,
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(12, 4, 4, 4),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
                BorderBrush = Resource<IBrush>("LineBrush"),
                BorderThickness = new Thickness(1)
            });
        }
    }

    private void SetSupportBusy(bool busy, Button button, Control spinner)
    {
        _supportBusy = busy;
        button.IsEnabled = !busy;
        spinner.IsVisible = busy;
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            >= 1 << 20 => $"{bytes / 1048576.0:0.#} MB",
            >= 1 << 10 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes} B"
        };
    }
}

/// <summary>One conversation in the support list.</summary>
public sealed class ColituSupportRow
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Preview { get; init; } = "";
    public string TimeText { get; init; } = "";
    public int Unread { get; init; }
    public bool HasUnread => Unread > 0;
    public string StatusText { get; init; } = "";
    public IBrush? StatusBackground { get; init; }
    public IBrush? StatusForeground { get; init; }

    public static ColituSupportRow From(ColituSupportConversation conversation)
    {
        var (text, background, foreground) = StatusLook(conversation.Status);
        var local = conversation.LastMessageAt.ToLocalTime();
        return new ColituSupportRow
        {
            Id = conversation.Id,
            Title = string.IsNullOrWhiteSpace(conversation.Subject) ? Loc.I["support.title"] : conversation.Subject!,
            Preview = (conversation.LastMessage ?? "").ReplaceLineEndings(" "),
            TimeText = local.Date == DateTime.Today ? local.ToString("t", Loc.I.Culture) : local.ToString("d MMM", Loc.I.Culture),
            Unread = conversation.Unread,
            StatusText = text,
            StatusBackground = background,
            StatusForeground = foreground
        };
    }

    public static (string Text, IBrush Background, IBrush Foreground) StatusLook(string? status)
    {
        return status switch
        {
            "open" => (Loc.I["support.status.open"], Tint(0x29, 0x9F, 0x8C, 0xFF), Tint(0xFF, 0xC4, 0xB5, 0xFD)),
            "resolved" => (Loc.I["support.status.resolved"], Tint(0x26, 0x5E, 0xE0, 0xA0), Tint(0xFF, 0x5E, 0xE0, 0xA0)),
            "closed" => (Loc.I["support.status.closed"], Tint(0x1A, 0xFF, 0xFF, 0xFF), Tint(0xFF, 0x9A, 0x9A, 0xAB)),
            _ => (Loc.I["support.status.waiting"], Tint(0x26, 0xF5, 0xC3, 0x6B), Tint(0xFF, 0xF5, 0xC3, 0x6B))
        };
    }

    private static IBrush Tint(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));
}
