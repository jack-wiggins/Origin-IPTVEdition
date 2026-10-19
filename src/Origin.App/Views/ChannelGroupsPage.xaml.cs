using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Origin.Controls;
using Origin.Core.Lineup;
using Origin.Core.Settings;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>Settings › channel groups: which playlist groups are copied into the lineup, and how channels are numbered.</summary>
    public partial class ChannelGroupsPage : OriginPage
    {
        private readonly List<GroupRow> _groups;
        private readonly List<Choice> _numbering = new List<Choice>
        {
            new Choice("from the playlist", ChannelNumbering.Playlist),
            new Choice("from Schedules Direct", ChannelNumbering.SchedulesDirect),
        };

        public ChannelGroupsPage(IShell shell) : base(shell)
        {
            InitializeComponent();

            var settings = Services.Settings.Current.Lineup;
            var playlist = Services.Playlists.Load();

            _groups = (playlist?.GetLiveGroups() ?? new List<Core.Iptv.PlaylistGroup>())
                .Select(g => new GroupRow(g.Name, g.ChannelCount, settings.IsGroupIncluded(g.Name)))
                .ToList();

            foreach (var group in _groups)
            {
                group.Changed += UpdateSummary;
            }

            IncludeNewBox.IsChecked = settings.IncludeNewGroups;
            NumberingSpinner.ItemsSource = _numbering;
            NumberingSpinner.SelectedItem = _numbering.First(c => (ChannelNumbering)c.Value == settings.Numbering);

            EmptyText.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            GroupList.ItemsSource = _groups;
            UpdateSummary();
        }

        public override string Title => "channel groups";

        public override void FocusDefault()
        {
            if (_groups.Count > 0)
            {
                FocusList(GroupList);
            }
            else
            {
                IncludeNewBox.Focus();
            }
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            if (command == RemoteCommand.Ok && Keyboard.FocusedElement is ListBoxItem item && item.DataContext is GroupRow group)
            {
                group.IsIncluded = !group.IsIncluded;
                return true;
            }

            return LeaveListInDirection(command);
        }

        private void UpdateSummary()
        {
            var included = _groups.Where(g => g.IsIncluded).ToList();
            var channels = included.Sum(g => g.Count);
            var total = _groups.Sum(g => g.Count);
            SummaryText.Text = _groups.Count == 0
                ? "No groups yet"
                : channels.ToString("N0") + " of " + total.ToString("N0") + " live channels\nin " + included.Count + " of " + _groups.Count + " groups";
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            var search = (SearchBox.Text ?? string.Empty).Trim();
            GroupList.ItemsSource = search.Length == 0
                ? _groups
                : _groups.Where(g => g.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        private void OnGroupClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(GroupList, source) is ListBoxItem item
                && item.DataContext is GroupRow group)
            {
                group.IsIncluded = !group.IsIncluded;
            }
        }

        private void OnSelectAll(object sender, RoutedEventArgs e) => SetAll(true);

        private void OnSelectNone(object sender, RoutedEventArgs e) => SetAll(false);

        private void SetAll(bool included)
        {
            foreach (var group in (IEnumerable<GroupRow>)GroupList.ItemsSource)
            {
                group.IsIncluded = included;
            }
        }

        private void OnApply(object sender, RoutedEventArgs e)
        {
            var settings = Services.Settings.Current.Lineup;
            settings.IncludeNewGroups = IncludeNewBox.IsChecked == true;
            settings.Numbering = (ChannelNumbering)((Choice)NumberingSpinner.SelectedItem).Value;

            var groups = new Dictionary<string, bool>(settings.Groups ?? new Dictionary<string, bool>(), StringComparer.Ordinal);
            foreach (var group in _groups)
            {
                groups[group.Name] = group.IsIncluded;
            }

            settings.Groups = groups;
            Services.Settings.Save();

            if (_groups.Count == 0)
            {
                Shell.ShowToast("Saved. These choices apply the next time the lineup is updated.", ToastKind.Success);
                return;
            }

            // The playlist is already on disk; rebuild from it without downloading again.
            Shell.Replace(new LineupUpdatePage(Shell, new LineupUpdateOptions { DownloadPlaylist = false, RefreshSchedulesDirect = false }));
        }

        private sealed class GroupRow : ObservableObject
        {
            private static readonly Brush OnBrush = (Brush)Application.Current.FindResource("Brush.Accent");
            private static readonly Brush OffBrush = (Brush)Application.Current.FindResource("Brush.Input");

            private bool _isIncluded;

            public GroupRow(string name, int count, bool included)
            {
                Name = name;
                Count = count;
                _isIncluded = included;
            }

            public event Action Changed;

            public string Name { get; }

            public int Count { get; }

            public string CountText => Count.ToString("N0");

            public bool IsIncluded
            {
                get => _isIncluded;
                set
                {
                    if (Set(ref _isIncluded, value))
                    {
                        OnPropertyChanged(nameof(BoxBrush));
                        Changed?.Invoke();
                    }
                }
            }

            public Brush BoxBrush => _isIncluded ? OnBrush : OffBrush;
        }
    }
}
