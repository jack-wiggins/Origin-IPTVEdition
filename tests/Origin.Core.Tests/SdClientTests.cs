using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Origin.Core.Guide;
using Origin.Core.SchedulesDirect;
using Xunit;

namespace Origin.Core.Tests
{
    public class SdClientTests
    {
        [Fact]
        public void Hashes_password_as_lowercase_sha1()
        {
            Assert.Equal("5baa61e4c9b93f3f0682250b6cf8331b7ee68fd8", SdClient.HashPassword("password"));
        }

        [Fact]
        public async Task Signs_in_and_sends_the_token_header()
        {
            var handler = new FakeHandler();
            handler.On("POST", "/20141201/token", HttpStatusCode.OK,
                "{\"code\":0,\"message\":\"OK\",\"token\":\"abc123\",\"tokenExpires\":4102444800}");
            handler.On("GET", "/20141201/lineups/USA-OTA-60030", HttpStatusCode.OK,
                "{\"map\":[{\"stationID\":\"20454\",\"channel\":\"5\",\"atscMajor\":5,\"atscMinor\":1}]," +
                "\"stations\":[{\"stationID\":\"20454\",\"name\":\"WMAQ\",\"callsign\":\"WMAQDT\",\"affiliate\":\"NBC\"," +
                "\"stationLogo\":[{\"URL\":\"http://logo/light.png\",\"category\":\"light\"},{\"URL\":\"http://logo/dark.png\",\"category\":\"dark\"}]}]," +
                "\"metadata\":{\"lineup\":\"USA-OTA-60030\",\"transport\":\"Antenna\"}}");

            using (var client = new SdClient(new HttpClient(handler), "https://json.schedulesdirect.org/20141201"))
            {
                await client.LoginAsync("jack", SdClient.HashPassword("password"), CancellationToken.None);
                var map = await client.GetLineupMapAsync("USA-OTA-60030", CancellationToken.None);

                Assert.Equal("abc123", client.Token);
                Assert.Equal(new DateTime(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc), client.TokenExpiresUtc);

                var login = JObject.Parse(handler.Requests[0].Body);
                Assert.Equal("jack", (string)login["username"]);
                Assert.Equal("5baa61e4c9b93f3f0682250b6cf8331b7ee68fd8", (string)login["password"]);
                Assert.Equal("abc123", handler.Requests[1].Token);

                Assert.Equal("5.1", map.Map.Single().DisplayChannel);
                Assert.Equal("http://logo/dark.png", map.Stations.Single().BestLogoUrl);
            }
        }

        [Fact]
        public async Task Maps_api_errors_to_readable_messages()
        {
            var handler = new FakeHandler();
            handler.On("POST", "/20141201/token", HttpStatusCode.BadRequest,
                "{\"response\":\"INVALID_USER\",\"code\":4003,\"serverID\":\"20141201.web.1\",\"message\":\"Invalid user.\"}");

            using (var client = new SdClient(new HttpClient(handler), "https://json.schedulesdirect.org/20141201/"))
            {
                var error = await Assert.ThrowsAsync<SdApiException>(() => client.LoginAsync("jack", "x", CancellationToken.None));
                Assert.Equal(4003, error.Code);
                Assert.Contains("username and password", error.Message);
            }
        }

        [Fact]
        public async Task Treats_no_lineups_as_an_empty_list()
        {
            var handler = new FakeHandler();
            handler.On("POST", "/20141201/token", HttpStatusCode.OK, "{\"code\":0,\"token\":\"t\"}");
            handler.On("GET", "/20141201/lineups", HttpStatusCode.BadRequest,
                "{\"response\":\"NO_LINEUPS\",\"code\":4102,\"message\":\"No lineups have been added to this account.\"}");

            using (var client = new SdClient(new HttpClient(handler), null))
            {
                await client.LoginAsync("jack", "x", CancellationToken.None);
                Assert.Empty(await client.GetAccountLineupsAsync(CancellationToken.None));
            }
        }

        [Fact]
        public async Task Flattens_countries_by_region()
        {
            var handler = new FakeHandler();
            handler.On("GET", "/20141201/available/countries", HttpStatusCode.OK,
                "{\"North America\":[{\"fullName\":\"United States\",\"shortName\":\"USA\",\"postalCodeExample\":\"60030\"}," +
                "{\"fullName\":\"Canada\",\"shortName\":\"CAN\",\"postalCodeExample\":\"N7L4G3\"}],\"Europe\":[{\"fullName\":\"Germany\",\"shortName\":\"DEU\"}]}");

            using (var client = new SdClient(new HttpClient(handler), null))
            {
                var countries = await client.GetCountriesAsync(CancellationToken.None);
                Assert.Equal(new[] { "Canada", "Germany", "United States" }, countries.Select(c => c.FullName));
                Assert.Equal("Europe", countries[1].Region);
            }
        }

