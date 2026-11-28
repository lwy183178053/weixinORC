using System.Diagnostics;
using Client.Models;
using Client.Services.WeChat4Db;

namespace Client.Services
{
    public sealed class FriendAutoAcceptRecord
    {
        public DateTime Timestamp { get; init; } = DateTime.Now;
        public string RequesterName { get; init; } = string.Empty;
        public string VerifyMessage { get; init; } = string.Empty;
        public string ActionSummary { get; init; } = string.Empty;
        public bool Succeeded { get; init; }
        public bool Skipped { get; init; }
        public long ElapsedMs { get; init; }

        public string FormattedLine
        {
            get
            {
                string tag = Succeeded ? "✓ 已同意" : (Skipped ? "• 已跳过" : "! 异常");
                string who = string.IsNullOrWhiteSpace(RequesterName) ? "好友申请" : $"「{RequesterName}」";
                string detail = string.IsNullOrWhiteSpace(ActionSummary) ? string.Empty : $" · {ActionSummary}";
                string cost = ElapsedMs > 0 ? $" ({ElapsedMs}ms)" : string.Empty;
                return $"[{Timestamp:HH:mm:ss}] {tag} {who}{detail}{cost}";
            }
        }
    }

    public sealed class FriendAutoAcceptPassResult
    {
        public bool Success { get; init; }
        public bool CancelledByEsc { get; init; }
        public int AcceptedCount { get; init; }
        public int SkippedCount { get; init; }
        public string Message { get; init; } = string.Empty;
    }

    /// <summary>
    /// OCR + Win32 hardware-input RPA engine for automatically accepting incoming personal WeChat
    /// friend verification requests (WeChat 4.x / 3.x):
    /// 1. Brings personal WeChat window forward (respecting user idle guard in background mode)
    /// 2. Visually locates and clicks the「通讯录」icon on WeChat's left rail (verified by OCR)
    /// 3. Expands「新的朋友」and scans pending requests (「接受」/「通过验证」/ optional「已过期 -> 前往验证」)
    /// 4. Optionally filters by verification keywords, then opens the floating「通过朋友验证」dialog
    /// 5. Optionally prepends Remark prefix, assigns Tag, sets「仅聊天」permission, and clicks「确定」
    /// 6. Optionally switches back to the「聊天」tab. Pressing ESC aborts immediately at any step.
    /// </summary>
    public sealed class WeChatFriendAutoAcceptService : IDisposable
    {
        private static readonly string[] NextSectionHeaders =
        [
            "企业微信联系人", "企业微信", "公众号", "服务号", "群聊", "标签", "星标朋友", "通讯录"
        ];

        private static readonly string[] PendingButtonLabels =
        [
            "接受", "通过验证", "同意", "接受申请", "查看"
        ];

        private static readonly char[] KeywordSeparators = [',', '，', ';', '；', '/', '|', '\n', '\r'];

        private readonly SemaphoreSlim _execGate = new(1, 1);
        private readonly object _stateLock = new();
        private readonly LinkedList<FriendAutoAcceptRecord> _records = new();
        private readonly Dictionary<string, DateTimeOffset> _recentAttemptedKeys = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _loopCts;
        private string _lastDbWatchFingerprint = string.Empty;
        private bool _hasDoneInitialSmartScan;
        private bool _isExecuting;
        private bool _disposed;

        public bool IsWatcherRunning => _loopCts is not null && !_loopCts.IsCancellationRequested;
        public bool IsExecuting => _isExecuting;
        public string StatusText { get; private set; } = "自动同意好友：未开启";
        public DateTimeOffset? LastCheckedAt { get; private set; }

        public event Action? StateChanged;
        public event Action<FriendAutoAcceptRecord>? RecordAdded;

        public IReadOnlyList<FriendAutoAcceptRecord> GetRecentRecords()
        {
            lock (_stateLock)
            {
                return _records.ToList();
            }
        }

        public void ClearRecords()
        {
            lock (_stateLock)
            {
                _records.Clear();
            }
            StateChanged?.Invoke();
        }

        public void StartIfConfigured()
        {
            if (AppServices.Settings.Current.AutoAcceptFriendEnabled)
            {
                StartWatching();
            }
            else
            {
                UpdateStatus("自动同意好友：未开启");
            }
        }

        public void ConfigureEnabled(bool enabled)
        {
            AppServices.Settings.Current.AutoAcceptFriendEnabled = enabled;
            AppServices.Settings.Save();
            if (enabled)
            {
                _hasDoneInitialSmartScan = false;
                StartWatching();
            }
            else
            {
                StopWatching();
            }
        }

        public void NotifyConfigUpdated()
        {
            if (AppServices.Settings.Current.AutoAcceptFriendEnabled)
            {
                if (!IsWatcherRunning) StartWatching();
                else
                {
                    UpdateStatus(BuildIdleStatusLabel());
                }
            }
            else
            {
                UpdateStatus("自动同意好友：未开启");
            }
        }

