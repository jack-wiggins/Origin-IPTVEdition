using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Origin.Core.Diagnostics;
using Origin.Core.Settings;

namespace Origin.Playback
{
    public enum PlaybackState
    {
        Idle,
        Opening,
        Buffering,
        Playing,
        Paused,
        Stopped,
        Ended,
        Error,
    }

    public sealed class PlaybackStateEventArgs : EventArgs
    {
        public PlaybackStateEventArgs(PlaybackState state, float bufferPercent = 0)
        {
            State = state;
            BufferPercent = bufferPercent;
        }

        public PlaybackState State { get; }

        public float BufferPercent { get; }
    }

    /// <summary>
    /// Live TV playback through LibVLC, which handles MPEG-TS and HLS streams and uses DXVA2
    /// hardware decoding on Windows 7. Calls into LibVLC are queued onto a background thread
    /// because some of them (Stop in particular) block.
    /// </summary>
    public sealed class PlaybackService : IDisposable
    {
        private readonly SettingsService _settings;
        private readonly object _queueLock = new object();
        private Task _queue = Task.FromResult(0);
        private LibVLC _libVlc;
        private MediaPlayer _player;
        private Media _media;

        public PlaybackService(SettingsService settings)
        {
            _settings = settings;
        }

        /// <summary>Raised on a LibVLC thread.</summary>
        public event EventHandler<PlaybackStateEventArgs> StateChanged;

        public MediaPlayer Player => _player;

        /// <summary>Why playback is unavailable (e.g. LibVLC is missing), or null.</summary>
        public string UnavailableReason { get; private set; }

        public bool EnsureInitialized()
        {
            if (_player != null)
            {
                return true;
            }

            if (UnavailableReason != null)
            {
                return false;
            }

            try
            {
                // Loads libvlc from libvlc\win-x86 or libvlc\win-x64 next to the executable.
                LibVLCSharp.Shared.Core.Initialize();

                var playback = _settings.Current.Playback;
                var options = new List<string>
                {
                    "--no-video-title-show",
                    "--no-osd",
                    "--no-snapshot-preview",
                    "--no-stats",
                    "--http-reconnect",
                    "--network-caching=" + playback.NetworkCachingMs.ToString(CultureInfo.InvariantCulture),
                };

                if (!playback.HardwareDecoding)
                {
                    options.Add("--avcodec-hw=none");
                }

                _libVlc = new LibVLC(options.ToArray());
                _player = new MediaPlayer(_libVlc)
                {
                    EnableHardwareDecoding = playback.HardwareDecoding,
                    EnableKeyInput = false,
                    EnableMouseInput = false,
                };

                _player.Opening += (s, e) => Raise(PlaybackState.Opening);
                _player.Buffering += (s, e) => Raise(PlaybackState.Buffering, e.Cache);
                _player.Playing += (s, e) => Raise(PlaybackState.Playing);
                _player.Paused += (s, e) => Raise(PlaybackState.Paused);
                _player.Stopped += (s, e) => Raise(PlaybackState.Stopped);
                _player.EndReached += (s, e) => Raise(PlaybackState.Ended);
                _player.EncounteredError += (s, e) => Raise(PlaybackState.Error);

                Log.Info("LibVLC " + _libVlc.Version + " initialised");
                return true;
            }
            catch (Exception ex)
            {
                UnavailableReason = "The video player (LibVLC) could not be started: " + ex.GetBaseException().Message;
                Log.Error("LibVLC initialisation failed", ex);
                return false;
            }
        }

        /// <param name="url">The stream URL, including credentials. Never logged unsanitised.</param>
        /// <param name="playlistOptions">#EXTVLCOPT options from the playlist, e.g. "http-user-agent=...".</param>
        public void Play(string url, IEnumerable<string> playlistOptions)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            var userAgent = _settings.Current.Xtream.UserAgent;
            Enqueue(() =>
            {
                var media = new Media(_libVlc, url, FromType.FromLocation);
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    media.AddOption(":http-user-agent=" + userAgent.Trim());
                }

                if (playlistOptions != null)
                {
                    foreach (var option in playlistOptions)
                    {
                        if (!string.IsNullOrWhiteSpace(option))
                        {
                            media.AddOption(":" + option.Trim().TrimStart(':'));
                        }
                    }
                }

                var previous = _media;
                _media = media;
                Log.Info("Playing " + Log.Sanitize(url));
                _player.Play(media);
                previous?.Dispose();
            });
        }

        public void TogglePause()
        {
            Enqueue(() =>
            {
                if (_player.CanPause)
                {
                    _player.Pause();
                }
            });
        }

        public void SetPaused(bool paused)
        {
            Enqueue(() => _player.SetPause(paused));
        }

        public void Stop()
        {
            if (_player == null)
            {
                return;
            }

            Enqueue(() =>
            {
                _player.Stop();
                _media?.Dispose();
                _media = null;
            });
        }

        public void Dispose()
        {
            if (_player == null)
            {
                return;
            }

            var player = _player;
            var libVlc = _libVlc;
            _player = null;
            _libVlc = null;

            Task shutdown;
            lock (_queueLock)
            {
                shutdown = _queue.ContinueWith(_ =>
                {
                    player.Stop();
                    _media?.Dispose();
                    player.Dispose();
                    libVlc.Dispose();
                }, TaskScheduler.Default);
            }

            // Never let a stuck stream hold up closing the application.
            shutdown.Wait(TimeSpan.FromSeconds(3));
        }

        private void Enqueue(Action action)
        {
            lock (_queueLock)
            {
                _queue = _queue.ContinueWith(_ =>
                {
                    try
                    {
                        if (_player != null)
                        {
                            action();
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Playback command failed", ex);
                        Raise(PlaybackState.Error);
                    }
                }, TaskScheduler.Default);
            }
        }

        private void Raise(PlaybackState state, float buffer = 0)
        {
            StateChanged?.Invoke(this, new PlaybackStateEventArgs(state, buffer));
        }
    }
}
