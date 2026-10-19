using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Origin.Core.Tests
{
    /// <summary>
    /// The schedule and program endpoints of the Schedules Direct API, served by a
    /// <see cref="LineupServiceIntegrationTests.MockServer"/>. Every station airs the same
    /// programs each day in equal slots, and every md5 is derived from the data, so bumping a
    /// version changes exactly what it would on the real service.
    /// </summary>
    internal sealed class FakeGuideService
    {
        public const string Token = "mock-token";

        private const string Prefix = "/20141201/";

        private readonly Dictionary<string, (string Station, string Date, int Slot)> _programs =
            new Dictionary<string, (string, string, int)>(StringComparer.Ordinal);

        /// <summary>Programs per station per day; each lasts 24 hours divided by this.</summary>
        public int SlotsPerDay { get; set; } = 24;

        /// <summary>When true, each day airs different programs (so there are many more of them).</summary>
        public bool UniqueProgramsPerDay { get; set; }

        /// <summary>Bump to change one station-day ("station|yyyy-MM-dd") without changing its programs.</summary>
        public Dictionary<string, int> DayVersions { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Bump to change a program's details (and so every day it airs on).</summary>
        public Dictionary<string, int> ProgramVersions { get; } = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Stations Schedules Direct has no schedules for.</summary>
        public HashSet<string> StationsWithoutData { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Station-days ("station|yyyy-MM-dd") whose schedule comes back as SCHEDULE_QUEUED.</summary>
        public HashSet<string> QueuedDays { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Programs that come back as PROGRAMID_QUEUED.</summary>
        public HashSet<string> QueuedPrograms { get; } = new HashSet<string>(StringComparer.Ordinal);

        public List<List<(string Station, List<string> Dates)>> Md5Requests { get; } = new List<List<(string, List<string>)>>();

        public List<List<(string Station, List<string> Dates)>> ScheduleRequests { get; } = new List<List<(string, List<string>)>>();

        public List<List<string>> ProgramRequests { get; } = new List<List<string>>();

        public void Serve(LineupServiceIntegrationTests.MockServer server)
        {
            server.Route(Prefix + "schedules/md5", (request, body) => Authorized(request) ?? (200, Md5s(Record(Md5Requests, body))));
            server.Route(Prefix + "schedules", (request, body) => Authorized(request) ?? (200, Schedules(Record(ScheduleRequests, body))));
            server.Route(Prefix + "programs", (request, body) =>
            {
                var ids = JArray.Parse(body).Select(t => (string)t).ToList();
                ProgramRequests.Add(ids);
                return Authorized(request) ?? (200, Programs(ids));
            });
        }

        public void ClearRequests()
        {
            Md5Requests.Clear();
            ScheduleRequests.Clear();
            ProgramRequests.Clear();
        }

        public string ProgramId(string station, string date, int slot)
        {
            return UniqueProgramsPerDay
                ? "EP" + station + date.Replace("-", string.Empty) + slot.ToString("000", CultureInfo.InvariantCulture)
                : "EP" + station + slot.ToString("000", CultureInfo.InvariantCulture);
        }

        public static string Title(string station, int slot) => "Show " + slot + " on " + station;

        public static string Today(int offset = 0) => DateTime.UtcNow.Date.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static (int, string)? Authorized(HttpListenerRequest request)
        {
            return request.Headers["token"] == Token
                ? ((int, string)?)null
                : (403, "{\"code\":4006,\"response\":\"TOKEN_EXPIRED\",\"message\":\"Token has expired.\"}");
        }

        private static List<(string Station, List<string> Dates)> Record(List<List<(string, List<string>)>> log, string body)
        {
            var stations = JArray.Parse(body)
                .Select(s => ((string)s["stationID"], s["date"]?.Select(d => (string)d).ToList() ?? new List<string>()))
                .ToList();
            log.Add(stations);
            return stations;
        }

        private string Md5s(List<(string Station, List<string> Dates)> stations)
        {
            var response = new JObject();
            foreach (var (station, dates) in stations)
            {
                if (StationsWithoutData.Contains(station))
                {
                    response[station] = new JArray();
                    continue;
                }

                var days = new JObject();
                foreach (var date in dates)
                {
                    days[date] = new JObject
                    {
                        ["code"] = 0,
                        ["message"] = "OK",
                        ["lastModified"] = "2026-01-01T00:00:00Z",
                        ["md5"] = DayMd5(station, date),
                    };
                }

                response[station] = days;
            }

            return response.ToString(Formatting.None);
        }

        private string Schedules(List<(string Station, List<string> Dates)> stations)
        {
            var response = new JArray();
            foreach (var (station, dates) in stations)
            {
                foreach (var date in dates)
                {
                    if (StationsWithoutData.Contains(station) || QueuedDays.Contains(station + "|" + date))
                    {
                        response.Add(new JObject
                        {
                            ["stationID"] = station,
                            ["code"] = 7100,
                            ["response"] = "SCHEDULE_QUEUED",
                            ["message"] = "The schedule you requested has been queued for generation but is not yet ready for download.",
                        });
                        continue;
                    }

                    var programs = new JArray();
                    foreach (var (programId, start, duration) in Airings(station, date))
                    {
                        programs.Add(new JObject
                        {
                            ["programID"] = programId,
                            ["airDateTime"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                            ["duration"] = duration,
                            ["md5"] = ProgramMd5(programId),
                            ["new"] = programId.EndsWith("000", StringComparison.Ordinal),
                            ["audioProperties"] = new JArray("stereo", "cc"),
                            ["videoProperties"] = new JArray("hdtv"),
                            ["ratings"] = new JArray(new JObject { ["body"] = "USA Parental Rating", ["code"] = "TVPG" }),
                        });
                    }

                    response.Add(new JObject
                    {
                        ["stationID"] = station,
                        ["programs"] = programs,
                        ["metadata"] = new JObject { ["modified"] = "2026-01-01T00:00:00Z", ["md5"] = DayMd5(station, date), ["startDate"] = date },
                    });
                }
            }

            return response.ToString(Formatting.None);
        }

        private string Programs(List<string> ids)
        {
            var response = new JArray();
            foreach (var id in ids)
            {
                if (QueuedPrograms.Contains(id) || !_programs.TryGetValue(id, out var source))
                {
                    response.Add(new JObject
                    {
                        ["programID"] = id,
                        ["code"] = 6001,
                        ["response"] = "PROGRAMID_QUEUED",
                        ["message"] = "Program queued for generation.",
                    });
                    continue;
                }

                response.Add(new JObject
                {
                    ["programID"] = id,
                    ["titles"] = new JArray(new JObject { ["title120"] = Title(source.Station, source.Slot) }),
                    ["episodeTitle150"] = "Episode " + source.Slot,
                    ["descriptions"] = new JObject
                    {
                        ["description100"] = new JArray(new JObject { ["descriptionLanguage"] = "en", ["description"] = "Short." }),
                        ["description1000"] = new JArray(new JObject
                        {
                            ["descriptionLanguage"] = "en",
                            ["description"] = "The long description of show " + source.Slot + " (version " + Version(ProgramVersions, id) + ").",
                        }),
                    },
                    ["originalAirDate"] = "2020-01-0" + (1 + source.Slot % 9),
                    ["genres"] = new JArray("Drama"),
                    ["metadata"] = new JArray(new JObject { ["Gracenote"] = new JObject { ["season"] = 1, ["episode"] = source.Slot + 1 } }),
                    ["entityType"] = "Episode",
                    ["showType"] = "Series",
                    ["md5"] = ProgramMd5(id),
                });
            }

            return response.ToString(Formatting.None);
        }

        private IEnumerable<(string ProgramId, DateTime Start, int Duration)> Airings(string station, string date)
        {
            var day = DateTime.SpecifyKind(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture), DateTimeKind.Utc);
            var length = 86400 / SlotsPerDay;
            for (var slot = 0; slot < SlotsPerDay; slot++)
            {
                var id = ProgramId(station, date, slot);
                _programs[id] = (station, date, slot);
                yield return (id, day.AddSeconds(slot * length), length);
            }
        }

        private string DayMd5(string station, string date)
        {
            var programs = string.Join(",", Airings(station, date).Select(a => ProgramMd5(a.ProgramId)));
            return Md5(station + "|" + date + "|" + Version(DayVersions, station + "|" + date) + "|" + programs);
        }

        private string ProgramMd5(string programId) => Md5(programId + "|" + Version(ProgramVersions, programId));

        private static int Version(Dictionary<string, int> versions, string key) => versions.TryGetValue(key, out var version) ? version : 0;

        private static string Md5(string text)
        {
            using (var md5 = MD5.Create())
            {
                return Convert.ToBase64String(md5.ComputeHash(Encoding.UTF8.GetBytes(text))).TrimEnd('=');
            }
        }
    }
}