        public void StartWatching()
        {
            lock (_stateLock)
            {
                if (IsWatcherRunning) return;
                _loopCts = new CancellationTokenSource();
            }

            var cts = _loopCts!;
            UpdateStatus(BuildIdleStatusLabel());

            _ = Task.Run(async () =>
            {
                try
                {
                    _lastDbWatchFingerprint = ComputeWeChatDbWriteFingerprint();
                    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
                    DateTimeOffset lastRunUtc = DateTimeOffset.MinValue;

                    while (await timer.WaitForNextTickAsync(cts.Token))
                    {
                        var cfg = AppServices.Settings.Current;
                        if (!cfg.AutoAcceptFriendEnabled) break;

                        if (AppServices.GroupAutoReply.IsExecuting || AppServices.IsModalDialogOpen)
                        {
                            continue;
                        }

                        if (WeChat4Paths.FindWeixinProcesses().Count == 0 &&
                            !AppServices.WeChatWindows.EnumerateWindows(false).Any(w => w.AccountType == WeChatAccountType.Personal))
                        {
                            continue;
                        }

                        bool shouldRun = false;
                        string mode = cfg.AutoAcceptTriggerMode ?? "smart";
                        if (string.Equals(mode, "smart", StringComparison.OrdinalIgnoreCase))
                        {
                            string fp = ComputeWeChatDbWriteFingerprint();
                            if (!_hasDoneInitialSmartScan)
                            {
                                shouldRun = true;
                            }
                            else if (!string.IsNullOrEmpty(fp) && !string.Equals(fp, _lastDbWatchFingerprint, StringComparison.Ordinal))
                            {
                                _lastDbWatchFingerprint = fp;
                                shouldRun = (DateTimeOffset.UtcNow - lastRunUtc).TotalSeconds >= 8;
                            }
                        }
                        else
                        {
                            int intervalSec = mode switch
                            {
                                "interval_30" => 30,
                                "interval_60" => 60,
                                "interval_120" => 120,
                                _ => 60
                            };
                            shouldRun = (DateTimeOffset.UtcNow - lastRunUtc).TotalSeconds >= intervalSec;
                        }

                        if (!shouldRun) continue;

                        // Respect idle guard in automatic background mode so we don't steal focus while employee is typing/clicking
                        if (cfg.AutoAcceptOnlyWhenIdle)
                        {
                            long idleMs = Win32WindowHelper.GetUserIdleMilliseconds();
                            if (idleMs < 5000)
                            {
                                continue;
                            }
                        }

                        _hasDoneInitialSmartScan = true;
                        lastRunUtc = DateTimeOffset.UtcNow;
                        _lastDbWatchFingerprint = ComputeWeChatDbWriteFingerprint();

                        await RunPassInternalAsync(isManual: false, cts.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    AppServices.Logs.Warn("FRIEND", $"自动同意好友巡检循环异常：{ex.Message}");
                }
            }, cts.Token);
        }

        public void StopWatching()
        {
            lock (_stateLock)
            {
                try { _loopCts?.Cancel(); _loopCts?.Dispose(); } catch { }
                _loopCts = null;
            }
            UpdateStatus("自动同意好友：已关闭");
        }

        public Task<FriendAutoAcceptPassResult> RunOnceNowAsync(CancellationToken ct = default) =>
            RunPassInternalAsync(isManual: true, ct);

        private string BuildIdleStatusLabel()
        {
            string mode = AppServices.Settings.Current.AutoAcceptTriggerMode ?? "smart";
            return mode switch
            {
                "interval_30" => "自动同意好友：每 30 秒巡检",
                "interval_60" => "自动同意好友：每 60 秒巡检",
                "interval_120" => "自动同意好友：每 120 秒巡检",
                _ => "自动同意好友：智能免打扰监听中"
            };
        }

        private async Task<FriendAutoAcceptPassResult> RunPassInternalAsync(bool isManual, CancellationToken ct)
        {
            if (!await _execGate.WaitAsync(isManual ? TimeSpan.FromSeconds(2) : TimeSpan.Zero, ct))
            {
                return new FriendAutoAcceptPassResult { Success = false, Message = "已有一次好友申请检测正在执行中，请稍候。" };
            }

            _isExecuting = true;
            UpdateStatus("自动同意好友：正在执行 OCR 检测...");

            var cfg = AppServices.Settings.Current;
            bool prevOverlay = ClickVisualOverlay.IsEnabled;
            ClickVisualOverlay.IsEnabled = cfg.AutoAcceptShowClickOverlay;
            string? savedClipboard = Win32WindowHelper.TryGetClipboardUnicodeText();
            bool touchedClipboard = false;

            int acceptedCount = 0;
            int skippedCount = 0;

            try
            {
                if (!AppServices.Ocr.RapidEngine.IsAvailable)
                {
                    string err = $"本地 OCR 引擎不可用：{AppServices.Ocr.RapidEngine.AvailabilityNote}";
                    RecordEntry(new FriendAutoAcceptRecord { Succeeded = false, ActionSummary = err });
                    return new FriendAutoAcceptPassResult { Success = false, Message = err };
                }

                if (Win32WindowHelper.IsEscapePressed())
                {
                    return new FriendAutoAcceptPassResult { Success = false, CancelledByEsc = true, Message = "已按 ESC 中止操作。" };
                }

                // Step 1: Ensure personal WeChat window is foreground & visible
                var ensured = await AppServices.WeChatWindows.EnsurePersonalWeChatWindowReadyAsync(
                    autoLaunchIfClosed: false,
                    allowLoginPageSuccess: false,
                    ct: ct);

                if (!ensured.Success || ensured.Window is null)
                {
                    if (isManual)
                    {
                        RecordEntry(new FriendAutoAcceptRecord { Succeeded = false, ActionSummary = ensured.Message });
                    }
                    return new FriendAutoAcceptPassResult { Success = false, Message = ensured.Message };
                }

                nint hwnd = ensured.Window.Hwnd;
                int pid = ensured.Window.ProcessId;

                string? uipiErr = Win32WindowHelper.GetPrivilegeMismatchDetail(hwnd);
                if (!string.IsNullOrWhiteSpace(uipiErr))
                {
                    RecordEntry(new FriendAutoAcceptRecord { Succeeded = false, ActionSummary = "微信为管理员权限，请以管理员重启本工作台" });
                    return new FriendAutoAcceptPassResult { Success = false, Message = uipiErr };
                }

                if (!Win32WindowHelper.GetWindowRect(hwnd, out var rawRect) || rawRect.Width < 360 || rawRect.Height < 300)
                {
                    return new FriendAutoAcceptPassResult { Success = false, Message = "无法读取微信主窗口有效区域，请确认微信窗口未被最小化。" };
                }

                uint dpi = Win32WindowHelper.GetDpiForWindow(hwnd);
                double scale = dpi >= 96 ? dpi / 96.0 : 1.0;

                // Step 2: Detect left rail icons & switch to「通讯录」tab
                var (railFound, chatIconScreenX, chatIconScreenY, contactsIconScreenX, contactsIconScreenY) =
                    DetectLeftRailTargets(hwnd, rawRect, scale);

                bool onContactsTab = await EnsureOnContactsTabAsync(
                    hwnd, rawRect, scale, contactsIconScreenX, contactsIconScreenY, ct);

                if (!onContactsTab)
                {
                    string err = "未能通过 OCR 确认进入微信「通讯录」页签，请检查微信主窗口是否被遮挡。";
                    if (isManual)
                    {
                        RecordEntry(new FriendAutoAcceptRecord { Succeeded = false, ActionSummary = err });
                    }
                    return new FriendAutoAcceptPassResult { Success = false, Message = err };
                }

                // Step 3: Expand「新的朋友」and process pending requests one by one
                var keywords = ParseKeywords(cfg.AutoAcceptKeywordFilter);
                var processedThisPass = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                const int maxPerPass = 10;

                for (int iter = 0; iter < maxPerPass; iter++)
                {
                    if (Win32WindowHelper.IsEscapePressed())
                    {
                        return new FriendAutoAcceptPassResult
                        {
                            Success = acceptedCount > 0,
                            CancelledByEsc = true,
                            AcceptedCount = acceptedCount,
                            SkippedCount = skippedCount,
                            Message = $"检测到 ESC，已中止（本次已同意 {acceptedCount} 位）。"
                        };
                    }

                    if (Win32WindowHelper.GetWindowRect(hwnd, out var latestRect) && latestRect.Width >= 360)
                    {
                        rawRect = latestRect;
                    }

                    var leftSnap = await EnsureNewFriendsExpandedAndScanAsync(hwnd, rawRect, scale, ct);
                    if (leftSnap is null) break;

                    var candidates = FindActionableFriendRequests(
                        leftSnap.Value.Ocr,
                        leftSnap.Value.CropRect,
                        scale,
                        includeExpired: cfg.AutoAcceptIncludeExpired);

                    var next = candidates.FirstOrDefault(c =>
                        !processedThisPass.Contains(c.Signature) &&
                        !IsRecentlyAttempted(c.Signature));

                    if (next is null) break;

                    processedThisPass.Add(next.Signature);
                    MarkAttempted(next.Signature);

                    var itemSw = Stopwatch.StartNew();
                    string requesterName = next.RequesterName;
                    string verifyText = next.VerifyPreview;

                    // Optional keyword check (if left preview doesn't match, click row to inspect full detail card)
                    if (keywords.Count > 0 && !MatchesAnyKeyword(requesterName + " " + verifyText, keywords))
                    {
                        Win32WindowHelper.LeftClickAt(next.RowSelectScreenX, next.RowSelectScreenY, hwnd, $"选中好友申请「{requesterName}」查验关键词");
                        await Task.Delay(320, ct);
                        var midSnap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.30, 0.10, 0.68, 0.72, "核验申请验证语", ct);
                        if (midSnap is not null)
                        {
                            verifyText = string.Join(" ", midSnap.Value.Ocr.Lines.Select(l => l.Text.Trim()));
                        }
                        if (!MatchesAnyKeyword(requesterName + " " + verifyText, keywords))
                        {
                            skippedCount++;
                            itemSw.Stop();
                            RecordEntry(new FriendAutoAcceptRecord
                            {
                                RequesterName = requesterName,
                                VerifyMessage = verifyText,
                                Skipped = true,
                                ActionSummary = $"验证语未命中关键词（{cfg.AutoAcceptKeywordFilter}）",
                                ElapsedMs = itemSw.ElapsedMilliseconds
                            });
                            continue;
                        }
                    }

                    // Trigger the「通过朋友验证」popup dialog
                    bool popupOpened = false;
                    (nint PopupHwnd, Win32WindowHelper.RECT PopupRect, OcrRecognitionResult PopupOcr)? verifyPopup = null;

                    if (!next.IsExpiredRow && !next.ButtonLabel.Equals("查看", StringComparison.Ordinal))
                    {
                        // Standard pending item: click right-side「接受」/「通过验证」directly
                        Win32WindowHelper.LeftClickAt(next.ButtonScreenX, next.ButtonScreenY, hwnd, $"点击好友申请「{next.ButtonLabel}」({requesterName})");
                        await Task.Delay(480, ct);
                        verifyPopup = await WaitForVerifyPopupAsync(hwnd, pid, rawRect, timeoutMs: 1400, ct);
                        popupOpened = verifyPopup is not null;
                    }

                    // Fallback /「前往验证」flow (exact match to WeChat 4.x middle detail panel in user screenshot!)
                    if (!popupOpened)
                    {
                        Win32WindowHelper.LeftClickAt(next.RowSelectScreenX, next.RowSelectScreenY, hwnd, $"点选「{requesterName}」进入验证详情");
                        await Task.Delay(340, ct);

                        var midSnap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.30, 0.12, 0.68, 0.76, "识别中间详情页前往验证按钮", ct);
                        if (midSnap is not null)
                        {
                            var verifyBtn = LocateMiddlePaneActionButton(midSnap.Value.Ocr);
                            if (verifyBtn is not null)
                            {
                                var (vx, vy) = ImageCenterToScreen(midSnap.Value.CropRect, midSnap.Value.Ocr, verifyBtn.Bounds);
                                Win32WindowHelper.LeftClickAt(vx, vy, hwnd, $"点击详情页「{verifyBtn.Text.Trim()}」");
                                await Task.Delay(500, ct);
                                verifyPopup = await WaitForVerifyPopupAsync(hwnd, pid, rawRect, timeoutMs: 1600, ct);
                                popupOpened = verifyPopup is not null;
                            }
                        }
                    }

                    if (!popupOpened || verifyPopup is null)
                    {
                        itemSw.Stop();
                        RecordEntry(new FriendAutoAcceptRecord
                        {
                            RequesterName = requesterName,
                            VerifyMessage = verifyText,
                            Succeeded = false,
                            ActionSummary = "未检测到「通过朋友验证」弹窗",
                            ElapsedMs = itemSw.ElapsedMilliseconds
                        });
                        continue;
                    }

                    // Step 4: Fill optional remark/tag/permission and click green「确定」inside「通过朋友验证」popup
                    var (confirmed, appliedNote, usedClipboard) = await CompleteVerifyPopupAsync(
                        verifyPopup.Value.PopupHwnd,
                        verifyPopup.Value.PopupRect,
                        verifyPopup.Value.PopupOcr,
                        hwnd,
                        scale,
                        cfg,
                        ct);
                    if (usedClipboard) touchedClipboard = true;

                    itemSw.Stop();
                    if (confirmed)
                    {
                        acceptedCount++;
                        cfg.AutoAcceptTotalAcceptedCount++;
                        AppServices.Settings.Save();
                        RecordEntry(new FriendAutoAcceptRecord
                        {
                            RequesterName = requesterName,
                            VerifyMessage = verifyText,
                            Succeeded = true,
                            ActionSummary = string.IsNullOrWhiteSpace(appliedNote) ? "已点击确定通过验证" : appliedNote,
                            ElapsedMs = itemSw.ElapsedMilliseconds
                        });
                        await Task.Delay(450, ct);
                    }
                    else
                    {
                        RecordEntry(new FriendAutoAcceptRecord
                        {
                            RequesterName = requesterName,
                            VerifyMessage = verifyText,
                            Succeeded = false,
                            ActionSummary = appliedNote,
                            ElapsedMs = itemSw.ElapsedMilliseconds
                        });
                        // Dismiss stuck popup safely with ESC
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        await Task.Delay(250, ct);
                    }
                }

                // Step 5: Optionally return WeChat back to「聊天」tab
                if (cfg.AutoAcceptReturnToChatTab && railFound && !Win32WindowHelper.IsEscapePressed())
                {
                    Win32WindowHelper.LeftClickAt(chatIconScreenX, chatIconScreenY, hwnd, "切回微信「聊天」页签");
                    await Task.Delay(180, ct);
                }

                LastCheckedAt = DateTimeOffset.Now;
                string summary = acceptedCount > 0
                    ? $"本次自动同意了 {acceptedCount} 位微信好友申请" + (skippedCount > 0 ? $"（跳过 {skippedCount} 位）" : string.Empty)
                    : (skippedCount > 0
                        ? $"检测到 {skippedCount} 条申请但均未命中关键词，已跳过"
                        : "已检查「新的朋友」，当前暂无待处理的微信好友申请");

                if (acceptedCount > 0)
                {
                    _ = AppServices.WeChat4Sync.DetectAndSyncCurrentWeChatAccountAsync(forceReload: false);
                }

                return new FriendAutoAcceptPassResult
                {
                    Success = true,
                    AcceptedCount = acceptedCount,
                    SkippedCount = skippedCount,
                    Message = summary
                };
            }
            catch (OperationCanceledException)
            {
                return new FriendAutoAcceptPassResult { Success = false, CancelledByEsc = true, Message = "好友自动同意已取消。" };
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("FRIEND", $"自动同意好友执行异常：{ex.Message}");
                RecordEntry(new FriendAutoAcceptRecord { Succeeded = false, ActionSummary = $"执行异常：{ex.Message}" });
                return new FriendAutoAcceptPassResult { Success = false, Message = $"执行异常：{ex.Message}" };
            }
            finally
            {
                ClickVisualOverlay.IsEnabled = prevOverlay;
                if (touchedClipboard && savedClipboard is not null)
                {
                    try { _ = Win32WindowHelper.SetClipboardUnicodeText(savedClipboard); } catch { }
                }
                _isExecuting = false;
                UpdateStatus(cfg.AutoAcceptFriendEnabled ? BuildIdleStatusLabel() : "自动同意好友：未开启");
                _execGate.Release();
            }
        }

