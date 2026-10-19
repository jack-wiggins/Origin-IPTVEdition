using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Origin.Core.SchedulesDirect;

namespace Origin.Core.Guide
{
    /// <summary>A program's details: what the guide shows for every airing of it.</summary>
    public sealed class GuideProgram
    {
        public string ProgramId { get; set; }

        /// <summary>Schedules Direct's fingerprint; when an airing carries a different one, the details are downloaded again.</summary>
        public string Md5 { get; set; }

        public string Title { get; set; }

        public string EpisodeTitle { get; set; }

        public string Description { get; set; }

        public int? Season { get; set; }

        public int? Episode { get; set; }

        public DateTime? OriginalAirDate { get; set; }

        public List<string> Genres { get; set; }

        /// <summary>"Episode", "Movie", "Show" or "Sports".</summary>
        public string EntityType { get; set; }

        public int? MovieYear { get; set; }

        /// <summary>e.g. "TV14" or "PG-13".</summary>
        public string ContentRating { get; set; }

        [JsonIgnore]
        public bool IsMovie => string.Equals(EntityType, "Movie", StringComparison.OrdinalIgnoreCase)
                               || (ProgramId != null && ProgramId.StartsWith("MV", StringComparison.Ordinal));

        /// <summary>
        /// The line under the title: "S2 E6 · The Poisoned Sausage", "Movie · 1990" or the genres.
        /// Empty when there is nothing to add.
        /// </summary>
        [JsonIgnore]
        public string EpisodeLine
        {
            get
            {
                var parts = new List<string>();
                if (Season.HasValue && Episode.HasValue)
                {
                    parts.Add("S" + Season.Value.ToString(CultureInfo.CurrentCulture) + " E" + Episode.Value.ToString(CultureInfo.CurrentCulture));
                }
                else if (Episode.HasValue)
                {
                    parts.Add("Episode " + Episode.Value.ToString(CultureInfo.CurrentCulture));
                }

                if (!string.IsNullOrWhiteSpace(EpisodeTitle))
                {
                    parts.Add(EpisodeTitle);
                }

                if (parts.Count == 0 && IsMovie)
                {
                    parts.Add(MovieYear.HasValue ? "Movie · " + MovieYear.Value.ToString(CultureInfo.InvariantCulture) : "Movie");
                }

                if (parts.Count == 0 && Genres != null && Genres.Count > 0)
                {
                    parts.Add(string.Join(", ", Genres.Take(2)));
                }

                return string.Join(" · ", parts);
            }
        }

        /// <summary>Converts a POST /programs element. The description prefers <paramref name="language"/>, then English.</summary>
        public static GuideProgram FromSchedulesDirect(SdProgram program, string language = null)
        {
            var numbers = program.EpisodeNumbers;
            var year = program.Movie?.Year;
            return new GuideProgram
            {
                ProgramId = program.ProgramId,
                Md5 = program.Md5,
                Title = program.Title,
                EpisodeTitle = string.IsNullOrWhiteSpace(program.EpisodeTitle) ? null : program.EpisodeTitle.Trim(),
                Description = PickDescription(program.Descriptions?.Long, language) ?? PickDescription(program.Descriptions?.Short, language),
                Season = numbers?.Season,
                Episode = numbers?.Episode,
                OriginalAirDate = DateTime.TryParseExact(program.OriginalAirDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var aired)
                    ? aired
                    : (DateTime?)null,
                Genres = program.Genres != null && program.Genres.Count > 0 ? program.Genres : null,
                EntityType = program.EntityType,
                MovieYear = int.TryParse(year, NumberStyles.Integer, CultureInfo.InvariantCulture, out var movieYear) ? movieYear : (int?)null,
                ContentRating = program.ContentRating?.Select(r => r.Code).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
            };
        }

        private static string PickDescription(List<SdDescription> descriptions, string language)
        {
            var usable = (descriptions ?? new List<SdDescription>()).Where(d => !string.IsNullOrWhiteSpace(d?.Text)).ToList();
            var chosen = (language == null ? null : usable.FirstOrDefault(d => string.Equals(d.Language, language, StringComparison.OrdinalIgnoreCase)))
                         ?? usable.FirstOrDefault(d => string.Equals(d.Language, "en", StringComparison.OrdinalIgnoreCase))
                         ?? usable.FirstOrDefault();
            return chosen?.Text.Trim();
        }
    }

