namespace Client.Services.WeComDb
{
    /// <summary>
    /// Live read-only watcher &amp; snapshot service for Windows WeCom (WXWork 4.x/5.x).
    /// Monitors Data/*.db-wal changes, incrementally decrypts into local cache, detects new messages
    /// and group metadata diffs (name change, member join/leave), and supports post-send WAL confirmation.
    /// </summary>
    public sealed class WeComSyncService : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _watchLock = new();

        private WeComAccountDir? _activeAccount;
        private WeComReader? _activeReader;
        private byte[]? _activeKey;
        private readonly Dictionary<string, WeComAccountSnapshot> _loadedSnapshots = new(StringComparer.OrdinalIgnoreCase);
        private long _lastObservedMessageId;
        private readonly Dictionary<string, (string Name, HashSet<string> Members)> _lastGroupSnapshots = new(StringComparer.OrdinalIgnoreCase);

        private FileSystemWatcher? _watcher;
        private CancellationTokenSource? _watchCts;
        private CancellationTokenSource? _debounceCts;
        private bool _wasProcessRunning;
        private bool _disposed;

        public WeComAccountDir? ActiveAccount => _activeAccount;
        public WeComReader? ActiveReader => _activeReader;
        public IReadOnlyList<WeComAccountSnapshot> LoadedAccounts
        {
            get
            {
                lock (_watchLock)
                {
                    return _loadedSnapshots.Values
                        .OrderByDescending(a => a.IsPrimary)
                        .ThenByDescending(a => a.LastModified)
                        .ToList();
                }
            }
        }
        public bool IsWatcherRunning => _watchCts is not null && !_watchCts.IsCancellationRequested;
        public string StatusText { get; private set; } = "企业微信监听：未启动";

        public event Action<WeComEvent>? EventDetected;
        public event Action<string>? StatusChanged;
        public event Action? AccountsUpdated;

        public WeComReader? GetReaderForAccount(string? accountId)
        {
            if (string.IsNullOrWhiteSpace(accountId)) return _activeReader;
            lock (_watchLock)
            {
                if (_loadedSnapshots.TryGetValue(accountId.Trim(), out var snap)) return snap.Reader;
            }
            return _activeReader;
        }

