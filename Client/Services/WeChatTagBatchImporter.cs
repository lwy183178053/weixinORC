using System.Runtime.InteropServices.WindowsRuntime;
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

        public async Task<TagScanResult> LocateAndScanTagsAsync(bool scrollLeftSidebarDown = false, CancellationToken ct = default)
        {
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                throw new InvalidOperationException(AppServices.Ocr.RapidEngine.AvailabilityNote);
            }

            var (mgrHwnd, pid) = await FindOrOpenContactManagerAsync(resetExisting: !scrollLeftSidebarDown, ct);
            if (mgrHwnd == nint.Zero)
            {
                throw new InvalidOperationException("未找到微信「通讯录管理」窗口。请确认个人微信已登录并处于桌面可见状态后再试。");
            }

            NormalizeWindowSize(mgrHwnd, StandardMgrLogicalWidth, StandardMgrLogicalHeight, offsetX: 60, offsetY: 50);
            await Task.Delay(350, ct);

            if (!Win32WindowHelper.GetWindowRect(mgrHwnd, out var rect))
            {
                throw new InvalidOperationException("无法获取「通讯录管理」窗口坐标。");
            }

            if (scrollLeftSidebarDown)
            {
                int leftX = rect.Left + (int)(rect.Width * 0.11);
                int leftY = rect.Top + (int)(rect.Height * 0.55);
                Win32WindowHelper.ScrollWheelAt(leftX, leftY, -5);
                await Task.Delay(350, ct);
            }

            var tags = await ScanTagsFromManagerWindowAsync(mgrHwnd, ct);
            if (tags.Count == 0)
            {
                // Expand collapsed "标签" header if needed
                using var checkBmp = CaptureWindowSafe(mgrHwnd);
                var checkOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(checkBmp, ct);
                var labelLine = checkOcr.Lines.FirstOrDefault(l =>
                    l.Bounds.X < checkBmp.PixelWidth * 0.22 &&
                    l.Text.Trim() is "标签" or "标鉴");
                if (labelLine is not null)
                {
                    int clickX = rect.Left + (int)Math.Round(labelLine.Bounds.X + labelLine.Bounds.Width / 2.0);
                    int clickY = rect.Top + (int)Math.Round(labelLine.Bounds.Y + labelLine.Bounds.Height / 2.0);
                    Win32WindowHelper.LeftClickAt(clickX, clickY);
                    await Task.Delay(450, ct);
                    tags = await ScanTagsFromManagerWindowAsync(mgrHwnd, ct);
                }
            }

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

                NormalizeWindowSize(contactManagerHwnd, StandardMgrLogicalWidth, StandardMgrLogicalHeight, offsetX: 60, offsetY: 50);
                await Task.Delay(350, ct);

                var globalAccountKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var tag in selectedTags)
                {
                    EnsureSafeToContinue(contactManagerHwnd, ct);
                    Report(tag.Name, $"正在切换标签「{tag.Name}」...", $"▶ 开始导入标签「{tag.Name}」{(tag.Count > 0 ? $"（预计 {tag.Count} 人）" : "")}");

                    if (!Win32WindowHelper.GetWindowRect(contactManagerHwnd, out var mgrRect))
                    {
                        throw new OperationCanceledException("无法读取「通讯录管理」窗口位置。");
                    }

                    // 1. Click target tag on left sidebar
                    var tagPos = await FindTagClickPositionAsync(contactManagerHwnd, mgrRect, tag, ct);
                    if (tagPos is null)
                    {
                        Report(tag.Name, $"未在左侧栏找到标签「{tag.Name}」，跳过。", $"⚠ 跳过标签「{tag.Name}」：左侧栏未识别到该标签。");
                        continue;
                    }

                    AutoClick(tagPos.Value.X, tagPos.Value.Y);
                    await Task.Delay(Math.Max(420, stepDelayMs), ct);

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
                        if (!Win32WindowHelper.GetWindowRect(contactManagerHwnd, out mgrRect))
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

                            AutoClick(clickX, clickY);
                            await WaitForProfilePopupAsync(contactManagerHwnd, wechatPid, Math.Max(260, stepDelayMs / 2), ct);

                            using var cardBmp = CaptureProfileCardPopup(contactManagerHwnd, wechatPid, mgrRect, out bool isDedicatedPopup);
                            var cardOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(cardBmp, ct);
                            if (!isDedicatedPopup)
                            {
                                cardOcr = FilterOcrToProfileCard(cardOcr, cardBmp.PixelWidth);
                            }

                            // Dismiss floating profile card safely by clicking empty top-header space (NEVER ESC!)
                            DismissPopupSafe(contactManagerHwnd, mgrRect);
                            await Task.Delay(Math.Clamp(stepDelayMs / 4, 110, 220), ct);

                            var parsed = WeChatOcrAnalyzer.Analyze(null, cardOcr);
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

                            var existing = FindExistingCustomer(existingCustomers, parsed.CandidateWechatAccount, resolvedName, parsed.CandidateWechatRemark);
                            if (existing is not null)
                            {
                                if (!overwriteExisting)
                                {
                                    skipped++;
                                    Report(tag.Name, $"已存在跳过「{existing.DisplayName}」", $"  ⊘ 已存在跳过：{existing.DisplayName} ({existing.WechatAccount})");
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
                                    Description = existing.Description,
                                    FollowStatus = existing.FollowStatus,
                                    LocalKey = existing.LocalKey,
                                    NativeId = existing.NativeId,
                                    Note = existing.Note
                                };

                                var updRes = await AppServices.Backend.UpdateCustomerAsync(existing.Id, patch, ct);
                                if (updRes.IsSuccess && updRes.Data is not null)
                                {
                                    if (avatarPng is not null)
                                    {
                                        _ = await AppServices.Backend.UploadCustomerAvatarAsync(updRes.Data.Id, avatarPng, ct);
                                    }
                                    ReplaceInList(existingCustomers, updRes.Data);
                                    updated++;
                                    Report(tag.Name, $"已覆盖更新「{updRes.Data.DisplayName}」", $"  ↻ 覆盖更新：{updRes.Data.DisplayName} ({updRes.Data.WechatAccount})");
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
                                Source = parsed.CandidateSource,
                                AddedDate = parsed.CandidateAddedDate,
                                Note = string.Empty
                            };

                            var createRes = await AppServices.Backend.CreateCustomerAsync(createReq, ct);
                            if (createRes.IsSuccess && createRes.Data is not null)
                            {
                                if (avatarPng is not null)
                                {
                                    _ = await AppServices.Backend.UploadCustomerAvatarAsync(createRes.Data.Id, avatarPng, ct);
                                }
                                existingCustomers.Add(createRes.Data);
                                created++;
                                Report(tag.Name, $"已导入「{createRes.Data.DisplayName}」", $"  ✓ 新增入库：{createRes.Data.DisplayName} ({createRes.Data.WechatAccount})");
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

            SoftwareBitmap bgra = bmp.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? bmp
                : SoftwareBitmap.Convert(bmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            try
            {
                byte[] pixels = new byte[w * h * 4];
                bgra.CopyToBuffer(pixels.AsBuffer());

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
            }
            finally
            {
                if (!ReferenceEquals(bgra, bmp)) bgra.Dispose();
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

        private async Task<(nint Hwnd, int Pid)> FindOrOpenContactManagerAsync(bool resetExisting, CancellationToken ct)
        {
            var (existingHwnd, existingPid) = FindContactManagerWindow();
            if (existingHwnd != nint.Zero)
            {
                if (!resetExisting) return (existingHwnd, existingPid);

                Win32WindowHelper.CloseWindowGracefully(existingHwnd);
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(120, ct);
                    if (!Win32WindowHelper.IsWindow(existingHwnd) || !Win32WindowHelper.IsWindowVisible(existingHwnd)) break;
                }
                await Task.Delay(180, ct);
            }

            var windows = AppServices.WeChatWindows.EnumerateWindows(includeAllVisibleWindows: false);
            var mainWin = windows.FirstOrDefault(w => w.AccountType == WeChatAccountType.Personal && w.Width >= 400);
            if (mainWin is null) return (nint.Zero, 0);

            NormalizeWindowSize(mainWin.Hwnd, StandardMainLogicalWidth, StandardMainLogicalHeight, offsetX: 40, offsetY: 40);
            await Task.Delay(380, ct);

            if (!Win32WindowHelper.GetWindowRect(mainWin.Hwnd, out var mainRect)) return (nint.Zero, 0);

            uint dpi = Win32WindowHelper.GetDpiForWindow(mainWin.Hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;

            // Click left-rail Contacts icon
            Win32WindowHelper.LeftClickAt(
                mainRect.Left + (int)Math.Round(30 * scale),
                mainRect.Top + (int)Math.Round(152 * scale));
            await Task.Delay(400, ct);

            // Scroll friend list all the way UP to the top
            int listScrollX = mainRect.Left + (int)Math.Round(180 * scale);
            int listScrollY = mainRect.Top + (int)Math.Round(300 * scale);
            for (int s = 0; s < 3; s++)
            {
                Win32WindowHelper.ScrollWheelAt(listScrollX, listScrollY, +12);
                await Task.Delay(120, ct);
            }
            await Task.Delay(250, ct);

            // Locate & click "通讯录管理" button
            var btnPos = await FindTextCenterInWindowAsync(mainWin.Hwnd, mainRect, "通讯录管理", ct);
            Win32WindowHelper.LeftClickAt(
                btnPos?.X ?? (mainRect.Left + (int)Math.Round(190 * scale)),
                btnPos?.Y ?? (mainRect.Top + (int)Math.Round(94 * scale)));

            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(250, ct);
                var found = FindContactManagerWindow();
                if (found.Hwnd != nint.Zero) return found;
            }
            return (nint.Zero, 0);
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
                if (!Win32WindowHelper.IsWindowVisible(hWnd) || Win32WindowHelper.IsIconic(hWnd)) return true;
                _ = Win32WindowHelper.GetWindowThreadProcessId(hWnd, out uint rawPid);
                int pid = unchecked((int)rawPid);
                if (!wechatPids.Contains(pid)) return true;

                string title = Win32WindowHelper.GetWindowTitleText(hWnd);
                string cls = Win32WindowHelper.GetWindowClassName(hWnd);
                if (!Win32WindowHelper.GetWindowRect(hWnd, out var r) || r.Width < 420 || r.Height < 320) return true;

                if (title.Contains("通讯录", StringComparison.Ordinal) ||
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
            uint dpi = Win32WindowHelper.GetDpiForWindow(hWnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            _ = Win32WindowHelper.MoveAndResizeWindow(
                hWnd,
                (int)Math.Round(offsetX * scale),
                (int)Math.Round(offsetY * scale),
                (int)Math.Round(logicalW * scale),
                (int)Math.Round(logicalH * scale));
        }

        private async Task<List<WeChatTagItem>> ScanTagsFromManagerWindowAsync(nint mgrHwnd, CancellationToken ct)
        {
            using var bmp = CaptureWindowSafe(mgrHwnd);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);

            int sidebarMaxX = (int)(bmp.PixelWidth * 0.225);
            var leftLines = ocr.Lines.Where(l => l.Bounds.X < sidebarMaxX).OrderBy(l => l.Bounds.Y).ToList();
            var tagHeader = leftLines.FirstOrDefault(l => l.Text.Trim() is "标签" or "标鉴");
            double minTagY = tagHeader is not null ? tagHeader.Bounds.Y + tagHeader.Bounds.Height : bmp.PixelHeight * 0.26;

            var tags = new List<WeChatTagItem>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in leftLines)
            {
                if (line.Bounds.Y < minTagY) continue;
                string txt = line.Text.Trim();
                if (string.IsNullOrEmpty(txt)) continue;
                if (txt.Contains("新建标签", StringComparison.Ordinal) || txt.Contains("最近群聊", StringComparison.Ordinal)) break;
                if (txt is "朋友权限" or "筛选" or "通讯录管理") continue;

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
                    RelativeX = (line.Bounds.X + line.Bounds.Width / 2.0) / bmp.PixelWidth,
                    RelativeY = (line.Bounds.Y + line.Bounds.Height / 2.0) / bmp.PixelHeight,
                    IsSelected = count > 0
                });
            }

            return tags;
        }

        private async Task<(int X, int Y)?> FindTagClickPositionAsync(nint mgrHwnd, Win32WindowHelper.RECT mgrRect, WeChatTagItem tag, CancellationToken ct)
        {
            using var bmp = CaptureWindowSafe(mgrHwnd);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);

            int sidebarMaxX = (int)(bmp.PixelWidth * 0.225);
            var matched = ocr.Lines
                .Where(l => l.Bounds.X < sidebarMaxX)
                .FirstOrDefault(l =>
                {
                    string clean = TagWithCountRegex().Replace(l.Text.Trim(), "$1").Trim();
                    return clean.Equals(tag.Name, StringComparison.OrdinalIgnoreCase) ||
                           l.Text.Trim().StartsWith(tag.Name, StringComparison.OrdinalIgnoreCase);
                });

            if (matched is not null)
            {
                return (
                    mgrRect.Left + (int)Math.Round(matched.Bounds.X + matched.Bounds.Width / 2.0),
                    mgrRect.Top + (int)Math.Round(matched.Bounds.Y + matched.Bounds.Height / 2.0)
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

        private static async Task WaitForProfilePopupAsync(nint mgrHwnd, int wechatPid, int maxWaitMs, CancellationToken ct)
        {
            int elapsed = 0;
            const int pollStep = 55;
            await Task.Delay(120, ct);
            elapsed += 120;
            while (elapsed < maxWaitMs)
            {
                if (FindProfilePopupWindow(wechatPid, mgrHwnd) != nint.Zero)
                {
                    await Task.Delay(60, ct);
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
            if (Win32WindowHelper.GetWindowRect(hWnd, out var r) && r.Width > 0 && r.Height > 0)
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

        private static CustomerDto? FindExistingCustomer(List<CustomerDto> list, string account, string nickname, string remark)
        {
            if (!string.IsNullOrWhiteSpace(account))
            {
                var byAcc = list.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.WechatAccount) &&
                    c.WechatAccount.Equals(account, StringComparison.OrdinalIgnoreCase));
                if (byAcc is not null) return byAcc;
            }
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                var byName = list.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.WechatName) &&
                    c.WechatName.Equals(nickname, StringComparison.OrdinalIgnoreCase));
                if (byName is not null) return byName;
            }
            if (!string.IsNullOrWhiteSpace(remark))
            {
                var byRem = list.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(c.WechatRemark) &&
                    c.WechatRemark.Equals(remark, StringComparison.OrdinalIgnoreCase));
                if (byRem is not null) return byRem;
            }
            return null;
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
