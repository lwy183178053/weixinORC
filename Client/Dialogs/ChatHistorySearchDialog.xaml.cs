using System.Collections.ObjectModel;
using System.Globalization;
using Client.Models;
using Client.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Client.Dialogs
{
    public sealed class ChatHistorySearchItem
    {
        public long Id { get; init; }
        public string SenderName { get; init; } = string.Empty;
        public bool IsOutgoing { get; init; }
        public string Content { get; init; } = string.Empty;
        public string ImageUrl { get; init; } = string.Empty;
        public string AudioUrl { get; init; } = string.Empty;
        public string VideoUrl { get; init; } = string.Empty;
        public DateTime LocalTime { get; init; }
        public DateOnly LocalDate => DateOnly.FromDateTime(LocalTime);

        public bool HasImage => !string.IsNullOrWhiteSpace(ImageUrl);
        public bool HasAudio => !string.IsNullOrWhiteSpace(AudioUrl) || Content.Contains("[语音", StringComparison.Ordinal);
        public bool HasVideo => !string.IsNullOrWhiteSpace(VideoUrl) || Content.Contains("[视频", StringComparison.Ordinal);

        public string SenderInitial => !string.IsNullOrWhiteSpace(SenderName)
            ? SenderName.Trim()[..1].ToUpperInvariant()
            : (IsOutgoing ? "我" : "客");

        public string FullTimeText => LocalTime != DateTime.MinValue
            ? LocalTime.ToString("yyyy-MM-dd HH:mm:ss")
            : string.Empty;

        public string MediaBadgeText => HasVideo ? "视频" : HasAudio ? "语音" : HasImage ? "图片" : string.Empty;
        public Visibility MediaBadgeVisibility => !string.IsNullOrEmpty(MediaBadgeText) ? Visibility.Visible : Visibility.Collapsed;
        public Visibility HasImageVisibility => HasImage ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayPreview
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Content)) return Content;
                if (HasVideo) return "[视频消息]";
                if (HasAudio) return "[语音消息]";
                if (HasImage) return "[图片消息]";
                return "(空消息)";
            }
        }

        public Brush AvatarBgBrush => (Brush)Application.Current.Resources[IsOutgoing ? "BrandSoftBgBrush" : "AppInputBgBrush"];
        public Brush AvatarFgBrush => (Brush)Application.Current.Resources[IsOutgoing ? "BrandPrimaryBrush" : "TextSecondaryBrush"];
    }

    public sealed partial class ChatHistorySearchDialog : ContentDialog
    {
        private const int InitialFetchLimit = 2000;
        private const int LoadMoreBatchSize = 2000;

        private readonly bool _isGroup;
        private readonly long _targetId;
        private readonly string _conversationTitle;
        private readonly string _peerFallbackName;

        private readonly List<ChatHistorySearchItem> _allItems = [];
        private readonly HashSet<long> _loadedIds = [];
        private readonly ObservableCollection<ChatHistorySearchItem> _visibleItems = [];

        private long _nextCursor;
        private bool _isLoading;
        private bool _isUpdatingDatePicker;
        private DateOnly? _selectedDate;
        private string _activeMediaFilter = "all";
        private CancellationTokenSource? _loadCts;

        public long SelectedJumpMessageId { get; private set; }

        public ChatHistorySearchDialog(bool isGroup, long targetId, string conversationTitle, string? peerFallbackName = null)
        {
            InitializeComponent();
            RequestedTheme = AppServices.Settings.CurrentElementTheme;
            _isGroup = isGroup;
            _targetId = targetId;
            _conversationTitle = string.IsNullOrWhiteSpace(conversationTitle) ? "当前会话" : conversationTitle.Trim();
            _peerFallbackName = string.IsNullOrWhiteSpace(peerFallbackName) ? _conversationTitle : peerFallbackName.Trim();

            DialogTitleText.Text = $"查找聊天记录 · {_conversationTitle}";
            ResultsListView.ItemsSource = _visibleItems;

            Loaded += ChatHistorySearchDialog_Loaded;
            Closed += ChatHistorySearchDialog_Closed;
        }

        private async void ChatHistorySearchDialog_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= ChatHistorySearchDialog_Loaded;
            UpdateMediaFilterVisuals();
            await LoadMessagesBatchAsync(InitialFetchLimit);
            KeywordBox.Focus(FocusState.Programmatic);
        }

        private void ChatHistorySearchDialog_Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;
        }

        private async Task LoadMessagesBatchAsync(int maxToFetch)
        {
            if (_isLoading) return;
            _isLoading = true;
            LoadMoreHistoryButton.IsEnabled = false;

            if (_allItems.Count == 0)
            {
                LoadingPanel.Visibility = Visibility.Visible;
                LoadingRing.IsActive = true;
                ResultsListView.Visibility = Visibility.Collapsed;
                EmptyResultsHint.Visibility = Visibility.Collapsed;
            }

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = new CancellationTokenSource();
            var ct = _loadCts.Token;

            try
            {
                int fetched = 0;
                while (fetched < maxToFetch)
                {
                    ct.ThrowIfCancellationRequested();
                    var (ok, err, nextCursor, pageItems) = await FetchSearchPageAsync(ct);
                    if (!ok)
                    {
                        if (_allItems.Count == 0)
                        {
                            DialogSubtitleText.Text = $"加载{(_isGroup ? "群聊" : "聊天")}记录失败：{err}";
                        }
                        break;
                    }

                    foreach (var item in pageItems)
                    {
                        if (_loadedIds.Add(item.Id))
                        {
                            _allItems.Add(item);
                        }
                    }

                    fetched += pageItems.Count;
                    _nextCursor = nextCursor;
                    if (pageItems.Count == 0 || _nextCursor <= 0) break;

                    LoadingText.Text = $"已加载 {_allItems.Count} 条聊天记录...";
                }

                RebuildDateBoundsAndChips();
                ApplySearchAndFilter();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                DialogSubtitleText.Text = $"加载聊天记录异常：{ex.Message}";
            }
            finally
            {
                _isLoading = false;
                LoadingRing.IsActive = false;
                LoadingPanel.Visibility = Visibility.Collapsed;
                LoadMoreHistoryButton.Visibility = _nextCursor > 0 ? Visibility.Visible : Visibility.Collapsed;
                LoadMoreHistoryButton.IsEnabled = _nextCursor > 0;
            }
        }

        private async Task<(bool Ok, string Error, long NextCursor, List<ChatHistorySearchItem> Items)> FetchSearchPageAsync(CancellationToken ct)
        {
            if (_isGroup)
            {
                var res = await AppServices.Backend.ListGroupMessagesAsync(_targetId, limit: 100, cursor: _nextCursor, ct: ct);
                if (!res.IsSuccess || res.Data is null) return (false, res.ErrorMessage, 0, []);
                var list = res.Data.Items.Select(m => new ChatHistorySearchItem
                {
                    Id = m.Id,
                    SenderName = m.IsOutgoing ? "我" : (string.IsNullOrWhiteSpace(m.SenderName) ? "群成员" : m.SenderName),
                    IsOutgoing = m.IsOutgoing,
                    Content = m.Content ?? string.Empty,
                    ImageUrl = m.ImageUrl ?? string.Empty,
                    AudioUrl = m.AudioUrl ?? string.Empty,
                    VideoUrl = m.VideoUrl ?? string.Empty,
                    LocalTime = ParseLocalDateTime(m.SentAt)
                }).ToList();
                return (true, string.Empty, res.Data.NextCursor, list);
            }
            else
            {
                var res = await AppServices.Backend.ListCustomerMessagesAsync(_targetId, limit: 100, cursor: _nextCursor, ct: ct);
                if (!res.IsSuccess || res.Data is null) return (false, res.ErrorMessage, 0, []);
                var list = res.Data.Items.Select(m => new ChatHistorySearchItem
                {
                    Id = m.Id,
                    SenderName = m.IsOutgoing ? "我" : _peerFallbackName,
                    IsOutgoing = m.IsOutgoing,
                    Content = m.Content ?? string.Empty,
                    ImageUrl = m.ImageUrl ?? string.Empty,
                    AudioUrl = m.AudioUrl ?? string.Empty,
                    VideoUrl = m.VideoUrl ?? string.Empty,
                    LocalTime = ParseLocalDateTime(m.SentAt)
                }).ToList();
                return (true, string.Empty, res.Data.NextCursor, list);
            }
        }

        private static DateTimeOffset ToLocalDateOffset(DateOnly d) =>
            new(new DateTime(d.Year, d.Month, d.Day, 0, 0, 0, DateTimeKind.Local));

        private void RebuildDateBoundsAndChips()
        {
            var dateGroups = _allItems
                .Where(x => x.LocalTime != DateTime.MinValue)
                .GroupBy(x => x.LocalDate)
                .OrderByDescending(g => g.Key)
                .ToList();

            string moreHint = _nextCursor > 0 ? "（可点击左下角加载更早记录）" : string.Empty;
            DialogSubtitleText.Text = $"已加载 {_allItems.Count} 条记录 · 覆盖 {dateGroups.Count} 个有聊天日期{moreHint}";

            if (dateGroups.Count > 0)
            {
                ChatDatePicker.MinDate = ToLocalDateOffset(dateGroups[^1].Key);
                ChatDatePicker.MaxDate = ToLocalDateOffset(dateGroups[0].Key);
            }

            DateChipsPanel.Children.Clear();
            if (dateGroups.Count == 0)
            {
                DateChipsBar.Visibility = Visibility.Collapsed;
                return;
            }

            DateChipsBar.Visibility = Visibility.Visible;
            DateChipsPanel.Children.Add(BuildDateChipButton(null, $"全部 ({_allItems.Count})"));

            var today = DateOnly.FromDateTime(DateTime.Now);
            var yesterday = today.AddDays(-1);
            foreach (var grp in dateGroups.Take(30))
            {
                string label;
                if (grp.Key == today) label = $"今天 ({grp.Count()})";
                else if (grp.Key == yesterday) label = $"昨天 ({grp.Count()})";
                else if (grp.Key.Year == today.Year) label = $"{grp.Key:MM-dd} ({grp.Count()})";
                else label = $"{grp.Key:yyyy-MM-dd} ({grp.Count()})";

                DateChipsPanel.Children.Add(BuildDateChipButton(grp.Key, label));
            }
            UpdateDateChipHighlights();
        }

        private Button BuildDateChipButton(DateOnly? date, string text)
        {
            var btn = new Button
            {
                Height = 25,
                Padding = new Thickness(9, 0, 9, 0),
                CornerRadius = new CornerRadius(12),
                FontSize = 11,
                Content = text,
                Tag = date,
                Style = (Style)Application.Current.Resources["SecondaryOutlineButtonStyle"]
            };
            btn.Click += DateChip_Click;
            return btn;
        }

        private void UpdateDateChipHighlights()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeBorder = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var normalBg = (Brush)Application.Current.Resources["ButtonBgBrush"];
            var normalBorder = (Brush)Application.Current.Resources["ButtonBorderBrush"];
            var normalFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            foreach (var child in DateChipsPanel.Children)
            {
                if (child is not Button btn) continue;
                var chipDate = btn.Tag as DateOnly?;
                bool isSelected = chipDate == _selectedDate;
                btn.Background = isSelected ? activeBg : normalBg;
                btn.BorderBrush = isSelected ? activeBorder : normalBorder;
                btn.Foreground = isSelected ? activeFg : normalFg;
            }
        }

        private void ApplySearchAndFilter()
        {
            string q = KeywordBox.Text?.Trim() ?? string.Empty;
            IEnumerable<ChatHistorySearchItem> query = _allItems;

            if (_selectedDate.HasValue)
            {
                var d = _selectedDate.Value;
                query = query.Where(x => x.LocalTime != DateTime.MinValue && x.LocalDate == d);
            }

            query = _activeMediaFilter switch
            {
                "text" => query.Where(x => !x.HasImage && !x.HasAudio && !x.HasVideo && !string.IsNullOrWhiteSpace(x.Content)),
                "image" => query.Where(x => x.HasImage && !x.HasVideo),
                "audio" => query.Where(x => x.HasAudio),
                "video" => query.Where(x => x.HasVideo),
                _ => query
            };

            if (!string.IsNullOrEmpty(q))
            {
                query = query.Where(x =>
                    x.Content.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                    x.SenderName.Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            // When browsing a specific date without keyword, show chronological order (oldest -> newest) for natural reading;
            // otherwise show newest matches first.
            var results = (_selectedDate.HasValue && string.IsNullOrEmpty(q))
                ? query.OrderBy(x => x.LocalTime).ThenBy(x => x.Id).ToList()
                : query.OrderByDescending(x => x.LocalTime).ThenByDescending(x => x.Id).ToList();

            _visibleItems.Clear();
            foreach (var item in results.Take(500))
            {
                _visibleItems.Add(item);
            }

            string dateNote = _selectedDate.HasValue ? $"{_selectedDate.Value:yyyy-MM-dd} · " : string.Empty;
            string overflowNote = results.Count > 500 ? "（显示前 500 条）" : string.Empty;
            MatchSummaryText.Text = $"{dateNote}共 {results.Count} 条结果{overflowNote}";

            bool hasItems = _visibleItems.Count > 0;
            ResultsListView.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyResultsHint.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
            if (!hasItems)
            {
                EmptyResultsTitle.Text = _selectedDate.HasValue
                    ? $"{_selectedDate.Value:yyyy-MM-dd} 暂无匹配记录"
                    : "未找到匹配的聊天记录";
            }

            ClearDateButton.Visibility = _selectedDate.HasValue ? Visibility.Visible : Visibility.Collapsed;
            UpdateDateChipHighlights();
        }

        private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplySearchAndFilter();
        }

        private void ChatDatePicker_DateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
        {
            if (_isUpdatingDatePicker) return;
            _selectedDate = args.NewDate.HasValue
                ? DateOnly.FromDateTime(args.NewDate.Value.LocalDateTime)
                : null;
            ApplySearchAndFilter();
        }

        private void DateChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            SetSelectedDate(btn.Tag as DateOnly?);
        }

        private void ClearDateButton_Click(object sender, RoutedEventArgs e)
        {
            SetSelectedDate(null);
        }

        private void SetSelectedDate(DateOnly? date)
        {
            _selectedDate = date;
            _isUpdatingDatePicker = true;
            try
            {
                ChatDatePicker.Date = date.HasValue ? ToLocalDateOffset(date.Value) : null;
            }
            finally
            {
                _isUpdatingDatePicker = false;
            }
            ApplySearchAndFilter();
        }

        private void MediaFilter_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not string tag) return;
            _activeMediaFilter = tag;
            UpdateMediaFilterVisuals();
            ApplySearchAndFilter();
        }

        private void UpdateMediaFilterVisuals()
        {
            var activeBg = (Brush)Application.Current.Resources["BrandSoftBgBrush"];
            var activeBorder = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var activeFg = (Brush)Application.Current.Resources["BrandPrimaryBrush"];
            var normalBg = (Brush)Application.Current.Resources["ButtonBgBrush"];
            var normalBorder = (Brush)Application.Current.Resources["ButtonBorderBrush"];
            var normalFg = (Brush)Application.Current.Resources["TextSecondaryBrush"];

            void StyleBtn(Button b, string key)
            {
                bool active = _activeMediaFilter == key;
                b.Background = active ? activeBg : normalBg;
                b.BorderBrush = active ? activeBorder : normalBorder;
                b.Foreground = active ? activeFg : normalFg;
            }

            StyleBtn(TypeAllBtn, "all");
            StyleBtn(TypeTextBtn, "text");
            StyleBtn(TypeImageBtn, "image");
            StyleBtn(TypeAudioBtn, "audio");
            StyleBtn(TypeVideoBtn, "video");
        }

        private async void LoadMoreHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadMessagesBatchAsync(LoadMoreBatchSize);
        }

        private void ResultsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ChatHistorySearchItem item)
            {
                SelectedJumpMessageId = item.Id;
                Hide();
            }
        }

        private void JumpToMessageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is long id && id > 0)
            {
                SelectedJumpMessageId = id;
                Hide();
            }
        }

        private async void ViewImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not long id) return;
            var item = _allItems.FirstOrDefault(x => x.Id == id);
            if (item is null || string.IsNullOrWhiteSpace(item.ImageUrl)) return;

            try
            {
                byte[]? bytes = await AppServices.Backend.DownloadChatImageBytesAsync(item.ImageUrl);
                if (bytes is { Length: > 0 })
                {
                    ImageViewerWindow.ShowImage(bytes, $"{_conversationTitle} · {item.FullTimeText}");
                }
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("UI", $"预览聊天图片失败：{ex.Message}");
            }
        }

        private static DateTime ParseLocalDateTime(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return DateTime.MinValue;
            return DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto)
                ? dto.ToLocalTime().DateTime
                : DateTime.MinValue;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();

        private void CloseAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
        {
            args.Handled = true;
            Hide();
        }
    }
}
