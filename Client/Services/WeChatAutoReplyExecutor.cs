using System.Diagnostics;
using System.Text.RegularExpressions;
using Client.Models;

namespace Client.Services
{
    public sealed class AutoReplyAutomationResult
    {
        public bool Success { get; init; }
        public bool WasSent { get; init; }
        public bool WasCancelledByEsc { get; init; }
        public string Message { get; init; } = string.Empty;
        public long ElapsedMs { get; init; }
    }

    /// <summary>
    /// Visual OCR + Win32 hardware-input automation executor for WeChat PC (supports WeChat 4.x &amp; 3.x):
    /// 1. Restores &amp; activates the WeChat main window (with UIPI privilege diagnostics)
    /// 2. Uses screen-composited BitBlt over WeChat's window rect (captures floating Qt search dropdown popups that PrintWindow cannot see!)
    /// 3. OCR locates top-left search box -> clicks (flashing ClickVisualOverlay) -> pastes group name
    /// 4. Polls &amp; confirms the floating search dropdown ACTUALLY rendered and contains target group under "群聊" BEFORE clicking
    /// 5. Clicks the matched row without reactivating main window (so Qt popup stays open), then verifies right chat header switched
    ///    to the target group with up to 3 automatic adaptive retry rounds (including coordinate offsets &amp; re-search recovery)
    /// 6. OCR locates bottom-right "发送" button to compute exact message input box -> clicks -> pastes AI reply -> optionally clicks "发送"
    /// Pressing ESC at any step aborts immediately.
    /// </summary>
    public sealed partial class WeChatAutoReplyExecutor
    {
        private static readonly string[] NonGroupDropdownHeaders =
        [
            "搜索网络结果", "聊天记录", "联系人", "公众号", "服务号", "视频号",
            "功能", "文章", "搜一搜", "小程序", "最常使用", "朋友圈"
        ];

        private static readonly string[] DropdownPresenceMarkers =
        [
            "群聊", "搜索网络结果", "聊天记录", "联系人", "最常使用", "搜一搜", "功能"
        ];

        private sealed record MatchedDropdownLine(
            string Text,
            PixelRect Bounds,
            double Score,
            bool ConfirmedDropdownVisible,
            bool InsideGroupSection);

        public async Task<AutoReplyAutomationResult> SendReplyToGroupAsync(
            string groupName,
            string replyText,
            AutoReplyConfigData config,
            bool? overrideAutoSend = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string cleanGroup = (groupName ?? string.Empty).Trim();
            string cleanReply = (replyText ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanGroup))
            {
                return Fail("群名称为空，无法定位微信群聊。", sw);
            }
            if (string.IsNullOrWhiteSpace(cleanReply))
            {
                return Fail("回复内容为空，取消发送。", sw);
            }
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                string note = AppServices.Ocr.RapidEngine.AvailabilityNote;
                AppServices.Logs.Error("AUTO", $"本地识别组件不可用：{note}");
                return Fail($"本地图文识别组件不可用：{note}", sw);
            }

            bool autoSend = overrideAutoSend ?? config.AutoSendAfterPaste;
            int slowPad = config.SlowStableMode ? 260 : 0;
            bool prevOverlayEnabled = ClickVisualOverlay.IsEnabled;
            ClickVisualOverlay.IsEnabled = config.ShowClickOverlay;
            string? savedClipboardText = config.RestoreClipboardAfterSend
                ? Win32WindowHelper.TryGetClipboardUnicodeText()
                : null;

            AppServices.Logs.Info("AUTO",
                $"开始微信自动回复流程：群「{cleanGroup}」，模式={(autoSend ? "粘贴并自动发送" : "仅粘贴待确认")}，遮罩={(config.ShowClickOverlay ? "开" : "关")}，回复长度={cleanReply.Length} 字");

