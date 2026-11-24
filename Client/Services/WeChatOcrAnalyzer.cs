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

        private static readonly HashSet<string> PlaceholderDescriptions = new(StringComparer.OrdinalIgnoreCase)
        {
            "添加更多备注信息", "添加描述", "点击添加描述"
        };

        private static readonly HashSet<string> KnownSectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "朋友资料", "更多信息", "朋友圈", "朋友权限", "发消息", "语音聊天", "视频聊天",
            "备注", "备注名", "标签", "电话", "手机号", "描述", "个性签名", "来源", "添加时间",
            "地区", "微信号", "昵称", "共同群聊", "企业", "公司", "部门", "职位", "邮箱", "地址",
            "实名", "视频号", "设置备注和标签"
        };

        private enum ActiveField
        {
            None,
            HeaderNickname,
            HeaderAccount,
            HeaderRegion,
            TableRemark,
            TableTags,
            TablePhone,
            TableDescription,
            TableSignature,
            TableSource,
            TableAddedDate
        }

        public static WeChatOcrAnalysis Analyze(WeChatWindowInfo? window, OcrRecognitionResult ocr)
        {
            var (state, reason) = InferLoginState(window, ocr.FullText);
            var rows = GroupIntoVisualRows(ocr.Lines);
            var profile = ExtractProfileFromRows(rows);

            return new WeChatOcrAnalysis
            {
                InferredState = state,
                StateReason = reason,
                CandidateWechatRemark = profile.Remark,
                CandidateWechatName = profile.Name,
                CandidateWechatAccount = profile.Account,
                CandidateTags = profile.Tags,
                CandidateRegion = profile.Region,
                CandidatePhone = profile.Phone,
                CandidateDescription = profile.Description,
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
                double lineL = line.Bounds.X;
                double lineR = line.Bounds.X + line.Bounds.Width;

                var row = rows.FirstOrDefault(r =>
                {
                    double rcy = r.Average(x => x.Bounds.Y + x.Bounds.Height / 2.0);
                    double rh = r.Average(x => x.Bounds.Height);
                    // Strict vertical alignment: same-row columns align within ~45% of text height (<= 12px);
                    // wrapped lines in the same column sit ~85-100% of text height apart and overlap horizontally!
                    if (Math.Abs(cy - rcy) > Math.Min(12.0, Math.Max(6.0, rh * 0.45)))
                    {
                        return false;
                    }

                    // Never merge two boxes that overlap horizontally (those are vertically stacked wrapped lines!)
                    foreach (var existing in r)
                    {
                        double exL = existing.Bounds.X;
                        double exR = existing.Bounds.X + existing.Bounds.Width;
                        double overlap = Math.Min(lineR, exR) - Math.Max(lineL, exL);
                        double minW = Math.Min(line.Bounds.Width, existing.Bounds.Width);
                        if (minW > 0 && overlap > minW * 0.25)
                        {
                            return false;
                        }
                    }

                    return true;
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

        private static (string Remark, string Name, string Account, string Tags, string Region, string Phone, string Description, string Signature, string Source, string AddedDate)
            ExtractProfileFromRows(List<List<OcrLineResult>> rows)
        {
            string remarkFromTable = string.Empty;
            string explicitNickname = string.Empty;
            string account = string.Empty;
            string tags = string.Empty;
            string region = string.Empty;
            string phone = string.Empty;
            string description = string.Empty;
            string signature = string.Empty;
            string source = string.Empty;
            string addedDate = string.Empty;

            int accountRowIndex = -1;
            int regionRowIndex = -1;

            // Unified multi-line continuation state machine: supports wrapping on EVERY field
            // (昵称 / 微信号 / 地区 / 备注 / 标签 / 电话 / 描述 / 个性签名 / 来源 / 添加时间)
            ActiveField activeField = ActiveField.None;
            bool activeIsHeaderInline = false;
            double activeAnchorX = -1;
            double activeValueX = -1;
            double activePrevBottom = -1;
            double activePrevHeight = 20;

            void SetActive(ActiveField field, bool isHeaderInline, double anchorX, double valueX, double prevBottom, double prevHeight)
            {
                activeField = field;
                activeIsHeaderInline = isHeaderInline;
                activeAnchorX = anchorX;
                activeValueX = valueX;
                activePrevBottom = prevBottom;
                activePrevHeight = prevHeight;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                string fullRow = string.Join(" ", row.Select(x => x.Text.Trim())).Trim();
                if (string.IsNullOrEmpty(fullRow))
                {
                    continue;
                }

                double rowTop = row.Min(x => x.Bounds.Y);
                double rowBottom = row.Max(x => x.Bounds.Y + x.Bounds.Height);
                double rowHeight = Math.Max(12.0, row.Average(x => x.Bounds.Height));
                double rowFirstX = row[0].Bounds.X;

                // 1. Inline header key:value fields (e.g. "微信号: xxx", "昵称: xxx", "地区: xxx")
                if (string.IsNullOrEmpty(account))
                {
                    var m = WechatIdRegex().Match(fullRow);
                    if (m.Success)
                    {
                        account = m.Groups[1].Value.Trim();
                        accountRowIndex = i;
                        SetActive(ActiveField.HeaderAccount, true, rowFirstX, row.Count > 1 ? row[1].Bounds.X : -1, rowBottom, rowHeight);
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(region))
                {
                    var m = RegionRegex().Match(fullRow);
                    if (m.Success)
                    {
                        region = m.Groups[1].Value.Trim();
                        regionRowIndex = i;
                        SetActive(ActiveField.HeaderRegion, true, rowFirstX, row.Count > 1 ? row[1].Bounds.X : -1, rowBottom, rowHeight);
                        continue;
                    }
                }

                if (string.IsNullOrEmpty(explicitNickname))
                {
                    var m = NicknameRegex().Match(fullRow);
                    if (m.Success)
                    {
                        explicitNickname = m.Groups[1].Value.Trim();
                        SetActive(ActiveField.HeaderNickname, true, rowFirstX, row.Count > 1 ? row[1].Bounds.X : -1, rowBottom, rowHeight);
                        continue;
                    }
                }

                // 2. Two-column WeChat 4.x profile table fields (Left label | Right value)
                string label = row[0].Text.Trim().TrimEnd(':', '：');
                string rightValue = row.Count > 1
                    ? string.Join(" ", row.Skip(1).Select(x => x.Text.Trim())).Trim()
                    : string.Empty;
                double rightValueX = row.Count > 1 ? row[1].Bounds.X : -1;

                if (label is "备注" or "备注名" or "备")
                {
                    if (!string.IsNullOrEmpty(rightValue) && !PlaceholderRemarks.Contains(rightValue))
                    {
                        remarkFromTable = rightValue;
                    }
                    SetActive(ActiveField.TableRemark, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label == "标签")
                {
                    if (!string.IsNullOrEmpty(rightValue) && !rightValue.Contains("添加标签", StringComparison.Ordinal))
                    {
                        tags = rightValue;
                    }
                    SetActive(ActiveField.TableTags, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label is "电话" or "手机号")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        phone = rightValue;
                    }
                    SetActive(ActiveField.TablePhone, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label == "描述")
                {
                    if (!string.IsNullOrEmpty(rightValue) && !PlaceholderDescriptions.Contains(rightValue))
                    {
                        description = rightValue;
                    }
                    SetActive(ActiveField.TableDescription, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label == "个性签名")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        signature = rightValue;
                    }
                    SetActive(ActiveField.TableSignature, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label == "来源")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        source = rightValue;
                    }
                    SetActive(ActiveField.TableSource, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                if (label == "添加时间")
                {
                    if (!string.IsNullOrEmpty(rightValue))
                    {
                        addedDate = rightValue;
                    }
                    SetActive(ActiveField.TableAddedDate, false, rowFirstX, rightValueX, rowBottom, rowHeight);
                    continue;
                }

                // Any other known section header or left-column label immediately terminates continuation
                if (KnownSectionLabels.Contains(label) || KnownSectionLabels.Contains(fullRow))
                {
                    activeField = ActiveField.None;
                    continue;
                }

                // 3. Generic multi-line continuation for ANY currently active field (2, 3, ... N wrapped lines)
                if (activeField != ActiveField.None)
                {
                    double verticalGap = rowTop - activePrevBottom;
                    bool verticallyClose = verticalGap <= Math.Max(18.0, activePrevHeight * 1.35);

                    bool horizontallyAligned;
                    if (activeIsHeaderInline)
                    {
                        // Wrapped header value sits indented under the value start (e.g. X=124 after "昵称:" at X=88) or flush with keyX
                        double minX = activeAnchorX - 10;
                        double maxX = activeValueX >= 0
                            ? activeValueX + 45
                            : activeAnchorX + Math.Max(80.0, activePrevHeight * 4.5);
                        horizontallyAligned = rowFirstX >= minX && rowFirstX <= maxX;
                    }
                    else
                    {
                        // Wrapped table value sits strictly inside the right column (never in the left label column!)
                        bool rightOfLabel = activeAnchorX < 0 || rowFirstX > activeAnchorX + 25;
                        bool alignedWithValue = activeValueX < 0 || Math.Abs(rowFirstX - activeValueX) <= Math.Max(48.0, activePrevHeight * 2.2);
                        horizontallyAligned = rightOfLabel && alignedWithValue;
                    }

                    if (!verticallyClose || !horizontallyAligned)
                    {
                        activeField = ActiveField.None;
                        continue;
                    }

                    bool consumed = true;
                    switch (activeField)
                    {
                        case ActiveField.HeaderNickname:
                            explicitNickname = JoinWrappedText(explicitNickname, fullRow);
                            break;
                        case ActiveField.HeaderAccount:
                            if (WechatIdContinuationRegex().IsMatch(fullRow))
                            {
                                account = $"{account}{fullRow}";
                            }
                            else
                            {
                                consumed = false;
                            }
                            break;
                        case ActiveField.HeaderRegion:
                            region = JoinWrappedText(region, fullRow);
                            break;
                        case ActiveField.TableRemark:
                            if (!PlaceholderRemarks.Contains(fullRow))
                            {
                                remarkFromTable = JoinWrappedText(remarkFromTable, fullRow);
                            }
                            break;
                        case ActiveField.TableTags:
                            if (!fullRow.Contains("添加标签", StringComparison.Ordinal))
                            {
                                tags = JoinWrappedText(tags, fullRow);
                            }
                            break;
                        case ActiveField.TablePhone:
                            phone = JoinWrappedPhone(phone, fullRow);
                            break;
                        case ActiveField.TableDescription:
                            if (!PlaceholderDescriptions.Contains(fullRow))
                            {
                                description = JoinWrappedText(description, fullRow);
                            }
                            break;
                        case ActiveField.TableSignature:
                            signature = JoinWrappedText(signature, fullRow);
                            break;
                        case ActiveField.TableSource:
                            source = JoinWrappedText(source, fullRow);
                            break;
                        case ActiveField.TableAddedDate:
                            addedDate = JoinWrappedText(addedDate, fullRow);
                            break;
                        default:
                            consumed = false;
                            break;
                    }

                    if (consumed)
                    {
                        activePrevBottom = rowBottom;
                        activePrevHeight = rowHeight;
                        if (activeValueX < 0)
                        {
                            activeValueX = rowFirstX;
                        }
                    }
                    else
                    {
                        activeField = ActiveField.None;
                    }
                }
            }

            string wechatRemark;
            string wechatName;

            if (!string.IsNullOrEmpty(explicitNickname))
            {
                // When WeChat shows an explicit "昵称:" row, the bold text above it is merely a duplicate display
                // of the remark (which may wrap across multiple lines). Ignore topHeadline completely and take
                // the clean remark solely from the "备注" row in 朋友资料!
                wechatName = explicitNickname;
                wechatRemark = remarkFromTable;
            }
            else if (!string.IsNullOrEmpty(remarkFromTable))
            {
                // Friend has a real remark in 朋友资料, but "昵称:" row wasn't recognized (e.g. emoji-only nickname).
                // Do not copy the top bold remark text into nickname.
                wechatRemark = remarkFromTable;
                wechatName = string.Empty;
            }
            else
            {
                // No WeChat remark set: collect ALL contiguous wrapped headline rows directly above "微信号:" / "地区:"
                string topHeadline = string.Empty;
                int anchorIdx = accountRowIndex >= 0 ? accountRowIndex : regionRowIndex;
                if (anchorIdx > 0)
                {
                    double anchorX = rows[anchorIdx].Count > 0 ? rows[anchorIdx][0].Bounds.X : -1;
                    double nextTop = rows[anchorIdx].Min(x => x.Bounds.Y);
                    var headlineParts = new List<string>();

                    for (int j = anchorIdx - 1; j >= 0; j--)
                    {
                        double curBottom = rows[j].Max(x => x.Bounds.Y + x.Bounds.Height);
                        double curH = Math.Max(14.0, rows[j].Average(x => x.Bounds.Height));
                        if (nextTop - curBottom > Math.Max(24.0, curH * 1.4))
                        {
                            break;
                        }

                        var alignedTokens = anchorX >= 0
                            ? rows[j].Where(t => Math.Abs(t.Bounds.X - anchorX) < 90).ToList()
                            : rows[j];
                        string candidate = CleanHeadlineRow(alignedTokens);
                        if (string.IsNullOrEmpty(candidate) || candidate.Length > 48 || KnownSectionLabels.Contains(candidate))
                        {
                            if (headlineParts.Count > 0) break;
                            continue;
                        }

                        headlineParts.Add(candidate);
                        nextTop = rows[j].Min(x => x.Bounds.Y);
                        if (headlineParts.Count >= 3) break;
                    }

                    headlineParts.Reverse();
                    foreach (var part in headlineParts)
                    {
                        topHeadline = JoinWrappedText(topHeadline, part);
                    }
                }
                wechatRemark = string.Empty;
                wechatName = topHeadline;
            }

            return (wechatRemark, wechatName, account, tags, region, phone, description, signature, source, addedDate);
        }

        private static string JoinWrappedText(string first, string next)
        {
            first = first.Trim();
            next = next.Trim();
            if (string.IsNullOrEmpty(first)) return next;
            if (string.IsNullOrEmpty(next)) return first;
            // WeChat 4.x uses character-level WrapAnywhere (e.g. "Alex D'Ada" + "mo)", "诺沃" + "克"),
            // so direct concatenation reconstructs split Chinese & English tokens without injecting false spaces.
            return $"{first}{next}";
        }

        private static string JoinWrappedPhone(string first, string next)
        {
            first = first.Trim();
            next = next.Trim();
            if (string.IsNullOrEmpty(first)) return next;
            if (string.IsNullOrEmpty(next)) return first;
            if (first.Length >= 7 && (char.IsAsciiDigit(next[0]) || next[0] == '+'))
            {
                return $"{first} / {next}";
            }
            return $"{first}{next}";
        }

        private static string CleanHeadlineRow(List<OcrLineResult> row)
        {
            var parts = row
                .Select(x => x.Text.Trim())
                .Where(t => !string.IsNullOrEmpty(t) && !t.All(ch => ch is '.' or '·' or '…' or '•' or '▲' or '△' or '●' or '○'))
                .ToList();

            string joined = string.Join(" ", parts).Trim();
            return joined.TrimEnd('·', '.', ' ', '…', '•', '▲', '△', '●', '○', '♂', '♀');
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

        [GeneratedRegex(@"微信号\s*(?:[:：]\s*|\s+)([A-Za-z0-9_\-]+)")]
        private static partial Regex WechatIdRegex();

        [GeneratedRegex(@"^[A-Za-z0-9_\-]+$")]
        private static partial Regex WechatIdContinuationRegex();

        [GeneratedRegex(@"昵称\s*(?:[:：]\s*|\s+)(.+)")]
        private static partial Regex NicknameRegex();

        [GeneratedRegex(@"地区\s*(?:[:：]\s*|\s+)(.+)")]
        private static partial Regex RegionRegex();

        [GeneratedRegex(@"^(.+?)\s*[\(（]\s*(\d{1,4})\s*[\)）]$")]
        private static partial Regex GroupTitleWithCountRegex();

        [GeneratedRegex(@"群聊名称\s*[:：]\s*(.+)")]
        private static partial Regex GroupNameLabelRegex();
    }
}
