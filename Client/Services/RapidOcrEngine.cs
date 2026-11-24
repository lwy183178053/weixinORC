using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Client.Models;
using RapidOcrNet;
using SkiaSharp;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class RapidOcrEngine : IOcrEngine, IDisposable
    {
        private readonly object _initLock = new();
        private RapidOcr? _ocr;
        private readonly string _detPath;
        private readonly string _clsPath;
        private readonly string _recPath;
        private readonly string _keysPath;

        public RapidOcrEngine()
        {
            string baseDir = AppContext.BaseDirectory;
            _detPath = Path.Combine(baseDir, "models", "v5", "ch_PP-OCRv5_mobile_det.onnx");
            _clsPath = Path.Combine(baseDir, "models", "v5", "ch_PP-LCNet_x0_25_textline_ori_cls_mobile.onnx");
            _recPath = Path.Combine(baseDir, "Assets", "OcrModels", "ch_PP-OCRv5_rec_mobile.onnx");
            _keysPath = Path.Combine(baseDir, "Assets", "OcrModels", "ppocrv5_dict.txt");
        }

        public string Id => "rapidocr-ppocrv5-ch";

        public string DisplayName => "本地图文识别服务";

        public string Description => "支持截图与本地图片文字提取。";

        public bool IsAvailable =>
            File.Exists(_detPath) &&
            File.Exists(_clsPath) &&
            File.Exists(_recPath) &&
            File.Exists(_keysPath);

        public string AvailabilityNote
        {
            get
            {
                if (IsAvailable)
                {
                    return "识别服务已就绪";
                }

                var missing = new List<string>();
                if (!File.Exists(_detPath)) missing.Add(Path.GetFileName(_detPath));
                if (!File.Exists(_clsPath)) missing.Add(Path.GetFileName(_clsPath));
                if (!File.Exists(_recPath)) missing.Add(Path.GetFileName(_recPath));
                if (!File.Exists(_keysPath)) missing.Add(Path.GetFileName(_keysPath));
                return $"识别组件缺失：{string.Join("、", missing)}";
            }
        }

        public string GetModelDiagnostics()
        {
            static string FormatFile(string label, string path)
            {
                if (!File.Exists(path)) return $"  [缺失] {label}: {path}";
                var lenMb = new FileInfo(path).Length / (1024.0 * 1024.0);
                return $"  [已就绪] {label}: {Path.GetFileName(path)} ({lenMb:F2} MB)";
            }

            return string.Join(Environment.NewLine, new[]
            {
                FormatFile("检测模型 (Det)", _detPath),
                FormatFile("方向分类 (Cls)", _clsPath),
                FormatFile("中文识别 (Rec)", _recPath),
                FormatFile("中文字典 (Dict)", _keysPath)
            });
        }

        private RapidOcr EnsureInitialized()
        {
            if (_ocr is not null)
            {
                return _ocr;
            }

            lock (_initLock)
            {
                if (_ocr is not null)
                {
                    return _ocr;
                }

                if (!IsAvailable)
                {
                    throw new FileNotFoundException(AvailabilityNote);
                }

                AppServices.Logs.Info("OCR", "正在初始化本地识别组件...");
                var engine = new RapidOcr();
                engine.InitModels(_detPath, _clsPath, _recPath, _keysPath);
                _ocr = engine;
                AppServices.Logs.Success("OCR", "本地识别组件初始化完成。");
                return _ocr;
            }
        }

        public Task<OcrRecognitionResult> RecognizeAsync(
            SoftwareBitmap bitmap,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var skBitmap = ConvertToSkBitmap(bitmap);
                return RecognizeSkBitmap(skBitmap, cancellationToken);
            }, cancellationToken);
        }

        private OcrRecognitionResult RecognizeSkBitmap(SKBitmap skBitmap, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var ocr = EnsureInitialized();
            ct.ThrowIfCancellationRequested();

            // Desktop screenshots are upright; skip 180° orientation flip for faster & cleaner text recognition.
            var options = RapidOcrOptions.Default with
            {
                DoAngle = false,
                Padding = 24,
                ImgResize = 1440,
                TextScore = 0.45f
            };

            OcrResult raw = ocr.Detect(skBitmap, options);
            sw.Stop();

            var blocks = raw.TextBlocks ?? [];
            var parsed = new List<(string Text, float Score, PixelRect Bounds)>(blocks.Length);

            foreach (var block in blocks)
            {
                string text = (block.Text ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var pts = block.BoxPoints;
                if (pts is null || pts.Length == 0)
                {
                    continue;
                }

                double minX = pts.Min(p => p.X);
                double minY = pts.Min(p => p.Y);
                double maxX = pts.Max(p => p.X);
                double maxY = pts.Max(p => p.Y);
                float score = block.CharScores is { Length: > 0 } scores ? scores.Average() : 0f;

                parsed.Add((text, score, new PixelRect(minX, minY, Math.Max(1, maxX - minX), Math.Max(1, maxY - minY))));
            }

            // Sort in natural reading order: top-to-bottom rows, left-to-right within each row.
            var ordered = SortReadingOrder(parsed);
            var lines = new List<OcrLineResult>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                lines.Add(new OcrLineResult
                {
                    Index = i + 1,
                    Text = ordered[i].Text,
                    Confidence = ordered[i].Score,
                    Bounds = ordered[i].Bounds
                });
            }

            AppServices.Logs.Success("OCR", $"图片识别完成 ({skBitmap.Width}x{skBitmap.Height})，提取 {lines.Count} 行文本，耗时 {sw.ElapsedMilliseconds} ms。");

            return new OcrRecognitionResult
            {
                EngineId = Id,
                EngineName = DisplayName,
                LanguageTag = "zh-CN / en",
                ImageWidth = skBitmap.Width,
                ImageHeight = skBitmap.Height,
                ElapsedMilliseconds = sw.ElapsedMilliseconds,
                Lines = lines,
                FullText = string.Join(Environment.NewLine, lines.Select(l => l.Text))
            };
        }

        private static List<(string Text, float Score, PixelRect Bounds)> SortReadingOrder(
            List<(string Text, float Score, PixelRect Bounds)> items)
        {
            if (items.Count <= 1)
            {
                return items;
            }

            var sortedByY = items.OrderBy(i => i.Bounds.Y).ToList();
            var rows = new List<List<(string Text, float Score, PixelRect Bounds)>>();

            foreach (var item in sortedByY)
            {
                double itemCenterY = item.Bounds.Y + item.Bounds.Height / 2.0;
                var matchedRow = rows.FirstOrDefault(r =>
                {
                    double rowCenterY = r.Average(x => x.Bounds.Y + x.Bounds.Height / 2.0);
                    double rowHeight = r.Average(x => x.Bounds.Height);
                    return Math.Abs(itemCenterY - rowCenterY) <= Math.Max(12.0, rowHeight * 0.65);
                });

                if (matchedRow is not null)
                {
                    matchedRow.Add(item);
                }
                else
                {
                    rows.Add([item]);
                }
            }

            var result = new List<(string Text, float Score, PixelRect Bounds)>(items.Count);
            foreach (var row in rows.OrderBy(r => r.Min(x => x.Bounds.Y)))
            {
                result.AddRange(row.OrderBy(x => x.Bounds.X));
            }
            return result;
        }

        private static SKBitmap ConvertToSkBitmap(SoftwareBitmap source)
        {
            SoftwareBitmap? converted = null;
            SoftwareBitmap target = source;
            if (source.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
            {
                converted = SoftwareBitmap.Convert(source, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                target = converted;
            }

            try
            {
                int width = target.PixelWidth;
                int height = target.PixelHeight;
                byte[] pixels = new byte[checked(width * height * 4)];
                target.CopyToBuffer(pixels.AsBuffer());

                var alpha = target.BitmapAlphaMode == BitmapAlphaMode.Ignore
                    ? SKAlphaType.Opaque
                    : SKAlphaType.Premul;

                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, alpha);
                var skBitmap = new SKBitmap(info);
                Marshal.Copy(pixels, 0, skBitmap.GetPixels(), pixels.Length);
                return skBitmap;
            }
            finally
            {
                converted?.Dispose();
            }
        }

        public void Dispose()
        {
            _ocr?.Dispose();
            _ocr = null;
        }
    }
}
