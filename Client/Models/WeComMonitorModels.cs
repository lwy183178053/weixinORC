using System.ComponentModel;
using System.Runtime.CompilerServices;
using Client.Services.WeComDb;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    /// <summary>
    /// Collapsible enterprise-grouped row model for WeCom Personal Monitor (WeComContactsPage)
    /// and WeCom Group Monitor (WeComGroupsPage), matching the visual language of CustomersPage/GroupsPage.
    /// </summary>
    public sealed class WeComSidebarRow : INotifyPropertyChanged
    {
        private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

        private bool _isCollapsed;
        private bool _isActive;
        private bool _isMultiSelectMode;
        private bool _isChecked;
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsCorpHeader { get; init; }
        public string ItemKey { get; init; } = string.Empty;
        public string CorpKey { get; init; } = string.Empty;
        public string CorpName { get; init; } = string.Empty;
        public string CorpSubtitle { get; init; } = string.Empty;
        public int CorpItemCount { get; init; }

        public WeComUser? User { get; init; }
        public WeComGroup? Group { get; init; }

        public Visibility CorpHeaderVisibility => IsCorpHeader ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemRowVisibility => IsCorpHeader ? Visibility.Collapsed : Visibility.Visible;
        public string CorpCountText => CorpItemCount.ToString();

        public bool IsMultiSelectMode
        {
            get => _isMultiSelectMode;
            set
            {
                if (_isMultiSelectMode != value)
                {
                    _isMultiSelectMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ItemCheckVisibility));
                }
            }
        }

        public Visibility ItemCheckVisibility =>
            !IsCorpHeader && _isMultiSelectMode ? Visibility.Visible : Visibility.Collapsed;

        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (_isCollapsed != value)
                {
                    _isCollapsed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ChevronGlyph));
                }
            }
        }

        public string ChevronGlyph => _isCollapsed ? "\uE76C" : "\uE70D";

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RowBackground));
                    OnPropertyChanged(nameof(RowBorderBrush));
                }
            }
        }

        public Brush RowBackground => _isActive
            ? (Brush)Application.Current.Resources["BrandSoftBgBrush"]
            : TransparentBrush;

        public Brush RowBorderBrush => _isActive
            ? (Brush)Application.Current.Resources["BrandSoftBorderBrush"]
            : TransparentBrush;

        public ImageSource? AvatarImage
        {
            get => _avatarImage;
            set
            {
                if (!ReferenceEquals(_avatarImage, value))
                {
                    _avatarImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarImageVisibility));
                    OnPropertyChanged(nameof(AvatarInitialVisibility));
                }
            }
        }

        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayName => User is not null
            ? User.DisplayName
            : Group?.DisplayName ?? string.Empty;

        public string AvatarInitial
        {
            get
            {
                string d = DisplayName;
                return !string.IsNullOrWhiteSpace(d) ? d[..1].ToUpperInvariant() : (Group is not null ? "群" : "企");
            }
        }

        public string AvatarUrl => User is not null
            ? User.AvatarUrl
            : Group?.AvatarUrl ?? string.Empty;

        public string TimeShort
        {
            get
            {
                long ts = User is not null ? User.LastMessageTime : (Group?.LastMessageTime ?? 0);
                if (ts <= 0) return string.Empty;
                var dt = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime();
                var now = DateTimeOffset.Now;
                if (dt.Date == now.Date) return dt.ToString("HH:mm");
                if (dt.Year == now.Year) return dt.ToString("MM/dd");
                return dt.ToString("yy/MM");
            }
        }

        public string PreviewLine
        {
            get
            {
                if (User is not null)
                {
                    if (!string.IsNullOrWhiteSpace(User.LastMessageSummary)) return User.LastMessageSummary;
                    if (!string.IsNullOrWhiteSpace(User.ExternalCorpName)) return $"@{User.ExternalCorpName}";
                    return User.IsExternal ? "外部微信客户" : "企业同事";
                }
                if (Group is not null)
                {
                    if (!string.IsNullOrWhiteSpace(Group.LastMessageSummary)) return Group.LastMessageSummary;
                    return $"群成员 {Group.Members.Count} 人";
                }
                return string.Empty;
            }
        }

        public string TagBadgeText
        {
            get
            {
                if (User is not null)
                {
                    if (User.IsExternal)
                        return !string.IsNullOrWhiteSpace(User.ExternalCorpName) ? User.ExternalCorpName : "@微信";
                    return string.Empty;
                }
                if (Group is not null && Group.Members.Count > 0)
                {
                    return $"{Group.Members.Count}人";
                }
                return string.Empty;
            }
        }

        public Visibility TagBadgeVisibility =>
            !IsCorpHeader && !string.IsNullOrWhiteSpace(TagBadgeText) ? Visibility.Visible : Visibility.Collapsed;

        public Visibility PinnedVisibility =>
            Group is not null && Group.IsPinned ? Visibility.Visible : Visibility.Collapsed;

        public void NotifyVisualsChanged()
        {
            OnPropertyChanged(nameof(RowBackground));
            OnPropertyChanged(nameof(RowBorderBrush));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeComGroupMemberRow : INotifyPropertyChanged
    {
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string UserId { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string SubText { get; init; } = string.Empty;
        public string RoleText { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;

        public string AvatarInitial => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName[..1].ToUpperInvariant() : "员";
        public Visibility RoleVisibility => !string.IsNullOrWhiteSpace(RoleText) ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? AvatarImage
        {
            get => _avatarImage;
            set
            {
                if (!ReferenceEquals(_avatarImage, value))
                {
                    _avatarImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarImageVisibility));
                    OnPropertyChanged(nameof(AvatarInitialVisibility));
                }
            }
        }

        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Enterprise category filter row inside ImportWeComContactsDialog / ImportWeComGroupsDialog.
    /// </summary>
    public sealed class WeComCorpFilterOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _selectedCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string AccountId { get; init; } = string.Empty;
        public string CorpName { get; init; } = string.Empty;
        public bool IsAllBucket { get; init; }
        public int TotalCount { get; init; }

        public string DisplayLabel => $"{CorpName} ({TotalCount})";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public int SelectedCount
        {
            get => _selectedCount;
            set
            {
                if (_selectedCount != value)
                {
                    _selectedCount = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedCountText));
                    OnPropertyChanged(nameof(SelectedBadgeVisibility));
                }
            }
        }

        public string SelectedCountText => _selectedCount.ToString();
        public Visibility SelectedBadgeVisibility => _selectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Selectable contact item inside ImportWeComContactsDialog.
    /// </summary>
    public sealed class WeComContactPickItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; init; } = string.Empty;
        public WeComUser User { get; init; } = null!;
        public bool IsAlreadyImported { get; init; }

        public bool HasMessages => User.MessageCount > 0 || User.LastMessageTime > 0;
        public bool IsExternal => User.IsExternal;
        public string DisplayName => User.DisplayName;
        public string AvatarUrl => User.AvatarUrl;
        public string AvatarInitial => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName[..1].ToUpperInvariant() : "企";

        public string TypeBadgeText => User.IsExternal
            ? (!string.IsNullOrWhiteSpace(User.ExternalCorpName) ? $"@{User.ExternalCorpName}" : "@微信")
            : "内部同事";

        public string SubLine
        {
            get
            {
                var parts = new List<string>(3) { User.OwnerCorpName };
                if (User.MessageCount > 0) parts.Add($"{User.MessageCount} 条记录");
                if (!string.IsNullOrWhiteSpace(User.LastMessageSummary)) parts.Add(User.LastMessageSummary);
                else if (!string.IsNullOrWhiteSpace(User.Position)) parts.Add(User.Position);
                return string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        public Visibility ImportedBadgeVisibility => IsAlreadyImported ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? AvatarImage
        {
            get => _avatarImage;
            set
            {
                if (!ReferenceEquals(_avatarImage, value))
                {
                    _avatarImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarImageVisibility));
                    OnPropertyChanged(nameof(AvatarInitialVisibility));
                }
            }
        }

        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Selectable group item inside ImportWeComGroupsDialog.
    /// </summary>
    public sealed class WeComGroupPickItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Key { get; init; } = string.Empty;
        public WeComGroup Group { get; init; } = null!;
        public bool IsAlreadyImported { get; init; }

        public bool HasMessages => Group.MessageCount > 0 || Group.LastMessageTime > 0;
        public string DisplayTitle => Group.Members.Count > 0
            ? $"{Group.DisplayName} ({Group.Members.Count}人)"
            : Group.DisplayName;
        public string AvatarUrl => Group.AvatarUrl;
        public string AvatarInitial => !string.IsNullOrWhiteSpace(Group.DisplayName) ? Group.DisplayName[..1].ToUpperInvariant() : "群";

        public string SubLine
        {
            get
            {
                var parts = new List<string>(3) { Group.OwnerCorpName };
                if (Group.MessageCount > 0) parts.Add($"{Group.MessageCount} 条群消息");
                if (!string.IsNullOrWhiteSpace(Group.LastMessageSummary)) parts.Add(Group.LastMessageSummary);
                return string.Join(" · ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        public Visibility ImportedBadgeVisibility => IsAlreadyImported ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public ImageSource? AvatarImage
        {
            get => _avatarImage;
            set
            {
                if (!ReferenceEquals(_avatarImage, value))
                {
                    _avatarImage = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(AvatarImageVisibility));
                    OnPropertyChanged(nameof(AvatarInitialVisibility));
                }
            }
        }

        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
