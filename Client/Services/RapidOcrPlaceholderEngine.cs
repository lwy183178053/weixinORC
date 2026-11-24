using Client.Models;
using Windows.Graphics.Imaging;

namespace Client.Services
{
    /// <summary>
    /// Placeholder adapter for Phase 2 RapidOCR (ONNX Runtime + PaddleOCR PP-OCRv4).
    /// Allows the UI and pipeline to validate multi-engine switching before bundling ONNX models.
    /// </summary>
    public sealed class RapidOcrPlaceholderEngine : IOcrEngine
    {
        public string Id => "rapidocr-ppocrv4-onnx";

        public string DisplayName => "RapidOCR PP-OCRv4 (ONNX · 交付推荐，待接入模型)";

        public string Description =>
            "基于 ONNX Runtime + PaddleOCR PP-OCRv4 的离线高精度中文/英数混排引擎，不依赖 Windows 语言包。";

        public bool IsAvailable => false;

        public string AvailabilityNote =>
            "待接入 · 确认引入 RapidOcrNet / ONNX Runtime 与 PP-OCRv4 离线模型（约 25MB）后即可无缝启用。";

        public Task<OcrRecognitionResult> RecognizeAsync(
            SoftwareBitmap bitmap,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException(AvailabilityNote);
        }
    }
}
