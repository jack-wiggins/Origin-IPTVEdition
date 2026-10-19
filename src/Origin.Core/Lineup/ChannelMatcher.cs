using System;
using System.Collections.Generic;
using System.Linq;
using Origin.Core.SchedulesDirect;

namespace Origin.Core.Lineup
{
    /// <summary>A Schedules Direct station as it appears in one lineup.</summary>
    public sealed class LineupStation
    {
        public LineupStation(SdStation station, string channel)
        {
            StationId = station.StationId;
            Callsign = station.Callsign;
            Name = station.Name;
            Affiliate = station.Affiliate;
            LogoUrl = station.BestLogoUrl;
            Channel = channel;

            NameKey = NameNormalizer.Normalize(station.Name);
            CallsignName = NameNormalizer.Normalize(station.Callsign);
            CallsignKey = NameNormalizer.CallsignKey(station.Callsign);
            IsHd = NameKey.HasHdTag || CallsignName.HasHdTag
                   || (station.Callsign ?? string.Empty).EndsWith("HD", StringComparison.OrdinalIgnoreCase);
        }

        public string StationId { get; }

        public string Callsign { get; }

        public string Name { get; }

        public string Affiliate { get; }

        public string LogoUrl { get; }

        /// <summary>Channel number in the Schedules Direct lineup (may be null).</summary>
        public string Channel { get; }

        internal NormalizedName NameKey { get; }

        internal NormalizedName CallsignName { get; }

        internal string CallsignKey { get; }

        internal bool IsHd { get; }

        public ChannelMapping ToMapping(MatchMethod method, double confidence)
        {
            return new ChannelMapping
            {
                StationId = StationId,
                Callsign = Callsign,
                StationName = Name,
                Affiliate = Affiliate,
                LineupChannel = Channel,
                LogoUrl = LogoUrl,
                Method = method,
                Confidence = Math.Round(confidence, 3),
            };
        }

        public override string ToString() => Callsign + " (" + Name + ")";
    }

    public sealed class StationMatch
    {
        public static readonly StationMatch None = new StationMatch(null, MatchMethod.None, 0);

        public StationMatch(LineupStation station, MatchMethod method, double confidence)
        {
            Station = station;
            Method = method;
            Confidence = confidence;
        }

        public LineupStation Station { get; }

        public MatchMethod Method { get; }

        public double Confidence { get; }

        public bool IsMatch => Station != null;
    }

    public sealed class ScoredStation
    {
        public ScoredStation(LineupStation station, double score)
        {
            Station = station;
            Score = score;
        }

        public LineupStation Station { get; }

        public double Score { get; }
    }

    /// <summary>
    /// Finds the Schedules Direct station for an IPTV channel. Tries, in order: the tvg-id as a
    /// station ID, the tvg-id as a callsign or name, a broadcast callsign in the channel name,
    /// an exact cleaned-up name, and finally a fuzzy name comparison.
    /// </summary>
    public sealed class ChannelMatcher
    {
        private readonly double _fuzzyThreshold;
        private readonly Dictionary<string, LineupStation> _byId = new Dictionary<string, LineupStation>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<LineupStation>> _byCallsign = new Dictionary<string, List<LineupStation>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<LineupStation>> _byCompact = new Dictionary<string, List<LineupStation>>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<LineupStation>> _candidateIndex = new Dictionary<string, List<LineupStation>>(StringComparer.Ordinal);

        public ChannelMatcher(IEnumerable<LineupStation> stations, double fuzzyThreshold = 0.82)
        {
            _fuzzyThreshold = fuzzyThreshold;
            Stations = stations.ToList();

            foreach (var station in Stations)
            {
                if (!string.IsNullOrEmpty(station.StationId) && !_byId.ContainsKey(station.StationId))
                {
                    _byId.Add(station.StationId, station);
                }

                Add(_byCallsign, station.CallsignKey, station);
                Add(_byCompact, station.NameKey.Compact, station);
                Add(_byCompact, station.CallsignName.Compact, station);
                Add(_byCompact, station.CallsignKey.ToLowerInvariant(), station);

                foreach (var token in station.NameKey.Tokens.Concat(station.CallsignName.Tokens))
                {
                    if (token.Length >= 2)
                    {
                        Add(_candidateIndex, token, station);
                    }
                }

                Add(_candidateIndex, PrefixKey(station.NameKey.Compact), station);
                Add(_candidateIndex, PrefixKey(station.CallsignName.Compact), station);
            }
        }

