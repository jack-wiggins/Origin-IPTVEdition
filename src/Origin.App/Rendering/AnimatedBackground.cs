using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Origin.Core.Diagnostics;
using Origin.Core.Settings;

namespace Origin.Rendering
{
    /// <summary>
    /// Hosts the DirectX ribbon background. Falls back to a plain gradient when Direct3D 9Ex is
    /// unavailable (no WDDM driver, some Remote Desktop sessions) or the quality setting is Off.
    /// </summary>
    public sealed class AnimatedBackground : Grid
    {
        private const int MaxRestartAttempts = 3;

        private readonly Image _image;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private D3DImage _surface;
        private RibbonRenderer _renderer;
        private TimeSpan _lastFrame;
        private bool _rendering;
        private bool _sizeDirty = true;
        private bool _paused;
        private int _restarts;
        private BackgroundQuality _quality = BackgroundQuality.Balanced;

        public AnimatedBackground()
        {
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(0x0B, 0x34, 0x78), 0),
                    new GradientStop(Color.FromRgb(0x05, 0x1A, 0x44), 0.55),
                    new GradientStop(Color.FromRgb(0x02, 0x0A, 0x1E), 1),
                },
                new Point(0.2, 0),
                new Point(0.8, 1));

            _image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.Linear);
            Children.Add(_image);

            IsHitTestVisible = false;
            Loaded += (s, e) => Start();
            Unloaded += (s, e) => Stop();
            SizeChanged += (s, e) => _sizeDirty = true;
        }

        /// <summary>True while the GPU renders the ribbons; false when the static fallback is showing.</summary>
        public bool IsDirectXActive => _renderer != null;

        public BackgroundQuality Quality
        {
            get => _quality;
            set
            {
                if (_quality == value)
                {
                    return;
                }

                _quality = value;
                _sizeDirty = true;
                if (IsLoaded)
                {
                    Stop();
                    Start();
                }
            }
        }

        /// <summary>Pause while video covers the screen or the window is minimised.</summary>
        public bool IsPaused
        {
            get => _paused;
            set
            {
                _paused = value;
                _image.Visibility = value ? Visibility.Hidden : Visibility.Visible;
            }
        }

        private void Start()
        {
            if (_quality == BackgroundQuality.Off || _renderer != null)
            {
                return;
            }

            try
            {
                _renderer = new RibbonRenderer();
                _renderer.Initialize();

                _surface = new D3DImage();
                _surface.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
                _image.Source = _surface;
                _sizeDirty = true;

                if (!_rendering)
                {
                    CompositionTarget.Rendering += OnRendering;
                    _rendering = true;
                }

                Log.Info("DirectX background started (Direct3D 9Ex).");
            }
            catch (Exception ex)
            {
                Log.Warn("DirectX background unavailable; using the static background.", ex);
                Stop();
            }
        }

        private void Stop()
        {
            if (_rendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                _rendering = false;
            }

            if (_surface != null)
            {
                _surface.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
                _surface = null;
            }

            _image.Source = null;
            _renderer?.Dispose();
            _renderer = null;
        }

        private void OnFrontBufferAvailableChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // The front buffer goes away on lock screen, UAC prompts, display mode changes and
            // driver resets. Rebuild the device when it comes back.
            if (_surface != null && _surface.IsFrontBufferAvailable)
            {
                Restart(afterFailure: false);
            }
        }

        private void OnRendering(object sender, EventArgs e)
        {
            // Rendering can fire more than once per frame; only draw once.
            var renderingTime = ((RenderingEventArgs)e).RenderingTime;
            if (renderingTime == _lastFrame)
            {
                return;
            }

            _lastFrame = renderingTime;

            if (_paused || _renderer == null || _surface == null || !_surface.IsFrontBufferAvailable)
            {
                return;
            }

            try
            {
                if (_sizeDirty && !UpdateSize())
                {
                    return;
                }

                if (!_renderer.CheckDevice())
                {
                    Log.Warn("Direct3D device lost; recreating it.");
                    Restart(afterFailure: true);
                    return;
                }

                _surface.Lock();
                try
                {
                    _renderer.Render(_clock.Elapsed.TotalSeconds);
                    _surface.AddDirtyRect(new Int32Rect(0, 0, _surface.PixelWidth, _surface.PixelHeight));
                }
                finally
                {
                    _surface.Unlock();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("DirectX background frame failed.", ex);
                Restart(afterFailure: true);
            }
        }

        private bool UpdateSize()
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null || ActualWidth < 1 || ActualHeight < 1)
            {
                return false;
            }

            var dpi = source.CompositionTarget.TransformToDevice;
            var width = ActualWidth * dpi.M11;
            var height = ActualHeight * dpi.M22;

            // The ribbons are soft, so render below native resolution and let WPF scale.
            var maxWidth = _quality == BackgroundQuality.High ? 1920.0 : 1280.0;
            if (width > maxWidth)
            {
                height *= maxWidth / width;
                width = maxWidth;
            }

            _renderer.SetSize(Math.Max(64, (int)width), Math.Max(64, (int)height));

            _surface.Lock();
            try
            {
                // enableSoftwareFallback keeps the background working over Remote Desktop.
                _surface.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderer.SurfacePointer, true);
            }
            finally
            {
                _surface.Unlock();
            }

            _sizeDirty = false;
            return true;
        }

        /// <param name="afterFailure">Device errors count towards giving up; lock-screen and display changes do not.</param>
        private void Restart(bool afterFailure)
        {
            Stop();
            if (afterFailure && ++_restarts > MaxRestartAttempts)
            {
                Log.Warn("DirectX background failed repeatedly; staying on the static background.");
                return;
            }

            Start();
        }
    }
}
