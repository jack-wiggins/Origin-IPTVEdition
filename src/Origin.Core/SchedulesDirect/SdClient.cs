using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Origin.Core.Diagnostics;
using Origin.Core.Net;
using Origin.Core.Storage;

namespace Origin.Core.SchedulesDirect
{
    /// <summary>
    /// Client for the Schedules Direct JSON API (https://github.com/SchedulesDirect/JSON-Service/wiki/API-20141201).
    /// Covers sign-in, lineups, schedules and program metadata.
    /// </summary>
    public sealed class SdClient : IDisposable
    {
        /// <summary>The API's limit for POST /schedules and /schedules/md5.</summary>
        public const int MaxStationsPerRequest = 5000;

        /// <summary>The API's limit for POST /programs.</summary>
        public const int MaxProgramsPerRequest = 5000;

        private static readonly TimeSpan TokenLifetimeFallback = TimeSpan.FromHours(23);

        private readonly HttpClient _http;
        private readonly bool _ownsClient;
        private readonly Uri _baseUri;
        private readonly JsonSerializer _serializer = JsonSerializer.Create(JsonFile.SerializerSettings);

        public SdClient(string baseUrl)
            : this(OriginHttp.CreateClient(OriginHttp.DefaultUserAgent, TimeSpan.FromMinutes(2)), baseUrl, ownsClient: true)
        {
        }

        public SdClient(HttpClient http, string baseUrl, bool ownsClient = false)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _ownsClient = ownsClient;

            var text = string.IsNullOrWhiteSpace(baseUrl) ? Settings.SchedulesDirectSettings.DefaultApiBaseUrl : baseUrl.Trim();
            _baseUri = new Uri(text.EndsWith("/", StringComparison.Ordinal) ? text : text + "/", UriKind.Absolute);
        }

        public string Token { get; private set; }

        public DateTime TokenExpiresUtc { get; private set; }

        public bool HasValidToken => Token != null && DateTime.UtcNow < TokenExpiresUtc - TimeSpan.FromMinutes(5);

        /// <summary>Schedules Direct expects the lowercase hex SHA-1 of the password, never the password itself.</summary>
        public static string HashPassword(string password)
        {
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(password ?? string.Empty));
                var builder = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    builder.Append(b.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        public async Task LoginAsync(string username, string passwordHash, CancellationToken cancellationToken)
        {
            var body = new JObject { ["username"] = username, ["password"] = passwordHash };
            var response = await SendAsync<SdTokenResponse>(HttpMethod.Post, "token", body, authenticated: false, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(response.Token))
            {
                throw new SdApiException("Schedules Direct did not return a session token.");
            }

            Token = response.Token;
            TokenExpiresUtc = response.TokenExpires.HasValue && response.TokenExpires.Value > 0
                ? DateTimeOffset.FromUnixTimeSeconds(response.TokenExpires.Value).UtcDateTime
                : DateTime.UtcNow + TokenLifetimeFallback;
            Log.Info("Signed in to Schedules Direct; token valid until " + TokenExpiresUtc.ToString("u"));
        }

        public void SignOut()
        {
            Token = null;
            TokenExpiresUtc = DateTime.MinValue;
        }

        public Task<SdStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            return SendAsync<SdStatus>(HttpMethod.Get, "status", null, authenticated: true, cancellationToken);
        }

