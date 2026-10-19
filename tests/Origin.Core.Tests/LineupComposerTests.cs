using System;
using System.Collections.Generic;
using System.Linq;
using Origin.Core.Iptv;
using Origin.Core.Lineup;
using Origin.Core.Settings;
using Xunit;

namespace Origin.Core.Tests
{
    public class LineupComposerTests
    {
        private static PlaylistSnapshot Playlist(params PlaylistChannel[] channels)
        {
            for (var i = 0; i < channels.Length; i++)
            {
                channels[i].Order = i;
                channels[i].Key = channels[i].Key ?? "xc:live:" + (100 + i);
                channels[i].Stream = new StreamReference { BaseUrl = "http://host", Segment = "live", StreamId = (100 + i).ToString(), Extension = "ts" };
            }

            return new PlaylistSnapshot { DownloadedUtc = DateTime.UtcNow, Source = "host", TotalEntries = channels.Length, Channels = channels.ToList() };
        }

        private static PlaylistChannel Live(string name, string group = "US", string number = null, string tvgId = null)
        {
            return new PlaylistChannel { Name = name, Group = group, ChannelNumber = number, TvgId = tvgId, Kind = ContentKind.Live };
        }

        [Fact]
        public void Copies_live_channels_in_playlist_order_and_maps_them()
        {
            var playlist = Playlist(
                Live("US: ESPN"),
                Live("US: Fox Sports 1"),
                Live("Unknown Channel"),
                new PlaylistChannel { Name = "A Movie", Group = "Movies", Kind = ContentKind.Movie });

            var lineup = LineupComposer.Compose(playlist, new LineupSettings(), ChannelMatcherTests.SampleLineup(), "USA-NY31519-X", "Cable", null);

            Assert.Equal(new[] { "1", "2", "3" }, lineup.Channels.Select(c => c.Number));
            Assert.Equal(new[] { "US: ESPN", "US: Fox Sports 1", "Unknown Channel" }, lineup.Channels.Select(c => c.Name));
            Assert.Equal("ESPN", lineup.Channels[0].Mapping.Callsign);
            Assert.Equal("FS1", lineup.Channels[1].Mapping.Callsign);
            Assert.Null(lineup.Channels[2].Mapping);
            Assert.Equal(2, lineup.MappedCount);
            Assert.Equal("USA-NY31519-X", lineup.SchedulesDirectLineupId);
        }

        [Fact]
        public void Honours_group_selection()
        {
            var playlist = Playlist(Live("ESPN", "US Sports"), Live("BBC One", "UK"), Live("New One", "Brand New Group"));
            var settings = new LineupSettings
            {
                IncludeNewGroups = false,
                Groups = new Dictionary<string, bool> { ["US Sports"] = true, ["UK"] = false },
            };

            var lineup = LineupComposer.Compose(playlist, settings, null, null, null, null);

            Assert.Equal("ESPN", lineup.Channels.Single().Name);
        }

        [Fact]
        public void Uses_tvg_chno_then_fills_gaps_in_order()
        {
            var playlist = Playlist(Live("A", number: "2"), Live("B"), Live("C", number: "2"), Live("D", number: "007"));

            var lineup = LineupComposer.Compose(playlist, new LineupSettings(), null, null, null, null);

            var numbers = lineup.Channels.ToDictionary(c => c.Name, c => c.Number);
            Assert.Equal("2", numbers["A"]);
            Assert.Equal("1", numbers["B"]);
            Assert.Equal("3", numbers["C"]);
            Assert.Equal("7", numbers["D"]);
            Assert.Equal(new[] { "1", "2", "3", "7" }, lineup.Channels.Select(c => c.Number));
        }

        [Fact]
        public void Can_number_channels_like_the_schedules_direct_lineup()
        {
            var playlist = Playlist(Live("US: ESPN"), Live("Unknown"), Live("US: Fox Sports 1"));
            var settings = new LineupSettings { Numbering = ChannelNumbering.SchedulesDirect };

            var lineup = LineupComposer.Compose(playlist, settings, ChannelMatcherTests.SampleLineup(), "id", "name", null);

            var numbers = lineup.Channels.ToDictionary(c => c.Name, c => c.Number);
            Assert.Equal("206", numbers["US: ESPN"]);
            Assert.Equal("219", numbers["US: Fox Sports 1"]);
            Assert.Equal("220", numbers["Unknown"]);
        }

        [Fact]
        public void Keeps_manual_choices_and_hidden_channels_across_updates()
        {
            var playlist = Playlist(Live("US: ESPN"), Live("Mystery Sports"), Live("US: Fox Sports 1"));
            var sd = ChannelMatcherTests.SampleLineup();
            var first = LineupComposer.Compose(playlist, new LineupSettings(), sd, "id", "name", null);

            var mystery = first.Channels.Single(c => c.Name == "Mystery Sports");
            mystery.Override = MappingOverride.Station;
            mystery.Mapping = new ChannelMapping { StationId = "45507", Callsign = "ESPN2", Method = MatchMethod.Manual, Confidence = 1 };

            var fs1 = first.Channels.Single(c => c.Name == "US: Fox Sports 1");
            fs1.Override = MappingOverride.NoMatch;
            fs1.Mapping = null;
            fs1.Enabled = false;

            var second = LineupComposer.Compose(playlist, new LineupSettings(), sd, "id", "name", first);

            var mysteryAgain = second.Channels.Single(c => c.Name == "Mystery Sports");
            Assert.Equal(MatchMethod.Manual, mysteryAgain.Mapping.Method);
            Assert.Equal("ESPN2", mysteryAgain.Mapping.Callsign);
            Assert.Equal("209", mysteryAgain.Mapping.LineupChannel);

            var fs1Again = second.Channels.Single(c => c.Name == "US: Fox Sports 1");
            Assert.Null(fs1Again.Mapping);
            Assert.False(fs1Again.Enabled);
        }

        [Fact]
        public void Keeps_previous_matches_when_schedules_direct_is_unreachable()
        {
            var playlist = Playlist(Live("US: ESPN"));
            var first = LineupComposer.Compose(playlist, new LineupSettings(), ChannelMatcherTests.SampleLineup(), "id", "name", null);

            var offline = LineupComposer.Compose(playlist, new LineupSettings(), null, "id", "name", first, keepPreviousMatches: true);
            var notConfigured = LineupComposer.Compose(playlist, new LineupSettings(), null, null, null, first);

            Assert.Equal("ESPN", offline.Channels.Single().Mapping.Callsign);
            Assert.Null(notConfigured.Channels.Single().Mapping);
        }

        [Theory]
        [InlineData("002", "2")]
        [InlineData("7-1", "7.1")]
        [InlineData("7.01", "7.1")]
        [InlineData("0", "0")]
        [InlineData("abc", null)]
        [InlineData("", null)]
        public void Normalizes_channel_numbers(string input, string expected)
        {
            Assert.Equal(expected, ChannelNumber.Normalize(input));
        }
    }
}
