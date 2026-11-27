using System.Diagnostics;
using System.Text.RegularExpressions;
using Client.Models;

namespace Client.Services
{
    /// <summary>
    /// Visual OCR + Win32 hardware-input RPA executor for Windows WeCom (企业微信 / WXWork 4.x &amp; 5.x).
    /// Supports private &amp; group chats, text, group @mentions, and image/video/file delivery via CF_HDROP paste,
    /// with top-bar title verification and post-send message.db WAL delivery confirmation.
    /// </summary>
    public sealed partial class WeComAutoReplyExecutor
    {
        private static readonly string[] DropdownStopHeaders =
        [
            "聊天记录", "功能", "微盘", "文档", "邮件", "应用", "收藏", "网络搜索", "日程", "会议"
        ];

        public async Task<(bool Success, bool VerifiedWal, string Message)> SendMessageAsync(
            string searchKey,
            string expectedDisplayName,
            bool isGroup,
            string? text = null,
            string? filePath = null,
            IReadOnlyList<string>? atDisplayNames = null,
            string? verifyConversationId = null,
            CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            string cleanKey = (searchKey ?? string.Empty).Trim();
            string cleanTitle = string.IsNullOrWhiteSpace(expectedDisplayName) ? cleanKey : expectedDisplayName.Trim();
            string cleanText = (text ?? string.Empty).Trim();
            bool hasFile = !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);

            if (string.IsNullOrWhiteSpace(cleanKey))
            {
                return (false, false, "缺少企业微信目标联系人或群名称。");
            }
            if (string.IsNullOrWhiteSpace(cleanText) && !hasFile)
            {
                return (false, false, "待发送的消息正文和文件均为空。");
            }
            if (!AppServices.Ocr.RapidEngine.IsAvailable)
            {
                return (false, false, $"本地图文识别组件不可用：{AppServices.Ocr.RapidEngine.AvailabilityNote}");
            }

            var cfg = AppServices.GroupAutoReply.Config;
            int slowPad = cfg.SlowStableMode ? 240 : 0;
            bool prevOverlay = ClickVisualOverlay.IsEnabled;
            ClickVisualOverlay.IsEnabled = cfg.ShowClickOverlay;
            string? savedClipboard = cfg.RestoreClipboardAfterSend
                ? Win32WindowHelper.TryGetClipboardUnicodeText()
                : null;
            bool touchedClipboard = false;

            try
            {
                if (Win32WindowHelper.IsEscapePressed()) return (false, false, "用户按下 ESC 中止发送。");

                var ensure = await AppServices.WeChatWindows.EnsureEnterpriseWeChatWindowReadyAsync(ct);
                if (!ensure.Success || ensure.Window is null)
                {
                    return (false, false, $"未能唤起企业微信主窗口：{ensure.Message}");
                }

                nint hwnd = ensure.Window.Hwnd;
                string? uipiErr = Win32WindowHelper.GetPrivilegeMismatchDetail(hwnd);
                if (!string.IsNullOrWhiteSpace(uipiErr))
                {
                    return (false, false, uipiErr.Replace("微信", "企业微信"));
                }

                bool alreadyFg = Win32WindowHelper.GetForegroundWindow() == hwnd;
                _ = alreadyFg || Win32WindowHelper.EnsureWindowTrulyActive(hwnd);
                await Task.Delay(alreadyFg ? (60 + slowPad / 2) : (220 + slowPad), ct);

                if (!Win32WindowHelper.GetWindowRect(hwnd, out var rawRect) || rawRect.Width < 360 || rawRect.Height < 280)
                {
                    return (false, false, "无法获取有效的企业微信窗口尺寸。");
                }

                uint dpi = Win32WindowHelper.GetDpiForWindow(hwnd);
                double scale = dpi > 0 ? dpi / 96.0 : 1.0;

                // Step 1: Top-bar fast check — if WeCom is already opened on the target conversation, skip searching!
                var topCap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.0, 0.0, 1.0, 0.16, "企微顶部快检", ct);
                bool switched = false;
                if (topCap is not null)
                {
                    var (alreadyIn, header) = VerifyWeComTitle(topCap.Value.Ocr, cleanTitle);
                    if (alreadyIn)
                    {
                        AppServices.Logs.Info("WECOM-RPA", $"企微免搜索直达：当前窗口已是「{header}」。");
                        switched = true;
                    }
                }

