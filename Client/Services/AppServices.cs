namespace Client.Services
{
    public static class AppServices
    {
        private static readonly Lazy<OcrService> LazyOcr = new(() => new OcrService());

        public static AppSettingsService Settings { get; } = new();
        public static AppLogService Logs { get; } = new();
        public static BackendApiClient Backend { get; } = new();
        public static WeChatWindowService WeChatWindows { get; } = new();
        public static OcrService Ocr => LazyOcr.Value;
        public static nint MainWindowHandle { get; set; }
    }
}