            try
            {
                if (CheckEscAborted(out var escRes, sw)) return escRes;

                // Step 1: Ensure WeChat window is visible & foreground
                var ensure = await AppServices.WeChatWindows.EnsurePersonalWeChatWindowReadyAsync(
                    autoLaunchIfClosed: false,
                    allowLoginPageSuccess: false,
                    ct: ct);
                if (!ensure.Success || ensure.Window is null)
                {
                    AppServices.Logs.Warn("AUTO", $"未能唤起微信主窗口：{ensure.Message}");
                    return Fail($"未能唤起微信主窗口：{ensure.Message}", sw);
                }

                nint hwnd = ensure.Window.Hwnd;
                string? uipiErr = Win32WindowHelper.GetPrivilegeMismatchDetail(hwnd);
                if (!string.IsNullOrWhiteSpace(uipiErr))
                {
                    AppServices.Logs.Error("AUTO", uipiErr);
                    return Fail(uipiErr, sw);
                }

                bool activated = Win32WindowHelper.EnsureWindowTrulyActive(hwnd);
                await Task.Delay(240 + slowPad, ct);

                if (!Win32WindowHelper.GetWindowRect(hwnd, out var rawRect) || rawRect.Width < 300 || rawRect.Height < 250)
                {
                    return Fail("无法获取有效的微信窗口坐标尺寸。", sw);
                }

                uint dpi = Win32WindowHelper.GetDpiForWindow(hwnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                AppServices.Logs.Info("AUTO",
                    $"微信主窗口已就绪：PID={ensure.Window.ProcessId}, 前台={(activated ? "是" : "否")}, 坐标=({rawRect.Left},{rawRect.Top}), 尺寸={rawRect.Width}x{rawRect.Height}, DPI={dpi}({scale:F2}x)");

                if (CheckEscAborted(out escRes, sw)) return escRes;

                // Step 2: Screen-composited OCR to locate search bar (dismiss any stale search dropdown first if open)
                var initialOcr = await CaptureScreenOverWindowAndOcrAsync(hwnd, rawRect, "主窗口定位搜索栏", ct);
                if (initialOcr is null)
                {
                    return Fail("截取微信窗口画面失败，请确认微信窗口未被最小化。", sw);
                }

                if (IsSearchDropdownStillOpen(initialOcr, initialOcr.ImageHeight * 0.07))
                {
                    AppServices.Logs.Info("AUTO", "检测到微信当前已有残留搜索下拉框，先按 ESC 关闭后再定位搜索栏。");
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    await Task.Delay(240 + slowPad, ct);
                    var cleanOcr = await CaptureScreenOverWindowAndOcrAsync(hwnd, rawRect, "关闭残留搜索框后重定位", ct);
                    if (cleanOcr is not null) initialOcr = cleanOcr;
                }

                var (searchX, searchY, searchBottomImgY) = LocateSearchBar(initialOcr, rawRect, scale);

                // Step 3 + 4: Search group -> confirm dropdown actually rendered -> click with multi-round retry & title verification
                const int maxSwitchRounds = 3;
                OcrRecognitionResult? verifiedChatOcr = null;
                string lastHeaderSeen = string.Empty;
                bool switchedOk = false;

                // Offsets in logical px to explore across retries in case text center wasn't the best hit area
                (int Dx, int Dy)[] clickOffsets =
                [
                    (0, 0),
                    (-22, 0),
                    (24, 2)
                ];

                for (int round = 1; round <= maxSwitchRounds; round++)
                {
                    if (CheckEscAborted(out escRes, sw)) return escRes;

                    // Re-read fresh window rect in case window moved slightly
                    if (Win32WindowHelper.GetWindowRect(hwnd, out var latestRect) && latestRect.Width >= 300 && latestRect.Height >= 250)
                    {
                        rawRect = latestRect;
                    }

                    AppServices.Logs.Info("AUTO", $"第 {round}/{maxSwitchRounds} 轮切群尝试：点击搜索栏 ({searchX},{searchY}) 并输入「{cleanGroup}」");
                    Win32WindowHelper.LeftClickAt(searchX, searchY, hwnd, $"点击微信搜索栏(第{round}轮)");
                    await Task.Delay(200 + slowPad, ct);

                    if (CheckEscAborted(out escRes, sw)) return escRes;

                    await InputSearchKeywordAsync(cleanGroup, ct);

                    // Poll & confirm the floating search dropdown ACTUALLY appeared with target group under "群聊"
                    var (dropdownOcr, matchedGroup) = await WaitForGroupInDropdownAsync(
                        hwnd, rawRect, cleanGroup, searchBottomImgY, slowPad, ct);

                    if (CheckEscAborted(out escRes, sw))
                    {
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        return escRes;
                    }

                    if (dropdownOcr is null || matchedGroup is null)
                    {
                        AppServices.Logs.Warn("AUTO", $"第 {round}/{maxSwitchRounds} 轮未在搜索下拉框中确认到群聊「{cleanGroup}」，关闭搜索框准备重试...");
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        await Task.Delay(320 + slowPad, ct);
                        continue;
                    }

                    // Click matched group row WITHOUT reactivating mainHwnd (so floating Qt dropdown popup is NOT dismissed!)
                    var (dx, dy) = clickOffsets[Math.Min(round - 1, clickOffsets.Length - 1)];
                    double targetImgX = matchedGroup.Bounds.X + matchedGroup.Bounds.Width * 0.5 + dx * scale;
                    double targetImgY = matchedGroup.Bounds.Y + matchedGroup.Bounds.Height * 0.5 + dy * scale;
                    var (grpClickX, grpClickY) = ImageToScreen(rawRect, dropdownOcr, targetImgX, targetImgY);

                    AppServices.Logs.Info("AUTO",
                        $"已确认下拉框出现群聊「{matchedGroup.Text}」(匹配分={matchedGroup.Score:F2}, 群聊栏目={(matchedGroup.InsideGroupSection ? "是" : "顶部区")}) -> 执行第 {round} 轮点击 ({grpClickX},{grpClickY})");

                    var clickDiag = Win32WindowHelper.LeftClickAt(
                        grpClickX,
                        grpClickY,
                        hwnd,
                        $"点击群聊搜索结果「{cleanGroup}」#{round}",
                        skipReactivate: true);

                    await Task.Delay(500 + slowPad, ct);

                    if (CheckEscAborted(out escRes, sw)) return escRes;

                    // Verify click succeeded by capturing screen over WeChat & checking right chat title bar!
                    var afterClickOcr = await CaptureScreenOverWindowAndOcrAsync(hwnd, rawRect, $"核对切群结果(第{round}轮)", ct);
                    if (afterClickOcr is null)
                    {
                        continue;
                    }

                    var (titleOk, detectedHeader) = VerifyOpenedChatTitle(afterClickOcr, cleanGroup);
                    bool dropdownStillOpen = IsSearchDropdownStillOpen(afterClickOcr, searchBottomImgY);
                    lastHeaderSeen = detectedHeader;

                    // If dropdown is still stuck open (whether title changed or not), press Enter once while the matched item is focused
                    if (dropdownStillOpen)
                    {
                        AppServices.Logs.Warn("AUTO",
                            $"第 {round} 轮点击后下拉框仍未关闭（命中窗口={clickDiag.HitWindowClass}, 标题=「{detectedHeader}」），补发回车确认打开并收起搜索框...");
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                        await Task.Delay(480 + slowPad, ct);

                        afterClickOcr = await CaptureScreenOverWindowAndOcrAsync(hwnd, rawRect, $"回车补确认切群(第{round}轮)", ct);
                        if (afterClickOcr is not null)
                        {
                            (titleOk, detectedHeader) = VerifyOpenedChatTitle(afterClickOcr, cleanGroup);
                            dropdownStillOpen = IsSearchDropdownStillOpen(afterClickOcr, searchBottomImgY);
                            lastHeaderSeen = detectedHeader;
                        }
                    }

                    if (!dropdownStillOpen && (titleOk || !config.VerifyGroupTitleBeforePaste))
                    {
                        switchedOk = true;
                        verifiedChatOcr = afterClickOcr;
                        AppServices.Logs.Success("AUTO",
                            $"第 {round} 轮切群确认成功（目标=「{cleanGroup}」，当前聊天窗标题=「{detectedHeader}」）。");
                        break;
                    }

                    AppServices.Logs.Warn("AUTO",
                        $"第 {round}/{maxSwitchRounds} 轮点击后聊天窗标题仍为「{detectedHeader}」（目标「{cleanGroup}」），将自动重试...");
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    await Task.Delay(320 + slowPad, ct);
                }

                if (!switchedOk || verifiedChatOcr is null)
                {
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    string reason = string.IsNullOrWhiteSpace(lastHeaderSeen)
                        ? $"连续 {maxSwitchRounds} 次搜索均未在微信「群聊」栏目中确认到「{cleanGroup}」（已安全中止，未误发消息）。"
                        : $"连续 {maxSwitchRounds} 次尝试点击进入群「{cleanGroup}」未成功（当前聊天标题仍为「{lastHeaderSeen}」，已安全中止发送）。";
                    AppServices.Logs.Warn("AUTO", reason);
                    return Fail(reason, sw);
                }

                if (CheckEscAborted(out escRes, sw)) return escRes;

                // Step 5: Locate "发送" button & message input box -> click -> paste AI reply -> optionally click "发送"
                var sendBtnLine = LocateSendButton(verifiedChatOcr);
                int inputX;
                int inputY;
                int sendX = 0;
                int sendY = 0;

                if (sendBtnLine is not null)
                {
                    (sendX, sendY) = ImageToScreen(
                        rawRect, verifiedChatOcr,
                        sendBtnLine.Bounds.X + sendBtnLine.Bounds.Width * 0.5,
                        sendBtnLine.Bounds.Y + sendBtnLine.Bounds.Height * 0.5);

                    double imgInputX = Math.Clamp(verifiedChatOcr.ImageWidth * 0.58, verifiedChatOcr.ImageWidth * 0.42, sendBtnLine.Bounds.X - 40 * scale);
                    double imgInputY = Math.Clamp(sendBtnLine.Bounds.Y - 76 * scale, verifiedChatOcr.ImageHeight * 0.72, sendBtnLine.Bounds.Y - 36 * scale);
                    (inputX, inputY) = ImageToScreen(rawRect, verifiedChatOcr, imgInputX, imgInputY);
                    AppServices.Logs.Info("AUTO", $"OCR 已定位「发送」按钮 ({sendX},{sendY})，推算消息输入框坐标 ({inputX},{inputY})");
                }
                else
                {
                    inputX = rawRect.Left + (int)Math.Round(rawRect.Width * 0.58);
                    inputY = rawRect.Top + (int)Math.Round(rawRect.Height * 0.84);
                    AppServices.Logs.Info("AUTO", $"未识别到「发送」文字按钮，使用比例定位消息输入框 ({inputX},{inputY})");
                }

                Win32WindowHelper.LeftClickAt(inputX, inputY, hwnd, "点击微信消息输入框");
                await Task.Delay(200 + slowPad, ct);

                if (CheckEscAborted(out escRes, sw)) return escRes;

                if (!Win32WindowHelper.SetClipboardUnicodeText(cleanReply))
                {
                    return Fail("无法将 AI 回复写入系统剪贴板，请检查是否有其他软件锁定剪贴板。", sw);
                }

                await Task.Delay(60, ct);
                Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                AppServices.Logs.Info("AUTO", $"已将 AI 回复粘贴至群「{cleanGroup}」输入框（{cleanReply.Length} 字）。");
                await Task.Delay(260 + slowPad, ct);

                if (!autoSend)
                {
                    sw.Stop();
                    AppServices.Logs.Success("AUTO", $"自动回复流程完成（人工确认模式）：已进入群「{cleanGroup}」并粘贴回复到输入框（耗时 {sw.ElapsedMilliseconds} ms）。");
                    return new AutoReplyAutomationResult
                    {
                        Success = true,
                        WasSent = false,
                        Message = $"已打开群「{cleanGroup}」并将 AI 回复粘贴到输入框（未自动发送，等待确认）。",
                        ElapsedMs = sw.ElapsedMilliseconds
                    };
                }

                if (CheckEscAborted(out escRes, sw)) return escRes;

                if (sendBtnLine is not null)
                {
                    Win32WindowHelper.LeftClickAt(sendX, sendY, hwnd, "点击微信发送按钮");
                }
                else
                {
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                    AppServices.Logs.Info("AUTO", "已发送回车键提交群回复。");
                }

                await Task.Delay(220, ct);
                sw.Stop();
                AppServices.Logs.Success("AUTO", $"已成功在群「{cleanGroup}」发送 AI 自动回复（总耗时 {sw.ElapsedMilliseconds} ms）。");
                return new AutoReplyAutomationResult
                {
                    Success = true,
                    WasSent = true,
                    Message = $"已在群「{cleanGroup}」自动发送回复。",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                AppServices.Logs.Warn("AUTO", $"群「{cleanGroup}」自动回复流程被取消（耗时 {sw.ElapsedMilliseconds} ms）。");
                return new AutoReplyAutomationResult
                {
                    Success = false,
                    WasCancelledByEsc = true,
                    Message = "自动回复操作已取消。",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                AppServices.Logs.Error("AUTO", $"群「{cleanGroup}」微信界面自动操控异常：{ex.Message}");
                return Fail($"微信自动操作异常：{ex.Message}", sw);
            }
            finally
            {
                ClickVisualOverlay.IsEnabled = prevOverlayEnabled;
                if (config.RestoreClipboardAfterSend && savedClipboardText is not null)
                {
                    try
                    {
                        await Task.Delay(120, CancellationToken.None);
                        _ = Win32WindowHelper.SetClipboardUnicodeText(savedClipboardText);
                    }
                    catch { }
                }
            }
        }

        private static async Task InputSearchKeywordAsync(string keyword, CancellationToken ct)
        {
            Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_A);
            await Task.Delay(70, ct);
            Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_BACK);
            await Task.Delay(90, ct);

            if (!Win32WindowHelper.SetClipboardUnicodeText(keyword))
            {
                AppServices.Logs.Warn("AUTO", "写入群名到剪贴板失败，回退为 Unicode 逐字输入。");
                Win32WindowHelper.SendUnicodeText(keyword);
            }
            else
            {
                await Task.Delay(50, ct);
                Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
            }
        }

