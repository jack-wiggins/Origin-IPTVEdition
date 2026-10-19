using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Origin.Core.Lineup;
using Origin.Infrastructure;
using Origin.Input;
using Origin.Navigation;

namespace Origin.Views
{
    /// <summary>
    /// The channel guide. Program data from Schedules Direct is not downloaded yet, so each row
    /// shows which station the channel is matched to; the grid is laid out for schedules later.
    /// </summary>
    public partial class GuidePage : OriginPage
    {
        private readonly DispatcherTimer _clock;
        private readonly ChannelNumberEntry _numberEntry;
        private List<GuideRow> _rows = new List<GuideRow>();

        public GuidePage(IShell shell) : base(shell)
        {
            InitializeComponent();

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _clock.Tick += (s, e) => UpdateTimeRuler();

            _numberEntry = new ChannelNumberEntry(Dispatcher, NumberEntry, NumberText, JumpToNumber);
            Services.Lineups.Changed += OnLineupChanged;
        }

        public override string Title => "guide";

        public override void OnNavigatedTo(bool isBack)
        {
            if (!isBack)
            {
                Load(Services.Settings.Current.Playback.LastChannelKey);
            }

            UpdateTimeRuler();
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
                    Watch(ChannelList.SelectedItem as GuideRow);
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

            if (hasRows)
            {
                var index = Math.Max(0, _rows.FindIndex(r => r.Key == selectKey));
                ChannelList.SelectedIndex = index;
                ChannelList.ScrollIntoView(_rows[index]);
            }
        }

        private void OnLineupChanged(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var selected = (ChannelList.SelectedItem as GuideRow)?.Key;
                Load(selected);
            }));
        }

        private void UpdateTimeRuler()
        {
            var now = DateTime.Now;
            var slot = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute < 30 ? 0 : 30, 0);
            DayText.Text = now.ToString("dddd");
            Slot0.Text = slot.ToString("t");
            Slot1.Text = slot.AddMinutes(30).ToString("t");
            Slot2.Text = slot.AddMinutes(60).ToString("t");
        }

        private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(ChannelList.SelectedItem is GuideRow row))
            {
                return;
            }

            DetailTitle.Text = row.Number + "   " + row.Name;
            var mapping = row.Channel.Mapping;
            DetailText.Text = mapping != null
                ? "Schedules Direct: " + mapping.Callsign
                  + (string.IsNullOrEmpty(mapping.StationName) || mapping.StationName == mapping.Callsign ? string.Empty : " · " + mapping.StationName)
                  + (string.IsNullOrEmpty(mapping.Affiliate) ? string.Empty : " (" + mapping.Affiliate + ")")
                  + (string.IsNullOrEmpty(mapping.LineupChannel) ? string.Empty : " · lineup channel " + mapping.LineupChannel)
                  + "     Group: " + row.Channel.Group
                : "Not matched to Schedules Direct     Group: " + row.Channel.Group;
        }

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

        private sealed class GuideRow
        {
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

            public string ProgramTitle => Channel.IsMapped
                ? Channel.Mapping.StationName ?? Channel.Mapping.Callsign
                : Channel.Name;

            public string ProgramDetail => Channel.IsMapped
                ? "Program information will appear once guide data is downloaded"
                : "No guide match · choose one under settings › channel mapping";
        }
    }
}
