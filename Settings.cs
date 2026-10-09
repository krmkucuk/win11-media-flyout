using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace MediaFlyout
{
    public enum FlyoutPosition
    {
        TopLeft,
        TopCenter,
        TopRight,
    }

    public sealed class Settings
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "MediaFlyout";

        public static string Folder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaFlyout");

        private static string FilePath => Path.Combine(Folder, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        public bool ShowOnTrackChange { get; set; } = true;
        public FlyoutPosition Position { get; set; } = FlyoutPosition.TopLeft;
        public int HideDelayMs { get; set; } = 3000;
        public AppLanguage Language { get; set; } = AppLanguage.Auto;

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), JsonOptions) ?? new Settings();
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch { }
        }

        private static string StartupCommand => $"\"{Environment.ProcessPath}\"";

        public static bool StartWithWindows
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(RunValue) is string;
            }
            set
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value)
                    key.SetValue(RunValue, StartupCommand);
                else
                    key.DeleteValue(RunValue, false);
            }
        }

        /// <summary>Keeps the autostart entry pointing at this exe if it was moved.</summary>
        public static void RefreshStartupPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(RunValue) is string current && current != StartupCommand)
                    key.SetValue(RunValue, StartupCommand);
            }
            catch { }
        }
    }
}
