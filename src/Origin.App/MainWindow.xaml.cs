using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;
using Origin.Views;

namespace Origin
{
    /// <summary>
    /// The one window: hosts pages with animated transitions, turns keyboard, remote and mouse
    /// input into <see cref="RemoteCommand"/>s and shows the clock, toasts and mouse controls.
    /// </summary>
    public partial class MainWindow : Window, IShell
    {
        private const double DesignHeight = 1080;
        private const double SlideDistance = 90;

        private readonly List<OriginPage> _stack = new List<OriginPage>();
        private readonly DispatcherTimer _clockTimer;
        private readonly DispatcherTimer _chromeTimer;
        private Point _lastMousePosition = new Point(double.NaN, double.NaN);
        private bool _isFullScreen;

        public MainWindow(AppServices services, bool startFullScreen)
        {
            Services = services;
            InitializeComponent();

            Backdrop.Quality = services.Settings.Current.Interface.Background;

            _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (s, e) => UpdateClock(), Dispatcher);
            _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _chromeTimer.Tick += (s, e) => HideChrome();

            Root.SizeChanged += (s, e) => UpdateScale();
            StateChanged += (s, e) => UpdateBackdrop();
            Loaded += (s, e) =>
            {
                UpdateClock();
                Navigate(new StartPage(this));
            };

            SetFullScreen(startFullScreen);
        }

        public AppServices Services { get; }

        public bool CanGoBack => _stack.Count > 1;

        private OriginPage Current => _stack.Count > 0 ? _stack[_stack.Count - 1] : null;

        // ------------------------------------------------------------------ Navigation

        public void Navigate(OriginPage page)
        {
            var from = Current;
            _stack.Add(page);
            Transition(from, page, isBack: false);
        }

        public void Replace(OriginPage page)
        {
            var from = Current;
            if (from != null && _stack.Count > 1)
            {
                _stack.RemoveAt(_stack.Count - 1);
            }

            _stack.Add(page);
            Transition(from, page, isBack: false);

            if (from != null && !_stack.Contains(from))
            {
                from.OnRemoved();
            }
        }

        public void GoBack()
        {
            if (!CanGoBack)
            {
                return;
            }

            var from = Current;
            _stack.RemoveAt(_stack.Count - 1);
            Transition(from, Current, isBack: true);
            from.OnRemoved();
        }

        public void GoHome()
        {
            if (!CanGoBack)
            {
                return;
            }

            var from = Current;
            var removed = _stack.Skip(1).ToList();
            _stack.RemoveRange(1, _stack.Count - 1);
            Transition(from, Current, isBack: true);
            foreach (var page in removed)
            {
                page.OnRemoved();
                if (page != from)
                {
                    RemoveFromHost(page);
                }
            }
        }

        public void PlayChannel(string channelKey)
        {
            var lineups = Services.Lineups;
            var channel = lineups.FindByKey(channelKey);
            if (channel == null || !channel.Enabled)
            {
                channel = lineups.VisibleChannels.FirstOrDefault();
            }

            if (channel == null)
            {
                ShowToast("There are no channels yet. Set up your IPTV provider, then choose Tasks › update lineup.", ToastKind.Warning);
                return;
            }

            if (Current is PlayerPage player)
            {
                player.Tune(channel);
                return;
            }

            // Guide → Live TV → Guide → another channel returns to the same player instead of stacking a new one.
            var existing = _stack.OfType<PlayerPage>().LastOrDefault();
            if (existing != null)
            {
                existing.SetChannel(channel);
                PopTo(existing);
                return;
            }

            Navigate(new PlayerPage(this, channel));
        }

        private void PopTo(OriginPage page)
        {
            var index = _stack.IndexOf(page);
            if (index < 0 || index == _stack.Count - 1)
            {
                return;
            }

            var from = Current;
            var removed = _stack.Skip(index + 1).ToList();
            _stack.RemoveRange(index + 1, _stack.Count - index - 1);
            Transition(from, page, isBack: true);
            foreach (var gone in removed)
            {
                gone.OnRemoved();
                if (gone != from)
                {
                    RemoveFromHost(gone);
                }
            }
        }

