using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Origin.Core.Guide;
using Origin.Core.SchedulesDirect;
using Origin.Core.Settings;
using Origin.Core.Storage;
using Xunit;
using static Origin.Core.Tests.LineupServiceIntegrationTests;

namespace Origin.Core.Tests
{
    /// <summary>The guide download over real HTTP against <see cref="FakeGuideService"/>.</summary>
    public class GuideDownloaderTests
    {
        private static readonly string[] Stations = { "10212", "32645" };

        [Fact]
        public async Task Downloads_two_weeks_of_schedules_and_the_programs_in_them()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                var (downloader, paths) = Create(server, temp.Path);

                var result = await downloader.UpdateAsync(Stations, onlyMissing: false, null, CancellationToken.None);

                // Yesterday (what is on just after midnight UTC may have started before it) plus 14 days.
                var md5Request = guide.Md5Requests.Single();
                Assert.Equal(Stations, md5Request.Select(s => s.Station));
                Assert.Equal(Enumerable.Range(-1, 15).Select(d => FakeGuideService.Today(d)), md5Request[0].Dates);

                Assert.Equal(30, result.DaysDownloaded);
                Assert.Equal(48, result.ProgramsDownloaded);
                Assert.Equal(2, result.StationsWithListings);
                Assert.Single(guide.ScheduleRequests);
                Assert.Equal(48, guide.ProgramRequests.Single().Count);

                // Read back from disk, as the app would after a restart.
                var store = new GuideStore(paths.GuideDirectory);
                var now = DateTime.UtcNow;
                var current = store.GetListingAt("10212", now);
                Assert.Equal(FakeGuideService.Title("10212", now.Hour), current.Title);
                Assert.Equal(now.Date.AddHours(now.Hour), current.StartUtc);
                Assert.Equal(TimeSpan.FromHours(1), current.EndUtc - current.StartUtc);
                Assert.Equal("S1 E" + (now.Hour + 1) + " · Episode " + now.Hour, current.EpisodeLine);
                Assert.Contains("long description of show " + now.Hour, current.Description);
                Assert.True(current.Airing.IsHd);
                Assert.Equal("TVPG", current.Rating);

