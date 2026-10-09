using System;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MediaFlyout.Services
{
    /// <summary>Tracks the default playback device's master volume.</summary>
    public sealed class VolumeService : IMMNotificationClient, IDisposable
    {
        private readonly Dispatcher _ui;
        private readonly MMDeviceEnumerator _enumerator = new();
        private MMDevice? _device;
        private AudioEndpointVolume? _endpoint;

        public float Level { get; private set; }
        public bool Muted { get; private set; }

        /// <summary>(level, muted, shouldShowFlyout). Raised on the UI thread.</summary>
        public event Action<float, bool, bool>? Changed;

        public VolumeService(Dispatcher ui)
        {
            _ui = ui;
            Attach();
            try { _enumerator.RegisterEndpointNotificationCallback(this); } catch { }
        }

        private void Attach()
        {
            Detach();
            try
            {
                _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                _endpoint = _device.AudioEndpointVolume;
                _endpoint.OnVolumeNotification += OnVolumeNotification;
                Level = _endpoint.MasterVolumeLevelScalar;
                Muted = _endpoint.Mute;
            }
            catch
            {
                _device = null;
                _endpoint = null;
            }
        }

        private void Detach()
        {
            if (_endpoint != null)
            {
                try { _endpoint.OnVolumeNotification -= OnVolumeNotification; } catch { }
            }
            try { _device?.Dispose(); } catch { }
            _endpoint = null;
            _device = null;
        }

        // Called on a COM worker thread.
        private void OnVolumeNotification(AudioVolumeNotificationData data)
        {
            float level = data.MasterVolume;
            bool muted = data.Muted;
            _ui.BeginInvoke(new Action(() =>
            {
                bool changed = Math.Abs(level - Level) > 0.0005f || muted != Muted;
                Level = level;
                Muted = muted;
                if (changed)
                    Changed?.Invoke(level, muted, true);
            }));
        }

        public void SetLevel(float level)
        {
            if (_endpoint == null) return;
            try
            {
                _endpoint.MasterVolumeLevelScalar = Math.Clamp(level, 0f, 1f);
                if (_endpoint.Mute && level > 0)
                    _endpoint.Mute = false;
            }
            catch { }
        }

        public void ToggleMute()
        {
            if (_endpoint == null) return;
            try { _endpoint.Mute = !_endpoint.Mute; } catch { }
        }

        // ---- IMMNotificationClient (COM worker thread) ----

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            _ui.BeginInvoke(new Action(() =>
            {
                Attach();
                Changed?.Invoke(Level, Muted, false);
            }));
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

        public void Dispose()
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch { }
            Detach();
            _enumerator.Dispose();
        }
    }
}
