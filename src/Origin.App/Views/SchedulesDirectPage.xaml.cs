using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Origin.Core.Diagnostics;
using Origin.Core.Lineup;
using Origin.Core.SchedulesDirect;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>Settings › schedules direct: sign in, manage lineups and choose the one Origin maps onto.</summary>
    public partial class SchedulesDirectPage : OriginPage
    {
        private readonly SchedulesDirectViewModel _model;

        public SchedulesDirectPage(IShell shell) : base(shell)
        {
            InitializeComponent();
            _model = new SchedulesDirectViewModel(shell, () => PasswordBox.Password, () => PasswordBox.Clear());
            DataContext = _model;
        }

        public override string Title => "schedules direct";

        public override void OnNavigatedTo(bool isBack)
        {
            if (!isBack)
            {
                _model.LoadAsync();
            }
        }

        public override void OnNavigatedFrom()
        {
            _model.Cancel();
        }

        public override void FocusDefault()
        {
            if (string.IsNullOrEmpty(UserBox.Text))
            {
                UserBox.Focus();
            }
            else
            {
                FocusList(AccountList);
            }
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            return LeaveListInDirection(command);
        }
    }

    public sealed class LineupItem : ObservableObject
    {
        private bool _isActive;

        public LineupItem(string id, string name, string location, string transport)
        {
            Id = id;
            Name = string.IsNullOrWhiteSpace(name) ? id : name;
            Location = location;
            Transport = transport;
        }

        public string Id { get; }

        public string Name { get; }

        public string Location { get; }

        public string Transport { get; }

        public string Detail => string.Join("  ·  ", new[] { Location, Transport, Id }.Where(s => !string.IsNullOrWhiteSpace(s)));

        public bool IsActive
        {
            get => _isActive;
            set => Set(ref _isActive, value);
        }
    }

    public sealed class SchedulesDirectViewModel : ObservableObject
    {
        private static readonly SdCountry[] FallbackCountries =
        {
            new SdCountry { FullName = "United States", ShortName = "USA", PostalCodeExample = "60030" },
            new SdCountry { FullName = "Canada", ShortName = "CAN", PostalCodeExample = "N7L4G3" },
        };

        private readonly IShell _shell;
        private readonly Func<string> _readPassword;
        private readonly Action _clearPassword;
        private CancellationTokenSource _cancel = new CancellationTokenSource();
        private string _username;
        private string _accountText;
        private IReadOnlyList<SdCountry> _countries = FallbackCountries;
        private SdCountry _selectedCountry;
        private string _postalCode;
        private LineupItem _selectedAccountLineup;
        private LineupItem _selectedSearchResult;
        private string _statusText;
        private Brush _statusBrush;
        private bool _showUpdatePrompt;
        private bool _signedIn;
        private bool _busy;
        private string _pendingRemoveId;
        private string _searchHint = "Choose your country, enter a postal code and search.";

        public SchedulesDirectViewModel(IShell shell, Func<string> readPassword, Action clearPassword)
        {
            _shell = shell;
            _readPassword = readPassword;
            _clearPassword = clearPassword;

            var settings = Settings;
            _username = settings.Username;
            _postalCode = settings.PostalCode;
            _selectedCountry = FindCountry(settings.Country);
            _accountText = settings.IsConfigured ? "Saved account. Signing in…" : "Not signed in.";

            SignInCommand = new AsyncCommand(() => SignInAsync(explicitly: true), () => !_busy);
            SearchCommand = new AsyncCommand(SearchAsync, () => !_busy && _signedIn);
            AddCommand = new AsyncCommand(AddAsync, () => !_busy && _signedIn && SelectedSearchResult != null);
            RemoveCommand = new AsyncCommand(RemoveAsync, () => !_busy && _signedIn && SelectedAccountLineup != null);
            UseCommand = new RelayCommand(Use, () => SelectedAccountLineup != null);
            UpdateNowCommand = new RelayCommand(() => _shell.Navigate(new LineupUpdatePage(_shell, new LineupUpdateOptions())));
        }

        public ICommand SignInCommand { get; }

        public ICommand SearchCommand { get; }

        public ICommand AddCommand { get; }

        public ICommand RemoveCommand { get; }

        public ICommand UseCommand { get; }

        public ICommand UpdateNowCommand { get; }

        public ObservableCollection<LineupItem> AccountLineups { get; } = new ObservableCollection<LineupItem>();

        public ObservableCollection<LineupItem> SearchResults { get; } = new ObservableCollection<LineupItem>();

        public string Username
        {
            get => _username;
            set => Set(ref _username, value);
        }

        public string PasswordLabel => Settings.IsConfigured ? "PASSWORD  ·  SAVED, LEAVE BLANK TO KEEP" : "PASSWORD";

        public string AccountText
        {
            get => _accountText;
            private set => Set(ref _accountText, value);
        }

        public IReadOnlyList<SdCountry> Countries
        {
            get => _countries;
            private set => Set(ref _countries, value);
        }

        public SdCountry SelectedCountry
        {
            get => _selectedCountry;
            set
            {
                if (Set(ref _selectedCountry, value))
                {
                    OnPropertyChanged(nameof(PostalCodeHint));
                }
            }
        }

        public string PostalCode
        {
            get => _postalCode;
            set => Set(ref _postalCode, value);
        }

        public string PostalCodeHint => SelectedCountry?.PostalCodeExample != null
            ? "postal code, e.g. " + SelectedCountry.PostalCodeExample
            : "postal code";

        public LineupItem SelectedAccountLineup
        {
            get => _selectedAccountLineup;
            set
            {
                if (Set(ref _selectedAccountLineup, value))
                {
                    _pendingRemoveId = null;
                    OnPropertyChanged(nameof(RemoveLabel));
                }
            }
        }

        public LineupItem SelectedSearchResult
        {
            get => _selectedSearchResult;
            set => Set(ref _selectedSearchResult, value);
        }

        public string RemoveLabel => _pendingRemoveId != null ? "press again to remove" : "remove";

        public string AccountListHint => AccountLineups.Count > 0 ? string.Empty
            : _signedIn ? "No lineups yet. Find one on the right and add it." : "Sign in to see your lineups.";

        public string SearchHint
        {
            get => SearchResults.Count > 0 ? string.Empty : _searchHint;
            private set
            {
                _searchHint = value;
                OnPropertyChanged();
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        public Brush StatusBrush
        {
            get => _statusBrush;
            private set => Set(ref _statusBrush, value);
        }

        public bool ShowUpdatePrompt
        {
            get => _showUpdatePrompt;
            private set => Set(ref _showUpdatePrompt, value);
        }

        private Core.Settings.SchedulesDirectSettings Settings => _shell.Services.Settings.Current.SchedulesDirect;

        private SchedulesDirectService Service => _shell.Services.SchedulesDirect;

        public async void LoadAsync()
        {
            await LoadCountriesAsync();
            if (Settings.IsConfigured)
            {
                await SignInAsync(explicitly: false);
            }
        }

        public void Cancel()
        {
            _cancel.Cancel();
            _cancel = new CancellationTokenSource();
        }

        private async Task LoadCountriesAsync()
        {
            try
            {
                using (var client = new SdClient(Settings.ApiBaseUrl))
                {
                    var countries = await client.GetCountriesAsync(_cancel.Token);
                    if (countries.Count > 0)
                    {
                        Countries = countries;
                        SelectedCountry = FindCountry(Settings.Country);
                    }
                }
            }
            catch (Exception ex) when (ex is SdApiException || ex is OperationCanceledException)
            {
                Log.Warn("Could not load the Schedules Direct country list", ex);
            }
        }

        private async Task SignInAsync(bool explicitly)
        {
            if (string.IsNullOrWhiteSpace(Username))
            {
                SetStatus("Enter your Schedules Direct username.", "Brush.Warning");
                return;
            }

            var typed = _readPassword();
            var hash = !string.IsNullOrEmpty(typed) ? SdClient.HashPassword(typed) : _shell.Services.Settings.GetSchedulesDirectPasswordHash();
            if (hash == null)
            {
                SetStatus("Enter your Schedules Direct password.", "Brush.Warning");
                return;
            }

            await RunAsync("Signing in to Schedules Direct…", async token =>
            {
                await Service.SignInAsync(Username, hash, token);

                Settings.Username = Username.Trim();
                _shell.Services.Settings.SetSchedulesDirectPasswordHash(hash);
                _shell.Services.Settings.Save();
                _clearPassword();
                OnPropertyChanged(nameof(PasswordLabel));
                _signedIn = true;

                var status = await Service.CallAsync((client, ct) => client.GetStatusAsync(ct), token);
                var expires = status.Account?.Expires;
                var max = status.Account?.MaxLineups ?? 0;
                var offline = status.SystemStatus?.FirstOrDefault(s => !string.Equals(s.Status, "Online", StringComparison.OrdinalIgnoreCase));

                await RefreshAccountLineupsAsync(token);

                AccountText = "Signed in as " + Settings.Username
                              + (expires.HasValue ? "\nSubscription until " + expires.Value.ToLocalTime().ToString("D") : string.Empty)
                              + "\n" + AccountLineups.Count + (max > 0 ? " of " + max : string.Empty) + " lineups on the account"
                              + (offline != null ? "\nService status: " + offline.Status + " — " + offline.Message : string.Empty);

                SetStatus(explicitly ? "Signed in." : string.Empty, "Brush.Success");
            });
        }

        private async Task RefreshAccountLineupsAsync(CancellationToken token)
        {
            var lineups = await Service.CallAsync((client, ct) => client.GetAccountLineupsAsync(ct), token);
            var selectedId = SelectedAccountLineup?.Id ?? Settings.LineupId;

            AccountLineups.Clear();
            foreach (var lineup in lineups)
            {
                AccountLineups.Add(new LineupItem(lineup.LineupId, lineup.Name, lineup.Location, lineup.Transport)
                {
                    IsActive = lineup.LineupId == Settings.LineupId,
                });
            }

            SelectedAccountLineup = AccountLineups.FirstOrDefault(l => l.Id == selectedId) ?? AccountLineups.FirstOrDefault();
            OnPropertyChanged(nameof(AccountListHint));

            if (AccountLineups.Count > 0 && Settings.LineupId != null && AccountLineups.All(l => l.Id != Settings.LineupId))
            {
                SetStatus("The lineup Origin was using is no longer on your account. Choose another.", "Brush.Warning");
            }
        }

        private async Task SearchAsync()
        {
            var country = SelectedCountry;
            if (country == null)
            {
                return;
            }

            var postal = (PostalCode ?? string.Empty).Trim();
            if (postal.Length == 0)
            {
                if (!country.OnePostalCode || country.PostalCodeExample == null)
                {
                    SetStatus("Enter a postal code to search.", "Brush.Warning");
                    return;
                }

                postal = country.PostalCodeExample;
            }

            Settings.Country = country.ShortName;
            Settings.PostalCode = postal;
            _shell.Services.Settings.Save();

            await RunAsync("Searching for lineups near " + postal + "…", async token =>
            {
                var headends = await Service.CallAsync((client, ct) => client.GetHeadendsAsync(country.ShortName, postal, ct), token);

                SearchResults.Clear();
                foreach (var headend in headends.OrderBy(h => TransportOrder(h.Transport)).ThenBy(h => h.Location))
                {
                    foreach (var lineup in headend.Lineups ?? new List<SdHeadendLineup>())
                    {
                        SearchResults.Add(new LineupItem(lineup.LineupId, lineup.Name, headend.Location, headend.Transport)
                        {
                            IsActive = AccountLineups.Any(a => a.Id == lineup.LineupId),
                        });
                    }
                }

                SelectedSearchResult = SearchResults.FirstOrDefault();
                SearchHint = "No lineups found for that postal code.";
                SetStatus(SearchResults.Count + " lineups found. Lineups marked IN USE are already on your account.", "Brush.Text");
            });
        }

        private async Task AddAsync()
        {
            var item = SelectedSearchResult;
            if (item == null)
            {
                return;
            }

            await RunAsync("Adding " + item.Name + "…", async token =>
            {
                var result = await Service.CallAsync((client, ct) => client.AddLineupAsync(item.Id, ct), token);
                await RefreshAccountLineupsAsync(token);

                SelectedAccountLineup = AccountLineups.FirstOrDefault(l => l.Id == item.Id) ?? SelectedAccountLineup;
                if (string.IsNullOrEmpty(Settings.LineupId))
                {
                    Use();
                }

                SetStatus("Added " + item.Name + "."
                          + (result.ChangesRemaining.HasValue ? " " + result.ChangesRemaining + " lineup changes left today." : string.Empty),
                    "Brush.Success");
            });
        }

        private async Task RemoveAsync()
        {
            var item = SelectedAccountLineup;
            if (item == null)
            {
                return;
            }

            // Lineup changes are rationed by Schedules Direct, so ask twice.
            if (_pendingRemoveId != item.Id)
            {
                _pendingRemoveId = item.Id;
                OnPropertyChanged(nameof(RemoveLabel));
                SetStatus("Removing a lineup uses one of your daily lineup changes. Press remove again to confirm.", "Brush.Warning");
                return;
            }

            _pendingRemoveId = null;
            OnPropertyChanged(nameof(RemoveLabel));

            await RunAsync("Removing " + item.Name + "…", async token =>
            {
                await Service.CallAsync((client, ct) => client.RemoveLineupAsync(item.Id, ct), token);
                if (Settings.LineupId == item.Id)
                {
                    Settings.LineupId = null;
                    Settings.LineupName = null;
                    _shell.Services.Settings.Save();
                }

                await RefreshAccountLineupsAsync(token);
                SetStatus("Removed " + item.Name + ".", "Brush.Success");
            });
        }

        private void Use()
        {
            var item = SelectedAccountLineup;
            if (item == null)
            {
                return;
            }

            Settings.LineupId = item.Id;
            Settings.LineupName = item.Name;
            _shell.Services.Settings.Save();

            foreach (var lineup in AccountLineups)
            {
                lineup.IsActive = lineup.Id == item.Id;
            }

            ShowUpdatePrompt = true;
            SetStatus("Origin will match your playlist to " + item.Name + ". Update the lineup to apply it.", "Brush.Success");
        }

        private async Task RunAsync(string busyText, Func<CancellationToken, Task> work)
        {
            _busy = true;
            CommandManager.InvalidateRequerySuggested();
            SetStatus(busyText, "Brush.TextSecondary");
            try
            {
                await work(_cancel.Token);
            }
            catch (SdApiException ex)
            {
                SetStatus(ex.Message, "Brush.Error");
                if (!Service.IsSignedIn)
                {
                    _signedIn = false;
                    AccountText = "Not signed in.";
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _busy = false;
                CommandManager.InvalidateRequerySuggested();
                OnPropertyChanged(nameof(AccountListHint));
                OnPropertyChanged(nameof(SearchHint));
            }
        }

        private void SetStatus(string text, string brushKey)
        {
            StatusText = text;
            StatusBrush = (Brush)Application.Current.FindResource(brushKey);
        }

        private SdCountry FindCountry(string shortName)
        {
            return Countries.FirstOrDefault(c => string.Equals(c.ShortName, shortName, StringComparison.OrdinalIgnoreCase))
                   ?? Countries.FirstOrDefault(c => c.ShortName == "USA")
                   ?? Countries.FirstOrDefault();
        }

        private static int TransportOrder(string transport)
        {
            switch ((transport ?? string.Empty).ToLowerInvariant())
            {
                case "cable":
                    return 0;
                case "satellite":
                    return 1;
                case "antenna":
                    return 2;
                default:
                    return 3;
            }
        }
    }
}
