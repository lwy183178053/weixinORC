using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using ZstdSharp;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Reads decrypted Windows WeChat 4.x SQLite databases (contact.db, session.db, head_image.db,
    /// message_N.db, media_N.db) into structured contacts, tags, groups, group members, avatars,
    /// rich formatted messages (text, emojis, images, voice, video, files, quotes, forwards, transfers),
    /// and local image attachment bytes.
    /// </summary>
    public sealed partial class WeChat4Reader : IDisposable
    {
        private static readonly byte[] ZstdMagic = [0x28, 0xB5, 0x2F, 0xFD];

        private static readonly HashSet<string> SystemUsernames = new(StringComparer.OrdinalIgnoreCase)
        {
            "filehelper", "fmessage", "medianote", "newsapp", "notification_messages",
            "weixin", "qqmail", "tmessage", "qmessage", "floatbottle", "shakeapp",
            "lbsapp", "qqsync", "feedsapp", "voipapp", "blogapp", "brandsessionholder",
            "weixinguanhaozhushou", "weixinreminder", "officialaccounts", "notifymessage",
            "mphelper", "chatroom_session_box", "brandservice"
        };

        private static readonly Dictionary<int, string> TypeNames = new()
        {
            [1] = "文本", [3] = "图片", [34] = "语音", [37] = "好友请求", [42] = "名片",
            [43] = "视频", [47] = "动画表情", [48] = "位置", [49] = "应用消息", [50] = "音视频通话",
            [51] = "状态", [62] = "小视频", [66] = "连线", [10000] = "系统消息", [10002] = "系统消息",
            [11000] = "动画表情", [11001] = "引用消息", [11002] = "文件"
        };

        private static readonly Dictionary<int, string> AppMsgSubTypes = new()
        {
            [1] = "文本", [2] = "图片", [3] = "音乐", [4] = "视频", [5] = "链接", [6] = "文件",
            [7] = "合并转发", [8] = "动画表情", [9] = "小程序", [10] = "视频号", [11] = "视频号直播",
            [13] = "卡包", [14] = "聊天记录", [15] = "位置共享", [16] = "音乐", [17] = "位置共享",
            [19] = "合并转发聊天记录", [20] = "聊天记录", [21] = "卡包", [24] = "笔记",
            [33] = "小程序", [36] = "小程序", [44] = "微信红包封面", [48] = "视频号",
            [51] = "视频号直播", [53] = "接龙", [57] = "引用消息", [62] = "视频号",
            [63] = "直播", [87] = "群公告", [88] = "直播",
            [2000] = "转账", [2001] = "红包", [2003] = "红包"
        };

        [GeneratedRegex(@"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)", RegexOptions.Compiled)]
        private static partial Regex BareAmpRegex();

        [GeneratedRegex(@"^message(?:_\d+)?\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MessageDbNameRegex();

        [GeneratedRegex(@"^media(?:_\d+)?\.db$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
        private static partial Regex MediaDbNameRegex();

        [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.@-]{1,190}$", RegexOptions.Compiled)]
        private static partial Regex SenderPrefixRegex();

        [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)\$|\$\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled)]
        private static partial Regex SysTemplateVarRegex();

        [GeneratedRegex(@"^[0-9a-fA-F]{32}$", RegexOptions.Compiled)]
        private static partial Regex Md5HexRegex();

        public string DecryptedDir { get; }
        public string AccountPath { get; }
        public string SelfWxid { get; private set; }
        public WeChat4Contact? SelfContact { get; private set; }
        public List<WeChat4Contact> Friends { get; } = [];
        public List<WeChat4Group> Groups { get; } = [];
        public Dictionary<string, string> LabelNames { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, WeChat4Contact> _allContactsByUsername = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WeChat4Contact> _allContactsByRowId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> _groupMemberSet = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Path, SqliteConnection Conn, HashSet<string> Tables, Dictionary<long, string> Name2Id)> _msgDbs = [];
        private readonly List<(SqliteConnection Conn, HashSet<string> Tables)> _mediaDbs = [];
        private readonly List<(SqliteConnection Conn, List<string> Tables)> _headImageDbs = [];
        private SqliteConnection? _resourceConn;
        private readonly Dictionary<string, long> _resourceChatName2Id = new(StringComparer.OrdinalIgnoreCase);
        private bool _hasResourceDetailTable;
        private readonly WeChat4MediaDecoder _mediaDecoder;
        private readonly ConcurrentDictionary<string, byte[]?> _avatarBytesCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HttpClient AvatarHttpClient = new() { Timeout = TimeSpan.FromSeconds(4) };
        private readonly Decompressor _zstd = new();
        private readonly object _zstdLock = new();
        private bool _disposed;

        public WeChat4Reader(string decryptedDir, string wxid, string accountPath = "", Action<string>? progress = null)
        {
            DecryptedDir = decryptedDir;
            AccountPath = accountPath;
            SelfWxid = WeChat4Paths.SplitWxid(wxid);
            _mediaDecoder = new WeChat4MediaDecoder(accountPath, SelfWxid);

            LoadAll(progress);
        }

        private void LoadAll(Action<string>? progress)
        {
            string? contactDb = null;
            string? sessionDb = null;
            string? resourceDb = null;
            var msgDbPaths = new List<string>();
            var mediaDbPaths = new List<string>();
            var headDbPaths = new List<string>();

            foreach (var path in Directory.EnumerateFiles(DecryptedDir, "*.db", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(path).ToLowerInvariant();
                if (name == "contact.db") contactDb = path;
                else if (name == "session.db") sessionDb = path;
                else if (name == "message_resource.db") resourceDb = path;
                else if (name.Contains("head") || name.Contains("avatar")) headDbPaths.Add(path);
                else if (MessageDbNameRegex().IsMatch(name)) msgDbPaths.Add(path);
                else if (MediaDbNameRegex().IsMatch(name)) mediaDbPaths.Add(path);
            }

            if (resourceDb is not null)
            {
                LoadResourceDbIndex(resourceDb);
            }

            msgDbPaths.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var p in msgDbPaths)
            {
                var conn = OpenReadOnly(p);
                if (conn is null) continue;
                var tables = ListTables(conn);
                var n2i = LoadName2Id(conn, tables);
                _msgDbs.Add((p, conn, tables, n2i));
            }

            foreach (var p in mediaDbPaths)
            {
                var conn = OpenReadOnly(p);
                if (conn is null) continue;
                _mediaDbs.Add((conn, ListTables(conn)));
            }

            foreach (var p in headDbPaths)
            {
                var conn = OpenReadOnly(p);
                if (conn is null) continue;
                _headImageDbs.Add((conn, ListTables(conn).ToList()));
            }

            if (contactDb is not null)
            {
                LoadContactsAndRooms(contactDb, progress);
            }
            else
            {
                progress?.Invoke("警告：未在解密结果中找到 contact.db");
            }

            if (sessionDb is not null)
            {
                LoadSessionMetadata(sessionDb);
            }

            AnnotateMessageCounts(progress);

            Friends.Sort((a, b) =>
            {
                int cmp = b.LastMessageTime.CompareTo(a.LastMessageTime);
                if (cmp != 0) return cmp;
                cmp = b.HasMessages.CompareTo(a.HasMessages);
                if (cmp != 0) return cmp;
                return string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase);
            });

            Groups.Sort((a, b) =>
            {
                int cmp = b.LastMessageTime.CompareTo(a.LastMessageTime);
                if (cmp != 0) return cmp;
                cmp = b.HasMessages.CompareTo(a.HasMessages);
                if (cmp != 0) return cmp;
                return string.Compare(a.GroupName, b.GroupName, StringComparison.CurrentCultureIgnoreCase);
            });

            int groupsWithMsg = Groups.Count(g => g.HasMessages);
            progress?.Invoke($"解析完成：{Friends.Count} 位好友、{LabelNames.Count} 个微信标签、{Groups.Count} 个群聊（其中 {groupsWithMsg} 个有群聊记录）、{_msgDbs.Count} 个消息库");
        }

        /// <summary>
        /// Builds the tag selector list for tag-driven customer import.
        /// </summary>
        public List<WeChat4TagOption> BuildTagOptions(ISet<string>? preselectedTags = null, bool preselectUntagged = false)
        {
            var tagStats = new Dictionary<string, (int Total, int WithMsgs)>(StringComparer.OrdinalIgnoreCase);
            foreach (var tagName in LabelNames.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                tagStats[tagName] = (0, 0);
            }

            int untaggedTotal = 0, untaggedWithMsgs = 0;
            foreach (var f in Friends)
            {
                if (f.TagList.Count == 0)
                {
                    untaggedTotal++;
                    if (f.HasMessages) untaggedWithMsgs++;
                    continue;
                }
                foreach (var t in f.TagList)
                {
                    tagStats.TryGetValue(t, out var cur);
                    tagStats[t] = (cur.Total + 1, cur.WithMsgs + (f.HasMessages ? 1 : 0));
                }
            }

            var options = tagStats
                .OrderByDescending(kv => kv.Value.Total)
                .ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(kv => new WeChat4TagOption
                {
                    TagName = kv.Key,
                    IsUntaggedBucket = false,
                    ContactCount = kv.Value.Total,
                    ContactsWithMessagesCount = kv.Value.WithMsgs,
                    IsSelected = preselectedTags is not null && preselectedTags.Contains(kv.Key)
                })
                .ToList();

            if (untaggedTotal > 0)
            {
                options.Add(new WeChat4TagOption
                {
                    TagName = "未打标签好友",
                    IsUntaggedBucket = true,
                    ContactCount = untaggedTotal,
                    ContactsWithMessagesCount = untaggedWithMsgs,
                    IsSelected = preselectUntagged
                });
            }

            return options;
        }

        private void LoadContactsAndRooms(string contactDbPath, Action<string>? progress)
        {
            using var conn = OpenReadOnly(contactDbPath);
            if (conn is null) return;
            var tables = ListTables(conn);

            foreach (var labelTable in tables.Where(t => t.Contains("label", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{labelTable.Replace("\"", "\"\"")}\"";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string id = GetColString(r, "label_id_", "label_id", "labelid", "id");
                        string name = GetColString(r, "label_name_", "label_name", "labelname", "name");
                        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
                        {
                            LabelNames[id.Trim()] = name.Trim();
                        }
                    }
                }
                catch { }
            }

            foreach (var table in new[] { "contact", "stranger" })
            {
                if (!tables.Contains(table)) continue;
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT * FROM \"{table}\"";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string username = GetColString(r, "username", "user_name");
                        if (string.IsNullOrWhiteSpace(username)) continue;

                        string rowId = GetColString(r, "id");
                        string alias = GetColString(r, "alias");
                        string nick = GetColString(r, "nick_name", "nickname");
                        string remark = GetColString(r, "remark");
                        string desc = GetColString(r, "description");
                        string bigHead = GetColString(r, "big_head_url");
                        string smallHead = GetColString(r, "small_head_url");
                        int verifyFlag = GetColInt(r, "verify_flag", "verifyflag");
                        int localType = GetColInt(r, "local_type", "type");
                        string rawLabels = GetColString(r, "label_id_list", "label_id_list_", "labelidlist", "label_ids", "label_id", "contact_label_list");
                        byte[]? extraBuf = GetColBytes(r, "extra_buffer");

                        var (gender, region, signature, phone, extraDesc, extraLabelIds) = ParseContactExtraBuffer(extraBuf);
                        if (string.IsNullOrWhiteSpace(desc) && !string.IsNullOrWhiteSpace(extraDesc))
                        {
                            desc = extraDesc;
                        }
                        if (string.IsNullOrWhiteSpace(rawLabels) && !string.IsNullOrWhiteSpace(extraLabelIds))
                        {
                            rawLabels = extraLabelIds;
                        }

                        var tagList = ResolveLabelList(rawLabels);
                        string tags = string.Join(", ", tagList);
                        bool isGroup = username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);
                        bool isOfficial = username.StartsWith("gh_", StringComparison.OrdinalIgnoreCase) || (verifyFlag & 8) != 0;
                        bool isSystem = SystemUsernames.Contains(username) || username.StartsWith("fake_", StringComparison.OrdinalIgnoreCase);

                        bool isSelf = !string.IsNullOrWhiteSpace(SelfWxid) &&
                                      username.Equals(SelfWxid, StringComparison.OrdinalIgnoreCase);
                        bool isFriend = table == "contact" && !isGroup && !isOfficial && !isSystem && !isSelf &&
                                        (localType == 0 || localType == 1 || localType == 3 || localType == 5 || !string.IsNullOrWhiteSpace(remark));

                        var contact = new WeChat4Contact
                        {
                            Username = username,
                            Alias = alias,
                            NickName = nick,
                            Remark = remark,
                            Gender = gender,
                            Region = region,
                            Signature = signature,
                            Description = desc,
                            Tags = tags,
                            TagList = tagList,
                            Phone = phone,
                            BigHeadUrl = bigHead,
                            SmallHeadUrl = smallHead,
                            VerifyFlag = verifyFlag,
                            LocalType = localType,
                            IsFriend = isFriend,
                            IsGroup = isGroup,
                            IsOfficial = isOfficial,
                            IsSystem = isSystem
                        };

                        if (!string.IsNullOrEmpty(rowId))
                        {
                            _allContactsByRowId.TryAdd(rowId, contact);
                        }
                        if (!_allContactsByUsername.ContainsKey(username))
                        {
                            _allContactsByUsername[username] = contact;
                            if (isFriend)
                            {
                                Friends.Add(contact);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    progress?.Invoke($"读取 {table} 表时出现异常：{ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(SelfWxid) && _allContactsByUsername.TryGetValue(SelfWxid, out var me))
            {
                SelfContact = me;
            }

            if (tables.Contains("chat_room"))
            {
                var roomById = new Dictionary<string, (string Username, string Owner, string DisplayName, Dictionary<string, string> MemberNicks)>();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT * FROM \"chat_room\"";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        string username = GetColString(r, "username", "user_name");
                        if (string.IsNullOrWhiteSpace(username) || !username.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string id = GetColString(r, "id");
                        string owner = GetColString(r, "owner");
                        byte[]? extBuf = GetColBytes(r, "ext_buffer");
                        var memberNicks = ParseChatRoomMemberDisplayNames(extBuf);

                        string name = string.Empty;
                        if (_allContactsByUsername.TryGetValue(username, out var roomContact))
                        {
                            name = !string.IsNullOrWhiteSpace(roomContact.Remark)
                                ? roomContact.Remark
                                : roomContact.NickName;
                        }
                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = GetColString(r, "nick_name", "remark", "name");
                        }

                        if (!string.IsNullOrEmpty(id))
                        {
                            roomById[id] = (username, owner, name, memberNicks);
                        }
                    }
                }
                catch { }

                var membersByRoom = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                if (tables.Contains("chatroom_member"))
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "SELECT room_id, member_id FROM \"chatroom_member\"";
                        using var r = cmd.ExecuteReader();
                        while (r.Read())
                        {
                            string roomId = GetColString(r, "room_id");
                            string memId = GetColString(r, "member_id");
                            if (!roomById.TryGetValue(roomId, out var roomInfo)) continue;
                            if (!_allContactsByRowId.TryGetValue(memId, out var memContact)) continue;

                            if (!membersByRoom.TryGetValue(roomInfo.Username, out var list))
                            {
                                list = [];
                                membersByRoom[roomInfo.Username] = list;
                            }
                            list.Add(memContact.Username);
                        }
                    }
                    catch { }
                }

                foreach (var kv in roomById.Values)
                {
                    var memberUsernames = membersByRoom.TryGetValue(kv.Username, out var rawMembers)
                        ? rawMembers.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                        : [];

                    _groupMemberSet[kv.Username] = new HashSet<string>(memberUsernames, StringComparer.OrdinalIgnoreCase);
                    var members = new List<WeChat4GroupMember>(memberUsernames.Count);
                    string myNick = string.Empty;

                    foreach (var mWxid in memberUsernames)
                    {
                        _allContactsByUsername.TryGetValue(mWxid, out var mc);
                        kv.MemberNicks.TryGetValue(mWxid, out var gNick);
                        gNick ??= string.Empty;

                        if (!string.IsNullOrWhiteSpace(SelfWxid) && mWxid.Equals(SelfWxid, StringComparison.OrdinalIgnoreCase))
                        {
                            myNick = !string.IsNullOrWhiteSpace(gNick) ? gNick : (mc?.NickName ?? string.Empty);
                        }

                        members.Add(new WeChat4GroupMember
                        {
                            Username = mWxid,
                            Alias = mc?.Alias ?? string.Empty,
                            NickName = mc?.NickName ?? string.Empty,
                            Remark = mc?.Remark ?? string.Empty,
                            GroupNickname = gNick,
                            Gender = mc?.Gender ?? string.Empty,
                            Region = mc?.Region ?? string.Empty,
                            Signature = mc?.Signature ?? string.Empty,
                            Role = !string.IsNullOrWhiteSpace(kv.Owner) && mWxid.Equals(kv.Owner, StringComparison.OrdinalIgnoreCase) ? "群主" : string.Empty,
                            AvatarUrl = mc?.AvatarUrl ?? string.Empty
                        });
                    }

                    string groupName = kv.DisplayName;
                    if (string.IsNullOrWhiteSpace(groupName))
                    {
                        var sampleNames = members.Take(3).Select(m => m.DisplayName).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
                        groupName = sampleNames.Count > 0
                            ? string.Join("、", sampleNames)
                            : kv.Username.Replace("@chatroom", "", StringComparison.OrdinalIgnoreCase);
                    }

                    _allContactsByUsername.TryGetValue(kv.Username, out var roomContactForAvatar);
                    Groups.Add(new WeChat4Group
                    {
                        Username = kv.Username,
                        GroupName = groupName,
                        MyNickname = myNick,
                        AvatarUrl = roomContactForAvatar?.AvatarUrl ?? string.Empty,
                        MemberCount = members.Count,
                        Members = members
                    });
                }
            }
        }

        private void LoadSessionMetadata(string sessionDbPath)
        {
            using var conn = OpenReadOnly(sessionDbPath);
            if (conn is null) return;
            var tables = ListTables(conn);
            if (!tables.Contains("SessionTable")) return;

            var friendMap = Friends.ToDictionary(f => f.Username, StringComparer.OrdinalIgnoreCase);
            var groupMap = Groups.ToDictionary(g => g.Username, StringComparer.OrdinalIgnoreCase);

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT * FROM \"SessionTable\"";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    string username = GetColString(r, "username");
                    if (string.IsNullOrWhiteSpace(username)) continue;
                    string summary = WeChat4EmojiMap.FormatWeChatFaces(DecodeValueToText(GetRawColumn(r, "summary")));
                    if (summary.Length > 100) summary = summary[..100];
                    long lastTime = GetColLong(r, "sort_timestamp", "last_timestamp");

                    if (friendMap.TryGetValue(username, out var f))
                    {
                        f.LastMessageSummary = summary;
                        f.LastMessageTime = Math.Max(f.LastMessageTime, lastTime);
                    }
                    else if (groupMap.TryGetValue(username, out var g))
                    {
                        g.LastMessageSummary = summary;
                        g.LastMessageTime = Math.Max(g.LastMessageTime, lastTime);
                    }
                }
            }
            catch { }
        }

        private void AnnotateMessageCounts(Action<string>? progress)
        {
            if (_msgDbs.Count == 0) return;
            progress?.Invoke("正在统计好友与群聊的历史消息数量...");

            foreach (var f in Friends)
            {
                int cnt = CountMessagesForSession(f.Username);
                f.HasMessages = cnt > 0;
                f.EstimatedMessageCount = cnt;
            }
            foreach (var g in Groups)
            {
                int cnt = CountMessagesForSession(g.Username);
                g.HasMessages = cnt > 0;
                g.EstimatedMessageCount = cnt;
            }
        }

        public int CountMessagesForSession(string username)
        {
            string table = MsgTableFor(username);
            int total = 0;
            foreach (var (_, conn, tables, _) in _msgDbs)
            {
                if (!tables.Contains(table)) continue;
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM \"{table}\"";
                    var v = cmd.ExecuteScalar();
                    if (v is long l) total += (int)l;
                    else if (v is int i) total += i;
                }
                catch { }
            }
            return total;
        }

        public byte[]? TryResolveMessageImageBytes(string sessionUsername, WeChat4Message msg)
        {
            if (msg.Kind is not ("image" or "emoji") || msg.MediaHints.Count == 0) return null;
            return _mediaDecoder.TryResolveSessionImage(sessionUsername, msg.MediaHints);
        }

        /// <summary>
        /// Resolves local image / animated emoji bytes first from disk (.dat), then falls back to
        /// trusted Tencent CDN emoji URLs (cdnurl/thumburl) for animated stickers not cached locally.
        /// </summary>
        public async Task<byte[]?> TryResolveMessageImageBytesAsync(string sessionUsername, WeChat4Message msg, CancellationToken ct = default)
        {
            byte[]? local = await Task.Run(() => TryResolveMessageImageBytes(sessionUsername, msg), ct);
            if (local is { Length: > 0 }) return local;

            if (msg.Kind == "emoji")
            {
                foreach (var hint in msg.MediaHints)
                {
                    if (string.IsNullOrWhiteSpace(hint) || !hint.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!IsTrustedTencentAvatarUrl(hint)) continue;
                    try
                    {
                        using var resp = await AvatarHttpClient.GetAsync(hint, ct);
                        if (!resp.IsSuccessStatusCode) continue;
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        if (bytes.Length is >= 32 and <= 4 * 1024 * 1024 && IsValidImageBytes(bytes))
                        {
                            return bytes;
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        public (byte[]? CoverImageBytes, string? VideoFilePath) TryResolveVideoAssets(WeChat4Message msg)
        {
            if (msg.Kind != "video" || msg.MediaHints.Count == 0) return (null, null);
            return _mediaDecoder.TryResolveVideoAssets(msg.MediaHints);
        }

        /// <summary>
        /// Reads voice audio blob from decrypted media_N.db (VoiceInfo) for a voice message
        /// and decodes Tencent SILK v3 into playable 24kHz mono PCM WAV bytes.
        /// </summary>
        public byte[]? TryResolveVoiceWavBytes(string sessionUsername, WeChat4Message msg)
        {
            if (msg.Kind != "voice" || string.IsNullOrWhiteSpace(msg.ServerId)) return null;
            if (!long.TryParse(msg.ServerId, out long svrId) &&
                !(ulong.TryParse(msg.ServerId, out ulong uSvr) && (svrId = unchecked((long)uSvr)) != 0))
            {
                return null;
            }
            if (svrId == 0) return null;

            byte[]? rawBlob = TryReadVoiceBlob(sessionUsername, svrId);
            if (rawBlob is null || rawBlob.Length == 0) return null;
            return WeChat4MediaDecoder.DecodeSilkToWav(rawBlob);
        }

        private byte[]? TryReadVoiceBlob(string sessionUsername, long svrId)
        {
            bool mapped = false;
            foreach (var (conn, tables) in _mediaDbs)
            {
                if (!tables.Contains("VoiceInfo")) continue;
                try
                {
                    if (tables.Contains("Name2Id"))
                    {
                        using var idCmd = conn.CreateCommand();
                        idCmd.CommandText = "SELECT rowid FROM Name2Id WHERE user_name = @u LIMIT 1";
                        idCmd.Parameters.AddWithValue("@u", sessionUsername);
                        var rowIdObj = idCmd.ExecuteScalar();
                        if (rowIdObj is long chatNameId)
                        {
                            mapped = true;
                            using var vCmd = conn.CreateCommand();
                            vCmd.CommandText = "SELECT voice_data FROM VoiceInfo WHERE chat_name_id = @cid AND svr_id = @sid AND length(voice_data) BETWEEN 1 AND 16777216 ORDER BY create_time DESC LIMIT 1";
                            vCmd.Parameters.AddWithValue("@cid", chatNameId);
                            vCmd.Parameters.AddWithValue("@sid", svrId);
                            if (vCmd.ExecuteScalar() is byte[] blob && blob.Length > 0)
                            {
                                return blob;
                            }
                        }
                    }
                }
                catch { }
            }

            if (!mapped && svrId > 0)
            {
                foreach (var (conn, tables) in _mediaDbs)
                {
                    if (!tables.Contains("VoiceInfo")) continue;
                    try
                    {
                        using var vCmd = conn.CreateCommand();
                        vCmd.CommandText = "SELECT voice_data FROM VoiceInfo WHERE svr_id = @sid AND length(voice_data) BETWEEN 1 AND 16777216 LIMIT 2";
                        vCmd.Parameters.AddWithValue("@sid", svrId);
                        using var r = vCmd.ExecuteReader();
                        byte[]? found = null;
                        while (r.Read())
                        {
                            if (r.GetValue(0) is byte[] b && b.Length > 0)
                            {
                                if (found is not null) return null; // ambiguous
                                found = b;
                            }
                        }
                        if (found is not null) return found;
                    }
                    catch { }
                }
            }
            return null;
        }

        public List<WeChat4Message> ReadMessages(string sessionUsername, int maxCount = 0, bool skipSystem = true, CancellationToken ct = default)
        {
            string table = MsgTableFor(sessionUsername);
            var all = new List<WeChat4Message>();
            bool isGroup = sessionUsername.EndsWith("@chatroom", StringComparison.OrdinalIgnoreCase);

            for (int shard = 0; shard < _msgDbs.Count; shard++)
            {
                ct.ThrowIfCancellationRequested();
                var (_, conn, tables, name2Id) = _msgDbs[shard];
                if (!tables.Contains(table)) continue;

                try
                {
                    var tableCols = ListColumns(conn, table);
                    string Sel(string col, string fallback) => tableCols.Contains(col) ? $"\"{col}\"" : fallback;
                    string orderBy = tableCols.Contains("sort_seq")
                        ? "ORDER BY sort_seq ASC, local_id ASC"
                        : (tableCols.Contains("create_time") ? "ORDER BY create_time ASC, local_id ASC" : "ORDER BY local_id ASC");

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText =
                        $"SELECT {Sel("local_id", "rowid")}, {Sel("server_id", "0")}, {Sel("local_type", "1")}, " +
                        $"{Sel("sort_seq", "0")}, {Sel("real_sender_id", "0")}, {Sel("create_time", "0")}, " +
                        $"{Sel("message_content", "NULL")}, {Sel("compress_content", "NULL")}, {Sel("packed_info_data", "NULL")} " +
                        $"FROM \"{table}\" {orderBy}";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        long localId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                        string serverId = r.IsDBNull(1) ? string.Empty : Convert.ToString(r.GetValue(1)) ?? string.Empty;
                        long rawType = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                        long realSenderId = r.IsDBNull(4) ? 0 : r.GetInt64(4);
                        long createTime = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                        object? rawMsgContent = r.IsDBNull(6) ? null : r.GetValue(6);
                        object? rawCompress = r.IsDBNull(7) ? null : r.GetValue(7);
                        byte[]? packedInfo = r.IsDBNull(8) ? null : r.GetValue(8) as byte[];

                        int baseType = NormalizeLocalType(rawType);
                        if (skipSystem && baseType is 10000 or 10002 or 51) continue;

                        string body = DecodeContent(rawMsgContent, rawCompress);
                        name2Id.TryGetValue(realSenderId, out string? sender);
                        sender ??= string.Empty;

                        if (isGroup)
                        {
                            StripGroupSenderPrefix(sessionUsername, name2Id, ref sender, ref body);
                        }

                        var (kind, text) = DescribeMessage(baseType, rawType, body, packedInfo);
                        if (skipSystem && kind == "system") continue;
                        if (string.IsNullOrWhiteSpace(text)) continue;

                        text = WeChat4EmojiMap.FormatWeChatFaces(text);
                        var hints = kind is "image" or "emoji" or "video" ? ExtractMediaHints(body, packedInfo) : [];
                        if (kind is "image" or "emoji" or "video")
                        {
                            EnrichMediaHintsFromResourceDb(sessionUsername, localId, serverId, hints);
                        }

                        bool isOutgoing = !string.IsNullOrEmpty(sender) &&
                                          !string.IsNullOrEmpty(SelfWxid) &&
                                          sender.Equals(SelfWxid, StringComparison.OrdinalIgnoreCase);

                        string senderName = !string.IsNullOrEmpty(sender)
                            ? ResolveDisplayName(sender)
                            : (kind == "system" ? "系统" : "群成员");

                        string extId = !string.IsNullOrWhiteSpace(serverId) && serverId != "0"
                            ? $"wx4:s:{serverId}"
                            : $"wx4:l:{shard}:{localId}:{createTime}";

                        all.Add(new WeChat4Message
                        {
                            ExternalId = extId,
                            LocalId = localId,
                            ServerId = serverId,
                            Shard = shard,
                            BaseType = baseType,
                            Kind = kind,
                            SenderWxid = sender,
                            SenderName = senderName,
                            IsOutgoing = isOutgoing,
                            Content = text,
                            MediaHints = hints,
                            CreateTime = createTime
                        });
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("WX4", $"读取消息表 {table} (分片 {shard}) 失败：{ex.Message}");
                }
            }

            all.Sort((a, b) =>
            {
                int cmp = a.CreateTime.CompareTo(b.CreateTime);
                if (cmp != 0) return cmp;
                cmp = a.Shard.CompareTo(b.Shard);
                if (cmp != 0) return cmp;
                return a.LocalId.CompareTo(b.LocalId);
            });

            if (maxCount > 0 && all.Count > maxCount)
            {
                return all.GetRange(all.Count - maxCount, maxCount);
            }
            return all;
        }

        /// <summary>
        /// Gets avatar image bytes for a friend or group (@chatroom):
        /// 1. Checks local head_image.db first (fast, offline).
        /// 2. Falls back to downloading big_head_url / small_head_url from trusted Tencent CDN hosts.
        /// </summary>
        public async Task<byte[]?> TryGetAvatarBytesAsync(string username, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;
            if (_avatarBytesCache.TryGetValue(username, out var cached)) return cached;

            byte[]? local = TryGetLocalAvatarBytes(username);
            if (local is { Length: > 0 })
            {
                _avatarBytesCache[username] = local;
                return local;
            }

            if (_allContactsByUsername.TryGetValue(username, out var contact) &&
                !string.IsNullOrWhiteSpace(contact.AvatarUrl) &&
                IsTrustedTencentAvatarUrl(contact.AvatarUrl))
            {
                try
                {
                    using var resp = await AvatarHttpClient.GetAsync(contact.AvatarUrl, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        byte[] bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                        if (bytes.Length is >= 32 and <= 2 * 1024 * 1024 && IsValidImageBytes(bytes))
                        {
                            _avatarBytesCache[username] = bytes;
                            return bytes;
                        }
                    }
                }
                catch { }
            }

            _avatarBytesCache[username] = null;
            return null;
        }

        /// <summary>
        /// Gets avatar image bytes for a WeChat group (@chatroom):
        /// 1. Tries direct group avatar from head_image.db / contact.db head URL.
        /// 2. If not cached yet by WeChat, composes a classic WeChat 9-grid (up to 9 members) PNG collage.
        /// </summary>
        public async Task<byte[]?> TryGetGroupAvatarOrCollageBytesAsync(string groupUsername, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(groupUsername)) return null;

            byte[]? direct = await TryGetAvatarBytesAsync(groupUsername, ct);
            if (direct is { Length: > 0 }) return direct;

            string collageKey = "__collage__:" + groupUsername;
            if (_avatarBytesCache.TryGetValue(collageKey, out var cachedCollage)) return cachedCollage;

            var group = Groups.FirstOrDefault(g => g.Username.Equals(groupUsername, StringComparison.OrdinalIgnoreCase));
            if (group is null || group.Members.Count == 0)
            {
                _avatarBytesCache[collageKey] = null;
                return null;
            }

            var memberAvatars = new List<byte[]>(9);
            foreach (var member in group.Members)
            {
                if (memberAvatars.Count >= 9) break;
                ct.ThrowIfCancellationRequested();
                byte[]? mBytes = await TryGetAvatarBytesAsync(member.Username, ct);
                if (mBytes is { Length: > 0 })
                {
                    memberAvatars.Add(mBytes);
                }
            }

            byte[]? collage = memberAvatars.Count > 0
                ? await AvatarImageService.BuildWeChatGroupCollagePngAsync(memberAvatars, 192)
                : null;

            _avatarBytesCache[collageKey] = collage;
            return collage;
        }

        private static bool IsTrustedTencentAvatarUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            string host = uri.Host.ToLowerInvariant();
            return host == "qpic.cn" || host.EndsWith(".qpic.cn", StringComparison.Ordinal) ||
                   host == "qlogo.cn" || host.EndsWith(".qlogo.cn", StringComparison.Ordinal) ||
                   host == "weixin.qq.com" || host.EndsWith(".weixin.qq.com", StringComparison.Ordinal) ||
                   host == "wxapp.tc.qq.com" || host.EndsWith(".tc.qq.com", StringComparison.Ordinal);
        }

        public byte[]? TryGetLocalAvatarBytes(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            foreach (var (conn, tables) in _headImageDbs)
            {
                foreach (var table in tables)
                {
                    try
                    {
                        var cols = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        using (var infoCmd = conn.CreateCommand())
                        {
                            infoCmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                            using var ir = infoCmd.ExecuteReader();
                            while (ir.Read())
                            {
                                string colName = ir.GetString(1);
                                cols[colName] = colName;
                            }
                        }

                        string? userCol = new[] { "username", "user_name", "usrname", "wxid" }
                            .FirstOrDefault(c => cols.ContainsKey(c));
                        if (userCol is null) continue;

                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" WHERE \"{cols[userCol]}\" = @u LIMIT 1";
                        cmd.Parameters.AddWithValue("@u", username);
                        using var r = cmd.ExecuteReader();
                        if (r.Read())
                        {
                            for (int i = 0; i < r.FieldCount; i++)
                            {
                                if (r.IsDBNull(i)) continue;
                                if (r.GetValue(i) is byte[] bytes && bytes.Length is >= 32 and <= 2 * 1024 * 1024 && IsValidImageBytes(bytes))
                                {
                                    return bytes;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        public string ResolveDisplayName(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return string.Empty;
            if (_allContactsByUsername.TryGetValue(username, out var c))
            {
                return c.DisplayName;
            }
            if (!string.IsNullOrEmpty(SelfWxid) && username.Equals(SelfWxid, StringComparison.OrdinalIgnoreCase))
            {
                return "我";
            }
            return username;
        }

        public static string MsgTableFor(string username)
        {
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(username));
            return "Msg_" + Convert.ToHexString(hash).ToLowerInvariant();
        }

        private void StripGroupSenderPrefix(
            string sessionUsername,
            Dictionary<long, string> id2Name,
            ref string sender,
            ref string content)
        {
            int limit = Math.Min(content.Length, 256);
            int nl = content.IndexOf('\n', 0, limit);
            if (nl <= 1) return;

            string head = content[..nl].TrimEnd('\r');
            if (!head.EndsWith(':')) return;

            string maybe = head[..^1];
            if (!SenderPrefixRegex().IsMatch(maybe)) return;

            bool known = maybe.Equals(sender, StringComparison.OrdinalIgnoreCase)
                || _allContactsByUsername.ContainsKey(maybe)
                || id2Name.Values.Contains(maybe, StringComparer.OrdinalIgnoreCase)
                || (_groupMemberSet.TryGetValue(sessionUsername, out var mset) && mset.Contains(maybe));

            bool recognizable = string.IsNullOrEmpty(sender) &&
                (maybe.StartsWith("wxid_", StringComparison.OrdinalIgnoreCase) ||
                 maybe.EndsWith("@openim", StringComparison.OrdinalIgnoreCase));

            if (known || recognizable)
            {
                if (string.IsNullOrEmpty(sender)) sender = maybe;
                content = content[(nl + 1)..];
            }
        }

        private string DecodeContent(object? messageContent, object? compressContent)
        {
            foreach (var raw in new[] { messageContent, compressContent })
            {
                if (raw is byte[] b && b.Length >= 4 && b.AsSpan(0, 4).SequenceEqual(ZstdMagic))
                {
                    string s = DecodeValueToText(b);
                    if (!string.IsNullOrWhiteSpace(s)) return s;
                }
            }
            foreach (var raw in new[] { compressContent, messageContent })
            {
                string s = DecodeValueToText(raw);
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
            return string.Empty;
        }

        private string DecodeValueToText(object? value)
        {
            if (value is null || value is DBNull) return string.Empty;
            if (value is string s) return s;
            if (value is byte[] bytes)
            {
                if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(ZstdMagic))
                {
                    try
                    {
                        lock (_zstdLock)
                        {
                            var decompressed = _zstd.Unwrap(bytes);
                            return Encoding.UTF8.GetString(decompressed);
                        }
                    }
                    catch
                    {
                        return string.Empty;
                    }
                }

                string txt = Encoding.UTF8.GetString(bytes);
                int lt = txt.IndexOf('<');
                if (lt is > 0 and <= 24) txt = txt[lt..];
                int end = txt.IndexOf("\u0001\u0000", StringComparison.Ordinal);
                if (end > 0) txt = txt[..end];
                return txt;
            }
            return Convert.ToString(value) ?? string.Empty;
        }

        private (string Kind, string Text) DescribeMessage(int mtype, long rawType, string content, byte[]? packedInfo)
        {
            switch (mtype)
            {
                case 1:
                    return ("text", content);
                case 3:
                    return ("image", "🖼️ [图片]");
                case 43 or 62:
                {
                    var root = ParseXmlSafe(content);
                    string lenStr = XmlAttrOrText(root, "playlength");
                    string dur = int.TryParse(lenStr, out int sec) && sec > 0 ? $" {sec}\"" : string.Empty;
                    return ("video", $"🎬 [视频{dur}]");
                }
                case 34:
                {
                    var root = ParseXmlSafe(content);
                    string secStr = XmlAttrOrText(root, "voicelength");
                    string dur = int.TryParse(secStr, out int ms) && ms > 0 ? $" {Math.Max(1, ms / 1000)}\"" : string.Empty;
                    string transcript = ExtractVoiceTranscriptFromPacked(packedInfo);
                    return string.IsNullOrWhiteSpace(transcript)
                        ? ("voice", $"🎤 [语音{dur}]")
                        : ("voice", $"🎤 [语音{dur}] {transcript}");
                }
                case 47 or 11000:
                {
                    var root = ParseXmlSafe(content);
                    string gameLabel = DescribeGameEmoji(root);
                    if (!string.IsNullOrEmpty(gameLabel)) return ("emoji", gameLabel);
                    string desc = XmlAttrOrText(root, "desc");
                    return ("emoji", string.IsNullOrWhiteSpace(desc) ? "😄 [动画表情]" : $"😄 [动画表情: {desc}]");
                }
                case 42:
                {
                    var root = ParseXmlSafe(content);
                    string nick = XmlAttrOrText(root, "nickname");
                    return ("card", string.IsNullOrWhiteSpace(nick) ? "🪪 [名片]" : $"🪪 [名片] {nick}");
                }
                case 48:
                {
                    var root = ParseXmlSafe(content);
                    var loc = FindXmlElement(root, "location");
                    string poi = loc?.Attribute("poiname")?.Value ?? string.Empty;
                    string locLabel = loc?.Attribute("label")?.Value ?? string.Empty;
                    string detail = string.Join(" · ", new[] { poi, locLabel }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                    return ("location", string.IsNullOrWhiteSpace(detail) ? "📍 [位置]" : $"📍 [位置] {detail}");
                }
                case 50:
                    return ("voip", "📞 [音视频通话]");
                case 10000 or 10002:
                    return ("system", DescribeSystemMessage(content));
                case 49 or 11001 or 11002:
                    return DescribeAppMsg(rawType, content);
                default:
                    if (content.Contains("<appmsg", StringComparison.OrdinalIgnoreCase))
                    {
                        return DescribeAppMsg(rawType, content);
                    }
                    string tn = TypeNames.TryGetValue(mtype, out var label) ? label : $"消息({mtype})";
                    return ("other", $"[{tn}]");
            }
        }

        private static string DescribeGameEmoji(XElement? root)
        {
            if (root is null) return string.Empty;
            string gameType = XmlAttrOrText(root, "gametype");
            string gameExt = XmlAttrOrText(root, "gameext");
            if (gameType == "1" && gameExt is "1" or "2" or "3")
            {
                string rps = gameExt switch { "1" => "剪刀 ✌️", "2" => "石头 ✊", _ => "布 ✋" };
                return $"🎮 [猜拳: {rps}]";
            }
            if (gameType == "2" && int.TryParse(gameExt, out int dice) && dice is >= 1 and <= 6)
            {
                return $"🎲 [骰子: {dice}点]";
            }
            return string.Empty;
        }

        /// <summary>
        /// Best-effort extraction of WeChat's own voice-to-text transcript if cached inside packed_info_data.
        /// </summary>
        private static string ExtractVoiceTranscriptFromPacked(byte[]? packed)
        {
            if (packed is null || packed.Length < 4) return string.Empty;
            try
            {
                int pos = 0;
                while (pos < packed.Length)
                {
                    if (!TryReadVarint(packed, ref pos, out ulong tag)) break;
                    int wire = (int)(tag & 7);
                    if (wire == 0) { if (!TryReadVarint(packed, ref pos, out _)) break; }
                    else if (wire == 1) pos += 8;
                    else if (wire == 5) pos += 4;
                    else if (wire == 2)
                    {
                        if (!TryReadVarint(packed, ref pos, out ulong lenU) || lenU > 8192 || pos + (int)lenU > packed.Length) break;
                        string s = SafeUtf8String(packed.AsSpan(pos, (int)lenU));
                        pos += (int)lenU;
                        // Transcripts typically contain CJK or readable sentence text (not hex hashes or xml/paths)
                        if (s.Length >= 2 && !s.StartsWith('<') && !s.Contains('\\') && !Md5HexRegex().IsMatch(s) &&
                            s.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                        {
                            return s;
                        }
                    }
                    else break;
                }
            }
            catch { }
            return string.Empty;
        }

        private static List<string> ExtractMediaHints(string content, byte[]? packedInfo)
        {
            var hints = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? v)
            {
                if (string.IsNullOrWhiteSpace(v)) return;
                string clean = v.Trim();
                if (seen.Add(clean)) hints.Add(clean);
            }

            var root = ParseXmlSafe(content);
            if (root is not null)
            {
                foreach (var el in root.DescendantsAndSelf())
                {
                    foreach (var attr in el.Attributes())
                    {
                        string an = attr.Name.LocalName.ToLowerInvariant();
                        string av = attr.Value.Trim();
                        if ((an.Contains("md5") && Md5HexRegex().IsMatch(av)) || an is "filename" or "datname")
                        {
                            Add(av);
                        }
                        else if (an is "cdnurl" or "thumburl" or "encrypturl" &&
                                 (av.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                  av.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                        {
                            Add(av);
                        }
                    }
                    string tn = el.Name.LocalName.ToLowerInvariant();
                    string tv = el.Value.Trim();
                    if ((tn is "md5" or "filemd5" or "fullmd5" or "thumbfullmd5" or "rawmd5") && Md5HexRegex().IsMatch(tv))
                    {
                        Add(tv);
                    }
                }
            }

            // Recursively scan packed_info_data protobufs (WeChat 4.x nests the 32-char MD5 inside sub-messages!)
            if (packedInfo is { Length: > 0 and <= 65536 })
            {
                ScanProtobufForMediaHints(packedInfo, 0, Add);
            }

            return hints;
        }

        private static void ScanProtobufForMediaHints(ReadOnlySpan<byte> buf, int depth, Action<string?> add)
        {
            if (depth > 4 || buf.Length == 0) return;
            try
            {
                int pos = 0;
                while (pos < buf.Length)
                {
                    if (!TryReadVarint(buf, ref pos, out ulong tag)) break;
                    if ((tag >> 3) == 0) break;
                    int wire = (int)(tag & 7);
                    if (wire == 0) { if (!TryReadVarint(buf, ref pos, out _)) break; }
                    else if (wire == 1) pos += 8;
                    else if (wire == 5) pos += 4;
                    else if (wire == 2)
                    {
                        if (!TryReadVarint(buf, ref pos, out ulong lenU) || pos + (int)lenU > buf.Length) break;
                        var slice = buf.Slice(pos, (int)lenU);
                        pos += (int)lenU;
                        if (lenU == 16)
                        {
                            add(Convert.ToHexString(slice).ToLowerInvariant());
                        }
                        else if (lenU is >= 8 and <= 256)
                        {
                            string s = SafeUtf8String(slice);
                            if (Md5HexRegex().IsMatch(s))
                            {
                                add(s);
                            }
                            else if (s.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) ||
                                     s.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                     s.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                            {
                                add(Path.GetFileNameWithoutExtension(s));
                            }
                            else if (lenU >= 16)
                            {
                                ScanProtobufForMediaHints(slice, depth + 1, add);
                            }
                        }
                        else if (lenU > 256 && lenU <= 65536)
                        {
                            ScanProtobufForMediaHints(slice, depth + 1, add);
                        }
                    }
                    else break;
                }
            }
            catch { }
        }

        private (string Kind, string Text) DescribeAppMsg(long rawType, string content)
        {
            var root = ParseXmlSafe(content);
            if (root is null)
            {
                bool plain = !string.IsNullOrWhiteSpace(content) && !content.TrimStart().StartsWith('<');
                return plain ? ("text", content) : ("other", "[应用消息]");
            }

            var appmsg = root.Name.LocalName.Equals("appmsg", StringComparison.OrdinalIgnoreCase)
                ? root
                : FindXmlElement(root, "appmsg") ?? root;

            string subStr = appmsg.Element("type")?.Value ?? string.Empty;
            int rawSub = (int)((rawType >> 32) & 0xFFFFFFFF);
            if (!int.TryParse(subStr, out int sub)) sub = rawSub;
            if (appmsg.Element("refermsg") is not null && (string.IsNullOrEmpty(subStr) || rawSub == 57))
            {
                sub = 57;
            }

            string title = appmsg.Element("title")?.Value?.Trim() ?? string.Empty;
            string desc = appmsg.Element("des")?.Value?.Trim() ?? string.Empty;
            string url = appmsg.Element("url")?.Value?.Trim() ?? string.Empty;

            if (sub == 1)
            {
                return ("text", !string.IsNullOrWhiteSpace(title) ? title : (!string.IsNullOrWhiteSpace(desc) ? desc : content));
            }
            if (sub == 57)
            {
                var refer = FindXmlElement(appmsg, "refermsg");
                string refName = XmlText(refer, "displayname");
                string refContent = XmlText(refer, "content");
                if (refContent.TrimStart().StartsWith('<'))
                {
                    var refXml = ParseXmlSafe(refContent);
                    refContent = XmlText(refXml, "title");
                    if (string.IsNullOrWhiteSpace(refContent)) refContent = "[媒体消息]";
                }
                if (refContent.Length > 80) refContent = refContent[..80] + "...";
                string reply = string.IsNullOrWhiteSpace(title) ? "[引用回复]" : title;
                return ("quote", string.IsNullOrWhiteSpace(refContent)
                    ? reply
                    : $"{reply}\n「引用 {(string.IsNullOrWhiteSpace(refName) ? "" : refName + ": ")}{refContent}」");
            }
            if (sub is 19 or 20 or 7 or 14)
            {
                var rec = FindXmlElement(appmsg, "recorditem");
                var previews = new List<string>();
                if (rec is not null)
                {
                    var inner = ParseXmlSafe(rec.Value);
                    if (inner is not null)
                    {
                        foreach (var item in inner.Descendants("dataitem").Take(3))
                        {
                            string sName = XmlText(item, "sourcename");
                            string sDesc = XmlText(item, "datadesc");
                            if (!string.IsNullOrWhiteSpace(sDesc))
                            {
                                previews.Add(string.IsNullOrWhiteSpace(sName) ? sDesc : $"{sName}: {sDesc}");
                            }
                        }
                    }
                }
                string head = $"📋 [合并转发] {(string.IsNullOrWhiteSpace(title) ? "聊天记录" : title)}";
                return ("chatrecord", previews.Count > 0 ? $"{head}\n" + string.Join("\n", previews.Select(p => "· " + (p.Length > 60 ? p[..60] + "..." : p))) : head);
            }
            if (sub == 6)
            {
                var attach = FindXmlElement(appmsg, "appattach");
                string fname = XmlText(attach, "filename");
                if (string.IsNullOrWhiteSpace(fname)) fname = title;
                string sizeStr = XmlText(attach, "totallen");
                string sizeTag = long.TryParse(sizeStr, out long sz) && sz > 0 ? $" ({FormatBytes(sz)})" : string.Empty;
                return ("file", $"📎 [文件] {(string.IsNullOrWhiteSpace(fname) ? desc : fname)}{sizeTag}");
            }
            if (sub == 8)
            {
                return ("emoji", string.IsNullOrWhiteSpace(title) ? "😄 [动画表情]" : $"😄 [{title}]");
            }
            if (sub is 2000 or 2001 or 2003)
            {
                var pay = FindXmlElement(appmsg, "wcpayinfo");
                string fee = XmlText(pay, "feedesc");
                string memo = XmlText(pay, "pay_memo");
                if (sub == 2000 || !string.IsNullOrWhiteSpace(fee))
                {
                    string extra = string.Join(" · ", new[] { fee, memo }.Where(x => !string.IsNullOrWhiteSpace(x)));
                    return ("transfer", string.IsNullOrWhiteSpace(extra) ? "💰 [微信转账]" : $"💰 [微信转账] {extra}");
                }
                return ("hongbao", $"🧧 [微信红包] {(string.IsNullOrWhiteSpace(title) ? desc : title)}");
            }
            if (sub is 33 or 36 or 44)
            {
                string srcName = XmlText(appmsg, "sourcedisplayname");
                string detail = string.Join(" · ", new[] { srcName, title }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                string line = $"🧩 [小程序] {detail}";
                return ("miniprogram", !string.IsNullOrWhiteSpace(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? $"{line}\n{url}" : line);
            }
            if (sub == 5)
            {
                var lines = new List<string>(3);
                lines.Add($"🔗 [链接] {(string.IsNullOrWhiteSpace(title) ? "网页链接" : title)}");
                if (!string.IsNullOrWhiteSpace(desc) && !desc.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(desc);
                }
                if (!string.IsNullOrWhiteSpace(url))
                {
                    lines.Add(url);
                }
                return ("link", string.Join("\n", lines));
            }

            string kindLabel = AppMsgSubTypes.TryGetValue(sub, out var lbl) ? lbl : $"应用消息({sub})";
            string rest = string.Join(" · ", new[] { title, desc, url }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return ("appmsg", string.IsNullOrWhiteSpace(rest) ? $"[{kindLabel}]" : $"[{kindLabel}] {rest}");
        }

        private string DescribeSystemMessage(string content)
        {
            var root = ParseXmlSafe(content);
            if (root is null) return string.IsNullOrWhiteSpace(content) ? "[系统消息]" : content;

            string replace = XmlText(root, "replacemsg");
            if (!string.IsNullOrWhiteSpace(replace)) return StripInnerTags(replace);

            var container = FindXmlElement(root, "content_template") ?? root;
            string template = XmlText(container, "template");
            if (!string.IsNullOrWhiteSpace(template))
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in container.Descendants("link"))
                {
                    string? name = link.Attribute("name")?.Value;
                    if (string.IsNullOrEmpty(name)) continue;
                    var members = link.Descendants("member").ToList();
                    if (members.Count > 0)
                    {
                        var names = members.Select(m =>
                        {
                            string u = m.Element("username")?.Value ?? string.Empty;
                            string n = m.Element("nickname")?.Value ?? m.Element("displayname")?.Value ?? string.Empty;
                            return !string.IsNullOrWhiteSpace(n) ? n : ResolveDisplayName(u);
                        }).Where(x => !string.IsNullOrWhiteSpace(x));
                        string sep = link.Element("separator")?.Value ?? "、";
                        values[name] = string.Join(sep, names);
                    }
                    else
                    {
                        foreach (var f in new[] { "plain", "title", "nickname", "text" })
                        {
                            string v = XmlText(link, f);
                            if (!string.IsNullOrWhiteSpace(v)) { values[name] = v; break; }
                        }
                    }
                }
                return SysTemplateVarRegex().Replace(template, m =>
                {
                    string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    return values.TryGetValue(key, out var val) ? val : string.Empty;
                }).Trim();
            }

            string plain = XmlText(root, "plain");
            return !string.IsNullOrWhiteSpace(plain) ? plain : "[系统消息]";
        }

        private List<string> ResolveLabelList(string rawLabels)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(rawLabels)) return names;
            foreach (var part in rawLabels.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (LabelNames.TryGetValue(part, out var name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
            return names;
        }

        private void LoadResourceDbIndex(string resourceDbPath)
        {
            var conn = OpenReadOnly(resourceDbPath);
            if (conn is null) return;
            var tables = ListTables(conn);
            if (!tables.Contains("ChatName2Id") || !tables.Contains("MessageResourceInfo"))
            {
                conn.Dispose();
                return;
            }

            _resourceConn = conn;
            _hasResourceDetailTable = tables.Contains("MessageResourceDetail");
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT rowid, user_name FROM ChatName2Id";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0) && !r.IsDBNull(1))
                    {
                        string uname = r.GetString(1);
                        if (!string.IsNullOrWhiteSpace(uname))
                        {
                            _resourceChatName2Id[uname] = r.GetInt64(0);
                        }
                    }
                }
            }
            catch { }
        }

        private void EnrichMediaHintsFromResourceDb(string sessionUsername, long localId, string serverId, List<string> hints)
        {
            if (_resourceConn is null || !_resourceChatName2Id.TryGetValue(sessionUsername, out long chatId))
                return;

            var seen = new HashSet<string>(hints, StringComparer.OrdinalIgnoreCase);
            void AddHint(string? raw)
            {
                if (string.IsNullOrWhiteSpace(raw)) return;
                string clean = raw.Trim();
                if (seen.Add(clean)) hints.Add(clean);
            }

            try
            {
                bool hasSvr = long.TryParse(serverId, out long svrId) && svrId != 0;
                using var cmd = _resourceConn.CreateCommand();
                if (hasSvr)
                {
                    cmd.CommandText = "SELECT message_id, packed_info FROM MessageResourceInfo WHERE chat_id = @cid AND (message_svr_id = @sid OR message_local_id = @lid) LIMIT 4";
                    cmd.Parameters.AddWithValue("@cid", chatId);
                    cmd.Parameters.AddWithValue("@sid", svrId);
                    cmd.Parameters.AddWithValue("@lid", localId);
                }
                else
                {
                    cmd.CommandText = "SELECT message_id, packed_info FROM MessageResourceInfo WHERE chat_id = @cid AND message_local_id = @lid LIMIT 4";
                    cmd.Parameters.AddWithValue("@cid", chatId);
                    cmd.Parameters.AddWithValue("@lid", localId);
                }

                var matchedMsgIds = new List<long>();
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (!r.IsDBNull(0)) matchedMsgIds.Add(r.GetInt64(0));
                        if (!r.IsDBNull(1) && r.GetValue(1) is byte[] packed && packed.Length > 0)
                        {
                            foreach (var h in ExtractMediaHints(string.Empty, packed)) AddHint(h);
                        }
                    }
                }

                if (_hasResourceDetailTable && matchedMsgIds.Count > 0)
                {
                    foreach (long mid in matchedMsgIds)
                    {
                        using var dCmd = _resourceConn.CreateCommand();
                        dCmd.CommandText = "SELECT data_index, packed_info FROM MessageResourceDetail WHERE message_id = @mid LIMIT 8";
                        dCmd.Parameters.AddWithValue("@mid", mid);
                        using var dr = dCmd.ExecuteReader();
                        while (dr.Read())
                        {
                            if (!dr.IsDBNull(0))
                            {
                                string dataIdx = dr.GetString(0)?.Trim() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(dataIdx))
                                {
                                    AddHint(dataIdx);
                                    string fname = Path.GetFileNameWithoutExtension(dataIdx);
                                    if (!string.IsNullOrWhiteSpace(fname)) AddHint(fname);
                                }
                            }
                            if (!dr.IsDBNull(1) && dr.GetValue(1) is byte[] dPacked && dPacked.Length > 0)
                            {
                                foreach (var h in ExtractMediaHints(string.Empty, dPacked)) AddHint(h);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):F1} MB";
            if (bytes >= 1024) return $"{bytes / 1024.0:F0} KB";
            return $"{bytes} B";
        }

        private static (string Gender, string Region, string Signature, string Phone, string Description, string LabelIdList) ParseContactExtraBuffer(byte[]? buf)
        {
            if (buf is null || buf.Length == 0) return (string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);

            int genderCode = 0;
            string country = string.Empty, province = string.Empty, city = string.Empty;
            string signature = string.Empty, phone = string.Empty, description = string.Empty;
            string labelIdList = string.Empty;

            try
            {
                int pos = 0;
                while (pos < buf.Length)
                {
                    if (!TryReadVarint(buf, ref pos, out ulong tag)) break;
                    int fieldNo = (int)(tag >> 3);
                    int wire = (int)(tag & 7);
                    if (fieldNo <= 0) break;

                    if (wire == 0)
                    {
                        if (!TryReadVarint(buf, ref pos, out ulong val)) break;
                        if (fieldNo == 2 && val is 1 or 2) genderCode = (int)val;
                    }
                    else if (wire == 1) pos += 8;
                    else if (wire == 5) pos += 4;
                    else if (wire == 2)
                    {
                        if (!TryReadVarint(buf, ref pos, out ulong lenU) || lenU > 65536) break;
                        int len = (int)lenU;
                        if (pos + len > buf.Length) break;
                        string str = SafeUtf8String(buf.AsSpan(pos, len));
                        pos += len;

                        switch (fieldNo)
                        {
                            case 4: signature = str; break;
                            case 5: country = str; break;
                            case 6: province = str; break;
                            case 7: city = str; break;
                            case 10: if (string.IsNullOrEmpty(description)) description = str; break;
                            case 13:
                            case 14:
                                if (string.IsNullOrEmpty(phone) && str.Any(char.IsDigit) && str.Length <= 32)
                                    phone = str;
                                break;
                            case 30:
                                // Windows WeChat 4.x stores comma-separated contact_label IDs in extra_buffer field 30
                                if (string.IsNullOrEmpty(labelIdList) && !string.IsNullOrWhiteSpace(str))
                                    labelIdList = str;
                                break;
                        }
                    }
                    else break;
                }
            }
            catch { }

            string gender = genderCode switch { 1 => "男", 2 => "女", _ => string.Empty };
            var regionParts = new[] { country, province, city }
                .Where(s => !string.IsNullOrWhiteSpace(s) && s != "CN")
                .Distinct()
                .ToList();
            return (gender, string.Join(" ", regionParts), signature, phone, description, labelIdList);
        }

        private static Dictionary<string, string> ParseChatRoomMemberDisplayNames(byte[]? extBuf)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (extBuf is null || extBuf.Length == 0) return result;
            try
            {
                int pos = 0;
                while (pos < extBuf.Length)
                {
                    if (!TryReadVarint(extBuf, ref pos, out ulong tag)) break;
                    int wire = (int)(tag & 7);
                    if (wire == 0) { if (!TryReadVarint(extBuf, ref pos, out _)) break; }
                    else if (wire == 1) pos += 8;
                    else if (wire == 5) pos += 4;
                    else if (wire == 2)
                    {
                        if (!TryReadVarint(extBuf, ref pos, out ulong lenU) || lenU > 256 * 1024) break;
                        int len = (int)lenU;
                        if (pos + len > extBuf.Length) break;
                        var slice = extBuf.AsSpan(pos, len);
                        pos += len;

                        int ip = 0;
                        string wx = string.Empty, nick = string.Empty;
                        while (ip < slice.Length)
                        {
                            if (!TryReadVarint(slice, ref ip, out ulong itag)) break;
                            int ifield = (int)(itag >> 3);
                            int iwire = (int)(itag & 7);
                            if (iwire == 0) { if (!TryReadVarint(slice, ref ip, out _)) break; }
                            else if (iwire == 1) ip += 8;
                            else if (iwire == 5) ip += 4;
                            else if (iwire == 2)
                            {
                                if (!TryReadVarint(slice, ref ip, out ulong ilenU) || ip + (int)ilenU > slice.Length) break;
                                string s = SafeUtf8String(slice.Slice(ip, (int)ilenU));
                                ip += (int)ilenU;
                                if (ifield == 1 && string.IsNullOrEmpty(wx)) wx = s;
                                else if (ifield == 2 && string.IsNullOrEmpty(nick)) nick = s;
                            }
                            else break;
                        }
                        if (!string.IsNullOrWhiteSpace(wx) && !string.IsNullOrWhiteSpace(nick) && SenderPrefixRegex().IsMatch(wx))
                        {
                            result[wx] = nick;
                        }
                    }
                    else break;
                }
            }
            catch { }
            return result;
        }

        private static bool TryReadVarint(ReadOnlySpan<byte> buf, ref int pos, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (pos < buf.Length && shift < 64)
            {
                byte b = buf[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
            }
            return false;
        }

        private static string SafeUtf8String(ReadOnlySpan<byte> span)
        {
            if (span.Length == 0 || span.IndexOf((byte)0) >= 0) return string.Empty;
            try
            {
                string s = Encoding.UTF8.GetString(span).Trim();
                return s.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t') ? string.Empty : s;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static int NormalizeLocalType(long t)
        {
            if (t is >= 0 and <= 0xFFFF) return (int)t;
            int low = (int)(t & 0xFFFFFFFF);
            if (TypeNames.ContainsKey(low) || (low >= 1 && low <= 100)) return low;
            return low & 0xFF;
        }

        private static XElement? ParseXmlSafe(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            int idx = text.IndexOf('<');
            if (idx < 0) return null;
            string xml = text[idx..].Trim();
            try { return XElement.Parse(xml); } catch { }
            try
            {
                string fixedXml = BareAmpRegex().Replace(xml, "&amp;");
                return XElement.Parse(fixedXml);
            }
            catch
            {
                return null;
            }
        }

        private static XElement? FindXmlElement(XElement? root, string localName) =>
            root?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

        private static string XmlText(XElement? root, string localName)
        {
            if (root is null) return string.Empty;
            var direct = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));
            if (direct is not null) return direct.Value.Trim();
            var desc = FindXmlElement(root, localName);
            return desc?.Value.Trim() ?? string.Empty;
        }

        private static string XmlAttrOrText(XElement? root, string name)
        {
            if (root is null) return string.Empty;
            foreach (var el in root.DescendantsAndSelf())
            {
                var attr = el.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (attr is not null && !string.IsNullOrWhiteSpace(attr.Value)) return attr.Value.Trim();
            }
            return XmlText(root, name);
        }

        private static string StripInnerTags(string text)
        {
            if (!text.Contains('<')) return text;
            var el = ParseXmlSafe($"<r>{text}</r>");
            return el?.Value.Trim() ?? text;
        }

        private static bool IsValidImageBytes(byte[] b)
        {
            if (b.Length < 12) return false;
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true;
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true;
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;
            return false;
        }

        private static SqliteConnection? OpenReadOnly(string path)
        {
            try
            {
                var csb = new SqliteConnectionStringBuilder
                {
                    DataSource = path,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                };
                var conn = new SqliteConnection(csb.ToString());
                conn.Open();
                return conn;
            }
            catch
            {
                return null;
            }
        }

        private static HashSet<string> ListColumns(SqliteConnection conn, string table)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(1)) set.Add(r.GetString(1));
            }
            return set;
        }

        private static HashSet<string> ListTables(SqliteConnection conn)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0)) set.Add(r.GetString(0));
            }
            return set;
        }

        private static Dictionary<long, string> LoadName2Id(SqliteConnection conn, HashSet<string> tables)
        {
            var map = new Dictionary<long, string>();
            if (!tables.Contains("Name2Id")) return map;
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT rowid, user_name FROM Name2Id";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0) && !r.IsDBNull(1))
                    {
                        map[r.GetInt64(0)] = r.GetString(1);
                    }
                }
            }
            catch { }
            return map;
        }

        private static object? GetRawColumn(SqliteDataReader reader, params string[] names)
        {
            foreach (var name in names)
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    if (reader.GetName(i).Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        return reader.IsDBNull(i) ? null : reader.GetValue(i);
                    }
                }
            }
            return null;
        }

        private static string GetColString(SqliteDataReader reader, params string[] names)
        {
            var raw = GetRawColumn(reader, names);
            if (raw is null) return string.Empty;
            if (raw is string s) return s.Trim();
            if (raw is byte[] b) return SafeUtf8String(b);
            return Convert.ToString(raw)?.Trim() ?? string.Empty;
        }

        private static int GetColInt(SqliteDataReader reader, params string[] names)
        {
            var raw = GetRawColumn(reader, names);
            if (raw is null) return 0;
            try { return Convert.ToInt32(raw); } catch { return 0; }
        }

        private static long GetColLong(SqliteDataReader reader, params string[] names)
        {
            var raw = GetRawColumn(reader, names);
            if (raw is null) return 0;
            try { return Convert.ToInt64(raw); } catch { return 0; }
        }

        private static byte[]? GetColBytes(SqliteDataReader reader, params string[] names)
        {
            var raw = GetRawColumn(reader, names);
            if (raw is byte[] b) return b;
            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _zstd.Dispose();
            foreach (var (_, conn, _, _) in _msgDbs) { try { conn.Dispose(); } catch { } }
            _msgDbs.Clear();
            foreach (var (conn, _) in _mediaDbs) { try { conn.Dispose(); } catch { } }
            _mediaDbs.Clear();
            foreach (var (conn, _) in _headImageDbs) { try { conn.Dispose(); } catch { } }
            _headImageDbs.Clear();
            try { _resourceConn?.Dispose(); } catch { }
            _resourceConn = null;
        }
    }
}