        public IReadOnlyList<LineupStation> Stations { get; }

        /// <summary>One entry per station in the lineup, carrying the lowest channel number it appears on.</summary>
        public static List<LineupStation> FromLineupMap(SdLineupMap map)
        {
            var channels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in map.Map ?? new List<SdChannelMapEntry>())
            {
                if (string.IsNullOrEmpty(entry.StationId))
                {
                    continue;
                }

                var channel = entry.DisplayChannel;
                if (!channels.TryGetValue(entry.StationId, out var existing)
                    || ChannelNumber.SortKey(channel) < ChannelNumber.SortKey(existing))
                {
                    channels[entry.StationId] = channel;
                }
            }

            return (map.Stations ?? new List<SdStation>())
                .Where(s => !string.IsNullOrEmpty(s.StationId))
                .GroupBy(s => s.StationId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Select(s => new LineupStation(s, channels.TryGetValue(s.StationId, out var c) ? c : null))
                .OrderBy(s => ChannelNumber.SortKey(s.Channel))
                .ThenBy(s => s.Callsign, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public LineupStation FindStation(string stationId)
        {
            return stationId != null && _byId.TryGetValue(stationId, out var station) ? station : null;
        }

        public StationMatch Match(string name, string tvgId = null, string tvgName = null, string channelNumber = null)
        {
            var normalized = NameNormalizer.Normalize(name);

            // 1. tvg-id: a station ID, or a callsign/name with a country suffix ("WABC.us", "ESPN.us").
            if (!string.IsNullOrWhiteSpace(tvgId))
            {
                var id = tvgId.Trim();
                if (_byId.TryGetValue(id, out var byId))
                {
                    return new StationMatch(byId, MatchMethod.StationId, 1.0);
                }

                var dot = id.IndexOf('.');
                var core = dot > 0 ? id.Substring(0, dot) : id;

                var callsign = NameNormalizer.CallsignKey(core);
                if (callsign.Length >= 3 && _byCallsign.TryGetValue(callsign, out var byCallsign))
                {
                    return new StationMatch(Pick(byCallsign, normalized, channelNumber), MatchMethod.TvgIdCallsign, 0.97);
                }

                var idName = NameNormalizer.Normalize(core).Compact;
                if (idName.Length >= 3 && _byCompact.TryGetValue(idName, out var byIdName))
                {
                    return new StationMatch(Pick(byIdName, normalized, channelNumber), MatchMethod.TvgIdName, 0.93);
                }
            }

            // 2. A broadcast callsign in the name: "ABC 7 New York (WABC)".
            foreach (var candidate in NameNormalizer.ExtractBroadcastCallsigns(name).Concat(NameNormalizer.ExtractBroadcastCallsigns(tvgName)))
            {
                if (_byCallsign.TryGetValue(candidate, out var stations))
                {
                    return new StationMatch(Pick(stations, normalized, channelNumber), MatchMethod.Callsign, 0.95);
                }
            }

            // 3. Exact cleaned-up name or callsign.
            foreach (var key in new[] { normalized, NameNormalizer.Normalize(tvgName) })
            {
                if (!key.IsEmpty && _byCompact.TryGetValue(key.Compact, out var stations))
                {
                    return new StationMatch(Pick(stations, normalized, channelNumber), MatchMethod.ExactName, 0.9);
                }
            }

            // 4. Fuzzy name comparison against stations that share a word or prefix.
            if (normalized.IsEmpty)
            {
                return StationMatch.None;
            }

            LineupStation best = null;
            var bestScore = 0.0;
            foreach (var station in Candidates(normalized))
            {
                var score = Similarity(normalized, station);
                if (score > bestScore
                    || (Math.Abs(score - bestScore) < 1e-9 && best != null && Prefer(station, best, normalized, channelNumber)))
                {
                    best = station;
                    bestScore = score;
                }
            }

            return best != null && bestScore >= _fuzzyThreshold
                ? new StationMatch(best, MatchMethod.Fuzzy, bestScore * 0.9)
                : StationMatch.None;
        }

        /// <summary>Stations ordered by how well they fit a channel name, for the manual picker.</summary>
        public IReadOnlyList<ScoredStation> Rank(string name, int max)
        {
            var normalized = NameNormalizer.Normalize(name);
            if (normalized.IsEmpty)
            {
                return new ScoredStation[0];
            }

            return Stations
                .Select(s => new ScoredStation(s, Similarity(normalized, s)))
                .Where(s => s.Score >= 0.35)
                .OrderByDescending(s => s.Score)
                .ThenBy(s => ChannelNumber.SortKey(s.Station.Channel))
                .Take(max)
                .ToList();
        }

        internal static double Similarity(NormalizedName channel, LineupStation station)
        {
            var best = 0.0;
            foreach (var key in new[] { station.NameKey, station.CallsignName })
            {
                if (key.IsEmpty)
                {
                    continue;
                }

                var score = Dice(channel.Compact, key.Compact);

                // "Fox Sports 1" is not "Fox Sports 2", and "ESPN" is not "ESPN2".
                if (!channel.Numbers.SetEquals(key.Numbers))
                {
                    score *= 0.6;
                }

                // East and West feeds carry different schedules.
                if ((channel.HasEast && key.HasWest) || (channel.HasWest && key.HasEast))
                {
                    score *= 0.7;
                }

                best = Math.Max(best, score);
            }

            return best;
        }

        /// <summary>Sørensen–Dice coefficient over character bigrams.</summary>
        internal static double Dice(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1;
            }

            if (a.Length < 2 || b.Length < 2)
            {
                return 0;
            }

            var counts = new Dictionary<int, int>();
            for (var i = 0; i < a.Length - 1; i++)
            {
                var bigram = (a[i] << 16) | a[i + 1];
                counts.TryGetValue(bigram, out var n);
                counts[bigram] = n + 1;
            }

            var shared = 0;
            for (var i = 0; i < b.Length - 1; i++)
            {
                var bigram = (b[i] << 16) | b[i + 1];
                if (counts.TryGetValue(bigram, out var n) && n > 0)
                {
                    counts[bigram] = n - 1;
                    shared++;
                }
            }

            return 2.0 * shared / (a.Length - 1 + b.Length - 1);
        }

        private IEnumerable<LineupStation> Candidates(NormalizedName name)
        {
            var seen = new HashSet<LineupStation>();
            var keys = name.Tokens.Where(t => t.Length >= 2).Concat(new[] { PrefixKey(name.Compact) });
            foreach (var key in keys)
            {
                if (key != null && _candidateIndex.TryGetValue(key, out var stations))
                {
                    foreach (var station in stations)
                    {
                        if (seen.Add(station))
                        {
                            yield return station;
                        }
                    }
                }
            }
        }

        /// <summary>Several stations can share a name or callsign (SD and HD feeds); choose the closest.</summary>
        private static LineupStation Pick(List<LineupStation> stations, NormalizedName channel, string channelNumber)
        {
            var best = stations[0];
            for (var i = 1; i < stations.Count; i++)
            {
                if (Prefer(stations[i], best, channel, channelNumber))
                {
                    best = stations[i];
                }
            }

            return best;
        }

        private static bool Prefer(LineupStation candidate, LineupStation current, NormalizedName channel, string channelNumber)
        {
            var number = ChannelNumber.Normalize(channelNumber);
            if (number != null)
            {
                var candidateOnNumber = ChannelNumber.Normalize(candidate.Channel) == number;
                var currentOnNumber = ChannelNumber.Normalize(current.Channel) == number;
                if (candidateOnNumber != currentOnNumber)
                {
                    return candidateOnNumber;
                }
            }

            if (candidate.IsHd != current.IsHd)
            {
                return candidate.IsHd == channel.HasHdTag;
            }

            return ChannelNumber.SortKey(candidate.Channel) < ChannelNumber.SortKey(current.Channel);
        }

        private static string PrefixKey(string compact)
        {
            return compact != null && compact.Length >= 4 ? "~" + compact.Substring(0, 4) : null;
        }

        private static void Add(Dictionary<string, List<LineupStation>> index, string key, LineupStation station)
        {
            if (string.IsNullOrEmpty(key))
            {
                return;
            }

            if (!index.TryGetValue(key, out var list))
            {
                list = new List<LineupStation>();
                index.Add(key, list);
            }

            if (!list.Contains(station))
            {
                list.Add(station);
            }
        }
    }
}
