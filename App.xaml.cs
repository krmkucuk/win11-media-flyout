using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using MediaFlyout.Interop;
using MediaFlyout.Services;

namespace MediaFlyout
{
    public partial class App : Application
    {
        private const string MutexName = @"Local\MediaFlyout.SingleInstance";
        private const string ShowEventName = @"Local\MediaFlyout.Show";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private RegisteredWaitHandle? _showWait;

        private Settings _settings = new();
        private MediaService? _media;
        private VolumeService? _volume;
        private KeyboardHook? _hook;
        private TrayIcon? _tray;
        private FlyoutWindow? _flyout;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out bool isFirstInstance);
            if (!isFirstInstance)
            {
                // Already running: ask the running instance to show its flyout.
                try
                {
                    using var ev = EventWaitHandle.OpenExisting(ShowEventName);
                    ev.Set();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, args) =>
            {
                Log(args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, args) => Log(args.ExceptionObject as Exception);

            _settings = Settings.Load();
            Strings.Apply(_settings.Language);
            Settings.RefreshStartupPath();

            _media = new MediaService(Dispatcher);
            _volume = new VolumeService(Dispatcher);
            _flyout = new FlyoutWindow(_media, _volume, _settings);

            _media.Changed += (info, trackChanged) =>
            {
                _flyout.UpdateMedia(info);
                if (trackChanged && _settings.ShowOnTrackChange && !Native.IsFullscreenAppRunning())
                    _flyout.ShowFlyout();
            };

            _volume.Changed += (level, muted, userVisible) =>
            {
                _flyout.UpdateVolume(level, muted);
                if (userVisible)
                    _flyout.ShowFlyout();
            };
            _flyout.UpdateVolume(_volume.Level, _volume.Muted);

            _hook = new KeyboardHook();
            _hook.MediaKeyPressed += () => Dispatcher.BeginInvoke(new Action(() => _flyout.ShowFlyout()));

            _tray = new TrayIcon(_settings,
                showPreview: () => _flyout.ShowFlyout(),
                settingsChanged: () => _flyout.ApplySettings(),
                exit: ExitApp);

            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent,
                (_, _) => Dispatcher.BeginInvoke(new Action(() => _flyout.ShowFlyout())),
                null, Timeout.Infinite, false);

            try
            {
                await _media.InitializeAsync();
            }
            catch (Exception ex)
            {
                Log(ex);
            }

            if (e.Args.Contains("--show"))
                _flyout.ShowFlyout();
        }

        private void ExitApp()
        {
            _showWait?.Unregister(null);
            _showEvent?.Dispose();
            _hook?.Dispose();
            _tray?.Dispose();
            _volume?.Dispose();
            _media?.Dispose();
            _flyout?.Close();
            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            Shutdown();
        }

        internal static void Log(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                File.AppendAllText(Path.Combine(Settings.Folder, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        }
    }
}
