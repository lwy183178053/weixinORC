using System.Runtime.InteropServices.WindowsRuntime;
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
    }
}
