using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Origin.Core.Iptv;
using Origin.Core.SchedulesDirect;
using Origin.Core.Settings;

namespace Origin.Core.Lineup
{
    /// <summary>
    /// Builds Origin's lineup: copies the playlist's live channels in (playlist order, selected
    /// groups only) and maps each one onto a station in the Schedules Direct lineup.
    /// </summary>
    public static class LineupComposer
    {
        /// <param name="playlist">The downloaded playlist.</param>
        /// <param name="settings">Group selection, numbering and match threshold.</param>
        /// <param name="schedulesDirectMap">The Schedules Direct lineup, or null if there is none.</param>
        /// <param name="schedulesDirectLineupId">The Schedules Direct lineup's ID, e.g. "USA-OTA-60030".</param>
        /// <param name="schedulesDirectLineupName">The Schedules Direct lineup's display name.</param>
        /// <param name="previous">The current lineup, so hidden channels and manual matches survive.</param>
        /// <param name="keepPreviousMatches">
        /// When <paramref name="schedulesDirectMap"/> is null because it could not be downloaded
        /// (rather than because none is chosen), keep the matches the previous lineup had.
        /// </param>
        public static OriginLineup Compose(
            PlaylistSnapshot playlist,
            LineupSettings settings,
            SdLineupMap schedulesDirectMap,
            string schedulesDirectLineupId,
            string schedulesDirectLineupName,
            OriginLineup previous,
            bool keepPreviousMatches = false)
        {
            var previousByKey = new Dictionary<string, OriginChannel>(StringComparer.Ordinal);
            foreach (var channel in previous?.Channels ?? new List<OriginChannel>())
            {
                if (channel.Key != null && !previousByKey.ContainsKey(channel.Key))
                {
                    previousByKey.Add(channel.Key, channel);
                }
            }

            var matcher = schedulesDirectMap != null
                ? new ChannelMatcher(ChannelMatcher.FromLineupMap(schedulesDirectMap), settings.FuzzyMatchThreshold)
                : null;

            var channels = new List<OriginChannel>();
            var preferredNumbers = new Dictionary<OriginChannel, string>();

            foreach (var source in playlist.Channels.Where(c => c.Kind == ContentKind.Live).OrderBy(c => c.Order))
            {
                if (!settings.IsGroupIncluded(source.Group))
                {
                    continue;
                }

                previousByKey.TryGetValue(source.Key, out var earlier);

                var channel = new OriginChannel
                {
                    Key = source.Key,
                    Name = source.Name,
                    Group = source.Group,
                    TvgId = source.TvgId,
                    LogoUrl = source.LogoUrl,
                    PlaylistOrder = source.Order,
                    Stream = source.Stream,
                    PlayerOptions = source.PlayerOptions,
                    Enabled = earlier?.Enabled ?? true,
                    Override = earlier?.Override ?? MappingOverride.None,
                };

                channel.Mapping = ResolveMapping(source, channel.Override, earlier, matcher, keepPreviousMatches);
                if (channel.Override == MappingOverride.Station && channel.Mapping == null)
                {
                    channel.Override = MappingOverride.None;
                }

                channels.Add(channel);
                preferredNumbers[channel] = settings.Numbering == ChannelNumbering.SchedulesDirect
                    ? channel.Mapping?.LineupChannel ?? source.ChannelNumber
                    : source.ChannelNumber;
            }

            AssignNumbers(channels, preferredNumbers, settings.Numbering);

            var hasLineup = schedulesDirectMap != null || keepPreviousMatches;
            return new OriginLineup
            {
                UpdatedUtc = DateTime.UtcNow,
                PlaylistDownloadedUtc = playlist.DownloadedUtc,
                PlaylistSource = playlist.Source,
                SchedulesDirectLineupId = hasLineup ? schedulesDirectLineupId : null,
                SchedulesDirectLineupName = hasLineup ? schedulesDirectLineupName : null,
                Channels = channels
                    .OrderBy(c => ChannelNumber.SortKey(c.Number))
                    .ThenBy(c => c.PlaylistOrder)
                    .ToList(),
            };
        }

        private static ChannelMapping ResolveMapping(
            PlaylistChannel source,
            MappingOverride mappingOverride,
            OriginChannel earlier,
            ChannelMatcher matcher,
            bool keepPreviousMatches)
        {
            if (mappingOverride == MappingOverride.NoMatch)
            {
                return null;
            }

            if (mappingOverride == MappingOverride.Station && earlier?.Mapping != null)
            {
                // Respect the user's pick; refresh its details if the station is still in the lineup.
                var station = matcher?.FindStation(earlier.Mapping.StationId);
                if (station != null)
                {
                    return station.ToMapping(MatchMethod.Manual, 1.0);
                }

                return matcher == null || keepPreviousMatches ? earlier.Mapping : null;
            }

            if (matcher == null)
            {
                return keepPreviousMatches ? earlier?.Mapping : null;
            }

            var match = matcher.Match(source.Name, source.TvgId, source.TvgName, source.ChannelNumber);
            return match.IsMatch ? match.Station.ToMapping(match.Method, match.Confidence) : null;
        }

        /// <summary>
        /// Each channel gets its preferred number (tvg-chno, or the Schedules Direct channel) if it
        /// is free; the rest are numbered in playlist order from the first free number.
        /// </summary>
        private static void AssignNumbers(List<OriginChannel> channels, Dictionary<OriginChannel, string> preferred, ChannelNumbering numbering)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var channel in channels)
            {
                var number = ChannelNumber.Normalize(preferred[channel]);
                if (number != null && used.Add(number))
                {
                    channel.Number = number;
                }
            }

            var next = 1;
            if (numbering == ChannelNumbering.SchedulesDirect && used.Count > 0)
            {
                // Keep unmatched channels clear of the provider's numbering.
                next = used.Max(ChannelNumber.Major) + 1;
            }

            foreach (var channel in channels.Where(c => c.Number == null))
            {
                while (used.Contains(next.ToString(CultureInfo.InvariantCulture)))
                {
                    next++;
                }

                channel.Number = next.ToString(CultureInfo.InvariantCulture);
                used.Add(channel.Number);
                next++;
            }
        }
    }
}
