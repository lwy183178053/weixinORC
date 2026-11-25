using System.Text.RegularExpressions;
using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class WeChatTagItem
    {
        public string Name { get; set; } = string.Empty;
        public int Count { get; set; }
        public double RelativeX { get; set; }
        public double RelativeY { get; set; }
        public bool IsSelected { get; set; }

        public string DisplayLabel => Count >= 0 ? $"{Name} ({Count})" : Name;
    }

    public sealed class BatchTagImportProgress
    {
        public string CurrentTag { get; init; } = string.Empty;
        public string StatusMessage { get; init; } = string.Empty;
        public int Processed { get; init; }
        public int ExpectedTotal { get; init; }
        public int CreatedCount { get; init; }
        public int UpdatedCount { get; init; }
        public int SkippedCount { get; init; }
        public int FailedCount { get; init; }
        public string? LogLine { get; init; }
    }

    public sealed class BatchTagImportSummary
    {
        public bool WasCancelled { get; init; }
        public string StopReason { get; init; } = string.Empty;
        public int CreatedCount { get; init; }
        public int UpdatedCount { get; init; }
        public int SkippedCount { get; init; }
        public int FailedCount { get; init; }
    }

    /// <summary>
    /// 微信「通讯录管理」按标签批量导入引擎（清晰四步循环）：
    /// 1. 归位重置：关闭旧「通讯录管理」→ 规范微信主窗口(900×656) → 点通讯录 → 列表上滚归顶 → 打开并规范「通讯录管理」(900×666) → 扫描左侧标签；
    /// 2. 切到目标标签并把右侧列表滚回最顶；
    /// 3. 每屏先做一次整页 OCR 读取「昵称列」：用标准化昵称清单区分「已点过」与「待点击」，同时校准滚动步长并严格对齐每行左侧头像坐标；
    /// 4. 仅点击未处理行的头像 → 抓资料卡 OCR + 头像裁剪（含白底头像几何兜底）→ 点击标题栏空白处关闭资料卡 → 按唯一微信号去重入库并计数。
    /// </summary>
    public sealed partial class WeChatTagBatchImporter
    {
        private const int StandardMainLogicalWidth = 900;
        private const int StandardMainLogicalHeight = 656;
        private const int StandardMgrLogicalWidth = 900;
        private const int StandardMgrLogicalHeight = 666;

        private Win32WindowHelper.POINT _lastAutoCursor;
        private bool _hasAutoCursor;

        public sealed record TagScanResult(
            nint ContactManagerHwnd,
            int ProcessId,
            List<WeChatTagItem> Tags,
            string Message);

        private sealed record TableRowEntry(int CenterY, string RawNickname, string NormalizedKey)
        {
            public bool HasNameKey => !string.IsNullOrEmpty(NormalizedKey);
        }

        private sealed record TablePageSnapshot(int AvatarClickRelX, List<TableRowEntry> Rows);

        // =====================================================================
        // Step 1: Locate / Clean-Reset Contact Manager & Scan Left Sidebar Tags
        // =====================================================================

        public async Task<TagScanResult> LocateAndScanTagsAsync(
            bool scrollLeftSidebarDown = false,
            IProgress<string>? scanLog = null,
            CancellationToken ct = default)
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                throw new InvalidOperationException(AppServices.Ocr.RapidEngine.AvailabilityNote);
            }

            var (mgrHwnd, pid) = await FindOrOpenContactManagerAsync(resetExisting: !scrollLeftSidebarDown, scanLog, ct);
            if (mgrHwnd == nint.Zero)
            {
                throw new InvalidOperationException("未找到微信「通讯录管理」窗口。请确认个人微信已登录并处于桌面可见状态后再试。");
            }

            NormalizeWindowSize(mgrHwnd, StandardMgrLogicalWidth, StandardMgrLogicalHeight, offsetX: 60, offsetY: 50);
            await Task.Delay(480, ct);

            if (!Win32WindowHelper.GetVisibleWindowRect(mgrHwnd, out var rect))
            {
                throw new InvalidOperationException("无法获取「通讯录管理」窗口坐标。");
            }

            void LogScan(string text)
            {
                AppServices.Logs.Info("TAG", text);
                scanLog?.Report(text);
            }

            if (scrollLeftSidebarDown)
            {
                int leftX = rect.Left + (int)(rect.Width * 0.11);
                int leftY = rect.Top + (int)(rect.Height * 0.55);
                Win32WindowHelper.ScrollWheelAt(leftX, leftY, -5);
                await Task.Delay(420, ct);
            }

            // Adaptive readiness wait for slower/new devices: wait up to ~2.4s until Contact Manager paints its sidebar
            var scan = await ScanTagsFromManagerWindowDetailedAsync(mgrHwnd, ct);
            for (int waitReady = 0; waitReady < 5 && !scrollLeftSidebarDown && scan.SidebarLines.Count < 4; waitReady++)
            {
                LogScan($"「通讯录管理」界面仍在加载中（当前 {scan.SidebarLines.Count} 行），等待加载完成...");
                await Task.Delay(380, ct);
                scan = await ScanTagsFromManagerWindowDetailedAsync(mgrHwnd, ct);
            }
            LogScan($"左侧栏扫描（{scan.SidebarWidth}×{scan.WindowHeight}，{scan.SidebarLines.Count} 行）：[{string.Join(" | ", scan.SidebarLines.Take(16))}]");

            // Only click "标签" to expand when the section is genuinely collapsed (neither tags nor "无标签"/"新建标签" appeared!)
            if (!scrollLeftSidebarDown && scan.Tags.Count == 0 && !scan.IsTagSectionExpanded)
            {
                LogScan("检测到左侧「标签」尚未展开，启动多点位自适应点击展开（文字中心 -> 右侧箭头 -> 偏左/微调）...");

                if (scan.TagHeaderScreenCenter is null)
                {
                    LogScan("未在左侧栏识别到「标签」标题行，无法自动点击展开。");
                }
                else
                {
                    uint mgrDpi = Win32WindowHelper.GetDpiForWindow(mgrHwnd);
                    double mgrScale = mgrDpi > 0 ? mgrDpi / 96.0 : 1.0;
                    int prevLineCount = scan.SidebarLines.Count;

                    // Adaptive exploration across the full "标签" row: text center -> right-side '>' chevron -> left side -> slight vertical nudge
                    var expandOffsets = new (int Dx, int Dy)[]
                    {
                        (0, 0),     // exact OCR center of "标签"
                        (58, 0),    // right-side expand chevron '>'
                        (-16, 0),   // left side of "标签"
                        (26, 3),    // mid-right slight nudge
                        (0, -5)     // slight upward nudge
                    };

                    var expandRes = await Win32WindowHelper.ClickWithAdaptiveRetryAsync(
                        mgrHwnd,
                        scan.TagHeaderScreenCenter.Value.X,
                        scan.TagHeaderScreenCenter.Value.Y,
                        mgrScale,
                        actionLabel: $"展开「{scan.TagHeaderText}」",
                        verifySucceededAsync: async () =>
                        {
                            scan = await ScanTagsFromManagerWindowDetailedAsync(mgrHwnd, ct);
                            LogScan($"展开校验（{scan.SidebarLines.Count} 行）：[{string.Join(" | ", scan.SidebarLines.Take(16))}]");
                            // Stop immediately if expanded OR if sidebar grew (prevent accidental double-toggle collapse!)
                            return scan.Tags.Count > 0 || scan.IsTagSectionExpanded || scan.SidebarLines.Count > prevLineCount;
                        },
                        log: LogScan,
                        waitAfterClickMs: 650,
                        allowPostMessageFallback: true,
                        customOffsetsLogical: expandOffsets,
                        ct: ct);

                    if (expandRes.LastPostMessageError == 5)
                    {
                        throw new InvalidOperationException("Windows 权限隔离 (UIPI/ACCESS_DENIED) 拦截了点击：请以管理员身份运行本工作台或普通权限重启微信。");
                    }
                }
            }

            var tags = scan.Tags;
            string msg = tags.Count > 0
                ? $"已归位打开「通讯录管理」（{StandardMgrLogicalWidth}×{StandardMgrLogicalHeight}），扫描到 {tags.Count} 个标签。"
                : "已定位「通讯录管理」窗口，但左侧未识别到带人数的标签（请确认左侧已展开「标签」或手动补充标签名）。";

            return new TagScanResult(mgrHwnd, pid, tags, msg);
        }

        // =====================================================================
        // Step 2-4: Clean Nickname-Aligned Batch Import Loop
        // =====================================================================

        public async Task<BatchTagImportSummary> RunBatchImportAsync(
            nint contactManagerHwnd,
            int wechatPid,
            List<WeChatTagItem> selectedTags,
            bool overwriteExisting,
            int stepDelayMs,
            IProgress<BatchTagImportProgress> progress,
            CancellationToken ct)
        {
            _hasAutoCursor = false;
            int created = 0, updated = 0, skipped = 0, failed = 0;
            int expectedTotal = selectedTags.Sum(t => Math.Max(0, t.Count));

            void Report(string tag, string status, string? log = null)
            {
                int totalProcessed = created + updated + skipped + failed;
                progress.Report(new BatchTagImportProgress
                {
                    CurrentTag = tag,
                    StatusMessage = status,
                    Processed = totalProcessed,
                    ExpectedTotal = expectedTotal,
                    CreatedCount = created,
                    UpdatedCount = updated,
                    SkippedCount = skipped,
                    FailedCount = failed,
                    LogLine = log
                });
            }

            // Load all existing customers from Go backend for accurate local dedup
            var existingCustomers = await LoadAllExistingCustomersAsync(ct);

            try
            {
                if (!Win32WindowHelper.IsWindow(contactManagerHwnd))
                {
                    return new BatchTagImportSummary
                    {
                        WasCancelled = true,
                        StopReason = "「通讯录管理」窗口已关闭，导入终止。"
                    };
                }

                // Automatically restore & pop up Contact Manager if minimized or behind other windows
                _ = Win32WindowHelper.ForceBringToForeground(contactManagerHwnd);
                NormalizeWindowSize(contactManagerHwnd, StandardMgrLogicalWidth, StandardMgrLogicalHeight, offsetX: 60, offsetY: 50);
                await Task.Delay(350, ct);

                var globalAccountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in selectedTags)
                {
                    EnsureSafeToContinue(contactManagerHwnd, ct);
                    Report(tag.Name, $"正在切换标签「{tag.Name}」...", $"▶ 开始导入标签「{tag.Name}」{(tag.Count > 0 ? $"（预计 {tag.Count} 人）" : "")}");

                    if (!Win32WindowHelper.GetVisibleWindowRect(contactManagerHwnd, out var mgrRect))
                    {
                        throw new OperationCanceledException("无法读取「通讯录管理」窗口位置。");
                    }

                    // 1. Click target tag on left sidebar with adaptive multi-point retry
                    var tagPos = await FindTagClickPositionAsync(contactManagerHwnd, mgrRect, tag, ct);
                    if (tagPos is null)
                    {
                        Report(tag.Name, $"未在左侧栏找到标签「{tag.Name}」，跳过。", $"⚠ 跳过标签「{tag.Name}」：左侧栏未识别到该标签。");
                        continue;
                    }

                    uint tagDpi = Win32WindowHelper.GetDpiForWindow(contactManagerHwnd);
                    double tagScale = tagDpi > 0 ? tagDpi / 96.0 : 1.0;
                    var tagOffsets = new (int Dx, int Dy)[] { (0, 0), (22, 0), (-14, 0) };
                    for (int tTry = 0; tTry < tagOffsets.Length; tTry++)
                    {
                        EnsureSafeToContinue(contactManagerHwnd, ct);
                        int tx = tagPos.Value.X + (int)Math.Round(tagOffsets[tTry].Dx * tagScale);
                        int ty = tagPos.Value.Y + (int)Math.Round(tagOffsets[tTry].Dy * tagScale);
                        var tagDiag = Win32WindowHelper.LeftClickAt(tx, ty, contactManagerHwnd, $"切换标签「{tag.Name}」#{tTry + 1}");
                        _lastAutoCursor = new Win32WindowHelper.POINT { X = tx, Y = ty };
                        _hasAutoCursor = true;
                        await Task.Delay(Math.Max(380, stepDelayMs), ct);
                        if (tagDiag.SendDown == 1 && tagDiag.SendUp == 1 && tagDiag.HitTargetProcess)
                        {
                            break;
                        }
                    }

                    // 2. Scroll right contact table all the way to the top so we always start at row #1
                    int tableMidX = mgrRect.Left + (int)(mgrRect.Width * 0.62);
                    int tableMidY = mgrRect.Top + (int)(mgrRect.Height * 0.56);
                    for (int up = 0; up < 3; up++)
                    {
                        AutoScroll(tableMidX, tableMidY, +12);
                        await Task.Delay(60, ct);
                    }
                    await Task.Delay(180, ct);

                    // Per-tag clean state:
                    // - clickedRowKeys: normalized table nicknames already clicked in this tag (prevents re-clicking overlapped rows!)
                    // - tagAccountKeys: unique contacts actually captured in this tag (exact count, never double-counted!)
                    var clickedRowKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var tagAccountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int scrollNotches = 10;
                    int stalledPages = 0;

                    for (int page = 0; page < 120; page++)
                    {
                        EnsureSafeToContinue(contactManagerHwnd, ct);
                        if (!Win32WindowHelper.GetVisibleWindowRect(contactManagerHwnd, out mgrRect))
                        {
                            throw new OperationCanceledException("「通讯录管理」窗口位置失效。");
                        }

                        // Step A: Screenshot current table page & OCR the Nickname column to get aligned rows
                        var pageSnap = await ScanCurrentTablePageAsync(contactManagerHwnd, ct);
                        if (pageSnap.Rows.Count == 0)
                        {
                            Report(tag.Name, $"标签「{tag.Name}」列表为空。", $"  标签「{tag.Name}」当前无联系人。");
                            break;
                        }

                        // Step B: Partition rows into already-clicked vs pending-to-click using normalized Nickname keys
                        int overlapRows = pageSnap.Rows.Count(r => r.HasNameKey && clickedRowKeys.Contains(r.NormalizedKey));
                        var pendingRows = pageSnap.Rows
                            .Where(r => !r.HasNameKey || !clickedRowKeys.Contains(r.NormalizedKey))
                            .ToList();

                        // Step C: Calibrate scroll alignment on pages > 0
                        if (page > 0 && pageSnap.Rows.Count >= 6)
                        {
                            if (overlapRows == 0 && scrollNotches > 6)
                            {
                                // Scrolled slightly too far (0 overlap with previous page) -> step back 2 notches & re-scan so 0 rows are missed
                                AutoScroll(mgrRect.Left + (int)(mgrRect.Width * 0.62), mgrRect.Top + (int)(mgrRect.Height * 0.56), +2);
                                scrollNotches = Math.Max(6, scrollNotches - 2);
                                await Task.Delay(240, ct);

                                pageSnap = await ScanCurrentTablePageAsync(contactManagerHwnd, ct);
                                overlapRows = pageSnap.Rows.Count(r => r.HasNameKey && clickedRowKeys.Contains(r.NormalizedKey));
                                pendingRows = pageSnap.Rows
                                    .Where(r => !r.HasNameKey || !clickedRowKeys.Contains(r.NormalizedKey))
                                    .ToList();
                            }
                            else if (overlapRows >= Math.Max(4, pageSnap.Rows.Count / 2) && pendingRows.Count > 0)
                            {
                                // Too much overlap -> scroll more next page
                                scrollNotches = Math.Min(22, scrollNotches + 4);
                            }
                        }

                        // If every visible row was already clicked, we reached the bottom
                        if (page > 0 && pendingRows.Count == 0)
                        {
                            stalledPages++;
                            if (stalledPages >= 2)
                            {
                                Report(tag.Name, $"标签「{tag.Name}」已到达底部（共 {tagAccountKeys.Count} 人）。", $"✓ 标签「{tag.Name}」扫描完成（共 {tagAccountKeys.Count} 人）。");
                                break;
                            }
                            AutoScrollSmooth(mgrRect.Left + (int)(mgrRect.Width * 0.62), mgrRect.Top + (int)(mgrRect.Height * 0.56), scrollNotches + 4);
                            await Task.Delay(Math.Max(260, stepDelayMs / 2), ct);
                            continue;
                        }

                        int newlyCapturedThisPage = 0;

                        // Step D: Click ONLY the unclicked rows, aligned strictly to each row's avatar center
                        foreach (var row in pendingRows)
                        {
                            EnsureSafeToContinue(contactManagerHwnd, ct);

                            // Stop immediately if we already collected the exact expected number of contacts in this tag
                            if (tag.Count > 0 && tagAccountKeys.Count >= tag.Count)
                            {
                                break;
                            }

                            // Mark this table row as clicked so it will never be clicked again after scrolling
                            if (row.HasNameKey)
                            {
                                clickedRowKeys.Add(row.NormalizedKey);
                            }

                            int clickX = mgrRect.Left + pageSnap.AvatarClickRelX;
                            int clickY = mgrRect.Top + row.CenterY;

                            var (cardBmp, cardOcr, parsed) = await OpenAndCaptureProfileCardWithRetryAsync(
                                contactManagerHwnd, wechatPid, mgrRect, clickX, clickY, stepDelayMs, ct);
                            using var ownedCardBmp = cardBmp;

                            // Dismiss floating profile card safely by clicking empty top-header space (NEVER ESC!)
                            DismissPopupSafe(contactManagerHwnd, mgrRect);
                            await Task.Delay(Math.Clamp(stepDelayMs / 4, 110, 220), ct);

                            if (parsed.InferredState == WeChatLoginState.Disconnected)
                            {
                                throw new OperationCanceledException("检测到微信掉线/退出提示，已安全熔断停止导入。");
                            }

                            // Resolve nickname (card's 昵称: first; fallback to table nickname if card had emoji-only nickname)
                            string resolvedName = !string.IsNullOrWhiteSpace(parsed.CandidateWechatName)
                                ? parsed.CandidateWechatName
                                : row.RawNickname.TrimEnd('.', '…', ' ');

                            if (string.IsNullOrWhiteSpace(parsed.CandidateWechatAccount) &&
                                string.IsNullOrWhiteSpace(resolvedName) &&
                                string.IsNullOrWhiteSpace(parsed.CandidateWechatRemark))
                            {
                                continue;
                            }

                            // Unique contact identity key (WechatAccount is globally unique!)
                            string accountKey = !string.IsNullOrWhiteSpace(parsed.CandidateWechatAccount)
                                ? $"acc:{parsed.CandidateWechatAccount.Trim()}"
                                : !string.IsNullOrWhiteSpace(resolvedName)
                                    ? $"name:{NormalizeRowKey(resolvedName)}"
                                    : $"rem:{NormalizeRowKey(parsed.CandidateWechatRemark)}";

                            // Strict exact dedup: if already captured in this tag, do NOT count or save twice!
                            if (!tagAccountKeys.Add(accountKey))
                            {
                                continue;
                            }

                            newlyCapturedThisPage++;
                            string displayLabel = DisplayCandidateName(resolvedName, parsed.CandidateWechatAccount, parsed.CandidateWechatRemark);

                            // Cross-tag duplicate check within the same batch run
                            if (!globalAccountKeys.Add(accountKey))
                            {
                                skipped++;
                                Report(tag.Name, $"跨标签去重跳过「{displayLabel}」", $"  ⊘ 跨标签重复跳过：{displayLabel}");
                                continue;
                            }

                            // Extract avatar (with geometric square fallback for white-background avatars!) & gender
                            var (avatarPng, detectedGender) = await AvatarImageService.TryExtractProfileAvatarAndGenderAsync(cardBmp, cardOcr);
                            string mergedTags = MergeTagName(parsed.CandidateTags, tag.Name);

                            var existing = BackendApiClient.FindDuplicateCustomer(existingCustomers, parsed.CandidateWechatAccount, resolvedName, parsed.CandidateWechatRemark);
                            if (existing is not null)
                            {
                                if (!overwriteExisting)
                                {
                                    // Smart backfill: even in skip-duplicate mode, automatically fill missing avatar or empty fields
                                    // without ever overwriting any existing non-empty data or employee notes!
                                    var filledItems = new List<string>();
                                    bool needAvatarBackfill = string.IsNullOrWhiteSpace(existing.AvatarUrl) && avatarPng is not null;
                                    string nextTags = MergeTagName(existing.Tags, mergedTags);

                                    string nextRemark = FillIfEmpty(existing.WechatRemark, parsed.CandidateWechatRemark, "备注", filledItems);
                                    string nextName = FillIfEmpty(existing.WechatName, resolvedName, "昵称", filledItems);
                                    string nextAccount = FillIfEmpty(existing.WechatAccount, parsed.CandidateWechatAccount, "微信号", filledItems);
                                    string nextGender = FillIfEmpty(existing.Gender, detectedGender, "性别", filledItems);
                                    string nextRegion = FillIfEmpty(existing.Region, parsed.CandidateRegion, "地区", filledItems);
                                    string nextPhone = FillIfEmpty(existing.Phone, parsed.CandidatePhone, "电话", filledItems);
                                    string nextSig = FillIfEmpty(existing.Signature, parsed.CandidateSignature, "签名", filledItems);
                                    string nextDesc = FillIfEmpty(existing.Description, parsed.CandidateDescription, "描述", filledItems);
                                    string nextSource = FillIfEmpty(existing.Source, parsed.CandidateSource, "来源", filledItems);
                                    string nextAdded = FillIfEmpty(existing.AddedDate, parsed.CandidateAddedDate, "添加时间", filledItems);
                                    bool tagsChanged = !string.Equals(nextTags, existing.Tags, StringComparison.Ordinal);
                                    if (tagsChanged) filledItems.Add("标签");
                                    if (needAvatarBackfill) filledItems.Add("头像");

                                    if (filledItems.Count == 0)
                                    {
                                        skipped++;
                                        Report(tag.Name, $"资料完整跳过「{existing.DisplayName}」", $"  ⊘ 资料完整跳过：{existing.DisplayName} ({existing.WechatAccount})");
                                        continue;
                                    }

                                    var latestCustomer = existing;
                                    bool anySuccess = false;
                                    bool needTextPatch = filledItems.Any(item => item != "头像");

                                    if (needTextPatch)
                                    {
                                        var backfillPatch = new CreateCustomerRequest
                                        {
                                            WechatRemark = nextRemark,
                                            WechatName = nextName,
                                            WechatAccount = nextAccount,
                                            Gender = nextGender,
                                            Tags = nextTags,
                                            Region = nextRegion,
                                            Phone = nextPhone,
                                            Signature = nextSig,
                                            Source = nextSource,
                                            AddedDate = nextAdded,
                                            Description = nextDesc,
                                            FollowStatus = existing.FollowStatus,
                                            LocalKey = existing.LocalKey,
                                            NativeId = existing.NativeId,
                                            Note = existing.Note
                                        };
                                        var bfRes = await AppServices.Backend.UpdateCustomerAsync(existing.Id, backfillPatch, ct);
                                        if (bfRes.IsSuccess && bfRes.Data is not null)
                                        {
                                            latestCustomer = bfRes.Data;
                                            anySuccess = true;
                                        }
                                    }

                                    if (needAvatarBackfill && avatarPng is not null)
                                    {
                                        var avRes = await AppServices.Backend.UploadCustomerAvatarAsync(existing.Id, avatarPng, ct);
                                        if (avRes.IsSuccess && avRes.Data is not null)
                                        {
                                            latestCustomer = avRes.Data;
                                            anySuccess = true;
                                        }
                                    }

                                    if (anySuccess)
                                    {
                                        ReplaceInList(existingCustomers, latestCustomer);
                                        updated++;
                                        string what = string.Join("、", filledItems);
                                        Report(tag.Name, $"已补全「{latestCustomer.DisplayName}」缺失项（{what}）", $"  ✦ 补全缺失（{what}）：{latestCustomer.DisplayName} ({latestCustomer.WechatAccount})");
                                    }
                                    else
                                    {
                                        failed++;
                                        Report(tag.Name, $"补全失败「{existing.DisplayName}」", $"  ✕ 补全失败：{existing.DisplayName}");
                                    }
                                    continue;
                                }

                                var patch = new CreateCustomerRequest
                                {
                                    WechatRemark = FirstNonEmpty(parsed.CandidateWechatRemark, existing.WechatRemark),
                                    WechatName = FirstNonEmpty(resolvedName, existing.WechatName),
                                    WechatAccount = FirstNonEmpty(parsed.CandidateWechatAccount, existing.WechatAccount),
                                    Gender = FirstNonEmpty(detectedGender, existing.Gender),
                                    Tags = MergeTagName(existing.Tags, mergedTags),
                                    Region = FirstNonEmpty(parsed.CandidateRegion, existing.Region),
                                    Phone = FirstNonEmpty(parsed.CandidatePhone, existing.Phone),
                                    Signature = FirstNonEmpty(parsed.CandidateSignature, existing.Signature),
                                    Source = FirstNonEmpty(parsed.CandidateSource, existing.Source),
                                    AddedDate = FirstNonEmpty(parsed.CandidateAddedDate, existing.AddedDate),
                                    Description = FirstNonEmpty(parsed.CandidateDescription, existing.Description),
                                    FollowStatus = existing.FollowStatus,
                                    LocalKey = existing.LocalKey,
                                    NativeId = existing.NativeId,
                                    Note = existing.Note
                                };

                                var updRes = await AppServices.Backend.UpdateCustomerAsync(existing.Id, patch, ct);
                                if (updRes.IsSuccess && updRes.Data is not null)
                                {
                                    var latest = updRes.Data;
                                    if (avatarPng is not null)
                                    {
                                        var avRes = await AppServices.Backend.UploadCustomerAvatarAsync(latest.Id, avatarPng, ct);
                                        if (avRes.IsSuccess && avRes.Data is not null) latest = avRes.Data;
                                    }
                                    ReplaceInList(existingCustomers, latest);
                                    updated++;
                                    Report(tag.Name, $"已覆盖更新「{latest.DisplayName}」", $"  ↻ 覆盖更新：{latest.DisplayName} ({latest.WechatAccount})");
                                }
                                else
                                {
                                    failed++;
                                    Report(tag.Name, $"更新失败：{updRes.ErrorMessage}", $"  ✕ 更新失败：{displayLabel} - {updRes.ErrorMessage}");
                                }
                                continue;
                            }

                            var createReq = new CreateCustomerRequest
                            {
                                WechatRemark = parsed.CandidateWechatRemark,
                                WechatName = resolvedName,
                                WechatAccount = parsed.CandidateWechatAccount,
                                Gender = detectedGender,
                                Tags = mergedTags,
                                Region = parsed.CandidateRegion,
                                Phone = parsed.CandidatePhone,
                                Signature = parsed.CandidateSignature,
                                Description = parsed.CandidateDescription,
                                Source = parsed.CandidateSource,
                                AddedDate = parsed.CandidateAddedDate,
                                Note = string.Empty
                            };

                            var createRes = await AppServices.Backend.CreateCustomerAsync(createReq, ct);
                            if (createRes.IsSuccess && createRes.Data is not null)
                            {
                                var latest = createRes.Data;
                                if (avatarPng is not null)
                                {
                                    var avRes = await AppServices.Backend.UploadCustomerAvatarAsync(latest.Id, avatarPng, ct);
                                    if (avRes.IsSuccess && avRes.Data is not null) latest = avRes.Data;
                                }
                                existingCustomers.Add(latest);
                                created++;
                                Report(tag.Name, $"已导入「{latest.DisplayName}」", $"  ✓ 新增入库：{latest.DisplayName} ({latest.WechatAccount})");
                            }
                            else if (createRes.StatusCode == System.Net.HttpStatusCode.Conflict)
                            {
                                skipped++;
                                Report(tag.Name, $"去重跳过「{displayLabel}」", $"  ⊘ 服务端去重跳过：{displayLabel}");
                            }
                            else
                            {
                                failed++;
                                Report(tag.Name, $"导入失败：{createRes.ErrorMessage}", $"  ✕ 入库失败：{displayLabel} - {createRes.ErrorMessage}");
                            }
                        }

                        // Exit tag cleanly once exact tag count is reached
                        if (tag.Count > 0 && tagAccountKeys.Count >= tag.Count)
                        {
                            Report(tag.Name, $"标签「{tag.Name}」采集完成（{tagAccountKeys.Count}/{tag.Count}）。", $"✓ 标签「{tag.Name}」采集完成（共 {tagAccountKeys.Count} 人）。");
                            break;
                        }

                        if (newlyCapturedThisPage == 0)
                        {
                            stalledPages++;
                            if (stalledPages >= 2)
                            {
                                Report(tag.Name, $"标签「{tag.Name}」已到达底部（共 {tagAccountKeys.Count} 人）。", $"✓ 标签「{tag.Name}」已到达底部（共 {tagAccountKeys.Count} 人）。");
                                break;
                            }
                        }
                        else
                        {
                            stalledPages = 0;
                        }

                        // Scroll down smoothly to next page
                        EnsureSafeToContinue(contactManagerHwnd, ct);
                        AutoScrollSmooth(mgrRect.Left + (int)(mgrRect.Width * 0.62), mgrRect.Top + (int)(mgrRect.Height * 0.56), scrollNotches);
                        await Task.Delay(Math.Max(260, stepDelayMs / 2), ct);
                    }
                }

                string tagSummary = string.Join(",", selectedTags.Select(t => t.Name));
                _ = await AppServices.Backend.CreateAuditLogAsync(
                    "batch_import_by_tag",
                    "customer",
                    tagSummary,
                    $"created={created},updated={updated},skipped={skipped},failed={failed}",
                    CancellationToken.None);

                return new BatchTagImportSummary
                {
                    WasCancelled = false,
                    StopReason = "批量导入已完成。",
                    CreatedCount = created,
                    UpdatedCount = updated,
                    SkippedCount = skipped,
                    FailedCount = failed
                };
            }
            catch (OperationCanceledException ex)
            {
                string reason = string.IsNullOrWhiteSpace(ex.Message) || ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    ? "已手动停止或触发安全熔断。"
                    : ex.Message;
                return new BatchTagImportSummary
                {
                    WasCancelled = true,
                    StopReason = reason,
                    CreatedCount = created,
                    UpdatedCount = updated,
                    SkippedCount = skipped,
                    FailedCount = failed
                };
            }
        }

        // =====================================================================
        // Clean Table Page Scanner (Nickname-Column Primary + Avatar-X Aligned)
        // =====================================================================

        private async Task<TablePageSnapshot> ScanCurrentTablePageAsync(nint mgrHwnd, CancellationToken ct)
        {
            using var tableBmp = CaptureWindowSafe(mgrHwnd);
            var tableOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(tableBmp, ct);
            return BuildTablePageSnapshot(tableBmp, tableOcr);
        }

        private static TablePageSnapshot BuildTablePageSnapshot(SoftwareBitmap bmp, OcrRecognitionResult ocr)
        {
            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;

            var nickHeader = ocr.Lines
                .Where(l => l.Bounds.X > w * 0.22 && l.Bounds.X < w * 0.45 && l.Bounds.Y < h * 0.22)
                .FirstOrDefault(l => l.Text.Contains("昵称", StringComparison.Ordinal));
            var remarkHeader = ocr.Lines
                .Where(l => l.Bounds.X > w * 0.40 && l.Bounds.X < w * 0.65 && l.Bounds.Y < h * 0.22)
                .FirstOrDefault(l => l.Text.Contains("备注", StringComparison.Ordinal));

            int tableTop = nickHeader is not null
                ? Math.Clamp((int)Math.Ceiling(nickHeader.Bounds.Y + nickHeader.Bounds.Height) + 8, (int)(h * 0.08), (int)(h * 0.22))
                : (int)(h * 0.12);
            int tableBottom = h - 8;

            // Avatar squares align strictly under the "昵称" header (left edge ≈ nickX, width ≈ 36px -> center ≈ nickX + 18)
            int nickX = nickHeader is not null ? (int)Math.Floor(nickHeader.Bounds.X) : (int)(w * 0.279);
            int avatarLeft = Math.Clamp(nickX - 2, (int)(w * 0.25), (int)(w * 0.36));
            int avatarWidth = Math.Max(34, (int)Math.Round(w * 0.042));
            int avatarClickRelX = avatarLeft + avatarWidth / 2;

            // Nickname text starts immediately to the right of the avatar square and ends before the "备注" column
            double nameColMinX = avatarLeft + avatarWidth + 2;
            double nameColMaxX = remarkHeader is not null ? remarkHeader.Bounds.X - 14 : w * 0.47;

            // 1. Primary signal: group OCR text tokens in the Nickname column into clean visual rows
            var nickTokens = ocr.Lines
                .Where(l =>
                    l.Bounds.Y >= tableTop &&
                    l.Bounds.Y + l.Bounds.Height <= tableBottom &&
                    l.Bounds.X >= nameColMinX &&
                    l.Bounds.X < nameColMaxX &&
                    !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            var rowClusters = new List<(int CenterY, List<OcrLineResult> Tokens)>();
            foreach (var tok in nickTokens)
            {
                int cy = (int)Math.Round(tok.Bounds.Y + tok.Bounds.Height / 2.0);
                var existing = rowClusters.FirstOrDefault(c => Math.Abs(c.CenterY - cy) <= 18);
                if (existing.Tokens is not null)
                {
                    existing.Tokens.Add(tok);
                }
                else
                {
                    rowClusters.Add((cy, [tok]));
                }
            }

            var rows = new List<TableRowEntry>();
            foreach (var cluster in rowClusters)
            {
                int avgY = (int)Math.Round(cluster.Tokens.Average(t => t.Bounds.Y + t.Bounds.Height / 2.0));
                string rawNick = string.Join(" ", cluster.Tokens.OrderBy(t => t.Bounds.X).Select(t => t.Text.Trim())).Trim();
                rows.Add(new TableRowEntry(avgY, rawNick, NormalizeRowKey(rawNick)));
            }

            // 2. Fallback supplement: check avatar column ONLY for rows where Nickname was pure emoji (no OCR text found at that Y)
            foreach (int avY in DetectAvatarColumnCenters(bmp, tableTop, tableBottom, avatarLeft, avatarLeft + avatarWidth))
            {
                if (!rows.Any(r => Math.Abs(r.CenterY - avY) <= 24))
                {
                    rows.Add(new TableRowEntry(avY, string.Empty, string.Empty));
                }
            }

            rows.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));
            return new TablePageSnapshot(avatarClickRelX, rows);
        }

        private static List<int> DetectAvatarColumnCenters(SoftwareBitmap bmp, int tableTop, int tableBottom, int scanL, int scanR)
        {
            int w = bmp.PixelWidth;
            int h = bmp.PixelHeight;
            scanL = Math.Clamp(scanL, 0, w - 1);
            scanR = Math.Clamp(scanR, scanL, w - 1);
            var centers = new List<int>();
            byte[] pixels = AvatarImageService.CopyBgraPixels(bmp);

            int bgIdx = (Math.Clamp(tableTop + 2, 0, h - 1) * w + Math.Clamp((int)(w * 0.90), 0, w - 1)) * 4;
            byte bgB = pixels[bgIdx], bgG = pixels[bgIdx + 1], bgR = pixels[bgIdx + 2];

            int minFgPerRow = Math.Max(8, (int)((scanR - scanL + 1) * 0.30));
            int runStart = -1, gap = 0;

            for (int y = tableTop; y <= tableBottom; y++)
            {
                int fg = 0;
                int rowOff = y * w * 4;
                for (int x = scanL; x <= scanR; x++)
                {
                    int idx = rowOff + x * 4;
                    if (Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR) > 28)
                        fg++;
                }

                if (fg >= minFgPerRow)
                {
                    if (runStart < 0) runStart = y;
                    gap = 0;
                }
                else if (runStart >= 0)
                {
                    gap++;
                    if (gap >= 4)
                    {
                        int runEnd = y - gap;
                        int runH = runEnd - runStart + 1;
                        if (runH >= 18 && runH <= 76) centers.Add((runStart + runEnd) / 2);
                        runStart = -1;
                        gap = 0;
                    }
                }
            }
            if (runStart >= 0)
            {
                int runEnd = tableBottom - gap;
                int runH = runEnd - runStart + 1;
                if (runH >= 18 && runH <= 76) centers.Add((runStart + runEnd) / 2);
            }
            return centers;
        }

        private static string NormalizeRowKey(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            // Remove spaces, ellipsis, full-width/half-width brackets variance so adjacent pages always match identically
            string s = raw.Trim()
                .Replace("（", "(").Replace("）", ")")
                .Replace(" ", "")
                .TrimEnd('.', '…', '·', '。');
            return s.ToLowerInvariant();
        }

        // =====================================================================
        // Window Management, Tag Scanning, Popup Capture & Safety Guards
        // =====================================================================

        private async Task<List<CustomerDto>> LoadAllExistingCustomersAsync(CancellationToken ct)
        {
            var list = new List<CustomerDto>();
            long cursor = 0;
            while (list.Count < 2000)
            {
                var pageRes = await AppServices.Backend.ListCustomersAsync(100, cursor, ct);
                if (!pageRes.IsSuccess || pageRes.Data is null || pageRes.Data.Items.Count == 0) break;
                list.AddRange(pageRes.Data.Items);
                cursor = pageRes.Data.NextCursor;
                if (cursor <= 0) break;
            }
            return list;
        }

        private async Task<(nint Hwnd, int Pid)> FindOrOpenContactManagerAsync(
            bool resetExisting,
            IProgress<string>? scanLog,
            CancellationToken ct)
        {
            void LogStep(string msg)
            {
                AppServices.Logs.Info("TAG", msg);
                scanLog?.Report(msg);
            }

            var (existingHwnd, existingPid) = FindContactManagerWindow();
            if (existingHwnd != nint.Zero)
            {
                if (!resetExisting)
                {
                    _ = Win32WindowHelper.ForceBringToForeground(existingHwnd);
                    await Task.Delay(220, ct);
                    return (existingHwnd, existingPid);
                }

                Win32WindowHelper.CloseWindowGracefully(existingHwnd);
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(120, ct);
                    if (!Win32WindowHelper.IsWindow(existingHwnd) || !Win32WindowHelper.IsWindowVisible(existingHwnd)) break;
                }
                await Task.Delay(180, ct);
            }

            // Automatically restore/pop up WeChat if minimized, hidden in tray, or not yet launched
            var ensured = await AppServices.WeChatWindows.EnsurePersonalWeChatWindowReadyAsync(
                autoLaunchIfClosed: true,
                allowLoginPageSuccess: false,
                ct: ct);
            if (!ensured.Success || ensured.Window is null)
            {
                throw new InvalidOperationException(ensured.Message);
            }

            var mainWin = ensured.Window;

            // Detect Windows UIPI elevation mismatch (including ERROR_ACCESS_DENIED on OpenProcessToken!)
            string? privilegeErr = Win32WindowHelper.GetPrivilegeMismatchDetail(mainWin.Hwnd);
            if (privilegeErr is not null)
            {
                throw new InvalidOperationException(privilegeErr);
            }

            NormalizeWindowSize(mainWin.Hwnd, StandardMainLogicalWidth, StandardMainLogicalHeight, offsetX: 36, offsetY: 32);
            await Task.Delay(420, ct);

            if (!Win32WindowHelper.GetVisibleWindowRect(mainWin.Hwnd, out var mainRect)) return (nint.Zero, 0);

            uint dpi = Win32WindowHelper.GetDpiForWindow(mainWin.Hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            // ---- Step A: Switch to left-rail Contacts tab (2nd icon) with visual detection & OCR verification ----
            using (var initBmp = CaptureVisibleWindow(mainWin.Hwnd, out mainRect))
            {
                var initOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(initBmp, ct);
                bool onContacts = IsOnContactsTab(initOcr, initBmp.PixelWidth, initBmp.PixelHeight);

                if (!onContacts)
                {
                    var railIcons = Win32WindowHelper.DetectWeChatLeftRailIcons(initBmp, scale);
                    double rx = mainRect.Width / (double)Math.Max(1, initBmp.PixelWidth);
                    double ry = mainRect.Height / (double)Math.Max(1, initBmp.PixelHeight);

                    var contactCandidates = new List<(int X, int Y, string Source)>();
                    if (railIcons.Count >= 3)
                    {
                        // Run 0 = Avatar, Run 1 = Messages, Run 2 = Contacts
                        var c2 = railIcons[2];
                        contactCandidates.Add((
                            mainRect.Left + (int)Math.Round(c2.CenterX * rx),
                            mainRect.Top + (int)Math.Round(c2.CenterY * ry),
                            "visual-rail-2"));
                    }
                    // Curated DPI-scaled fallback offsets on the visible frame (covers WeChat 4.x & 3.9 across 100%~200% DPI)
                    int railCenterX = railIcons.Count > 0
                        ? mainRect.Left + (int)Math.Round(railIcons[0].CenterX * rx)
                        : mainRect.Left + (int)Math.Round(28 * scale);
                    foreach (int logicalY in new[] { 150, 166, 136, 182 })
                    {
                        int cy = mainRect.Top + (int)Math.Round(logicalY * scale);
                        if (!contactCandidates.Any(p => Math.Abs(p.Y - cy) <= (int)Math.Round(10 * scale)))
                        {
                            contactCandidates.Add((railCenterX, cy, $"fallback-y{logicalY}"));
                        }
                    }

                    LogStep($"微信主窗口可见区域 ({mainRect.Left},{mainRect.Top}) {mainRect.Width}×{mainRect.Height} | DPI={dpi} (scale={scale:0.00}) | 视觉识别左侧栏图标 {railIcons.Count} 个");
                    for (int ri = 0; ri < Math.Min(railIcons.Count, 5); ri++)
                    {
                        var ic = railIcons[ri];
                        LogStep($"  · 左侧栏图标[{ri}] 相对中心=({ic.CenterX},{ic.CenterY}) 尺寸={ic.Width}×{ic.Height}");
                    }

                    foreach (var cand in contactCandidates)
                    {
                        ct.ThrowIfCancellationRequested();
                        var diag = Win32WindowHelper.LeftClickAt(cand.X, cand.Y, mainWin.Hwnd, $"左侧通讯录[{cand.Source}]");
                        LogStep($"🖱 点击左侧通讯录 [{cand.Source}] -> {diag.Summary}");
                        await Task.Delay(420, ct);

                        using var verifyBmp = CaptureVisibleWindow(mainWin.Hwnd, out mainRect);
                        var verifyOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(verifyBmp, ct);
                        if (IsOnContactsTab(verifyOcr, verifyBmp.PixelWidth, verifyBmp.PixelHeight))
                        {
                            onContacts = true;
                            LogStep($"✓ 已切换到微信「通讯录」页 ({cand.Source})");
                            break;
                        }

                        // If visually detected icon didn't switch on first hardware click, try direct window click once
                        if (cand.Source == "visual-rail-2")
                        {
                            bool okPost = Win32WindowHelper.PostWindowLeftClickAt(mainWin.Hwnd, cand.X, cand.Y, out int postErr);
                            LogStep($"🖱 窗口消息兜底点击左侧通讯录 ({cand.X},{cand.Y}) -> ok={okPost}, err={postErr}");
                            if (postErr == 5)
                            {
                                throw new InvalidOperationException("Windows 权限隔离 (UIPI/ACCESS_DENIED) 拦截了自动点击：请以「管理员身份」运行本工作台，或关闭微信后以普通权限重启微信。");
                            }
                            await Task.Delay(380, ct);
                            using var retryBmp = CaptureVisibleWindow(mainWin.Hwnd, out mainRect);
                            var retryOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(retryBmp, ct);
                            if (IsOnContactsTab(retryOcr, retryBmp.PixelWidth, retryBmp.PixelHeight))
                            {
                                onContacts = true;
                                LogStep($"✓ 已通过窗口消息切换到「通讯录」页");
                                break;
                            }
                        }
                    }
                }
                else
                {
                    LogStep($"微信主窗口 ({mainRect.Left},{mainRect.Top}) {mainRect.Width}×{mainRect.Height} (DPI={dpi}) 已处于「通讯录」页");
                }
            }

            // ---- Step B: Ensure top of Contacts column is visible, then locate & click「通讯录管理」with retry ----
            int listScrollX = mainRect.Left + (int)Math.Round(175 * scale);
            int listScrollY = mainRect.Top + (int)Math.Round(290 * scale);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                _ = Win32WindowHelper.ForceBringToForeground(mainWin.Hwnd);
                await Task.Delay(120, ct);

                using var curBmp = CaptureVisibleWindow(mainWin.Hwnd, out mainRect);
                var curOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(curBmp, ct);
                var btnPos = FindContactManagerButtonInWindow(curOcr, curBmp.PixelWidth, curBmp.PixelHeight, mainRect, scale);

                // If "通讯录管理" wasn't found yet, scroll the Contacts list strongly to the very top first
                if (btnPos is null)
                {
                    LogStep($"第 {attempt + 1} 轮未在顶部看到「通讯录管理」，向上滚动联系人列表归顶...");
                    for (int s = 0; s < 5; s++)
                    {
                        Win32WindowHelper.ScrollWheelAt(listScrollX, listScrollY, +18);
                        await Task.Delay(80, ct);
                    }
                    await Task.Delay(260, ct);

                    using var afterScrollBmp = CaptureVisibleWindow(mainWin.Hwnd, out mainRect);
                    var afterScrollOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(afterScrollBmp, ct);
                    btnPos = FindContactManagerButtonInWindow(afterScrollOcr, afterScrollBmp.PixelWidth, afterScrollBmp.PixelHeight, mainRect, scale);
                }

                int clickX = btnPos?.X ?? (mainRect.Left + (int)Math.Round(178 * scale));
                int clickY = btnPos?.Y ?? (mainRect.Top + (int)Math.Round((attempt == 0 ? 86 : 102) * scale));
                (nint Hwnd, int Pid) foundMgr = (nint.Zero, 0);

                var mgrOffsets = new (int Dx, int Dy)[]
                {
                    (0, 0),
                    (-26, 0),
                    (28, 0),
                    (0, -6),
                    (0, 7)
                };

                var clickRes = await Win32WindowHelper.ClickWithAdaptiveRetryAsync(
                    mainWin.Hwnd,
                    clickX,
                    clickY,
                    scale,
                    actionLabel: $"通讯录管理[{btnPos?.Method ?? "fallback"}]",
                    verifySucceededAsync: async () =>
                    {
                        for (int i = 0; i < 10; i++)
                        {
                            foundMgr = FindContactManagerWindow();
                            if (foundMgr.Hwnd != nint.Zero) return true;
                            await Task.Delay(220, ct);
                        }
                        return false;
                    },
                    log: LogStep,
                    waitAfterClickMs: 320,
                    allowPostMessageFallback: true,
                    customOffsetsLogical: mgrOffsets,
                    ct: ct);

                if (foundMgr.Hwnd != nint.Zero)
                {
                    LogStep($"✓ 已检测到「通讯录管理」窗口 (HWND=0x{foundMgr.Hwnd:X})");
                    return foundMgr;
                }
                if (clickRes.LastPostMessageError == 5)
                {
                    throw new InvalidOperationException("Windows 权限隔离 (UIPI/ACCESS_DENIED) 拦截了自动点击：请右键「以管理员身份运行」本工作台，或彻底退出微信后以普通权限重开微信。");
                }
            }
            bool fgNow = Win32WindowHelper.GetForegroundWindow() == mainWin.Hwnd;
            throw new InvalidOperationException(
                $"已尝试点击微信「通讯录管理」3 次但窗口未弹出（微信前景激活={(fgNow ? "是" : "否")}）。\n" +
                "若看到鼠标已悬停变色但无法点开，常见原因：① 微信以管理员权限运行（请右键以管理员身份运行本工作台，或普通权限重启微信）；② 杀毒/安全软件拦截了模拟点击（请将工作台加入白名单）。");
        }

        private static bool IsOnContactsTab(OcrRecognitionResult ocr, int bmpW, int bmpH)
        {
            double minX = bmpW * 0.04;
            double maxX = bmpW * 0.48;
            double maxY = bmpH * 0.65;

            foreach (var l in ocr.Lines)
            {
                if (l.Bounds.X < minX || l.Bounds.X > maxX || l.Bounds.Y > maxY) continue;
                string t = l.Text.Replace(" ", "").Trim();
                if (string.IsNullOrEmpty(t)) continue;

                if (FuzzyMatchContactManagerText(t) ||
                    t.Contains("新的朋友", StringComparison.Ordinal) ||
                    t.Contains("企业微信联系人", StringComparison.Ordinal) ||
                    t is "公众号" or "服务号" or "标签" or "群聊" or "新的朋友")
                {
                    return true;
                }
            }
            return false;
        }

        private static bool FuzzyMatchContactManagerText(string compactText)
        {
            if (compactText.Contains("通讯录管理", StringComparison.Ordinal) ||
                compactText.Contains("ContactManager", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (compactText.Length is >= 3 and <= 8)
            {
                const string target = "通讯录管理";
                int matched = 0;
                foreach (char ch in target)
                {
                    if (compactText.Contains(ch)) matched++;
                }
                if (matched >= 3 && (compactText.Contains('通') || compactText.Contains('管') || compactText.Contains('录')))
                {
                    return true;
                }
            }
            return false;
        }

        private static (int X, int Y, string Method)? FindContactManagerButtonInWindow(
            OcrRecognitionResult ocr,
            int bmpW,
            int bmpH,
            Win32WindowHelper.RECT rect,
            double scale)
        {
            double rx = rect.Width / (double)Math.Max(1, bmpW);
            double ry = rect.Height / (double)Math.Max(1, bmpH);
            double minX = bmpW * 0.04;
            double maxX = bmpW * 0.48;
            double minY = bmpH * 0.04;
            double maxY = bmpH * 0.36;

            // Priority 1 & 2: Exact or fuzzy OCR match for "通讯录管理"
            var direct = ocr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX && l.Bounds.Y >= minY && l.Bounds.Y <= maxY)
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault(l => FuzzyMatchContactManagerText(l.Text.Replace(" ", "").Trim()));
            if (direct is not null)
            {
                return (
                    rect.Left + (int)Math.Round((direct.Bounds.X + direct.Bounds.Width / 2.0) * rx),
                    rect.Top + (int)Math.Round((direct.Bounds.Y + direct.Bounds.Height / 2.0) * ry),
                    $"ocr:{direct.Text.Trim()}"
                );
            }

            // Priority 3: Geometric anchor right above "新的朋友" (in WeChat Contacts, 通讯录管理 sits directly above 新的朋友)
            var newFriends = ocr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX && l.Bounds.Y >= bmpH * 0.08 && l.Bounds.Y <= bmpH * 0.45)
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault(l => l.Text.Replace(" ", "").Contains("新的朋友", StringComparison.Ordinal));
            if (newFriends is not null)
            {
                int anchorX = rect.Left + (int)Math.Round((newFriends.Bounds.X + newFriends.Bounds.Width / 2.0) * rx);
                int anchorY = rect.Top + (int)Math.Round(newFriends.Bounds.Y * ry - 38 * scale);
                if (anchorY > rect.Top + (int)Math.Round(45 * scale))
                {
                    return (anchorX, anchorY, "above-new-friends");
                }
            }

            return null;
        }

        private static SoftwareBitmap CaptureVisibleWindow(nint hWnd, out Win32WindowHelper.RECT visibleRect)
        {
            if (Win32WindowHelper.GetVisibleWindowRect(hWnd, out visibleRect) && visibleRect.Width > 0 && visibleRect.Height > 0)
            {
                return Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(visibleRect.Left, visibleRect.Top, visibleRect.Width, visibleRect.Height);
            }
            _ = Win32WindowHelper.GetWindowRect(hWnd, out visibleRect);
            return Win32WindowHelper.CaptureWindowToSoftwareBitmap(hWnd);
        }

        private static (nint Hwnd, int Pid) FindContactManagerWindow()
        {
            nint foundHwnd = nint.Zero;
            int foundPid = 0;

            var all = AppServices.WeChatWindows.EnumerateWindows(includeAllVisibleWindows: false);
            var byTitle = all.FirstOrDefault(w =>
                w.AccountType == WeChatAccountType.Personal &&
                (w.Title.Contains("通讯录管理", StringComparison.Ordinal) ||
                 w.Title.Contains("Contact", StringComparison.OrdinalIgnoreCase)));
            if (byTitle is not null) return (byTitle.Hwnd, byTitle.ProcessId);

            var wechatPids = all.Where(w => w.AccountType == WeChatAccountType.Personal).Select(w => w.ProcessId).Distinct().ToHashSet();
            if (wechatPids.Count == 0) return (nint.Zero, 0);

            Win32WindowHelper.EnumWindows((hWnd, lParam) =>
            {
                if (!Win32WindowHelper.IsWindow(hWnd)) return true;
                _ = Win32WindowHelper.GetWindowThreadProcessId(hWnd, out uint rawPid);
                int pid = unchecked((int)rawPid);
                if (!wechatPids.Contains(pid)) return true;

                string title = Win32WindowHelper.GetWindowTitleText(hWnd);
                string cls = Win32WindowHelper.GetWindowClassName(hWnd);
                bool isMinimized = Win32WindowHelper.IsIconic(hWnd);
                if (!Win32WindowHelper.GetWindowRect(hWnd, out var r)) return true;
                if (!isMinimized && (r.Width < 420 || r.Height < 320)) return true;

                if (title.Contains("通讯录管理", StringComparison.Ordinal) ||
                    cls.Contains("ContactManager", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("ContactManage", StringComparison.OrdinalIgnoreCase))
                {
                    foundHwnd = hWnd;
                    foundPid = pid;
                    return false;
                }
                return true;
            }, nint.Zero);

            return (foundHwnd, foundPid);
        }

        private static void NormalizeWindowSize(nint hWnd, int logicalW, int logicalH, int offsetX, int offsetY)
        {
            _ = Win32WindowHelper.NormalizeWindowOnCurrentMonitor(hWnd, logicalW, logicalH, offsetX, offsetY);
        }

        private static bool IsTagSectionHeader(string rawText)
        {
            if (string.IsNullOrWhiteSpace(rawText)) return false;
            string s = rawText.Trim();
            if (s.Contains("新建标签", StringComparison.Ordinal) ||
                s.Contains("无标签", StringComparison.Ordinal) ||
                s.Contains("添加标签", StringComparison.Ordinal))
            {
                return false;
            }
            // Strip spaces, chevrons, triangles, arrows & punctuation that OCR often appends next to "标签"
            string clean = s
                .Replace(" ", "")
                .Trim('>', '<', 'v', 'V', '^', '∨', '∧', '›', '‹', '»', '▾', '▸', '▼', '▶', '·', '.', '-', '_', ':', '：');
            return clean is "标签" or "标鉴" or "标签管理" or "标" ||
                   (clean.StartsWith("标签", StringComparison.Ordinal) && clean.Length <= 4);
        }

        private static OcrLineResult? FindTagHeaderLine(OcrRecognitionResult ocr, int bmpW, int bmpH)
        {
            double sidebarMaxX = bmpW * 0.28;
            return ocr.Lines
                .Where(l => l.Bounds.X < sidebarMaxX && l.Bounds.Y > bmpH * 0.05 && l.Bounds.Y < bmpH * 0.85)
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault(l => IsTagSectionHeader(l.Text));
        }

        private sealed record SidebarScanResult(
            List<WeChatTagItem> Tags,
            bool IsTagSectionExpanded,
            List<string> SidebarLines,
            (int X, int Y)? TagHeaderScreenCenter,
            string TagHeaderText,
            int SidebarWidth,
            int WindowHeight);

        private sealed record MergedSidebarRow(string Text, double Left, double Top, double Right, double Bottom)
        {
            public double CenterX => (Left + Right) / 2.0;
            public double CenterY => (Top + Bottom) / 2.0;
        }

        private async Task<SidebarScanResult> ScanTagsFromManagerWindowDetailedAsync(nint mgrHwnd, CancellationToken ct)
        {
            // Hide any active click reticle and ensure Contact Manager is visible on top before capturing
            ClickVisualOverlay.HideImmediately();
            _ = Win32WindowHelper.ForceBringToForeground(mgrHwnd);
            await Task.Delay(55, ct);

            if (!Win32WindowHelper.GetVisibleWindowRect(mgrHwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
            {
                return new SidebarScanResult([], false, [], null, string.Empty, 0, 0);
            }

            uint dpi = Win32WindowHelper.GetDpiForWindow(mgrHwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            // Crop ONLY the left sidebar (26% of window width) so RapidOCR focuses 100% on crisp small sidebar text
            // without being diluted by 60+ dense contact table cells on the right!
            int sidebarW = Math.Clamp((int)Math.Round(rect.Width * 0.26), 160, rect.Width);
            using var sidebarBmp = Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(rect.Left, rect.Top, sidebarW, rect.Height);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(sidebarBmp, ct);

            // Merge horizontally-split tokens on the same visual row (e.g. black "家人" + gray "(18)" -> "家人 (18)")
            double rowMergeTol = Math.Max(10.0, 12.0 * scale);
            var rawClusters = new List<List<OcrLineResult>>();
            foreach (var line in ocr.Lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
            {
                if (string.IsNullOrWhiteSpace(line.Text)) continue;
                double cy = line.Bounds.Y + line.Bounds.Height / 2.0;
                var cluster = rawClusters.FirstOrDefault(c =>
                    Math.Abs(c.Average(x => x.Bounds.Y + x.Bounds.Height / 2.0) - cy) <= rowMergeTol);
                if (cluster is not null)
                {
                    cluster.Add(line);
                }
                else
                {
                    rawClusters.Add([line]);
                }
            }

            var rows = new List<MergedSidebarRow>(rawClusters.Count);
            foreach (var c in rawClusters)
            {
                var ordered = c.OrderBy(x => x.Bounds.X).ToList();
                string joined = string.Join(" ", ordered.Select(x => x.Text.Trim())).Trim();
                rows.Add(new MergedSidebarRow(
                    joined,
                    ordered.Min(x => x.Bounds.X),
                    ordered.Min(x => x.Bounds.Y),
                    ordered.Max(x => x.Bounds.X + x.Bounds.Width),
                    ordered.Max(x => x.Bounds.Y + x.Bounds.Height)));
            }
            rows.Sort((a, b) => a.CenterY.CompareTo(b.CenterY));

            var sidebarDebug = rows.Select(r => r.Text).ToList();
            double rx = sidebarW / (double)Math.Max(1, sidebarBmp.PixelWidth);
            double ry = rect.Height / (double)Math.Max(1, sidebarBmp.PixelHeight);

            var tagHeader = rows.FirstOrDefault(r =>
                r.Top > sidebarBmp.PixelHeight * 0.05 &&
                r.Top < sidebarBmp.PixelHeight * 0.85 &&
                IsTagSectionHeader(r.Text));

            (int X, int Y)? headerCenter = tagHeader is not null
                ? (rect.Left + (int)Math.Round(tagHeader.CenterX * rx), rect.Top + (int)Math.Round(tagHeader.CenterY * ry))
                : null;

            double minTagY = tagHeader is not null ? tagHeader.Bottom : sidebarBmp.PixelHeight * 0.22;

            var tags = new List<WeChatTagItem>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool sawUntaggedOrNewTag = false;

            foreach (var row in rows)
            {
                if (row.Top < minTagY) continue;
                string txt = row.Text.Trim();
                if (string.IsNullOrEmpty(txt)) continue;

                if (txt.Contains("新建标签", StringComparison.Ordinal))
                {
                    sawUntaggedOrNewTag = true;
                    break;
                }
                if (txt.Contains("最近群聊", StringComparison.Ordinal)) break;
                if (txt.StartsWith("无标签", StringComparison.Ordinal))
                {
                    sawUntaggedOrNewTag = true;
                    continue;
                }
                if (txt is "朋友权限" or "筛选" or "通讯录管理" or "仅聊天" or "不看他" or "不让他看" ||
                    txt.StartsWith("聊天、朋友圈", StringComparison.Ordinal) ||
                    IsTagSectionHeader(txt))
                {
                    continue;
                }

                var m = TagWithCountRegex().Match(txt);
                string tagName;
                int count;
                if (m.Success)
                {
                    tagName = m.Groups[1].Value.Trim();
                    _ = int.TryParse(m.Groups[2].Value, out count);
                }
                else
                {
                    if (txt.Length < 2 || txt.StartsWith("全部", StringComparison.Ordinal)) continue;
                    tagName = txt;
                    count = -1;
                }

                if (string.IsNullOrWhiteSpace(tagName) || tagName == "全部" || tagName == "无标签") continue;
                if (!seenNames.Add(tagName)) continue;

                tags.Add(new WeChatTagItem
                {
                    Name = tagName,
                    Count = count,
                    RelativeX = (row.CenterX * rx) / Math.Max(1.0, rect.Width),
                    RelativeY = (row.CenterY * ry) / Math.Max(1.0, rect.Height),
                    IsSelected = count > 0
                });
            }

            bool isExpanded = sawUntaggedOrNewTag || tags.Any(t => t.Count >= 0);
            return new SidebarScanResult(
                tags,
                isExpanded,
                sidebarDebug,
                headerCenter,
                tagHeader?.Text ?? string.Empty,
                sidebarW,
                rect.Height);
        }

        private async Task<(int X, int Y)?> FindTagClickPositionAsync(nint mgrHwnd, Win32WindowHelper.RECT mgrRect, WeChatTagItem tag, CancellationToken ct)
        {
            var scan = await ScanTagsFromManagerWindowDetailedAsync(mgrHwnd, ct);
            var matched = scan.Tags.FirstOrDefault(t =>
                t.Name.Equals(tag.Name, StringComparison.OrdinalIgnoreCase) ||
                t.Name.StartsWith(tag.Name, StringComparison.OrdinalIgnoreCase) ||
                tag.Name.StartsWith(t.Name, StringComparison.OrdinalIgnoreCase));

            if (matched is not null && matched.RelativeX > 0 && matched.RelativeY > 0)
            {
                return (
                    mgrRect.Left + (int)Math.Round(matched.RelativeX * mgrRect.Width),
                    mgrRect.Top + (int)Math.Round(matched.RelativeY * mgrRect.Height)
                );
            }

            if (tag.RelativeX > 0 && tag.RelativeY > 0)
            {
                return (
                    mgrRect.Left + (int)Math.Round(tag.RelativeX * mgrRect.Width),
                    mgrRect.Top + (int)Math.Round(tag.RelativeY * mgrRect.Height)
                );
            }

            return null;
        }

        private async Task<(int X, int Y)?> FindTextCenterInWindowAsync(nint hwnd, Win32WindowHelper.RECT rect, string keyword, CancellationToken ct)
        {
            using var bmp = CaptureWindowSafe(hwnd);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
            var line = ocr.Lines.FirstOrDefault(l => l.Text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            if (line is null) return null;
            return (
                rect.Left + (int)Math.Round(line.Bounds.X + line.Bounds.Width / 2.0),
                rect.Top + (int)Math.Round(line.Bounds.Y + line.Bounds.Height / 2.0)
            );
        }

        private async Task<(SoftwareBitmap Bitmap, OcrRecognitionResult Ocr, WeChatOcrAnalysis Parsed)> OpenAndCaptureProfileCardWithRetryAsync(
            nint mgrHwnd,
            int wechatPid,
            Win32WindowHelper.RECT mgrRect,
            int baseX,
            int baseY,
            int stepDelayMs,
            CancellationToken ct)
        {
            uint dpi = Win32WindowHelper.GetDpiForWindow(mgrHwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            var offsets = new (int Dx, int Dy)[] { (0, 0), (14, 0), (-6, 4), (6, -5) };

            SoftwareBitmap? lastBmp = null;
            OcrRecognitionResult? lastOcr = null;
            WeChatOcrAnalysis? lastParsed = null;

            for (int i = 0; i < offsets.Length; i++)
            {
                EnsureSafeToContinue(mgrHwnd, ct);
                int cx = baseX + (int)Math.Round(offsets[i].Dx * scale);
                int cy = baseY + (int)Math.Round(offsets[i].Dy * scale);

                AutoClick(cx, cy);
                await WaitForProfilePopupAsync(mgrHwnd, wechatPid, Math.Max(520, stepDelayMs), ct);

                ClickVisualOverlay.HideImmediately();
                await Task.Delay(40, ct);

                lastBmp?.Dispose();
                lastBmp = CaptureProfileCardPopup(mgrHwnd, wechatPid, mgrRect, out bool isDedicatedPopup);
                lastOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(lastBmp, ct);
                if (!isDedicatedPopup)
                {
                    lastOcr = FilterOcrToProfileCard(lastOcr, lastBmp.PixelWidth);
                }
                lastParsed = WeChatOcrAnalyzer.Analyze(null, lastOcr);

                bool gotProfile = isDedicatedPopup ||
                    !string.IsNullOrWhiteSpace(lastParsed.CandidateWechatAccount) ||
                    !string.IsNullOrWhiteSpace(lastParsed.CandidateWechatName) ||
                    !string.IsNullOrWhiteSpace(lastParsed.CandidateWechatRemark);
                if (gotProfile)
                {
                    // On slower/new PCs, WeChat draws popup text before finishing async avatar decoding.
                    // If the avatar box still looks like a blank/gray placeholder, wait 340ms and re-capture once!
                    if (AvatarImageService.IsLikelyPlaceholderAvatar(lastBmp, lastOcr))
                    {
                        await Task.Delay(340, ct);
                        var refreshedBmp = CaptureProfileCardPopup(mgrHwnd, wechatPid, mgrRect, out bool refreshedDedicated);
                        var refreshedOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(refreshedBmp, ct);
                        if (!refreshedDedicated)
                        {
                            refreshedOcr = FilterOcrToProfileCard(refreshedOcr, refreshedBmp.PixelWidth);
                        }
                        lastBmp.Dispose();
                        lastBmp = refreshedBmp;
                        lastOcr = refreshedOcr;
                        lastParsed = WeChatOcrAnalyzer.Analyze(null, lastOcr);
                    }
                    return (lastBmp, lastOcr, lastParsed);
                }

                await Task.Delay(140, ct);
            }

            return (lastBmp!, lastOcr!, lastParsed!);
        }

        private static async Task WaitForProfilePopupAsync(nint mgrHwnd, int wechatPid, int maxWaitMs, CancellationToken ct)
        {
            int elapsed = 0;
            const int pollStep = 60;
            await Task.Delay(150, ct);
            elapsed += 150;
            while (elapsed < maxWaitMs)
            {
                if (FindProfilePopupWindow(wechatPid, mgrHwnd) != nint.Zero)
                {
                    // Give WeChat 200ms after popup creation to finish rendering high-res avatar & text
                    await Task.Delay(200, ct);
                    return;
                }
                await Task.Delay(pollStep, ct);
                elapsed += pollStep;
            }
        }

        private static SoftwareBitmap CaptureProfileCardPopup(nint mgrHwnd, int wechatPid, Win32WindowHelper.RECT mgrRect, out bool isDedicatedPopup)
        {
            nint popupHwnd = FindProfilePopupWindow(wechatPid, mgrHwnd);
            if (popupHwnd != nint.Zero)
            {
                try
                {
                    isDedicatedPopup = true;
                    return Win32WindowHelper.CaptureWindowToSoftwareBitmap(popupHwnd);
                }
                catch { }
            }

            isDedicatedPopup = false;
            return Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(mgrRect.Left, mgrRect.Top, mgrRect.Width, mgrRect.Height);
        }

        private static OcrRecognitionResult FilterOcrToProfileCard(OcrRecognitionResult ocr, int imageWidth)
        {
            double minX = imageWidth * 0.28;
            var anchor = ocr.Lines
                .Where(l => l.Bounds.X >= minX)
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault(l => l.Text.TrimStart().StartsWith("微信号", StringComparison.OrdinalIgnoreCase))
                ?? ocr.Lines
                    .Where(l => l.Bounds.X >= minX)
                    .OrderBy(l => l.Bounds.Y)
                    .FirstOrDefault(l =>
                    {
                        string t = l.Text.TrimStart();
                        return t.StartsWith("昵称:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("昵称：", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区：", StringComparison.OrdinalIgnoreCase);
                    });

            double cardLeft = anchor is not null
                ? Math.Max(minX, anchor.Bounds.X - Math.Max(95, imageWidth * 0.115))
                : minX;
            double cardRight = anchor is not null
                ? Math.Min(imageWidth, anchor.Bounds.X + Math.Max(200, imageWidth * 0.24))
                : imageWidth;

            var kept = ocr.Lines.Where(l => l.Bounds.X >= cardLeft && l.Bounds.X <= cardRight).ToList();
            return new OcrRecognitionResult
            {
                EngineId = ocr.EngineId,
                EngineName = ocr.EngineName,
                LanguageTag = ocr.LanguageTag,
                ImageWidth = ocr.ImageWidth,
                ImageHeight = ocr.ImageHeight,
                ElapsedMilliseconds = ocr.ElapsedMilliseconds,
                Lines = kept,
                FullText = string.Join(Environment.NewLine, kept.Select(l => l.Text))
            };
        }

        private static nint FindProfilePopupWindow(int wechatPid, nint mgrHwnd)
        {
            if (wechatPid <= 0) return nint.Zero;
            nint popup = nint.Zero;

            Win32WindowHelper.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == mgrHwnd || !Win32WindowHelper.IsWindowVisible(hWnd) || Win32WindowHelper.IsIconic(hWnd)) return true;
                _ = Win32WindowHelper.GetWindowThreadProcessId(hWnd, out uint rawPid);
                if (unchecked((int)rawPid) != wechatPid) return true;

                if (!Win32WindowHelper.GetWindowRect(hWnd, out var r)) return true;
                if (r.Width >= 240 && r.Width <= 560 && r.Height >= 280 && r.Height <= 780)
                {
                    string cls = Win32WindowHelper.GetWindowClassName(hWnd);
                    if (!cls.Equals("mmui::MainWindow", StringComparison.OrdinalIgnoreCase) &&
                        !cls.Equals("WeChatMainWndForPC", StringComparison.OrdinalIgnoreCase))
                    {
                        popup = hWnd;
                        return false;
                    }
                }
                return true;
            }, nint.Zero);

            return popup;
        }

        private void DismissPopupSafe(nint mgrHwnd, Win32WindowHelper.RECT mgrRect)
        {
            uint dpi = Win32WindowHelper.GetDpiForWindow(mgrHwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            AutoClick(
                mgrRect.Left + (int)Math.Round(mgrRect.Width * 0.68),
                mgrRect.Top + (int)Math.Round(18 * scale));
        }

        private static SoftwareBitmap CaptureWindowSafe(nint hWnd)
        {
            ClickVisualOverlay.HideImmediately();
            if (Win32WindowHelper.GetVisibleWindowRect(hWnd, out var r) && r.Width > 0 && r.Height > 0)
            {
                return Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(r.Left, r.Top, r.Width, r.Height);
            }
            return Win32WindowHelper.CaptureWindowToSoftwareBitmap(hWnd);
        }

        private void EnsureSafeToContinue(nint mgrHwnd, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Win32WindowHelper.IsEscapePressed())
            {
                throw new OperationCanceledException("检测到按下 ESC 键，已安全停止导入。");
            }
            if (!Win32WindowHelper.IsWindow(mgrHwnd) || Win32WindowHelper.IsIconic(mgrHwnd))
            {
                throw new OperationCanceledException("「通讯录管理」窗口已最小化或关闭，已安全停止导入。");
            }
            if (_hasAutoCursor && Win32WindowHelper.GetCursorPos(out var cur))
            {
                int dx = cur.X - _lastAutoCursor.X;
                int dy = cur.Y - _lastAutoCursor.Y;
                if (dx * dx + dy * dy > 110 * 110)
                {
                    throw new OperationCanceledException("检测到人工移动鼠标，已自动安全中止导入。");
                }
            }
        }

        private void AutoClick(int x, int y)
        {
            Win32WindowHelper.LeftClickAt(x, y);
            _lastAutoCursor = new Win32WindowHelper.POINT { X = x, Y = y };
            _hasAutoCursor = true;
        }

        private void AutoScroll(int x, int y, int notches)
        {
            Win32WindowHelper.ScrollWheelAt(x, y, notches);
            _lastAutoCursor = new Win32WindowHelper.POINT { X = x, Y = y };
            _hasAutoCursor = true;
        }

        private void AutoScrollSmooth(int x, int y, int downNotches)
        {
            int remaining = Math.Max(1, downNotches);
            while (remaining > 0)
            {
                int step = Math.Min(5, remaining);
                AutoScroll(x, y, -step);
                remaining -= step;
                if (remaining > 0) Thread.Sleep(70);
            }
        }

        private static void ReplaceInList(List<CustomerDto> list, CustomerDto updated)
        {
            int idx = list.FindIndex(c => c.Id == updated.Id);
            if (idx >= 0) list[idx] = updated;
            else list.Add(updated);
        }

        private static string MergeTagName(string existingTags, string newTag)
        {
            var parts = existingTags
                .Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            foreach (var t in newTag.Split([',', '，', '、', ';', '；'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!parts.Any(p => p.Equals(t, StringComparison.OrdinalIgnoreCase)))
                {
                    parts.Add(t);
                }
            }
            return string.Join("、", parts);
        }

        private static string FirstNonEmpty(string preferred, string fallback) =>
            string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

        private static string FillIfEmpty(string existingValue, string candidateValue, string fieldLabel, List<string> filledItems)
        {
            if (string.IsNullOrWhiteSpace(existingValue) && !string.IsNullOrWhiteSpace(candidateValue))
            {
                filledItems.Add(fieldLabel);
                return candidateValue;
            }
            return existingValue;
        }

        private static string DisplayCandidateName(string nickname, string account, string remark)
        {
            if (!string.IsNullOrWhiteSpace(nickname)) return nickname.Trim();
            if (!string.IsNullOrWhiteSpace(account)) return account.Trim();
            return remark.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        [GeneratedRegex(@"^(.+?)\s*[\(（]\s*(\d{1,5})\s*[\)）]$")]
        private static partial Regex TagWithCountRegex();
    }
}
