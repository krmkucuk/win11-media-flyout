using System.Globalization;

namespace MediaFlyout
{
    public enum AppLanguage
    {
        Auto,
        Turkish,
        English,
    }

    /// <summary>UI text in Turkish and English. Auto follows the Windows display language.</summary>
    internal static class Strings
    {
        private static bool _turkish = DetectTurkish();

        public static void Apply(AppLanguage language)
        {
            _turkish = language switch
            {
                AppLanguage.Turkish => true,
                AppLanguage.English => false,
                _ => DetectTurkish(),
            };
        }

        private static bool DetectTurkish() =>
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

        private static string T(string turkish, string english) => _turkish ? turkish : english;

        // Flyout
        public static string UnknownTrack => T("Bilinmeyen parça", "Unknown track");
        public static string MediaPlayer => T("Medya Oynatıcı", "Media Player");
        public static string MoviesAndTv => T("Filmler ve TV", "Movies & TV");

        // Tray menu
        public static string Preview => T("Önizle", "Preview");
        public static string ShowOnTrackChange => T("Şarkı değişince göster", "Show when the track changes");
        public static string Position => T("Konum", "Position");
        public static string TopLeft => T("Sol üst", "Top left");
        public static string TopCenter => T("Üst orta", "Top center");
        public static string TopRight => T("Sağ üst", "Top right");
        public static string DisplayDuration => T("Görünme süresi", "Display duration");
        public static string Seconds(int n) => T($"{n} saniye", $"{n} seconds");
        public static string StartWithWindows => T("Windows ile başlat", "Start with Windows");
        public static string Language => T("Dil", "Language");
        public static string SystemLanguage => T("Sistem dili", "System default");
        public static string Exit => T("Çıkış", "Exit");
    }
}
