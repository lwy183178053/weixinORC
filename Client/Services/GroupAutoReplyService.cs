using System.Diagnostics;
using System.Text.Json;
using Client.Models;
using Client.Services.WeChat4Db;

namespace Client.Services
{
    /// <summary>
    /// High-throughput, rock-solid group @mention AI auto-reply engine:
    /// - Captures ALL eligible @me messages in chronological order (never drops concurrent @mentions from multiple people!)
    /// - Per-(group, sender) anti-spam cooldown: different people in the same group NEVER block each other
    /// - Parallel AI prefetch pipeline (up to 4 concurrent LLM generations) decoupled from single-threaded WeChat UI control
    /// - Speculative pre-opening: while LLM is generating, immediately brings WeChat forward and enters the target group
    ///   (or verifies already-in-group fast path), so once AI responds, paste &amp; send takes ~0.5s!
    /// - Ready-first with group-affinity scheduling: a slow/timing-out LLM request never stalls other ready replies,
    ///   and consecutive replies for the same group reuse the open chat window without re-searching.
    /// </summary>
    public sealed class GroupAutoReplyService
    {
        public const string DefaultWelcomeTemplate =
            "欢迎 {新人昵称} 加入 {群名}！有任何关于智美产品、项目或服务的问题，都可以随时在群里 @我 咨询哦～";

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static readonly char[] NameSeparators = [',', '，', ';', '；', '/', '|', '\n', '\r'];
        private static readonly char[] MentionBoundaries = [' ', '\t', '\u2005', '\u00A0', '\r', '\n', ',', '，', '.', '。', ':', '：', '?', '？', '!', '！', '、'];

        private sealed class PendingWelcomeBatch
        {
            public long GroupId { get; init; }
            public string GroupKey { get; init; } = string.Empty;
            public string GroupName { get; init; } = string.Empty;
            public string Template { get; init; } = string.Empty;
            public List<string> NewMemberNames { get; } = [];
            public CancellationTokenSource? TimerCts { get; set; }
        }

        private sealed class QueuedAutoReplyItem
        {
            public long SeqId { get; init; }
            public long GroupId { get; init; }
            public string GroupKey { get; init; } = string.Empty;
            public string GroupName { get; init; } = string.Empty;
            public string SenderName { get; init; } = string.Empty;
            public string CooldownKey { get; init; } = string.Empty;
            public string TriggerMessage { get; init; } = string.Empty;
            public string CleanedQuestion { get; init; } = string.Empty;
            public string MatchedBy { get; init; } = string.Empty;
            public bool IsManualTest { get; init; }
            public bool IsWelcome { get; init; }
            public string? FixedReplyText { get; init; }
            public WeChat4Message? TriggerMessageObj { get; init; }
            public IReadOnlyList<WeChat4Message> RecentSessionMessages { get; init; } = [];
            public bool? OverrideAutoSend { get; init; }
            public bool PrefixMention { get; init; }
            public Stopwatch TotalStopwatch { get; } = Stopwatch.StartNew();
            public CancellationTokenSource? LinkedCts { get; init; }
            public CancellationToken CancellationToken { get; init; }
            public Task<(bool Ok, string Reply, string SourceMode, string Error)> GenerationTask { get; set; } = null!;
            public TaskCompletionSource<AutoReplyExecutionRecord>? ManualTcs { get; init; }
            public bool PreOpenAttempted { get; set; }

            public void DisposeResources()
            {
                try { LinkedCts?.Dispose(); } catch { }
            }
        }

        private readonly object _configLock = new();
        private readonly object _stateLock = new();
        private readonly object _queueLock = new();
        private readonly SemaphoreSlim _aiConcurrencyGate = new(4, 4);
        private readonly string _configPath;

        private readonly Dictionary<long, GroupDto> _groupsById = [];
        private readonly HashSet<string> _processedExternalIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _processedOrder = new();
        private readonly Dictionary<string, DateTimeOffset> _lastReplyAtByGroupSender = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<long, PendingWelcomeBatch> _pendingWelcomeByGroup = [];
        private readonly LinkedList<AutoReplyExecutionRecord> _recentRecords = new();

        private readonly List<QueuedAutoReplyItem> _queue = [];
        private TaskCompletionSource<bool> _queueSignal = CreateSignal();
        private CancellationTokenSource _sessionCts = new();
        private PreparedGroupSession? _preparedSession;
        private long _nextSeqId;
        private bool _workerRunning;
        private int _pendingQueueCount;

        private DateTimeOffset _listeningStartedUtc = DateTimeOffset.UtcNow;
        private bool _initialized;
        private bool _isExecuting;
        private string _liveStatusText = "待命中";

        public AutoReplyConfigData Config { get; private set; } = new();
        public bool IsExecuting => _isExecuting;
        public int PendingQueueCount => Volatile.Read(ref _pendingQueueCount);
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

