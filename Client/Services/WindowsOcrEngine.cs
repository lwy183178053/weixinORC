using System.Diagnostics;
using System.Text.RegularExpressions;
using Client.Models;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Client.Services
{
    public sealed partial class WindowsOcrEngine : IOcrEngine
    {
        private readonly OcrEngine? _engine;
        private readonly string _languageTag;

        public WindowsOcrEngine()
        {
            var available = OcrEngine.AvailableRecognizerLanguages;
            Language? chosen =
                available.FirstOrDefault(l => l.LanguageTag.Equals("zh-Hans-CN", StringComparison.OrdinalIgnoreCase)) ??
                available.FirstOrDefault(l => l.LanguageTag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase)) ??
                available.FirstOrDefault(l => l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) ??
                available.FirstOrDefault();

            if (chosen is not null)
            {
                _engine = OcrEngine.TryCreateFromLanguage(chosen);
                _languageTag = chosen.LanguageTag;
            }
            else
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
                _languageTag = _engine?.RecognizerLanguage.LanguageTag ?? string.Empty;
            }
        }

        public string Id => "windows-native-ocr";

        public string DisplayName => string.IsNullOrEmpty(_languageTag)
            ? "Windows 原生 OCR（未检测到语言包）"
            : $"Windows 原生 OCR ({_languageTag})";

        public string Description =>
            "系统内置离线 OCR，零外部依赖、启动快；原型验证即开即用，自动 2x 放大以提升微信小字识别率。";

        public bool IsAvailable => _engine is not null;

        public string AvailabilityNote => _engine is not null
            ? $"可用 · 识别语言: {_languageTag} · 最大边长: {OcrEngine.MaxImageDimension}px"
            : "不可用 · 当前系统未安装 Windows OCR 语言包（需安装 zh-CN OCR 功能包）。";

        public async Task<OcrRecognitionResult> RecognizeAsync(
            SoftwareBitmap bitmap,
            CancellationToken cancellationToken = default)
        {
            if (_engine is null)
            {
                throw new InvalidOperationException(AvailabilityNote);
            }

            var sw = Stopwatch.StartNew();
            int origWidth = bitmap.PixelWidth;
            int origHeight = bitmap.PixelHeight;

            var (preparedBitmap, scale, ownsPrepared) = await PrepareBitmapForOcrAsync(bitmap, cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawResult = await _engine.RecognizeAsync(preparedBitmap).AsTask(cancellationToken).ConfigureAwait(false);
                sw.Stop();

                var lines = new List<OcrLineResult>(rawResult.Lines.Count);
                int idx = 1;
                foreach (var line in rawResult.Lines)
                {
                    if (line.Words.Count == 0)
                    {
                        continue;
                    }

                    double minX = line.Words.Min(w => w.BoundingRect.X) / scale;
                    double minY = line.Words.Min(w => w.BoundingRect.Y) / scale;
                    double maxX = line.Words.Max(w => w.BoundingRect.X + w.BoundingRect.Width) / scale;
                    double maxY = line.Words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) / scale;

                    string cleanText = NormalizeCjkSpacing(line.Text);
                    lines.Add(new OcrLineResult
                    {
                        Index = idx++,
                        Text = cleanText,
                        Bounds = new PixelRect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY))
                    });
                }

                return new OcrRecognitionResult
                {
                    EngineId = Id,
                    EngineName = DisplayName,
                    LanguageTag = _languageTag,
                    ImageWidth = origWidth,
                    ImageHeight = origHeight,
                    ElapsedMilliseconds = sw.ElapsedMilliseconds,
                    Lines = lines,
                    FullText = string.Join(Environment.NewLine, lines.Select(l => l.Text))
                };
            }
            finally
            {
                if (ownsPrepared)
                {
                    preparedBitmap.Dispose();
                }
            }
        }

        private static async Task<(SoftwareBitmap Bitmap, double Scale, bool OwnsBitmap)> PrepareBitmapForOcrAsync(
            SoftwareBitmap source,
            CancellationToken cancellationToken)
        {
            uint maxDim = OcrEngine.MaxImageDimension;
            double scale = 1.0;

            // Upscale smaller UI captures up to 2x to noticeably improve small font recognition in WeChat.
            int longestEdge = Math.Max(source.PixelWidth, source.PixelHeight);
            if (longestEdge > 0 && longestEdge < 1600)
            {
                scale = Math.Min(2.0, (double)maxDim / longestEdge);
            }
            else if (longestEdge > maxDim)
            {
                scale = (double)maxDim / longestEdge;
            }

            bool needsFormatConvert = source.BitmapPixelFormat != BitmapPixelFormat.Bgra8 ||
                                      source.BitmapAlphaMode != BitmapAlphaMode.Premultiplied;

            if (Math.Abs(scale - 1.0) < 0.05)
            {
                if (!needsFormatConvert)
                {
                    return (source, 1.0, false);
                }

                var converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                return (converted, 1.0, true);
            }

            uint targetWidth = Math.Max(1u, (uint)Math.Round(source.PixelWidth * scale));
            uint targetHeight = Math.Max(1u, (uint)Math.Round(source.PixelHeight * scale));

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
            using var encSource = needsFormatConvert
                ? SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                : null;
            encoder.SetSoftwareBitmap(encSource ?? source);
            await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight,
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            var scaledBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);

            return (scaledBitmap, (double)targetWidth / source.PixelWidth, true);
        }

        // Windows zh-CN OCR inserts spaces between adjacent Chinese/CJK characters; strip only those.
        private static string NormalizeCjkSpacing(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            string current = input.Trim();
            string next;
            do
            {
                next = CjkSpaceRegex().Replace(current, "$1$2");
                if (next == current)
                {
                    break;
                }

                current = next;
            } while (true);

            return current;
        }

        [GeneratedRegex(@"([\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])\s+([\u4e00-\u9fff\u3000-\u303f\uff00-\uffef])")]
        private static partial Regex CjkSpaceRegex();
    }
}
