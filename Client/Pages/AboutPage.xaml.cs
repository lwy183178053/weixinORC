using Client.Services;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Ocr;

namespace Client.Pages
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            InitializeComponent();

            var lines = AppServices.Ocr.Engines.Select(e =>
                $"• {e.DisplayName}\n  状态：{e.AvailabilityNote}\n  说明：{e.Description}");
            EnginesSummaryText.Text = string.Join("\n\n", lines);

            var langs = OcrEngine.AvailableRecognizerLanguages
                .Select(l => $"{l.LanguageTag} ({l.DisplayName})")
                .ToList();

            WindowsLanguagesText.Text = langs.Count > 0
                ? string.Join("\n", langs)
                : "未检测到已安装的 Windows OCR 语言包。";
        }
    }
}