        private static (bool Found, int ChatX, int ChatY, int ContactsX, int ContactsY) DetectLeftRailTargets(
            nint hwnd, Win32WindowHelper.RECT rawRect, double scale)
        {
            try
            {
                int railW = Math.Clamp((int)Math.Ceiling(140 * scale), 125, Math.Min(240, rawRect.Width));
                int railH = Math.Min(rawRect.Height, Math.Max(260, (int)Math.Ceiling(440 * scale)));
                using var bmp = Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(rawRect.Left, rawRect.Top, railW, railH);
                var icons = Win32WindowHelper.DetectWeChatLeftRailIcons(bmp, scale);
                if (icons.Count >= 3)
                {
                    // Run 0 = Avatar, Run 1 = Chat, Run 2 = Contacts
                    var chat = icons[1];
                    var contacts = icons[2];
                    return (true,
                        rawRect.Left + chat.CenterX,
                        rawRect.Top + chat.CenterY,
                        rawRect.Left + contacts.CenterX,
                        rawRect.Top + contacts.CenterY);
                }
            }
            catch { }

            // Reliable WeChat 4.x DPI-scaled fallback coordinates
            int rx = rawRect.Left + (int)Math.Round(28 * scale);
            return (true,
                rx,
                rawRect.Top + (int)Math.Round(104 * scale),
                rx,
                rawRect.Top + (int)Math.Round(154 * scale));
        }

        private async Task<bool> EnsureOnContactsTabAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double scale,
            int contactsX,
            int contactsY,
            CancellationToken ct)
        {
            var pre = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.04, 0.02, 0.36, 0.44, "检测当前是否在通讯录", ct);
            if (pre is not null && IsContactsPaneVisible(pre.Value.Ocr))
            {
                return true;
            }

            // Try detected Contacts icon first, then WeChat 4.x / 3.x DPI-scaled vertical fallbacks
            int railX = rawRect.Left + (int)Math.Round(28 * scale);
            (int X, int Y)[] attempts =
            [
                (contactsX, contactsY),
                (railX, rawRect.Top + (int)Math.Round(154 * scale)),
                (railX, rawRect.Top + (int)Math.Round(184 * scale)),
                (railX, rawRect.Top + (int)Math.Round(134 * scale))
            ];

            foreach (var (cx, cy) in attempts)
            {
                if (Win32WindowHelper.IsEscapePressed()) return false;
                Win32WindowHelper.LeftClickAt(cx, cy, hwnd, "点击微信左侧「通讯录」图标");
                await Task.Delay(340, ct);
                var check = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.04, 0.02, 0.36, 0.44, "核验已切入通讯录", ct);
                if (check is not null && IsContactsPaneVisible(check.Value.Ocr))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsContactsPaneVisible(OcrRecognitionResult ocr)
        {
            return ocr.Lines.Any(l =>
                l.Text.Contains("通讯录管理", StringComparison.Ordinal) ||
                l.Text.Contains("新的朋友", StringComparison.Ordinal));
        }

