using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Client.Models
{
    public sealed class EmployeeIdentity
    {
        [JsonPropertyName("employee_id")]
        public string EmployeeId { get; set; } = string.Empty;

        [JsonPropertyName("account")]
        public string Account { get; set; } = string.Empty;

        [JsonPropertyName("display_name")]
        public string DisplayName { get; set; } = string.Empty;
    }

    public sealed class LoginRequest
    {
        [JsonPropertyName("account")]
        public string Account { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    public sealed class LoginResponse
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("expires_at")]
        public DateTimeOffset ExpiresAt { get; set; }

        [JsonPropertyName("employee")]
        public EmployeeIdentity Employee { get; set; } = new();
    }

    public sealed class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }

    public sealed class UploadAvatarRequest
    {
        [JsonPropertyName("image_base64")]
        public string ImageBase64 { get; set; } = string.Empty;
    }

    public sealed class UploadChatImageResponse
    {
        [JsonPropertyName("image_url")]
        public string ImageUrl { get; set; } = string.Empty;
    }

    public sealed class UploadChatAudioResponse
    {
        [JsonPropertyName("audio_url")]
        public string AudioUrl { get; set; } = string.Empty;
    }

    public sealed class UploadAudioRequest
    {
        [JsonPropertyName("audio_base64")]
        public string AudioBase64 { get; set; } = string.Empty;
    }

    public sealed class CustomerDto : INotifyPropertyChanged
    {
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public string Tags { get; set; } = string.Empty;

        [JsonPropertyName("follow_status")]
        public string FollowStatus { get; set; } = string.Empty;

        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("added_date")]
        public string AddedDate { get; set; } = string.Empty;

        [JsonPropertyName("last_message")]
        public string LastMessage { get; set; } = string.Empty;

        [JsonPropertyName("last_message_at")]
        public string LastMessageAt { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonIgnore]
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

        [JsonIgnore]
        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayName => !string.IsNullOrWhiteSpace(WechatName)
            ? WechatName
            : !string.IsNullOrWhiteSpace(WechatRemark)
                ? WechatRemark.Replace("\r", " ").Replace("\n", " ").Trim()
                : !string.IsNullOrWhiteSpace(WechatAccount) ? WechatAccount : $"客户 #{Id}";

        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "客";

        public string PreviewLine
        {
            get
            {
                var parts = new List<string>(2);
                if (!string.IsNullOrWhiteSpace(WechatAccount)) parts.Add(WechatAccount);
                if (!string.IsNullOrWhiteSpace(Region)) parts.Add(Region);
                if (parts.Count > 0) return string.Join(" · ", parts);
                if (!string.IsNullOrWhiteSpace(WechatRemark))
                    return $"备注：{WechatRemark.Replace("\r", " ").Replace("\n", " ").Trim()}";
                if (!string.IsNullOrWhiteSpace(Phone)) return Phone;
                return $"客户 #{Id}";
            }
        }

        public string TimeShort => FormatShortTime(string.IsNullOrWhiteSpace(LastMessageAt) ? UpdatedAt : LastMessageAt);

        internal static string FormatShortTime(string raw)
        {
            if (DateTimeOffset.TryParse(raw, out var dto))
            {
                var local = dto.ToLocalTime();
                return local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("MM/dd");
            }
            return string.Empty;
        }

        internal static string FormatMessageTime(string raw)
        {
            if (DateTimeOffset.TryParse(raw, out var dto))
            {
                var local = dto.ToLocalTime();
                return local.Date == DateTime.Today
                    ? local.ToString("HH:mm")
                    : local.ToString("yyyy-MM-dd HH:mm");
            }
            return string.Empty;
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class CustomerListResponse
    {
        [JsonPropertyName("items")]
        public List<CustomerDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateCustomerRequest
    {
        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public string Tags { get; set; } = string.Empty;

        [JsonPropertyName("follow_status")]
        public string FollowStatus { get; set; } = string.Empty;

        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("added_date")]
        public string AddedDate { get; set; } = string.Empty;

        [JsonPropertyName("allow_duplicate")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool AllowDuplicate { get; set; }
    }

    public sealed class MessageDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("customer_id")]
        public long CustomerId { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "incoming";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string ImageUrl { get; set; } = string.Empty;

        [JsonPropertyName("audio_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("video_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string VideoUrl { get; set; } = string.Empty;

        [JsonPropertyName("sent_at")]
        public string SentAt { get; set; } = string.Empty;

        public bool IsOutgoing => Direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
        public string TimeLabel => CustomerDto.FormatMessageTime(SentAt);
    }

    public sealed class MessageListResponse
    {
        [JsonPropertyName("items")]
        public List<MessageDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateMessageRequest
    {
        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "outgoing";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        public string ImageUrl { get; set; } = string.Empty;

        [JsonPropertyName("sent_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SentAt { get; set; }
    }

    public sealed class GroupDto : INotifyPropertyChanged
    {
        private ImageSource? _avatarImage;
        private bool _isSelectionMode;
        private bool _isChecked;
        private bool _isSelectedRow;

        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("my_nickname")]
        public string MyNickname { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("member_count")]
        public int MemberCount { get; set; }

        [JsonPropertyName("members_text")]
        public string MembersText { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;

        [JsonPropertyName("last_message")]
        public string LastMessage { get; set; } = string.Empty;

        [JsonPropertyName("last_message_at")]
        public string LastMessageAt { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("updated_at")]
        public string UpdatedAt { get; set; } = string.Empty;

        [JsonIgnore]
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

        [JsonIgnore]
        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public bool IsSelectionMode
        {
            get => _isSelectionMode;
            set
            {
                if (_isSelectionMode != value)
                {
                    _isSelectionMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CheckVisibility));
                }
            }
        }

        [JsonIgnore]
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

        [JsonIgnore]
        public bool IsSelectedRow
        {
            get => _isSelectedRow;
            set
            {
                if (_isSelectedRow != value)
                {
                    _isSelectedRow = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RowBackground));
                    OnPropertyChanged(nameof(RowBorderBrush));
                }
            }
        }

        [JsonIgnore]
        public Visibility CheckVisibility => _isSelectionMode ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Brush RowBackground => _isSelectedRow
            ? (Brush)Application.Current.Resources["BrandSoftBgBrush"]
            : new SolidColorBrush(Colors.Transparent);

        [JsonIgnore]
        public Brush RowBorderBrush => _isSelectedRow
            ? (Brush)Application.Current.Resources["BrandSoftBorderBrush"]
            : new SolidColorBrush(Colors.Transparent);

        public void NotifyRowVisualsChanged()
        {
            OnPropertyChanged(nameof(RowBackground));
            OnPropertyChanged(nameof(RowBorderBrush));
        }

        public string DisplayName => !string.IsNullOrWhiteSpace(GroupName) ? GroupName : $"群组 #{Id}";
        public string DisplayTitleWithCount => MemberCount > 0 ? $"{DisplayName} ({MemberCount})" : DisplayName;
        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "群";

        public string PreviewLine
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(LastMessage)) return LastMessage;
                if (MemberCount > 0) return $"{MemberCount} 位成员";
                if (!string.IsNullOrWhiteSpace(GroupKey)) return GroupKey;
                return $"群组 #{Id}";
            }
        }

        public string TimeShort => CustomerDto.FormatShortTime(string.IsNullOrWhiteSpace(LastMessageAt) ? UpdatedAt : LastMessageAt);

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class GroupListResponse
    {
        [JsonPropertyName("items")]
        public List<GroupDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateGroupRequest
    {
        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("my_nickname")]
        public string MyNickname { get; set; } = string.Empty;

        [JsonPropertyName("member_count")]
        public int MemberCount { get; set; }

        [JsonPropertyName("members_text")]
        public string MembersText { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;
    }

    public sealed class GroupMemberDto : INotifyPropertyChanged
    {
        private ImageSource? _avatarImage;

        public event PropertyChangedEventHandler? PropertyChanged;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("group_nickname")]
        public string GroupNickname { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;

        [JsonIgnore]
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

        [JsonIgnore]
        public Visibility AvatarImageVisibility => _avatarImage is not null ? Visibility.Visible : Visibility.Collapsed;

        [JsonIgnore]
        public Visibility AvatarInitialVisibility => _avatarImage is null ? Visibility.Visible : Visibility.Collapsed;

        public string DisplayName => !string.IsNullOrWhiteSpace(GroupNickname)
            ? GroupNickname
            : !string.IsNullOrWhiteSpace(WechatRemark)
                ? WechatRemark
                : !string.IsNullOrWhiteSpace(WechatName)
                    ? WechatName
                    : !string.IsNullOrWhiteSpace(WechatAccount) ? WechatAccount : $"成员 #{Id}";

        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "成";

        public string Subtitle
        {
            get
            {
                var parts = new List<string>(3);
                if (!string.IsNullOrWhiteSpace(WechatName) && !WechatName.Equals(DisplayName, StringComparison.OrdinalIgnoreCase))
                    parts.Add($"昵称:{WechatName}");
                if (!string.IsNullOrWhiteSpace(WechatAccount))
                    parts.Add(WechatAccount);
                if (!string.IsNullOrWhiteSpace(Region))
                    parts.Add(Region);
                if (parts.Count == 0 && !string.IsNullOrWhiteSpace(Phone))
                    parts.Add(Phone);
                return parts.Count > 0 ? string.Join(" · ", parts) : "群成员";
            }
        }

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class GroupMemberListResponse
    {
        [JsonPropertyName("items")]
        public List<GroupMemberDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateGroupMemberRequest
    {
        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("group_nickname")]
        public string GroupNickname { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("note")]
        public string Note { get; set; } = string.Empty;
    }

    public sealed class GroupMessageDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("group_id")]
        public long GroupId { get; set; }

        [JsonPropertyName("sender_name")]
        public string SenderName { get; set; } = string.Empty;

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "incoming";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string ImageUrl { get; set; } = string.Empty;

        [JsonPropertyName("audio_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("video_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string VideoUrl { get; set; } = string.Empty;

        [JsonPropertyName("sent_at")]
        public string SentAt { get; set; } = string.Empty;

        public bool IsOutgoing => Direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
        public string TimeLabel => CustomerDto.FormatMessageTime(SentAt);
    }

    public sealed class GroupMessageListResponse
    {
        [JsonPropertyName("items")]
        public List<GroupMessageDto> Items { get; set; } = [];

        [JsonPropertyName("next_cursor")]
        public long NextCursor { get; set; }
    }

    public sealed class CreateGroupMessageRequest
    {
        [JsonPropertyName("sender_name")]
        public string SenderName { get; set; } = string.Empty;

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "outgoing";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        public string ImageUrl { get; set; } = string.Empty;

        [JsonPropertyName("sent_at")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SentAt { get; set; }
    }

    public sealed class AuditLogDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("target_type")]
        public string TargetType { get; set; } = string.Empty;

        [JsonPropertyName("target_id")]
        public string TargetId { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string CreatedAt { get; set; } = string.Empty;
    }

    public sealed class CreateAuditLogRequest
    {
        [JsonPropertyName("action")]
        public string Action { get; set; } = string.Empty;

        [JsonPropertyName("target_type")]
        public string TargetType { get; set; } = string.Empty;

        [JsonPropertyName("target_id")]
        public string TargetId { get; set; } = string.Empty;

        [JsonPropertyName("detail")]
        public string Detail { get; set; } = string.Empty;
    }

    // ---- AI Gateway & Built-in Agent Skills DTOs ----

    public sealed class AiSkillDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;
    }

    public sealed class AiStatusResponse
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("configured")]
        public bool Configured { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("base_url")]
        public string BaseUrl { get; set; } = string.Empty;

        [JsonPropertyName("skills")]
        public List<AiSkillDto> Skills { get; set; } = [];
    }

    public sealed class AiChatTurnDto
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "user";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }

    public sealed class AiToolTraceDto
    {
        [JsonPropertyName("skill_id")]
        public string SkillId { get; set; } = string.Empty;

        [JsonPropertyName("skill_name")]
        public string SkillName { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("ok")]
        public bool Ok { get; set; }
    }

    public sealed class AiTranscribeResponse
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = string.Empty;

        [JsonPropertyName("latency_ms")]
        public long LatencyMs { get; set; }
    }

    public sealed class AiCustomerSummaryResponse
    {
        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("latency_ms")]
        public long LatencyMs { get; set; }

        [JsonPropertyName("message_count")]
        public int MessageCount { get; set; }
    }

    public sealed class AiChatResponse
    {
        [JsonPropertyName("reply")]
        public string Reply { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("latency_ms")]
        public long LatencyMs { get; set; }

        [JsonPropertyName("tool_trace")]
        public List<AiToolTraceDto> ToolTrace { get; set; } = [];
    }

    // ---- WeChat 4.x Direct DB Batch Sync DTOs ----

    public sealed class WeChatSyncCustomerPayload
    {
        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("tags")]
        public string Tags { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("phone")]
        public string Phone { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }

    public sealed class WeChatSyncCustomerResultDto
    {
        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("created")]
        public bool Created { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;
    }

    public sealed class WeChatSyncCustomersResponse
    {
        [JsonPropertyName("items")]
        public List<WeChatSyncCustomerResultDto> Items { get; set; } = [];

        [JsonPropertyName("created")]
        public int Created { get; set; }

        [JsonPropertyName("updated")]
        public int Updated { get; set; }
    }

    public sealed class WeChatSyncGroupMemberPayload
    {
        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("local_key")]
        public string LocalKey { get; set; } = string.Empty;

        [JsonPropertyName("group_nickname")]
        public string GroupNickname { get; set; } = string.Empty;

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_remark")]
        public string WechatRemark { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

        [JsonPropertyName("gender")]
        public string Gender { get; set; } = string.Empty;

        [JsonPropertyName("region")]
        public string Region { get; set; } = string.Empty;

        [JsonPropertyName("signature")]
        public string Signature { get; set; } = string.Empty;

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;
    }

    public sealed class WeChatSyncGroupPayload
    {
        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("my_nickname")]
        public string MyNickname { get; set; } = string.Empty;

        [JsonPropertyName("member_count")]
        public int MemberCount { get; set; }

        [JsonPropertyName("members_text")]
        public string MembersText { get; set; } = string.Empty;

        [JsonPropertyName("members")]
        public List<WeChatSyncGroupMemberPayload> Members { get; set; } = [];
    }

    public sealed class WeChatSyncGroupMemberResultDto
    {
        [JsonPropertyName("native_id")]
        public string NativeId { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("created")]
        public bool Created { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;
    }

    public sealed class WeChatSyncGroupResultDto
    {
        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("created")]
        public bool Created { get; set; }

        [JsonPropertyName("avatar_url")]
        public string AvatarUrl { get; set; } = string.Empty;

        [JsonPropertyName("members")]
        public List<WeChatSyncGroupMemberResultDto> Members { get; set; } = [];
    }

    public sealed class WeChatSyncGroupsResponse
    {
        [JsonPropertyName("items")]
        public List<WeChatSyncGroupResultDto> Items { get; set; } = [];

        [JsonPropertyName("created")]
        public int Created { get; set; }

        [JsonPropertyName("updated")]
        public int Updated { get; set; }
    }

    public sealed class WeChatSyncMessagePayload
    {
        [JsonPropertyName("external_id")]
        public string ExternalId { get; set; } = string.Empty;

        [JsonPropertyName("sender_name")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? SenderName { get; set; }

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = "incoming";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("image_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string ImageUrl { get; set; } = string.Empty;

        [JsonPropertyName("audio_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string AudioUrl { get; set; } = string.Empty;

        [JsonPropertyName("video_url")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string VideoUrl { get; set; } = string.Empty;

        [JsonPropertyName("sent_at")]
        public string SentAt { get; set; } = string.Empty;
    }

    public sealed class WeChatSyncMessagesResponse
    {
        [JsonPropertyName("inserted")]
        public int Inserted { get; set; }

        [JsonPropertyName("skipped")]
        public int Skipped { get; set; }
    }

    public sealed class WeChatExistingIdsResponse
    {
        [JsonPropertyName("existing_ids")]
        public List<string> ExistingIds { get; set; } = [];
    }

    public sealed class UploadChatVideoResponse
    {
        [JsonPropertyName("video_url")]
        public string VideoUrl { get; set; } = string.Empty;
    }
}
