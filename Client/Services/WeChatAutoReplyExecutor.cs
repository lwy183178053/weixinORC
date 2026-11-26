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
        public PreparedGroupSession? Session { get; init; }
    }

    /// <summary>
    /// Represents a WeChat group chat window that has already been brought to front,
    /// switched to the verified target group, and had its input box &amp; Send button coordinates located.
    /// Produced by PrepareGroupChatAsync (even while AI generation is still in flight!) and consumed
    /// by DeliverPreparedReplyAsync for near-instant paste &amp; send.
    /// </summary>
    public sealed class PreparedGroupSession
    {
        public bool Success { get; init; }
        public bool WasCancelledByEsc { get; init; }
        public bool UsedFastPathAlreadyInGroup { get; init; }
        public string Message { get; init; } = string.Empty;
        public string GroupName { get; init; } = string.Empty;
        public string DetectedHeader { get; init; } = string.Empty;
        public nint Hwnd { get; init; }
        public int WindowLeft { get; init; }
        public int WindowTop { get; init; }
        public int WindowWidth { get; init; }
        public int WindowHeight { get; init; }
        public int InputX { get; init; }
        public int InputY { get; init; }
        public int SendX { get; init; }
        public int SendY { get; init; }
        public bool HasSendButton { get; init; }
        public long ElapsedMs { get; init; }
    }

    /// <summary>
    /// High-speed, high-stability visual OCR + Win32 hardware-input automation executor for WeChat PC (4.x &amp; 3.x):
    /// 1. Uses cropped ROI desktop BitBlt (SRCCOPY | CAPTUREBLT) — 3x~6x faster than full-window OCR while capturing
    ///    floating Qt search dropdown popups that PrintWindow cannot see
    /// 2. Already-in-target-group fast path: inspects ONLY the top bar strip (~100ms); if WeChat is already open on
    ///    the target group with no stale dropdown, skips searching completely and sends directly!
    /// 3. Two-phase Prepare + Deliver API: allows opening &amp; switching to the target group IN PARALLEL while LLM
    ///    generation is still running, then verifies title (~100ms) and pastes/sends immediately upon AI completion
    /// 4. Pre-click confirmation + 3-round adaptive retry (skipReactivate click + header check + Enter fallback)
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

        /// <summary>
        /// Phase 1 (can run in parallel with LLM generation!):
        /// Ensures WeChat is foreground, checks already-in-group fast path, otherwise searches &amp; confirms dropdown
        /// &amp; clicks into the target group (up to 3 rounds), and locates the Send button + message input box.
        /// </summary>
        public async Task<PreparedGroupSession> PrepareGroupChatAsync(
            string groupName,
            AutoReplyConfigData config,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string cleanGroup = (groupName ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(cleanGroup))
            {
                return PrepareFail(cleanGroup, "群名称为空，无法定位微信群聊。", sw);
            }
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                string note = AppServices.Ocr.RapidEngine.AvailabilityNote;
                AppServices.Logs.Error("AUTO", $"本地识别组件不可用：{note}");
                return PrepareFail(cleanGroup, $"本地图文识别组件不可用：{note}", sw);
            }

            int slowPad = config.SlowStableMode ? 260 : 0;
            bool prevOverlayEnabled = ClickVisualOverlay.IsEnabled;
            ClickVisualOverlay.IsEnabled = config.ShowClickOverlay;
            string? savedSearchClipboard = config.RestoreClipboardAfterSend
                ? Win32WindowHelper.TryGetClipboardUnicodeText()
                : null;
            bool touchedClipboard = false;

            try
            {
                if (Win32WindowHelper.IsEscapePressed())
                {
                    return PrepareCancelled(cleanGroup, sw);
                }

                // Step 1: Ensure WeChat window is visible & foreground
                var (readyOk, hwnd, rawRect, scale, readyErr) = await EnsureWeChatWindowReadyAsync(slowPad, ct);
                if (!readyOk)
                {
                    return PrepareFail(cleanGroup, readyErr, sw);
                }

                if (Win32WindowHelper.IsEscapePressed()) return PrepareCancelled(cleanGroup, sw);

                // Step 2: Fast Top-Bar ROI OCR (~100ms) -> check if ALREADY in target group!
                var topCapture = await CaptureTopBarRoiAsync(hwnd, rawRect, "顶部栏快检", ct);
                if (topCapture is null)
                {
                    return PrepareFail(cleanGroup, "截取微信顶部栏画面失败，请确认微信窗口未被最小化。", sw);
                }

                var (topOcr, topRect) = topCapture.Value;
                var (alreadyInGroup, detectedHeader) = VerifyOpenedChatTitle(topOcr, cleanGroup);
                bool searchBarIdle = HasIdleSearchPlaceholder(topOcr);

                if (alreadyInGroup && !searchBarIdle)
                {
                    // Right header matches target group, but search box may have stale text/dropdown -> press ESC once & re-check
                    AppServices.Logs.Info("AUTO", $"右侧已是目标群「{detectedHeader}」，但搜索栏似有残留内容，先按 ESC 收起后核验...");
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    await Task.Delay(180 + slowPad, ct);
                    topCapture = await CaptureTopBarRoiAsync(hwnd, rawRect, "收起搜索框后快检", ct);
                    if (topCapture is not null)
                    {
                        (topOcr, topRect) = topCapture.Value;
                        (alreadyInGroup, detectedHeader) = VerifyOpenedChatTitle(topOcr, cleanGroup);
                        searchBarIdle = HasIdleSearchPlaceholder(topOcr);
                    }
                }

                if (alreadyInGroup && searchBarIdle)
                {
                    // FAST PATH: already in target group with clean UI -> zero search clicks needed!
                    var (inX, inY, sX, sY, hasSend) = await LocateInputAndSendBoxAsync(hwnd, rawRect, scale, ct);
                    sw.Stop();
                    AppServices.Logs.Success("AUTO",
                        $"免搜索直达：微信已处于目标群「{detectedHeader}」（耗时 {sw.ElapsedMilliseconds} ms，输入框=({inX},{inY})）。");
                    return new PreparedGroupSession
                    {
                        Success = true,
                        UsedFastPathAlreadyInGroup = true,
                        GroupName = cleanGroup,
                        DetectedHeader = detectedHeader,
                        Hwnd = hwnd,
                        WindowLeft = rawRect.Left,
                        WindowTop = rawRect.Top,
                        WindowWidth = rawRect.Width,
                        WindowHeight = rawRect.Height,
                        InputX = inX,
                        InputY = inY,
                        SendX = sX,
                        SendY = sY,
                        HasSendButton = hasSend,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Message = $"微信已处于目标群「{detectedHeader}」。"
                    };
                }

                // If search bar had stale content from an earlier search, dismiss with ESC before locating search bar
                if (!searchBarIdle)
                {
                    AppServices.Logs.Info("AUTO", "检测到微信搜索栏可能有残留内容，先按 ESC 清理后再定位搜索栏。");
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    await Task.Delay(170 + slowPad, ct);
                    var cleanTop = await CaptureTopBarRoiAsync(hwnd, rawRect, "清理搜索栏后重定位", ct);
                    if (cleanTop is not null)
                    {
                        (topOcr, topRect) = cleanTop.Value;
                    }
                }

                var (searchX, searchY, searchBottomScreenY) = LocateSearchBarInTopBar(topOcr, topRect, rawRect, scale);

                // Step 3: Search group -> confirm floating dropdown rendered -> click with up to 3 retry rounds
                const int maxSwitchRounds = 3;
                string lastHeaderSeen = detectedHeader;
                bool switchedOk = false;

                (int Dx, int Dy)[] clickOffsets =
                [
                    (0, 0),
                    (-22, 0),
                    (24, 2)
                ];

                for (int round = 1; round <= maxSwitchRounds; round++)
                {
                    if (Win32WindowHelper.IsEscapePressed()) return PrepareCancelled(cleanGroup, sw);

                    if (Win32WindowHelper.GetWindowRect(hwnd, out var latestRect) && latestRect.Width >= 300 && latestRect.Height >= 250)
                    {
                        rawRect = latestRect;
                    }

                    AppServices.Logs.Info("AUTO", $"第 {round}/{maxSwitchRounds} 轮切群尝试：点击搜索栏 ({searchX},{searchY}) 并输入「{cleanGroup}」");
                    Win32WindowHelper.LeftClickAt(searchX, searchY, hwnd, $"点击微信搜索栏(第{round}轮)");
                    await Task.Delay(120 + slowPad, ct);

                    if (Win32WindowHelper.IsEscapePressed()) return PrepareCancelled(cleanGroup, sw);

                    touchedClipboard = true;
                    await InputSearchKeywordAsync(cleanGroup, ct);

                    // Poll cropped left dropdown ROI until dropdown ACTUALLY rendered & matched target group
                    var (dropdownOcr, dropdownRect, matchedGroup) = await WaitForGroupInDropdownAsync(
                        hwnd, rawRect, cleanGroup, searchBottomScreenY, slowPad, ct);

                    if (Win32WindowHelper.IsEscapePressed())
                    {
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        return PrepareCancelled(cleanGroup, sw);
                    }

                    if (dropdownOcr is null || matchedGroup is null)
                    {
                        AppServices.Logs.Warn("AUTO", $"第 {round}/{maxSwitchRounds} 轮未在搜索下拉框中确认到群聊「{cleanGroup}」，关闭搜索框准备重试...");
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        await Task.Delay(240 + slowPad, ct);
                        continue;
                    }

                    var (dx, dy) = clickOffsets[Math.Min(round - 1, clickOffsets.Length - 1)];
                    double targetImgX = matchedGroup.Bounds.X + matchedGroup.Bounds.Width * 0.5 + dx * scale;
                    double targetImgY = matchedGroup.Bounds.Y + matchedGroup.Bounds.Height * 0.5 + dy * scale;
                    var (grpClickX, grpClickY) = ImageToScreen(dropdownRect, dropdownOcr, targetImgX, targetImgY);

                    AppServices.Logs.Info("AUTO",
                        $"已确认下拉框出现群聊「{matchedGroup.Text}」(匹配分={matchedGroup.Score:F2}, 群聊栏目={(matchedGroup.InsideGroupSection ? "是" : "顶部区")}) -> 执行第 {round} 轮点击 ({grpClickX},{grpClickY})");

                    var clickDiag = Win32WindowHelper.LeftClickAt(
                        grpClickX,
                        grpClickY,
                        hwnd,
                        $"点击群聊搜索结果「{cleanGroup}」#{round}",
                        skipReactivate: true);

                    await Task.Delay(300 + slowPad, ct);
                    if (Win32WindowHelper.IsEscapePressed()) return PrepareCancelled(cleanGroup, sw);

                    // Fast Top-Bar ROI verification (~100ms)
                    var verifyTop = await CaptureTopBarRoiAsync(hwnd, rawRect, $"核对切群标题(第{round}轮)", ct);
                    bool titleOk = false;
                    bool idleAfter = false;
                    if (verifyTop is not null)
                    {
                        (titleOk, detectedHeader) = VerifyOpenedChatTitle(verifyTop.Value.Ocr, cleanGroup);
                        idleAfter = HasIdleSearchPlaceholder(verifyTop.Value.Ocr);
                        lastHeaderSeen = detectedHeader;
                    }

                    // If WeChat UI was still transitioning, give it one brief extra settle before deciding to fallback
                    if (!(titleOk && idleAfter))
                    {
                        await Task.Delay(220 + slowPad, ct);
                        verifyTop = await CaptureTopBarRoiAsync(hwnd, rawRect, $"二次核对切群标题(第{round}轮)", ct);
                        if (verifyTop is not null)
                        {
                            (titleOk, detectedHeader) = VerifyOpenedChatTitle(verifyTop.Value.Ocr, cleanGroup);
                            idleAfter = HasIdleSearchPlaceholder(verifyTop.Value.Ocr);
                            lastHeaderSeen = detectedHeader;
                        }
                    }

                    // If dropdown looks stuck open (search bar didn't return to idle placeholder), confirm via Left ROI & try Enter once
                    if (!idleAfter)
                    {
                        var dropCheck = await CaptureLeftDropdownRoiAsync(hwnd, rawRect, $"检测下拉框状态(第{round}轮)", ct);
                        bool stillOpen = dropCheck is not null &&
                                         IsSearchDropdownStillOpen(dropCheck.Value.Ocr, dropCheck.Value.CropRect, searchBottomScreenY);
                        if (stillOpen)
                        {
                            AppServices.Logs.Warn("AUTO",
                                $"第 {round} 轮点击后下拉框仍未关闭（命中窗口={clickDiag.HitWindowClass}, 标题=「{detectedHeader}」），补发回车确认打开...");
                            Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                            await Task.Delay(360 + slowPad, ct);

                            verifyTop = await CaptureTopBarRoiAsync(hwnd, rawRect, $"回车后核对标题(第{round}轮)", ct);
                            if (verifyTop is not null)
                            {
                                (titleOk, detectedHeader) = VerifyOpenedChatTitle(verifyTop.Value.Ocr, cleanGroup);
                                idleAfter = HasIdleSearchPlaceholder(verifyTop.Value.Ocr);
                                lastHeaderSeen = detectedHeader;
                            }
                        }
                        else
                        {
                            // Dropdown actually closed even if placeholder wasn't detected (e.g. narrow window)
                            idleAfter = true;
                        }
                    }

                    if (idleAfter && (titleOk || !config.VerifyGroupTitleBeforePaste))
                    {
                        switchedOk = true;
                        AppServices.Logs.Success("AUTO",
                            $"第 {round} 轮切群确认成功（目标=「{cleanGroup}」，当前聊天窗标题=「{detectedHeader}」）。");
                        break;
                    }

                    AppServices.Logs.Warn("AUTO",
                        $"第 {round}/{maxSwitchRounds} 轮点击后聊天窗标题仍为「{detectedHeader}」（目标「{cleanGroup}」），将自动重试...");
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    await Task.Delay(240 + slowPad, ct);
                }

                if (!switchedOk)
                {
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                    string reason = string.IsNullOrWhiteSpace(lastHeaderSeen)
                        ? $"连续 {maxSwitchRounds} 次搜索均未在微信「群聊」栏目中确认到「{cleanGroup}」（已安全中止，未误发消息）。"
                        : $"连续 {maxSwitchRounds} 次尝试点击进入群「{cleanGroup}」未成功（当前聊天标题仍为「{lastHeaderSeen}」，已安全中止发送）。";
                    AppServices.Logs.Warn("AUTO", reason);
                    return PrepareFail(cleanGroup, reason, sw);
                }

                // Step 4: Locate Send button & message input box via tiny bottom-right ROI (~70ms)
                var (inputX, inputY, sendX, sendY, hasSendBtn) = await LocateInputAndSendBoxAsync(hwnd, rawRect, scale, ct);
                sw.Stop();
                return new PreparedGroupSession
                {
                    Success = true,
                    UsedFastPathAlreadyInGroup = false,
                    GroupName = cleanGroup,
                    DetectedHeader = detectedHeader,
                    Hwnd = hwnd,
                    WindowLeft = rawRect.Left,
                    WindowTop = rawRect.Top,
                    WindowWidth = rawRect.Width,
                    WindowHeight = rawRect.Height,
                    InputX = inputX,
                    InputY = inputY,
                    SendX = sendX,
                    SendY = sendY,
                    HasSendButton = hasSendBtn,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Message = $"已进入目标群「{detectedHeader}」。"
                };
            }
            catch (OperationCanceledException)
            {
                return PrepareCancelled(cleanGroup, sw);
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("AUTO", $"准备微信群「{cleanGroup}」界面异常：{ex.Message}");
                return PrepareFail(cleanGroup, $"定位微信群异常：{ex.Message}", sw);
            }
            finally
            {
                ClickVisualOverlay.IsEnabled = prevOverlayEnabled;
                if (touchedClipboard && config.RestoreClipboardAfterSend && savedSearchClipboard is not null)
                {
                    try
                    {
                        _ = Win32WindowHelper.SetClipboardUnicodeText(savedSearchClipboard);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Phase 2: Pastes and optionally sends the reply text into an already-prepared WeChat group session
        /// (with an ultra-fast ~100ms safety re-check that WeChat is still on the target group; if the user switched
        /// chats while AI was generating, seamlessly re-prepares before pasting!).
        /// </summary>
        public async Task<AutoReplyAutomationResult> DeliverPreparedReplyAsync(
            PreparedGroupSession? prepared,
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

            bool autoSend = overrideAutoSend ?? config.AutoSendAfterPaste;
            int slowPad = config.SlowStableMode ? 260 : 0;
            bool prevOverlayEnabled = ClickVisualOverlay.IsEnabled;
            ClickVisualOverlay.IsEnabled = config.ShowClickOverlay;
            string? savedClipboardText = config.RestoreClipboardAfterSend
                ? Win32WindowHelper.TryGetClipboardUnicodeText()
                : null;

            AppServices.Logs.Info("AUTO",
                $"开始写入微信回复：群「{cleanGroup}」，预开复用={(prepared?.Success == true ? "是" : "否")}，模式={(autoSend ? "粘贴并自动发送" : "仅粘贴待确认")}，长度={cleanReply.Length} 字");

            try
            {
                if (CheckEscAborted(out var escRes, sw)) return escRes;

                PreparedGroupSession? session = prepared;

                // Validate pre-opened session (ensure window still exists, foreground, and still on target group!)
                if (session is { Success: true } &&
                    string.Equals(session.GroupName, cleanGroup, StringComparison.OrdinalIgnoreCase) &&
                    session.Hwnd != nint.Zero &&
                    Win32WindowHelper.IsWindow(session.Hwnd))
                {
                    bool alreadyFg = Win32WindowHelper.GetForegroundWindow() == session.Hwnd;
                    if (!alreadyFg)
                    {
                        _ = Win32WindowHelper.EnsureWindowTrulyActive(session.Hwnd);
                        await Task.Delay(140 + slowPad, ct);
                    }

                    if (!Win32WindowHelper.GetWindowRect(session.Hwnd, out var curRect) || curRect.Width < 300 || curRect.Height < 250)
                    {
                        session = null;
                    }
                    else
                    {
                        bool moved = Math.Abs(curRect.Left - session.WindowLeft) > 4 ||
                                     Math.Abs(curRect.Top - session.WindowTop) > 4 ||
                                     Math.Abs(curRect.Width - session.WindowWidth) > 4 ||
                                     Math.Abs(curRect.Height - session.WindowHeight) > 4;

                        if (config.VerifyGroupTitleBeforePaste)
                        {
                            var topCheck = await CaptureTopBarRoiAsync(session.Hwnd, curRect, "发送前复核群标题", ct);
                            bool stillTarget = topCheck is not null &&
                                               VerifyOpenedChatTitle(topCheck.Value.Ocr, cleanGroup).Ok &&
                                               HasIdleSearchPlaceholder(topCheck.Value.Ocr);
                            if (!stillTarget)
                            {
                                AppServices.Logs.Warn("AUTO", $"等待 AI 期间微信窗口会话可能已变动，重新定位目标群「{cleanGroup}」...");
                                session = null;
                            }
                        }

                        if (session is not null && moved)
                        {
                            uint dpi = Win32WindowHelper.GetDpiForWindow(session.Hwnd);
                            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
                            var (inX, inY, sX, sY, hasSend) = await LocateInputAndSendBoxAsync(session.Hwnd, curRect, scale, ct);
                            session = new PreparedGroupSession
                            {
                                Success = true,
                                GroupName = cleanGroup,
                                DetectedHeader = session.DetectedHeader,
                                Hwnd = session.Hwnd,
                                WindowLeft = curRect.Left,
                                WindowTop = curRect.Top,
                                WindowWidth = curRect.Width,
                                WindowHeight = curRect.Height,
                                InputX = inX,
                                InputY = inY,
                                SendX = sX,
                                SendY = sY,
                                HasSendButton = hasSend
                            };
                        }
                    }
                }
                else
                {
                    session = null;
                }

                // If not prepared or user switched away during AI wait, run full PrepareGroupChatAsync now
                if (session is null)
                {
                    session = await PrepareGroupChatAsync(cleanGroup, config, ct);
                    if (session.WasCancelledByEsc)
                    {
                        sw.Stop();
                        return new AutoReplyAutomationResult
                        {
                            Success = false,
                            WasCancelledByEsc = true,
                            Message = session.Message,
                            ElapsedMs = sw.ElapsedMilliseconds
                        };
                    }
                    if (!session.Success)
                    {
                        return Fail(session.Message, sw);
                    }
                }

                if (CheckEscAborted(out escRes, sw)) return escRes;

                // Click input box -> paste AI reply -> optionally click Send
                Win32WindowHelper.LeftClickAt(session.InputX, session.InputY, session.Hwnd, "点击微信消息输入框");
                await Task.Delay(130 + slowPad, ct);

                if (CheckEscAborted(out escRes, sw)) return escRes;

                if (!Win32WindowHelper.SetClipboardUnicodeText(cleanReply))
                {
                    return Fail("无法将 AI 回复写入系统剪贴板，请检查是否有其他软件锁定剪贴板。", sw);
                }

                await Task.Delay(45, ct);
                Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                AppServices.Logs.Info("AUTO", $"已将 AI 回复粘贴至群「{cleanGroup}」输入框（{cleanReply.Length} 字）。");
                await Task.Delay(160 + slowPad, ct);

                if (!autoSend)
                {
                    sw.Stop();
                    AppServices.Logs.Success("AUTO", $"自动回复完成（人工确认模式）：已进入群「{cleanGroup}」并粘贴到输入框（操控耗时 {sw.ElapsedMilliseconds} ms）。");
                    return new AutoReplyAutomationResult
                    {
                        Success = true,
                        WasSent = false,
                        Message = $"已打开群「{cleanGroup}」并将 AI 回复粘贴到输入框（未自动发送，等待确认）。",
                        ElapsedMs = sw.ElapsedMilliseconds,
                        Session = session
                    };
                }

                if (CheckEscAborted(out escRes, sw)) return escRes;

                if (session.HasSendButton)
                {
                    Win32WindowHelper.LeftClickAt(session.SendX, session.SendY, session.Hwnd, "点击微信发送按钮");
                }
                else
                {
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                    AppServices.Logs.Info("AUTO", "已发送回车键提交群回复。");
                }

                await Task.Delay(150, ct);
                sw.Stop();
                AppServices.Logs.Success("AUTO", $"已成功在群「{cleanGroup}」发送 AI 自动回复（操控耗时 {sw.ElapsedMilliseconds} ms）。");
                return new AutoReplyAutomationResult
                {
                    Success = true,
                    WasSent = true,
                    Message = $"已在群「{cleanGroup}」自动发送回复。",
                    ElapsedMs = sw.ElapsedMilliseconds,
                    Session = session
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
                        await Task.Delay(100, CancellationToken.None);
                        _ = Win32WindowHelper.SetClipboardUnicodeText(savedClipboardText);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Convenience single-shot wrapper (Prepares &amp; Delivers in one call).
        /// </summary>
        public Task<AutoReplyAutomationResult> SendReplyToGroupAsync(
            string groupName,
            string replyText,
            AutoReplyConfigData config,
            bool? overrideAutoSend = null,
            CancellationToken ct = default) =>
            DeliverPreparedReplyAsync(null, groupName, replyText, config, overrideAutoSend, ct);

        private static async Task<(bool Ok, nint Hwnd, Win32WindowHelper.RECT Rect, double Scale, string Error)> EnsureWeChatWindowReadyAsync(
            int slowPad,
            CancellationToken ct)
        {
            var ensure = await AppServices.WeChatWindows.EnsurePersonalWeChatWindowReadyAsync(
                autoLaunchIfClosed: false,
                allowLoginPageSuccess: false,
                ct: ct);
            if (!ensure.Success || ensure.Window is null)
            {
                AppServices.Logs.Warn("AUTO", $"未能唤起微信主窗口：{ensure.Message}");
                return (false, 0, default, 1.0, $"未能唤起微信主窗口：{ensure.Message}");
            }

            nint hwnd = ensure.Window.Hwnd;
            string? uipiErr = Win32WindowHelper.GetPrivilegeMismatchDetail(hwnd);
            if (!string.IsNullOrWhiteSpace(uipiErr))
            {
                AppServices.Logs.Error("AUTO", uipiErr);
                return (false, 0, default, 1.0, uipiErr);
            }

            bool alreadyForeground = Win32WindowHelper.GetForegroundWindow() == hwnd;
            bool activated = alreadyForeground || Win32WindowHelper.EnsureWindowTrulyActive(hwnd);
            await Task.Delay(alreadyForeground ? (45 + slowPad / 2) : (180 + slowPad), ct);

            if (!Win32WindowHelper.GetWindowRect(hwnd, out var rawRect) || rawRect.Width < 300 || rawRect.Height < 250)
            {
                return (false, 0, default, 1.0, "无法获取有效的微信窗口坐标尺寸。");
            }

            uint dpi = Win32WindowHelper.GetDpiForWindow(hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            AppServices.Logs.Info("AUTO",
                $"微信主窗口已就绪：PID={ensure.Window.ProcessId}, 前台={(activated ? "是" : "否")}{(alreadyForeground ? "(已在前台)" : "")}, 坐标=({rawRect.Left},{rawRect.Top}), 尺寸={rawRect.Width}x{rawRect.Height}, DPI={dpi}({scale:F2}x)");
            return (true, hwnd, rawRect, scale, string.Empty);
        }

        private static async Task<(int InputX, int InputY, int SendX, int SendY, bool HasSendButton)> LocateInputAndSendBoxAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double scale,
            CancellationToken ct)
        {
            var sendCapture = await CaptureBottomRightRoiAsync(hwnd, rawRect, "定位发送按钮", ct);
            if (sendCapture is not null)
            {
                var (sendOcr, sendRect) = sendCapture.Value;
                var sendBtnLine = LocateSendButtonInRoi(sendOcr);
                if (sendBtnLine is not null)
                {
                    var (sendX, sendY) = ImageToScreen(
                        sendRect, sendOcr,
                        sendBtnLine.Bounds.X + sendBtnLine.Bounds.Width * 0.5,
                        sendBtnLine.Bounds.Y + sendBtnLine.Bounds.Height * 0.5);

                    int inputX = (int)Math.Round(Math.Clamp(
                        rawRect.Left + rawRect.Width * 0.58,
                        rawRect.Left + rawRect.Width * 0.42,
                        sendX - 42 * scale));
                    int inputY = (int)Math.Round(Math.Clamp(
                        sendY - 82 * scale,
                        rawRect.Top + rawRect.Height * 0.72,
                        sendY - 36 * scale));

                    AppServices.Logs.Info("AUTO", $"区域 OCR 已定位「发送」按钮 ({sendX},{sendY})，推算消息输入框坐标 ({inputX},{inputY})");
                    return (inputX, inputY, sendX, sendY, true);
                }
            }

            int fbInputX = rawRect.Left + (int)Math.Round(rawRect.Width * 0.58);
            int fbInputY = rawRect.Top + (int)Math.Round(rawRect.Height * 0.84);
            AppServices.Logs.Info("AUTO", $"未识别到「发送」文字按钮，使用比例定位消息输入框 ({fbInputX},{fbInputY})");
            return (fbInputX, fbInputY, 0, 0, false);
        }

        private static async Task InputSearchKeywordAsync(string keyword, CancellationToken ct)
        {
            Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_A);
            await Task.Delay(50, ct);
            Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_BACK);
            await Task.Delay(65, ct);

            if (!Win32WindowHelper.SetClipboardUnicodeText(keyword))
            {
                AppServices.Logs.Warn("AUTO", "写入群名到剪贴板失败，回退为 Unicode 逐字输入。");
                Win32WindowHelper.SendUnicodeText(keyword);
            }
            else
            {
                await Task.Delay(40, ct);
                Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
            }
        }

        /// <summary>
        /// Polls up to 5 times with cropped Left-Dropdown BitBlt + OCR until the floating search dropdown
        /// is confirmed visible AND contains the target group row under the "群聊" / top result section.
        /// </summary>
        private static async Task<(OcrRecognitionResult? Ocr, Win32WindowHelper.RECT CropRect, MatchedDropdownLine? Match)> WaitForGroupInDropdownAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            string cleanGroup,
            int searchBottomScreenY,
            int slowPad,
            CancellationToken ct)
        {
            int[] pollDelaysMs = [220, 260, 320, 420, 550];

            for (int poll = 0; poll < pollDelaysMs.Length; poll++)
            {
                await Task.Delay(pollDelaysMs[poll] + slowPad, ct);
                if (Win32WindowHelper.IsEscapePressed()) return (null, default, null);

                var cap = await CaptureLeftDropdownRoiAsync(hwnd, rawRect, $"确认搜索下拉框({poll + 1}/{pollDelaysMs.Length})", ct);
                if (cap is null) continue;

                var (ocr, cropRect) = cap.Value;
                var match = LocateGroupInSearchDropdown(ocr, cropRect, cleanGroup, searchBottomScreenY);
                if (match is not null && match.ConfirmedDropdownVisible)
                {
                    return (ocr, cropRect, match);
                }

                AppServices.Logs.Info("OCR",
                    $"第 {poll + 1}/{pollDelaysMs.Length} 次等待下拉框确认：尚未稳定出现目标群「{cleanGroup}」（下拉可见={(match?.ConfirmedDropdownVisible == true ? "是" : "否")}），继续等待...");
            }

            return (null, default, null);
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

        // ---- Cropped ROI Screen-Composited BitBlt + RapidOCR Helpers ----

        private static Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureTopBarRoiAsync(
            nint hwnd, Win32WindowHelper.RECT rawRect, string stageLabel, CancellationToken ct) =>
            CaptureCropOverWindowAndOcrAsync(hwnd, rawRect, 0.0, 0.0, 1.0, 0.16, stageLabel, ct);

        private static Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureLeftDropdownRoiAsync(
            nint hwnd, Win32WindowHelper.RECT rawRect, string stageLabel, CancellationToken ct) =>
            CaptureCropOverWindowAndOcrAsync(hwnd, rawRect, 0.0, 0.05, 0.52, 0.70, stageLabel, ct);

        private static Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureBottomRightRoiAsync(
            nint hwnd, Win32WindowHelper.RECT rawRect, string stageLabel, CancellationToken ct) =>
            CaptureCropOverWindowAndOcrAsync(hwnd, rawRect, 0.65, 0.74, 0.35, 0.26, stageLabel, ct);

        private static async Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureCropOverWindowAndOcrAsync(
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
            AppServices.Logs.Info("OCR", $"[{stageLabel}] 区域识别完成：{ocr.ImageWidth}x{ocr.ImageHeight}，{ocr.Lines.Count} 行文本，耗时 {ocr.ElapsedMilliseconds} ms");
            return (ocr, cropRect);
        }

        private static bool HasIdleSearchPlaceholder(OcrRecognitionResult topOcr)
        {
            double maxX = topOcr.ImageWidth * 0.36;
            return topOcr.Lines.Any(l =>
                l.Bounds.X <= maxX &&
                l.Text.Contains("搜索", StringComparison.Ordinal) &&
                !l.Text.Contains("网络", StringComparison.Ordinal));
        }

        private static (int ScreenX, int ScreenY, int BottomScreenY) LocateSearchBarInTopBar(
            OcrRecognitionResult topOcr,
            Win32WindowHelper.RECT topRect,
            Win32WindowHelper.RECT rawRect,
            double scale)
        {
            double maxX = topOcr.ImageWidth * 0.38;

            var candidate = topOcr.Lines
                .Where(l =>
                    l.Bounds.X <= maxX &&
                    l.Text.Contains("搜索", StringComparison.Ordinal) &&
                    !l.Text.Contains("网络", StringComparison.Ordinal))
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault();

            if (candidate is not null)
            {
                double cx = candidate.Bounds.X + Math.Max(candidate.Bounds.Width * 0.65, 28 * scale);
                double cy = candidate.Bounds.Y + candidate.Bounds.Height * 0.5;
                var (sx, sy) = ImageToScreen(topRect, topOcr, cx, cy);
                var (_, bottomSy) = ImageToScreen(topRect, topOcr, cx, candidate.Bounds.Y + candidate.Bounds.Height);
                AppServices.Logs.Info("OCR", $"已通过顶部区域 OCR 定位搜索栏「{candidate.Text}」 -> 屏幕 ({sx},{sy})");
                return (sx, sy, bottomSy);
            }

            int fsx = rawRect.Left + (int)Math.Round(Math.Clamp(rawRect.Width * 0.18, 85 * scale, rawRect.Width * 0.28));
            int fsy = rawRect.Top + (int)Math.Round(Math.Clamp(rawRect.Height * 0.08, 42 * scale, rawRect.Height * 0.11));
            int fBottom = fsy + (int)Math.Round(18 * scale);
            AppServices.Logs.Info("OCR", $"顶部未直接识别到「搜索」占位字，回退比例定位搜索栏 -> 屏幕 ({fsx},{fsy})");
            return (fsx, fsy, fBottom);
        }

        private static bool IsSearchDropdownStillOpen(
            OcrRecognitionResult leftOcr,
            Win32WindowHelper.RECT cropRect,
            int searchBottomScreenY)
        {
            double syRatio = leftOcr.ImageHeight > 0 ? (double)cropRect.Height / leftOcr.ImageHeight : 1.0;
            double minLocalY = Math.Max(0, (searchBottomScreenY - cropRect.Top) / syRatio - 4);
            double minX = leftOcr.ImageWidth * 0.06;

            return leftOcr.Lines.Any(l =>
                l.Bounds.X >= minX &&
                l.Bounds.Y >= minLocalY &&
                DropdownPresenceMarkers.Any(m => l.Text.Trim().StartsWith(m, StringComparison.Ordinal)));
        }

        private static MatchedDropdownLine? LocateGroupInSearchDropdown(
            OcrRecognitionResult leftOcr,
            Win32WindowHelper.RECT cropRect,
            string targetGroupName,
            int searchBottomScreenY)
        {
            double syRatio = leftOcr.ImageHeight > 0 ? (double)cropRect.Height / leftOcr.ImageHeight : 1.0;
            double searchBottomLocalY = Math.Max(0, (searchBottomScreenY - cropRect.Top) / syRatio);
            double minX = leftOcr.ImageWidth * 0.06;

            var dropdownLines = leftOcr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.Y >= searchBottomLocalY - 2)
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
                bandBottom = nextHeader is not null ? nextHeader.Bounds.Y - 4 : leftOcr.ImageHeight * 0.92;
                AppServices.Logs.Info("OCR", $"搜索下拉框已确认出现「群聊」栏目：Y=[{bandTop:F0}..{bandBottom:F0}]");
            }
            else
            {
                insideGroupSection = false;
                bandTop = searchBottomLocalY + 4;
                var firstBadHeader = otherHeaders
                    .FirstOrDefault(h => h.Text.Contains("网络", StringComparison.Ordinal) || h.Text.Contains("聊天记录", StringComparison.Ordinal));
                bandBottom = firstBadHeader is not null ? firstBadHeader.Bounds.Y - 4 : leftOcr.ImageHeight * 0.72;
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
                string preview = string.Join(" | ", dropdownLines.Take(10).Select(l => $"{l.Text}@{l.Bounds.Y:F0}"));
                AppServices.Logs.Warn("OCR", $"下拉框「群聊」区未匹配到「{targetGroupName}」，识别内容：{preview}");
                return null;
            }

            return scored
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Bounds.Y)
                .First();
        }

        private static (bool Ok, string DetectedHeader) VerifyOpenedChatTitle(OcrRecognitionResult topOcr, string targetGroupName)
        {
            double minX = topOcr.ImageWidth * 0.33;
            double maxX = topOcr.ImageWidth * 0.88;

            var headerLines = topOcr.Lines
                .Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX)
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

        private static OcrLineResult? LocateSendButtonInRoi(OcrRecognitionResult bottomRightOcr)
        {
            return bottomRightOcr.Lines
                .Where(l =>
                    l.Text.Trim().Equals("发送", StringComparison.Ordinal) ||
                    l.Text.Trim().Equals("Send", StringComparison.OrdinalIgnoreCase) ||
                    l.Text.Trim().Contains("发送", StringComparison.Ordinal))
                .OrderByDescending(l => l.Bounds.Y)
                .ThenByDescending(l => l.Bounds.X)
                .FirstOrDefault();
        }

        private static (int ScreenX, int ScreenY) ImageToScreen(
            Win32WindowHelper.RECT cropRect,
            OcrRecognitionResult ocr,
            double imgX,
            double imgY)
        {
            double sxRatio = ocr.ImageWidth > 0 ? (double)cropRect.Width / ocr.ImageWidth : 1.0;
            double syRatio = ocr.ImageHeight > 0 ? (double)cropRect.Height / ocr.ImageHeight : 1.0;
            int sx = cropRect.Left + (int)Math.Round(imgX * sxRatio);
            int sy = cropRect.Top + (int)Math.Round(imgY * syRatio);
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

        private static PreparedGroupSession PrepareFail(string groupName, string msg, Stopwatch sw)
        {
            sw.Stop();
            return new PreparedGroupSession
            {
                Success = false,
                GroupName = groupName,
                Message = msg,
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        private static PreparedGroupSession PrepareCancelled(string groupName, Stopwatch sw)
        {
            sw.Stop();
            AppServices.Logs.Warn("AUTO", $"检测到用户按下 ESC 键，中止预开群「{groupName}」。");
            return new PreparedGroupSession
            {
                Success = false,
                WasCancelledByEsc = true,
                GroupName = groupName,
                Message = "用户按下 ESC 紧急中止了自动回复操作。",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        [GeneratedRegex(@"\s*[（(]\s*\d+\s*[)）]\s*$")]
        private static partial Regex TrailingMemberCountRegex();
    }
}
