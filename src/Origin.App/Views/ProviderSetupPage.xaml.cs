using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Origin.Controls;
using Origin.Core.Lineup;
using Origin.Core.Security;
using Origin.Core.Xtream;
using Origin.Infrastructure;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>Settings › iptv provider: the Xtream Codes server, username and password.</summary>
    public partial class ProviderSetupPage : OriginPage
    {
        private readonly ProviderSetupViewModel _model;

        public ProviderSetupPage(IShell shell) : base(shell)
        {
            InitializeComponent();
            _model = new ProviderSetupViewModel(shell, () => PasswordBox.Password, () => PasswordBox.Clear());
            DataContext = _model;
        }

        public override string Title => "iptv provider";

        public override void FocusDefault()
        {
            ServerBox.Focus();
            ServerBox.CaretIndex = ServerBox.Text.Length;
        }

        public override void OnNavigatedFrom()
        {
            _model.Cancel();
        }

        /// <summary>Pasting a whole get.php link fills in all three fields.</summary>
        private void OnServerLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (XtreamUrls.TryExtractCredentials(ServerBox.Text, out var server, out var username, out var password))
            {
                _model.ServerUrl = server;
                _model.Username = username;
                PasswordBox.Password = password;
                Shell.ShowToast("Server, username and password were read from the link.", ToastKind.Success);
            }
        }
    }

    public sealed class ProviderSetupViewModel : ObservableObject
    {
        private readonly IShell _shell;
        private readonly Func<string> _readPassword;
        private readonly Action _clearPassword;
        private CancellationTokenSource _cancel;
        private string _serverUrl;
        private string _username;
        private string _userAgent;
        private Choice _selectedFormat;
        private string _statusTitle;
        private string _statusDetail;
        private Brush _statusBrush;
        private bool _hasSavedPassword;

        public ProviderSetupViewModel(IShell shell, Func<string> readPassword, Action clearPassword)
        {
            _shell = shell;
            _readPassword = readPassword;
            _clearPassword = clearPassword;

            Formats = new List<Choice>
            {
                new Choice("MPEG-TS (.ts)", "ts"),
                new Choice("HLS (.m3u8)", "m3u8"),
            };

            var settings = shell.Services.Settings.Current.Xtream;
            _serverUrl = settings.ServerUrl;
            _username = settings.Username;
            _userAgent = settings.UserAgent;
            _selectedFormat = Formats.FirstOrDefault(f => (string)f.Value == settings.OutputFormat) ?? Formats[0];
            _hasSavedPassword = !string.IsNullOrEmpty(settings.ProtectedPassword);

            TestCommand = new AsyncCommand(TestAsync);
            SaveCommand = new RelayCommand(() => Save(updateLineup: false));
            SaveAndUpdateCommand = new RelayCommand(() => Save(updateLineup: true));

            if (settings.IsConfigured)
            {
                SetStatus("Saved", "Choose test connection to check the account with " + SafeHost(settings.ServerUrl) + ".", "Brush.Text");
            }
            else
            {
                SetStatus("Not set up", "Your provider's details usually arrive by email as a server URL, a username and a password — or as a single M3U link, which you can paste into the server box.", "Brush.TextSecondary");
            }
        }

        public IReadOnlyList<Choice> Formats { get; }

        public ICommand TestCommand { get; }

        public ICommand SaveCommand { get; }

        public ICommand SaveAndUpdateCommand { get; }

        public string ServerUrl
        {
            get => _serverUrl;
            set => Set(ref _serverUrl, value);
        }

        public string Username
        {
            get => _username;
            set => Set(ref _username, value);
        }

        public string UserAgent
        {
            get => _userAgent;
            set => Set(ref _userAgent, value);
        }

        public Choice SelectedFormat
        {
            get => _selectedFormat;
            set => Set(ref _selectedFormat, value);
        }

        public string PasswordLabel => _hasSavedPassword ? "PASSWORD  ·  SAVED, LEAVE BLANK TO KEEP" : "PASSWORD";

        public string StatusTitle
        {
            get => _statusTitle;
            private set => Set(ref _statusTitle, value);
        }

        public string StatusDetail
        {
            get => _statusDetail;
            private set => Set(ref _statusDetail, value);
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            private set => Set(ref _statusBrush, value);
        }

        public void Cancel()
        {
            _cancel?.Cancel();
        }

        private async Task TestAsync()
        {
            if (!TryBuildAccount(out var account))
            {
                return;
            }

            _cancel?.Cancel();
            _cancel = new CancellationTokenSource();
            SetStatus("Checking…", "Contacting " + account.Host, "Brush.Text");

            try
            {
                XtreamAccountInfo info;
                using (var client = new XtreamClient(UserAgent))
                {
                    info = await client.GetAccountInfoAsync(account, _cancel.Token);
                }

                var detail = new StringBuilder();
                detail.AppendLine("Status: " + (info.Status ?? "unknown") + (info.IsTrial ? " (trial)" : string.Empty));
                detail.AppendLine(info.ExpiresUtc.HasValue
                    ? "Expires: " + info.ExpiresUtc.Value.ToLocalTime().ToString("D")
                    : "Expires: never");
                if (info.MaxConnections.HasValue)
                {
                    detail.AppendLine("Connections: " + (info.ActiveConnections ?? 0) + " of " + info.MaxConnections + " in use");
                }

                var format = (string)SelectedFormat.Value;
                if (info.AllowedOutputFormats.Count > 0)
                {
                    detail.AppendLine("Formats: " + string.Join(", ", info.AllowedOutputFormats));
                    if (!info.AllowedOutputFormats.Contains(format, StringComparer.OrdinalIgnoreCase))
                    {
                        detail.AppendLine("Your provider doesn't list " + format + " — pick another stream format.");
                    }
                }

                SetStatus(info.IsActive ? "Connected" : "Account " + (info.Status ?? "inactive").ToLowerInvariant(),
                    detail.ToString().TrimEnd(), info.IsActive ? "Brush.Success" : "Brush.Warning");
            }
            catch (XtreamException ex)
            {
                SetStatus("Couldn't connect", ex.Message, "Brush.Error");
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void Save(bool updateLineup)
        {
            if (!TryBuildAccount(out var account))
            {
                return;
            }

            var services = _shell.Services;
            var settings = services.Settings.Current.Xtream;
            var serverChanged = !string.Equals(settings.ServerUrl, account.ServerUrl, StringComparison.OrdinalIgnoreCase)
                                || !string.Equals(settings.Username, account.Username, StringComparison.Ordinal);

            settings.ServerUrl = account.ServerUrl;
            settings.Username = account.Username;
            settings.OutputFormat = account.OutputFormat;
            settings.UserAgent = string.IsNullOrWhiteSpace(UserAgent) ? null : UserAgent.Trim();

            var typed = _readPassword();
            if (!string.IsNullOrEmpty(typed))
            {
                services.Settings.SetXtreamPassword(typed);
            }

            services.Settings.Save();

            _clearPassword();
            _hasSavedPassword = true;
            OnPropertyChanged(nameof(PasswordLabel));
            ServerUrl = account.ServerUrl;

            if (updateLineup)
            {
                _shell.Navigate(new LineupUpdatePage(_shell, new LineupUpdateOptions()));
            }
            else
            {
                _shell.ShowToast(serverChanged
                    ? "IPTV provider saved. Choose tasks › update lineup to download your channels."
                    : "IPTV provider saved.", ToastKind.Success);
            }
        }

        private bool TryBuildAccount(out XtreamAccount account)
        {
            account = null;
            var password = _readPassword();
            if (string.IsNullOrEmpty(password) && _hasSavedPassword)
            {
                password = SecretProtector.Unprotect(_shell.Services.Settings.Current.Xtream.ProtectedPassword);
            }

            if (string.IsNullOrWhiteSpace(Username))
            {
                SetStatus("Username needed", "Enter the username from your provider.", "Brush.Warning");
                return false;
            }

            if (string.IsNullOrEmpty(password))
            {
                SetStatus("Password needed", "Enter the password from your provider.", "Brush.Warning");
                return false;
            }

            try
            {
                account = new XtreamAccount(ServerUrl, Username, password, (string)SelectedFormat.Value);
                return true;
            }
            catch (FormatException ex)
            {
                SetStatus("Check the server URL", ex.Message, "Brush.Warning");
                return false;
            }
        }

        private void SetStatus(string title, string detail, string brushKey)
        {
            StatusTitle = title;
            StatusDetail = detail;
            StatusBrush = (Brush)Application.Current.FindResource(brushKey);
        }

        private static string SafeHost(string url)
        {
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;
        }
    }
}
