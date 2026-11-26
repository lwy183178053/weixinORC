using System.Collections.Concurrent;
using System.Diagnostics;
using Client.Models;

namespace Client.Services.WeChat4Db
{
    /// <summary>
    /// Automatic WeChat 4.x local database sync &amp; file-change watcher service:
    /// - Popup-driven explicit import for selected friends (by tag or individual) and selected groups
    /// - Real-time file-change detection on WeChat db_storage (*.db / *.db-wal) + lightweight patrol
    /// - Targeted auto-sync: ONLY synchronizes chat messages, profiles, and avatars for friends &amp; groups
    ///   that have already been imported into the workbench
    /// - Syncs customer avatars, group avatars, group member avatars, and local .dat chat images
    /// </summary>
    public sealed class WeChat4SyncService : IDisposable
    {
        private readonly SemaphoreSlim _syncGate = new(1, 1);
        private readonly object _watcherLock = new();
        private readonly object _logLock = new();
        private readonly LinkedList<string> _recentActivityLogs = new();

        private WeChat4Reader? _activeReader;
        private string? _activeDecryptedDir;
        private WeChat4AccountDir? _activeAccount;
        private readonly Dictionary<string, long> _lastSyncedSessionTime = new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _dbWatcher;
        private CancellationTokenSource? _watcherCts;
        private CancellationTokenSource? _debounceCts;
        private Task? _patrolTask;
        private string _watchedDbDir = string.Empty;
        private string _lastDbFingerprint = string.Empty;
        private DateTimeOffset _activeAccountLastModified = DateTimeOffset.MinValue;
        private long _syncRevision;
        private readonly HashSet<string> _uploadedSelfAvatarWxids = new(StringComparer.OrdinalIgnoreCase);
        private List<WeChatAccountDto> _knownAccounts = [];

        public WeChat4Reader? ActiveReader => _activeReader;
        public WeChat4AccountDir? ActiveAccount => _activeAccount;
        public IReadOnlyList<WeChatAccountDto> KnownAccounts => _knownAccounts;
        public WeChatAccountDto? CurrentOnlineAccount =>
            (_activeAccount is not null
                ? _knownAccounts.FirstOrDefault(a => string.Equals(a.WechatId, _activeAccount.Wxid, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? _knownAccounts.FirstOrDefault(a => a.IsOnline)
            ?? _knownAccounts.FirstOrDefault();

        /// <summary>
        /// The wxid of the currently logged-in / active WeChat account on this PC (empty if none detected yet).
        /// </summary>
        public string ActiveOwnerWxid =>
            !string.IsNullOrWhiteSpace(_activeReader?.SelfWxid) ? _activeReader.SelfWxid :
            !string.IsNullOrWhiteSpace(_activeAccount?.Wxid) ? _activeAccount.Wxid :
            CurrentOnlineAccount?.WechatId ?? string.Empty;
        public bool HasLoadedSnapshot => _activeReader is not null;
        public bool IsFileWatcherRunning => _watcherCts is not null && !_watcherCts.IsCancellationRequested;
        public string WatchedDbStoragePath => _watchedDbDir;
        public long SyncRevision => Volatile.Read(ref _syncRevision);
        public int MonitoredCustomersCount { get; private set; }
        public int MonitoredGroupsCount { get; private set; }
        public DateTimeOffset? LastSyncAt { get; private set; }
        public DateTimeOffset? LastFileChangeAt { get; private set; }
        public WeChat4SyncReport? LastReport { get; private set; }
        public bool LastSyncNeedsAdmin { get; private set; }
        public string WatchStatusText { get; private set; } = "微信自动监听：未启动";

        public event Action<WeChat4SyncReport>? SyncCompleted;
        public event Action<string>? AutoSyncStatusChanged;
        public event Action<string>? ActivityLogged;
        public event Action? AccountsUpdated;
        public event Action<WeChat4GroupNewMessagesEvent>? GroupNewMessagesSynced;

        public string ResolveAccountDisplayName(string? ownerWxid)
        {
            if (string.IsNullOrWhiteSpace(ownerWxid)) return string.Empty;
            var acc = _knownAccounts.FirstOrDefault(a => string.Equals(a.WechatId, ownerWxid, StringComparison.OrdinalIgnoreCase));
            if (acc is not null)
            {
                return !string.IsNullOrWhiteSpace(acc.Nickname) && !acc.Nickname.Equals(acc.DisplayAccountId, StringComparison.OrdinalIgnoreCase)
                    ? $"{acc.Nickname} ({acc.DisplayAccountId})"
                    : acc.DisplayName;
            }
            if (_activeReader is not null && string.Equals(_activeReader.SelfWxid, ownerWxid, StringComparison.OrdinalIgnoreCase))
            {
                return _activeReader.SelfContact?.DisplayName ?? ownerWxid;
            }
            return ownerWxid;
        }

        public void ResetAccountCachesForLogout()
        {
            StopAutoSyncTimer();
            _knownAccounts = [];
            _uploadedSelfAvatarWxids.Clear();
            _lastSyncedSessionTime.Clear();
            AccountsUpdated?.Invoke();
        }

        public IReadOnlyList<string> GetRecentActivityLogs()
        {
            lock (_logLock)
            {
                return _recentActivityLogs.ToList();
            }
        }

        private void RecordActivity(string message)
        {
            string stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
            lock (_logLock)
            {
                _recentActivityLogs.AddFirst(stamped);
                while (_recentActivityLogs.Count > 80) _recentActivityLogs.RemoveLast();
            }
            AppServices.Logs.Info("WX4", message);
            ActivityLogged?.Invoke(stamped);
        }

        private void UpdateWatchStatus(string status)
        {
            WatchStatusText = status;
            AutoSyncStatusChanged?.Invoke(status);
        }

        public sealed class LoadSnapshotResult
        {
            public bool Success { get; init; }
            public bool NeedsAdminPrivilege { get; init; }
            public string ErrorMessage { get; init; } = string.Empty;
            public WeChat4AccountDir? Account { get; init; }
            public int FriendsCount { get; init; }
            public int GroupsCount { get; init; }
            public int GroupsWithMessagesCount { get; init; }
            public int TagsCount { get; init; }
            public int DecryptedDbCount { get; init; }
        }

        public async Task<LoadSnapshotResult> EnsureSnapshotReadyAsync(
            bool forceReload = false,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            await _syncGate.WaitAsync(ct);
            try
            {
                if (!forceReload && _activeReader is not null && _activeAccount is not null)
                {
                    return new LoadSnapshotResult
                    {
                        Success = true,
                        Account = _activeAccount,
                        FriendsCount = _activeReader.Friends.Count,
                        GroupsCount = _activeReader.Groups.Count,
                        GroupsWithMessagesCount = _activeReader.Groups.Count(g => g.HasMessages),
                        TagsCount = _activeReader.LabelNames.Count
                    };
                }

                var account = ResolveTargetAccount(AppServices.Settings.Current.WeChatSyncAccountDir);
                if (account is null)
                {
                    return new LoadSnapshotResult
                    {
                        Success = false,
                        ErrorMessage = "未检测到本机微信数据目录，请确认电脑版微信已登录并正常运行。"
                    };
                }

                return await LoadSnapshotUnlockedAsync(account, null, onProgress, ct);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        public async Task<LoadSnapshotResult> LoadWeChatSnapshotAsync(
            WeChat4AccountDir account,
            string? manualPassphraseHex = null,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            await _syncGate.WaitAsync(ct);
            try
            {
                return await LoadSnapshotUnlockedAsync(account, manualPassphraseHex, onProgress, ct);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private Task<LoadSnapshotResult> LoadSnapshotUnlockedAsync(
            WeChat4AccountDir account,
            string? manualPassphraseHex,
            Action<WeChat4SyncProgress>? onProgress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "key",
                        Message = $"正在连接微信账号 {account.Wxid}...",
                        Percent = 12
                    });

                    var extractor = new WeChat4KeyExtractor(
                        account.DbStoragePath,
                        msg => onProgress?.Invoke(new WeChat4SyncProgress { Stage = "key", Message = msg, Percent = 25 }));

                    var keys = extractor.Extract(manualPassphraseHex, useCache: true, ct: ct);
                    if (keys.Count == 0)
                    {
                        LastSyncNeedsAdmin = extractor.EncounteredAccessDenied;
                        string reason = extractor.EncounteredAccessDenied
                            ? "读取运行中的微信进程权限不足，请点击「以管理员身份重启」后再试。"
                            : "未能自动连接运行中的电脑微信。请确认电脑版微信已登录，必要时请以管理员身份运行本程序。";
                        return new LoadSnapshotResult
                        {
                            Success = false,
                            NeedsAdminPrivilege = extractor.EncounteredAccessDenied,
                            ErrorMessage = reason
                        };
                    }

                    LastSyncNeedsAdmin = false;
                    string cacheDir = WeChat4Decryptor.GetPersistentCacheDir(account.AccountPath);

                    // Fast path: if zero DB files changed AND we already have an active reader on this cache dir, reuse immediately!
                    if (_activeReader is not null &&
                        string.Equals(_activeDecryptedDir, cacheDir, StringComparison.OrdinalIgnoreCase) &&
                        !WeChat4Decryptor.HasAnyDatabaseChanged(account.DbStoragePath, keys, cacheDir))
                    {
                        onProgress?.Invoke(new WeChat4SyncProgress
                        {
                            Stage = "ready",
                            Message = $"本地数据无变化，秒速复用缓存（{_activeReader.Friends.Count} 位好友 · {_activeReader.Groups.Count} 个群聊）。",
                            Percent = 100
                        });
                        return new LoadSnapshotResult
                        {
                            Success = true,
                            Account = account,
                            FriendsCount = _activeReader.Friends.Count,
                            GroupsCount = _activeReader.Groups.Count,
                            GroupsWithMessagesCount = _activeReader.Groups.Count(g => g.HasMessages),
                            TagsCount = _activeReader.LabelNames.Count,
                            DecryptedDbCount = keys.Count
                        };
                    }

                    // Release SQLite file handles on the cache dir before incremental replacement
                    if (_activeReader is not null &&
                        string.Equals(_activeDecryptedDir, cacheDir, StringComparison.OrdinalIgnoreCase))
                    {
                        _activeReader.Dispose();
                        _activeReader = null;
                    }

                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "decrypt",
                        Message = $"正在增量读取最新微信数据（共 {keys.Count} 个数据分片）...",
                        Percent = 48
                    });

                    var decResult = WeChat4Decryptor.DecryptIncremental(
                        account.DbStoragePath,
                        keys,
                        cacheDir,
                        msg => onProgress?.Invoke(new WeChat4SyncProgress { Stage = "decrypt", Message = msg, Percent = 68 }),
                        retries: 3,
                        ct: ct);

                    if (decResult.TotalAvailable == 0)
                    {
                        return new LoadSnapshotResult
                        {
                            Success = false,
                            ErrorMessage = "读取微信数据失败，请稍后重试或重新登录微信后再试。"
                        };
                    }

                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "parse",
                        Message = $"正在整理好友标签、群聊与消息索引（更新 {decResult.Succeeded.Count} / 复用 {decResult.Reused.Count}）...",
                        Percent = 85
                    });

                    var reader = new WeChat4Reader(
                        cacheDir,
                        account.Wxid,
                        account.AccountPath,
                        msg => onProgress?.Invoke(new WeChat4SyncProgress { Stage = "parse", Message = msg, Percent = 92 }));

                    ReplaceActiveSnapshot(reader, cacheDir, account);

                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "ready",
                        Message = $"已就绪：{reader.Friends.Count} 位好友 · {reader.LabelNames.Count} 个标签 · {reader.Groups.Count} 个群聊。",
                        Percent = 100
                    });

