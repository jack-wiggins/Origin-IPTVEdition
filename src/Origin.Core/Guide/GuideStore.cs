using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Origin.Core.Diagnostics;
using Origin.Core.Storage;

namespace Origin.Core.Guide
{
    /// <summary>
    /// The guide listings downloaded from Schedules Direct, kept under cache\guide: one file per
    /// station with its schedule by day, and the program details spread over a few shard files.
    /// Files are read the first time they are needed, so showing what is on one channel does not
    /// wait for the whole guide to load.
    /// </summary>
    public sealed class GuideStore
    {
        private const int ProgramShardCount = 32;

        private static readonly IReadOnlyList<GuideListing> NoListings = new GuideListing[0];

        private readonly object _sync = new object();
        private readonly string _stationsDirectory;
        private readonly string _programsDirectory;
        private readonly Dictionary<string, Station> _stations = new Dictionary<string, Station>(StringComparer.Ordinal);
        private readonly ProgramShard[] _shards = new ProgramShard[ProgramShardCount];

        public GuideStore(string directory)
        {
            _stationsDirectory = Path.Combine(directory, "stations");
            _programsDirectory = Path.Combine(directory, "programs");
            Directory.CreateDirectory(_stationsDirectory);
            Directory.CreateDirectory(_programsDirectory);
        }

        /// <summary>Raised after a guide download changed the listings, possibly on a background thread.</summary>
        public event EventHandler Changed;

        /// <summary>Every airing on the station that overlaps [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), in time order.</summary>
        public IReadOnlyList<GuideListing> GetListings(string stationId, DateTime fromUtc, DateTime toUtc)
        {
            if (stationId == null || toUtc <= fromUtc)
            {
                return NoListings;
            }

            lock (_sync)
            {
                var timeline = GetStation(stationId).Timeline;
                var listings = new List<GuideListing>();
                for (var i = FirstEndingAfter(timeline, fromUtc); i < timeline.Length && timeline[i].StartUtc < toUtc; i++)
                {
                    listings.Add(ToListing(stationId, timeline[i]));
                }

                return listings;
            }
        }

        /// <summary>What is on at <paramref name="utc"/>, or null.</summary>
        public GuideListing GetListingAt(string stationId, DateTime utc)
        {
            if (stationId == null)
            {
                return null;
            }

            lock (_sync)
            {
                var timeline = GetStation(stationId).Timeline;
                var index = FirstEndingAfter(timeline, utc);
                return index < timeline.Length && timeline[index].StartUtc <= utc ? ToListing(stationId, timeline[index]) : null;
            }
        }

        /// <summary>The first airing that starts at or after <paramref name="utc"/>, or null.</summary>
        public GuideListing GetNextListing(string stationId, DateTime utc)
        {
            if (stationId == null)
            {
                return null;
            }

            lock (_sync)
            {
                var timeline = GetStation(stationId).Timeline;
                var index = FirstStartingAtOrAfter(timeline, utc);
                return index < timeline.Length ? ToListing(stationId, timeline[index]) : null;
            }
        }

        /// <summary>Whether any schedule has been downloaded for the station.</summary>
        public bool HasListings(string stationId)
        {
            if (stationId == null)
            {
                return false;
            }

            lock (_sync)
            {
                return GetStation(stationId).Timeline.Length > 0;
            }
        }

        public GuideProgram GetProgram(string programId)
        {
            if (programId == null)
            {
                return null;
            }

            lock (_sync)
            {
                return GetShard(programId).Programs.TryGetValue(programId, out var program) ? program : null;
            }
        }

        // ------------------------------------------------------------------ Used by GuideDownloader

        internal string GetDayMd5(string stationId, string date)
        {
            lock (_sync)
            {
                return GetStation(stationId).Schedule.Days.TryGetValue(date, out var day) ? day.Md5 : null;
            }
        }

        /// <summary>Every cached airing on the station, in time order.</summary>
        internal IReadOnlyList<GuideAiring> GetAirings(string stationId)
        {
            lock (_sync)
            {
                return GetStation(stationId).Timeline;
            }
        }

        /// <summary>Adds or replaces days in a station's schedule and saves it.</summary>
        internal void SaveDays(string stationId, IReadOnlyDictionary<string, GuideDay> days)
        {
            lock (_sync)
            {
                var station = GetStation(stationId);
                foreach (var day in days)
                {
                    station.Schedule.Days[day.Key] = day.Value;
                }

                SaveStation(station);
            }
        }

        internal string GetProgramMd5(string programId)
        {
            return GetProgram(programId)?.Md5;
        }

        /// <summary>Adds or replaces program details in memory. <see cref="Flush"/> writes them out.</summary>
        internal void AddPrograms(IEnumerable<GuideProgram> programs)
        {
            lock (_sync)
            {
                foreach (var program in programs)
                {
                    var shard = GetShard(program.ProgramId);
                    shard.Programs[program.ProgramId] = program;
                    shard.Dirty = true;
                }
            }
        }

