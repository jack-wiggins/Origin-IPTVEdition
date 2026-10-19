using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Origin.Core.Diagnostics;
using Origin.Core.Lineup;
using Origin.Core.SchedulesDirect;

namespace Origin.Core.Guide
{
    public sealed class GuideUpdateResult
    {
        /// <summary>Stations in the lineup that have a Schedules Direct match.</summary>
        public int Stations { get; set; }

        /// <summary>Stations that have listings after the update.</summary>
        public int StationsWithListings { get; set; }

        /// <summary>UTC days requested per station.</summary>
        public int Days { get; set; }

        public int DaysDownloaded { get; set; }

        /// <summary>Station-days whose md5 matched the cache, so they were not downloaded.</summary>
        public int DaysUnchanged { get; set; }

        /// <summary>Station-days Schedules Direct had no data for (or had not prepared yet).</summary>
        public int DaysUnavailable { get; set; }

        public int ProgramsDownloaded { get; set; }

        public int ProgramsUnchanged { get; set; }

        public int ProgramsUnavailable { get; set; }

        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Downloads about two weeks of schedules for the lineup's stations, and the details of every
    /// program in them, into a <see cref="GuideStore"/>. Schedules Direct's md5 fingerprints are
    /// compared with the cache first, so a daily update only downloads what changed.
    /// </summary>
    public sealed class GuideDownloader
    {
        /// <summary>Days of listings from today (UTC), as Media Center offered.</summary>
        public const int DaysAhead = 14;

        /// <summary>Yesterday is kept too: what is on just after midnight UTC may have started the day before.</summary>
        public const int DaysBehind = 1;

        /// <summary>
        /// Station-days per POST /schedules. The API allows 5000 stations per request, but at 14 days
        /// each that response would be hundreds of megabytes, so requests are kept to a few megabytes.
        /// </summary>
        internal const int ScheduleDaysPerRequest = 1500;

        private readonly SchedulesDirectService _schedulesDirect;
        private readonly Func<DateTime> _utcNow;

        public GuideDownloader(SchedulesDirectService schedulesDirect, GuideStore store, Func<DateTime> utcNow = null)
        {
            _schedulesDirect = schedulesDirect;
            Store = store;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public GuideStore Store { get; }

        /// <summary>
        /// Brings the listings for <paramref name="stationIds"/> up to date and forgets every other
        /// station. With <paramref name="onlyMissing"/>, only stations that have no listings yet are
        /// downloaded (e.g. after choosing more channel groups). Throws <see cref="SdApiException"/>
        /// when Schedules Direct cannot be reached; whatever was downloaded by then is kept.
        /// </summary>
        public async Task<GuideUpdateResult> UpdateAsync(IEnumerable<string> stationIds, bool onlyMissing, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var stations = stationIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
            var today = _utcNow().Date;
            var dates = Enumerable.Range(-DaysBehind, DaysBehind + DaysAhead)
                .Select(offset => today.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();
            var result = new GuideUpdateResult { Stations = stations.Count, Days = dates.Count };
            var toUpdate = onlyMissing ? stations.Where(id => !Store.HasListings(id)).ToList() : stations;

            try
            {
                if (toUpdate.Count > 0)
                {
                    var changed = await FindChangedDaysAsync(toUpdate, dates, result, progress, cancellationToken).ConfigureAwait(false);
                    await DownloadSchedulesAsync(changed, result, progress, cancellationToken).ConfigureAwait(false);
                    await DownloadProgramsAsync(toUpdate, today.AddDays(-DaysBehind), result, progress, cancellationToken).ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LineupUpdateProgress("Saving the guide"));
                Store.Prune(stations, dates[0]);
            }
            finally
            {
                try
                {
                    Store.Flush();
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Log.Warn("Could not save guide program details", ex);
                }

                Store.NotifyChanged();
            }

            result.StationsWithListings = stations.Count(Store.HasListings);
            result.Duration = stopwatch.Elapsed;
            Log.Info($"Guide updated: {result.StationsWithListings} of {result.Stations} stations have listings; "
                     + $"{result.DaysDownloaded} days downloaded, {result.DaysUnchanged} unchanged, {result.DaysUnavailable} unavailable; "
                     + $"{result.ProgramsDownloaded} programs downloaded, {result.ProgramsUnchanged} unchanged, {result.ProgramsUnavailable} unavailable "
                     + $"in {result.Duration.TotalSeconds:0.0}s");
            return result;
        }

        /// <summary>Station → the dates whose md5 differs from the cached copy, with the new md5.</summary>
        private async Task<Dictionary<string, Dictionary<string, string>>> FindChangedDaysAsync(
            List<string> stations, List<string> dates, GuideUpdateResult result, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            progress?.Report(new LineupUpdateProgress("Checking the guide for changes", Count(stations.Count, "station")));

            var wanted = new HashSet<string>(dates, StringComparer.Ordinal);
            var changed = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var batch in Batch(stations, SdClient.MaxStationsPerRequest))
            {
                var request = batch.Select(id => new SdScheduleRequest(id, dates)).ToList();
                var md5s = await _schedulesDirect.CallAsync((client, ct) => client.GetScheduleMd5sAsync(request, ct), cancellationToken).ConfigureAwait(false);

                foreach (var day in md5s.Where(d => d.StationId != null && wanted.Contains(d.Date)))
                {
                    if (day.Code != 0 || string.IsNullOrEmpty(day.Md5))
                    {
                        result.DaysUnavailable++;
                    }
                    else if (Store.GetDayMd5(day.StationId, day.Date) == day.Md5)
                    {
                        result.DaysUnchanged++;
                    }
                    else
                    {
                        if (!changed.TryGetValue(day.StationId, out var list))
                        {
                            changed[day.StationId] = list = new Dictionary<string, string>(StringComparer.Ordinal);
                        }

                        list[day.Date] = day.Md5;
                    }
                }
            }

            return changed;
        }

        private async Task DownloadSchedulesAsync(
            Dictionary<string, Dictionary<string, string>> changed, GuideUpdateResult result, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            const string stage = "Downloading schedules";
            var total = changed.Values.Sum(d => d.Count);
            var done = 0;
            var problems = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var batch in BatchScheduleRequests(changed))
            {
                progress?.Report(new LineupUpdateProgress(stage, done.ToString("N0") + " of " + Count(total, "day")));
                var schedules = await _schedulesDirect.CallAsync((client, ct) => client.GetSchedulesAsync(batch, ct), cancellationToken).ConfigureAwait(false);

                foreach (var station in schedules.Where(s => s?.StationId != null && changed.ContainsKey(s.StationId)).GroupBy(s => s.StationId, StringComparer.Ordinal))
                {
                    var expected = changed[station.Key];
                    var days = new Dictionary<string, GuideDay>(StringComparer.Ordinal);
                    foreach (var schedule in station)
                    {
                        if (schedule.Code != 0 || schedule.Metadata?.StartDate == null)
                        {
                            var reason = schedule.Response ?? "code " + schedule.Code;
                            problems[reason] = problems.TryGetValue(reason, out var count) ? count + 1 : 1;
                            result.DaysUnavailable++;
                            continue;
                        }

                        days[schedule.Metadata.StartDate] = new GuideDay
                        {
                            Md5 = schedule.Metadata.Md5 ?? (expected.TryGetValue(schedule.Metadata.StartDate, out var md5) ? md5 : null),
                            ModifiedUtc = schedule.Metadata.Modified,
                            Airings = (schedule.Programs ?? new List<SdScheduledProgram>())
                                .Where(p => p?.ProgramId != null && p.Duration > 0)
                                .Select(GuideAiring.FromSchedulesDirect)
                                .ToList(),
                        };
                    }

                    if (days.Count > 0)
                    {
                        Store.SaveDays(station.Key, days);
                        result.DaysDownloaded += days.Count;
                    }
                }

                done += batch.Sum(s => s.Dates.Count);
            }

            if (problems.Count > 0)
            {
                Log.Warn("Schedules Direct did not return some schedules: " + string.Join(", ", problems.Select(p => p.Value + " × " + p.Key)));
            }
        }

