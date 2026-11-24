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

        /// <summary>
        /// 先在原始像素坐标系精确裁剪子图，再按目标尺寸缩放编码为 PNG，
        /// 彻底规避 WinRT BitmapEncoder 同时设置 Bounds 与 ScaledWidth 时先缩放后裁剪的坐标错位问题。
        /// </summary>
        public static async Task<byte[]> CropAndEncodePngAsync(
            SoftwareBitmap source,
            int x,
            int y,
            int width,
            int height,
            int outputSize = 256)
        {
            int srcW = source.PixelWidth;
            int srcH = source.PixelHeight;
            int clampedX = Math.Clamp(x, 0, Math.Max(0, srcW - 1));
            int clampedY = Math.Clamp(y, 0, Math.Max(0, srcH - 1));
            int clampedW = Math.Clamp(width, 1, srcW - clampedX);
            int clampedH = Math.Clamp(height, 1, srcH - clampedY);

            SoftwareBitmap bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 &&
                                  source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? source
                : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            try
            {
                byte[] srcPixels = new byte[srcW * srcH * 4];
                bgra.CopyToBuffer(srcPixels.AsBuffer());

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
            finally
            {
                if (!ReferenceEquals(bgra, source))
                {
                    bgra.Dispose();
                }
            }
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

            // Find the bold display name line directly above the anchor line (same column alignment).
            var headlineLine = ocr.Lines
                .Where(l =>
                    l.Bounds.Y < anchorLine.Bounds.Y - 2 &&
                    l.Bounds.Y >= anchorLine.Bounds.Y - Math.Max(90, anchorLine.Bounds.Height * 5) &&
                    Math.Abs(l.Bounds.X - anchorLine.Bounds.X) < 64)
                .OrderByDescending(l => l.Bounds.Y)
                .FirstOrDefault();

            // Check if there is an explicit "昵称:" row between headline and 微信号; if so, step one line higher for the bold headline.
            if (headlineLine is not null && headlineLine.Text.Contains("昵称", StringComparison.OrdinalIgnoreCase))
            {
                var aboveNickname = ocr.Lines
                    .Where(l =>
                        l.Bounds.Y < headlineLine.Bounds.Y - 2 &&
                        l.Bounds.Y >= headlineLine.Bounds.Y - Math.Max(70, headlineLine.Bounds.Height * 4) &&
                        Math.Abs(l.Bounds.X - headlineLine.Bounds.X) < 64)
                    .OrderByDescending(l => l.Bounds.Y)
                    .FirstOrDefault();
                if (aboveNickname is not null)
                {
                    headlineLine = aboveNickname;
                }
            }

            var topRef = headlineLine ?? anchorLine;
            int w = source.PixelWidth;
            int h = source.PixelHeight;

            SoftwareBitmap bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? source
                : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            try
            {
                byte[] pixels = new byte[w * h * 4];
                bgra.CopyToBuffer(pixels.AsBuffer());

                // 1. Detect gender icon right next to the bold headline
                string gender = DetectGenderIcon(pixels, w, h, headlineLine);

                // 2. Sample card background color directly to the left of the anchor text (works on full-window screenshots too)
                int bgX = Math.Clamp((int)Math.Floor(anchorLine.Bounds.X) - 5, 0, w - 1);
                int bgY = Math.Clamp((int)Math.Round(anchorLine.Bounds.Y + anchorLine.Bounds.Height / 2.0), 0, h - 1);
                int bgIdx = (bgY * w + bgX) * 4;
                byte bgB = pixels[bgIdx];
                byte bgG = pixels[bgIdx + 1];
                byte bgR = pixels[bgIdx + 2];

                int searchRight = Math.Clamp((int)Math.Floor(Math.Min(topRef.Bounds.X, anchorLine.Bounds.X)) - 6, 0, w - 1);
                if (searchRight < 36)
                {
                    return (null, gender);
                }

                // Estimate vertical band of the profile header block
                var regionLine = ocr.Lines.FirstOrDefault(l =>
                    l.Text.TrimStart().StartsWith("地区", StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(l.Bounds.X - anchorLine.Bounds.X) < 64 &&
                    l.Bounds.Y >= anchorLine.Bounds.Y &&
                    l.Bounds.Y <= anchorLine.Bounds.Y + 140);

                int bandTop = Math.Clamp((int)Math.Floor(topRef.Bounds.Y) - 14, 0, h - 1);
                int bandBottom = regionLine is not null
                    ? Math.Clamp((int)Math.Ceiling(regionLine.Bounds.Y + regionLine.Bounds.Height) + 14, bandTop + 40, h - 1)
                    : Math.Clamp((int)Math.Ceiling(anchorLine.Bounds.Y + anchorLine.Bounds.Height * 2.6) + 14, bandTop + 40, h - 1);

                int estHeight = Math.Clamp(bandBottom - bandTop, 44, 200);
                int searchLeft = Math.Max(0, searchRight - (int)(estHeight * 2.1));
                int minColForeground = Math.Max(10, (int)(estHeight * 0.24));

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

                if (avatarRight <= avatarLeft)
                {
                    return (null, gender);
                }

                int boxW = avatarRight - avatarLeft + 1;
                if (boxW < 28 || boxW > 240)
                {
                    return (null, gender);
                }

                // 4. Scan vertically within [avatarLeft..avatarRight] to get exact top & bottom edges
                int vStart = Math.Max(0, bandTop - 16);
                int vEnd = Math.Min(h - 1, bandBottom + 16);
                int minRowForeground = Math.Max(8, (int)(boxW * 0.24));

                int avatarTop = -1;
                int avatarBottom = -1;
                for (int py = vStart; py <= vEnd; py++)
                {
                    int fg = 0;
                    int rowOff = py * w * 4;
                    for (int px = avatarLeft; px <= avatarRight; px++)
                    {
                        int idx = rowOff + px * 4;
                        int diff = Math.Abs(pixels[idx] - bgB) + Math.Abs(pixels[idx + 1] - bgG) + Math.Abs(pixels[idx + 2] - bgR);
                        if (diff > 28) fg++;
                    }

                    if (fg >= minRowForeground)
                    {
                        if (avatarTop < 0) avatarTop = py;
                        avatarBottom = py;
                    }
                    else if (avatarTop >= 0 && (py - avatarBottom) >= 4)
                    {
                        break;
                    }
                }

                if (avatarBottom <= avatarTop)
                {
                    return (null, gender);
                }

                int boxH = avatarBottom - avatarTop + 1;
                double ratio = boxW / (double)boxH;
                if (boxH >= 28 && ratio >= 0.72 && ratio <= 1.38)
                {
                    // Trim 1px outer anti-aliased edge when avatar is large enough
                    int inset = Math.Min(boxW, boxH) >= 40 ? 1 : 0;
                    byte[] png = await CropAndEncodePngAsync(
                        bgra,
                        avatarLeft + inset,
                        avatarTop + inset,
                        boxW - inset * 2,
                        boxH - inset * 2,
                        256);
                    return (png, gender);
                }

                return (null, gender);
            }
            finally
            {
                if (!ReferenceEquals(bgra, source))
                {
                    bgra.Dispose();
                }
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