    /// <summary>One entry in a station's schedule: a program at a time.</summary>
    public sealed class GuideAiring
    {
        public string ProgramId { get; set; }

        public DateTime StartUtc { get; set; }

        /// <summary>Seconds.</summary>
        public int Duration { get; set; }

        /// <summary>The md5 of the program's details at the time the schedule was downloaded.</summary>
        public string ProgramMd5 { get; set; }

        public bool IsNew { get; set; }

        public bool IsLive { get; set; }

        /// <summary>"Season Premiere", "Series Finale" ..., or "Premiere".</summary>
        public string PremiereOrFinale { get; set; }

        /// <summary>e.g. "TV14".</summary>
        public string Rating { get; set; }

        public bool IsHd { get; set; }

        [JsonIgnore]
        public DateTime EndUtc => StartUtc.AddSeconds(Duration);

        public static GuideAiring FromSchedulesDirect(SdScheduledProgram airing)
        {
            var video = airing.VideoProperties ?? new List<string>();
            return new GuideAiring
            {
                ProgramId = airing.ProgramId,
                StartUtc = DateTime.SpecifyKind(airing.AirDateTime, DateTimeKind.Utc),
                Duration = airing.Duration,
                ProgramMd5 = airing.Md5,
                IsNew = airing.New,
                IsLive = string.Equals(airing.LiveTapeDelay, "Live", StringComparison.OrdinalIgnoreCase),
                PremiereOrFinale = !string.IsNullOrWhiteSpace(airing.IsPremiereOrFinale) ? airing.IsPremiereOrFinale.Trim()
                    : airing.Premiere ? "Premiere" : null,
                Rating = airing.Ratings?.Select(r => r.Code).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c)),
                IsHd = video.Any(v => string.Equals(v, "hdtv", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "uhdtv", StringComparison.OrdinalIgnoreCase)),
            };
        }
    }

    /// <summary>One station's schedule for one UTC day, as Schedules Direct publishes it.</summary>
    public sealed class GuideDay
    {
        /// <summary>Schedules Direct's fingerprint of the day; unchanged days are not downloaded again.</summary>
        public string Md5 { get; set; }

        public DateTime? ModifiedUtc { get; set; }

        public List<GuideAiring> Airings { get; set; } = new List<GuideAiring>();
    }

    /// <summary>A station's cached schedule (cache\guide\stations\{stationId}.json).</summary>
    public sealed class GuideStationSchedule
    {
        public string StationId { get; set; }

        /// <summary>Keyed by UTC date, "yyyy-MM-dd".</summary>
        public Dictionary<string, GuideDay> Days { get; set; } = new Dictionary<string, GuideDay>(StringComparer.Ordinal);
    }

    /// <summary>A program airing on a station: the schedule entry together with the program's details.</summary>
    public sealed class GuideListing
    {
        public const string UnknownTitle = "To be announced";

        public GuideListing(string stationId, GuideAiring airing, GuideProgram program)
        {
            StationId = stationId;
            Airing = airing;
            Program = program;
        }

        public string StationId { get; }

        public GuideAiring Airing { get; }

        /// <summary>Null when the program's details have not been downloaded.</summary>
        public GuideProgram Program { get; }

        public DateTime StartUtc => Airing.StartUtc;

        public DateTime EndUtc => Airing.EndUtc;

        public string Title => string.IsNullOrWhiteSpace(Program?.Title) ? UnknownTitle : Program.Title;

        public string EpisodeLine => Program?.EpisodeLine ?? string.Empty;

        public string Description => Program?.Description;

        public string Rating => Airing.Rating ?? Program?.ContentRating;

        /// <summary>A short flag for the guide cell: "LIVE", "NEW", or null.</summary>
        public string Badge => Airing.IsLive ? "LIVE" : Airing.IsNew ? "NEW" : null;

        public bool IsOnAt(DateTime utc) => StartUtc <= utc && utc < EndUtc;

        /// <summary>How far through the program <paramref name="utc"/> is, from 0 to 1.</summary>
        public double ProgressAt(DateTime utc)
        {
            var total = (EndUtc - StartUtc).TotalSeconds;
            return total <= 0 ? 0 : Math.Max(0, Math.Min(1, (utc - StartUtc).TotalSeconds / total));
        }

        public override string ToString() => StartUtc.ToString("u", CultureInfo.InvariantCulture) + " " + Title;
    }
}
