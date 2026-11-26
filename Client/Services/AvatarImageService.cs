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
        /// Composes up to 9 member avatar images into a classic WeChat-style square group collage PNG
        /// (2x2 for 2-4 members, 3x3 for 5-9 members) using WinRT BitmapDecoder/BitmapEncoder.
        /// </summary>
        public static async Task<byte[]?> BuildWeChatGroupCollagePngAsync(IReadOnlyList<byte[]> memberAvatarBytes, int canvasSize = 192)
        {
            if (memberAvatarBytes is null || memberAvatarBytes.Count == 0) return null;
            int count = Math.Min(9, memberAvatarBytes.Count);
            if (count == 1) return memberAvatarBytes[0];

            try
            {
                int gap = Math.Max(3, canvasSize / 48);
                int pad = Math.Max(4, canvasSize / 32);
                int cols = count <= 4 ? 2 : 3;
                int tileSize = Math.Max(16, (canvasSize - pad * 2 - gap * (cols - 1)) / cols);

                // Row layouts matching official WeChat group avatar grid
                int[] rowCounts = count switch
                {
                    2 => [2],
                    3 => [1, 2],
                    4 => [2, 2],
                    5 => [2, 3],
                    6 => [3, 3],
                    7 => [1, 3, 3],
                    8 => [2, 3, 3],
                    _ => [3, 3, 3]
                };

                // Allocate BGRA8 premultiplied canvas filled with WeChat soft neutral gray (#E5E7EB)
                byte[] canvas = new byte[canvasSize * canvasSize * 4];
                for (int i = 0; i < canvas.Length; i += 4)
                {
                    canvas[i] = 0xEB;     // B
                    canvas[i + 1] = 0xE7; // G
                    canvas[i + 2] = 0xE5; // R
                    canvas[i + 3] = 0xFF; // A
                }

                int totalGridH = rowCounts.Length * tileSize + (rowCounts.Length - 1) * gap;
                int startY = (canvasSize - totalGridH) / 2;

                int imgIdx = 0;
                int drawn = 0;
                for (int r = 0; r < rowCounts.Length && imgIdx < count; r++)
                {
                    int inRow = rowCounts[r];
                    int rowW = inRow * tileSize + (inRow - 1) * gap;
                    int startX = (canvasSize - rowW) / 2;
                    int dstY = startY + r * (tileSize + gap);

                    for (int c = 0; c < inRow && imgIdx < count; c++, imgIdx++)
                    {
                        byte[]? tileBgra = await DecodeSquareTileBgraAsync(memberAvatarBytes[imgIdx], tileSize);
                        if (tileBgra is null) continue;

                        int dstX = startX + c * (tileSize + gap);
                        for (int y = 0; y < tileSize; y++)
                        {
                            int cy = dstY + y;
                            if (cy < 0 || cy >= canvasSize) continue;
                            int copyPixels = Math.Min(tileSize, canvasSize - Math.Max(0, dstX));
                            if (copyPixels <= 0) continue;
                            int srcStart = y * tileSize * 4 + (dstX < 0 ? -dstX * 4 : 0);
                            int dstStart = (cy * canvasSize + Math.Max(0, dstX)) * 4;
                            System.Buffer.BlockCopy(tileBgra, srcStart, canvas, dstStart, copyPixels * 4);
                        }
                        drawn++;
                    }
                }

                if (drawn == 0) return null;

                using var outBmp = SoftwareBitmap.CreateCopyFromBuffer(
                    canvas.AsBuffer(),
                    BitmapPixelFormat.Bgra8,
                    canvasSize,
                    canvasSize,
                    BitmapAlphaMode.Premultiplied);

                using var outStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
                encoder.SetSoftwareBitmap(outBmp);
                await encoder.FlushAsync();
                outStream.Seek(0);

                var pngBytes = new byte[outStream.Size];
                await outStream.ReadAsync(pngBytes.AsBuffer(), (uint)pngBytes.Length, InputStreamOptions.None);
                return pngBytes;
            }
            catch
            {
                return null;
            }
        }

        private static async Task<byte[]?> DecodeSquareTileBgraAsync(byte[] rawBytes, int tileSize)
        {
            if (rawBytes is null || rawBytes.Length < 12) return null;
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(rawBytes.AsBuffer());
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                uint srcW = decoder.PixelWidth;
                uint srcH = decoder.PixelHeight;
                if (srcW == 0 || srcH == 0) return null;

                // Scale so the shorter side equals tileSize, then center-crop to exact square
                double scale = Math.Max(tileSize / (double)srcW, tileSize / (double)srcH);
                uint scaledW = Math.Max((uint)tileSize, (uint)Math.Round(srcW * scale));
                uint scaledH = Math.Max((uint)tileSize, (uint)Math.Round(srcH * scale));

                var transform = new BitmapTransform
                {
                    ScaledWidth = scaledW,
                    ScaledHeight = scaledH,
                    InterpolationMode = BitmapInterpolationMode.Fant,
                    Bounds = new BitmapBounds
                    {
                        X = (scaledW - (uint)tileSize) / 2,
                        Y = (scaledH - (uint)tileSize) / 2,
                        Width = (uint)tileSize,
                        Height = (uint)tileSize
                    }
                };

                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = pixelData.DetachPixelData();
                return pixels.Length == tileSize * tileSize * 4 ? pixels : null;
            }
            catch
            {
                return null;
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

            int w = source.PixelWidth;
            int h = source.PixelHeight;

            // Prefer lines starting with "微信号" / "地区:" / "昵称:" / "群昵称:"
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
                               t.StartsWith("群昵称:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("群昵称：", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区:", StringComparison.OrdinalIgnoreCase) ||
                               t.StartsWith("地区：", StringComparison.OrdinalIgnoreCase);
                    });

            bool isShortCardHeadlineAnchor = false;
            if (anchorLine is null)
            {
                // Support short non-friend group member cards (only Avatar + Headline + "朋友圈" + "添加到通讯录")
                bool looksLikeCompactCard = (w <= 560 && h <= 560) ||
                    ocr.Lines.Any(l =>
                    {
                        string t = l.Text.Replace(" ", "").Trim();
                        return t.Contains("添加到通讯录", StringComparison.Ordinal) ||
                               t == "朋友圈" ||
                               t == "发消息";
                    });
                if (looksLikeCompactCard)
                {
                    anchorLine = ocr.Lines
                        .Where(l =>
                            l.Bounds.Y < h * 0.40 &&
                            l.Bounds.X >= w * 0.22 &&
                            !string.IsNullOrWhiteSpace(l.Text))
                        .OrderBy(l => l.Bounds.Y)
                        .FirstOrDefault(l =>
                        {
                            string t = l.Text.Replace(" ", "").Trim();
                            return t is not ("朋友圈" or "添加到通讯录" or "发消息" or "更多" or "..." or "···");
                        });
                    isShortCardHeadlineAnchor = anchorLine is not null;
                }
            }

            if (anchorLine is null)
            {
                return (null, string.Empty);
            }

            // Find contiguous header lines directly above anchor inside the SAME profile card.
            // Walk strictly upward from anchorLine and STOP immediately at any vertical gap > 1.8x line height
            // so full-window screenshots NEVER jump out of the popup card into table rows above!
            double lineH = Math.Max(14.0, anchorLine.Bounds.Height);
            double maxGap = Math.Max(22.0, lineH * 1.85);
            double maxHeaderSpan = Math.Max(96.0, lineH * 4.8);
            var candidatesAbove = ocr.Lines
                .Where(l =>
                    l.Bounds.Y < anchorLine.Bounds.Y - 2 &&
                    l.Bounds.Y >= anchorLine.Bounds.Y - maxHeaderSpan &&
                    Math.Abs(l.Bounds.X - anchorLine.Bounds.X) < 56)
                .OrderByDescending(l => l.Bounds.Y)
                .ToList();

            var headerLinesAbove = new List<OcrLineResult>();
            double currentTopY = anchorLine.Bounds.Y;
            foreach (var cand in candidatesAbove)
            {
                double gap = currentTopY - (cand.Bounds.Y + cand.Bounds.Height);
                if (gap > maxGap)
                {
                    break;
                }
                headerLinesAbove.Add(cand);
                currentTopY = Math.Min(currentTopY, cand.Bounds.Y);
            }
            headerLinesAbove.Reverse();

            var topRef = headerLinesAbove.FirstOrDefault() ?? anchorLine;
            var nickLabelAbove = headerLinesAbove.FirstOrDefault(l =>
                l.Text.TrimStart().StartsWith("昵称", StringComparison.OrdinalIgnoreCase));
            // Gender icon sits at the end of the headline lines strictly above 昵称: (or next to anchorLine on short non-friend cards!)
            var genderCandidates = headerLinesAbove
                .Where(l => nickLabelAbove is null || l.Bounds.Y < nickLabelAbove.Bounds.Y - 2)
                .ToList();
            if (isShortCardHeadlineAnchor || genderCandidates.Count == 0)
            {
                genderCandidates.Add(anchorLine);
            }
            byte[] pixels = CopyBgraPixels(source);

            // 1. Detect gender icon right next to the headline (checks both right-of-box and inside right tail of box,
            //    so even when RapidOCR swallows the gender icon into the headline box, we still detect it accurately)
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
                    : Math.Clamp((int)Math.Round(anchorLine.Bounds.Height * (isShortCardHeadlineAnchor ? 4.1 : 3.8)), 66, 135);

                // Empirically across WeChat 4.x at all DPIs: avatar side ≈ 0.79 * colSpan, left ≈ leftColX + 0.06 * colSpan
                int expectedSide = Math.Clamp((int)Math.Round(colSpan * 0.79), 48, 128);
                int geoLeft = leftLabelLine is not null
                    ? Math.Clamp((int)Math.Round(leftLabelLine.Bounds.X + colSpan * 0.06), 0, Math.Max(0, w - expectedSide))
                    : Math.Clamp((int)Math.Floor(rightColX) - Math.Max(12, (int)Math.Round(colSpan * 0.21)) - expectedSide, 0, Math.Max(0, w - expectedSide));
                int geoTop = Math.Clamp((int)Math.Round(topRef.Bounds.Y) + 2, 0, Math.Max(0, h - expectedSide));

                int searchRight = Math.Clamp((int)Math.Floor(rightColX) - 6, 0, w - 1);
                if (searchRight < 36)
                {
                    return (null, gender);
                }

                int bandTop = Math.Clamp(geoTop - 10, 0, h - 1);
                int bandBottom = Math.Clamp(geoTop + expectedSide + 14, bandTop + 36, h - 1);
                // Strictly clamp searchLeft to card interior so we NEVER touch black popup shadow margins!
                int searchLeft = Math.Max(0, geoLeft - (leftLabelLine is not null ? 6 : 14));
                int minColForeground = Math.Max(10, (int)(expectedSide * 0.24));

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

                    // Use exact pixel-fitted box for normal photo/colorful avatars;
                    // white-background/line-art avatars fall back to the left-label-aligned geometric square above.
                    if (bw >= expectedSide * 0.76 && bh >= expectedSide * 0.76 &&
                        bw <= expectedSide * 1.20 && bh <= expectedSide * 1.20 &&
                        ratio >= 0.78 && ratio <= 1.26)
                    {
                        int inset = Math.Min(bw, bh) >= 40 ? 1 : 0;
                        finalLeft = avatarLeftFound + inset;
                        finalTop = avatarTop + inset;
                        finalW = Math.Max(28, bw - inset * 2);
                        finalH = Math.Max(28, bh - inset * 2);
                    }
                }

                // 1b. Geometric fallback for gender icon: scan the clean header zone strictly to the right of the avatar
                //     and above "昵称:"/"微信号:"/"地区:" (works even if RapidOCR missed or shifted the top headline!)
                if (string.IsNullOrEmpty(gender))
                {
                    int gx1 = Math.Clamp(finalLeft + finalW + 6, 0, w - 1);
                    int gx2 = Math.Clamp(finalLeft + finalW + Math.Clamp((int)Math.Round(colSpan * 2.3), 130, 235), gx1, w - 1);
                    int gy1 = Math.Clamp(finalTop - 6, 0, h - 1);
                    double headerFloorY = !isShortCardHeadlineAnchor
                        ? (nickLabelAbove ?? anchorLine).Bounds.Y - 2
                        : finalTop + finalH * 0.65;
                    int gy2 = Math.Clamp((int)Math.Floor(headerFloorY), gy1, h - 1);
                    if (gx2 > gx1 + 12 && gy2 > gy1 + 8)
                    {
                        gender = DetectGenderIconInRect(pixels, w, h, gx1, gy1, gx2, gy2);
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

            // 1) Scan directly to the right of the headline box (standard case)
            int x1 = Math.Clamp((int)Math.Floor(headline.Bounds.X + headline.Bounds.Width) - 2, 0, w - 1);
            int x2 = Math.Clamp(x1 + Math.Max(42, (int)Math.Ceiling(headline.Bounds.Height * 2.2)), x1, w - 1);
            int y1 = Math.Clamp((int)Math.Floor(headline.Bounds.Y) - 5, 0, h - 1);
            int y2 = Math.Clamp((int)Math.Ceiling(headline.Bounds.Y + headline.Bounds.Height) + 5, y1, h - 1);

            string hit = DetectGenderIconInRect(pixels, w, h, x1, y1, x2, y2);
            if (!string.IsNullOrEmpty(hit)) return hit;

            // 2) Fallback: RapidOCR often stretches the headline bounding box over the gender icon itself!
            //    Scan the right tail inside the headline box (black headline text has near-zero color saturation, so zero false positives).
            int innerX1 = Math.Clamp((int)Math.Floor(headline.Bounds.X), 0, w - 1);
            int innerX2 = Math.Clamp((int)Math.Ceiling(headline.Bounds.X + headline.Bounds.Width) + 4, innerX1, w - 1);
            return DetectGenderIconInRect(pixels, w, h, innerX1, y1, innerX2, y2);
        }

        private static string DetectGenderIconInRect(byte[] pixels, int w, int h, int x1, int y1, int x2, int y2)
        {
            x1 = Math.Clamp(x1, 0, w - 1);
            x2 = Math.Clamp(x2, x1, w - 1);
            y1 = Math.Clamp(y1, 0, h - 1);
            y2 = Math.Clamp(y2, y1, h - 1);

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

                    // WeChat male icon: vivid sky blue (~R:45..110, G:155..205, B:230..255)
                    if (b >= 170 && b - r >= 60 && g >= 95)
                    {
                        blueCount++;
                    }
                    // WeChat female icon: vivid coral pink/red (~R:235..255, G:105..145, B:105..145)
                    else if (r >= 185 && r - g >= 50 && r - b >= 20 && b >= 55)
                    {
                        pinkCount++;
                    }
                }
            }

            if (blueCount >= 10 && blueCount > pinkCount * 2) return "男";
            if (pinkCount >= 10 && pinkCount > blueCount * 2) return "女";
            return string.Empty;
        }
    }
}
