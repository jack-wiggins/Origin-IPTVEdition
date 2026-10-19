using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Origin.Controls;
using Origin.Core.Guide;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>
    /// The channel guide: a 90-minute window of program blocks per channel, as in Media Center.
    /// Up/Down move between channels at the same time; Left/Right move to the previous or next
    /// program, scrolling the window half an hour at a time.
    /// </summary>
    public partial class GuidePage : OriginPage
    {
        private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(90);
        private static readonly TimeSpan Step = TimeSpan.FromMinutes(30);

        private readonly DispatcherTimer _clock;
        private readonly ChannelNumberEntry _numberEntry;
        private List<GuideRow> _rows = new List<GuideRow>();
        private DateTime _windowStartUtc;

        /// <summary>The time the highlight follows from row to row: the start of the program last moved to.</summary>
        private DateTime _cursorUtc;

        private GuideCellView _selectedCell;

        public GuidePage(IShell shell) : base(shell)
        {
            InitializeComponent();

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _clock.Tick += (s, e) => OnClockTick();

            _numberEntry = new ChannelNumberEntry(Dispatcher, NumberEntry, NumberText, JumpToNumber);
            Services.Lineups.Changed += OnLineupChanged;
            Services.Guide.Changed += OnGuideChanged;
        }

        public override string Title => "guide";

        private GuideRow SelectedRow => ChannelList.SelectedItem as GuideRow;

        public override void OnNavigatedTo(bool isBack)
        {
            if (!isBack)
            {
                _windowStartUtc = EarliestWindowStart();
                _cursorUtc = DateTime.UtcNow;
                Load(Services.Settings.Current.Playback.LastChannelKey);
            }
            else
            {
                OnClockTick();
            }

            _clock.Start();
        }

        public override void OnNavigatedFrom()
        {
            _clock.Stop();
            _numberEntry.Cancel();
        }

        public override void OnRemoved()
        {
            Services.Lineups.Changed -= OnLineupChanged;
            Services.Guide.Changed -= OnGuideChanged;
        }

        public override void FocusDefault()
        {
            FocusRow(Math.Max(0, ChannelList.SelectedIndex));
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
                case RemoteCommand.Play:
                    Watch(SelectedRow);
                    return true;
                case RemoteCommand.Left:
                    MoveToPreviousProgram();
                    return true;
                case RemoteCommand.Right:
                    MoveToNextProgram();
                    return true;
                case RemoteCommand.ChannelUp:
                    Page(-1);
                    return true;
                case RemoteCommand.ChannelDown:
                    Page(1);
                    return true;
                case RemoteCommand.Guide:
                    return true;
                case RemoteCommand.Back when _numberEntry.IsActive:
                    _numberEntry.Cancel();
                    return true;
                default:
                    return false;
            }
        }

        private void Load(string selectKey)
        {
            _rows = Services.Lineups.VisibleChannels.Select(c => new GuideRow(c)).ToList();
            ChannelList.ItemsSource = _rows;

            var hasRows = _rows.Count > 0;
            EmptyPanel.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
            DetailsPanel.Visibility = hasRows ? Visibility.Visible : Visibility.Hidden;

            ApplyWindow();
            if (hasRows)
            {
                var index = Math.Max(0, _rows.FindIndex(r => r.Key == selectKey));
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(_rows[index]);
            }
        }

        private void OnLineupChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() => Load(SelectedRow?.Key)));
        }

        private void OnGuideChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ApplyWindow));
        }

        // ------------------------------------------------------------------ Time window

        /// <summary>The half hour we are in, in local time (some time zones are offset by 30 or 45 minutes).</summary>
        private static DateTime EarliestWindowStart()
        {
            var now = DateTime.Now;
            return new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0, DateTimeKind.Local).ToUniversalTime();
        }

        private static DateTime LatestWindowStart() => EarliestWindowStart() + TimeSpan.FromDays(GuideDownloader.DaysAhead) - WindowLength;

        /// <summary>Rebuilds every row's blocks for the current window and moves the highlight to the cursor.</summary>
        private void ApplyWindow()
        {
            var window = new GuideWindow(Services.Guide, _windowStartUtc, _windowStartUtc + WindowLength);
            foreach (var row in _rows)
            {
                row.SetWindow(window);
            }

            _selectedCell = null;
            UpdateTimeRuler();
            SelectCellAtCursor();
        }

        private void OnClockTick()
        {
            var earliest = EarliestWindowStart();
            if (_windowStartUtc < earliest)
            {
                // The half hour on screen is over; Media Center keeps the guide on the present.
                _windowStartUtc = earliest;
                if (_cursorUtc < earliest)
                {
                    _cursorUtc = DateTime.UtcNow;
                }

                ApplyWindow();
                return;
            }

            UpdateTimeRuler();
            ShowDetails(SelectedRow, _selectedCell);
        }

        private void UpdateTimeRuler()
        {
            var start = _windowStartUtc.ToLocalTime();
            DayText.Text = FormatDay(start);
            Slot0.Text = start.ToString("t");
            Slot1.Text = start.Add(Step).ToString("t");
            Slot2.Text = start.Add(Step + Step).ToString("t");

            var now = (DateTime.UtcNow - _windowStartUtc).TotalSeconds / WindowLength.TotalSeconds;
            NowLine.Visibility = now >= 0 && now < 1 ? Visibility.Visible : Visibility.Collapsed;
            TimelinePanel.SetStart(NowLine, now);
            TimelinePanel.SetEnd(NowLine, now);
        }

        /// <summary>Left: the previous program in this row, scrolling back (but not before the present half hour) until it shows.</summary>
        private void MoveToPreviousProgram()
        {
            var cell = SelectedRow?.CellAt(_cursorUtc)?.Cell;
            if (cell == null)
            {
                return;
            }

            var programStart = cell.ProgramStartUtc;
            var earliest = EarliestWindowStart();
            var start = _windowStartUtc;
            while (start >= programStart && start > earliest)
            {
                start -= Step;
            }

            SetWindowStart(start < earliest ? earliest : start);
            _cursorUtc = programStart > _windowStartUtc
                ? SelectedRow.CellAt(programStart.AddTicks(-1)).Cell.StartUtc
                : _windowStartUtc;
            SelectCellAtCursor();
        }

        /// <summary>Right: the next program in this row, scrolling forward half an hour at a time until it shows.</summary>
        private void MoveToNextProgram()
        {
            var cell = SelectedRow?.CellAt(_cursorUtc)?.Cell;
            if (cell == null)
            {
                return;
            }

            var programEnd = cell.ProgramEndUtc;
            var latest = LatestWindowStart();
            var start = _windowStartUtc;
            while (programEnd >= start + WindowLength && start < latest)
            {
                start += Step;
            }

            SetWindowStart(start);
            _cursorUtc = programEnd < _windowStartUtc + WindowLength
                ? programEnd
                : (_cursorUtc > _windowStartUtc ? _cursorUtc : _windowStartUtc);
            SelectCellAtCursor();
        }

        private void SetWindowStart(DateTime startUtc)
        {
            if (startUtc != _windowStartUtc)
            {
                _windowStartUtc = startUtc;
                ApplyWindow();
            }
        }

        // ------------------------------------------------------------------ Selection

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectCellAtCursor();
        }

        /// <summary>Highlights the block under the time cursor in the selected row and shows its details.</summary>
        private void SelectCellAtCursor()
        {
            var row = SelectedRow;
            var cell = row?.CellAt(_cursorUtc);
            if (!ReferenceEquals(cell, _selectedCell))
            {
                if (_selectedCell != null)
                {
                    _selectedCell.IsSelected = false;
                }

                _selectedCell = cell;
                if (cell != null)
                {
                    cell.IsSelected = true;
                }
            }

            ShowDetails(row, cell);
        }

        private void OnCellMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is GuideCellView cell)
                || !(ItemsControl.ContainerFromElement(ChannelList, (DependencyObject)sender) is ListBoxItem item)
                || !(item.DataContext is GuideRow row))
            {
                return;
            }

            // The list selects the row itself; the cursor has to move first so the right block lights up.
            _cursorUtc = cell.Cell.StartUtc;
            if (ReferenceEquals(row, SelectedRow))
            {
                SelectCellAtCursor();
            }
        }

        private void ShowDetails(GuideRow row, GuideCellView cell)
        {
            if (row == null)
            {
                return;
            }

            var listing = cell?.Cell.Listing;
            var badges = new List<string>();
            if (listing == null)
            {
                DetailTitle.Text = row.Number + "   " + row.Name;
                DetailTime.Text = cell != null && row.HasListings ? FormatAirTime(cell.Cell.StartUtc, cell.Cell.EndUtc, false) : string.Empty;
                DetailEpisode.Text = DescribeMapping(row.Channel);
                DetailText.Text = !row.Channel.IsMapped ? "Choose a Schedules Direct station for this channel under settings › channel mapping."
                    : row.HasListings ? "No program is listed for this time."
                    : "Guide listings download with tasks › update lineup.";
            }
            else
            {
                var program = listing.Program;
                DetailTitle.Text = listing.Title;
                DetailTime.Text = FormatAirTime(listing.StartUtc, listing.EndUtc, listing.IsOnAt(DateTime.UtcNow)) + "   ·   " + row.Number + "  " + row.Name;
                DetailEpisode.Text = DescribeEpisode(listing);
                DetailText.Text = listing.Description
                                  ?? (program == null ? "The details of this program have not been downloaded yet." : string.Empty);

                if (listing.Airing.IsLive)
                {
                    badges.Add("LIVE");
                }

                if (listing.Airing.IsNew)
                {
                    badges.Add("NEW");
                }

                if (listing.Airing.PremiereOrFinale != null)
                {
                    badges.Add(listing.Airing.PremiereOrFinale.ToUpper(CultureInfo.CurrentCulture));
                }

                if (listing.Airing.IsHd)
                {
                    badges.Add("HD");
                }

                if (listing.Rating != null)
                {
                    badges.Add(listing.Rating);
                }
            }

            DetailBadges.ItemsSource = badges;
        }

        // ------------------------------------------------------------------ Text

        private static string FormatDay(DateTime local)
        {
            var today = DateTime.Today;
            if (local.Date == today)
            {
                return "Today";
            }

            if (local.Date == today.AddDays(1))
            {
                return "Tomorrow";
            }

            return local.ToString("ddd", CultureInfo.CurrentCulture) + " " + local.ToString("M", CultureInfo.CurrentCulture);
        }

        /// <summary>"Today 8:00 PM – 9:00 PM", with the minutes left for a program that is on now.</summary>
        private static string FormatAirTime(DateTime startUtc, DateTime endUtc, bool onNow)
        {
            var start = startUtc.ToLocalTime();
            var text = FormatDay(start) + "  " + start.ToString("t") + " – " + endUtc.ToLocalTime().ToString("t");
            if (onNow)
            {
                var left = Math.Max(1, (int)Math.Ceiling((endUtc - DateTime.UtcNow).TotalMinutes));
                text += "   ·   " + left.ToString(CultureInfo.CurrentCulture) + " min left";
            }

            return text;
        }

        /// <summary>"Season 2, episode 6 · The Poisoned Sausage · First aired 11/4/1985".</summary>
        private static string DescribeEpisode(GuideListing listing)
        {
            var program = listing.Program;
            if (program == null)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (program.Season.HasValue && program.Episode.HasValue)
            {
                parts.Add("Season " + program.Season.Value.ToString(CultureInfo.CurrentCulture) + ", episode " + program.Episode.Value.ToString(CultureInfo.CurrentCulture));
            }
            else if (program.Episode.HasValue)
            {
                parts.Add("Episode " + program.Episode.Value.ToString(CultureInfo.CurrentCulture));
            }

            if (program.EpisodeTitle != null)
            {
                parts.Add(program.EpisodeTitle);
            }

            if (program.IsMovie)
            {
                parts.Add(program.MovieYear.HasValue ? "Movie (" + program.MovieYear.Value.ToString(CultureInfo.InvariantCulture) + ")" : "Movie");
            }

            if (program.Genres != null && program.Genres.Count > 0)
            {
                parts.Add(string.Join(", ", program.Genres.Take(3)));
            }

            if (!listing.Airing.IsNew && program.OriginalAirDate.HasValue && !program.IsMovie)
            {
                parts.Add("First aired " + program.OriginalAirDate.Value.ToString("d"));
            }

            return string.Join("   ·   ", parts);
        }

        private static string DescribeMapping(OriginChannel channel)
        {
            var mapping = channel.Mapping;
            return mapping != null
                ? "Schedules Direct: " + mapping.Callsign
                  + (string.IsNullOrEmpty(mapping.StationName) || mapping.StationName == mapping.Callsign ? string.Empty : " · " + mapping.StationName)
                  + (string.IsNullOrEmpty(mapping.Affiliate) ? string.Empty : " (" + mapping.Affiliate + ")")
                  + (string.IsNullOrEmpty(mapping.LineupChannel) ? string.Empty : " · lineup channel " + mapping.LineupChannel)
                  + "     Group: " + channel.Group
                : "Not matched to Schedules Direct     Group: " + channel.Group;
        }

        // ------------------------------------------------------------------ Channels

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ChannelList, source) is ListBoxItem item)
            {
                Watch(item.DataContext as GuideRow);
            }
        }

        private void Watch(GuideRow row)
        {
            if (row != null)
            {
                Shell.PlayChannel(row.Key);
            }
        }

        private void Page(int direction)
        {
            if (_rows.Count == 0)
            {
                return;
            }

            var visible = Math.Max(1, (int)(ChannelList.ActualHeight / 84) - 1);
            var index = Math.Max(0, Math.Min(_rows.Count - 1, ChannelList.SelectedIndex + direction * visible));
            FocusRow(index);
        }

        private void JumpToNumber(string number)
        {
            var channel = Services.Lineups.FindByNumber(number);
            if (channel == null)
            {
                return;
            }

            var index = _rows.FindIndex(r => r.Key == channel.Key);
            if (index >= 0)
            {
                FocusRow(index);
            }
        }

        private void FocusRow(int index)
        {
            if (index < 0 || index >= _rows.Count)
            {
                return;
            }

            ChannelList.SelectedIndex = index;
            ChannelList.ScrollIntoView(_rows[index]);
            ChannelList.UpdateLayout();
            (ChannelList.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem)?.Focus();
        }

        // ------------------------------------------------------------------ View models

        /// <summary>The time span on screen; shared by every row.</summary>
        private sealed class GuideWindow
        {
            public GuideWindow(GuideStore store, DateTime startUtc, DateTime endUtc)
            {
                Store = store;
                StartUtc = startUtc;
                EndUtc = endUtc;
            }

            public GuideStore Store { get; }

            public DateTime StartUtc { get; }

            public DateTime EndUtc { get; }
        }

        private sealed class GuideRow : ObservableObject
        {
            private GuideWindow _window;
            private IReadOnlyList<GuideCellView> _cells;

            public GuideRow(OriginChannel channel)
            {
                Channel = channel;
            }

            public OriginChannel Channel { get; }

            public string Key => Channel.Key;

            public string Number => Channel.Number;

            public string Name => Channel.Name;

            public string LogoUrl => Channel.DisplayLogoUrl;

            public string Callsign => Channel.Mapping?.Callsign ?? Channel.Group;

            public bool HasListings => Channel.IsMapped && _window != null && _window.Store.HasListings(Channel.Mapping.StationId);

            /// <summary>Built when first shown, so a guide of hundreds of channels only reads the rows on screen.</summary>
            public IReadOnlyList<GuideCellView> Cells => _cells ?? (_cells = BuildCells());

            public void SetWindow(GuideWindow window)
            {
                _window = window;
                _cells = null;
                OnPropertyChanged(nameof(Cells));
            }

            public GuideCellView CellAt(DateTime utc)
            {
                var cells = Cells;
                if (cells.Count == 0)
                {
                    return null;
                }

                return cells.FirstOrDefault(c => utc < c.Cell.EndUtc) ?? cells[cells.Count - 1];
            }

            private IReadOnlyList<GuideCellView> BuildCells()
            {
                if (_window == null)
                {
                    return new GuideCellView[0];
                }

                var whole = new GuideCell(null, _window.StartUtc, _window.EndUtc, _window.StartUtc, _window.EndUtc);
                if (!Channel.IsMapped)
                {
                    return new[] { new GuideCellView(whole, Channel.Name, "No guide match · choose one under settings › channel mapping") };
                }

                var stationId = Channel.Mapping.StationId;
                var listings = _window.Store.GetListings(stationId, _window.StartUtc, _window.EndUtc);
                if (listings.Count == 0 && !_window.Store.HasListings(stationId))
                {
                    return new[] { new GuideCellView(whole, Channel.Mapping.StationName ?? Channel.Mapping.Callsign, "No listings yet · they download with tasks › update lineup") };
                }

                return GuideLayout.Build(listings, _window.StartUtc, _window.EndUtc).Select(c => new GuideCellView(c)).ToList();
            }
        }

        private sealed class GuideCellView : ObservableObject
        {
            private readonly string _title;
            private readonly string _subtitle;
            private bool _isSelected;

            /// <param name="title">Overrides the program title, for rows that have no listings.</param>
            public GuideCellView(GuideCell cell, string title = null, string subtitle = null)
            {
                Cell = cell;
                _title = title;
                _subtitle = subtitle;
            }

            public GuideCell Cell { get; }

            public string Title => _title ?? (Cell.IsGap ? "No listings" : Cell.Listing.Title);

            public string Subtitle => _subtitle ?? Cell.Listing?.EpisodeLine;

            public string Badge => Cell.Listing?.Badge;

            /// <summary>A gap in the listings is drawn fainter than a program.</summary>
            public bool IsDim => Cell.IsGap && _title == null;

            public bool ContinuesBefore => Cell.ContinuesBefore;

            public bool ContinuesAfter => Cell.ContinuesAfter;

            public double Start => Cell.StartFraction;

            public double End => Cell.EndFraction;

            public bool IsSelected
            {
                get => _isSelected;
                set => Set(ref _isSelected, value);
            }
        }
    }
}
