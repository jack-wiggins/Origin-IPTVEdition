using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Origin.Core.Diagnostics;
using Origin.Core.Net;
using Origin.Core.Storage;
using Origin.Infrastructure;

namespace Origin
{
    public partial class App : Application
    {
        private const string InstanceMutexName = @"Local\Origin.HTPC.SingleInstance";

        private Mutex _instanceMutex;
        private bool _ownsMutex;
        private AppServices _services;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _instanceMutex = new Mutex(true, InstanceMutexName, out _ownsMutex);
            if (!_ownsMutex)
            {
                ActivateRunningInstance();
                Shutdown();
                return;
            }

            var paths = AppPaths.CreateDefault();
            Log.Initialize(paths.Logs);
            Log.Info("Origin " + Assembly.GetExecutingAssembly().GetName().Version + " starting on " + Environment.OSVersion
                     + (Environment.Is64BitProcess ? " (64-bit)" : " (32-bit)") + ", CLR " + Environment.Version);

            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += (s, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                Log.Warn("Unobserved task exception", args.Exception);
                args.SetObserved();
            };

            OriginHttp.Initialize();
            RegisterHoverFocus();

            _services = new AppServices(paths);

            var windowed = e.Args.Any(a => string.Equals(a, "--windowed", StringComparison.OrdinalIgnoreCase));
            var window = new MainWindow(_services, startFullScreen: !windowed && _services.Settings.Current.Interface.StartFullScreen);
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _services?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Error while shutting down", ex);
            }

            if (_ownsMutex)
            {
                _instanceMutex.ReleaseMutex();
            }

            _instanceMutex?.Dispose();
            Log.Info("Origin closed");
            base.OnExit(e);
        }

        /// <summary>
        /// Pointing at a button or list item focuses it, as in Media Center, so mouse and remote
        /// always agree on what is highlighted. Only real mouse movement counts; content
        /// scrolling under a resting cursor must not steal focus.
        /// </summary>
        private static void RegisterHoverFocus()
        {
            void OnHover(object sender, MouseEventArgs args)
            {
                if (!MouseActivity.MovedRecently || !(sender is UIElement element) || !element.Focusable || !element.IsEnabled)
                {
                    return;
                }

                element.Focus();
                if (sender is ListBoxItem item)
                {
                    item.IsSelected = true;
                }
            }

            EventManager.RegisterClassHandler(typeof(ButtonBase), UIElement.MouseEnterEvent, new MouseEventHandler(OnHover));
            EventManager.RegisterClassHandler(typeof(ListBoxItem), UIElement.MouseEnterEvent, new MouseEventHandler(OnHover));

            // Arrowing onto a list lands on its selected item rather than on the list itself.
            EventManager.RegisterClassHandler(typeof(ListBox), UIElement.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler((sender, args) =>
            {
                var list = (ListBox)sender;
                if (args.NewFocus != list || list.Items.Count == 0)
                {
                    return;
                }

                var index = Math.Max(0, list.SelectedIndex);
                list.ScrollIntoView(list.Items[index]);
                list.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    (list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
                }));
            }));
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;

            if (MainWindow is MainWindow window)
            {
                window.ShowToast("Something went wrong: " + e.Exception.GetBaseException().Message, Navigation.ToastKind.Error);
            }
        }

        private static void ActivateRunningInstance()
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(process.MainWindowHandle, 9 /* SW_RESTORE */);
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