        [Fact]
        public async Task Reads_schedule_md5s_per_station_and_day()
        {
            var handler = SignedIn();
            handler.On("POST", "/20141201/schedules/md5", HttpStatusCode.OK,
                "{\"20454\":{\"2026-10-18\":{\"code\":0,\"message\":\"OK\",\"lastModified\":\"2026-10-17T18:50:39Z\",\"md5\":\"Dw6pUtqLQNagcfZWWe2dQw\"}," +
                "\"2026-10-19\":{\"code\":7100,\"message\":\"SCHEDULE_QUEUED\"}}," +
                "\"10021\":[]}");

            using (var client = await SignInAsync(handler))
            {
                var days = await client.GetScheduleMd5sAsync(
                    new[] { new SdScheduleRequest("20454", new[] { "2026-10-18", "2026-10-19" }), new SdScheduleRequest("10021", new[] { "2026-10-18" }) },
                    CancellationToken.None);

                var request = JArray.Parse(handler.Requests.Last().Body);
                Assert.Equal("20454", (string)request[0]["stationID"]);
                Assert.Equal(new[] { "2026-10-18", "2026-10-19" }, request[0]["date"].Select(d => (string)d));
                Assert.Equal("t", handler.Requests.Last().Token);

                Assert.Equal(2, days.Count);
                Assert.Equal("20454", days[0].StationId);
                Assert.Equal("2026-10-18", days[0].Date);
                Assert.Equal("Dw6pUtqLQNagcfZWWe2dQw", days[0].Md5);
                Assert.Equal(new DateTime(2026, 10, 17, 18, 50, 39, DateTimeKind.Utc), days[0].LastModified);
                Assert.Equal(7100, days[1].Code);
            }
        }

        [Fact]
        public async Task Reads_schedules_and_days_that_are_not_ready()
        {
            var handler = SignedIn();
            handler.On("POST", "/20141201/schedules", HttpStatusCode.OK,
                "[{\"stationID\":\"20454\",\"programs\":[{\"programID\":\"SH005371070000\",\"airDateTime\":\"2026-10-18T00:00:00Z\",\"duration\":1800," +
                "\"md5\":\"Sy8HEMBPcuiAx3FBukUhKQ\",\"new\":true,\"liveTapeDelay\":\"Live\",\"isPremiereOrFinale\":\"Season Premiere\"," +
                "\"audioProperties\":[\"stereo\",\"cc\"],\"videoProperties\":[\"hdtv\"],\"ratings\":[{\"body\":\"USA Parental Rating\",\"code\":\"TV14\"}]}]," +
                "\"metadata\":{\"modified\":\"2026-10-17T15:39:38Z\",\"md5\":\"Dw6pUtqLQNagcfZWWe2dQw\",\"startDate\":\"2026-10-18\"}}," +
                "{\"stationID\":\"10021\",\"code\":7100,\"response\":\"SCHEDULE_QUEUED\",\"message\":\"Not ready yet.\"}]");

            using (var client = await SignInAsync(handler))
            {
                var schedules = await client.GetSchedulesAsync(new[] { new SdScheduleRequest("20454", new[] { "2026-10-18" }) }, CancellationToken.None);

                var day = schedules[0];
                Assert.Equal("2026-10-18", day.Metadata.StartDate);
                Assert.Equal("Dw6pUtqLQNagcfZWWe2dQw", day.Metadata.Md5);

                var airing = GuideAiring.FromSchedulesDirect(day.Programs.Single());
                Assert.Equal("SH005371070000", airing.ProgramId);
                Assert.Equal(new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc), airing.StartUtc);
                Assert.Equal(DateTimeKind.Utc, airing.StartUtc.Kind);
                Assert.Equal(TimeSpan.FromMinutes(30), airing.EndUtc - airing.StartUtc);
                Assert.Equal("Sy8HEMBPcuiAx3FBukUhKQ", airing.ProgramMd5);
                Assert.True(airing.IsNew);
                Assert.True(airing.IsLive);
                Assert.True(airing.IsHd);
                Assert.Equal("Season Premiere", airing.PremiereOrFinale);
                Assert.Equal("TV14", airing.Rating);

                Assert.Equal(7100, schedules[1].Code);
                Assert.Equal("SCHEDULE_QUEUED", schedules[1].Response);
            }
        }

