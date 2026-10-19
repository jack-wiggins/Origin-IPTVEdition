using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;

namespace Origin.Core.SchedulesDirect
{
    // Shapes of the Schedules Direct JSON API (version 20141201). Only the fields Origin uses are mapped.

    public sealed class SdTokenResponse
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("token")]
        public string Token { get; set; }

        /// <summary>Unix time in seconds.</summary>
        [JsonProperty("tokenExpires")]
        public long? TokenExpires { get; set; }
    }

    public sealed class SdStatus
    {
        [JsonProperty("account")]
        public SdAccount Account { get; set; }

        [JsonProperty("lineups")]
        public List<SdStatusLineup> Lineups { get; set; } = new List<SdStatusLineup>();

        [JsonProperty("lastDataUpdate")]
        public DateTime? LastDataUpdate { get; set; }

        [JsonProperty("systemStatus")]
        public List<SdSystemStatus> SystemStatus { get; set; } = new List<SdSystemStatus>();
    }

    public sealed class SdAccount
    {
        [JsonProperty("expires")]
        public DateTime? Expires { get; set; }

        [JsonProperty("maxLineups")]
        public int MaxLineups { get; set; }

        [JsonProperty("messages")]
        public List<SdAccountMessage> Messages { get; set; } = new List<SdAccountMessage>();
    }

    public sealed class SdAccountMessage
    {
        [JsonProperty("msgID")]
        public string Id { get; set; }

        [JsonProperty("date")]
        public DateTime? Date { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public sealed class SdStatusLineup
    {
        [JsonProperty("lineup")]
        public string LineupId { get; set; }

        [JsonProperty("modified")]
        public DateTime? Modified { get; set; }

        [JsonProperty("isDeleted")]
        public bool IsDeleted { get; set; }
    }

    public sealed class SdSystemStatus
    {
        [JsonProperty("date")]
        public DateTime? Date { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public sealed class SdCountry
    {
        public string Region { get; set; }

        [JsonProperty("fullName")]
        public string FullName { get; set; }

        /// <summary>The ISO 3166-1 alpha-3 code the API expects, e.g. "USA", "CAN", "GBR".</summary>
        [JsonProperty("shortName")]
        public string ShortName { get; set; }

        [JsonProperty("postalCodeExample")]
        public string PostalCodeExample { get; set; }

        [JsonProperty("postalCode")]
        public string PostalCodePattern { get; set; }

        [JsonProperty("onePostalCode")]
        public bool OnePostalCode { get; set; }

        public override string ToString() => FullName;
    }

    public sealed class SdHeadend
    {
        [JsonProperty("headend")]
        public string HeadendId { get; set; }

        /// <summary>"Cable", "Satellite", "Antenna", "IPTV" ...</summary>
        [JsonProperty("transport")]
        public string Transport { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("lineups")]
        public List<SdHeadendLineup> Lineups { get; set; } = new List<SdHeadendLineup>();
    }

    public sealed class SdHeadendLineup
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("lineup")]
        public string LineupId { get; set; }
    }

    public sealed class SdLineupList
    {
        [JsonProperty("lineups")]
        public List<SdLineupSummary> Lineups { get; set; } = new List<SdLineupSummary>();
    }

    /// <summary>A lineup that has been added to the account.</summary>
    public sealed class SdLineupSummary
    {
        [JsonProperty("lineup")]
        public string LineupId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("transport")]
        public string Transport { get; set; }

        [JsonProperty("location")]
        public string Location { get; set; }

        [JsonProperty("isDeleted")]
        public bool IsDeleted { get; set; }
    }

    public sealed class SdChangeResponse
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>Lineup additions left today. Schedules Direct limits these per 24 hours.</summary>
        [JsonProperty("changesRemaining")]
        public int? ChangesRemaining { get; set; }
    }

    /// <summary>GET /lineups/{id}: which station is on which channel, and the stations themselves.</summary>
    public sealed class SdLineupMap
    {
        [JsonProperty("map")]
        public List<SdChannelMapEntry> Map { get; set; } = new List<SdChannelMapEntry>();

        [JsonProperty("stations")]
        public List<SdStation> Stations { get; set; } = new List<SdStation>();

        [JsonProperty("metadata")]
        public SdLineupMetadata Metadata { get; set; }

        /// <summary>When Origin downloaded this copy (not part of the API response).</summary>
        [JsonProperty("originDownloadedUtc")]
        public DateTime? DownloadedUtc { get; set; }
    }

    public sealed class SdChannelMapEntry
    {
        [JsonProperty("stationID")]
        public string StationId { get; set; }

        [JsonProperty("channel")]
        public string Channel { get; set; }

        [JsonProperty("atscMajor")]
        public int? AtscMajor { get; set; }

        [JsonProperty("atscMinor")]
        public int? AtscMinor { get; set; }

        [JsonProperty("uhfVhf")]
        public int? UhfVhf { get; set; }

        /// <summary>The number a viewer would tune: "704", or "7.1" for over-the-air.</summary>
        [JsonIgnore]
        public string DisplayChannel
        {
            get
            {
                if (AtscMajor.HasValue && AtscMinor.HasValue)
                {
                    return AtscMajor.Value.ToString(CultureInfo.InvariantCulture) + "." + AtscMinor.Value.ToString(CultureInfo.InvariantCulture);
                }

                if (!string.IsNullOrWhiteSpace(Channel))
                {
                    return Channel.Trim();
                }

                return UhfVhf?.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class SdStation
    {
        [JsonProperty("stationID")]
        public string StationId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("callsign")]
        public string Callsign { get; set; }

        /// <summary>Network affiliation for broadcast stations, e.g. "ABC".</summary>
        [JsonProperty("affiliate")]
        public string Affiliate { get; set; }

        [JsonProperty("broadcastLanguage")]
        public List<string> BroadcastLanguage { get; set; }

        [JsonProperty("logo")]
        public SdLogo Logo { get; set; }

        [JsonProperty("stationLogo")]
        public List<SdLogo> StationLogos { get; set; }

        /// <summary>Prefers the logo variant drawn for dark backgrounds, which suits Origin's interface.</summary>
        [JsonIgnore]
        public string BestLogoUrl
        {
            get
            {
                var logos = StationLogos ?? new List<SdLogo>();
                var preferred = logos.FirstOrDefault(l => string.Equals(l.Category, "dark", StringComparison.OrdinalIgnoreCase))
                                ?? logos.FirstOrDefault(l => string.Equals(l.Category, "white", StringComparison.OrdinalIgnoreCase))
                                ?? logos.FirstOrDefault();
                return preferred?.Url ?? Logo?.Url;
            }
        }
    }

    public sealed class SdLogo
    {
        [JsonProperty("URL")]
        public string Url { get; set; }

        [JsonProperty("height")]
        public int Height { get; set; }

        [JsonProperty("width")]
        public int Width { get; set; }

        [JsonProperty("category")]
        public string Category { get; set; }
    }

    public sealed class SdLineupMetadata
    {
        [JsonProperty("lineup")]
        public string LineupId { get; set; }

        [JsonProperty("modified")]
        public DateTime? Modified { get; set; }

        [JsonProperty("transport")]
        public string Transport { get; set; }
    }
}