        private async Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> EnsureNewFriendsExpandedAndScanAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double scale,
            CancellationToken ct)
        {
            var snap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.04, 0.04, 0.35, 0.94, "扫描通讯录新的朋友列表", ct);
            if (snap is null) return null;

            var newFriendLine = FindNewFriendsHeaderLine(snap.Value.Ocr);
            if (newFriendLine is null)
            {
                // Scroll left pane to top and re-scan
                int scrollX = rawRect.Left + (int)Math.Round(165 * scale);
                int scrollY = rawRect.Top + (int)Math.Round(240 * scale);
                Win32WindowHelper.ScrollWheelAt(scrollX, scrollY, 8);
                await Task.Delay(260, ct);
                snap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.04, 0.04, 0.35, 0.94, "回滚顶部后扫描新的朋友", ct);
                if (snap is null) return null;
                newFriendLine = FindNewFriendsHeaderLine(snap.Value.Ocr);
                if (newFriendLine is null) return snap;
            }

            // Determine whether「新的朋友」is currently collapsed
            double nfBottom = newFriendLine.Bounds.Y + newFriendLine.Bounds.Height;
            var nextHeader = snap.Value.Ocr.Lines
                .Where(l => l.Bounds.Y > nfBottom + 4 && NextSectionHeaders.Any(h => l.Text.Trim().StartsWith(h, StringComparison.Ordinal)))
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault();

            bool hasRowsUnderNewFriends = snap.Value.Ocr.Lines.Any(l =>
                l.Bounds.Y > nfBottom + 6 &&
                (nextHeader is null || l.Bounds.Y < nextHeader.Bounds.Y - 4) &&
                (l.Text.Contains("接受", StringComparison.Ordinal) ||
                 l.Text.Contains("已添加", StringComparison.Ordinal) ||
                 l.Text.Contains("已过期", StringComparison.Ordinal) ||
                 l.Text.Contains("等待验证", StringComparison.Ordinal) ||
                 l.Text.Contains("通过验证", StringComparison.Ordinal) ||
                 l.Text.Contains("查看", StringComparison.Ordinal)));

            if (!hasRowsUnderNewFriends)
            {
                var (nfx, nfy) = ImageCenterToScreen(snap.Value.CropRect, snap.Value.Ocr, newFriendLine.Bounds);
                Win32WindowHelper.LeftClickAt(nfx, nfy, hwnd, "展开「新的朋友」列表");
                await Task.Delay(340, ct);
                snap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.04, 0.04, 0.35, 0.94, "展开后扫描新的朋友", ct);
            }

            return snap;
        }

        private static OcrLineResult? FindNewFriendsHeaderLine(OcrRecognitionResult ocr) =>
            ocr.Lines
                .Where(l => l.Text.Contains("新的朋友", StringComparison.Ordinal))
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault();

        private sealed record FriendRequestCandidate(
            string Signature,
            string RequesterName,
            string VerifyPreview,
            string ButtonLabel,
            bool IsExpiredRow,
            int ButtonScreenX,
            int ButtonScreenY,
            int RowSelectScreenX,
            int RowSelectScreenY);

        private static List<FriendRequestCandidate> FindActionableFriendRequests(
            OcrRecognitionResult ocr,
            Win32WindowHelper.RECT cropRect,
            double scale,
            bool includeExpired)
        {
            var results = new List<FriendRequestCandidate>();
            var nfHeader = FindNewFriendsHeaderLine(ocr);
            double bandTop = nfHeader is not null ? nfHeader.Bounds.Y + nfHeader.Bounds.Height + 4 : ocr.ImageHeight * 0.14;

            var nextHeader = ocr.Lines
                .Where(l => l.Bounds.Y > bandTop + 12 && NextSectionHeaders.Any(h => l.Text.Trim().StartsWith(h, StringComparison.Ordinal)))
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault();
            double bandBottom = nextHeader is not null ? nextHeader.Bounds.Y - 4 : ocr.ImageHeight * 0.96;

            double rightMinX = ocr.ImageWidth * 0.54;

            var statusLines = ocr.Lines
                .Where(l =>
                    l.Bounds.Y >= bandTop &&
                    l.Bounds.Y <= bandBottom &&
                    l.Bounds.X >= rightMinX)
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            foreach (var st in statusLines)
            {
                string label = st.Text.Trim();
                bool isPending = PendingButtonLabels.Any(p => label.Equals(p, StringComparison.Ordinal) || label.Contains(p, StringComparison.Ordinal));
                bool isExpired = label.Equals("已过期", StringComparison.Ordinal);

                if (!isPending && !(includeExpired && isExpired)) continue;

                double btnCy = st.Bounds.Y + st.Bounds.Height * 0.5;
                double rowHalfH = Math.Max(34, 34 * scale);

                // Gather requester name & verification preview on the left side of the same row
                var leftRowLines = ocr.Lines
                    .Where(l =>
                        l.Bounds.X < rightMinX &&
                        Math.Abs((l.Bounds.Y + l.Bounds.Height * 0.5) - btnCy) <= rowHalfH &&
                        l.Bounds.Y >= bandTop - 2)
                    .OrderBy(l => l.Bounds.Y)
                    .ToList();

                string name = leftRowLines.Count > 0 ? leftRowLines[0].Text.Trim() : "微信新好友";
                string preview = leftRowLines.Count > 1
                    ? string.Join(" ", leftRowLines.Skip(1).Select(x => x.Text.Trim()))
                    : string.Empty;

                var (btnSx, btnSy) = ImageCenterToScreen(cropRect, ocr, st.Bounds);
                int rowSelectX = cropRect.Left + (int)Math.Round(cropRect.Width * 0.36);
                int rowSelectY = btnSy;

                string sig = $"{name}|{preview}|{label}";
                results.Add(new FriendRequestCandidate(
                    sig, name, preview, label, isExpired, btnSx, btnSy, rowSelectX, rowSelectY));
            }

            return results;
        }

        private static OcrLineResult? LocateMiddlePaneActionButton(OcrRecognitionResult midOcr)
        {
            string[] targets = ["前往验证", "通过验证", "接受", "同意"];
            return midOcr.Lines
                .Where(l =>
                {
                    string t = l.Text.Trim();
                    return targets.Any(k => t.Equals(k, StringComparison.Ordinal) || t.Contains(k, StringComparison.Ordinal));
                })
                .OrderByDescending(l => l.Bounds.Y)
                .FirstOrDefault();
        }

        private async Task<(nint PopupHwnd, Win32WindowHelper.RECT PopupRect, OcrRecognitionResult PopupOcr)?> WaitForVerifyPopupAsync(
            nint mainHwnd,
            int mainPid,
            Win32WindowHelper.RECT mainRect,
            int timeoutMs,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (Win32WindowHelper.IsEscapePressed()) return null;

                // 1. Check separate floating top-level popup windows belonging to Weixin.exe / WeChat.exe
                // (In WeChat 4.x, 「通过朋友验证」is an independent floating window that can overhang outside mainRect!)
                var popups = Win32WindowHelper.EnumVisibleTopLevelWindowsForPid(mainPid, excludeHwnd: mainHwnd);
                foreach (var p in popups)
                {
                    if (p.Rect.Width < 200 || p.Rect.Height < 260) continue;
                    try
                    {
                        using var bmp = await Task.Run(() =>
                            Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(p.Rect.Left, p.Rect.Top, p.Rect.Width, p.Rect.Height), ct);
                        var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
                        if (LooksLikeVerifyFriendPopup(ocr))
                        {
                            AppServices.Logs.Info("OCR", $"已识别浮动「通过朋友验证」弹窗：{p.ClassName} ({p.Rect.Width}x{p.Rect.Height})");
                            return (p.Hwnd, p.Rect, ocr);
                        }
                    }
                    catch { }
                }

                // 2. Fallback: capture expanded area around main window (including right overhang)
                var desktop = Win32WindowHelper.GetVirtualDesktopRect();
                int left = Math.Max(desktop.Left, mainRect.Left + (int)(mainRect.Width * 0.25));
                int top = Math.Max(desktop.Top, mainRect.Top - 40);
                int right = Math.Min(desktop.Right, mainRect.Right + 460);
                int bottom = Math.Min(desktop.Bottom, mainRect.Bottom + 80);
                if (right - left >= 240 && bottom - top >= 260)
                {
                    var expandedRect = new Win32WindowHelper.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
                    try
                    {
                        using var bmp = await Task.Run(() =>
                            Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(left, top, right - left, bottom - top), ct);
                        var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
                        if (LooksLikeVerifyFriendPopup(ocr))
                        {
                            AppServices.Logs.Info("OCR", "已在扩展桌面区域识别到「通过朋友验证」弹窗。");
                            return (nint.Zero, expandedRect, ocr);
                        }
                    }
                    catch { }
                }

                await Task.Delay(240, ct);
            }

            return null;
        }

        private static bool LooksLikeVerifyFriendPopup(OcrRecognitionResult ocr)
        {
            bool hasTitle = ocr.Lines.Any(l =>
                l.Text.Contains("通过朋友验证", StringComparison.Ordinal) ||
                l.Text.Contains("朋友权限", StringComparison.Ordinal));
            bool hasConfirm = ocr.Lines.Any(l => l.Text.Trim().Equals("确定", StringComparison.Ordinal));
            bool hasCancel = ocr.Lines.Any(l => l.Text.Trim().Equals("取消", StringComparison.Ordinal));
            return (hasTitle && hasConfirm) || (hasConfirm && hasCancel && ocr.Lines.Any(l => l.Text.Contains("备注", StringComparison.Ordinal)));
        }

        private async Task<(bool Confirmed, string Note, bool TouchedClipboard)> CompleteVerifyPopupAsync(
            nint popupHwnd,
            Win32WindowHelper.RECT popupRect,
            OcrRecognitionResult popupOcr,
            nint mainHwnd,
            double scale,
            AppSettingsData cfg,
            CancellationToken ct)
        {
            bool touchedClipboard = false;
            var actions = new List<string>(4);
            nint clickTargetHwnd = popupHwnd != nint.Zero ? popupHwnd : mainHwnd;

            // Optional 1: Prepend Remark prefix into「备注」textbox
            if (!string.IsNullOrWhiteSpace(cfg.AutoAcceptRemarkPrefix))
            {
                var remarkLabel = popupOcr.Lines
                    .Where(l => l.Text.Trim().Equals("备注", StringComparison.Ordinal) && l.Bounds.Y < popupOcr.ImageHeight * 0.40)
                    .OrderBy(l => l.Bounds.Y)
                    .FirstOrDefault();

                if (remarkLabel is not null)
                {
                    var (_, labelBottomY) = ImageToScreen(popupRect, popupOcr, remarkLabel.Bounds.X, remarkLabel.Bounds.Y + remarkLabel.Bounds.Height);
                    int inputX = popupRect.Left + (int)Math.Round(popupRect.Width * 0.42);
                    int inputY = labelBottomY + (int)Math.Round(24 * scale);

                    Win32WindowHelper.LeftClickAt(inputX, inputY, clickTargetHwnd, "点击「通过朋友验证」备注框", skipReactivate: true);
                    await Task.Delay(110, ct);
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_HOME);
                    await Task.Delay(60, ct);
                    if (Win32WindowHelper.SetClipboardUnicodeText(cfg.AutoAcceptRemarkPrefix))
                    {
                        touchedClipboard = true;
                        Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                        await Task.Delay(130, ct);
                        actions.Add($"备注前缀「{cfg.AutoAcceptRemarkPrefix}」");
                    }
                }
            }

            // Optional 2: Add Tag into「标签」(搜索或创建标签...)
            if (!string.IsNullOrWhiteSpace(cfg.AutoAcceptAutoTag))
            {
                var tagPlaceholder = popupOcr.Lines
                    .FirstOrDefault(l => l.Text.Contains("搜索或创建标签", StringComparison.Ordinal));
                int tagX = 0, tagY = 0;
                if (tagPlaceholder is not null)
                {
                    (tagX, tagY) = ImageCenterToScreen(popupRect, popupOcr, tagPlaceholder.Bounds);
                }
                else
                {
                    var tagLabel = popupOcr.Lines
                        .Where(l => l.Text.Trim().Equals("标签", StringComparison.Ordinal) && l.Bounds.Y < popupOcr.ImageHeight * 0.55)
                        .OrderBy(l => l.Bounds.Y)
                        .FirstOrDefault();
                    if (tagLabel is not null)
                    {
                        var (_, bottomY) = ImageToScreen(popupRect, popupOcr, tagLabel.Bounds.X, tagLabel.Bounds.Y + tagLabel.Bounds.Height);
                        tagX = popupRect.Left + (int)Math.Round(popupRect.Width * 0.42);
                        tagY = bottomY + (int)Math.Round(24 * scale);
                    }
                }

                if (tagX > 0 && tagY > 0)
                {
                    Win32WindowHelper.LeftClickAt(tagX, tagY, clickTargetHwnd, "点击「通过朋友验证」标签框", skipReactivate: true);
                    await Task.Delay(130, ct);
                    if (Win32WindowHelper.SetClipboardUnicodeText(cfg.AutoAcceptAutoTag.Trim()))
                    {
                        touchedClipboard = true;
                        Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                        await Task.Delay(220, ct);
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                        await Task.Delay(180, ct);
                        actions.Add($"标签「{cfg.AutoAcceptAutoTag.Trim()}」");
                    }
                }
            }

            // Optional 3: Set friend permission to「仅聊天」
            if (cfg.AutoAcceptChatOnlyPermission)
            {
                var chatOnlyLine = popupOcr.Lines
                    .FirstOrDefault(l => l.Text.Trim().Equals("仅聊天", StringComparison.Ordinal) && l.Bounds.Y >= popupOcr.ImageHeight * 0.35);
                if (chatOnlyLine is not null)
                {
                    var (cx, cy) = ImageCenterToScreen(popupRect, popupOcr, chatOnlyLine.Bounds);
                    Win32WindowHelper.LeftClickAt(cx, cy, clickTargetHwnd, "选择权限「仅聊天」", skipReactivate: true);
                    await Task.Delay(140, ct);
                    actions.Add("权限:仅聊天");
                }
            }

            if (Win32WindowHelper.IsEscapePressed())
            {
                return (false, "按 ESC 中止确认", touchedClipboard);
            }

            // Locate green「确定」button in lower band of popup (with「取消」to its right)
            var confirmBtn = popupOcr.Lines
                .Where(l =>
                    l.Text.Trim().Equals("确定", StringComparison.Ordinal) &&
                    l.Bounds.Y >= popupOcr.ImageHeight * 0.72)
                .OrderByDescending(l => l.Bounds.Y)
                .FirstOrDefault();

            if (confirmBtn is null)
            {
                return (false, "未在「通过朋友验证」弹窗底部识别到「确定」按钮", touchedClipboard);
            }

            var (okX, okY) = ImageCenterToScreen(popupRect, popupOcr, confirmBtn.Bounds);
            Win32WindowHelper.LeftClickAt(okX, okY, clickTargetHwnd, "点击「通过朋友验证 -> 确定」", skipReactivate: true);
            await Task.Delay(520, ct);

            // Verify popup actually closed; if still open, retry click once
            bool stillOpen = popupHwnd != nint.Zero && Win32WindowHelper.IsWindow(popupHwnd) && Win32WindowHelper.IsWindowVisible(popupHwnd);
            if (stillOpen)
            {
                Win32WindowHelper.LeftClickAt(okX + (int)Math.Round(6 * scale), okY, clickTargetHwnd, "二次点击「通过朋友验证 -> 确定」", skipReactivate: true);
                await Task.Delay(520, ct);
                stillOpen = popupHwnd != nint.Zero && Win32WindowHelper.IsWindow(popupHwnd) && Win32WindowHelper.IsWindowVisible(popupHwnd);
            }

            if (stillOpen)
            {
                return (false, "点击「确定」后验证弹窗未关闭，已安全跳过", touchedClipboard);
            }

            actions.Add("已点击确定");
            return (true, string.Join(" · ", actions), touchedClipboard);
        }

        private static async Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureCropAndOcrAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double fracX,
            double fracY,
            double fracW,
            double fracH,
            string stageLabel,
            CancellationToken ct)
        {
            ClickVisualOverlay.HideImmediately();
            await Task.Delay(40, ct);

            if (Win32WindowHelper.GetWindowRect(hwnd, out var curRect) && curRect.Width >= 300 && curRect.Height >= 250)
            {
                rawRect = curRect;
            }

            int left = rawRect.Left + (int)Math.Floor(rawRect.Width * fracX);
            int top = rawRect.Top + (int)Math.Floor(rawRect.Height * fracY);
            int width = Math.Max(64, (int)Math.Ceiling(rawRect.Width * fracW));
            int height = Math.Max(48, (int)Math.Ceiling(rawRect.Height * fracH));
            if (left + width > rawRect.Right) width = Math.Max(64, rawRect.Right - left);
            if (top + height > rawRect.Bottom) height = Math.Max(48, rawRect.Bottom - top);

            var cropRect = new Win32WindowHelper.RECT
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };

            using var bmp = await Task.Run(
                () => Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(left, top, width, height),
                ct);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
            AppServices.Logs.Info("OCR", $"[好友验证-{stageLabel}] {ocr.ImageWidth}x{ocr.ImageHeight}, {ocr.Lines.Count}行, {ocr.ElapsedMilliseconds}ms");
            return (ocr, cropRect);
        }

        private static (int ScreenX, int ScreenY) ImageToScreen(
            Win32WindowHelper.RECT cropRect, OcrRecognitionResult ocr, double imgX, double imgY)
        {
            double sxRatio = ocr.ImageWidth > 0 ? (double)cropRect.Width / ocr.ImageWidth : 1.0;
            double syRatio = ocr.ImageHeight > 0 ? (double)cropRect.Height / ocr.ImageHeight : 1.0;
            return (
                cropRect.Left + (int)Math.Round(imgX * sxRatio),
                cropRect.Top + (int)Math.Round(imgY * syRatio));
        }

        private static (int ScreenX, int ScreenY) ImageCenterToScreen(
            Win32WindowHelper.RECT cropRect, OcrRecognitionResult ocr, PixelRect bounds) =>
            ImageToScreen(cropRect, ocr, bounds.X + bounds.Width * 0.5, bounds.Y + bounds.Height * 0.5);

        private static List<string> ParseKeywords(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return raw.Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool MatchesAnyKeyword(string haystack, List<string> keywords)
        {
            if (keywords.Count == 0) return true;
            if (string.IsNullOrWhiteSpace(haystack)) return false;
            return keywords.Any(k => haystack.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsRecentlyAttempted(string sig)
        {
            lock (_stateLock)
            {
                if (_recentAttemptedKeys.TryGetValue(sig, out var at))
                {
                    if ((DateTimeOffset.UtcNow - at).TotalMinutes < 10) return true;
                    _recentAttemptedKeys.Remove(sig);
                }
                return false;
            }
        }

        private void MarkAttempted(string sig)
        {
            lock (_stateLock)
            {
                _recentAttemptedKeys[sig] = DateTimeOffset.UtcNow;
                if (_recentAttemptedKeys.Count > 200)
                {
                    var cutoff = DateTimeOffset.UtcNow.AddMinutes(-15);
                    foreach (var old in _recentAttemptedKeys.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                    {
                        _recentAttemptedKeys.Remove(old);
                    }
                }
            }
        }

        private static string ComputeWeChatDbWriteFingerprint()
        {
            try
            {
                string dbDir = AppServices.WeChat4Sync.WatchedDbStoragePath;
                if (string.IsNullOrWhiteSpace(dbDir) || !Directory.Exists(dbDir))
                {
                    var acc = WeChat4Paths.ListAccountDirs(AppServices.Settings.Current.WeChatSyncAccountDir).FirstOrDefault();
                    dbDir = acc?.DbStoragePath ?? string.Empty;
                }
                if (string.IsNullOrWhiteSpace(dbDir) || !Directory.Exists(dbDir)) return string.Empty;

                long latestTicks = 0;
                long totalSize = 0;
                foreach (var sub in new[] { "contact", "general", "session" })
                {
                    string subPath = Path.Combine(dbDir, sub);
                    if (!Directory.Exists(subPath)) continue;
                    foreach (var f in Directory.EnumerateFiles(subPath, "*.db*"))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (fi.LastWriteTimeUtc.Ticks > latestTicks) latestTicks = fi.LastWriteTimeUtc.Ticks;
                            totalSize += fi.Length;
                        }
                        catch { }
                    }
                }
                return $"{latestTicks}:{totalSize}";
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RecordEntry(FriendAutoAcceptRecord rec)
        {
            lock (_stateLock)
            {
                _records.AddFirst(rec);
                while (_records.Count > 60) _records.RemoveLast();
            }
            AppServices.Logs.Info("FRIEND", rec.FormattedLine);
            RecordAdded?.Invoke(rec);
            StateChanged?.Invoke();
        }

        private void UpdateStatus(string status)
        {
            StatusText = status;
            StateChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            StopWatching();
            _execGate.Dispose();
        }
    }
}
