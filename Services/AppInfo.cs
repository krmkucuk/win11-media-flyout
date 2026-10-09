using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaFlyout.Interop;

namespace MediaFlyout.Services
{
    /// <summary>Friendly name + icon of the app that owns a media session.</summary>
    public sealed class AppInfo
    {
        public static readonly AppInfo Unknown = new("", null, null);

        public string Name { get; }
        public ImageSource? Icon { get; }
        public string? ProcessName { get; }

        private AppInfo(string name, ImageSource? icon, string? processName)
        {
            Name = name;
            Icon = icon;
            ProcessName = processName;
        }

        private static readonly Dictionary<string, AppInfo> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, (Func<string> Name, string Process)> KnownIds = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Chrome"] = (() => "Google Chrome", "chrome"),
            ["MSEdge"] = (() => "Microsoft Edge", "msedge"),
            ["Brave"] = (() => "Brave", "brave"),
            ["Opera"] = (() => "Opera", "opera"),
            ["Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic"] = (() => Strings.MediaPlayer, "Microsoft.Media.Player"),
            ["Microsoft.ZuneVideo_8wekyb3d8bbwe!Microsoft.ZuneVideo"] = (() => Strings.MoviesAndTv, "Video.UI"),
        };

        /// <summary>Forgets resolved names, e.g. after the UI language changes.</summary>
        public static void ClearCache()
        {
            lock (Cache) Cache.Clear();
        }

        private static readonly Regex FirefoxId = new("^[0-9A-F]{16}$", RegexOptions.IgnoreCase);

        public static AppInfo Resolve(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return Unknown;
            lock (Cache)
            {
                if (Cache.TryGetValue(aumid, out var cached)) return cached;
            }

            string name;
            string process;
            bool fixedName = false;

            if (KnownIds.TryGetValue(aumid, out var known))
            {
                name = known.Name();
                process = known.Process;
                fixedName = true;
            }
            else if (FirefoxId.IsMatch(aumid))
            {
                (name, process) = ("Firefox", "firefox");
                fixedName = true;
            }
            else if (aumid.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                process = aumid[..^4];
                int slash = process.LastIndexOfAny(new[] { '\\', '/' });
                if (slash >= 0) process = process[(slash + 1)..];
                name = process;
            }
            else if (aumid.Contains('!'))
            {
                process = aumid[(aumid.IndexOf('!') + 1)..];
                name = process;
            }
            else
            {
                process = aumid;
                name = aumid;
            }

            ImageSource? icon = null;
            bool found = false;
            foreach (var p in SafeGetProcesses(process))
            {
                try
                {
                    if (found) continue;
                    string? path = p.MainModule?.FileName;
                    if (path == null) continue;
                    found = true;
                    if (!fixedName)
                    {
                        string? desc = FileVersionInfo.GetVersionInfo(path).FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) name = desc.Trim();
                    }
                    icon = ExtractIcon(path);
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }

            var info = new AppInfo(name, icon, process);
            if (found)
            {
                lock (Cache) Cache[aumid] = info;
            }
            return info;
        }

        private static Process[] SafeGetProcesses(string name)
        {
            try { return Process.GetProcessesByName(name); }
            catch { return Array.Empty<Process>(); }
        }

        private static ImageSource? ExtractIcon(string path)
        {
            try
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon == null) return null;
                var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Brings the app's main window to the front, if it has one.</summary>
        public void Activate()
        {
            if (ProcessName == null) return;
            foreach (var p in SafeGetProcesses(ProcessName))
            {
                try
                {
                    IntPtr hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;
                    if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
                    Native.SetForegroundWindow(hwnd);
                    return;
                }
                catch { }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }
}
