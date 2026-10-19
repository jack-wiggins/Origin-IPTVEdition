using System;
using System.Collections.Generic;

namespace Origin.Core.Xtream
{
    /// <summary>Builds and picks apart Xtream Codes panel URLs.</summary>
    public static class XtreamUrls
    {
        private static readonly string[] EndpointFiles =
        {
            "get.php", "player_api.php", "xmltv.php", "panel_api.php", "enigma2.php",
        };

        /// <summary>
        /// Accepts what people actually type or paste ("line.example.com:8080",
        /// "http://host:80/", a full get.php link ...) and returns "http://host:port".
        /// </summary>
        public static string NormalizeServerUrl(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                throw new FormatException("Enter the server URL from your IPTV provider.");
            }

            var text = input.Trim();
            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                text = "http://" + text;
            }

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrEmpty(uri.Host))
            {
                throw new FormatException("\"" + input.Trim() + "\" is not a valid server URL. It should look like http://example.com:8080");
            }

            var path = uri.AbsolutePath.TrimEnd('/');
            var lastSlash = path.LastIndexOf('/');
            var lastSegment = lastSlash >= 0 ? path.Substring(lastSlash + 1) : path;
            foreach (var endpoint in EndpointFiles)
            {
                if (string.Equals(lastSegment, endpoint, StringComparison.OrdinalIgnoreCase))
                {
                    path = path.Substring(0, Math.Max(lastSlash, 0));
                    break;
                }
            }

            return uri.GetLeftPart(UriPartial.Authority) + path.TrimEnd('/');
        }

        /// <summary>
        /// If <paramref name="input"/> is a full link such as
        /// http://host:8080/get.php?username=u&amp;password=p&amp;type=m3u_plus, returns its parts.
        /// </summary>
        public static bool TryExtractCredentials(string input, out string serverUrl, out string username, out string password)
        {
            serverUrl = username = password = null;
            if (string.IsNullOrWhiteSpace(input) || input.IndexOf('?') < 0)
            {
                return false;
            }

            string normalized;
            try
            {
                normalized = NormalizeServerUrl(input);
            }
            catch (FormatException)
            {
                return false;
            }

            var query = ParseQuery(input.Substring(input.IndexOf('?') + 1));
            if (!query.TryGetValue("username", out username) || !query.TryGetValue("password", out password)
                || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                username = password = null;
                return false;
            }

            serverUrl = normalized;
            return true;
        }

        /// <summary>The M3U Plus playlist: every live channel, movie and series episode.</summary>
        public static string GetPlaylistUrl(XtreamAccount account)
        {
            return account.ServerUrl + "/get.php?username=" + Uri.EscapeDataString(account.Username)
                   + "&password=" + Uri.EscapeDataString(account.Password)
                   + "&type=m3u_plus&output=" + account.OutputFormat;
        }

        public static string GetPlayerApiUrl(XtreamAccount account, string action = null)
        {
            var url = account.ServerUrl + "/player_api.php?username=" + Uri.EscapeDataString(account.Username)
                      + "&password=" + Uri.EscapeDataString(account.Password);
            return action == null ? url : url + "&action=" + Uri.EscapeDataString(action);
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hash = query.IndexOf('#');
            if (hash >= 0)
            {
                query = query.Substring(0, hash);
            }

            foreach (var pair in query.Split('&'))
            {
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(pair.Substring(0, equals).Replace('+', ' '));
                var value = Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
                result[key] = value;
            }

            return result;
        }
    }
}
