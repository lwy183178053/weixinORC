using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Services.WeChat4Db
{
    public sealed class WeChat4AccountDir
    {
        public string DirName { get; init; } = string.Empty;
        public string Wxid { get; init; } = string.Empty;
        public string AccountPath { get; init; } = string.Empty;
        public string DbStoragePath { get; init; } = string.Empty;
        public DateTimeOffset LastModified { get; init; }

        public string DisplayLabel => string.IsNullOrWhiteSpace(Wxid)
            ? DirName
            : $"{Wxid}  ({DirName})";
    }

    public sealed class WeChat4ProcessInfo
    {
        public int Pid { get; init; }
        public string Name { get; init; } = string.Empty;
        public long MemoryKb { get; init; }
    }

    public sealed class WeChat4TagOption : INotifyPropertyChanged
    {
        private bool _isSelected;
        private int _selectedCount;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string TagName { get; init; } = string.Empty;
        public bool IsAllBucket { get; init; }
        public bool IsUntaggedBucket { get; init; }
        public int ContactCount { get; init; }
        public int ContactsWithMessagesCount { get; init; }

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

        public string SelectedCountText => $"已选 {SelectedCount}";
        public Visibility SelectedBadgeVisibility => SelectedCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayLabel => IsAllBucket
            ? $"全部好友 ({ContactCount})"
            : IsUntaggedBucket
                ? $"未打标签 ({ContactCount})"
                : $"{TagName} ({ContactCount})";

        public string CountBadge => ContactCount.ToString();

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeChat4Contact : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isAlreadyImported;
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Username { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
        public string NickName { get; init; } = string.Empty;
        public string Remark { get; init; } = string.Empty;
        public string Gender { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Tags { get; init; } = string.Empty;
        public List<string> TagList { get; init; } = [];
        public string Phone { get; init; } = string.Empty;
        public string BigHeadUrl { get; init; } = string.Empty;
        public string SmallHeadUrl { get; init; } = string.Empty;
        public int VerifyFlag { get; init; }
        public int LocalType { get; init; }
        public bool IsFriend { get; init; }
        public bool IsGroup { get; init; }
        public bool IsOfficial { get; init; }
        public bool IsSystem { get; init; }
        public bool HasMessages { get; set; }
        public int EstimatedMessageCount { get; set; }
        public string LastMessageSummary { get; set; } = string.Empty;
        public long LastMessageTime { get; set; }

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

        public bool IsAlreadyImported
        {
            get => _isAlreadyImported;
            set
            {
                if (_isAlreadyImported != value)
                {
                    _isAlreadyImported = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ImportedBadgeVisibility));
                }
            }
        }

        public Visibility ImportedBadgeVisibility => _isAlreadyImported ? Visibility.Visible : Visibility.Collapsed;

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

        public string DisplayName => !string.IsNullOrWhiteSpace(Remark)
            ? Remark
            : !string.IsNullOrWhiteSpace(NickName)
                ? NickName
                : !string.IsNullOrWhiteSpace(Alias) ? Alias : Username;

        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "友";

        public string AvatarUrl => !string.IsNullOrWhiteSpace(BigHeadUrl) ? BigHeadUrl : SmallHeadUrl;

        public string SubLine
        {
            get
            {
                var parts = new List<string>(4);
                if (!string.IsNullOrWhiteSpace(Alias)) parts.Add($"微信号: {Alias}");
                else if (!string.IsNullOrWhiteSpace(NickName) && !NickName.Equals(DisplayName, StringComparison.OrdinalIgnoreCase)) parts.Add($"昵称: {NickName}");
                if (!string.IsNullOrWhiteSpace(Tags)) parts.Add($"标签: {Tags}");
                if (HasMessages) parts.Add(EstimatedMessageCount > 0 ? $"聊天 {EstimatedMessageCount} 条" : "有聊天记录");
                else parts.Add("暂无本地聊天");
                return string.Join(" · ", parts);
            }
        }

        public bool MatchesTags(ISet<string> selectedTags, bool includeUntagged)
        {
            if (TagList.Count == 0) return includeUntagged;
            return TagList.Any(t => selectedTags.Contains(t));
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeChat4GroupMember
    {
        public string Username { get; init; } = string.Empty;
        public string Alias { get; init; } = string.Empty;
        public string NickName { get; init; } = string.Empty;
        public string Remark { get; init; } = string.Empty;
        public string GroupNickname { get; init; } = string.Empty;
        public string Gender { get; init; } = string.Empty;
        public string Region { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
        public string Role { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;

        public string DisplayName => !string.IsNullOrWhiteSpace(GroupNickname)
            ? GroupNickname
            : !string.IsNullOrWhiteSpace(Remark)
                ? Remark
                : !string.IsNullOrWhiteSpace(NickName)
                    ? NickName
                    : !string.IsNullOrWhiteSpace(Alias) ? Alias : Username;
    }

    public sealed class WeChat4Group : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _isAlreadyImported;
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Username { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
        public string MyNickname { get; init; } = string.Empty;
        public string AvatarUrl { get; init; } = string.Empty;
        public int MemberCount { get; init; }
        public List<WeChat4GroupMember> Members { get; init; } = [];
        public bool HasMessages { get; set; }
        public int EstimatedMessageCount { get; set; }
        public string LastMessageSummary { get; set; } = string.Empty;
        public long LastMessageTime { get; set; }

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

        public bool IsAlreadyImported
        {
            get => _isAlreadyImported;
            set
            {
                if (_isAlreadyImported != value)
                {
                    _isAlreadyImported = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ImportedBadgeVisibility));
                }
            }
        }

        public Visibility ImportedBadgeVisibility => _isAlreadyImported ? Visibility.Visible : Visibility.Collapsed;

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

        public string DisplayTitle => !string.IsNullOrWhiteSpace(GroupName)
            ? $"{GroupName} ({MemberCount}人)"
            : $"{Username} ({MemberCount}人)";

        public string AvatarInitial => !string.IsNullOrWhiteSpace(GroupName) ? GroupName[..1].ToUpperInvariant() : "群";

        public string SubLine
        {
            get
            {
                var parts = new List<string>(3);
                parts.Add($"成员 {MemberCount} 人");
                if (HasMessages) parts.Add(EstimatedMessageCount > 0 ? $"群消息 {EstimatedMessageCount} 条" : "有群聊记录");
                else parts.Add("暂无本地群聊记录");
                if (!string.IsNullOrWhiteSpace(LastMessageSummary)) parts.Add(LastMessageSummary);
                return string.Join(" · ", parts);
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class WeChat4Message
    {
        public string ExternalId { get; init; } = string.Empty;
        public long LocalId { get; init; }
        public string ServerId { get; init; } = string.Empty;
        public int Shard { get; init; }
        public int BaseType { get; init; }
        public string Kind { get; init; } = "text";
        public string SenderWxid { get; init; } = string.Empty;
        public string SenderName { get; init; } = string.Empty;
        public bool IsOutgoing { get; init; }
        public string Content { get; init; } = string.Empty;
        public List<string> MediaHints { get; init; } = [];
        public long CreateTime { get; init; }
        public DateTimeOffset SentAtUtc => CreateTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(CreateTime)
            : DateTimeOffset.UtcNow;
    }

    public sealed class WeChat4SyncOptions
    {
        public bool ImportCustomers { get; set; } = true;
        public bool ImportGroups { get; set; } = true;
        public bool ImportCustomerMessages { get; set; } = true;
        public bool ImportGroupMessages { get; set; } = true;
        public bool SyncChatImages { get; set; } = true;
        public bool SyncLocalAvatars { get; set; } = true;
        public bool SkipSystemMessages { get; set; } = true;
        /// <summary>0 means unlimited (import all messages per session).</summary>
        public int MaxMessagesPerSession { get; set; } = 500;
        public HashSet<string>? SelectedCustomerUsernames { get; set; }
        public HashSet<string>? SelectedGroupUsernames { get; set; }
    }

    public sealed class WeChat4SyncProgress
    {
        public string Stage { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public int Percent { get; init; }
    }

    public sealed class WeChat4SyncReport
    {
        public string ScopeLabel { get; set; } = "同步";
        public bool IsBackgroundAuto { get; set; }
        public int CustomersCreated { get; set; }
        public int CustomersUpdated { get; set; }
        public int GroupsCreated { get; set; }
        public int GroupsUpdated { get; set; }
        public int GroupMembersSynced { get; set; }
        public int CustomerMessagesInserted { get; set; }
        public int CustomerMessagesSkipped { get; set; }
        public int GroupMessagesInserted { get; set; }
        public int GroupMessagesSkipped { get; set; }
        public int ChatImagesUploaded { get; set; }
        public int AvatarsUploaded { get; set; }
        public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.Now;
        public TimeSpan Elapsed { get; set; }
    }
}
