using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Origin.Core.Lineup
{
    /// <summary>A channel or station name reduced to what matters for matching.</summary>
    public sealed class NormalizedName
    {
        internal NormalizedName(IReadOnlyList<string> tokens, bool hasHdTag)
        {
            Tokens = tokens;
            Compact = string.Concat(tokens);
            HasHdTag = hasHdTag;
            Numbers = new HashSet<string>(tokens.SelectMany(DigitRuns), StringComparer.Ordinal);
            HasEast = tokens.Contains("east");
            HasWest = tokens.Contains("west") || tokens.Contains("pacific");
        }

        /// <summary>Lowercase words, without country prefixes, quality tags or filler words.</summary>
        public IReadOnlyList<string> Tokens { get; }

        /// <summary>The tokens joined with no spaces, so "ESPN 2" and "ESPN2" compare equal.</summary>
        public string Compact { get; }

        /// <summary>The original carried HD/FHD/UHD/4K.</summary>
        public bool HasHdTag { get; }

        internal ISet<string> Numbers { get; }

        internal bool HasEast { get; }

        internal bool HasWest { get; }

        public bool IsEmpty => Compact.Length == 0;

        public override string ToString() => string.Join(" ", Tokens);

        private static IEnumerable<string> DigitRuns(string token)
        {
            return Regex.Matches(token, @"\d+").Cast<Match>().Select(m => m.Value.TrimStart('0').PadLeft(1, '0'));
        }
    }

    /// <summary>
    /// Cleans up IPTV channel names ("US: ESPN FHD", "|US| ABC 7 WABC ᴴᴰ") and Schedules Direct
    /// station names so they can be compared.
    /// </summary>
    public static class NameNormalizer
    {
        // "US:", "USA |", "UK -", "|US|", "[US]", "(US)", "VIP »" ... possibly several in a row.
        private static readonly Regex PrefixPattern = new Regex(
            @"^\s*(?:\|[^|]{1,12}\||\[[^\]]{1,12}\]|\([^)]{1,12}\)|(?<code>[A-Z]{2,4}(?:-[A-Z]{2,3})?)\s*(?:[:|»]|\s-\s))\s*",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // A few networks are written "HBO: ..." and must not be taken for a country prefix.
        private static readonly HashSet<string> NetworkPrefixes = new HashSet<string>(StringComparer.Ordinal)
        {
            "ABC", "AMC", "BBC", "BET", "CBS", "CMT", "CNN", "ESPN", "FOX", "HBO", "HGTV", "ITV", "MTV", "NBC", "PBS", "TBS", "TLC", "TNT",
        };

        private static readonly HashSet<string> QualityTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "hd", "fhd", "uhd", "sd", "hq", "lq", "4k", "8k", "hevc", "h264", "h265", "x264", "x265",
            "1080", "1080p", "1080i", "720", "720p", "2160p", "50fps", "60fps", "fps", "raw", "vip", "backup", "alt", "multi",
        };

        private static readonly HashSet<string> HdTokens = new HashSet<string>(StringComparer.Ordinal) { "hd", "fhd", "uhd", "4k", "8k", "1080", "1080p", "1080i", "2160p" };

        // Dropped only when something else is left: "Discovery Channel" == "Discovery".
        private static readonly HashSet<string> FillerTokens = new HashSet<string>(StringComparer.Ordinal)
        {
            "channel", "network", "tv", "television", "the",
        };

        private static readonly Regex BroadcastCallsign = new Regex(
            @"(?<![A-Z0-9])(?<call>[KW][A-Z]{2,3})(?:-?(?:TV|DT\d?|HD|LD|LP|CD|CA))?(?![A-Z0-9])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CallsignSuffix = new Regex(
            @"^(?<call>[KW][A-Z]{2,3})(?:TV|DT\d?|HD|LD|LP|CD|CA)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static NormalizedName Normalize(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new NormalizedName(new string[0], false);
            }

            var text = StripPrefixes(name.Trim());
            text = text.Replace("ᴴᴰ", " HD ").Replace("ᵁᴴᴰ", " UHD ").Replace("ᶠᴴᴰ", " FHD ").Replace("ˢᴰ", " SD ")
                       .Replace("&", " and ").Replace("+", " plus ");
            text = RemoveDiacritics(text).ToLowerInvariant();

            var words = Regex.Split(text, @"[^\p{L}\p{Nd}]+").Where(w => w.Length > 0).ToList();
            var hasHd = words.Any(HdTokens.Contains);
            var kept = words.Where(w => !QualityTokens.Contains(w)).ToList();
            var meaningful = kept.Where(w => !FillerTokens.Contains(w)).ToList();

            return new NormalizedName(meaningful.Count > 0 ? meaningful : kept, hasHd);
        }

        /// <summary>Uppercase callsign without digital/HD suffixes: "WABC-DT" → "WABC", "ESPNHD" → "ESPN".</summary>
        public static string CallsignKey(string callsign)
        {
            if (string.IsNullOrWhiteSpace(callsign))
            {
                return string.Empty;
            }

            var key = new string(callsign.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
            var match = CallsignSuffix.Match(key);
            if (match.Success)
            {
                return match.Groups["call"].Value;
            }

            if (key.Length > 4 && key.EndsWith("HD", StringComparison.Ordinal))
            {
                return key.Substring(0, key.Length - 2);
            }

            return key;
        }

        /// <summary>North American broadcast callsigns (K/W + 2-3 letters) that appear in a channel name.</summary>
        public static IEnumerable<string> ExtractBroadcastCallsigns(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                yield break;
            }

            var text = StripPrefixes(name).ToUpperInvariant();
            foreach (Match match in BroadcastCallsign.Matches(text))
            {
                yield return match.Groups["call"].Value;
            }
        }

        internal static string StripPrefixes(string text)
        {
            while (true)
            {
                var match = PrefixPattern.Match(text);
                if (!match.Success || match.Length == 0 || match.Length >= text.Length)
                {
                    return text;
                }

                var code = match.Groups["code"];
                if (code.Success && NetworkPrefixes.Contains(code.Value))
                {
                    return text;
                }

                text = text.Substring(match.Length);
            }
        }

        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }
    }
}
