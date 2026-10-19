using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Origin.Core.SchedulesDirect
{
    // Schedules and program metadata from the Schedules Direct JSON API (version 20141201).
    // Only the fields Origin uses are mapped.

    /// <summary>One station and the UTC dates ("yyyy-MM-dd") wanted from POST /schedules or /schedules/md5.</summary>
    public sealed class SdScheduleRequest
    {
        public SdScheduleRequest()
        {
        }

        public SdScheduleRequest(string stationId, IEnumerable<string> dates)
        {
            StationId = stationId;
            Dates = dates?.ToList();
        }

        [JsonProperty("stationID")]
        public string StationId { get; set; }

        [JsonProperty("date")]
        public List<string> Dates { get; set; }
    }

    /// <summary>
    /// The fingerprint of one station's schedule for one day (POST /schedules/md5). It changes
    /// whenever anything in that day's schedule changes.
    /// </summary>
    public sealed class SdScheduleMd5
    {
        public string StationId { get; set; }

        /// <summary>UTC date, "yyyy-MM-dd".</summary>
        public string Date { get; set; }

        /// <summary>0 when the day is available; otherwise e.g. 7100 (SCHEDULE_QUEUED).</summary>
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("lastModified")]
        public DateTime? LastModified { get; set; }

        [JsonProperty("md5")]
        public string Md5 { get; set; }
    }

    /// <summary>One station's schedule for one day (an element of the POST /schedules response).</summary>
    public sealed class SdStationSchedule
    {
        [JsonProperty("stationID")]
        public string StationId { get; set; }

        [JsonProperty("programs")]
        public List<SdScheduledProgram> Programs { get; set; } = new List<SdScheduledProgram>();

        [JsonProperty("metadata")]
        public SdScheduleMetadata Metadata { get; set; }

        /// <summary>Non-zero when this station-day could not be returned, e.g. 7100 (SCHEDULE_QUEUED).</summary>
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }
    }

    public sealed class SdScheduleMetadata
    {
        [JsonProperty("modified")]
        public DateTime? Modified { get; set; }

        [JsonProperty("md5")]
        public string Md5 { get; set; }

        /// <summary>The UTC date this schedule covers, "yyyy-MM-dd".</summary>
        [JsonProperty("startDate")]
        public string StartDate { get; set; }
    }

    /// <summary>A program in a station's schedule: what airs, when, and how.</summary>
    public sealed class SdScheduledProgram
    {
        [JsonProperty("programID")]
        public string ProgramId { get; set; }

        [JsonProperty("airDateTime")]
        public DateTime AirDateTime { get; set; }

        /// <summary>Seconds.</summary>
        [JsonProperty("duration")]
        public int Duration { get; set; }

        /// <summary>The md5 of the program's details, as returned by POST /programs.</summary>
        [JsonProperty("md5")]
        public string Md5 { get; set; }

        [JsonProperty("new")]
        public bool New { get; set; }

        /// <summary>"Live", "Tape" or "Delay".</summary>
        [JsonProperty("liveTapeDelay")]
        public string LiveTapeDelay { get; set; }

        /// <summary>"Series Premiere", "Season Premiere", "Season Finale", "Series Finale" ...</summary>
        [JsonProperty("isPremiereOrFinale")]
        public string IsPremiereOrFinale { get; set; }

        [JsonProperty("premiere")]
        public bool Premiere { get; set; }

        [JsonProperty("ratings")]
        public List<SdRating> Ratings { get; set; }

        /// <summary>"hdtv", "uhdtv", "letterbox" ...</summary>
        [JsonProperty("videoProperties")]
        public List<string> VideoProperties { get; set; }

        /// <summary>"stereo", "cc", "DD 5.1" ...</summary>
        [JsonProperty("audioProperties")]
        public List<string> AudioProperties { get; set; }
    }

    public sealed class SdRating
    {
        /// <summary>The rating body, e.g. "USA Parental Rating" or "Motion Picture Association of America".</summary>
        [JsonProperty("body")]
        public string Body { get; set; }

        /// <summary>e.g. "TV14", "PG-13".</summary>
        [JsonProperty("code")]
        public string Code { get; set; }
    }

    /// <summary>A program's details (an element of the POST /programs response).</summary>
    public sealed class SdProgram
    {
        [JsonProperty("programID")]
        public string ProgramId { get; set; }

        [JsonProperty("md5")]
        public string Md5 { get; set; }

        [JsonProperty("titles")]
        public List<SdProgramTitle> Titles { get; set; }

        [JsonProperty("episodeTitle150")]
        public string EpisodeTitle { get; set; }

        [JsonProperty("descriptions")]
        public SdProgramDescriptions Descriptions { get; set; }

        /// <summary>"yyyy-MM-dd".</summary>
        [JsonProperty("originalAirDate")]
        public string OriginalAirDate { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; }

        /// <summary>"Series", "Feature Film", "Sports event", "Special" ...</summary>
        [JsonProperty("showType")]
        public string ShowType { get; set; }

        /// <summary>"Episode", "Movie", "Show" or "Sports".</summary>
        [JsonProperty("entityType")]
        public string EntityType { get; set; }

        /// <summary>Season and episode numbers by source, e.g. [{"Gracenote": {"season": 2, "episode": 6}}].</summary>
        [JsonProperty("metadata")]
        public List<JObject> Metadata { get; set; }

        [JsonProperty("movie")]
        public SdMovieInfo Movie { get; set; }

        [JsonProperty("contentRating")]
        public List<SdRating> ContentRating { get; set; }

        /// <summary>Non-zero when the program could not be returned, e.g. 6001 (PROGRAMID_QUEUED).</summary>
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("response")]
        public string Response { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonIgnore]
        public string Title => Titles?.Select(t => t.Title120).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))?.Trim();

        /// <summary>Gracenote's numbering when there is one, otherwise the first source that has a season or episode.</summary>
        [JsonIgnore]
        public SdEpisodeNumbers EpisodeNumbers
        {
            get
            {
                // Read by hand: other sources use other shapes, and one odd entry must not lose the whole batch.
                var sources = (Metadata ?? new List<JObject>())
                    .Where(m => m != null)
                    .SelectMany(m => m.Properties())
                    .Where(p => p.Value is JObject)
                    .Select(p => new { Source = p.Name, Numbers = ReadNumbers((JObject)p.Value) })
                    .Where(s => s.Numbers.Season.HasValue || s.Numbers.Episode.HasValue)
                    .ToList();
                return (sources.FirstOrDefault(s => string.Equals(s.Source, "Gracenote", StringComparison.OrdinalIgnoreCase)) ?? sources.FirstOrDefault())?.Numbers;
            }
        }

        private static SdEpisodeNumbers ReadNumbers(JObject value)
        {
            int? Number(string name)
            {
                var token = value[name];
                return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.String)
                       && int.TryParse((string)token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                    ? number
                    : (int?)null;
            }

            return new SdEpisodeNumbers { Season = Number("season"), Episode = Number("episode") };
        }
    }

    public sealed class SdProgramTitle
    {
        [JsonProperty("title120")]
        public string Title120 { get; set; }
    }

    public sealed class SdProgramDescriptions
    {
        [JsonProperty("description100")]
        public List<SdDescription> Short { get; set; }

        [JsonProperty("description1000")]
        public List<SdDescription> Long { get; set; }
    }

    public sealed class SdDescription
    {
        /// <summary>ISO 639-1, e.g. "en".</summary>
        [JsonProperty("descriptionLanguage")]
        public string Language { get; set; }

        [JsonProperty("description")]
        public string Text { get; set; }
    }

    public sealed class SdEpisodeNumbers
    {
        public int? Season { get; set; }

        public int? Episode { get; set; }
    }

    public sealed class SdMovieInfo
    {
        [JsonProperty("year")]
        public string Year { get; set; }

        /// <summary>Seconds.</summary>
        [JsonProperty("duration")]
        public int? Duration { get; set; }
    }
}