        private async Task DownloadProgramsAsync(
            List<string> stations, DateTime sinceUtc, GuideUpdateResult result, IProgress<LineupUpdateProgress> progress, CancellationToken cancellationToken)
        {
            const string stage = "Downloading program details";
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var needed = new List<string>();
            foreach (var airing in stations.SelectMany(Store.GetAirings).Where(a => a.EndUtc > sinceUtc))
            {
                if (!seen.Add(airing.ProgramId))
                {
                    continue;
                }

                var cached = Store.GetProgramMd5(airing.ProgramId);
                if (cached != null && (airing.ProgramMd5 == null || cached == airing.ProgramMd5))
                {
                    result.ProgramsUnchanged++;
                }
                else
                {
                    needed.Add(airing.ProgramId);
                }
            }

            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var done = 0;
            foreach (var batch in Batch(needed, SdClient.MaxProgramsPerRequest))
            {
                progress?.Report(new LineupUpdateProgress(stage, done.ToString("N0") + " of " + Count(needed.Count, "program")));
                var programs = await _schedulesDirect.CallAsync((client, ct) => client.GetProgramsAsync(batch, ct), cancellationToken).ConfigureAwait(false);

                var usable = programs
                    .Where(p => p?.ProgramId != null && p.Code == 0)
                    .Select(p => GuideProgram.FromSchedulesDirect(p, language))
                    .ToList();
                Store.AddPrograms(usable);
                result.ProgramsDownloaded += usable.Count;
                result.ProgramsUnavailable += batch.Count - usable.Count;
                done += batch.Count;
            }
        }

        /// <summary>Groups stations into requests of at most 5000 stations and about <see cref="ScheduleDaysPerRequest"/> station-days.</summary>
        private static IEnumerable<List<SdScheduleRequest>> BatchScheduleRequests(Dictionary<string, Dictionary<string, string>> changed)
        {
            var batch = new List<SdScheduleRequest>();
            var days = 0;
            foreach (var station in changed)
            {
                if (batch.Count > 0 && (batch.Count >= SdClient.MaxStationsPerRequest || days + station.Value.Count > ScheduleDaysPerRequest))
                {
                    yield return batch;
                    batch = new List<SdScheduleRequest>();
                    days = 0;
                }

                batch.Add(new SdScheduleRequest(station.Key, station.Value.Keys));
                days += station.Value.Count;
            }

            if (batch.Count > 0)
            {
                yield return batch;
            }
        }

        private static IEnumerable<List<T>> Batch<T>(List<T> items, int size)
        {
            for (var start = 0; start < items.Count; start += size)
            {
                yield return items.GetRange(start, Math.Min(size, items.Count - start));
            }
        }

        private static string Count(int count, string noun) => count.ToString("N0") + " " + noun + (count == 1 ? string.Empty : "s");
    }
}
