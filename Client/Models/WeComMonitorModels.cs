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
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool IsCorpHeader { get; init; }
        public string CorpKey { get; init; } = string.Empty;
        public string CorpName { get; init; } = string.Empty;
        public string CorpSubtitle { get; init; } = string.Empty;
        public int CorpItemCount { get; init; }

        public WeComUser? User { get; init; }
        public WeComGroup? Group { get; init; }

        public Visibility CorpHeaderVisibility => IsCorpHeader ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ItemRowVisibility => IsCorpHeader ? Visibility.Collapsed : Visibility.Visible;
        public string CorpCountText => CorpItemCount.ToString();

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
}
