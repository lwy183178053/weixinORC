using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Client.Services
{
    public sealed class PendingChatImage
    {
        public byte[] Bytes { get; init; } = [];
        public BitmapImage Preview { get; init; } = new();
        public string DisplayName { get; init; } = "图片";
    }

    /// <summary>
    /// Lightweight chat image preparation &amp; compression helper:
    /// - Downscales large chat photos/screenshots/video covers (default max edge 960px) and re-encodes as compact JPEG
    /// - Preserves animated GIFs and small transparent PNG stickers without bloating file size
    /// - Always returns the smaller of compressed vs original bytes.
    /// </summary>
    public static class ChatImageHelper
    {
        private const uint DefaultMaxEdge = 960;
        private const float DefaultJpegQuality = 0.72f;
        private const int SkipSmallJpegBytes = 48 * 1024;
        private const int SkipSmallPngStickerBytes = 72 * 1024;

        public static async Task<PendingChatImage?> TryLoadFromFileAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

            string displayName = Path.GetFileName(filePath);
            byte[] raw = await File.ReadAllBytesAsync(filePath);
            if (raw.Length > 0 && IsSupportedFormat(raw))
            {
                byte[] compact = await CompressForStorageAsync(raw, DefaultMaxEdge, DefaultJpegQuality);
                var preview = await BytesToBitmapImageAsync(compact);
                if (preview is not null)
                {
                    return new PendingChatImage
                    {
                        Bytes = compact,
                        Preview = preview,
                        DisplayName = displayName
                    };
                }
            }

            using var bmp = await Task.Run(() => OcrService.LoadImageFileFromPathAsync(filePath));
            return await EncodeSoftwareBitmapAsync(bmp, displayName);
        }

        public static async Task<PendingChatImage?> TryLoadFromClipboardAsync()
        {
            var (bmp, _) = await OcrService.TryLoadImageFromClipboardAsync();
            if (bmp is null) return null;
            using (bmp)
            {
                return await EncodeSoftwareBitmapAsync(bmp, "剪贴板图片");
            }
        }

        /// <summary>
        /// Compresses a raw image byte array into a lightweight JPEG suitable for long-term chat storage.
        /// Animated GIFs and tiny stickers/JPEGs are kept intact; larger images are scaled down and JPEG-compressed.
        /// </summary>
        public static async Task<byte[]> CompressForStorageAsync(
            byte[] rawBytes,
            uint maxEdge = DefaultMaxEdge,
            float jpegQuality = DefaultJpegQuality)
        {
            if (rawBytes is null || rawBytes.Length == 0) return rawBytes ?? [];
            if (IsGif(rawBytes)) return rawBytes;
            if (IsJpeg(rawBytes) && rawBytes.Length <= SkipSmallJpegBytes) return rawBytes;

            try
            {
                using var inputStream = new InMemoryRandomAccessStream();
                await inputStream.WriteAsync(rawBytes.AsBuffer());
                inputStream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(inputStream);
                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;
                if (w == 0 || h == 0) return rawBytes;

                // Keep small stickers (PNG/WebP <= 512px and <= 72KB) crisp with transparency intact
                if (!IsJpeg(rawBytes) && w <= 512 && h <= 512 && rawBytes.Length <= SkipSmallPngStickerBytes)
                {
                    return rawBytes;
                }

                uint targetW = w;
                uint targetH = h;
                if (w > maxEdge || h > maxEdge)
                {
                    double scale = Math.Min((double)maxEdge / w, (double)maxEdge / h);
                    targetW = Math.Max(1, (uint)Math.Round(w * scale));
                    targetH = Math.Max(1, (uint)Math.Round(h * scale));
                }

                // Already compact JPEG within dimension bounds
                if (IsJpeg(rawBytes) && targetW == w && targetH == h && rawBytes.Length <= 85 * 1024)
                {
                    return rawBytes;
                }

                using var decoded = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied);

                // Flatten transparent pixels onto white so PNG stickers/screenshots never turn black in JPEG
                if (!IsJpeg(rawBytes))
                {
                    FlattenAlphaOntoWhite(decoded);
                }

                byte[] compressed = await EncodeAsync(decoded, BitmapEncoder.JpegEncoderId, targetW, targetH, jpegQuality);
                if (compressed.Length > 0 && compressed.Length < rawBytes.Length)
                {
                    return compressed;
                }
                return rawBytes;
            }
            catch
            {
                return rawBytes;
            }
        }

        public static async Task<BitmapImage?> BytesToBitmapImageAsync(byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0) return null;
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(stream);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsGif(byte[] raw) =>
            raw.Length >= 4 && raw[0] == (byte)'G' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'8';

        private static bool IsJpeg(byte[] raw) =>
            raw.Length >= 3 && raw[0] == 0xFF && raw[1] == 0xD8 && raw[2] == 0xFF;

        private static bool IsPng(byte[] raw) =>
            raw.Length >= 8 &&
            raw[0] == 0x89 && raw[1] == 0x50 && raw[2] == 0x4E && raw[3] == 0x47 &&
            raw[4] == 0x0D && raw[5] == 0x0A && raw[6] == 0x1A && raw[7] == 0x0A;

        private static bool IsWebp(byte[] raw) =>
            raw.Length >= 12 &&
            raw[0] == (byte)'R' && raw[1] == (byte)'I' && raw[2] == (byte)'F' && raw[3] == (byte)'F' &&
            raw[8] == (byte)'W' && raw[9] == (byte)'E' && raw[10] == (byte)'B' && raw[11] == (byte)'P';

        private static bool IsSupportedFormat(byte[] raw) =>
            IsPng(raw) || IsJpeg(raw) || IsWebp(raw) || IsGif(raw);

        private static void FlattenAlphaOntoWhite(SoftwareBitmap bgraPremul)
        {
            try
            {
                int byteCount = bgraPremul.PixelWidth * bgraPremul.PixelHeight * 4;
                if (byteCount <= 0 || byteCount > 40 * 1024 * 1024) return;
                byte[] pixels = new byte[byteCount];
                bgraPremul.CopyToBuffer(pixels.AsBuffer());

                bool modified = false;
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte a = pixels[i + 3];
                    if (a == 255) continue;
                    int invA = 255 - a;
                    pixels[i] = (byte)Math.Min(255, pixels[i] + invA);
                    pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] + invA);
                    pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] + invA);
                    pixels[i + 3] = 255;
                    modified = true;
                }
                if (modified)
                {
                    bgraPremul.CopyFromBuffer(pixels.AsBuffer());
                }
            }
            catch
            {
            }
        }

        private static async Task<PendingChatImage?> EncodeSoftwareBitmapAsync(SoftwareBitmap source, string displayName)
        {
            using var bgra = source.BitmapPixelFormat == BitmapPixelFormat.Bgra8 && source.BitmapAlphaMode == BitmapAlphaMode.Premultiplied
                ? SoftwareBitmap.Copy(source)
                : SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            FlattenAlphaOntoWhite(bgra);

            uint w = (uint)bgra.PixelWidth;
            uint h = (uint)bgra.PixelHeight;
            uint targetW = w;
            uint targetH = h;
            if (w > DefaultMaxEdge || h > DefaultMaxEdge)
            {
                double scale = Math.Min((double)DefaultMaxEdge / w, (double)DefaultMaxEdge / h);
                targetW = Math.Max(1, (uint)Math.Round(w * scale));
                targetH = Math.Max(1, (uint)Math.Round(h * scale));
            }

            byte[] encoded = await EncodeAsync(bgra, BitmapEncoder.JpegEncoderId, targetW, targetH, quality: DefaultJpegQuality);
            var preview = await BytesToBitmapImageAsync(encoded);
            if (preview is null) return null;

            return new PendingChatImage
            {
                Bytes = encoded,
                Preview = preview,
                DisplayName = displayName
            };
        }

        public static string DetectMimeType(byte[]? b)
        {
            if (b is null || b.Length < 4) return "image/jpeg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return "image/gif";
            if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
            return "image/jpeg";
        }

        private static async Task<byte[]> EncodeAsync(SoftwareBitmap bgra, Guid encoderId, uint targetW, uint targetH, float? quality)
        {
            using var stream = new InMemoryRandomAccessStream();
            BitmapEncoder encoder;
            if (quality.HasValue)
            {
                var props = new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(quality.Value, Windows.Foundation.PropertyType.Single) }
                };
                encoder = await BitmapEncoder.CreateAsync(encoderId, stream, props);
            }
            else
            {
                encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
            }

            encoder.SetSoftwareBitmap(bgra);
            if (targetW != (uint)bgra.PixelWidth || targetH != (uint)bgra.PixelHeight)
            {
                encoder.BitmapTransform.ScaledWidth = targetW;
                encoder.BitmapTransform.ScaledHeight = targetH;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            await encoder.FlushAsync();

            byte[] bytes = new byte[stream.Size];
            stream.Seek(0);
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
    }
}
