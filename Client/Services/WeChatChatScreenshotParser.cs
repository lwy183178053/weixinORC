using System.Text.RegularExpressions;
using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class ParsedChatMessage
    {
        public string Direction { get; set; } = "incoming"; // "incoming" | "outgoing"
        public string SenderName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public double TopY { get; set; }
        public double BottomY { get; set; }
        public double LeftX { get; set; }
        public double RightX { get; set; }

        public bool IsOutgoing => Direction.Equals("outgoing", StringComparison.OrdinalIgnoreCase);
    }

    public static partial class WeChatChatScreenshotParser
    {
        private static readonly HashSet<string> NoiseUiWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "发送", "发送(S)", "按Enter发送", "语音聊天", "视频聊天", "聊天记录", "搜索", "表情", "文件"
        };

        /// <summary>
        /// Parses a WeChat desktop chat screenshot into ordered incoming/outgoing chat bubbles
        /// using RapidOCR bounding boxes + pixel color inspection of each bubble background.
        /// </summary>
        public static List<ParsedChatMessage> Parse(SoftwareBitmap bitmap, OcrRecognitionResult ocr)
        {
            if (ocr.Lines.Count == 0 || bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
            {
                return [];
            }

            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            byte[] pixels = AvatarImageService.CopyBgraPixels(bitmap);

            // Detect optional top header bar (title like "大数据25 刘德康" at y < 48 on far left)
            // and bottom composer area ("发送" button at bottom-right).
            double bottomCutoffY = height;
            foreach (var line in ocr.Lines)
            {
                string t = line.Text.Trim();
                if (t is "发送" or "发送(S)" && line.Bounds.Y > height * 0.65 && line.Bounds.X > width * 0.7)
                {
                    // Composer box is roughly 120-200px above the "发送" button; ignore anything below toolbar
                    bottomCutoffY = Math.Min(bottomCutoffY, line.Bounds.Y - 20);
                }
            }

            var rawBubbles = new List<ParsedChatMessage>();

            foreach (var line in ocr.Lines.OrderBy(l => l.Bounds.Y).ThenBy(l => l.Bounds.X))
            {
                string text = line.Text.Trim();
                if (string.IsNullOrEmpty(text)) continue;
                if (line.Bounds.Y >= bottomCutoffY) continue;
                if (NoiseUiWords.Contains(text)) continue;

                // Skip top chat window title bar (usually within top 48px and left margin < 40px, no bubble behind it)
                if (line.Bounds.Y < 48 && line.Bounds.X < 40 && !IsGreenBubble(pixels, width, height, line.Bounds))
                {
                    continue;
                }

                // Skip centered system notices / timestamps ("撤回了一条消息", "17:20", "昨天 14:20", etc.)
                double centerX = line.Bounds.X + line.Bounds.Width / 2.0;
                bool isHorizontallyCentered = Math.Abs(centerX - width / 2.0) < width * 0.16;
                if (isHorizontallyCentered && IsSystemOrTimeNotice(text))
                {
                    continue;
                }

                // Skip single garbled icon glyphs near bottom or top icons
                if (text.Length == 1 && !char.IsLetterOrDigit(text[0]) && !IsCjk(text[0]))
                {
                    continue;
                }

                bool isGreen = IsGreenBubble(pixels, width, height, line.Bounds);
                string direction;

                if (isGreen)
                {
                    direction = "outgoing";
                }
                else
                {
                    // Fallback to horizontal geometry when theme color varies
                    double leftMargin = line.Bounds.X;
                    double rightMargin = width - (line.Bounds.X + line.Bounds.Width);

                    if (isHorizontallyCentered && !HasLightBubbleBehind(pixels, width, height, line.Bounds))
                    {
                        continue;
                    }

                    direction = rightMargin < leftMargin && rightMargin < width * 0.25
                        ? "outgoing"
                        : "incoming";
                }

                // Merge wrapped multi-line text inside the same bubble
                if (rawBubbles.Count > 0)
                {
                    var prev = rawBubbles[^1];
                    double verticalGap = line.Bounds.Y - prev.BottomY;
                    bool sameSide = prev.Direction == direction;
                    bool aligned = direction == "incoming"
                        ? Math.Abs(line.Bounds.X - prev.LeftX) < 36
                        : Math.Abs((line.Bounds.X + line.Bounds.Width) - prev.RightX) < 42;

                    // Tight line spacing inside a single bubble (< 16px) vs separate bubbles (> 24px)
                    if (sameSide && aligned && verticalGap >= -4 && verticalGap <= Math.Max(16, line.Bounds.Height * 0.6))
                    {
                        rawBubbles[^1] = new ParsedChatMessage
                        {
                            Direction = prev.Direction,
                            SenderName = prev.SenderName,
                            Content = $"{prev.Content}\n{text}",
                            TopY = prev.TopY,
                            BottomY = Math.Max(prev.BottomY, line.Bounds.Y + line.Bounds.Height),
                            LeftX = Math.Min(prev.LeftX, line.Bounds.X),
                            RightX = Math.Max(prev.RightX, line.Bounds.X + line.Bounds.Width)
                        };
                        continue;
                    }
                }

                rawBubbles.Add(new ParsedChatMessage
                {
                    Direction = direction,
                    Content = text,
                    TopY = line.Bounds.Y,
                    BottomY = line.Bounds.Y + line.Bounds.Height,
                    LeftX = line.Bounds.X,
                    RightX = line.Bounds.X + line.Bounds.Width
                });
            }

            return rawBubbles;
        }

        private static bool IsGreenBubble(byte[] bgra, int w, int h, PixelRect rect)
        {
            // Sample a small margin ring just outside & inside the text bounding box where the bubble fill sits.
            int greenHits = 0;
            int samples = 0;

            int x1 = Math.Clamp((int)(rect.X - 8), 0, w - 1);
            int x2 = Math.Clamp((int)(rect.X + rect.Width + 8), 0, w - 1);
            int y1 = Math.Clamp((int)(rect.Y - 6), 0, h - 1);
            int y2 = Math.Clamp((int)(rect.Y + rect.Height + 6), 0, h - 1);

            int stepX = Math.Max(2, (x2 - x1) / 14);
            int stepY = Math.Max(2, (y2 - y1) / 6);

            for (int y = y1; y <= y2; y += stepY)
            {
                for (int x = x1; x <= x2; x += stepX)
                {
                    int idx = (y * w + x) * 4;
                    byte b = bgra[idx];
                    byte g = bgra[idx + 1];
                    byte r = bgra[idx + 2];
                    samples++;

                    // WeChat green bubble (#95EC69 / #89D961 / #A9EA7A): clearly green-dominant & bright
                    if (g >= 170 && g - r >= 25 && g - b >= 25)
                    {
                        greenHits++;
                    }
                }
            }

            return samples > 0 && (double)greenHits / samples >= 0.18;
        }

        private static bool HasLightBubbleBehind(byte[] bgra, int w, int h, PixelRect rect)
        {
            int x = Math.Clamp((int)(rect.X - 6), 0, w - 1);
            int y = Math.Clamp((int)(rect.Y + rect.Height / 2), 0, h - 1);
            int idx = (y * w + x) * 4;
            byte b = bgra[idx];
            byte g = bgra[idx + 1];
            byte r = bgra[idx + 2];
            return r >= 225 && g >= 225 && b >= 225;
        }

        private static bool IsSystemOrTimeNotice(string text)
        {
            if (text.Contains("撤回了一条消息", StringComparison.Ordinal) ||
                text.Contains("拍了拍", StringComparison.Ordinal) ||
                text.Contains("以上是打招呼的内容", StringComparison.Ordinal) ||
                text.Contains("你们已经是好友", StringComparison.Ordinal) ||
                text.Contains("加入群聊", StringComparison.Ordinal) ||
                text.Contains("修改群名为", StringComparison.Ordinal))
            {
                return true;
            }

            return TimeHeaderRegex().IsMatch(text);
        }

        private static bool IsCjk(char c) => c >= '\u4e00' && c <= '\u9fff';

        [GeneratedRegex(@"^(?:昨天|前天|星期[一二三四五六日天]|\d{4}[年/\-]\d{1,2}[月/\-]\d{1,2}日?|\d{1,2}月\d{1,2}日)?\s*(?:上午|下午|凌晨|晚上)?\s*\d{1,2}:\d{2}$")]
        private static partial Regex TimeHeaderRegex();
    }
}
