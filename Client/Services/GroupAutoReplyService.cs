using System.Diagnostics;
using System.Text.Json;
using Client.Models;
using Client.Services.WeChat4Db;

namespace Client.Services
{
    /// <summary>
    /// End-to-end group @mention AI auto-reply coordinator:
    /// - Persists Q&amp;A examples, reply persona, safety limits, and automation preferences in auto_reply_config.json
    /// - Listens to live new group messages from WeChat4SyncService (strictly real-time, never historical imports)
    /// - Matches @me via both WeChat protocol XML (&lt;atuserlist&gt;) and text (@群昵称 / @微信昵称 / 自定义别名)
    /// - Applies safety guards: enabled-group check, @all filter, sensitive keyword blacklist, message freshness, dedup, per-group cooldown
    /// - Generates replies via DirectMatch Q&amp;A rules or Server AI endpoint (/api/v1/ai/group-auto-reply)
    /// - Serializes WeChat OCR + ClickVisualOverlay execution through WeChatAutoReplyExecutor
    /// </summary>
    public sealed class GroupAutoReplyService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static readonly char[] NameSeparators = [',', '，', ';', '；', '/', '|', '\n', '\r'];
        private static readonly char[] MentionBoundaries = [' ', '\t', '\u2005', '\u00A0', '\r', '\n', ',', '，', '.', '。', ':', '：', '?', '？', '!', '！', '、'];

        private readonly object _configLock = new();
        private readonly object _stateLock = new();
        private readonly SemaphoreSlim _execGate = new(1, 1);
        private readonly string _configPath;

        private readonly Dictionary<long, GroupDto> _groupsById = [];
        private readonly HashSet<string> _processedExternalIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _processedOrder = new();
        private readonly Dictionary<long, DateTimeOffset> _lastReplyAtByGroup = [];
        private readonly LinkedList<AutoReplyExecutionRecord> _recentRecords = new();

        private DateTimeOffset _listeningStartedUtc = DateTimeOffset.UtcNow;
        private bool _initialized;
        private bool _isExecuting;
        private string _liveStatusText = "待命中";

        public AutoReplyConfigData Config { get; private set; } = new();
        public bool IsExecuting => _isExecuting;
        public string LiveStatusText => _liveStatusText;

        public event Action? StateChanged;
        public event Action<AutoReplyExecutionRecord>? ExecutionRecorded;
        public event Action<long, bool>? GroupAutoReplyToggled;