                // Step 2: Search & click target contact/group
                if (!switched)
                {
                    var keysToTry = new List<string>(2) { cleanKey };
                    if (!cleanTitle.Equals(cleanKey, StringComparison.OrdinalIgnoreCase))
                    {
                        keysToTry.Add(cleanTitle);
                    }

                    foreach (string query in keysToTry)
                    {
                        if (Win32WindowHelper.IsEscapePressed()) return (false, false, "用户按下 ESC 中止发送。");

                        // Dismiss any lingering popover, then focus WeCom search bar via Ctrl+F
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        await Task.Delay(120 + slowPad, ct);
                        Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_F);
                        await Task.Delay(180 + slowPad, ct);

                        Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_A);
                        await Task.Delay(40, ct);
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_BACK);
                        await Task.Delay(50, ct);

                        touchedClipboard = true;
                        if (Win32WindowHelper.SetClipboardUnicodeText(query))
                        {
                            await Task.Delay(40, ct);
                            Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                        }
                        else
                        {
                            Win32WindowHelper.SendUnicodeText(query);
                        }

                        // Poll search dropdown (WeCom search dropdown appears below top search bar on left/center-left)
                        int[] polls = [260, 320, 420, 520];
                        OcrLineResult? matchedLine = null;
                        Win32WindowHelper.RECT matchedRect = default;
                        OcrRecognitionResult? matchedOcr = null;

