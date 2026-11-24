using System.Text.RegularExpressions;
using Client.Models;

namespace Client.Services
{
    public static partial class WeChatOcrAnalyzer
    {
        private static readonly string[] DisconnectedKeywords =
        [
            "网络已断开", "网络不可用", "已退出登录", "重新登录", "在其他设备登录", "账号已在其他设备", "连接已断开"
        ];

        private static readonly string[] LoginPageKeywords =
        [
            "扫码登录", "微信登录", "请在手机上确认", "切换账号", "二维码已失效", "进入微信", "企业微信登录"
        ];

        private static readonly string[] LoggedInKeywords =
        [
            "搜索", "通讯录", "文件传输助手", "发消息", "朋友圈", "聊天记录", "收藏", "微信号", "朋友资料"
        ];

        private static readonly HashSet<string> PlaceholderRemarks = new(StringComparer.OrdinalIgnoreCase)
        {
            "添加备注名", "点击添加备注", "设置备注和标签", "添加备注", "设置备注"
        };

        private static readonly HashSet<string> KnownSectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "朋友资料", "更多信息", "朋友圈", "朋友权限", "发消息", "语音聊天", "视频聊天",
            "备注", "备注名", "电话", "手机号", "个性签名", "来源", "添加时间", "地区", "微信号", "昵称", "共同群聊"
        };

        public static WeChatOcrAnalysis Analyze(WeChatWindowInfo? window, OcrRecognitionResult ocr)
        {
            var (state, reason) = InferLoginState(window, ocr.FullText);
            var rows = GroupIntoVisualRows(ocr.Lines);
            var profile = ExtractProfileFromRows(rows);

            return new WeChatOcrAnalysis
            {
                InferredState = state,
                StateReason = reason,
                CandidateWechatName = profile.Name,
                CandidateWechatAccount = profile.Account,
                CandidateNote = profile.Note,
                CandidateRegion = profile.Region,
                CandidatePhone = profile.Phone,
                CandidateSignature = profile.Signature,
                CandidateSource = profile.Source,
                CandidateAddedDate = profile.AddedDate
            };
        }

        public static GroupOcrAnalysis AnalyzeGroup(OcrRecognitionResult ocr)
        {
            var rows = GroupIntoVisualRows(ocr.Lines);
            string groupName = string.Empty;
            int memberCount = 0;
            var memberCandidates = new List<string>();

            foreach (var row in rows)
            {
                string joined = string.Join(" ", row.Select(x => x.Text)).Trim();
                if (string.IsNullOrEmpty(joined))
                {
                    continue;
                }

                // Match titles like "2025级硕小组 (12)" or "客户对接群(25)"
                if (string.IsNullOrEmpty(groupName))
                {
                    var mCount = GroupTitleWithCountRegex().Match(joined);
                    if (mCount.Success)
                    {
                        groupName = mCount.Groups[1].Value.Trim();
                        _ = int.TryParse(mCount.Groups[2].Value, out memberCount);
                        continue;
                    }
                }

                var mName = GroupNameLabelRegex().Match(joined);
                if (mName.Success && string.IsNullOrEmpty(groupName))
                {
                    groupName = mName.Groups[1].Value.Trim();
                    continue;
                }

                if (joined.Length <= 24 && !KnownSectionLabels.Contains(joined) && !joined.Contains("群聊名称", StringComparison.Ordinal))
                {
                    memberCandidates.Add(joined);
                }
            }

            if (string.IsNullOrEmpty(groupName) && memberCandidates.Count > 0)
            {
                groupName = memberCandidates[0];
                memberCandidates.RemoveAt(0);
            }

            if (memberCount == 0 && memberCandidates.Count > 1)
            {
                memberCount = memberCandidates.Count;
            }

            return new GroupOcrAnalysis
            {
                CandidateGroupName = groupName,
                CandidateMemberCount = memberCount,
                CandidateMembersText = string.Join("、", memberCandidates.Distinct().Take(30)),
                CandidateNote = ocr.Lines.Count > 0 ? $"OCR 导入（共识别 {ocr.Lines.Count} 行）" : string.Empty
            };
        }

        private static List<List<OcrLineResult>> GroupIntoVisualRows(List<OcrLineResult> lines)
        {
            var rows = new List<List<OcrLineResult>>();
            foreach (var line in lines.OrderBy(l => l.Bounds.Y))
            {
                double cy = line.Bounds.Y + line.Bounds.Height / 2.0;
                var row = rows.FirstOrDefault(r =>
                {
                    double rcy = r.Average(x => x.Bounds.Y + x.Bounds.Height / 2.0);
                    double rh = r.Average(x => x.Bounds.Height);
                    return Math.Abs(cy - rcy) <= Math.Max(14.0, rh * 0.7);
                });

                if (row is not null)
                {
                    row.Add(line);
                }
                else
                {
                    rows.Add([line]);
                }
            }

            return rows
                .OrderBy(r => r.Min(x => x.Bounds.Y))
                .Select(r => r.OrderBy(x => x.Bounds.X).ToList())
                .ToList();
        }

