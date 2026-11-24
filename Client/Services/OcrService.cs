using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    public sealed class OcrService
    {
        private readonly List<IOcrEngine> _engines;

        public OcrService()
        {
            _engines =
            [
                new WindowsOcrEngine(),
                new RapidOcrPlaceholderEngine()
            ];
        }

        public IReadOnlyList<IOcrEngine> Engines => _engines;

        public IOcrEngine GetDefaultEngine() =>
            _engines.FirstOrDefault(e => e.IsAvailable) ?? _engines[0];

        public IOcrEngine? FindById(string engineId) =>
            _engines.FirstOrDefault(e => e.Id.Equals(engineId, StringComparison.OrdinalIgnoreCase));

        public async Task<SoftwareBitmap> LoadImageFileFromPathAsync(string filePath)
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath).ConfigureAwait(false);
            using var mem = new MemoryStream(bytes, writable: false);
            using var randomStream = mem.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomStream).AsTask().ConfigureAwait(false);
            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied).AsTask().ConfigureAwait(false);
        }
    }
}
