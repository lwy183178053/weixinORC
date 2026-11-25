using System.Runtime.InteropServices.WindowsRuntime;
using Client.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Client.Services
{
    public static class AvatarImageService
    {
        public static async Task<BitmapImage?> BytesToBitmapImageAsync(byte[]? bytes)
        {
            if (bytes is null || bytes.Length == 0) return null;
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var img = new BitmapImage();
            await img.SetSourceAsync(stream);
            return img;
        }

        public static async Task<byte[]> EncodeSquarePngAsync(SoftwareBitmap source, int outputSize = 256)
        {
            int side = Math.Min(source.PixelWidth, source.PixelHeight);
            int x = (source.PixelWidth - side) / 2;
            int y = (source.PixelHeight - side) / 2;
            return await CropAndEncodePngAsync(source, x, y, side, side, outputSize);
        }

        public static byte[] CopyBgraPixels(SoftwareBitmap source)
        {
            SoftwareBitmap bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                                  source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? source
                : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            try
            {
                byte[] pixels = new byte[checked(bgra.PixelWidth * bgra.PixelHeight * 4)];
                bgra.CopyToBuffer(pixels.AsBuffer());
                return pixels;
            }
            finally
            {
                if (!ReferenceEquals(bgra, source))
                {
                    bgra.Dispose();
                }
            }
        }

        /// <summary>
        /// 先在原始像素坐标系精确裁剪子图，再按目标尺寸缩放编码为 PNG，
        /// 彻底规避 WinRT BitmapEncoder 同时设置 Bounds 与 ScaledWidth 时先缩放后裁剪的坐标错位问题。
        /// </summary>
        public static Task<byte[]> CropAndEncodePngAsync(
            SoftwareBitmap source,
            int x,
            int y,
            int width,
            int height,
            int outputSize = 256)
        {
            byte[] srcPixels = CopyBgraPixels(source);
            return CropAndEncodePngAsync(srcPixels, source.PixelWidth, source.PixelHeight, x, y, width, height, outputSize);
        }

        public static async Task<byte[]> CropAndEncodePngAsync(
            byte[] srcPixels,
            int srcW,
            int srcH,
            int x,
            int y,
            int width,
            int height,
            int outputSize = 256)
        {
            int clampedX = Math.Clamp(x, 0, Math.Max(0, srcW - 1));
            int clampedY = Math.Clamp(y, 0, Math.Max(0, srcH - 1));
            int clampedW = Math.Clamp(width, 1, srcW - clampedX);
            int clampedH = Math.Clamp(height, 1, srcH - clampedY);

            // Exact 1:1 pixel slice in source coordinates before any scaling
            byte[] croppedPixels = new byte[clampedW * clampedH * 4];
            int rowBytes = clampedW * 4;
            for (int row = 0; row < clampedH; row++)
            {
                int srcOffset = ((clampedY + row) * srcW + clampedX) * 4;
                int dstOffset = row * rowBytes;
                System.Buffer.BlockCopy(srcPixels, srcOffset, croppedPixels, dstOffset, rowBytes);
            }

            using var croppedBmp = SoftwareBitmap.CreateCopyFromBuffer(
                croppedPixels.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                clampedW,
                clampedH,
                BitmapAlphaMode.Premultiplied);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(croppedBmp);

            int targetW = outputSize;
            int targetH = outputSize;
            if (clampedW != clampedH)
            {
                if (clampedW > clampedH)
                {
                    targetW = outputSize;
                    targetH = Math.Max(1, (int)Math.Round(outputSize * (clampedH / (double)clampedW)));
                }
                else
                {
                    targetH = outputSize;
                    targetW = Math.Max(1, (int)Math.Round(outputSize * (clampedW / (double)clampedH)));
                }
            }

            encoder.BitmapTransform.ScaledWidth = (uint)targetW;
            encoder.BitmapTransform.ScaledHeight = (uint)targetH;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;

            await encoder.FlushAsync();
            stream.Seek(0);

            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }

        /// <summary>
        /// 从微信资料卡截图（支持单卡片截图与整窗截图）中，结合 OCR 锚点与从右向左投影扫描，
        /// 精准裁剪左侧方形头像并识别昵称旁的蓝色男/粉色女图标。
        /// </summary>
        public static async Task<(byte[]? AvatarPng, string DetectedGender)> TryExtractProfileAvatarAndGenderAsync(
            SoftwareBitmap source,
            OcrRecognitionResult ocr)
        {
            if (ocr.Lines.Count == 0 || source.PixelWidth < 80 || source.PixelHeight < 80)
            {
                return (null, string.Empty);
            }

            // Prefer lines starting with "微信号" / "地区:" / "昵称:" (avoid false matches like "通过搜索微信号添加" or table header "昵称↑")
            var anchorLine = ocr.Lines
                .OrderBy(l => l.Bounds.Y)
                .FirstOrDefault(l => l.Text.TrimStart().StartsWith("微信号", StringComparison.OrdinalIgnoreCase))
                ?? ocr.Lines
                    .OrderBy(l => l.Bounds.Y)
                    .FirstOrDefault(l =>
                    {
                        string t = l.Text.TrimStart();
                        return t.StartsWith("昵称:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("昵称：", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区：", StringComparison.OrdinalIgnoreCase);
                    });

            if (anchorLine is null)
            {
                return (null, string.Empty);
            }

            // Find ALL aligned header lines directly above anchor (handles multi-line wrapped remark AND multi-line wrapped 昵称:)
            var headerLinesAbove = ocr.Lines
                .Where(l =>
                    l.Bounds.Y < anchorLine.Bounds.Y - 2 &&
                    l.Bounds.Y >= anchorLine.Bounds.Y - Math.Max(160, anchorLine.Bounds.Height * 8) &&
                    Math.Abs(l.Bounds.X - anchorLine.Bounds.X) < 64)
                .OrderBy(l => l.Bounds.Y)
                .ToList();

            var topRef = headerLinesAbove.FirstOrDefault() ?? anchorLine;
            var nickLabelAbove = headerLinesAbove.FirstOrDefault(l =>
                l.Text.TrimStart().StartsWith("昵称", StringComparison.OrdinalIgnoreCase));
            // Gender icon sits at the end of the headline lines strictly above 昵称: (or above anchor if no 昵称: row)
            var genderCandidates = headerLinesAbove
                .Where(l => nickLabelAbove is null || l.Bounds.Y < nickLabelAbove.Bounds.Y - 2)
                .ToList();
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            byte[] pixels = CopyBgraPixels(source);

            // 1. Detect gender icon right next to the headline (checks all wrapped headline lines)
            string gender = string.Empty;
            foreach (var cand in genderCandidates)
            {
                gender = DetectGenderIcon(pixels, w, h, cand);
                if (!string.IsNullOrEmpty(gender)) break;
            }

            // 2. Sample card background color directly to the left of the anchor text (works on full-window screenshots too)
            int bgX = Math.Clamp((int)Math.Floor(anchorLine.Bounds.X) - 5, 0, w - 1);
            int bgY = Math.Clamp((int)Math.Round(anchorLine.Bounds.Y + anchorLine.Bounds.Height / 2.0), 0, h - 1);
            int bgIdx = (bgY * w + bgX) * 4;
            byte bgB = pixels[bgIdx];
            byte bgG = pixels[bgIdx + 1];
            byte bgR = pixels[bgIdx + 2];

                // Find the left-column label anchor ("朋友资料" / "备注" / "更多信息" / "标签") to lock exact card left margin
                // and compute DPI-exact avatar square size (completely immune to 2-3 line wrapped remarks AND black popup shadow edges!)
                double rightColX = Math.Min(topRef.Bounds.X, anchorLine.Bounds.X);
                var leftLabelLine = ocr.Lines
                    .Where(l =>
                        l.Bounds.Y > anchorLine.Bounds.Y &&
                        l.Bounds.X < rightColX - 35 &&
                        l.Bounds.X >= rightColX - 180)
                    .FirstOrDefault(l =>
                    {
                        string t = l.Text.Trim().TrimEnd(':', '：');
                        return t is "朋友资料" or "备注" or "备注名" or "标签" or "更多信息" or "朋友圈" or "来源";
                    });

                int colSpan = leftLabelLine is not null
                    ? Math.Clamp((int)Math.Round(rightColX - leftLabelLine.Bounds.X), 55, 160)
                    : Math.Clamp((int)Math.Round(anchorLine.Bounds.Height * 3.5), 60, 130);

                // Empirically across WeChat 3.9 / 4.x at all DPIs: avatar side ≈ 0.78 * colSpan, left ≈ leftColX + 0.05 * colSpan
                int expectedSide = Math.Clamp((int)Math.Round(colSpan * 0.78), 42, 136);
                int geoLeft = leftLabelLine is not null
                    ? Math.Clamp((int)Math.Round(leftLabelLine.Bounds.X + colSpan * 0.05), 0, Math.Max(0, w - expectedSide))
                    : Math.Clamp((int)Math.Floor(rightColX) - Math.Max(10, (int)Math.Round(colSpan * 0.22)) - expectedSide, 0, Math.Max(0, w - expectedSide));
                int geoTop = Math.Clamp((int)Math.Round(topRef.Bounds.Y) + 1, 0, Math.Max(0, h - expectedSide));

                int searchRight = Math.Clamp((int)Math.Floor(rightColX) - 5, 0, w - 1);
                if (searchRight < 32)
                {
                    return (null, gender);
                }

                int bandTop = Math.Clamp(geoTop - 12, 0, h - 1);
                int bandBottom = Math.Clamp(geoTop + expectedSide + 18, bandTop + 34, h - 1);
                // Strictly clamp searchLeft to card interior so we NEVER touch black popup shadow margins!
                int searchLeft = Math.Max(0, geoLeft - 10);
                int minColForeground = Math.Max(8, (int)(expectedSide * 0.20));

                // 3. Scan strictly right-to-left to isolate ONLY the avatar directly left of the text block
                int avatarRight = -1;
                int avatarLeft = -1;
                int consecutiveFg = 0;
                int consecutiveBg = 0;

                for (int px = searchRight; px >= searchLeft; px--)
                {
                    int fg = 0;
                    for (int py = bandTop; py <= bandBottom; py++)
                    {
                        int idx = (py * w + px) * 4;
                        int diff = Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR);
                        if (diff > 28) fg++;
                    }

                    bool isFg = fg >= minColForeground;
                    if (avatarRight < 0)
                    {
                        if (isFg)
                        {
                            consecutiveFg++;
                            if (consecutiveFg >= 3)
                            {
                                avatarRight = px + consecutiveFg - 1;
                                avatarLeft = px;
                                consecutiveBg = 0;
                            }
                        }
                        else
                        {
                            consecutiveFg = 0;
                        }
                    }
                    else
                    {
                        if (isFg)
                        {
                            avatarLeft = px;
                            consecutiveBg = 0;
                        }
                        else
                        {
                            consecutiveBg++;
                            if (consecutiveBg >= 4)
                            {
                                break;
                            }
                        }
                    }
                }

                // 4. Scan vertically within [avatarLeft..avatarRight] and pick the real avatar vertical run (ignore thin card border shadows)
                int avatarLeftFound = avatarLeft;
                int avatarRightFound = avatarRight;
                int avatarTop = -1;
                int avatarBottom = -1;

                if (avatarRightFound > avatarLeftFound)
                {
                    int boxW = avatarRightFound - avatarLeftFound + 1;
                    if (boxW >= 24 && boxW <= 240)
                    {
                        int vStart = Math.Max(0, bandTop - 12);
                        int vEnd = Math.Min(h - 1, bandBottom + 16);
                        int minRowForeground = Math.Max(6, (int)(boxW * 0.18));

                        var vRuns = new List<(int Top, int Bottom)>();
                        int curTop = -1;
                        int curBottom = -1;
                        for (int py = vStart; py <= vEnd; py++)
                        {
                            int fg = 0;
                            int rowOff = py * w * 4;
                            for (int px = avatarLeftFound; px <= avatarRightFound; px++)
                            {
                                int idx = rowOff + px * 4;
                                int diff = Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR);
                                if (diff > 28) fg++;
                            }

                            if (fg >= minRowForeground)
                            {
                                if (curTop < 0) curTop = py;
                                curBottom = py;
                            }
                            else if (curTop >= 0 && (py - curBottom) >= 4)
                            {
                                vRuns.Add((curTop, curBottom));
                                curTop = -1;
                                curBottom = -1;
                            }
                        }
                        if (curTop >= 0)
                        {
                            vRuns.Add((curTop, curBottom));
                        }

                        var bestRun = vRuns
                            .OrderByDescending(r => r.Bottom - r.Top + 1)
                            .FirstOrDefault();

                        if (bestRun.Bottom > bestRun.Top)
                        {
                            avatarTop = bestRun.Top;
                            avatarBottom = bestRun.Bottom;
                        }
                    }
                }

                int finalLeft = geoLeft;
                int finalTop = geoTop;
                int finalW = Math.Min(expectedSide, w - finalLeft);
                int finalH = Math.Min(expectedSide, h - finalTop);

                if (avatarRightFound > avatarLeftFound && avatarBottom > avatarTop)
                {
                    int bw = avatarRightFound - avatarLeftFound + 1;
                    int bh = avatarBottom - avatarTop + 1;
                    double ratio = bw / (double)bh;

                    // Accept broader DPI/WeChat-version box ratios (0.64..1.35 * expectedSide)
                    if (bw >= expectedSide * 0.64 && bh >= expectedSide * 0.64 &&
                        bw <= expectedSide * 1.35 && bh <= expectedSide * 1.35 &&
                        ratio >= 0.72 && ratio <= 1.38)
                    {
                        int inset = Math.Min(bw, bh) >= 44 ? 1 : 0;
                        int cx = (avatarLeftFound + avatarRightFound) / 2;
                        int cy = (avatarTop + avatarBottom) / 2;
                        // Square-pad using the longer side when one edge of the avatar blended into the white card bg
                        int side = Math.Max(bw, bh) - inset * 2;
                        side = Math.Clamp(side, 28, Math.Min(w, h));
                        finalLeft = Math.Clamp(cx - side / 2, 0, Math.Max(0, w - side));
                        finalTop = Math.Clamp(cy - side / 2, 0, Math.Max(0, h - side));
                        finalW = side;
                        finalH = side;
                    }
                }

                if (finalW >= 28 && finalH >= 28)
                {
                    // Avoid aggressive over-upscaling that softens small 100%-DPI avatars: cap output cleanly at 192px (or native if larger)
                    int outSize = Math.Clamp(Math.Max(finalW, finalH) * 2, 128, 256);
                    byte[] png = await CropAndEncodePngAsync(pixels, w, h, finalLeft, finalTop, finalW, finalH, outSize);
                    return (png, gender);
                }

                return (null, gender);
        }

        /// <summary>
        /// Checks whether an avatar region is nearly uniform/blank (e.g. WeChat still loading the avatar placeholder).
        /// </summary>
        public static bool IsLikelyPlaceholderAvatar(SoftwareBitmap cardBmp, OcrRecognitionResult cardOcr)
        {
            try
            {
                if (cardBmp.PixelWidth < 80 || cardBmp.PixelHeight < 80) return true;
                var anchor = cardOcr.Lines
                    .OrderBy(l => l.Bounds.Y)
                    .FirstOrDefault(l => l.Text.TrimStart().StartsWith("微信号", StringComparison.OrdinalIgnoreCase) ||
                                         l.Text.TrimStart().StartsWith("昵称", StringComparison.OrdinalIgnoreCase));
                if (anchor is null) return false;

                int w = cardBmp.PixelWidth;
                int h = cardBmp.PixelHeight;
                byte[] pixels = CopyBgraPixels(cardBmp);

                int x2 = Math.Clamp((int)anchor.Bounds.X - 10, 12, w - 1);
                int x1 = Math.Clamp(x2 - 54, 0, x2 - 1);
                int y1 = Math.Clamp((int)anchor.Bounds.Y - 48, 0, h - 1);
                int y2 = Math.Clamp(y1 + 54, y1 + 1, h - 1);

                long sum = 0;
                long sumSq = 0;
                int count = 0;
                for (int y = y1; y <= y2; y += 2)
                {
                    int rowOff = y * w * 4;
                    for (int x = x1; x <= x2; x += 2)
                    {
                        int idx = rowOff + x * 4;
                        int lum = (pixels[idx] + pixels[idx + 1] + pixels[idx + 2]) / 3;
                        sum += lum;
                        sumSq += lum * lum;
                        count++;
                    }
                }
                if (count < 20) return false;
                double mean = sum / (double)count;
                double variance = (sumSq / (double)count) - (mean * mean);
                // Very low luminance variance (< 25) means solid white/gray placeholder box
                return variance < 25.0;
            }
            catch
            {
                return false;
            }
        }

        private static string DetectGenderIcon(byte[] pixels, int w, int h, OcrLineResult? headline)
        {
            if (headline is null) return string.Empty;

            int x1 = Math.Clamp((int)Math.Floor(headline.Bounds.X + headline.Bounds.Width) + 2, 0, w - 1);
            int x2 = Math.Clamp(x1 + Math.Max(34, (int)Math.Ceiling(headline.Bounds.Height * 1.8)), x1, w - 1);
            int y1 = Math.Clamp((int)Math.Floor(headline.Bounds.Y) - 4, 0, h - 1);
            int y2 = Math.Clamp((int)Math.Ceiling(headline.Bounds.Y + headline.Bounds.Height) + 4, y1, h - 1);

            int blueCount = 0;
            int pinkCount = 0;

            for (int py = y1; py <= y2; py++)
            {
                int rowOff = py * w * 4;
                for (int px = x1; px <= x2; px++)
                {
                    int idx = rowOff + px * 4;
                    byte b = pixels[idx];
                    byte g = pixels[idx + 1];
                    byte r = pixels[idx + 2];

                    if (b >= 175 && b - r >= 65 && g >= 100)
                    {
                        blueCount++;
                    }
                    else if (r >= 190 && r - g >= 55 && r - b >= 20 && b >= 70)
                    {
                        pinkCount++;
                    }
                }
            }

            if (blueCount >= 12 && blueCount > pinkCount * 2) return "男";
            if (pinkCount >= 12 && pinkCount > blueCount * 2) return "女";
            return string.Empty;
        }
    }
}
