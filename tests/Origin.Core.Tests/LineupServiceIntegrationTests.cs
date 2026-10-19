using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Origin.Core.Guide;
using Origin.Core.Iptv;
using Origin.Core.Lineup;
using Origin.Core.SchedulesDirect;
using Origin.Core.Settings;
using Origin.Core.Storage;
using Xunit;

namespace Origin.Core.Tests
{
    /// <summary>
    /// The whole "update lineup" flow over real HTTP against a local stand-in for an Xtream
    /// Codes panel and the Schedules Direct API.
    /// </summary>
    public class LineupServiceIntegrationTests
    {
        private const string User = "jack";
        private const string Password = "s3cret!pw";

        [Fact]
        public async Task Downloads_the_m3u_with_the_account_and_maps_it_onto_the_schedules_direct_lineup()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                var guide = ServeSchedulesDirect(server);

                var (service, settings, paths) = CreateService(server, temp.Path);
                var progress = new List<string>();

                var result = await service.UpdateAsync(new LineupUpdateOptions(), new SyncProgress(p => progress.Add(p.Stage)), CancellationToken.None);

                // Live channels copied in playlist order; movies and series set aside.
                Assert.Equal(6, result.Channels);
                Assert.Equal(1, result.MovieEntries);
                Assert.Equal(new[] { "1", "2", "3", "4", "5", "6" }, result.Lineup.Channels.Select(c => c.Number));
                Assert.Equal("US: ABC 7 New York", result.Lineup.Channels[0].Name);

                // Mapped onto the Schedules Direct lineup.
                var byName = result.Lineup.Channels.ToDictionary(c => c.Name);
                Assert.Equal("WABC", byName["US: ABC 7 New York"].Mapping.Callsign);
                Assert.Equal(MatchMethod.TvgIdCallsign, byName["US: ABC 7 New York"].Mapping.Method);
                Assert.Equal("ESPNHD", byName["US: ESPN FHD"].Mapping.Callsign);
                Assert.Equal("FS1", byName["US: Fox Sports 1"].Mapping.Callsign);
                Assert.Null(byName["UK: BBC One HD"].Mapping);
                Assert.Equal(4, result.Mapped);
                Assert.Equal("USA-TEST-X", result.Lineup.SchedulesDirectLineupId);
                Assert.Empty(result.Warnings);
                Assert.Contains("Downloading your playlist", progress);

                // Credentials never reach the files on disk, but streams still resolve.
                Assert.DoesNotContain(Password, File.ReadAllText(paths.PlaylistFile));
                Assert.DoesNotContain(Password, File.ReadAllText(paths.LineupFile));
                Assert.DoesNotContain(Password, File.ReadAllText(paths.SettingsFile));
                Assert.Equal(server.BaseUrl + "/live/jack/s3cret%21pw/1001.ts",
                    result.Lineup.Channels[0].Stream.Resolve(settings.GetXtreamAccount()));

                // Schedules Direct got the SHA-1 hash and the session token.
                var login = server.Requests.Single(r => r.Path == "/20141201/token");
                Assert.Contains(SdClient.HashPassword("sdpass"), login.Body);
                Assert.Equal("mock-token", server.Requests.Single(r => r.Path == "/20141201/lineups/USA-TEST-X").Token);
                Assert.True(File.Exists(paths.GetSchedulesDirectLineupFile("USA-TEST-X")));

                // Then the guide: two weeks of listings for the four matched stations, cached on disk.
                Assert.Equal(4, result.Guide.Stations);
                Assert.Equal(4, result.Guide.StationsWithListings);
                Assert.Equal(4 * 15, result.Guide.DaysDownloaded);
                Assert.Equal(new[] { "10003", "10212", "32630", "82547" }, guide.Md5Requests.Single().Select(s => s.Station).OrderBy(s => s));
                Assert.Contains("Downloading program details", progress);
                Assert.All(server.Requests.Where(r => r.Path.StartsWith("/20141201/schedules") || r.Path == "/20141201/programs"),
                    r => Assert.Equal("mock-token", r.Token));

