using System.Text;
using Microsoft.Data.Sqlite;

namespace Client.Services.WeComDb
{
    /// <summary>
    /// Reads decrypted WeCom (WXWork 4.x/5.x) SQLite databases (user.db, session.db, message.db, file.db)
    /// into structured contacts, groups, group members, messages (with protobuf text/@mention/media parsing),
    /// and local cached media file resolution.
    /// </summary>
    public sealed class WeComReader : IDisposable
    {
        public string DecryptedDir { get; }
        public string AccountId { get; }
        public string AccountPath { get; }
        public string CachePath { get; }

        public WeComUser? SelfUser { get; private set; }
        public List<WeComUser> Users { get; } = [];
        public List<WeComGroup> Groups { get; } = [];

        private readonly Dictionary<string, WeComUser> _usersById = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WeComGroup> _groupsByConvId = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WeComGroup> _groupsByRoomId = new(StringComparer.OrdinalIgnoreCase);
        private SqliteConnection? _msgConn;
        private bool _disposed;

        public WeComReader(string decryptedDir, string accountId, string accountPath)
        {
            DecryptedDir = decryptedDir;
            AccountId = accountId;
            AccountPath = accountPath;
            CachePath = Path.Combine(accountPath, "Cache");
            LoadAll();
        }

        public WeComUser? FindUser(string? userId) =>
            !string.IsNullOrWhiteSpace(userId) && _usersById.TryGetValue(userId.Trim(), out var u) ? u : null;

        public WeComGroup? FindGroup(string? chatRoomIdOrConvId)
        {
            if (string.IsNullOrWhiteSpace(chatRoomIdOrConvId)) return null;
            string clean = chatRoomIdOrConvId.Trim();
            if (_groupsByConvId.TryGetValue(clean, out var g)) return g;
            if (_groupsByRoomId.TryGetValue(clean, out g)) return g;
            if (clean.StartsWith("R:", StringComparison.OrdinalIgnoreCase) &&
                _groupsByRoomId.TryGetValue(clean[2..], out g))
            {
                return g;
            }
            if (_groupsByConvId.TryGetValue("R:" + clean, out g)) return g;
            return null;
        }

        private void LoadAll()
        {
            string userDb = Path.Combine(DecryptedDir, "user.db");
            string sessionDb = Path.Combine(DecryptedDir, "session.db");
            string messageDb = Path.Combine(DecryptedDir, "message.db");

            if (File.Exists(userDb)) LoadUsers(userDb);
            if (File.Exists(sessionDb)) LoadGroupsAndSessions(sessionDb);
            if (File.Exists(messageDb))
            {
                _msgConn = OpenReadOnly(messageDb);
            }
        }