        public GroupAutoReplyService()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC");
            _configPath = Path.Combine(dir, "auto_reply_config.json");
            LoadConfig();
        }

        public void EnsureInitialized()
        {
            if (_initialized) return;
            lock (_stateLock)
            {
                if (_initialized) return;
                _initialized = true;
                _listeningStartedUtc = DateTimeOffset.UtcNow;
            }
            AppServices.WeChat4Sync.GroupNewMessagesSynced -= OnGroupNewMessagesSynced;
            AppServices.WeChat4Sync.GroupNewMessagesSynced += OnGroupNewMessagesSynced;
            AppServices.Logs.Info("REPLY",
                $"群自动回复监听引擎已就绪：全局开关={(Config.GlobalEnabled ? "开启" : "暂停")}，问答示例 {Config.Examples.Count(e => e.Enabled)}/{Config.Examples.Count} 条，自动发送={(Config.AutoSendAfterPaste ? "开" : "仅粘贴")}。");
        }

        public void ResetRuntimeStateForLogout()
        {
            lock (_stateLock)
            {
                _groupsById.Clear();
                _processedExternalIds.Clear();
                _processedOrder.Clear();
                _lastReplyAtByGroup.Clear();
                _listeningStartedUtc = DateTimeOffset.UtcNow;
            }
            UpdateLiveStatus("已重置（等待重新登录）");
        }

        public void LoadConfig()
        {
            lock (_configLock)
            {
                try
                {
                    if (File.Exists(_configPath))
                    {
                        string json = File.ReadAllText(_configPath);
                        var parsed = JsonSerializer.Deserialize<AutoReplyConfigData>(json, JsonOpts);
                        if (parsed is not null)
                        {
                            Config = parsed;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("REPLY", $"读取自动回复配置失败，已恢复默认配置：{ex.Message}");
                    Config = new AutoReplyConfigData();
                }

                Config.Examples ??= [];
                Config.ExtraTriggerNames ??= string.Empty;
                Config.CustomSystemPrompt ??= string.Empty;
                Config.FallbackReply ??= string.Empty;
                Config.BlockedKeywords ??= string.Empty;
                Config.CooldownSecondsPerGroup = Math.Clamp(Config.CooldownSecondsPerGroup, 3, 600);
                Config.MaxMessageAgeSeconds = Math.Clamp(Config.MaxMessageAgeSeconds, 30, 1800);
                Config.MaxReplyChars = Math.Clamp(Config.MaxReplyChars, 40, 800);
                foreach (var ex in Config.Examples)
                {
                    if (string.IsNullOrWhiteSpace(ex.Id)) ex.Id = Guid.NewGuid().ToString("N");
                }
            }
        }

        public void SaveConfig()
        {
            lock (_configLock)
            {
                try
                {
                    string? dir = Path.GetDirectoryName(_configPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    string json = JsonSerializer.Serialize(Config, JsonOpts);
                    string tmp = _configPath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, _configPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    AppServices.Logs.Error("REPLY", $"保存自动回复配置失败：{ex.Message}");
                }
            }
            StateChanged?.Invoke();
        }

        public void SyncGroupsSnapshot(IEnumerable<GroupDto> groups)
        {
            lock (_stateLock)
            {
                _groupsById.Clear();
                foreach (var g in groups)
                {
                    _groupsById[g.Id] = g;
                }
            }
            StateChanged?.Invoke();
        }

        public void NotifyGroupAutoReplyChanged(GroupDto updatedGroup)
        {
            lock (_stateLock)
            {
                _groupsById[updatedGroup.Id] = updatedGroup;
            }
            AppServices.Logs.Info("REPLY",
                $"群「{updatedGroup.DisplayName}」(#{updatedGroup.Id}) AI 自动回复开关已设为：{(updatedGroup.AutoReplyEnabled ? "开启" : "关闭")}");
            GroupAutoReplyToggled?.Invoke(updatedGroup.Id, updatedGroup.AutoReplyEnabled);
            StateChanged?.Invoke();
        }

        public int GetEnabledGroupCount()
        {
            string activeWxid = AppServices.WeChat4Sync.ActiveOwnerWxid;
            lock (_stateLock)
            {
                return _groupsById.Values.Count(g =>
                    g.AutoReplyEnabled &&
                    (string.IsNullOrWhiteSpace(activeWxid) ||
                     string.IsNullOrWhiteSpace(g.OwnerWxid) ||
                     string.Equals(g.OwnerWxid, activeWxid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public IReadOnlyList<AutoReplyExecutionRecord> GetRecentRecords()
        {
            lock (_stateLock)
            {
                return _recentRecords.ToList();
            }
        }

        public void ClearRecentRecords()
        {
            lock (_stateLock)
            {
                _recentRecords.Clear();
            }
            StateChanged?.Invoke();
        }

        public List<AutoReplyExampleItem> BuildStarterTemplates() =>
        [
            new AutoReplyExampleItem
            {
                Category = "服务咨询",
                QuestionPattern = "工作时间 / 几点上班 / 周末在吗",
                AnswerExample = "您好，我们的在线服务时间是每天 9:00-21:00，期间随时为您解答，非工作时间留言看到后也会第一时间回复您～",
                DirectMatch = false
            },
            new AutoReplyExampleItem
            {
                Category = "报价预约",
                QuestionPattern = "多少钱 / 报价 / 怎么预约",
                AnswerExample = "您好，具体方案和活动价会根据您的需求略有不同，稍后我私信发您最新价目说明，也可以直接告诉我您方便的时间帮您优先安排预约～",
                DirectMatch = false
            },
            new AutoReplyExampleItem
            {
                Category = "进群答疑",
                QuestionPattern = "在吗 / 帮忙看一下 / 进度怎么样了",
                AnswerExample = "收到啦，这边已经看到您的消息，我马上为您核对最新进度并回复您，请稍等片刻～",
                DirectMatch = false
            }
        ];

        private void OnGroupNewMessagesSynced(WeChat4GroupNewMessagesEvent evt)
        {
            if (!evt.IsBackgroundAuto || evt.NewMessages.Count == 0) return;
            if (!AppServices.Backend.IsAuthenticated) return;

            if (!Config.GlobalEnabled)
            {
                return;
            }

            GroupDto? trackedGroup;
            lock (_stateLock)
            {
                _groupsById.TryGetValue(evt.GroupId, out trackedGroup);
            }
            if (trackedGroup is null || !trackedGroup.AutoReplyEnabled)
            {
                return;
            }
            if (!string.IsNullOrWhiteSpace(evt.OwnerWxid) &&
                !string.IsNullOrWhiteSpace(trackedGroup.OwnerWxid) &&
                !string.Equals(trackedGroup.OwnerWxid, evt.OwnerWxid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Pick the latest eligible @me message in this sync batch (avoid replying multiple times to one burst)
            var nowUtc = DateTimeOffset.UtcNow;
            WeChat4Message? targetMsg = null;
            string matchedBy = string.Empty;

            for (int i = evt.NewMessages.Count - 1; i >= 0; i--)
            {
                var m = evt.NewMessages[i];
                if (m.IsOutgoing || m.Kind != "text" || string.IsNullOrWhiteSpace(m.Content)) continue;

                if (IsAlreadyProcessed(m.ExternalId)) continue;

                // Freshness guard: never reply to messages from before app startup or older than MaxMessageAgeSeconds
                if (m.SentAtUtc < _listeningStartedUtc.AddSeconds(-10))
                {
                    MarkProcessed(m.ExternalId);
                    continue;
                }
                double ageSec = (nowUtc - m.SentAtUtc).TotalSeconds;
                if (ageSec > Config.MaxMessageAgeSeconds)
                {
                    MarkProcessed(m.ExternalId);
                    AppServices.Logs.Info("REPLY", $"跳过超时消息（已过去 {ageSec:F0}s > {Config.MaxMessageAgeSeconds}s）：群「{evt.GroupName}」");
                    continue;
                }

                if (TryMatchMentionSelf(m, evt, trackedGroup, out string reason))
                {
                    targetMsg = m;
                    matchedBy = reason;
                    break;
                }
            }

            if (targetMsg is null) return;

            // Mark all messages in this batch up to targetMsg as processed to prevent duplicate triggers
            MarkProcessed(targetMsg.ExternalId);

            AppServices.Logs.Info("REPLY",
                $"检测到群「{evt.GroupName}」被 @（命中方式：{matchedBy}），发言人={targetMsg.SenderName}，内容=「{Truncate(targetMsg.Content, 80)}」");

            _ = Task.Run(() => ProcessTriggeredMessageAsync(evt, trackedGroup, targetMsg, matchedBy));
        }

        private async Task ProcessTriggeredMessageAsync(
            WeChat4GroupNewMessagesEvent evt,
            GroupDto trackedGroup,
            WeChat4Message msg,
            string matchedBy)
        {
            var sw = Stopwatch.StartNew();
            string groupName = !string.IsNullOrWhiteSpace(trackedGroup.GroupName) ? trackedGroup.GroupName : evt.GroupName;
            string senderName = string.IsNullOrWhiteSpace(msg.SenderName) ? "群成员" : msg.SenderName;
            string cleanedQuestion = StripLeadingMention(msg.Content);

            // 1. Blocked sensitive keywords guard
            string? hitBlocked = FindBlockedKeyword(msg.Content, Config.BlockedKeywords);
            if (!string.IsNullOrEmpty(hitBlocked))
            {
                AppServices.Logs.Warn("REPLY", $"群「{groupName}」消息命中敏感词黑名单「{hitBlocked}」，已跳过自动回复。");
                RecordExecution(new AutoReplyExecutionRecord
                {
                    GroupId = evt.GroupId,
                    GroupName = groupName,
                    SenderName = senderName,
                    TriggerMessage = msg.Content,
                    SourceMode = matchedBy,
                    StatusLabel = "已跳过",
                    IsSkipped = true,
                    Detail = $"命中敏感词黑名单「{hitBlocked}」，交由人工处理。"
                });
                return;
            }

            // 2. Per-group cooldown guard
            lock (_stateLock)
            {
                if (_lastReplyAtByGroup.TryGetValue(evt.GroupId, out var lastAt))
                {
                    double elapsedSec = (DateTimeOffset.UtcNow - lastAt).TotalSeconds;
                    if (elapsedSec < Config.CooldownSecondsPerGroup)
                    {
                        AppServices.Logs.Info("REPLY",
                            $"群「{groupName}」处于自动回复冷却中（剩余 {Config.CooldownSecondsPerGroup - elapsedSec:F0}s），跳过本次触发。");
                        RecordExecution(new AutoReplyExecutionRecord
                        {
                            GroupId = evt.GroupId,
                            GroupName = groupName,
                            SenderName = senderName,
                            TriggerMessage = msg.Content,
                            SourceMode = matchedBy,
                            StatusLabel = "冷却跳过",
                            IsSkipped = true,
                            Detail = $"同群冷却时间 {Config.CooldownSecondsPerGroup} 秒内不重复回复。"
                        });
                        return;
                    }
                }
            }

            await _execGate.WaitAsync();
            _isExecuting = true;
            UpdateLiveStatus($"正在回复群「{groupName}」...");
            try
            {
                // 3. Generate reply (DirectMatch Q&A example first -> AI with examples + group context -> fallback)
                var (okGen, replyBody, sourceMode, genError) = await GenerateReplyTextAsync(
                    evt.GroupId, senderName, cleanedQuestion, CancellationToken.None);

                if (!okGen || string.IsNullOrWhiteSpace(replyBody))
                {
                    sw.Stop();
                    AppServices.Logs.Error("REPLY", $"群「{groupName}」生成回复失败：{genError}");
                    RecordExecution(new AutoReplyExecutionRecord
                    {
                        GroupId = evt.GroupId,
                        GroupName = groupName,
                        SenderName = senderName,
                        TriggerMessage = msg.Content,
                        SourceMode = sourceMode,
                        StatusLabel = "生成失败",
                        Detail = genError,
                        ElapsedMs = sw.ElapsedMilliseconds
                    });
                    return;
                }

                string finalReply = FormatFinalReply(replyBody, senderName, Config.PrefixMentionSender);
                AppServices.Logs.Info("REPLY", $"群「{groupName}」回复已就绪（{sourceMode}）：「{Truncate(finalReply, 90)}」");

                // 4. Execute WeChat OCR + ClickVisualOverlay automation
                var executor = new WeChatAutoReplyExecutor();
                var autoRes = await executor.SendReplyToGroupAsync(groupName, finalReply, Config);

                sw.Stop();
                if (autoRes.Success)
                {
                    lock (_stateLock)
                    {
                        _lastReplyAtByGroup[evt.GroupId] = DateTimeOffset.UtcNow;
                    }
                    RecordExecution(new AutoReplyExecutionRecord
                    {
                        GroupId = evt.GroupId,
                        GroupName = groupName,
                        SenderName = senderName,
                        TriggerMessage = msg.Content,
                        GeneratedReply = finalReply,
                        SourceMode = sourceMode,
                        StatusLabel = autoRes.WasSent ? "已发送" : "已粘贴待确认",
                        IsSuccess = true,
                        Detail = autoRes.Message,
                        ElapsedMs = sw.ElapsedMilliseconds
                    });
                }
                else
                {
                    RecordExecution(new AutoReplyExecutionRecord
                    {
                        GroupId = evt.GroupId,
                        GroupName = groupName,
                        SenderName = senderName,
                        TriggerMessage = msg.Content,
                        GeneratedReply = finalReply,
                        SourceMode = sourceMode,
                        StatusLabel = autoRes.WasCancelledByEsc ? "已中止" : "操控失败",
                        IsSkipped = autoRes.WasCancelledByEsc,
                        Detail = autoRes.Message,
                        ElapsedMs = sw.ElapsedMilliseconds
                    });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppServices.Logs.Error("REPLY", $"处理群「{groupName}」自动回复异常：{ex.Message}");
                RecordExecution(new AutoReplyExecutionRecord
                {
                    GroupId = evt.GroupId,
                    GroupName = groupName,
                    SenderName = senderName,
                    TriggerMessage = msg.Content,
                    SourceMode = "自动回复",
                    StatusLabel = "异常",
                    Detail = ex.Message,
                    ElapsedMs = sw.ElapsedMilliseconds
                });
            }
            finally
            {
                _isExecuting = false;
                _execGate.Release();
                UpdateLiveStatus(Config.GlobalEnabled ? $"监听中（已启用 {GetEnabledGroupCount()} 个群）" : "全局已暂停");
            }
        }

        /// <summary>
        /// Runs a full live end-to-end test on a selected group (AI generation + OCR search + ClickVisualOverlay + input paste),
        /// defaulting to paste-only unless sendImmediately is explicitly requested.
        /// </summary>
        public async Task<AutoReplyExecutionRecord> RunManualTestAsync(
            GroupDto targetGroup,
            string simulatedQuestion,
            bool sendImmediately,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string question = string.IsNullOrWhiteSpace(simulatedQuestion) ? "您好，请问工作时间是几点？" : simulatedQuestion.Trim();

            await _execGate.WaitAsync(ct);
            _isExecuting = true;
            UpdateLiveStatus($"正在测试群「{targetGroup.DisplayName}」...");
            try
            {
                AppServices.Logs.Info("REPLY",
                    $"开始手动测试自动回复流程：群「{targetGroup.DisplayName}」，问题=「{Truncate(question, 80)}」，自动发送={(sendImmediately ? "是" : "否(仅粘贴)")}");

                var (okGen, replyBody, sourceMode, genErr) = await GenerateReplyTextAsync(
                    targetGroup.Id, "测试群友", question, ct);

                if (!okGen || string.IsNullOrWhiteSpace(replyBody))
                {
                    sw.Stop();
                    var failRec = new AutoReplyExecutionRecord
                    {
                        GroupId = targetGroup.Id,
                        GroupName = targetGroup.DisplayName,
                        SenderName = "手动测试",
                        TriggerMessage = question,
                        SourceMode = $"测试·{sourceMode}",
                        StatusLabel = "生成失败",
                        Detail = genErr,
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                    RecordExecution(failRec);
                    return failRec;
                }

                var executor = new WeChatAutoReplyExecutor();
                var autoRes = await executor.SendReplyToGroupAsync(
                    targetGroup.DisplayName,
                    replyBody,
                    Config,
                    overrideAutoSend: sendImmediately,
                    ct: ct);

                sw.Stop();
                var rec = new AutoReplyExecutionRecord
                {
                    GroupId = targetGroup.Id,
                    GroupName = targetGroup.DisplayName,
                    SenderName = "手动测试",
                    TriggerMessage = question,
                    GeneratedReply = replyBody,
                    SourceMode = $"测试·{sourceMode}",
                    StatusLabel = autoRes.Success ? (autoRes.WasSent ? "测试已发送" : "已粘贴待确认") : (autoRes.WasCancelledByEsc ? "已中止" : "测试失败"),
                    IsSuccess = autoRes.Success,
                    IsSkipped = autoRes.WasCancelledByEsc,
                    Detail = autoRes.Message,
                    ElapsedMs = sw.ElapsedMilliseconds
                };
                RecordExecution(rec);
                return rec;
            }
            finally
            {
                _isExecuting = false;
                _execGate.Release();
                UpdateLiveStatus(Config.GlobalEnabled ? $"监听中（已启用 {GetEnabledGroupCount()} 个群）" : "全局已暂停");
            }
        }

        private async Task<(bool Ok, string Reply, string SourceMode, string Error)> GenerateReplyTextAsync(
            long groupId,
            string senderName,
            string cleanedQuestion,
            CancellationToken ct)
        {
            List<AutoReplyExampleItem> enabledExamples;
            lock (_configLock)
            {
                enabledExamples = Config.Examples
                    .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.QuestionPattern) && !string.IsNullOrWhiteSpace(e.AnswerExample))
                    .ToList();
            }

            // 1. Check DirectMatch examples first
            foreach (var ex in enabledExamples.Where(e => e.DirectMatch))
            {
                var keywords = ex.QuestionPattern
                    .Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(k => k.Length >= 2)
                    .ToList();
                if (keywords.Any(k => cleanedQuestion.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    AppServices.Logs.Info("REPLY", $"命中「关键词直出」示例（分类：{ex.CategoryDisplay}）");
                    return (true, ex.AnswerExample.Trim(), "关键词直出", string.Empty);
                }
            }

            // 2. Call Server AI endpoint with Q&A few-shot examples + group context + knowledge base
            var payloads = enabledExamples.Select(e => new AutoReplyExamplePayload
            {
                Category = e.Category,
                QuestionPattern = e.QuestionPattern,
                AnswerExample = e.AnswerExample
            }).ToList();

            var aiRes = await AppServices.Backend.GenerateGroupAutoReplyAsync(
                groupId,
                senderName,
                cleanedQuestion,
                Config.CustomSystemPrompt,
                Config.MaxReplyChars,
                payloads,
                ct);

            if (aiRes.IsSuccess && !string.IsNullOrWhiteSpace(aiRes.Data?.Reply))
            {
                AppServices.Logs.Info("AI",
                    $"群自动回复生成成功：model={aiRes.Data.Model}, latency={aiRes.Data.LatencyMs} ms, chars={aiRes.Data.Reply.Length}");
                return (true, aiRes.Data.Reply.Trim(), payloads.Count > 0 ? "AI 示例生成" : "AI 生成", string.Empty);
            }

            // 3. Optional fallback reply
            if (!string.IsNullOrWhiteSpace(Config.FallbackReply))
            {
                AppServices.Logs.Warn("REPLY", $"AI 生成失败（{aiRes.ErrorMessage}），已启用兜底回复话术。");
                return (true, Config.FallbackReply.Trim(), "兜底话术", string.Empty);
            }

            return (false, string.Empty, "AI 生成", string.IsNullOrWhiteSpace(aiRes.ErrorMessage) ? "AI 服务未返回内容" : aiRes.ErrorMessage);
        }

        private bool TryMatchMentionSelf(
            WeChat4Message msg,
            WeChat4GroupNewMessagesEvent evt,
            GroupDto trackedGroup,
            out string reason)
        {
            reason = string.Empty;
            string content = msg.Content;

            bool looksLikeAtAll = msg.MentionedAll ||
                                  content.Contains("@所有人", StringComparison.OrdinalIgnoreCase) ||
                                  content.Contains("@All", StringComparison.OrdinalIgnoreCase);

            // Protocol-level <atuserlist> exact SelfWxid match (highest accuracy!)
            if (msg.MentionedSelf)
            {
                reason = "微信原生 @提及";
                return true;
            }

            // Collect candidate self display names: group nickname -> WeChat nickname -> alias -> custom trigger names
            var candidates = new List<string>(8);
            void AddCandidate(string? s)
            {
                string t = (s ?? string.Empty).Trim();
                if (t.Length >= 1 && !candidates.Contains(t, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(t);
                }
            }

            AddCandidate(trackedGroup.MyNickname);
            AddCandidate(evt.MyNickname);
            AddCandidate(evt.SelfNickname);
            AddCandidate(evt.SelfAlias);

            if (!string.IsNullOrWhiteSpace(Config.ExtraTriggerNames))
            {
                foreach (string extra in Config.ExtraTriggerNames.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    AddCandidate(extra);
                }
            }

            foreach (string name in candidates)
            {
                string token = "@" + name;
                int idx = content.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                while (idx >= 0)
                {
                    int after = idx + token.Length;
                    bool boundaryOk = after >= content.Length || MentionBoundaries.Contains(content[after]);
                    if (boundaryOk)
                    {
                        reason = $"文本匹配 @{name}";
                        return true;
                    }
                    idx = content.IndexOf(token, idx + 1, StringComparison.OrdinalIgnoreCase);
                }
            }

            if (looksLikeAtAll && !Config.IgnoreAtAll)
            {
                reason = "@所有人";
                return true;
            }

            return false;
        }

        private static string StripLeadingMention(string text)
        {
            string s = (text ?? string.Empty).Trim();
            while (s.StartsWith('@'))
            {
                int sep = s.IndexOfAny(['\u2005', ' ', '\t', '\r', '\n']);
                if (sep > 0 && sep < s.Length - 1)
                {
                    s = s[(sep + 1)..].TrimStart();
                }
                else
                {
                    break;
                }
            }
            return string.IsNullOrWhiteSpace(s) ? (text ?? string.Empty).Trim() : s;
        }

        private static string FormatFinalReply(string body, string senderName, bool prefixMention)
        {
            string clean = body.Trim();
            if (prefixMention &&
                !string.IsNullOrWhiteSpace(senderName) &&
                !senderName.Equals("群成员", StringComparison.Ordinal) &&
                !clean.StartsWith('@'))
            {
                return $"@{senderName} {clean}";
            }
            return clean;
        }

        private static string? FindBlockedKeyword(string content, string blockedList)
        {
            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(blockedList)) return null;
            foreach (string kw in blockedList.Split(NameSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (kw.Length > 0 && content.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return kw;
                }
            }
            return null;
        }

        private bool IsAlreadyProcessed(string extId)
        {
            if (string.IsNullOrWhiteSpace(extId)) return false;
            lock (_stateLock)
            {
                return _processedExternalIds.Contains(extId);
            }
        }

        private void MarkProcessed(string extId)
        {
            if (string.IsNullOrWhiteSpace(extId)) return;
            lock (_stateLock)
            {
                if (_processedExternalIds.Add(extId))
                {
                    _processedOrder.Enqueue(extId);
                    while (_processedOrder.Count > 2000)
                    {
                        string old = _processedOrder.Dequeue();
                        _processedExternalIds.Remove(old);
                    }
                }
            }
        }

        private void RecordExecution(AutoReplyExecutionRecord record)
        {
            lock (_stateLock)
            {
                _recentRecords.AddFirst(record);
                while (_recentRecords.Count > 100)
                {
                    _recentRecords.RemoveLast();
                }
            }
            ExecutionRecorded?.Invoke(record);
            StateChanged?.Invoke();
        }

        private void UpdateLiveStatus(string status)
        {
            _liveStatusText = status;
            StateChanged?.Invoke();
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            string flat = s.Replace("\r\n", " ").Replace('\n', ' ').Trim();
            return flat.Length <= maxLen ? flat : flat[..maxLen] + "…";
        }
    }
}
