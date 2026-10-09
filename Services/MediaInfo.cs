using System;
using System.Windows.Media;

namespace MediaFlyout.Services
{
    public sealed class MediaInfo
    {
        public static readonly MediaInfo Empty = new();

        public bool HasSession { get; init; }
        public string SessionId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Artist { get; init; } = "";
        public ImageSource? Art { get; init; }

        public AppInfo App { get; init; } = AppInfo.Unknown;

        public bool IsPlaying { get; init; }
        public bool CanPlayPause { get; init; }
        public bool CanPrevious { get; init; }
        public bool CanNext { get; init; }
        public bool CanSeek { get; init; }

        public TimeSpan Duration { get; init; }
        public TimeSpan Position { get; init; }
        public DateTimeOffset PositionTimestamp { get; init; }
        public double PlaybackRate { get; init; } = 1.0;

        /// <summary>Estimated current position, interpolated while playing.</summary>
        public TimeSpan GetPosition(DateTimeOffset now)
        {
            var pos = Position;
            if (IsPlaying)
            {
                var elapsed = now - PositionTimestamp;
                if (elapsed > TimeSpan.Zero)
                    pos += TimeSpan.FromTicks((long)(elapsed.Ticks * PlaybackRate));
            }
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && pos > Duration) pos = Duration;
            return pos;
        }
    }
}
