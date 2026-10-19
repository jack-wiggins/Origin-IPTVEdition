using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;
using Origin.Playback;

namespace Origin.Views
{
    /// <summary>Full-screen live TV with a channel banner, channel up/down and direct number entry.</summary>
    public partial class PlayerPage : OriginPage
    {
        private const int MaxReconnects = 3;
        private const double DesignHeight = 1080;

        private readonly DispatcherTimer _bannerTimer;
        private readonly DispatcherTimer _reconnectTimer;
        private readonly ChannelNumberEntry _numberEntry;
        private readonly LogoConverter _logos = new LogoConverter { DecodeHeight = 120 };
        private readonly DoubleAnimation _spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };

        private OriginChannel _channel;
        private PlaybackState _state = PlaybackState.Idle;
        private int _reconnects;
        private bool _bannerShown;
        private bool _failed;
        private Window _overlayWindow;

        public PlayerPage(IShell shell, OriginChannel channel) : base(shell)
        {
            InitializeComponent();
            _channel = channel;

            _bannerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _bannerTimer.Tick += (s, e) => HideBanner();

            _reconnectTimer = new DispatcherTimer();
            _reconnectTimer.Tick += (s, e) =>
            {
                _reconnectTimer.Stop();
                StartStream();
            };

            _numberEntry = new ChannelNumberEntry(Dispatcher, NumberEntry, NumberText, number =>
            {
                var target = Services.Lineups.FindByNumber(number);
                if (target != null)
                {
                    Tune(target);
                }
            });

            Overlay.SizeChanged += (s, e) =>
            {
                var scale = Math.Max(0.3, Overlay.ActualHeight / DesignHeight);
                OverlayScale.ScaleX = scale;
                OverlayScale.ScaleY = scale;
            };
            Overlay.Loaded += (s, e) => HookOverlayWindow();

            // The overlay is a separate window, so it tracks its own mouse movement.
            var lastMouse = new Point(double.NaN, double.NaN);
            Overlay.MouseMove += (s, e) =>
            {
                var position = e.GetPosition(Overlay);
                if (position != lastMouse && !double.IsNaN(lastMouse.X))
                {
                    Mouse.OverrideCursor = null;
                    ShowBanner();
                }

                lastMouse = position;
            };
            Overlay.MouseLeftButtonUp += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    ToggleBanner();
                }
            };
            Overlay.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    Shell.ToggleFullScreen();
                }
            };
        }

        public override string Title => "live tv";

        public override bool IsFullWindow => true;

        public OriginChannel Channel => _channel;

        /// <summary>Sets the channel to show the next time this page is navigated to.</summary>
        public void SetChannel(OriginChannel channel)
        {
            _channel = channel;
        }

        public override void OnNavigatedTo(bool isBack)
        {
            Services.Playback.StateChanged += OnPlaybackStateChanged;

            if (!Services.Playback.EnsureInitialized())
            {
                ShowStatus(Services.Playback.UnavailableReason, busy: false);
                return;
            }

            if (Video.MediaPlayer == null)
            {
                Video.MediaPlayer = Services.Playback.Player;
            }

            Tune(_channel);
        }

        public override void OnNavigatedFrom()
        {
            Services.Playback.StateChanged -= OnPlaybackStateChanged;
            Services.Playback.Stop();
            _bannerTimer.Stop();
            _reconnectTimer.Stop();
            _numberEntry.Cancel();
            HideStatus();
        }

        public override void OnRemoved()
        {
            // Closes the overlay window and releases the video surface.
            Video.Dispose();
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            if (RemoteInput.IsDigit(command))
            {
                _numberEntry.Add(RemoteInput.ToDigit(command));
                return true;
            }

            switch (command)
            {
                case RemoteCommand.Ok when _numberEntry.IsActive:
                    _numberEntry.Commit();
                    return true;
                case RemoteCommand.Ok:
                case RemoteCommand.Info:
                    ToggleBanner();
                    return true;
                case RemoteCommand.ChannelUp:
                case RemoteCommand.Up:
                    Zap(1);
                    return true;
                case RemoteCommand.ChannelDown:
                case RemoteCommand.Down:
                    Zap(-1);
                    return true;
                case RemoteCommand.Left:
                case RemoteCommand.Right:
                    // Reserved for pause/rewind of live TV.
                    return true;
                case RemoteCommand.PlayPause:
                    Services.Playback.TogglePause();
                    return true;
                case RemoteCommand.Pause:
                    Services.Playback.SetPaused(true);
                    return true;
                case RemoteCommand.Play:
                    Services.Playback.SetPaused(false);
                    return true;
                case RemoteCommand.Stop:
                    Shell.GoBack();
                    return true;
                case RemoteCommand.Back when _numberEntry.IsActive:
                    _numberEntry.Cancel();
                    return true;
                case RemoteCommand.LiveTv:
                    ShowBanner();
                    return true;
                default:
                    return false;
            }
        }

        public void Tune(OriginChannel channel)
        {
            _channel = channel;
            _reconnects = 0;
            _reconnectTimer.Stop();

            var playback = Services.Settings.Current.Playback;
            if (playback.LastChannelKey != channel.Key)
            {
                playback.LastChannelKey = channel.Key;
                Services.Settings.Save();
            }

            UpdateBanner();
            ShowBanner();
            StartStream();
        }

        private void StartStream()
        {
            if (Services.Playback.Player == null)
            {
                return;
            }

            string url;
            try
            {
                url = _channel.Stream?.Resolve(Services.Settings.GetXtreamAccount());
            }
            catch (InvalidOperationException ex)
            {
                ShowStatus(ex.Message, busy: false);
                return;
            }

            if (string.IsNullOrEmpty(url))
            {
                ShowStatus("This channel has no stream address. Try updating the lineup.", busy: false);
                return;
            }

            _failed = false;
            ShowStatus(_reconnects == 0 ? "Tuning to " + _channel.Number + " " + _channel.Name + "…" : "Signal lost. Reconnecting…", busy: true);
            Services.Playback.Play(url, _channel.PlayerOptions);
        }

        private void Zap(int direction)
        {
            var channels = Services.Lineups.VisibleChannels;
            if (channels.Count == 0)
            {
                return;
            }

            var index = channels.ToList().FindIndex(c => c.Key == _channel?.Key);
            var next = index < 0 ? 0 : (index + direction + channels.Count) % channels.Count;
            Tune(channels[next]);
        }

        private void OnPlaybackStateChanged(object sender, PlaybackStateEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => ApplyState(e)));
        }

        private void ApplyState(PlaybackStateEventArgs e)
        {
            var previous = _state;
            _state = e.State;

            switch (e.State)
            {
                case PlaybackState.Opening:
                    break;
                case PlaybackState.Buffering:
                    if (e.BufferPercent >= 100)
                    {
                        HideStatus();
                    }
                    else if (previous != PlaybackState.Playing || e.BufferPercent < 50)
                    {
                        ShowStatus("Buffering " + (int)e.BufferPercent + "%", busy: true);
                    }

                    _state = previous == PlaybackState.Playing ? PlaybackState.Playing : PlaybackState.Buffering;
                    break;
                case PlaybackState.Playing:
                    _reconnects = 0;
                    HideStatus();
                    break;
                case PlaybackState.Paused:
                    ShowStatus("Paused", busy: false);
                    break;
                case PlaybackState.Ended:
                case PlaybackState.Error:
                    if (_failed)
                    {
                        break;
                    }

                    if (_reconnects < MaxReconnects)
                    {
                        _reconnects++;
                        ShowStatus("Signal lost. Reconnecting…", busy: true);
                        _reconnectTimer.Interval = TimeSpan.FromSeconds(2 * _reconnects);
                        _reconnectTimer.Start();
                    }
                    else
                    {
                        _failed = true;
                        ShowStatus("Channel " + _channel.Number + " isn't available right now. Try another channel, or check your provider account.", busy: false);
                        ShowBanner();
                    }

                    break;
            }
        }

        // ------------------------------------------------------------------ Overlay

        private void UpdateBanner()
        {
            if (_channel == null)
            {
                return;
            }

            BannerNumber.Text = _channel.Number;
            BannerName.Text = _channel.Name;
            BannerLogo.Source = _logos.Convert(_channel.DisplayLogoUrl, typeof(ImageSource), null, null) as ImageSource;

            var mapping = _channel.Mapping;
            BannerDetail.Text = mapping != null
                ? mapping.Callsign + (string.IsNullOrEmpty(mapping.Affiliate) ? string.Empty : " · " + mapping.Affiliate) + "   ·   " + _channel.Group
                : _channel.Group;
        }

        private void ShowBanner()
        {
            BannerClock.Text = DateTime.Now.ToString("t");
            if (!_bannerShown)
            {
                _bannerShown = true;
                Anim.To(Banner, OpacityProperty, 1, 220);
                Anim.To(BannerSlide, TranslateTransform.YProperty, 0, 380);
                Anim.To(BackButton, OpacityProperty, 1, 220);
            }

            _bannerTimer.Stop();
            _bannerTimer.Start();
        }

        private void HideBanner()
        {
            _bannerTimer.Stop();
            if (!_bannerShown)
            {
                return;
            }

            _bannerShown = false;
            Anim.To(Banner, OpacityProperty, 0, 350, Anim.EaseIn);
            Anim.To(BannerSlide, TranslateTransform.YProperty, 40, 350, Anim.EaseIn);
            Anim.To(BackButton, OpacityProperty, 0, 350, Anim.EaseIn);
        }

        private void ToggleBanner()
        {
            if (_bannerShown)
            {
                HideBanner();
            }
            else
            {
                ShowBanner();
            }
        }

        private void ShowStatus(string message, bool busy)
        {
            StatusText.Text = message;
            Spinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, busy ? _spin : null);
            Anim.To(StatusPanel, OpacityProperty, 1, 200);
        }

        private void HideStatus()
        {
            Anim.To(StatusPanel, OpacityProperty, 0, 300, completed: () =>
            {
                if (StatusPanel.Opacity < 0.01)
                {
                    SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                }
            });
        }

        /// <summary>
        /// The overlay lives in its own window. If it takes focus (e.g. after a click), hand
        /// focus back so the remote keeps working.
        /// </summary>
        private void HookOverlayWindow()
        {
            var window = Window.GetWindow(Overlay);
            if (window == null || window == _overlayWindow || window is MainWindow)
            {
                return;
            }

            _overlayWindow = window;
            window.PreviewKeyDown += (s, e) =>
            {
                var command = RemoteInput.FromKey(e, textEntry: false);
                if (command != RemoteCommand.None && Application.Current.MainWindow is MainWindow main && main.Dispatch(command))
                {
                    e.Handled = true;
                }
            };
            window.Activated += (s, e) => Application.Current.MainWindow?.Activate();
        }

        private void OnBackClick(object sender, RoutedEventArgs e)
        {
            Shell.GoBack();
        }
    }
}
