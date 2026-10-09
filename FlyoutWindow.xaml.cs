using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MediaFlyout.Interop;
using MediaFlyout.Services;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace MediaFlyout
{
    public partial class FlyoutWindow : Window
    {
        private const double ScreenMargin = 20;

        private readonly MediaService _media;
        private readonly VolumeService _volume;
        private readonly Settings _settings;
        private readonly DispatcherTimer _hideTimer;
        private readonly DispatcherTimer _tickTimer;
        private readonly UISettings _uiSettings = new();

        private IntPtr _hwnd;
        private MediaInfo _info = MediaInfo.Empty;
        private bool _isShown;
        private bool _isHiding;
        private bool _updatingVolume;
        private bool _allowClose;
        private string? _themeKey;

        public FlyoutWindow(MediaService media, VolumeService volume, Settings settings)
        {
            _media = media;
            _volume = volume;
            _settings = settings;

            InitializeComponent();

            _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(settings.HideDelayMs) };
            _hideTimer.Tick += (_, _) =>
            {
                _hideTimer.Stop();
                if (IsMouseOver) return; // MouseLeave restarts the timer
                HideFlyout();
            };

            _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _tickTimer.Tick += (_, _) => UpdateTimeline();

            MouseEnter += (_, _) => _hideTimer.Stop();
            MouseLeave += (_, _) => RestartHideTimer();

            ApplyTheme();
            UpdateMedia(MediaInfo.Empty);
            new WindowInteropHelper(this).EnsureHandle();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;

            var source = HwndSource.FromHwnd(_hwnd);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.AddHook(WndProc);

            long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
            ex |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
            ex &= ~Native.WS_EX_APPWINDOW;
            Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex));

            Native.SetRoundedCorners(_hwnd);
            _themeKey = null;
            ApplyTheme();
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                HideFlyout();
            }
            base.OnClosing(e);
        }

        public new void Close()
        {
            _allowClose = true;
            base.Close();
        }

        // ---------------- Show / hide ----------------

        public void ShowFlyout()
        {
            ApplyTheme();
            PositionWindow();

            if (!_isShown || _isHiding)
            {
                bool wasHidden = !_isShown;
                _isShown = true;
                _isHiding = false;

                Root.BeginAnimation(OpacityProperty, null);
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, null);

                if (wasHidden)
                {
                    Root.Opacity = 0;
                    RootTranslate.Y = -8;
                    UpdateVolume(_volume.Level, _volume.Muted);
                    UpdateTimeline();
                    Show();
                }

                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                Root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
                RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
                _tickTimer.Start();
            }

            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            RestartHideTimer();
        }

        public void HideFlyout()
        {
            if (!_isShown || _isHiding) return;
            _isHiding = true;
            _hideTimer.Stop();

            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) =>
            {
                if (!_isHiding) return; // re-shown during the fade
                _isHiding = false;
                _isShown = false;
                _tickTimer.Stop();
                Hide();
            };
            Root.BeginAnimation(OpacityProperty, fade);
            RootTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, TimeSpan.FromMilliseconds(140)));
        }

        private void RestartHideTimer()
        {
            if (!_isShown) return;
            _hideTimer.Stop();
            _hideTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, _settings.HideDelayMs));
            _hideTimer.Start();
        }

        public void ApplySettings()
        {
            if (_isShown) PositionWindow();
            UpdateMedia(_info);
            RestartHideTimer();
        }

        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            Left = _settings.Position switch
            {
                FlyoutPosition.TopCenter => area.Left + (area.Width - width) / 2,
                FlyoutPosition.TopRight => area.Right - width - ScreenMargin,
                _ => area.Left + ScreenMargin,
            };
            Top = area.Top + ScreenMargin;
        }

        // ---------------- Theme ----------------

        private static bool SystemUsesLightTheme()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyTheme()
        {
            bool light = SystemUsesLightTheme();
            Windows.UI.Color accent;
            try
            {
                accent = _uiSettings.GetColorValue(light ? UIColorType.AccentDark1 : UIColorType.AccentLight2);
            }
            catch
            {
                accent = light ? Windows.UI.Color.FromArgb(255, 0, 95, 184) : Windows.UI.Color.FromArgb(255, 153, 235, 255);
            }

            string key = $"{light}|{accent}";
            if (key == _themeKey) return;
            _themeKey = key;

            static SolidColorBrush B(uint argb)
            {
                var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                b.Freeze();
                return b;
            }

            if (light)
            {
                Resources["TextPrimary"] = B(0xE4000000);
                Resources["TextSecondary"] = B(0x9E000000);
                Resources["TrackBrush"] = B(0x40000000);
                Resources["HoverBrush"] = B(0x0F000000);
                Resources["PressedBrush"] = B(0x08000000);
                Resources["SubtleBrush"] = B(0x0F000000);
                Resources["DividerBrush"] = B(0x14000000);
                Resources["ThumbOuter"] = B(0xFFFFFFFF);
                Resources["ThumbBorder"] = B(0x1A000000);
            }
            else
            {
                Resources["TextPrimary"] = B(0xFFFFFFFF);
                Resources["TextSecondary"] = B(0xC5FFFFFF);
                Resources["TrackBrush"] = B(0x50FFFFFF);
                Resources["HoverBrush"] = B(0x12FFFFFF);
                Resources["PressedBrush"] = B(0x0BFFFFFF);
                Resources["SubtleBrush"] = B(0x15FFFFFF);
                Resources["DividerBrush"] = B(0x18FFFFFF);
                Resources["ThumbOuter"] = B(0xFF454545);
                Resources["ThumbBorder"] = B(0x24FFFFFF);
            }

            var accentBrush = new SolidColorBrush(Color.FromArgb(255, accent.R, accent.G, accent.B));
            accentBrush.Freeze();
            Resources["AccentBrush"] = accentBrush;

            if (_hwnd != IntPtr.Zero)
            {
                Native.SetDarkMode(_hwnd, !light);
                if (light)
                    Native.EnableAcrylic(_hwnd, 0xDC, 0xF3, 0xF3, 0xF3);
                else
                    Native.EnableAcrylic(_hwnd, 0xDC, 0x20, 0x20, 0x20);
            }
        }

        // ---------------- Media ----------------

        public void UpdateMedia(MediaInfo info)
        {
            _info = info;

            MediaSection.Visibility = info.HasSession ? Visibility.Visible : Visibility.Collapsed;
            if (!info.HasSession) return;

            TitleText.Text = string.IsNullOrWhiteSpace(info.Title) ? Strings.UnknownTrack : info.Title;
            ArtistText.Text = info.Artist;
            ArtistText.Visibility = string.IsNullOrWhiteSpace(info.Artist) ? Visibility.Collapsed : Visibility.Visible;

            AppNameText.Text = info.App.Name;
            AppIcon.Source = info.App.Icon;
            AppIcon.Visibility = info.App.Icon == null ? Visibility.Collapsed : Visibility.Visible;

            ArtBrush.ImageSource = info.Art;
            ArtImage.Visibility = info.Art == null ? Visibility.Collapsed : Visibility.Visible;

            PlayPauseButton.Content = Glyph(info.IsPlaying ? 0xE769 : 0xE768);
            PlayPauseButton.IsEnabled = info.CanPlayPause;
            PrevButton.IsEnabled = info.CanPrevious;
            NextButton.IsEnabled = info.CanNext;

            TimelinePanel.Visibility = info.Duration > TimeSpan.FromSeconds(1) ? Visibility.Visible : Visibility.Collapsed;
            ProgressHost.Cursor = info.CanSeek ? Cursors.Hand : null;
            UpdateTimeline();
        }

        private void UpdateTimeline()
        {
            var info = _info;
            if (!info.HasSession || info.Duration <= TimeSpan.Zero) return;

            var position = info.GetPosition(DateTimeOffset.Now);
            double fraction = Math.Clamp(position.TotalSeconds / info.Duration.TotalSeconds, 0, 1);
            ProgressFill.Width = fraction * ProgressHost.ActualWidth;
            PositionText.Text = FormatTime(position);
            DurationText.Text = FormatTime(info.Duration);
        }

        private static string Glyph(int codePoint) => ((char)codePoint).ToString();

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

        private void ProgressHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTimeline();

        private async void Progress_MouseDown(object sender, MouseButtonEventArgs e)
        {
            RestartHideTimer();
            var info = _info;
            if (!info.CanSeek || info.Duration <= TimeSpan.Zero || ProgressHost.ActualWidth <= 0) return;
            double fraction = Math.Clamp(e.GetPosition(ProgressHost).X / ProgressHost.ActualWidth, 0, 1);
            ProgressFill.Width = fraction * ProgressHost.ActualWidth;
            await _media.SeekAsync(TimeSpan.FromTicks((long)(info.Duration.Ticks * fraction)));
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            RestartHideTimer();
            await _media.PlayPauseAsync();
        }

        private async void Prev_Click(object sender, RoutedEventArgs e)
        {
            RestartHideTimer();
            await _media.PreviousAsync();
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            RestartHideTimer();
            await _media.NextAsync();
        }

        private void Source_Click(object sender, MouseButtonEventArgs e)
        {
            _info.App.Activate();
            HideFlyout();
        }

        // ---------------- Volume ----------------

        public void UpdateVolume(float level, bool muted)
        {
            int percent = (int)Math.Round(level * 100);
            _updatingVolume = true;
            VolumeSlider.Value = percent;
            _updatingVolume = false;

            VolumeText.Text = percent.ToString();
            MuteButton.Content = Glyph(muted ? 0xE74F
                : percent == 0 ? 0xE992
                : percent < 34 ? 0xE993
                : percent < 67 ? 0xE994
                : 0xE995);
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updatingVolume) return;
            VolumeText.Text = ((int)Math.Round(e.NewValue)).ToString();
            _volume.SetLevel((float)(e.NewValue / 100.0));
            RestartHideTimer();
        }

        private void Mute_Click(object sender, RoutedEventArgs e)
        {
            _volume.ToggleMute();
            RestartHideTimer();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            double step = e.Delta > 0 ? 2 : -2;
            _volume.SetLevel((float)(Math.Clamp(VolumeSlider.Value + step, 0, 100) / 100.0));
            e.Handled = true;
        }
    }
}
