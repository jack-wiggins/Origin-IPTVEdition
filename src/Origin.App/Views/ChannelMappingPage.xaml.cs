using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Origin.Controls;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>
    /// Settings › channel mapping: every channel copied from the M3U, the Schedules Direct
    /// station it was matched to and how. OK opens a picker to change or clear the match.
    /// </summary>
    public partial class ChannelMappingPage : OriginPage
    {
        private enum Filter
        {
            All,
            Unmatched,
            Matched,
            Manual,
            Hidden,
        }

        private readonly List<Choice> _filters = new List<Choice>
        {
            new Choice("show: all channels", Filter.All),
            new Choice("show: not matched", Filter.Unmatched),
            new Choice("show: matched", Filter.Matched),
            new Choice("show: chosen by you", Filter.Manual),
            new Choice("show: hidden", Filter.Hidden),
        };

        private ChannelMatcher _matcher;
        private List<MappingRow> _rows = new List<MappingRow>();
        private MappingRow _pickerRow;
        private List<PickerOption> _pickerOptions = new List<PickerOption>();
        private bool _pickerOpen;

        public ChannelMappingPage(IShell shell) : base(shell)
        {
            InitializeComponent();

            FilterSpinner.ItemsSource = _filters;
            FilterSpinner.SelectedItem = _filters[0];
            FilterSpinner.SelectedItemChanged += (s, e) => Refresh();

            Services.Lineups.Changed += OnLineupChanged;
        }

        public override string Title => "channel mapping";

        public override void OnNavigatedTo(bool isBack)
        {
            if (!isBack)
            {
                _matcher = Services.Lineup.CreateMatcherFromCache();
                Refresh();
            }
        }

        public override void OnRemoved()
        {
            Services.Lineups.Changed -= OnLineupChanged;
        }

        public override void FocusDefault()
        {
            if (_rows.Count > 0)
            {
                FocusList(ChannelList);
            }
            else
            {
                FilterSpinner.Focus();
            }
        }

        public override bool HandleCommand(RemoteCommand command)
        {
            if (_pickerOpen)
            {
                switch (command)
                {
                    case RemoteCommand.Back:
                        ClosePicker();
                        return true;
                    case RemoteCommand.Ok when Keyboard.FocusedElement is ListBoxItem item && IsIn(item, PickerList):
                        Apply(item.DataContext as PickerOption);
                        return true;
                    case RemoteCommand.Ok when Keyboard.FocusedElement == PickerSearch:
                        FocusList(PickerList);
                        return true;
                    case RemoteCommand.Up when Keyboard.FocusedElement is ListBoxItem first && IsIn(first, PickerList) && PickerList.Items.IndexOf(first.DataContext) == 0:
                        PickerSearch.Focus();
                        return true;
                    default:
                        return false;
                }
            }

            if (command == RemoteCommand.Ok && Keyboard.FocusedElement is ListBoxItem row && IsIn(row, ChannelList))
            {
                OpenPicker(row.DataContext as MappingRow);
                return true;
            }

            return LeaveListInDirection(command);
        }

        // ------------------------------------------------------------------ Channel list

        private void OnLineupChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_pickerOpen)
                {
                    Refresh();
                }
            }));
        }

        private void Refresh()
        {
            var lineup = Services.Lineups.Current;
            var selectedKey = (ChannelList.SelectedItem as MappingRow)?.Key;
            var focusWasInList = ChannelList.IsKeyboardFocusWithin;

            var filter = (Filter)((Choice)FilterSpinner.SelectedItem).Value;
            var search = (SearchBox.Text ?? string.Empty).Trim();

            IEnumerable<OriginChannel> channels = lineup.Channels;
            switch (filter)
            {
                case Filter.Unmatched:
                    channels = channels.Where(c => !c.IsMapped);
                    break;
                case Filter.Matched:
                    channels = channels.Where(c => c.IsMapped);
                    break;
                case Filter.Manual:
                    channels = channels.Where(c => c.Override != MappingOverride.None);
                    break;
                case Filter.Hidden:
                    channels = channels.Where(c => !c.Enabled);
                    break;
            }

            if (search.Length > 0)
            {
                channels = channels.Where(c => Contains(c.Name, search) || Contains(c.Number, search) || Contains(c.Group, search)
                                               || Contains(c.Mapping?.Callsign, search) || Contains(c.Mapping?.StationName, search));
            }

            _rows = channels.Select(c => new MappingRow(c)).ToList();
            ChannelList.ItemsSource = _rows;

            var total = lineup.Channels.Count;
            SummaryText.Text = total == 0
                ? "No channels yet"
                : total.ToString("N0") + " channels   ·   " + lineup.MappedCount.ToString("N0") + " matched   ·   "
                  + lineup.UnmappedCount.ToString("N0") + " not matched";
            LineupText.Text = lineup.SchedulesDirectLineupName != null
                ? "Matched against Schedules Direct lineup " + lineup.SchedulesDirectLineupName + " (" + lineup.SchedulesDirectLineupId + ")"
                : "No Schedules Direct lineup in use. Choose one under settings › schedules direct, then update the lineup.";

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = total == 0
                ? "Your lineup is empty. Set up your IPTV provider, then choose tasks › update lineup."
                : "No channels match this filter.";

            if (_rows.Count > 0)
            {
                var index = Math.Max(0, _rows.FindIndex(r => r.Key == selectedKey));
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(_rows[index]);
                if (focusWasInList)
                {
                    FocusList(ChannelList);
                }
            }
        }

        private void OnSearchChanged(object sender, TextChangedEventArgs e)
        {
            Refresh();
        }

        private void OnChannelDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ChannelList, source) is ListBoxItem item)
            {
                OpenPicker(item.DataContext as MappingRow);
            }
        }

        // ------------------------------------------------------------------ Station picker

        private void OpenPicker(MappingRow row)
        {
            if (row == null)
            {
                return;
            }

            _pickerRow = row;
            _pickerOpen = true;
            PickerTitle.Text = "Match “" + row.Name + "”";
            PickerSubtitle.Text = row.Channel.IsMapped
                ? "Now: " + row.StationTitle + "  ·  " + row.Badge
                : _matcher == null
                    ? "No Schedules Direct lineup is downloaded yet, so only these options are available."
                    : "Not matched yet. Suggestions are listed first.";

            PickerSearch.Text = string.Empty;
            BuildPickerOptions(string.Empty);

            Picker.Visibility = Visibility.Visible;
            Anim.FromTo(Picker, OpacityProperty, 0, 1, 200);
            Anim.FromTo(PickerSlide, TranslateTransform.YProperty, 40, 0, 320);
            Dispatcher.BeginInvoke(new Action(() => FocusList(PickerList)), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ClosePicker()
        {
            _pickerOpen = false;
            Anim.To(Picker, OpacityProperty, 0, 180, Anim.EaseIn, () =>
            {
                if (!_pickerOpen)
                {
                    Picker.Visibility = Visibility.Collapsed;
                }
            });

            Refresh();
            FocusList(ChannelList);
        }

        private void BuildPickerOptions(string search)
        {
            var row = _pickerRow;
            var options = new List<PickerOption>();
            search = search.Trim();

            if (search.Length == 0)
            {
                options.Add(new PickerOption(PickerAction.Automatic, "Match automatically", "Let Origin choose the station from the channel's name and tvg-id"));
                options.Add(new PickerOption(PickerAction.NoMatch, "No guide match", "This channel has no Schedules Direct station"));
                options.Add(new PickerOption(PickerAction.ToggleHidden,
                    row.Channel.Enabled ? "Hide from the guide" : "Show in the guide",
                    row.Channel.Enabled ? "The channel stays in the lineup but won't appear in the guide" : "The channel is hidden at the moment"));
            }

            if (_matcher != null)
            {
                var suggested = new HashSet<string>();
                if (search.Length == 0)
                {
                    foreach (var scored in _matcher.Rank(row.Name, 8))
                    {
                        suggested.Add(scored.Station.StationId);
                        options.Add(PickerOption.ForStation(scored.Station, "Suggested  ·  " + scored.Score.ToString("P0") + " similar"));
                    }
                }

                var stations = _matcher.Stations
                    .Where(s => !suggested.Contains(s.StationId))
                    .Where(s => search.Length == 0 || Contains(s.Callsign, search) || Contains(s.Name, search)
                                || Contains(s.Channel, search) || Contains(s.Affiliate, search))
                    .OrderBy(s => s.Callsign, StringComparer.OrdinalIgnoreCase);

                options.AddRange(stations.Select(s => PickerOption.ForStation(s, null)));
            }

            _pickerOptions = options;
            PickerList.ItemsSource = options;
            if (options.Count > 0)
            {
                PickerList.SelectedIndex = 0;
                PickerList.ScrollIntoView(options[0]);
            }
        }

        private void Apply(PickerOption option)
        {
            if (option == null || _pickerRow == null)
            {
                return;
            }

            var key = _pickerRow.Key;
            var lineup = Services.Lineup;
            switch (option.Action)
            {
                case PickerAction.Automatic:
                    lineup.ResetToAutomatic(key, _matcher);
                    break;
                case PickerAction.NoMatch:
                    lineup.SetNoMatch(key);
                    break;
                case PickerAction.ToggleHidden:
                    lineup.SetEnabled(key, !_pickerRow.Channel.Enabled);
                    break;
                case PickerAction.Station:
                    lineup.SetManualStation(key, option.Station);
                    break;
            }

            ClosePicker();
        }

        private void OnPickerSearchChanged(object sender, TextChangedEventArgs e)
        {
            if (_pickerOpen)
            {
                BuildPickerOptions(PickerSearch.Text ?? string.Empty);
            }
        }

        /// <summary>Typing while the list has focus goes to the search box.</summary>
        private void OnPickerListTextInput(object sender, TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
            {
                return;
            }

            PickerSearch.Focus();
            PickerSearch.Text += e.Text;
            PickerSearch.CaretIndex = PickerSearch.Text.Length;
            e.Handled = true;
        }

        private void OnPickerDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(PickerList, source) is ListBoxItem item)
            {
                Apply(item.DataContext as PickerOption);
            }
        }

        private void OnScrimClick(object sender, MouseButtonEventArgs e)
        {
            ClosePicker();
        }

        private static bool IsIn(ListBoxItem item, ListBox list)
        {
            return ItemsControl.ItemsControlFromItemContainer(item) == list;
        }

        private static bool Contains(string text, string search)
        {
            return text != null && text.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private enum PickerAction
        {
            Automatic,
            NoMatch,
            ToggleHidden,
            Station,
        }

        private sealed class PickerOption
        {
            public PickerOption(PickerAction action, string title, string subtitle, LineupStation station = null)
            {
                Action = action;
                Title = title;
                Subtitle = subtitle;
                Station = station;
            }

            public PickerAction Action { get; }

            public string Title { get; }

            public string Subtitle { get; }

            public LineupStation Station { get; }

            public static PickerOption ForStation(LineupStation station, string note)
            {
                var title = station.Callsign + (string.IsNullOrEmpty(station.Name) || station.Name == station.Callsign ? string.Empty : "  —  " + station.Name);
                var parts = new List<string>();
                if (note != null)
                {
                    parts.Add(note);
                }

                if (!string.IsNullOrEmpty(station.Channel))
                {
                    parts.Add("channel " + station.Channel);
                }

                if (!string.IsNullOrEmpty(station.Affiliate))
                {
                    parts.Add(station.Affiliate);
                }

                parts.Add("station " + station.StationId);
                return new PickerOption(PickerAction.Station, title, string.Join("  ·  ", parts), station);
            }
        }

        private sealed class MappingRow
        {
            private static readonly Brush ManualBrush = Freeze(Color.FromRgb(0x1E, 0x68, 0xBF));
            private static readonly Brush ExactBrush = Freeze(Color.FromArgb(0x99, 0x2E, 0x8B, 0x57));
            private static readonly Brush FuzzyBrush = Freeze(Color.FromArgb(0x99, 0xB0, 0x7D, 0x1A));
            private static readonly Brush NoneBrush = Freeze(Color.FromArgb(0x66, 0x60, 0x70, 0x88));

            public MappingRow(OriginChannel channel)
            {
                Channel = channel;
            }

            public OriginChannel Channel { get; }

            public string Key => Channel.Key;

            public string Number => Channel.Number;

            public string Name => Channel.Name;

            public string Group => Channel.Enabled ? Channel.Group : Channel.Group + "  ·  hidden from the guide";

            public string LogoUrl => Channel.DisplayLogoUrl;

            public string StationTitle => Channel.IsMapped
                ? Channel.Mapping.Callsign + (string.IsNullOrEmpty(Channel.Mapping.StationName) || Channel.Mapping.StationName == Channel.Mapping.Callsign
                    ? string.Empty
                    : "  —  " + Channel.Mapping.StationName)
                : "No guide match";

            public string StationDetail
            {
                get
                {
                    var mapping = Channel.Mapping;
                    if (mapping == null)
                    {
                        return Channel.Override == MappingOverride.NoMatch ? "You chose no match" : "OK to choose a station";
                    }

                    var parts = new List<string>();
                    if (!string.IsNullOrEmpty(mapping.LineupChannel))
                    {
                        parts.Add("channel " + mapping.LineupChannel);
                    }

                    if (!string.IsNullOrEmpty(mapping.Affiliate))
                    {
                        parts.Add(mapping.Affiliate);
                    }

                    parts.Add("station " + mapping.StationId);
                    return string.Join("  ·  ", parts);
                }
            }

            public double StationOpacity => Channel.IsMapped ? 1 : 0.6;

            public string Badge
            {
                get
                {
                    if (Channel.Override == MappingOverride.NoMatch)
                    {
                        return "NO MATCH (YOURS)";
                    }

                    switch (Channel.Mapping?.Method ?? MatchMethod.None)
                    {
                        case MatchMethod.StationId:
                            return "STATION ID";
                        case MatchMethod.TvgIdCallsign:
                        case MatchMethod.TvgIdName:
                            return "TVG-ID";
                        case MatchMethod.Callsign:
                            return "CALLSIGN";
                        case MatchMethod.ExactName:
                            return "NAME";
                        case MatchMethod.Fuzzy:
                            return "SIMILAR " + Channel.Mapping.Confidence.ToString("P0");
                        case MatchMethod.Manual:
                            return "CHOSEN BY YOU";
                        default:
                            return "NOT MATCHED";
                    }
                }
            }

            public Brush BadgeBrush
            {
                get
                {
                    if (Channel.Override != MappingOverride.None)
                    {
                        return ManualBrush;
                    }

                    switch (Channel.Mapping?.Method ?? MatchMethod.None)
                    {
                        case MatchMethod.None:
                            return NoneBrush;
                        case MatchMethod.Fuzzy:
                            return FuzzyBrush;
                        default:
                            return ExactBrush;
                    }
                }
            }

            private static Brush Freeze(Color color)
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            }
        }
    }
}