        private void LoadUsers(string userDbPath)
        {
            using var conn = OpenReadOnly(userDbPath);
            if (conn is null) return;
            var tables = ListTables(conn);

            var externalIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tables.Contains("external_user_ids"))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT value FROM external_user_ids";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string v = ReadSafeString(r, 0);
                        if (!string.IsNullOrWhiteSpace(v)) externalIds.Add(v);
                    }
                }
                catch { }
            }

            if (tables.Contains("user_table"))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT * FROM user_table";
                    using var r = cmd.ExecuteReader();
                    var cols = BuildColumnMap(r);
                    while (r.Read())
                    {
                        string uid = ReadColString(r, cols, "id");
                        if (string.IsNullOrWhiteSpace(uid)) continue;

                        var user = new WeComUser
                        {
                            UserId = uid,
                            Name = ReadColString(r, cols, "name"),
                            EnglishName = ReadColString(r, cols, "english_name"),
                            RealName = ReadColString(r, cols, "real_name"),
                            Account = ReadColString(r, cols, "account"),
                            Mobile = ReadColString(r, cols, "mobile"),
                            Phone = ReadColString(r, cols, "phone"),
                            Email = ReadColString(r, cols, "email"),
                            AvatarUrl = ReadColString(r, cols, "avator_url", "avatar_url"),
                            CorpId = ReadColString(r, cols, "corp_id"),
                            ExternalCorpName = ReadColString(r, cols, "external_corp_name"),
                            Position = ReadColString(r, cols, "position", "external_job"),
                            UnionId = ReadColString(r, cols, "unionid"),
                            Gender = (int)ReadColLong(r, cols, "gender"),
                            IsExternal = externalIds.Contains(uid)
                        };

                        if (string.IsNullOrWhiteSpace(user.Phone) && !string.IsNullOrWhiteSpace(user.Mobile))
                        {
                            user.Phone = user.Mobile;
                        }

                        _usersById[uid] = user;
                        if (string.Equals(uid, AccountId, StringComparison.OrdinalIgnoreCase))
                        {
                            SelfUser = user;
                        }
                        else
                        {
                            Users.Add(user);
                        }
                    }
                }
                catch { }
            }

            // Enrich external contact remarks, phones, and add_customer_time
            if (tables.Contains("external_user_relation_v3"))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT * FROM external_user_relation_v3";
                    using var r = cmd.ExecuteReader();
                    var cols = BuildColumnMap(r);
                    while (r.Read())
                    {
                        string uid = ReadColString(r, cols, "user_id");
                        if (string.IsNullOrWhiteSpace(uid)) continue;

                        if (!_usersById.TryGetValue(uid, out var user))
                        {
                            user = new WeComUser { UserId = uid, IsExternal = true };
                            _usersById[uid] = user;
                            Users.Add(user);
                        }

                        user.IsExternal = true;
                        string remark = ReadColString(r, cols, "real_remarks", "remarks");
                        if (!string.IsNullOrWhiteSpace(remark)) user.Remark = remark;

                        string corpRemark = ReadColString(r, cols, "corp_remark");
                        if (!string.IsNullOrWhiteSpace(corpRemark)) user.Description = corpRemark;

                        string phone1 = ReadColString(r, cols, "remark_phone_1");
                        if (!string.IsNullOrWhiteSpace(phone1) && string.IsNullOrWhiteSpace(user.Phone))
                        {
                            user.Phone = phone1;
                        }

                        long addTime = ReadColLong(r, cols, "add_customer_time", "create_time");
                        if (addTime > 0) user.AddTime = addTime;
                    }
                }
                catch { }
            }

            // Ensure SelfUser always exists
            SelfUser ??= new WeComUser { UserId = AccountId, Name = AccountId };
        }

        private void LoadGroupsAndSessions(string sessionDbPath)
        {
            using var conn = OpenReadOnly(sessionDbPath);
            if (conn is null) return;
            var tables = ListTables(conn);
            if (!tables.Contains("conversation_table")) return;

            var memberNicks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (tables.Contains("conversation_member_nickname_table"))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT room_id, userid, nickname FROM conversation_member_nickname_table";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string roomId = ReadSafeString(r, 0);
                        string uid = ReadSafeString(r, 1);
                        string nick = ReadSafeString(r, 2);
                        if (!string.IsNullOrWhiteSpace(roomId) && !string.IsNullOrWhiteSpace(uid) && !string.IsNullOrWhiteSpace(nick))
                        {
                            memberNicks[$"{roomId}|{uid}"] = nick;
                        }
                    }
                }
                catch { }
            }

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM conversation_table WHERE id LIKE 'R:%' OR id LIKE 'S:%'";
                using var r = cmd.ExecuteReader();
                var cols = BuildColumnMap(r);
                while (r.Read())
                {
                    string convId = ReadColString(r, cols, "id");
                    if (string.IsNullOrWhiteSpace(convId)) continue;

                    if (convId.StartsWith("R:", StringComparison.OrdinalIgnoreCase))
                    {
                        string roomId = convId[2..];
                        var grp = new WeComGroup
                        {
                            ConversationId = convId,
                            RoomId = roomId,
                            Name = ReadColString(r, cols, "name"),
                            Remark = ReadColString(r, cols, "roomname_remark"),
                            OwnerUserId = ReadColString(r, cols, "create_user_id"),
                            Announcement = ReadColString(r, cols, "notice_content"),
                            WelcomeMessage = ReadColString(r, cols, "room_welcome_message"),
                            AvatarUrl = ReadColString(r, cols, "personal_room_avatar_url"),
                            IsPinned = ReadColLong(r, cols, "is_sticked") > 0,
                            CreateTime = ReadColLong(r, cols, "create_time"),
                            LastMessageTime = ReadColLong(r, cols, "last_message_time")
                        };

                        _groupsByConvId[convId] = grp;
                        _groupsByRoomId[roomId] = grp;
                        Groups.Add(grp);
                    }
                    else if (convId.StartsWith("S:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ensure single-chat peers also appear in Users list even if absent from user_table
                        string peerId = ExtractPeerIdFromSingleConv(convId, AccountId);
                        if (!string.IsNullOrWhiteSpace(peerId) && !_usersById.ContainsKey(peerId))
                        {
                            var u = new WeComUser { UserId = peerId, Name = ReadColString(r, cols, "name") };
                            _usersById[peerId] = u;
                            Users.Add(u);
                        }
                    }
                }
            }
            catch { }

            if (tables.Contains("conversation_user_table"))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT * FROM conversation_user_table";
                    using var r = cmd.ExecuteReader();
                    var cols = BuildColumnMap(r);
                    while (r.Read())
                    {
                        string convId = ReadColString(r, cols, "conversation_id");
                        string uid = ReadColString(r, cols, "user_id");
                        if (string.IsNullOrWhiteSpace(convId) || string.IsNullOrWhiteSpace(uid)) continue;
                        if (!_groupsByConvId.TryGetValue(convId, out var grp)) continue;

                        _usersById.TryGetValue(uid, out var user);
                        string nick = ReadColString(r, cols, "nick_name");
                        if (string.IsNullOrWhiteSpace(nick) && memberNicks.TryGetValue($"{grp.RoomId}|{uid}", out var extraNick))
                        {
                            nick = extraNick;
                        }

                        grp.Members.Add(new WeComGroupMember
                        {
                            UserId = uid,
                            GroupNickname = nick,
                            Name = user?.Name ?? string.Empty,
                            Remark = user?.Remark ?? string.Empty,
                            AvatarUrl = user?.AvatarUrl ?? string.Empty,
                            IsAdmin = ReadColLong(r, cols, "is_admin") > 0 || string.Equals(uid, grp.OwnerUserId, StringComparison.OrdinalIgnoreCase),
                            JoinTime = ReadColLong(r, cols, "join_time")
                        });
                    }
                }
                catch { }
            }

            Groups.Sort((a, b) => b.LastMessageTime.CompareTo(a.LastMessageTime));
        }

        public long GetLatestMessageId()
        {
            if (_msgConn is null) return 0;
            try
            {
                using var cmd = _msgConn.CreateCommand();
                cmd.CommandText = "SELECT IFNULL(MAX(message_id), 0) FROM message_table WHERE conversation_id LIKE 'S:%' OR conversation_id LIKE 'R:%'";
                return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
            }
            catch
            {
                return 0;
            }
        }

        public List<WeComMessage> ReadMessagesAfter(long afterMessageId, int limit = 200)
        {
            var list = new List<WeComMessage>();
            if (_msgConn is null) return list;
            try
            {
                using var cmd = _msgConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT message_id, server_id, sequence, sender_id, conversation_id, content_type, send_time, content, extra_content
                    FROM message_table
                    WHERE (conversation_id LIKE 'S:%' OR conversation_id LIKE 'R:%')
                      AND message_id > @after
                    ORDER BY message_id ASC
                    LIMIT @lim";
                cmd.Parameters.AddWithValue("@after", afterMessageId);
                cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 1000));
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var msg = ParseMessageRow(r);
                    if (msg is not null) list.Add(msg);
                }
            }
            catch { }
            return list;
        }

        public bool HasRecentOutgoingMessageMatching(string conversationId, string expectedText, long minUnixSeconds)
        {
            if (_msgConn is null || string.IsNullOrWhiteSpace(conversationId)) return false;
            string expectedNorm = NormalizeForMatch(expectedText);

            try
            {
                using var cmd = _msgConn.CreateCommand();
                cmd.CommandText = @"
                    SELECT message_id, server_id, sequence, sender_id, conversation_id, content_type, send_time, content, extra_content
                    FROM message_table
                    WHERE conversation_id = @cid AND send_time >= @minTs
                    ORDER BY message_id DESC
                    LIMIT 15";
                cmd.Parameters.AddWithValue("@cid", conversationId);
                cmd.Parameters.AddWithValue("@minTs", minUnixSeconds);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var msg = ParseMessageRow(r);
                    if (msg is null || !msg.IsOutgoing) continue;
                    if (string.IsNullOrEmpty(expectedNorm)) return true; // Media / file send check
                    string actualNorm = NormalizeForMatch(msg.Content);
                    if (actualNorm.Contains(expectedNorm, StringComparison.OrdinalIgnoreCase) ||
                        expectedNorm.Contains(actualNorm, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public string BuildSingleConversationId(string peerUserId)
        {
            string peer = (peerUserId ?? string.Empty).Trim();
            if (peer.StartsWith("S:", StringComparison.OrdinalIgnoreCase)) return peer;
            // WeCom S: conversations order the two IDs lexicographically or numerically
            string candidate1 = $"S:{peer}_{AccountId}";
            string candidate2 = $"S:{AccountId}_{peer}";
            if (_msgConn is not null)
            {
                try
                {
                    using var cmd = _msgConn.CreateCommand();
                    cmd.CommandText = "SELECT conversation_id FROM message_table WHERE conversation_id IN (@c1, @c2) LIMIT 1";
                    cmd.Parameters.AddWithValue("@c1", candidate1);
                    cmd.Parameters.AddWithValue("@c2", candidate2);
                    var hit = cmd.ExecuteScalar()?.ToString();
                    if (!string.IsNullOrWhiteSpace(hit)) return hit;
                }
                catch { }
            }
            return string.Compare(peer, AccountId, StringComparison.Ordinal) <= 0 ? candidate1 : candidate2;
        }

        public string? TryLocateLocalMediaFile(string? md5, string? fileName, string? cdnKey, string kind)
        {
            if (!Directory.Exists(CachePath)) return null;
            string[] subDirs = kind switch
            {
                "image" => ["Image", "File"],
                "video" => ["Video", "File"],
                "audio" => ["Voice", "Audio", "File"],
                _ => ["File", "Image", "Video"]
            };

            string cleanMd5 = (md5 ?? string.Empty).Trim().ToLowerInvariant();
            string cleanName = (fileName ?? string.Empty).Trim();

            foreach (string sub in subDirs)
            {
                string root = Path.Combine(CachePath, sub);
                if (!Directory.Exists(root)) continue;
                try
                {
                    // Search newest month folders first
                    var dirs = Directory.GetDirectories(root)
                        .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                        .Prepend(root);

                    foreach (string dir in dirs)
                    {
                        if (!string.IsNullOrEmpty(cleanName))
                        {
                            string direct = Path.Combine(dir, cleanName);
                            if (File.Exists(direct) && new FileInfo(direct).Length > 0) return direct;
                        }
                        if (!string.IsNullOrEmpty(cleanMd5) && cleanMd5.Length >= 16)
                        {
                            foreach (string f in Directory.EnumerateFiles(dir, $"*{cleanMd5}*"))
                            {
                                if (new FileInfo(f).Length > 0) return f;
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private WeComMessage? ParseMessageRow(SqliteDataReader r)
        {
            long msgId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
            long svrId = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            long seq = r.IsDBNull(2) ? 0 : r.GetInt64(2);
            string senderId = ReadSafeString(r, 3);
            string convId = ReadSafeString(r, 4);
            int ct = r.IsDBNull(5) ? 0 : r.GetInt32(5);
            long sendTime = r.IsDBNull(6) ? 0 : r.GetInt64(6);
            byte[] contentBlob = ReadSafeBytes(r, 7);

            bool isGroup = convId.StartsWith("R:", StringComparison.OrdinalIgnoreCase);
            string peerId = isGroup ? convId[2..] : ExtractPeerIdFromSingleConv(convId, AccountId);
            bool isOutgoing = string.Equals(senderId, AccountId, StringComparison.OrdinalIgnoreCase);
            string senderName = _usersById.TryGetValue(senderId, out var u) ? u.DisplayName : senderId;

            string kind = "text";
            string text = string.Empty;
            string fileName = string.Empty;
            long fileSize = 0;
            string md5 = string.Empty;
            string cdnKey = string.Empty;
            string aesKey = string.Empty;
            string mediaUrl = string.Empty;
            var atIds = new List<string>();

            switch (ct)
            {
                case 0: // Plain text
                case 2: // Rich text + @mentions
                    kind = "text";
                    text = ParseRichTextProtobuf(contentBlob, atIds);
                    break;
                case 14: // Image
                    kind = "image";
                    ParseMediaFields(contentBlob, out cdnKey, out fileName, out fileSize, out md5, out aesKey, out mediaUrl);
                    text = string.IsNullOrWhiteSpace(fileName) ? "[图片]" : $"[图片: {fileName}]";
                    break;
                case 15:
                case 20: // File
                    kind = "file";
                    ParseMediaFields(contentBlob, out cdnKey, out fileName, out fileSize, out md5, out aesKey, out mediaUrl);
                    text = string.IsNullOrWhiteSpace(fileName) ? "[文件]" : $"[文件: {fileName}]";
                    break;
                case 16: // Voice
                    kind = "audio";
                    ParseMediaFields(contentBlob, out cdnKey, out fileName, out fileSize, out md5, out aesKey, out mediaUrl);
                    text = "[语音]";
                    break;
                case 22: // Video
                    kind = "video";
                    ParseVideoFields(contentBlob, out cdnKey, out fileSize, out md5, out mediaUrl);
                    text = "[视频]";
                    break;
                case 29: // Emoji / Sticker
                    kind = "emoji";
                    ParseEmojiFields(contentBlob, out mediaUrl, out cdnKey, out md5, out aesKey, out fileSize);
                    text = "[动画表情]";
                    break;
                case 123: // Mixed text + image
                    kind = "text";
                    text = ParseMixedMessageProtobuf(contentBlob, atIds, out cdnKey, out fileName, out fileSize, out md5, out aesKey);
                    break;
                case 1011:
                case 1022:
                case 1073:
                case 1006:
                    kind = "system";
                    text = ExtractReadableStrings(contentBlob);
                    break;
                default:
                    kind = "unknown";
                    text = ExtractReadableStrings(contentBlob);
                    break;
            }

            var msg = new WeComMessage
            {
                MessageId = msgId,
                ServerId = svrId,
                Sequence = seq,
                ConversationId = convId,
                IsGroup = isGroup,
                PeerId = peerId,
                SenderId = senderId,
                SenderName = senderName,
                IsOutgoing = isOutgoing,
                ContentType = ct,
                Kind = kind,
                Content = text,
                SendTime = sendTime,
                FileName = fileName,
                FileSize = fileSize,
                FileMd5 = md5,
                CdnKey = cdnKey,
                AesKey = aesKey,
                MediaUrl = mediaUrl
            };
            msg.AtUserIds.AddRange(atIds);

            if (kind is "image" or "file" or "video" or "audio")
            {
                msg.LocalFilePath = TryLocateLocalMediaFile(md5, fileName, cdnKey, kind) ?? string.Empty;
            }

            return msg;
        }

        // ---- Lightweight Zero-Dependency Protobuf Wire Decoders ----

        private static string ParseRichTextProtobuf(byte[] blob, List<string> atUserIds)
        {
            if (blob.Length == 0) return string.Empty;
            var sb = new StringBuilder();
            foreach (var f in ReadPbFields(blob))
            {
                if (f.FieldNumber != 1 || f.WireType != 2 || f.Bytes is null) continue;
                int segType = -1;
                byte[]? segPayload = null;
                foreach (var sf in ReadPbFields(f.Bytes))
                {
                    if (sf.FieldNumber == 1 && sf.WireType == 0) segType = (int)sf.Varint;
                    else if (sf.FieldNumber == 2 && sf.WireType == 2) segPayload = sf.Bytes;
                }
                if (segPayload is null) continue;

                if (segType == 0) // Text segment: either raw UTF-8 or nested { f1: text }
                {
                    string segText = TryDecodeNestedText(segPayload);
                    if (!string.IsNullOrEmpty(segText)) sb.Append(segText);
                }
                else if (segType == 5) // @mention segment: { f1: varint user_id (0=all), f2: "@display" }
                {
                    long targetUid = -1;
                    string mentionText = string.Empty;
                    foreach (var mf in ReadPbFields(segPayload))
                    {
                        if (mf.FieldNumber == 1 && mf.WireType == 0) targetUid = (long)mf.Varint;
                        else if (mf.FieldNumber == 2 && mf.WireType == 2 && mf.Bytes is not null)
                        {
                            mentionText = TryUtf8(mf.Bytes);
                        }
                    }
                    if (targetUid == 0) atUserIds.Add("notify@all");
                    else if (targetUid > 0) atUserIds.Add(targetUid.ToString());

                    if (!string.IsNullOrEmpty(mentionText))
                    {
                        if (!mentionText.StartsWith('@')) sb.Append('@');
                        sb.Append(mentionText);
                        if (!mentionText.EndsWith(' ')) sb.Append(' ');
                    }
                }
                else if (segType == 3) // Built-in emoji
                {
                    string emoji = TryDecodeNestedText(segPayload);
                    if (!string.IsNullOrEmpty(emoji)) sb.Append(emoji);
                }
            }

            if (sb.Length == 0) return ExtractReadableStrings(blob);
            return sb.ToString().Trim();
        }

        private static string ParseMixedMessageProtobuf(
            byte[] blob,
            List<string> atIds,
            out string cdnKey,
            out string fileName,
            out long fileSize,
            out string md5,
            out string aesKey)
        {
            cdnKey = fileName = md5 = aesKey = string.Empty;
            fileSize = 0;
            var sb = new StringBuilder();

            foreach (var f in ReadPbFields(blob))
            {
                if (f.FieldNumber != 1 || f.WireType != 2 || f.Bytes is null) continue;
                int itemType = 0;
                byte[]? textPart = null;
                byte[]? imgPart = null;
                foreach (var sf in ReadPbFields(f.Bytes))
                {
                    if (sf.FieldNumber == 5 && sf.WireType == 0) itemType = (int)sf.Varint;
                    else if (sf.FieldNumber == 101 && sf.WireType == 2) textPart = sf.Bytes;
                    else if (sf.FieldNumber == 103 && sf.WireType == 2) imgPart = sf.Bytes;
                }

                if (textPart is not null)
                {
                    string t = ParseRichTextProtobuf(textPart, atIds);
                    if (!string.IsNullOrEmpty(t))
                    {
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append(t);
                    }
                }
                if (imgPart is not null && string.IsNullOrEmpty(cdnKey))
                {
                    ParseMediaFields(imgPart, out cdnKey, out fileName, out fileSize, out md5, out aesKey, out _);
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append("[图片]");
                }
            }

            return sb.ToString().Trim();
        }

        private static void ParseMediaFields(
            byte[] blob,
            out string cdnKey,
            out string fileName,
            out long fileSize,
            out string md5,
            out string aesKey,
            out string mediaUrl)
        {
            cdnKey = fileName = md5 = aesKey = mediaUrl = string.Empty;
            fileSize = 0;
            foreach (var f in ReadPbFields(blob))
            {
                if (f.WireType == 2 && f.Bytes is not null)
                {
                    string s = TryUtf8(f.Bytes);
                    if (string.IsNullOrEmpty(s)) continue;
                    switch (f.FieldNumber)
                    {
                        case 1: cdnKey = s; break;
                        case 2: fileName = s; break;
                        case 8: md5 = s; break;
                        case 10: aesKey = s; break;
                        case 202 or 203 when string.IsNullOrEmpty(mediaUrl) && s.StartsWith("http", StringComparison.OrdinalIgnoreCase):
                            mediaUrl = s;
                            break;
                    }
                }
                else if (f.WireType == 0 && f.FieldNumber == 4)
                {
                    fileSize = (long)f.Varint;
                }
            }
        }

        private static void ParseVideoFields(
            byte[] blob,
            out string cdnKey,
            out long fileSize,
            out string md5,
            out string mediaUrl)
        {
            cdnKey = md5 = mediaUrl = string.Empty;
            fileSize = 0;
            foreach (var f in ReadPbFields(blob))
            {
                if (f.WireType == 2 && f.Bytes is not null)
                {
                    string s = TryUtf8(f.Bytes);
                    if (string.IsNullOrEmpty(s)) continue;
                    switch (f.FieldNumber)
                    {
                        case 1 or 2 when string.IsNullOrEmpty(cdnKey): cdnKey = s; break;
                        case 7 when s.StartsWith("http", StringComparison.OrdinalIgnoreCase): mediaUrl = s; break;
                        case 9 or 18 when string.IsNullOrEmpty(md5): md5 = s; break;
                    }
                }
                else if (f.WireType == 0 && f.FieldNumber == 3)
                {
                    fileSize = (long)f.Varint;
                }
            }
        }

        private static void ParseEmojiFields(
            byte[] blob,
            out string mediaUrl,
            out string cdnKey,
            out string md5,
            out string aesKey,
            out long fileSize)
        {
            mediaUrl = cdnKey = md5 = aesKey = string.Empty;
            fileSize = 0;
            foreach (var f in ReadPbFields(blob))
            {
                if (f.WireType == 2 && f.Bytes is not null)
                {
                    string s = TryUtf8(f.Bytes);
                    if (string.IsNullOrEmpty(s)) continue;
                    switch (f.FieldNumber)
                    {
                        case 1 when s.StartsWith("http", StringComparison.OrdinalIgnoreCase): mediaUrl = s; break;
                        case 3: cdnKey = s; break;
                        case 5: md5 = s; break;
                        case 6: aesKey = s; break;
                    }
                }
                else if (f.WireType == 0 && f.FieldNumber == 4)
                {
                    fileSize = (long)f.Varint;
                }
            }
        }

        private static string TryDecodeNestedText(byte[] payload)
        {
            // Check if payload is a sub-message { f1: utf8-string } first
            var fields = ReadPbFields(payload);
            if (fields.Count == 1 && fields[0].FieldNumber == 1 && fields[0].WireType == 2 && fields[0].Bytes is byte[] nestedBytes)
            {
                string nested = TryUtf8(nestedBytes);
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
            return TryUtf8(payload);
        }

        private static string ExtractReadableStrings(byte[] blob)
        {
            if (blob.Length == 0) return string.Empty;
            var parts = new List<string>();
            foreach (var f in ReadPbFields(blob))
            {
                if (f.WireType == 2 && f.Bytes is not null)
                {
                    string s = TryUtf8(f.Bytes);
                    if (!string.IsNullOrWhiteSpace(s) && s.Length >= 2)
                    {
                        parts.Add(s.Trim());
                    }
                }
            }
            return string.Join(" ", parts.Take(4));
        }

        private readonly record struct PbField(int FieldNumber, int WireType, ulong Varint, byte[]? Bytes);

        private static List<PbField> ReadPbFields(byte[] data)
        {
            var list = new List<PbField>();
            int i = 0, n = data.Length;
            try
            {
                while (i < n && list.Count < 128)
                {
                    if (!TryReadVarint(data, ref i, out ulong tag)) return [];
                    int fn = (int)(tag >> 3);
                    int wt = (int)(tag & 7);
                    if (fn <= 0 || fn > 100000) return [];
                    switch (wt)
                    {
                        case 0:
                            if (!TryReadVarint(data, ref i, out ulong v)) return [];
                            list.Add(new PbField(fn, wt, v, null));
                            break;
                        case 1:
                            if (i + 8 > n) return [];
                            i += 8;
                            list.Add(new PbField(fn, wt, 0, null));
                            break;
                        case 2:
                            if (!TryReadVarint(data, ref i, out ulong lenU) || lenU > (ulong)(n - i)) return [];
                            int len = (int)lenU;
                            byte[] sub = new byte[len];
                            Buffer.BlockCopy(data, i, sub, 0, len);
                            i += len;
                            list.Add(new PbField(fn, wt, 0, sub));
                            break;
                        case 5:
                            if (i + 4 > n) return [];
                            i += 4;
                            list.Add(new PbField(fn, wt, 0, null));
                            break;
                        default:
                            return [];
                    }
                }
            }
            catch
            {
                return [];
            }
            return list;
        }

        private static bool TryReadVarint(byte[] data, ref int idx, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (idx < data.Length && shift < 64)
            {
                byte b = data[idx++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static string TryUtf8(byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;
            try
            {
                string s = new UTF8Encoding(false, true).GetString(bytes);
                foreach (char c in s)
                {
                    if (char.IsControl(c) && c is not ('\r' or '\n' or '\t'))
                    {
                        return string.Empty;
                    }
                }
                return s;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractPeerIdFromSingleConv(string convId, string selfId)
        {
            if (!convId.StartsWith("S:", StringComparison.OrdinalIgnoreCase)) return convId;
            string[] parts = convId[2..].Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                return string.Equals(parts[0], selfId, StringComparison.OrdinalIgnoreCase) ? parts[1] : parts[0];
            }
            return convId[2..];
        }

        private static string NormalizeForMatch(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static SqliteConnection? OpenReadOnly(string path)
        {
            try
            {
                var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
                conn.Open();
                return conn;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<string> ListTables(SqliteConnection conn)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string name = ReadSafeString(r, 0);
                if (!string.IsNullOrWhiteSpace(name)) set.Add(name);
            }
            return set;
        }

        private static Dictionary<string, int> BuildColumnMap(SqliteDataReader r)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < r.FieldCount; i++) map[r.GetName(i)] = i;
            return map;
        }

        private static string ReadColString(SqliteDataReader r, Dictionary<string, int> cols, params string[] names)
        {
            foreach (string name in names)
            {
                if (cols.TryGetValue(name, out int idx))
                {
                    string v = ReadSafeString(r, idx);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            return string.Empty;
        }

        private static long ReadColLong(SqliteDataReader r, Dictionary<string, int> cols, params string[] names)
        {
            foreach (string name in names)
            {
                if (cols.TryGetValue(name, out int idx) && !r.IsDBNull(idx))
                {
                    try { return Convert.ToInt64(r.GetValue(idx)); } catch { }
                }
            }
            return 0;
        }

        private static string ReadSafeString(SqliteDataReader r, int idx)
        {
            if (r.IsDBNull(idx)) return string.Empty;
            object val = r.GetValue(idx);
            if (val is string s) return s;
            if (val is byte[] b) return TryUtf8(b);
            return Convert.ToString(val) ?? string.Empty;
        }

        private static byte[] ReadSafeBytes(SqliteDataReader r, int idx)
        {
            if (r.IsDBNull(idx)) return [];
            object val = r.GetValue(idx);
            if (val is byte[] b) return b;
            if (val is string s) return Encoding.UTF8.GetBytes(s);
            return [];
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _msgConn?.Dispose(); } catch { }
            _msgConn = null;
        }
    }
}