        public async Task<IReadOnlyList<SdCountry>> GetCountriesAsync(CancellationToken cancellationToken)
        {
            var regions = await SendAsync<JObject>(HttpMethod.Get, "available/countries", null, authenticated: false, cancellationToken)
                .ConfigureAwait(false);

            var countries = new List<SdCountry>();
            foreach (var region in regions.Properties())
            {
                if (!(region.Value is JArray list))
                {
                    continue;
                }

                foreach (var item in list.OfType<JObject>())
                {
                    var country = item.ToObject<SdCountry>(_serializer);
                    country.Region = region.Name;
                    countries.Add(country);
                }
            }

            return countries.OrderBy(c => c.FullName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Cable, satellite and antenna lineups available at a postal code.</summary>
        public async Task<IReadOnlyList<SdHeadend>> GetHeadendsAsync(string country, string postalCode, CancellationToken cancellationToken)
        {
            var path = "headends?country=" + Uri.EscapeDataString(country ?? "USA")
                       + "&postalcode=" + Uri.EscapeDataString((postalCode ?? string.Empty).Trim());
            return await SendAsync<List<SdHeadend>>(HttpMethod.Get, path, null, authenticated: true, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>Lineups on the account. An account with none is not an error.</summary>
        public async Task<IReadOnlyList<SdLineupSummary>> GetAccountLineupsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var list = await SendAsync<SdLineupList>(HttpMethod.Get, "lineups", null, authenticated: true, cancellationToken)
                    .ConfigureAwait(false);
                return (list.Lineups ?? new List<SdLineupSummary>()).Where(l => !l.IsDeleted).ToList();
            }
            catch (SdApiException ex) when (ex.IsNoLineups)
            {
                return new SdLineupSummary[0];
            }
        }

        public Task<SdChangeResponse> AddLineupAsync(string lineupId, CancellationToken cancellationToken)
        {
            return SendAsync<SdChangeResponse>(HttpMethod.Put, "lineups/" + Uri.EscapeDataString(lineupId), null, authenticated: true, cancellationToken);
        }

        public Task<SdChangeResponse> RemoveLineupAsync(string lineupId, CancellationToken cancellationToken)
        {
            return SendAsync<SdChangeResponse>(HttpMethod.Delete, "lineups/" + Uri.EscapeDataString(lineupId), null, authenticated: true, cancellationToken);
        }

        /// <summary>The channel map and station list for a lineup on the account.</summary>
        public Task<SdLineupMap> GetLineupMapAsync(string lineupId, CancellationToken cancellationToken)
        {
            return SendAsync<SdLineupMap>(HttpMethod.Get, "lineups/" + Uri.EscapeDataString(lineupId), null, authenticated: true, cancellationToken);
        }

        /// <summary>
        /// POST /schedules/md5: a fingerprint per station and day, so days that have not changed
        /// need not be downloaded again. Days without data come back with a non-zero code.
        /// </summary>
        public async Task<IReadOnlyList<SdScheduleMd5>> GetScheduleMd5sAsync(IReadOnlyCollection<SdScheduleRequest> stations, CancellationToken cancellationToken)
        {
            CheckBatch(stations, MaxStationsPerRequest, "stations");
            if (stations.Count == 0)
            {
                return new SdScheduleMd5[0];
            }

            // { "20454": { "2026-10-18": { "code": 0, "md5": "..." }, ... }, ... }
            var response = await SendAsync<JObject>(HttpMethod.Post, "schedules/md5", JToken.FromObject(stations, _serializer), authenticated: true, cancellationToken)
                .ConfigureAwait(false);

            var days = new List<SdScheduleMd5>();
            foreach (var station in response.Properties())
            {
                // A station with nothing to report is an empty array, a station-level error a flat object.
                if (!(station.Value is JObject dates))
                {
                    continue;
                }

                foreach (var date in dates.Properties())
                {
                    if (date.Value is JObject entry)
                    {
                        var day = entry.ToObject<SdScheduleMd5>(_serializer);
                        day.StationId = station.Name;
                        day.Date = date.Name;
                        days.Add(day);
                    }
                }
            }

            return days;
        }

        /// <summary>POST /schedules: one element per station and day; days that could not be returned carry a non-zero code.</summary>
        public async Task<IReadOnlyList<SdStationSchedule>> GetSchedulesAsync(IReadOnlyCollection<SdScheduleRequest> stations, CancellationToken cancellationToken)
        {
            CheckBatch(stations, MaxStationsPerRequest, "stations");
            if (stations.Count == 0)
            {
                return new SdStationSchedule[0];
            }

            return await SendAsync<List<SdStationSchedule>>(HttpMethod.Post, "schedules", JToken.FromObject(stations, _serializer), authenticated: true, cancellationToken)
                .ConfigureAwait(false);
        }

        /// <summary>POST /programs: titles, descriptions and episode details for up to <see cref="MaxProgramsPerRequest"/> program IDs.</summary>
        public async Task<IReadOnlyList<SdProgram>> GetProgramsAsync(IReadOnlyCollection<string> programIds, CancellationToken cancellationToken)
        {
            CheckBatch(programIds, MaxProgramsPerRequest, "programs");
            if (programIds.Count == 0)
            {
                return new SdProgram[0];
            }

            return await SendAsync<List<SdProgram>>(HttpMethod.Post, "programs", JToken.FromObject(programIds, _serializer), authenticated: true, cancellationToken)
                .ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_ownsClient)
            {
                _http.Dispose();
            }
        }

        private static void CheckBatch<T>(IReadOnlyCollection<T> items, int limit, string what)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            if (items.Count > limit)
            {
                throw new ArgumentException("Schedules Direct accepts at most " + limit + " " + what + " per request.", nameof(items));
            }
        }

        private async Task<T> SendAsync<T>(HttpMethod method, string relativePath, JToken body, bool authenticated, CancellationToken cancellationToken)
        {
            using (var request = new HttpRequestMessage(method, new Uri(_baseUri, relativePath)))
            {
                if (authenticated)
                {
                    if (Token == null)
                    {
                        throw new SdApiException("Sign in to Schedules Direct first.");
                    }

                    request.Headers.TryAddWithoutValidation("token", Token);
                }

                if (body != null)
                {
                    request.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                }

                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is WebException)
                {
                    throw new SdApiException("Could not reach Schedules Direct: " + OriginHttp.Describe(ex) + ".", ex);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new SdApiException("Schedules Direct did not respond in time.", ex);
                }

                using (response)
                {
                    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    JToken json = null;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        try
                        {
                            json = JToken.Parse(text);
                        }
                        catch (JsonException)
                        {
                        }
                    }

                    if (json is JObject error && error["code"] != null && error["code"].Type == JTokenType.Integer && (int)error["code"] != 0)
                    {
                        throw SdApiException.FromResponse(error, (int)response.StatusCode);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        throw new SdApiException("Schedules Direct returned HTTP " + (int)response.StatusCode + ".", (int)response.StatusCode);
                    }

                    if (json == null)
                    {
                        throw new SdApiException("Schedules Direct returned a response Origin could not read.");
                    }

                    return json.ToObject<T>(_serializer);
                }
            }
        }
    }
}
