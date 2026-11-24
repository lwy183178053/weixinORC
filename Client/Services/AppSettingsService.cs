using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Client.Services
{
    public sealed class AppSettingsData
    {
        public string Theme { get; set; } = "light";
        public string BackendUrl { get; set; } = "http://127.0.0.1:28765";
    }

    public sealed class AppSettingsService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string _settingsPath;

        public AppSettingsData Current { get; private set; } = new();
        public bool IsDarkMode => string.Equals(Current.Theme, "dark", StringComparison.OrdinalIgnoreCase);
        public ElementTheme CurrentElementTheme => IsDarkMode ? ElementTheme.Dark : ElementTheme.Light;

        public event Action<bool>? ThemeChanged;

        public AppSettingsService()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "weixinORC");
            _settingsPath = Path.Combine(dir, "settings.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var parsed = JsonSerializer.Deserialize<AppSettingsData>(json, JsonOpts);
                    if (parsed is not null)
                    {
                        Current = parsed;
                    }
                }
            }
            catch
            {
                Current = new AppSettingsData();
            }

            if (string.IsNullOrWhiteSpace(Current.BackendUrl))
            {
                Current.BackendUrl = "http://127.0.0.1:28765";
            }
        }

        public void Save()
        {
            try
            {
                string? dir = Path.GetDirectoryName(_settingsPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string json = JsonSerializer.Serialize(Current, JsonOpts);
                File.WriteAllText(_settingsPath, json);
            }
            catch
            {
                // Best-effort persistence
            }
        }

        public void ToggleTheme()
        {
            ApplyTheme(!IsDarkMode, save: true);
        }

        public void ApplyTheme(bool isDark, bool save = true)
        {
            Current.Theme = isDark ? "dark" : "light";
            if (save)
            {
                Save();
            }

            UpdateResourceBrushes(isDark);
            MainWindow.Instance?.ApplyWindowTheme(isDark);
            ThemeChanged?.Invoke(isDark);
        }

        public static void UpdateResourceBrushes(bool isDark)
        {
            if (Application.Current?.Resources is not ResourceDictionary res)
            {
                return;
            }

            if (isDark)
            {
                SetBrush(res, "BrandPrimaryBrush", 0x8B, 0x6E, 0xFF);
                SetBrush(res, "BrandPrimaryHoverBrush", 0x9B, 0x82, 0xFF);
                SetBrush(res, "BrandPrimaryPressedBrush", 0x74, 0x54, 0xF5);
                SetBrush(res, "BrandPrimaryDisabledBrush", 0x4A, 0x3D, 0x7A);
                SetBrush(res, "BrandSoftBgBrush", 0x22, 0x1D, 0x3B);
                SetBrush(res, "BrandSoftBorderBrush", 0x3D, 0x33, 0x66);
                SetBrush(res, "BrandCardTintBrush", 0x19, 0x18, 0x29);
                SetBrush(res, "BrandHighlightTextBrush", 0xC4, 0xB5, 0xFD);

                SetBrush(res, "AppChromeBgBrush", 0x12, 0x14, 0x1B);
                SetBrush(res, "AppRailBgBrush", 0x0F, 0x11, 0x17);
                SetBrush(res, "AppSurfaceBrush", 0x16, 0x19, 0x22);
                SetBrush(res, "AppSurfaceAltBrush", 0x13, 0x16, 0x1E);
                SetBrush(res, "AppCanvasBgBrush", 0x0E, 0x10, 0x16);
                SetBrush(res, "AppChatCanvasBgBrush", 0x0C, 0x0E, 0x14);
                SetBrush(res, "AppFooterBgBrush", 0x12, 0x14, 0x1B);
                SetBrush(res, "AppInputBgBrush", 0x1D, 0x21, 0x2D);
                SetBrush(res, "AppBorderBrush", 0x26, 0x2B, 0x38);
                SetBrush(res, "AppBorderSubtleBrush", 0x1F, 0x24, 0x30);
                SetBrush(res, "AppSplitterGripBrush", 0x3B, 0x42, 0x56);

                SetBrush(res, "ButtonBgBrush", 0x1D, 0x21, 0x2D);
                SetBrush(res, "ButtonBorderBrush", 0x2F, 0x35, 0x45);
                SetBrush(res, "ButtonHoverBgBrush", 0x26, 0x2B, 0x3A);
                SetBrush(res, "ButtonHoverBorderBrush", 0x43, 0x4C, 0x63);
                SetBrush(res, "ButtonPressedBgBrush", 0x2E, 0x34, 0x46);
                SetBrush(res, "ButtonPressedBorderBrush", 0x52, 0x5C, 0x76);
                SetBrush(res, "ButtonDisabledBgBrush", 0x16, 0x19, 0x22);
                SetBrush(res, "ButtonDisabledBorderBrush", 0x23, 0x28, 0x36);

                SetBrush(res, "CloseHoverBgBrush", 0x25, 0x2A, 0x38);
                SetBrush(res, "ClosePressedBgBrush", 0x30, 0x37, 0x4A);
                SetBrush(res, "RailHoverBgBrush", 0x1D, 0x21, 0x30);
                SetBrush(res, "RailPressedBgBrush", 0x26, 0x2B, 0x40);

                SetBrush(res, "RadioBorderBrush", 0x2C, 0x32, 0x42);
                SetBrush(res, "RadioHoverBorderBrush", 0x44, 0x4D, 0x64);
                SetBrush(res, "RadioPressedBorderBrush", 0x56, 0x60, 0x7A);
                SetBrush(res, "RadioRingStrokeBrush", 0x5A, 0x63, 0x7A);
                SetBrush(res, "RadioHoverOverlayBrush", 0xFF, 0xFF, 0xFF);

                SetBrush(res, "TextPrimaryBrush", 0xF0, 0xF2, 0xF7);
                SetBrush(res, "TextSecondaryBrush", 0xA3, 0xAC, 0xBE);
                SetBrush(res, "TextMutedBrush", 0x72, 0x7B, 0x8E);

                SetBrush(res, "ChatIncomingBgBrush", 0x1D, 0x21, 0x2D);
                SetBrush(res, "ChatIncomingBorderBrush", 0x2B, 0x31, 0x42);
                SetBrush(res, "ChatOutgoingBgBrush", 0x25, 0x5A, 0x33);
                SetBrush(res, "ChatOutgoingTextBrush", 0xEB, 0xF9, 0xEE);

                SetBrush(res, "SuccessBgBrush", 0x13, 0x2B, 0x1E);
                SetBrush(res, "SuccessTextBrush", 0x34, 0xD3, 0x99);
                SetBrush(res, "WarningBgBrush", 0x2B, 0x21, 0x12);
                SetBrush(res, "WarningTextBrush", 0xFB, 0xBF, 0x24);
                SetBrush(res, "DangerBgBrush", 0x2E, 0x16, 0x19);
                SetBrush(res, "DangerHoverBgBrush", 0x3D, 0x1C, 0x21);
                SetBrush(res, "DangerPressedBgBrush", 0x4F, 0x23, 0x29);
                SetBrush(res, "DangerBorderBrush", 0x5E, 0x2A, 0x31);
                SetBrush(res, "DangerTextBrush", 0xF8, 0x71, 0x71);
                SetBrush(res, "InfoBgBrush", 0x15, 0x24, 0x3B);
                SetBrush(res, "InfoBorderBrush", 0x24, 0x3E, 0x66);
                SetBrush(res, "InfoTextBrush", 0x60, 0xA5, 0xFA);

                SetBrush(res, "AccentFillColorDefaultBrush", 0x8B, 0x6E, 0xFF);
                SetBrush(res, "AccentFillColorSecondaryBrush", 0x9B, 0x82, 0xFF);
                SetBrush(res, "AccentFillColorTertiaryBrush", 0x74, 0x54, 0xF5);
                SetBrush(res, "ContentDialogBackground", 0x15, 0x18, 0x21);
                SetBrush(res, "ContentDialogBorderBrush", 0x2A, 0x30, 0x40);
                SetBrush(res, "SmokeFillColorDefaultBrush", 0x04, 0x05, 0x0A, 0xB2);
                SetBrush(res, "ContentDialogSmokeFill", 0x04, 0x05, 0x0A, 0xB2);
                SetBrush(res, "SystemControlPageBackgroundMediumAltMediumBrush", 0x04, 0x05, 0x0A, 0xB2);
                SetBrush(res, "LoginBgBrush", 0x0C, 0x0E, 0x14);
            }
            else
            {
                SetBrush(res, "BrandPrimaryBrush", 0x7C, 0x5C, 0xFC);
                SetBrush(res, "BrandPrimaryHoverBrush", 0x69, 0x46, 0xF5);
                SetBrush(res, "BrandPrimaryPressedBrush", 0x58, 0x35, 0xE0);
                SetBrush(res, "BrandPrimaryDisabledBrush", 0xC4, 0xB5, 0xFD);
                SetBrush(res, "BrandSoftBgBrush", 0xF1, 0xEE, 0xFF);
                SetBrush(res, "BrandSoftBorderBrush", 0xDD, 0xD5, 0xFF);
                SetBrush(res, "BrandCardTintBrush", 0xF8, 0xF7, 0xFF);
                SetBrush(res, "BrandHighlightTextBrush", 0x5B, 0x3C, 0xE6);

                SetBrush(res, "AppChromeBgBrush", 0xF4, 0xF5, 0xF9);
                SetBrush(res, "AppRailBgBrush", 0xF2, 0xF3, 0xF8);
                SetBrush(res, "AppSurfaceBrush", 0xFF, 0xFF, 0xFF);
                SetBrush(res, "AppSurfaceAltBrush", 0xFA, 0xFA, 0xFC);
                SetBrush(res, "AppCanvasBgBrush", 0xF8, 0xF9, 0xFC);
                SetBrush(res, "AppChatCanvasBgBrush", 0xF5, 0xF6, 0xF9);
                SetBrush(res, "AppFooterBgBrush", 0xF7, 0xF8, 0xFC);
                SetBrush(res, "AppInputBgBrush", 0xF5, 0xF6, 0xFA);
                SetBrush(res, "AppBorderBrush", 0xE6, 0xE8, 0xF0);
                SetBrush(res, "AppBorderSubtleBrush", 0xEE, 0xF0, 0xF6);
                SetBrush(res, "AppSplitterGripBrush", 0xCB, 0xD0, 0xDC);

                SetBrush(res, "ButtonBgBrush", 0xFF, 0xFF, 0xFF);
                SetBrush(res, "ButtonBorderBrush", 0xD8, 0xDC, 0xE6);
                SetBrush(res, "ButtonHoverBgBrush", 0xF2, 0xF4, 0xF8);
                SetBrush(res, "ButtonHoverBorderBrush", 0xBF, 0xC5, 0xD4);
                SetBrush(res, "ButtonPressedBgBrush", 0xE7, 0xEA, 0xF2);
                SetBrush(res, "ButtonPressedBorderBrush", 0xA9, 0xB1, 0xC4);
                SetBrush(res, "ButtonDisabledBgBrush", 0xF7, 0xF8, 0xFA);
                SetBrush(res, "ButtonDisabledBorderBrush", 0xE8, 0xEB, 0xF2);

                SetBrush(res, "CloseHoverBgBrush", 0xF0, 0xF2, 0xF7);
                SetBrush(res, "ClosePressedBgBrush", 0xE4, 0xE7, 0xF0);
                SetBrush(res, "RailHoverBgBrush", 0xE7, 0xE5, 0xF8);
                SetBrush(res, "RailPressedBgBrush", 0xDD, 0xD8, 0xFA);

                SetBrush(res, "RadioBorderBrush", 0xDF, 0xE3, 0xEE);
                SetBrush(res, "RadioHoverBorderBrush", 0xC4, 0xCA, 0xDB);
                SetBrush(res, "RadioPressedBorderBrush", 0xA9, 0xB2, 0xC8);
                SetBrush(res, "RadioRingStrokeBrush", 0xB8, 0xC0, 0xD0);
                SetBrush(res, "RadioHoverOverlayBrush", 0x1F, 0x23, 0x29);

                SetBrush(res, "TextPrimaryBrush", 0x1F, 0x23, 0x29);
                SetBrush(res, "TextSecondaryBrush", 0x64, 0x6A, 0x73);
                SetBrush(res, "TextMutedBrush", 0x8F, 0x95, 0x9E);

                SetBrush(res, "ChatIncomingBgBrush", 0xFF, 0xFF, 0xFF);
                SetBrush(res, "ChatIncomingBorderBrush", 0xE5, 0xE7, 0xEB);
                SetBrush(res, "ChatOutgoingBgBrush", 0x95, 0xEC, 0x69);
                SetBrush(res, "ChatOutgoingTextBrush", 0x1F, 0x23, 0x29);

                SetBrush(res, "SuccessBgBrush", 0xE9, 0xF9, 0xF0);
                SetBrush(res, "SuccessTextBrush", 0x17, 0xA3, 0x4A);
                SetBrush(res, "WarningBgBrush", 0xFF, 0xF4, 0xE5);
                SetBrush(res, "WarningTextBrush", 0xD9, 0x77, 0x06);
                SetBrush(res, "DangerBgBrush", 0xFF, 0xF5, 0xF5);
                SetBrush(res, "DangerHoverBgBrush", 0xFE, 0xE2, 0xE2);
                SetBrush(res, "DangerPressedBgBrush", 0xFE, 0xCA, 0xCA);
                SetBrush(res, "DangerBorderBrush", 0xFE, 0xCA, 0xCA);
                SetBrush(res, "DangerTextBrush", 0xDC, 0x26, 0x26);
                SetBrush(res, "InfoBgBrush", 0xEA, 0xF3, 0xFF);
                SetBrush(res, "InfoBorderBrush", 0xBF, 0xDB, 0xFE);
                SetBrush(res, "InfoTextBrush", 0x25, 0x63, 0xEB);

                SetBrush(res, "AccentFillColorDefaultBrush", 0x7C, 0x5C, 0xFC);
                SetBrush(res, "AccentFillColorSecondaryBrush", 0x69, 0x46, 0xF5);
                SetBrush(res, "AccentFillColorTertiaryBrush", 0x58, 0x35, 0xE0);
                SetBrush(res, "ContentDialogBackground", 0xFF, 0xFF, 0xFF);
                SetBrush(res, "ContentDialogBorderBrush", 0xE2, 0xE5, 0xF0);
                SetBrush(res, "SmokeFillColorDefaultBrush", 0x0F, 0x17, 0x2A, 0x4D);
                SetBrush(res, "ContentDialogSmokeFill", 0x0F, 0x17, 0x2A, 0x4D);
                SetBrush(res, "SystemControlPageBackgroundMediumAltMediumBrush", 0x0F, 0x17, 0x2A, 0x4D);
                SetBrush(res, "LoginBgBrush", 0xF2, 0xF3, 0xFB);
            }
        }

        private static void SetBrush(ResourceDictionary res, string key, byte r, byte g, byte b, byte a = 255)
        {
            var color = Color.FromArgb(a, r, g, b);
            if (res.TryGetValue(key, out object? existing) && existing is SolidColorBrush brush)
            {
                brush.Color = color;
            }
            else
            {
                res[key] = new SolidColorBrush(color);
            }

            if (res.ThemeDictionaries is not null)
            {
                foreach (var entry in res.ThemeDictionaries.Values)
                {
                    if (entry is ResourceDictionary td &&
                        td.TryGetValue(key, out object? tdExisting) &&
                        tdExisting is SolidColorBrush tdBrush)
                    {
                        tdBrush.Color = color;
                    }
                }
            }
        }
    }
}