        [Fact]
        public async Task Reads_program_details()
        {
            var handler = SignedIn();
            handler.On("POST", "/20141201/programs", HttpStatusCode.OK,
                "[{\"programID\":\"EP000000060003\",\"titles\":[{\"title120\":\"'Allo 'Allo!\"}],\"episodeTitle150\":\"The Poisoned Sausage\"," +
                "\"descriptions\":{\"description100\":[{\"descriptionLanguage\":\"en\",\"description\":\"Short one.\"}]," +
                "\"description1000\":[{\"descriptionLanguage\":\"fr\",\"description\":\"En français.\"},{\"descriptionLanguage\":\"en\",\"description\":\"The long one.\"}]}," +
                "\"originalAirDate\":\"1985-11-04\",\"genres\":[\"Sitcom\"],\"entityType\":\"Episode\",\"showType\":\"Series\",\"md5\":\"Jo5NKxoo44xRvBCAq8QT2A\"," +
                "\"metadata\":[{\"TheTVDB\":{\"season\":9,\"episode\":9,\"seriesID\":\"x\"}},{\"Gracenote\":{\"season\":2,\"episode\":6}},{\"Odd\":\"not an object\"}]," +
                "\"contentRating\":[{\"body\":\"USA Parental Rating\",\"code\":\"TVPG\"}]}," +
                "{\"programID\":\"MV000000010000\",\"titles\":[{\"title120\":\"Die Hard\"}],\"entityType\":\"Movie\",\"movie\":{\"year\":\"1988\",\"duration\":7920}," +
                "\"descriptions\":{\"description100\":[{\"descriptionLanguage\":\"en\",\"description\":\"A cop in a skyscraper.\"}]},\"md5\":\"abc\"}," +
                "{\"programID\":\"EP999999990001\",\"code\":6001,\"response\":\"PROGRAMID_QUEUED\",\"message\":\"Queued.\"}]");

            using (var client = await SignInAsync(handler))
            {
                var programs = await client.GetProgramsAsync(new[] { "EP000000060003", "MV000000010000", "EP999999990001" }, CancellationToken.None);

                Assert.Equal(new[] { "EP000000060003", "MV000000010000", "EP999999990001" }, JArray.Parse(handler.Requests.Last().Body).Select(t => (string)t));

                var episode = GuideProgram.FromSchedulesDirect(programs[0], "en");
                Assert.Equal("'Allo 'Allo!", episode.Title);
                Assert.Equal("The Poisoned Sausage", episode.EpisodeTitle);
                Assert.Equal("The long one.", episode.Description);
                Assert.Equal(2, episode.Season);
                Assert.Equal(6, episode.Episode);
                Assert.Equal(new DateTime(1985, 11, 4), episode.OriginalAirDate);
                Assert.Equal("TVPG", episode.ContentRating);
                Assert.Equal("Jo5NKxoo44xRvBCAq8QT2A", episode.Md5);
                Assert.Equal("S2 E6 · The Poisoned Sausage", episode.EpisodeLine);
                Assert.Equal("En français.", GuideProgram.FromSchedulesDirect(programs[0], "fr").Description);

                var movie = GuideProgram.FromSchedulesDirect(programs[1]);
                Assert.True(movie.IsMovie);
                Assert.Equal(1988, movie.MovieYear);
                Assert.Equal("A cop in a skyscraper.", movie.Description);
                Assert.Equal("Movie · 1988", movie.EpisodeLine);

                Assert.Equal(6001, programs[2].Code);
            }
        }

        [Fact]
        public async Task Refuses_batches_over_the_api_limit_without_calling_it()
        {
            var handler = SignedIn();
            using (var client = await SignInAsync(handler))
            {
                var ids = Enumerable.Range(0, SdClient.MaxProgramsPerRequest + 1).Select(i => "EP" + i).ToList();
                await Assert.ThrowsAsync<ArgumentException>(() => client.GetProgramsAsync(ids, CancellationToken.None));
                Assert.Empty(await client.GetProgramsAsync(new string[0], CancellationToken.None));
                Assert.Single(handler.Requests);
            }
        }

        private static FakeHandler SignedIn()
        {
            var handler = new FakeHandler();
            handler.On("POST", "/20141201/token", HttpStatusCode.OK, "{\"code\":0,\"token\":\"t\"}");
            return handler;
        }

        private static async Task<SdClient> SignInAsync(FakeHandler handler)
        {
            var client = new SdClient(new HttpClient(handler), null);
            await client.LoginAsync("jack", "x", CancellationToken.None);
            return client;
        }

        private sealed class FakeHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, Tuple<HttpStatusCode, string>> _routes = new Dictionary<string, Tuple<HttpStatusCode, string>>();

            public List<(string Body, string Token)> Requests { get; } = new List<(string, string)>();

            public void On(string method, string path, HttpStatusCode status, string body)
            {
                _routes[method + " " + path] = Tuple.Create(status, body);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var body = request.Content == null ? null : await request.Content.ReadAsStringAsync();
                var token = request.Headers.TryGetValues("token", out var values) ? values.First() : null;
                Requests.Add((body, token));

                if (!_routes.TryGetValue(request.Method.Method + " " + request.RequestUri.AbsolutePath, out var route))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") };
                }

                return new HttpResponseMessage(route.Item1) { Content = new StringContent(route.Item2, Encoding.UTF8, "application/json") };
            }
        }
    }
}