                        for (int p = 0; p < polls.Length; p++)
                        {
                            await Task.Delay(polls[p] + slowPad, ct);
                            if (Win32WindowHelper.IsEscapePressed()) return (false, false, "用户按下 ESC 中止发送。");

                            var dropCap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.0, 0.06, 0.56, 0.72, $"企微搜索下拉({p + 1})", ct);
                            if (dropCap is null) continue;
                            var hit = LocateTargetInWeComDropdown(dropCap.Value.Ocr, query, cleanTitle, isGroup);
                            if (hit is not null)
                            {
                                matchedLine = hit;
                                matchedOcr = dropCap.Value.Ocr;
                                matchedRect = dropCap.Value.CropRect;
                                break;
                            }
                        }

                        if (matchedLine is not null && matchedOcr is not null)
                        {
                            var (cx, cy) = ImageToScreen(
                                matchedRect, matchedOcr,
                                matchedLine.Bounds.X + matchedLine.Bounds.Width * 0.5,
                                matchedLine.Bounds.Y + matchedLine.Bounds.Height * 0.5);
                            Win32WindowHelper.LeftClickAt(cx, cy, hwnd, $"点击企微目标「{cleanTitle}」", skipReactivate: true);
                            await Task.Delay(320 + slowPad, ct);

                            var verify = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.0, 0.0, 1.0, 0.16, "企微核验窗口标题", ct);
                            if (verify is not null)
                            {
                                var (okTitle, detected) = VerifyWeComTitle(verify.Value.Ocr, cleanTitle);
                                if (okTitle)
                                {
                                    AppServices.Logs.Success("WECOM-RPA", $"企微已切换到目标会话「{detected}」。");
                                    switched = true;
                                    break;
                                }
                            }

                            // Enter fallback once if dropdown item was highlighted
                            Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                            await Task.Delay(300 + slowPad, ct);
                            var verify2 = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.0, 0.0, 1.0, 0.16, "企微二次核验标题", ct);
                            if (verify2 is not null && VerifyWeComTitle(verify2.Value.Ocr, cleanTitle).Ok)
                            {
                                switched = true;
                                break;
                            }
                        }
                    }

                    if (!switched)
                    {
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_ESCAPE);
                        return (false, false, $"未能在企业微信中准确切换到「{cleanTitle}」，已安全中止发送以防发错对象。");
                    }
                }

                // Step 3: Locate input box & send button
                var (inputX, inputY, sendX, sendY, hasSendBtn) = await LocateWeComInputAndSendAsync(hwnd, rawRect, scale, ct);
                Win32WindowHelper.LeftClickAt(inputX, inputY, hwnd, "点击企微消息输入框", skipReactivate: true);
                await Task.Delay(120 + slowPad, ct);

                DateTimeOffset sentWindowStartUtc = DateTimeOffset.UtcNow;

                // Step 4A: Send file/image/video via CF_HDROP paste
                if (hasFile && filePath is not null)
                {
                    touchedClipboard = true;
                    if (!Win32WindowHelper.SetClipboardFileDropList(filePath))
                    {
                        return (false, false, $"将文件写入剪贴板失败：{filePath}");
                    }
                    await Task.Delay(60, ct);
                    Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                    await Task.Delay(420 + slowPad, ct);

                    // Confirm paste & send (WeCom pastes images into input box or pops send-file confirm; Enter sends cleanly)
                    Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                    await Task.Delay(260 + slowPad, ct);
                    if (hasSendBtn)
                    {
                        Win32WindowHelper.LeftClickAt(sendX, sendY, hwnd, "点击企微发送按钮(媒体)", skipReactivate: true);
                        await Task.Delay(180 + slowPad, ct);
                    }
                }

                // Step 4B: Send text (with optional @mentions in group)
                if (!string.IsNullOrWhiteSpace(cleanText))
                {
                    if (isGroup && atDisplayNames is { Count: > 0 })
                    {
                        foreach (string rawAt in atDisplayNames)
                        {
                            string atName = (rawAt ?? string.Empty).Trim();
                            if (string.IsNullOrWhiteSpace(atName)) continue;
                            Win32WindowHelper.SendUnicodeText("@");
                            await Task.Delay(140 + slowPad / 2, ct);
                            if (atName.Equals("notify@all", StringComparison.OrdinalIgnoreCase) || atName == "所有人")
                            {
                                Win32WindowHelper.SendUnicodeText("所有人");
                            }
                            else
                            {
                                Win32WindowHelper.SendUnicodeText(atName);
                            }
                            await Task.Delay(220 + slowPad / 2, ct);
                            Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                            await Task.Delay(100, ct);
                        }
                    }

                    touchedClipboard = true;
                    if (Win32WindowHelper.SetClipboardUnicodeText(cleanText))
                    {
                        await Task.Delay(50, ct);
                        Win32WindowHelper.SendKeyCombo(Win32WindowHelper.VK_CONTROL, Win32WindowHelper.VK_V);
                    }
                    else
                    {
                        Win32WindowHelper.SendUnicodeText(cleanText);
                    }
                    await Task.Delay(180 + slowPad, ct);

                    if (hasSendBtn)
                    {
                        Win32WindowHelper.LeftClickAt(sendX, sendY, hwnd, "点击企微发送按钮", skipReactivate: true);
                    }
                    else
                    {
                        Win32WindowHelper.SendVirtualKey(Win32WindowHelper.VK_RETURN);
                    }
                }

                // Step 5: Post-send WAL confirmation in WeCom message.db
                bool walConfirmed = false;
                if (!string.IsNullOrWhiteSpace(verifyConversationId))
                {
                    walConfirmed = await AppServices.WeComSync.VerifyOutgoingMessageDeliveredAsync(
                        verifyConversationId,
                        cleanText,
                        sentWindowStartUtc,
                        ct);
                }

                sw.Stop();
                AppServices.Logs.Success("WECOM-RPA",
                    $"企微消息发送完成（目标=「{cleanTitle}」，耗时 {sw.ElapsedMilliseconds} ms，WAL确权={(walConfirmed ? "已确认" : "待核验")}）。");
                return (true, walConfirmed, walConfirmed
                    ? $"已向企微「{cleanTitle}」发送并通过 message.db WAL 落库确权。"
                    : $"已向企微「{cleanTitle}」执行发送（耗时 {sw.ElapsedMilliseconds} ms）。");
            }
            catch (OperationCanceledException)
            {
                return (false, false, "企业微信发送已取消。");
            }
            catch (Exception ex)
            {
                AppServices.Logs.Error("WECOM-RPA", $"企微发送异常：{ex.Message}");
                return (false, false, $"企业微信发送异常：{ex.Message}");
            }
            finally
            {
                ClickVisualOverlay.IsEnabled = prevOverlay;
                if (touchedClipboard && cfg.RestoreClipboardAfterSend && savedClipboard is not null)
                {
                    try { _ = Win32WindowHelper.SetClipboardUnicodeText(savedClipboard); } catch { }
                }
            }
        }

        private static async Task<(OcrRecognitionResult Ocr, Win32WindowHelper.RECT CropRect)?> CaptureCropAndOcrAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double fx,
            double fy,
            double fw,
            double fh,
            string label,
            CancellationToken ct)
        {
            ClickVisualOverlay.HideImmediately();
            await Task.Delay(35, ct);
            if (Win32WindowHelper.GetWindowRect(hwnd, out var cur) && cur.Width >= 320 && cur.Height >= 250)
            {
                rawRect = cur;
            }
            int left = rawRect.Left + (int)Math.Floor(rawRect.Width * fx);
            int top = rawRect.Top + (int)Math.Floor(rawRect.Height * fy);
            int width = Math.Max(64, (int)Math.Ceiling(rawRect.Width * fw));
            int height = Math.Max(48, (int)Math.Ceiling(rawRect.Height * fh));
            if (left + width > rawRect.Right) width = Math.Max(64, rawRect.Right - left);
            if (top + height > rawRect.Bottom) height = Math.Max(48, rawRect.Bottom - top);

            var rect = new Win32WindowHelper.RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
            using var bmp = await Task.Run(() => Win32WindowHelper.CaptureScreenRectToSoftwareBitmap(left, top, width, height), ct);
            var ocr = await AppServices.Ocr.RapidEngine.RecognizeAsync(bmp, ct);
            AppServices.Logs.Info("OCR", $"[{label}] {ocr.ImageWidth}x{ocr.ImageHeight}, {ocr.Lines.Count} 行, {ocr.ElapsedMilliseconds} ms");
            return (ocr, rect);
        }

        private static OcrLineResult? LocateTargetInWeComDropdown(
            OcrRecognitionResult ocr,
            string searchKey,
            string expectedTitle,
            bool isGroup)
        {
            var lines = ocr.Lines
                .Where(l => l.Bounds.Y >= ocr.ImageHeight * 0.06 && l.Bounds.X >= ocr.ImageWidth * 0.05)
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            string[] sectionKeywords = isGroup
                ? ["群聊", "客户群", "外部群"]
                : ["联系人", "外部联系人", "客户", "同事", "微信联系人"];

            var preferredHeader = lines.FirstOrDefault(l =>
                sectionKeywords.Any(k => l.Text.Trim().StartsWith(k, StringComparison.Ordinal)));

            var stopHeaders = lines
                .Where(l => DropdownStopHeaders.Any(h => l.Text.Trim().StartsWith(h, StringComparison.Ordinal)))
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            double bandTop = preferredHeader is not null
                ? preferredHeader.Bounds.Y + preferredHeader.Bounds.Height * 0.6
                : ocr.ImageHeight * 0.08;
            var nextStop = stopHeaders.FirstOrDefault(h => h.Bounds.Y > bandTop + 6);
            double bandBottom = nextStop is not null ? nextStop.Bounds.Y - 4 : ocr.ImageHeight * 0.90;

            string normTitle = NormalizeTitle(expectedTitle);
            string normKey = NormalizeTitle(searchKey);

            OcrLineResult? bestLine = null;
            double bestScore = 0;

            foreach (var line in lines)
            {
                double cy = line.Bounds.Y + line.Bounds.Height * 0.5;
                if (cy < bandTop || cy > bandBottom) continue;
                string t = line.Text.Trim();
                if (string.IsNullOrWhiteSpace(t)) continue;
                if (sectionKeywords.Any(k => t == k) || DropdownStopHeaders.Any(h => t.StartsWith(h, StringComparison.Ordinal)))
                {
                    continue;
                }

                string norm = NormalizeTitle(t);
                double score = Math.Max(ComputeSimilarity(normTitle, norm), ComputeSimilarity(normKey, norm));
                if (score >= 0.55 && score > bestScore)
                {
                    bestScore = score;
                    bestLine = line;
                }
            }

            return bestLine;
        }

        private static (bool Ok, string Header) VerifyWeComTitle(OcrRecognitionResult topOcr, string expectedTitle)
        {
            double minX = topOcr.ImageWidth * 0.28;
            double maxX = topOcr.ImageWidth * 0.88;
            string targetNorm = NormalizeTitle(expectedTitle);
            double bestScore = 0;
            string bestHeader = string.Empty;

            foreach (var line in topOcr.Lines.Where(l => l.Bounds.X >= minX && l.Bounds.X <= maxX))
            {
                double s = ComputeSimilarity(targetNorm, NormalizeTitle(line.Text));
                if (s > bestScore)
                {
                    bestScore = s;
                    bestHeader = line.Text.Trim();
                }
            }

            return (bestScore >= 0.58, bestHeader);
        }

        private static async Task<(int InputX, int InputY, int SendX, int SendY, bool HasSend)> LocateWeComInputAndSendAsync(
            nint hwnd,
            Win32WindowHelper.RECT rawRect,
            double scale,
            CancellationToken ct)
        {
            var cap = await CaptureCropAndOcrAsync(hwnd, rawRect, 0.65, 0.72, 0.35, 0.28, "企微定位发送按钮", ct);
            if (cap is not null)
            {
                var (ocr, cropRect) = cap.Value;
                var sendLine = ocr.Lines
                    .Where(l => l.Text.Trim().Equals("发送", StringComparison.Ordinal) ||
                                l.Text.Trim().Equals("Send", StringComparison.OrdinalIgnoreCase) ||
                                l.Text.Trim().Contains("发送", StringComparison.Ordinal))
                    .OrderByDescending(l => l.Bounds.Y)
                    .ThenByDescending(l => l.Bounds.X)
                    .FirstOrDefault();

                if (sendLine is not null)
                {
                    var (sx, sy) = ImageToScreen(
                        cropRect, ocr,
                        sendLine.Bounds.X + sendLine.Bounds.Width * 0.5,
                        sendLine.Bounds.Y + sendLine.Bounds.Height * 0.5);

                    int ix = (int)Math.Round(Math.Clamp(
                        rawRect.Left + rawRect.Width * 0.56,
                        rawRect.Left + rawRect.Width * 0.40,
                        sx - 45 * scale));
                    int iy = (int)Math.Round(Math.Clamp(
                        sy - 78 * scale,
                        rawRect.Top + rawRect.Height * 0.72,
                        sy - 34 * scale));
                    return (ix, iy, sx, sy, true);
                }
            }

            return (
                rawRect.Left + (int)Math.Round(rawRect.Width * 0.58),
                rawRect.Top + (int)Math.Round(rawRect.Height * 0.85),
                0, 0, false);
        }

        private static (int ScreenX, int ScreenY) ImageToScreen(
            Win32WindowHelper.RECT cropRect,
            OcrRecognitionResult ocr,
            double imgX,
            double imgY)
        {
            double sxRatio = ocr.ImageWidth > 0 ? (double)cropRect.Width / ocr.ImageWidth : 1.0;
            double syRatio = ocr.ImageHeight > 0 ? (double)cropRect.Height / ocr.ImageHeight : 1.0;
            return (
                cropRect.Left + (int)Math.Round(imgX * sxRatio),
                cropRect.Top + (int)Math.Round(imgY * syRatio));
        }

        private static string NormalizeTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            string s = TrailingCountRegex().Replace(raw.Trim(), string.Empty);
            // Strip "@微信" suffix shown on WeCom external contacts
            s = s.Replace("@微信", "", StringComparison.Ordinal);
            return new string(s.Where(c => !char.IsWhiteSpace(c) && c != '\u2005' && c != '·').ToArray()).ToLowerInvariant();
        }

        private static double ComputeSimilarity(string targetNorm, string candidateNorm)
        {
            if (string.IsNullOrEmpty(targetNorm) || string.IsNullOrEmpty(candidateNorm)) return 0;
            if (targetNorm == candidateNorm) return 1.0;
            if (candidateNorm.Contains(targetNorm, StringComparison.Ordinal))
                return 0.88 + 0.11 * ((double)targetNorm.Length / candidateNorm.Length);
            if (targetNorm.Contains(candidateNorm, StringComparison.Ordinal) && candidateNorm.Length >= 2)
                return 0.75 + 0.15 * ((double)candidateNorm.Length / targetNorm.Length);

            var tSet = new HashSet<char>(targetNorm);
            var cSet = new HashSet<char>(candidateNorm);
            int inter = tSet.Count(cSet.Contains);
            if (inter == 0) return 0;
            double recall = (double)inter / tSet.Count;
            double precision = (double)inter / cSet.Count;
            return 2.0 * recall * precision / (recall + precision);
        }

        [GeneratedRegex(@"\s*[（(]\s*\d+\s*[)）]\s*$")]
        private static partial Regex TrailingCountRegex();
    }
}