        /// <summary>
        /// Forgets stations that are no longer in the lineup, days before <paramref name="firstDate"/>
        /// ("yyyy-MM-dd"), and programs that no remaining airing refers to.
        /// </summary>
        internal void Prune(ICollection<string> keepStations, string firstDate)
        {
            var keep = new HashSet<string>(keepStations, StringComparer.Ordinal);
            lock (_sync)
            {
                var keepFiles = new HashSet<string>(keep.Select(StationPath), StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.GetFiles(_stationsDirectory, "*.json").Where(f => !keepFiles.Contains(f)))
                {
                    TryDelete(file);
                }

                foreach (var stationId in _stations.Keys.Where(id => !keep.Contains(id)).ToList())
                {
                    _stations.Remove(stationId);
                }

                var referenced = new HashSet<string>(StringComparer.Ordinal);
                foreach (var stationId in keep)
                {
                    var station = GetStation(stationId);
                    var old = station.Schedule.Days.Keys.Where(date => string.CompareOrdinal(date, firstDate) < 0).ToList();
                    if (old.Count > 0)
                    {
                        old.ForEach(date => station.Schedule.Days.Remove(date));
                        SaveStation(station);
                    }

                    foreach (var airing in station.Timeline)
                    {
                        referenced.Add(airing.ProgramId);
                    }
                }

                for (var index = 0; index < ProgramShardCount; index++)
                {
                    var shard = GetShard(index);
                    foreach (var programId in shard.Programs.Keys.Where(id => !referenced.Contains(id)).ToList())
                    {
                        shard.Programs.Remove(programId);
                        shard.Dirty = true;
                    }
                }

                Flush();
            }
        }

        /// <summary>Writes program shards that changed since they were loaded.</summary>
        internal void Flush()
        {
            lock (_sync)
            {
                for (var index = 0; index < ProgramShardCount; index++)
                {
                    var shard = _shards[index];
                    if (shard != null && shard.Dirty)
                    {
                        JsonFile.Save(ShardPath(index), shard.Programs.Values.OrderBy(p => p.ProgramId, StringComparer.Ordinal).ToList(), indented: false);
                        shard.Dirty = false;
                    }
                }
            }
        }

        internal void NotifyChanged()
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------ Storage

        private Station GetStation(string stationId)
        {
            if (!_stations.TryGetValue(stationId, out var station))
            {
                var schedule = JsonFile.TryLoad<GuideStationSchedule>(StationPath(stationId)) ?? new GuideStationSchedule();
                schedule.StationId = stationId;
                schedule.Days = schedule.Days ?? new Dictionary<string, GuideDay>(StringComparer.Ordinal);
                station = new Station(schedule);
                _stations[stationId] = station;
            }

            return station;
        }

        private void SaveStation(Station station)
        {
            station.Rebuild();
            JsonFile.Save(StationPath(station.Schedule.StationId), station.Schedule, indented: false);
        }

        private ProgramShard GetShard(string programId) => GetShard(ShardIndex(programId));

        private ProgramShard GetShard(int index)
        {
            var shard = _shards[index];
            if (shard == null)
            {
                var programs = JsonFile.TryLoad<List<GuideProgram>>(ShardPath(index)) ?? new List<GuideProgram>();
                shard = new ProgramShard();
                foreach (var program in programs.Where(p => p?.ProgramId != null))
                {
                    shard.Programs[program.ProgramId] = program;
                }

                _shards[index] = shard;
            }

            return shard;
        }

        private GuideListing ToListing(string stationId, GuideAiring airing)
        {
            return new GuideListing(stationId, airing, GetShard(airing.ProgramId).Programs.TryGetValue(airing.ProgramId, out var program) ? program : null);
        }

        private string StationPath(string stationId)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return Path.Combine(_stationsDirectory, new string(stationId.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + ".json");
        }

        private string ShardPath(int index) => Path.Combine(_programsDirectory, "programs-" + index.ToString("00") + ".json");

        /// <summary>FNV-1a, so a program lands in the same shard on every run and on 32- and 64-bit Windows.</summary>
        private static int ShardIndex(string programId)
        {
            var hash = 2166136261u;
            foreach (var c in programId)
            {
                hash = unchecked((hash ^ c) * 16777619u);
            }

            return (int)(hash % ProgramShardCount);
        }

        /// <summary>Index of the first airing that ends after <paramref name="utc"/>.</summary>
        private static int FirstEndingAfter(GuideAiring[] timeline, DateTime utc)
        {
            var last = FirstStartingAtOrAfter(timeline, utc.AddTicks(1)) - 1;
            return last >= 0 && timeline[last].EndUtc > utc ? last : last + 1;
        }

        /// <summary>Index of the first airing that starts at or after <paramref name="utc"/> (the length if none does).</summary>
        private static int FirstStartingAtOrAfter(GuideAiring[] timeline, DateTime utc)
        {
            int low = 0, high = timeline.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (timeline[middle].StartUtc < utc)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            return low;
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Log.Warn("Could not delete " + path, ex);
            }
        }

        private sealed class Station
        {
            public Station(GuideStationSchedule schedule)
            {
                Schedule = schedule;
                Rebuild();
            }

            public GuideStationSchedule Schedule { get; }

            /// <summary>Every day's airings in one list, sorted by start time.</summary>
            public GuideAiring[] Timeline { get; private set; }

            public void Rebuild()
            {
                var airings = Schedule.Days.Values
                    .Where(d => d?.Airings != null)
                    .SelectMany(d => d.Airings)
                    .Where(a => a?.ProgramId != null && a.Duration > 0)
                    .OrderBy(a => a.StartUtc)
                    .ToList();

                // A program that runs past midnight can be listed in both days.
                Timeline = airings.Where((a, i) => i == 0 || a.StartUtc != airings[i - 1].StartUtc).ToArray();
            }
        }

        private sealed class ProgramShard
        {
            public Dictionary<string, GuideProgram> Programs { get; } = new Dictionary<string, GuideProgram>(StringComparer.Ordinal);

            public bool Dirty { get; set; }
        }
    }
}
