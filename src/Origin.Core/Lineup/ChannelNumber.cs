using System.Globalization;
using System.Text.RegularExpressions;

namespace Origin.Core.Lineup
{
    /// <summary>Channel numbers as viewers type them: "206", "7.1", "007" (= "7").</summary>
    public static class ChannelNumber
    {
        private static readonly Regex Pattern = new Regex(@"^\s*0*(?<major>\d{1,6})(?:[.\-](?<minor>\d{1,4}))?\s*$", RegexOptions.Compiled);

        /// <summary>Canonical form ("002" → "2", "7-1" → "7.1"), or null if <paramref name="value"/> is not a channel number.</summary>
        public static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var match = Pattern.Match(value);
            if (!match.Success)
            {
                return null;
            }

            var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
            if (!match.Groups["minor"].Success)
            {
                return major.ToString(CultureInfo.InvariantCulture);
            }

            var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
            return major.ToString(CultureInfo.InvariantCulture) + "." + minor.ToString(CultureInfo.InvariantCulture);
        }

        public static double SortKey(string value)
        {
            var normalized = Normalize(value);
            if (normalized == null)
            {
                return double.MaxValue;
            }

            var dot = normalized.IndexOf('.');
            if (dot < 0)
            {
                return int.Parse(normalized, CultureInfo.InvariantCulture);
            }

            return int.Parse(normalized.Substring(0, dot), CultureInfo.InvariantCulture)
                   + int.Parse(normalized.Substring(dot + 1), CultureInfo.InvariantCulture) / 10000.0;
        }

        public static int Major(string value)
        {
            var normalized = Normalize(value);
            if (normalized == null)
            {
                return 0;
            }

            var dot = normalized.IndexOf('.');
            return int.Parse(dot < 0 ? normalized : normalized.Substring(0, dot), CultureInfo.InvariantCulture);
        }
    }
}