                var lastDay = DateTime.UtcNow.Date.AddDays(13);
                Assert.NotNull(store.GetListingAt("32645", lastDay.AddHours(23).AddMinutes(30)));
                Assert.Null(store.GetListingAt("32645", lastDay.AddDays(1).AddHours(1)));
            }
        }

        [Fact]
        public async Task Skips_days_and_programs_whose_md5_has_not_changed()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                var (downloader, _) = Create(server, temp.Path);
                await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);

                // Nothing changed: only the md5 check goes out.
                guide.ClearRequests();
                var unchanged = await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);
                Assert.Single(guide.Md5Requests);
                Assert.Empty(guide.ScheduleRequests);
                Assert.Empty(guide.ProgramRequests);
                Assert.Equal(30, unchanged.DaysUnchanged);
                Assert.Equal(0, unchanged.DaysDownloaded);
                Assert.Equal(48, unchanged.ProgramsUnchanged);

                // One day was revised: only that day is downloaded, and its programs are already known.
                guide.ClearRequests();
                var tomorrow = FakeGuideService.Today(1);
                guide.DayVersions["32645|" + tomorrow] = 1;
                var oneDay = await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);
                var request = guide.ScheduleRequests.Single().Single();
                Assert.Equal("32645", request.Station);
                Assert.Equal(new[] { tomorrow }, request.Dates);
                Assert.Empty(guide.ProgramRequests);
                Assert.Equal(1, oneDay.DaysDownloaded);

                // One program's details changed: every day it airs on changes, but only that program is downloaded.
                guide.ClearRequests();
                var changedProgram = guide.ProgramId("10212", null, 20);
                guide.ProgramVersions[changedProgram] = 1;
                var oneProgram = await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);
                Assert.Equal(new[] { "10212" }, guide.ScheduleRequests.Single().Select(s => s.Station));
                Assert.Equal(15, guide.ScheduleRequests.Single().Single().Dates.Count);
                Assert.Equal(new[] { changedProgram }, guide.ProgramRequests.Single());
                Assert.Equal(1, oneProgram.ProgramsDownloaded);
                Assert.Contains("version 1", downloader.Store.GetProgram(changedProgram).Description);
            }
        }

        [Fact]
        public async Task Requests_program_details_in_batches_of_at_most_5000()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                guide.UniqueProgramsPerDay = true;
                guide.SlotsPerDay = 200;
                var (downloader, _) = Create(server, temp.Path);

                var result = await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);

                // 2 stations × 15 days × 200 programs.
                Assert.Equal(6000, result.ProgramsDownloaded);
                Assert.Equal(new[] { SdClient.MaxProgramsPerRequest, 1000 }, guide.ProgramRequests.Select(r => r.Count));
                Assert.Equal(6000, guide.ProgramRequests.SelectMany(r => r).Distinct().Count());
            }
        }

        [Fact]
        public async Task Keeps_each_schedule_request_to_a_few_megabytes()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                guide.SlotsPerDay = 1;
                var (downloader, _) = Create(server, temp.Path);
                var stations = Enumerable.Range(20000, 120).Select(id => id.ToString()).ToList();

                var result = await downloader.UpdateAsync(stations, false, null, CancellationToken.None);

                // 120 stations × 15 days = 1800 station-days: one md5 check, two schedule requests.
                Assert.Single(guide.Md5Requests);
                Assert.Equal(2, guide.ScheduleRequests.Count);
                Assert.All(guide.ScheduleRequests, r => Assert.True(r.Sum(s => s.Dates.Count) <= GuideDownloader.ScheduleDaysPerRequest));
                Assert.Equal(1800, result.DaysDownloaded);
                Assert.Equal(120, result.StationsWithListings);
            }
        }

        [Fact]
        public async Task Tries_queued_days_and_programs_again_next_time()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                var today = FakeGuideService.Today();
                var queuedProgram = guide.ProgramId("10212", null, 5);
                guide.QueuedDays.Add("10212|" + today);
                guide.QueuedPrograms.Add(queuedProgram);
                guide.StationsWithoutData.Add("99999");
                var (downloader, _) = Create(server, temp.Path);

                var first = await downloader.UpdateAsync(Stations.Concat(new[] { "99999" }), false, null, CancellationToken.None);

                Assert.Equal(29, first.DaysDownloaded);
                Assert.Equal(1, first.DaysUnavailable);
                Assert.Equal(1, first.ProgramsUnavailable);
                Assert.Equal(2, first.StationsWithListings);
                Assert.DoesNotContain(guide.ScheduleRequests.Single(), s => s.Station == "99999");

                var listing = downloader.Store.GetListingAt("10212", DateTime.UtcNow.Date.AddDays(1).AddHours(5).AddMinutes(10));
                Assert.Null(listing.Program);
                Assert.Equal(GuideListing.UnknownTitle, listing.Title);
                Assert.Empty(downloader.Store.GetListings("10212", DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddHours(23)));

                guide.ClearRequests();
                guide.QueuedDays.Clear();
                guide.QueuedPrograms.Clear();
                var second = await downloader.UpdateAsync(Stations.Concat(new[] { "99999" }), false, null, CancellationToken.None);

                Assert.Equal(new[] { today }, guide.ScheduleRequests.Single().Single().Dates);
                Assert.Equal(new[] { queuedProgram }, guide.ProgramRequests.Single());
                Assert.Equal(1, second.DaysDownloaded);
                Assert.Equal(FakeGuideService.Title("10212", 5), downloader.Store.GetProgram(queuedProgram).Title);
            }
        }

        [Fact]
        public async Task Only_missing_stations_are_downloaded_when_asked()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                var (downloader, _) = Create(server, temp.Path);
                await downloader.UpdateAsync(new[] { "10212" }, false, null, CancellationToken.None);
                guide.ClearRequests();

                await downloader.UpdateAsync(Stations, onlyMissing: true, null, CancellationToken.None);

                Assert.Equal(new[] { "32645" }, guide.Md5Requests.Single().Select(s => s.Station));
                Assert.True(downloader.Store.HasListings("32645"));
            }
        }

        [Fact]
        public async Task Forgets_stations_that_left_the_lineup()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                var guide = Serve(server);
                var (downloader, paths) = Create(server, temp.Path);
                await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);

                await downloader.UpdateAsync(new[] { "10212" }, false, null, CancellationToken.None);

                var store = new GuideStore(paths.GuideDirectory);
                Assert.True(store.HasListings("10212"));
                Assert.False(store.HasListings("32645"));
                Assert.Null(store.GetProgram(guide.ProgramId("32645", null, 0)));
                Assert.NotNull(store.GetProgram(guide.ProgramId("10212", null, 0)));
            }
        }

        [Fact]
        public async Task Signs_in_again_when_the_token_has_expired()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                Serve(server);
                var logins = 0;
                server.Route("/20141201/token", _ => (200, "{\"code\":0,\"token\":\"" + (++logins == 1 ? "stale-token" : FakeGuideService.Token) + "\",\"tokenExpires\":4102444800}"));
                var (downloader, _) = Create(server, temp.Path);

                var result = await downloader.UpdateAsync(Stations, false, null, CancellationToken.None);

                Assert.Equal(2, logins);
                Assert.Equal(2, result.StationsWithListings);
            }
        }

        private static FakeGuideService Serve(MockServer server)
        {
            var guide = new FakeGuideService();
            guide.Serve(server);
            server.Route("/20141201/token", _ => (200, "{\"code\":0,\"token\":\"" + FakeGuideService.Token + "\",\"tokenExpires\":4102444800}"));
            return guide;
        }

        private static (GuideDownloader, AppPaths) Create(MockServer server, string root)
        {
            var paths = new AppPaths(root);
            var settings = new SettingsService(paths.SettingsFile);
            settings.Current.SchedulesDirect.ApiBaseUrl = server.BaseUrl + "/20141201/";
            settings.Current.SchedulesDirect.Username = "sduser";
            settings.SetSchedulesDirectPasswordHash(SdClient.HashPassword("sdpass"));

            return (new GuideDownloader(new SchedulesDirectService(settings, paths), new GuideStore(paths.GuideDirectory)), paths);
        }
    }
}