        public async Task<(bool Ok, string Message)> SwitchPrimaryAccountAsync(string accountId, CancellationToken ct = default)
        {
            var dirs = WeComPaths.ListAccountDirs();
            var match = dirs.FirstOrDefault(d => string.Equals(d.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return (false, $"未找到企业微信账号目录：{accountId}");
            }
            AppServices.Settings.Current.WeComAccountDir = match.AccountPath;
            AppServices.Settings.Save();
            var res = await EnsureAllAccountsReadyAsync(forceRefresh: true, ct);
            AttachFileWatcher();
            return res;
        }

        public async Task<(bool Ok, string Message)> EnsureAllAccountsReadyAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                var primaryRes = await RefreshUnlockedAsync(forceRefresh, emitEvents: false, ct);
                await LoadAllAccessibleAccountsUnlockedAsync(forceRefresh, ct);
                AccountsUpdated?.Invoke();
                return primaryRes;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<(bool Ok, string Message)> EnsureReadyAsync(bool forceRefresh = false, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            try
            {
                return await RefreshUnlockedAsync(forceRefresh, emitEvents: false, ct);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<bool> VerifyOutgoingMessageDeliveredAsync(
            string conversationId,
            string expectedText,
            DateTimeOffset sentWindowStartUtc,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(conversationId)) return false;
            long minUnix = Math.Max(0, sentWindowStartUtc.ToUnixTimeSeconds() - 15);
            int[] delaysMs = [650, 1000, 1350];

            for (int i = 0; i < delaysMs.Length; i++)
            {
                try { await Task.Delay(delaysMs[i], ct); }
                catch (OperationCanceledException) { return false; }

                if (!await _gate.WaitAsync(TimeSpan.FromSeconds(3), ct)) continue;
                try
                {
                    var (ok, _) = await RefreshUnlockedAsync(forceRefresh: true, emitEvents: false, ct);
                    if (ok && _activeReader is not null &&
                        _activeReader.HasRecentOutgoingMessageMatching(conversationId, expectedText, minUnix))
                    {
                        return true;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            return false;
        }

        public void StartWatching()
        {
            lock (_watchLock)
            {
                if (IsWatcherRunning) return;
                _watchCts = new CancellationTokenSource();
            }

            var cts = _watchCts!;
            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureReadyAsync(forceRefresh: true, ct: cts.Token);
                    AttachFileWatcher();
                    _wasProcessRunning = WeComPaths.FindWXWorkProcesses().Count > 0;

                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        bool runningNow = WeComPaths.FindWXWorkProcesses().Count > 0;
                        if (_wasProcessRunning && !runningNow)
                        {
                            _wasProcessRunning = false;
                            UpdateStatus("企业微信进程已退出");
                            EventDetected?.Invoke(new WeComEvent { EventKind = "logout" });
                            continue;
                        }
                        _wasProcessRunning = runningNow;

                        if (!runningNow) continue;

                        await _gate.WaitAsync(cts.Token);
                        try
                        {
                            // Detect active account switch or unattached watcher
                            var newest = ResolveTargetAccount();
                            if (newest is not null &&
                                (_activeAccount is null ||
                                 !string.Equals(newest.AccountId, _activeAccount.AccountId, StringComparison.OrdinalIgnoreCase) &&
                                 newest.LastModified > _activeAccount.LastModified.AddSeconds(2)))
                            {
                                _activeAccount = newest;
                                _activeKey = null;
                                _lastObservedMessageId = 0;
                                _lastGroupSnapshots.Clear();
                                AttachFileWatcher();
                            }

                            await RefreshUnlockedAsync(forceRefresh: false, emitEvents: true, cts.Token);
                        }
                        finally
                        {
                            _gate.Release();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("WECOM", $"企业微信监听巡检异常：{ex.Message}");
                }
            }, cts.Token);
        }

        public void StopWatching()
        {
            lock (_watchLock)
            {
                try { _debounceCts?.Cancel(); _debounceCts?.Dispose(); } catch { }
                _debounceCts = null;
                DetachFileWatcher();
                try { _watchCts?.Cancel(); _watchCts?.Dispose(); } catch { }
                _watchCts = null;
            }
            UpdateStatus("企业微信监听：已停止");
        }

        private void AttachFileWatcher()
        {
            lock (_watchLock)
            {
                DetachFileWatcher();
                if (_activeAccount is null || !Directory.Exists(_activeAccount.DataPath)) return;
                try
                {
                    var fsw = new FileSystemWatcher(_activeAccount.DataPath)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                        Filter = "*.db*",
                        EnableRaisingEvents = true
                    };
                    fsw.Changed += OnDataFileChanged;
                    fsw.Created += OnDataFileChanged;
                    fsw.Renamed += OnDataFileChanged;
                    _watcher = fsw;
                }
                catch { }
            }
        }

        private void DetachFileWatcher()
        {
            if (_watcher is not null)
            {
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.Changed -= OnDataFileChanged;
                    _watcher.Created -= OnDataFileChanged;
                    _watcher.Renamed -= OnDataFileChanged;
                    _watcher.Dispose();
                }
                catch { }
                _watcher = null;
            }
        }

        private void OnDataFileChanged(object sender, FileSystemEventArgs e)
        {
            string name = (e.Name ?? string.Empty).ToLowerInvariant();
            if (!name.StartsWith("message.db", StringComparison.Ordinal) &&
                !name.StartsWith("session.db", StringComparison.Ordinal) &&
                !name.StartsWith("user.db", StringComparison.Ordinal))
            {
                return;
            }

            CancellationTokenSource? linked;
            lock (_watchLock)
            {
                if (_watchCts is null || _watchCts.IsCancellationRequested) return;
                try { _debounceCts?.Cancel(); _debounceCts?.Dispose(); } catch { }
                linked = CancellationTokenSource.CreateLinkedTokenSource(_watchCts.Token);
                _debounceCts = linked;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(750, linked.Token);
                    await _gate.WaitAsync(linked.Token);
                    try
                    {
                        await RefreshUnlockedAsync(forceRefresh: false, emitEvents: true, linked.Token);
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("WECOM", $"企业微信实时变化处理异常：{ex.Message}");
                }
            }, linked.Token);
        }

