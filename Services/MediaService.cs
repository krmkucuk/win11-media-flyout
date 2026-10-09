using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace MediaFlyout.Services
{
    /// <summary>Follows the system's current media session (Spotify, browsers, Media Player, ...).</summary>
    public sealed class MediaService : IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly object _lock = new();
        private SessionManager? _manager;
        private Session? _session;

        private int _version;
        private int _pendingProperties; // 1 when the next refresh must re-read title/art
        private string? _lastTrackKey;
        private string? _lastSessionId;

        public MediaInfo Current { get; private set; } = MediaInfo.Empty;

        /// <summary>(info, trackChanged). Raised on the UI thread.</summary>
        public event Action<MediaInfo, bool>? Changed;

        public MediaService(Dispatcher ui)
        {
            _ui = ui;
        }

        public async Task InitializeAsync()
        {
            _manager = await SessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnManagerChanged;
            _manager.SessionsChanged += OnManagerChanged;
            SelectSession();
        }

        private void OnManagerChanged(SessionManager sender, object args) => SelectSession();

        private void SelectSession()
        {
            var manager = _manager;
            if (manager == null) return;

            Session? next = null;
            try
            {
                next = manager.GetCurrentSession();
                if (next == null)
                {
                    foreach (var s in manager.GetSessions())
                    {
                        next ??= s;
                        if (s.GetPlaybackInfo()?.PlaybackStatus == PlaybackStatus.Playing)
                        {
                            next = s;
                            break;
                        }
                    }
                }
            }
            catch { }

            lock (_lock)
            {
                if (_session != null)
                {
                    try
                    {
                        _session.MediaPropertiesChanged -= OnPropertiesChanged;
                        _session.PlaybackInfoChanged -= OnPlaybackChanged;
                        _session.TimelinePropertiesChanged -= OnTimelineChanged;
                    }
                    catch { }
                }

                _session = next;

                if (_session != null)
                {
                    try
                    {
                        _session.MediaPropertiesChanged += OnPropertiesChanged;
                        _session.PlaybackInfoChanged += OnPlaybackChanged;
                        _session.TimelinePropertiesChanged += OnTimelineChanged;
                    }
                    catch { }
                }
            }

            QueueRefresh(true);
        }

        private void OnPropertiesChanged(Session s, MediaPropertiesChangedEventArgs e) => QueueRefresh(true);
        private void OnPlaybackChanged(Session s, PlaybackInfoChangedEventArgs e) => QueueRefresh(false);
        private void OnTimelineChanged(Session s, TimelinePropertiesChangedEventArgs e) => QueueRefresh(false);

        /// <summary>Coalesces bursts of events (apps often fire several in a row).</summary>
        private async void QueueRefresh(bool properties)
        {
            if (properties) Interlocked.Exchange(ref _pendingProperties, 1);
            int version = Interlocked.Increment(ref _version);

            await Task.Delay(120).ConfigureAwait(false);
            if (version != Volatile.Read(ref _version)) return;

            bool readProps = Interlocked.Exchange(ref _pendingProperties, 0) == 1;
            Session? session;
            lock (_lock) session = _session;

            MediaInfo info;
            try
            {
                info = session == null ? MediaInfo.Empty : await BuildAsync(session, readProps, Current).ConfigureAwait(false);
            }
            catch
            {
                info = MediaInfo.Empty;
            }

            if (version != Volatile.Read(ref _version))
            {
                if (readProps) Interlocked.Exchange(ref _pendingProperties, 1);
                return;
            }

            _ = _ui.BeginInvoke(new Action(() => Deliver(info)));
        }

        private void Deliver(MediaInfo info)
        {
            string? key = info.HasSession && !string.IsNullOrEmpty(info.Title)
                ? info.SessionId + "\n" + info.Title + "\n" + info.Artist
                : null;

            bool trackChanged = key != null
                && _lastTrackKey != null
                && key != _lastTrackKey
                && info.SessionId == _lastSessionId;

            if (key != null)
            {
                _lastTrackKey = key;
                _lastSessionId = info.SessionId;
            }
            else if (!info.HasSession)
            {
                _lastTrackKey = null;
                _lastSessionId = null;
            }

            Current = info;
            Changed?.Invoke(info, trackChanged);
        }

        private static async Task<MediaInfo> BuildAsync(Session session, bool readProps, MediaInfo previous)
        {
            string id = session.SourceAppUserModelId ?? "";
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            string title = previous.Title;
            string artist = previous.Artist;
            ImageSource? art = previous.Art;

            if (readProps || previous.SessionId != id || !previous.HasSession)
            {
                var props = await session.TryGetMediaPropertiesAsync();
                title = props?.Title ?? "";
                artist = props?.Artist ?? "";
                if (string.IsNullOrWhiteSpace(artist)) artist = props?.AlbumArtist ?? "";
                art = await LoadThumbnailAsync(props?.Thumbnail).ConfigureAwait(false);
            }

            var controls = playback?.Controls;
            var duration = timeline.EndTime - timeline.StartTime;
            var position = timeline.Position - timeline.StartTime;

            // Some apps report a bogus timestamp; fall back to "now".
            var stamp = timeline.LastUpdatedTime;
            var now = DateTimeOffset.Now;
            if (stamp.Year < 2000 || stamp > now) stamp = now;

            return new MediaInfo
            {
                HasSession = true,
                SessionId = id,
                Title = title,
                Artist = artist,
                Art = art,
                App = AppInfo.Resolve(id),
                IsPlaying = playback?.PlaybackStatus == PlaybackStatus.Playing,
                CanPlayPause = controls?.IsPlayPauseToggleEnabled ?? false,
                CanPrevious = controls?.IsPreviousEnabled ?? false,
                CanNext = controls?.IsNextEnabled ?? false,
                CanSeek = controls?.IsPlaybackPositionEnabled ?? false,
                Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                Position = position,
                PositionTimestamp = stamp,
                PlaybackRate = playback?.PlaybackRate is double rate && rate > 0 ? rate : 1.0,
            };
        }

        private static async Task<ImageSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
        {
            if (reference == null) return null;
            try
            {
                using var winrtStream = await reference.OpenReadAsync();
                using var stream = winrtStream.AsStreamForRead();
                var memory = new MemoryStream();
                await stream.CopyToAsync(memory).ConfigureAwait(false);
                if (memory.Length == 0) return null;
                memory.Position = 0;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelHeight = 192;
                bitmap.StreamSource = memory;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        // ---- Commands ----

        private Session? CurrentSession
        {
            get { lock (_lock) return _session; }
        }

        public async Task PlayPauseAsync()
        {
            try { if (CurrentSession is { } s) await s.TryTogglePlayPauseAsync(); } catch { }
        }

        public async Task NextAsync()
        {
            try { if (CurrentSession is { } s) await s.TrySkipNextAsync(); } catch { }
        }

        public async Task PreviousAsync()
        {
            try { if (CurrentSession is { } s) await s.TrySkipPreviousAsync(); } catch { }
        }

        public async Task SeekAsync(TimeSpan position)
        {
            try { if (CurrentSession is { } s) await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
        }

        public void Dispose()
        {
            if (_manager != null)
            {
                _manager.CurrentSessionChanged -= OnManagerChanged;
                _manager.SessionsChanged -= OnManagerChanged;
            }
            lock (_lock)
            {
                if (_session != null)
                {
                    try
                    {
                        _session.MediaPropertiesChanged -= OnPropertiesChanged;
                        _session.PlaybackInfoChanged -= OnPlaybackChanged;
                        _session.TimelinePropertiesChanged -= OnTimelineChanged;
                    }
                    catch { }
                }
                _session = null;
            }
        }
    }
}
