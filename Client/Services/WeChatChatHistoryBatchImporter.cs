using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class BatchCapturedChatMessage : INotifyPropertyChanged
    {
        private string _direction = "incoming";
        private string _content = string.Empty;
        private bool _isIncluded = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string SenderName { get; init; } = string.Empty;
        public string TimestampText { get; init; } = string.Empty;

        public string Direction
        {
            get => _direction;
            set
            {
                if (_direction != value)
                {
                    _direction = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DirectionLabel));
                    OnPropertyChanged(nameof(IsOutgoing));
                }
            }
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content != value)
                {
                    _content = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (_isIncluded != value)
                {
                    _isIncluded = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOutgoing => _direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
        public string DirectionLabel => IsOutgoing ? "我方 (绿)" : "对方 (白)";

        public string MetaHeader => string.IsNullOrWhiteSpace(TimestampText)
            ? SenderName
            : $"{SenderName} · {TimestampText}";

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class BatchChatHistoryProgress
    {
        public int ScannedPages { get; init; }
        public int CapturedCount { get; init; }
        public int MaxMessages { get; init; }
        public int SkippedDuplicateCount { get; init; }
        public string StatusMessage { get; init; } = string.Empty;
        public string? LogLine { get; init; }
    }

    public sealed class BatchChatHistoryResult
    {
        public bool Success { get; init; }
        public bool WasCancelled { get; init; }
        public string SummaryMessage { get; init; } = string.Empty;
        public List<BatchCapturedChatMessage> Messages { get; init; } = [];
        public int SkippedExistingCount { get; init; }
    }

    /// <summary>
    /// 微信聊天记录自动定位与批量 OCR 采集引擎：
    /// 1. 自动唤起/启动微信并规范主窗口尺寸 (900×656)；
    /// 2. 点击左侧第一个「消息」图标 → 聚焦顶部搜索栏 → 依次尝试微信号、备注名、昵称搜索；
    /// 3. 命中好友则点第一条进入聊天界面，全部未命中则立即中止提示；
    /// 4. 点击聊天区右上角「...」→ OCR 点击「查找聊天内容」弹出独立聊天记录窗口；
    /// 5. 规范聊天记录窗口 (640×780) → 按目标条数上限逐屏翻页 OCR，剔除表情/裁剪乱码/悬停提示，
    ///    连续 3 次滚动无新内容即自动判定到底并按时间正序输出。
    /// </summary>
    public sealed partial class WeChatChatHistoryBatchImporter
    {
        private const int StandardMainLogicalWidth = 900;
        private const int StandardMainLogicalHeight = 656;
        private const int StandardHistoryLogicalWidth = 640;
        private const int StandardHistoryLogicalHeight = 780;
        private const int HardScrollSafetyCap = 240;

        private static readonly HashSet<string> AllowedShortAsciiWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "ok", "yes", "no", "hi", "hey", "bye", "thx", "ty", "vip", "app", "api",
            "pdf", "ppt", "doc", "xls", "wx", "vx", "qq", "ai", "ui", "pm", "hr", "it", "go"
        };

        private Win32WindowHelper.POINT _lastAutoCursor;
        private bool _hasAutoCursor;

        public async Task<BatchChatHistoryResult> CaptureChatHistoryAsync(
            IReadOnlyList<string> searchKeywords,
            IEnumerable<string> customerNameAliases,
            bool isGroupSearch,
            int maxMessages,
            HashSet<string>? existingMessageSignatures,
            IProgress<BatchChatHistoryProgress> progress,
            CancellationToken ct)
        {
            _hasAutoCursor = false;

            var candidates = searchKeywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidates.Count == 0)
            {
                return new BatchChatHistoryResult
                {
                    Success = false,
                    SummaryMessage = "请先填写用于搜索的微信号、备注名或昵称。"
                };
            }

            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                return new BatchChatHistoryResult
                {
                    Success = false,
                    SummaryMessage = AppServices.Ocr.RapidEngine.AvailabilityNote
                };
            }

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in customerNameAliases)
            {
                string norm = NormalizePersonName(raw);
                if (!string.IsNullOrEmpty(norm)) aliases.Add(norm);
            }

            int targetLimit = maxMessages > 0 ? maxMessages : 0; // 0 = unlimited until bottom
            int scannedPages = 0;
            int skippedExisting = 0;
            var collectedNewestFirst = new List<BatchCapturedChatMessage>();
            nint historyHwnd = nint.Zero;

            void Report(string status, string? log = null) =>
                progress.Report(new BatchChatHistoryProgress
                {
                    ScannedPages = scannedPages,
                    CapturedCount = collectedNewestFirst.Count,
                    MaxMessages = targetLimit,
                    SkippedDuplicateCount = skippedExisting,
                    StatusMessage = status,
                    LogLine = log
                });

            try
            {
                // Step 1: Ensure personal WeChat window is visible & ready
                Report("正在检测并唤起微信窗口...", "▶ 正在检测并自动唤起个人微信主窗口...");
                var ensured = await AppServices.WeChatWindows.EnsurePersonalWeChatWindowReadyAsync(
                    autoLaunchIfClosed: true,
                    allowLoginPageSuccess: false,
                    ct: ct);
                if (!ensured.Success || ensured.Window is null)
                {
                    return new BatchChatHistoryResult
                    {
                        Success = false,
                        SummaryMessage = ensured.Message
                    };
                }

                var mainWin = ensured.Window;
                int wechatPid = mainWin.ProcessId;
                if (Win32WindowHelper.IsTargetProcessElevatedMismatch(mainWin.Hwnd))
                {
                    return new BatchChatHistoryResult
                    {
                        Success = false,
                        SummaryMessage = "检测到微信正以管理员权限运行，Windows 安全机制会拦截自动点击。请以管理员身份运行本工作台，或以普通权限重启微信后再试。"
                    };
                }
                Report("已定位微信主窗口，正在归位尺寸...", $"✓ 已定位微信主窗口 (PID {wechatPid})。");

                nint staleHistory = FindChatHistoryWindow(wechatPid, mainWin.Hwnd);
                if (staleHistory != nint.Zero)
                {
                    Win32WindowHelper.CloseWindowGracefully(staleHistory);
                    await Task.Delay(260, ct);
                }

                NormalizeWindowSize(mainWin.Hwnd, StandardMainLogicalWidth, StandardMainLogicalHeight, offsetX: 36, offsetY: 32);
                await Task.Delay(400, ct);

                if (!Win32WindowHelper.GetVisibleWindowRect(mainWin.Hwnd, out var mainRect))
                {
                    return new BatchChatHistoryResult { Success = false, SummaryMessage = "无法读取微信主窗口坐标。" };
                }

                uint dpi = Win32WindowHelper.GetDpiForWindow(mainWin.Hwnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;

                // Step 2: Visually detect & click left-rail first Message icon (fallback to DPI-scaled visible coords)
                EnsureSafeToContinue(mainWin.Hwnd, ct);
                int msgIconX = mainRect.Left + (int)Math.Round(28 * scale);
                int msgIconY = mainRect.Top + (int)Math.Round(104 * scale);
                try
                {
                    using var railBmp = CaptureWindowSafe(mainWin.Hwnd);
                    var railIcons = Win32WindowHelper.DetectWeChatLeftRailIcons(railBmp, scale);
                    if (railIcons.Count >= 2)
                    {
                        double rx = mainRect.Width / (double)Math.Max(1, railBmp.PixelWidth);
                        double ry = mainRect.Height / (double)Math.Max(1, railBmp.PixelHeight);
                        msgIconX = mainRect.Left + (int)Math.Round(railIcons[1].CenterX * rx);
                        msgIconY = mainRect.Top + (int)Math.Round(railIcons[1].CenterY * ry);
                    }
                }
                catch { }
                AutoClick(msgIconX, msgIconY);
                await Task.Delay(280, ct);

                // Step 3: Search with automatic fallback (Account -> Remark -> Nickname)
                int dropLeft = mainRect.Left + (int)Math.Round(54 * scale);
                int dropTop = mainRect.Top + (int)Math.Round(70 * scale);
                int dropW = Math.Min(mainRect.Width - (int)Math.Round(54 * scale), (int)Math.Round(360 * scale));
                int dropH = Math.Min(mainRect.Height - (int)Math.Round(78 * scale), (int)Math.Round(520 * scale));

                (int X, int Y, string MatchedLabel)? hit = null;
                string matchedKeyword = string.Empty;

                for (int idx = 0; idx < candidates.Count; idx++)
                {
                    string kw = candidates[idx];
                    EnsureSafeToContinue(mainWin.Hwnd, ct);
                    Report(
                        $"正在搜索「{kw}」...",
                        idx == 0
                            ? $"▶ 点击顶部搜索栏并输入「{kw}」..."
                            : $"↻ 未命中，自动改用「{kw}」再次搜索...");

                    AutoClick(
                        mainRect.Left + (int)Math.Round(165 * scale),
                        mainRect.Top + (int)Math.Round(52 * scale));
                    await Task.Delay(200, ct);

                    Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_A);
                    await Task.Delay(70, ct);
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_BACK);
                    await Task.Delay(100, ct);
                    Win32WindowHelper.SendUnicodeText(kw);
                    await Task.Delay(950, ct);

                    EnsureSafeToContinue(mainWin.Hwnd, ct);
                    using var dropBmp = Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(dropLeft, dropTop, dropW, dropH);
                    var dropOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(dropBmp, ct);
                    hit = FindFirstSearchResultHit(dropOcr, dropLeft, dropTop, isGroupSearch);
                    if (hit is not null)
                    {
                        matchedKeyword = kw;
                        break;
                    }
                }

                if (hit is null)
                {
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    string triedList = string.Join(" / ", candidates.Select(c => $"「{c}」"));
                    return new BatchChatHistoryResult
                    {
                        Success = false,
                        SummaryMessage = $"已依次尝试搜索 {triedList}，均未在微信中找到匹配联系人，请核对备注名或微信号后再试。"
                    };
                }

                Report($"已搜索到目标，正在进入聊天界面...", $"✓ 通过「{matchedKeyword}」命中「{hit.Value.MatchedLabel}」，进入聊天界面...");
                AutoClick(hit.Value.X, hit.Value.Y);
                await Task.Delay(480, ct);

                // Step 4: Click top-right "..." button with adaptive multi-point retry & locate "查找聊天内容"
                EnsureSafeToContinue(mainWin.Hwnd, ct);
                Report("正在打开聊天信息并进入「查找聊天内容」...", "▶ 自适应点击聊天界面右上角「...」按钮...");

                var findChatBtn = await FindTextInRightDrawerAsync(mainRect, ct);
                if (findChatBtn is null)
                {
                    var moreOffsets = new (int Dx, int Dy)[]
                    {
                        (0, 0),
                        (-10, 0),
                        (6, 4),
                        (-18, -3),
                        (-6, 10)
                    };
                    _ = await Win32WindowHelper.ClickWithAdaptiveRetryAsync(
                        mainWin.Hwnd,
                        mainRect.Right - (int)Math.Round(28 * scale),
                        mainRect.Top + (int)Math.Round(52 * scale),
                        scale,
                        actionLabel: "右上角「...」按钮",
                        verifySucceededAsync: async () =>
                        {
                            EnsureSafeToContinue(mainWin.Hwnd, ct);
                            findChatBtn = await FindTextInRightDrawerAsync(mainRect, ct);
                            return findChatBtn is not null;
                        },
                        log: msg => Report("正在打开聊天信息...", msg),
                        waitAfterClickMs: 420,
                        allowPostMessageFallback: true,
                        customOffsetsLogical: moreOffsets,
                        onCursorMoved: (cx, cy) =>
                        {
                            _lastAutoCursor = new Win32WindowHelper.POINT { X = cx, Y = cy };
                            _hasAutoCursor = true;
                        },
                        ct: ct);
                }

                if (findChatBtn is null)
                {
                    return new BatchChatHistoryResult
                    {
                        Success = false,
                        SummaryMessage = "已进入聊天界面，但多点位尝试点击右上角「...」后仍未在右侧抽屉识别到「查找聊天内容」按钮，请确认窗口未被遮挡。"
                    };
                }

                Report("正在打开独立聊天记录窗口...", "✓ 已找到「查找聊天内容」，正在自适应点击打开聊天记录窗口...");
                var openHistOffsets = new (int Dx, int Dy)[]
                {
                    (0, 0),
                    (-26, 0),
                    (28, 0),
                    (0, 6)
                };
                _ = await Win32WindowHelper.ClickWithAdaptiveRetryAsync(
                    mainWin.Hwnd,
                    findChatBtn.Value.X,
                    findChatBtn.Value.Y,
                    scale,
                    actionLabel: "查找聊天内容",
                    verifySucceededAsync: async () =>
                    {
                        for (int wait = 0; wait < 10; wait++)
                        {
                            historyHwnd = FindChatHistoryWindow(wechatPid, mainWin.Hwnd);
                            if (historyHwnd != nint.Zero) return true;
                            await Task.Delay(240, ct);
                        }
                        return false;
                    },
                    log: msg => Report("正在打开独立聊天记录窗口...", msg),
                    waitAfterClickMs: 260,
                    allowPostMessageFallback: true,
                    customOffsetsLogical: openHistOffsets,
                    onCursorMoved: (cx, cy) =>
                    {
                        _lastAutoCursor = new Win32WindowHelper.POINT { X = cx, Y = cy };
                        _hasAutoCursor = true;
                    },
                    ct: ct);

                if (historyHwnd == nint.Zero)
                {
                    return new BatchChatHistoryResult
                    {
                        Success = false,
                        SummaryMessage = "已多点位尝试点击「查找聊天内容」，但未检测到新弹出的聊天记录窗口。"
                    };
                }

                // Step 5: Normalize chat history window & extract exact customer display name from title
                string histTitle = Win32WindowHelper.GetWindowTitleText(historyHwnd);
                var titleMatch = HistoryWindowTitleRegex().Match(histTitle);
                if (titleMatch.Success)
                {
                    string targetFromTitle = NormalizePersonName(titleMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(targetFromTitle))
                    {
                        aliases.Add(targetFromTitle);
                    }
                }

                NormalizeWindowSize(historyHwnd, StandardHistoryLogicalWidth, StandardHistoryLogicalHeight, offsetX: 120, offsetY: 36);
                await Task.Delay(420, ct);

                if (!Win32WindowHelper.GetVisibleWindowRect(historyHwnd, out var histRect))
                {
                    return new BatchChatHistoryResult { Success = false, SummaryMessage = "无法读取聊天记录窗口坐标。" };
                }

                string limitText = targetLimit > 0 ? $"最多 {targetLimit} 条" : "全部到底";
                Report($"开始采集聊天记录（目标：{limitText}）...", $"✓ 已规范「{histTitle}」（640×780），目标：{limitText}...");

                var seenInRun = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string prevPageFingerprint = string.Empty;
                int stalledScrolls = 0;
                bool reachedLimit = false;

                int MergeParsedPage(List<BatchCapturedChatMessage> pageMsgs)
                {
                    int added = 0;
                    foreach (var msg in pageMsgs)
                    {
                        if (targetLimit > 0 && collectedNewestFirst.Count >= targetLimit)
                        {
                            reachedLimit = true;
                            break;
                        }

                        string runKey = BuildRunDedupKey(msg.Direction, msg.TimestampText, msg.Content);
                        if (!seenInRun.Add(runKey)) continue;

                        if (IsNearDuplicateOfRecent(collectedNewestFirst, msg)) continue;

                        if (existingMessageSignatures is not null &&
                            existingMessageSignatures.Contains(BuildExistingSignature(msg.Direction, msg.Content)))
                        {
                            skippedExisting++;
                            continue;
                        }

                        collectedNewestFirst.Add(msg);
                        added++;

                        if (targetLimit > 0 && collectedNewestFirst.Count >= targetLimit)
                        {
                            reachedLimit = true;
                            break;
                        }
                    }
                    return added;
                }

                for (int page = 0; page < HardScrollSafetyCap; page++)
                {
                    EnsureSafeToContinue(historyHwnd, ct);
                    if (!Win32WindowHelper.GetWindowRect(historyHwnd, out histRect))
                    {
                        throw new OperationCanceledException("聊天记录窗口坐标失效。");
                    }

                    scannedPages = page + 1;
                    OcrRecognitionResult pageOcr;
                    using (var pageBmp = CaptureWindowSafe(historyHwnd))
                    {
                        pageOcr = await AppServices.Ocr.RapidEngine.RecognizeAsync(pageBmp, ct);
                    }

                    // Skip partially-clipped bottom edge row during scrolling (it will be read cleanly on the next screen!)
                    var pageMsgs = ParseHistoryPage(pageOcr, aliases, skipBottomClippedEdge: true);
                    string fingerprint = BuildPageFingerprint(pageOcr);
                    int addedThisPage = MergeParsedPage(pageMsgs);

                    string progressLabel = targetLimit > 0
                        ? $"已采集 {collectedNewestFirst.Count}/{targetLimit} 条（第 {scannedPages} 屏）"
                        : $"已采集 {collectedNewestFirst.Count} 条（第 {scannedPages} 屏）";
                    if (skippedExisting > 0) progressLabel += $" · 跳过重复 {skippedExisting}";

                    Report(progressLabel, $"  第 {scannedPages} 屏：有效 {pageMsgs.Count} 条，新增 {addedThisPage} 条（累计 {collectedNewestFirst.Count} 条）。");

                    if (reachedLimit)
                    {
                        Report($"已达到设定上限 {targetLimit} 条，停止采集。", $"✓ 已采集满 {targetLimit} 条消息，停止滚动。");
                        break;
                    }

                    bool screenUnchanged = page > 0 && (fingerprint == prevPageFingerprint || addedThisPage == 0);
                    prevPageFingerprint = fingerprint;

                    if (screenUnchanged)
                    {
                        stalledScrolls++;
                        if (stalledScrolls >= 3)
                        {
                            // Final bottom pass: include bottom edge row since no further scrolling is possible
                            var bottomMsgs = ParseHistoryPage(pageOcr, aliases, skipBottomClippedEdge: false);
                            int tailAdded = MergeParsedPage(bottomMsgs);
                            if (tailAdded > 0)
                            {
                                AppendTailLog(Report, tailAdded, collectedNewestFirst.Count);
                            }
                            Report($"已到达聊天记录底部，共采集 {collectedNewestFirst.Count} 条。", "✓ 连续 3 次滚动无新内容，已到达聊天记录底部。");
                            break;
                        }
                    }
                    else
                    {
                        stalledScrolls = 0;
                    }

                    // Place cursor near the right whitespace margin so it avoids hovering rows and triggering "定位到聊天位置"
                    int scrollX = histRect.Left + (int)(histRect.Width * 0.90);
                    int scrollY = histRect.Top + (int)(histRect.Height * 0.56);
                    AutoScroll(scrollX, scrollY, -5);
                    await Task.Delay(340, ct);
                }

                if (historyHwnd != nint.Zero && Win32WindowHelper.IsWindow(historyHwnd))
                {
                    Win32WindowHelper.CloseWindowGracefully(historyHwnd);
                }

                // Reverse from newest-first into chronological oldest-first order
                collectedNewestFirst.Reverse();

                string summary = collectedNewestFirst.Count > 0
                    ? $"采集完成：共获取 {collectedNewestFirst.Count} 条有效文本消息" +
                      (skippedExisting > 0 ? $"（已自动跳过 {skippedExisting} 条重复消息）。" : "。")
                    : skippedExisting > 0
                        ? $"采集完成：扫描到的有效消息均已存在于历史记录中（跳过 {skippedExisting} 条重复）。"
                        : "未在聊天记录窗口中识别到有效文本消息（纯表情/图片已自动忽略）。";

                return new BatchChatHistoryResult
                {
                    Success = true,
                    SummaryMessage = summary,
                    Messages = collectedNewestFirst,
                    SkippedExistingCount = skippedExisting
                };
            }
            catch (OperationCanceledException ex)
            {
                collectedNewestFirst.Reverse();
                string reason = string.IsNullOrWhiteSpace(ex.Message) || ex.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                    ? "已安全停止采集。"
                    : ex.Message;

                return new BatchChatHistoryResult
                {
                    Success = collectedNewestFirst.Count > 0,
                    WasCancelled = true,
                    SummaryMessage = $"{reason}（已保留已采集的 {collectedNewestFirst.Count} 条消息）",
                    Messages = collectedNewestFirst,
                    SkippedExistingCount = skippedExisting
                };
            }
        }

        private static void AppendTailLog(Action<string, string?> report, int tailAdded, int total) =>
            report($"已补充底部 {tailAdded} 条，共 {total} 条。", $"  补充底部边缘 {tailAdded} 条消息。");

        // =====================================================================
        // OCR Parsing, Emoji Stripping & Garbage Token Filtering
        // =====================================================================

        private static (int X, int Y, string MatchedLabel)? FindFirstSearchResultHit(
            OcrRecognitionResult dropOcr,
            int dropLeft,
            int dropTop,
            bool isGroupSearch)
        {
            var lines = dropOcr.Lines
                .Where(l => !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Bounds.Y)
                .ThenBy(l => l.Bounds.X)
                .ToList();

            string section = "none";
            foreach (var line in lines)
            {
                string txt = line.Text.Trim();

                if (txt.Contains("网络查找", StringComparison.Ordinal) ||
                    txt.Contains("搜索网络结果", StringComparison.Ordinal) ||
                    txt.Contains("搜一搜", StringComparison.Ordinal) ||
                    txt == "更多" ||
                    txt.StartsWith("查找微信号", StringComparison.Ordinal))
                {
                    break;
                }

                if (txt is "最常使用" or "联系人" or "好友")
                {
                    section = "contact";
                    continue;
                }
                if (txt is "群聊" or "聊天记录" or "功能" or "公众号" or "小程序")
                {
                    section = txt == "群聊" ? "group" : "other";
                    continue;
                }
                if (txt.StartsWith("查看全部", StringComparison.Ordinal))
                {
                    continue;
                }

                bool sectionAllowed = isGroupSearch
                    ? section is "contact" or "group"
                    : section is "contact";

                bool looksLikeWechatAccountMatch = !isGroupSearch &&
                    section is "none" or "contact" &&
                    (txt.StartsWith("微信号:", StringComparison.OrdinalIgnoreCase) ||
                     txt.StartsWith("微信号：", StringComparison.OrdinalIgnoreCase));

                if (sectionAllowed || looksLikeWechatAccountMatch)
                {
                    if (line.Bounds.X > dropOcr.ImageWidth * 0.78 || (txt.Length == 1 && !char.IsLetterOrDigit(txt[0])))
                    {
                        continue;
                    }

                    int clickX = dropLeft + (int)Math.Round(Math.Min(line.Bounds.X + line.Bounds.Width / 2.0, dropOcr.ImageWidth * 0.55));
                    int clickY = dropTop + (int)Math.Round(line.Bounds.Y + line.Bounds.Height / 2.0);
                    return (clickX, clickY, txt);
                }
            }

            return null;
        }

        private static async Task<(int X, int Y)?> FindTextInRightDrawerAsync(
            Win32WindowHelper.RECT mainRect,
            CancellationToken ct)
        {
            int drawerLeft = mainRect.Left + (int)(mainRect.Width * 0.56);
            int drawerTop = mainRect.Top;
            int drawerW = mainRect.Right - drawerLeft;
            int drawerH = mainRect.Height;
            if (drawerW <= 40 || drawerH <= 40) return null;

            using var bmp = Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(drawerLeft, drawerTop, drawerW, drawerH);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);

            var target = ocr.Lines.FirstOrDefault(l =>
            {
                string t = l.Text.Replace(" ", "").Trim();
                return t.Contains("查找聊天内容", StringComparison.Ordinal) ||
                       t.Contains("查看聊天内容", StringComparison.Ordinal) ||
                       t.Contains("查找聊天记录", StringComparison.Ordinal) ||
                       t.Contains("聊天内容", StringComparison.Ordinal);
            });

            if (target is null) return null;

            return (
                drawerLeft + (int)Math.Round(target.Bounds.X + target.Bounds.Width / 2.0),
                drawerTop + (int)Math.Round(target.Bounds.Y + target.Bounds.Height / 2.0)
            );
        }

        internal static List<BatchCapturedChatMessage> ParseHistoryPage(
            OcrRecognitionResult ocr,
            HashSet<string> normalizedCustomerAliases,
            bool skipBottomClippedEdge = true)
        {
            var results = new List<BatchCapturedChatMessage>();
            if (ocr.Lines.Count == 0 || ocr.ImageWidth <= 0 || ocr.ImageHeight <= 0) return results;

            double w = ocr.ImageWidth;
            double h = ocr.ImageHeight;

            var filterLine = ocr.Lines
                .Where(l => l.Bounds.Y < h * 0.30)
                .FirstOrDefault(l =>
                {
                    string t = l.Text.Trim();
                    return t.Contains("图片与视频", StringComparison.Ordinal) ||
                           t == "文件" ||
                           t == "日期" ||
                           t == "小程序";
                });

            double contentMinY = filterLine is not null
                ? filterLine.Bounds.Y + filterLine.Bounds.Height + 12
                : h * 0.17;

            // Strict left margin excludes the avatar photo column (which ends around 10.8% width)
            double textMinX = w * 0.115;

            var bodyCandidates = ocr.Lines
                .Where(l => l.Bounds.Y >= contentMinY && !string.IsNullOrWhiteSpace(l.Text))
                .OrderBy(l => l.Bounds.Y)
                .ThenBy(l => l.Bounds.X)
                .ToList();

            var timestampLines = bodyCandidates
                .Where(l => l.Bounds.X >= w * 0.50 && IsHistoryTimestamp(l.Text.Trim()))
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            if (timestampLines.Count == 0) return results;

            double yBandTol = Math.Max(14, h * 0.022);
            var usedHeaderIndices = new HashSet<int>();

            var anchors = new List<(OcrLineResult TimeLine, OcrLineResult? SenderLine)>(timestampLines.Count);
            foreach (var ts in timestampLines)
            {
                usedHeaderIndices.Add(ts.Index);
                double tsMidY = ts.Bounds.Y + ts.Bounds.Height / 2.0;

                var senderLine = bodyCandidates
                    .Where(l =>
                        !usedHeaderIndices.Contains(l.Index) &&
                        l.Bounds.X >= textMinX &&
                        l.Bounds.X < w * 0.62 &&
                        Math.Abs((l.Bounds.Y + l.Bounds.Height / 2.0) - tsMidY) <= yBandTol)
                    .OrderBy(l => Math.Abs((l.Bounds.Y + l.Bounds.Height / 2.0) - tsMidY))
                    .ThenBy(l => l.Bounds.X)
                    .FirstOrDefault();

                if (senderLine is not null)
                {
                    usedHeaderIndices.Add(senderLine.Index);
                }

                anchors.Add((ts, senderLine));
            }

            // Bottom safety zone to drop partially-clipped half-rows during active scrolling
            double bottomSafeCutoff = skipBottomClippedEdge ? h - Math.Max(34, h * 0.048) : h - 2;

            for (int i = 0; i < anchors.Count; i++)
            {
                var (tsLine, senderLine) = anchors[i];
                double rowTop = Math.Max(tsLine.Bounds.Y + tsLine.Bounds.Height, senderLine is not null ? senderLine.Bounds.Y + senderLine.Bounds.Height : 0) - 2;
                double rowBottom = i + 1 < anchors.Count
                    ? Math.Min(anchors[i + 1].TimeLine.Bounds.Y, anchors[i + 1].SenderLine?.Bounds.Y ?? double.MaxValue) - 4
                    : h - 2;

                // If this is the last anchor on screen and it sits right at the clipped bottom edge, defer it to the next scroll screen
                if (skipBottomClippedEdge && i == anchors.Count - 1 && rowTop > bottomSafeCutoff - 26)
                {
                    continue;
                }

                var rawLines = bodyCandidates
                    .Where(l =>
                        !usedHeaderIndices.Contains(l.Index) &&
                        l.Bounds.X >= textMinX &&
                        (l.Bounds.Y + l.Bounds.Height / 2.0) > rowTop &&
                        (l.Bounds.Y + l.Bounds.Height / 2.0) < rowBottom)
                    .OrderBy(l => l.Bounds.Y)
                    .ThenBy(l => l.Bounds.X)
                    .ToList();

                if (rawLines.Count == 0) continue;

                // If the last line of the last row is clipped at the bottom edge while scrolling, skip whole row (next scroll captures it intact)
                if (skipBottomClippedEdge && i == anchors.Count - 1 && rawLines[^1].Bounds.Y + rawLines[^1].Bounds.Height > bottomSafeCutoff)
                {
                    continue;
                }

                var cleanedLines = new List<string>(rawLines.Count);
                foreach (var line in rawLines)
                {
                    string cleaned = SanitizeHistoryLine(line, w);
                    if (!string.IsNullOrEmpty(cleaned))
                    {
                        cleanedLines.Add(cleaned);
                    }
                }

                if (cleanedLines.Count == 0) continue;

                string content = string.Join("\n", cleanedLines).Trim();
                if (string.IsNullOrEmpty(content)) continue;

                string senderName = senderLine?.Text.Trim() ?? string.Empty;
                bool isIncoming = IsCustomerSender(senderName, normalizedCustomerAliases);

                results.Add(new BatchCapturedChatMessage
                {
                    SenderName = string.IsNullOrEmpty(senderName) ? (isIncoming ? "客户" : "我方") : senderName,
                    TimestampText = tsLine.Text.Trim(),
                    Direction = isIncoming ? "incoming" : "outgoing",
                    Content = content,
                    IsIncluded = true
                });
            }

            return results;
        }

        /// <summary>
        /// 清理单行 OCR 文本：过滤悬停按钮「定位到聊天位置」、剥离 Unicode 表情与纯表情占位、
        /// 剔除 RapidOCR 对微信小表情/头像/半截边角误识别出的短乱码字母（如 gp- / oi / yp / OR）。
        /// </summary>
        private static string SanitizeHistoryLine(OcrLineResult line, double imageWidth)
        {
            string raw = line.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            // 1. Filter WeChat UI hover/chrome text ("定位到聊天位置" on the right side)
            string compact = raw.Replace(" ", "");
            if (compact.Contains("定位到聊天位置", StringComparison.Ordinal) ||
                compact == "定位到聊天" ||
                (line.Bounds.X > imageWidth * 0.68 && compact.Contains("聊天位置", StringComparison.Ordinal)))
            {
                return string.Empty;
            }

            // 2. Drop very low-confidence OCR hallucinations (usually stickers/emojis/avatars)
            if (line.Confidence > 0 && line.Confidence < 0.68f)
            {
                return string.Empty;
            }

            // 3. Strip Unicode emoji/pictograph symbols & standalone WeChat [表情] placeholders
            string cleaned = StripEmojisAndNonStandardSymbols(raw).Trim();
            if (string.IsNullOrEmpty(cleaned)) return string.Empty;

            // Remove pure bracketed emoji tags like "[微笑]" "[捂脸]" "[动画表情]" "[表情]"
            string withoutBracketEmojis = BracketEmojiTagRegex().Replace(cleaned, "").Trim();
            if (string.IsNullOrEmpty(withoutBracketEmojis))
            {
                return string.Empty;
            }
            cleaned = withoutBracketEmojis;

            // 4. Keep any line containing Chinese characters (real chat text)
            if (ContainsCjk(cleaned))
            {
                return cleaned;
            }

            // 5. Keep pure numbers (e.g. "666", "100", phone numbers)
            string alnumOnly = NonAlnumRegex().Replace(cleaned, "");
            if (alnumOnly.Length == 0)
            {
                // Punctuation-only noise like "-", "...", "·"
                return string.Empty;
            }

            if (alnumOnly.All(char.IsDigit))
            {
                // Single stray digit with lower confidence is often an emoji artifact
                if (alnumOnly.Length == 1 && line.Confidence > 0 && line.Confidence < 0.90f) return string.Empty;
                return cleaned;
            }

            // 6. Short non-Chinese ASCII tokens (length <= 4): major source of emoji OCR artifacts like "gp-", "yp", "oi", "OR"
            if (cleaned.Length <= 4)
            {
                if (AllowedShortAsciiWords.Contains(alnumOnly) && cleaned.Length == alnumOnly.Length)
                {
                    return cleaned;
                }
                return string.Empty;
            }

            // 7. Longer non-Chinese text (URLs, WeChat IDs, English sentences): require decent confidence
            if (line.Confidence > 0 && line.Confidence < 0.78f)
            {
                return string.Empty;
            }

            return cleaned;
        }

        private static string StripEmojisAndNonStandardSymbols(string input)
        {
            var sb = new StringBuilder(input.Length);
            foreach (var rune in input.EnumerateRunes())
            {
                int v = rune.Value;
                // Skip Supplementary Plane emojis/pictographs (U+1F000..U+1FAFF), Misc Symbols & Dingbats (U+2600..U+27BF),
                // Variation Selectors (U+FE00..U+FE0F), ZWJ (U+200D), control chars, replacement char
                if ((v >= 0x1F000 && v <= 0x1FAFF) ||
                    (v >= 0x2600 && v <= 0x27BF) ||
                    (v >= 0xFE00 && v <= 0xFE0F) ||
                    v == 0x200D ||
                    v == 0x20E3 ||
                    v == 0xFFFD ||
                    (v < 0x20 && v != '\n'))
                {
                    continue;
                }
                sb.Append(rune);
            }
            return sb.ToString();
        }

        private static bool ContainsCjk(string s)
        {
            foreach (char ch in s)
            {
                if (ch >= 0x4E00 && ch <= 0x9FFF) return true;
            }
            return false;
        }

        private static bool IsNearDuplicateOfRecent(List<BatchCapturedChatMessage> collected, BatchCapturedChatMessage candidate)
        {
            int start = Math.Max(0, collected.Count - 12);
            string candNorm = NormalizeContentForMatch(candidate.Content);
            if (candNorm.Length < 4) return false;

            for (int i = start; i < collected.Count; i++)
            {
                var prev = collected[i];
                if (!prev.Direction.Equals(candidate.Direction, StringComparison.OrdinalIgnoreCase)) continue;
                if (!prev.TimestampText.Equals(candidate.TimestampText, StringComparison.OrdinalIgnoreCase)) continue;

                string prevNorm = NormalizeContentForMatch(prev.Content);
                if (prevNorm.Equals(candNorm, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string BuildPageFingerprint(OcrRecognitionResult ocr)
        {
            var topLines = ocr.Lines
                .Where(l => l.Bounds.Y >= ocr.ImageHeight * 0.16 && l.Bounds.X >= ocr.ImageWidth * 0.11)
                .OrderBy(l => l.Bounds.Y)
                .Select(l => l.Text.Trim())
                .Where(t => !t.Contains("定位到聊天位置", StringComparison.Ordinal));
            return string.Join("|", topLines);
        }

        private static bool IsCustomerSender(string senderName, HashSet<string> normalizedAliases)
        {
            if (string.IsNullOrWhiteSpace(senderName) || normalizedAliases.Count == 0) return true;
            string normSender = NormalizePersonName(senderName);
            if (string.IsNullOrEmpty(normSender)) return true;

            foreach (string alias in normalizedAliases)
            {
                if (normSender.Equals(alias, StringComparison.OrdinalIgnoreCase)) return true;
                if (normSender.Length >= 2 && alias.StartsWith(normSender, StringComparison.OrdinalIgnoreCase)) return true;
                if (alias.Length >= 2 && normSender.StartsWith(alias, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsHistoryTimestamp(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 28) return false;
            return HistoryTimestampRegex().IsMatch(text);
        }

        public static string BuildExistingSignature(string direction, string content)
        {
            string norm = NormalizeContentForMatch(content);
            return $"{direction.ToLowerInvariant()}|{norm}";
        }

        private static string BuildRunDedupKey(string direction, string timestamp, string content)
        {
            string normTime = timestamp.Replace(" ", "").Trim();
            string normContent = NormalizeContentForMatch(content);
            return $"{direction.ToLowerInvariant()}|{normTime}|{normContent}";
        }

        private static string NormalizeContentForMatch(string content) =>
            WhitespaceRegex().Replace(content.Trim(), " ");

        private static string NormalizePersonName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            return raw.Replace("\r", "")
                .Replace("\n", "")
                .Replace(" ", "")
                .TrimEnd('.', '…', '·', '。');
        }

        // =====================================================================
        // Window Finding, Capture & Safety Guards
        // =====================================================================

        private static nint FindChatHistoryWindow(int wechatPid, nint mainHwnd)
        {
            if (wechatPid <= 0) return nint.Zero;
            nint found = nint.Zero;

            Win32WindowHelper.EnumWindows((hWnd, lParam) =>
            {
                if (hWnd == mainHwnd || !Win32WindowHelper.IsWindow(hWnd) || !Win32WindowHelper.IsWindowVisible(hWnd))
                {
                    return true;
                }

                _ = Win32WindowHelper.GetWindowThreadProcessId(hWnd, out uint rawPid);
                if (unchecked((int)rawPid) != wechatPid) return true;

                string title = Win32WindowHelper.GetWindowTitleText(hWnd);
                string cls = Win32WindowHelper.GetWindowClassName(hWnd);

                if (title.Contains("聊天记录", StringComparison.Ordinal) ||
                    cls.Contains("ChatRecord", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("MsgSearch", StringComparison.OrdinalIgnoreCase) ||
                    cls.Contains("SearchChat", StringComparison.OrdinalIgnoreCase))
                {
                    found = hWnd;
                    return false;
                }

                return true;
            }, nint.Zero);

            return found;
        }

        private static void NormalizeWindowSize(nint hWnd, int logicalW, int logicalH, int offsetX, int offsetY)
        {
            _ = Win32WindowHelper.NormalizeWindowOnCurrentMonitor(hWnd, logicalW, logicalH, offsetX, offsetY);
        }

        private static SoftwareBitmap CaptureWindowSafe(nint hWnd)
        {
            if (Win32WindowHelper.GetVisibleWindowRect(hWnd, out var r) && r.Width > 0 && r.Height > 0)
            {
                return Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(r.Left, r.Top, r.Width, r.Height);
            }
            return Win32WindowHelper.CaptureWindowToSoftwareBitmap(hWnd);
        }

        private void EnsureSafeToContinue(nint activeHwnd, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Win32WindowHelper.IsEscapePressed())
            {
                throw new OperationCanceledException("检测到按下 ESC 键，已安全停止采集。");
            }
            if (!Win32WindowHelper.IsWindow(activeHwnd) || Win32WindowHelper.IsIconic(activeHwnd))
            {
                throw new OperationCanceledException("目标微信窗口已最小化或关闭，已安全停止采集。");
            }
            if (_hasAutoCursor && Win32WindowHelper.GetCursorPos(out var cur))
            {
                int dx = cur.X - _lastAutoCursor.X;
                int dy = cur.Y - _lastAutoCursor.Y;
                if (dx * dx + dy * dy > 110 * 110)
                {
                    throw new OperationCanceledException("检测到人工移动鼠标，已自动安全中止采集。");
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

        [GeneratedRegex(@"与[""“「](.+?)[""”」]的聊天记录")]
        private static partial Regex HistoryWindowTitleRegex();

        [GeneratedRegex(@"(\d{4}年\d{1,2}月\d{1,2}日|\d{4}[/\-]\d{1,2}[/\-]\d{1,2}|\d{1,2}月\d{1,2}日|昨天|前天|星期[一二三四五六日天]|周[一二三四五六日天]).*\d{1,2}:\d{2}|^\d{1,2}:\d{2}$")]
        private static partial Regex HistoryTimestampRegex();

        [GeneratedRegex(@"\[[\u4e00-\u9fa5A-Za-z0-9]{1,8}\]")]
        private static partial Regex BracketEmojiTagRegex();

        [GeneratedRegex(@"[^A-Za-z0-9]")]
        private static partial Regex NonAlnumRegex();

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