                    return new LoadSnapshotResult
                    {
                        Success = true,
                        Account = account,
                        FriendsCount = reader.Friends.Count,
                        GroupsCount = reader.Groups.Count,
                        GroupsWithMessagesCount = reader.Groups.Count(g => g.HasMessages),
                        TagsCount = reader.LabelNames.Count,
                        DecryptedDbCount = decResult.TotalAvailable
                    };
                }
                catch (OperationCanceledException)
                {
                    return new LoadSnapshotResult { Success = false, ErrorMessage = "已取消读取。" };
                }
                catch (Exception ex)
                {
                    return new LoadSnapshotResult { Success = false, ErrorMessage = ex.Message };
                }
            }, ct);
        }

        /// <summary>
        /// Imports user-selected WeChat friends (plus their chat history &amp; avatars) from the active snapshot.
        /// </summary>
        public async Task<WeChat4SyncReport> ImportSelectedFriendsAsync(
            ISet<string> selectedUsernames,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            var cfg = AppServices.Settings.Current;
            var opts = new WeChat4SyncOptions
            {
                ImportCustomers = true,
                ImportCustomerMessages = true,
                ImportGroups = false,
                ImportGroupMessages = false,
                SyncChatImages = cfg.WeChatSyncIncludeImages,
                SyncLocalAvatars = cfg.WeChatSyncIncludeAvatars,
                SkipSystemMessages = true,
                MaxMessagesPerSession = cfg.WeChatSyncMaxMessagesPerSession,
                SelectedCustomerUsernames = new HashSet<string>(selectedUsernames, StringComparer.OrdinalIgnoreCase)
            };

            var report = await SyncToBackendAsync(opts, "导入所选微信好友", isBackgroundAuto: false, onProgress, ct);
            RecordActivity($"已导入所选好友：新增 {report.CustomersCreated} 位 / 更新 {report.CustomersUpdated} 位，聊天消息 +{report.CustomerMessagesInserted} 条，图片 {report.ChatImagesUploaded} 张。");
            _ = RefreshMonitoredCountsAsync();
            StartAutoSyncIfConfigured();
            return report;
        }

        /// <summary>
        /// Imports user-selected WeChat groups (plus group avatars, members &amp; chat history) from the active snapshot.
        /// </summary>
        public async Task<WeChat4SyncReport> ImportSelectedGroupsAsync(
            ISet<string> selectedGroupUsernames,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            var cfg = AppServices.Settings.Current;
            var opts = new WeChat4SyncOptions
            {
                ImportCustomers = false,
                ImportCustomerMessages = false,
                ImportGroups = true,
                ImportGroupMessages = true,
                SyncChatImages = cfg.WeChatSyncIncludeImages,
                SyncLocalAvatars = cfg.WeChatSyncIncludeAvatars,
                SkipSystemMessages = true,
                MaxMessagesPerSession = cfg.WeChatSyncMaxMessagesPerSession,
                SelectedGroupUsernames = new HashSet<string>(selectedGroupUsernames, StringComparer.OrdinalIgnoreCase)
            };

            var report = await SyncToBackendAsync(opts, "导入所选微信群聊", isBackgroundAuto: false, onProgress, ct);
            RecordActivity($"已导入所选群聊：新增 {report.GroupsCreated} 个 / 更新 {report.GroupsUpdated} 个，群成员 {report.GroupMembersSynced} 人，群消息 +{report.GroupMessagesInserted} 条，头像 {report.AvatarsUploaded} 个。");
            _ = RefreshMonitoredCountsAsync();
            StartAutoSyncIfConfigured();
            return report;
        }

        public async Task<WeChat4SyncReport> SyncToBackendAsync(
            WeChat4SyncOptions options,
            string scopeLabel = "同步",
            bool isBackgroundAuto = false,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            await _syncGate.WaitAsync(ct);
            try
            {
                return await SyncToBackendUnlockedAsync(options, scopeLabel, isBackgroundAuto, onProgress, ct);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        /// <summary>
        /// Targeted incremental sync: ONLY syncs the specific customers and/or groups that the user has ALREADY
        /// imported into the workbench! Never blindly imports unrequested contacts or groups.
        /// </summary>
        public async Task<(bool Success, string Message, WeChat4SyncReport? Report)> SyncImportedTargetsAsync(
            bool syncCustomers = true,
            bool syncGroups = true,
            bool isBackgroundAuto = false,
            Action<WeChat4SyncProgress>? onProgress = null,
            CancellationToken ct = default)
        {
            if (!AppServices.Backend.IsAuthenticated)
            {
                return (false, "尚未登录工作台账号，跳过同步。", null);
            }

            if (!await _syncGate.WaitAsync(TimeSpan.FromSeconds(1), ct))
            {
                return (false, "已有同步任务正在执行中。", null);
            }

            try
            {
                var importedCustomers = syncCustomers
                    ? await AppServices.Backend.ListAllCustomersAsync(2000, ct)
                    : [];
                var importedGroups = syncGroups
                    ? await AppServices.Backend.ListAllGroupsAsync(1000, ct)
                    : [];

                if (syncCustomers) MonitoredCustomersCount = importedCustomers.Count;
                if (syncGroups)
                {
                    MonitoredGroupsCount = importedGroups.Count;
                    AppServices.GroupAutoReply.SyncGroupsSnapshot(importedGroups);
                }

                if (importedCustomers.Count == 0 && importedGroups.Count == 0)
                {
                    string emptyMsg = (syncCustomers, syncGroups) switch
                    {
                        (true, false) => "当前还没有已导入的客户，请点击左上角「+」选择要导入的微信好友。",
                        (false, true) => "当前还没有已导入的群聊，请点击左上角「+」选择要导入的微信群。",
                        _ => "当前还没有已导入的客户或群聊，导入后将自动监听消息变化。"
                    };
                    return (true, emptyMsg, null);
                }

                var settings = AppServices.Settings.Current;
                var account = ResolveTargetAccount(settings.WeChatSyncAccountDir);
                if (account is null)
                {
                    return (false, "未检测到本机微信数据目录。", null);
                }

                var loaded = await LoadSnapshotUnlockedAsync(account, null, onProgress, ct);
                if (!loaded.Success || _activeReader is null)
                {
                    return (false, loaded.ErrorMessage, null);
                }

                // Always keep current WeChat self-profile (nickname, alias, avatar, online status) synced
                await SyncSelfWeChatAccountUnlockedAsync(_activeReader, ct);

                string currentWxid = _activeReader.SelfWxid;

                // Match ONLY already-imported customers belonging to THIS WeChat account (or unassigned legacy rows)
                var scopedCustomers = importedCustomers
                    .Where(c => string.IsNullOrWhiteSpace(c.OwnerWxid) ||
                                string.Equals(c.OwnerWxid, currentWxid, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var targetCustomerWxids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (syncCustomers && scopedCustomers.Count > 0)
                {
                    var nativeIds = scopedCustomers
                        .Select(c => c.NativeId?.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var accounts = scopedCustomers
                        .Select(c => c.WechatAccount?.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var f in _activeReader.Friends)
                    {
                        if (nativeIds.Contains(f.Username) ||
                            (!string.IsNullOrWhiteSpace(f.Alias) && accounts.Contains(f.Alias)))
                        {
                            targetCustomerWxids.Add(f.Username);
                        }
                    }
                }

                // Match ONLY already-imported groups belonging to THIS WeChat account (or unassigned legacy rows)
                var scopedGroups = importedGroups
                    .Where(g => string.IsNullOrWhiteSpace(g.OwnerWxid) ||
                                string.Equals(g.OwnerWxid, currentWxid, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var targetGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (syncGroups && scopedGroups.Count > 0)
                {
                    var groupKeys = scopedGroups
                        .Select(g => g.GroupKey?.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var groupNames = scopedGroups
                        .Where(g => string.IsNullOrWhiteSpace(g.GroupKey))
                        .Select(g => g.GroupName?.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var g in _activeReader.Groups)
                    {
                        if (groupKeys.Contains(g.Username) ||
                            (groupNames.Count > 0 && groupNames.Contains(g.GroupName)))
                        {
                            targetGroupKeys.Add(g.Username);
                        }
                    }
                }

                if (targetCustomerWxids.Count == 0 && targetGroupKeys.Count == 0)
                {
                    await RefreshKnownAccountsInternalAsync(ct);
                    return (true, $"当前微信（{ResolveAccountDisplayName(currentWxid)}）暂无已导入的匹配客户或群聊，可点左上角「+」导入。", null);
                }

                var opts = new WeChat4SyncOptions
                {
                    ImportCustomers = targetCustomerWxids.Count > 0,
                    ImportCustomerMessages = targetCustomerWxids.Count > 0,
                    ImportGroups = targetGroupKeys.Count > 0,
                    ImportGroupMessages = targetGroupKeys.Count > 0,
                    SyncChatImages = settings.WeChatSyncIncludeImages,
                    SyncLocalAvatars = settings.WeChatSyncIncludeAvatars,
                    SkipSystemMessages = true,
                    MaxMessagesPerSession = settings.WeChatSyncMaxMessagesPerSession,
                    SelectedCustomerUsernames = targetCustomerWxids,
                    SelectedGroupUsernames = targetGroupKeys
                };

                string scope = (syncCustomers, syncGroups) switch
                {
                    (true, true) => isBackgroundAuto ? "自动监听同步" : "同步已导入客户与群",
                    (true, false) => "同步已导入客户",
                    (false, true) => "同步已导入群聊",
                    _ => "同步"
                };

                var report = await SyncToBackendUnlockedAsync(opts, scope, isBackgroundAuto, onProgress, ct);
                int totalNewMsgs = report.CustomerMessagesInserted + report.GroupMessagesInserted;
                string summary = totalNewMsgs > 0
                    ? $"{scope}完成：新增私聊 {report.CustomerMessagesInserted} 条、群聊 {report.GroupMessagesInserted} 条，图片 {report.ChatImagesUploaded} 张。"
                    : $"{scope}完成：已核对 {targetCustomerWxids.Count} 位客户、{targetGroupKeys.Count} 个群聊，暂无新消息。";

                if (!isBackgroundAuto || totalNewMsgs > 0 || report.AvatarsUploaded > 0)
                {
                    RecordActivity(summary);
                }
                return (true, summary, report);
            }
            catch (OperationCanceledException)
            {
                return (false, "同步已取消。", null);
            }
            catch (Exception ex)
            {
                RecordActivity($"同步失败：{ex.Message}");
                return (false, $"同步失败：{ex.Message}", null);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private async Task<WeChat4SyncReport> SyncToBackendUnlockedAsync(
            WeChat4SyncOptions options,
            string scopeLabel,
            bool isBackgroundAuto,
            Action<WeChat4SyncProgress>? onProgress,
            CancellationToken ct)
        {
            var reader = _activeReader ?? throw new InvalidOperationException("请先连接微信数据。");
            var sw = Stopwatch.StartNew();
            var report = new WeChat4SyncReport
            {
                ScopeLabel = scopeLabel,
                IsBackgroundAuto = isBackgroundAuto
            };

            await SyncSelfWeChatAccountUnlockedAsync(reader, ct);
            string ownerWxid = reader.SelfWxid ?? string.Empty;

            // 1. Sync selected customers
            var customerIdByWxid = new Dictionary<string, (long Id, string AvatarUrl)>(StringComparer.OrdinalIgnoreCase);
            var selectedFriends = options.ImportCustomers
                ? reader.Friends.Where(f => options.SelectedCustomerUsernames is null || options.SelectedCustomerUsernames.Contains(f.Username)).ToList()
                : [];

            if (selectedFriends.Count > 0)
            {
                const int batchSize = 200;
                for (int i = 0; i < selectedFriends.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = selectedFriends.Skip(i).Take(batchSize).ToList();
                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "customers",
                        Message = $"同步客户资料 ({Math.Min(i + chunk.Count, selectedFriends.Count)}/{selectedFriends.Count})...",
                        Percent = 10 + (int)(18.0 * (i + chunk.Count) / selectedFriends.Count)
                    });

                    var payload = chunk.Select(f => new WeChatSyncCustomerPayload
                    {
                        OwnerWxid = ownerWxid,
                        NativeId = f.Username,
                        LocalKey = f.Username,
                        WechatRemark = f.Remark,
                        WechatName = f.NickName,
                        WechatAccount = f.Alias,
                        Gender = f.Gender,
                        Description = f.Description,
                        Tags = f.Tags,
                        Region = f.Region,
                        Phone = f.Phone,
                        Signature = f.Signature,
                        Source = string.Empty
                    }).ToList();

                    var res = await AppServices.Backend.SyncWeChatCustomersAsync(payload, ownerWxid, ct);
                    if (!res.IsSuccess || res.Data is null)
                    {
                        throw new InvalidOperationException($"同步客户失败：{res.ErrorMessage}");
                    }

                    report.CustomersCreated += res.Data.Created;
                    report.CustomersUpdated += res.Data.Updated;
                    foreach (var item in res.Data.Items)
                    {
                        customerIdByWxid[item.NativeId] = (item.Id, item.AvatarUrl);
                    }
                }
            }

            // 2. Sync selected groups & members
            var groupIdByKey = new Dictionary<string, (long Id, string AvatarUrl, List<WeChatSyncGroupMemberResultDto> Members)>(StringComparer.OrdinalIgnoreCase);
            var selectedGroups = options.ImportGroups
                ? reader.Groups.Where(g => options.SelectedGroupUsernames is null || options.SelectedGroupUsernames.Contains(g.Username)).ToList()
                : [];

            if (selectedGroups.Count > 0)
            {
                const int batchSize = 30;
                for (int i = 0; i < selectedGroups.Count; i += batchSize)
                {
                    ct.ThrowIfCancellationRequested();
                    var chunk = selectedGroups.Skip(i).Take(batchSize).ToList();
                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "groups",
                        Message = $"同步群聊与群成员 ({Math.Min(i + chunk.Count, selectedGroups.Count)}/{selectedGroups.Count})...",
                        Percent = 28 + (int)(18.0 * (i + chunk.Count) / selectedGroups.Count)
                    });

                    var payload = chunk.Select(g => new WeChatSyncGroupPayload
                    {
                        OwnerWxid = ownerWxid,
                        GroupKey = g.Username,
                        GroupName = g.GroupName,
                        MyNickname = g.MyNickname,
                        MemberCount = g.MemberCount,
                        MembersText = string.Join("、", g.Members.Take(60).Select(m => m.DisplayName).Where(n => !string.IsNullOrWhiteSpace(n))),
                        Members = g.Members.Select(m => new WeChatSyncGroupMemberPayload
                        {
                            NativeId = m.Username,
                            LocalKey = m.Username,
                            GroupNickname = m.GroupNickname,
                            WechatName = m.NickName,
                            WechatRemark = m.Remark,
                            WechatAccount = m.Alias,
                            Gender = m.Gender,
                            Region = m.Region,
                            Signature = m.Signature,
                            Role = m.Role
                        }).ToList()
                    }).ToList();

                    var res = await AppServices.Backend.SyncWeChatGroupsAsync(payload, ownerWxid, ct);
                    if (!res.IsSuccess || res.Data is null)
                    {
                        throw new InvalidOperationException($"同步群聊失败：{res.ErrorMessage}");
                    }

                    report.GroupsCreated += res.Data.Created;
                    report.GroupsUpdated += res.Data.Updated;
                    foreach (var item in res.Data.Items)
                    {
                        groupIdByKey[item.GroupKey] = (item.Id, item.AvatarUrl ?? string.Empty, item.Members);
                        report.GroupMembersSynced += item.Members.Count;
                    }
                }
            }

            // 3. Sync customer chat messages (skip unchanged sessions during background auto-sync via SessionTable timestamp)
            if (options.ImportCustomerMessages && customerIdByWxid.Count > 0)
            {
                var friendsWithMsgs = selectedFriends
                    .Where(f => (f.HasMessages || selectedFriends.Count <= 25) && customerIdByWxid.ContainsKey(f.Username))
                    .Where(f => !isBackgroundAuto ||
                                f.LastMessageTime <= 0 ||
                                !_lastSyncedSessionTime.TryGetValue($"{ownerWxid}|{f.Username}", out long prevTs) ||
                                f.LastMessageTime > prevTs)
                    .ToList();
                for (int i = 0; i < friendsWithMsgs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = friendsWithMsgs[i];
                    var (custId, _) = customerIdByWxid[f.Username];

                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "customer_messages",
                        Message = $"同步私聊记录：{f.DisplayName} ({i + 1}/{friendsWithMsgs.Count})...",
                        Percent = 46 + (int)(20.0 * (i + 1) / Math.Max(1, friendsWithMsgs.Count))
                    });

                    var msgs = await Task.Run(
                        () => reader.ReadMessages(f.Username, options.MaxMessagesPerSession, options.SkipSystemMessages, ct),
                        ct);

                    await UploadAndSyncMessagesBatchAsync(
                        reader, f.Username, msgs, isGroup: false, targetId: custId,
                        syncImages: options.SyncChatImages, report: report, ct: ct);

                    if (f.LastMessageTime > 0)
                    {
                        _lastSyncedSessionTime[$"{ownerWxid}|{f.Username}"] = f.LastMessageTime;
                    }
                }
            }

            // 4. Sync group chat messages (skip unchanged sessions during background auto-sync via SessionTable timestamp)
            if (options.ImportGroupMessages && groupIdByKey.Count > 0)
            {
                var groupsWithMsgs = selectedGroups
                    .Where(g => (g.HasMessages || selectedGroups.Count <= 25) && groupIdByKey.ContainsKey(g.Username))
                    .Where(g => !isBackgroundAuto ||
                                g.LastMessageTime <= 0 ||
                                !_lastSyncedSessionTime.TryGetValue($"{ownerWxid}|{g.Username}", out long prevTs) ||
                                g.LastMessageTime > prevTs)
                    .ToList();
                for (int i = 0; i < groupsWithMsgs.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var g = groupsWithMsgs[i];
                    var (groupId, _, _) = groupIdByKey[g.Username];

                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "group_messages",
                        Message = $"同步群聊记录：{g.GroupName} ({i + 1}/{groupsWithMsgs.Count})...",
                        Percent = 66 + (int)(18.0 * (i + 1) / Math.Max(1, groupsWithMsgs.Count))
                    });

                    var msgs = await Task.Run(
                        () => reader.ReadMessages(g.Username, options.MaxMessagesPerSession, options.SkipSystemMessages, ct),
                        ct);

                    var newlyUploaded = await UploadAndSyncMessagesBatchAsync(
                        reader, g.Username, msgs, isGroup: true, targetId: groupId,
                        syncImages: options.SyncChatImages, report: report, ct: ct);

                    if (isBackgroundAuto && newlyUploaded.Count > 0)
                    {
                        try
                        {
                            GroupNewMessagesSynced?.Invoke(new WeChat4GroupNewMessagesEvent
                            {
                                GroupId = groupId,
                                OwnerWxid = ownerWxid,
                                GroupKey = g.Username,
                                GroupName = g.GroupName,
                                MyNickname = g.MyNickname,
                                SelfNickname = reader.SelfContact?.NickName ?? string.Empty,
                                SelfAlias = reader.SelfContact?.Alias ?? string.Empty,
                                IsBackgroundAuto = isBackgroundAuto,
                                NewMessages = newlyUploaded
                            });
                        }
                        catch (Exception ex)
                        {
                            AppServices.Logs.Warn("REPLY", $"分发群新消息自动回复事件异常：{ex.Message}");
                        }
                    }

                    if (g.LastMessageTime > 0)
                    {
                        _lastSyncedSessionTime[$"{ownerWxid}|{g.Username}"] = g.LastMessageTime;
                    }
                }
            }

            // 5. Upload missing avatars for customers, groups, and group members
            if (options.SyncLocalAvatars)
            {
                var missingCustomers = customerIdByWxid
                    .Where(kv => string.IsNullOrWhiteSpace(kv.Value.AvatarUrl))
                    .ToList();

                for (int i = 0; i < missingCustomers.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (wxid, (custId, _)) = missingCustomers[i];
                    if (i % 8 == 0)
                    {
                        onProgress?.Invoke(new WeChat4SyncProgress
                        {
                            Stage = "avatars",
                            Message = $"同步客户头像 ({i + 1}/{missingCustomers.Count})...",
                            Percent = 84 + (int)(6.0 * (i + 1) / Math.Max(1, missingCustomers.Count))
                        });
                    }
                    byte[]? bytes = await reader.TryGetAvatarBytesAsync(wxid, ct);
                    if (bytes is { Length: > 0 })
                    {
                        var up = await AppServices.Backend.UploadCustomerAvatarAsync(custId, bytes, ct);
                        if (up.IsSuccess) report.AvatarsUploaded++;
                    }
                }

                // Group avatars (direct head_image.db / head URL first, then 9-grid member collage fallback!)
                var missingGroups = groupIdByKey
                    .Where(kv => string.IsNullOrWhiteSpace(kv.Value.AvatarUrl))
                    .ToList();

                for (int i = 0; i < missingGroups.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (groupKey, (groupId, _, _)) = missingGroups[i];
                    onProgress?.Invoke(new WeChat4SyncProgress
                    {
                        Stage = "avatars",
                        Message = $"同步群头像 ({i + 1}/{missingGroups.Count})...",
                        Percent = 90 + (int)(4.0 * (i + 1) / Math.Max(1, missingGroups.Count))
                    });
                    byte[]? bytes = await reader.TryGetGroupAvatarOrCollageBytesAsync(groupKey, ct);
                    if (bytes is { Length: > 0 })
                    {
                        var up = await AppServices.Backend.UploadGroupAvatarAsync(groupId, bytes, ct);
                        if (up.IsSuccess)
                        {
                            report.AvatarsUploaded++;
                        }
                        else
                        {
                            AppServices.Logs.Warn("WX4", $"上传群头像失败 (groupId={groupId})：{up.ErrorMessage}");
                        }
                    }
                }

                // Group member avatars (budget-capped per run)
                int memberAvatarBudget = 80;
                foreach (var (_, (groupId, _, members)) in groupIdByKey)
                {
                    if (memberAvatarBudget <= 0) break;
                    foreach (var m in members)
                    {
                        if (memberAvatarBudget <= 0) break;
                        if (!string.IsNullOrWhiteSpace(m.AvatarURLSafe())) continue;
                        ct.ThrowIfCancellationRequested();
                        byte[]? bytes = await reader.TryGetAvatarBytesAsync(m.NativeId, ct);
                        if (bytes is { Length: > 0 })
                        {
                            var up = await AppServices.Backend.UploadGroupMemberAvatarAsync(groupId, m.Id, bytes, ct);
                            if (up.IsSuccess)
                            {
                                report.AvatarsUploaded++;
                                memberAvatarBudget--;
                            }
                        }
                    }
                }
            }

            sw.Stop();
            report.Elapsed = sw.Elapsed;
            report.FinishedAt = DateTimeOffset.Now;
            LastSyncAt = report.FinishedAt;
            LastReport = report;
            if (report.CustomersCreated > 0 || report.CustomersUpdated > 0 ||
                report.GroupsCreated > 0 || report.GroupsUpdated > 0 ||
                report.CustomerMessagesInserted > 0 || report.GroupMessagesInserted > 0 ||
                report.AvatarsUploaded > 0)
            {
                Interlocked.Increment(ref _syncRevision);
            }

            onProgress?.Invoke(new WeChat4SyncProgress
            {
                Stage = "done",
                Message = $"{scopeLabel}完成（{sw.Elapsed.TotalSeconds:F1}s）",
                Percent = 100
            });

            await RefreshKnownAccountsInternalAsync(ct);
            SyncCompleted?.Invoke(report);
            return report;
        }

        /// <summary>
        /// Explicitly detects the currently running/most-recently-active local WeChat account,
        /// extracts its self profile (nickname, alias, wxid, region, signature, avatar),
        /// upserts it to backend as online, and refreshes all linked WeChat accounts of this employee.
        /// </summary>
        public async Task<(bool Ok, string Message, WeChatAccountDto? ActiveAccount)> DetectAndSyncCurrentWeChatAccountAsync(
            bool forceReload = true,
            CancellationToken ct = default)
        {
            if (!AppServices.Backend.IsAuthenticated)
            {
                return (false, "请先登录员工账号。", null);
            }

            await _syncGate.WaitAsync(ct);
            try
            {
                var account = ResolveTargetAccount(AppServices.Settings.Current.WeChatSyncAccountDir, preferMostRecentLocal: true);
                if (account is null)
                {
                    await RefreshKnownAccountsInternalAsync(ct);
                    return (false, "未检测到本机微信数据目录，请确认电脑已登录微信。", CurrentOnlineAccount);
                }

                bool switched = _activeAccount is not null &&
                                !string.Equals(_activeAccount.AccountPath, account.AccountPath, StringComparison.OrdinalIgnoreCase);

                var loaded = await LoadSnapshotUnlockedAsync(account, null, null, ct);
                if (!loaded.Success || _activeReader is null)
                {
                    await RefreshKnownAccountsInternalAsync(ct);
                    return (false, loaded.ErrorMessage, CurrentOnlineAccount);
                }

                var synced = await SyncSelfWeChatAccountUnlockedAsync(_activeReader, ct);
                await RefreshKnownAccountsInternalAsync(ct);

                if (switched && AppServices.Settings.Current.WeChatAutoSyncEnabled)
                {
                    RestartFileWatcher();
                }

                string label = synced?.DisplayName ?? _activeReader.SelfContact?.DisplayName ?? _activeReader.SelfWxid;
                RecordActivity(switched
                    ? $"检测到微信切换登录账号，已自动同步当前账号：{label}（{_activeReader.SelfWxid}）"
                    : $"已识别当前登录微信：{label}（{_activeReader.SelfWxid}）");
                return (true, $"已识别当前登录微信：{label}", synced ?? CurrentOnlineAccount);
            }
            catch (OperationCanceledException)
            {
                return (false, "识别已取消。", CurrentOnlineAccount);
            }
            catch (Exception ex)
            {
                return (false, $"识别当前微信账号失败：{ex.Message}", CurrentOnlineAccount);
            }
            finally
            {
                _syncGate.Release();
            }
        }

        private async Task<WeChatAccountDto?> SyncSelfWeChatAccountUnlockedAsync(WeChat4Reader reader, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(reader.SelfWxid) || !AppServices.Backend.IsAuthenticated)
            {
                return null;
            }

            try
            {
                var me = reader.SelfContact;
                string nick = me?.NickName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(nick)) nick = me?.DisplayName ?? reader.SelfWxid;
                string alias = me?.Alias ?? string.Empty;
                string region = me?.Region ?? string.Empty;
                string signature = me?.Signature ?? string.Empty;

                var upRes = await AppServices.Backend.UpsertWeChatAccountAsync(
                    accountType: "personal",
                    wechatId: reader.SelfWxid,
                    nickname: nick,
                    status: "online",
                    alias: alias,
                    region: region,
                    signature: signature,
                    ct: ct);

                if (!upRes.IsSuccess || upRes.Data is null) return null;
                var acc = upRes.Data;

                // Upload self avatar when missing or on first detection in this app session
                if (string.IsNullOrWhiteSpace(acc.AvatarUrl) || !_uploadedSelfAvatarWxids.Contains(reader.SelfWxid))
                {
                    byte[]? avatarBytes = await reader.TryGetAvatarBytesAsync(reader.SelfWxid, ct);
                    if (avatarBytes is { Length: > 0 })
                    {
                        var avRes = await AppServices.Backend.UploadWeChatAccountAvatarAsync(acc.Id, avatarBytes, ct);
                        if (avRes.IsSuccess && avRes.Data is not null)
                        {
                            acc = avRes.Data;
                            _uploadedSelfAvatarWxids.Add(reader.SelfWxid);
                        }
                    }
                }
                return acc;
            }
            catch (Exception ex)
            {
                AppServices.Logs.Warn("WX4", $"同步本人微信资料失败：{ex.Message}");
                return null;
            }
        }

        private async Task RefreshKnownAccountsInternalAsync(CancellationToken ct = default)
        {
            if (!AppServices.Backend.IsAuthenticated) return;
            try
            {
                var list = await AppServices.Backend.ListWeChatAccountsAsync(ct);
                _knownAccounts = list;
                AccountsUpdated?.Invoke();
            }
            catch { }
        }

        public async Task<(bool Ok, string Message)> SyncSingleCustomerMessagesAsync(CustomerDto target, int maxCount = 500, CancellationToken ct = default)
        {
            var ensured = await EnsureSnapshotReadyAsync(forceReload: true, ct: ct);
            if (!ensured.Success || _activeReader is null)
            {
                return (false, ensured.ErrorMessage);
            }

            var reader = _activeReader;
            var matched = reader.Friends.FirstOrDefault(f =>
                (!string.IsNullOrWhiteSpace(target.NativeId) && f.Username.Equals(target.NativeId, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.WechatAccount) && f.Alias.Equals(target.WechatAccount, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.WechatRemark) && f.Remark.Equals(target.WechatRemark, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(target.WechatName) && f.NickName.Equals(target.WechatName, StringComparison.OrdinalIgnoreCase)));

            if (matched is null)
            {
                return (false, $"在当前微信账号中未找到好友「{target.DisplayName}」。");
            }

            var msgs = await Task.Run(() => reader.ReadMessages(matched.Username, maxCount, skipSystem: true, ct), ct);
            if (msgs.Count == 0)
            {
                return (true, $"微信本地暂无「{target.DisplayName}」的新消息。");
            }

            var report = new WeChat4SyncReport();
            await UploadAndSyncMessagesBatchAsync(
                reader, matched.Username, msgs, isGroup: false, targetId: target.Id,
                syncImages: AppServices.Settings.Current.WeChatSyncIncludeImages, report: report, ct: ct);

            return (true, $"✓ 已同步「{target.DisplayName}」：新增 {report.CustomerMessagesInserted} 条消息，上传图片 {report.ChatImagesUploaded} 张。");
        }

        public async Task<(bool Ok, string Message)> SyncSingleGroupMessagesAsync(GroupDto target, int maxCount = 500, CancellationToken ct = default)
        {
            var ensured = await EnsureSnapshotReadyAsync(forceReload: true, ct: ct);
            if (!ensured.Success || _activeReader is null)
            {
                return (false, ensured.ErrorMessage);
            }

            var reader = _activeReader;
            var matched = reader.Groups.FirstOrDefault(g =>
                (!string.IsNullOrWhiteSpace(target.GroupKey) && g.Username.Equals(target.GroupKey, StringComparison.OrdinalIgnoreCase)) ||
                g.GroupName.Equals(target.GroupName, StringComparison.OrdinalIgnoreCase));

            if (matched is null)
            {
                return (false, $"在当前微信账号中未找到群聊「{target.DisplayName}」。");
            }

            var msgs = await Task.Run(() => reader.ReadMessages(matched.Username, maxCount, skipSystem: true, ct), ct);
            if (msgs.Count == 0)
            {
                return (true, $"微信本地暂无群聊「{target.DisplayName}」的新消息。");
            }

            var report = new WeChat4SyncReport();
            await UploadAndSyncMessagesBatchAsync(
                reader, matched.Username, msgs, isGroup: true, targetId: target.Id,
                syncImages: AppServices.Settings.Current.WeChatSyncIncludeImages, report: report, ct: ct);

            // Also upload group avatar if missing
            if (string.IsNullOrWhiteSpace(target.AvatarUrl))
            {
                byte[]? bytes = await reader.TryGetGroupAvatarOrCollageBytesAsync(matched.Username, ct);
                if (bytes is { Length: > 0 })
                {
                    var up = await AppServices.Backend.UploadGroupAvatarAsync(target.Id, bytes, ct);
                    if (!up.IsSuccess)
                    {
                        AppServices.Logs.Warn("WX4", $"上传群头像失败 (groupId={target.Id})：{up.ErrorMessage}");
                    }
                }
            }

            return (true, $"✓ 已同步群聊「{target.DisplayName}」：新增 {report.GroupMessagesInserted} 条消息，上传图片 {report.ChatImagesUploaded} 张。");
        }

        private static async Task<List<WeChat4Message>> UploadAndSyncMessagesBatchAsync(
            WeChat4Reader reader,
            string sessionUsername,
            List<WeChat4Message> msgs,
            bool isGroup,
            long targetId,
            bool syncImages,
            WeChat4SyncReport report,
            CancellationToken ct)
        {
            if (msgs.Count == 0) return [];

            var existingIds = new HashSet<string>(StringComparer.Ordinal);
            var allExtIds = msgs.Select(m => m.ExternalId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            if (allExtIds.Count > 0)
            {
                string scope = isGroup ? "group" : "customer";
                var checkRes = await AppServices.Backend.CheckExistingMessageIdsAsync(scope, targetId, allExtIds, ct);
                if (checkRes.IsSuccess && checkRes.Data is not null)
                {
                    foreach (var id in checkRes.Data.ExistingIds) existingIds.Add(id);
                }
            }

            var newMsgs = existingIds.Count > 0
                ? msgs.Where(m => !existingIds.Contains(m.ExternalId)).ToList()
                : msgs;

            int preSkipped = msgs.Count - newMsgs.Count;
            if (isGroup) report.GroupMessagesSkipped += preSkipped;
            else report.CustomerMessagesSkipped += preSkipped;

            if (newMsgs.Count == 0) return [];

            var imageUrlByExtId = new Dictionary<string, string>(StringComparer.Ordinal);
            var audioUrlByExtId = new Dictionary<string, string>(StringComparer.Ordinal);

            if (syncImages)
            {
                int imageBudget = 80;
                for (int idx = newMsgs.Count - 1; idx >= 0 && imageBudget > 0; idx--)
                {
                    var m = newMsgs[idx];
                    if (m.Kind is not ("image" or "emoji") || m.MediaHints.Count == 0) continue;
                    ct.ThrowIfCancellationRequested();
                    byte[]? imgBytes = await reader.TryResolveMessageImageBytesAsync(sessionUsername, m, ct);
                    if (imgBytes is null) continue;

                    imgBytes = await ChatImageHelper.CompressForStorageAsync(imgBytes);
                    var upload = await AppServices.Backend.UploadChatImageAsync(imgBytes, ct);
                    if (upload.IsSuccess && !string.IsNullOrWhiteSpace(upload.Data?.ImageUrl))
                    {
                        imageUrlByExtId[m.ExternalId] = upload.Data.ImageUrl;
                        report.ChatImagesUploaded++;
                        imageBudget--;
                    }
                }

                // Videos: save ONLY the lightweight compressed first-frame cover thumbnail + video marker (never upload bulky MP4 files)
                int videoBudget = 40;
                for (int idx = newMsgs.Count - 1; idx >= 0 && videoBudget > 0; idx--)
                {
                    var m = newMsgs[idx];
                    if (m.Kind != "video" || m.MediaHints.Count == 0) continue;
                    ct.ThrowIfCancellationRequested();
                    var (coverBytes, _) = await Task.Run(() => reader.TryResolveVideoAssets(m), ct);
                    if (coverBytes is not { Length: > 0 }) continue;

                    coverBytes = await ChatImageHelper.CompressForStorageAsync(coverBytes, maxEdge: 720, jpegQuality: 0.70f);
                    var upCover = await AppServices.Backend.UploadChatImageAsync(coverBytes, ct);
                    if (upCover.IsSuccess && !string.IsNullOrWhiteSpace(upCover.Data?.ImageUrl))
                    {
                        imageUrlByExtId[m.ExternalId] = upCover.Data.ImageUrl;
                        report.ChatImagesUploaded++;
                        videoBudget--;
                    }
                }
            }

            // Decode & upload playable WAV audio for voice messages (capped at 60 per session per sync)
            int voiceBudget = 60;
            for (int idx = newMsgs.Count - 1; idx >= 0 && voiceBudget > 0; idx--)
            {
                var m = newMsgs[idx];
                if (m.Kind != "voice") continue;
                ct.ThrowIfCancellationRequested();
                byte[]? wavBytes = await Task.Run(() => reader.TryResolveVoiceWavBytes(sessionUsername, m), ct);
                if (wavBytes is null) continue;

                var upload = await AppServices.Backend.UploadChatAudioAsync(wavBytes, ct);
                if (upload.IsSuccess && !string.IsNullOrWhiteSpace(upload.Data?.AudioUrl))
                {
                    audioUrlByExtId[m.ExternalId] = upload.Data.AudioUrl;
                    voiceBudget--;
                }
            }

            const int msgBatch = 800;
            for (int m = 0; m < newMsgs.Count; m += msgBatch)
            {
                var slice = newMsgs.Skip(m).Take(msgBatch).Select(x =>
                {
                    imageUrlByExtId.TryGetValue(x.ExternalId, out string? imgUrl);
                    audioUrlByExtId.TryGetValue(x.ExternalId, out string? audUrl);
                    return new WeChatSyncMessagePayload
                    {
                        ExternalId = x.ExternalId,
                        SenderName = isGroup ? x.SenderName : null,
                        Direction = x.IsOutgoing ? "outgoing" : "incoming",
                        Content = string.IsNullOrWhiteSpace(imgUrl) ? x.Content : (x.Kind is "image" or "emoji" ? "" : x.Content),
                        ImageUrl = imgUrl ?? string.Empty,
                        AudioUrl = audUrl ?? string.Empty,
                        VideoUrl = string.Empty,
                        SentAt = x.SentAtUtc.ToString("O")
                    };
                }).ToList();

                var res = isGroup
                    ? await AppServices.Backend.SyncGroupMessagesAsync(targetId, slice, ct)
                    : await AppServices.Backend.SyncCustomerMessagesAsync(targetId, slice, ct);

                if (res.IsSuccess && res.Data is not null)
                {
                    if (isGroup)
                    {
                        report.GroupMessagesInserted += res.Data.Inserted;
                        report.GroupMessagesSkipped += res.Data.Skipped;
                    }
                    else
                    {
                        report.CustomerMessagesInserted += res.Data.Inserted;
                        report.CustomerMessagesSkipped += res.Data.Skipped;
                    }
                }
                else
                {
                    string kindLabel = isGroup ? "群聊消息" : "私聊消息";
                    string errMsg = $"同步{kindLabel}失败 (targetId={targetId})：{res.ErrorMessage}";
                    AppServices.Logs.Warn("WX4", errMsg);
                    throw new InvalidOperationException($"{errMsg}（若刚刚更新过服务端，请重启后端服务后重试）");
                }
            }
            return newMsgs;
        }

        // ---- File-Change-Driven Targeted Auto Watcher ----

        public void SetCustomAccountDir(string dirPath)
        {
            var found = WeChat4Paths.ResolveSelectedDirs(dirPath);
            if (found.Count > 0)
            {
                _activeAccount = found[0];
                AppServices.Settings.Current.WeChatSyncAccountDir = found[0].AccountPath;
                AppServices.Settings.Save();
                RecordActivity($"已切换微信数据目录：{found[0].Wxid}");
                if (AppServices.Settings.Current.WeChatAutoSyncEnabled)
                {
                    RestartFileWatcher();
                }
            }
        }

        public void ConfigureAutoWatch(bool enabled)
        {
            AppServices.Settings.Current.WeChatAutoSyncEnabled = enabled;
            AppServices.Settings.Save();

            if (enabled)
            {
                RestartFileWatcher();
            }
            else
            {
                StopAutoSyncTimer();
                UpdateWatchStatus("微信自动监听：已暂停");
                RecordActivity("已暂停微信文件变化自动监听。");
            }
        }

        public void StartAutoSyncIfConfigured()
        {
            if (AppServices.Settings.Current.WeChatAutoSyncEnabled && !IsFileWatcherRunning)
            {
                RestartFileWatcher();
            }
        }

        public async Task RefreshMonitoredCountsAsync()
        {
            if (!AppServices.Backend.IsAuthenticated) return;
            try
            {
                var cTask = AppServices.Backend.ListAllCustomersAsync(2000);
                var gTask = AppServices.Backend.ListAllGroupsAsync(1000);
                var aTask = AppServices.Backend.ListWeChatAccountsAsync();
                await Task.WhenAll(cTask, gTask, aTask);
                _knownAccounts = aTask.Result;
                string activeWxid = ActiveOwnerWxid;
                MonitoredCustomersCount = string.IsNullOrWhiteSpace(activeWxid)
                    ? cTask.Result.Count
                    : cTask.Result.Count(c => string.IsNullOrWhiteSpace(c.OwnerWxid) ||
                                              string.Equals(c.OwnerWxid, activeWxid, StringComparison.OrdinalIgnoreCase));
                MonitoredGroupsCount = string.IsNullOrWhiteSpace(activeWxid)
                    ? gTask.Result.Count
                    : gTask.Result.Count(g => string.IsNullOrWhiteSpace(g.OwnerWxid) ||
                                              string.Equals(g.OwnerWxid, activeWxid, StringComparison.OrdinalIgnoreCase));
                AppServices.GroupAutoReply.SyncGroupsSnapshot(gTask.Result);
                AccountsUpdated?.Invoke();
                if (IsFileWatcherRunning)
                {
                    UpdateWatchStatus(BuildIdleWatchStatusText());
                }
            }
            catch { }
        }

        private void RestartFileWatcher()
        {
            StopAutoSyncTimer();

            var account = ResolveTargetAccount(AppServices.Settings.Current.WeChatSyncAccountDir, preferMostRecentLocal: true);
            if (account is null || !Directory.Exists(account.DbStoragePath))
            {
                UpdateWatchStatus("微信自动监听：等待检测到电脑微信");
                return;
            }

            _activeAccount = account;
            _activeAccountLastModified = account.LastModified;
            _watchedDbDir = account.DbStoragePath;
            _lastDbFingerprint = ComputeDbStorageFingerprint(_watchedDbDir);

            var cts = new CancellationTokenSource();
            _watcherCts = cts;

            try
            {
                var fsw = new FileSystemWatcher(_watchedDbDir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                    Filter = "*.*",
                    EnableRaisingEvents = true
                };
                fsw.Changed += OnDbFileEvent;
                fsw.Created += OnDbFileEvent;
                fsw.Renamed += OnDbFileEvent;
                _dbWatcher = fsw;
            }
            catch (Exception ex)
            {
                RecordActivity($"文件系统事件监听降级为轻量巡检：{ex.Message}");
            }

            // Lightweight 15-second patrol: detects both WAL writes AND multi-WeChat account switches on the PC!
            _patrolTask = Task.Run(async () =>
            {
                await RefreshMonitoredCountsAsync();
                // Auto-identify current WeChat self profile & avatar once on startup so WeChatSyncPage shows real profile immediately
                if (WeChat4Paths.FindWeixinProcesses().Count > 0)
                {
                    try { await DetectAndSyncCurrentWeChatAccountAsync(forceReload: false, ct: cts.Token); }
                    catch { }
                }

                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
                try
                {
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        if (!AppServices.Backend.IsAuthenticated) continue;

                        // Check whether the user switched to another WeChat account on this PC
                        var newestLocal = WeChat4Paths.ListAccountDirs().FirstOrDefault();
                        if (newestLocal is not null &&
                            !string.Equals(newestLocal.AccountPath, _activeAccount?.AccountPath, StringComparison.OrdinalIgnoreCase) &&
                            newestLocal.LastModified > _activeAccountLastModified.AddSeconds(2) &&
                            WeChat4Paths.FindWeixinProcesses().Count > 0)
                        {
                            string oldWxid = _activeAccount?.Wxid ?? "-";
                            RecordActivity($"检测到微信切换账号：{oldWxid} → {newestLocal.Wxid}，正在切换监听上下文...");
                            _activeAccount = newestLocal;
                            _activeAccountLastModified = newestLocal.LastModified;
                            AppServices.Settings.Current.WeChatSyncAccountDir = newestLocal.AccountPath;
                            AppServices.Settings.Save();
                            _ = Task.Run(async () =>
                            {
                                await DetectAndSyncCurrentWeChatAccountAsync(forceReload: true);
                                await SyncImportedTargetsAsync(syncCustomers: true, syncGroups: true, isBackgroundAuto: true);
                            });
                            RestartFileWatcher();
                            return;
                        }

                        string fp = ComputeDbStorageFingerprint(_watchedDbDir);
                        if (!string.Equals(fp, _lastDbFingerprint, StringComparison.Ordinal))
                        {
                            _lastDbFingerprint = fp;
                            _activeAccountLastModified = DateTimeOffset.UtcNow;
                            LastFileChangeAt = DateTimeOffset.Now;
                            ScheduleDebouncedTargetedSync("检测到微信消息文件变化");
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }, cts.Token);

            UpdateWatchStatus(BuildIdleWatchStatusText());
            RecordActivity($"已启动自动监听（账号 {account.Wxid}），仅自动同步该账号下已导入的客户与群聊。");
        }

        private void OnDbFileEvent(object sender, FileSystemEventArgs e)
        {
            string name = e.Name ?? string.Empty;
            if (!IsRelevantWeChatDbFile(name)) return;
            LastFileChangeAt = DateTimeOffset.Now;
            ScheduleDebouncedTargetedSync($"检测到 {Path.GetFileName(name)} 更新");
        }

        private static bool IsRelevantWeChatDbFile(string relativeOrFileName)
        {
            string lower = relativeOrFileName.ToLowerInvariant();
            if (!lower.EndsWith(".db", StringComparison.Ordinal) &&
                !lower.EndsWith(".db-wal", StringComparison.Ordinal))
            {
                return false;
            }
            string file = Path.GetFileName(lower);
            return file.StartsWith("message", StringComparison.Ordinal) ||
                   file.StartsWith("session", StringComparison.Ordinal) ||
                   file.StartsWith("contact", StringComparison.Ordinal) ||
                   file.StartsWith("head_image", StringComparison.Ordinal);
        }

        private static string ComputeDbStorageFingerprint(string dbStorageDir)
        {
            if (string.IsNullOrWhiteSpace(dbStorageDir) || !Directory.Exists(dbStorageDir))
                return string.Empty;
            try
            {
                long totalBytes = 0;
                long maxTicks = 0;
                int count = 0;
                foreach (string file in Directory.EnumerateFiles(dbStorageDir, "*.db*", SearchOption.AllDirectories))
                {
                    if (!IsRelevantWeChatDbFile(file)) continue;
                    var fi = new FileInfo(file);
                    totalBytes += fi.Length;
                    long t = fi.LastWriteTimeUtc.Ticks;
                    if (t > maxTicks) maxTicks = t;
                    count++;
                }
                return $"{count}:{totalBytes}:{maxTicks}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private void ScheduleDebouncedTargetedSync(string reason)
        {
            CancellationToken rootToken;
            CancellationTokenSource debounceCts;
            lock (_watcherLock)
            {
                if (_watcherCts is null || _watcherCts.IsCancellationRequested) return;
                rootToken = _watcherCts.Token;

                if (_debounceCts is not null)
                {
                    try { _debounceCts.Cancel(); } catch { }
                    _debounceCts.Dispose();
                }
                debounceCts = CancellationTokenSource.CreateLinkedTokenSource(rootToken);
                _debounceCts = debounceCts;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    // Wait 4 seconds for WeChat to finish writing burst messages / WAL frames
                    await Task.Delay(TimeSpan.FromSeconds(4), debounceCts.Token);
                    if (!AppServices.Backend.IsAuthenticated) return;

                    _lastDbFingerprint = ComputeDbStorageFingerprint(_watchedDbDir);
                    UpdateWatchStatus($"监听中：{reason}，正在自动同步已导入客户与群...");

                    var (ok, msg, report) = await SyncImportedTargetsAsync(
                        syncCustomers: true,
                        syncGroups: true,
                        isBackgroundAuto: true,
                        ct: debounceCts.Token);

                    if (ok && report is not null)
                    {
                        int totalNew = report.CustomerMessagesInserted + report.GroupMessagesInserted;
                        UpdateWatchStatus(totalNew > 0
                            ? $"监听中 · 上次自动同步 {DateTime.Now:HH:mm}（+{totalNew} 条新消息）"
                            : BuildIdleWatchStatusText());
                    }
                    else
                    {
                        UpdateWatchStatus(BuildIdleWatchStatusText());
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("WX4-WATCH", $"自动监听同步异常：{ex.Message}");
                    UpdateWatchStatus(BuildIdleWatchStatusText());
                }
            }, debounceCts.Token);
        }

        private string BuildIdleWatchStatusText()
        {
            string last = LastSyncAt.HasValue ? $" · 上次同步 {LastSyncAt.Value:HH:mm}" : string.Empty;
            return $"自动监听中 · 已托管 {MonitoredCustomersCount} 位客户 / {MonitoredGroupsCount} 个群{last}";
        }

        public void StopAutoSyncTimer()
        {
            lock (_watcherLock)
            {
                if (_debounceCts is not null)
                {
                    try { _debounceCts.Cancel(); } catch { }
                    _debounceCts.Dispose();
                    _debounceCts = null;
                }
                if (_dbWatcher is not null)
                {
                    try
                    {
                        _dbWatcher.EnableRaisingEvents = false;
                        _dbWatcher.Changed -= OnDbFileEvent;
                        _dbWatcher.Created -= OnDbFileEvent;
                        _dbWatcher.Renamed -= OnDbFileEvent;
                        _dbWatcher.Dispose();
                    }
                    catch { }
                    _dbWatcher = null;
                }
                if (_watcherCts is not null)
                {
                    try { _watcherCts.Cancel(); } catch { }
                    _watcherCts.Dispose();
                    _watcherCts = null;
                }
                _patrolTask = null;
            }
        }

        private WeChat4AccountDir? ResolveTargetAccount(string savedDir, bool preferMostRecentLocal = false)
        {
            var detected = WeChat4Paths.ListAccountDirs();
            if (preferMostRecentLocal && detected.Count > 1 && WeChat4Paths.FindWeixinProcesses().Count > 0)
            {
                var newest = detected[0];
                if (_activeAccount is null || newest.LastModified > _activeAccount.LastModified.AddSeconds(2))
                {
                    return newest;
                }
            }
            if (_activeAccount is not null && Directory.Exists(_activeAccount.DbStoragePath))
            {
                return _activeAccount;
            }
            if (!string.IsNullOrWhiteSpace(savedDir) && Directory.Exists(savedDir))
            {
                try
                {
                    var found = WeChat4Paths.ResolveSelectedDirs(savedDir);
                    if (found.Count > 0) return found[0];
                }
                catch { }
            }
            return detected.FirstOrDefault();
        }

        private void ReplaceActiveSnapshot(WeChat4Reader newReader, string newDir, WeChat4AccountDir account)
        {
            var oldReader = _activeReader;
            var oldDir = _activeDecryptedDir;

            _activeReader = newReader;
            _activeDecryptedDir = newDir;
            _activeAccount = account;

            AppServices.Settings.Current.WeChatSyncAccountDir = account.AccountPath;
            AppServices.Settings.Save();

            oldReader?.Dispose();
            // Never delete the persistent cache directory; only clean up legacy temp directories if switching away
            if (oldDir is not null &&
                !string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase) &&
                oldDir.Contains("weixinORC_wx4_", StringComparison.OrdinalIgnoreCase))
            {
                TryCleanupDir(oldDir);
            }
        }

        private static void TryCleanupDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }

        public void Dispose()
        {
            StopAutoSyncTimer();
            _activeReader?.Dispose();
            _activeReader = null;
            _activeDecryptedDir = null;
            _syncGate.Dispose();
        }
    }

    internal static class WeChatSyncDtoHelpers
    {
        public static string AvatarURLSafe(this WeChatSyncGroupMemberResultDto dto) => dto.AvatarUrl ?? string.Empty;
    }
}
