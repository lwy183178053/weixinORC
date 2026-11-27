namespace Client.Services.WeComDb
{
    public sealed class WeComAccountDir
    {
        public string AccountId { get; init; } = string.Empty;
        public string AccountPath { get; init; } = string.Empty;
        public string DataPath { get; init; } = string.Empty;
        public string CachePath { get; init; } = string.Empty;
        public DateTimeOffset LastModified { get; init; }
        public int DbCount { get; init; }
    }

    public sealed class WeComProcessInfo
    {
        public int Pid { get; init; }
        public long MemoryKb { get; init; }
        public bool IsBroker { get; init; }
        public string ExePath { get; init; } = string.Empty;
    }

    public sealed class WeComUser
    {
        public string UserId { get; init; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string EnglishName { get; set; } = string.Empty;
        public string RealName { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Account { get; set; } = string.Empty;
        public string Mobile { get; set; } = string.Empty;
        public string Phone { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public string CorpId { get; set; } = string.Empty;
        public string ExternalCorpName { get; set; } = string.Empty;
        public string Position { get; set; } = string.Empty;
        public string UnionId { get; set; } = string.Empty;
        public int Gender { get; set; } // 0 unknown, 1 male, 2 female
        public bool IsExternal { get; set; }
        public long AddTime { get; set; }

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Remark) ? Remark :
            !string.IsNullOrWhiteSpace(Name) ? Name :
            !string.IsNullOrWhiteSpace(RealName) ? RealName :
            !string.IsNullOrWhiteSpace(EnglishName) ? EnglishName :
            UserId;
    }

    public sealed class WeComGroupMember
    {
        public string UserId { get; init; } = string.Empty;
        public string GroupNickname { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public bool IsAdmin { get; set; }
        public long JoinTime { get; set; }

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(GroupNickname) ? GroupNickname :
            !string.IsNullOrWhiteSpace(Remark) ? Remark :
            !string.IsNullOrWhiteSpace(Name) ? Name :
            UserId;
    }

    public sealed class WeComGroup
    {
        public string ConversationId { get; init; } = string.Empty; // e.g. "R:172055180323330"
        public string RoomId { get; init; } = string.Empty;         // e.g. "172055180323330"
        public string Name { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public string OwnerUserId { get; set; } = string.Empty;
        public string Announcement { get; set; } = string.Empty;
        public string WelcomeMessage { get; set; } = string.Empty;
        public string AvatarUrl { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
        public long CreateTime { get; set; }
        public long LastMessageTime { get; set; }
        public List<WeComGroupMember> Members { get; } = [];

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Remark) ? Remark :
            !string.IsNullOrWhiteSpace(Name) ? Name :
            Members.Count > 0 ? string.Join("、", Members.Take(3).Select(m => m.DisplayName)) :
            ConversationId;
    }

    public sealed class WeComMessage
    {
        public long MessageId { get; init; }
        public long ServerId { get; init; }
        public long Sequence { get; init; }
        public string ConversationId { get; init; } = string.Empty; // S:... or R:...
        public bool IsGroup { get; init; }
        public string PeerId { get; init; } = string.Empty; // peer user_id (S:) or chat_room_id (R:)
        public string SenderId { get; init; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public bool IsOutgoing { get; init; }
        public int ContentType { get; init; }
        public string Kind { get; init; } = "text"; // text/image/file/video/audio/emoji/system/unknown
        public string Content { get; init; } = string.Empty;
        public long SendTime { get; init; }
        public DateTimeOffset SentAtUtc => SendTime > 0
            ? DateTimeOffset.FromUnixTimeSeconds(SendTime)
            : DateTimeOffset.UtcNow;
        public List<string> AtUserIds { get; } = [];
        public string FileName { get; init; } = string.Empty;
        public long FileSize { get; init; }
        public string FileMd5 { get; init; } = string.Empty;
        public string CdnKey { get; init; } = string.Empty;
        public string AesKey { get; init; } = string.Empty;
        public string MediaUrl { get; init; } = string.Empty;
        public string LocalFilePath { get; set; } = string.Empty;
    }

    public sealed class WeComEvent
    {
        public string EventKind { get; init; } = string.Empty; // message, group_name_changed, group_member_joined, group_member_left, logout
        public WeComMessage? Message { get; init; }
        public string ConversationId { get; init; } = string.Empty;
        public string RoomId { get; init; } = string.Empty;
        public string OldGroupName { get; init; } = string.Empty;
        public string NewGroupName { get; init; } = string.Empty;
        public List<string> MemberIds { get; init; } = [];
    }
}
