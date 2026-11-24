using System.Text.RegularExpressions;
using Client.Models;

namespace Client.Services
{
    public static partial class WeChatOcrAnalyzer
    {
        private static readonly string[] DisconnectedKeywords =
        [
            "网络已断开",
            "网络不可用",
            "已退出登录",
            "重新登录",
            "在其他设备登录",
            "账号已在其他设备",
            "连接已断开"
        ];

        private static readonly string[] LoginPageKeywords =
        [
            "扫码登录",
            "微信登录",
            "请在手机上确认",
            "切换账号",
            "二维码已失效",
            "进入微信",
            "企业微信登录"
        ];

        private static readonly string[] LoggedInKeywords =
        [
            "搜索",
            "通讯录",
            "文件传输助手",
            "发消息",
            "朋友圈",
            "聊天记录",
            "收藏",
            "微信号"
        ];

        public static WeChatOcrAnalysis Analyze(WeChatWindowInfo? window, OcrRecognitionResult ocr)
        {
            string fullText = ocr.FullText;
            var lines = ocr.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();

            var (state, reason) = InferLoginState(window, fullText);
            var (wechatAccount, region, note, wechatName) = ExtractCandidateFields(lines);

            return new WeChatOcrAnalysis
            {
                InferredState = state,
                StateReason = reason,
                CandidateWechatName = wechatName,
                CandidateWechatAccount = wechatAccount,
                CandidateNote = note,
                CandidateRegion = region
            };
        }

        private static (WeChatLoginState State, string Reason) InferLoginState(
            WeChatWindowInfo? window,
            string fullText)
        {
            foreach (var kw in DisconnectedKeywords)
            {
                if (fullText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return (WeChatLoginState.Disconnected, $"命中掉线/退出提示关键词：“{kw}”");
                }
            }

            foreach (var kw in LoginPageKeywords)
            {
                if (fullText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return (WeChatLoginState.LoginPage, $"命中登录页关键词：“{kw}”");
                }
            }

            var matchedMain = LoggedInKeywords
                .Where(kw => fullText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                .Take(3)
                .ToList();

            if (matchedMain.Count > 0)
            {
                return (WeChatLoginState.LoggedIn, $"命中主界面/资料页关键词：{string.Join("、", matchedMain)}");
            }

            if (window is not null && window.DetectedState != WeChatLoginState.Unknown)
            {
                return (window.DetectedState, $"按窗口特征 ({window.ClassName}) 推断，OCR 未命中特定状态词");
            }

            return (WeChatLoginState.Unknown, "未命中明确的登录/主界面/掉线特征词（如为非微信截图属正常现象）");
        }

        private static (string Account, string Region, string Note, string Name) ExtractCandidateFields(List<string> lines)
        {
            string account = string.Empty;
            string region = string.Empty;
            string note = string.Empty;
            string name = string.Empty;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];

                if (string.IsNullOrEmpty(account))
                {
                    var m = WechatIdRegex().Match(line);
                    if (m.Success)
                    {
                        account = m.Groups[1].Value.Trim();
                        // Often the nickname or remark appears 1-2 lines right above "微信号:" on profile cards.
                        if (string.IsNullOrEmpty(name) && i > 0)
                        {
                            string prev = lines[i - 1];
                            if (!prev.Contains("昵称", StringComparison.Ordinal) &&
                                !prev.Contains("微信号", StringComparison.Ordinal) &&
                                prev.Length <= 40)
                            {
                                name = prev;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(name))
                {
                    var m = NicknameRegex().Match(line);
                    if (m.Success)
                    {
                        name = m.Groups[1].Value.Trim();
                    }
                }

                if (string.IsNullOrEmpty(note))
                {
                    var m = RemarkRegex().Match(line);
                    if (m.Success)
                    {
                        note = m.Groups[1].Value.Trim();
                    }
                }

                if (string.IsNullOrEmpty(region))
                {
                    var m = RegionRegex().Match(line);
                    if (m.Success)
                    {
                        region = m.Groups[1].Value.Trim();
                    }
                }
            }

            return (account, region, note, name);
        }

        [GeneratedRegex(@"微信号\s*[:：]\s*([A-Za-z0-9_\-]+)")]
        private static partial Regex WechatIdRegex();

        [GeneratedRegex(@"昵称\s*[:：]\s*(.+)")]
        private static partial Regex NicknameRegex();

        [GeneratedRegex(@"备注(?:名)?\s*[:：]\s*(.+)")]
        private static partial Regex RemarkRegex();

        [GeneratedRegex(@"地区\s*[:：]\s*(.+)")]
        private static partial Regex RegionRegex();
    }
}
