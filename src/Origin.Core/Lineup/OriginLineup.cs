using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Origin.Core.Iptv;

namespace Origin.Core.Lineup
{
    /// <summary>How a playlist channel was matched to a Schedules Direct station.</summary>
    public enum MatchMethod
    {
        None,

        /// <summary>The playlist's tvg-id is the Schedules Direct station ID.</summary>
        StationId,

        /// <summary>The tvg-id carries the callsign, e.g. "WABC.us".</summary>
        TvgIdCallsign,

        /// <summary>The tvg-id carries the station name, e.g. "ESPN.us".</summary>
        TvgIdName,

        /// <summary>A broadcast callsign appears in the channel name, e.g. "ABC 7 (WABC)".</summary>
        Callsign,

        /// <summary>The cleaned-up channel name equals the station name or callsign.</summary>
        ExactName,

        /// <summary>The names are similar enough to pass the fuzzy-match threshold.</summary>
        Fuzzy,

        /// <summary>The user picked the station.</summary>
        Manual,
    }

    /// <summary>A choice the user made that automatic matching must not undo.</summary>
    public enum MappingOverride
    {
        None,

        /// <summary>The user picked the station by hand.</summary>
        Station,

        /// <summary>The user said this channel has no Schedules Direct station.</summary>
        NoMatch,
    }

    /// <summary>The Schedules Direct station a channel takes its guide data from.</summary>
    public sealed class ChannelMapping
    {
        public string StationId { get; set; }

        public string Callsign { get; set; }

        public string StationName { get; set; }

        public string Affiliate { get; set; }

        /// <summary>The station's channel number in the Schedules Direct lineup.</summary>
        public string LineupChannel { get; set; }

        public string LogoUrl { get; set; }

        public MatchMethod Method { get; set; }

        /// <summary>0-1. 1 for exact identifiers and manual choices.</summary>
        public double Confidence { get; set; }
    }

    /// <summary>A channel in Origin's lineup: a live playlist entry plus its Schedules Direct mapping.</summary>
    public sealed class OriginChannel
    {
        /// <summary>Same as <see cref="PlaylistChannel.Key"/>.</summary>
        public string Key { get; set; }

        /// <summary>The number shown in the guide and used for direct tuning, e.g. "206" or "7.1".</summary>
        public string Number { get; set; }

        public string Name { get; set; }

        public string Group { get; set; }

        public string TvgId { get; set; }

        public string LogoUrl { get; set; }

        public int PlaylistOrder { get; set; }

        /// <summary>Hidden channels stay in the lineup but are left out of the guide.</summary>
        public bool Enabled { get; set; } = true;

        public StreamReference Stream { get; set; }

        public List<string> PlayerOptions { get; set; }

        public ChannelMapping Mapping { get; set; }

        public MappingOverride Override { get; set; }

        [JsonIgnore]
        public bool IsMapped => Mapping?.StationId != null;

        /// <summary>The playlist logo, falling back to the Schedules Direct station logo.</summary>
        [JsonIgnore]
        public string DisplayLogoUrl => !string.IsNullOrWhiteSpace(LogoUrl) ? LogoUrl : Mapping?.LogoUrl;
    }

    /// <summary>Origin's lineup: the M3U's live channels, copied in and mapped onto a Schedules Direct lineup.</summary>
    public sealed class OriginLineup
    {
        public int Version { get; set; } = 1;

        public DateTime UpdatedUtc { get; set; }

        public DateTime? PlaylistDownloadedUtc { get; set; }

        public string PlaylistSource { get; set; }

        public string SchedulesDirectLineupId { get; set; }

        public string SchedulesDirectLineupName { get; set; }

        /// <summary>Sorted by channel number.</summary>
        public List<OriginChannel> Channels { get; set; } = new List<OriginChannel>();

        [JsonIgnore]
        public int MappedCount => Channels.Count(c => c.IsMapped);

        [JsonIgnore]
        public int UnmappedCount => Channels.Count(c => !c.IsMapped);

        [JsonIgnore]
        public bool IsEmpty => Channels.Count == 0;
    }
}
