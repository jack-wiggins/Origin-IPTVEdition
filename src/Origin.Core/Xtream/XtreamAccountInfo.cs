using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Origin.Core.Xtream
{
    /// <summary>The user_info / server_info block returned by player_api.php.</summary>
    public sealed class XtreamAccountInfo
    {
        public bool IsAuthenticated { get; set; }

        /// <summary>"Active", "Expired", "Banned", "Disabled" ...</summary>
        public string Status { get; set; }

        public string Message { get; set; }

        public DateTime? ExpiresUtc { get; set; }

        public bool IsTrial { get; set; }

        public int? ActiveConnections { get; set; }

        public int? MaxConnections { get; set; }

        public IReadOnlyList<string> AllowedOutputFormats { get; set; } = new string[0];

        public string ServerTimezone { get; set; }

        public bool IsActive => IsAuthenticated && string.Equals(Status, "Active", StringComparison.OrdinalIgnoreCase);

        /// <summary>Panels are inconsistent about types (numbers arrive as strings and vice versa), so parse loosely.</summary>
        internal static XtreamAccountInfo FromJson(JObject root)
        {
            var user = root["user_info"] as JObject;
            var server = root["server_info"] as JObject;
            var info = new XtreamAccountInfo();

            if (user == null)
            {
                return info;
            }

            info.IsAuthenticated = ParseInt(user["auth"]) == 1;
            info.Status = ParseString(user["status"]);
            info.Message = ParseString(user["message"]);
            info.IsTrial = ParseInt(user["is_trial"]) == 1;
            info.ActiveConnections = ParseInt(user["active_cons"]);
            info.MaxConnections = ParseInt(user["max_connections"]);

            var expires = ParseLong(user["exp_date"]);
            if (expires.HasValue && expires.Value > 0)
            {
                info.ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(expires.Value).UtcDateTime;
            }

            if (user["allowed_output_formats"] is JArray formats)
            {
                info.AllowedOutputFormats = formats.Select(f => f.ToString()).ToArray();
            }

            if (server != null)
            {
                info.ServerTimezone = ParseString(server["timezone"]);
            }

            return info;
        }

        private static string ParseString(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? null : token.ToString();
        }

        private static int? ParseInt(JToken token)
        {
            var value = ParseLong(token);
            return value.HasValue ? (int?)value.Value : null;
        }

        private static long? ParseLong(JToken token)
        {
            var text = ParseString(token);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : (long?)null;
        }
    }
}
