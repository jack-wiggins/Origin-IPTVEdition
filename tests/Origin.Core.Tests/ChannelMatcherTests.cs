using System.Collections.Generic;
using Origin.Core.Lineup;
using Origin.Core.SchedulesDirect;
using Xunit;

namespace Origin.Core.Tests
{
    public class ChannelMatcherTests
    {
        internal static SdLineupMap SampleLineup()
        {
            var map = new SdLineupMap { Metadata = new SdLineupMetadata { LineupId = "USA-NY31519-X" } };

            void Add(string id, string callsign, string name, string channel, string affiliate = null)
            {
                map.Stations.Add(new SdStation { StationId = id, Callsign = callsign, Name = name, Affiliate = affiliate });
                map.Map.Add(new SdChannelMapEntry { StationId = id, Channel = channel });
            }

            Add("10003", "WCBS", "WCBS", "002", "CBS");
            Add("10212", "WABC", "WABC", "007", "ABC");
            Add("19621", "WABCDT", "WABC-DT", "507", "ABC");
            Add("32645", "ESPN", "ESPN", "206");
            Add("45507", "ESPN2", "ESPN2", "209");
            Add("32630", "ESPNHD", "ESPN HD", "706");
            Add("82547", "FS1", "Fox Sports 1", "219");
            Add("82548", "FS2", "Fox Sports 2", "618");
            Add("11150", "DSC", "Discovery Channel", "120");
            Add("10240", "HBO", "HBO East", "300");
            Add("10241", "HBOP", "HBO West", "301");
            Add("16615", "AETV", "A&E", "118");
            Add("58646", "CNNHD", "CNN HD", "202");
            return map;
        }

        private static ChannelMatcher CreateMatcher()
        {
            return new ChannelMatcher(ChannelMatcher.FromLineupMap(SampleLineup()));
        }

        [Fact]
        public void Matches_tvg_id_that_is_a_station_id()
        {
            var match = CreateMatcher().Match("Anything", tvgId: "82547");
            Assert.Equal(MatchMethod.StationId, match.Method);
            Assert.Equal("FS1", match.Station.Callsign);
        }

        [Fact]
        public void Matches_tvg_id_callsign_with_country_suffix()
        {
            var match = CreateMatcher().Match("US: ABC 7 New York", tvgId: "WABC.us");
            Assert.Equal(MatchMethod.TvgIdCallsign, match.Method);
            Assert.Equal("10212", match.Station.StationId);
        }

        [Fact]
        public void Matches_broadcast_callsign_in_the_name()
        {
            var match = CreateMatcher().Match("|US| CBS 2 (WCBS) New York ᴴᴰ");
            Assert.Equal(MatchMethod.Callsign, match.Method);
            Assert.Equal("WCBS", match.Station.Callsign);
        }

        [Theory]
        [InlineData("US: ESPN", "ESPN")]
        [InlineData("USA | ESPN FHD", "ESPNHD")]
        [InlineData("US: ESPN 2 HD", "ESPN2")]
        [InlineData("US - Discovery HD", "DSC")]
        [InlineData("A&E", "AETV")]
        [InlineData("US: CNN", "CNNHD")]
        public void Matches_cleaned_up_names(string name, string expectedCallsign)
        {
            var match = CreateMatcher().Match(name);
            Assert.True(match.IsMatch, name + " did not match");
            Assert.Equal(expectedCallsign, match.Station.Callsign);
        }

        [Fact]
        public void Prefers_the_station_on_the_playlist_channel_number()
        {
            var match = CreateMatcher().Match("ESPN", channelNumber: "706");
            Assert.Equal("ESPNHD", match.Station.Callsign);
        }

        [Theory]
        [InlineData("US: Fox Sports 2", "FS2")]
        [InlineData("Fox Sports 1 USA", "FS1")]
        [InlineData("HBO West HD", "HBOP")]
        [InlineData("HBO East", "HBO")]
        public void Fuzzy_matching_respects_numbers_and_feeds(string name, string expectedCallsign)
        {
            var match = CreateMatcher().Match(name);
            Assert.True(match.IsMatch, name + " did not match");
            Assert.Equal(expectedCallsign, match.Station.Callsign);
        }

        [Theory]
        [InlineData("FR: TF1 HD")]
        [InlineData("UK: Sky Sports Main Event")]
        [InlineData("ESPN 3")]
        public void Leaves_unknown_channels_unmatched(string name)
        {
            Assert.False(CreateMatcher().Match(name).IsMatch);
        }

        [Fact]
        public void Ranks_suggestions_for_manual_matching()
        {
            var ranked = CreateMatcher().Rank("Fox Sports", 3);
            Assert.NotEmpty(ranked);
            Assert.StartsWith("FS", ranked[0].Station.Callsign);
        }

        [Theory]
        [InlineData("WABC-DT", "WABC")]
        [InlineData("WLSDT", "WLS")]
        [InlineData("ESPNHD", "ESPN")]
        [InlineData("HBO", "HBO")]
        [InlineData("WHDH", "WHDH")]
        public void Callsign_keys_drop_suffixes(string callsign, string expected)
        {
            Assert.Equal(expected, NameNormalizer.CallsignKey(callsign));
        }

        [Theory]
        [InlineData("US: ESPN FHD", "espn")]
        [InlineData("USA | VIP | The Weather Channel HD", "weather")]
        [InlineData("[UK] BBC One", "bbcone")]
        [InlineData("HBO: Comedy", "hbocomedy")]
        [InlineData("Télé-Québec", "telequebec")]
        public void Normalizes_names(string name, string compact)
        {
            Assert.Equal(compact, NameNormalizer.Normalize(name).Compact);
        }

        [Fact]
        public void Uses_lowest_channel_for_stations_listed_twice()
        {
            var map = new SdLineupMap
            {
                Stations = new List<SdStation> { new SdStation { StationId = "1", Callsign = "WXYZ", Name = "WXYZ" } },
                Map = new List<SdChannelMapEntry>
                {
                    new SdChannelMapEntry { StationId = "1", Channel = "1007" },
                    new SdChannelMapEntry { StationId = "1", Channel = "7" },
                },
            };

            Assert.Equal("7", ChannelMatcher.FromLineupMap(map)[0].Channel);
        }
    }
}
