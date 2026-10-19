using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
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