        private static (string Name, string Account, string Note, string Region, string Phone, string Signature, string Source, string AddedDate)
            ExtractProfileFromRows(List<List<OcrLineResult>> rows)
        {
            string name = string.Empty;
            string account = string.Empty;
            string note = string.Empty;
            string region = string.Empty;
            string phone = string.Empty;
            string signature = string.Empty;
            string source = string.Empty;
            string addedDate = string.Empty;

            int accountRowIndex = -1;
            double signatureValueX = -1;
            int signatureRowIndex = -1;

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string fullRow = string.Join(" ", row.Select(x => x.Text.Trim())).Trim();
                if (string.IsNullOrEmpty(fullRow))
                {
                    continue;
                }

                // Inline key:value matching (e.g. "微信号: w341884", "地区: 江苏 常州")
                if (string.IsNullOrEmpty(account))
                {
                    var m = WechatIdRegex().Match(fullRow);
                    if (m.Success)
                    {
                        account = m.Groups[1].Value.Trim();
                        accountRowIndex = i;
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(region))
                {
                    var m = RegionRegex().Match(fullRow);
                    if (m.Success)
                    {
                        region = m.Groups[1].Value.Trim();
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(name))
                {
                    var m = NicknameRegex().Match(fullRow);
                    if (m.Success)
                    {
                        name = m.Groups[1].Value.Trim();
                        continue;
                    }
                }

                // Two-column WeChat 4.x profile table matching (Left label | Right value)
                string label = row[0].Text.Trim().TrimEnd(':', '：');
                string rightValue = row.Count > 1
                    ? string.Join(" ", row.Skip(1).Select(x => x.Text.Trim())).Trim()
                    : string.Empty;

                if (label is "备注" or "备注名" or "备")
                {
                    if (!string.IsNullOrEmpty(rightValue) && !PlaceholderRemarks.Contains(rightValue))
                    {
                        note = rightValue;
                    }
                    continue;
                }

                if (label is "电话" or "手机号")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        phone = rightValue;
                    }
                    continue;
                }

                if (label == "个性签名")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        signature = rightValue;
                        signatureRowIndex = i;
                        signatureValueX = row[1].Bounds.X;
                    }
                    continue;
                }

                if (label == "来源")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        source = rightValue;
                    }
                    continue;
                }

                if (label == "添加时间")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        addedDate = rightValue;
                    }
                    continue;
                }

                // Handle multi-line wrapped signature right below the "个性签名" row (like WeChat 4.x cards!)
                if (signatureRowIndex >= 0 && i == signatureRowIndex + 1 &&
                    row.Count == 1 &&
                    !KnownSectionLabels.Contains(label) &&
                    signatureValueX >= 0 &&
                    Math.Abs(row[0].Bounds.X - signatureValueX) < 60)
                {
                    signature = $"{signature} {row[0].Text.Trim()}".Trim();
                    signatureRowIndex = i;
                }
            }

            // If no explicit "昵称:" label, use the clean headline right above "微信号:" (standard WeChat 4.x card layout)
            if (string.IsNullOrEmpty(name) && accountRowIndex > 0)
            {
                for (int j = accountRowIndex - 1; j >= 0; j--)
                {
                    string candidate = string.Join(" ", rows[j].Select(x => x.Text.Trim())).Trim();
                    candidate = candidate.TrimEnd('·', '.', ' ', '…');
                    if (!string.IsNullOrEmpty(candidate) && candidate.Length <= 40 && !KnownSectionLabels.Contains(candidate))
                    {
                        name = candidate;
                        break;
                    }
                }
            }

            return (name, account, note, region, phone, signature, source, addedDate);
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
                return (window.DetectedState, $"按窗口特征 ({window.ClassName}) 推断");
            }

            return (WeChatLoginState.Unknown, "未命中明确的登录/主界面/掉线特征词");
        }

        [GeneratedRegex(@"微信号\s*[:：]\s*([A-Za-z0-9_\-]+)")]
        private static partial Regex WechatIdRegex();

        [GeneratedRegex(@"昵称\s*[:：]\s*(.+)")]
        private static partial Regex NicknameRegex();

        [GeneratedRegex(@"地区\s*[:：]\s*(.+)")]
        private static partial Regex RegionRegex();

        [GeneratedRegex(@"^(.+?)\s*[\(（]\s*(\d{1,4})\s*[\)）]$")]
        private static partial Regex GroupTitleWithCountRegex();

        [GeneratedRegex(@"群聊名称\s*[:：]\s*(.+)")]
        private static partial Regex GroupNameLabelRegex();
    }
}
