using System.Text.Json.Serialization;

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

    public sealed class MeResponse
    {
        [JsonPropertyName("employee")]
        public EmployeeIdentity Employee { get; set; } = new();
    }

    public sealed class ApiErrorResponse
    {
        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }

    public sealed class CustomerDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

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

        public string DisplayName => !string.IsNullOrWhiteSpace(WechatName)
            ? WechatName
            : !string.IsNullOrWhiteSpace(WechatAccount) ? WechatAccount : $"客户 #{Id}";

        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "客";

        public string PreviewLine => !string.IsNullOrWhiteSpace(LastMessage)
            ? LastMessage
            : !string.IsNullOrWhiteSpace(Note)
                ? $"备注：{Note}"
                : !string.IsNullOrWhiteSpace(WechatAccount)
                    ? $"微信号：{WechatAccount}"
                    : "点击查看详情与聊天记录";

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
        [JsonPropertyName("wechat_name")]
        public string WechatName { get; set; } = string.Empty;

        [JsonPropertyName("wechat_account")]
        public string WechatAccount { get; set; } = string.Empty;

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

        [JsonPropertyName("sent_at")]
        public string SentAt { get; set; } = string.Empty;

        public bool IsOutgoing => Direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
        public string TimeLabel => CustomerDto.FormatShortTime(SentAt);
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
    }

    public sealed class GroupDto
    {
        [JsonPropertyName("id")]
        public long Id { get; set; }

        [JsonPropertyName("group_name")]
        public string GroupName { get; set; } = string.Empty;

        [JsonPropertyName("group_key")]
        public string GroupKey { get; set; } = string.Empty;

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

        public string DisplayName => !string.IsNullOrWhiteSpace(GroupName) ? GroupName : $"群组 #{Id}";
        public string DisplayTitleWithCount => MemberCount > 0 ? $"{DisplayName} ({MemberCount})" : DisplayName;
        public string AvatarInitial => DisplayName.Length > 0 ? DisplayName[..1].ToUpperInvariant() : "群";

        public string PreviewLine => !string.IsNullOrWhiteSpace(LastMessage)
            ? LastMessage
            : !string.IsNullOrWhiteSpace(Note)
                ? Note
                : MemberCount > 0 ? $"{MemberCount} 位群成员" : "点击查看群档案与聊天记录";

        public string TimeShort => CustomerDto.FormatShortTime(string.IsNullOrWhiteSpace(LastMessageAt) ? UpdatedAt : LastMessageAt);
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

        [JsonPropertyName("member_count")]
        public int MemberCount { get; set; }

        [JsonPropertyName("members_text")]
        public string MembersText { get; set; } = string.Empty;

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

        [JsonPropertyName("sent_at")]
        public string SentAt { get; set; } = string.Empty;

        public bool IsOutgoing => Direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
        public string TimeLabel => CustomerDto.FormatShortTime(SentAt);
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
    }
}