        private static TaskCompletionSource<bool> CreateSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                $"群自动回复并发队列引擎已就绪：全局开关={(Config.GlobalEnabled ? "开启" : "暂停")}，问答示例 {Config.Examples.Count(e => e.Enabled)}/{Config.Examples.Count} 条，自动发送={(Config.AutoSendAfterPaste ? "开" : "仅粘贴")}，并行预开={(Config.PreOpenGroupWhileGenerating ? "开" : "关")}。");
        }

        public void ResetRuntimeStateForLogout()
        {
            lock (_stateLock)
            {
                _groupsById.Clear();
                _processedExternalIds.Clear();
                _processedOrder.Clear();
                _lastReplyAtByGroupSender.Clear();
                foreach (var wb in _pendingWelcomeByGroup.Values)
                {
                    try { wb.TimerCts?.Cancel(); wb.TimerCts?.Dispose(); } catch { }
                }
                _pendingWelcomeByGroup.Clear();
                _listeningStartedUtc = DateTimeOffset.UtcNow;
            }

            List<QueuedAutoReplyItem> dropped;
            lock (_queueLock)
            {
                try { _sessionCts.Cancel(); } catch { }
                _sessionCts.Dispose();
                _sessionCts = new CancellationTokenSource();

                dropped = [.. _queue];
                _queue.Clear();
                _preparedSession = null;
                Volatile.Write(ref _pendingQueueCount, 0);
                SignalQueueLocked();
            }

            foreach (var d in dropped)
            {
                d.ManualTcs?.TrySetCanceled();
                d.DisposeResources();
            }

            _isExecuting = false;
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
                Config.CooldownSecondsPerGroup = Math.Clamp(Config.CooldownSecondsPerGroup, 0, 600);
                Config.MaxMessageAgeSeconds = Math.Clamp(Config.MaxMessageAgeSeconds, 30, 1800);
                Config.MaxReplyChars = Math.Clamp(Config.MaxReplyChars, 40, 2000);
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

        public void NotifyGroupWelcomeChanged(GroupDto updatedGroup)
        {
            lock (_stateLock)
            {
                _groupsById[updatedGroup.Id] = updatedGroup;
            }
            AppServices.Logs.Info("REPLY",
                $"群「{updatedGroup.DisplayName}」(#{updatedGroup.Id}) 新人自动欢迎已设为：{(updatedGroup.WelcomeEnabled ? "开启" : "关闭")}");
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
            if (!Config.GlobalEnabled) return;

            GroupDto? trackedGroup;
            lock (_stateLock)
            {
                _groupsById.TryGetValue(evt.GroupId, out trackedGroup);
            }
            if (trackedGroup is null || (!trackedGroup.AutoReplyEnabled && !trackedGroup.WelcomeEnabled)) return;
            if (!string.IsNullOrWhiteSpace(evt.OwnerWxid) &&
                !string.IsNullOrWhiteSpace(trackedGroup.OwnerWxid) &&
                !string.Equals(trackedGroup.OwnerWxid, evt.OwnerWxid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string groupName = !string.IsNullOrWhiteSpace(trackedGroup.GroupName) ? trackedGroup.GroupName : evt.GroupName;
            var nowUtc = DateTimeOffset.UtcNow;

            // Scan ALL new messages in chronological order:
            // A) New member join events -> per-group fixed-template auto-welcome (with 6s burst coalescing)
            // B) Text & Quote @mention messages -> parallel AI multimodal reply pipeline
            int enqueuedInBatch = 0;
            for (int i = 0; i < evt.NewMessages.Count; i++)
            {
                var m = evt.NewMessages[i];
                if (IsAlreadyProcessed(m.ExternalId)) continue;

                // Freshness guard: never reply/welcome to historical messages from before app startup or older than MaxMessageAgeSeconds
                if (m.SentAtUtc < _listeningStartedUtc.AddSeconds(-10))
                {
                    MarkProcessed(m.ExternalId);
                    continue;
                }
                double ageSec = (nowUtc - m.SentAtUtc).TotalSeconds;
                if (ageSec > Config.MaxMessageAgeSeconds)
                {
                    MarkProcessed(m.ExternalId);
                    AppServices.Logs.Info("REPLY", $"跳过超时消息（已过去 {ageSec:F0}s > {Config.MaxMessageAgeSeconds}s）：群「{groupName}」");
                    continue;
                }

                // A. New member joined the group -> schedule coalesced fixed-template welcome
                if (m.Kind == "member_join")
                {
                    MarkProcessed(m.ExternalId);
                    if (trackedGroup.WelcomeEnabled)
                    {
                        var names = m.JoinedMembers.Count > 0 ? m.JoinedMembers : ["新朋友"];
                        ScheduleCoalescedWelcome(evt.GroupId, evt.GroupKey, groupName, trackedGroup.WelcomeTemplate, names);
                    }
                    continue;
                }

                // B. @mention AI auto-reply (supports BOTH normal text AND quoted messages including quoted images!)
                if (!trackedGroup.AutoReplyEnabled) continue;
                if (m.IsOutgoing || m.IsSystemEvent || m.Kind is not ("text" or "quote") || string.IsNullOrWhiteSpace(m.Content)) continue;

                if (!TryMatchMentionSelf(m, evt, trackedGroup, out string matchedBy)) continue;

                // Mark processed immediately so duplicate syncs never re-enqueue
                MarkProcessed(m.ExternalId);

                string senderName = string.IsNullOrWhiteSpace(m.SenderName) ? "群成员" : m.SenderName.Trim();
                string cleanedQuestion = StripLeadingMention(m.Content);

                // 1. Blocked sensitive keywords guard
                string? hitBlocked = FindBlockedKeyword(m.Content, Config.BlockedKeywords);
                if (!string.IsNullOrEmpty(hitBlocked))
                {
                    AppServices.Logs.Warn("REPLY", $"群「{groupName}」成员「{senderName}」消息命中敏感词黑名单「{hitBlocked}」，已跳过。");
                    RecordExecution(new AutoReplyExecutionRecord
                    {
                        GroupId = evt.GroupId,
                        GroupName = groupName,
                        SenderName = senderName,
                        TriggerMessage = m.Content,
                        SourceMode = matchedBy,
                        StatusLabel = "已跳过",
                        IsSkipped = true,
                        Detail = $"命中敏感词黑名单「{hitBlocked}」，交由人工处理。"
                    });
                    continue;
                }

                // 2. Per-(group, sender) anti-spam cooldown: different people in the same group NEVER block each other!
                string cooldownKey = BuildCooldownKey(evt.GroupId, senderName);
                int cdSec = Config.CooldownSecondsPerGroup;
                if (cdSec > 0)
                {
                    bool coolingDown = false;
                    double remainingSec = 0;
                    lock (_stateLock)
                    {
                        var now = DateTimeOffset.UtcNow;
                        if (_lastReplyAtByGroupSender.Count > 256)
                        {
                            var expired = _lastReplyAtByGroupSender
                                .Where(kv => (now - kv.Value).TotalMinutes >= 60)
                                .Select(kv => kv.Key)
                                .ToList();
                            foreach (string k in expired) _lastReplyAtByGroupSender.Remove(k);
                        }
                        if (_lastReplyAtByGroupSender.TryGetValue(cooldownKey, out var lastAt))
                        {
                            double elapsed = (now - lastAt).TotalSeconds;
                            if (elapsed < cdSec)
                            {
                                coolingDown = true;
                                remainingSec = cdSec - elapsed;
                            }
                        }
                        if (!coolingDown)
                        {
                            _lastReplyAtByGroupSender[cooldownKey] = now;
                        }
                    }

                    if (coolingDown)
                    {
                        AppServices.Logs.Info("REPLY",
                            $"群「{groupName}」成员「{senderName}」处于同人防刷屏冷却中（剩余 {remainingSec:F0}s），跳过重复触发。");
                        RecordExecution(new AutoReplyExecutionRecord
                        {
                            GroupId = evt.GroupId,
                            GroupName = groupName,
                            SenderName = senderName,
                            TriggerMessage = m.Content,
                            SourceMode = matchedBy,
                            StatusLabel = "冷却跳过",
                            IsSkipped = true,
                            Detail = $"同一群友 {cdSec} 秒内防重复刷屏冷却中（不影响其他群友排队回复）。"
                        });
                        continue;
                    }
                }

                AppServices.Logs.Info("REPLY",
                    $"检测到群「{groupName}」被 @（命中方式：{matchedBy}{(m.Kind == "quote" ? " · 引用消息" : "")}），发言人={senderName}，内容=「{Truncate(m.Content, 80)}」-> 已加入回复队列并启动并行生成");

                EnqueueItem(new QueuedAutoReplyItem
                {
                    SeqId = Interlocked.Increment(ref _nextSeqId),
                    GroupId = evt.GroupId,
                    GroupKey = evt.GroupKey,
                    GroupName = groupName,
                    SenderName = senderName,
                    CooldownKey = cooldownKey,
                    TriggerMessage = m.Content,
                    CleanedQuestion = cleanedQuestion,
                    MatchedBy = matchedBy,
                    IsManualTest = false,
                    TriggerMessageObj = m,
                    RecentSessionMessages = evt.AllRecentSessionMessages,
                    OverrideAutoSend = null,
                    PrefixMention = Config.PrefixMentionSender,
                    CancellationToken = _sessionCts.Token
                });
                enqueuedInBatch++;
            }

            if (enqueuedInBatch > 1)
            {
                AppServices.Logs.Info("REPLY",
                    $"本批同步共捕获 {enqueuedInBatch} 条 @消息（群「{groupName}」），已全部入队并行生成回复并将依次发送。");
            }
        }

        private void ScheduleCoalescedWelcome(
            long groupId,
            string groupKey,
            string groupName,
            string rawTemplate,
            IEnumerable<string> memberNames)
        {
            PendingWelcomeBatch batch;
            CancellationTokenSource timerCts;
            lock (_stateLock)
            {
                if (!_pendingWelcomeByGroup.TryGetValue(groupId, out batch!))
                {
                    batch = new PendingWelcomeBatch
                    {
                        GroupId = groupId,
                        GroupKey = groupKey,
                        GroupName = groupName,
                        Template = string.IsNullOrWhiteSpace(rawTemplate) ? DefaultWelcomeTemplate : rawTemplate.Trim()
                    };
                    _pendingWelcomeByGroup[groupId] = batch;
                }

                foreach (string name in memberNames)
                {
                    string clean = (name ?? string.Empty).Trim();
                    if (!string.IsNullOrEmpty(clean) && !batch.NewMemberNames.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    {
                        batch.NewMemberNames.Add(clean);
                    }
                }

                try { batch.TimerCts?.Cancel(); batch.TimerCts?.Dispose(); } catch { }
                timerCts = CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token);
                batch.TimerCts = timerCts;
            }

            AppServices.Logs.Info("REPLY",
                $"检测到新人进群「{groupName}」：{string.Join("、", memberNames)}，将在 6 秒合批窗口后发送本群自动欢迎语。");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(6), timerCts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                List<string> joinedSnapshot;
                string templateSnapshot;
                string keySnapshot;
                string nameSnapshot;
                lock (_stateLock)
                {
                    if (!_pendingWelcomeByGroup.TryGetValue(groupId, out var current) || !ReferenceEquals(current, batch))
                    {
                        return;
                    }
                    _pendingWelcomeByGroup.Remove(groupId);
                    joinedSnapshot = [.. batch.NewMemberNames];
                    templateSnapshot = batch.Template;
                    keySnapshot = batch.GroupKey;
                    nameSnapshot = batch.GroupName;
                    try { batch.TimerCts?.Dispose(); } catch { }
                    batch.TimerCts = null;
                }

                if (joinedSnapshot.Count == 0 || !Config.GlobalEnabled) return;

                string formatted = FormatWelcomeMessage(templateSnapshot, nameSnapshot, joinedSnapshot);
                if (string.IsNullOrWhiteSpace(formatted)) return;

                string joinedDisplay = string.Join("、", joinedSnapshot);
                EnqueueItem(new QueuedAutoReplyItem
                {
                    SeqId = Interlocked.Increment(ref _nextSeqId),
                    GroupId = groupId,
                    GroupKey = keySnapshot,
                    GroupName = nameSnapshot,
                    SenderName = joinedDisplay,
                    CooldownKey = string.Empty,
                    TriggerMessage = $"👋 新人进群：{joinedDisplay}",
                    CleanedQuestion = joinedDisplay,
                    MatchedBy = "新人进群",
                    IsManualTest = false,
                    IsWelcome = true,
                    FixedReplyText = formatted,
                    OverrideAutoSend = null,
                    PrefixMention = false,
                    CancellationToken = _sessionCts.Token
                });
            });
        }

        public static string FormatWelcomeMessage(string template, string groupName, IReadOnlyList<string> memberNames)
        {
            string tpl = string.IsNullOrWhiteSpace(template) ? DefaultWelcomeTemplate : template.Trim();
            var names = memberNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names.Count == 0) names = ["新朋友"];

            string mentionList = string.Join(" ", names.Select(n => n == "新朋友" ? n : $"@{n}"));
            string plainList = string.Join("、", names);

            // Avoid double "@@" if user already wrote "@{新人昵称}" in the template
            string res = tpl
                .Replace("@{新人昵称}", mentionList, StringComparison.Ordinal)
                .Replace("{新人昵称}", mentionList, StringComparison.Ordinal)
                .Replace("{新人}", plainList, StringComparison.Ordinal)
                .Replace("{群名}", groupName ?? string.Empty, StringComparison.Ordinal);

            return res.Trim();
        }

        /// <summary>
        /// Runs a live end-to-end test through the exact same parallel-prefetch + pre-open queue pipeline
        /// so manual tests also demonstrate pre-opening speed and never collide with background auto-replies.
        /// </summary>
        public Task<AutoReplyExecutionRecord> RunManualTestAsync(
            GroupDto targetGroup,
            string simulatedQuestion,
            bool sendImmediately,
            CancellationToken ct = default)
        {
            string question = string.IsNullOrWhiteSpace(simulatedQuestion) ? "您好，请问工作时间是几点？" : simulatedQuestion.Trim();
            AppServices.Logs.Info("REPLY",
                $"开始手动测试自动回复流程：群「{targetGroup.DisplayName}」，问题=「{Truncate(question, 80)}」，自动发送={(sendImmediately ? "是" : "否(仅粘贴)")}，并行预开={(Config.PreOpenGroupWhileGenerating ? "开" : "关")}");

            var tcs = new TaskCompletionSource<AutoReplyExecutionRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
            var linkedCts = ct.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_sessionCts.Token, ct)
                : null;

            EnqueueItem(new QueuedAutoReplyItem
            {
                SeqId = Interlocked.Increment(ref _nextSeqId),
                GroupId = targetGroup.Id,
                GroupKey = targetGroup.GroupKey,
                GroupName = targetGroup.DisplayName,
                SenderName = "手动测试",
                CooldownKey = string.Empty,
                TriggerMessage = question,
                CleanedQuestion = question,
                MatchedBy = "手动测试",
                IsManualTest = true,
                OverrideAutoSend = sendImmediately,
                PrefixMention = false,
                LinkedCts = linkedCts,
                CancellationToken = linkedCts?.Token ?? _sessionCts.Token,
                ManualTcs = tcs
            });

            return tcs.Task;
        }

        private void EnqueueItem(QueuedAutoReplyItem item)
        {
            // Immediately kick off parallel AI reply generation (bounded by _aiConcurrencyGate = 4)
            // BEFORE waiting on the single-threaded WeChat UI automation dispatcher!
            item.GenerationTask = GenerateReplyParallelAsync(item);

            bool shouldStartWorker;
            int queueLen;
            lock (_queueLock)
            {
                _queue.Add(item);
                queueLen = _queue.Count;
                Volatile.Write(ref _pendingQueueCount, queueLen);
                SignalQueueLocked();

                shouldStartWorker = !_workerRunning;
                if (shouldStartWorker)
                {
                    _workerRunning = true;
                }
            }

            if (queueLen > 1)
            {
                AppServices.Logs.Info("REPLY", $"回复队列当前待处理 {queueLen} 条（最新入队：群「{item.GroupName}」/ {item.SenderName}）。");
            }
            StateChanged?.Invoke();

            if (shouldStartWorker)
            {
                _ = Task.Run(RunUiDispatcherLoopAsync);
            }
        }

        private async Task<(bool Ok, string Reply, string SourceMode, string Error)> GenerateReplyParallelAsync(QueuedAutoReplyItem item)
        {
            if (!string.IsNullOrWhiteSpace(item.FixedReplyText))
            {
                return (true, item.FixedReplyText.Trim(), item.IsWelcome ? "新人欢迎模板" : "固定模板", string.Empty);
            }

            await _aiConcurrencyGate.WaitAsync(item.CancellationToken);
            try
            {
                List<string>? attachedImages = null;
                if (item.TriggerMessageObj is not null && !string.IsNullOrWhiteSpace(item.GroupKey))
                {
                    var imgs = await AppServices.WeChat4Sync.ResolveGroupAutoReplyImagesAsync(
                        item.GroupKey,
                        item.TriggerMessageObj,
                        item.RecentSessionMessages,
                        item.CancellationToken);
                    if (imgs.Count > 0)
                    {
                        attachedImages = imgs;
                        AppServices.Logs.Info("AI", $"群「{item.GroupName}」自动回复已附带 {imgs.Count} 张引用/近期图片进入多模态识别。");
                    }
                }

                string senderForAi = item.IsManualTest ? "测试群友" : item.SenderName;
                return await GenerateReplyTextAsync(item.GroupId, senderForAi, item.CleanedQuestion, attachedImages, item.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                return (false, string.Empty, "已取消", "请求已取消");
            }
            catch (Exception ex)
            {
                return (false, string.Empty, "AI 生成", ex.Message);
            }
            finally
            {
                _aiConcurrencyGate.Release();
            }
        }

        /// <summary>
        /// Single-threaded WeChat UI automation dispatcher:
        /// 1. Picks any ALREADY-READY AI reply immediately (preferring manual test, then same-group affinity for zero-search delivery, then FIFO)
        /// 2. If all queued items are still waiting for LLM responses, speculatively PRE-OPENS the head item's target group
        ///    in WeChat IN PARALLEL while AI is thinking, then sleeps on Task.WhenAny(all generation tasks + new-arrival signal)
        /// 3. As soon as ANY queued reply finishes AI generation, delivers it immediately without being head-of-line blocked
        ///    by another slow/timing-out LLM call!
        /// </summary>
        private async Task RunUiDispatcherLoopAsync()
        {
            var executor = new WeChatAutoReplyExecutor();

            while (true)
            {
                List<QueuedAutoReplyItem> snapshot;
                Task signalTask;
                PreparedGroupSession? currentPrepared;

                lock (_queueLock)
                {
                    if (_queue.Count == 0)
                    {
                        _workerRunning = false;
                        _isExecuting = false;
                        _preparedSession = null;
                        Volatile.Write(ref _pendingQueueCount, 0);
                        UpdateLiveStatus(Config.GlobalEnabled ? $"监听中（已启用 {GetEnabledGroupCount()} 个群）" : "全局已暂停");
                        return;
                    }

                    snapshot = [.. _queue];
                    signalTask = _queueSignal.Task;
                    currentPrepared = _preparedSession;
                    Volatile.Write(ref _pendingQueueCount, _queue.Count);
                    _isExecuting = true;
                }

                // 1. Check if any queued task has ALREADY completed AI generation
                var readyItems = snapshot.Where(x => x.GenerationTask.IsCompleted).ToList();
                if (readyItems.Count > 0)
                {
                    QueuedAutoReplyItem chosen = SelectNextReadyItem(readyItems, currentPrepared?.GroupName);
                    int remainingAfter;
                    lock (_queueLock)
                    {
                        _queue.Remove(chosen);
                        remainingAfter = _queue.Count;
                        Volatile.Write(ref _pendingQueueCount, remainingAfter);
                    }

                    await ExecuteDeliveryForItemAsync(executor, chosen, remainingAfter);
                    continue;
                }

                // 2. No AI reply is ready yet -> Speculatively PRE-OPEN the head task's target group while LLM is still thinking!
                QueuedAutoReplyItem head = snapshot.FirstOrDefault(x => x.IsManualTest) ?? snapshot[0];
                bool alreadyPreparedForHead = currentPrepared is { Success: true } &&
                                              string.Equals(currentPrepared.GroupName, head.GroupName, StringComparison.OrdinalIgnoreCase);

                if (Config.PreOpenGroupWhileGenerating &&
                    (head.IsManualTest || Config.GlobalEnabled) &&
                    !alreadyPreparedForHead &&
                    !head.PreOpenAttempted)
                {
                    head.PreOpenAttempted = true;
                    string qSuffix = snapshot.Count > 1 ? $" · 排队 {snapshot.Count} 条" : string.Empty;
                    UpdateLiveStatus($"AI 生成中 · 预开「{head.GroupName}」{qSuffix}");
                    AppServices.Logs.Info("AUTO", $"AI 请求进行中，同步提前打开并定位目标群「{head.GroupName}」...");

                    var prep = await executor.PrepareGroupChatAsync(head.GroupName, Config, head.CancellationToken);
                    if (prep.WasCancelledByEsc)
                    {
                        bool removed;
                        lock (_queueLock)
                        {
                            removed = _queue.Remove(head);
                            _preparedSession = null;
                            Volatile.Write(ref _pendingQueueCount, _queue.Count);
                        }
                        if (removed)
                        {
                            ClearCooldownOnFailure(head);
                            CompleteCancelledRecord(head, prep.Message);
                        }
                        continue;
                    }

                    if (prep.Success)
                    {
                        lock (_queueLock)
                        {
                            _preparedSession = prep;
                        }
                        AppServices.Logs.Info("AUTO",
                            $"目标群「{head.GroupName}」已预开就绪（{(prep.UsedFastPathAlreadyInGroup ? "免搜索直达" : "已切群")}，耗时 {prep.ElapsedMs} ms），静候 AI 回复后即刻粘贴发送。");
                    }

                    // Re-check immediately in case an AI reply finished while we were pre-opening!
                    continue;
                }

                // 3. Wait until ANY queued AI generation completes OR a new item arrives in the queue
                string waitSuffix = snapshot.Count > 1 ? $"（{snapshot.Count} 条并行生成中）" : "（AI 生成中）";
                string preTag = alreadyPreparedForHead ? $"已预开「{head.GroupName}」" : $"等待「{head.GroupName}」";
                UpdateLiveStatus($"{preTag} {waitSuffix}");

                var waitTasks = new List<Task>(snapshot.Count + 1) { signalTask };
                foreach (var item in snapshot)
                {
                    waitTasks.Add(item.GenerationTask);
                }

                try
                {
                    await Task.WhenAny(waitTasks);
                }
                catch
                {
                }
            }
        }

        private static QueuedAutoReplyItem SelectNextReadyItem(List<QueuedAutoReplyItem> readyItems, string? openGroupName)
        {
            // Manual tests always get first priority once ready
            var manual = readyItems.FirstOrDefault(x => x.IsManualTest);
            if (manual is not null) return manual;

            // Group-affinity optimization: if WeChat is already open on group X and a reply for group X is ready,
            // send it immediately (~0.5s, zero group switching!) before switching to another group
            if (!string.IsNullOrWhiteSpace(openGroupName))
            {
                var sameGroup = readyItems
                    .Where(x => string.Equals(x.GroupName, openGroupName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.SeqId)
                    .FirstOrDefault();
                if (sameGroup is not null) return sameGroup;
            }

            // Strict FIFO arrival order otherwise
            return readyItems.OrderBy(x => x.SeqId).First();
        }

        private async Task ExecuteDeliveryForItemAsync(
            WeChatAutoReplyExecutor executor,
            QueuedAutoReplyItem item,
            int remainingInQueue)
        {
            string sourcePrefix = item.IsManualTest ? "测试·" : string.Empty;
            try
            {
                if (!item.IsManualTest && !Config.GlobalEnabled)
                {
                    item.TotalStopwatch.Stop();
                    ClearCooldownOnFailure(item);
                    AppServices.Logs.Info("REPLY", $"全局自动回复已暂停，跳过发送排队中的群「{item.GroupName}」（{item.SenderName}）回复。");
                    RecordExecution(new AutoReplyExecutionRecord
                    {
                        GroupId = item.GroupId,
                        GroupName = item.GroupName,
                        SenderName = item.SenderName,
                        TriggerMessage = item.TriggerMessage,
                        SourceMode = item.MatchedBy,
                        StatusLabel = "已暂停跳过",
                        IsSkipped = true,
                        Detail = "AI 生成期间用户关闭了全局自动回复开关，已安全取消发送。",
                        ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
                    });
                    return;
                }

                var (okGen, replyBody, sourceMode, genError) = await item.GenerationTask;
                if (!okGen || string.IsNullOrWhiteSpace(replyBody))
                {
                    item.TotalStopwatch.Stop();
                    ClearCooldownOnFailure(item);
                    AppServices.Logs.Error("REPLY", $"群「{item.GroupName}」（{item.SenderName}）生成回复失败：{genError}");
                    var failRec = new AutoReplyExecutionRecord
                    {
                        GroupId = item.GroupId,
                        GroupName = item.GroupName,
                        SenderName = item.SenderName,
                        TriggerMessage = item.TriggerMessage,
                        SourceMode = sourcePrefix + sourceMode,
                        StatusLabel = "生成失败",
                        Detail = genError,
                        ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
                    };
                    RecordExecution(failRec);
                    item.ManualTcs?.TrySetResult(failRec);
                    return;
                }

                string finalReply = FormatFinalReply(replyBody, item.SenderName, item.PrefixMention);
                AppServices.Logs.Info("REPLY", $"群「{item.GroupName}」（{item.SenderName}）回复已就绪（{sourceMode}）：「{Truncate(finalReply, 90)}」");

                string queueNote = remainingInQueue > 0 ? $" · 排队剩余 {remainingInQueue} 条" : string.Empty;
                UpdateLiveStatus($"正在回复「{item.GroupName}」{queueNote}");

                PreparedGroupSession? matchingPrepared;
                lock (_queueLock)
                {
                    matchingPrepared = _preparedSession is { Success: true } &&
                                       string.Equals(_preparedSession.GroupName, item.GroupName, StringComparison.OrdinalIgnoreCase)
                        ? _preparedSession
                        : null;
                }

                DateTimeOffset sendWindowStartUtc = DateTimeOffset.UtcNow;
                var autoRes = await executor.DeliverPreparedReplyAsync(
                    matchingPrepared,
                    item.GroupName,
                    finalReply,
                    Config,
                    overrideAutoSend: item.OverrideAutoSend,
                    groupKey: item.GroupKey,
                    ct: item.CancellationToken);

                item.TotalStopwatch.Stop();

                lock (_queueLock)
                {
                    _preparedSession = autoRes.Success ? autoRes.Session : null;
                }

                if (autoRes.Success)
                {
                    if (!item.IsManualTest && !string.IsNullOrEmpty(item.CooldownKey))
                    {
                        lock (_stateLock)
                        {
                            _lastReplyAtByGroupSender[item.CooldownKey] = DateTimeOffset.UtcNow;
                        }
                    }

                    bool deliveryConfirmed = false;
                    bool verifiedAttempted = false;
                    string finalDetail = autoRes.Message;

                    if (autoRes.WasSent && !string.IsNullOrWhiteSpace(item.GroupKey))
                    {
                        verifiedAttempted = true;
                        UpdateLiveStatus($"正在落库确权「{item.GroupName}」...");
                        var (confirmed, confirmDetail) = await AppServices.WeChat4Sync.VerifyGroupOutgoingMessageDeliveredAsync(
                            item.GroupKey,
                            finalReply,
                            sendWindowStartUtc,
                            item.CancellationToken);
                        deliveryConfirmed = confirmed;
                        finalDetail = $"{autoRes.Message} {confirmDetail}";
                        if (confirmed)
                        {
                            AppServices.Logs.Success("REPLY", $"群「{item.GroupName}」发送确权通过：{confirmDetail}");
                        }
                        else
                        {
                            AppServices.Logs.Warn("REPLY", $"群「{item.GroupName}」发送未确认落库：{confirmDetail}");
                        }
                    }

                    string statusLabel;
                    bool recordSuccess = true;
                    bool recordSkipped = false;
                    if (!autoRes.WasSent)
                    {
                        statusLabel = "已粘贴待确认";
                    }
                    else if (verifiedAttempted && !deliveryConfirmed)
                    {
                        statusLabel = "未确认送达";
                        recordSuccess = false;
                        recordSkipped = true;
                    }
                    else if (item.IsWelcome)
                    {
                        statusLabel = deliveryConfirmed ? "欢迎已确权" : "欢迎已发送";
                    }
                    else if (item.IsManualTest)
                    {
                        statusLabel = deliveryConfirmed ? "测试已确权" : "测试已发送";
                    }
                    else
                    {
                        statusLabel = deliveryConfirmed ? "已发送·已确权" : "已发送";
                    }

                    var okRec = new AutoReplyExecutionRecord
                    {
                        GroupId = item.GroupId,
                        GroupName = item.GroupName,
                        SenderName = item.SenderName,
                        TriggerMessage = item.TriggerMessage,
                        GeneratedReply = finalReply,
                        SourceMode = sourcePrefix + sourceMode,
                        StatusLabel = statusLabel,
                        IsSuccess = recordSuccess,
                        IsSkipped = recordSkipped,
                        Detail = finalDetail,
                        ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
                    };
                    RecordExecution(okRec);
                    item.ManualTcs?.TrySetResult(okRec);
                }
                else
                {
                    ClearCooldownOnFailure(item);
                    var errRec = new AutoReplyExecutionRecord
                    {
                        GroupId = item.GroupId,
                        GroupName = item.GroupName,
                        SenderName = item.SenderName,
                        TriggerMessage = item.TriggerMessage,
                        GeneratedReply = finalReply,
                        SourceMode = sourcePrefix + sourceMode,
                        StatusLabel = autoRes.WasSkippedByDraft
                            ? "草稿避让"
                            : (autoRes.WasCancelledByEsc ? "已中止" : (item.IsManualTest ? "测试失败" : "操控失败")),
                        IsSkipped = autoRes.WasCancelledByEsc || autoRes.WasSkippedByDraft,
                        Detail = autoRes.Message,
                        ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
                    };
                    RecordExecution(errRec);
                    item.ManualTcs?.TrySetResult(errRec);
                }
            }
            catch (Exception ex)
            {
                item.TotalStopwatch.Stop();
                ClearCooldownOnFailure(item);
                AppServices.Logs.Error("REPLY", $"处理群「{item.GroupName}」（{item.SenderName}）自动回复异常：{ex.Message}");
                var exRec = new AutoReplyExecutionRecord
                {
                    GroupId = item.GroupId,
                    GroupName = item.GroupName,
                    SenderName = item.SenderName,
                    TriggerMessage = item.TriggerMessage,
                    SourceMode = item.IsManualTest ? "测试·异常" : "自动回复",
                    StatusLabel = "异常",
                    Detail = ex.Message,
                    ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
                };
                RecordExecution(exRec);
                item.ManualTcs?.TrySetResult(exRec);
            }
            finally
            {
                item.DisposeResources();
            }
        }

        private void ClearCooldownOnFailure(QueuedAutoReplyItem item)
        {
            if (item.IsManualTest || string.IsNullOrEmpty(item.CooldownKey)) return;
            lock (_stateLock)
            {
                _lastReplyAtByGroupSender.Remove(item.CooldownKey);
            }
        }

        private void CompleteCancelledRecord(QueuedAutoReplyItem item, string detail)
        {
            item.TotalStopwatch.Stop();
            var rec = new AutoReplyExecutionRecord
            {
                GroupId = item.GroupId,
                GroupName = item.GroupName,
                SenderName = item.SenderName,
                TriggerMessage = item.TriggerMessage,
                SourceMode = item.IsManualTest ? "测试·中止" : item.MatchedBy,
                StatusLabel = "已中止",
                IsSkipped = true,
                Detail = detail,
                ElapsedMs = item.TotalStopwatch.ElapsedMilliseconds
            };
            RecordExecution(rec);
            item.ManualTcs?.TrySetResult(rec);
            item.DisposeResources();
        }

        private void SignalQueueLocked()
        {
            var prev = _queueSignal;
            _queueSignal = CreateSignal();
            prev.TrySetResult(true);
        }

        private static string BuildCooldownKey(long groupId, string senderName) =>
            $"{groupId}|{(senderName ?? string.Empty).Trim().ToLowerInvariant()}";

        private async Task<(bool Ok, string Reply, string SourceMode, string Error)> GenerateReplyTextAsync(
            long groupId,
            string senderName,
            string cleanedQuestion,
            List<string>? attachedImages,
            CancellationToken ct)
        {
            List<AutoReplyExampleItem> enabledExamples;
            lock (_configLock)
            {
                enabledExamples = Config.Examples
                    .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.QuestionPattern) && !string.IsNullOrWhiteSpace(e.AnswerExample))
                    .ToList();
            }

            // 1. Check DirectMatch examples first (instant 0ms!) — skip direct-match when images are attached so vision AI inspects them
            if (attachedImages is not { Count: > 0 })
            {
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
            }

            // 2. Call Server AI endpoint with Q&A few-shot examples + compact group context + knowledge base + optional multimodal images
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
                attachedImages,
                ct);

            if (aiRes.IsSuccess && !string.IsNullOrWhiteSpace(aiRes.Data?.Reply))
            {
                string toolInfo = string.Empty;
                if (aiRes.Data.ToolTrace is { Count: > 0 } traces)
                {
                    toolInfo = ", tools=" + string.Join(",", traces.Select(t => $"{t.SkillName}({t.Summary})"));
                }
                string modeLabel = attachedImages is { Count: > 0 }
                    ? "多模态 AI 生成"
                    : (payloads.Count > 0 ? "AI 示例生成" : "AI 生成");
                AppServices.Logs.Info("AI",
                    $"群自动回复生成成功：model={aiRes.Data.Model}, latency={aiRes.Data.LatencyMs} ms, chars={aiRes.Data.Reply.Length}, imgs={attachedImages?.Count ?? 0}{toolInfo}");
                return (true, aiRes.Data.Reply.Trim(), modeLabel, string.Empty);
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

            if (msg.MentionedSelf)
            {
                reason = "微信原生 @提及";
                return true;
            }

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
                !senderName.Equals("手动测试", StringComparison.Ordinal) &&
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