                var store = new GuideStore(paths.GuideDirectory);
                var abc = byName["US: ABC 7 New York"].Mapping.StationId;
                var now = DateTime.UtcNow;
                Assert.Equal(FakeGuideService.Title(abc, now.Hour), store.GetListingAt(abc, now).Title);
                Assert.Equal(FakeGuideService.Title(abc, (now.Hour + 1) % 24), store.GetNextListing(abc, now).Title);
                var nextNinetyMinutes = store.GetListings(abc, now, now.AddMinutes(90));
                Assert.InRange(nextNinetyMinutes.Count, 2, 3);
                Assert.All(nextNinetyMinutes, l => Assert.NotNull(l.Program));
            }
        }

        [Fact]
        public async Task Downloads_the_guide_for_newly_included_channels_without_refreshing_the_rest()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                var guide = ServeSchedulesDirect(server);
                var (service, settings, paths) = CreateService(server, temp.Path);
                settings.Current.Lineup.IncludeNewGroups = false;
                settings.Current.Lineup.Groups["USA | Sports"] = true;
                await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);
                guide.ClearRequests();

                settings.Current.Lineup.Groups["USA | Locals"] = true;
                var result = await service.UpdateAsync(
                    new LineupUpdateOptions { DownloadPlaylist = false, RefreshSchedulesDirect = false }, null, CancellationToken.None);

                // Only the two local stations had no listings yet.
                Assert.Equal(new[] { "10003", "10212" }, guide.Md5Requests.Single().Select(s => s.Station).OrderBy(s => s));
                Assert.Equal(4, result.Guide.StationsWithListings);
                Assert.Empty(result.Warnings);
                Assert.True(new GuideStore(paths.GuideDirectory).HasListings("10212"));
            }
        }

        [Fact]
        public async Task Keeps_the_guide_and_the_lineup_when_schedules_direct_is_down_for_listings()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                ServeSchedulesDirect(server);
                var (service, _, paths) = CreateService(server, temp.Path);
                await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);
                File.Delete(paths.LineupFile);

                server.Route("/20141201/schedules/md5", _ => (503, "{\"code\":3000,\"response\":\"SERVICE_OFFLINE\",\"message\":\"Down for maintenance\"}"));
                var result = await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);

                // The update still succeeds and saves the lineup; the guide keeps yesterday's download.
                Assert.Equal(4, result.Mapped);
                Assert.True(File.Exists(paths.LineupFile));
                Assert.Null(result.Guide);
                Assert.Contains(result.Warnings, w => w.Contains("temporarily offline") && w.Contains("keeps the listings"));
                Assert.NotNull(new GuideStore(paths.GuideDirectory).GetListingAt("10212", DateTime.UtcNow));
            }
        }

        [Fact]
        public async Task Cancelling_during_the_guide_download_keeps_the_new_lineup()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            using (var cancel = new CancellationTokenSource())
            {
                ServeProvider(server);
                ServeSchedulesDirect(server);
                var (service, _, paths) = CreateService(server, temp.Path);
                var progress = new SyncProgress(p =>
                {
                    if (p.Stage == "Downloading schedules")
                    {
                        cancel.Cancel();
                    }
                });

                var result = await service.UpdateAsync(new LineupUpdateOptions(), progress, cancel.Token);

                Assert.Equal(4, result.Mapped);
                Assert.True(File.Exists(paths.LineupFile));
                Assert.Contains(result.Warnings, w => w.Contains("guide download was cancelled"));
            }
        }

        [Fact]
        public async Task Rebuilds_from_the_saved_playlist_when_groups_change()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                ServeSchedulesDirect(server);
                var (service, settings, _) = CreateService(server, temp.Path);
                await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);
                var requestsAfterFirst = server.Requests.Count;

                settings.Current.Lineup.IncludeNewGroups = false;
                settings.Current.Lineup.Groups["USA | Sports"] = true;
                var result = await service.UpdateAsync(
                    new LineupUpdateOptions { DownloadPlaylist = false, RefreshSchedulesDirect = false }, null, CancellationToken.None);

                Assert.Equal(new[] { "US: ESPN FHD", "US: Fox Sports 1" }, result.Lineup.Channels.Select(c => c.Name));
                Assert.Equal(2, result.Mapped);
                Assert.Equal(requestsAfterFirst, server.Requests.Count);
            }
        }

        [Fact]
        public async Task Reports_a_rejected_password_and_keeps_the_old_lineup()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                var (service, settings, paths) = CreateService(server, temp.Path);
                settings.SetXtreamPassword("wrong");

                var error = await Assert.ThrowsAsync<LineupUpdateException>(
                    () => service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None));

                Assert.Contains("rejected the username or password", error.Message);
                Assert.False(File.Exists(paths.LineupFile));
            }
        }

        [Fact]
        public async Task Keeps_matches_when_schedules_direct_is_down()
        {
            using (var server = new MockServer())
            using (var temp = new TempDirectory())
            {
                ServeProvider(server);
                ServeSchedulesDirect(server);
                var (service, _, _) = CreateService(server, temp.Path);
                await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);

                server.Route("/20141201/lineups/USA-TEST-X", _ => (503, "{\"code\":3000,\"response\":\"SERVICE_OFFLINE\",\"message\":\"Down for maintenance\"}"));
                var result = await service.UpdateAsync(new LineupUpdateOptions(), null, CancellationToken.None);

                Assert.Equal(4, result.Mapped);
                Assert.Contains(result.Warnings, w => w.Contains("temporarily offline") && w.Contains("last saved copy"));
            }
        }

        private static (LineupService, SettingsService, AppPaths) CreateService(MockServer server, string root)
        {
            var paths = new AppPaths(root);
            var settings = new SettingsService(paths.SettingsFile);
            settings.Current.Xtream.ServerUrl = server.BaseUrl;
            settings.Current.Xtream.Username = User;
            settings.SetXtreamPassword(Password);
            settings.Current.SchedulesDirect.ApiBaseUrl = server.BaseUrl + "/20141201/";
            settings.Current.SchedulesDirect.Username = "sduser";
            settings.SetSchedulesDirectPasswordHash(SdClient.HashPassword("sdpass"));
            settings.Current.SchedulesDirect.LineupId = "USA-TEST-X";
            settings.Current.SchedulesDirect.LineupName = "Test Cable";
            settings.Save();

            var schedulesDirect = new SchedulesDirectService(settings, paths);
            var service = new LineupService(settings, new PlaylistStore(paths.PlaylistFile), new LineupStore(paths.LineupFile),
                schedulesDirect, new GuideDownloader(schedulesDirect, new GuideStore(paths.GuideDirectory)));
            return (service, settings, paths);
        }

        private static void ServeProvider(MockServer server)
        {
            var b = server.BaseUrl;
            var m3u = "#EXTM3U\n" +
                      "#EXTINF:-1 tvg-id=\"WABC.us\" group-title=\"USA | Locals\",US: ABC 7 New York\n" + b + "/live/jack/s3cret%21pw/1001.ts\n" +
                      "#EXTINF:-1 group-title=\"USA | Locals\",US: CBS 2 (WCBS) HD\n" + b + "/live/jack/s3cret%21pw/1002.ts\n" +
                      "#EXTINF:-1 tvg-id=\"ESPN.us\" group-title=\"USA | Sports\",US: ESPN FHD\n" + b + "/live/jack/s3cret%21pw/1003.ts\n" +
                      "#EXTINF:-1 group-title=\"USA | Sports\",US: Fox Sports 1\n" + b + "/live/jack/s3cret%21pw/1005.ts\n" +
                      "#EXTINF:-1 group-title=\"UK | General\",UK: BBC One HD\n" + b + "/live/jack/s3cret%21pw/2001.ts\n" +
                      "#EXTINF:-1 group-title=\"FR | General\",FR: TF1\n" + b + "/live/jack/s3cret%21pw/3001.ts\n" +
                      "#EXTINF:-1 group-title=\"Movies\",Die Hard (1988)\n" + b + "/movie/jack/s3cret%21pw/9001.mkv\n";

            server.Route("/get.php", request =>
                request.QueryString["username"] == User && request.QueryString["password"] == Password
                    ? (200, m3u)
                    : (403, string.Empty));
        }

        private static FakeGuideService ServeSchedulesDirect(MockServer server)
        {
            var map = ChannelMatcherTests.SampleLineup();
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(map);
            server.Route("/20141201/token", _ => (200, "{\"code\":0,\"token\":\"mock-token\",\"tokenExpires\":4102444800}"));
            server.Route("/20141201/lineups/USA-TEST-X", request =>
                request.Headers["token"] == "mock-token" ? (200, json) : (403, "{\"code\":4006,\"response\":\"TOKEN_EXPIRED\"}"));

            var guide = new FakeGuideService();
            guide.Serve(server);
            return guide;
        }

        private sealed class SyncProgress : IProgress<LineupUpdateProgress>
        {
            private readonly Action<LineupUpdateProgress> _report;

            public SyncProgress(Action<LineupUpdateProgress> report)
            {
                _report = report;
            }

            public void Report(LineupUpdateProgress value) => _report(value);
        }

        internal sealed class TempDirectory : IDisposable
        {
            public TempDirectory()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "origin-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }

            public string Path { get; }

            public void Dispose()
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        internal sealed class MockServer : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            private readonly ConcurrentDictionary<string, Func<HttpListenerRequest, string, (int Status, string Body)>> _routes =
                new ConcurrentDictionary<string, Func<HttpListenerRequest, string, (int, string)>>();

            public MockServer()
            {
                var probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();

                BaseUrl = "http://localhost:" + port;
                _listener.Prefixes.Add(BaseUrl + "/");
                _listener.Start();
                Task.Run(ServeAsync);
            }

            public string BaseUrl { get; }

            public ConcurrentQueue<RecordedRequest> RequestLog { get; } = new ConcurrentQueue<RecordedRequest>();

            public List<RecordedRequest> Requests => RequestLog.ToList();

            public void Route(string path, Func<HttpListenerRequest, (int, string)> handler)
            {
                _routes[path] = (request, body) => handler(request);
            }

            /// <summary>For POST endpoints: the handler also gets the request body.</summary>
            public void Route(string path, Func<HttpListenerRequest, string, (int, string)> handler)
            {
                _routes[path] = handler;
            }

            public void Dispose()
            {
                _listener.Close();
            }

            private async Task ServeAsync()
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    var request = context.Request;
                    string body;
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }

                    RequestLog.Enqueue(new RecordedRequest(request.Url.AbsolutePath, body, request.Headers["token"]));

                    var (status, text) = _routes.TryGetValue(request.Url.AbsolutePath, out var handler) ? handler(request, body) : (404, "not found");
                    var bytes = Encoding.UTF8.GetBytes(text);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = "application/json";
                    context.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    context.Response.Close();
                }
            }
        }

        internal sealed class RecordedRequest
        {
            public RecordedRequest(string path, string body, string token)
            {
                Path = path;
                Body = body;
                Token = token;
            }

            public string Path { get; }

            public string Body { get; }

            public string Token { get; }
        }
    }
}