        private void Transition(OriginPage from, OriginPage to, bool isBack)
        {
            if (from != null && from != to)
            {
                from.OnNavigatedFrom();
                AnimateOut(from, isBack);
            }

            var host = to.IsFullWindow ? FullWindowHost : PageHost;
            if (!host.Children.Contains(to))
            {
                host.Children.Add(to);
            }

            to.Visibility = Visibility.Visible;
            to.IsHitTestVisible = true;
            Scaled.Visibility = to.IsFullWindow ? Visibility.Collapsed : Visibility.Visible;
            UpdateBackdrop(to);

            PageTitle.Text = to.Title;
            Anim.FromTo(PageTitle, OpacityProperty, 0, 0.92, 450);

            if (to.IsFullWindow)
            {
                Anim.Set(to, OpacityProperty, 1);
            }
            else
            {
                var slide = EnsureTranslate(to);
                Anim.FromTo(to, OpacityProperty, 0, 1, 320);
                Anim.FromTo(slide, TranslateTransform.XProperty, isBack ? -SlideDistance : SlideDistance, 0, 450);
            }

            to.OnNavigatedTo(isBack);
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                if (Current == to)
                {
                    to.FocusDefault();
                }
            }));
        }

        private void AnimateOut(OriginPage page, bool isBack)
        {
            page.IsHitTestVisible = false;

            void Finish()
            {
                if (Current == page)
                {
                    return;
                }

                page.Visibility = Visibility.Collapsed;
                if (!_stack.Contains(page))
                {
                    RemoveFromHost(page);
                }
            }

            if (page.IsFullWindow)
            {
                Finish();
                return;
            }

            var slide = EnsureTranslate(page);
            Anim.To(page, OpacityProperty, 0, 220, Anim.EaseIn);
            Anim.To(slide, TranslateTransform.XProperty, isBack ? SlideDistance : -SlideDistance, 260, Anim.EaseIn, Finish);
        }

        private void RemoveFromHost(OriginPage page)
        {
            PageHost.Children.Remove(page);
            FullWindowHost.Children.Remove(page);
        }

        private static TranslateTransform EnsureTranslate(UIElement element)
        {
            if (!(element.RenderTransform is TranslateTransform translate) || translate.IsFrozen)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            return translate;
        }

        // ------------------------------------------------------------------ Input

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            HideChrome();
            if (e.Handled)
            {
                return;
            }

            var focused = Keyboard.FocusedElement;
            var textEntry = focused is TextBoxBase || focused is PasswordBox;
            var command = RemoteInput.FromKey(e, textEntry);
            if (command != RemoteCommand.None && Dispatch(command, textEntry))
            {
                e.Handled = true;
            }
        }

        /// <summary>Lets other windows (the video overlay) forward input here.</summary>
        public bool Dispatch(RemoteCommand command, bool textEntry = false)
        {
            if (command == RemoteCommand.FullScreen)
            {
                ToggleFullScreen();
                return true;
            }

            var page = Current;
            if (page != null && page.HandleCommand(command))
            {
                return true;
            }

            switch (command)
            {
                case RemoteCommand.Back when CanGoBack:
                    GoBack();
                    return true;
                case RemoteCommand.Home:
                    GoHome();
                    return true;
                case RemoteCommand.Guide when !(page is GuidePage):
                    Navigate(new GuidePage(this));
                    return true;
                case RemoteCommand.LiveTv:
                    PlayChannel(Services.Settings.Current.Playback.LastChannelKey);
                    return true;
                case RemoteCommand.Ok when textEntry:
                    // Enter in a text box moves on to the next field, as on a set-top box keyboard.
                    (Keyboard.FocusedElement as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    return true;
            }

            return false;
        }

        private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == RemoteInput.WM_APPCOMMAND)
            {
                var command = RemoteInput.FromAppCommand(RemoteInput.GetAppCommand(lParam.ToInt64()));
                if (command != RemoteCommand.None && Dispatch(command))
                {
                    handled = true;
                    return new IntPtr(1);
                }
            }

            return IntPtr.Zero;
        }

        protected override void OnPreviewMouseMove(MouseEventArgs e)
        {
            base.OnPreviewMouseMove(e);
            var position = e.GetPosition(this);
            if (position == _lastMousePosition)
            {
                return;
            }

            var firstMove = double.IsNaN(_lastMousePosition.X);
            _lastMousePosition = position;
            if (!firstMove)
            {
                MouseActivity.NoteMove();
                ShowChrome();
            }
        }

        protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
        {
            base.OnPreviewMouseDown(e);
            if (e.ChangedButton == MouseButton.XButton1 && Dispatch(RemoteCommand.Back))
            {
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ Window

        public void ToggleFullScreen() => SetFullScreen(!_isFullScreen);

        public void Minimize() => WindowState = WindowState.Minimized;

        public void Exit() => Close();

        private void SetFullScreen(bool fullScreen)
        {
            _isFullScreen = fullScreen;
            if (fullScreen)
            {
                // Going via Normal makes Windows recalculate the maximised bounds to cover the taskbar.
                WindowState = WindowState.Normal;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Maximized;
            }
            else
            {
                WindowStyle = WindowStyle.SingleBorderWindow;
                ResizeMode = ResizeMode.CanResize;
                WindowState = WindowState.Normal;
                Mouse.OverrideCursor = null;
            }
        }

        private void UpdateScale()
        {
            var scale = Math.Max(0.3, Root.ActualHeight / DesignHeight);
            UiScale.ScaleX = scale;
            UiScale.ScaleY = scale;
        }

        private void UpdateBackdrop(OriginPage page = null)
        {
            page = page ?? Current;
            Backdrop.IsPaused = WindowState == WindowState.Minimized || (page?.IsFullWindow ?? false);
        }

        private void UpdateClock()
        {
            ClockText.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        }

        private void ShowChrome()
        {
            Mouse.OverrideCursor = null;
            if (Current?.IsFullWindow != true)
            {
                ChromeBack.Visibility = CanGoBack ? Visibility.Visible : Visibility.Collapsed;
                MouseChrome.Visibility = Visibility.Visible;
                Anim.To(MouseChrome, OpacityProperty, 1, 150);
            }

            _chromeTimer.Stop();
            _chromeTimer.Start();
        }

        private void HideChrome()
        {
            _chromeTimer.Stop();
            if (MouseChrome.Visibility == Visibility.Visible)
            {
                Anim.To(MouseChrome, OpacityProperty, 0, 400, completed: () =>
                {
                    if (MouseChrome.Opacity < 0.01)
                    {
                        MouseChrome.Visibility = Visibility.Collapsed;
                    }
                });
            }

            if (_isFullScreen && IsActive && !MouseChrome.IsMouseOver)
            {
                Mouse.OverrideCursor = Cursors.None;
            }
        }

        private void OnChromeBack(object sender, RoutedEventArgs e) => Dispatch(RemoteCommand.Back);

        private void OnChromeHome(object sender, RoutedEventArgs e) => GoHome();

        private void OnChromeMinimize(object sender, RoutedEventArgs e) => Minimize();

        private void OnChromeFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();

        private void OnChromeClose(object sender, RoutedEventArgs e) => Exit();

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            Mouse.OverrideCursor = null;
        }

        // ------------------------------------------------------------------ Toasts

        public void ShowToast(string message, ToastKind kind = ToastKind.Info)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => ShowToast(message, kind)));
                return;
            }

            Brush accent;
            switch (kind)
            {
                case ToastKind.Success:
                    accent = (Brush)FindResource("Brush.Success");
                    break;
                case ToastKind.Warning:
                    accent = (Brush)FindResource("Brush.Warning");
                    break;
                case ToastKind.Error:
                    accent = (Brush)FindResource("Brush.Error");
                    break;
                default:
                    accent = (Brush)FindResource("Brush.Accent");
                    break;
            }

            var slide = new TranslateTransform(80, 0);
            var toast = new Border
            {
                Background = (Brush)FindResource("Brush.PanelStrong"),
                BorderBrush = accent,
                BorderThickness = new Thickness(5, 0, 0, 0),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(28, 18, 30, 18),
                Margin = new Thickness(0, 12, 0, 0),
                MaxWidth = 720,
                HorizontalAlignment = HorizontalAlignment.Right,
                Opacity = 0,
                RenderTransform = slide,
                Child = new TextBlock { Text = message, FontSize = 24, TextWrapping = TextWrapping.Wrap },
            };

            while (ToastHost.Children.Count >= 3)
            {
                ToastHost.Children.RemoveAt(0);
            }

            ToastHost.Children.Add(toast);
            Anim.To(toast, OpacityProperty, 1, 250);
            Anim.To(slide, TranslateTransform.XProperty, 0, 380);

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Min(10, 4 + message.Length / 40.0)) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                Anim.To(toast, OpacityProperty, 0, 400, Anim.EaseIn, () => ToastHost.Children.Remove(toast));
            };
            timer.Start();
        }
    }
}