        /// <summary>
        /// Polls up to 5 times with screen-composited BitBlt + OCR until the floating search dropdown
        /// is confirmed visible AND contains the target group row under the "群聊" section.
        /// </summary>
        private static async Task<(OcrRecognitionResult? Ocr, MatchedDropdownLine? Match)> WaitForGroupInDropdownAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            string cleanGroup,
            double searchBottomImgY,
            int slowPad,
            CancellationToken ct)
        {
            int[] pollDelaysMs = [380, 420, 480, 560, 650];

            for (int poll = 0; poll < pollDelaysMs.Length; poll++)
            {
                await Task.Delay(pollDelaysMs[poll] + slowPad, ct);
                if (Win32WindowHelper.IsEscapePressed()) return (null, null);

                var ocr = await CaptureScreenOverWindowAndOcrAsync(hwnd, rawRect, $"确认搜索下拉框({poll + 1}/{pollDelaysMs.Length})", ct);
                if (ocr is null) continue;

                var match = LocateGroupInSearchDropdown(ocr, cleanGroup, searchBottomImgY);
                if (match is not null && match.ConfirmedDropdownVisible)
                {
                    return (ocr, match);
                }

                AppServices.Logs.Info("OCR",
                    $"第 {poll + 1}/{pollDelaysMs.Length} 次等待下拉框确认：尚未稳定出现目标群「{cleanGroup}」（下拉可见={(match?.ConfirmedDropdownVisible == true ? "是" : "否")}），继续等待...");
            }

            return (null, null);
        }

        private static bool CheckEscAborted(out AutoReplyAutomationResult result, Stopwatch sw)
        {
            if (Win32WindowHelper.IsEscapePressed())
            {
                sw.Stop();
                AppServices.Logs.Warn("AUTO", "检测到用户按下 ESC 键，立即中止自动回复模拟操作。");
                result = new AutoReplyAutomationResult
                {
                    Success = false,
                    WasCancelledByEsc = true,
                    Message = "用户按下 ESC 紧急中止了自动回复操作。",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
                return true;
            }
            result = null!;
            return false;
        }

        /// <summary>
        /// Captures the TRUE composited screen rectangle covering WeChat's main window (via desktop BitBlt),
        /// which includes floating Qt popups such as WeChat 4.x's search dropdown that PrintWindow misses!
        /// </summary>
        private static async Task<OcrRecognitionResult?> CaptureScreenOverWindowAndOcrAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            string stageLabel,
            CancellationToken ct)
        {
            ClickVisualOverlay.HideImmediately();
            await Task.Delay(55, ct);

            if (Win32WindowHelper.GetWindowRect(hwnd, out var curRect) && curRect.Width >= 300 && curRect.Height >= 250)
            {
                rawRect = curRect;
            }

            using var bmp = await Task.Run(
                () => Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(rawRect.Left, rawRect.Top, rawRect.Width, rawRect.Height),
                ct);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
            AppServices.Logs.Info("OCR", $"[{stageLabel}] 屏幕区域识别完成：{ocr.ImageWidth}x{ocr.ImageHeight}，{ocr.Lines.Count} 行文本，耗时 {ocr.ElapsedMilliseconds} ms");
            return ocr;
        }

        private static (int ScreenX, int ScreenY, double BottomImgY) LocateSearchBar(
            OcrRecognitionResult ocr,
            Win32WindowHelper.RECT rawRect,
            double scale)
        {
            double maxX = ocr.ImageWidth * 0.38;
            double maxY = ocr.ImageHeight * 0.14;

            var candidate = ocr.Lines
                .Where(l =>
                    l.Bounds.X <= maxX &&
                    l.Bounds.Y <= maxY &&
                    l.Text.Contains("搜索", StringComparison.Ordinal) &&
                    !l.Text.Contains("网络", StringComparison.Ordinal))
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault();

            if (candidate is not null)
            {
                double cx = candidate.Bounds.X + Math.Max(candidate.Bounds.Width * 0.65, 28 * scale);
                double cy = candidate.Bounds.Y + candidate.Bounds.Height * 0.5;
                var (sx, sy) = ImageToScreen(rawRect, ocr, cx, cy);
                AppServices.Logs.Info("OCR", $"已通过 OCR 定位搜索栏「{candidate.Text}」 -> 窗口坐标 ({cx:F0},{cy:F0})");
                return (sx, sy, candidate.Bounds.Y + candidate.Bounds.Height);
            }

            double fbX = Math.Clamp(ocr.ImageWidth * 0.18, 85 * scale, ocr.ImageWidth * 0.28);
            double fbY = Math.Clamp(ocr.ImageHeight * 0.08, 42 * scale, ocr.ImageHeight * 0.11);
            var (fsx, fsy) = ImageToScreen(rawRect, ocr, fbX, fbY);
            AppServices.Logs.Info("OCR", $"未直接识别到「搜索」占位字，回退比例定位搜索栏 -> 窗口坐标 ({fbX:F0},{fbY:F0})");
            return (fsx, fsy, fbY + 18 * scale);
        }

        private static bool IsSearchDropdownStillOpen(OcrRecognitionResult ocr, double searchBottomImgY)
        {
            double minX = ocr.ImageWidth * 0.04;
            double maxX = ocr.ImageWidth * 0.48;
            return ocr.Lines.Any(l =>
                l.Bounds.X >= minX &&
                l.Bounds.X <= maxX &&
                l.Bounds.Y >= searchBottomImgY &&
                l.Bounds.Y <= ocr.ImageHeight * 0.65 &&
                DropdownPresenceMarkers.Any(m => l.Text.Trim().StartsWith(m, StringComparison.Ordinal)));
        }

        private static MatchedDropdownLine? LocateGroupInSearchDropdown(
            OcrRecognitionResult ocr,
            string targetGroupName,
            double searchBottomImgY)
        {
            double minX = ocr.ImageWidth * 0.04;
            double maxX = ocr.ImageWidth * 0.48;

            var dropdownLines = ocr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX && l.Bounds.Y >= searchBottomImgY)
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            var groupHeader = dropdownLines.FirstOrDefault(l =>
            {
                string t = l.Text.Trim();
                return t == "群聊" || (t.StartsWith("群聊", StringComparison.Ordinal) && t.Length <= 4);
            });

            var otherHeaders = dropdownLines
                .Where(l => NonGroupDropdownHeaders.Any(h => l.Text.Trim().StartsWith(h, StringComparison.Ordinal)))
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            bool dropdownVisible = groupHeader is not null || otherHeaders.Count > 0;
            if (!dropdownVisible)
            {
                string preview = string.Join(" | ", dropdownLines.Take(8).Select(l => $"{l.Text}@{l.Bounds.Y:F0}"));
                AppServices.Logs.Info("OCR", $"尚未检测到搜索下拉框栏目头（当前左侧识别：{preview}）");
                return null;
            }

            double bandTop;
            double bandBottom;
            bool insideGroupSection;
            if (groupHeader is not null)
            {
                insideGroupSection = true;
                bandTop = groupHeader.Bounds.Y + groupHeader.Bounds.Height * 0.6;
                var nextHeader = otherHeaders.FirstOrDefault(h => h.Bounds.Y > groupHeader.Bounds.Y + 6);
                bandBottom = nextHeader is not null ? nextHeader.Bounds.Y - 4 : ocr.ImageHeight * 0.85;
                AppServices.Logs.Info("OCR", $"搜索下拉框已确认出现「群聊」栏目：Y=[{bandTop:F0}..{bandBottom:F0}]");
            }
            else
            {
                insideGroupSection = false;
                bandTop = searchBottomImgY + 4;
                var firstBadHeader = otherHeaders
                    .FirstOrDefault(h => h.Text.Contains("网络", StringComparison.Ordinal) || h.Text.Contains("聊天记录", StringComparison.Ordinal));
                bandBottom = firstBadHeader is not null ? firstBadHeader.Bounds.Y - 4 : ocr.ImageHeight * 0.55;
                AppServices.Logs.Info("OCR", $"搜索下拉框已出现，限定顶部结果区 Y=[{bandTop:F0}..{bandBottom:F0}]");
            }

            string targetNorm = NormalizeGroupTitle(targetGroupName);
            var scored = new List<MatchedDropdownLine>();

            foreach (var line in dropdownLines)
            {
                double cy = line.Bounds.Y + line.Bounds.Height * 0.5;
                if (cy < bandTop || cy > bandBottom) continue;

                string txt = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(txt) || txt == "群聊") continue;
                if (NonGroupDropdownHeaders.Any(h => txt.StartsWith(h, StringComparison.Ordinal))) continue;
                if (txt.StartsWith("包含:", StringComparison.Ordinal) || txt.StartsWith("包含：", StringComparison.Ordinal)) continue;

                double score = ComputeGroupNameSimilarity(targetNorm, NormalizeGroupTitle(txt));
                if (score >= 0.52)
                {
                    scored.Add(new MatchedDropdownLine(txt, line.Bounds, score, ConfirmedDropdownVisible: true, InsideGroupSection: insideGroupSection));
                }
            }

            if (scored.Count == 0)
            {
                string preview = string.Join(" | ", dropdownLines.Take(12).Select(l => $"{l.Text}@{l.Bounds.Y:F0}"));
                AppServices.Logs.Warn("OCR", $"下拉框「群聊」区未匹配到「{targetGroupName}」，识别内容：{preview}");
                return null;
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Bounds.Y)
                .First();
        }

        private static (bool Ok, string DetectedHeader) VerifyOpenedChatTitle(OcrRecognitionResult ocr, string targetGroupName)
        {
            double minX = ocr.ImageWidth * 0.33;
            double maxX = ocr.ImageWidth * 0.85;
            double maxY = ocr.ImageHeight * 0.13;

            var headerLines = ocr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX && l.Bounds.Y <= maxY)
                .OrderBy(l => l.Bounds.Y)
                .ThenBy(l => l.Bounds.X)
                .ToList();

            string targetNorm = NormalizeGroupTitle(targetGroupName);
            double bestScore = 0;
            string bestText = string.Empty;

            foreach (var l in headerLines)
            {
                string norm = NormalizeGroupTitle(l.Text);
                double score = ComputeGroupNameSimilarity(targetNorm, norm);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestText = l.Text.Trim();
                }
            }

            string fallbackPreview = headerLines.Count > 0
                ? string.Join(" / ", headerLines.Take(3).Select(x => x.Text.Trim()))
                : "(未识别到标题)";

            return bestScore >= 0.58
                ? (true, bestText)
                : (false, fallbackPreview);
        }

        private static OcrLineResult? LocateSendButton(OcrRecognitionResult ocr)
        {
            double minX = ocr.ImageWidth * 0.70;
            double minY = ocr.ImageHeight * 0.78;
            return ocr.Lines
                .Where(l =>
                    l.Bounds.X >= minX &&
                    l.Bounds.Y >= minY &&
                    (l.Text.Trim().Equals("发送", StringComparison.Ordinal) ||
                     l.Text.Trim().Equals("Send", StringComparison.OrdinalIgnoreCase) ||
                     l.Text.Trim().Contains("发送", StringComparison.Ordinal)))
                .OrderByDescending(l => l.Bounds.Y)
                .ThenByDescending(l => l.Bounds.X)
                .FirstOrDefault();
        }

        private static (int ScreenX, int ScreenY) ImageToScreen(
            Win32WindowHelper.RECT rawRect,
            OcrRecognitionResult ocr,
            double imgX,
            double imgY)
        {
            double sxRatio = ocr.ImageWidth > 0 ? (double)rawRect.Width / ocr.ImageWidth : 1.0;
            double syRatio = ocr.ImageHeight > 0 ? (double)rawRect.Height / ocr.ImageHeight : 1.0;
            int sx = rawRect.Left + (int)Math.Round(imgX * sxRatio);
            int sy = rawRect.Top + (int)Math.Round(imgY * syRatio);
            return (sx, sy);
        }

        private static string NormalizeGroupTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = TrailingMemberCountRegex().Replace(raw.Trim(), string.Empty);
            var chars = s.Where(c => !char.IsWhiteSpace(c) && c != '\u2005' && c != '·').ToArray();
            return new string(chars).ToLowerInvariant();
        }

        private static double ComputeGroupNameSimilarity(string targetNorm, string candidateNorm)
        {
            if (string.IsNullOrEmpty(targetNorm) || string.IsNullOrEmpty(candidateNorm)) return 0;
            if (targetNorm == candidateNorm) return 1.0;
            if (candidateNorm.Contains(targetNorm, StringComparison.Ordinal))
            {
                return 0.88 + 0.11 * ((double)targetNorm.Length / candidateNorm.Length);
            }
            if (targetNorm.Contains(candidateNorm, StringComparison.Ordinal) && candidateNorm.Length >= 2)
            {
                return 0.75 + 0.15 * ((double)candidateNorm.Length / targetNorm.Length);
            }

            var tSet = new HashSet<char>(targetNorm);
            var cSet = new HashSet<char>(candidateNorm);
            int inter = tSet.Count(cSet.Contains);
            if (inter == 0) return 0;
            double recall = (double)inter / tSet.Count;
            double precision = (double)inter / cSet.Count;
            return 2.0 * recall * precision / (recall + precision);
        }

        private static AutoReplyAutomationResult Fail(string msg, Stopwatch sw)
        {
            sw.Stop();
            return new AutoReplyAutomationResult
            {
                Success = false,
                WasSent = false,
                Message = msg,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        [GeneratedRegex(@"\s*[（(]\s*\d+\s*[)）]\s*$")]
        private static partial Regex TrailingMemberCountRegex();
    }
}