        private WeComAccountDir? ResolveTargetAccount()
        {
            string configured = AppServices.Settings.Current.WeComAccountDir;
            var dirs = WeComPaths.ListAccountDirs(string.IsNullOrWhiteSpace(configured) ? null : configured);
            if (dirs.Count == 0 && !string.IsNullOrWhiteSpace(configured))
            {
                dirs = WeComPaths.ListAccountDirs();
            }
            return dirs.FirstOrDefault();
        }

        private Task<(bool Ok, string Message)> RefreshUnlockedAsync(bool forceRefresh, bool emitEvents, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var account = ResolveTargetAccount();
                if (account is null)
                {
                    UpdateStatus("企业微信：未检测到本地数据目录");
                    return (false, "未检测到本机企业微信数据目录（Documents\\WXWork）。");
                }

                bool switchedAccount = _activeAccount is null ||
                                       !string.Equals(_activeAccount.AccountId, account.AccountId, StringComparison.OrdinalIgnoreCase);
                if (switchedAccount)
                {
                    _activeAccount = account;
                    _activeKey = null;
                    _lastObservedMessageId = 0;
                    _lastGroupSnapshots.Clear();
                }

                if (_activeKey is null)
                {
                    var extractor = new WeComKeyExtractor();
                    _activeKey = extractor.ExtractForAccount(account, useCache: true, ct: ct);
                    if (_activeKey is null)
                    {
                        string reason = extractor.EncounteredAccessDenied
                            ? "读取企业微信进程权限不足，请以管理员身份运行。"
                            : "未提取到企业微信数据库密钥，请确认电脑端企业微信已登录运行。";
                        UpdateStatus($"企业微信：{reason}");
                        return (false, reason);
                    }
                }

                string cacheDir = WeComDecryptor.GetCacheDir(account.AccountId);
                bool dbChanged = forceRefresh || switchedAccount || _activeReader is null ||
                                 WeComDecryptor.HasAnyCoreDbChanged(account.DataPath, _activeKey, cacheDir);

                if (!dbChanged && _activeReader is not null)
                {
                    return (true, $"企业微信已就绪（{account.AccountId}）");
                }

                // Close reader handles before overwriting cache files
                var prevReader = _activeReader;
                _activeReader = null;
                try { prevReader?.Dispose(); } catch { }

                var dec = WeComDecryptor.DecryptIncremental(account.DataPath, _activeKey, cacheDir, retries: 3, ct: ct);
                if (dec.TotalAvailable == 0)
                {
                    UpdateStatus("企业微信：解密本地数据库失败");
                    return (false, "企业微信本地数据库解密失败，请稍后重试。");
                }

                var reader = new WeComReader(cacheDir, account.AccountId, account.AccountPath);
                _activeReader = reader;
                UpsertSnapshot(account, reader, isPrimary: true);

                if (_lastObservedMessageId == 0)
                {
                    _lastObservedMessageId = reader.GetLatestMessageId();
                    CaptureGroupSnapshots(reader, emitDiffs: false);
                }
                else
                {
                    var newMsgs = reader.ReadMessagesAfter(_lastObservedMessageId, 200);
                    foreach (var m in newMsgs)
                    {
                        if (m.MessageId > _lastObservedMessageId) _lastObservedMessageId = m.MessageId;
                        if (emitEvents)
                        {
                            EventDetected?.Invoke(new WeComEvent
                            {
                                EventKind = "message",
                                Message = m,
                                ConversationId = m.ConversationId,
                                RoomId = m.IsGroup ? m.PeerId : string.Empty
                            });
                        }
                    }

                    CaptureGroupSnapshots(reader, emitDiffs: emitEvents);
                }

                string label = $"{reader.SelfCorpName} · {reader.SelfUser?.DisplayName ?? account.AccountId}";
                UpdateStatus($"企业微信在线 · {label}（{reader.Users.Count} 联系人 / {reader.Groups.Count} 群）");
                AccountsUpdated?.Invoke();
                return (true, StatusText);
            }, ct);
        }

        private Task LoadAllAccessibleAccountsUnlockedAsync(bool forceRefresh, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var allDirs = WeComPaths.ListAccountDirs();
                if (allDirs.Count == 0) return;

                var extractor = new WeComKeyExtractor();
                var keys = extractor.ExtractForAccounts(allDirs, useCache: true, ct: ct);

                foreach (var dir in allDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    if (_activeAccount is not null &&
                        string.Equals(dir.AccountId, _activeAccount.AccountId, StringComparison.OrdinalIgnoreCase) &&
                        _activeReader is not null)
                    {
                        UpsertSnapshot(dir, _activeReader, isPrimary: true);
                        continue;
                    }

                    if (!keys.TryGetValue(dir.AccountId, out var keyBytes)) continue;

                    string cacheDir = WeComDecryptor.GetCacheDir(dir.AccountId);
                    WeComAccountSnapshot? existing;
                    lock (_watchLock) _loadedSnapshots.TryGetValue(dir.AccountId, out existing);

                    bool changed = forceRefresh || existing is null ||
                                   WeComDecryptor.HasAnyCoreDbChanged(dir.DataPath, keyBytes, cacheDir);
                    if (!changed && existing is not null)
                    {
                        existing.IsPrimary = false;
                        continue;
                    }

                    if (existing is not null)
                    {
                        lock (_watchLock) _loadedSnapshots.Remove(dir.AccountId);
                        try { existing.Reader.Dispose(); } catch { }
                    }

                    var dec = WeComDecryptor.DecryptIncremental(dir.DataPath, keyBytes, cacheDir, retries: 2, ct: ct);
                    if (dec.TotalAvailable == 0) continue;

                    var reader = new WeComReader(cacheDir, dir.AccountId, dir.AccountPath);
                    UpsertSnapshot(dir, reader, isPrimary: false);
                }
            }, ct);
        }

        private void UpsertSnapshot(WeComAccountDir account, WeComReader reader, bool isPrimary)
        {
            lock (_watchLock)
            {
                if (isPrimary)
                {
                    foreach (var s in _loadedSnapshots.Values) s.IsPrimary = false;
                }
                _loadedSnapshots[account.AccountId] = new WeComAccountSnapshot
                {
                    AccountId = account.AccountId,
                    AccountPath = account.AccountPath,
                    CorpId = reader.SelfCorpId,
                    CorpName = reader.SelfCorpName,
                    CorpLogoUrl = reader.SelfCorpLogoUrl,
                    SelfUser = reader.SelfUser ?? new WeComUser { UserId = account.AccountId, Name = account.AccountId },
                    Reader = reader,
                    IsPrimary = isPrimary,
                    LastModified = account.LastModified
                };
            }
        }

        private void CaptureGroupSnapshots(WeComReader reader, bool emitDiffs)
        {
            foreach (var grp in reader.Groups)
            {
                var curMembers = grp.Members.Select(m => m.UserId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                string curName = grp.DisplayName;

                if (_lastGroupSnapshots.TryGetValue(grp.ConversationId, out var prev))
                {
                    if (emitDiffs)
                    {
                        if (!string.IsNullOrWhiteSpace(prev.Name) &&
                            !string.IsNullOrWhiteSpace(curName) &&
                            !string.Equals(prev.Name, curName, StringComparison.Ordinal))
                        {
                            EventDetected?.Invoke(new WeComEvent
                            {
                                EventKind = "group_name_changed",
                                ConversationId = grp.ConversationId,
                                RoomId = grp.RoomId,
                                OldGroupName = prev.Name,
                                NewGroupName = curName
                            });
                        }

                        var joined = curMembers.Except(prev.Members, StringComparer.OrdinalIgnoreCase).ToList();
                        if (joined.Count > 0)
                        {
                            EventDetected?.Invoke(new WeComEvent
                            {
                                EventKind = "group_member_joined",
                                ConversationId = grp.ConversationId,
                                RoomId = grp.RoomId,
                                MemberIds = joined
                            });
                        }

                        var left = prev.Members.Except(curMembers, StringComparer.OrdinalIgnoreCase).ToList();
                        if (left.Count > 0)
                        {
                            EventDetected?.Invoke(new WeComEvent
                            {
                                EventKind = "group_member_left",
                                ConversationId = grp.ConversationId,
                                RoomId = grp.RoomId,
                                MemberIds = left
                            });
                        }
                    }
                }

                _lastGroupSnapshots[grp.ConversationId] = (curName, curMembers);
            }
        }

        private void UpdateStatus(string status)
        {
            StatusText = status;
            StatusChanged?.Invoke(status);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
            lock (_watchLock)
            {
                foreach (var snap in _loadedSnapshots.Values)
                {
                    try { snap.Reader.Dispose(); } catch { }
                }
                _loadedSnapshots.Clear();
            }
            _activeReader = null;
            _gate.Dispose();
        }
    }
}
